using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;

namespace Backup.Core
{
    // A private HTTP connection group for one warehouse/configuration. Request views
    // share connections, never cancellation, upload ownership or mutable paths.
    // Connected is the warehouse's successful communication lease. HTTP handles
    // Connection: close per request; it does not close this entire private group
    // or reset the warehouse before its configured idle timeout.
    internal sealed class WebDavConnectionPool:IDisposable
    {
        private readonly object gate=new object();
        private readonly string group="Backup-DAV-"+Guid.NewGuid().ToString("N");
        private readonly HashSet<ServicePoint> points=new HashSet<ServicePoint>();
        private readonly int idleMilliseconds;
        private int active;
        private bool connected,failed,disposed;private string errorMessage;
        private Timer timer;private long version,idleSince;
        internal event Action Changed;
        internal event Action<string> UploadDiagnostic;
        internal void ReportUploadFailure(string message)
        {var handler=UploadDiagnostic;if(handler!=null)foreach(Action<string> callback in handler.GetInvocationList())try{callback(message);}catch{}}
        internal WebDavConnectionPool(TimeSpan idle)
        {if(idle.TotalMilliseconds<1||idle.TotalMilliseconds>int.MaxValue)throw new ArgumentOutOfRangeException("idle");idleMilliseconds=(int)idle.TotalMilliseconds;}
        private string StateUnsafe(){return disposed?"空闲":connected?"已连接":active>0?"正在连接":failed?"连接失败":"空闲";}
        internal string State{get{lock(gate)return StateUnsafe();}}
        internal string Error{get{lock(gate)return errorMessage;}}
        private void Notify(){var handler=Changed;if(handler!=null)foreach(Action callback in handler.GetInvocationList())try{callback();}catch{}}
        private void StopTimerUnsafe(){version++;var old=timer;timer=null;if(old!=null)old.Dispose();}
        internal WebDavRequest Begin(Uri uri,string method,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();var request=(HttpWebRequest)WebRequest.Create(uri);request.Method=method;request.Proxy=null;request.ConnectionGroupName=group;request.KeepAlive=true;
            var point=request.ServicePoint;point.ConnectionLimit=TransferScheduler.MaximumDownloads+8;
            // ServicePoint is per origin. Never shorten another warehouse's idle lease;
            // CloseConnectionGroup below closes only this warehouse's private group.
            if(point.MaxIdleTime<idleMilliseconds)point.MaxIdleTime=idleMilliseconds;
            bool changed;lock(gate){if(disposed)throw new ObjectDisposedException("WebDavDestination");string old=StateUnsafe();StopTimerUnsafe();points.Add(point);active++;changed=old!=StateUnsafe();}if(changed)Notify();
            try{return new WebDavRequest(this,request,cancellation);}catch{Release();throw;}
        }
        internal void Observe(HttpWebResponse response)
        {
            bool changed;lock(gate)
            {
                if(disposed)return;string old=StateUnsafe();int code=(int)response.StatusCode;
                if(code==401){failed=true;connected=false;errorMessage="WebDAV 身份验证失败（HTTP 401），请检查账号和密码。";}
                else{failed=false;connected=true;errorMessage=null;}
                changed=old!=StateUnsafe();
            }if(changed)Notify();
        }
        internal void Failure(Exception error,bool cancelled)
        {
            if(cancelled||error is TemporaryObjectConflictException||error is BackupObjectConflictException||error is InvalidBackupObjectException)return;
            var transfer=error as RemoteTransferException;if(transfer!=null&&transfer.StatusCode>0&&transfer.StatusCode!=401)return;
            var web=error as WebException;
            if(web!=null&&web.Response!=null){var response=web.Response as HttpWebResponse;if(response!=null)Observe(response);return;}
            if(!(error is IOException)&&web==null&&!(error is System.Net.Sockets.SocketException))return;
            bool changed;lock(gate){if(disposed)return;string old=StateUnsafe(),previous=errorMessage;failed=true;connected=false;errorMessage=error.Message;changed=old!=StateUnsafe()||previous!=errorMessage;}if(changed)Notify();
        }
        private void CloseUnsafe(){foreach(var point in points)try{point.CloseConnectionGroup(group);}catch{}points.Clear();}
        internal void Release()
        {
            bool changed;lock(gate)
            {
                string old=StateUnsafe();if(--active<0)throw new InvalidOperationException("Unbalanced WebDAV request lease.");
                if(active==0)
                {
                    StopTimerUnsafe();
                    if(disposed||!connected){CloseUnsafe();connected=false;}
                    else{idleSince=Stopwatch.GetTimestamp();long expected=version;timer=new Timer(_=>Expire(expected),null,idleMilliseconds,Timeout.Infinite);}
                }
                changed=old!=StateUnsafe();
            }if(changed)Notify();
        }
        private void Expire(long expected)
        {
            bool changed=false;lock(gate)
            {
                if(disposed||active!=0||expected!=version)return;
                double elapsed=(Stopwatch.GetTimestamp()-idleSince)*1000.0/Stopwatch.Frequency;
                if(elapsed<idleMilliseconds){if(timer!=null)timer.Change(Math.Max(1,(int)Math.Ceiling(idleMilliseconds-elapsed)),Timeout.Infinite);return;}
                string old=StateUnsafe();StopTimerUnsafe();CloseUnsafe();connected=false;changed=old!=StateUnsafe();
            }if(changed)Notify();
        }
        public void Dispose()
        {
            bool changed;lock(gate){if(disposed)return;string old=StateUnsafe();disposed=true;StopTimerUnsafe();if(active==0)CloseUnsafe();connected=false;failed=false;changed=old!=StateUnsafe();}if(changed)Notify();
        }
    }
    internal sealed class WebDavRequest:IDisposable
    {
        private WebDavConnectionPool owner;private readonly CancellationToken cancellation;private CancellationTokenRegistration registration;
        internal readonly HttpWebRequest Request;
        internal WebDavRequest(WebDavConnectionPool pool,HttpWebRequest request,CancellationToken token)
        {owner=pool;Request=request;cancellation=token;registration=token.Register(request.Abort);}
        internal void Observe(HttpWebResponse response){var value=owner;if(value!=null)value.Observe(response);}
        internal void Failure(Exception error){var value=owner;if(value!=null)value.Failure(error,cancellation.IsCancellationRequested);}
        internal void CheckCancellation(){cancellation.ThrowIfCancellationRequested();}
        public void Dispose(){var value=Interlocked.Exchange(ref owner,null);if(value==null)return;registration.Dispose();value.Release();}
    }
}
