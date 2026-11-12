using System;
using System.Text;
using System.Linq;
using System.Security.Cryptography;
using Backup.Core;
namespace Backup.Desktop
{
    public sealed class RemoteCredentialsUnavailableException:System.IO.IOException {public RemoteConnectionOptions Reference;public RemoteCredentialsUnavailableException(RemoteConnectionOptions value):base("远程凭据无法由当前 Windows 用户解密，请重新填写账号或私钥口令；已保留私钥路径。"){Reference=value;}}
    public sealed partial class DesktopBackend
    {
        public RemoteConnectionOptions LoadConnection(string id)
        {
            string value=Catalog.GetSetting("connection:"+id);if(string.IsNullOrEmpty(value))return AttachServerTrust(new RemoteConnectionOptions());
            try{return AttachServerTrust(JsonSettings.Decode(Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value),null,DataProtectionScope.CurrentUser)),new RemoteConnectionOptions()));}
            catch(CryptographicException){throw new RemoteCredentialsUnavailableException(ConnectionReference(id));}
            catch(FormatException){throw new RemoteCredentialsUnavailableException(ConnectionReference(id));}
        }
        public static string ProtectConnection(RemoteConnectionOptions options){return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonSettings.Encode(options)),null,DataProtectionScope.CurrentUser));}
        public static RemoteConnectionOptions UnprotectConnection(string value){return string.IsNullOrEmpty(value)?new RemoteConnectionOptions():JsonSettings.Decode(Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value),null,DataProtectionScope.CurrentUser)),new RemoteConnectionOptions());}
        public static string EncodeConnectionReference(RemoteConnectionOptions options){return JsonSettings.Encode(new RemoteConnectionOptions{Username=options.Username,AuthenticationMode=options.AuthenticationMode,PrivateKeyPath=options.PrivateKeyPath});}
        private void MigrateConnectionReferences(){foreach(var d in Catalog.LoadTopology().Destinations.Where(d=>d.Protocol=="sftp"))if(Catalog.GetSetting("connection_reference:"+d.Id)==null)try{Catalog.SetSetting("connection_reference:"+d.Id,EncodeConnectionReference(LoadConnection(d.Id)));}catch(System.IO.IOException){}}
        public RemoteConnectionOptions ConnectionReference(string id){return JsonSettings.Decode(Catalog.GetSetting("connection_reference:"+id),new RemoteConnectionOptions());}
        public void SaveConnection(string id,RemoteConnectionOptions options){string previous=Catalog.GetSetting("connection:"+id);Catalog.SaveConfiguration(ConnectionConfiguration(id,options));if(previous!=Catalog.GetSetting("connection:"+id))InvalidateRepositoryConnections(id);}
        public string ObjectLocation(DestinationDefinition destination,string name){string owner=BackupObjectNaming.ObjectName(name)??name;string folder=ObjectStoragePath.Absolute(destination,Catalog.ObjectStorageDirectory(destination.Id,owner)??"");return RepositoryModules.ChildLocation(destination,folder,name);}
    }
}
