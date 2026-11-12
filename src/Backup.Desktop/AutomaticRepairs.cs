using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Backup.Core;
namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        private ISet<long> AwaitingUploadArchiveIds()
        {return new HashSet<long>(Catalog.PendingVolumeTransfers().Where(item=>!item.ConfirmationDamaged&&!item.MetadataMatched&&!item.Phase.StartsWith("repair-",StringComparison.Ordinal)&&item.Phase!="checking").Select(item=>item.ArchiveId));}
        internal IList<ArchiveSummary> KnownRepairArchives(WorkspaceDefinition workspace,ISet<string> taskIds)
        {
            var active=new HashSet<string>(workspace.Tasks.Where(task=>Catalog.TaskHasNodes(task.Id)&&(taskIds==null||taskIds.Contains(task.Id))).Select(task=>task.Id));var current=Catalog.CurrentArchiveIds();
            if(taskIds==null||taskIds.Contains("__catalog__"))active.Add("__catalog__");
            current.UnionWith(inspectedNodeArchives);
            foreach(var revision in Catalog.NodeRevisions().Where(n=>active.Contains(n.TaskId))){active.Add(Catalog.GetArchive(revision.ArchiveId).TaskId);current.Add(revision.ArchiveId);}
            var awaiting=AwaitingUploadArchiveIds();return Catalog.ListArchives(true).Where(archive=>active.Contains(archive.TaskId)&&!awaiting.Contains(archive.ArchiveId)&&archive.Status!="failed"&&archive.Status!="writing"&&archive.Status!="cleaned"&&(archive.CurrentValidCopies<archive.TargetCopies||archive.Status=="repair-required")&&current.Contains(archive.ArchiveId)).ToList();
        }
        private void SaveRepairQueue(IList<ConfirmedRepair> queue)
        {Catalog.SetSetting("confirmed_repairs_v1",JsonSettings.Encode(queue));}
        private int QueueKnownRepairs(WorkspaceDefinition workspace,ISet<string> taskIds)
        {
            var queue=PendingRepairs();int before=queue.Count;TrimRepairQueue(queue,taskIds);bool changed=queue.Count!=before;var known=new Dictionary<long,ConfirmedRepair>();foreach(var item in queue)if(!known.ContainsKey(item.ArchiveId))known.Add(item.ArchiveId,item);
            foreach(var archive in KnownRepairArchives(workspace,taskIds))
            {
                ConfirmedRepair item;if(!known.TryGetValue(archive.ArchiveId,out item)){item=new ConfirmedRepair{ArchiveId=archive.ArchiveId,TaskId=archive.TaskId};queue.Add(item);known.Add(item.ArchiveId,item);changed=true;}
                string owner=archive.TaskId;if(item.TaskId!=owner){item.TaskId=owner;changed=true;}
            }
            if(changed)SaveRepairQueue(queue);return queue.Count;
        }
        public IList<string> RepairKnownArchives(WorkspaceDefinition workspace,IEnumerable<string> taskIds,Action<string> report=null,bool deferRetirement=false)
        {
            var requested=new HashSet<string>(taskIds);var errors=new List<string>();
            foreach(var task in workspace.Tasks.Where(task=>requested.Contains(task.Id)&&task.RecoveryPendingConfirmation)){requested.Remove(task.Id);errors.Add(task.Name+"：导入的任务尚未确认，未自动修复。");}
            if(requested.Count==0)return errors;foreach(var revision in Catalog.NodeRevisions().Where(n=>requested.Contains(n.TaskId)))requested.Add(Catalog.GetArchive(revision.ArchiveId).TaskId);QueueKnownRepairs(workspace,requested);if(!PendingRepairs().Any(item=>requested.Contains(item.TaskId))&&!Catalog.InterruptedArchives(requested).Any(item=>item.Status=="damaged")&&Catalog.TemporaryProblemArchives(requested).Count==0)return errors;
            bool owns=ActiveBackup==null;if(owns)BeginBackup();
            try{if(report!=null)report("正在自动修复所选任务的已知异常备份包…");errors.AddRange(RunConfirmedRepairs(workspace,report,requested,null,null,deferRetirement,false));}
            finally{if(owns)EndBackup();}
            if(report!=null)foreach(string error in errors)report("自动修复未完成："+error);return errors;
        }
    }
    public sealed partial class MainWindowController
    {
        private Task<IList<string>> CheckAndRepairScheduledBackupsAsync(IList<string> taskIds)
        {return RunOperationAsync(OperationKind.Check,()=>CheckAndRepairScheduledBackupsCoreAsync(taskIds));}
        private async Task<IList<string>> CheckAndRepairScheduledBackupsCoreAsync(IList<string> taskIds)
        {
            if(busy)throw new InvalidOperationException("当前任务尚未结束。");await EnsureTaskFileListsAsync(taskIds,sourceReadGeneration);if(disposed||closeAfterWork)return new List<string>();Backend.BeginBackup();repairingBackups=true;SetBusy(true,true,"任务计划：检查备份完整性…");var report=OperationReporter("");
            Text("HistoryCheckSummary").Visibility=System.Windows.Visibility.Visible;SetHistoryCheckText("正在检查计划关联任务…",OperationState.Running);
            try
            {
                var result=await Task.Run(()=>Backend.CheckAllBackups(report,taskIds));var scope=new HashSet<string>(taskIds);int before=Backend.KnownRepairArchives(Workspace,scope).Count;
                SetBusy(true,true,"任务计划：修复备份完整性…");SetHistoryCheckText("检查已完成，正在修复计划关联任务…",OperationState.Running);
                var errors=result.Errors.ToList();errors.AddRange(await Task.Run(()=>Backend.RepairCheckedBackups(Workspace,scope,report)));Backend.ActiveBackup.Checkpoint(false);
                foreach(var archive in Backend.KnownRepairArchives(Workspace,scope))if(!errors.Any(error=>error.StartsWith("#"+archive.ArchiveId.ToString("D8"))))errors.Add("#"+archive.ArchiveId.ToString("D8")+"：仍有异常副本，保留待重试。");
                string summary=errors.Count==0?"检查完成：已自动修复 "+before+" 个异常备份包。":"操作未完成：检查与自动修复结束，以下项目待重试：\n"+string.Join("\n",errors);
                ApplyCommittedChanges();PublishHistoryCheckSummary(taskIds,result.Errors.Count,errors.Count);SetOperationText(summary,errors.Count==0?OperationState.Succeeded:OperationState.Failed);Log(summary);OperationProgress(100,false);return errors;
            }
            catch(OperationCanceledException){ApplyCommittedChanges();SetHistoryCheckText("检查与修复已停止，未完成项目保留待下次核对。",OperationState.Attention);SetOperationText(RepairStoppedNotice,OperationState.Cancelled);OperationProgress(0,false);throw;}
            catch{SetHistoryCheckText("检查与自动修复未完成，请查看错误信息后重试。",OperationState.Failed);throw;}
            finally{repairingBackups=false;Backend.EndBackup();SetBusy(false,false,null);}
        }
    }
}
