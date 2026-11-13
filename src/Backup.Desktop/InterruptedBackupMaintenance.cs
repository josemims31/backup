using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        private BackupControl stoppedUploadCleanup;
        private IList<string> DiscardInactiveUploadTemporaries(ISet<string> taskIds, Action<string> report, CancellationToken cancellation, ISet<long> archiveIds = null)
        {
            var errors = new List<string>();
            var owners = IntegrityTaskIds(taskIds);
            var definitions = Catalog.LoadTopology().Destinations.ToDictionary(item => item.Id);
            var stages = Catalog.PendingUploadStages().Where(item => (archiveIds == null || archiveIds.Contains(item.ArchiveId)) && (owners == null || owners.Contains(Catalog.GetArchive(item.ArchiveId).TaskId)) &&
                !Catalog.OwnsUploadRetry(item.DestinationId, item.Name))
                .ToList();
            var adapters = Adapters();
            adapters.Cancellation = cancellation;
            // Keep the old native context until formal-file confirmation has run.
            // A stale DAV cache must not hide an already completed cloud object.
            if (stages.Count > 0)
                new PendingUploadConfirmation(Catalog, adapters).Check(new HashSet<long>(stages.Select(item => item.ArchiveId)), cancellation, ReportUploadConfirmation, false, false, false, false, true);
            using (adapters.BeginScope())
                foreach (var stage in stages)
                {
                    IBackupDestination created = null;
                    try
                    {
                        cancellation.ThrowIfCancellationRequested();
                        DestinationDefinition definition;
                        if (!definitions.TryGetValue(stage.DestinationId, out definition))
                            throw new IOException("原仓库配置已不存在，无法删除远程临时文件。");
                        var names = Catalog.UploadStages(stage.DestinationId, stage.Name);
                        if (names.Count == 0)
                            continue;
                        if (report != null)
                            report("正在删除停止后遗留的临时文件：" + stage.Name);
                        created = adapters.CreateTransfer(definition);
                        var target = created as IManagedBackupDestination;
                        if (target == null)
                            throw new NotSupportedException("仓库不支持清理临时文件。");
                        var cancel = target as ICancellableBackupDestination;
                        if (cancel != null)
                            cancel.OperationCancellation = cancellation;
                        bool deleted = false;
                        try
                        {
                            var abandoned = target as IAbandonedUploadDestination;
                            if (abandoned != null)
                                abandoned.DiscardTemporary(stage.Name);
                            else
                                target.DeleteTemporary(stage.Name);
                            deleted = true;
                        }
                        finally
                        {
                            foreach (string name in names)
                                Catalog.ForgetUploadStage(stage.DestinationId, name);
                            foreach (var pending in Catalog.PendingVolumeTransfers().Where(item => item.Placement.DestinationId == stage.DestinationId && item.Object.Name == stage.Name))
                                Catalog.ResolveVolumeTransfer(pending, false);
                            Catalog.RecordTemporaryProblem(stage.ArchiveId, false);
                            ReportUploadConfirmation(new TransferProgress { DestinationId = stage.DestinationId, ObjectName = stage.Name, State = "temporary-discarded" });
                            if (deleted)
                            {
                                string folder = ObjectStoragePath.Absolute(definition, Catalog.ObjectStorageDirectory(stage.DestinationId, stage.Name) ?? "");
                                repositoryRuntime.EntriesCommitted(names.Select(name => new RepositoryObjectResult {
                                    DestinationId = stage.DestinationId,
                                    Folder = folder,
                                    Name = name,
                                    Exists = false,
                                    IgnoreCase = RepositoryModules.IsFileSystem(definition.Protocol)
                                })
                                    .ToList());
                                PhysicalDeletionCommitted();
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/InterruptedBackupMaintenance.cs:DiscardInactiveUploadTemporaries", error);
                        if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is NotSupportedException))
                            throw;
                        errors.Add(stage.Name + " 临时文件清理失败：" + error.Message);
                    }
                    finally
                    {
                        if (created != null)
                            adapters.Release(created);
                    }
                }

            FlushRepositoryResults();
            return errors;
        }

        private void CleanupStoppedUploadTemporaries(Action<string> errorReport = null)
        {
            var control = ActiveBackup;
            if (control == null || !control.IsStopping && !control.IsFinishing || ReferenceEquals(stoppedUploadCleanup, control))
                return;
            stoppedUploadCleanup = control;
            if (confirmationMonitor != null)
            {
                confirmationMonitor.Dispose();
                confirmationMonitor = null;
            }

            var ids = new HashSet<long>(control.ArchiveIds);
            try
            {
                if (!control.ExitToken.IsCancellationRequested)
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(control.CleanupToken))
                    {
                        deadline.CancelAfter(10000);
                        var errors = new List<string>();
                        try
                        {
                            errors.AddRange(DiscardInactiveUploadTemporaries(null, errorReport, deadline.Token, ids));
                        }
                        catch (OperationCanceledException)
                        {
                            errors.Add("临时文件可能残留，检查完整性时将标记为未登记，修复完整性时清理。");
                        }

                        if (errors.Count > 0 && errorReport != null)
                            errorReport(string.Join("；", errors));
                    }
            }
            finally
            {
                foreach (var stage in Catalog.PendingUploadStages().Where(item => ids.Contains(item.ArchiveId) && !Catalog.OwnsUploadRetry(item.DestinationId, item.Name)))
                {
                    foreach (string name in Catalog.UploadStages(stage.DestinationId, stage.Name))
                        Catalog.ForgetUploadStage(stage.DestinationId, name);
                    Catalog.RecordTemporaryProblem(stage.ArchiveId, false);
                    ReportUploadConfirmation(new TransferProgress { DestinationId = stage.DestinationId, ObjectName = stage.Name, State = "temporary-discarded" });
                }

                foreach (var pending in Catalog.PendingVolumeTransfers().Where(item => ids.Contains(item.ArchiveId) && !Catalog.OwnsUploadRetry(item.Placement.DestinationId, item.Object.Name)))
                    Catalog.ResolveVolumeTransfer(pending, false);
            }
        }

        private CleanupResult DeleteUnregisteredTemporaryFiles(AllBackupCheckResult review, Action<string> report)
        {
            var result = new CleanupResult();
            var committed = new List<RepositoryObjectResult>();
            var definitions = Catalog.LoadTopology().Destinations.ToDictionary(item => item.Id);
            try
            {
                foreach (var finding in review.Findings.Where(item => item.RepairAction == "delete-orphan" && BackupObjectNaming.IsTemporary(item.Name)).ToList())
                {
                    OperationCancellation.ThrowIfCancellationRequested();
                    string owner = BackupObjectNaming.ObjectName(finding.Name);
                    if (Catalog.UploadStages(finding.Destination, owner).Contains(finding.Name) || Catalog.OwnsUploadRetry(finding.Destination, owner))
                        continue;
                    IBackupDestination adapter = null;
                    try
                    {
                        var definition = definitions[finding.Destination];
                        adapter = OpenWarehouseLocation(definition, finding.Folder, OperationCancellation);
                        var removal = adapter as IManagedBackupDestination;
                        if (removal == null)
                            throw new NotSupportedException("仓库不支持删除临时文件。");
                        removal.DeleteObject(finding.Name);
                        committed.Add(new RepositoryObjectResult {
                            DestinationId = finding.Destination,
                            Folder = finding.Folder,
                            Name = finding.Name,
                            Exists = false,
                            IgnoreCase = RepositoryModules.IsFileSystem(definition.Protocol)
                        });
                        PhysicalDeletionCommitted();
                        result.DeletedObjects++;
                        result.FreedBytes += Math.Max(0, finding.ActualSize);
                        review.Findings.Remove(finding);
                        if (report != null)
                            report("已删除未登记临时文件：" + finding.Name);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/InterruptedBackupMaintenance.cs:DeleteUnregisteredTemporaryFiles", error);
                        if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is NotSupportedException))
                            throw;
                        result.Errors.Add(finding.Name + "：未登记临时文件删除失败：" + error.Message);
                    }
                    finally
                    {
                        if (adapter != null)
                            DestinationAdapterRegistry.DisposeAdapter(adapter);
                    }
                }
            }
            finally
            {
                repositoryRuntime.EntriesCommitted(committed);
                FlushRepositoryResults();
            }

            review.ManagedUnregistered = review.Findings.Count(item => item.RepairAction == "delete-orphan" || item.RepairAction == "register-copy");
            return result;
        }

        private void CleanupStoppedOperation(Action<string> errorReport = null)
        {
            CleanupStoppedUploadTemporaries(errorReport);
            var control = ActiveBackup;
            if (control == null || !control.DeleteIncompleteOnStop)
                return;
            var ids = new HashSet<long>(control.ArchiveIds);
            var candidates = Catalog.ListArchives(true).Where(item => ids.Contains(item.ArchiveId) && new[] { "failed", "damaged", "complete", "repair-required", "redundancy-shortfall" }.Contains(item.Status))
                .ToList();
            if (candidates.Count == 0)
                return;
            var maintenance = new ArchiveMaintenance(Catalog, Adapters());
            var errors = new List<string>();
            foreach (var archive in candidates)
            {
                if (!control.DeleteIncompleteOnStop)
                    break;
                if (Catalog.IsCompactionArchive(archive.ArchiveId) || !control.TryBeginCleanup(archive.ArchiveId))
                    continue;
                errors.AddRange(maintenance.DeleteInterruptedArchive(archive.ArchiveId, control.CleanupToken));
            }

            if (errors.Count > 0)
            {
                string message = "未完成归档清理待重试：" + string.Join("；", errors);
                Catalog.SetSetting("last_upload_alert_v1", message);
                if (errorReport != null)
                    errorReport(message);
            }
        }

        private IList<IntegrityFinding> CheckInterruptedBackups(DestinationAdapterRegistry adapters, ISet<string> taskIds, Action<string> report, bool applyCorrections = false)
        {
            var result = new List<IntegrityFinding>();
            var definitions = Catalog.LoadTopology().Destinations.ToDictionary(item => item.Id);
            using (adapters.BeginScope())
                foreach (var archive in Catalog.InterruptedArchives(taskIds))
                {
                    OperationCancellation.ThrowIfCancellationRequested();
                    if (ActiveBackup != null)
                        ActiveBackup.Checkpoint();
                    if (report != null)
                        report("正在核对未完成归档：" + archive.ArchiveName);
                    var findings = new List<IntegrityFinding>();
                    var copies = Catalog.InterruptedCheckCopies(archive.ArchiveId);
                    foreach (var group in copies.GroupBy(copy => copy.DestinationId))
                    {
                        IBackupDestination adapter = null;
                        try
                        {
                            DestinationDefinition definition;
                            if (!definitions.TryGetValue(group.Key, out definition))
                                throw new IOException("原仓库配置已不存在，保留待核对记录。");
                            adapter = adapters.Create(definition);
                            var cancel = adapter as ICancellableBackupDestination;
                            if (cancel != null)
                                cancel.OperationCancellation = ActiveBackup == null ? OperationCancellation : ActiveBackup.Token;
                            findings.AddRange(DestinationIntegrityChecker.Check(adapter, group.Select(copy => copy.Object), ActiveBackup == null ? OperationCancellation : ActiveBackup.Token));
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception error)
                        {
                            global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/InterruptedBackupMaintenance.cs:CheckInterruptedBackups", error);
                            if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is NotSupportedException))
                                throw;
                            foreach (var copy in group)
                                findings.Add(new IntegrityFinding { Destination = group.Key, Name = copy.Object.Name, ExpectedSize = copy.Object.Size, ActualSize = -1, Status = "unavailable: " + error.Message });
                        }
                        finally
                        {
                            if (adapter != null)
                                adapters.Release(adapter);
                        }
                    }

                    if (applyCorrections)
                        foreach (var finding in findings)
                            finding.RepairApplied = true;
                    Catalog.RecordInterruptedIntegrity(archive.ArchiveId, findings);
                    foreach (var item in findings)
                        item.ArchiveId = archive.ArchiveId;
                    result.AddRange(findings);
                    if (Catalog.GetArchive(archive.ArchiveId).Status == "damaged")
                        result.Add(new IntegrityFinding {
                            ArchiveId = archive.ArchiveId,
                            ArchiveLevel = true,
                            Destination = "",
                            Name = archive.ArchiveName,
                            Status = "damaged",
                            ExpectedSize = copies.Sum(copy => copy.Object.Size),
                            ActualSize = -1,
                            RequiresManualRepairConfirmation = true
                        });
                    else if (Catalog.GetArchive(archive.ArchiveId).Status == "failed" && !findings.Any(item => item.Status.StartsWith("unavailable", StringComparison.Ordinal)))
                        result.Add(new IntegrityFinding {
                            ArchiveId = archive.ArchiveId,
                            ArchiveLevel = true,
                            Destination = "",
                            Name = archive.ArchiveName,
                            Status = Catalog.HasOutstandingTransfer(archive.ArchiveId) ? "reconcile-required" : "interrupted",
                            ActualSize = -1,
                            RequiresManualRepairConfirmation = true
                        });
                }

            return result;
        }

        private IList<string> RepairInterruptedBackups(ISet<string> taskIds, Action<string> report, bool inspectFailed)
        {
            var errors = new List<string>();
            var candidates = Catalog.InterruptedArchives(taskIds).Where(item => inspectFailed || item.Status == "damaged").ToList();
            if (candidates.Count == 0)
                return errors;
            // Reconcile ambiguous commits only on an explicit repair, never while checking.
            var ids = new HashSet<long>(candidates.Select(item => item.ArchiveId));
            var maintenance = new ArchiveMaintenance(Catalog, Adapters())
            {
                Control = ActiveBackup
            };
            if (inspectFailed)
            {
                errors.AddRange(maintenance.ReconcileVolumeTransfers(null, ids));
                CheckInterruptedBackups(Adapters(), taskIds, report, true);
            }

            foreach (string task in candidates.Select(archive => archive.TaskId).Distinct())
                Catalog.RefreshLocalBackupNode(task);
            foreach (var archive in Catalog.InterruptedArchives(taskIds).Where(item => ids.Contains(item.ArchiveId)))
            {
                // Referenced data belongs to the repair queue, even when only a
                // local unfinished version owns it. Never send it to stop cleanup.
                if (Catalog.IsNodeArchive(archive.ArchiveId))
                    continue;
                if (archive.Status != "damaged" &&
                    (Catalog.HasOutstandingTransfer(archive.ArchiveId) || Catalog.PendingVolumeTransfers().Any(item => item.ArchiveId == archive.ArchiveId) ||
                    Catalog.PendingUploadStages()
                    .Any(item => item.ArchiveId == archive.ArchiveId)))
                {
                    errors.Add(archive.ArchiveName + "：仓库暂不可访问或提交结果待核对，已保留。");
                    continue;
                }

                if (report != null)
                    report("正在清理无版本引用的未完成归档：" + archive.ArchiveName);
                errors.AddRange(maintenance.DeleteInterruptedArchive(archive.ArchiveId));
            }

            return errors;
        }

        private IList<IntegrityFinding> CheckOwnedTemporaryFiles(DestinationAdapterRegistry adapters, ISet<string> taskIds, ISet<long> excluded = null)
        {
            var result = new List<IntegrityFinding>();
            var definitions = Catalog.LoadTopology().Destinations.ToDictionary(item => item.Id);
            var confirming = new HashSet<string>(Catalog.PendingVolumeTransfers().Where(item => item.AwaitingConfirmation || item.ConfirmationDamaged).Select(item => item.Placement.DestinationId + "\0" +
                item.Object.Name));
            var pending = Catalog.PendingUploadStages().Where(item => (excluded == null || !excluded.Contains(item.ArchiveId)) && !confirming.Contains(item.DestinationId + "\0" + item.Name) &&
                Catalog.CanInspectUploadTemporaries(item.ArchiveId) &&
                (taskIds == null ||
                taskIds.Contains(Catalog.GetArchive(item.ArchiveId)
                .TaskId)))
                .GroupBy(item => item.ArchiveId);
            using (adapters.BeginScope())
                foreach (var archive in pending)
                {
                    bool found = false, unavailable = false;
                    foreach (var item in archive)
                    {
                        IBackupDestination adapter = null;
                        try
                        {
                            OperationCancellation.ThrowIfCancellationRequested();
                            if (ActiveBackup != null)
                                ActiveBackup.Checkpoint();
                            DestinationDefinition definition;
                            if (!definitions.TryGetValue(item.DestinationId, out definition))
                                throw new IOException("原仓库配置已不存在。");
                            adapter = adapters.Create(definition);
                            var cancel = adapter as ICancellableBackupDestination;
                            if (cancel != null)
                                cancel.OperationCancellation = ActiveBackup == null ? OperationCancellation : ActiveBackup.Token;
                            foreach (string name in Catalog.UploadStages(item.DestinationId, item.Name))
                            {
                                var info = adapter.Stat(name);
                                if (!info.Exists)
                                    continue;
                                found = true;
                                result.Add(new IntegrityFinding {
                                    ArchiveId = item.ArchiveId,
                                    ArchiveLevel = true,
                                    Destination = item.DestinationId,
                                    Name = name,
                                    Status = "interrupted",
                                    ActualSize = info.Size,
                                    RequiresManualRepairConfirmation = true
                                });
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception error)
                        {
                            global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/InterruptedBackupMaintenance.cs:CheckOwnedTemporaryFiles", error);
                            if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is NotSupportedException))
                                throw;
                            unavailable = true;
                            result.Add(new IntegrityFinding { ArchiveId = item.ArchiveId, ArchiveLevel = true, Destination = item.DestinationId, Name = item.Name, Status = "unavailable: " + error.Message });
                        }
                        finally
                        {
                            if (adapter != null)
                                adapters.Release(adapter);
                        }
                    }

                    if (!unavailable && Catalog.GetArchive(archive.Key).Status != "failed" && Catalog.GetArchive(archive.Key).Status != "damaged")
                        Catalog.RecordTemporaryProblem(archive.Key, found);
                }

            return result;
        }

        private IList<string> RepairOwnedTemporaryFiles(ISet<string> taskIds, bool inspectUnknown)
        {
            var known = Catalog.TemporaryProblemArchives(taskIds);
            var ids = new HashSet<long>(Catalog.PendingUploadStages().Where(item => Catalog.CanInspectUploadTemporaries(item.ArchiveId) && (inspectUnknown || known.Contains(item.ArchiveId)) &&
                (taskIds == null ||
                taskIds.Contains(Catalog.GetArchive(item.ArchiveId)
                .TaskId)))
                .Select(item => item.ArchiveId));
            ids.UnionWith(known.Where(Catalog.CanInspectUploadTemporaries));
            ids.UnionWith(Catalog.PendingVolumeTransfers().Where(item => item.ConfirmationDamaged && (taskIds == null || taskIds.Contains(Catalog.GetArchive(item.ArchiveId).TaskId))).Select(item => item.ArchiveId));
            if (ids.Count == 0)
                return new List<string>();
            var errors = new ArchiveMaintenance(Catalog, Adapters())
            {
                Control = ActiveBackup
            }.ReconcileVolumeTransfers(null, ids);
            var remaining = new HashSet<long>(Catalog.PendingUploadStages().Select(item => item.ArchiveId));
            foreach (long id in ids)
                if (!remaining.Contains(id))
                    Catalog.RecordTemporaryProblem(id, false);
            return errors;
        }
    }
}
