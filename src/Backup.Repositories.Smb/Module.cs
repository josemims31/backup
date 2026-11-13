using System;
using Backup.Core;

namespace Backup.Repositories.Smb
{
    public sealed class Module : IRepositoryModule
    {
        public int ApiVersion
        {
            get
            {
                return RepositoryModules.CurrentApiVersion;
            }
        }

        private static readonly RepositoryModuleInfo description = new RepositoryModuleInfo("smb", "SMB", RepositoryPathKind.FileSystem, RepositoryAuthentication.None);
        public RepositoryModuleInfo Info
        {
            get
            {
                return description;
            }
        }

        public IManagedBackupDestination Create(string id, string location, RemoteConnectionOptions options)
        {
            return new SmbDirectoryDestination(id, location);
        }
    }
}
