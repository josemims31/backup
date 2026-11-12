using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backup.Core;
namespace Backup.Desktop
{
    public sealed class WarehouseCompactionResult
    {
        public int DataPackages,DatabasePackages,NewDataPackages,NewDatabasePackages,Versions;
        public CleanupResult Cleanup=new CleanupResult();
        public readonly List<string> Errors=new List<string>();
        internal readonly HashSet<long> MergedDatabaseIds=new HashSet<long>();
    }
    internal sealed class RepackNode
    {
        internal BackupNode Node;
        internal HashSet<long> References;
    }
    internal sealed class RepackPolicy
    {
        internal List<string> Roots;
        internal int Copies;
        internal string Password;
        internal string Key {get{return string.Join("|",Roots)+"\0"+Copies+"\0"+Password;}}
    }
    internal sealed class RepackedFile
    {
        internal long ArchiveId;
        internal string EntryPath;
    }
    public sealed partial class DesktopBackend
    {
        private static string RepackFileKey(long id,string path){return id+":"+path;}
        private static string RepackFileKey(NodeFile file){return RepackFileKey(file.ArchiveId,!string.IsNullOrEmpty(file.ContentPath)?file.ContentPath:!string.IsNullOrEmpty(file.EntryPath)?file.EntryPath:file.Path);}
        private static string RepackEntryPath(NodeFile file){return string.IsNullOrEmpty(file.EntryPath)?file.Path:file.EntryPath;}
        private static List<List<NodeFile>> RepackFileBatches(IList<NodeFile> files)
        {
            var batches=new List<List<NodeFile>>();var entries=new List<HashSet<string>>();var directories=new List<HashSet<string>>();var next=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
            foreach(var file in files)
            {
                string path=RepackEntryPath(file);SafeRestorePath(Path.GetPathRoot(AppDomain.CurrentDomain.BaseDirectory),path);path=path.Replace('/','\\');
                var parents=new List<string>();for(int end=path.IndexOf('\\');end>=0;end=path.IndexOf('\\',end+1))parents.Add(path.Substring(0,end));
                int first;next.TryGetValue(path,out first);
                for(int index=first;;index++)
                {
                    if(index==batches.Count){batches.Add(new List<NodeFile>());entries.Add(new HashSet<string>(StringComparer.OrdinalIgnoreCase));directories.Add(new HashSet<string>(StringComparer.OrdinalIgnoreCase));}
                    if(entries[index].Contains(path)||directories[index].Contains(path)||parents.Any(parent=>entries[index].Contains(parent)))continue;
                    batches[index].Add(file);entries[index].Add(path);directories[index].UnionWith(parents);next[path]=index+1;break;
                }
            }
            return batches;
        }
        private RepackPolicy OriginalRepackPolicy(ArchiveSummary archive)
        {
            var roots=Catalog.GetReplicaPools(archive.ArchiveId).Select(p=>p.GroupId).Distinct().OrderBy(id=>id,StringComparer.Ordinal).ToList();
            if(roots.Count==0)roots=Catalog.VolumePlacements(archive.ArchiveId).Where(p=>!p.GroupId.StartsWith("transfer:",StringComparison.Ordinal)).Select(p=>p.GroupId).Distinct().OrderBy(id=>id,StringComparer.Ordinal).ToList();
            if(roots.Count==0)throw new IOException("压缩包缺少原始仓库策略："+archive.ArchiveName);
            return new RepackPolicy{Roots=roots,Copies=archive.TargetCopies,Password=Catalog.GetArchivePassword(archive.ArchiveId)};
        }
        private void RepackCheckpoint(string point)
        {
            CompactionPoint(point);OperationCancellation.ThrowIfCancellationRequested();
            if(StopRequested)throw new OperationCanceledException("整理已停止，尚未替换的旧包保留。");
        }
        private void VerifyRepackedArchive(long id,Action<string> report)
        {
            RepackCheckpoint("verify-repacked");var archive=Catalog.GetArchive(id);
            if(archive.Status!="complete"||archive.CurrentValidCopies<archive.TargetCopies||Catalog.HasOutstandingTransfer(id))throw new IOException("新包未满足原有副本要求，旧包保留："+archive.ArchiveName);
            var definitions=Catalog.LoadTopology().Destinations.ToDictionary(d=>d.Id);var adapters=Adapters();
            using(adapters.BeginScope())foreach(var copy in Catalog.GetCopies(id))
            {
                if(report!=null)report("正在核对整理后的分卷："+copy.Object.Name);
                RepackCheckpoint("verify-copy");var target=adapters.Create(definitions[copy.DestinationId]);
                var info=target.Stat(copy.Object.Name);if(!info.Exists||info.Name!=copy.Object.Name||info.Size!=copy.Object.Size)throw new IOException("新包的文件名或大小不符，旧包保留："+copy.Object.Name);
            }
        }
        private GroupedBackupResult WriteRepackedArchive(IList<SourceItem> sources,RepackPolicy policy,string taskId,int volume,Action<string> report,string namingTaskId=null)
        {
            RepackCheckpoint("write-repacked");var pipeline=new BackupPipelineView();
            volume=(int)Math.Min(volume,OperationMemory.AvailableBytes/3);volume-=volume%4096;if(volume<4096)throw new IOException("内存不足，旧压缩包保留。");
            try{using(var buffers=new VolumeBufferPool(MemoryLimitBytes,volume,ActiveBackup,OperationMemory))
                return new GroupedBackupEngine(Catalog,Adapters(),Path.Combine(BaseDirectory,"7za.dll")).BackupForTask(sources,policy.Copies,policy.Password,volume,"LZMA2",null,null,p=>{pipeline.Observe(p);ReportTransferNotice(p);if(report!=null)report(pipeline.Text("正在整理压缩包"));},taskId,policy.Roots,ActiveBackup,true,MemoryLimitBytes,buffers,namingTaskId);}
            finally{foreach(var archive in Catalog.ListArchives(true).Where(a=>a.TaskId==taskId))Catalog.StageRepackedArchive(archive.ArchiveId);}
        }
        private static IEnumerable<List<ArchiveSummary>> RepackBatches(IEnumerable<ArchiveSummary> archives,long limit)
        {
            var batch=new List<ArchiveSummary>();long bytes=0;
            foreach(var archive in archives.OrderBy(a=>a.ActualBytes).ThenBy(a=>a.ArchiveId))
            {
                if(batch.Count>0&&bytes>limit-archive.ActualBytes){yield return batch;batch=new List<ArchiveSummary>();bytes=0;}
                batch.Add(archive);bytes+=archive.ActualBytes;
            }
            if(batch.Count>0)yield return batch;
        }
        public WarehouseCompactionResult CompactAllArchives(Action<string> report=null,Action<CleanupProgress> cleanupProgress=null)
        {using(BeginMaintenanceSearch())return CompactAllArchivesCore(report,cleanupProgress);}
        private WarehouseCompactionResult CompactAllArchivesCore(Action<string> report,Action<CleanupProgress> cleanupProgress)
        {
            bool owns=ActiveBackup==null;if(owns)BeginBackup();var result=new WarehouseCompactionResult();
            var touched=new HashSet<long>();
            try
            {
                Catalog.RefreshLocalBackupNodes();var localReferences=Catalog.LocalBackupNodeReferences();
                var archives=Catalog.ListArchives(true).ToDictionary(a=>a.ArchiveId);int volume=(PreferredVolumeMiB)*1024*1024;long limit=volume*96L/100;
                var candidates=archives.Values.Where(a=>a.TaskId!="__catalog__"&&!a.TaskId.StartsWith("__",StringComparison.Ordinal)&&a.ActualBytes>0&&a.ActualBytes<limit&&a.CurrentValidCopies>=a.TargetCopies&&Catalog.CanRepackArchive(a.ArchiveId)).ToDictionary(a=>a.ArchiveId);
                foreach(long id in localReferences)candidates.Remove(id);
                var states=new List<RepackNode>();var files=new Dictionary<string,NodeFile>(StringComparer.Ordinal);bool complete=true;
                // Each logical version is read once. A shared physical node
                // package uses this same bounded encrypted-volume cache.
                using(var cache=new MemoryReadCache(Math.Max(ArchiveObjectIO.MaximumVolumeBytes,MemoryLimitBytes/3),TransferMemory))
                foreach(var node in Catalog.GetNodes())
                {
                    RepackCheckpoint("read-node");if(report!=null)report("正在读取备份版本："+node);
                    try
                    {
                        var db=ReadNodeDatabase(node,false,cache,true);var state=new RepackNode{Node=node,References=new HashSet<long>(db.Files.Select(f=>f.ArchiveId))};
                        foreach(var file in db.Files)
                        {
                            ArchiveSummary archive;if(!archives.TryGetValue(file.ArchiveId,out archive)||archive.TaskId!=node.TaskId||archive.Status=="cleaned")throw new IOException("备份版本含有无效的数据包引用。");
                            if(!candidates.ContainsKey(file.ArchiveId))continue;string key=RepackFileKey(file);NodeFile prior;
                            if(files.TryGetValue(key,out prior)&&(prior.Size!=file.Size||prior.Modified!=file.Modified||prior.EntryPath!=file.EntryPath))throw new IOException("同一历史文件的记录不一致。");files[key]=file;
                        }
                        states.Add(state);
                    }
                    catch(Exception error){if(!(error is IOException)&&!(error is UnauthorizedAccessException))throw;complete=false;result.Errors.Add(node+"："+error.Message);}
                }
                var lockedData=new HashSet<long>(states.Where(s=>!Catalog.CanRepackArchive(s.Node.ArchiveId)).SelectMany(s=>s.References));
                foreach(long id in lockedData)candidates.Remove(id);
                files=files.Where(p=>candidates.ContainsKey(p.Value.ArchiveId)).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
                var replacements=new Dictionary<string,RepackedFile>(StringComparer.Ordinal);var oldData=new HashSet<long>();
                var filesByArchive=files.Values.ToLookup(file=>file.ArchiveId);
                var policies=candidates.Values.Where(a=>filesByArchive.Contains(a.ArchiveId)).Select(a=>new{Archive=a,Policy=OriginalRepackPolicy(a)}).GroupBy(p=>p.Archive.TaskId+"\0"+p.Policy.Key).ToList();
                foreach(var group in policies)foreach(var batch in RepackBatches(group.Select(p=>p.Archive),limit))
                {
                    RepackCheckpoint("data-batch");var oldIds=new HashSet<long>(batch.Select(a=>a.ArchiveId));var selected=oldIds.SelectMany(id=>filesByArchive[id]).OrderBy(f=>f.ArchiveId).ThenBy(f=>f.Path,StringComparer.Ordinal).ToList();
                    try
                    {
                        // Different revisions of one path stay in separate packages.
                        // Every package remains directly extractable with original names.
                        var fileBatches=RepackFileBatches(selected);
                        bool renamed=selected.Any(file=>!string.IsNullOrEmpty(file.ContentPath)&&!string.Equals(file.ContentPath.Replace('/','\\'),RepackEntryPath(file).Replace('/','\\'),StringComparison.Ordinal));
                        if(fileBatches.Count>=batch.Count&&!renamed)continue;
                        var pending=new Dictionary<string,RepackedFile>(StringComparer.Ordinal);
                        using(var reservation=OperationMemory.Reserve(volume))using(var cache=new MemoryReadCache(volume))
                        foreach(var fileBatch in fileBatches)
                        {
                            using(var readers=new RepackArchiveReaders(this,cache,report)){var sources=new List<SourceItem>();
                            foreach(var file in fileBatch)
                            {
                                string entry=RepackEntryPath(file);var request=new RestoreRequest{RelativePath="file",File=new FileHistoryRow{ArchiveId=file.ArchiveId,Path=file.Path,EntryPath=file.EntryPath,ContentPath=file.ContentPath,Size=file.Size,Modified=file.Modified}};
                                var source=SourceItem.FromStream(()=>new ProducerReadStream(file.Size,(output,cancel)=>readers.Extract(request,output,cancel),readers.Cancellation),file.Size,entry);source.Modified=file.Modified;sources.Add(source);
                            }
                            var uploaded=WriteRepackedArchive(sources,group.First().Policy,"__repack__:"+batch[0].TaskId,volume,report);touched.Add(uploaded.Archive.ArchiveId);Catalog.StageRepackedArchive(uploaded.Archive.ArchiveId);VerifyRepackedArchive(uploaded.Archive.ArchiveId,report);Catalog.AdoptRepackedData(uploaded.Archive.ArchiveId,batch[0].TaskId);
                            for(int index=0;index<fileBatch.Count;index++)pending[RepackFileKey(fileBatch[index])]=new RepackedFile{ArchiveId=uploaded.Archive.ArchiveId,EntryPath=sources[index].StoredPath};}
                        }
                        foreach(var pair in pending)replacements[pair.Key]=pair.Value;
                        oldData.UnionWith(oldIds);touched.UnionWith(oldIds);result.DataPackages+=batch.Count;result.NewDataPackages+=fileBatches.Count;
                    }
                    catch(Exception error){if(!(error is IOException)&&!(error is UnauthorizedAccessException))throw;result.Errors.Add("数据包整理待重试："+error.Message);}
                }
                var metadata=states.Where(s=>archives.ContainsKey(s.Node.ArchiveId)&&Catalog.CanRepackArchive(s.Node.ArchiveId)).Select(s=>new{State=s,Policy=OriginalRepackPolicy(archives[s.Node.ArchiveId])}).GroupBy(p=>p.State.Node.TaskId+"|"+p.Policy.Key).ToList();
                foreach(var group in metadata)
                {
                    var batch=new List<RepackNode>();long bytes=0,rawLimit=Math.Min(96L*1024*1024,MemoryLimitBytes/8);
                    foreach(var value in group.OrderBy(p=>p.State.Node.ArchiveId).ThenBy(p=>p.State.Node.Id,StringComparer.Ordinal))
                    {
                        if(batch.Count>0&&bytes>rawLimit-value.State.Node.DatabaseBytes){RepackNodeBatch(batch,group.First().Policy,replacements,oldData,result,touched,volume,report);batch=new List<RepackNode>();bytes=0;}
                        batch.Add(value.State);bytes+=value.State.Node.DatabaseBytes;
                    }
                    if(batch.Count>0)RepackNodeBatch(batch,group.First().Policy,replacements,oldData,result,touched,volume,report);
                }
                var referenced=new HashSet<long>(states.SelectMany(s=>s.References));referenced.UnionWith(localReferences);bool canCollect=complete&&!Catalog.HasStorageGrowth()&&Catalog.PendingVolumeTransfers().Count==0&&Catalog.PendingUploadStages().Count==0;
                bool garbage=false;if(canCollect){Catalog.AllowNodeGarbage(referenced);garbage=Catalog.GetGarbageCandidates(false).Count>0;Catalog.ClearNodeGarbageReview();}
                var currentNodes=Catalog.GetNodes().ToDictionary(n=>n.Id);bool retired=Catalog.NodeRevisions().Any(n=>!currentNodes.ContainsKey(n.Id)||currentNodes[n.Id].ArchiveId!=n.ArchiveId);
                if(result.Versions==0&&!garbage&&!retired)return result;
                // Publish once after all new packages and version references are ready.
                // Old packages are removed only after that final publication succeeds.
                RepackCheckpoint("after-repack-snapshot");BackupDatabase(null,report);RepackCheckpoint("before-repack-delete");
                result.Errors.AddRange(RetryRetiredNodeDatabases(report));
                if(canCollect)
                {
                    try{Catalog.AllowNodeGarbage(referenced);result.Cleanup=new ArchiveMaintenance(Catalog,Adapters()){Control=ActiveBackup,Report=report}.CollectGarbageDetailed(cleanupProgress,false);result.Errors.AddRange(result.Cleanup.Errors);LastCleanup=result.Cleanup;}
                    finally{Catalog.ClearNodeGarbageReview();}
                }
                else if(oldData.Count>0)result.Errors.Add("部分版本无法读取或仍有上传待确认，旧数据包已全部保留，等待修复完整性回收。");
                RepackCheckpoint("after-repack-delete");return result;
            }
            finally
            {
                try
                {
                    Catalog.ClearNodeGarbageReview();RefreshManagedArchiveInventory(touched);
                }
                finally{if(owns)EndBackup();}
            }
        }
        private void RepackNodeBatch(IList<RepackNode> batch,RepackPolicy policy,IDictionary<string,RepackedFile> replacements,ISet<long> replacedArchives,WarehouseCompactionResult result,ISet<long> touched,int volume,Action<string> report)
        {
            RepackCheckpoint("database-batch");bool changed=batch.Any(s=>s.References.Overlaps(replacedArchives));var oldIds=batch.Select(s=>s.Node.ArchiveId).Distinct().ToList();
            if(batch.Select(s=>s.Node.ArchiveId).Distinct().Count()<2&&!changed)return;
            try
            {
                var pending=new List<Tuple<RepackNode,NodeFileDatabase,byte[],SourceItem>>();
                foreach(var state in batch)
                {
                    var db=ReadNodeDatabase(state.Node);bool rewritten=false;
                    foreach(var file in db.Files){RepackedFile target;if(!replacements.TryGetValue(RepackFileKey(file),out target))continue;file.ArchiveId=target.ArchiveId;file.ContentPath=target.EntryPath;rewritten=true;}
                    if(rewritten)db.Revision=checked(db.Revision+1);byte[] bytes=db.Serialize();var source=SourceItem.FromMemory(bytes,"nodes\\"+state.Node.Id+".db");pending.Add(Tuple.Create(state,db,bytes,source));
                }
                using(OperationMemory.Reserve(pending.Sum(p=>p.Item3.LongLength)*2))
                {
                    int actualVolume=(int)Math.Min(volume,OperationMemory.AvailableBytes/3);actualVolume-=actualVolume%4096;if(actualVolume<4096)throw new IOException("内存不足，旧版本数据库保留。");
                    var uploaded=WriteRepackedArchive(pending.Select(p=>p.Item4).ToList(),policy,"__node__:"+Guid.NewGuid().ToString("N"),actualVolume,report,batch[0].Node.TaskId);touched.Add(uploaded.Archive.ArchiveId);Catalog.StageRepackedArchive(uploaded.Archive.ArchiveId);VerifyRepackedArchive(uploaded.Archive.ArchiveId,report);
                    foreach(var item in pending)
                    {
                        RepackCheckpoint("publish-repacked-node");var old=item.Item1.Node;var db=item.Item2;var node=new BackupNode{Id=old.Id,TaskId=old.TaskId,CreatedUtc=old.CreatedUtc,Revision=db.Revision,Digest=NodeFileDatabase.Hash(item.Item3),DatabaseBytes=item.Item3.Length,ArchiveId=uploaded.Archive.ArchiveId,EntryPath=item.Item4.StoredPath};
                        Catalog.PublishNode(node,db.Files.Select(f=>f.ArchiveId));item.Item1.Node=node;item.Item1.References=new HashSet<long>(db.Files.Select(f=>f.ArchiveId));touched.Add(old.ArchiveId);result.Versions++;
                        CacheNode(node,item.Item3);if(Catalog.GetNodes(node.TaskId).First().Id==node.Id)Catalog.LoadWorkingNode(node.TaskId,db);
                    }
                    result.MergedDatabaseIds.UnionWith(oldIds);result.DatabasePackages=result.MergedDatabaseIds.Count;result.NewDatabasePackages++;
                }
            }
            catch(Exception error){if(!(error is IOException)&&!(error is UnauthorizedAccessException))throw;result.Errors.Add("版本数据库整理待重试："+error.Message);}
        }
    }
}
