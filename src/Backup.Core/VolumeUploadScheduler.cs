using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Backup.Core
{
    // One application queue for payload IO. Metadata retries never hold a lane.
    public sealed class TransferScheduler : IDisposable
    {
        public const int MaximumDownloads = 16;
        public static readonly TransferScheduler Shared = new TransferScheduler();
        private readonly Lane uploads = new Lane(5, 3), downloads = new Lane(MaximumDownloads, MaximumDownloads);
        private sealed class DownloadWork
        {
            internal object Owner;
            internal string Destination;
            internal Action<CancellationToken> Work;
            internal Action<Exception> Completed;
            internal CancellationTokenSource Cancellation;
            internal CancellationTokenRegistration Registration;
            internal int Finished;
            internal readonly TaskCompletionSource<bool> Ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly object downloadGate = new object();
        private readonly List<DownloadWork> downloadQueue = new List<DownloadWork>(), helpers = new List<DownloadWork>();
        private readonly List<Thread> downloadWorkers = new List<Thread>();
        private readonly CancellationTokenSource downloadLifetime = new CancellationTokenSource();
        private bool downloadClosed;
        private object lastDownloadOwner;
        public int ActiveUploads
        {
            get
            {
                return uploads.Active;
            }
        }

        public int ActiveDownloads
        {
            get
            {
                return downloads.Active;
            }
        }

        public IDisposable BeginDownload(string destination, CancellationToken token)
        {
            return downloads.Enter(destination, token);
        }

        public void Upload(string destination, Action work, CancellationToken token)
        {
            using (uploads.Enter(destination, token))
            {
                token.ThrowIfCancellationRequested();
                work();
            }
        }

        internal bool HasSpareDownloadCapacity
        {
            get
            {
                lock (downloadGate)
                    return !downloadClosed && downloadQueue.Count == 0 && helpers.Count == 0 && downloads.Active < MaximumDownloads;
            }
        }

        internal Task QueueDownload(object owner, string destination, Action<CancellationToken> work, CancellationToken token, Action<Exception> completed, bool spareOnly = false)
        {
            lock (downloadGate)
            {
                if (downloadClosed)
                    throw new ObjectDisposedException("TransferScheduler");
                if (spareOnly && (downloadQueue.Count > 0 || helpers.Count > 0 || downloads.Active >= MaximumDownloads))
                    return null;
                var job = new DownloadWork
                {
                    Owner = owner,
                    Destination = destination,
                    Work = work,
                    Completed = completed,
                    Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, downloadLifetime.Token)
                };
                (spareOnly ? helpers : downloadQueue).Add(job);
                job.Registration = job.Cancellation.Token.Register(() => ThreadPool.QueueUserWorkItem(_ => CancelQueuedDownload(job)));
                if (downloadWorkers.Count == 0)
                    for (int i = 0; i < MaximumDownloads; i++)
                    {
                        var thread = new Thread(DownloadWorker)
                        {
                            IsBackground = true,
                            Name = "Backup download " + (i + 1)
                        };
                        downloadWorkers.Add(thread);
                        thread.Start();
                    }

                Monitor.PulseAll(downloadGate);
                return job.Ready.Task;
            }
        }

        private void DownloadWorker()
        {
            while (true)
            {
                DownloadWork job;
                lock (downloadGate)
                {
                    while (!downloadClosed && downloadQueue.Count == 0 && helpers.Count == 0)
                        Monitor.Wait(downloadGate);
                    if (downloadQueue.Count == 0 && helpers.Count == 0)
                        return;
                    var queue = downloadQueue.Count > 0 ? downloadQueue : helpers;
                    int next = queue.FindIndex(item => !ReferenceEquals(item.Owner, lastDownloadOwner));
                    if (next < 0)
                        next = 0;
                    job = queue[next];
                    queue.RemoveAt(next);
                    lastDownloadOwner = job.Owner;
                }

                Exception failure = null;
                try
                {
                    using (BeginDownload(job.Destination, job.Cancellation.Token))
                    {
                        job.Cancellation.Token.ThrowIfCancellationRequested();
                        job.Work(job.Cancellation.Token);
                    }
                }
                catch (Exception error)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/VolumeUploadScheduler.cs:DownloadWorker", error);
                    failure = error;
                }

                FinishDownload(job, failure);
            }
        }

        private void CancelQueuedDownload(DownloadWork job)
        {
            bool removed;
            lock (downloadGate)
                removed = downloadQueue.Remove(job) || helpers.Remove(job);
            if (removed)
                FinishDownload(job, new OperationCanceledException(job.Cancellation.Token));
        }

        private static void FinishDownload(DownloadWork job, Exception failure)
        {
            if (Interlocked.Exchange(ref job.Finished, 1) != 0)
                return;
            try
            {
                if (job.Completed != null)
                    job.Completed(failure);
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/VolumeUploadScheduler.cs:FinishDownload", error);
                failure = error;
            }

            if (failure == null)
                job.Ready.TrySetResult(true);
            else if (failure is OperationCanceledException)
                job.Ready.TrySetCanceled();
            else
                job.Ready.TrySetException(failure);
            job.Registration.Dispose();
            job.Cancellation.Dispose();
        }

        public void Dispose()
        {
            Thread[] workers;
            lock (downloadGate)
            {
                if (downloadClosed)
                    return;
                downloadClosed = true;
                workers = downloadWorkers.ToArray();
                Monitor.PulseAll(downloadGate);
            }

            downloadLifetime.Cancel();
            uploads.Dispose();
            downloads.Dispose();
            foreach (var worker in workers)
                if (worker != Thread.CurrentThread)
                    worker.Join();
            downloadLifetime.Dispose();
        }

        private sealed class Lane : IDisposable
        {
            private sealed class Request
            {
                internal string Destination;
                internal readonly TaskCompletionSource<IDisposable> Ready = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            private sealed class Lease : IDisposable
            {
                private Lane owner;
                private readonly string destination;
                internal Lease(Lane value, string id)
                {
                    owner = value;
                    destination = id;
                }

                public void Dispose()
                {
                    var value = Interlocked.Exchange(ref owner, null);
                    if (value != null)
                        value.Release(destination);
                }
            }

            private readonly object gate = new object();
            private readonly int maximum, perWarehouse;
            private int active;
            private bool closed;
            private readonly List<Request> pending = new List<Request>();
            private readonly Dictionary<string, int> running = new Dictionary<string, int>();
            internal Lane(int count, int per)
            {
                maximum = count;
                perWarehouse = per;
            }

            internal int Active
            {
                get
                {
                    lock (gate)
                        return active;
                }
            }

            private void Pump()
            {
                while (active < maximum)
                {
                    var next = pending.FirstOrDefault(item => !running.ContainsKey(item.Destination) || running[item.Destination] < perWarehouse);
                    if (next == null)
                        return;
                    pending.Remove(next);
                    int count;
                    running.TryGetValue(next.Destination, out count);
                    running[next.Destination] = count + 1;
                    active++;
                    next.Ready.TrySetResult(new Lease(this, next.Destination));
                }
            }

            internal IDisposable Enter(string destination, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                var request = new Request
                {
                    Destination = destination
                };
                lock (gate)
                {
                    if (closed)
                        throw new ObjectDisposedException("TransferScheduler");
                    pending.Add(request);
                    Pump();
                }

                using (token.Register(() =>
                {
                    lock (gate)
                        if (pending.Remove(request))
                        {
                            request.Ready.TrySetCanceled();
                            Pump();
                        }
                }))
                {
                    var lease = request.Ready.Task.GetAwaiter().GetResult();
                    if (token.IsCancellationRequested)
                    {
                        lease.Dispose();
                        token.ThrowIfCancellationRequested();
                    }

                    return lease;
                }
            }

            private void Release(string destination)
            {
                lock (gate)
                {
                    active--;
                    if (--running[destination] == 0)
                        running.Remove(destination);
                    if (!closed)
                        Pump();
                }
            }

            public void Dispose()
            {
                lock (gate)
                {
                    closed = true;
                    foreach (var request in pending)
                        request.Ready.TrySetCanceled();
                    pending.Clear();
                }
            }
        }
    }

    public sealed class PreparedBackup : IDisposable
    {
        private readonly StripedArchiveSession session;
        private readonly VolumeBufferPool buffers;
        private readonly long earlyBytes;
        internal PreparedBackup(StripedArchiveSession value, VolumeBufferPool pool, long early)
        {
            session = value;
            buffers = pool;
            earlyBytes = early;
        }

        public GroupedBackupResult Complete()
        {
            return session.Complete(buffers.PeakBytes, earlyBytes);
        }

        public async Task<GroupedBackupResult> CompleteAsync()
        {
            await session.WhenDrained().ConfigureAwait(false);
            return Complete();
        }

        public void Dispose()
        {
            session.Dispose();
        }
    }
}
