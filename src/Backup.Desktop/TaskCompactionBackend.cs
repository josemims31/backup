using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed class TaskCompactionResult
    {
        public int OldArchives,SourceFiles,NewArchives;
        public CleanupResult Cleanup=new CleanupResult();
        public List<string> Errors=new List<string>();
        internal IList<SourceItem> ReadSources=new List<SourceItem>();
    }
    public sealed partial class DesktopBackend
    {
#if UI_TEST
        public Action<string> TestCompactionPoint;
#endif
        private void CompactionPoint(string name)
        {
#if UI_TEST
            if(TestCompactionPoint!=null)TestCompactionPoint(name);
#endif
            if(ActiveBackup!=null)ActiveBackup.Checkpoint();
        }
        private void ValidateMissingCompactionSource(string path,WorkspaceDefinition workspace)
        {
            string root=workspace.Sources.Where(s=>!s.Deleted&&!string.IsNullOrWhiteSpace(s.Location)&&Under(path,s.Location)).OrderByDescending(s=>s.Location.Length).Select(s=>s.Location).FirstOrDefault();
            ValidateSourceMissing(path,root);
        }
        public TaskCompactionResult CompactTaskArchives(BackupTaskDefinition task,Action<DesktopProgress> progress=null,Action<CleanupProgress> cleanupProgress=null,Action<string> report=null,ISet<long> archiveIds=null,bool deferRetirement=false,bool synchronize=true)
        {
            if(task==null||task.Deleted)throw new ArgumentException("请选择任务。");
            if(Catalog.TaskHasNodes(task.Id))throw new IOException("历史备份版本可能引用旧包，不能从当前源文件重打包替换。请修复完整性回收无引用的数据包。");
            if(task.RecoveryPendingConfirmation)throw new InvalidOperationException("导入的任务尚未确认，请先完成恢复并手动确认备份。");
            if(task.PendingTargetReplacement||task.TargetIds.Count==0)throw new IOException("请先为任务补齐备份仓库。");
            var workspace=WorkspaceMigration.Load(Catalog);var saved=workspace.Tasks.SingleOrDefault(t=>t.Id==task.Id&&!t.Deleted);
            if(saved==null||saved.RecoveryPendingConfirmation)throw new IOException("任务不存在或尚未确认。");
            WorkspaceMigration.ValidateTargetPolicy(task,Catalog.LoadTopology());
            if(string.IsNullOrEmpty(ResolveBackupPassword(task)))throw new IOException("请先设置备份密码。");
            bool owns=ActiveBackup==null;if(owns)BeginBackup();var result=new TaskCompactionResult();var touched=new HashSet<long>();
            try
            {
                var allFiles=Catalog.GetTaskArchiveFiles(task.Id);var latest=Catalog.GetLatestFileViews(task.Id).ToDictionary(f=>f.ArchivePath,StringComparer.OrdinalIgnoreCase);
                var candidates=new HashSet<long>(Catalog.PendingCompactionRetirements(task.Id).Where(id=>archiveIds==null||archiveIds.Contains(id)));
                if(archiveIds==null)candidates.UnionWith(allFiles.Where(file=>file.FileState=="expired"||file.FileState=="deleted").Select(file=>file.ArchiveId));
                else candidates.UnionWith(archiveIds.Where(id=>Catalog.GetArchive(id).TaskId==task.Id&&Catalog.GetArchive(id).Status!="cleaned"));
                result.OldArchives=candidates.Count;if(candidates.Count==0)return result;
                var transferErrors=new ArchiveMaintenance(Catalog,Adapters()){Control=ActiveBackup,Report=report}.ReconcileVolumeTransfers(task.Id,candidates);
                if(transferErrors.Count>0)throw new IOException("这些压缩包的传输仍待核对："+string.Join("；",transferErrors));
                var files=allFiles.Where(file=>candidates.Contains(file.ArchiveId)).ToList();var deleted=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var deletedSources=new List<string>();var local=new Dictionary<string,SourceItem>(StringComparer.OrdinalIgnoreCase);var layout=BackupRootLayout(task);var policy=Catalog.LoadTopology().Scope(task.TargetIds);var coverage=new Dictionary<long,bool>();
                foreach(var file in files)
                {
                    if(file.FileState!="current")continue;FileBackupView view;
                    if(latest.TryGetValue(file.Path,out view)&&view.ArchiveId!=file.ArchiveId)
                    {
                        bool complete;if(!coverage.TryGetValue(view.ArchiveId,out complete)){complete=Catalog.CoversArchivePolicy(view.ArchiveId,policy,task.RequiredCopies);coverage[view.ArchiveId]=complete;}if(complete)continue;
                    }
                    CompactionPoint("inspect-source");string path=SourcePath(file);
                    local[file.Path]=new SourceItem{Path=path,ArchivePath=file.Path,Size=file.Size,Modified=file.Modified,EntryPath=string.IsNullOrEmpty(file.EntryPath)?layout.Relative(path,file.Path):file.EntryPath,VerifyContentDuringRead=false};
                }
                touched.UnionWith(candidates);var survivors=local.Where(item=>!deleted.Contains(item.Key)).Select(item=>item.Value).ToList();
                var exclusions=new BackupExclusions(Rules());var protectedRoots=ProtectedRoots();
                foreach(var file in survivors)if(exclusions.Match(file.Path)!=null||protectedRoots.Any(root=>Under(file.Path,root)))throw new IOException("仍有效的文件被当前排除规则或受保护目录排除，旧包保留："+file.Path);
                result.SourceFiles=survivors.Count;
                if(report!=null)report("将按批次核对并整合 "+survivors.Count+" 个文件，替换 "+candidates.Count+" 个旧压缩包。");
                using(var session=Catalog.BeginCompaction(task.Id,candidates))
                {
                    if(survivors.Count>0)
                    {
                        // The normal task writer batches all survivors together, regardless of old package boundaries.
                        var backup=BackupTask(survivors,task.RequiredCopies,ResolveBackupPassword(task),PreferredVolumeMiB,progress,task.Id,task.TargetIds,null,true,missing=>{deleted.Add(missing.ArchivePath);deletedSources.Add(missing.Path);});
                        result.ReadSources=backup.Sources;
                        if(backup.Stopped)throw new OperationCanceledException("清理已停止，旧包保留。");
                        if(backup.ObservedSources.Count!=survivors.Count-deleted.Count||backup.Sources.Count!=survivors.Count-deleted.Count)throw new IOException("有效文件未全部重新打包，旧包保留。");
                        survivors=backup.Sources;result.SourceFiles=survivors.Count;
                    }
                    touched.UnionWith(session.NewArchives);CompactionPoint("before-publish");foreach(var source in survivors)source.CheckUnchanged();
                    foreach(string path in deletedSources)
                    {
                        bool stillMissing=false;try{File.GetAttributes(path);}catch(FileNotFoundException){stillMissing=true;}catch(DirectoryNotFoundException){stillMissing=true;}
                        if(!stillMissing)throw new IOException("源文件已重新出现，旧包保留："+path);ValidateMissingCompactionSource(path,workspace);
                    }
                    result.NewArchives=session.NewArchives.Count;
                    session.Publish(deleted,Catalog.LoadTopology().Scope(task.TargetIds),task.RequiredCopies);
                }
                // A backup publishes repairs and new data together, then releases these holds.
                if(deferRetirement){if(report!=null)report("异常包已重新打包；任务数据上传完成后同步数据库快照，再清理旧包。");return result;}
                // Standalone compaction and integrity repair each publish once,
                // after their own changes and cleanup have completed.
                CompactionPoint("before-delete");
                Catalog.ReleaseCompactionRetirements(task.Id,candidates);
                result.Cleanup=new ArchiveMaintenance(Catalog,Adapters()){Control=ActiveBackup,Report=report}.CollectGarbageDetailed(cleanupProgress,false,task.Id,candidates);
                LastCleanup=result.Cleanup;result.Errors.AddRange(result.Cleanup.Errors);
                if(synchronize){CompactionPoint("before-snapshot");BackupDatabase(task,report);}return result;
            }
            catch(OperationCanceledException){result.Errors.Add("清理已停止；尚未删除的旧包已保留，可稍后重试。");return result;}
            catch(Exception error)
            {
                if(!(error is IOException)&&!(error is UnauthorizedAccessException)&&!(error is ArgumentException)&&!(error is NotSupportedException))throw;
                result.Errors.Add(error.Message+"；尚未删除的旧包已保留，可稍后重试。");return result;
            }
            finally{try{CleanupStoppedOperation();RefreshManagedArchiveInventory(touched);}finally{if(owns)EndBackup();}}
        }
    }
}
