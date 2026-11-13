using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Backup.Core
{
    public sealed class SftpDestination : IRepositorySession,
        ICancellableRemoteFolderBrowser,
        IStorageDirectoryListing,
        IStorageEntryRemoval,
        ICancellableBackupDestination,
        IDisposable,
        IOwnedUploadDestination,
        IInterruptedUploadDestination,
        IRecoverableUploadDestination,
        IReadOnlyDestinationProbe,
        IBandwidthDestination,
        ITemporaryUploadProbe,
        ITaskStorageRemoval,
        IRangedBackupDestination    {
        private DirectoryRefreshCache directoryRefresh = new DirectoryRefreshCache();
        private string directoryScope;
        public void ShareDirectoryRefresh(DirectoryRefreshCache cache, string scope)
        {
            directoryRefresh = cache;
            directoryScope = scope;
        }

        IManagedBackupDestination IRepositorySession.ForDirectory(string location, CancellationToken cancellation)
        {
            return ForDirectory(location, cancellation);
        }

        public IList<StorageEntry> ListEntriesAt(string location, CancellationToken cancellation, bool fresh)
        {
            if (fresh)
                directoryRefresh.InvalidateFolder(location);
            return ListEntriesAt(location, cancellation);
        }

        private readonly Uri root;
        private readonly string path;
        private readonly RemoteConnectionOptions connection;
        public string Id { get; private set; }
        public CancellationToken OperationCancellation { get; set; }
        public TransferBandwidth Bandwidth { get; set; }

        private readonly object sessionGate = new object();
        private readonly IdleSessionPool<Session> idleSessions = new IdleSessionPool<Session>(TransferScheduler.MaximumDownloads + 3);
        private Timer idleTimer;
        private bool disposed;
        private readonly CancellationTokenSource readConnectionLifetime = new CancellationTokenSource();
        private readonly int idleTimeoutMilliseconds;
        private long idleVersion, idleSince;
        private readonly SftpDestination sessionOwner;
        private readonly HashSet<Session> connectedSessions = new HashSet<Session>(), rentedSessions = new HashSet<Session>();
        private string connectionError;
        private int connecting;
        private bool writePrepared;
        public event Action ConnectionStateChanged;
        public string ConnectionState
        {
            get
            {
                if (sessionOwner != null)
                    return sessionOwner.ConnectionState;
                lock (sessionGate)
                    return connectedSessions.Any(session => session.Connected) ? "已连接" : connecting > 0 ? "正在连接" : connectionError != null ? "连接失败" : "空闲";
            }
        }

        public string ConnectionError
        {
            get
            {
                if (sessionOwner != null)
                    return sessionOwner.ConnectionError;
                lock (sessionGate)
                    return connectionError;
            }
        }

        private void NotifyConnection()
        {
            var observers = ConnectionStateChanged;
            if (observers != null)
                foreach (Action observer in observers.GetInvocationList())
                    try
                    {
                        observer();
                    }
                    catch (global::System.Exception diagnosticFailure)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Repositories.Sftp/SftpDestination.cs:NotifyConnection", diagnosticFailure);
                    }
        }

        public SftpDestination ForDirectory(string location, CancellationToken cancellation)
        {
            var address = RemotePath.Root(location, "sftp");
            if (address.Authority != root.Authority || !address.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.Ordinal))
                throw new IOException("仓库地址越界。");
            return new SftpDestination(this, location, cancellation);
        }

        private SftpDestination(SftpDestination owner, string location, CancellationToken cancellation) : this(owner.Id, location, owner.connection, TimeSpan.FromMilliseconds(owner.idleTimeoutMilliseconds))
        {
            sessionOwner = owner.sessionOwner ?? owner;
            OperationCancellation = cancellation;
            Bandwidth = owner.Bandwidth;
            UploadJournal = owner.UploadJournal;
            directoryRefresh = owner.directoryRefresh;
            directoryScope = owner.directoryScope;
        }

        private readonly UploadStaging staging;
        public IUploadJournal UploadJournal
        {
            get
            {
                return staging.Journal;
            }

            set
            {
                staging.Journal = value;
            }
        }

        public void RejectTemporary(string name)
        {
            staging.Rejected(name);
        }

        public RemoteObjectInfo StatTemporary(string name)
        {
            return Stat(staging.Current(name));
        }

        public void ResumeTemporary(string name, string temporary)
        {
            staging.Resume(name, temporary);
        }

        public void SuspendTemporary(string name)
        {
            staging.Suspend(name);
        }

        public bool HasInterruptedTemporary(string name)
        {
            return staging.HasInterrupted(name);
        }

        public SftpDestination(string id, string location, RemoteConnectionOptions options) : this(id, location, options, TimeSpan.FromSeconds(RemoteConnectionPolicy.IdleSeconds))
        {
        }

        public SftpDestination(string id, string location, RemoteConnectionOptions options, TimeSpan idleTimeout)
        {
            if (idleTimeout.TotalMilliseconds < 1 || idleTimeout.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException("idleTimeout");
            idleTimeoutMilliseconds = (int)idleTimeout.TotalMilliseconds;
            Id = id;
            staging = new UploadStaging(id);
            root = RemotePath.Root(location, "sftp");
            path = Uri.UnescapeDataString(root.AbsolutePath).TrimEnd('/');
            if (path.Length == 0)
                path = "/";
            connection = options ?? new RemoteConnectionOptions();
            if (connection.ServerTrust == null)
                connection.ServerTrust = new RemoteServerTrust();
        }

        private string Resolve(string name)
        {
            return path.TrimEnd('/') + "/" + RemotePath.Name(name);
        }

        private sealed class Session : IDisposable
        {
            public SftpClient Client;
            public PrivateKeyFile Key;
            public IDisposable Authentication;
            public CancellationTokenRegistration Cancellation;
            public CancellationToken Token;
            private readonly object cancellationGate = new object(), closeGate = new object();
            private long cancellationVersion;
            private int closeSignaled;
            internal Action<Session> Closed;
            internal bool IsClosing
            {
                get
                {
                    return Interlocked.CompareExchange(ref closeSignaled, 0, 0) != 0;
                }
            }

            internal bool Connected
            {
                get
                {
                    var client = Client;
                    return client != null && client.IsConnected;
                }
            }

            private void SignalClosed()
            {
                if (Interlocked.Exchange(ref closeSignaled, 1) == 0)
                {
                    var callback = Closed;
                    if (callback != null)
                        callback(this);
                }
            }

            internal void BindCancellation(CancellationToken token)
            {
                long version;
                lock (cancellationGate)
                {
                    Token = token;
                    version = ++cancellationVersion;
                }

                Cancellation = token.Register(() => ThreadPool.QueueUserWorkItem(_ => Cancel(version)));
                token.ThrowIfCancellationRequested();
            }

            private void Cancel(long version)
            {
                lock (closeGate)
                {
                    SftpClient close;
                    lock (cancellationGate)
                    {
                        if (version != cancellationVersion || !Token.IsCancellationRequested)
                            return;
                        close = Client;
                        Client = null;
                    }

                    SignalClosed();
                    if (close != null)
                        try
                        {
                            close.Dispose();
                        }
                        catch (global::System.Exception diagnosticFailure)
                        {
                            global::Backup.Core.RuntimeDiagnostics.Record("Backup.Repositories.Sftp/SftpDestination.cs:Cancel", diagnosticFailure);
                        }
                }
            }

            internal bool DetachCancellation()
            {
                CancellationTokenRegistration registration;
                bool canceled;
                lock (cancellationGate)
                {
                    canceled = Token.IsCancellationRequested;
                    Token = CancellationToken.None;
                    cancellationVersion++;
                    registration = Cancellation;
                    Cancellation = default(CancellationTokenRegistration);
                }

                registration.Dispose();
                return !canceled;
            }

            public void Dispose()
            {
                SignalClosed();
                DetachCancellation();
                lock (closeGate)
                {
                    SftpClient close;
                    lock (cancellationGate)
                    {
                        close = Client;
                        Client = null;
                    }

                    try
                    {
                        if (close != null)
                            close.Dispose();
                    }
                    finally
                    {
                        try
                        {
                            if (Authentication != null)
                            {
                                Authentication.Dispose();
                                Authentication = null;
                            }
                        }
                        finally
                        {
                            if (Key != null)
                            {
                                Key.Dispose();
                                Key = null;
                            }
                        }
                    }
                }
            }
        }

        private Session Connect(CancellationToken cancellation = default(CancellationToken), bool preserveReadConnection = false)
        {
            if (!cancellation.CanBeCanceled)
                cancellation = OperationCancellation;
            if (sessionOwner != null)
                return sessionOwner.Connect(cancellation, preserveReadConnection);
            lock (sessionGate)
            {
                if (disposed)
                    throw new ObjectDisposedException("SftpDestination");
                connecting++;
            }

            NotifyConnection();
            try
            {
                var trust = connection.ServerTrust;
                string key = RemoteServerIdentity.Endpoint("sftp", root.AbsoluteUri);
                trust.Ensure(key, token => ProbeServerIdentity(root, token), cancellation);
                try
                {
                    return ConnectVerified(cancellation, preserveReadConnection);
                }
                catch (RemoteServerTrustException error)
                {
                    trust.Ensure(key, _ => error.Notice.Identity, cancellation);
                    return ConnectVerified(cancellation, preserveReadConnection);
                }
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Repositories.Sftp/SftpDestination.cs:Connect", error);
                cancellation.ThrowIfCancellationRequested();
                var failure = error as IOException ?? Failure(error);
                lock (sessionGate)
                    connectionError = failure.Message;
                if (error is IOException)
                    throw;
                throw failure;
            }
            finally
            {
                lock (sessionGate)
                    connecting--;
                NotifyConnection();
            }
        }

        private Session ConnectVerified(CancellationToken cancellation, bool preserveReadConnection)
        {
            if (!cancellation.CanBeCanceled)
                cancellation = OperationCancellation;
            if (sessionOwner != null)
                return sessionOwner.Connect(cancellation, preserveReadConnection);
            cancellation.ThrowIfCancellationRequested();
            lock (sessionGate)
            {
                if (disposed)
                    throw new ObjectDisposedException("SftpDestination");
            }

            var session = new Session
            {
                Token = cancellation
            };
            RemoteServerTrustNotice identityFailure = null;
            try
            {
                AuthenticationMethod authentication;
                if (connection.AuthenticationMode == "private-key")
                {
                    try
                    {
                        using (var input = new FileStream(connection.PrivateKeyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                            session.Key = new PrivateKeyFile(input, connection.PrivateKeyPassphrase ?? "");
                    }
                    catch (Exception error)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Repositories.Sftp/SftpDestination.cs:ConnectVerified", error);
                        if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is SshException) && !(error is ArgumentException) &&
                            !(error is System.Security.Cryptography.CryptographicException))
                            throw;
                        throw new IOException("无法读取 SFTP 私钥，请检查文件格式和私钥口令。");
                    }

                    authentication = new PrivateKeyAuthenticationMethod(connection.Username, session.Key);
                }
                else
                    authentication = new PasswordAuthenticationMethod(connection.Username, connection.Password ?? "");
                session.Authentication = authentication as IDisposable;
                session.Client = new SftpClient(new ConnectionInfo(root.Host, root.IsDefaultPort || root.Port < 1 ? 22 : root.Port, connection.Username, authentication));
                session.Client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(RemoteConnectionPolicy.ConnectSeconds);
                session.Client.OperationTimeout = TimeSpan.FromSeconds(RemoteConnectionPolicy.OperationSeconds);
                session.Client.KeepAliveInterval = TimeSpan.FromSeconds(RemoteConnectionPolicy.KeepAliveSeconds);
                session.Client.BufferSize = 32 * 1024;
                session.Closed = SessionClosed;
                session.Client.ErrorOccurred += (sender, error) => SessionError(session, error.Exception);
                session.Client.HostKeyReceived += (sender, args) =>
                {
                    var identity = RemoteServerIdentity.Ssh(root, args.HostKey, args.HostKeyName);
                    connection.ServerFingerprint = identity.Fingerprint;
                    args.CanTrust = connection.ServerTrust.CheckPresented(identity);
                    if (!args.CanTrust)
                        identityFailure = connection.ServerTrust.Notice("sftp", root.AbsoluteUri);
                };
                if (cancellation.CanBeCanceled)
                    session.Client.ConnectAsync(cancellation).GetAwaiter().GetResult();
                else
                    session.Client.Connect();
                session.BindCancellation(preserveReadConnection ? readConnectionLifetime.Token : cancellation);
                cancellation.ThrowIfCancellationRequested();
                lock (sessionGate)
                {
                    connectedSessions.Add(session);
                    rentedSessions.Add(session);
                    connectionError = null;
                }

                NotifyConnection();
                return session;
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Repositories.Sftp/SftpDestination.cs:ConnectVerified", error);
                session.Dispose();
                cancellation.ThrowIfCancellationRequested();
                var failure = identityFailure != null 
                    ? new RemoteServerTrustException(identityFailure) 
                    : error is SshOperationTimeoutException ? new IOException("SFTP 建立连接超时（60 秒），请检查网络和服务器应答。", error) : error as IOException ??
                    Failure(error);
                lock (sessionGate)
                    connectionError = failure.Message;
                if (error is IOException && identityFailure == null)
                    throw;
                throw failure;
            }
        }

        public static string ProbeFingerprint(string location)
        {
            Uri uri = RemotePath.Root(location, "sftp");
            string fingerprint = null;
            using (var client = new SftpClient(uri.Host, uri.IsDefaultPort || uri.Port < 1 ? 22 : uri.Port, "fingerprint-probe", ""))
            {
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(10);
                client.HostKeyReceived += (s, e) =>
                {
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                        fingerprint = "SHA256:" + Convert.ToBase64String(sha.ComputeHash(e.HostKey)).TrimEnd('=');
                    e.CanTrust = false;
                };
                try
                {
                    client.Connect();
                }
                catch (SshException)
                { // Expected cancellation, shutdown, or a deliberately rejected identity probe.
                }

                if (fingerprint == null)
                    throw new IOException("无法读取 SFTP 主机指纹。");
                return fingerprint;
            }
        }

        private static RemoteServerIdentity ProbeServerIdentity(Uri uri, CancellationToken cancellation)
        {
            RemoteServerIdentity identity = null;
            using (var client = new SftpClient(uri.Host, uri.IsDefaultPort || uri.Port < 1 ? 22 : uri.Port, "fingerprint-probe", ""))
            {
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(RemoteConnectionPolicy.ConnectSeconds);
                client.HostKeyReceived += (sender, args) =>
                {
                    identity = RemoteServerIdentity.Ssh(uri, args.HostKey, args.HostKeyName);
                    args.CanTrust = false;
                };
                try
                {
                    client.ConnectAsync(cancellation).GetAwaiter().GetResult();
                }
                catch (SshException)
                {
                    if (identity == null)
                        throw;
                }
            }

            cancellation.ThrowIfCancellationRequested();
            if (identity == null)
                throw new IOException("无法读取 SFTP 主机指纹。");
            return identity;
        }

        private static IOException Failure(Exception error)
        {
            var closed = error as SshConnectionException;
            return new IOException(
                error is SshAuthenticationException 
                ? "SFTP 身份验证失败，请检查账号、密码或授权公钥。" 
                : error is SshOperationTimeoutException 
                ? "SFTP 连续 120 秒未收到操作应答。" 
                : closed != null ? "SFTP 连接中断：" +
                error.Message +
                "（" +
                closed.DisconnectReason +
                "）" : "SFTP 操作失败（" +
                error.GetType()
                .Name +
                "）：" +
                error.Message,
                error
            );
        }

        private Session Borrow()
        {
            bool reused;
            return Borrow(OperationCancellation, out reused);
        }

        private Session Borrow(CancellationToken cancellation, out bool reused, bool preserveReadConnection = false)
        {
            if (disposed)
                throw new ObjectDisposedException("SftpDestination");
            if (sessionOwner != null)
                return sessionOwner.Borrow(cancellation, out reused, preserveReadConnection);
            cancellation.ThrowIfCancellationRequested();
            Session session;
            long since;
            bool idlePeriod;
            lock (sessionGate)
            {
                if (disposed)
                    throw new ObjectDisposedException("SftpDestination");
                idlePeriod = rentedSessions.Count == 0;
                session = idleSessions.Take();
                since = idleSince;
                idleVersion++;
                if (session != null)
                    rentedSessions.Add(session);
                if (idleTimer != null)
                {
                    idleTimer.Dispose();
                    idleTimer = null;
                }
            }

            reused = false;
            if (session != null)
            {
                // The existing idle timeout starts when all rented sessions
                // return, not while another operation is still using the pool.
                if (session.Connected && (!idlePeriod || (Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency < idleTimeoutMilliseconds))
                {
                    try
                    {
                        session.BindCancellation(preserveReadConnection ? readConnectionLifetime.Token : cancellation);
                        reused = true;
                        return session;
                    }
                    catch (global::System.Exception diagnosticFailure)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Repositories.Sftp/SftpDestination.cs:Borrow", diagnosticFailure);
                        session.Dispose();
                        throw;
                    }
                }

                session.Dispose();
            }

            return Connect(cancellation, preserveReadConnection);
        }

        private void ScheduleIdle()
        {
            if (idleTimer != null)
            {
                idleTimer.Dispose();
                idleTimer = null;
            }

            idleVersion++;
            if (!disposed && idleSessions.Count > 0 && rentedSessions.Count == 0)
            {
                idleSince = Stopwatch.GetTimestamp();
                long version = idleVersion;
                idleTimer = new Timer(_ => ExpireSession(version), null, idleTimeoutMilliseconds, Timeout.Infinite);
            }
        }

        private void Release(Session session)
        {
            if (sessionOwner != null)
            {
                sessionOwner.Release(session);
                return;
            }

            Session previous = null;
            bool reusable = session.DetachCancellation(), keep = false;
            lock (sessionGate)
            {
                rentedSessions.Remove(session);
                if (reusable && !disposed && session.Connected)
                {
                    previous = idleSessions.Return(session);
                    connectionError = null;
                    keep = true;
                }

                ScheduleIdle();
            }

            if (previous != null && previous != session)
                previous.Dispose();
            if (!keep)
                session.Dispose();
            NotifyConnection();
        }

        private void SessionClosed(Session session)
        {
            lock (sessionGate)
            {
                connectedSessions.Remove(session);
                rentedSessions.Remove(session);
                idleSessions.Remove(session);
                ScheduleIdle();
            }

            NotifyConnection();
        }

        private void SessionError(Session session, Exception error)
        {
            if (session.IsClosing || session.Token.IsCancellationRequested || disposed)
                return;
            lock (sessionGate)
                connectionError = Failure(error).Message;
            NotifyConnection();
            ThreadPool.QueueUserWorkItem(_ => session.Dispose());
        }

        private void ExpireSession(long version = 0)
        {
            Session[] sessions;
            lock (sessionGate)
            {
                if (version != 0 && (version != idleVersion || rentedSessions.Count != 0))
                    return;
                sessions = idleSessions.Drain();
                idleVersion++;
                if (idleTimer != null)
                {
                    idleTimer.Dispose();
                    idleTimer = null;
                }
            }

            foreach (var session in sessions)
                session.Dispose();
            NotifyConnection();
        }

        public void Dispose()
        {
            lock (sessionGate)
            {
                if (disposed)
                    return;
                disposed = true;
            }

            if (sessionOwner == null)
            {
                readConnectionLifetime.Cancel();
                ExpireSession();
            }

            readConnectionLifetime.Dispose();
        }

        private T Use<T>(Func<SftpClient, T> operation)
        {
            return Use(operation, OperationCancellation, false);
        }

        private void RememberFailure(Exception error)
        {
            if (sessionOwner != null)
            {
                sessionOwner.RememberFailure(error);
                return;
            }

            if (!TransportFailure(error) && !(error is SshOperationTimeoutException))
                return;
            lock (sessionGate)
            {
                if (disposed)
                    return;
                connectionError = Failure(error).Message;
            }

            NotifyConnection();
        }

        private static bool TransportFailure(Exception error)
        {
            return error is SshConnectionException || error is System.Net.Sockets.SocketException || error is ObjectDisposedException ||
                error is IOException && error.InnerException != null &&
                TransportFailure(error.InnerException);
        }

        private T Use<T>(Func<SftpClient, T> operation, CancellationToken cancellation, bool reconnect, bool preserveReadConnection = false)
        {
            bool reused;
            var session = Borrow(cancellation, out reused, preserveReadConnection);
            try
            {
                var result = operation(session.Client);
                cancellation.ThrowIfCancellationRequested();
                return result;
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Repositories.Sftp/SftpDestination.cs:Use", error);
                if (preserveReadConnection && error is OperationCanceledException && cancellation.IsCancellationRequested && session.Connected)
                    throw;
                writePrepared = false;
                session.Dispose();
                session = null;
                cancellation.ThrowIfCancellationRequested();
                if (reconnect && reused && TransportFailure(error))
                    return Use(operation, cancellation, false, preserveReadConnection);
                RememberFailure(error);
                if (error is IOException)
                    throw;
                throw Failure(error);
            }
            finally
            {
                if (session != null)
                    Release(session);
            }
        }

        private void CheckRoot(SftpClient client, string directory, CancellationToken token = default(CancellationToken))
        {
            if (!token.CanBeCanceled)
                token = OperationCancellation;
            var attrs = client.GetAttributesAsync(directory, token).GetAwaiter().GetResult();
            if (!attrs.IsDirectory || attrs.IsSymbolicLink)
                throw new IOException("SFTP 目录不存在或为符号链接。");
        }

        public void CheckReadableRoot()
        {
            Use(client =>
            {
                CheckRoot(client, path);
                return true;
            }, OperationCancellation, true);
        }

        // A request view prepares ancestors once. Every actual read/write still checks its
        // directory and immutable-object/temporary ownership guards remain in place.
        public void PrepareWrite()
        {
            if (writePrepared)
                return;
            Use(client =>
            {
                string current = "";
                foreach (string part in path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    current += "/" + part;
                    if (!client.ExistsAsync(current, OperationCancellation).GetAwaiter().GetResult())
                        client.CreateDirectoryAsync(current, OperationCancellation).GetAwaiter().GetResult();
                    CheckRoot(client, current);
                }

                return true;
            });
            writePrepared = true;
        }

        public Stream OpenTemporary(string name)
        {
            PrepareWrite();
            var session = Borrow();
            try
            {
                var client = session.Client;
                var cancellation = session.Token;
                CheckRoot(client, path);
                if (client.ExistsAsync(Resolve(name), OperationCancellation).GetAwaiter().GetResult())
                    throw new IOException("拒绝覆盖已有备份对象。");
                string temporary = staging.Begin(name);
                try
                {
                    return BandwidthStream.Wrap(
                        new ResponseStream(
                        client.OpenAsync(Resolve(temporary), FileMode.CreateNew, FileAccess.Write, cancellation).GetAwaiter().GetResult(),
                        () => Release(session),
                        session.Dispose,
                        failure: error =>
                    {
                        writePrepared = false;
                        if (!cancellation.IsCancellationRequested)
                            RememberFailure(error);
                    },
                        ioToken: cancellation
                    ),
                        Bandwidth,
                        true,
                        cancellation
                    );
                }
                catch (SftpPermissionDeniedException)
                {
                    staging.Rejected(name);
                    throw;
                }
            }
            catch (Exception e)
            {
                writePrepared = false;
                session.Dispose();
                OperationCancellation.ThrowIfCancellationRequested();
                if (staging.HasActive(name))
                    try
                    {
                        staging.Delete(name, Delete);
                    }
                    catch (Exception cleanup)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Repositories.Sftp/SftpDestination.cs:OpenTemporary", cleanup);
                        throw new TransferReconciliationRequiredException("SFTP 临时上传状态待核对，所有权记录已保留。", cleanup);
                    }

                OperationCancellation.ThrowIfCancellationRequested();
                RememberFailure(e);
                if (e is IOException)
                    throw;
                throw Failure(e);
            }
        }

        public void CommitTemporary(string name)
        {
            Use(client =>
            {
                CheckRoot(client, path);
                if (client.ExistsAsync(Resolve(name), OperationCancellation).GetAwaiter().GetResult())
                    throw new BackupObjectConflictException();
                client.RenameFileAsync(Resolve(staging.Current(name)), Resolve(name), OperationCancellation).GetAwaiter().GetResult();
                return true;
            });
            directoryRefresh.InvalidateFolder(root.AbsoluteUri);
        }

        public void DeleteTemporary(string name)
        {
            staging.Delete(name, Delete);
        }

        public void DeleteObject(string name)
        {
            Delete(name);
            directoryRefresh.InvalidateFolder(root.AbsoluteUri);
        }

        private void Delete(string name)
        {
            Use(client =>
            {
                CheckRoot(client, path);
                if (client.ExistsAsync(Resolve(name), OperationCancellation).GetAwaiter().GetResult())
                {
                    var attrs = client.GetAttributesAsync(Resolve(name), OperationCancellation).GetAwaiter().GetResult();
                    if (attrs.IsSymbolicLink || attrs.IsDirectory)
                        throw new InvalidBackupObjectException("备份对象类型无效。");
                    client.DeleteFileAsync(Resolve(name), OperationCancellation).GetAwaiter().GetResult();
                }

                return true;
            });
        }

        public RemoteObjectInfo Stat(string name)
        {
            return Use(
                client =>
            {
                CheckRoot(client, path);
                try
                {
                    var attrs = client.GetAttributesAsync(Resolve(name), OperationCancellation).GetAwaiter().GetResult();
                    if (attrs.IsDirectory || attrs.IsSymbolicLink)
                        throw new InvalidBackupObjectException("备份对象类型无效。");
                    return new RemoteObjectInfo
                    {
                        Name = name,
                        Exists = true,
                        Size = attrs.Size
                    };
                }
                catch (SftpPathNotFoundException)
                {
                    return new RemoteObjectInfo
                    {
                        Name = name,
                        Exists = false
                    };
                }
            },
                OperationCancellation,
                true
            );
        }

        public Stream OpenRead(string name)
        {
            return OpenRead(name, true);
        }

        public DownloadRange OpenReadRange(string name, long offset, long count, long totalSize, CancellationToken cancellation)
        {
            bool reused;
            var session = Borrow(cancellation, out reused);
            try
            {
                var client = session.Client;
                CheckRoot(client, path, cancellation);
                var attrs = client.GetAttributesAsync(Resolve(name), cancellation).GetAwaiter().GetResult();
                if (attrs.IsDirectory || attrs.IsSymbolicLink)
                    throw new InvalidBackupObjectException("备份对象类型无效。");
                if (attrs.Size != totalSize || offset < 0 || count <= 0 || offset > totalSize - count)
                    throw new IOException("下载文件长度已变化。");
                var stream = client.OpenAsync(Resolve(name), FileMode.Open, FileAccess.Read, session.Token).GetAwaiter().GetResult();
                try
                {
                    stream.Seek(offset, SeekOrigin.Begin);
                }
                catch (global::System.Exception diagnosticFailure)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Repositories.Sftp/SftpDestination.cs:OpenReadRange", diagnosticFailure);
                    stream.Dispose();
                    throw;
                }

                return new DownloadRange(
                    new BoundedDownloadStream(
                    BandwidthStream.Wrap(
                    new ResponseStream(
                    stream,
                    () => Release(session),
                    session.Dispose,
                    failure: error =>
                {
                    if (!cancellation.IsCancellationRequested)
                        RememberFailure(error);
                },
                    ioToken: session.Token
                ),
                    Bandwidth,
                    false,
                    cancellation
                ),
                    count,
                    cancellation
                ),
                    true,
                    attrs.LastWriteTimeUtc.Ticks.ToString()
                );
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Repositories.Sftp/SftpDestination.cs:OpenReadRange", error);
                session.Dispose();
                cancellation.ThrowIfCancellationRequested();
                RememberFailure(error);
                if (error is IOException)
                    throw;
                throw Failure(error);
            }
        }

        private Stream OpenRead(string name, bool reconnect)
        {
            bool reused;
            var session = Borrow(OperationCancellation, out reused);
            try
            {
                var client = session.Client;
                var cancellation = session.Token;
                CheckRoot(client, path);
                var attrs = client.GetAttributesAsync(Resolve(name), OperationCancellation).GetAwaiter().GetResult();
                if (attrs.IsDirectory || attrs.IsSymbolicLink)
                    throw new InvalidBackupObjectException("备份对象类型无效。");
                return BandwidthStream.Wrap(
                    new ResponseStream(
                    client.OpenAsync(Resolve(name), FileMode.Open, FileAccess.Read, cancellation).GetAwaiter().GetResult(),
                    () => Release(session),
                    session.Dispose,
                    failure: error =>
                {
                    writePrepared = false;
                    if (!cancellation.IsCancellationRequested)
                        RememberFailure(error);
                },
                    ioToken: cancellation
                ),
                    Bandwidth,
                    false,
                    cancellation
                );
            }
            catch (Exception e)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Repositories.Sftp/SftpDestination.cs:OpenRead", e);
                writePrepared = false;
                session.Dispose();
                OperationCancellation.ThrowIfCancellationRequested();
                if (reconnect && reused && TransportFailure(e))
                    return OpenRead(name, false);
                RememberFailure(e);
                if (e is IOException)
                    throw;
                throw Failure(e);
            }
        }

        public IList<RemoteFolder> ListFolders()
        {
            return ListFolders(CancellationToken.None);
        }

        public IList<RemoteFolder> ListFolders(CancellationToken cancellation)
        {
            return ListEntriesAt(root.AbsoluteUri, cancellation).Where(item => item.Directory).Select(item => new RemoteFolder { Name = item.Name, Location = item.Location.TrimEnd('/') + "/" })
                .ToList();
        }

        public IList<StorageEntry> ListEntries()
        {
            return ListEntriesAt(root.AbsoluteUri, OperationCancellation);
        }

        public IList<StorageEntry> ListEntriesAt(string location, CancellationToken cancellation)
        {
            RepositoryModules.CheckDirectory("sftp", root.AbsoluteUri, location);
            return directoryRefresh.Read(directoryScope ?? Id, location, false, () => ReadEntriesAt(location, cancellation), cancellation);
        }

        private IList<StorageEntry> ReadEntriesAt(string location, CancellationToken cancellation)
        {
            var directory = RemotePath.Root(location, "sftp");
            if (directory.Authority != root.Authority || !directory.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.Ordinal))
                throw new IOException("仓库地址越界。");
            string folder = Uri.UnescapeDataString(directory.AbsolutePath).TrimEnd('/');
            if (folder.Length == 0)
                folder = "/";
            return Use<IList<StorageEntry>>(
                client =>
            {
                // Cancel the view between responses. Let an outstanding directory
                // request finish and close its handle without closing the SSH client.
                CheckRoot(client, folder, cancellation);
                var result = new List<StorageEntry>();
                var entries = client.ListDirectoryAsync(folder, CancellationToken.None).GetAsyncEnumerator(CancellationToken.None);
                try
                {
                    while (true)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var next = entries.MoveNextAsync();
                        if (!(next.IsCompleted ? next.GetAwaiter().GetResult() : next.AsTask().GetAwaiter().GetResult()))
                            break;
                        cancellation.ThrowIfCancellationRequested();
                        var item = entries.Current;
                        if (item.Name != "." && item.Name != "..")
                            result.Add(new StorageEntry {
                                Name = item.Name,
                                Location = RemotePath.Append(directory.AbsoluteUri, item.Name),
                                Directory = item.IsDirectory && !item.IsSymbolicLink,
                                Size = item.IsDirectory ? 0 : item.Length
                            });
                    }
                }
                finally
                {
                    entries.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                return result;
            },
                cancellation,
                true,
                true
            );
        }

        public void CreateFolder(string name)
        {
            Use(client =>
            {
                CheckRoot(client, path);
                client.CreateDirectoryAsync(Resolve(name), OperationCancellation).GetAwaiter().GetResult();
                return true;
            });
            directoryRefresh.InvalidateFolder(root.AbsoluteUri);
        }

        // Servers can collapse repeated separators during REALPATH without following a link.
        private static string ComparableSftpPath(string value)
        {
            return "/" + string.Join("/", value.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private Renci.SshNet.Sftp.ISftpFile DeletionEntry(SftpClient client, string name)
        {
            CheckRoot(client, path);
            var directory = client.GetAsync(path, OperationCancellation).GetAwaiter().GetResult();
            if (ComparableSftpPath(directory.FullName) != ComparableSftpPath(path))
                throw new IOException("不能通过符号链接删除仓库文件。");
            var item = client.GetAsync(Resolve(name), OperationCancellation).GetAwaiter().GetResult();
            if (item.IsSymbolicLink || ComparableSftpPath(item.FullName) != ComparableSftpPath(Resolve(name)))
                throw new IOException("不能删除符号链接。");
            return item;
        }

        public StorageEntry InspectEntry(string name)
        {
            return Use<StorageEntry>(client =>
            {
                try
                {
                    var item = DeletionEntry(client, name);
                    return new StorageEntry
                    {
                        Name = name,
                        Location = RemotePath.Append(root.AbsoluteUri, name),
                        Directory = item.IsDirectory,
                        Size = item.IsDirectory ? 0 : item.Length
                    };
                }
                catch (SftpPathNotFoundException)
                {
                    return null;
                }
            });
        }

        public void RemoveEntry(string name, bool directory)
        {
            Use(client =>
            {
                Renci.SshNet.Sftp.ISftpFile item;
                try
                {
                    item = DeletionEntry(client, name);
                }
                catch (SftpPathNotFoundException)
                {
                    return true;
                }

                if (item.IsDirectory != directory)
                    throw new IOException("文件类型已变化，请刷新后重试。");
                if (directory)
                    client.DeleteDirectoryAsync(Resolve(name), OperationCancellation).GetAwaiter().GetResult();
                else
                    client.DeleteFileAsync(Resolve(name), OperationCancellation).GetAwaiter().GetResult();
                return true;
            });
            directoryRefresh.InvalidateFolder(root.AbsoluteUri);
            if (directory)
                directoryRefresh.InvalidateFolder(RemotePath.Append(root.AbsoluteUri, name), true);
        }

        private List<Renci.SshNet.Sftp.ISftpFile> TaskDeleteEntries(SftpClient client, string directory)
        {
            var result = new List<Renci.SshNet.Sftp.ISftpFile>();
            var entries = client.ListDirectoryAsync(directory, OperationCancellation).GetAsyncEnumerator(OperationCancellation);
            try
            {
                while (entries.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                {
                    OperationCancellation.ThrowIfCancellationRequested();
                    if (entries.Current.Name != "." && entries.Current.Name != "..")
                        result.Add(entries.Current);
                }
            }
            finally
            {
                entries.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            return result;
        }

        private void DeleteTaskDirectory(SftpClient client, string directory, int depth)
        {
            if (depth > 64)
                throw new IOException("任务目录层数超过删除上限。");
            OperationCancellation.ThrowIfCancellationRequested();
            foreach (var entry in TaskDeleteEntries(client, directory))
                try
                {
                    string child = directory.TrimEnd('/') + "/" + RemotePath.Name(entry.Name);
                    if (entry.IsDirectory && !entry.IsSymbolicLink)
                        DeleteTaskDirectory(client, child, depth + 1);
                    else
                        client.DeleteFileAsync(child, OperationCancellation).GetAwaiter().GetResult();
                }
                catch (SftpPathNotFoundException diagnosticFailure)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Repositories.Sftp/SftpDestination.cs:DeleteTaskDirectory", diagnosticFailure);
                }

            try
            {
                client.DeleteDirectoryAsync(directory, OperationCancellation).GetAwaiter().GetResult();
            }
            catch (SftpPathNotFoundException diagnosticFailure)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Repositories.Sftp/SftpDestination.cs:DeleteTaskDirectory", diagnosticFailure);
            }
        }

        public void DeleteDirectoryTree(string name)
        {
            Use(client =>
            {
                CheckRoot(client, path);
                Renci.SshNet.Sftp.ISftpFile item;
                try
                {
                    item = client.GetAsync(Resolve(name), OperationCancellation).GetAwaiter().GetResult();
                }
                catch (SftpPathNotFoundException)
                {
                    return true;
                }

                if (item.IsSymbolicLink)
                    client.DeleteFileAsync(Resolve(name), OperationCancellation).GetAwaiter().GetResult();
                else
                {
                    if (!item.IsDirectory || ComparableSftpPath(item.FullName) != ComparableSftpPath(Resolve(name)))
                        throw new IOException("任务目录不是独立文件夹，已保留。");
                    DeleteTaskDirectory(client, Resolve(name), 0);
                }

                return true;
            });
            directoryRefresh.InvalidateFolder(root.AbsoluteUri);
            directoryRefresh.InvalidateFolder(RemotePath.Append(root.AbsoluteUri, name), true);
        }
    }
}
