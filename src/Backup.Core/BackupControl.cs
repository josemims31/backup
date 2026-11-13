using System;
using System.Threading;

namespace Backup.Core
{
    public sealed class BackupControl : IDisposable
    {
        private readonly CancellationTokenSource cancellation;
        private readonly BackupControl root;
        private readonly CancellationTokenSource sourceCancellation;
        private readonly CancellationTokenSource exitCancellation;
        private readonly CancellationTokenSource cleanupCancellation;
        private CancellationTokenSource pauseCancellation;
        private readonly System.Collections.Generic.List<CancellationTokenSource> pauseTokens = new System.Collections.Generic.List<CancellationTokenSource>();
        private readonly ManualResetEventSlim resume = new ManualResetEventSlim(true, 0);
        private readonly object stateGate = new object();
        private int paused, stopping, finishing;
        private int deleteIncomplete;
        private Exception failure;
        private readonly System.Collections.Generic.HashSet<long> archives = new System.Collections.Generic.HashSet<long>();
        private readonly System.Collections.Generic.HashSet<long> cleanupAttempts = new System.Collections.Generic.HashSet<long>();
        public BackupControl() : this((TransferBandwidth)null)
        {
        }

        public BackupControl(TransferBandwidth bandwidth)
        {
            Bandwidth = bandwidth;
            root = this;
            cancellation = new CancellationTokenSource();
            sourceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            exitCancellation = new CancellationTokenSource();
            cleanupCancellation = CancellationTokenSource.CreateLinkedTokenSource(exitCancellation.Token);
            pauseCancellation = new CancellationTokenSource();
        }

        private BackupControl(BackupControl parent)
        {
            root = parent.root;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(parent.Token);
        }

        internal CancellationToken BandwidthPauseToken
        {
            get
            {
                return root.Bandwidth == null ? CancellationToken.None : root.Bandwidth.PauseToken;
            }
        }

        public BackupControl CreateArchiveControl()
        {
            return new BackupControl(this);
        }

        public void RegisterArchive(long id)
        {
            lock (root.stateGate)
                root.archives.Add(id);
        }

        public long[] ArchiveIds
        {
            get
            {
                lock (root.stateGate)
                {
                    var ids = new long[root.archives.Count];
                    root.archives.CopyTo(ids);
                    return ids;
                }
            }
        }

        public bool TryBeginCleanup(long id)
        {
            lock (root.stateGate)
                return root.cleanupAttempts.Add(id);
        }

        public CancellationToken Token
        {
            get
            {
                return cancellation.Token;
            }
        }

        public CancellationToken SourceToken
        {
            get
            {
                return root.sourceCancellation.Token;
            }
        }

        public TransferBandwidth Bandwidth { get; private set; }

        public bool IsManuallyPaused
        {
            get
            {
                return Interlocked.CompareExchange(ref root.paused, 0, 0) != 0;
            }
        }

        public bool IsPaused
        {
            get
            {
                return IsManuallyPaused || root.Bandwidth != null && root.Bandwidth.IsPaused;
            }
        }

        public bool IsStopping
        {
            get
            {
                return Interlocked.CompareExchange(ref root.stopping, 0, 0) != 0 || cancellation.IsCancellationRequested;
            }
        }

        public bool IsFinishing
        {
            get
            {
                return Interlocked.CompareExchange(ref root.finishing, 0, 0) != 0;
            }
        }

        public bool DeleteIncompleteOnStop
        {
            get
            {
                return Interlocked.CompareExchange(ref root.deleteIncomplete, 0, 0) != 0;
            }
        }

        public CancellationToken ExitToken
        {
            get
            {
                return root.exitCancellation.Token;
            }
        }

        public CancellationToken CleanupToken
        {
            get
            {
                return root.cleanupCancellation.Token;
            }
        }

        public void Exit()
        {
            Interlocked.Exchange(ref root.deleteIncomplete, 0);
            root.exitCancellation.Cancel();
            root.Stop();
        }

        public void Pause()
        {
            CancellationTokenSource interrupt;
            lock (root.stateGate)
            {
                if (IsStopping || IsFinishing || IsManuallyPaused)
                    return;
                Interlocked.Exchange(ref root.paused, 1);
                root.resume.Reset();
                interrupt = root.pauseCancellation;
            }

            interrupt.Cancel();
        }

        private void ResumeUnsafe()
        {
            if (IsManuallyPaused || root.pauseCancellation.IsCancellationRequested)
            {
                root.pauseTokens.Add(root.pauseCancellation);
                root.pauseCancellation = new CancellationTokenSource();
            }

            Interlocked.Exchange(ref root.paused, 0);
            root.resume.Set();
        }

