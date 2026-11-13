using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        private UploadConfirmationMonitor confirmationMonitor;
        public event Action<TransferProgress> UploadConfirmationProgress;
        private void ReportUploadConfirmation(TransferProgress progress)
        {
            if (progress.State != "confirmation-succeeded" && progress.State != "upload-not-started" && progress.State != "temporary-discarded")
                return;
            if (progress.State == "confirmation-succeeded" || progress.State == "temporary-discarded")
                ReportTransferNotice(progress);
            var group = Catalog.LoadTopology().Groups.FirstOrDefault(item => item.Id == "single:" + progress.DestinationId);
            string label = group == null ? progress.DestinationId : group.Name;
            foreach (string key in new[]
            {
                "last_upload_alert_v1",
                "last_upload_warnings_v1"
            }

            )
            {
                string old = Catalog.GetSetting(key);
                if (string.IsNullOrEmpty(old))
                    continue;
                string next = UploadNoticeText.Remove(old, progress.DestinationId, label, progress.ObjectName);
                if (next != old)
                    Catalog.SetSetting(key, next);
            }

            var observers = UploadConfirmationProgress;
            if (observers != null)
                foreach (Action<TransferProgress> observer in observers.GetInvocationList())
                    try
                    {
                        observer(progress);
                    }
                    catch (global::System.Exception diagnosticFailure)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/UploadConfirmationMonitor.cs:ReportUploadConfirmation", diagnosticFailure);
                    }
        }

        private IList<IntegrityFinding> CheckPendingUploads(ISet<string> tasks = null, bool recoverOwnedTemporary = false, ISet<long> excluded = null, bool observeOnly = false, bool formalOnly = false)
        {
            ISet<long> ids = tasks == null ? null : new HashSet<long>(tasks.SelectMany(id => Catalog.ArchiveIdsForTask(id)));
            if (excluded != null)
            {
                if (ids == null)
                    ids = new HashSet<long>(Catalog.ListArchives(true).Select(item => item.ArchiveId));
                ids.ExceptWith(excluded);
            }

            Func<CancellationToken, IList<IntegrityFinding>> check = token => new PendingUploadConfirmation(Catalog, Adapters()).Check(ids, token, ReportUploadConfirmation, false, recoverOwnedTemporary, observeOnly, false, formalOnly);
            return ActiveBackup == null ? check(OperationCancellation) : ActiveBackup.Transfer(check);
        }

        private void QueuePendingUploadDamage(WorkspaceDefinition workspace, IEnumerable<IntegrityFinding> findings)
        {
            var ids = new HashSet<long>(findings.Where(item => item.RequiresManualRepairConfirmation).Select(item => item.ArchiveId));
            if (ids.Count == 0)
                return;
            var queue = PendingRepairs();
            bool changed = false;
            foreach (long id in ids)
            {
                var archive = Catalog.GetArchive(id);
                if (archive.TaskId != "__catalog__" && !workspace.Tasks.Any(task => !task.Deleted && !task.RecoveryPendingConfirmation && task.Id == archive.TaskId) ||
                    queue.Any(item => item.ArchiveId == id))
                    continue;
                queue.Add(new ConfirmedRepair { ArchiveId = id, TaskId = archive.TaskId });
                changed = true;
            }

            if (changed)
                SaveRepairQueue(queue);
        }

        private sealed class UploadConfirmationMonitor : IDisposable
        {
            private readonly CancellationTokenSource ending;
            private readonly Task worker;
            private readonly DesktopBackend backend;
            private readonly BackupControl control;
            private readonly object previousGate = new object();
#if UI_TEST
            internal static int IntervalMilliseconds = 20000;
#else
            private const int IntervalMilliseconds = 20000;
#endif
            internal UploadConfirmationMonitor(DesktopBackend backend, BackupControl control)
            {
                this.backend = backend;
                this.control = control;
                ending = CancellationTokenSource.CreateLinkedTokenSource(control.Token);
                worker = Task.Run(async delegate
                {
                    bool initial = true;
                    try
                    {
                        while (true)
                        {
                            try
                            {
                                await Task.Delay(initial ? Math.Min(1000, IntervalMilliseconds) : IntervalMilliseconds, ending.Token).ConfigureAwait(false);
                                if (control.IsPaused)
                                    continue;
                                using (var io = control.TryBeginIo())
                                {
                                    if (io == null)
                                        continue;
                                    using (var cycle = CancellationTokenSource.CreateLinkedTokenSource(ending.Token, io.Token))
                                    {
                                        var ids = new HashSet<long>(control.ArchiveIds);
                                        if (ids.Count == 0)
                                            continue;
                                        initial = false;
                                        var tasks = new HashSet<string>(ids.Select(id => backend.Catalog.GetArchive(id).TaskId));
                                        new PendingUploadConfirmation(backend.Catalog, backend.Adapters()).Check(ids, cycle.Token, backend.ReportUploadConfirmation, true, true, false, true);
                                        ReconcilePrevious(tasks, ids, cycle.Token);
                                    }
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                if (ending.IsCancellationRequested || control.IsStopping)
                                    break;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    { // Expected cancellation, shutdown, or a deliberately rejected identity probe.
                    }
                    catch (Exception error)
                    {
                        RuntimeLog.Append(backend.BaseDirectory, "upload-confirmation.log", error.ToString());
                    }
                });
            }

            private void ReconcilePrevious(ISet<string> tasks, ISet<long> current, CancellationToken cancellation)
            {
                if (!Monitor.TryEnter(previousGate))
                    return;
                try
                {
                    var enabled = new HashSet<string>(backend.Catalog.LoadTopology().Destinations.Where(d => d.Enabled && !d.Retired).Select(d => d.Id));
                    var previous = new HashSet<long>(backend.Catalog.PendingVolumeTransfers().Where(p => enabled.Contains(p.Placement.DestinationId) && !current.Contains(p.ArchiveId) &&
                        tasks.Contains(backend.Catalog.GetArchive(p.ArchiveId)
                        .TaskId) &&
                        backend.Catalog.CanConfirmUpload(p.ArchiveId, false, true))
                        .Select(p => p.ArchiveId));
                    if (previous.Count > 0)
                        new PendingUploadConfirmation(backend.Catalog, backend.Adapters()).Check(previous, cancellation, backend.ReportUploadConfirmation, false, true, false, true);
                }
                finally
                {
                    Monitor.Exit(previousGate);
                }
            }

            internal void CompletePrevious(string task)
            {
                if (control.IsStopping || ending.IsCancellationRequested)
                    return;
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ending.Token, control.Token))
                {
                    deadline.CancelAfter(10000);
                    try
                    {
                        ReconcilePrevious(new HashSet<string> { task }, new HashSet<long>(control.ArchiveIds), deadline.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        if (control.Token.IsCancellationRequested)
                            throw;
                    }
                }
            }

            public void Dispose()
            {
                ending.Cancel();
                try
                {
                    worker.GetAwaiter().GetResult();
                }
                finally
                {
                    ending.Dispose();
                }
            }
        }
    }
}
