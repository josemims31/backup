using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Backup.Core
{
    public sealed class RemoteServerIdentity
    {
        public string Kind,Host,Fingerprint,Algorithm,Subject,Issuer,Errors;
        public int Port,PolicyErrors;public long ChainErrors;
        public DateTime NotBefore=DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc),NotAfter=DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc);
        public string Key{get{return Kind+"\n"+Host+"\n"+Port;}}
        public string Address{get{return (Kind=="ssh"?"sftp":"https")+"://"+(Host.Contains(":")?"["+Host+"]":Host)+":"+Port;}}
        public static string Endpoint(string protocol,string location)
        {Uri uri;if(protocol!="sftp"&&protocol!="webdav"||!Uri.TryCreate(location,UriKind.Absolute,out uri)||protocol!="sftp"&&uri.Scheme!="https")return null;return (protocol=="sftp"?"ssh":"tls")+"\n"+uri.IdnHost.TrimEnd('.').ToLowerInvariant()+"\n"+(protocol=="sftp"&&(uri.Port<1||uri.IsDefaultPort)?22:uri.Port);}
        public static RemoteServerIdentity Ssh(Uri uri,byte[] key,string algorithm)
        {return new RemoteServerIdentity{Kind="ssh",Host=uri.IdnHost.TrimEnd('.').ToLowerInvariant(),Port=uri.Port<1||uri.IsDefaultPort?22:uri.Port,Fingerprint=Hash(key),Algorithm=algorithm??"SSH",Errors="首次连接需要核对服务器主机指纹。"};}
        public static RemoteServerIdentity Tls(Uri uri,X509Certificate certificate,X509Chain chain,SslPolicyErrors errors)
        {
            if(certificate==null)throw new IOException("服务器未提供 TLS 证书，无法验证身份。");
            using(var cert=new X509Certificate2(certificate)){
                long flags=0;if(chain!=null)foreach(var status in chain.ChainStatus)flags|=(uint)status.Status;
                var reasons=new List<string>();
                if((errors&SslPolicyErrors.RemoteCertificateNameMismatch)!=0)reasons.Add("证书域名与服务器地址不匹配");
                if((flags&(long)X509ChainStatusFlags.NotTimeValid)!=0)reasons.Add(DateTime.UtcNow>cert.NotAfter.ToUniversalTime()?"证书已过期":"证书尚未生效");
                if((flags&(long)X509ChainStatusFlags.UntrustedRoot)!=0)reasons.Add(cert.Subject==cert.Issuer?"自签名证书，签发者未受系统信任":"证书签发者未受系统信任");
                if((flags&(long)X509ChainStatusFlags.PartialChain)!=0)reasons.Add("证书链不完整");
                long other=flags&~((long)X509ChainStatusFlags.NotTimeValid|(long)X509ChainStatusFlags.UntrustedRoot|(long)X509ChainStatusFlags.PartialChain);if(other!=0)reasons.Add("证书链验证异常："+((X509ChainStatusFlags)other));
                if(errors!=SslPolicyErrors.None&&reasons.Count==0)reasons.Add("证书验证异常："+errors);
                return new RemoteServerIdentity{Kind="tls",Host=uri.IdnHost.TrimEnd('.').ToLowerInvariant(),Port=uri.Port,Fingerprint=Hash(cert.RawData),Algorithm=cert.PublicKey.Oid.FriendlyName,Subject=cert.Subject,Issuer=cert.Issuer,NotBefore=cert.NotBefore.ToUniversalTime(),NotAfter=cert.NotAfter.ToUniversalTime(),PolicyErrors=(int)errors,ChainErrors=flags,Errors=string.Join("；",reasons)};
            }
        }
        private static string Hash(byte[] bytes){using(var sha=SHA256.Create())return "SHA256:"+Convert.ToBase64String(sha.ComputeHash(bytes)).TrimEnd('=');}
    }
    public sealed class RemoteServerTrustNotice
    {
        public RemoteServerIdentity Identity,Previous;
        public bool Changed{get{return Previous!=null&&Previous.Fingerprint!=Identity.Fingerprint;}}
        public string State{get{return Identity.Kind=="ssh"?(Changed?"指纹已变化，等待确认":"指纹待确认"):(Changed?"证书已变化，等待确认":"证书待确认");}}
        public string Details{get{return Identity.Address+"\n"+State+(Previous==null?"":"\n原指纹："+Previous.Fingerprint)+"\n当前指纹："+Identity.Fingerprint+(string.IsNullOrEmpty(Identity.Errors)?"":"\n"+Identity.Errors);}}
    }
    public sealed class RemoteServerTrustState
    {public List<RemoteServerIdentity> Trusted=new List<RemoteServerIdentity>();public List<RemoteServerTrustNotice> Pending=new List<RemoteServerTrustNotice>();}
    public sealed class RemoteServerTrustException:IOException
    {public readonly RemoteServerTrustNotice Notice;public RemoteServerTrustException(RemoteServerTrustNotice notice):base(notice.Details){Notice=notice;}}

    // One local trust store and one decision flight per server, shared by all
    // repository folders. Never retain account passwords or accept observed keys.
    public sealed class RemoteServerTrust:IDisposable
    {
        private sealed class Flight{internal Task Task;internal CancellationTokenSource Cancellation;internal int Waiters;internal bool CleanupScheduled;}
        private sealed class Entry{internal bool Verified,Declined;internal Flight Flight;}
        private readonly object gate=new object(),saveGate=new object();
        private readonly Dictionary<string,Entry> entries=new Dictionary<string,Entry>(StringComparer.Ordinal);
        private readonly Dictionary<string,RemoteServerIdentity> trusted=new Dictionary<string,RemoteServerIdentity>(StringComparer.Ordinal);
        private readonly Dictionary<string,RemoteServerTrustNotice> pending=new Dictionary<string,RemoteServerTrustNotice>(StringComparer.Ordinal);
        private readonly CancellationTokenSource lifetime=new CancellationTokenSource();private readonly Action<RemoteServerTrustState> save;private bool disposed;
        public Func<RemoteServerTrustNotice,CancellationToken,bool> Prompt;
        public event Action<string> Changed;
        public RemoteServerTrust(RemoteServerTrustState state=null,Action<RemoteServerTrustState> persist=null)
        {save=persist;if(state!=null){foreach(var item in state.Trusted??new List<RemoteServerIdentity>())if(Valid(item))trusted[item.Key]=item;foreach(var item in state.Pending??new List<RemoteServerTrustNotice>())if(item!=null&&Valid(item.Identity))pending[item.Identity.Key]=item;}}
        private static bool Valid(RemoteServerIdentity item){return item!=null&&(item.Kind=="tls"||item.Kind=="ssh")&&!string.IsNullOrEmpty(item.Host)&&item.Port>0&&item.Port<=65535&&!string.IsNullOrEmpty(item.Fingerprint);}
        private Entry GetEntry(string key){Entry entry;if(!entries.TryGetValue(key,out entry)){entry=new Entry();entries.Add(key,entry);}return entry;}
        public RemoteServerTrustNotice Notice(string protocol,string location){string key=RemoteServerIdentity.Endpoint(protocol,location);lock(gate){RemoteServerTrustNotice value;return key!=null&&pending.TryGetValue(key,out value)?value:null;}}
        public void BeginAttempt(){lock(gate)foreach(var entry in entries.Values)if(entry.Flight==null)entry.Declined=false;}
        public RemoteServerTrustState Snapshot(){lock(gate)return new RemoteServerTrustState{Trusted=trusted.Values.ToList(),Pending=pending.Values.ToList()};}
        private void Publish(string key)
        {lock(saveGate){if(save!=null&&!disposed)save(Snapshot());}var handler=Changed;if(handler!=null)handler(key);}
        private bool Acceptable(RemoteServerIdentity identity)
        {
            RemoteServerIdentity pin;
            if(!trusted.TryGetValue(identity.Key,out pin))return identity.Kind=="tls"&&identity.PolicyErrors==0;
            return pin.Fingerprint==identity.Fingerprint&&(identity.Kind=="ssh"||(identity.PolicyErrors&~pin.PolicyErrors)==0&&(identity.ChainErrors&~pin.ChainErrors)==0&&(DateTime.UtcNow<=identity.NotAfter||(pin.ChainErrors&(long)X509ChainStatusFlags.NotTimeValid)!=0));
        }
        private RemoteServerTrustNotice Observe(RemoteServerIdentity identity)
        {RemoteServerIdentity prior;trusted.TryGetValue(identity.Key,out prior);var notice=new RemoteServerTrustNotice{Identity=identity,Previous=prior};pending[identity.Key]=notice;GetEntry(identity.Key).Verified=false;return notice;}
        public bool CheckPresented(RemoteServerIdentity identity)
        {
            bool accepted,changed=false;lock(gate){if(disposed)return false;accepted=Acceptable(identity);if(!accepted){RemoteServerTrustNotice prior;changed=!pending.TryGetValue(identity.Key,out prior)||prior.Identity.Fingerprint!=identity.Fingerprint||prior.Identity.PolicyErrors!=identity.PolicyErrors||prior.Identity.ChainErrors!=identity.ChainErrors;Observe(identity);}}
            if(changed)Publish(identity.Key);return accepted;
        }
        public void ObserveAsync(RemoteServerIdentity identity,CancellationToken cancellation)
        {Task.Run(()=>{try{Ensure(identity.Key,_=>identity,cancellation);}catch(OperationCanceledException){}catch(IOException){}catch(ObjectDisposedException){} });}
        public void Ensure(string key,Func<CancellationToken,RemoteServerIdentity> probe,CancellationToken cancellation)
        {
            while(true){
            cancellation.ThrowIfCancellationRequested();Flight flight;Entry entry;
            lock(gate){if(disposed)throw new ObjectDisposedException("RemoteServerTrust");entry=GetEntry(key);if(entry.Verified)return;if(entry.Declined){RemoteServerTrustNotice notice;if(pending.TryGetValue(key,out notice))throw new RemoteServerTrustException(notice);}
                flight=entry.Flight;if(flight==null){flight=new Flight{Cancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)};entry.Flight=flight;var current=flight;flight.Task=Task.Factory.StartNew(()=>Resolve(key,probe,current.Cancellation.Token),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);}flight.Waiters++;}
            try{int done=WaitHandle.WaitAny(new[]{((IAsyncResult)flight.Task).AsyncWaitHandle,cancellation.WaitHandle});if(done==1)cancellation.ThrowIfCancellationRequested();flight.Task.GetAwaiter().GetResult();return;}
            catch(OperationCanceledException){cancellation.ThrowIfCancellationRequested();if(lifetime.IsCancellationRequested||!flight.Cancellation.IsCancellationRequested)throw;}
            finally{Release(entry,flight);}
            }
        }
        private void Release(Entry entry,Flight flight)
        {
            bool cancel=false,cleanup=false,dispose=false;
            lock(gate){flight.Waiters--;if(flight.Waiters==0){if(flight.Task.IsCompleted){if(ReferenceEquals(entry.Flight,flight))entry.Flight=null;dispose=true;}else{cancel=true;if(!flight.CleanupScheduled){flight.CleanupScheduled=true;cleanup=true;}}}}
            if(cancel)try{flight.Cancellation.Cancel();}catch(ObjectDisposedException){}
            if(dispose){var ignored=flight.Task.Exception;flight.Cancellation.Dispose();}
            if(cleanup)flight.Task.ContinueWith(done=>{bool release=false;lock(gate){if(flight.Waiters==0&&ReferenceEquals(entry.Flight,flight)){entry.Flight=null;release=true;}}var ignored=done.Exception;if(release)flight.Cancellation.Dispose();},TaskScheduler.Default);
        }
        private void Resolve(string key,Func<CancellationToken,RemoteServerIdentity> probe,CancellationToken cancellation)
        {
            var identity=probe(cancellation);cancellation.ThrowIfCancellationRequested();if(!Valid(identity)||identity.Key!=key)throw new IOException("服务器身份与请求地址不一致。");
            RemoteServerTrustNotice notice;bool accepted,cleared=false;
            lock(gate){accepted=Acceptable(identity);notice=accepted?null:Observe(identity);if(accepted){GetEntry(key).Verified=true;cleared=pending.Remove(key);}}
            if(accepted){if(cleared)Publish(key);return;}Publish(key);
            var prompt=Prompt;bool decision=prompt!=null&&prompt(notice,cancellation);cancellation.ThrowIfCancellationRequested();
            lock(gate){if(decision){if(identity.Kind=="tls"&&identity.PolicyErrors==0)trusted.Remove(key);else trusted[key]=identity;pending.Remove(key);var entry=GetEntry(key);entry.Verified=true;entry.Declined=false;}else GetEntry(key).Declined=true;}
            Publish(key);if(!decision)throw new RemoteServerTrustException(notice);
        }
        public void Dispose(){lock(gate){if(disposed)return;disposed=true;}lifetime.Cancel();Prompt=null;Changed=null;}
    }
}
