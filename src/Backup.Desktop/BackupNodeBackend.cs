using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backup.Core;
namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        private string NodePath(BackupNode node)
        {
            string id=node.Id;
            Guid value;if(!Guid.TryParseExact(id,"N",out value))throw new IOException("备份版本标识无效。");
            string directory=Catalog.NodeDirectory(node.TaskId);
            if(Directory.Exists(directory)&&(File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new IOException("备份版本目录不能是链接。");
            string path=Path.Combine(directory,id+".db");
            if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("备份版本数据库不能是链接。");return path;
        }
        private void CacheNode(BackupNode node,byte[] bytes)
        {
            string path=NodePath(node);Directory.CreateDirectory(Path.GetDirectoryName(path));string stage=path+".new-"+Guid.NewGuid().ToString("N");
            try{using(var stream=new FileStream(stage,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(bytes,0,bytes.Length);stream.Flush(true);}if(File.Exists(path))File.Replace(stage,path,null);else File.Move(stage,path);}
            finally{if(File.Exists(stage))File.Delete(stage);}
        }
        private NodeFileDatabase ReadCachedNodeDatabase(BackupNode node)
        {
            string path=NodePath(node);if(File.Exists(path))try{return Catalog.ReadRegisteredNodeDatabase(File.ReadAllBytes(path),node);}catch(IOException){}return RecoverMovedNodeCache(node);
        }
        private void RequireRegisteredNode(BackupNode node)
        {
            if(!Catalog.GetNodes(node.TaskId).Any(current=>current.Id==node.Id&&current.ArchiveId==node.ArchiveId&&current.Revision==node.Revision&&current.Digest==node.Digest))throw new IOException("备份版本已删除或变更，不再读取或恢复该版本。");
        }
        public NodeFileDatabase ReadNodeDatabase(BackupNode node,bool remote=false,MemoryReadCache cache=null,bool findMoved=true,string warehouseId=null)
        {
            if(node.LocalOnly)return Catalog.ReadLocalBackupNode(node.Id);
            RequireRegisteredNode(node);
#if UI_TEST
            if(TestNodeDatabaseRead!=null)TestNodeDatabaseRead(node);
#endif
            if(!remote){var cached=ReadCachedNodeDatabase(node);if(cached!=null)return cached;}
            if(node.DatabaseBytes<=0||node.DatabaseBytes>int.MaxValue||node.DatabaseBytes>MemoryLimitBytes/3)throw new IOException("备份版本数据库超出当前内存预算，请提高内存限制。");
            var request=new RestoreRequest{RelativePath="node.db",File=new FileHistoryRow{ArchiveId=node.ArchiveId,Path="node.db",EntryPath=node.EntryPath,Size=node.DatabaseBytes}};
            using((OperationMemory??new MemoryBudget(MemoryLimitBytes)).Reserve(node.DatabaseBytes*2))
            {
                byte[] bytes;
                if(cache==null&&warehouseId==null)using(var restored=Materialize(new[]{request}))bytes=restored.ReadFileBytes("node.db");
                else using(var output=new MemoryStream((int)node.DatabaseBytes)){var allowed=warehouseId==null?null:new HashSet<string>(Catalog.LoadTopology().ResolveDestinations(warehouseId,false));ExtractSelection(new[]{request},null,output,cache,DownloadCancellation,null,new SmartDownloadRouting(),findMoved:findMoved,allowedDestinations:allowed);bytes=output.ToArray();}
                var result=Catalog.ReadRegisteredNodeDatabase(bytes,node);
                if(result.Revision!=node.Revision)throw new IOException("备份版本数据库修订号不匹配。");
                // Only the current revision occupies the local node path.
                RequireRegisteredNode(node);CacheNode(node,bytes);return result;
            }
        }
        public IList<FileHistoryRow> ReadNodeFiles(string id)
        {if(string.IsNullOrEmpty(id))return new List<FileHistoryRow>();var node=FindBrowseNode(id);return Catalog.ReadNodeHistory(node,ReadNodeDatabase(node));}
        private sealed class BrowseNodeSnapshot
        {internal string Identity,Path;internal long Length,Modified,Used,Bytes;internal NodeFileDatabase Files;}
        private readonly object browseNodeGate=new object();
        private readonly Dictionary<string,BrowseNodeSnapshot> browseNodeSnapshots=new Dictionary<string,BrowseNodeSnapshot>();
        private long browseNodeUse;
        private void ClearBrowseNodeSnapshots(){lock(browseNodeGate)browseNodeSnapshots.Clear();}
        internal IList<FileBackupView> LatestFileViewsForDisplay(string taskId)
        {
            var views=Catalog.GetLatestFileViews(taskId);if(views.Count>0||string.IsNullOrEmpty(taskId))return views;
            var latest=Catalog.GetNodes(taskId).FirstOrDefault();if(latest==null)return views;var files=BrowseNodeFiles(latest);
            return files==null?views:Catalog.GetNodeFileViews(taskId,files);
        }
        private NodeFileDatabase BrowseNodeFiles(BackupNode node)
        {
            string identity=HistoricalNodeKey(node),path=node.LocalOnly?null:NodePath(node);long length=0,modified=0;
            if(path!=null){var info=new FileInfo(path);if(info.Exists){length=info.Length;modified=info.LastWriteTimeUtc.Ticks;}}
            long budget=Math.Max(1024*1024,Math.Min(64L*1024*1024,MemoryLimitBytes/8));
            lock(browseNodeGate){while(browseNodeSnapshots.Count>0&&browseNodeSnapshots.Values.Sum(value=>value.Bytes)>budget)browseNodeSnapshots.Remove(browseNodeSnapshots.OrderBy(pair=>pair.Value.Used).First().Key);BrowseNodeSnapshot cached;if(browseNodeSnapshots.TryGetValue(node.TaskId,out cached)&&cached.Identity==identity&&cached.Path==path&&cached.Length==length&&cached.Modified==modified){cached.Used=++browseNodeUse;return cached.Files;}}
            var files=node.LocalOnly?Catalog.ReadLocalBackupNode(node.Id):ReadCachedNodeDatabase(node);if(files==null)return null;
            if(path!=null){var info=new FileInfo(path);if(info.Exists){length=info.Length;modified=info.LastWriteTimeUtc.Ticks;}}
            lock(browseNodeGate){browseNodeSnapshots.Remove(node.TaskId);if(node.DatabaseBytes<=budget){while(browseNodeSnapshots.Count>0&&browseNodeSnapshots.Values.Sum(value=>value.Bytes)>budget-node.DatabaseBytes)browseNodeSnapshots.Remove(browseNodeSnapshots.OrderBy(pair=>pair.Value.Used).First().Key);browseNodeSnapshots[node.TaskId]=new BrowseNodeSnapshot{Identity=identity,Path=path,Length=length,Modified=modified,Bytes=node.DatabaseBytes,Files=files,Used=++browseNodeUse};}}return files;
        }
        public IList<FileHistoryRow> ReadNodeFilesForBrowse(string id,string warehouseId,out bool needsWarehouse)
        {
            needsWarehouse=false;if(string.IsNullOrEmpty(id))return new List<FileHistoryRow>();var node=FindBrowseNode(id);var cached=BrowseNodeFiles(node);
            if(cached!=null)return Catalog.ReadNodeHistory(node,cached);
            if(string.IsNullOrEmpty(warehouseId)){needsWarehouse=true;return new List<FileHistoryRow>();}
            return Catalog.ReadNodeHistory(node,ReadNodeDatabase(node,warehouseId:warehouseId));
        }
        private void EnsureTaskNodeBasis(string taskId){EnsureTaskNodeBasis(taskId,null,null);}
        private void EnsureTaskNodeBasis(string taskId,BackupNode compared,NodeFileDatabase files)
        {
            var latest=Catalog.GetNodes(taskId).FirstOrDefault();string expected=latest==null?"":latest.Id+":"+latest.Revision;
            if(Catalog.WorkingNodeBasis(taskId)==expected)return;
            Catalog.LoadWorkingNode(taskId,latest==null?null:compared!=null&&files!=null&&HistoricalNodeKey(latest)==HistoricalNodeKey(compared)?files:ReadNodeDatabase(latest));
        }
        private BackupNode UploadNode(BackupTaskDefinition task,NodeFileDatabase files,Action<string> report)
        {
            byte[] bytes=files.Serialize();var node=new BackupNode{Id=files.Id,TaskId=task.Id,CreatedUtc=files.CreatedUtc,Revision=files.Revision,Digest=NodeFileDatabase.Hash(bytes),DatabaseBytes=bytes.Length};
            using(OperationMemory.Reserve(bytes.LongLength*2))
            {
                var source=SourceItem.FromMemory(bytes,"node.db");long available=OperationMemory.AvailableBytes;
                int volume=(int)Math.Min(PreferredVolumeMiB*1024L*1024,available/3);volume-=volume%4096;
                if(volume<4096)throw new IOException("内存不足，备份版本数据库尚未发布。");
                var pipeline=new BackupPipelineView();GroupedBackupResult uploaded;
                using(var buffers=new VolumeBufferPool(MemoryLimitBytes,volume,ActiveBackup,OperationMemory))
                    uploaded=new GroupedBackupEngine(Catalog,Adapters(),Path.Combine(BaseDirectory,"7za.dll")).BackupForTask(new[]{source},task.RequiredCopies,ResolveBackupPassword(task),volume,"LZMA2",null,null,p=>{pipeline.Observe(p);ReportTransferNotice(p);if(report!=null)report(pipeline.Text("正在上传备份版本数据库"));},"__node__:"+node.Id,task.TargetIds,ActiveBackup,true,MemoryLimitBytes,buffers,task.Id);
                if(uploaded.Archive.CurrentValidCopies<uploaded.Archive.TargetCopies)throw new IOException("备份版本数据库未达到所选仓库要求，保留已有备份版本；已完成的数据包等待后续备份或完整性检查处理。");
                node.ArchiveId=uploaded.Archive.ArchiveId;
                Catalog.RegisterNodeRevision(node);
                Catalog.PublishNode(node,files.Files.Select(f=>f.ArchiveId));Catalog.ClearNodeGarbageReview();
                try{CacheNode(node,bytes);}catch(IOException error){if(report!=null)report("备份版本已在仓库保存，本地备份版本副本待重新读取："+error.Message);}
            }return node;
        }
        private IList<string> DeleteNodeMetadata(IEnumerable<BackupNode> revisions,Action<string> report)
        {
            var errors=new List<string>();var maintenance=new ArchiveMaintenance(Catalog,Adapters()){Control=ActiveBackup,Report=report};
            foreach(var revision in revisions)
            {
                if(Catalog.NodeMetadataShared(revision.ArchiveId,revision.Id)){Catalog.ForgetRetiredNodeRevision(revision.Id,revision.ArchiveId);continue;}
                if(ActiveBackup!=null)ActiveBackup.Checkpoint();Catalog.PrepareNodeMetadataDeletion(revision.ArchiveId);
                try{errors.AddRange(maintenance.DeleteReviewedObsoleteArchive(revision.ArchiveId));Catalog.ForgetRetiredNodeRevision(revision.Id,revision.ArchiveId);}finally{Catalog.EndNodeMetadataDeletion(revision.ArchiveId);}
            }return errors;
        }
        public void DeleteBackupNode(string id,Action<string> report=null,bool synchronize=true,bool unregisterUnreferenced=true)
        {
            var local=Catalog.GetLocalBackupNodes().FirstOrDefault(n=>n.Id==id);if(local!=null){Catalog.ForgetLocalBackupNode(local.TaskId);if(unregisterUnreferenced)Catalog.MarkLastVersionDeleted(local.TaskId);return;}
            bool owns=ActiveBackup==null;if(owns)BeginBackup();
            try
            {
                var node=Catalog.FindNode(id);bool wasLatest=Catalog.GetNodes(node.TaskId).First().Id==id;Catalog.ClearNodeGarbageReview();
                var errors=DeleteNodeMetadata(Catalog.NodeRevisions().Where(r=>r.Id==id).Concat(new[]{node}).GroupBy(r=>r.ArchiveId).Select(g=>g.First()).ToList(),report);
                Catalog.ForgetNode(id);
                if(unregisterUnreferenced)Catalog.MarkLastVersionDeleted(node.TaskId);
                try{string path=NodePath(node);if(File.Exists(path))File.Delete(path);}catch(IOException error){if(report!=null)report("本地版本缓存清理待重试："+error.Message);}
                if(errors.Count>0&&report!=null)report("备份版本已移除；远程残留文件将在修复完整性时重试删除："+string.Join("；",errors));
                // Deleting the latest node must also change the next incremental basis.
                if(wasLatest)
                {
                    Catalog.SetWorkingNodeBasis(node.TaskId,"pending-refresh");
                    if(!Catalog.TaskHasNodes(node.TaskId))Catalog.LoadWorkingNode(node.TaskId,null);
                    else if(synchronize)try{EnsureTaskNodeBasis(node.TaskId);}catch(IOException error){if(report!=null)report("备份版本已删除，当前清单读取待重试："+error.Message);}
                }
                if(synchronize)BackupDatabase(null,report);
            }
            finally{if(owns)EndBackup();}
        }
        public void DeleteNodeFiles(string id,IEnumerable<string> paths,Action<string> report=null)
        {
            var local=Catalog.GetLocalBackupNodes().FirstOrDefault(n=>n.Id==id);if(local!=null){var files=Catalog.ReadLocalBackupNode(id);var removed=new HashSet<string>(paths,StringComparer.Ordinal);if(files.Files.RemoveAll(f=>removed.Contains(f.Path))>0){files.Revision=checked(files.Revision+1);Catalog.UpdateLocalBackupNode(local.TaskId,files);}return;}
            bool owns=ActiveBackup==null;if(owns)BeginBackup();
            try
            {
                var old=Catalog.FindNode(id);var task=WorkspaceMigration.Load(Catalog).Tasks.Single(t=>t.Id==old.TaskId);
                if(task.Deleted){task=JsonSettings.Decode(JsonSettings.Encode(task),new BackupTaskDefinition());task.UseCustomPassword=true;task.Password=Catalog.GetArchivePassword(old.ArchiveId);}
                var files=ReadNodeDatabase(old);var removed=new HashSet<string>(paths,StringComparer.Ordinal);if(files.Files.RemoveAll(f=>removed.Contains(f.Path))==0)return;
                files.Revision=checked(old.Revision+1);var current=UploadNode(task,files,report);
                if(Catalog.GetNodes(task.Id).First().Id==id)Catalog.LoadWorkingNode(task.Id,files);
                var errors=DeleteNodeMetadata(Catalog.NodeRevisions().Where(r=>r.Id==id&&r.ArchiveId!=current.ArchiveId).ToList(),report);
                if(report!=null&&errors.Count>0)report("文件已从所选备份版本移除；远程残留文件将在修复完整性时重试删除："+string.Join("；",errors));
                BackupDatabase(null,report);
            }
            finally{if(owns)EndBackup();}
        }
        private IList<BackupNode> RecyclableTaskNodes(BackupTaskDefinition task,DateTime nowUtc,bool enforceSavedTime)
        {
            DateTime? cutoff=enforceSavedTime?(DateTime?)SmartRecycle.Cutoff(nowUtc,task.SavedTimeValue,task.SavedTimeUnit):null;
            var nodes=Catalog.GetNodes(task.Id);var keep=SmartRecycle.Retained(nodes,nowUtc,cutoff,true);return nodes.Where(n=>!keep.Contains(n.Id)).OrderBy(n=>n.CreatedUtc).ToList();
        }
        public IList<string> RecycleTaskNodes(BackupTaskDefinition task,DateTime nowUtc,Action<string> report=null,bool enforceSavedTime=true)
        {
            var errors=new List<string>();foreach(var node in RecyclableTaskNodes(task,nowUtc,enforceSavedTime))
                try{if(report!=null)report("正在轮换任务备份版本："+node);DeleteBackupNode(node.Id,report,false);}
                catch(IOException error){errors.Add(node+"："+error.Message);}
            return errors;
        }
        private readonly HashSet<long> inspectedNodeArchives=new HashSet<long>();
        private readonly Dictionary<string,ISet<long>> inspectedHistoricalArchives=new Dictionary<string,ISet<long>>();
        private readonly Dictionary<string,string> inspectedHistoricalSignatures=new Dictionary<string,string>();
        private static string HistoricalNodeKey(BackupNode node){return node.Id+":"+node.ArchiveId+":"+node.Revision+":"+node.Digest;}
        private static string HistoricalSignature(IEnumerable<BackupNode> nodes){return string.Join("\n",nodes.Select(HistoricalNodeKey).OrderBy(key=>key,StringComparer.Ordinal));}
        private bool InspectNodeReferences(ISet<string> taskIds,IList<string> errors,Action<string> report)
        {
            inspectedNodeArchives.Clear();inspectedNodeArchives.UnionWith(Catalog.LocalBackupNodeReferences());inspectedHistoricalArchives.Clear();inspectedHistoricalSignatures.Clear();bool complete=true;
            var archives=Catalog.ListArchives(true).ToDictionary(a=>a.ArchiveId);
            var nodes=Catalog.GetNodes().Where(n=>taskIds==null||taskIds.Contains(n.TaskId)).ToList();var read=new HashSet<string>();
            using(var cache=new MemoryReadCache(Math.Max(ArchiveObjectIO.MaximumVolumeBytes,MemoryLimitBytes/3),TransferMemory))
            foreach(var node in nodes)
            {
                OperationCancellation.ThrowIfCancellationRequested();
                try{
                    if(report!=null)report("正在读取备份版本数据库并核对引用："+node.Id);
                    var files=ReadNodeDatabase(node,cache:cache);
                    foreach(var file in files.Files){ArchiveSummary archive;if(!archives.TryGetValue(file.ArchiveId,out archive)||archive.TaskId!=node.TaskId||archive.Status=="cleaned")throw new IOException("备份版本含有无效的数据包引用。");inspectedNodeArchives.Add(file.ArchiveId);}
                    ISet<long> references;if(!inspectedHistoricalArchives.TryGetValue(node.TaskId,out references))inspectedHistoricalArchives[node.TaskId]=references=new HashSet<long>();references.UnionWith(files.Files.Select(file=>file.ArchiveId));read.Add(HistoricalNodeKey(node));
                }
                catch(Exception error){if(!(error is IOException)&&!(error is UnauthorizedAccessException)&&!(error is NotSupportedException))throw;complete=false;errors.Add(node+"：备份版本清单读取失败，已保留已有文件及引用："+error.Message);}
            }
            foreach(var group in nodes.GroupBy(node=>node.TaskId))if(group.All(node=>read.Contains(HistoricalNodeKey(node))))inspectedHistoricalSignatures[group.Key]=HistoricalSignature(group);
            return complete;
        }
        private bool RebuildNodePackageFromCache(BackupNode damaged,BackupTaskDefinition task,Action<string> report)
        {
            var current=Catalog.GetNodes(task.Id).FirstOrDefault(n=>n.Id==damaged.Id&&n.ArchiveId==damaged.ArchiveId);if(current==null||Catalog.HasOutstandingTransfer(damaged.ArchiveId))return false;
            var states=Catalog.IntegrityCopyStates(damaged.ArchiveId).Values;
            if(states.Any(state=>state=="present"||state.StartsWith("unavailable",StringComparison.Ordinal)))return false;
            var files=ReadCachedNodeDatabase(current);if(files==null||files.Revision!=current.Revision)return false;
            var repairTask=JsonSettings.Decode(JsonSettings.Encode(task),new BackupTaskDefinition());repairTask.UseCustomPassword=true;repairTask.Password=Catalog.GetArchivePassword(damaged.ArchiveId);
            if(report!=null)report("正在使用已校验的本地数据库重建备份版本："+current);
            UploadNode(repairTask,files,report);
            var errors=DeleteNodeMetadata(new[]{damaged},report);
            if(lastIntegrityReview!=null){lastIntegrityReview.Findings.RemoveAll(f=>f.ArchiveId==damaged.ArchiveId);lastIntegrityReview.ObsoleteIds.Remove(damaged.ArchiveId);}
            if(report!=null)foreach(string error in errors)report("旧版本数据库残留待清理："+error);
            return true;
        }
        private IList<string> RetryRetiredNodeDatabases(Action<string> report)
        {
            if(Catalog.PendingVolumeTransfers().Count>0||Catalog.PendingUploadStages().Count>0)return new List<string>();
            var nodes=Catalog.GetNodes().ToDictionary(n=>n.Id);var retired=Catalog.NodeRevisions().Where(r=>!nodes.ContainsKey(r.Id)||nodes[r.Id].ArchiveId!=r.ArchiveId&&Catalog.GetArchive(nodes[r.Id].ArchiveId).Status!="cleaned").ToList();
            if(retired.Count==0)return new List<string>();
            var errors=DeleteNodeMetadata(retired,report);if(errors.Count>0&&report!=null)report("旧版本登记已移除；远程残留文件将在修复完整性时重试删除："+string.Join("；",errors));return new List<string>();
        }
        // All retained databases have already been read once by the inspection.
        private ISet<long> MarkUnreferencedNodeData(ISet<long> referenced)
        {
            Catalog.ClearNodeGarbageReview();
            if(Catalog.HasStorageGrowth())throw new IOException("目录扩展尚未完成，本次保留旧包；下次写入该目录时继续完成扩展。");
            if(Catalog.PendingVolumeTransfers().Count>0||Catalog.PendingUploadStages().Count>0)throw new IOException("仍有上传结果或临时文件待确认，本次跳过空间回收。");
            var revisions=Catalog.NodeRevisions();var metadata=new HashSet<string>(revisions.Select(n=>n.Id+":"+n.ArchiveId));
            foreach(var node in Catalog.GetNodes())
                if(!metadata.Contains(node.Id+":"+node.ArchiveId))throw new IOException("备份版本数据库副本未确认，本次跳过空间回收："+node);
            // Checking may expire local registrations, but only explicit repair
            // may remove the corresponding remote objects.
            try{Catalog.AllowNodeGarbage(referenced);return new HashSet<long>(Catalog.GetGarbageCandidates(false));}
            finally{Catalog.ClearNodeGarbageReview();}
        }
    }
}
