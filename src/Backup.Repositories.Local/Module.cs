using System;
using Backup.Core;
namespace Backup.Repositories.Local
{
    public sealed class Module:IRepositoryModule
    {
        public int ApiVersion{get{return RepositoryModules.CurrentApiVersion;}}
        private static readonly RepositoryModuleInfo description=new RepositoryModuleInfo("local","本地",RepositoryPathKind.FileSystem,RepositoryAuthentication.None);
        public RepositoryModuleInfo Info{get{return description;}}
        public IManagedBackupDestination Create(string id,string location,RemoteConnectionOptions options){return new LocalDirectoryDestination(id,location);}
    }
}
