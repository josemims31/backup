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
        internal readonly List<string> UnresolvedTaskPolicies = new List<string>();
        internal Dictionary<string, List<string>> Directories = new Dictionary<string, List<string>>();
        internal string TaskScope, Topology;
        internal long CatalogStamp, InventoryStamp, RecoveryRevision;
        internal bool Completed;
        internal BackupIntegritySummary RecordedSummary;
        internal long RecordedSummaryStamp;
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
        private readonly IDictionary<string, WarehouseDirectoryRead> folders;
        private Dictionary<string, List<StorageEntry>> entries;
        private IDictionary<string, string> locations;
        private IList<KeyValuePair<string, WarehouseDirectoryRead>> failedFolders;
        private readonly bool filesystem;
        internal InventoryMetadataDestination(BackupCatalog value, DestinationDefinition target, IDictionary<string, WarehouseDirectoryRead> records)
        {
            catalog = value;
            destination = target;
            folders = records;
            Id = target.Id;
            filesystem = RepositoryModules.IsFileSystem(target.Protocol);
        }

        internal static string FolderKey(string path, bool filesystem)
        {
            return filesystem ? StoragePath.NormalizeRoot(path).ToUpperInvariant() : path.TrimEnd('/');
        }

        internal static bool IsInDirectory(string pathKey, string directoryKey, bool filesystem)
        {
            char separator = filesystem ? '\\' : '/';
            return pathKey == directoryKey || pathKey.StartsWith(directoryKey.TrimEnd(separator) + separator, StringComparison.Ordinal);
        }

        public RemoteObjectInfo Stat(string name)
        {
            RemoteObjectInfo confirmed;
            if (ConfirmedUploads != null && ConfirmedUploads.TryGetValue(name, out confirmed))
                return confirmed;
            if (entries == null)
            {
                entries = new Dictionary<string, List<StorageEntry>>(filesystem ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                foreach (var read in folders.Values.Where(read => read.Entries != null))
                    foreach (var entry in read.Entries.Where(e => !e.Directory))
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
                var item = choices[0];
                if (choices.Count > 1)
                {
                    var expected = catalog.FindStoredObject(name);
                    if (expected != null)
                        item = choices.FirstOrDefault(e => e.Size == expected.Size) ?? item;
                }
                return new RemoteObjectInfo
                {
                    Name = name,
                    Exists = true,
                    Size = item.Size,
                    UnavailableReason = item.Size < 0 ? "仓库没有提供有效文件大小。" : null
                };
            }

            if (locations == null)
                locations = catalog.ObjectStorageDirectories(Id);
            string relative;
            locations.TryGetValue(BackupObjectNaming.ObjectName(name) ?? name, out relative);
            string directory = FolderKey(ObjectStoragePath.Absolute(destination, relative ?? ""), filesystem);
            WarehouseDirectoryRead observed;
            bool readable = folders.TryGetValue(directory, out observed) && observed.Entries != null;
            if (!readable)
            {
                if (failedFolders == null)
                    failedFolders = folders.Where(pair => pair.Value.Entries == null).ToList();
                foreach (var failed in failedFolders)
                    if (IsInDirectory(directory, failed.Key, filesystem))
                        return new RemoteObjectInfo { Name = name, UnavailableReason = failed.Value.Record.Error };
            }
            return new RemoteObjectInfo
            {
                Name = name,
                UnavailableReason = !folders.TryGetValue(FolderKey(destination.Location, filesystem), out observed) || observed.Entries == null
                    ? "仓库暂不可访问，保留上次记录。" : null
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

        private WarehouseDirectoryRead ReadWarehouseDirectory(string id, string folder, System.Threading.CancellationToken cancellation, bool checkIntegrity, string checkedUtc = null, bool missingDirectoryIsEmpty = false, DestinationDefinition definition = null)
        {
            var ticket = repositoryRuntime.BeginRead(id, folder);
            try
            {
                IList<StorageEntry> entries;
                try
                {
                    entries = ListWarehouseEntries(id, folder, cancellation, checkIntegrity || checkedUtc != null, definition);
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
                definition = definition ?? Catalog.LoadTopology().Destinations.Single(d => d.Id == id);
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
            var knownCopies = taskIds == null ? null : Catalog.GetIntegrityCopies(archives.Select(archive => archive.ArchiveId))
                .SelectMany(copies => copies).ToLookup(copy => copy.DestinationId);
            var members = taskIds == null 
                ? null 
                : new HashSet<string>(knownCopies.Select(copies => copies.Key)
                    .Concat(interrupted.SelectMany(archive => Catalog.InterruptedCheckCopies(archive.ArchiveId)).Select(copy => copy.DestinationId)));
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
            var snapshots = new Dictionary<string, Dictionary<string, WarehouseDirectoryRead>>();
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
                    Func<string, string> key = path => InventoryMetadataDestination.FolderKey(path, local);
                    var folders = new Dictionary<string, WarehouseDirectoryRead>();
                    lock (resultsGate)
                        snapshots[d.Id] = folders;
                    var queue = new Stack<Tuple<string, int>>();
                    if (knownCopies != null && knownCopies.Contains(d.Id))
                    {
                        var locations = Catalog.ObjectStorageDirectories(d.Id);
                        foreach (string folder in knownCopies[d.Id].Select(copy =>
                        {
                            string directory;
                            locations.TryGetValue(copy.Object.Name, out directory);
                            return ObjectStoragePath.Absolute(d, directory ?? "");
                        }).Distinct())
                            queue.Push(Tuple.Create(folder, 0));
                    }
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
                        WarehouseDirectoryRead read;
                        try
                        {
                            if (next.Item2 > 64)
                                throw new IOException("目录层数超过检查上限。");
                            if (report != null)
                                lock (reportsGate)
                                    report("正在检查仓库目录：" + next.Item1);
                            read = ReadWarehouseDirectory(
                                d.Id,
                                next.Item1,
                                OperationCancellation,
                                false,
                                stamp,
                                !SameFolder(next.Item1, d.Location, local) && folders.ContainsKey(key(d.Location)) &&
                                folders[key(d.Location)].Entries != null,
                                d
                            );
                            var entries = read.Entries;
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
                            read = new WarehouseDirectoryRead
                            {
                                Record = new InventoryFolderRecord { DestinationId = d.Id, Folder = next.Item1, CheckedUtc = stamp, Error = error.Message }
                            };
                            errors.Add(next.Item1 + "：" + error.Message);
                        }

                        folders[key(next.Item1)] = read;
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
                        adapter = new InventoryMetadataDestination(Catalog, d, snapshots.ContainsKey(d.Id) ? snapshots[d.Id] : new Dictionary<string, WarehouseDirectoryRead>());
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
            if (report != null)
                report("正在核对待确认上传");
            result.Findings.AddRange(CheckPendingUploads(archiveTasks, false, result.ObsoleteIds, true, formalOnly));
            var engine = new GroupedBackupEngine(Catalog, adapters, Path.Combine(BaseDirectory, "7za.dll"));
            var definitions = topology.Destinations.ToDictionary(d => d.Id);
            var compared = archives.Where(item => !result.ObsoleteIds.Contains(item.ArchiveId)).ToList();
            var registered = Catalog.GetIntegrityCopies(compared.Select(archive => archive.ArchiveId));
            var observations = new List<IntegrityObservation>();
            for (int index = 0; index < compared.Count; index++)
            {
                OperationCancellation.ThrowIfCancellationRequested();
                if (ActiveBackup != null)
                    ActiveBackup.Checkpoint();
                var archive = compared[index];
                if (report != null)
                    report("正在比对文件列表：" + archive.ArchiveName + " · " + (index + 1) + " / " + compared.Count);
                result.Findings.AddRange(engine.CheckIntegrity(archive.ArchiveId, registered[archive.ArchiveId], definitions, OperationCancellation, observations.Add));
                // Commit small batches so other local database readers can proceed.
                if (index % 32 == 31 || index == compared.Count - 1)
                {
                    Catalog.RecordIntegrityBatch(observations, OperationCancellation);
                    observations.Clear();
                }
            }

            result.Findings.AddRange(CheckInterruptedBackups(adapters, archiveTasks, report));
            if (!formalOnly)
                result.Findings.AddRange(CheckOwnedTemporaryFiles(adapters, archiveTasks, result.ObsoleteIds));
            if (report != null)
                report("正在汇总完整性检查结果");
            CompleteIntegrityReview(result, snapshots, topology, false);
            if (taskIds == null && result.Errors.Count == 0 &&
                !result.Findings.Any(f => f.Status.StartsWith("unavailable", StringComparison.Ordinal) || f.Status == "confirmation-pending"))
                foreach (var destination in topology.Destinations)
                    Catalog.AcceptInspectedStorageGrowth(destination.Id, result.Findings);
            // Check only recorded objects against the directory inventory. Version
            // contents and unreferenced-package collection belong to repair/compaction.
            ReviewLatestTaskTargets(result, topology);
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

        private async Task<AllBackupCheckResult> ReadIntegrityCheckAsync(IList<string> taskIds, bool summarize = false)
        {
            var progress = OperationReporter("");
            var interval = System.Diagnostics.Stopwatch.StartNew();
            string lastPhase = null;
            Action<string> report = message =>
            {
                int separator = message.IndexOf('：');
                string phase = separator < 0 ? message : message.Substring(0, separator);
                if (phase == lastPhase && interval.ElapsedMilliseconds < 1000)
                    return;
                lastPhase = phase;
                interval.Restart();
                progress(message);
            };
            historySummaryActive = false;
            deferIntegrityPresentation = true;
            try
            {
                return await Task.Run(() =>
                {
                    var result = Backend.CheckAllBackups(report, taskIds);
                    if (summarize)
                    {
                        var catalog = Backend.Catalog;
                        result.RecordedSummaryStamp = catalog.DataChangeStamp;
                        result.RecordedSummary = catalog.GetRecordedIntegritySummary(taskIds == null ? null : new HashSet<string>(taskIds),
                            ObservedRecoveryObjects(catalog, result), Backend.CanRebuildFileArchiveFromPeers);
                    }
                    return result;
                });
            }
            finally
            {
                deferIntegrityPresentation = false;
            }
        }

        private async Task<IList<IntegrityFinding>> CheckAllBackupsCoreAsync(IList<string> taskIds = null)
        {
            if (busy)
                throw new InvalidOperationException("当前任务尚未结束。");
            await EnsureTaskFileListsAsync(taskIds, sourceReadGeneration);
            if (disposed || closeAfterWork)
                return new List<IntegrityFinding>();
            SetBusy(true, false, "正在检查全部备份完整性…");
            Text("HistoryCheckSummary").Visibility = System.Windows.Visibility.Visible;
            SetHistoryCheckText("正在检查备份完整性…", OperationState.Running);
            try
            {
                var result = await ReadIntegrityCheckAsync(taskIds, true);
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
                if (result.UnresolvedTaskPolicies.Count > 0)
                    text += "\n以下任务的最新版本副本引用未核实，请运行完整性修复：" + string.Join("、", result.UnresolvedTaskPolicies);
                string message = text + "\n按名称和大小核对；仅修正本地登记状态，未修改仓库文件。";
                var state = result.Findings.Any(f => f.RequiresManualRepairConfirmation) 
                    ? OperationState.Failed 
                    : result.Errors.Count > 0 || result.UnresolvedTaskPolicies.Count > 0 ||
                    result.Findings.Any(f => f.Status.StartsWith("unavailable") ||
                    f.Status == "confirmation-pending") 
                    ? OperationState.Attention 
                    : OperationState.Succeeded;
                SetOperationText(message, state, false);
                PublishHistoryCheckSummary(taskIds, result.Errors.Count, counts: result.RecordedSummary, stamp: result.RecordedSummaryStamp);
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
