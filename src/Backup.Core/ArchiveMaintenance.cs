using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;

namespace Backup.Core
{
    public sealed class NoReadableReplicaException : IOException
    {
        public NoReadableReplicaException() : base("所有加密副本均不完整或暂不可访问；旧记录保留。")
        {
        }
    }

    public static class ArchiveObjectIO
    {
        public const long MaximumVolumeBytes = 200L * 1024 * 1024;
        public static void CopyVerified(
            IManagedBackupDestination source,
            ObjectRecord item,
            Stream target,
            BackupControl control = null,
            CancellationToken cancellation = default(CancellationToken),
            Action<long> progress = null,
            TransferScheduler transfers = null,
            MemoryBudget memory = null
        )
        {
            var located = source as LocatedBackupDestination;
            if (located == null || !located.FindsMovedObjects)
            {
                CopyDownloaded(source, item, target, control, cancellation, progress, transfers, memory);
                return;
            }

            long start = target.CanSeek ? target.Position : 0;
            IOException failure = null;
            foreach (string directory in located.ReadDirectories(item.Name, item.Size))
                try
                {
                    if (target.CanSeek)
                        target.Position = start;
                    CopyDownloaded(located.ReadSourceAt(directory), item, target, control, cancellation, progress, transfers, memory);
                    located.RememberReadDirectory(item.Name, directory);
                    return;
                }
                catch (IOException error)
                {
                    if (!target.CanSeek)
                        throw;
                    failure = error;
                }

            throw failure ?? new System.IO.FileNotFoundException("没有找到名称、大小及内容校验均匹配的备份分卷：" + item.Name);
        }

