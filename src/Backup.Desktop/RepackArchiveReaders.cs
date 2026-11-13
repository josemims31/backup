using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        private sealed class RepackReadContext : IDisposable
        {
            internal long ArchiveId;
            internal SevenZipArchiveReader.ReadSession Reader;
            internal Stream Joined;
            internal CancellationTokenSource Transfer;
            internal IDisposable Scope, Lease;
            internal MemoryReadCache Cache;
            public void Dispose()
            {
                if (Transfer != null)
                    Transfer.Cancel();
                try
                {
                    if (Reader != null)
                        Reader.Dispose();
                    if (Joined != null)
                        Joined.Dispose();
                    if (Cache != null)
                        Cache.WaitForPending();
                }
                finally
                {
                    try
                    {
                        if (Scope != null)
                            Scope.Dispose();
                    }
                    finally
                    {
                        if (Lease != null)
                            Lease.Dispose();
                        if (Transfer != null)
                            Transfer.Dispose();
                    }
                }
            }
        }

        private RepackReadContext OpenRepackReader(long id, MemoryReadCache cache, SmartDownloadRouting routing, Action<string> report, CancellationToken cancellation)
        {
            var context = new RepackReadContext
            {
                ArchiveId = id,
                Cache = cache,
                Transfer = CancellationTokenSource.CreateLinkedTokenSource(cancellation)
            };
            try
            {
                context.Lease = Catalog.AcquireArchiveLease(id, true);
                var definitions = Catalog.LoadTopology().Destinations.ToDictionary(d => d.Id);
                var registry = Adapters(maintenanceSearch.Value != null);
                context.Scope = registry.BeginScope();
                var objects = Catalog.GetObjects(id);
                var copies = Catalog.GetCopies(id).ToLookup(copy => copy.Object.Name);
                var states = Catalog.IntegrityCopyStates(id);
                if (objects.Count == 0)
                    throw new IOException("没有已备份的分卷。");
                var token = context.Transfer.Token;
                Action<ObjectRecord, byte[], CancellationToken> load = (item, buffer, cancellationToken) => ReadRestoreVolumeInto(id, item, copies[item.Name], definitions, registry, routing, cancellationToken, report, null, null, null, states, buffer);
                context.Joined = VerifiedVolumeStream.FromCache(objects, cache, load, Math.Max(ArchiveObjectIO.MaximumVolumeBytes, MemoryLimitBytes - 192L * 1024 * 1024), token);
                context.Reader = new SevenZipArchiveReader(Path.Combine(BaseDirectory, "7za.dll"))
                {
                    PauseCheckpoint = Bandwidth.WaitForResume
                }.OpenSession(context.Joined, Catalog.GetArchivePassword(id), token);
                return context;
            }
            catch (global::System.Exception diagnosticFailure)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/RepackArchiveReaders.cs:OpenRepackReader", diagnosticFailure);
                context.Dispose();
                throw;
            }
        }

        // Keep native readers on their owning MTA threads. Two workers allow
        // compression to consume one stream while another producer fills its queue.
        private sealed class RepackArchiveReaders : IDisposable
        {
            private readonly DesktopBackend backend;
            private readonly MemoryReadCache cache;
            private readonly Action<string> report;
            private readonly SmartDownloadRouting routing = new SmartDownloadRouting();
            private sealed class Job
            {
                internal RestoreRequest Request;
                internal Stream Output;
                internal CancellationToken Cancellation;
                internal readonly System.Threading.Tasks.TaskCompletionSource<bool> Done = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            }

            private readonly BlockingCollection<Job> jobs = new BlockingCollection<Job>(2);
            private readonly System.Threading.Tasks.Task[] workers;
            private readonly CancellationTokenSource ending;
            private int disposed;
            internal CancellationToken Cancellation
            {
                get
                {
                    return ending.Token;
                }
            }

            internal RepackArchiveReaders(DesktopBackend owner, MemoryReadCache volumes, Action<string> progress)
            {
                backend = owner;
                cache = volumes;
                report = progress;
                ending = CancellationTokenSource.CreateLinkedTokenSource(owner.OperationCancellation);
                workers = Enumerable.Range(0, 2).Select(_ => System.Threading.Tasks.Task.Factory.StartNew(Work, CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.LongRunning, System.Threading.Tasks.TaskScheduler.Default))
                    .ToArray();
            }

            internal void Extract(RestoreRequest request, Stream output, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                var job = new Job
                {
                    Request = request,
                    Output = output,
                    Cancellation = cancellation
                };
                jobs.Add(job, cancellation);
                job.Done.Task.GetAwaiter().GetResult();
            }

            private void Work()
            {
                RepackReadContext reader = null;
                try
                {
                    foreach (var job in jobs.GetConsumingEnumerable(ending.Token))
                        try
                        {
                            job.Cancellation.ThrowIfCancellationRequested();
                            if (reader == null || reader.ArchiveId != job.Request.File.ArchiveId)
                            {
                                if (reader != null)
                                {
                                    reader.Dispose();
                                    reader = null;
                                }

                                reader = backend.OpenRepackReader(job.Request.File.ArchiveId, cache, routing, report, ending.Token);
                            }

                            using (var token = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation, ending.Token))
                                reader.Reader.ExtractToStreams(
                                    new Dictionary<string, Stream>(StringComparer.OrdinalIgnoreCase) { { job.Request.File.StoredEntryPath, job.Output } },
                                    new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) {
                                    {
                                    job.Request.File.StoredEntryPath,
                                    job.Request.File.Size
                                }
                                },
                                    token.Token
                                );
                            job.Done.TrySetResult(true);
                        }
                        catch (Exception error)
                        {
                            global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/RepackArchiveReaders.cs:Work", error);
                            job.Done.TrySetException(error);
                            if (reader != null)
                            {
                                reader.Dispose();
                                reader = null;
                            }
                        }
                }
                catch (OperationCanceledException)
                { // Expected cancellation, shutdown, or a deliberately rejected identity probe.
                }
                catch (global::System.Exception diagnosticFailure)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/RepackArchiveReaders.cs:Work", diagnosticFailure);
                    ending.Cancel();
                    throw;
                }
                finally
                {
                    try
                    {
                        if (reader != null)
                            reader.Dispose();
                    }
                    finally
                    {
                        Job pending;
                        while (jobs.TryTake(out pending))
                            pending.Done.TrySetCanceled();
                    }
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                    return;
                ending.Cancel();
                jobs.CompleteAdding();
                try
                {
                    System.Threading.Tasks.Task.WaitAll(workers);
                }
                finally
                {
                    jobs.Dispose();
                    ending.Dispose();
                }
            }
        }
    }
}
