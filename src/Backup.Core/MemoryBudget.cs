using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Backup.Core
{
    // One operation budget covers live payload buffers and native database images.
    public sealed class MemoryBudget
    {
        private readonly object gate = new object();
        private long used;
        private int waiting;
        internal event Action Pressure;
        internal bool HasWaiters
        {
            get
            {
                return Volatile.Read(ref waiting) > 0;
            }
        }

        public long LimitBytes { get; private set; }

        public long UsedBytes
        {
            get
            {
                lock (gate)
                    return used;
            }
        }

        public long AvailableBytes
        {
            get
            {
                lock (gate)
                    return LimitBytes - used;
            }
        }

        public long PeakBytes { get; private set; }

        public MemoryBudget(long limit)
        {
            if (limit <= 0)
                throw new ArgumentOutOfRangeException("limit");
            LimitBytes = limit;
        }

        public IDisposable Reserve(long bytes)
        {
            IDisposable value;
            if (TryReserve(bytes, out value))
                return value;
            var trim = Pressure;
            if (trim != null)
                foreach (Action action in trim.GetInvocationList())
                    action();
            if (TryReserve(bytes, out value))
                return value;
            throw new IOException("软件总内存预算不足，上一份数据库备份已保留；请释放当前操作或提高内存限制后重试。");
        }

        public bool TryReserve(long bytes, out IDisposable reservation)
        {
            lock (gate)
            {
                reservation = null;
                if (bytes < 0 || bytes > LimitBytes - used)
                    return false;
                used += bytes;
                PeakBytes = Math.Max(PeakBytes, used);
                reservation = new Reservation(this, bytes);
                return true;
            }
        }

        public IDisposable WaitReserve(long bytes, CancellationToken token)
        {
            if (bytes < 0 || bytes > LimitBytes)
                throw new IOException("分卷超过总内存预算。");
            token.ThrowIfCancellationRequested();
            IDisposable ready;
            if (TryReserve(bytes, out ready))
                return ready;
            Interlocked.Increment(ref waiting);
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    IDisposable value;
                    if (TryReserve(bytes, out value))
                        return value;
                    var trim = Pressure;
                    if (trim != null)
                        foreach (Action action in trim.GetInvocationList())
                            action();
                    if (TryReserve(bytes, out value))
                        return value;
                    lock (gate)
                    {
                        token.ThrowIfCancellationRequested();
                        Monitor.Wait(gate, 100);
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref waiting);
            }
        }

        private sealed class Reservation : IDisposable
        {
            private MemoryBudget owner;
            private readonly long bytes;
            internal Reservation(MemoryBudget value, long count)
            {
                owner = value;
                bytes = count;
            }

            public void Dispose()
            {
                var value = Interlocked.Exchange(ref owner, null);
                if (value != null)
                    lock (value.gate)
                    {
                        value.used -= bytes;
                        Monitor.PulseAll(value.gate);
                    }
            }
        }
    }

    public sealed class DatabaseMemorySnapshot : IDisposable
    {
        private IntPtr data;
        private IDisposable reservation;
        public long Length { get; private set; }

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void sqlite3_free(IntPtr value);
        internal DatabaseMemorySnapshot(IntPtr value, long size, IDisposable memory)
        {
            data = value;
            Length = size;
            reservation = memory;
        }

        ~DatabaseMemorySnapshot()
        {
            Dispose();
        }

        public Stream OpenRead()
        {
            if (data == IntPtr.Zero)
                throw new ObjectDisposedException("DatabaseMemorySnapshot");
            return new NativeStream(this);
        }

        public void Dispose()
        {
            var value = Interlocked.Exchange(ref data, IntPtr.Zero);
            if (value != IntPtr.Zero)
                sqlite3_free(value);
            var lease = Interlocked.Exchange(ref reservation, null);
            if (lease != null)
                lease.Dispose();
            GC.SuppressFinalize(this);
        }

        private sealed class NativeStream : Stream
        {
            private readonly DatabaseMemorySnapshot owner;
            private long position;
            private bool closed;
            internal NativeStream(DatabaseMemorySnapshot value)
            {
                owner = value;
            }

            public override bool CanRead
            {
                get
                {
                    return !closed && owner.data != IntPtr.Zero;
                }
            }

            public override bool CanSeek
            {
                get
                {
                    return CanRead;
                }
            }

            public override bool CanWrite
            {
                get
                {
                    return false;
                }
            }

            public override long Length
            {
                get
                {
                    return owner.Length;
                }
            }

            public override long Position
            {
                get
                {
                    return position;
                }

                set
                {
                    Seek(value, SeekOrigin.Begin);
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (!CanRead)
                    throw new ObjectDisposedException("DatabaseMemorySnapshot");
                if (buffer == null || offset < 0 || count < 0 || offset > buffer.Length - count)
                    throw new ArgumentException("Invalid read buffer.");
                int copied = (int)Math.Min(count, Length - position);
                if (copied > 0)
                    Marshal.Copy(new IntPtr(owner.data.ToInt64() + position), buffer, offset, copied);
                position += copied;
                return copied;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                if (!CanRead)
                    throw new ObjectDisposedException("DatabaseMemorySnapshot");
                if (origin != SeekOrigin.Begin && origin != SeekOrigin.Current && origin != SeekOrigin.End)
                    throw new ArgumentOutOfRangeException("origin");
                long next = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? checked(position + offset) : checked(Length + offset);
                if (next < 0 || next > Length)
                    throw new IOException("Snapshot seek out of range.");
                return position = next;
            }

            protected override void Dispose(bool disposing)
            {
                closed = true;
                base.Dispose(disposing);
            }

            public override void Flush()
            {
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }
    }
}
