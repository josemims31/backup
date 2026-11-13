using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace Backup.Core
{
    public interface IArchiveObjectSink
    {
        void Upload(string name, byte[] buffer, int count);
    }

    public sealed class RemoteObjectInfo
    {
        public string Name;
        public bool Exists;
        public long Size;
    }

    public sealed class InvalidBackupObjectException : IOException
    {
        public InvalidBackupObjectException(string message) : base(message)
        {
        }
    }

    // Protocol adapters own I/O only. Scheduling, retry and integrity policy stay outside adapters.
    public interface IBackupDestination
    {
        string Id { get; }

        Stream OpenTemporary(string name);
        void CommitTemporary(string name);
        void DeleteTemporary(string name);
        RemoteObjectInfo Stat(string name);
    }

    public interface IManagedBackupDestination : IBackupDestination
    {
        void PrepareWrite();
        Stream OpenRead(string name);
        void DeleteObject(string name);
    }

    public interface IKnownLengthUploadDestination
    {
        Stream OpenTemporary(string name, long length);
    }

    public interface IReadOnlyDestinationProbe
    {
        void CheckReadableRoot();
    }

    public sealed class UploadEvent
    {
        public string Destination;
        public string ObjectName;
        public int Attempt;
        public string Result;
        public long Written;
        public long SourceBytesAtAttempt;
    }

    public sealed class TransferProgress
    {
        public string DestinationLabel;
        public string TransferStage;
        public string BackgroundState;
        public long BackgroundWritten, BackgroundSize;
        public string Error;
        public int AttemptLimit;
        public string GroupId;
        public string DestinationId;
        public string ObjectName;
        public int Attempt;
        public int CheckAttempt;
        public bool CheckConfirmed;
        public long Written;
        public long Size;
        public string State;
        public long MemoryBytes;
        public long MemoryLimit;
        public bool WaitingForMemory;
    }

    public sealed class ObjectRecord
    {
        public string Name;
        public long Size;
        public string Sha256;
    }

    public sealed class TransferReconciliationRequiredException : IOException
    {
        public TransferReconciliationRequiredException(string message, Exception inner = null) : base(message, inner)
        {
        }
    }

    public sealed class RetryUploader : IArchiveObjectSink
    {
        public const int DefaultRetries = 3;
        public const int DefaultAttempts = DefaultRetries + 1;
        public static string FailureMessage(TransferProgress p)
        {
            string text = p.State == "retrying" 
                ? "上传失败，正在重试 " + p.Attempt + " / " + (p.AttemptLimit - 1) 
                : p.State == "retry-succeeded" 
                ? "重试成功" 
                : p.State == "confirmation-succeeded" 
                ? "上传结果已确认" 
                : p.State == "confirmation-pending" ? "上传结果待确认" : p.State == "upload-check-result" ? "上传核对 " +
                p.CheckAttempt +
                " / 3" : p.State == "copy-missing" ? "上传失败，待完整性修复" : "上传失败";
            string detail = string.IsNullOrEmpty(p.Error) ? "" : p.Error.Replace("\r\n", "；").Replace("\r", "；").Replace("\n", "；");
            string subject = text + "：" + p.DestinationId + " · " + p.ObjectName;
            if (p.State == "upload-check-result")
                return subject + " · 结果：" + (string.IsNullOrEmpty(detail) ? "未取得核对结果" : detail);
            if (p.State == "confirmation-pending")
                return subject + (string.IsNullOrEmpty(detail) ? "" : " · 原因：" + detail);
            return subject + (p.State == "retrying" || string.IsNullOrEmpty(detail) ? "" : "\n原因：" + detail);
        }

        private static string UploadFailureReason(Exception error)
        {
            Exception selected = error;
            for (int count = 0; count < 12 && error != null; count++, error = error.InnerException)
            {
                if (error is OpenListTaskPendingException && error.InnerException != null)
                {
                    if (ReferenceEquals(selected, error) || selected is TransferReconciliationRequiredException)
                        selected = error.InnerException;
                    continue;
                }

                if (error.Message.StartsWith("OpenList", StringComparison.Ordinal))
                    selected = error;
                else if (selected is TransferReconciliationRequiredException && !(error is TransferReconciliationRequiredException))
                    selected = error;
            }

            return selected.Message;
        }

        private static bool HasUncertainOpenListTask(Exception error)
        {
            for (int count = 0; count < 12 && error != null; count++, error = error.InnerException)
                if (error is OpenListTaskQueryException || error is OpenListTaskPendingException || error is RemoteDirectoryPendingException)
                    return true;
            return false;
        }

        private static bool HasPendingDirectory(Exception error)
        {
            for (int count = 0; count < 12 && error != null; count++, error = error.InnerException)
                if (error is RemoteDirectoryPendingException)
                    return true;
            return false;
        }

        public static string StageMessage(TransferProgress p, string kind)
        {
            string stage = p.State == "waiting-destination" 
                ? "正在等待仓库就绪" 
                : p.State == "preparing-destination" 
                ? "正在连接并准备仓库" 
                : p.State == "checking-target" 
                ? "正在检查上传目标" 
                : p.State == "opening-upload" ? "正在建立上传" : p.State == "committing-upload" ? "正在提交上传" : p.State == "verifying-upload" ? "正在核对上传结果" : null;
            return stage == null 
                ? null 
                : stage + " · " + kind + " " + p.ObjectName + " → " + p.DestinationId + (p.State == "waiting-destination" && !string.IsNullOrEmpty(p.Error) ? "；" + p.Error : "");
        }

        private readonly IList<IBackupDestination> destinations;
        private readonly int attempts;
        public readonly List<UploadEvent> Events = new List<UploadEvent>();
        public readonly List<ObjectRecord> Objects = new List<ObjectRecord>();
        public Func<string, string, int, long, bool> InjectFailure;
        public Func<long> SourceBytes;
        public BackupControl Control;
        public TransferScheduler Transfers = TransferScheduler.Shared;
        public Action<string> PhaseChanged;
        public Action<TransferProgress> Progress;
        internal bool SingleVerificationAttempt, DeferVerificationRetry;
        internal UploadRetryState RetryState;
        public RetryUploader(IList<IBackupDestination> destinations, int attempts)
        {
            if (destinations.Count == 0 || attempts < 1)
                throw new ArgumentException("Destination and retry count required.");
            this.destinations = destinations;
            this.attempts = attempts;
        }

        public void Upload(string name, byte[] buffer, int count)
        {
            Upload(CreateRecord(name, buffer, count), buffer, count);
        }

        internal static ObjectRecord CreateRecord(string name, byte[] buffer, int count)
        {
            if (buffer == null || count <= 0 || count > buffer.Length)
                throw new ArgumentException("Invalid payload.");
            using (SHA256 sha = SHA256.Create())
                return new ObjectRecord
                {
                    Name = name,
                    Size = count,
                    Sha256 = BitConverter.ToString(sha.ComputeHash(buffer, 0, count)).Replace("-", "").ToLowerInvariant()
                };
        }

        // A committed object is not always visible to the first metadata query.
        // Gateways that copy or index on commit can take seconds to expose the final
        // name and size, so poll briefly before declaring the result unconfirmed.
        private RemoteObjectInfo ConfirmCommittedObject(IBackupDestination destination, string name, long count)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Exception lastError = null;
            while (true)
            {
                try
                {
                    RemoteObjectInfo remote = destination.Stat(name);
                    if (remote.Exists && remote.Name == name && remote.Size == count)
                        return remote;
                }
                catch (IOException error)
                {
                    lastError = error;
                }
                catch (UnauthorizedAccessException error)
                {
                    lastError = error;
                }

                if (clock.Elapsed >= TimeSpan.FromSeconds(120))
                    break;
                if (Control != null)
                {
                    if (Control.Token.WaitHandle.WaitOne(5000))
                        Control.Checkpoint(false);
                }
                else
                    System.Threading.Thread.Sleep(5000);
            }

            throw new TransferReconciliationRequiredException("上传已提交，等待仓库确认文件名称和大小。", lastError);
        }

        // True only when the destination stored the complete temporary object even
        // though the upload response reported a failure (for example HTTP 405 after
        // the final byte). Exact size equality is the evidence a normal commit uses.
        private bool TemporaryObjectComplete(IBackupDestination destination, string name, long count)
        {
            var probe = destination as ITemporaryUploadProbe;
            if (probe == null)
                return false;
            try
            {
                RemoteObjectInfo temporary = probe.StatTemporary(name);
                return temporary != null && temporary.Exists && temporary.Size == count;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        internal void Upload(ObjectRecord record, byte[] buffer, int count)
        {
            if (SingleVerificationAttempt)
            {
                UploadAttempt(record, buffer, count);
                return;
            }

            foreach (var destination in destinations)
            {
                var state = RetryState ?? new UploadRetryState();
                bool owns = RetryState == null;
                state.Bind(destination, record.Name);
                try
                {
                    while (true)
                    {
                        if (Control != null)
                            Control.Checkpoint();
                        if (state.Checking)
                        {
                            if (!DeferVerificationRetry)
                                UploadVerificationRetry.Wait(Control, state.CheckDelay);
                            string detail;
                            bool confirmed;
                            var live = destination as IBackgroundUploadProgress;
                            if (live != null)
                                live.BackgroundProgress = Progress;
                            try
                            {
                                confirmed = UploadVerificationRetry.Check(destination, record, Control, PhaseChanged, out detail, state);
                            }
                            finally
                            {
                                if (live != null)
                                    live.BackgroundProgress = null;
                            }

                            if (state.BackgroundPending)
                            {
                                if (DeferVerificationRetry)
                                    throw new DeferredUploadRetryException(state.CheckDelay);
                                continue;
                            }

                            state.Checks++;
                            if (Progress != null)
                                Progress(new TransferProgress {
                                    DestinationId = destination.Id,
                                    ObjectName = record.Name,
                                    State = "upload-check-result",
                                    Attempt = state.Retries + 1,
                                    CheckAttempt = state.Checks,
                                    AttemptLimit = attempts,
                                    Size = count,
                                    Error = detail,
                                    CheckConfirmed = confirmed
                                });
                            if (confirmed)
                            {
                                try
                                {
                                    destination.DeleteTemporary(record.Name);
                                }
                                catch (IOException diagnosticFailure)
                                {
                                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/Destinations.cs:Upload", diagnosticFailure);
                                }
                                catch (UnauthorizedAccessException diagnosticFailure)
                                {
                                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/Destinations.cs:Upload", diagnosticFailure);
                                }

                                var owned = destination as IOwnedUploadDestination;
                                if (owned != null)
                                    owned.RejectTemporary(record.Name);
                                if (Progress != null)
                                    Progress(new TransferProgress {
                                        DestinationId = destination.Id,
                                        ObjectName = record.Name,
                                        State = state.Retries > 0 ? "retry-succeeded" : "confirmation-succeeded",
                                        Attempt = state.Retries +
                                        1,
                                        AttemptLimit = attempts,
                                        Size = count,
                                        Written = count,
                                        CheckConfirmed = true
                                    });
                                break;
                            }

                            if (state.Checks < 3)
                            {
                                if (DeferVerificationRetry)
                                    throw new DeferredUploadRetryException(state.CheckDelay);
                                continue;
                            }

                            if (state.Retries >= attempts - 1)
                            {
                                if (state.CheckUncertain || state.DirectoryPending || HasUncertainOpenListTask(state.Error))
                                    throw new TransferReconciliationRequiredException(detail + "；本轮及三轮重试的核对均未能确认结果，保留原上传任务待后续核对。", state.Error);
                                if (Progress != null)
                                    Progress(new TransferProgress {
                                        DestinationId = destination.Id,
                                        ObjectName = record.Name,
                                        State = "upload-failed",
                                        Attempt = state.Retries + 1,
                                        AttemptLimit = attempts,
                                        Error = detail +
                                        "；已重传 " +
                                        state.Retries +
                                        " 次，上传失败。"
                                    });
                                throw new UploadRetriesExhaustedException(state.Error);
                            }

                            state.Retries++;
                            state.Checks = 0;
                            state.Checking = false;
                            UploadVerificationRetry.Suspend(destination, record.Name);
                            if (Progress != null)
                                Progress(new TransferProgress { DestinationId = destination.Id, ObjectName = record.Name, State = "retrying", Attempt = state.Retries, AttemptLimit = attempts, Error = detail });
                            if (DeferVerificationRetry)
                                throw new DeferredUploadRetryException(0);
                        }

                        var single = new RetryUploader(new[] { destination }, 1)
                        {
                            SingleVerificationAttempt = true,
                            Control = Control,
                            SourceBytes = SourceBytes,
                            PhaseChanged = phase =>
                            {
                                if (phase == "writing" || phase == "committing")
                                    state.HasSent = true;
                                if (PhaseChanged != null)
                                    PhaseChanged(phase);
                            },
                            Progress = p =>
                            {
                                p.Attempt += state.Retries;
                                p.AttemptLimit = attempts;
                                if (Progress != null)
                                    Progress(p);
                            },
                            InjectFailure = InjectFailure == null ? null : (Func<string, string, int, long, bool>)((id, name, attempt, written) => InjectFailure(id, name, attempt + state.Retries, written))
                        };
                        try
                        {
                            Action prepare = () =>
                            {
                                var managed = destination as IManagedBackupDestination;
                                if (managed != null)
                                    managed.PrepareWrite();
                                var info = destination.Stat(record.Name);
                                if (info.Exists)
                                {
                                    var owned = destination as IOwnedUploadDestination;
                                    if (!state.HasSent && (owned == null || owned.UploadJournal == null || owned.UploadJournal.UploadStages(destination.Id, record.Name).Count == 0))
                                        state.UnownedConflict = true;
                                    throw new BackupObjectConflictException();
                                }
                            };
                            if (Control == null)
                                prepare();
                            else
                                Control.Transfer(token =>
                                {
                                    var cancel = destination as ICancellableBackupDestination;
                                    if (cancel != null)
                                        cancel.OperationCancellation = token;
                                    prepare();
                                });
                            var transferCancellation = destination as ICancellableBackupDestination;
                            Transfers.Upload(
                                destination.Id,
                                () => single.Upload(record, buffer, count),
                                Control == null ? (transferCancellation == null ? CancellationToken.None : transferCancellation.OperationCancellation) : Control.Token
                            );
                            if (state.Retries > 0 && Progress != null)
                                Progress(new TransferProgress {
                                    DestinationId = destination.Id,
                                    ObjectName = record.Name,
                                    State = "retry-succeeded",
                                    Attempt = state.Retries + 1,
                                    AttemptLimit = attempts,
                                    Size = count,
                                    Written = count
                                });
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception error)
                        {
                            if (error is OpenListAuthorizationException)
                                throw;
                            if (!(error is IOException) && !(error is UnauthorizedAccessException))
                                throw;
                            state.Error = error;
                            state.DirectoryPending |= HasPendingDirectory(error);
                            state.Checking = true;
                            state.Checks = 0;
                            state.ScheduleCheck();
                            var staged = destination as IOwnedUploadDestination;
                            bool uncertain = state.HasSent || staged != null && staged.UploadJournal != null && staged.UploadJournal.UploadStages(destination.Id, record.Name).Count > 0;
                            if (uncertain && state.Catalog != null)
                                state.Catalog.RetainRetryUpload(destination.Id, record.Name);
                            else if (!uncertain && PhaseChanged != null)
                                PhaseChanged("checking");
                            try
                            {
                                var owned = destination as IOwnedUploadDestination;
                                if (owned == null || owned.UploadJournal != null)
                                    UploadVerificationRetry.Suspend(destination, record.Name);
                            }
                            catch (IOException diagnosticFailure)
                            {
                                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/Destinations.cs:Upload", diagnosticFailure);
                            }

                            if (Progress != null)
                                Progress(new TransferProgress {
                                    DestinationId = destination.Id,
                                    ObjectName = record.Name,
                                    State = uncertain ? "confirmation-pending" : "waiting-destination",
                                    Attempt = state.Retries +
                                    1,
                                    AttemptLimit = attempts,
                                    Size = count,
                                    Error = UploadFailureReason(error)
                                });
                            if (DeferVerificationRetry)
                                throw new DeferredUploadRetryException(state.CheckDelay);
                        }
                        finally
                        {
                            Events.AddRange(single.Events.Select(item =>
                            {
                                item.Attempt += state.Retries;
                                return item;
                            }));
                        }
                    }
                }
                finally
                {
                    if (owns)
                        state.Dispose();
                }
            }

            Objects.Add(record);
        }

        private void UploadAttempt(ObjectRecord record, byte[] buffer, int count)
        {
            if (record.Size != count)
                throw new ArgumentException("Payload size does not match object record.");
            string name = record.Name;
            foreach (IBackupDestination destination in destinations)
            {
                var cancellable = destination as ICancellableBackupDestination;
                if (cancellable != null && Control != null)
                    cancellable.OperationCancellation = Control.Token;
                bool complete = false;
                for (int attempt = 1; attempt <= attempts; attempt++)
                {
                    var background = destination as IBackgroundUploadProgress;
                    if (background != null)
                        background.BackgroundProgress = p =>
                        {
                            p.Attempt = attempt;
                            if (Progress != null)
                                Progress(p);
                        };
                    BackupIoLease io = Control == null ? null : Control.BeginIo();
                    if (cancellable != null && io != null)
                        cancellable.OperationCancellation = io.Token;
                    bool commitAttempted = false, temporaryOpened = false;
                    UploadEvent item = new UploadEvent
                    {
                        Destination = destination.Id,
                        ObjectName = name,
                        Attempt = attempt,
                        SourceBytesAtAttempt = SourceBytes == null ? 0 : SourceBytes()
                    };
                    Events.Add(item);
                    try
                    {
                        if (Control != null)
                            Control.Checkpoint(false);
                        var sized = destination as IKnownLengthUploadDestination;
                        if (Progress != null)
                            Progress(new TransferProgress { DestinationId = destination.Id, ObjectName = name, Attempt = attempt, Size = count, State = "opening-upload" });
                        using (Stream target = sized == null ? destination.OpenTemporary(name) : sized.OpenTemporary(name, count))
                        {
                            temporaryOpened = true;
                            if (PhaseChanged != null)
                                PhaseChanged("writing");
                            while (item.Written < count)
                            {
                                int chunk = (int)Math.Min(1024 * 1024, count - item.Written);
                                if (Control != null)
                                    Control.Checkpoint(false);
                                var local = target as FileStream;
                                if (local != null && Control != null)
                                    local.WriteAsync(buffer, (int)item.Written, chunk, Control.Token).GetAwaiter().GetResult();
                                else
                                    target.Write(buffer, (int)item.Written, chunk);
                                item.Written += chunk;
                                if (Progress != null && (background == null || !background.BackgroundUploadActive))
                                    Progress(new TransferProgress {
                                        DestinationId = destination.Id,
                                        ObjectName = name,
                                        Attempt = attempt,
                                        Written = item.Written,
                                        Size = count,
                                        State = "uploading",
                                        TransferStage = background != null &&
                                        background.BackgroundUploadActive ? "发送至 OpenList" : null
                                    });
                                if (InjectFailure != null && InjectFailure(destination.Id, name, attempt, item.Written))
                                {
                                    target.Flush();
                                    throw new IOException("Injected transfer interruption after actual destination write.");
                                }
                            }

                            FileStream file = target as FileStream;
                            if (file != null)
                                file.Flush(true);
                            else
                                target.Flush();
                        }

                        if (Control != null)
                            Control.Checkpoint(false);
                        if (PhaseChanged != null)
                            PhaseChanged("committing");
                        if (Progress != null)
                            Progress(new TransferProgress { DestinationId = destination.Id, ObjectName = name, Attempt = attempt, Written = count, Size = count, State = "committing-upload" });
                        commitAttempted = true;
                        destination.CommitTemporary(name);
                        if (Progress != null)
                            Progress(new TransferProgress { DestinationId = destination.Id, ObjectName = name, Attempt = attempt, Written = count, Size = count, State = "verifying-upload" });
                        ConfirmCommittedObject(destination, name, count);
                        var confirmed = destination as IOwnedUploadDestination;
                        if (confirmed != null)
                            confirmed.RejectTemporary(name);
                        item.Result = "success";
                        complete = true;
                        break;
                    }
                    catch (Exception e)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/Destinations.cs:UploadAttempt", e);
                        if (io != null && io.PauseInterrupted)
                        {
                            io.Dispose();
                            io = null;
                            item.Result = "paused";
                            Control.Transfer(token =>
                            {
                                if (cancellable != null)
                                    cancellable.OperationCancellation = token;
                                if (commitAttempted)
                                {
                                    var observed = destination.Stat(name);
                                    if (observed.Exists)
                                    {
                                        if (observed.Name != name || observed.Size != count)
                                            throw new TransferReconciliationRequiredException("暂停前提交的对象状态待核对，已保留记录。");
                                        complete = true;
                                        item.Result = "success-after-pause";
                                    }
                                }

                                var owned = destination as IOwnedUploadDestination;
                                var interrupted = destination as IInterruptedUploadDestination;
                                if (!complete && interrupted != null)
                                    interrupted.SuspendTemporary(name);
                                else if (temporaryOpened || owned != null && owned.UploadJournal != null && owned.UploadJournal.UploadStages(destination.Id, name).Count > 0)
                                    destination.DeleteTemporary(name);
                            });
                            if (complete)
                                break;
                            attempt--;
                            continue;
                        }

                        if (Control != null && Control.IsStopping)
                            Control.Checkpoint(false);
                        if (e is OpenListAuthorizationException || !(e is IOException) && !(e is UnauthorizedAccessException))
                            throw;
                        item.Result = "pending-confirmation";
                        throw new TransferReconciliationRequiredException("上传结果未确认，进入三次定时核对。", e);
                    }
                    finally
                    {
                        if (background != null)
                            background.BackgroundProgress = null;
                        if (io != null)
                            io.Dispose();
                    }
                }

                if (!complete)
                    throw new IOException("Upload did not complete.");
                var pausedUpload = destination as IInterruptedUploadDestination;
                if (pausedUpload != null && pausedUpload.HasInterruptedTemporary(name))
                    try
                    {
                        if (Control != null)
                            Control.Transfer(token =>
                            {
                                if (cancellable != null)
                                    cancellable.OperationCancellation = token;
                                destination.DeleteTemporary(name);
                            });
                        else
                            destination.DeleteTemporary(name);
                    }
                    catch (Exception error)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/Destinations.cs:UploadAttempt", error);
                        if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is OperationCanceledException))
                            throw;
                        if (Progress != null)
                            Progress(new TransferProgress { DestinationId = destination.Id, ObjectName = name, State = "cleanup-pending", Error = error.Message });
                    }

                if (Progress != null && Events[Events.Count - 1].Attempt > 1)
                    Progress(new TransferProgress { DestinationId = destination.Id, ObjectName = name, State = "retry-succeeded", Attempt = Events[Events.Count - 1].Attempt, AttemptLimit = attempts });
            }

            Objects.Add(record);
        }
    }

    public sealed class IntegrityFinding
    {
        public long ArchiveId;
        public bool ArchiveLevel, UploadConfirmation, RepairApplied, ContentVerified;
        public string RecordedStatus, ObservedStatus, RepairAction, Folder, Detail;
        public string Destination;
        public string Name;
        public long ExpectedSize;
        public long ActualSize;
        public string Status;
        public bool RequiresManualRepairConfirmation;
    }

    public static class DestinationIntegrityChecker
    {
        // Deliberately has no upload/delete API and never opens object content.
        public static IList<IntegrityFinding> Check(IBackupDestination destination, IEnumerable<ObjectRecord> objects, System.Threading.CancellationToken cancellation = default(System.Threading.CancellationToken))
        {
            List<IntegrityFinding> findings = new List<IntegrityFinding>();
            foreach (ObjectRecord item in objects)
            {
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    RemoteObjectInfo remote = destination.Stat(item.Name);
                    cancellation.ThrowIfCancellationRequested();
                    bool bad = !remote.Exists || remote.Name != item.Name || remote.Size != item.Size;
                    findings.Add(new IntegrityFinding {
                        Destination = destination.Id,
                        Name = item.Name,
                        ExpectedSize = item.Size,
                        ActualSize = remote.Size,
                        Status = !remote.Exists ? "missing" : bad ? "size-or-name-mismatch" : "present-size-matched",
                        RequiresManualRepairConfirmation = bad
                    });
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (InvalidBackupObjectException)
                {
                    findings.Add(new IntegrityFinding {
                        Destination = destination.Id,
                        Name = item.Name,
                        ExpectedSize = item.Size,
                        ActualSize = -1,
                        Status = "size-or-name-mismatch",
                        RequiresManualRepairConfirmation = true
                    });
                }
                catch (Exception e)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/Destinations.cs:Check", e);
                    cancellation.ThrowIfCancellationRequested();
                    findings.Add(new IntegrityFinding {
                        Destination = destination.Id,
                        Name = item.Name,
                        ExpectedSize = item.Size,
                        ActualSize = -1,
                        Status = "unavailable: " + e.Message,
                        RequiresManualRepairConfirmation = false
                    });
                }
            }

            return findings;
        }
    }
}
