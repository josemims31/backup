using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Backup.Core
{
    public sealed partial class WebDavDestination
    {
        private void EnsureServerTrust(CancellationToken cancellation)
        {
            connection.ServerTrust.Ensure(RemoteServerIdentity.Endpoint("webdav", root.AbsoluteUri), ProbeServerIdentity, cancellation);
        }

        // A single, shared credential-free handshake obtains the certificate.
        // Confirmation happens after the probe is closed, outside HTTP timeouts.
        private RemoteServerIdentity ProbeServerIdentity(CancellationToken cancellation)
        {
            try
            {
                RemoteServerIdentity identity = null;
                using (var socket = new TcpClient())
                using (cancellation.Register(() => socket.Close()))
                {
                    WaitIdentityProbe(socket.ConnectAsync(root.DnsSafeHost, root.Port), cancellation);
                    using (var tls = new SslStream(
                        socket.GetStream(),
                        false,
                        (sender, cert, chain, errors) =>
                    {
                        identity = RemoteServerIdentity.Tls(root, cert, chain, errors);
                        return true;
                    }
                    ))
                        WaitIdentityProbe(
                            tls.AuthenticateAsClientAsync(root.DnsSafeHost, null, (System.Security.Authentication.SslProtocols)System.Net.ServicePointManager.SecurityProtocol, System.Net.ServicePointManager.CheckCertificateRevocationList),
                            cancellation
                        );
                }

                cancellation.ThrowIfCancellationRequested();
                if (identity == null)
                    throw new IOException("未取得 WebDAV 服务器证书。");
                return identity;
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Repositories.WebDav/WebDavServerTrust.cs:ProbeServerIdentity", error);
                cancellation.ThrowIfCancellationRequested();
                if (error is System.Security.Authentication.AuthenticationException || error is SocketException)
                    throw new IOException("WebDAV 服务器身份检查失败：" + error.Message, error);
                throw;
            }
        }

        private static void WaitIdentityProbe(Task task, CancellationToken cancellation)
        {
            int ready = WaitHandle.WaitAny(new[] { ((IAsyncResult)task).AsyncWaitHandle, cancellation.WaitHandle }, RemoteConnectionPolicy.ConnectSeconds * 1000);
            if (ready == WaitHandle.WaitTimeout)
            {
                task.ContinueWith(failed =>
                {
                    var ignored = failed.Exception;
                }, TaskContinuationOptions.OnlyOnFaulted);
                throw new IOException("服务器证书连接检查超时。");
            }

            if (ready == 1)
            {
                task.ContinueWith(failed =>
                {
                    var ignored = failed.Exception;
                }, TaskContinuationOptions.OnlyOnFaulted);
                cancellation.ThrowIfCancellationRequested();
            }

            task.GetAwaiter().GetResult();
        }
    }
}
