using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Backup.Desktop
{
    internal sealed class ProducerReadStream : Stream
    {
        private readonly BlockingCollection<byte[]> blocks = new BlockingCollection<byte[]>(8);
        private readonly CancellationTokenSource cancellation;
        private readonly Lazy<Task> producer;
        private readonly long length;
        private byte[] current;
        private int currentOffset;
        private long position;
        private Exception error;
        private bool disposed;
        public ProducerReadStream(long size, Action<Stream, CancellationToken> write, CancellationToken parent)
        {
            length = size;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(parent);
            producer = new Lazy<Task>(() => Task.Factory.StartNew(
                delegate
            {
                try
                {
                    write(new Output(this), cancellation.Token);
                }
                catch (Exception failure)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/ProducerReadStream.cs:ProducerReadStream", failure);
                    error = failure;
                }
                finally
                {
                    blocks.CompleteAdding();
                }
            },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            ));
        }

        private sealed class Output : Stream
        {
            private readonly ProducerReadStream owner;
            public Output(ProducerReadStream value)
            {
                owner = value;
            }

            public override void Write(byte[] bytes, int offset, int count)
            {
                while (count > 0)
                {
                    int n = Math.Min(count, 64 * 1024);
                    var block = new byte[n];
                    Buffer.BlockCopy(bytes, offset, block, 0, n);
                    owner.blocks.Add(block, owner.cancellation.Token);
                    offset += n;
                    count -= n;
                }
            }

            public override bool CanWrite
            {
                get
                {
                    return true;
                }
            }

            public override bool CanRead
            {
                get
                {
                    return false;
                }
            }

            public override bool CanSeek
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
                    throw new NotSupportedException();
                }
            }

            public override long Position
            {
                get
                {
                    throw new NotSupportedException();
                }

                set
                {
                    throw new NotSupportedException();
                }
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] b, int o, int n)
            {
                throw new NotSupportedException();
            }

            public override long Seek(long o, SeekOrigin s)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long n)
            {
                throw new NotSupportedException();
            }
        }

        public override int Read(byte[] bytes, int offset, int count)
        {
            if (disposed)
                throw new ObjectDisposedException("ProducerReadStream");
            if (count == 0)
                return 0;
            var running = producer.Value;
            if (current == null || currentOffset == current.Length)
            {
                current = null;
                currentOffset = 0;
                if (!blocks.TryTake(out current, Timeout.Infinite, cancellation.Token))
                {
                    running.GetAwaiter().GetResult();
                    if (error != null)
                        throw new IOException("恢复流未完成。", error);
                    if (position != length)
                        throw new IOException("恢复文件长度不符。");
                    return 0;
                }
            }

            int n = Math.Min(count, current.Length - currentOffset);
            Buffer.BlockCopy(current, currentOffset, bytes, offset, n);
            currentOffset += n;
            position += n;
            if (position > length)
                throw new IOException("恢复文件超过清单长度。");
            if (position == length)
            {
                running.GetAwaiter().GetResult();
                if (error != null)
                    throw new IOException("恢复校验未通过。", error);
            }

            return n;
        }

        internal void WaitForPending()
        {
            if (producer.IsValueCreated)
                producer.Value.GetAwaiter().GetResult();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                cancellation.Cancel();
                current = null;
                Action release = () =>
                {
                    byte[] item;
                    while (blocks.TryTake(out item))
                    {
                    }

                    blocks.Dispose();
                    cancellation.Dispose();
                };
                if (producer.IsValueCreated)
                    producer.Value.ContinueWith(_ => release(), TaskScheduler.Default);
                else
                    release();
            }

            base.Dispose(disposing);
        }

        public override bool CanRead
        {
            get
            {
                return !disposed;
            }
        }

        public override bool CanWrite
        {
            get
            {
                return false;
            }
        }

        public override bool CanSeek
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
                return length;
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
                throw new NotSupportedException();
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] bytes, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
