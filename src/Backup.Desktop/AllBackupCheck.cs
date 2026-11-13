using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed class AllBackupCheckResult
    {
        public int Folders, Entries, ExternalFiles, ManagedUnregistered, RegistrationAnomalies;
        public List<string> TaskIds;
        internal HashSet<string> ArchiveTasks;
        internal HashSet<long> ObsoleteIds = new HashSet<long>();
        internal Dictionary<string, List<string>> Directories = new Dictionary<string, List<string>>();
        internal string TaskScope, Topology;
        internal long CatalogStamp, RecoveryRevision;
        internal bool Completed;
        internal HashSet<long> ReferencedArchives;
        internal string ReferenceIdentity;
        public CleanupResult Cleanup;
        public List<string> Errors = new List<string>();
        public List<IntegrityFinding> Findings = new List<IntegrityFinding>();
        public int RepairItems
        {
            get
            {
                var archives = new HashSet<long>(Findings.Where(item => item.ArchiveLevel && item.RequiresManualRepairConfirmation).Select(item => item.ArchiveId));
                return archives.Count +
                    Findings.Where(item => !item.ArchiveLevel && item.RequiresManualRepairConfirmation && !archives.Contains(item.ArchiveId)).Select(item => item.ArchiveId + "\0" +
                    item.Destination +
                    "\0" +
                    (item.ArchiveId == 0 ? item.Folder : "") +
                    "\0" +
                    item.Name)
                    .Distinct()
                    .Count();
            }
        }
    }

    internal sealed class WarehouseDirectoryRead
    {
        internal IList<StorageEntry> Entries;
        internal InventoryFolderRecord Record;
        internal long Version;
    }

    internal sealed class WarehouseFolderSnapshot
    {
        internal IList<BackupHistoryNode> Nodes;
        internal string Notice;
        internal long Version, CatalogStamp;
    }

    internal sealed class InventoryMetadataDestination : IBackupDestination
    {
        internal IDictionary<string, RemoteObjectInfo> ConfirmedUploads;
        public string Id { get; private set; }

        private readonly BackupCatalog catalog;
        private readonly DestinationDefinition destination;
        private readonly Dictionary<string, InventoryFolderRecord> folders;
        private Dictionary<string, List<StorageEntry>> entries;
        private readonly bool filesystem;
        internal InventoryMetadataDestination(BackupCatalog value, DestinationDefinition target, Dictionary<string, InventoryFolderRecord> records)
        {
            catalog = value;
            destination = target;
            folders = records;
            Id = target.Id;
            filesystem = RepositoryModules.IsFileSystem(target.Protocol);
        }

        private string Key(string path)
        {
            return filesystem ? StoragePath.NormalizeRoot(path).ToUpperInvariant() : path.TrimEnd('/');
        }

        public RemoteObjectInfo Stat(string name)
        {
            RemoteObjectInfo confirmed;
            if (ConfirmedUploads != null && ConfirmedUploads.TryGetValue(name, out confirmed))
                return confirmed;
            if (entries == null)
            {
                entries = new Dictionary<string, List<StorageEntry>>(filesystem ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                foreach (var record in folders.Values.Where(r => string.IsNullOrEmpty(r.Error)))
                    foreach (var entry in JsonSettings.Decode(record.Json, new List<StorageEntry>()).Where(e => !e.Directory))
                    {
                        List<StorageEntry> found;
                        if (!entries.TryGetValue(entry.Name, out found))
                        {
                            found = new List<StorageEntry>();
                            entries.Add(entry.Name, found);
                        }

                        found.Add(entry);
                    }
            }

            List<StorageEntry> choices;
            if (entries.TryGetValue(name, out choices))
            {
                var expected = catalog.FindStoredObject(name);
                var item = choices.FirstOrDefault(e => expected != null && e.Size == expected.Size) ?? choices[0];
                if (item.Size < 0)
                    throw new IOException("仓库没有提供有效文件大小。");
                return new RemoteObjectInfo
                {
                    Name = name,
                    Exists = true,
                    Size = item.Size
                };
            }

            string directory = ObjectStoragePath.Absolute(destination, catalog.ObjectStorageDirectory(Id, BackupObjectNaming.ObjectName(name) ?? name) ?? "");
            InventoryFolderRecord failed;
            if (folders.TryGetValue(Key(directory), out failed) && !string.IsNullOrEmpty(failed.Error))
                throw new IOException(failed.Error);
            if (!folders.TryGetValue(Key(destination.Location), out failed) || !string.IsNullOrEmpty(failed.Error))
                throw new IOException("仓库暂不可访问，保留上次记录。");
            return new RemoteObjectInfo
            {
                Name = name
            };
        }

        public Stream OpenTemporary(string name)
        {
            throw new NotSupportedException();
        }

        public void CommitTemporary(string name)
        {
            throw new NotSupportedException();
        }

        public void DeleteTemporary(string name)
        {
            throw new NotSupportedException();
        }
    }

    public sealed partial class DesktopBackend
    {
        public IList<BackupHistoryNode> ReadCachedWarehouseFolder(string id, string folder)
        {
            FlushRepositoryResults();
            var cached = Catalog.GetInventoryFolder(id, folder);
            if (cached == null)
                throw new IOException("尚无此目录的本地清单，请点击“检查全部备份完整性”。");
            return ReadCachedWarehouseFolder(id, folder, cached);
        }

        internal IList<BackupHistoryNode> ReadCachedWarehouseFolder(string id, string folder, InventoryFolderRecord cached)
        {
            return BuildWarehouseNodes(id, folder, JsonSettings.Decode(cached.Json, new List<StorageEntry>()));
        }

        public IList<BackupHistoryNode> RefreshWarehouseFolder(string id, string folder, System.Threading.CancellationToken cancellation = default(System.Threading.CancellationToken), bool checkIntegrity = false)
        {
            return RefreshWarehouseFolderSnapshot(id, folder, cancellation, checkIntegrity).Nodes;
        }

        internal WarehouseFolderSnapshot RefreshWarehouseFolderSnapshot(string id, string folder, System.Threading.CancellationToken cancellation, bool checkIntegrity)
        {
            var read = ReadWarehouseDirectory(id, folder, cancellation, checkIntegrity);
            long stamp = Catalog.DataChangeStamp;
            return new WarehouseFolderSnapshot
            {
                Nodes = BuildWarehouseNodes(id, folder, read.Entries),
                Notice = InventoryNotice(read.Record),
                Version = read.Version,
                CatalogStamp = stamp
            };
        }

        private WarehouseDirectoryRead ReadWarehouseDirectory(string id, string folder, System.Threading.CancellationToken cancellation, bool checkIntegrity, string checkedUtc = null, bool missingDirectoryIsEmpty = false)
        {
            var ticket = repositoryRuntime.BeginRead(id, folder);
            try
            {
                IList<StorageEntry> entries;
                try
                {
                    entries = ListWarehouseEntries(id, folder, cancellation, checkIntegrity || checkedUtc != null);
#if UI_TEST
                    if (TestWarehouseListingResult != null)
                        TestWarehouseListingResult(id, folder, entries);
#endif
                }
                catch (Exception error)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/AllBackupCheck.cs:ReadWarehouseDirectory", error);
                    if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is NotSupportedException))
                        throw;
                    cancellation.ThrowIfCancellationRequested();
                    if (missingDirectoryIsEmpty && ObjectStorageLayout.IsMissingPath(error))
                        entries = new List<StorageEntry>();
                    else
                    {
                        var cachedError = error as DirectoryRefreshException;
                        repositoryRuntime.FailRead(ticket, error.Message, checkedUtc ?? (cachedError == null ? null : cachedError.CheckedUtc.ToString("o")));
                        if (checkIntegrity)
                            repositoryRuntime.CommitObservation(ticket, () => ObserveWarehouseDirectory(id, folder, new List<StorageEntry>(), error.Message, cancellation), cancellation);
                        throw;
                    }
                }

                cancellation.ThrowIfCancellationRequested();
                var definition = Catalog.LoadTopology().Destinations.Single(d => d.Id == id);
                Catalog.RememberListedObjectDirectories(id, ObjectStoragePath.Relative(definition, folder), entries);
                long version;
                InventoryFolderRecord record;
                var snapshot = entries as DirectoryEntries;
                entries = repositoryRuntime.CompleteRead(ticket, entries, out version, out record, checkedUtc ?? (snapshot == null ? null : snapshot.CheckedUtc.ToString("o")));
                if (checkIntegrity)
                    repositoryRuntime.CommitObservation(ticket, () => ObserveWarehouseDirectory(id, folder, entries, null, cancellation), cancellation);
                return new WarehouseDirectoryRead
                {
                    Entries = entries,
                    Record = record,
                    Version = version
                };
            }
            finally
            {
                repositoryRuntime.EndRead(ticket);
            }
        }

        public string InventoryNotice(string id, string folder)
        {
            return InventoryNotice(Catalog.GetInventoryFolder(id, folder));
        }

        internal static string InventoryNotice(InventoryFolderRecord cached)
        {
            return cached == null 
                ? "尚无本地清单。" 
                : (string.IsNullOrEmpty(cached.CheckedUtc) ? "本地记录 · 尚未读取目录" : "本地清单 · 上次读取：" + cached.CheckedUtc) +
                (string.IsNullOrEmpty(cached.Error) ? "" : "\n上次联网读取未完成：" +
                cached.Error);
        }

        public AllBackupCheckResult CheckAllBackups(Action<string> report = null, IList<string> taskIds = null, bool formalOnly = false)
        {
            using (BeginMaintenanceSearch())
            {
                var search = maintenanceSearch.Value;
                var inventory = search.SearchInventory;
                try
                {
                    return CheckAllBackupsCore(report, taskIds, formalOnly);
                }
                finally
                {
                    search.SearchInventory = inventory;
                }
            }
        }

        private AllBackupCheckResult CheckAllBackupsCore(Action<string> report, IList<string> taskIds, bool formalOnly)
        {
            var requested = taskIds == null ? null : new HashSet<string>(taskIds);
            Catalog.UnregisterAbandonedArchiveOutputs(requested, ActiveBackup == null ? null : new HashSet<long>(ActiveBackup.ArchiveIds));
            Catalog.UnregisterDeletedLocalNodeData(requested);
            if (taskIds == null)
                Catalog.RefreshLocalBackupNodes();
            else
                foreach (string task in taskIds)
                    Catalog.RefreshLocalBackupNode(task);
            Catalog.ClearNodeGarbageReview();
            lastIntegrityReview = null;
            var result = new AllBackupCheckResult
            {
                TaskIds = taskIds == null ? null : taskIds.Distinct().ToList()
            };
            OperationCancellation.ThrowIfCancellationRequested();
            var archiveTasks = IntegrityTaskIds(taskIds);
            var topology = Catalog.LoadTopology();
            var archives = Catalog.ListArchives(true).Where(archive => !new[] { "writing", "cleaned", "failed", "damaged" }.Contains(archive.Status) &&
                (archiveTasks == null ||
                archiveTasks.Contains(archive.TaskId)))
                .ToList();
            result.ObsoleteIds.UnionWith(Catalog.ObsoleteOwnedArchiveIds(archiveTasks));
            var interrupted = Catalog.InterruptedArchives(archiveTasks);
            var knownCopies = taskIds == null ? null : archives.SelectMany(archive => Catalog.GetCopies(archive.ArchiveId)).ToList();
            var members = taskIds == null 
                ? null 
                : new HashSet<string>(knownCopies.Concat(interrupted.SelectMany(archive => Catalog.InterruptedCheckCopies(archive.ArchiveId))).Select(copy => copy.DestinationId));
            var temporaryDestinations = new HashSet<string>(Catalog.PendingUploadStages().Where(item => Catalog.CanInspectUploadTemporaries(item.ArchiveId) &&
                (archiveTasks == null ||
                archiveTasks.Contains(Catalog.GetArchive(item.ArchiveId)
                .TaskId)))
                .Select(item => item.DestinationId));
            if (members != null)
                members.UnionWith(temporaryDestinations);
            if (members != null)
                foreach (var task in WorkspaceMigration.Load(Catalog).Tasks.Where(task => taskIds.Contains(task.Id)))
                    foreach (string root in task.TargetIds.Where(root => topology.Groups.Any(group => group.Id == root)))
                        members.UnionWith(topology.ResolveDestinations(root, false));
            var snapshots = new Dictionary<string, Dictionary<string, InventoryFolderRecord>>();
            string stamp = DateTime.UtcNow.ToString("o");
            var resultsGate = new object();
            var reportsGate = new object();
            var destinations = topology.Destinations.Where(d => (members == null || members.Contains(d.Id)) && (!d.Retired || Catalog.GetWarehouseObjects(d.Id).Count > 0 || temporaryDestinations.Contains(d.Id)))
                .ToList();
            try
            {
                Parallel.ForEach(
                    destinations,
                    new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = OperationCancellation },
                    d =>
                {
                    int checkedFolders = 0, checkedEntries = 0;
                    var errors = new List<string>();
                    bool local = RepositoryModules.IsFileSystem(d.Protocol);
                    Func<string, string> key = path => local ? StoragePath.NormalizeRoot(path).ToUpperInvariant() : path.TrimEnd('/');
                    var folders = new Dictionary<string, InventoryFolderRecord>();
                    lock (resultsGate)
                        snapshots[d.Id] = folders;
                    var queue = new Stack<Tuple<string, int>>();
                    if (taskIds != null)
                        foreach (string folder in knownCopies.Where(c => c.DestinationId == d.Id).Select(c => ObjectStoragePath.Absolute(d, Catalog.ObjectStorageDirectory(d.Id, c.Object.Name) ?? "")).Distinct())
                            queue.Push(Tuple.Create(folder, 0));
                    if (!SameFolder(d.Location, ObjectDirectory(d), local))
                        queue.Push(Tuple.Create(ObjectDirectory(d), 0));
                    queue.Push(Tuple.Create(d.Location, 0));
                    bool complete = true;
                    while (queue.Count > 0)
                    {
                        OperationCancellation.ThrowIfCancellationRequested();
                        if (ActiveBackup != null)
                            ActiveBackup.Checkpoint();
                        var next = queue.Pop();
                        if (folders.ContainsKey(key(next.Item1)))
                            continue;
                        var cached = Catalog.GetInventoryFolder(d.Id, next.Item1);
                        var record = new InventoryFolderRecord
                        {
                            DestinationId = d.Id,
                            Folder = next.Item1,
                            CheckedUtc = stamp,
                            Json = cached == null ? "[]" : cached.Json,
                            Error = ""
                        };
                        try
                        {
                            if (next.Item2 > 64)
                                throw new IOException("目录层数超过检查上限。");
                            if (report != null)
                                lock (reportsGate)
                                    report("正在检查仓库目录：" + next.Item1);
                            var read = ReadWarehouseDirectory(
                                d.Id,
                                next.Item1,
                                OperationCancellation,
                                false,
                                stamp,
                                !SameFolder(next.Item1, d.Location, local) && folders.ContainsKey(key(d.Location)) &&
                                string.IsNullOrEmpty(folders[key(d.Location)].Error)
                            );
                            var entries = read.Entries;
                            record = read.Record;
                            checkedFolders++;
                            checkedEntries += entries.Count;
                            if (taskIds == null)
                                foreach (var entry in entries.Where(e => e.Directory))
                                    queue.Push(Tuple.Create(entry.Location, next.Item2 + 1));
                        }
                        catch (Exception error)
                        {
                            global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/AllBackupCheck.cs:CheckAllBackupsCore", error);
                            if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is NotSupportedException))
                                throw;
                            complete = false;
                            record.Error = error.Message;
                            errors.Add(next.Item1 + "：" + error.Message);
                        }

                        folders[key(next.Item1)] = record;
                    }

                    if (complete && taskIds == null)
                        repositoryRuntime.PruneInventory(d.Id, stamp);
                    lock (resultsGate)
                    {
                        result.Folders += checkedFolders;
                        result.Entries += checkedEntries;
                        result.Errors.AddRange(errors);
                    }
                }
                );
            }
            catch (AggregateException error)
            {
                var failures = error.Flatten().InnerExceptions;
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures.FirstOrDefault(failure => !(failure is OperationCanceledException)) ?? failures[0]).Throw();
                throw;
            }

            var metadataAdapters = new Dictionary<string, InventoryMetadataDestination>();
            var adapters = new DestinationAdapterRegistry();
            foreach (string protocol in topology.Destinations.Select(d => d.Protocol).Distinct(StringComparer.OrdinalIgnoreCase))
                adapters.Register(
                    protocol,
                    d =>
                {
                    InventoryMetadataDestination adapter;
                    if (!metadataAdapters.TryGetValue(d.Id, out adapter))
                    {
                        adapter = new InventoryMetadataDestination(Catalog, d, snapshots.ContainsKey(d.Id) ? snapshots[d.Id] : new Dictionary<string, InventoryFolderRecord>());
                        adapter.ConfirmedUploads = result.Findings.Where(f => f.UploadConfirmation && f.Destination == d.Id && f.Status == "present-size-matched").GroupBy(f => f.Name, StringComparer.Ordinal)
                            .ToDictionary(g => g.Key, g => new RemoteObjectInfo {
                            Name = g.Key,
                            Exists = true,
                            Size = g.First()
                            .ExpectedSize
                        }, StringComparer.Ordinal);
                        metadataAdapters[d.Id] = adapter;
                    }

                    return adapter;
                }
                );
            var scope = taskIds == null ? null : new HashSet<string>(taskIds);
            result.Findings.AddRange(CheckPendingUploads(archiveTasks, false, result.ObsoleteIds, true, formalOnly));
            var engine = new GroupedBackupEngine(Catalog, adapters, Path.Combine(BaseDirectory, "7za.dll"));
            foreach (var archive in archives.Where(item => !result.ObsoleteIds.Contains(item.ArchiveId)))
            {
                if (report != null)
                    report("正在核对备份包：" + archive.ArchiveName);
                result.Findings.AddRange(engine.CheckIntegrity(archive.ArchiveId, OperationCancellation, true));
            }

            result.Findings.AddRange(CheckInterruptedBackups(adapters, archiveTasks, report));
            if (!formalOnly)
                result.Findings.AddRange(CheckOwnedTemporaryFiles(adapters, archiveTasks, result.ObsoleteIds));
            CompleteIntegrityReview(result, snapshots);
            long classifiedRevision = Catalog.RecoveryRevision;
            int classifiedCount = result.Findings.Count;
            var classifiedObsolete = new HashSet<long>(result.ObsoleteIds);
            // Only a complete warehouse walk can prove a name is absent. Partial
            // task checks and failed folders retain the existing remote fallback.
            if (taskIds == null)
                maintenanceSearch.Value.SearchInventory = destination =>
                {
                    Dictionary<string, InventoryFolderRecord> folders;
                    if (!snapshots.TryGetValue(destination.Id, out folders) || folders.Values.Any(record => !string.IsNullOrEmpty(record.Error)))
                        return null;
                    var entries = new Dictionary<string, IList<StorageEntry>>();
                    foreach (var record in folders.Values)
                        entries[ObjectStoragePath.Relative(destination, record.Folder)] = JsonSettings.Decode(record.Json, new List<StorageEntry>());
                    return entries;
                };
            bool referencesComplete = InspectNodeReferences(scope, result.Errors, report);
            if (referencesComplete)
            {
                result.ReferencedArchives = new HashSet<long>(inspectedNodeArchives);
                result.ReferenceIdentity = ReviewReferenceIdentity();
            }

            if (referencesComplete)
            {
                var removed = Catalog.UnregisterUnreferencedInterruptedArchives(inspectedNodeArchives, archiveTasks);
                result.Findings.RemoveAll(finding => removed.Contains(finding.ArchiveId));
                result.ObsoleteIds.ExceptWith(removed);
            }

            if (taskIds == null && result.Errors.Count == 0 &&
                !result.Findings.Any(f => f.Status.StartsWith("unavailable", StringComparison.Ordinal) || f.Status == "confirmation-pending"))
                foreach (var destination in topology.Destinations)
                    Catalog.AcceptInspectedStorageGrowth(destination.Id, result.Findings);
            if (taskIds == null && result.Errors.Count == 0 &&
                !result.Findings.Any(f => f.Status.StartsWith("unavailable", StringComparison.Ordinal) || f.Status == "confirmation-pending") &&
                Catalog.PendingVolumeTransfers()
                .Count == 0 &&
                Catalog.PendingUploadStages()
                .Count == 0)
            {
                try
                {
                    if (referencesComplete)
                        result.ObsoleteIds.UnionWith(MarkUnreferencedNodeData(result.ReferencedArchives));
                }
                catch (IOException error)
                {
                    result.Errors.Add("无引用数据核对未完成：" + error.Message);
                }
                catch (UnauthorizedAccessException error)
                {
                    result.Errors.Add("无引用数据核对未完成：" + error.Message);
                }
                finally
                {
                    Catalog.ClearNodeGarbageReview();
                }
            }

            if (Catalog.RecoveryRevision != classifiedRevision || result.Findings.Count != classifiedCount || !result.ObsoleteIds.SetEquals(classifiedObsolete))
            {
                result.ExternalFiles = 0;
                CompleteIntegrityReview(result, snapshots);
            }

            result.CatalogStamp = Catalog.DataChangeStamp;
            result.RecoveryRevision = Catalog.RecoveryRevision;
            return result;
        }
    }

    public sealed partial class MainWindowController
    {
        public Task<IList<IntegrityFinding>> CheckAllBackupsAsync(IList<string> taskIds = null)
        {
            return RunOperationAsync(OperationKind.Check, () => CheckAllBackupsCoreAsync(taskIds));
        }

        private async Task<IList<IntegrityFinding>> CheckAllBackupsCoreAsync(IList<string> taskIds = null)
        {
            if (busy)
                throw new InvalidOperationException("当前任务尚未结束。");
            await EnsureTaskFileListsAsync(taskIds, sourceReadGeneration);
            if (disposed || closeAfterWork)
                return new List<IntegrityFinding>();
            SetBusy(true, false, "正在检查全部备份完整性…");
            var progress = OperationReporter("");
            long lastTick = 0;
            Action<string> report = message =>
            {
                long now = DateTime.UtcNow.Ticks;
                if (now - lastTick < TimeSpan.TicksPerMillisecond * 120)
                    return;
                lastTick = now;
                progress(message);
            };
            Text("HistoryCheckSummary").Visibility = System.Windows.Visibility.Visible;
            SetHistoryCheckText("正在检查备份完整性…", OperationState.Running);
            try
            {
                var result = await Task.Run(() => Backend.CheckAllBackups(report, taskIds));
                operationGeneration++;
                ApplyCommittedChanges();
                string text = "检查完成：读取 " + result.Folders + " 个目录、" + result.Entries + " 项，核对 " + result.Findings.Count(f => !f.ArchiveLevel && f.ArchiveId > 0) + " 个分卷副本；" + result.RepairItems +
                    " 项待修复，" +
                    result.Findings.Count(f => f.Status.StartsWith("unavailable")) +
                    " 项暂不可访问；" +
                    result.ManagedUnregistered +
                    " 个无登记备份文件，" +
                    result.ExternalFiles +
                    " 个外部文件。" +
                    (result.Errors.Count == 0 ? "" : "\n部分目录保留上次清单：" +
                    string.Join("；", result.Errors));
                string message = text + "\n按名称和大小核对；仅修正本地登记状态，未修改仓库文件。";
                var state = result.Findings.Any(f => f.RequiresManualRepairConfirmation) 
                    ? OperationState.Failed 
                    : result.Errors.Count > 0 ||
                    result.Findings.Any(f => f.Status.StartsWith("unavailable") ||
                    f.Status == "confirmation-pending") 
                    ? OperationState.Attention 
                    : OperationState.Succeeded;
                SetOperationText(message, state, false);
                PublishHistoryCheckSummary(taskIds, result.Errors.Count);
                Log(message, state);
                OperationProgress(100, false);
                return result.Findings;
            }
            catch (global::System.Exception diagnosticFailure)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/AllBackupCheck.cs:CheckAllBackupsCoreAsync", diagnosticFailure);
                SetHistoryCheckText("检查未完成，请查看错误提示后重试。", OperationState.Failed);
                throw;
            }
            finally
            {
                SetBusy(false, false, null);
            }
        }
    }
}
