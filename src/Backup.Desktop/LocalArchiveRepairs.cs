using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backup.Core;
namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        // Update only entries owned by this operation; unrelated cached files are retained.
        private void RefreshManagedArchiveInventory(ISet<long> archiveIds)
        {
            if(archiveIds.Count>0)FlushRepositoryResults();
        }
        private IList<string> RepackRequestedRepairs(WorkspaceDefinition workspace,IList<ConfirmedRepair> pending,ISet<string> taskIds,Action<string> report,Action<DesktopProgress> progress,Action<CleanupProgress> cleanup,bool deferRetirement,bool synchronize)
        {
            var errors=new List<string>();
            foreach(var group in pending.Where(item=>taskIds==null||taskIds.Contains(item.TaskId)).GroupBy(item=>item.TaskId).ToList())
            {
                var nodeRevision=group.Key.StartsWith("__node__:",StringComparison.Ordinal)?Catalog.NodeRevisions().FirstOrDefault(n=>Catalog.GetArchive(n.ArchiveId).TaskId==group.Key):null;
                var task=workspace.Tasks.FirstOrDefault(t=>t.Id==(nodeRevision==null?group.Key:nodeRevision.TaskId));
                try
                {
                    if(group.Key=="__catalog__")
                    {
                        if(DatabaseBackupTargets().Count==0){errors.Add("数据库备份已关闭，数据库快照的异常记录已保留。");continue;}
                        if(synchronize){BackupDatabase(null,report);TrimRepairQueue(pending,new HashSet<string>{"__catalog__"});}continue;
                    }
                    if(task==null)throw new IOException("找不到原任务，修复记录已保留。");
                    if(nodeRevision==null&&!Catalog.TaskHasNodes(task.Id)){TrimRepairQueue(pending,new HashSet<string>{group.Key});continue;}
                    if(task.RecoveryPendingConfirmation)throw new IOException("导入的任务尚未确认。");
                    if(nodeRevision!=null||Catalog.TaskHasNodes(task.Id))
                    {
                        // Published history requires verified encrypted peers.
                        // Local-only unfinished versions may use unchanged sources
                        // after the peer repair fails and history ownership is excluded.
                        foreach(var item in group)
                        {
                            bool sourceAttempted=false;
                            try{
                                if(nodeRevision==null&&!Catalog.HasReadableInspectedManifest(item.ArchiveId)&&!Catalog.IntegrityCopyStates(item.ArchiveId).Values.Any(state=>state.StartsWith("unavailable",StringComparison.Ordinal))){sourceAttempted=true;if(RebuildUnfinishedArchiveFromSources(item.ArchiveId,task,report))continue;}
                                new ArchiveMaintenance(Catalog,Adapters(true,true)){Control=ActiveBackup,Report=report,UploadProgress=ReportTransferNotice,BeforeRetiredDeletion=id=>PublishMigrationCatalogs(new HashSet<long>{id},null,report,synchronize)}.Migrate(item.ArchiveId,Catalog.LoadTopology().Scope(task.TargetIds),task.RequiredCopies);Catalog.ClearArchiveUnrepairable(item.ArchiveId);}
                            catch(Exception error){
                                if(!(error is IOException)&&!(error is UnauthorizedAccessException)&&!(error is NotSupportedException))throw;
                                if(nodeRevision!=null&&error is IOException&&RebuildNodePackageFromCache(nodeRevision,task,report))continue;
                                if(nodeRevision==null&&!sourceAttempted&&error is IOException)try{if(RebuildUnfinishedArchiveFromSources(item.ArchiveId,task,report))continue;}
                                catch(Exception sourceError){if(!(sourceError is IOException)&&!(sourceError is UnauthorizedAccessException)&&!(sourceError is NotSupportedException))throw;errors.Add(Catalog.GetArchive(item.ArchiveId).ArchiveName+"："+RestoreFailureMessage(sourceError));continue;}
                                if(error is NoReadableReplicaException){Catalog.MarkArchiveUnrepairable(item.ArchiveId,"本次未找到健康副本。");errors.Add(Catalog.GetArchive(item.ArchiveId).ArchiveName+"：本次未找到健康副本，保留缺失或损坏状态；后续修复完整性会重新尝试。");}
                                else errors.Add(Catalog.GetArchive(item.ArchiveId).ArchiveName+"："+RestoreFailureMessage(error));
                            }
                        }
                        TrimRepairQueue(pending,new HashSet<string>{group.Key});continue;
                    }
                    TrimRepairQueue(pending,new HashSet<string>{group.Key});
                    var remaining=new HashSet<ConfirmedRepair>(pending);var active=group.Where(remaining.Contains).ToList();if(active.Count==0)continue;
                    var records=active.Select(value=>value.ArchiveId).Distinct().ToDictionary(id=>id,id=>Catalog.GetArchive(id));
                    var versions=new HashSet<long>(Catalog.PendingVersionRetirements(group.Key));
                    var retiring=new HashSet<long>(active.Where(item=>versions.Contains(item.ArchiveId)&&(!Catalog.ArchiveHasCurrentFiles(item.ArchiveId)||records[item.ArchiveId].Status=="complete"&&records[item.ArchiveId].CurrentValidCopies>=records[item.ArchiveId].TargetCopies)).Select(item=>item.ArchiveId));
                    if(retiring.Count>0&&!deferRetirement){if(synchronize)BackupDatabase(task,report);LastCleanup=new ArchiveMaintenance(Catalog,Adapters()){Control=ActiveBackup}.CollectGarbageDetailed(cleanup,false,task.Id,retiring);errors.AddRange(LastCleanup.Errors.Select(message=>task.Name+"："+message));}
                    var data=new HashSet<long>(active.Where(item=>!retiring.Contains(item.ArchiveId)&&records[item.ArchiveId].TaskId!="__catalog__").Select(item=>item.ArchiveId));
                    if(data.Count>0)
                    {
                        var result=CompactTaskArchives(task,progress,cleanup,report,data,deferRetirement,synchronize);
                        LastRepairSources.AddRange(result.ReadSources);
                        foreach(string error in result.Errors)errors.Add(task.Name+"："+error);
                    }
                    var removed=new HashSet<ConfirmedRepair>();foreach(var item in active)
                    {
                        var archive=Catalog.GetArchive(item.ArchiveId);
                        if(archive.TaskId=="__catalog__")
                        {
                            if(synchronize&&!deferRetirement&&Catalog.NeedsPreservation(item.ArchiveId))BackupDatabase(task,report);
                            if(!Catalog.NeedsPreservation(item.ArchiveId))removed.Add(item);
                        }
                        else if(archive.Status=="cleaned")removed.Add(item);
                    }
                    RemoveRepairEntries(pending,removed);
                    TrimRepairQueue(pending,new HashSet<string>{group.Key});
                    remaining=new HashSet<ConfirmedRepair>(pending);if(!deferRetirement&&group.Any(remaining.Contains)&&!errors.Any(message=>message.StartsWith(task.Name+"：",StringComparison.Ordinal)))errors.Add(task.Name+"：仍有修复或旧包清理待重试。");
                }
                catch(Exception error)
                {if(!(error is IOException)&&!(error is UnauthorizedAccessException)&&!(error is ArgumentException)&&!(error is NotSupportedException))throw;errors.Add((task==null?group.Key:task.Name)+"："+RestoreFailureMessage(error));}
                finally{SaveRepairQueue(pending);}
            }
            return errors;
        }
        private void TrimRepairQueue(IList<ConfirmedRepair> queue,ISet<string> taskIds=null,ISet<long> archiveIds=null)
        {
            var selected=queue.Where(value=>(taskIds==null||taskIds.Contains(value.TaskId))&&(archiveIds==null||archiveIds.Contains(value.ArchiveId))).ToList();if(selected.Count==0)return;
            var records=new Dictionary<long,ArchiveSummary>();foreach(long id in selected.Select(item=>item.ArchiveId).Distinct())try{records[id]=Catalog.GetArchive(id);}catch(ArgumentException){}
            var stages=new HashSet<long>();
            foreach(string id in selected.Select(item=>item.TaskId).Distinct()){stages.UnionWith(Catalog.PendingVersionRetirements(id));stages.UnionWith(Catalog.PendingCompactionRetirements(id));}
            var removed=new HashSet<ConfirmedRepair>();foreach(var item in selected)
            {
                ArchiveSummary archive;bool remove=!records.TryGetValue(item.ArchiveId,out archive)||archive.Status=="cleaned";
                if(!remove&&archive.TaskId!="__catalog__"&&archive.TaskId!=item.TaskId)remove=true;
                if(!remove&&!stages.Contains(item.ArchiveId)&&(!Catalog.ArchiveHasCurrentFiles(item.ArchiveId)&&!Catalog.IsNodeArchive(item.ArchiveId)&&!archive.TaskId.StartsWith("__node__:",StringComparison.Ordinal)||archive.Status=="complete"&&archive.CurrentValidCopies>=archive.TargetCopies))remove=true;
                if(remove)removed.Add(item);
            }
            RemoveRepairEntries(queue,removed);
        }
        private static void RemoveRepairEntries(IList<ConfirmedRepair> queue,ISet<ConfirmedRepair> removed)
        {if(removed.Count==0)return;var list=queue as List<ConfirmedRepair>;if(list!=null)list.RemoveAll(item=>removed.Contains(item));else for(int index=queue.Count-1;index>=0;index--)if(removed.Contains(queue[index]))queue.RemoveAt(index);}
    }
}
