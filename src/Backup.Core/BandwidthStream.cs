using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Backup.Core
{
    public sealed class BandwidthStream : Stream
    {
        private readonly Stream inner;
        private readonly TransferBandwidth bandwidth;
        private readonly bool sending;
        private readonly CancellationToken cancellation;
        private readonly Action<bool> waiting;
        private readonly bool measureNetwork;
        private readonly Action<int> transferred;
        public BandwidthStream(Stream value, TransferBandwidth policy, bool upload, CancellationToken token, Action<bool> whileWaiting = null, bool measureNetwork = true, Action<int> progress = null)
        {
            inner = value;
            bandwidth = policy;
            sending = upload;
            cancellation = token;
            waiting = whileWaiting;
            this.measureNetwork = measureNetwork;
            transferred = progress;
        }

        public static Stream Wrap(Stream value, TransferBandwidth policy, bool upload, CancellationToken token, Action<bool> whileWaiting = null, bool measureNetwork = true, Action<int> progress = null)
        {
            return policy == null && progress == null ? value : new BandwidthStream(value, policy, upload, token, whileWaiting, measureNetwork, progress);
        }

        private void Record(int count)
        {
            if (measureNetwork && bandwidth != null)
                bandwidth.RecordBytes(count, sending);
            if (transferred != null && count > 0)
                transferred(count);
        }

        public override bool CanRead
        {
            get
            {
                return inner.CanRead;
            }
        }

        public override bool CanSeek
        {
            get
            {
                return inner.CanSeek;
            }
        }

        public override bool CanWrite
        {
            get
            {
                return inner.CanWrite;
            }
        }

        public override long Length
        {
            get
            {
                return inner.Length;
            }
        }

        public override long Position
        {
            get
            {
                return inner.Position;
            }

            set
            {
                inner.Position = value;
            }
        }

        private int Take(int count, CancellationToken token)
        {
            if (bandwidth == null)
            {
                token.ThrowIfCancellationRequested();
                return count;
            }

            if (waiting != null)
                waiting(true);
            try
            {
                return bandwidth.Acquire(count, sending, token);
            }
            finally
            {
                if (waiting != null)
                    waiting(false);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Take(count, cancellation);
            int read = inner.Read(buffer, offset, n);
            Record(read);
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int n = Take(count, cancellation);
                inner.Write(buffer, offset, n);
                Record(n);
                offset += n;
                count -= n;
            }
        }

        private CancellationTokenSource Link(CancellationToken token, out CancellationToken effective)
        {
            if (!token.CanBeCanceled || token == cancellation)
            {
                effective = cancellation;
                return null;
            }

            if (!cancellation.CanBeCanceled)
            {
                effective = token;
                return null;
            }

            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, token);
            effective = linked.Token;
            return linked;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            if ((bandwidth == null || bandwidth.Unrestricted(sending)) && (!token.CanBeCanceled || !cancellation.CanBeCanceled || token == cancellation))
            {
                var read = inner.ReadAsync(buffer, offset, count, token.CanBeCanceled ? token : cancellation);
                if (!measureNetwork && transferred == null)
                    return read;
                if (read.Status == TaskStatus.RanToCompletion)
                {
                    Record(read.Result);
                    return read;
                }

                return ReadMeasuredAsync(read);
            }

            return ReadLimitedAsync(buffer, offset, count, token);
        }

        private async Task<int> ReadMeasuredAsync(Task<int> read)
        {
            int count = await read.ConfigureAwait(false);
            Record(count);
            return count;
        }

        private async Task<int> ReadLimitedAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            CancellationToken effective;
            using (var linked = Link(token, out effective))
            {
                if (waiting != null)
                    waiting(true);
                int n;
                try
                {
                    n = bandwidth == null ? count : await bandwidth.AcquireAsync(count, sending, effective).ConfigureAwait(false);
                }
                finally
                {
                    if (waiting != null)
                        waiting(false);
                }

                int read = await inner.ReadAsync(buffer, offset, n, effective).ConfigureAwait(false);
                Record(read);
                return read;
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            if ((bandwidth == null || bandwidth.Unrestricted(sending)) && (!token.CanBeCanceled || !cancellation.CanBeCanceled || token == cancellation))
            {
                var write = inner.WriteAsync(buffer, offset, count, token.CanBeCanceled ? token : cancellation);
                if (!measureNetwork && transferred == null)
                    return write;
                if (write.Status == TaskStatus.RanToCompletion)
                {
                    Record(count);
                    return write;
                }

                return WriteMeasuredAsync(write, count);
            }

            return WriteLimitedAsync(buffer, offset, count, token);
        }

        private async Task WriteMeasuredAsync(Task write, int count)
        {
            await write.ConfigureAwait(false);
            Record(count);
        }

        private async Task WriteLimitedAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            CancellationToken effective;
            using (var linked = Link(token, out effective))
                while (count > 0)
                {
                    if (waiting != null)
                        waiting(true);
                    int n;
                    try
                    {
                        n = bandwidth == null ? count : await bandwidth.AcquireAsync(count, sending, effective).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (waiting != null)
                            waiting(false);
                    }

                    await inner.WriteAsync(buffer, offset, n, effective).ConfigureAwait(false);
                    Record(n);
                    offset += n;
                    count -= n;
                }
        }

        public override void Flush()
        {
            inner.Flush();
        }

        public override Task FlushAsync(CancellationToken token)
        {
            return inner.FlushAsync(token);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return inner.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            inner.SetLength(value);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
