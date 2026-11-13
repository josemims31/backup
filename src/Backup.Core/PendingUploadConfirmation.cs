using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Backup.Core
{
    public sealed partial class BackupCatalog
    {
        public void RecordMatchedUploadObservation(PendingVolumeTransfer transfer)
        {
            Transaction(delegate
            {
                string phase = transfer.Phase, state = "checked-" + phase;
                database.Execute("UPDATE volume_transfers SET state=? WHERE name=? AND destination_id=? AND state<>?", state, transfer.Object.Name, transfer.Placement.DestinationId, state);
                database.Execute(
                    "UPDATE placements SET status='repair-required' WHERE archive_id=? AND group_id=? AND destination_id=? AND status<>'repair-required'",
                    transfer.ArchiveId,
                    transfer.Placement.GroupId,
                    transfer.Placement.DestinationId
                );
                database.Execute("UPDATE archives SET status='repair-required' WHERE id=? AND status IN ('complete','redundancy-shortfall')", transfer.ArchiveId);
                RepositoryStatusChanged(CatalogChange.Archives, GetArchive(transfer.ArchiveId).TaskId);
                return 0;
            });
        }

        public bool CanConfirmUpload(long archiveId, bool activeOperation = false)
        {
            return CanConfirmUpload(archiveId, activeOperation, false);
        }

        public bool CanConfirmUpload(long archiveId, bool activeOperation, bool allowVersionRetirement)
        {
            lock (gate)
                return Scalar(
                    ("SELECT count(*) FROM archives a WHERE a.id=? AND a.status IN ('writing','complete','repair-required','redundancy-shortfall','failed','damaged') AND (?=1 OR " +
                    "a.status<>'writing') AND NOT EXISTS(SELECT 1 FROM archive_cleanup c WHERE c.archive_id=a.id AND c.state IN ('rollback','task-delete')) AND NOT EXISTS(SELECT 1 " +
                    "FROM archive_holds h WHERE h.archive_id=a.id AND h.reason<>'interrupted-awaiting-inspection' AND (?=0 OR h.reason NOT LIKE 'version-retire:%')) AND (?=1 OR NOT " +
                    "EXISTS(SELECT 1 FROM archive_leases l WHERE l.archive_id=a.id))"),
                    archiveId,
                    activeOperation,
                    allowVersionRetirement,
                    activeOperation
                ) > 0 &&
                    (activeOperation ||
                    !IsCompactionArchive(archiveId) &&
                    (HasCompleteArchiveManifest(archiveId) ||
                    !IsNodeArchive(archiveId)));
        }

        public void MarkUploadConfirmationUnavailable(PendingVolumeTransfer transfer)
        {
            Transaction(delegate
            {
                if (transfer.ConfirmationDamaged)
                    return 0;
                int before = database.ChangeStamp;
                database.Execute(
                    ("UPDATE placements SET status='unavailable' WHERE archive_id=? AND group_id=? AND destination_id=? AND status<>'unavailable' AND EXISTS(SELECT 1 FROM " +
                    "volume_transfers t WHERE t.name=? AND t.destination_id=?)"),
                    transfer.ArchiveId,
                    transfer.Placement.GroupId,
                    transfer.Placement.DestinationId,
                    transfer.Object.Name,
                    transfer.Placement.DestinationId
                );
                if (database.ChangeStamp != before)
                    RepositoryStatusChanged(CatalogChange.Archives, GetArchive(transfer.ArchiveId).TaskId);
                return 0;
            });
        }

        public void MarkUploadConfirmationPending(long archiveId, string name, string destination, bool damaged = false)
        {
            Transaction(delegate
            {
                string prefix = damaged ? "damaged-" : "pending-";
                var rows = database.Query("SELECT state,group_id FROM volume_transfers WHERE name=? AND destination_id=?", name, destination);
                if (rows.Count == 0)
                    return 0;
                string phase = (string)rows[0][0];
                if (!damaged && phase.StartsWith("damaged-", StringComparison.Ordinal))
                    return 0;
                if (phase.StartsWith("pending-", StringComparison.Ordinal) || phase.StartsWith("damaged-", StringComparison.Ordinal) || phase.StartsWith("checked-", StringComparison.Ordinal))
                    phase = phase.Substring(8);
                string state = prefix + phase;
                if (state == (string)rows[0][0])
                {
                    int before = database.ChangeStamp;
                    database.Execute(
                        "UPDATE placements SET status=? WHERE archive_id=? AND group_id=? AND destination_id=? AND status='unavailable'",
                        damaged ? "repair-required" : "reconcile-required",
                        archiveId,
                        rows[0][1],
                        destination
                    );
                    if (database.ChangeStamp != before)
                        RepositoryStatusChanged(CatalogChange.Archives, GetArchive(archiveId).TaskId);
                    return 0;
                }

                database.Execute("UPDATE volume_transfers SET state=? WHERE name=? AND destination_id=?", state, name, destination);
                database.Execute("UPDATE placements SET status=?,error='' WHERE archive_id=? AND group_id=? AND destination_id=?", damaged ? "repair-required" : "reconcile-required", archiveId, rows[0][1], destination);
                if (damaged)
                    database.Execute("UPDATE archives SET status='repair-required' WHERE id=? AND status IN ('complete','redundancy-shortfall')", archiveId);
                RepositoryStatusChanged(CatalogChange.Archives, GetArchive(archiveId).TaskId);
                return 0;
            });
        }

        private void RefreshConfirmedVolumeArchiveUnsafe(long id)
        {
            var archive = GetArchive(id);
            if (!new[]
            {
                "complete",
                "repair-required",
                "redundancy-shortfall"
            }.Contains(archive.Status))
                return;
            int[] counts;
            if (!VolumeStatistics(id).TryGetValue(id, out counts))
                return;
            bool damaged = Scalar("SELECT count(*) FROM object_copies c JOIN objects o ON o.name=c.name WHERE o.archive_id=? AND c.state IN ('missing','size-or-name-mismatch','content-damaged')", id) > 0 ||
                Scalar("SELECT count(*) FROM volume_transfers t JOIN objects o ON o.name=t.name WHERE o.archive_id=? AND (t.state LIKE 'checked-%' OR t.state LIKE 'damaged-%')", id) > 0;
            string status = damaged ? "repair-required" : counts[1] >= archive.TargetCopies ? "complete" : "redundancy-shortfall";
            if (status == "complete")
                database.Execute("DELETE FROM archive_unrepairable WHERE archive_id=?", id);
            database.Execute("UPDATE archives SET status=? WHERE id=? AND status<>?", status, id, status);
            if (status == "complete" && !IsCompactionArchive(id))
                ExpireOlderVersions(id);
        }
    }

    // Only exact upload intents and owned temporary names are probed. No directory scans.
    public sealed class PendingUploadConfirmation
    {
        private readonly BackupCatalog catalog;
        private readonly DestinationAdapterRegistry adapters;
        public PendingUploadConfirmation(BackupCatalog value, DestinationAdapterRegistry registry)
        {
            catalog = value;
            adapters = registry;
        }

        private bool ReleaseUnstarted(PendingVolumeTransfer transfer, IntegrityFinding finding, IManagedBackupDestination target, ISet<string> readableRoots, Action<TransferProgress> progress)
        {
            var probe = target as IReadOnlyDestinationProbe;
            if (probe == null)
                return false;
            if (!readableRoots.Contains(target.Id))
            {
                probe.CheckReadableRoot();
                readableRoots.Add(target.Id);
            }

            catalog.ResolveVolumeTransfer(transfer, false);
            finding.Status = "missing";
            finding.RequiresManualRepairConfirmation = true;
            finding.RepairAction = "replace";
            finding.UploadConfirmation = false;
            if (progress != null)
                progress(new TransferProgress { DestinationId = target.Id, ObjectName = transfer.Object.Name, Size = transfer.Object.Size, State = "upload-not-started" });
            return true;
        }

        public IList<IntegrityFinding> Check(
            ISet<long> archiveIds,
            CancellationToken cancellation,
            Action<TransferProgress> progress = null,
            bool activeOperation = false,
            bool recoverOwnedTemporary = false,
            bool observeOnly = false,
            bool preserveMissing = false,
            bool formalOnly = false
        )
        {
            var result = new List<IntegrityFinding>();
            var readableRoots = new HashSet<string>();
            var definitions = catalog.LoadTopology().Destinations.ToDictionary(d => d.Id);
            var candidates = catalog.PendingVolumeTransfers().Where(p => (archiveIds == null || archiveIds.Contains(p.ArchiveId)) && p.Phase != "checking" && (!activeOperation || p.AwaitingConfirmation))
                .ToList();
            var eligible = new HashSet<long>(candidates.Select(p => p.ArchiveId).Distinct().Where(id => catalog.CanConfirmUpload(id, activeOperation, preserveMissing)));
            foreach (var pending in candidates.Where(p => eligible.Contains(p.ArchiveId) && !catalog.OwnsUploadRetry(p.Placement.DestinationId, p.Object.Name) &&
                (!preserveMissing ||
                !p.UploadFailed &&
                definitions.ContainsKey(p.Placement.DestinationId) &&
                definitions[p.Placement.DestinationId].Enabled &&
                !definitions[p.Placement.DestinationId].Retired)))
            {
                // Older builds could mark a pre-PUT directory failure as pending.
                // A temporary phase with no journal token and no registered copy
                // predates OpenTemporary; it is missing data, not an uncertain PUT.
                bool unstarted = !activeOperation && pending.AwaitingConfirmation && pending.Phase == "temporary" && catalog.UploadStages(pending.Placement.DestinationId, pending.Object.Name).Count == 0 &&
                    !catalog.GetCopies(pending.ArchiveId)
                    .Any(copy => copy.DestinationId == pending.Placement.DestinationId &&
                    copy.Object.Name == pending.Object.Name);
                cancellation.ThrowIfCancellationRequested();
                IBackupDestination created = null;
                bool submitted = false;
                var finding = new IntegrityFinding
                {
                    ArchiveId = pending.ArchiveId,
                    Destination = pending.Placement.DestinationId,
                    Name = pending.Object.Name,
                    ExpectedSize = pending.Object.Size,
                    ActualSize = -1,
                    Status = "confirmation-pending",
                    UploadConfirmation = true
                };
                try
                {
                    DestinationDefinition definition;
                    if (!definitions.TryGetValue(pending.Placement.DestinationId, out definition))
                        throw new IOException("原仓库配置已不存在，请重新配置仓库。");
                    created = adapters.CreateTransfer(definition);
                    var target = created as IManagedBackupDestination;
                    if (target == null)
                        throw new IOException("仓库不支持核对上传结果。");
                    var cancel = target as ICancellableBackupDestination;
                    if (cancel != null)
                        cancel.OperationCancellation = cancellation;
                    var abandoned = formalOnly ? target as IAbandonedUploadDestination : null;
                    var info = abandoned == null ? target.Stat(pending.Object.Name) : abandoned.StatFormal(pending.Object.Name);
                    bool verified = false;
                    if (pending.UploadFailed && observeOnly)
                    {
                        finding.ActualSize = info.Exists ? info.Size : -1;
                        finding.Status = !info.Exists ? "missing" : info.Name == pending.Object.Name && info.Size == pending.Object.Size ? "present-size-matched" : "size-or-name-mismatch";
                        finding.RequiresManualRepairConfirmation = true;
                        finding.RepairAction = info.Exists && info.Name == pending.Object.Name && info.Size == pending.Object.Size ? "confirm-upload" : "replace";
                        result.Add(finding);
                        continue;
                    }

                    if (info.Exists)
                    {
                        if (pending.Phase == "repair-writing" && catalog.IntegrityCopyStates(pending.ArchiveId).ContainsKey(pending.Placement.DestinationId + "\0" + pending.Object.Name))
                        {
                            finding.Status = "content-damaged";
                            finding.RequiresManualRepairConfirmation = true;
                            catalog.MarkUploadConfirmationPending(pending.ArchiveId, pending.Object.Name, target.Id, true);
                            result.Add(finding);
                            continue;
                        }

                        finding.ActualSize = info.Size;
                        if (info.Name != pending.Object.Name || info.Size != pending.Object.Size)
                        {
                            if (!activeOperation && !preserveMissing)
                            {
                                finding.Status = "size-or-name-mismatch";
                                finding.RequiresManualRepairConfirmation = true;
                                catalog.MarkUploadConfirmationPending(pending.ArchiveId, pending.Object.Name, target.Id, true);
                            }

                            result.Add(finding);
                            continue;
                        }

                        verified = true;
                    }
                    else
                    {
                        if (formalOnly)
                        {
                            var rootProbe = target as IReadOnlyDestinationProbe;
                            if (rootProbe != null && !readableRoots.Contains(target.Id))
                            {
                                rootProbe.CheckReadableRoot();
                                readableRoots.Add(target.Id);
                            }

                            if (catalog.UploadStages(target.Id, pending.Object.Name).Count > 0)
                            {
                                finding.Status = "unavailable: 遗留临时文件尚未清理，保留清理记录后重试。";
                                catalog.MarkUploadConfirmationUnavailable(pending);
                            }
                            else
                            {
                                finding.Status = "missing";
                                finding.RequiresManualRepairConfirmation = true;
                                catalog.MarkUploadConfirmationPending(pending.ArchiveId, pending.Object.Name, target.Id, true);
                            }

                            result.Add(finding);
                            continue;
                        }

                        if (unstarted && ReleaseUnstarted(pending, finding, target, readableRoots, progress))
                        {
                            result.Add(finding);
                            continue;
                        }

                        bool readable = false;
                        if (!activeOperation)
                        {
                            var probe = target as IReadOnlyDestinationProbe;
                            if (probe != null)
                            {
                                if (!readableRoots.Contains(target.Id))
                                {
                                    probe.CheckReadableRoot();
                                    readableRoots.Add(target.Id);
                                }

                                readable = true;
                            }
                        }

                        bool temporaryFound = false, temporaryComplete = false;
                        bool discardIncomplete = recoverOwnedTemporary && !activeOperation && !preserveMissing && !catalog.HasCompleteArchiveManifest(pending.ArchiveId) &&
                            !catalog.IsNodeArchive(pending.ArchiveId);
                        var recoverable = target as IRecoverableUploadDestination;
                        if (recoverable != null)
                            foreach (string temporary in catalog.UploadStages(target.Id, pending.Object.Name))
                            {
                                cancellation.ThrowIfCancellationRequested();
                                if (!UploadStaging.IsOwnedTemporary(pending.Object.Name, temporary))
                                    throw new IOException("临时上传所有权记录无效，已保留。");
                                var temp = target.Stat(temporary);
                                if (!temp.Exists)
                                    continue;
                                temporaryFound = true;
                                if (temp.Size != pending.Object.Size)
                                {
                                    if (!activeOperation && !preserveMissing)
                                    {
                                        finding.Status = "size-or-name-mismatch";
                                        finding.ActualSize = temp.Size;
                                        finding.RequiresManualRepairConfirmation = true;
                                        catalog.MarkUploadConfirmationPending(pending.ArchiveId, pending.Object.Name, target.Id, true);
                                    }

                                    continue;
                                }

                                temporaryComplete = true;
                                if (!recoverOwnedTemporary || discardIncomplete)
                                    continue;
                                // The completed owned temporary has the expected name and length.
                                cancellation.ThrowIfCancellationRequested();
                                recoverable.ResumeTemporary(pending.Object.Name, temporary);
                                pending.Phase = pending.Phase.StartsWith("repair-", StringComparison.Ordinal) ? "repair-committing" : "committing";
                                catalog.VolumePhase(pending.Object.Name, target.Id, "pending-" + pending.Phase);
                                submitted = true;
                                target.CommitTemporary(pending.Object.Name);
                                info = target.Stat(pending.Object.Name);
                                verified = info.Exists && info.Name == pending.Object.Name && info.Size == pending.Object.Size;
                                break;
                            }

                        if (!verified && readable && !submitted && !preserveMissing && (!temporaryComplete || discardIncomplete))
                        {
                            finding.Status = temporaryFound ? "interrupted" : "missing";
                            finding.RequiresManualRepairConfirmation = true;
                            catalog.MarkUploadConfirmationPending(pending.ArchiveId, pending.Object.Name, target.Id, true);
                        }
                    }

                    cancellation.ThrowIfCancellationRequested();
                    if (verified)
                    {
                        if (observeOnly)
                        {
                            catalog.RecordMatchedUploadObservation(pending);
                            finding.Status = "present-size-matched";
                            finding.ActualSize = pending.Object.Size;
                            finding.RepairAction = "confirm-upload";
                            finding.RequiresManualRepairConfirmation = true;
                            result.Add(finding);
                            continue;
                        }

                        if (recoverOwnedTemporary && !formalOnly)
                        {
                            try
                            {
                                target.DeleteTemporary(pending.Object.Name);
                            }
                            catch (IOException diagnosticFailure)
                            {
                                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/PendingUploadConfirmation.cs:Check", diagnosticFailure);
                            }
                            catch (UnauthorizedAccessException diagnosticFailure)
                            {
                                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/PendingUploadConfirmation.cs:Check", diagnosticFailure);
                            }
                        }
                        else if (!formalOnly)
                            foreach (string temporary in catalog.UploadStages(target.Id, pending.Object.Name))
                            {
                                if (!UploadStaging.IsOwnedTemporary(pending.Object.Name, temporary))
                                    continue;
                                try
                                {
                                    if (!target.Stat(temporary).Exists)
                                        catalog.ForgetUploadStage(target.Id, temporary);
                                }
                                catch (IOException diagnosticFailure)
                                {
                                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/PendingUploadConfirmation.cs:Check", diagnosticFailure);
                                }
                                catch (UnauthorizedAccessException diagnosticFailure)
                                {
                                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/PendingUploadConfirmation.cs:Check", diagnosticFailure);
                                }
                            }

                        catalog.ResolveVolumeTransfer(pending, true);
                        finding.Status = "present-size-matched";
                        finding.ActualSize = pending.Object.Size;
                        if (progress != null)
                            progress(new TransferProgress {
                                DestinationId = target.Id,
                                ObjectName = pending.Object.Name,
                                Size = pending.Object.Size,
                                Written = pending.Object.Size,
                                State = "confirmation-succeeded"
                            });
                    }
                    else
                        catalog.MarkUploadConfirmationPending(pending.ArchiveId, pending.Object.Name, pending.Placement.DestinationId);
                }
                catch (OpenListTaskPendingException)
                {
                    catalog.MarkUploadConfirmationPending(pending.ArchiveId, pending.Object.Name, pending.Placement.DestinationId);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is NotSupportedException))
                        throw;
                    bool released = false;
                    if (unstarted && ObjectStorageLayout.IsMissingPath(error))
                        try
                        {
                            released = ReleaseUnstarted(pending, finding, created as IManagedBackupDestination, readableRoots, progress);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception probeError)
                        {
                            global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/PendingUploadConfirmation.cs:Check", probeError);
                            if (!(probeError is IOException) && !(probeError is UnauthorizedAccessException) && !(probeError is NotSupportedException))
                                throw;
                        }

                    if (!released)
                    {
                        if (!activeOperation && !preserveMissing && error is InvalidBackupObjectException)
                        {
                            finding.Status = "size-or-name-mismatch";
                            finding.RequiresManualRepairConfirmation = true;
                            catalog.MarkUploadConfirmationPending(pending.ArchiveId, pending.Object.Name, pending.Placement.DestinationId, true);
                        }
                        else if (!activeOperation && !submitted)
                        {
                            finding.Status = "unavailable: " + error.Message;
                            catalog.MarkUploadConfirmationUnavailable(pending);
                        }
                        else
                            catalog.MarkUploadConfirmationPending(pending.ArchiveId, pending.Object.Name, pending.Placement.DestinationId);
                    }
                }
                finally
                {
                    if (created != null)
                        adapters.Release(created);
                }

                result.Add(finding);
            }

            return result;
        }
    }
}
