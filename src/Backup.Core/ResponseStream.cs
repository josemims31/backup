using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;

namespace Backup.Core
{
    internal sealed class ResponseStream : Stream
    {
        private Stream inner;
        private readonly Action complete, abort;
        private readonly long expected;
        private readonly IDisposable cancellation;
        private readonly TransferActivity activity;
        private readonly Action<Exception> onFailure;
        private readonly long expectedRead;
        private long readBytes;
        private readonly CancellationToken ioCancellation;
        private bool failed;
        private long written;
        public ResponseStream(
            Stream stream,
            Action onComplete,
            Action onAbort,
            long expectedLength = -1,
            IDisposable cancel = null,
            TransferActivity progress = null,
            Action<Exception> failure = null,
            long expectedReadLength = -1,
            CancellationToken ioToken = default(CancellationToken)
        )
        {
            inner = stream;
            complete = onComplete;
            abort = onAbort;
            expected = expectedLength;
            cancellation = cancel;
            activity = progress;
            onFailure = failure;
            expectedRead = expectedReadLength;
            ioCancellation = ioToken;
        }

        private IOException Failure(Exception error)
        {
            failed = true;
            if (onFailure != null)
                try
                {
                    onFailure(error);
                }
                catch (global::System.Exception diagnosticFailure)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/ResponseStream.cs:Failure", diagnosticFailure);
                }

            return error as IOException ?? (activity == null ? new IOException("远程数据传输失败（" + error.GetType().Name + "）：" + error.Message, error) : activity.Failure(error));
        }

        public override bool CanRead
        {
            get
            {
                return inner != null && inner.CanRead;
            }
        }

        public override bool CanWrite
        {
            get
            {
                return inner != null && inner.CanWrite;
            }
        }

        public override bool CanSeek
        {
            get
            {
                return inner != null && inner.CanSeek;
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

        public override void Flush()
        {
            try
            {
                if (ioCancellation.CanBeCanceled)
                    inner.FlushAsync(ioCancellation).GetAwaiter().GetResult();
                else
                    inner.Flush();
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/ResponseStream.cs:Flush", error);
                throw Failure(error);
            }
        }

        public override int Read(byte[] bytes, int offset, int count)
        {
            try
            {
                int read = ioCancellation.CanBeCanceled ? inner.ReadAsync(bytes, offset, count, ioCancellation).GetAwaiter().GetResult() : inner.Read(bytes, offset, count);
                readBytes += read;
                if (read == 0 && count > 0 && expectedRead >= 0 && readBytes < expectedRead)
                    throw new EndOfStreamException("WebDAV 下载连接提前结束，收到 " + readBytes + " / " + expectedRead + " 字节。");
                if (activity != null && read > 0)
                    activity.Progress(read);
                return read;
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/ResponseStream.cs:Read", error);
                throw Failure(error);
            }
        }

        public override async System.Threading.Tasks.Task<int> ReadAsync(byte[] bytes, int offset, int count, CancellationToken token)
        {
            using (var linked = ioCancellation.CanBeCanceled && token.CanBeCanceled && token != ioCancellation ? CancellationTokenSource.CreateLinkedTokenSource(ioCancellation, token) : null)
            {
                try
                {
                    var effective = linked != null ? linked.Token : ioCancellation.CanBeCanceled ? ioCancellation : token;
                    int read = await inner.ReadAsync(bytes, offset, count, effective).ConfigureAwait(false);
                    readBytes += read;
                    if (read == 0 && count > 0 && expectedRead >= 0 && readBytes < expectedRead)
                        throw new EndOfStreamException("WebDAV 下载连接提前结束，收到 " + readBytes + " / " + expectedRead + " 字节。");
                    if (activity != null && read > 0)
                        activity.Progress(read);
                    return read;
                }
                catch (Exception error)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/ResponseStream.cs:ReadAsync", error);
                    throw Failure(error);
                }
            }
        }

        public override void Write(byte[] bytes, int offset, int count)
        {
            try
            {
                while (count > 0)
                {
                    int chunk = Math.Min(count, 8 * 1024);
                    if (ioCancellation.CanBeCanceled)
                        inner.WriteAsync(bytes, offset, chunk, ioCancellation).GetAwaiter().GetResult();
                    else
                        inner.Write(bytes, offset, chunk);
                    written += chunk;
                    offset += chunk;
                    count -= chunk;
                    if (activity != null)
                        activity.Progress(chunk);
                }
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/ResponseStream.cs:Write", error);
                throw Failure(error);
            }
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
            try
            {
                if (!disposing || inner == null)
                    return;
                var stream = inner;
                inner = null;
                if (ioCancellation.IsCancellationRequested)
                    failed = true;
                if (expected >= 0 && written != expected)
                    failed = true;
                if (failed && abort != null)
                    abort();
                try
                {
                    if (activity != null && !failed)
                        activity.BeginResponseWait();
                    stream.Dispose();
                    if (!failed && complete != null)
                        complete();
                    else if (abort != null)
                        abort();
                }
                catch (Exception error)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/ResponseStream.cs:Dispose", error);
                    if (abort != null)
                        abort();
                    if (error is OperationCanceledException)
                        throw;
                    ioCancellation.ThrowIfCancellationRequested();
                    if (!failed)
                        throw Failure(error);
                }
            }
            finally
            {
                if (disposing)
                {
                    if (activity != null)
                        activity.Dispose();
                    if (cancellation != null)
                        cancellation.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
