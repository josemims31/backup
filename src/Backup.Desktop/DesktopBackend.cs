using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using Backup.Core;

namespace Backup.Desktop
{
    public static class JsonSettings
    {
        public static string Encode<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public static T Decode<T>(string text, T fallback)
        {
            if (string.IsNullOrEmpty(text))
                return fallback;
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }
    }

    public sealed class DesktopProgress
    {
        public string UploadAlert;
        public long ReadBytes;
        public long TotalBytes;
        public int FinishedArchives;
        public int TotalArchives;
        public int UploadedVolumes;
        public int UploadedCopies;
        public long BufferedBytes;
        public long MemoryLimit;
        public bool WaitingForMemory;
        public string Message;
    }

    public sealed class DesktopBackupResult
    {
        public int UploadedFiles;
        public List<GroupedBackupResult> Archives = new List<GroupedBackupResult>();
        public List<SourceItem> Sources;
        public List<SourceItem> ObservedSources;
        public int SkippedUnchanged;
        public int AttentionUnchanged;
        public int ConfirmedDeleted;
        public int RemovedFromScope;
        public int PendingConfirmations;
        public BackupNode Node;
        public bool Stopped, NoChanges;
        public bool ReadyForPublication
        {
            get
            {
                return !Stopped && PendingConfirmations == 0 && Archives.All(item => item.Archive.Status == "complete" && item.Archive.CurrentValidCopies >= item.Archive.TargetCopies);
            }
        }

        public GroupedBackupResult CatalogArchive;
        public string CatalogBackupError;
        public bool CatalogSkippedUnchanged, CatalogBackupDisabled;
        public List<string> UploadWarnings = new List<string>();
    }

    public sealed partial class DesktopBackend : IDisposable
    {
        public BackupCatalog Catalog { get; private set; }

        private ObjectStorageLayout objectStorage;
        public string BaseDirectory { get; private set; }
        public string CacheDirectory { get; private set; }
        public string RepositoryId { get; private set; }

        public volatile bool StopRequested;
        public CancellationToken OperationCancellation { get; set; }

        public readonly TransferScheduler Transfers = new TransferScheduler();
        private readonly object memoryGate = new object();
        private MemoryBudget transferMemory;
        public MemoryBudget TransferMemory
        {
            get
            {
                lock (memoryGate)
                {
                    if (transferMemory == null || transferMemory.LimitBytes != MemoryLimitBytes && transferMemory.UsedBytes == 0)
                        transferMemory = new MemoryBudget(MemoryLimitBytes);
                    return transferMemory;
                }
            }
        }

        private readonly AsyncLocal<CancellationToken?> downloadCancellation = new AsyncLocal<CancellationToken?>();
        private CancellationToken DownloadCancellation
        {
            get
            {
                return downloadCancellation.Value ?? (ActiveBackup == null ? OperationCancellation : ActiveBackup.SourceToken);
            }
        }

        public IDisposable IndependentDownload(CancellationToken token)
        {
            var previous = downloadCancellation.Value;
            downloadCancellation.Value = token;
            return new DownloadScope(() => downloadCancellation.Value = previous);
        }

        private sealed class DownloadScope : IDisposable
        {
            private Action end;
            internal DownloadScope(Action value)
            {
                end = value;
            }

            public void Dispose()
            {
                var action = Interlocked.Exchange(ref end, null);
                if (action != null)
                    action();
            }
        }

        public CleanupResult LastCleanup { get; private set; }

