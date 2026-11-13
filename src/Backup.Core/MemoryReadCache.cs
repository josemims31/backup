using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Backup.Core
{
    // Reserve before I/O; Task results never retain buffers outside this cache.
    public sealed class MemoryReadCache : IDisposable
    {
        private sealed class Entry
        {
            public long Size;
            public byte[] Bytes;
            public Exception Error;
            public bool Done;
            public int Readers;
            public IDisposable Memory;
        }

        private readonly object gate = new object();
        private readonly long limit;
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        private readonly LinkedList<string> recent = new LinkedList<string>();
        private long bytes, prefetchRevision;
        private int active;
        private bool disposed;
        private readonly MemoryBudget shared;
        private readonly ReadBufferPool buffers;
        private readonly BackgroundReadQueue loaders = new BackgroundReadQueue();
        private readonly CancellationTokenSource ending = new CancellationTokenSource();
        private readonly CancellationToken lifetime;
        internal long PrefetchRevision
        {
            get
            {
                return Interlocked.Read(ref prefetchRevision);
            }
        }

#if UI_TEST
        public int TestPrefetchCalls;
#endif
        public long PeakBytes { get; private set; }

        public MemoryReadCache(long budget, MemoryBudget sharedBudget = null)
        {
            limit = budget;
            buffers = new ReadBufferPool(budget, sharedBudget);
            shared = buffers.Budget;
            lifetime = ending.Token;
            shared.Pressure += Trim;
        }

        private void Remove(string key, Entry value)
        {
            Entry current;
            if (!entries.TryGetValue(key, out current) || current != value)
                return;
            bytes -= value.Size;
            entries.Remove(key);
            recent.Remove(key);
            if (value.Memory != null)
            {
                value.Memory.Dispose();
                value.Memory = null;
            }

            Interlocked.Increment(ref prefetchRevision);
        }

        private void Trim()
        {
            lock (gate)
            {
                foreach (string key in recent.ToList())
                {
                    var entry = entries[key];
                    if (entry.Done && entry.Readers == 0)
                        Remove(key, entry);
                }

                Monitor.PulseAll(gate);
            }

            // Reserve() also raises pressure without setting HasWaiters. Release
            // buffers returned by the entry trim before its next reservation.
            buffers.TrimIdle();
        }

        private static string Key(ObjectRecord item)
        {
            return item.Name + "|" + item.Sha256;
        }

        private void Check()
        {
            if (disposed)
                throw new ObjectDisposedException("MemoryReadCache");
        }

        private bool MakeRoom(long size, string protect, ISet<string> upcoming = null)
        {
            if (size > limit)
                throw new IOException("分卷超过内存预算。");
            for (var node = recent.First; bytes + size > limit && node != null;)
            {
                var next = node.Next;
                var value = entries[node.Value];
                if (node.Value != protect && (upcoming == null || !upcoming.Contains(node.Value)) && value.Done && value.Readers == 0)
                    Remove(node.Value, value);
                node = next;
            }

            return bytes + size <= limit && active < 8;
        }

        private sealed class LoadedBuffer
        {
            internal byte[] Bytes;
            internal IDisposable Memory;
        }

        private void Start(ObjectRecord item, Func<CancellationToken, LoadedBuffer> load, CancellationToken cancellation)
        {
            string key = Key(item);
            var entry = new Entry
            {
                Size = item.Size
            };
            entries.Add(key, entry);
            recent.AddLast(key);
            bytes += item.Size;
            PeakBytes = Math.Max(PeakBytes, bytes);
            active++;
            Interlocked.Increment(ref prefetchRevision);
            try
            {
                loaders.Enqueue(delegate
                {
                    LoadedBuffer value = null;
                    Exception error = null;
                    try
                    {
                        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime))
                        {
                            cancel.Token.ThrowIfCancellationRequested();
                            value = load(cancel.Token);
                            if (value.Bytes.LongLength != item.Size)
                                throw new IOException("分卷长度已变化。");
                        }
                    }
                    catch (Exception failure)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/MemoryReadCache.cs:Start", failure);
                        error = failure;
                        if (value != null)
                        {
                            value.Memory.Dispose();
                            value = null;
                        }
                    }

                    lock (gate)
                    {
                        active--;
                        if (!disposed)
                        {
                            entry.Bytes = value == null ? null : value.Bytes;
                            entry.Error = error;
                            entry.Memory = value == null ? null : value.Memory;
                            entry.Done = true;
                            value = null;
                        }

                        if (value != null)
                            value.Memory.Dispose();
                        Interlocked.Increment(ref prefetchRevision);
                        Monitor.PulseAll(gate);
                    }
                });
            }
            catch (Exception error)
            {
                RuntimeDiagnostics.Record("MemoryReadCache:enqueue", error);
                active--;
                Remove(key, entry);
                Monitor.PulseAll(gate);
                throw;
            }
        }

        private Func<CancellationToken, LoadedBuffer> LegacyLoader(ObjectRecord item, Func<byte[]> load)
        {
            return token =>
            {
                IDisposable memory = shared.WaitReserve(item.Size, token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    var result = new LoadedBuffer
                    {
                        Bytes = load(),
                        Memory = memory
                    };
                    memory = null;
                    return result;
                }
                finally
                {
                    if (memory != null)
                        memory.Dispose();
                }
            };
        }

        private Func<CancellationToken, LoadedBuffer> PooledLoader(ObjectRecord item, Action<byte[], CancellationToken> load)
        {
            return token =>
            {
                var lease = buffers.Rent(item.Size, token);
                try
                {
                    load(lease.Bytes, token);
                    var result = new LoadedBuffer
                    {
                        Bytes = lease.Bytes,
                        Memory = lease
                    };
                    lease = null;
                    return result;
                }
                finally
                {
                    if (lease != null)
                        lease.Dispose();
                }
            };
        }

        public int Copy(ObjectRecord item, int source, byte[] target, int offset, int count, Func<byte[]> load, CancellationToken cancellation = default(CancellationToken))
        {
            return CopyCore(item, source, target, offset, count, load, null, cancellation);
        }

        internal int CopyInto(ObjectRecord item, int source, byte[] target, int offset, int count, Action<byte[], CancellationToken> load, CancellationToken cancellation)
        {
            return CopyCore(item, source, target, offset, count, null, load, cancellation);
        }

        private int CopyCore(
            ObjectRecord item,
            int source,
            byte[] target,
            int offset,
            int count,
            Func<byte[]> legacyLoad,
            Action<byte[], CancellationToken> pooledLoad,
            CancellationToken cancellation
        )
        {
            string key = Key(item);
            lock (gate)
                while (true)
                {
                    Check();
                    cancellation.ThrowIfCancellationRequested();
                    Entry entry;
                    if (!entries.TryGetValue(key, out entry))
                    {
                        if (!MakeRoom(item.Size, null))
                        {
                            Monitor.Wait(gate, 100);
                            continue;
                        }

                        // Cache hits need no new loader wrapper or closure.
                        Start(item, pooledLoad == null ? LegacyLoader(item, legacyLoad) : PooledLoader(item, pooledLoad), cancellation);
                        entry = entries[key];
                    }

                    entry.Readers++;
                    try
                    {
                        while (!entry.Done)
                        {
                            Check();
                            cancellation.ThrowIfCancellationRequested();
                            Monitor.Wait(gate, 100);
                        }

                        if (entry.Error != null)
                        {
                            var error = entry.Error;
                            Remove(key, entry);
                            Monitor.PulseAll(gate);
                            if (error is OperationCanceledException)
                            {
                                if (!cancellation.CanBeCanceled)
                                    throw error;
                                cancellation.ThrowIfCancellationRequested();
                                continue;
                            }

                            throw new IOException("下载分卷失败：" + item.Name, error);
                        }

                        recent.Remove(key);
                        recent.AddLast(key);
                        Buffer.BlockCopy(entry.Bytes, source, target, offset, count);
                        return count;
                    }
                    finally
                    {
                        entry.Readers--;
                    }
                }
        }

        public void Prefetch(IEnumerable<ObjectRecord> next, Func<ObjectRecord, byte[]> load, ObjectRecord current, CancellationToken cancellation = default(CancellationToken))
        {
            PrefetchSnapshot(next, load, current, cancellation);
        }

        internal long PrefetchSnapshot(IEnumerable<ObjectRecord> next, Func<ObjectRecord, byte[]> load, ObjectRecord current, CancellationToken cancellation)
        {
            return PrefetchCore(next, item => LegacyLoader(item, () => load(item)), current, cancellation);
        }

        internal long PrefetchInto(IEnumerable<ObjectRecord> next, Action<ObjectRecord, byte[], CancellationToken> load, ObjectRecord current, CancellationToken cancellation)
        {
            return PrefetchCore(next, item => PooledLoader(item, (buffer, token) => load(item, buffer, token)), current, cancellation);
        }

        private long PrefetchCore(IEnumerable<ObjectRecord> next, Func<ObjectRecord, Func<CancellationToken, LoadedBuffer>> loader, ObjectRecord current, CancellationToken cancellation)
        {
            lock (gate)
            {
                Check();
#if UI_TEST
                TestPrefetchCalls++;
#endif
                if (cancellation.IsCancellationRequested)
                    return prefetchRevision;
                string currentKey = Key(current);
                var upcoming = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in next)
                {
                    string key = Key(item);
                    if (entries.ContainsKey(key))
                    {
                        upcoming.Add(key);
                        continue;
                    }

                    if (!MakeRoom(item.Size, currentKey, upcoming))
                        break;
                    Start(item, loader(item), cancellation);
                    upcoming.Add(key);
                }

                return prefetchRevision;
            }
        }

        public void WaitForPending()
        {
            lock (gate)
                while (active > 0)
                    Monitor.Wait(gate);
        }

        public void Dispose()
        {
            if (shared != null)
                shared.Pressure -= Trim;
            lock (gate)
            {
                if (disposed)
                    return;
                disposed = true;
                foreach (var entry in entries.Values)
                    if (entry.Memory != null)
                        entry.Memory.Dispose();
                entries.Clear();
                recent.Clear();
                bytes = 0;
                Interlocked.Increment(ref prefetchRevision);
                Monitor.PulseAll(gate);
            }

            ending.Cancel();
            loaders.Complete();
            buffers.Dispose();
            ending.Dispose();
        }
    }
}
