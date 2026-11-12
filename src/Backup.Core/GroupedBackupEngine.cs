using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Backup.Core
{
    public sealed class DestinationAdapterRegistry
    {
        private readonly object scopeGate=new object();
        private readonly Dictionary<string,IBackupDestination> active=new Dictionary<string,IBackupDestination>(StringComparer.Ordinal);
        private int scopes;
        public IUploadJournal UploadJournal;
        public TransferBandwidth Bandwidth;
        public TransferScheduler Transfers=TransferScheduler.Shared;
        public MemoryBudget Memory;
        public System.Threading.CancellationToken Cancellation;
        public Func<DestinationDefinition,IBackupDestination> FallbackFactory;
        private readonly Dictionary<string, Func<DestinationDefinition, IBackupDestination>> factories =
            new Dictionary<string, Func<DestinationDefinition, IBackupDestination>>(StringComparer.OrdinalIgnoreCase);
        public void Register(string protocol, Func<DestinationDefinition, IBackupDestination> factory)
        {
            if (string.IsNullOrWhiteSpace(protocol) || factory == null) throw new ArgumentException("Protocol and factory required.");
            factories.Add(protocol, factory);
        }
        public bool Supports(string protocol) { return factories.ContainsKey(protocol)||FallbackFactory!=null; }
        public IDisposable BeginScope(){lock(scopeGate)scopes++;return new AdapterScope(this);}
        private sealed class AdapterScope:IDisposable
        {
            private DestinationAdapterRegistry owner;
            internal AdapterScope(DestinationAdapterRegistry value){owner=value;}
            public void Dispose(){var value=System.Threading.Interlocked.Exchange(ref owner,null);if(value!=null)value.EndScope();}
        }
        private void EndScope(){List<IBackupDestination> close=null;lock(scopeGate){if(--scopes==0){close=active.Values.Distinct().ToList();active.Clear();}}if(close!=null)foreach(var adapter in close)DisposeAdapter(adapter);}
        public static void DisposeAdapter(IBackupDestination adapter){var disposable=adapter as IDisposable;if(disposable!=null)disposable.Dispose();}
        public void Release(IBackupDestination adapter){lock(scopeGate)if(scopes>0&&active.Values.Contains(adapter))return;DisposeAdapter(adapter);}
        public IBackupDestination Create(DestinationDefinition destination){return Create(destination,true);}
        public IBackupDestination CreateTransfer(DestinationDefinition destination){return Create(destination,false);}
        private IBackupDestination Create(DestinationDefinition destination,bool shared)
        {
            lock(scopeGate)
            {
            string key=destination.Id+"\n"+destination.Protocol+"\n"+destination.Location;
            IBackupDestination existing;if(shared&&scopes>0&&active.TryGetValue(key,out existing))return existing;
            Func<DestinationDefinition, IBackupDestination> factory;
            if (!factories.TryGetValue(destination.Protocol, out factory)) {factory=FallbackFactory;if(factory==null)throw new NotSupportedException("Destination protocol is not registered: " + destination.Protocol);}
            IBackupDestination adapter = factory(destination);
            if (adapter == null || adapter.Id != destination.Id) throw new InvalidOperationException("Adapter identity does not match destination.");
            var staged=adapter as IOwnedUploadDestination;if(staged!=null)staged.UploadJournal=UploadJournal;
            var limited=adapter as IBandwidthDestination;if(limited!=null)limited.Bandwidth=Bandwidth;
            if(shared&&scopes>0)active.Add(key,adapter);
            return adapter;
            }
        }
    }
    public sealed class GroupedArchiveSession : IArchiveObjectSink, IDisposable
    {
        private readonly BackupCatalog catalog;
        private readonly DestinationAdapterRegistry registry;
        private readonly List<IBackupDestination> owned=new List<IBackupDestination>();
        private readonly Dictionary<string, RetryUploader> uploaders = new Dictionary<string, RetryUploader>();
        private bool finished;
        public ArchivePlan Plan { get; private set; }
        public readonly List<UploadEvent> Events = new List<UploadEvent>();
        public readonly List<ObjectRecord> Objects = new List<ObjectRecord>();
        public Func<string, string, int, long, bool> InjectFailure;
        public Func<long> SourceBytes;
        public Action<TransferProgress> Progress;
        public GroupedArchiveSession(BackupCatalog catalog, ArchivePlan plan, DestinationAdapterRegistry registry, int attempts)
        {
            this.catalog = catalog;this.registry=registry; Plan = plan;
            var definitions = catalog.LoadTopology().Destinations.ToDictionary(x => x.Id);
            foreach (ArchivePlacement place in plan.Placements)
            {
                try
                {
                    var adapter=registry.Create(definitions[place.DestinationId]);owned.Add(adapter);
                    var uploader = new RetryUploader(new[] { adapter }, attempts){Transfers=registry.Transfers};
                    uploader.SourceBytes = delegate { return SourceBytes == null ? 0 : SourceBytes(); };
                    uploader.InjectFailure = delegate(string id, string name, int attempt, long written)
                    { return InjectFailure != null && InjectFailure(id, name, attempt, written); };
                    string groupId = place.GroupId;
                    uploader.Progress = delegate(TransferProgress p) { p.GroupId = groupId; if (Progress != null) Progress(p); };
                    uploaders.Add(place.DestinationId, uploader);
                }
                catch (Exception e) { catalog.FailPlacement(plan, place, e.Message); }
            }
            if (uploaders.Count == 0)
            {
                catalog.FinishArchive(plan, false); finished = true;
                throw new IOException("No selected destination could be opened; source was not read.");
            }
        }
        public void Upload(string name, byte[] buffer, int count)
        {
            if (finished) throw new InvalidOperationException("Archive session is finished.");
            ObjectRecord record = RetryUploader.CreateRecord(name, buffer, count);
            catalog.RegisterObject(Plan, record);
            Objects.Add(record);
            int success = 0;
            foreach (ArchivePlacement place in Plan.Placements.Where(p => p.Status == "writing"))
            {
                RetryUploader uploader = uploaders[place.DestinationId];
                int eventsBefore = uploader.Events.Count;
                try
                {
                    uploader.Upload(record, buffer, count);
                    catalog.CommitObjectCopy(Plan, place, record);
                    place.StoredBytes += count; place.ReservedBytes -= count;
                    success++;
                }
                catch (Exception e)
                {
                    if (!(e is IOException) && !(e is UnauthorizedAccessException)) throw;
                    catalog.FailPlacement(Plan, place, e.Message, e is TransferReconciliationRequiredException);
                }
                finally { Events.AddRange(uploader.Events.Skip(eventsBefore)); }
            }
            if (success == 0) throw new IOException("No group received this object; the archive is incomplete.");
        }
        public ArchiveSummary Complete()
        {
            if (finished) throw new InvalidOperationException("Archive session is finished.");
            ArchiveSummary result = catalog.FinishArchive(Plan, true); finished = true; return result;
        }
        public void Dispose()
        {
            try{if(!finished){catalog.FinishArchive(Plan,false);finished=true;}}
            finally{foreach(var adapter in owned)registry.Release(adapter);owned.Clear();}
        }
    }
    public sealed class GroupedBackupResult
    {
        public ArchiveSummary Archive;
        public ArchivePlan Plan;
        public List<ObjectRecord> Objects;
        public List<UploadEvent> Uploads;
        public long PeakVolumeBufferBytes;
        public long UploadedBeforeArchiveCompletion;
    }
    public sealed class GroupedBackupEngine
    {
        private readonly BackupCatalog catalog;
        private readonly DestinationAdapterRegistry registry;
        private readonly string sevenZipDll;
        public GroupedBackupEngine(BackupCatalog catalog, DestinationAdapterRegistry registry, string sevenZipDll)
        { this.catalog = catalog; this.registry = registry; this.sevenZipDll = sevenZipDll;registry.UploadJournal=catalog; }
        public GroupedBackupResult Backup(IList<SourceItem> sources, int copies, string password, int volumeSize, string method,
            Func<DestinationDefinition, bool> available, Func<string, string, int, long, bool> injectFailure)
        { return Backup(sources, copies, password, volumeSize, method, available, injectFailure, null); }
        public GroupedBackupResult Backup(IList<SourceItem> sources, int copies, string password, int volumeSize, string method,
            Func<DestinationDefinition, bool> available, Func<string, string, int, long, bool> injectFailure, Action<TransferProgress> progress)
        {
            return BackupForTask(sources,copies,password,volumeSize,method,available,injectFailure,progress,"legacy",null);
        }
        public GroupedBackupResult BackupForTask(IList<SourceItem> sources,int copies,string password,int volumeSize,string method,Func<DestinationDefinition,bool> available,Func<string,string,int,long,bool> injectFailure,Action<TransferProgress> progress,string taskId,IList<string> groupIds,BackupControl control=null,bool distributeVolumes=false,long memoryLimit=1024L*1024*1024,VolumeBufferPool bufferPool=null,string namingTaskId=null)
        {
            if (sources == null || sources.Count == 0) throw new ArgumentException("Source files required.");
            if (sources.Any(x => x.ContentOpenCount != 0)) throw new ArgumentException("Use fresh source descriptors for each backup operation.");
            if(distributeVolumes){
                bool owns=control==null;if(owns)control=new BackupControl();
                VolumeBufferPool buffers=null;try{control.Checkpoint();var striped=catalog.ReserveVolumeArchive(GroupAllocator.EstimateArchiveBudget(sources),copies,password,sources,d=>registry.Supports(d.Protocol)&&(available==null||available(d)),taskId,groupIds,volumeSize,namingTaskId);
                    buffers=bufferPool??new VolumeBufferPool(memoryLimit,volumeSize,control);using(var counter=new SourceReadCounter(sources))using(var session=new StripedArchiveSession(catalog,striped,registry,control))using(var output=new MemoryVolumeOutput(volumeSize,striped.ArchiveName,session,buffers,session.ArchiveControl)){
                        session.Progress=p=>{p.MemoryBytes=buffers.InUseBytes;p.MemoryLimit=memoryLimit;p.WaitingForMemory=buffers.Waiting;if(progress!=null)progress(p);};session.InjectFailure=injectFailure;session.SourceBytes=()=>counter.Bytes;
                        if(progress!=null)progress(new TransferProgress{MemoryBytes=buffers.InUseBytes,MemoryLimit=memoryLimit,ObjectName=striped.ArchiveName,State="archive-compressing"});try{new SevenZipArchiveWriter(sevenZipDll).Write(sources,output,password,method,session.ArchiveControl);session.Seal();}catch(Exception){if(!session.UploadFailed)throw;}if(progress!=null)progress(new TransferProgress{MemoryBytes=buffers.InUseBytes,MemoryLimit=memoryLimit,ObjectName=striped.ArchiveName,State="archive-ready"});return session.Complete(buffers.PeakBytes,output.UploadedBeforeArchiveCompletion);
                    }
                }finally{if(bufferPool==null&&buffers!=null)buffers.Dispose();if(owns)control.Dispose();}
            }
            ArchivePlan plan = catalog.ReserveArchive(GroupAllocator.EstimateArchiveBudget(sources), copies, password, sources,
                delegate(DestinationDefinition d) { return registry.Supports(d.Protocol) && (available == null || available(d)); },taskId,groupIds,namingTaskId);
            using(var counter=new SourceReadCounter(sources))using (var session = new GroupedArchiveSession(catalog, plan, registry, RetryUploader.DefaultAttempts))
            {
                session.InjectFailure = injectFailure;
                session.Progress = progress;
                session.SourceBytes = delegate { return counter.Bytes; };
                var output = new MemoryVolumeOutput(volumeSize, plan.ArchiveName, session);
                new SevenZipArchiveWriter(sevenZipDll).Write(sources, output, password, method,control);
                return new GroupedBackupResult { Archive = session.Complete(), Plan = plan, Objects = session.Objects,
                    Uploads = session.Events, PeakVolumeBufferBytes = output.PeakVolumeBufferBytes,
                    UploadedBeforeArchiveCompletion = output.UploadedBeforeArchiveCompletion };
            }
        }
        public PreparedBackup PrepareForTask(IList<SourceItem> sources,int copies,string password,int volumeSize,string method,Action<TransferProgress> progress,string taskId,IList<string> groupIds,BackupControl control,VolumeBufferPool buffers,TransferScheduler scheduler)
        {
            if(sources==null||sources.Count==0||sources.Any(s=>s.ContentOpenCount!=0))throw new ArgumentException("Fresh source descriptors required.");control.Checkpoint();
            var plan=catalog.ReserveVolumeArchive(GroupAllocator.EstimateArchiveBudget(sources),copies,password,sources,d=>registry.Supports(d.Protocol),taskId,groupIds,volumeSize);
            var session=new StripedArchiveSession(catalog,plan,registry,control,scheduler);try{using(var counter=new SourceReadCounter(sources)){session.SourceBytes=()=>counter.Bytes;session.Progress=p=>{p.MemoryBytes=buffers.InUseBytes;p.MemoryLimit=buffers.LimitBytes;p.WaitingForMemory=buffers.Waiting;if(progress!=null)progress(p);};
                using(var output=new MemoryVolumeOutput(volumeSize,plan.ArchiveName,session,buffers,session.ArchiveControl)){if(progress!=null)progress(new TransferProgress{MemoryBytes=buffers.InUseBytes,MemoryLimit=buffers.LimitBytes,ObjectName=plan.ArchiveName,State="archive-compressing"});try{new SevenZipArchiveWriter(sevenZipDll).Write(sources,output,password,method,session.ArchiveControl);session.Seal();}catch(Exception){if(!session.UploadFailed)throw;}if(progress!=null)progress(new TransferProgress{MemoryBytes=buffers.InUseBytes,MemoryLimit=buffers.LimitBytes,ObjectName=plan.ArchiveName,State="archive-ready"});return new PreparedBackup(session,buffers,output.UploadedBeforeArchiveCompletion);}
            }}catch{if(progress!=null)progress(new TransferProgress{MemoryBytes=buffers.InUseBytes,MemoryLimit=buffers.LimitBytes,ObjectName=plan.ArchiveName,State="archive-aborted"});session.Dispose();throw;}
        }
        public IList<IntegrityFinding> CheckIntegrity(long archiveId,System.Threading.CancellationToken cancellation=default(System.Threading.CancellationToken),bool includeHistorical=false)
        {
            var definitions = catalog.LoadTopology().Destinations.ToDictionary(x => x.Id);
            var all = new List<IntegrityFinding>();
            foreach (var group in (includeHistorical?catalog.GetCopies(archiveId):catalog.GetVerifiableCopies(archiveId)).GroupBy(x => x.GroupId))
            {
                cancellation.ThrowIfCancellationRequested();
                var findings = new List<IntegrityFinding>();
                foreach (var member in group.GroupBy(x => x.DestinationId))
                {
                    IBackupDestination adapter=null;
                    try { cancellation.ThrowIfCancellationRequested();adapter=registry.Create(definitions[member.Key]);var cancelable=adapter as ICancellableBackupDestination;if(cancelable!=null)cancelable.OperationCancellation=cancellation;findings.AddRange(DestinationIntegrityChecker.Check(adapter, member.Select(x => x.Object),cancellation)); }
                    catch (OperationCanceledException) {throw;}
                    catch (Exception e)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        foreach (StoredCopy copy in member) findings.Add(new IntegrityFinding { Destination = member.Key, Name = copy.Object.Name,
                            ExpectedSize = copy.Object.Size, ActualSize = -1, Status = "unavailable: " + e.Message });
                    }
                    finally {var disposable=adapter as IDisposable;if(disposable!=null)disposable.Dispose();}
                }
                cancellation.ThrowIfCancellationRequested();foreach(var finding in findings)finding.ArchiveId=archiveId;catalog.RecordIntegrity(archiveId, group.Key, findings); all.AddRange(findings);
            }
            return all;
        }
    }
}
