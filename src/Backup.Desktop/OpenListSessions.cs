using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Backup.Core;
namespace Backup.Desktop
{
    public sealed class OpenListAuthorizationServices
    {public Action<string,RemoteConnectionOptions> Load;public Action<RemoteConnectionOptions> Save;}
    public sealed partial class MainWindowController
    {private OpenListAuthorizationServices OpenListServices{get{return new OpenListAuthorizationServices{Load=Backend.LoadSharedOpenListAuthorization,Save=Backend.SaveSharedOpenListAuthorization};}}}
    public sealed partial class DesktopBackend
    {
        private sealed class SharedOpenListSession{internal string Encrypted;internal RemoteConnectionOptions Options;}
        private readonly object openListSessionsGate=new object();
        private readonly Dictionary<string,SharedOpenListSession> openListSessions=new Dictionary<string,SharedOpenListSession>(StringComparer.Ordinal);
        private readonly object repairAuthorizationGate=new object();
        private readonly HashSet<string> repairAuthorizations=new HashSet<string>(StringComparer.Ordinal);
        private Func<string,RemoteConnectionOptions,bool> repairOpenListAuthorization;
        internal Func<string,RemoteConnectionOptions,bool> RepairOpenListAuthorization
        {set{lock(repairAuthorizationGate){repairOpenListAuthorization=value;repairAuthorizations.Clear();}}}
        private bool RenewRepairOpenListAuthorization(string location,RemoteConnectionOptions options)
        {
            return RequestOpenListAuthorization(location,options);
        }
        private static string OpenListSessionKey(string apiBase,string username)
        {using(var hash=SHA256.Create())return "openlist_authorization_v1:"+BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(apiBase+"\0"+username))).Replace("-","").ToLowerInvariant();}
        private static bool SameOpenListAccount(RemoteConnectionOptions value,string apiBase,string username)
        {return value!=null&&!string.IsNullOrEmpty(value.OpenListToken)&&value.OpenListApiBase==apiBase&&value.OpenListUsername==username;}
        public void LoadSharedOpenListAuthorization(string location,RemoteConnectionOptions options)
        {
            AttachServerTrust(options);
            string apiBase=OpenListAuthorization.OpenListServiceAddress(location);if(apiBase==null||string.IsNullOrEmpty(options.Username))return;
            string key=OpenListSessionKey(apiBase,options.Username),encrypted=Catalog.GetSetting(key);RemoteConnectionOptions session=null;
            lock(openListSessionsGate){SharedOpenListSession cached;if(openListSessions.TryGetValue(key,out cached)&&cached.Encrypted==encrypted)session=cached.Options;
                else{
                    if(encrypted!=null)try{session=UnprotectConnection(encrypted);}catch(CryptographicException){}catch(FormatException){}
                    // Reuse an already authorized 0.14.51 warehouse without logging
                    // in or connecting to any of the other repository folders.
                    if(encrypted==null)foreach(var destination in Catalog.LoadTopology().Destinations.Where(d=>d.Protocol=="webdav")){
                        string value=Catalog.GetSetting("connection:"+destination.Id);try{var candidate=UnprotectConnection(value);if(SameOpenListAccount(candidate,apiBase,options.Username)){session=candidate;break;}}catch(CryptographicException){}catch(FormatException){}
                    }
                    openListSessions[key]=new SharedOpenListSession{Encrypted=encrypted,Options=session};
                }
                if(!SameOpenListAccount(session,apiBase,options.Username))return;
                options.OpenListApiBase=apiBase;options.OpenListUsername=options.Username;options.OpenListToken=session.OpenListToken;
            }
        }
        private KeyValuePair<string,string>? SharedOpenListAuthorizationSetting(RemoteConnectionOptions options)
        {
            if(string.IsNullOrEmpty(options.OpenListApiBase)||!SameOpenListAccount(options,options.OpenListApiBase,options.Username))return null;
            string key=OpenListSessionKey(options.OpenListApiBase,options.Username),old=Catalog.GetSetting(key);
            if(old!=null)try{var saved=UnprotectConnection(old);if(SameOpenListAccount(saved,options.OpenListApiBase,options.Username)&&saved.OpenListToken==options.OpenListToken)return null;}catch(CryptographicException){}catch(FormatException){}
            var session=new RemoteConnectionOptions{Username=options.Username,OpenListUsername=options.Username,OpenListApiBase=options.OpenListApiBase,OpenListToken=options.OpenListToken};
            return new KeyValuePair<string,string>(key,ProtectConnection(session));
        }
        public void SaveSharedOpenListAuthorization(RemoteConnectionOptions options)
        {var value=SharedOpenListAuthorizationSetting(options);if(value.HasValue)Catalog.SaveConfiguration(new[]{value.Value});ClearOpenListFallback(options);}
    }
}
