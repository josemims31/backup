using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Backup.Core
{
    internal sealed class StripedVolume
    {
        private int references;
        public VolumeBuffer Buffer;
        public ObjectRecord Object;
        public int Successes, AwaitingConfirmation;
        public Exception Failure;
        public readonly ConcurrentDictionary<string, string> Failed = new ConcurrentDictionary<string, string>();
        public void Add()
        {
            Interlocked.Increment(ref references);
        }

        public bool Release()
        {
            if (Interlocked.Decrement(ref references) != 0)
                return false;
            Buffer.Dispose();
            return true;
        }
    }

    internal sealed class VolumeJob
    {
        public StripedVolume Volume;
        public ArchivePlacement Place;
        public int Pool;
        public UploadRetryState Retry = new UploadRetryState();
    }

    public sealed class StripedArchiveSession : IOwnedArchiveObjectSink, IDisposable
    {
        private readonly BackupCatalog catalog;
        private readonly ArchivePlan archive;
        private readonly DestinationAdapterRegistry registry;
        private readonly BackupControl control, parent;
        private readonly Dictionary<string, DestinationDefinition> destinations;
        private readonly TransferScheduler scheduler;
        private readonly object gate = new object();
        private readonly List<UploadEvent> events = new List<UploadEvent>();
        private readonly List<ObjectRecord> objects = new List<ObjectRecord>();
        private bool finished, produced;
        private int pending, completedVolumes, completedCopies;
        private TaskCompletionSource<bool> drained;
        private int uploadFailed;
        internal bool UploadFailed
        {
            get
            {
                return Interlocked.CompareExchange(ref uploadFailed, 0, 0) != 0;
            }
        }

        internal BackupControl ArchiveControl
        {
            get
            {
                return control;
            }
        }

        public Func<long> SourceBytes;
        public Action<TransferProgress> Progress;
        public Func<string, string, int, long, bool> InjectFailure;
        public int CompletedVolumes
        {
            get
            {
                return Interlocked.CompareExchange(ref completedVolumes, 0, 0);
            }
        }

        public int CompletedCopies
        {
            get
            {
                return Interlocked.CompareExchange(ref completedCopies, 0, 0);
            }
        }

        public IList<UploadEvent> Events
        {
            get
            {
                lock (gate)
                    return events.ToList();
            }
        }

        public IList<ObjectRecord> Objects
        {
            get
            {
                lock (gate)
                    return objects.ToList();
            }
        }

        public StripedArchiveSession(BackupCatalog value, ArchivePlan plan, DestinationAdapterRegistry adapters, BackupControl operation, TransferScheduler uploadScheduler = null)
        {
            catalog = value;
            archive = plan;
            registry = adapters;
            parent = operation;
            parent.RegisterArchive(plan.ArchiveId);
            control = operation.CreateArchiveControl();
            destinations = catalog.LoadTopology().Destinations.ToDictionary(destination => destination.Id);
            scheduler = uploadScheduler ?? adapters.Transfers;
        }

        public void Upload(string name, byte[] buffer, int count)
        {
            throw new NotSupportedException("Striped uploads require an owned memory buffer.");
        }

        public void UploadOwned(string name, VolumeBuffer buffer, int count)
        {
            var volume = new StripedVolume
            {
                Buffer = buffer
            };
            volume.Add();
            try
            {
                control.SourceCheckpoint();
                var item = RetryUploader.CreateRecord(name, buffer.Bytes, count);
                control.SourceCheckpoint();
                volume.Object = item;
                catalog.RegisterObject(archive, item);
                lock (gate)
                    objects.Add(item);
                List<ArchivePlacement> routes;
                lock (gate)
                {
                    routes = catalog.ReserveVolumeRoutes(archive, item, new HashSet<string>());
                }

                if (routes.Count == 0)
                    throw new IOException("没有仓库能接收当前分卷。");
                foreach (var route in routes)
                    Enqueue(new VolumeJob { Volume = volume, Place = route, Pool = catalog.VolumePool(item.Name, route.DestinationId) });
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/StripedArchiveSession.cs:UploadOwned", error);
                if (!control.IsStopping && !(error is OperationCanceledException))
                    control.Fail(error);
                throw;
            }
            finally
            {
                Release(volume, "", "");
            }
        }

        private void Enqueue(VolumeJob job, int delay = 0)
        {
            lock (gate)
            {
                job.Volume.Add();
                pending++;
            }

            Action run = () =>
            {
                try
                {
                    Work(job);
                }
                catch (Exception error)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/StripedArchiveSession.cs:Enqueue", error);
                    control.Fail(error);
                }
            };
            // Metadata checks must not queue behind long-running uploads. Their
            // lifetime is bounded by the retained volumes and this session's drain.
            // A subsequent retransmission returns to the normal upload scheduler.
            Action submit = () => Task.Factory.StartNew(run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            if (delay == 0)
            {
                try
                {
                    submit();
                }
                catch (global::System.Exception diagnosticFailure)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/StripedArchiveSession.cs:Enqueue", diagnosticFailure);
                    FinishJob(job);
                    throw;
                }

                return;
            }

            // Retain the bounded payload buffer, but release the warehouse lane
            // and global transfer slot while waiting to retry this exact route.
            Task.Run(async delegate
            {
                bool submitted = false;
                try
                {
                    await Task.Delay(delay, control.Token).ConfigureAwait(false);
                    control.Checkpoint();
                    submit();
                    submitted = true;
                }
                catch (OperationCanceledException)
                { // Expected cancellation, shutdown, or a deliberately rejected identity probe.
                }
                catch (Exception error)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/StripedArchiveSession.cs:Enqueue", error);
                    control.Fail(error);
                }
                finally
                {
                    if (!submitted)
                        try
                        {
                            Interlocked.Exchange(ref job.Volume.AwaitingConfirmation, 1);
                            if (job.Retry.HasSent || catalog.UploadStages(job.Place.DestinationId, job.Volume.Object.Name).Count > 0)
                                catalog.MarkUploadConfirmationPending(archive.ArchiveId, job.Volume.Object.Name, job.Place.DestinationId);
                            else
                                catalog.FailVolumeCopy(archive, job.Place, job.Volume.Object, false);
                            job.Retry.Dispose();
                        }
                        catch (Exception error)
                        {
                            global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/StripedArchiveSession.cs:Enqueue", error);
                            control.Fail(error);
                        }
                        finally
                        {
                            FinishJob(job);
                        }
                }
            });
        }

        private void FinishJob(VolumeJob job)
        {
            try
            {
                Release(job.Volume, job.Place.DestinationId, job.Place.GroupId);
            }
            finally
            {
                lock (gate)
                {
                    pending--;
                    if (pending == 0 && drained != null)
                        drained.TrySetResult(true);
                    Monitor.PulseAll(gate);
                }
            }
        }

        private void Work(VolumeJob job)
        {
            string id = job.Place.DestinationId;
            IBackupDestination adapter = null;
            try
            {
                if (control.IsPaused)
                {
                    Enqueue(job, 500);
                    return;
                }

                if (parent.IsFinishing && !produced && !catalog.HasCompleteArchiveManifest(archive.ArchiveId))
                    control.Stop();
                control.Checkpoint();
                adapter = registry.CreateTransfer(destinations[id]);
                var cancellable = adapter as ICancellableBackupDestination;
                if (cancellable != null)
                    cancellable.OperationCancellation = control.Token;
                if (!(adapter is IManagedBackupDestination))
                    throw new NotSupportedException("仓库不支持分卷上传。");
                var uploader = new RetryUploader(new[] { adapter }, RetryUploader.DefaultAttempts)
                {
                    Transfers = scheduler,
                    Control = control,
                    SourceBytes = SourceBytes,
                    InjectFailure = InjectFailure,
                    RetryState = job.Retry,
                    DeferVerificationRetry = true,
                    PhaseChanged = phase => catalog.VolumePhase(job.Volume.Object.Name, id, phase)
                };
                uploader.Progress = progress =>
                {
                    progress.GroupId = job.Place.GroupId;
                    if (Progress != null)
                        Progress(progress);
                };
                try
                {
                    uploader.Upload(job.Volume.Object, job.Volume.Buffer.Bytes, (int)job.Volume.Object.Size);
                    catalog.CommitVolumeCopy(archive, job.Place, job.Volume.Object);
                    Interlocked.Increment(ref job.Volume.Successes);
                    Interlocked.Increment(ref completedCopies);
                    job.Retry.Dispose();
                    if (Progress != null)
                        Progress(new TransferProgress {
                            ObjectName = job.Volume.Object.Name,
                            DestinationId = id,
                            GroupId = job.Place.GroupId,
                            State = "copy-complete",
                            Written = job.Volume.Object.Size,
                            Size = job.Volume.Object.Size
                        });
                }
                finally
                {
                    lock (gate)
                        events.AddRange(uploader.Events);
                }
            }
            catch (DeferredUploadRetryException waiting)
            {
                Enqueue(job, waiting.Delay);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Exchange(ref job.Volume.AwaitingConfirmation, 1);
                if (job.Retry.HasSent || catalog.UploadStages(id, job.Volume.Object.Name).Count > 0)
                    catalog.MarkUploadConfirmationPending(archive.ArchiveId, job.Volume.Object.Name, id);
                else
                    catalog.FailVolumeCopy(archive, job.Place, job.Volume.Object, false);
                job.Retry.Dispose();
            }
            catch (TransferReconciliationRequiredException error)
            {
                Interlocked.Exchange(ref job.Volume.AwaitingConfirmation, 1);
                catalog.MarkUploadConfirmationPending(archive.ArchiveId, job.Volume.Object.Name, id);
                job.Retry.Dispose();
                if (Progress != null)
                    Progress(new TransferProgress {
                        ObjectName = job.Volume.Object.Name,
                        DestinationId = id,
                        GroupId = job.Place.GroupId,
                        State = "confirmation-pending",
                        Attempt = RetryUploader.DefaultAttempts,
                        Error = error.Message
                    });
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/StripedArchiveSession.cs:Work", error);
                if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is NotSupportedException))
                    throw;
                Interlocked.CompareExchange(ref job.Volume.Failure, error, null);
                job.Volume.Failed[id] = error.Message;
                catalog.MarkVolumeUploadFailed(archive, job.Place, job.Volume.Object, error.Message);
                job.Retry.Dispose();
            }
            finally
            {
                try
                {
                    if (adapter != null)
                        registry.Release(adapter);
                }
                finally
                {
                    FinishJob(job);
                }
            }
        }

        private void Release(StripedVolume volume, string destination, string group)
        {
            if (volume.Release() && volume.Object != null)
            {
                if (volume.Successes > 0)
                {
                    Interlocked.Increment(ref completedVolumes);
                    if (Progress != null)
                    {
                        Progress(new TransferProgress {
                            ObjectName = volume.Object.Name,
                            DestinationId = destination,
                            GroupId = group,
                            Written = volume.Object.Size,
                            Size = volume.Object.Size,
                            State = "volume-complete"
                        });
                        foreach (var missing in volume.Failed)
                            Progress(new TransferProgress { ObjectName = volume.Object.Name, DestinationId = missing.Key, State = "copy-missing", Error = "其他仓库已有此文件的成功副本；本仓库上传失败，等待完整性修复。" });
                    }
                }
                else if (!control.IsStopping && volume.AwaitingConfirmation == 0)
                {
                    Interlocked.Exchange(ref uploadFailed, 1);
                    control.Stop();
                    if (Progress != null)
                        Progress(new TransferProgress {
                            ObjectName = volume.Object.Name,
                            DestinationId = destination,
                            GroupId = group,
                            State = "archive-upload-failed",
                            Error = "所有仓库均无此分卷的成功记录；已停止此归档，其余任务数据继续处理，失败归档不计入版本。"
                        });
                }
            }
        }

        private void Drain()
        {
            lock (gate)
                while (pending > 0)
                    Monitor.Wait(gate);
        }

        internal Task WhenDrained()
        {
            lock (gate)
            {
                if (pending == 0)
                    return Task.FromResult(true);
                if (drained == null)
                    drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                return drained.Task;
            }
        }

        public GroupedBackupResult Complete(long peakBytes, long earlyBytes)
        {
            Drain();
            if (!UploadFailed)
            {
                control.Checkpoint();
                if (!produced && !catalog.HasCompleteArchiveManifest(archive.ArchiveId))
                    throw new IOException("归档尚未完整生成。");
            }

            var summary = catalog.FinishVolumeArchive(archive, !UploadFailed);
            finished = true;
            archive.Placements = catalog.VolumePlacements(archive.ArchiveId);
            return new GroupedBackupResult
            {
                Archive = summary,
                Plan = archive,
                Objects = Objects.ToList(),
                Uploads = Events.ToList(),
                PeakVolumeBufferBytes = peakBytes,
                UploadedBeforeArchiveCompletion = earlyBytes
            };
        }

        public void Seal()
        {
            produced = true;
        }

        public void Dispose()
        {
            try
            {
                if (!finished)
                {
                    control.Stop();
                    try
                    {
                        Drain();
                    }
                    finally
                    {
                        catalog.FinishVolumeArchive(archive, false);
                        finished = true;
                    }
                }

                if (parent.DeleteIncompleteOnStop && !catalog.IsCompactionArchive(archive.ArchiveId) && parent.TryBeginCleanup(archive.ArchiveId))
                {
                    var errors = new ArchiveMaintenance(catalog, registry).DeleteInterruptedArchive(archive.ArchiveId, parent.CleanupToken);
                    if (errors.Count > 0 && Progress != null)
                        Progress(new TransferProgress { ObjectName = archive.ArchiveName, State = "cleanup-pending", Error = string.Join("；", errors) });
                }
            }
            finally
            {
                control.Dispose();
            }
        }
    }
}