        public DesktopBackend(string baseDirectory)
        {
            BaseDirectory = Path.GetFullPath(baseDirectory);
            string diagnosticDirectory = BaseDirectory;
            RuntimeDiagnostics.Configure((context, error) => RuntimeLog.Append(diagnosticDirectory, "diagnostics.log", context + Environment.NewLine + error));
            CacheDirectory = RuntimeFolders.Prepare(BaseDirectory);
            Catalog = new BackupCatalog(Path.Combine(BaseDirectory, "data", "catalog.db"));
            objectStorage = new ObjectStorageLayout(Catalog);
            InitializeRepositoryRuntime();
            try
            {
                InitializeServerTrust();
                InitializeBandwidth();
                RepositoryId = Catalog.GetSetting("repository_id");
                if (string.IsNullOrEmpty(RepositoryId))
                {
                    RepositoryId = Guid.NewGuid().ToString("N");
                    Catalog.SetSetting("repository_id", RepositoryId);
                }

                RuntimeFolders.Migrate(BaseDirectory, CacheDirectory, RemapRuntimeReferences);
                MigrateConnectionReferences();
                MigrateSourceCaches();
                MigrateSnapshotRepairOwners();
                Catalog.AbandonInterruptedVolumeArchives();
                Catalog.UnregisterAbandonedArchiveOutputs();
                Catalog.UnregisterDeletedLocalNodeData();
                Catalog.RefreshLocalBackupNodes();
                PruneDatabaseRecoveryCopies();
            }
            catch (global::System.Exception diagnosticFailure)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/DesktopBackend.cs:DesktopBackend", diagnosticFailure);
                try
                {
                    if (Bandwidth != null)
                        Bandwidth.Dispose();
                    if (repositoryRuntime != null)
                        repositoryRuntime.Dispose();
                }
                finally
                {
                    Catalog.Dispose();
                }

                throw;
            }
        }

        public string ObjectDirectory(DestinationDefinition member)
        {
            return member.Location;
        }

        public DestinationAdapterRegistry Adapters(bool findMoved = false, bool useCheckedInventory = false)
        {
            return Adapters(findMoved, useCheckedInventory, null);
        }

        private DestinationAdapterRegistry Adapters(bool findMoved, bool useCheckedInventory, RestoreSearchScope restoreSearch)
        {
            var maintenance = maintenanceSearch.Value;
            if (restoreSearch == null && maintenance != null)
                restoreSearch = maintenance.Search;
            var inventory = maintenance == null ? null : maintenance.SearchInventory;
            var registry = new DestinationAdapterRegistry
            {
                UploadJournal = Catalog,
                Bandwidth = Bandwidth,
                Transfers = Transfers,
                Memory = TransferMemory,
                Cancellation = downloadCancellation.Value ?? (ActiveBackup == null ? OperationCancellation : ActiveBackup.Token)
            };
            registry.FallbackFactory = d => objectStorage.Wrap(
                d,
                folder => PhysicalObjectDestination(d, folder),
                findMoved,
                true,
                useCheckedInventory || inventory != null 
                ? (maintenance == null ? CheckedInventory(d) : maintenance.Inventory(d, () => inventory == null ? CheckedInventory(d) : inventory(d))) 
                : null,
                restoreSearch
            );
            return registry;
        }

        private IManagedBackupDestination PhysicalObjectDestination(DestinationDefinition d, string folder)
        {
            var token = downloadCancellation.Value ?? (ActiveBackup == null ? OperationCancellation : ActiveBackup.Token);
            return RepositoryDirectory(d, folder, token);
        }

        public List<string> ProtectedRoots()
        {
            return new[]
            {
                BaseDirectory
            }.Concat(Catalog.LoadTopology().Destinations.Where(x => RepositoryModules.IsFileSystem(x.Protocol)).Select(x => x.Location))
                .ToList();
        }

        public List<ExclusionRule> Rules()
        {
            return BackupExclusions.NormalizeRules(JsonSettings.Decode(Catalog.GetSetting("exclusions"), BackupExclusions.Defaults()));
        }

        public void SaveRules(List<ExclusionRule> rules)
        {
            rules = BackupExclusions.NormalizeRules(rules);
            new BackupExclusions(rules);
            Catalog.SetSetting("exclusions", JsonSettings.Encode(rules));
        }

        public void SaveTopology(StorageTopology topology)
        {
            topology = ValidateStorageTopology(topology);
            ValidateDatabasePolicyChange(Catalog.LoadTopology(), topology);
            Catalog.SaveTopology(topology);
            InvalidateRepositoryConnections(topology: topology);
        }

