using System;
using Backup.Core;
namespace Backup.Repositories.Sftp
{
    public sealed class Module:IRepositoryModule
    {
        public int ApiVersion{get{return RepositoryModules.CurrentApiVersion;}}
        private static readonly RepositoryModuleInfo description=new RepositoryModuleInfo("sftp","SFTP",RepositoryPathKind.Uri,RepositoryAuthentication.Ssh,"sftp");
        public RepositoryModuleInfo Info{get{return description;}}
        public IManagedBackupDestination Create(string id,string location,RemoteConnectionOptions options){return new SftpDestination(id,location,options,TimeSpan.FromSeconds(RemoteConnectionPolicy.IdleSeconds));}
    }
}
