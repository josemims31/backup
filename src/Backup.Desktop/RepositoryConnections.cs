using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        public const int RepositoryIdleSeconds = RemoteConnectionPolicy.IdleSeconds;
        private sealed class RepositoryProfileEntry : IDisposable
        {
            internal string Id, Protocol, Location, Credentials, Username, DirectoryScope;
            internal IRepositorySession Session;
            internal IOpenListRepositorySession OpenList;
            internal string State
            {
                get
                {
                    return Session.ConnectionState;
                }
            }

            internal string Error
            {
                get
                {
                    return Session.ConnectionError;
                }
            }

            public void Dispose()
            {
                if (Session != null)
                    Session.Dispose();
            }
        }

        private readonly object repositoryConnectionsGate = new object();
        private readonly Dictionary<string, RepositoryProfileEntry> repositoryConnections = new Dictionary<string, RepositoryProfileEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, RepositoryProfileEntry> repositoryProfiles = new Dictionary<string, RepositoryProfileEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> repositoryStates = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> repositoryErrors = new Dictionary<string, string>(StringComparer.Ordinal);
        private long repositoryConnectionsVersion;
        private bool repositoryConnectionsClosed;
        private RepositoryProfileEntry RepositoryProfile(DestinationDefinition destination, RemoteConnectionOptions options)
        {
            AttachServerTrust(options);
            if (destination.Protocol == "webdav")
                LoadSharedOpenListAuthorization(destination.Location, options);
            string identity;
            using (var hash = SHA256.Create())
                identity = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(JsonSettings.Encode(new RemoteConnectionOptions {
                    Username = options.Username,
                    Password = options.Password,
                    AuthenticationMode = options.AuthenticationMode,
                    PrivateKeyPath = options.PrivateKeyPath,
                    PrivateKeyPassphrase = options.PrivateKeyPassphrase,
                    OpenListUploadOptimization = options.OpenListUploadOptimization,
                    OpenListApiBase = options.OpenListApiBase,
                    OpenListToken = options.OpenListToken,
                    OpenListUsername = options.OpenListUsername
                }))));
            string key = destination.Id + "\n" + destination.Protocol + "\n" + destination.Location + "\n" + identity;
            lock (repositoryConnectionsGate)
            {
                if (repositoryConnectionsClosed)
                    throw new ObjectDisposedException("DesktopBackend");
                RepositoryProfileEntry entry;
                if (!repositoryProfiles.TryGetValue(key, out entry))
                {
                    entry = new RepositoryProfileEntry
                    {
                        Id = destination.Id,
                        Protocol = destination.Protocol,
                        Location = destination.Location,
                        Username = options.Username,
                        DirectoryScope = key
                    };
                    string id = destination.Id;
                    entry.Session = (IRepositorySession)RepositoryModules.Create(id, destination.Protocol, destination.Location, options);
                    entry.Session.ShareDirectoryRefresh(directoryRefresh, key);
                    var limited = entry.Session as IBandwidthDestination;
                    if (limited != null)
                        limited.Bandwidth = Bandwidth;
                    var owned = entry.Session as IOwnedUploadDestination;
                    if (owned != null)
                        owned.UploadJournal = Catalog;
                    entry.OpenList = entry.Session as IOpenListRepositorySession;
                    if (entry.OpenList != null)
                    {
                        entry.OpenList.RenewOpenListAuthorization = RenewRepairOpenListAuthorization;
                        entry.OpenList.OpenListWebDavFallback = () => OpenListFallbackActive(destination.Location, options.Username);
                        entry.OpenList.UploadDiagnostic += message => RuntimeLog.Append(BaseDirectory, "upload-errors.log", message);
                    }

                    entry.Session.ConnectionStateChanged += () => RepositoryStateChanged(id);
                    repositoryProfiles.Add(key, entry);
                }

                return entry;
            }
        }

        private RepositoryProfileEntry RepositoryConnection(DestinationDefinition destination)
        {
            while (true)
            {
                long version;
                lock (repositoryConnectionsGate)
                {
                    if (repositoryConnectionsClosed)
                        throw new ObjectDisposedException("DesktopBackend");
                    version = repositoryConnectionsVersion;
                }

                string credentials = Catalog.GetSetting("connection:" + destination.Id);
                RepositoryProfileEntry current;
                lock (repositoryConnectionsGate)
                {
                    if (version != repositoryConnectionsVersion)
                        continue;
                    if (!repositoryConnections.TryGetValue(destination.Id, out current) || current.Protocol != destination.Protocol || current.Location != destination.Location ||
                        current.Credentials != credentials)
                        current = null;
                }

                if (current != null)
                {
                    if (current.OpenList != null)
                    {
                        var shared = new RemoteConnectionOptions
                        {
                            Username = current.Username
                        };
                        LoadSharedOpenListAuthorization(destination.Location, shared);
                        current.OpenList.UseOpenListAuthorization(shared);
                    }

                    return current;
                }

                RemoteConnectionOptions options;
                try
                {
                    options = RepositoryModules.Require(destination.Protocol).Info.Authentication == RepositoryAuthentication.None ? new RemoteConnectionOptions() : UnprotectConnection(credentials);
                }
                catch (CryptographicException)
                {
                    RepositoryFailure(destination.Id);
                    throw new RemoteCredentialsUnavailableException(ConnectionReference(destination.Id));
                }
                catch (FormatException)
                {
                    RepositoryFailure(destination.Id);
                    throw new RemoteCredentialsUnavailableException(ConnectionReference(destination.Id));
                }

                lock (repositoryConnectionsGate)
                {
                    if (version != repositoryConnectionsVersion)
                        continue;
                    var profile = RepositoryProfile(destination, options);
                    profile.Credentials = credentials;
                    repositoryConnections[destination.Id] = profile;
                    return profile;
                }
            }
        }

        private IManagedBackupDestination RepositoryDirectory(DestinationDefinition destination, string folder, System.Threading.CancellationToken cancellation, RepositoryProfileEntry profile = null)
        {
            cancellation.ThrowIfCancellationRequested();
            RepositoryModules.CheckDirectory(destination.Protocol, destination.Location, folder);
            return (profile ?? RepositoryConnection(destination)).Session.ForDirectory(folder, cancellation);
        }

        private string RepositoryStateUnsafe(string id)
        {
            var profiles = repositoryProfiles.Values.Where(entry => entry.Id == id).ToList();
            if (profiles.Any(entry => entry.State == "已连接"))
                return "已连接";
            if (profiles.Any(entry => entry.State == "正在连接"))
                return "正在连接";
            if (profiles.Any(entry => entry.State == "连接失败"))
                return "连接失败";
            string state;
            return repositoryStates.TryGetValue(id, out state) ? state : "空闲";
        }

        public string RepositoryConnectionState(string id)
        {
            var trust = TrustNotice(id);
            if (trust != null)
                return trust.State;
            if (OpenListFallbackFor(id) != null)
                return "无openlist优化";
            lock (repositoryConnectionsGate)
                return RepositoryStateUnsafe(id);
        }

        public string RepositoryConnectionError(string id)
        {
            var trust = TrustNotice(id);
            if (trust != null)
                return trust.Details;
            var notice = OpenListFallbackFor(id);
            if (notice != null)
                return "OpenList 授权待恢复，当前使用普通 WebDAV。" + notice.Reason;
            lock (repositoryConnectionsGate)
            {
                string error;
                return repositoryErrors.TryGetValue(id, out error) ? error : null;
            }
        }

        internal IDictionary<string, Tuple<string, string>> RepositoryConnectionSnapshot(StorageTopology topology)
        {
            var fallback = OpenListFallbackSnapshot(topology);
            var result = new Dictionary<string, Tuple<string, string>>(StringComparer.Ordinal);
            lock (repositoryConnectionsGate)
                foreach (var definition in topology.Destinations)
                {
                    OpenListFallbackNotice notice;
                    string error;
                    var trust = ServerTrust.Notice(definition.Protocol, definition.Location);
                    if (trust != null)
                        result[definition.Id] = Tuple.Create(trust.State, trust.Details);
                    else if (fallback.TryGetValue(definition.Id, out notice))
                        result[definition.Id] = Tuple.Create("无openlist优化", "OpenList 授权待恢复，当前使用普通 WebDAV。" + notice.Reason);
                    else
                    {
                        repositoryErrors.TryGetValue(definition.Id, out error);
                        result[definition.Id] = Tuple.Create(RepositoryStateUnsafe(definition.Id), error);
                    }
                }

            return result;
        }

        private void RepositoryFailure(string id)
        {
            lock (repositoryConnectionsGate)
            {
                repositoryStates[id] = "连接失败";
                repositoryErrors[id] = "仓库凭据无法由当前 Windows 用户解密，请重新填写账号、密码或私钥口令。";
            }

            RepositoryConnectionChanged(id);
        }

        private void RepositoryStateChanged(string id)
        {
            string diagnostic = null;
            lock (repositoryConnectionsGate)
            {
                var profiles = repositoryProfiles.Values.Where(entry => entry.Id == id).ToList();
                string state = profiles.Any(entry => entry.State == "已连接") ? "已连接" : profiles.Any(entry => entry.State == "正在连接") ? "正在连接" : profiles.Any(entry => entry.State == "连接失败") ? "连接失败" : "空闲";
                string error = state == "连接失败" ? profiles.Where(entry => entry.State == "连接失败").Select(entry => entry.Error).FirstOrDefault(value => !string.IsNullOrEmpty(value)) : null;
                string old, previous;
                repositoryErrors.TryGetValue(id, out previous);
                if (repositoryStates.TryGetValue(id, out old) && old == state && error == previous)
                    return;
                repositoryStates[id] = state;
                if (error == null)
                    repositoryErrors.Remove(id);
                else
                    repositoryErrors[id] = error;
                if (state == "连接失败" && !string.IsNullOrEmpty(error))
                    diagnostic = id + " · " + error;
            }

            RepositoryConnectionChanged(id);
            if (diagnostic != null)
                RuntimeLog.Append(BaseDirectory, "repository-connections.log", diagnostic);
        }

        private void InvalidateRepositoryConnections(string id = null, bool close = false, StorageTopology topology = null, RepositoryProfileEntry keep = null)
        {
            List<RepositoryProfileEntry> removed;
            List<string> changed;
            lock (repositoryConnectionsGate)
            {
                repositoryConnectionsVersion++;
                if (close)
                    repositoryConnectionsClosed = true;
                Func<RepositoryProfileEntry, bool> remove = entry => entry != keep &&
                    (id != null ? entry.Id == id : topology == null ||
                    !topology.Destinations.Any(d => d.Id == entry.Id &&
                    d.Protocol == entry.Protocol &&
                    d.Location == entry.Location));
                var keys = repositoryProfiles.Where(pair => remove(pair.Value)).Select(pair => pair.Key).ToList();
                removed = keys.Select(key => repositoryProfiles[key]).ToList();
                foreach (string key in keys)
                    repositoryProfiles.Remove(key);
                changed = removed.Select(entry => entry.Id).Distinct().ToList();
                changed.AddRange(repositoryStates.Keys.Where(key => (id == null || key == id) && !repositoryProfiles.Values.Any(entry => entry.Id == key)).Except(changed).ToList());
                foreach (string key in repositoryConnections.Where(pair => remove(pair.Value)).Select(pair => pair.Key).ToList())
                {
                    repositoryConnections.Remove(key);
                    if (!changed.Contains(key))
                        changed.Add(key);
                }

                foreach (string key in changed)
                {
                    repositoryStates.Remove(key);
                    repositoryErrors.Remove(key);
                }
            }

            foreach (var entry in removed)
                entry.Dispose();
            foreach (string key in changed)
                RepositoryStateChanged(key);
        }
    }
}
