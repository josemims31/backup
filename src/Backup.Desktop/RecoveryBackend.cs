using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backup.Core;
namespace Backup.Desktop
{
    public sealed class ConfirmedRepair {public long ArchiveId;public string TaskId;}
    public sealed partial class DesktopBackend
    {
        public GroupedBackupResult BackupDatabase(BackupTaskDefinition task,Action<string> report=null,bool force=false)
        {
            var targets=DatabaseBackupTargets();
            if(targets.Count==0){if(force)throw new ArgumentException("请先在设置中选择数据库备份仓库。");if(task!=null)Catalog.ReleasePublishedRetirements(task.Id);if(report!=null)report("数据库备份已关闭，保留本地数据库。");return null;}
            ValidateDatabaseBackupTargets(targets);
            string password=ResolveDatabaseBackupPassword();if(string.IsNullOrEmpty(password))throw new ArgumentException("请先在设置中填写数据库密码。");
            long revision=Catalog.RecoveryRevision;
            if(!force&&Catalog.HasCurrentCatalog(targets,revision,password))
            {if(task!=null)Catalog.ReleasePublishedRetirements(task.Id);if(report!=null)report("清单未变化，沿用已配置仓库中的完整数据库快照。");return null;}
            var inventory=new HashSet<long>(Catalog.ListArchives(true).Where(archive=>archive.TaskId=="__catalog__").Select(archive=>archive.ArchiveId));
            var destinations=new HashSet<string>(targets.SelectMany(root=>Catalog.LoadTopology().ResolveDestinations(root)));
            // This is a local check of a previously rejected token, not another
            // login or remote probe. Do not reserve a doomed catalog upload.
            foreach(var destination in Catalog.LoadTopology().Destinations.Where(item=>destinations.Contains(item.Id)&&item.Protocol=="webdav")){
                var options=LoadConnection(destination.Id);LoadSharedOpenListAuthorization(destination.Location,options);if(OpenListFallbackActive(destination.Location,options.Username))continue;try{OpenListAuthorization.ValidateCachedOpenListAuthorization(destination.Location,options);}catch(OpenListAuthorizationException){if(!RenewRepairOpenListAuthorization(destination.Location,options)&&!OpenListFallbackActive(destination.Location,options.Username))throw;if(!OpenListFallbackActive(destination.Location,options.Username))OpenListAuthorization.ValidateCachedOpenListAuthorization(destination.Location,options);}
            }
            Catalog.SetSetting("catalog_sequence_floor",Catalog.NextArchiveSequence().ToString(System.Globalization.CultureInfo.InvariantCulture));
            bool owns=ActiveBackup==null;if(owns)BeginBackup();
            try
            {
                ActiveBackup.Checkpoint();GroupedBackupResult result;var pipeline=new BackupPipelineView();
                using(var snapshot=Catalog.CreateRecoverySnapshot(OperationMemory))
                {
                    var source=SourceItem.FromSnapshot(snapshot,"catalog.db");
                    long available=OperationMemory.AvailableBytes;
                    int volume=(int)Math.Min(PreferredVolumeMiB*1024L*1024,available/3);
                    volume-=volume%4096;
                    if(volume<4096)throw new IOException("软件总内存预算不足以同时保留数据库快照和上传缓冲。");
                    if(report!=null)report("正在内存中加密压缩数据库快照…");
                    using(var buffers=new VolumeBufferPool(MemoryLimitBytes,volume,ActiveBackup,OperationMemory))
                    result=new GroupedBackupEngine(Catalog,Adapters(),Path.Combine(BaseDirectory,"7za.dll")).BackupForTask(new[]{source},targets.Count,password,volume,"LZMA2",null,null,p=>{
                        pipeline.Observe(p);ReportTransferNotice(p);if(report!=null)report(pipeline.Text("正在同步数据库快照"));
                    },"__catalog__",targets,ActiveBackup,true,MemoryLimitBytes,buffers);
                }
                if(result.Archive.CurrentValidCopies<result.Archive.TargetCopies){if(force&&Catalog.PendingVolumeTransfers().Any(item=>item.ArchiveId==result.Archive.ArchiveId&&item.AwaitingConfirmation)){if(report!=null)report("数据库快照上传结果待确认，上一份完整快照已保留。");return result;}throw new IOException("数据库备份未达到所选目标份数；上一份完整快照已保留。");}
                inventory.Add(result.Archive.ArchiveId);
                Catalog.PublishCatalogSnapshot(result.Archive.ArchiveId,targets,"__catalog__",revision);
                if(Catalog.GetSetting(PendingRepairCatalog)=="1")Catalog.SetSetting(PendingRepairCatalog,"");
                if(task!=null)Catalog.ReleasePublishedRetirements(task.Id);
                var cleanup=new ArchiveMaintenance(Catalog,Adapters()){Control=ActiveBackup}.CollectGarbageDetailed(null,true,null,inventory,destinations);RefreshManagedArchiveInventory(inventory);
                if(report!=null)report("数据库备份完成："+result.Archive.ArchiveName+(cleanup.PendingArchives>0?"；旧快照清理待重试":""));
                return result;
            }
            finally{try{CleanupStoppedOperation();}finally{if(owns)EndBackup();}}
        }
        public List<SourceItem> LastRepairSources {get;private set;}
        public IList<ConfirmedRepair> PendingRepairs(){return JsonSettings.Decode(Catalog.GetSetting("confirmed_repairs_v1"),new List<ConfirmedRepair>());}
        public int ConfirmRepairs(string taskId)
        {var scope=taskId==null?null:new HashSet<string>{taskId};return QueueKnownRepairs(WorkspaceMigration.Load(Catalog),scope)+Catalog.InterruptedArchives(scope).Count;}
        public IList<string> RunConfirmedRepairs(WorkspaceDefinition workspace,Action<string> report=null,ISet<string> taskIds=null,Action<DesktopProgress> progress=null,Action<CleanupProgress> cleanupProgress=null,bool deferRetirement=false,bool inspectInterrupted=true,ISet<long> repairArchiveIds=null,bool synchronize=true,bool uploadResultsChecked=false)
        {using(BeginMaintenanceSearch())return RunConfirmedRepairsCore(workspace,report,taskIds,progress,cleanupProgress,deferRetirement,inspectInterrupted,repairArchiveIds,synchronize,uploadResultsChecked);}
        private IList<string> RunConfirmedRepairsCore(WorkspaceDefinition workspace,Action<string> report,ISet<string> taskIds,Action<DesktopProgress> progress,Action<CleanupProgress> cleanupProgress,bool deferRetirement,bool inspectInterrupted,ISet<long> repairArchiveIds,bool synchronize,bool uploadResultsChecked)
        {
            ReconcileDatabaseBackupRegistrations(taskIds,report);if(inspectInterrupted&&!uploadResultsChecked)QueuePendingUploadDamage(workspace,CheckPendingUploads(taskIds,true,null,false,true));else if(!inspectInterrupted){var knownUploads=new HashSet<long>(Catalog.PendingVolumeTransfers().Where(item=>(item.MetadataMatched||item.ConfirmationDamaged||item.Phase.StartsWith("repair-",StringComparison.Ordinal))&&(taskIds==null||taskIds.Contains(Catalog.GetArchive(item.ArchiveId).TaskId))).Select(item=>item.ArchiveId));if(knownUploads.Count>0)QueuePendingUploadDamage(workspace,new PendingUploadConfirmation(Catalog,Adapters()).Check(knownUploads,ActiveBackup==null?OperationCancellation:ActiveBackup.Token,ReportUploadConfirmation,false,true,false,false,true));}if(inspectInterrupted){if(taskIds==null)Catalog.RefreshLocalBackupNodes();else foreach(string task in taskIds.Where(id=>!id.StartsWith("__",StringComparison.Ordinal)))Catalog.RefreshLocalBackupNode(task);}LastRepairSources=new List<SourceItem>();var errors=RepairInterruptedBackups(taskIds,report,inspectInterrupted).ToList();errors.AddRange(RepairOwnedTemporaryFiles(taskIds,inspectInterrupted));QueueKnownRepairs(workspace,taskIds);var awaiting=AwaitingUploadArchiveIds();errors.AddRange(RepackRequestedRepairs(workspace,PendingRepairs().Where(item=>!awaiting.Contains(item.ArchiveId)&&(repairArchiveIds==null||repairArchiveIds.Contains(item.ArchiveId))).ToList(),taskIds,report,progress,cleanupProgress,deferRetirement,synchronize));if(inspectInterrupted){int pending=Catalog.PendingVolumeTransfers().Count(item=>item.AwaitingConfirmation&&(taskIds==null||taskIds.Contains(Catalog.GetArchive(item.ArchiveId).TaskId)));if(pending>0&&!errors.Any(message=>message.Contains("尚无法核对上传结果")||message.Contains("上传结果仍未核对")))errors.Add(pending+" 个分卷暂时无法核对，请检查仓库连接后重新运行完整性检查。");}return errors;
        }
        public string ImportDatabase(string incoming)
        {
            var memory=new MemoryBudget(MemoryLimitBytes);
            using(memory.Reserve(MemoryLimitMiB>=512?128L*1024*1024:64L*1024*1024))
            using(var check=BackupCatalog.FromImportFileMemory(incoming,memory))
            {
                check.ValidateIntegrity();if(string.IsNullOrEmpty(check.GetSetting("repository_id")))throw new IOException("所选文件不是可恢复的 Backup 数据库。");NormalizeImportedConfiguration(check);WorkspaceMigration.Load(check);check.LoadTopology().Normalize();ProtectImportedRecovery(check);
                string rollback=Path.Combine(CacheDirectory,"catalog-before-import-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,6)+".db");
                Catalog.CreateSnapshot(rollback);
                lastIntegrityReview=null;repositoryRuntime.Reset(delegate{Catalog.ReplaceWith(check);sourceSnapshots.Clear();ClearBrowseNodeSnapshots();InvalidateRepositoryConnections();Catalog.ValidateIntegrity();RepositoryId=Catalog.GetSetting("repository_id");Catalog.ClearRuntimeCaches();InitializeServerTrust();MigrateConnectionReferences();MigrateSourceCaches();});
                ReloadBandwidth();MigrateSnapshotRepairOwners();PruneDatabaseRecoveryCopies();return rollback;
            }
        }
    }
}