        private static void CopyDownloaded(
            IManagedBackupDestination source,
            ObjectRecord item,
            Stream target,
            BackupControl control,
            CancellationToken cancellation,
            Action<long> progress,
            TransferScheduler transfers,
            MemoryBudget memory
        )
        {
            using (var hash = SHA256.Create())
            {
                DownloadIO.CopyTo(
                    source,
                    item.Name,
                    item.Size,
                    (bytes, offset, count) =>
                {
                    hash.TransformBlock(bytes, offset, count, null, 0);
                    target.Write(bytes, offset, count);
                },
                    transfers,
                    memory,
                    control == null ? cancellation : control.Token,
                    progress: progress,
                    control: control
                );
                hash.TransformFinalBlock(new byte[0], 0, 0);
                if (!BitConverter.ToString(hash.Hash).Replace("-", "").Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new ArchiveContentMismatchException(item.Name);
            }
        }

        public static void CopyVerified(
            Func<Stream> open,
            ObjectRecord item,
            Stream target,
            BackupControl control = null,
            CancellationToken cancellation = default(CancellationToken),
            Action<long> progress = null
        )
        {
            using (var input = open())
            using (var hash = SHA256.Create())
            {
                byte[] buffer = new byte[1024 * 1024];
                long total = 0;
                int count;
                if (progress != null)
                    progress(0);
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (control != null)
                        control.Checkpoint(false);
                    var token = control == null ? cancellation : control.Token;
                    count = token.CanBeCanceled ? input.ReadAsync(buffer, 0, buffer.Length, token).GetAwaiter().GetResult() : input.Read(buffer, 0, buffer.Length);
                    if (count == 0)
                        break;
                    total += count;
                    if (total > item.Size)
                        throw new ArchiveContentMismatchException(item.Name);
                    hash.TransformBlock(buffer, 0, count, null, 0);
                    target.Write(buffer, 0, count);
                    if (progress != null)
                        progress(total);
                }

                hash.TransformFinalBlock(new byte[0], 0, 0);
                if (total != item.Size || !BitConverter.ToString(hash.Hash).Replace("-", "").Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new ArchiveContentMismatchException(item.Name);
            }
        }

        public static byte[] ReadVerified(
            IManagedBackupDestination source,
            ObjectRecord item,
            BackupControl control = null,
            TransferScheduler transfers = null,
            byte[] buffer = null,
            Action<long> progress = null
        )
        {
            if (item.Size > MaximumVolumeBytes || item.Size <= 0)
                throw new IOException("分卷超过内存传输上限。");
            var bytes = buffer ?? new byte[(int)item.Size];
            ReadVerifiedInto(source, item, bytes, transfers, control, progress: progress);
            return bytes;
        }

        public static void ReadVerifiedInto(
            IManagedBackupDestination source,
            ObjectRecord item,
            byte[] buffer,
            TransferScheduler transfers = null,
            BackupControl control = null,
            CancellationToken cancellation = default(CancellationToken),
            Action<long> progress = null
        )
        {
            if (item.Size <= 0 || item.Size > MaximumVolumeBytes || buffer.LongLength < item.Size)
                throw new IOException("分卷超过内存传输上限。");
            var cancel = source as ICancellableBackupDestination;
            var token = cancellation.CanBeCanceled ? cancellation : control != null ? control.Token : cancel == null ? CancellationToken.None : cancel.OperationCancellation;
            var located = source as LocatedBackupDestination;
            if (located == null || !located.FindsMovedObjects)
            {
                ReadDownloadedInto(source, item, buffer, transfers, control, token, progress);
                return;
            }

            IOException failure = null;
            foreach (string directory in located.ReadDirectories(item.Name, item.Size))
                try
                {
                    ReadDownloadedInto(located.ReadSourceAt(directory), item, buffer, transfers, control, token, progress);
                    located.RememberReadDirectory(item.Name, directory);
                    return;
                }
                catch (IOException error)
                {
                    failure = error;
                }

            throw failure ?? new FileNotFoundException("没有找到名称、大小及内容校验均匹配的备份分卷：" + item.Name);
        }

        private static void ReadDownloadedInto(IManagedBackupDestination source, ObjectRecord item, byte[] buffer, TransferScheduler transfers, BackupControl control, CancellationToken token, Action<long> progress)
        {
            DownloadIO.ReadInto(source, item.Name, item.Size, buffer, transfers, token, progress, control);
            using (var hash = SHA256.Create())
                if (!BitConverter.ToString(hash.ComputeHash(buffer, 0, (int)item.Size)).Replace("-", "").Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new ArchiveContentMismatchException(item.Name);
        }
    }

    public sealed partial class ArchiveMaintenance
    {
        private readonly BackupCatalog catalog;
        private readonly DestinationAdapterRegistry adapters;
        private Dictionary<string, DestinationDefinition> operationDefinitions;
        private Dictionary<string, string> operationNames;
        private readonly object migrationGate = new object();
        private MemoryBudget repairMemory;
        private sealed class RepairDownload
        {
            internal ObjectRecord Object;
            internal string Destination;
            internal long Written, LastReport;
        }

        private readonly object downloadProgressGate = new object();
        private readonly List<RepairDownload> activeRepairDownloads = new List<RepairDownload>();
        private CancellationToken RepairCancellation
        {
            get
            {
                return Control == null ? adapters.Cancellation : Control.Token;
            }
        }

        private void RepairParallel<T>(IEnumerable<T> items, int maximum, Action<T> work)
        {
            var failures = new ConcurrentQueue<Exception>();
            try
            {
                Parallel.ForEach(
                    items,
                    new ParallelOptions { MaxDegreeOfParallelism = maximum, CancellationToken = RepairCancellation },
                    item =>
                {
                    try
                    {
                        work(item);
                    }
                    catch (Exception error)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/ArchiveMaintenance.cs:RepairParallel", error);
                        if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is NotSupportedException))
                            throw;
                        failures.Enqueue(error);
                    }
                }
                );
            }
            catch (AggregateException error)
            {
                RepairCancellation.ThrowIfCancellationRequested();
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.Flatten().InnerExceptions.First()).Throw();
                throw;
            }

