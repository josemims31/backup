using System;
using System.Collections.Generic;
using System.Threading;

namespace Backup.Core
{
    // Exact-size buffers preserve existing volume/hash length semantics. Idle
    // buffers remain charged to the same budget and are released under pressure.
    internal sealed class ReadBufferPool : IDisposable
    {
        internal sealed class Block
        {
            internal byte[] Bytes;
            internal IDisposable Memory;
        }

        internal sealed class Lease : IDisposable
        {
            private ReadBufferPool owner;
            private Block block;
            internal byte[] Bytes
            {
                get
                {
                    return block.Bytes;
                }
            }

            internal Lease(ReadBufferPool pool, Block value)
            {
                owner = pool;
                block = value;
            }

            public void Dispose()
            {
                var pool = Interlocked.Exchange(ref owner, null);
                if (pool != null)
                {
                    var value = block;
                    block = null;
                    pool.Return(value);
                }
            }
        }

        private readonly object gate = new object();
        private readonly List<Block> idle = new List<Block>();
        private readonly long idleLimit;
        private long idleBytes;
        private bool disposed;
        internal readonly MemoryBudget Budget;
        internal long Allocations { get; private set; }

        internal ReadBufferPool(long limit, MemoryBudget shared)
        {
            Budget = shared ?? new MemoryBudget(limit);
            idleLimit = Math.Min(limit / 4, 400L * 1024 * 1024);
            Budget.Pressure += TrimIdle;
        }

        internal Lease Rent(long size, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (size <= 0 || size > int.MaxValue)
                throw new System.IO.IOException("分卷超过内存传输上限。");
            lock (gate)
            {
                if (disposed)
                    throw new ObjectDisposedException("ReadBufferPool");
                for (int i = idle.Count - 1; i >= 0; i--)
                    if (idle[i].Bytes.LongLength == size)
                    {
                        var block = idle[i];
                        idle.RemoveAt(i);
                        idleBytes -= size;
                        return new Lease(this, block);
                    }
            }

            // Do not reserve a different-sized array while retaining unused tails.
            TrimIdle();
            IDisposable memory = Budget.WaitReserve(size, token);
            try
            {
                token.ThrowIfCancellationRequested();
                var block = new Block
                {
                    Bytes = new byte[(int)size],
                    Memory = memory
                };
                lock (gate)
                {
                    if (disposed)
                        throw new ObjectDisposedException("ReadBufferPool");
                    Allocations++;
                }

                memory = null;
                return new Lease(this, block);
            }
            finally
            {
                if (memory != null)
                    memory.Dispose();
            }
        }

        private void Return(Block block)
        {
            lock (gate)
            {
                if (!disposed && !Budget.HasWaiters && block.Bytes.LongLength <= idleLimit - idleBytes && idle.Count < 8)
                {
                    idle.Add(block);
                    idleBytes += block.Bytes.LongLength;
                    return;
                }
            }

            block.Memory.Dispose();
        }

        internal void TrimIdle()
        {
            Block[] blocks;
            lock (gate)
            {
                blocks = idle.ToArray();
                idle.Clear();
                idleBytes = 0;
            }

            foreach (var block in blocks)
                block.Memory.Dispose();
        }

        public void Dispose()
        {
            Budget.Pressure -= TrimIdle;
            lock (gate)
                disposed = true;
            TrimIdle();
        }
    }
}