        public void Resume()
        {
            lock (root.stateGate)
                ResumeUnsafe();
        }

        public void FinishCurrentArchive()
        {
            lock (root.stateGate)
            {
                Interlocked.Exchange(ref root.finishing, 1);
                ResumeUnsafe();
            }

            root.sourceCancellation.Cancel();
        }

        public void Stop()
        {
            Stop(false);
        }

        public void Stop(bool removeIncomplete)
        {
            if (this == root)
            {
                lock (stateGate)
                {
                    if (removeIncomplete && Interlocked.Exchange(ref deleteIncomplete, 1) == 0)
                        cleanupCancellation.CancelAfter(TimeSpan.FromSeconds(10));
                    Interlocked.Exchange(ref stopping, 1);
                    Interlocked.Exchange(ref paused, 0);
                    resume.Set();
                }
            }

            cancellation.Cancel();
        }

        public void Fail(Exception error)
        {
            Interlocked.CompareExchange(ref root.failure, error, null);
            root.Stop();
        }

        public void Checkpoint(bool waitForResume = true)
        {
            if (root.failure != null)
                throw new System.IO.IOException("备份传输失败。", root.failure);
            Token.ThrowIfCancellationRequested();
            if (waitForResume)
                while (true)
                {
                    root.resume.Wait(Token);
                    if (root.Bandwidth != null)
                        root.Bandwidth.WaitForResume(Token);
                    if (!IsManuallyPaused)
                        break;
                }

            if (root.failure != null)
                throw new System.IO.IOException("备份传输失败。", root.failure);
            Token.ThrowIfCancellationRequested();
        }

        public void SourceCheckpoint()
        {
            root.sourceCancellation.Token.ThrowIfCancellationRequested();
            Checkpoint();
            root.sourceCancellation.Token.ThrowIfCancellationRequested();
        }

        public BackupIoLease BeginIo(bool source = false)
        {
            while (true)
            {
                if (source)
                    SourceCheckpoint();
                else
                    Checkpoint();
                lock (root.stateGate)
                {
                    if (IsPaused)
                        continue;
                    return new BackupIoLease(this, root.pauseCancellation.Token, source ? root.sourceCancellation.Token : Token);
                }
            }
        }

        public BackupIoLease TryBeginIo()
        {
            Checkpoint(false);
            lock (root.stateGate)
            {
                if (IsPaused)
                    return null;
                return new BackupIoLease(this, root.pauseCancellation.Token, Token);
            }
        }

        public T Transfer<T>(Func<CancellationToken, T> action)
        {
            while (true)
                using (var io = BeginIo())
                {
                    try
                    {
                        return action(io.Token);
                    }
                    catch (Exception diagnosticFailure)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/BackupControl.cs:Transfer", diagnosticFailure);
                        if (!io.PauseInterrupted)
                            throw;
                        Checkpoint();
                    }
                }
        }

        public void Transfer(Action<CancellationToken> action)
        {
            Transfer(token =>
            {
                action(token);
                return true;
            });
        }

        public void Dispose()
        {
            if (this == root)
            {
                resume.Set();
                sourceCancellation.Dispose();
                cleanupCancellation.Dispose();
                exitCancellation.Dispose();
                pauseCancellation.Dispose();
                foreach (var token in pauseTokens)
                    token.Dispose();
            }

            resume.Dispose();
            cancellation.Dispose();
        }
    }

    public sealed class BackupIoLease : IDisposable
    {
        private readonly BackupControl control;
        private readonly CancellationToken pause, scheduledPause;
        private readonly CancellationTokenSource cancellation;
        internal BackupIoLease(BackupControl owner, CancellationToken paused, CancellationToken operation)
        {
            control = owner;
            pause = paused;
            scheduledPause = owner.BandwidthPauseToken;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(owner.Token, paused, operation, scheduledPause);
        }

        public CancellationToken Token
        {
            get
            {
                return cancellation.Token;
            }
        }

        public bool PauseInterrupted
        {
            get
            {
                return (pause.IsCancellationRequested || scheduledPause.IsCancellationRequested) && !control.IsStopping;
            }
        }

        public void Dispose()
        {
            cancellation.Dispose();
        }
    }

    public interface ICancellableBackupDestination
    {
        CancellationToken OperationCancellation { get; set; }
    }

    public sealed class ReplicaPool
    {
        public int Index;
        public string GroupId;
        public System.Collections.Generic.List<string> DestinationIds = new System.Collections.Generic.List<string>();
    }
}
