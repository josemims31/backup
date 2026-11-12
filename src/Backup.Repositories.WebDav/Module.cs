using System;
using Backup.Core;
namespace Backup.Repositories.WebDav
{
    public sealed class Module:IRepositoryModule,IOpenListRepositoryModule
    {
        public int ApiVersion{get{return RepositoryModules.CurrentApiVersion;}}
        private static readonly RepositoryModuleInfo description=new RepositoryModuleInfo("webdav","WebDAV",RepositoryPathKind.Uri,RepositoryAuthentication.Password,"http","https");
        public RepositoryModuleInfo Info{get{return description;}}
        public IManagedBackupDestination Create(string id,string location,RemoteConnectionOptions options){return new WebDavDestination(id,location,options,TimeSpan.FromSeconds(RemoteConnectionPolicy.OperationSeconds),TimeSpan.FromSeconds(RemoteConnectionPolicy.IdleSeconds));}

        public string ServiceAddress(string location){return WebDavDestination.OpenListServiceAddress(location);}
        public bool Authorize(string location,RemoteConnectionOptions options,string otp,System.Threading.CancellationToken cancellation){return WebDavDestination.AuthorizeOpenList(location,options,otp,cancellation);}
        public void ValidateAuthorization(string location,RemoteConnectionOptions options){WebDavDestination.ValidateCachedOpenListAuthorization(location,options);}
    }
}
