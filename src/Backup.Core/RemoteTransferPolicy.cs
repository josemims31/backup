using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Backup.Core
{
    public class RemoteTransferException : IOException
    {
        public readonly string Protocol, Stage;
        public readonly int StatusCode;
        public readonly long Written, ElapsedMilliseconds;
        public readonly int RetryAfterMilliseconds;
        public RemoteTransferException(string protocol, string stage, int status, long written, long elapsed, string reason, Exception inner = null, int retryAfter = 0) : base(
            protocol + " " + stage + "失败" +
            (status > 0 ? "（HTTP " +
            status +
            "）" : "") +
            "：" +
            reason +
            "；已传输 " +
            written +
            " 字节，耗时 " +
            (elapsed / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
            " 秒。",
            inner
        )
        {
            Protocol = protocol;
            Stage = stage;
            StatusCode = status;
            Written = written;
            ElapsedMilliseconds = elapsed;
            RetryAfterMilliseconds = retryAfter;
        }
    }

    // Preparation failed before a PUT or temporary upload was opened.
    public sealed class DestinationPreparationException : IOException
    {
        public DestinationPreparationException(string location, Exception inner = null) : base("仓库目录尚未就绪，等待后重试：" + location, inner)
        {
        }
    }

    public sealed class TemporaryObjectConflictException : IOException
    {
        public TemporaryObjectConflictException() : base("仓库存在不属于此次上传的同名临时对象，已保留，拒绝覆盖或清理。")
        {
        }
    }

    public sealed class BackupObjectConflictException : IOException
    {
        public BackupObjectConflictException() : base("仓库存在未登记的同名备份对象，拒绝覆盖或接管。")
        {
        }
    }

    // An upload has no total-duration deadline. Each successful stream operation renews
    // the inactivity lease, including low-bandwidth transfers that take many minutes.
    internal sealed class TransferActivity : IDisposable
    {
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private readonly Action abort;
        private readonly Timer timer;
        private readonly long idleMilliseconds, responseMilliseconds;
        private readonly object phaseGate = new object();
        private long responseStarted = -1;
        private long lastProgress, bytes;
        private int timedOut, disposed, rateWaiting;
        public void RateWaiting(bool value)
        {
            if (value)
                Interlocked.Increment(ref rateWaiting);
            else
            {
                Progress();
                Interlocked.Decrement(ref rateWaiting);
            }
        }

        public string Stage = "建立上传连接";
        public TransferActivity(Action cancel, TimeSpan idle, TimeSpan responseTimeout = default(TimeSpan))
        {
            if (idle <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException("idle");
            if (responseTimeout < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException("responseTimeout");
            abort = cancel;
            idleMilliseconds = (long)idle.TotalMilliseconds;
            responseMilliseconds = (long)(responseTimeout == TimeSpan.Zero ? idle : responseTimeout).TotalMilliseconds;
            int interval = (int)Math.Max(20, Math.Min(1000, idleMilliseconds / 4));
            timer = new Timer(Check, null, interval, interval);
        }

        private void Check(object state)
        {
            lock (phaseGate)
            {
                if (Volatile.Read(ref disposed) != 0)
                    return;
                bool responding = responseStarted >= 0;
                if (!responding && Volatile.Read(ref rateWaiting) > 0)
                    return;
                long since = responding ? responseStarted : Interlocked.Read(ref lastProgress), limit = responding ? responseMilliseconds : idleMilliseconds;
                if (elapsed.ElapsedMilliseconds - since < limit || Interlocked.Exchange(ref timedOut, 1) != 0)
                    return;
            }

            try
            {
                abort();
            }
            catch (ObjectDisposedException)
            { // Expected cancellation, shutdown, or a deliberately rejected identity probe.
            }
        }

        // Start once, after the complete PUT body has been written. Server-side
        // upload work can be silent; local rate waits must not renew this deadline.
        public void BeginResponseWait()
        {
            lock (phaseGate)
            {
                if (responseStarted >= 0)
                    return;
                Stage = "等待服务器确认";
                responseStarted = elapsed.ElapsedMilliseconds;
            }
        }

        public void Progress(int count = 0)
        {
            Interlocked.Add(ref bytes, count);
            Interlocked.Exchange(ref lastProgress, elapsed.ElapsedMilliseconds);
        }

        public IOException Failure(Exception inner)
        {
            var web = inner as System.Net.WebException;
            var response = web == null ? null : web.Response as System.Net.HttpWebResponse;
            if (response != null)
            {
                int code = (int)response.StatusCode;
                response.Dispose();
                if (code == 412)
                    return new TemporaryObjectConflictException();
                return new RemoteTransferException("WebDAV", Stage, code, Interlocked.Read(ref bytes), elapsed.ElapsedMilliseconds, "服务器拒绝请求", inner);
            }

            string reason = inner.Message;
            if (Volatile.Read(ref timedOut) != 0)
                lock (phaseGate)
                    reason = responseStarted >= 0 
                        ? "等待服务器确认超过 " + (responseMilliseconds / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " 秒" 
                        : "连续 " +
                        (idleMilliseconds / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) +
                        " 秒没有传输进展或服务器应答";
            return new RemoteTransferException("WebDAV", Stage, 0, Interlocked.Read(ref bytes), elapsed.ElapsedMilliseconds, reason, inner);
        }

        public long Bytes
        {
            get
            {
                return Interlocked.Read(ref bytes);
            }
        }

        public long ElapsedMilliseconds
        {
            get
            {
                return elapsed.ElapsedMilliseconds;
            }
        }

        public long ResponseWaitMilliseconds
        {
            get
            {
                lock (phaseGate)
                    return responseStarted < 0 ? 0 : elapsed.ElapsedMilliseconds - responseStarted;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                timer.Dispose();
        }
    }
}
