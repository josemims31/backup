using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Backup.Core;

namespace Backup.Desktop
{
    // Exact original locations are retained only while physical deletion is
    // incomplete. Startup, browsing and backup never execute this queue.
    public sealed class TaskDirectoryDelete
    {
        public string TaskId,Tag;
        public List<string> NodeIds=new List<string>();
        public List<TaskDirectoryTarget> Targets=new List<TaskDirectoryTarget>();
    }
    public sealed class TaskDirectoryTarget
    {
        public string DestinationId,Protocol,Location;
        public List<string> Roots=new List<string>();
    }
    public sealed partial class DesktopBackend
    {
        private const string TaskDirectoryDeleteKey="task_directory_deletes_v1";
        private List<TaskDirectoryDelete> PendingTaskDirectoryDeletes(){return JsonSettings.Decode(Catalog.GetSetting(TaskDirectoryDeleteKey),new List<TaskDirectoryDelete>());}
        private void SaveTaskDirectoryDeletes(List<TaskDirectoryDelete> queue){Catalog.SetSetting(TaskDirectoryDeleteKey,JsonSettings.Encode(queue));}
        private TaskDirectoryDelete TaskDeletePlan(BackupTaskDefinition task,TaskDeletionMetadata metadata)
        {
            var topology=Catalog.LoadTopology();var ids=new HashSet<string>(metadata.DestinationIds);
            foreach(string id in task.TargetIds.Where(id=>topology.Groups.Any(g=>g.Id==id)))ids.UnionWith(topology.ResolveDestinations(id));
            var plan=new TaskDirectoryDelete{TaskId=task.Id,Tag=metadata.Tag,NodeIds=metadata.NodeIds.ToList()};
            foreach(var destination in topology.Destinations.Where(d=>ids.Contains(d.Id)))
            {
                var target=new TaskDirectoryTarget{DestinationId=destination.Id,Protocol=destination.Protocol,Location=destination.Location};
                target.Roots.Add("data-"+metadata.Tag);if(!metadata.SharedMetadata)target.Roots.Add("node-"+metadata.Tag);
                plan.Targets.Add(target);
            }return plan;
        }
        private WorkspaceDefinition DeleteTaskDirectories(WorkspaceDefinition workspace,string id,Action<string> report)
        {
            if(ActiveBackup!=null)throw new InvalidOperationException("请先停止正在运行的备份或修复操作。");
            var draft=CopyWorkspace(workspace);var task=draft.Tasks.Single(t=>t.Id==id&&!t.Deleted);var metadata=Catalog.CaptureTaskDeletion(id);var queue=PendingTaskDirectoryDeletes();queue.Add(TaskDeletePlan(task,metadata));draft.Tasks.Remove(task);
            CommitSchedulePruning(draft,json=>Catalog.ForgetTaskBackupsAndSave(metadata,JsonSettings.Encode(draft),json,JsonSettings.Encode(PendingRepairs().Where(r=>r.TaskId!=id).ToList()),JsonSettings.Encode(queue)));
            LastCleanup=RunTaskDirectoryDeletes(queue,new HashSet<string>{id},report);
            if(metadata.SharedMetadata)LastCleanup.Errors.Add("历史版本包还被其他任务引用，已保留共享的版本包目录。");
            return draft;
        }
        private void TaskDeleteCommitted(DestinationDefinition destination,string folder,string name,bool directory)
        {PhysicalDeletionCommitted();repositoryRuntime.EntryCommitted(new RepositoryObjectResult{DestinationId=destination.Id,Folder=folder,Name=name,Directory=directory,Exists=false,IgnoreCase=RepositoryModules.IsFileSystem(destination.Protocol)});}
        private void DeleteTaskNodeDatabases(TaskDirectoryDelete plan,CleanupResult result)
        {
            try
            {
                string folder=Catalog.NodeDirectory(plan.TaskId);
                if(Path.GetFileName(folder)!="node-"+plan.Tag)throw new IOException("本地版本目录与待删除任务代号不一致。");
                if(Directory.Exists(folder))
                {
                    if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)throw new IOException("本地版本目录不能是链接。");
                    Directory.Delete(folder,true);
                }
                plan.NodeIds.Clear();
            }
            catch(IOException error){result.Errors.Add("本地版本库："+error.Message);}
            catch(UnauthorizedAccessException error){result.Errors.Add("本地版本库："+error.Message);}
        }
        private CleanupResult RunTaskDirectoryDeletes(List<TaskDirectoryDelete> queue,ISet<string> taskIds,Action<string> report)
        {
            var result=new CleanupResult();var definitions=Catalog.LoadTopology().Destinations.ToDictionary(d=>d.Id);
            try{foreach(var plan in queue.Where(p=>taskIds==null||taskIds.Contains(p.TaskId)).ToList())
            {
                if(!Regex.IsMatch(plan.Tag??"",@"\A[A-Za-z0-9]{4}\z"))throw new IOException("待清理任务代号无效。");
                DeleteTaskNodeDatabases(plan,result);SaveTaskDirectoryDeletes(queue);
                foreach(var target in plan.Targets.ToList())
                {
                    OperationCancellation.ThrowIfCancellationRequested();DestinationDefinition destination;
                    if(!definitions.TryGetValue(target.DestinationId,out destination)||destination.Protocol!=target.Protocol||!SameFolder(destination.Location,target.Location,RepositoryModules.IsFileSystem(target.Protocol))){result.Errors.Add(target.Location+"：原仓库已移除或路径已更换，未在新路径执行删除。");continue;}
                    foreach(string root in target.Roots.ToList())try
                    {
                        if(root!="data-"+plan.Tag&&root!="node-"+plan.Tag)throw new IOException("任务目录无效。");OperationCancellation.ThrowIfCancellationRequested();
                        string location=EntryLocation(destination,destination.Location,root);ValidateDeleteRequest(destination,new WarehouseDeleteRequest(destination.Id,destination.Location,root,location,true,0,0));
                        if(report!=null)report("正在删除任务目录："+root);var adapter=OpenWarehouseLocation(destination,destination.Location,OperationCancellation);
                        try{var removal=adapter as ITaskStorageRemoval;if(removal==null)throw new NotSupportedException("仓库不支持任务目录删除。");removal.DeleteDirectoryTree(root);}finally{DestinationAdapterRegistry.DisposeAdapter(adapter);}
                        TaskDeleteCommitted(destination,destination.Location,root,true);target.Roots.Remove(root);SaveTaskDirectoryDeletes(queue);
                    }catch(OperationCanceledException){throw;}catch(Exception error){if(!(error is IOException)&&!(error is UnauthorizedAccessException)&&!(error is NotSupportedException)&&!(error is ArgumentException))throw;result.Errors.Add(destination.Id+" / "+root+"："+error.Message);}
                    if(target.Roots.Count==0){plan.Targets.Remove(target);SaveTaskDirectoryDeletes(queue);}
                }
                if(plan.Targets.Count==0&&plan.NodeIds.Count==0){queue.Remove(plan);result.DeletedArchives++;SaveTaskDirectoryDeletes(queue);}
            }}catch(OperationCanceledException){result.Errors.Add("清理已停止；任务已删除，未完成的目录将在手动修复完整性时重试。");}
            finally{SaveTaskDirectoryDeletes(queue);FlushRepositoryResults();}
            result.PendingArchives=queue.Count(p=>taskIds==null||taskIds.Contains(p.TaskId));return result;
        }
        private IList<string> RetryTaskDirectoryDeletes(ISet<string> taskIds,Action<string> report,out bool attempted)
        {var queue=PendingTaskDirectoryDeletes();attempted=queue.Any(p=>taskIds==null||taskIds.Contains(p.TaskId));return attempted?RunTaskDirectoryDeletes(queue,taskIds,report).Errors:new List<string>();}
    }
}
