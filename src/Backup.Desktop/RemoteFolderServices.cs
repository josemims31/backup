using System;
using System.Threading;
using System.Threading.Tasks;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed class RemoteFolderServices
    {
        public Func<string, string, bool, RemoteConnectionOptions, CancellationToken, IRemoteFolderBrowser> Open;
        public Func<Func<Task>, Task> Mutate;
        public Func<CancellationTokenSource, IDisposable> Read;
        public Action<string, string, bool, RemoteConnectionOptions, string> Created;
    }

    public sealed partial class DesktopBackend
    {
        public IRemoteFolderBrowser OpenConnectionTestBrowser(string id, string url, bool sftp, RemoteConnectionOptions options, CancellationToken cancellation)
        {
            AttachServerTrust(options);
            ServerTrust.BeginAttempt();
            if (id == null)
                return null;
            var destination = Catalog.LoadTopology().Destinations.Find(d => d.Id == id && !d.Retired);
            if (destination == null || destination.Protocol != (sftp ? "sftp" : "webdav"))
                return null;
            var root = RemotePath.Root(destination.Location, sftp ? "sftp" : "http");
            var next = RemotePath.Root(url, sftp ? "sftp" : "http");
            if (!root.IsBaseOf(next))
                return null;
            try
            {
                if (!RemoteConnectionPolicy.SameCredentials(LoadConnection(id), options))
                    return null;
            }
            catch (RemoteCredentialsUnavailableException)
            {
                return null;
            }

            return (IRemoteFolderBrowser)RepositoryDirectory(destination, url, cancellation);
        }
    }

    public sealed partial class MainWindowController
    {
        private RemoteFolderServices ConnectionBrowserServices
        {
            get
            {
                return new RemoteFolderServices
                {
                    Open = Backend.OpenConnectionTestBrowser,
                    Mutate = work => RunOperationAsync(OperationKind.Configuration, work),
                    Read = cancellation => operations.Read(OperationKind.Browse, cancellation),
                    Created = Backend.ReportRemoteFolderCreated
                };
            }
        }
    }
}
