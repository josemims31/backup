using System;
using System.Collections.Generic;
using System.Threading;

namespace Backup.Core
{
    public interface IOwnedArchiveObjectSink : IArchiveObjectSink
    {
        void UploadOwned(string name, VolumeBuffer buffer, int count);
    }

    public sealed class VolumeBuffer : IDisposable
    {
        private VolumeBufferPool owner;
        public byte[] Bytes { get; private set; }

        internal VolumeBuffer(VolumeBufferPool value, byte[] bytes)
        {
            owner = value;
            Bytes = bytes;
        }

        public void Dispose()
        {
            var pool = Interlocked.Exchange(ref owner, null);
            if (pool != null)
            {
                var bytes = Bytes;
                Bytes = null;
                pool.Return(bytes);
            }
        }
    }

    public sealed class VolumeBufferPool : IDisposable
    {
        private readonly object gate = new object();
        private readonly Stack<byte[]> available = new Stack<byte[]>();
        private readonly Dictionary<byte[], IDisposable> reservations = new Dictionary<byte[], IDisposable>();
        private readonly MemoryBudget shared;
        private readonly BackupControl control;
        private readonly int blockSize, maximum;
        private int allocated, inUse;
        private bool disposed;
        private readonly CancellationTokenRegistration wake;
        public long PeakBytes { get; private set; }
        public long LimitBytes { get; private set; }

        public long InUseBytes
        {
            get
            {
                lock (gate)
                    return (long)inUse * blockSize;
            }
        }

        public bool Waiting { get; private set; }

        public VolumeBufferPool(long budget, int size, BackupControl value, MemoryBudget sharedBudget = null)
        {
            LimitBytes = budget;
            blockSize = size;
            control = value;
            shared = sharedBudget;
            long reserve = budget >= 512L * 1024 * 1024 ? 128L * 1024 * 1024 : 64L * 1024 * 1024;
            maximum = (int)Math.Min(int.MaxValue, ((shared == null ? budget : shared.LimitBytes) - reserve) / size);
            if (maximum < 3)
                throw new ArgumentException("软件总内存预算不足以容纳加密分卷缓冲。");
            if (control != null)
                wake = control.SourceToken.Register(() =>
                {
                    lock (gate)
                        Monitor.PulseAll(gate);
                });
            if (shared != null)
                shared.Pressure += Trim;
        }

        public VolumeBuffer Rent()
        {
            while (true)
            {
                if (control != null)
                    control.SourceCheckpoint();
                lock (gate)
                {
                    if (disposed)
                        throw new ObjectDisposedException("VolumeBufferPool");
                    byte[] bytes = null;
                    if (available.Count > 0)
                        bytes = available.Pop();
                    else if (allocated < maximum)
                    {
                        IDisposable memory = null;
                        if (shared == null || shared.TryReserve(blockSize, out memory))
                            try
                            {
                                bytes = new byte[blockSize];
                                if (memory != null)
                                    reservations.Add(bytes, memory);
                                allocated++;
                            }
                            catch (global::System.Exception diagnosticFailure)
                            {
                                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/VolumeBuffers.cs:Rent", diagnosticFailure);
                                if (memory != null)
                                    memory.Dispose();
                                throw;
                            }
                    }

                    if (bytes != null)
                    {
                        inUse++;
                        PeakBytes = Math.Max(PeakBytes, (long)inUse * blockSize);
                        Waiting = false;
                        return new VolumeBuffer(this, bytes);
                    }

                    Waiting = true;
                    Monitor.Wait(gate, 100);
                }
            }
        }

        private void ReleaseMemory(byte[] bytes)
        {
            IDisposable memory;
            if (reservations.TryGetValue(bytes, out memory))
            {
                reservations.Remove(bytes);
                memory.Dispose();
            }
        }

        internal void Return(byte[] bytes)
        {
            lock (gate)
            {
                inUse--;
                if (!disposed && (shared == null || !shared.HasWaiters) && (control == null || !control.IsStopping && !control.IsFinishing))
                    available.Push(bytes);
                else
                {
                    allocated--;
                    ReleaseMemory(bytes);
                }

                Waiting = false;
                Monitor.PulseAll(gate);
            }
        }

        private void Trim()
        {
            lock (gate)
            {
                while (available.Count > 0)
                {
                    allocated--;
                    ReleaseMemory(available.Pop());
                }

                Monitor.PulseAll(gate);
            }
        }

        public void Dispose()
        {
            if (shared != null)
                shared.Pressure -= Trim;
            lock (gate)
            {
                disposed = true;
                allocated -= available.Count;
                while (available.Count > 0)
                    ReleaseMemory(available.Pop());
                Monitor.PulseAll(gate);
            }

            wake.Dispose();
        }
    }
}
