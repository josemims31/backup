using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Backup.Core
{
    public sealed partial class ArchiveMaintenance
    {
        private static string CopyKey(StoredCopy copy)
        {
            return copy.DestinationId + "\0" + copy.Object.Name;
        }

        private static T CleanupRequest<T>(IManagedBackupDestination target, CancellationToken cancellation, Func<T> request)
        {
            var adapter = target as ICancellableBackupDestination;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                var previous = adapter == null ? CancellationToken.None : adapter.OperationCancellation;
                if (adapter != null)
                    adapter.OperationCancellation = deadline.Token;
                try
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    T result = request();
                    deadline.Token.ThrowIfCancellationRequested();
                    return result;
                }
                catch (Exception error)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (deadline.IsCancellationRequested && (error is OperationCanceledException || error is IOException))
                        throw new TimeoutException("仓库清理请求超时，未确认的项目已保留。", error);
                    throw;
                }
                finally
                {
                    if (adapter != null)
                        adapter.OperationCancellation = previous;
                }
            }
        }

        private static bool CleanupFailure(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException || error is NotSupportedException || error is TimeoutException;
        }

        private static ILookup<string, StorageEntry> CleanupEntries(IList<StorageEntry> entries, StringComparer names)
        {
            return entries == null ? null : entries.ToLookup(entry => entry.Name, names);
        }

        // One folder observation before deletion and one after the last change.
        // Exact checks remain available to adapters that only implement object I/O.
        private IList<string> DeleteRecordedCopies(IList<StoredCopy> copies, IList<PendingVolumeTransfer> pending, ISet<string> stages, string caption,
            CancellationToken cancellation, ISet<string> keep = null, ISet<string> owned = null)
        {
            var errors = new List<string>();
            var transfers = pending.ToDictionary(transfer => transfer.Placement.DestinationId + "\0" + transfer.Object.Name);
            int processed = 0;
            foreach (var repository in copies.GroupBy(copy => copy.DestinationId))
            {
                cancellation.ThrowIfCancellationRequested();
                string name = DestinationName(repository.Key);
                try
                {
                    IManagedBackupDestination target = Open(repository.Key);
                    var located = target as LocatedBackupDestination;
                    var comparer = located == null ? StringComparer.Ordinal : located.RecordedNameComparer;
                    var folders = repository.GroupBy(copy => located == null ? "" : located.RecordedFolder(copy.Object.Name));
                    bool readableRoot = false;
                    foreach (var folder in folders)
                    {
                        try
                        {
                            if (Report != null)
                                Report(caption + " · 正在核对 " + name + " · " + processed + " / " + copies.Count);
                            var before = located == null || folder.Count() < 3 ? null :
                                CleanupEntries(CleanupRequest(target, cancellation, () => located.ReadRecordedDirectory(folder.Key, true)), comparer);
                            if (before == null && !readableRoot)
                            {
                                var probe = target as IReadOnlyDestinationProbe;
                                if (probe != null)
                                    CleanupRequest(target, cancellation, () => { probe.CheckReadableRoot(); return true; });
                                readableRoot = true;
                            }

                            var deleted = new List<StoredCopy>();
                            foreach (var copy in folder)
                            {
                                if (Control != null)
                                    Control.Checkpoint(false);
                                cancellation.ThrowIfCancellationRequested();
                                string key = CopyKey(copy), file = copy.Object.Name;
                                processed++;
                                if (Report != null)
                                    Report(caption + " · " + processed + " / " + copies.Count + " · " + file + " → " + name);
                                try
                                {
                                    bool retained = keep != null && keep.Contains(key);
                                    if (!retained)
                                    {
                                        bool exists;
                                        if (before == null)
                                            exists = CleanupRequest(target, cancellation, () => target.Stat(file)).Exists;
                                        else
                                        {
                                            var matches = before[file];
                                            if (matches.Skip(1).Any() || matches.Any(entry => entry.Directory || entry.Size < 0))
                                                throw new InvalidBackupObjectException("目录中存在同名项目或类型不符，已保留。");
                                            // Directory responses can lag a recently present
                                            // file. Check only those absent from the listing.
                                            exists = matches.Any() || CleanupRequest(target, cancellation, () => target.Stat(file)).Exists;
                                        }

                                        if (exists)
                                        {
                                            PendingVolumeTransfer transfer;
                                            if (owned != null && !owned.Contains(key) && (!transfers.TryGetValue(key, out transfer) || transfer.Phase != "committing" || !BackupObjectNaming.IsManaged(file)))
                                                throw new IOException("远程同名文件没有已提交的所有权记录，已保留。");
                                            CleanupRequest(target, cancellation, () => { target.DeleteObject(file); return true; });
                                            if (before != null)
                                            {
                                                deleted.Add(copy);
                                                continue;
                                            }
                                            if (CleanupRequest(target, cancellation, () => target.Stat(file)).Exists)
                                                throw new IOException("远程删除尚未生效。");
                                        }
                                    }

                                    FinishRecordedDeletion(copy, retained, owned, stages, transfers, target, cancellation);
                                }
                                catch (Exception error)
                                {
                                    if (!CleanupFailure(error) || error is TimeoutException)
                                        throw;
                                    RuntimeDiagnostics.Record("ArchiveMaintenance:DeleteRecordedCopy", error);
                                    errors.Add(file + " → " + name + "：" + error.Message);
                                }
                            }

                            if (deleted.Count == 0)
                                continue;
                            if (Report != null)
                                Report(caption + " · 正在确认 " + name + " 删除结果 · " + processed + " / " + copies.Count);
                            var after = CleanupEntries(CleanupRequest(target, cancellation, () => located.ReadRecordedDirectory(folder.Key, true)), comparer);
                            foreach (var copy in deleted)
                            {
                                cancellation.ThrowIfCancellationRequested();
                                string file = copy.Object.Name;
                                if (after == null || after.Contains(file))
                                    errors.Add(file + " → " + name + "：远程删除尚未确认。");
                                else
                                    FinishRecordedDeletion(copy, false, owned, stages, transfers, target, cancellation);
                            }
                        }
                        catch (Exception error)
                        {
                            if (!CleanupFailure(error) || error is TimeoutException)
                                throw;
                            RuntimeDiagnostics.Record("ArchiveMaintenance:DeleteRecordedDirectory", error);
                            errors.Add(caption + " → " + name + "：目录清理待重试；" + error.Message);
                        }
                    }
                }
                catch (Exception error)
                {
                    if (!CleanupFailure(error))
                        throw;
                    RuntimeDiagnostics.Record("ArchiveMaintenance:DeleteRecordedCopies", error);
                    errors.Add(caption + " → " + name + "：" + error.Message);
                }
            }
            return errors;
        }

        private void FinishRecordedDeletion(StoredCopy copy, bool keep, ISet<string> owned, ISet<string> stages,
            IDictionary<string, PendingVolumeTransfer> transfers, IManagedBackupDestination target, CancellationToken cancellation)
        {
            string file = copy.Object.Name, key = CopyKey(copy);
            if (!keep)
            {
                if (owned == null)
                    catalog.ClearAbsentInspectedRecord(file, copy.DestinationId);
                else if (owned.Contains(key))
                    catalog.ObjectRemoved(file, copy.DestinationId);
            }
            if (stages.Contains(key))
                CleanupRequest(target, cancellation, () => { target.DeleteTemporary(file); return true; });
            PendingVolumeTransfer pending;
            if (transfers.TryGetValue(key, out pending))
                catalog.ResolveVolumeTransfer(pending, keep);
        }
    }
}
