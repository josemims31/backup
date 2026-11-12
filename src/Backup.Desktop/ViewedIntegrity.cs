using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backup.Core;
namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        private void ObserveWarehouseDirectory(string id,string folder,IList<StorageEntry> entries,string error,CancellationToken cancellation)
        {
            var destination=Catalog.LoadTopology().Destinations.Single(item=>item.Id==id);
            var current=Catalog.CurrentArchiveIds();var interrupted=new HashSet<long>(Catalog.InterruptedArchives().Select(item=>item.ArchiveId));var expected=Catalog.WarehouseFolderObjects(id,ObjectStoragePath.Relative(destination,folder)).Where(item=>current.Contains(item.ArchiveId)||interrupted.Contains(item.ArchiveId)).ToList();
            var comparer=RepositoryModules.IsFileSystem(destination.Protocol)?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal;
            var listed=entries.GroupBy(entry=>entry.Name,comparer).ToDictionary(group=>group.Key,group=>group.First(),comparer);
            foreach(var group in expected.GroupBy(item=>item.ArchiveId))
            {
                cancellation.ThrowIfCancellationRequested();var findings=new List<IntegrityFinding>();
                foreach(var item in group)
                {
                    StorageEntry entry;bool found=listed.TryGetValue(item.Name,out entry);string state=error!=null?"unavailable: "+error:!found?"missing":entry.Directory||entry.Size>=0&&entry.Size!=item.Size?"size-or-name-mismatch":entry.Size<0?"unavailable: 仓库未提供文件大小":"present-size-matched";
                    findings.Add(new IntegrityFinding{Destination=id,Name=item.Name,ExpectedSize=item.Size,ActualSize=found?entry.Size:-1,Status=state,RequiresManualRepairConfirmation=state=="missing"||state=="size-or-name-mismatch"});
                }
                if(interrupted.Contains(group.Key))Catalog.RecordInterruptedIntegrity(group.Key,findings);else Catalog.RecordScopedIntegrity(group.Key,findings);
            }
            if(error==null)foreach(var owner in Catalog.PendingUploadStages().Where(stage=>stage.DestinationId==id&&Catalog.CanInspectUploadTemporaries(stage.ArchiveId)).GroupBy(stage=>stage.ArchiveId))if(Catalog.GetArchive(owner.Key).Status!="failed"&&Catalog.GetArchive(owner.Key).Status!="damaged"&&owner.Any(stage=>Catalog.UploadStages(id,stage.Name).Any(listed.ContainsKey)))Catalog.RecordTemporaryProblem(owner.Key,true);
        }
        public IList<IntegrityFinding> CheckViewedWarehouseArchive(BackupHistoryNode node,CancellationToken cancellation=default(CancellationToken))
        {
            while(node!=null&&!node.ArchiveContainer)node=node.Parent;
            if(node==null||node.ArchiveId<=0||node.Entry==null)return new List<IntegrityFinding>();
            long archiveId=node.ArchiveId;string id=node.DestinationId,folder=node.BrowseLocation,name=node.Entry.Name;var ticket=repositoryRuntime.BeginRead(id,folder);try{
            bool interrupted=Catalog.InterruptedArchives().Any(item=>item.ArchiveId==archiveId);var copies=(interrupted?Catalog.InterruptedCheckCopies(archiveId):Catalog.GetVerifiableCopies(archiveId)).Where(copy=>copy.DestinationId==id&&copy.Object.Name==name).ToList();if(copies.Count==0)return new List<IntegrityFinding>();
            IList<IntegrityFinding> findings;try
            {
                var adapter=OpenWarehouseLocation(id,folder,cancellation);
                try{findings=DestinationIntegrityChecker.Check(adapter,copies.Select(copy=>copy.Object),cancellation);}finally{var disposable=adapter as IDisposable;if(disposable!=null)disposable.Dispose();}
            }
            catch(OperationCanceledException){throw;}
            catch(Exception error)
            {if(!(error is IOException)&&!(error is UnauthorizedAccessException)&&!(error is NotSupportedException))throw;findings=copies.Select(copy=>new IntegrityFinding{Destination=copy.DestinationId,Name=copy.Object.Name,ExpectedSize=copy.Object.Size,ActualSize=-1,Status="unavailable: "+error.Message}).ToList();}
            cancellation.ThrowIfCancellationRequested();repositoryRuntime.CommitObservation(ticket,()=>{if(interrupted)Catalog.RecordInterruptedIntegrity(archiveId,findings);else Catalog.RecordScopedIntegrity(archiveId,findings);},cancellation);return findings;
            }finally{repositoryRuntime.EndRead(ticket);}
        }
    }
    public sealed partial class MainWindowController
    {
        private BackupHistoryNode inspectingWarehouseNode;private Task viewedIntegrityLoading=Task.FromResult(true);private int viewedIntegrityGeneration;
        public Task ViewedIntegrityLoading{get{return viewedIntegrityLoading;}}
        private void BeginViewedIntegrity(BackupHistoryNode node)
        {
            if(node==null||node.Entry==null||!node.Entry.Directory||node.ArchiveContainer||IsFolderHistoryView||disposed)return;
            if(inspectingWarehouseNode==node&&!viewedIntegrityLoading.IsCompleted)return;
            inspectingWarehouseNode=node;viewedIntegrityLoading=InspectViewedIntegrityAsync(node,++viewedIntegrityGeneration,browserInputVersion);
        }
        private async Task InspectViewedIntegrityAsync(BackupHistoryNode node,int generation,int inputVersion)
        {
            try
            {
                await Task.Yield();if(disposed||generation!=viewedIntegrityGeneration||IsFolderHistoryView)return;
                if(node.RawReadInputVersion!=inputVersion)await OpenWarehouseFolderAsync(node,true,false,inputVersion);else if(node.RawLoading!=null)await node.RawLoading;
            }
            catch(OperationCanceledException){}
            catch(Exception error){if(!disposed&&generation==viewedIntegrityGeneration)Text("HistoryDetails").Text="当前目录检查未完成，已保留原清单："+error.Message;}
        }
    }
}