            if (!failures.IsEmpty)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures.FirstOrDefault(error => error is NoReadableReplicaException) ?? failures.First()).Throw();
        }

        private void RepairObjects(IList<ObjectRecord> items, Action<ObjectRecord> work)
        {
            int count = (int)Math.Max(1, Math.Min(TransferScheduler.MaximumDownloads, (repairMemory.LimitBytes - 64L * 1024 * 1024) / items.Max(item => item.Size)));
            RepairParallel(items, count, work);
        }

        private IDisposable BeginOperation()
        {
            var topology = catalog.LoadTopology();
            operationDefinitions = topology.Destinations.ToDictionary(d => d.Id);
            operationNames = topology.Groups.Where(g => g.Id.StartsWith("single:", StringComparison.Ordinal)).ToDictionary(g => g.Id.Substring(7), g => g.Name);
            return adapters.BeginScope();
        }

        private void ReportObject(string phase, ObjectRecord item, string destination, bool reading = false)
        {
            if (Report == null)
                return;
            lock (downloadProgressGate)
            {
                if (activeRepairDownloads.Count > 0)
                {
                    ReportRepairDownload(activeRepairDownloads[0]);
                    return;
                }

                Report(phase + item.Name + (reading ? " ← " : " → ") + DestinationName(destination));
            }
        }

        private string DestinationName(string destination)
        {
            string name;
            return operationNames != null && operationNames.TryGetValue(destination, out name) && !string.IsNullOrEmpty(name) ? name : destination;
        }

        private void ReportRepairDownload(RepairDownload value)
        {
            if (Report != null)
                Report("正在下载修复分卷：" + value.Object.Name + " ← " + DestinationName(value.Destination) + " · " +
                    (value.Written / 1048576.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) +
                    " / " +
                    (value.Object.Size / 1048576.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) +
                    " MiB" +
                    (activeRepairDownloads.Count > 1 ? "（同时下载 " +
                    activeRepairDownloads.Count +
                    " 个分卷）" : ""));
        }

        private RepairDownload BeginRepairDownload(ObjectRecord item, string destination)
        {
            lock (downloadProgressGate)
            {
                var value = new RepairDownload
                {
                    Object = item,
                    Destination = destination
                };
                activeRepairDownloads.Add(value);
                ReportDownloadProgress(value, "download-started");
                ReportRepairDownload(value);
                return value;
            }
        }

        private void UpdateRepairDownload(RepairDownload value, long written)
        {
            lock (downloadProgressGate)
            {
                value.Written = written;
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (value.LastReport != 0 && now - value.LastReport < System.Diagnostics.Stopwatch.Frequency / 2 && written != value.Object.Size)
                    return;
                value.LastReport = now;
                ReportDownloadProgress(value, "downloading");
                ReportRepairDownload(value);
            }
        }

        private void EndRepairDownload(RepairDownload value)
        {
            lock (downloadProgressGate)
            {
                activeRepairDownloads.Remove(value);
                ReportDownloadProgress(value, "download-finished");
                if (activeRepairDownloads.Count > 0)
                    ReportRepairDownload(activeRepairDownloads[0]);
            }
        }

        private void ReportDownloadProgress(RepairDownload value, string state)
        {
            ReportUploadProgress(new TransferProgress {
                ObjectName = value.Object.Name,
                DestinationId = value.Destination,
                DestinationLabel = DestinationName(value.Destination),
                Written = value.Written,
                Size = value.Object.Size,
                State = state
            });
        }

        public BackupControl Control;
        public Action<string> Report;
        public Func<string, string, int, long, bool> InjectFailure;
        public Action<TransferProgress> UploadProgress;
        private void ReportUploadProgress(TransferProgress value)
        {
            var handler = UploadProgress;
            if (handler != null)
                foreach (Action<TransferProgress> observer in handler.GetInvocationList())
                    try
                    {
                        observer(value);
                    }
                    catch (global::System.Exception diagnosticFailure)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/ArchiveMaintenance.cs:ReportUploadProgress", diagnosticFailure);
                    }
        }

        public Action<long> BeforeRetiredDeletion;
        public ISet<string> RetiredDeletionDestinations;
        public string RetiredDeletionGroupId;
        public ArchiveMaintenance(BackupCatalog value, DestinationAdapterRegistry registry)
        {
            catalog = value;
            adapters = registry;
            registry.UploadJournal = value;
        }

        private IManagedBackupDestination Open(string id)
        {
            if (Control != null)
                Control.Checkpoint(false);
            if (operationDefinitions == null)
                operationDefinitions = catalog.LoadTopology().Destinations.ToDictionary(d => d.Id);
            var value = adapters.Create(operationDefinitions[id]) as IManagedBackupDestination;
            if (value == null)
                throw new NotSupportedException("此仓库尚未实现下载和删除。");
            var cancel = value as ICancellableBackupDestination;
            if (cancel != null)
                cancel.OperationCancellation = RepairCancellation;
            return value;
        }

        private IManagedBackupDestination OpenTransfer(string id)
        {
            if (Control != null)
                Control.Checkpoint(false);
            var value = adapters.CreateTransfer(operationDefinitions[id]) as IManagedBackupDestination;
            if (value == null)
                throw new NotSupportedException("此仓库尚未实现下载和删除。");
            var cancel = value as ICancellableBackupDestination;
            if (cancel != null)
                cancel.OperationCancellation = RepairCancellation;
            return value;
        }

        private void PrepareRepairWrites(IEnumerable<string> targets, IDictionary<string, IManagedBackupDestination> ready, IDictionary<string, Exception> failed)
        {
            foreach (string id in targets.Distinct())
            {
                IManagedBackupDestination target = null;
                try
                {
                    target = OpenTransfer(id);
                    target.PrepareWrite();
                    ready.Add(id, target);
                    target = null;
                }
                catch (Exception error)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/ArchiveMaintenance.cs:PrepareRepairWrites", error);
                    if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is NotSupportedException))
                        throw;
                    failed.Add(id, error);
                }
                finally
                {
                    if (target != null)
                        adapters.Release(target);
                }
            }

            if (ready.Count == 0 && failed.Count > 0)
                throw failed.Values.First();
        }

        private IManagedBackupDestination TakePreparedRepairWrite(string id, IDictionary<string, IManagedBackupDestination> ready, IDictionary<string, Exception> failed)
        {
            IManagedBackupDestination target;
            lock (ready)
                if (ready.TryGetValue(id, out target))
                {
                    ready.Remove(id);
                    return target;
                }

            Exception error;
            if (failed.TryGetValue(id, out error))
                throw error;
            target = OpenTransfer(id);
            try
            {
                target.PrepareWrite();
                return target;
            }
            catch (global::System.Exception diagnosticFailure)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/ArchiveMaintenance.cs:TakePreparedRepairWrite", diagnosticFailure);
                adapters.Release(target);
                throw;
            }
        }

        private void Delete(string id, ObjectRecord item)
        {
            var destination = Open(id);
            destination.DeleteObject(item.Name);
            if (destination.Stat(item.Name).Exists)
                throw new IOException("备份对象未删除：" + item.Name);
            destination.DeleteTemporary(item.Name);
            catalog.ObjectRemoved(item.Name, id);
        }

        public IList<string> CollectGarbage()
        {
            return CollectGarbageDetailed(null).Errors;
        }

        public CleanupResult CollectGarbageDetailed(Action<CleanupProgress> progress, bool? systemOnly = null, string taskId = null, ISet<long> archiveIds = null, ISet<string> destinationIds = null, string groupId = null)
        {
            using (BeginOperation())
            {
                var result = new CleanupResult();
                if (progress != null)
                    progress(new CleanupProgress { Message = "正在查找全部失效的备份包…" });
                if (archiveIds != null && archiveIds.Count == 0)
                    return result;
                var candidates = catalog.GetGarbageCandidates(systemOnly, taskId).Where(id => archiveIds == null || archiveIds.Contains(id)).ToList();
                int done = 0, totalObjects = progress == null ? 0 : catalog.CountCleanupObjects(candidates, destinationIds, groupId);
                Action<string> notify = message =>
                {
                    if (progress != null)
                        progress(new CleanupProgress {
                            Message = message,
                            CompletedArchives = done,
                            TotalArchives = candidates.Count,
                            DeletedObjects = result.DeletedObjects,
                            TotalObjects = totalObjects,
                            FreedBytes = result.FreedBytes
                        });
                };
                foreach (long id in candidates)
                {
                    using (var lease = catalog.AcquireCleanupLease(id))
                    {
                        if (lease == null)
                        {
                            done++;
                            continue;
                        }

                        try
                        {
                            var objects = catalog.GetCopies(id).Select(c => new RetiredObject { ArchiveId = id, DestinationId = c.DestinationId, GroupId = c.GroupId, Object = c.Object }).Concat(catalog.GetRetiredObjects(id))
                                .ToList();
                            foreach (var copy in objects.Where(copy => (destinationIds == null || destinationIds.Contains(copy.DestinationId)) && (groupId == null || copy.GroupId == groupId)))
                            {
                                notify("正在清理 #" + id.ToString("D8") + " / " + copy.Object.Name + " → " + copy.DestinationId);
                                Delete(copy.DestinationId, copy.Object);
                                result.DeletedObjects++;
                                result.FreedBytes += copy.Object.Size;
                                notify("已删除 " + copy.Object.Name);
                            }

                            if ((destinationIds != null || groupId != null) && catalog.HasCleanupObjects(id))
                            {
                                catalog.FinishPartialCleanup(id);
                                done++;
                                continue;
                            }

                            catalog.FinishCleanup(id);
                            result.DeletedArchives++;
                            if (Report != null)
                                Report("已清理全部失效的压缩包 #" + id.ToString("D8"));
                        }
                        catch (Exception e)
                        {
                            global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/ArchiveMaintenance.cs:CollectGarbageDetailed", e);
                            if (!(e is IOException) && !(e is UnauthorizedAccessException) && !(e is NotSupportedException))
                                throw;
                            catalog.CleanupError(id, e.Message);
                            result.Errors.Add("#" + id.ToString("D8") + "：" + e.Message);
                        }

                        done++;
                        notify("已处理 " + done + " / " + candidates.Count + " 个备份包");
                    }
                }

                foreach (long id in catalog.GetBlockedCleanup(systemOnly, taskId).Where(id => archiveIds == null || archiveIds.Contains(id)))
                    result.Errors.Add("#" + id.ToString("D8") + "：归档仍在使用或传输待核对，暂不清理。");
                if (systemOnly != true)
                    foreach (long id in catalog.PendingSnapshotRetirements(taskId).Where(id => archiveIds == null || archiveIds.Contains(id)))
                        result.Errors.Add("#" + id.ToString("D8") + "：完整数据库快照尚未同步，旧包已保留。");
                result.PendingArchives = result.Errors.Count;
                notify("清理检查结束");
                return result;
            }
        }

        public void RemoveRetired(long? id = null, bool confirmPublication = false)
        {
            using (BeginOperation())
            {
                if (confirmPublication)
                {
                    if (BeforeRetiredDeletion == null)
                        throw new IOException("迁移后的完整数据库快照尚未确认，旧副本保留。");
                    BeforeRetiredDeletion(id.Value);
                }

                foreach (var group in catalog.GetRetiredObjects(id).Where(item => (RetiredDeletionDestinations == null || RetiredDeletionDestinations.Contains(item.DestinationId)) &&
                    (RetiredDeletionGroupId == null ||
                    item.GroupId == RetiredDeletionGroupId))
                    .GroupBy(item => item.ArchiveId))
                {
                    if (BeforeRetiredDeletion == null)
                        throw new IOException("迁移后的完整数据库快照尚未确认，旧副本保留。");
                    if (!confirmPublication)
                        BeforeRetiredDeletion(group.Key);
                    foreach (var item in group)
                        Delete(item.DestinationId, item.Object);
                }
            }
        }

        // Fast observations never disprove content damage found by an actual read.
        // Only a source needed for a replacement is downloaded and hashed.
        private HashSet<string> MatchedCopies(ObjectRecord item, IEnumerable<StoredCopy> copies, out Exception unavailable)
        {
            var result = new HashSet<string>();
            unavailable = null;
            foreach (var copy in copies)
            {
                IManagedBackupDestination source = null;
                try
                {
                    source = OpenTransfer(copy.DestinationId);
                    var info = source.Stat(item.Name);
                    if (info.Exists && info.Name == item.Name && info.Size == item.Size)
                        result.Add(copy.DestinationId);
                }
                catch (IOException error)
                {
                    unavailable = error;
                }
                catch (UnauthorizedAccessException error)
                {
                    unavailable = error;
                }
                finally
                {
                    if (source != null)
                        adapters.Release(source);
                }
            }

            return result;
        }

        private byte[] ReadRepairSource(long id, ObjectRecord item, ISet<string> available, IEnumerable<string> matched)
        {
            if (item.Size <= 0 || item.Size > ArchiveObjectIO.MaximumVolumeBytes)
                throw new IOException("分卷超过内存传输上限。");
            Exception failure = null;
            var buffer = new byte[(int)item.Size];
            foreach (string source in available.Concat(matched).Distinct().ToList())
            {
                IManagedBackupDestination reader = null;
                var download = BeginRepairDownload(item, source);
                try
                {
                    reader = OpenTransfer(source);
                    var bytes = ArchiveObjectIO.ReadVerified(reader, item, Control, adapters.Transfers, buffer, written => UpdateRepairDownload(download, written));
                    catalog.RecordDownloadedContentVerified(id, source, item);
                    available.Add(source);
                    return bytes;
                }
                catch (ArchiveContentMismatchException)
                {
                    catalog.RecordDownloadedContentDamage(id, source, item.Name);
                    available.Remove(source);
                }
                catch (IOException error)
                {
                    failure = error;
                }
                catch (UnauthorizedAccessException error)
                {
                    failure = error;
                }
                finally
                {
                    try
                    {
                        if (reader != null)
                            adapters.Release(reader);
                    }
                    finally
                    {
                        EndRepairDownload(download);
                    }
                }
            }

            if (failure != null)
                throw new IOException("分卷暂时无法读取，保留原副本和修复记录，请稍后重试：" + item.Name, failure);
            throw new NoReadableReplicaException();
        }

        public void Migrate(long id, StorageTopology targets, int copies)
        {
            using (BeginOperation())
            using (catalog.AcquireArchiveLease(id))
            {
                repairMemory = adapters.Memory ?? new MemoryBudget(2L * 1024 * 1024 * 1024);
                if (!catalog.HasCompleteArchiveManifest(id))
                    throw new IOException("压缩包分卷清单缺失或不完整，无法可靠校验和重建，已保留现有文件及版本引用。");
                if (catalog.IsVolumeArchive(id))
                {
                    MigrateVolumes(id, targets, copies);
                    return;
                }

                if (catalog.HasOutstandingTransfer(id))
                    throw new IOException("归档仍有待核对的原始上传，暂不迁移。");
                var objects = catalog.GetObjects(id);
                if (objects.Count == 0)
                    throw new IOException("没有完整的归档对象。");
                var existing = catalog.GetCopies(id);
                var states = catalog.IntegrityCopyStates(id);
                var matched = new Dictionary<string, HashSet<string>>();
                foreach (var item in objects)
                {
                    Exception unavailable;
                    var found = MatchedCopies(item, existing.Where(copy => copy.Object.Name == item.Name), out unavailable);
                    if (found.Count == 0)
                    {
                        if (unavailable != null)
                            throw new IOException("分卷暂时无法读取，保留原副本和修复记录，请稍后重试：" + item.Name, unavailable);
                        throw new NoReadableReplicaException();
                    }

                    matched[item.Name] = found;
                }

                var reusable = matched.ToDictionary(
                    pair => pair.Key,
                    pair => new HashSet<string>(pair.Value.Where(destination => !states.ContainsKey(destination + "\0" + pair.Key) ||
                    states[destination + "\0" +
                    pair.Key] != "content-damaged"))
                );
                var sources = new HashSet<string>(existing.Select(copy => copy.DestinationId).Where(destination => objects.All(item => reusable[item.Name].Contains(destination))));
                // Old publication cleanup may have failed after the new copy was committed.
                // Keep registered partial replicas: their matching objects can be reused.
                RemoveRetired(id);
                var usage = catalog.GetUsage().Select(u => new DestinationUsage { DestinationId = u.DestinationId, UsedBytes = u.UsedBytes, ReservedBytes = u.ReservedBytes }).ToList();
                long budget = objects.Sum(o => o.Size);
                var stages = catalog.GetReplicaStages(id);
                foreach (var stage in stages)
                {
                    var plan = catalog.ResumeReplica(stage);
                    usage.Single(u => u.DestinationId == stage.DestinationId).ReservedBytes -= plan.Placements[0].ReservedBytes;
                }

                foreach (var u in usage)
                    u.UsedBytes -= existing.Where(c => c.DestinationId == u.DestinationId).Sum(c => c.Object.Size);
                var selected = GroupAllocator.Plan(
                    targets,
                    usage,
                    budget,
                    copies,
                    d =>
                {
                    try
                    {
                        Open(d.Id);
                        return true;
                    }
                    catch (IOException)
                    {
                        return false;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return false;
                    }
                },
                    sources
                );
                int target = selected.TargetCopies;
                if (target == 0 || !selected.StructurallyComplete)
                    throw new IOException("没有可满足原组合策略的替代仓库；旧副本保留。");
                var desired = new ArchivePlan
                {
                    ArchiveId = id,
                    ArchiveName = catalog.GetArchive(id).ArchiveName,
                    BudgetBytes = budget,
                    RequestedCopies = copies,
                    TargetCopies = target,
                    Placements = selected.Placements
                };
                if (desired.Placements.Count < target)
                    throw new IOException("替代仓库容量不足或不可访问，旧副本保留。");
                var writes = new Dictionary<string, ArchivePlan>();
                var knownCopies = existing.ToDictionary(copy => Tuple.Create(copy.DestinationId, copy.Object.Name));
                RepairObjects(
                    objects,
                    item =>
                {
                    var available = reusable[item.Name];
                    if (desired.Placements.All(place => available.Contains(place.DestinationId)))
                        return;
                    var ready = new Dictionary<string, IManagedBackupDestination>();
                    var failed = new Dictionary<string, Exception>();
                    try
                    {
                        PrepareRepairWrites(desired.Placements.Where(place => !available.Contains(place.DestinationId)).Select(place => place.DestinationId), ready, failed);
                        using (repairMemory.WaitReserve(item.Size, RepairCancellation))
                        {
                            byte[] bytes = ReadRepairSource(id, item, available, matched[item.Name]);
                            RepairParallel(
                                desired.Placements.Where(p => !available.Contains(p.DestinationId)).ToList(),
                                5,
                                place =>
                            {
                                var destination = TakePreparedRepairWrite(place.DestinationId, ready, failed);
                                try
                                {
                                    ArchivePlan plan;
                                    lock (migrationGate)
                                    {
                                        if (!writes.TryGetValue(place.DestinationId, out plan))
                                        {
                                            var stage = stages.FirstOrDefault(s => s.DestinationId == place.DestinationId);
                                            long credit = existing.Where(c => c.DestinationId == place.DestinationId).Sum(c => c.Object.Size);
                                            plan = stage == null ? catalog.ReserveReplica(id, place.GroupId, place.DestinationId, budget, credit) : catalog.ResumeReplica(stage);
                                            writes[place.DestinationId] = plan;
                                        }
                                    }

                                    StoredCopy known;
                                    knownCopies.TryGetValue(Tuple.Create(place.DestinationId, item.Name), out known);
                                    if (known == null && destination.Stat(item.Name).Exists)
                                        throw new IOException("目标存在未登记的同名对象，拒绝覆盖。");
                                    catalog.ReplicaIntent(plan.Placements[0].GroupId, item.Name);
                                    if (known != null)
                                    {
                                        catalog.ReserveReplacementObject(plan, item);
                                        destination.DeleteObject(item.Name);
                                    }

                                    ReportObject("正在上传修复分卷：", item, place.DestinationId);
                                    var uploader = new RetryUploader(new[] { destination }, RetryUploader.DefaultAttempts)
                                    {
                                        Transfers = adapters.Transfers,
                                        Progress = p =>
                                        {
                                            ReportUploadProgress(p);
                                            if (Report != null && (p.State == "upload-failed" || p.State == "retrying" || p.State == "retry-succeeded"))
                                                Report(RetryUploader.FailureMessage(p));
                                        },
                                        InjectFailure = InjectFailure,
                                        Control = Control
                                    };
                                    uploader.Upload(item, bytes, bytes.Length);
                                    catalog.CommitObjectCopy(plan, plan.Placements[0], item);
                                    ReportUploadProgress(new TransferProgress { DestinationId = place.DestinationId, ObjectName = item.Name, State = "copy-complete", Written = item.Size, Size = item.Size });
                                    ReportObject("已补齐分卷副本：", item, place.DestinationId);
                                }
                                finally
                                {
                                    adapters.Release(destination);
                                }
                            }
                            );
                        }
                    }
                    finally
                    {
                        foreach (var preparedAdapter in ready.Values)
                            adapters.Release(preparedAdapter);
                    }
                }
                );
                catalog.PublishReplicaPlan(id, desired);
                RemoveRetired(id, true);
            }
        }
    }
}
