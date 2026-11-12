using System;
using System.Linq;
using System.Threading;
using Backup.Core;
namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        public RemoteServerTrust ServerTrust{get;private set;}
        public Func<RemoteServerTrustNotice,CancellationToken,bool> ServerTrustPrompt;
        private void InitializeServerTrust()
        {
            if(ServerTrust!=null)ServerTrust.Dispose();
            RemoteServerTrustState state;
            try{state=JsonSettings.Decode(Catalog.GetSetting("server_trust_v1"),new RemoteServerTrustState());}
            catch(System.Runtime.Serialization.SerializationException){state=new RemoteServerTrustState();}
            ServerTrust=new RemoteServerTrust(state,value=>Catalog.SetSetting("server_trust_v1",JsonSettings.Encode(value)));
            ServerTrust.Prompt=(notice,token)=>{var handler=ServerTrustPrompt;return handler!=null&&handler(notice,token);};
            ServerTrust.Changed+=key=>{
                foreach(var destination in Catalog.LoadTopology().Destinations.Where(item=>RemoteServerIdentity.Endpoint(item.Protocol,item.Location)==key))RepositoryConnectionChanged(destination.Id);
            };
        }
        private RemoteConnectionOptions AttachServerTrust(RemoteConnectionOptions options)
        {options.ServerTrust=ServerTrust;return options;}
        private RemoteServerTrustNotice TrustNotice(string id)
        {var destination=Catalog.LoadTopology().Destinations.FirstOrDefault(item=>item.Id==id);return destination==null?null:ServerTrust.Notice(destination.Protocol,destination.Location);}
        internal static bool IsServerTrustState(string state){return state!=null&&(state.StartsWith("指纹",StringComparison.Ordinal)||state.StartsWith("证书",StringComparison.Ordinal));}
    }
}