        private StorageTopology ValidateStorageTopology(StorageTopology topology)
        {
            topology = topology.Normalize();
            foreach (var member in topology.Destinations)
            {
                RepositoryModules.NormalizeLocation(member.Protocol, member.Location);
                if (!RepositoryModules.IsFileSystem(member.Protocol))
                    continue;
                string location = Path.GetFullPath(member.Location).TrimEnd('\\');
                string app = BaseDirectory.TrimEnd('\\');
                if (location.Equals(app, StringComparison.OrdinalIgnoreCase) || app.StartsWith(location + "\\", StringComparison.OrdinalIgnoreCase) ||
                    location.StartsWith(app + "\\", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("仓库不能与软件安装目录重叠。");
            }

            return topology;
        }

        public DesktopBackupResult Backup(IList<SourceItem> indexed, int copies, string password, int volumeMiB, Action<DesktopProgress> progress)
        {
            return BackupTask(indexed, copies, password, volumeMiB, progress, "legacy", null);
        }

#if UI_TEST
        public Action<TransferProgress> TestTransferProgress;
#endif
        public const int AutomaticVolumeMiB = 200;
        public int PreferredVolumeMiB
        {
            get
            {
                return MemoryLimitMiB >= 768 ? AutomaticVolumeMiB : MemoryLimitMiB >= 512 ? 100 : 50;
            }
        }

        public int MemoryLimitMiB
        {
            get
            {
                int value;
                return int.TryParse(Catalog.GetSetting("backup_memory_mib_v1"), out value) && value >= 256 && value <= 65536 ? value : 2048;
            }

            set
            {
                if (value < 256 || value > 65536)
                    throw new ArgumentException("内存限制应为 256–65536 MiB。");
                Catalog.SetSetting("backup_memory_mib_v1", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        public long MemoryLimitBytes
        {
            get
            {
                return MemoryLimitMiB * 1024L * 1024;
            }
        }

        public BackupControl ActiveBackup { get; private set; }

        public MemoryBudget OperationMemory
        {
            get
            {
                return TransferMemory;
            }
        }

        private IDisposable operationOverhead;
        public void BeginBackup()
        {
            if (ActiveBackup != null)
                throw new InvalidOperationException("备份正在进行。");
            StopRequested = false;
            operationOverhead = OperationMemory.Reserve(MemoryLimitMiB >= 512 ? 128L * 1024 * 1024 : 64L * 1024 * 1024);
            ActiveBackup = new BackupControl(Bandwidth);
            confirmationMonitor = new UploadConfirmationMonitor(this, ActiveBackup);
        }

        public void StopAfterCurrentArchive()
        {
            StopRequested = true;
            var value = ActiveBackup;
            if (value != null)
                value.FinishCurrentArchive();
        }

        public void EndBackup()
        {
            if (confirmationMonitor != null)
            {
                confirmationMonitor.Dispose();
                confirmationMonitor = null;
            }

            var value = ActiveBackup;
            ActiveBackup = null;
            if (value != null)
                value.Dispose();
            if (operationOverhead != null)
            {
                operationOverhead.Dispose();
                operationOverhead = null;
            }

            ClearPreparedSourceChecks();
        }

        public void StopBackup(bool removeIncomplete = false)
        {
            StopRequested = true;
            var value = ActiveBackup;
            if (value != null)
                value.Stop(removeIncomplete);
        }

        public void StopBackupForExit()
        {
            StopRequested = true;
            var value = ActiveBackup;
            if (value != null)
                value.Exit();
        }

        public string ResolveBackupPassword(BackupTaskDefinition task)
        {
            return task.UseCustomPassword ? task.Password ?? "" : Catalog.GetSetting("backup_password_v1") ?? task.Password ?? "";
        }

        public DesktopBackupResult Backup(BackupTaskDefinition task, IList<SourceItem> indexed, Action<DesktopProgress> progress, Action<CleanupProgress> cleanupProgress = null)
        {
            if (task.RecoveryPendingConfirmation)
                throw new InvalidOperationException("导入的任务尚未确认，请先完成恢复并手动确认备份。");
            if (ActiveBackup != null)
                ActiveBackup.Checkpoint();
            BackupNode comparedNode;
            NodeFileDatabase comparedFiles;
            var unchanged = UnchangedTaskBackup(task, indexed, out comparedNode, out comparedFiles);
            if (unchanged != null)
            {
                if (ActiveBackup != null)
                    ActiveBackup.Checkpoint();
                unchanged.Stopped = StopRequested;
                unchanged.NoChanges = !StopRequested;
                DiscardMissingSourceChecks(task.Id);
                LastCleanup = null;
                return unchanged;
            }

            EnsureTaskNodeBasis(task.Id, comparedNode, comparedFiles);
            if (task.RecoveryPendingConfirmation)
                throw new InvalidOperationException("导入的任务尚未确认，请先完成恢复并手动确认备份。");
            if (task.TargetIds.Count == 0)
                throw new ArgumentException("请为任务选择备份仓库或组合。");
            if (task.PendingTargetReplacement)
                throw new ArgumentException("任务仍缺少替代仓库，请先添加仓库或调整勾选目标。");
            WorkspaceMigration.ValidateTargetPolicy(task, Catalog.LoadTopology());
            var missing = TakeMissingSourceChecks(task.Id);
            var result = BackupTask(indexed, task.RequiredCopies, ResolveBackupPassword(task), PreferredVolumeMiB, progress, task.Id, task.TargetIds, BackupRootLayout(task), allowEmpty: true);
            if (!result.Stopped && result.Sources.Count > 0 && confirmationMonitor != null)
                confirmationMonitor.CompletePrevious(task.Id);
            if (!result.Stopped)
            {
                result.RemovedFromScope = ReconcileTaskBackupScope(task);
                result.ConfirmedDeleted += ConfirmMissingSources(task.Id, missing);
            }

            if (!result.Stopped && result.Archives.Count == 0 && result.RemovedFromScope == 0 && result.ConfirmedDeleted == 0 &&
                CompareUnchangedBackup(Catalog.CaptureNode(task.Id, "", DateTime.UtcNow).Files, comparedFiles) != null)
            {
                result.NoChanges = true;
                result.CatalogSkippedUnchanged = true;
            }

            if (StopRequested)
                result.Stopped = true;
            if (result.Stopped || result.Archives.Any(item => item.Archive.Status != "complete"))
            {
                Catalog.RefreshLocalBackupNode(task.Id);
                result.Node = Catalog.GetLocalBackupNodes(task.Id).FirstOrDefault();
            }

            LastCleanup = null;
            return result;
        }

        private DesktopBackupResult BackupTask(
            IList<SourceItem> indexed,
            int copies,
            string password,
            int volumeMiB,
            Action<DesktopProgress> progress,
            string taskId,
            IList<string> groupIds,
            ArchiveRootLayout layout = null,
            bool forceBackup = false,
            Action<SourceItem> sourceMissing = null,
            bool allowEmpty = false
        )
        {
            if (copies < 1 || volumeMiB != 50 && volumeMiB != 100 && volumeMiB != AutomaticVolumeMiB)
                throw new ArgumentException("份数或包大小无效。");
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("请在设置中填写备份密码。");
            bool owns = ActiveBackup == null;
            if (owns)
                BeginBackup();
            var control = ActiveBackup;
            try
            {
                var result = new DesktopBackupResult
                {
                    Sources = new List<SourceItem>(),
                    ObservedSources = new List<SourceItem>()
                };
                var sourceWorkspace = WorkspaceMigration.Load(Catalog);
                var prior = Catalog.GetLatestFileViews(taskId).GroupBy(x => x.ArchivePath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.ArchiveId).First(), StringComparer.OrdinalIgnoreCase);
                var topology = groupIds == null ? Catalog.LoadTopology() : Catalog.LoadTopology().Scope(groupIds);
                int target = GroupAllocator.Plan(topology, Catalog.GetUsage(), 1, copies, null).TargetCopies;
                // The entry workflow refreshes the index. Only candidates that need data
                // are probed at the source, immediately before their archive is read.
                var coverage = new Dictionary<long, bool>();
                var selected = new List<SourceItem>();
                int eligible = 0;
                foreach (var original in EligibleBackupSources(indexed))
                {
                    control.Checkpoint();
                    if (StopRequested)
                    {
                        result.Stopped = true;
                        return result;
                    }

                    eligible++;
                    bool attention;
                    if (NeedsBackup(original, forceBackup, prior, topology, target, coverage, out attention))
                        selected.Add(original);
                    else
                    {
                        result.SkippedUnchanged++;
                        if (attention)
                            result.AttentionUnchanged++;
                    }
                }

                if (eligible == 0 && !allowEmpty)
                    throw new ArgumentException("没有可备份的已勾选文件；已排除软件目录、仓库目录及匹配排除规则的文件。");
                long batchLimit = groupIds == null ? volumeMiB * 1024L * 1024 : volumeMiB * 1024L * 1024 * 96 / 100;
                var batches = ArchiveBatchPlanner.Create(selected, batchLimit);
                int totalArchives = batches.Count;
                long total = selected.Sum(x => x.Size), read = 0, lastTick = 0, buffered = 0, memory = MemoryLimitBytes;
                int finished = 0, volumes = 0, copyCount = 0;
                bool waiting = false;
                var warnings = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
                var alerts = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
                var labels = Catalog.LoadTopology().Groups.Where(g => g.Id.StartsWith("single:")).ToDictionary(g => g.Id.Substring(7), g => g.Name);
                var pipeline = new BackupPipelineView();
                Action<string, bool> report = delegate (string message, bool force)
                {
                    if (progress == null)
                        return;
                    long tick = Environment.TickCount & int.MaxValue;
                    if (!force && tick - Interlocked.Read(ref lastTick) < 120)
                        return;
                    Interlocked.Exchange(ref lastTick, tick);
                    progress(new DesktopProgress {
                        UploadAlert = string.Join("\n", alerts.Values),
                        ReadBytes = Interlocked.Read(ref read),
                        TotalBytes = Interlocked.Read(ref total),
                        FinishedArchives = Interlocked.CompareExchange(ref finished, 0, 0),
                        TotalArchives = totalArchives,
                        UploadedVolumes = Interlocked.CompareExchange(ref volumes, 0, 0),
                        UploadedCopies = Interlocked.CompareExchange(ref copyCount, 0, 0),
                        BufferedBytes = Interlocked.Read(ref buffered),
                        MemoryLimit = memory,
                        WaitingForMemory = waiting,
                        Message = pipeline.Text(message)
                    });
                };
                Action<TransferProgress> confirmed = p =>
                {
                    pipeline.Observe(p);
                    string ignored;
                    alerts.TryRemove("confirm:" + p.DestinationId + "|" + p.ObjectName, out ignored);
                    report((p.State == "temporary-discarded" ? "临时上传记录已清除：" : p.State == "upload-not-started" ? "分卷尚未上传，已移出待确认：" : "上传结果已确认：") + p.ObjectName, true);
                };
                UploadConfirmationProgress += confirmed;
                Action<long> observeRead = delegate (long bytes)
                {
                    Interlocked.Add(ref read, bytes);
                    report("正在读取并加密文件", false);
                };
                var adapters = Adapters();
                var engine = new GroupedBackupEngine(Catalog, adapters, Path.Combine(BaseDirectory, "7za.dll"));
                var pending = new List<System.Threading.Tasks.Task<GroupedBackupResult>>();
                Exception failure = null;
                var scheduler = Transfers;
                try
                {
                    using (adapters.BeginScope())
                    using (var buffers = new VolumeBufferPool(memory, volumeMiB * 1024 * 1024, control, OperationMemory))
                    {
                        var readBatches = ReadBackupBatches(batches, batchLimit, result, taskId, forceBackup, sourceMissing, sourceWorkspace, layout, prior, topology, target, coverage, difference => Interlocked.Add(ref total, difference));
                        int started = 0;
                        try
                        {
                            using (var next = readBatches.GetEnumerator())
                                while (true)
                                {
                                    if (StopRequested)
                                    {
                                        result.Stopped = true;
                                        break;
                                    }

                                    // Bound archive metadata/finalizers as well as payload memory.
                                    while (pending.Count(t => !t.IsCompleted) >= 8)
                                    {
                                        System.Threading.Tasks.Task.WhenAny(pending.Where(t => !t.IsCompleted)).GetAwaiter().GetResult();
                                        control.Checkpoint();
                                    }

                                    control.Checkpoint();
                                    if (StopRequested)
                                    {
                                        result.Stopped = true;
                                        break;
                                    }

                                    report("正在核对本批文件信息", true);
                                    if (!next.MoveNext())
                                        break;
                                    var batch = next.Current;
                                    control.Checkpoint();
                                    if (StopRequested)
                                    {
                                        result.Stopped = true;
                                        break;
                                    }

                                    started++;
                                    totalArchives = Math.Max(totalArchives, started);
                                    result.Sources.AddRange(batch);
                                    foreach (var source in batch)
                                        source.ReadObserver = observeRead;
                                    report("正在读取并压缩文件", true);
                                    string method = batch.All(x => new[] { ".7z", ".zip", ".mp4", ".mkv", ".wim", ".esd", ".jpg", ".png" }.Contains(Path.GetExtension(x.Path).ToLowerInvariant())) ? "Copy" : "LZMA2";
                                    Action<TransferProgress> transfer = p =>
                                    {
                                        pipeline.Observe(p);
                                        ReportTransferNotice(p);
                                        if (p.State == "cleanup-pending")
                                        {
                                            alerts["cleanup"] = "未完成归档清理待重试：" + p.Error;
                                            report(alerts["cleanup"], true);
                                            return;
                                        }

                                        string label;
                                        labels.TryGetValue(p.DestinationId ?? "", out label);
                                        label = label ?? p.DestinationId;
                                        if (p.State == "upload-check-result" || p.State == "confirmation-succeeded" || p.State == "copy-missing" || p.State == "archive-upload-failed")
                                        {
                                            if (p.State == "confirmation-succeeded")
                                            {
                                                string ignored;
                                                alerts.TryRemove("confirm:" + p.DestinationId + "|" + p.ObjectName, out ignored);
                                            }

                                            report(RetryUploader.FailureMessage(p), true);
                                            return;
                                        }

                                        if (p.State == "confirmation-pending")
                                        {
                                            alerts["confirm:" + p.DestinationId + "|" + p.ObjectName] = RetryUploader.FailureMessage(p).Replace(p.DestinationId + " · ", label + " · ");
                                            report(alerts["confirm:" + p.DestinationId + "|" + p.ObjectName], true);
                                            return;
                                        }

                                        if (p.State == "upload-failed" || p.State == "retrying" || p.State == "retry-succeeded")
                                        {
                                            string noticeKey = p.DestinationId + "|" + p.ObjectName;
                                            if (p.State == "retry-succeeded")
                                            {
                                                string ignored;
                                                alerts.TryRemove(noticeKey, out ignored);
                                                alerts.TryRemove("confirm:" + noticeKey, out ignored);
                                            }
                                            else if (p.State != "upload-failed" || p.Attempt >= p.AttemptLimit)
                                                alerts[noticeKey] = RetryUploader.FailureMessage(p).Replace(p.DestinationId + " · ", label + " · ");
                                            report("正在备份", true);
                                            return;
                                        }

                                        if (p.State == "destination-unavailable")
                                        {
                                            warnings.TryAdd(p.DestinationId, p.ObjectName + "：" + p.Error);
                                            alerts[p.DestinationId + "|" + p.ObjectName] = "上传失败，无法继续使用此仓库：" + label + " · " + p.ObjectName + "\n原因：" + p.Error;
                                            report(alerts[p.DestinationId + "|" + p.ObjectName], true);
                                            return;
                                        }

#if UI_TEST
                                        if (TestTransferProgress != null)
                                            TestTransferProgress(p);
#endif
                                        string stage = RetryUploader.StageMessage(p, "备份包");
                                        if (stage != null)
                                        {
                                            report(stage, true);
                                            return;
                                        }

                                        if (p.State == "volume-complete")
                                            Interlocked.Increment(ref volumes);
                                        if (p.State == "copy-complete")
                                            Interlocked.Increment(ref copyCount);
                                        Interlocked.Exchange(ref buffered, p.MemoryBytes);
                                        waiting = p.WaitingForMemory;
                                        report("正在备份", false);
                                    };
                                    if (groupIds == null)
                                    {
                                        var archive = engine.BackupForTask(batch, copies, password, volumeMiB * 1024 * 1024, method, null, null, transfer, taskId, groupIds);
                                        result.Archives.Add(archive);
                                        Interlocked.Increment(ref finished);
                                    }
                                    else
                                    {
                                        var prepared = engine.PrepareForTask(batch, copies, password, volumeMiB * 1024 * 1024, method, transfer, taskId, groupIds, control, buffers, scheduler);
                                        pending.Add(CompletePreparedAsync(
                                            prepared,
                                            completed =>
                                        {
                                            Interlocked.Increment(ref finished);
                                            report(completed.Archive.ArchiveName + " · " + Display.Status(completed.Archive.Status), true);
                                        }
                                        ));
                                    }
                                }

                            totalArchives = started;
                        }
                        catch (Exception error)
                        {
                            global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/DesktopBackend.cs:BackupTask", error);
                            if (error is OperationCanceledException && control.IsFinishing && !control.IsStopping)
                                result.Stopped = true;
                            else
                            {
                                failure = error;
                                control.Stop();
                            }
                        }

                        foreach (var job in pending)
                            try
                            {
                                result.Archives.Add(job.GetAwaiter().GetResult());
                            }
                            catch (Exception error)
                            {
                                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/DesktopBackend.cs:BackupTask", error);
                                if (failure == null)
                                    failure = error;
                                control.Stop();
                            }

                        if (StopRequested)
                            result.Stopped = true;
                    }

                    CleanupStoppedOperation(message => alerts["cleanup"] = message);
                    if (failure != null)
                    {
                        if (failure is OperationCanceledException)
                            result.Stopped = true;
                        else
                            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                    }
                }
                finally
                {
                    UploadConfirmationProgress -= confirmed;
                    result.UploadedFiles = Interlocked.CompareExchange(ref volumes, 0, 0);
                    if (result.Sources.Count > 0 || warnings.Count > 0 || alerts.Count > 0)
                    {
                        var warningTopology = Catalog.LoadTopology();
                        foreach (var warning in warnings)
                        {
                            var name = warningTopology.Groups.FirstOrDefault(g => g.Id == "single:" + warning.Key);
                            result.UploadWarnings.Add((name == null ? warning.Key : name.Name) + "：" + warning.Value);
                        }

                        Catalog.SetSetting("last_upload_warnings_v1", string.Join("\n", result.UploadWarnings));
                        Catalog.SetSetting("last_upload_alert_v1", string.Join("\n", alerts.Where(pair => !pair.Key.StartsWith("confirm:", StringComparison.Ordinal)).Select(pair => pair.Value)));
                    }
                }

                RefreshBackupResultState(result);
                return result;
            }
            finally
            {
                if (owns)
                    EndBackup();
            }
        }

        private void RefreshBackupResultState(DesktopBackupResult result)
        {
            if (result.Archives.Count == 0)
            {
                result.PendingConfirmations = 0;
                return;
            }

            var ids = new HashSet<long>(result.Archives.Select(item => item.Archive.ArchiveId));
            var pending = Catalog.PendingVolumeTransfers().Where(item => ids.Contains(item.ArchiveId) && item.AwaitingConfirmation).ToList();
            var pendingIds = new HashSet<long>(pending.Select(item => item.ArchiveId));
            foreach (var item in result.Archives)
                if (item.Archive.Status != "complete" || pendingIds.Contains(item.Archive.ArchiveId))
                    item.Archive = Catalog.GetArchive(item.Archive.ArchiveId);
            result.PendingConfirmations = pending.Count;
        }

        public IList<IntegrityFinding> CheckAll(Action<string> report)
        {
            return CheckTask(null, report);
        }

        public IList<IntegrityFinding> CheckTask(string taskId, Action<string> report)
        {
            return CheckAllBackups(report, taskId == null ? null : new[] { taskId }).Findings;
        }

        public void ReconcileDeleted(BackupTaskDefinition task, IList<SourceDefinition> sources, IEnumerable<SourceItem> indexed, Action<CleanupProgress> cleanupProgress = null, bool collectGarbage = true)
        {
            if (task.RecoveryPendingConfirmation)
                return;
            ConfirmMissingSources(task.Id, MissingSourceChecks(task, sources, indexed));
            if (collectGarbage)
                LastCleanup = CollectGarbageDetailed(cleanupProgress, task.Id);
        }

        private bool ConfirmDeleted(string taskId, string archivePath, string path, string sourceRoot)
        {
            try
            {
                ValidateSourceMissing(path, sourceRoot);
                Catalog.MarkDeleted(taskId, archivePath, true);
                return true;
            }
            catch (IOException diagnosticFailure)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/DesktopBackend.cs:ConfirmDeleted", diagnosticFailure);
            }
            catch (UnauthorizedAccessException diagnosticFailure)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/DesktopBackend.cs:ConfirmDeleted", diagnosticFailure);
            }

            return false;
        }

        private static async System.Threading.Tasks.Task<GroupedBackupResult> CompletePreparedAsync(PreparedBackup prepared, Action<GroupedBackupResult> completed)
        {
            using (prepared)
            {
                var result = await prepared.CompleteAsync().ConfigureAwait(false);
                completed(result);
                return result;
            }
        }

        public void Dispose()
        {
            try
            {
                if (ServerTrust != null)
                    ServerTrust.Dispose();
                Transfers.Dispose();
                if (Bandwidth != null)
                    Bandwidth.Dispose();
                InvalidateRepositoryConnections(close: true);
                if (repositoryRuntime != null)
                    repositoryRuntime.Dispose();
            }
            finally
            {
                Catalog.Dispose();
            }
        }
    }
}
