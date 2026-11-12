using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;

namespace Backup.Core
{
    // No process-wide certificate override, redirects, disk spool or payload buffering.
    public sealed partial class WebDavDestination : IRepositorySession,IOpenListRepositorySession,IKnownLengthUploadDestination,ICancellableRemoteFolderBrowser,IStorageDirectoryListing,IStorageEntryRemoval,ICancellableBackupDestination,IOwnedUploadDestination,IInterruptedUploadDestination,IRecoverableUploadDestination,IReadOnlyDestinationProbe,IDisposable,IBandwidthDestination,ITemporaryUploadProbe,ITaskStorageRemoval,IRangedBackupDestination,IAbandonedUploadDestination
    {
        static WebDavDestination(){ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;}
        private readonly Uri root;private readonly RemoteConnectionOptions connection;private bool prepared;
        private DateTime rootCheckedUtc;
        private readonly TimeSpan idleTimeout;
        private readonly UploadStaging staging;
        private readonly WebDavDirectorySafety directorySafety;
        private readonly WebDavConnectionPool connections;private readonly bool ownsConnections;
        private readonly System.Net.Security.RemoteCertificateValidationCallback certificateValidation;
        public string ConnectionState{get{return connections.State;}}
        public string ConnectionError{get{return connections.Error;}}
        public event Action ConnectionStateChanged{add{connections.Changed+=value;}remove{connections.Changed-=value;}}
        public event Action<string> UploadDiagnostic{add{connections.UploadDiagnostic+=value;}remove{connections.UploadDiagnostic-=value;}}
        public IUploadJournal UploadJournal{get{return staging.Journal;}set{staging.Journal=value;directorySafety.Attach(value as BackupCatalog);}}
        public void RejectTemporary(string name){FinishNative(name);staging.Rejected(name);}
        public RemoteObjectInfo StatTemporary(string name){return Stat(staging.Current(name));}
        public void ResumeTemporary(string name,string temporary){staging.Resume(name,temporary);}
        public void SuspendTemporary(string name){staging.Suspend(name);}public bool HasInterruptedTemporary(string name){return staging.HasInterrupted(name);}
        public string Id{get;private set;}
        public CancellationToken OperationCancellation{get;set;}
        public TransferBandwidth Bandwidth{get;set;}
        public WebDavDestination(string id,string location,RemoteConnectionOptions options):this(id,location,options,TimeSpan.FromSeconds(RemoteConnectionPolicy.OperationSeconds)){}
        public WebDavDestination(string id,string location,RemoteConnectionOptions options,TimeSpan inactivityTimeout):this(id,location,options,inactivityTimeout,TimeSpan.FromSeconds(RemoteConnectionPolicy.IdleSeconds)){}
        public WebDavDestination(string id,string location,RemoteConnectionOptions options,TimeSpan inactivityTimeout,TimeSpan connectionIdleTimeout):this(id,location,options,inactivityTimeout,new WebDavConnectionPool(connectionIdleTimeout),true){}
        private WebDavDestination(string id,string location,RemoteConnectionOptions options,TimeSpan inactivityTimeout,WebDavConnectionPool pool,bool owns,System.Net.Security.RemoteCertificateValidationCallback validation=null,OpenListCapability capability=null,WebDavDirectorySafety safety=null)
        {Id=id;staging=new UploadStaging(id);root=RemotePath.Root(location,"http");directorySafety=safety??new WebDavDirectorySafety(id,root);connection=options??new RemoteConnectionOptions();if(connection.ServerTrust==null)connection.ServerTrust=new RemoteServerTrust();idleTimeout=inactivityTimeout;if(idleTimeout<=TimeSpan.Zero)throw new ArgumentOutOfRangeException("inactivityTimeout");connections=pool;ownsConnections=owns;openListCapability=capability??new OpenListCapability();certificateValidation=validation??((sender,cert,chain,errors)=>{if(cert==null)return false;connection.ServerFingerprint=RemotePath.Fingerprint(cert.GetRawCertData());return connection.ServerTrust.CheckPresented(RemoteServerIdentity.Tls(root,cert,chain,errors));});}
        IManagedBackupDestination IRepositorySession.ForDirectory(string location,CancellationToken cancellation){return ForDirectory(location,cancellation);}
        public IList<StorageEntry> ListEntriesAt(string location,CancellationToken cancellation,bool fresh)
        {using(var next=ForDirectory(location,cancellation))return next.ListEntries(fresh);}
        public WebDavDestination ForDirectory(string location,CancellationToken cancellation)
        {var next=RemotePath.Root(location,"http");if(next.Scheme!=root.Scheme||next.Authority!=root.Authority||!next.AbsolutePath.StartsWith(root.AbsolutePath,StringComparison.Ordinal))throw new IOException("仓库地址越界。");return new WebDavDestination(Id,next.AbsoluteUri,connection,idleTimeout,connections,false,certificateValidation,openListCapability,directorySafety){OperationCancellation=cancellation,Bandwidth=Bandwidth,UploadJournal=UploadJournal};}
        public void Dispose(){if(ownsConnections)connections.Dispose();}
        public static string ProbeFingerprint(string location){Uri uri=RemotePath.Root(location,"http");if(uri.Scheme!="https")return "";using(var socket=new System.Net.Sockets.TcpClient()){var connect=socket.BeginConnect(uri.Host,uri.Port,null,null);if(!connect.AsyncWaitHandle.WaitOne(10000))throw new IOException("连接证书检查超时。");socket.EndConnect(connect);using(var tls=new System.Net.Security.SslStream(socket.GetStream(),false,(sender,cert,chain,errors)=>true)){tls.ReadTimeout=10000;tls.WriteTimeout=10000;tls.AuthenticateAsClient(uri.Host);return RemotePath.Fingerprint(tls.RemoteCertificate.GetRawCertData());}}}
        private Uri ObjectUri(string name){return new Uri(root,Uri.EscapeDataString(RemotePath.Name(name)));}
        private WebDavRequest Request(Uri uri,string method,CancellationToken cancellation=default(CancellationToken))
        {
            if(!cancellation.CanBeCanceled)cancellation=OperationCancellation;
            var lease=connections.Begin(uri,method,cancellation);try{
            if(uri.Scheme=="https"&&uri.Authority.Equals(root.Authority,StringComparison.OrdinalIgnoreCase))try{EnsureServerTrust(cancellation);}catch(Exception error){lease.Failure(error);throw;}
            var request=lease.Request;
            request.Method=method;request.AllowAutoRedirect=false;
            bool upload=method=="PUT"||method=="MOVE";
            request.Timeout=upload?Timeout.Infinite:30000;
            request.ReadWriteTimeout=upload?Timeout.Infinite:30000;
            request.KeepAlive=true;request.AllowWriteStreamBuffering=false;request.Proxy=null;
            request.CachePolicy=new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            // Bounded room for parallel warehouse lanes and their metadata requests.
            request.ServicePoint.ConnectionLimit=TransferScheduler.MaximumDownloads+8;
            bool origin=uri.Scheme==root.Scheme&&uri.Authority.Equals(root.Authority,StringComparison.OrdinalIgnoreCase);
            if(origin&&!string.IsNullOrEmpty(connection.Username))request.Headers[HttpRequestHeader.Authorization]="Basic "+Convert.ToBase64String(Encoding.UTF8.GetBytes(connection.Username+":"+connection.Password));
            // Exceptions are pinned to the confirmed server certificate. Other
            // origins (including cloud download redirects) keep system validation.
            if(origin)request.ServerCertificateValidationCallback=(sender,cert,chain,errors)=>{bool accepted=certificateValidation(sender,cert,chain,errors);if(!accepted&&cert!=null)connection.ServerTrust.ObserveAsync(RemoteServerIdentity.Tls(uri,cert,chain,errors),cancellation);return accepted;};
            return lease;}catch{lease.Dispose();throw;}
        }
        private HttpWebResponse Response(WebDavRequest lease,bool allowRedirect=false,TransferActivity activity=null)
        {
            lease.CheckCancellation();var request=lease.Request;
            try
            {
                var response=(HttpWebResponse)request.GetResponse();lease.Observe(response);
                if((int)response.StatusCode>=300&&!allowRedirect){int code=(int)response.StatusCode;response.Dispose();throw new DavException(code,request.Method,"服务器拒绝请求");}
                return response;
            }
            catch(WebException error)
            {
                lease.Failure(error);lease.CheckCancellation();
                if(error.Status==WebExceptionStatus.TrustFailure||error.Status==WebExceptionStatus.SecureChannelFailure)throw new RemoteCertificateException();
                var response=error.Response as HttpWebResponse;int code=response==null?0:(int)response.StatusCode,retryAfter=0;
                if(response!=null){int seconds;if(int.TryParse(response.Headers["Retry-After"],out seconds))retryAfter=(int)Math.Min(30000,Math.Max(0,(long)seconds*1000));response.Dispose();}
                if(code==412&&request.Method=="PUT")throw new TemporaryObjectConflictException();
                if(code==0&&activity!=null)throw activity.Failure(error);
                throw new DavException(code,request.Method,code==0?"网络连接中断（"+error.Status+"）":"服务器拒绝请求",error,activity,retryAfter);
            }
        }
        private sealed class DavException:RemoteTransferException
        {
            public readonly int Code;
            public DavException(int code,string method,string reason,Exception inner=null,TransferActivity activity=null,int retryAfter=0)
                :base("WebDAV",method,code,activity==null?0:activity.Bytes,activity==null?0:activity.ElapsedMilliseconds,reason,inner,retryAfter){Code=code;}
        }
        private List<RemoteFolder> Probe(Uri uri,int depth,out long length,out bool folder,IList<StorageEntry> entries=null,CancellationToken cancellation=default(CancellationToken))
        {
            if(!cancellation.CanBeCanceled)cancellation=OperationCancellation;
            cancellation.ThrowIfCancellationRequested();using(var lease=Request(uri,"PROPFIND",cancellation)){var request=lease.Request;
            request.Headers["Depth"]=depth.ToString();request.ContentType="application/xml; charset=utf-8";
            byte[] body=Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/><d:getcontentlength/></d:prop></d:propfind>");request.ContentLength=body.Length;
            try{using(var target=request.GetRequestStream())target.Write(body,0,body.Length);}catch(WebException e){lease.Failure(e);request.Abort();cancellation.ThrowIfCancellationRequested();if(e.Status==WebExceptionStatus.TrustFailure||e.Status==WebExceptionStatus.SecureChannelFailure)throw new RemoteCertificateException();throw new RemoteTransferException("WebDAV","PROPFIND",0,0,0,"元数据请求失败（"+e.Status+"）",e);}
            length=-1;folder=false;var folders=new List<RemoteFolder>();bool matched=false;
            using(var response=Response(lease))using(var input=response.GetResponseStream())using(var reader=XmlReader.Create(input,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=8*1024*1024}))
            {
                var document=new XmlDocument{XmlResolver=null};try{document.Load(reader);}catch(Exception error){lease.Failure(error);throw;}var ns=new XmlNamespaceManager(document.NameTable);ns.AddNamespace("d","DAV:");
                foreach(XmlNode row in document.SelectNodes("/d:multistatus/d:response",ns))
                {
                    var href=row.SelectSingleNode("d:href",ns);if(href==null)continue;Uri actual;if(!Uri.TryCreate(root,href.InnerText,out actual)||actual.Scheme!=root.Scheme||actual.Authority!=root.Authority)continue;
                    var prop=row.SelectSingleNode("d:propstat[contains(d:status,' 200 ')]/d:prop",ns);if(prop==null)continue;
                    bool isFolder=prop.SelectSingleNode("d:resourcetype/d:collection",ns)!=null;
                    if(actual.AbsolutePath.TrimEnd('/')==uri.AbsolutePath.TrimEnd('/')){matched=true;folder=isFolder;var size=prop.SelectSingleNode("d:getcontentlength",ns);if(size!=null)long.TryParse(size.InnerText,out length);}
                    else if(actual.AbsolutePath.StartsWith(uri.AbsolutePath.TrimEnd('/')+"/",StringComparison.Ordinal)){string relative=actual.AbsolutePath.Substring(uri.AbsolutePath.TrimEnd('/').Length+1).TrimEnd('/');if(relative.Length>0&&!relative.Contains("/")){string name=Uri.UnescapeDataString(relative);try{RemotePath.Name(name);}catch(ArgumentException){continue;}if(isFolder)folders.Add(new RemoteFolder{Name=name,Location=actual.AbsoluteUri.TrimEnd('/')+"/"});if(entries!=null){long size=-1;var sizeNode=prop.SelectSingleNode("d:getcontentlength",ns);if(sizeNode!=null&&!long.TryParse(sizeNode.InnerText,out size))size=-1;entries.Add(new StorageEntry{Name=name,Location=actual.AbsoluteUri,Directory=isFolder,Size=isFolder?0:size});}}}
                }
            }
            if(!matched)throw new IOException("WebDAV 未返回请求对象的有效属性。");if(folder)directorySafety.ObserveFolder(uri);return folders;
            }
        }
        private void CheckRoot(bool force=false){if(!force&&DateTime.UtcNow-rootCheckedUtc<TimeSpan.FromSeconds(15))return;long size;bool folder;try{Probe(root,0,out size,out folder);}catch(DavException error){if(error.Code==404||error.Code==409){prepared=false;rootCheckedUtc=DateTime.MinValue;}throw;}if(!folder)throw new InvalidBackupObjectException("WebDAV 仓库不是文件夹。");rootCheckedUtc=DateTime.UtcNow;}
        public void CheckReadableRoot(){CheckRoot(true);}
        public IList<StorageEntry> ListEntries(){return ListEntries(false);}
        public IList<StorageEntry> ListEntries(bool fresh){if(fresh)DirectoryRefresh.InvalidateFolder(root.AbsoluteUri);return DirectoryRefresh.Read(DirectoryScope,root.AbsoluteUri,false,()=>{long size;bool folder;var entries=new List<StorageEntry>();try{Probe(root,1,out size,out folder,entries);}catch(XmlException error){throw new IOException("WebDAV 返回的目录清单格式无效。",error);}if(!folder)throw new IOException("WebDAV 仓库不是文件夹。");return entries;},OperationCancellation);}
        internal static int DirectoryReadyChecks=8,DirectoryReadyDelayMilliseconds=250;
        private void VerifyKnownRepositoryRoot()
        {
            if(directorySafety.IsRepositoryRoot(root)||!directorySafety.NeedsRootCheck)return;long size;bool folder;
            try{Probe(directorySafety.RepositoryRoot,0,out size,out folder);if(!folder)throw new InvalidBackupObjectException("WebDAV 仓库不是文件夹。");}
            catch(IOException error){throw new RemoteDirectoryPendingException(directorySafety.RepositoryRoot.AbsoluteUri,false,error);}
            catch(XmlException error){throw new RemoteDirectoryPendingException(directorySafety.RepositoryRoot.AbsoluteUri,false,error);}
        }
        private void WaitForCreatedDirectory(Uri location)
        {
            try{for(int attempt=0;attempt<DirectoryReadyChecks;attempt++){
                OperationCancellation.ThrowIfCancellationRequested();long size;bool folder;
                try{Probe(location,0,out size,out folder);if(!folder)throw new InvalidBackupObjectException("WebDAV 新目录不可用。");return;}
                catch(DavException pending){if(pending.Code!=404&&pending.Code!=409)throw;if(attempt+1<DirectoryReadyChecks&&OperationCancellation.WaitHandle.WaitOne(Math.Min(2000,DirectoryReadyDelayMilliseconds*(1<<Math.Min(attempt,3)))))OperationCancellation.ThrowIfCancellationRequested();}
            }}catch(IOException error){throw new RemoteDirectoryPendingException(location.AbsoluteUri,true,error);}
            catch(XmlException error){throw new RemoteDirectoryPendingException(location.AbsoluteUri,true,error);}
            throw new RemoteDirectoryPendingException(location.AbsoluteUri,true);
        }
        public void PrepareWrite()
        {
            OperationCancellation.ThrowIfCancellationRequested();VerifyKnownRepositoryRoot();try{CheckRoot();prepared=true;return;}catch(DavException initial){if(prepared||directorySafety.IsRepositoryRoot(root)&&directorySafety.Known||initial.Code!=404&&initial.Code!=409)throw new RemoteDirectoryPendingException(root.AbsoluteUri,directorySafety.CreationPending(root),initial);}
            catch(IOException initial){throw new RemoteDirectoryPendingException(root.AbsoluteUri,directorySafety.CreationPending(root),initial);}
            catch(XmlException initial){throw new RemoteDirectoryPendingException(root.AbsoluteUri,directorySafety.CreationPending(root),initial);}
            Uri current=new Uri(root.GetLeftPart(UriPartial.Authority)+"/");
            foreach(string part in root.AbsolutePath.Split(new[]{'/'},StringSplitOptions.RemoveEmptyEntries))
            {
                current=new Uri(current,part+"/");long size;bool folder;
                try{Probe(current,0,out size,out folder);if(!folder)throw new InvalidBackupObjectException("WebDAV 路径存在同名文件。");}
                catch(DavException error){
                    if(error.Code!=404&&error.Code!=409)throw;
                    if(directorySafety.IsRepositoryRoot(current)&&directorySafety.Known)throw new RemoteDirectoryPendingException(current.AbsoluteUri,false,error);
                    OperationCancellation.ThrowIfCancellationRequested();
                    if(directorySafety.TryCreate(current)){
                        try{using(var lease=Request(current,"MKCOL"))using(var response=Response(lease)){} }
                        catch(DavException create){if(create.Code==401||create.Code==403){directorySafety.RejectCreation(current);throw;}if(create.Code!=405)throw new RemoteDirectoryPendingException(current.AbsoluteUri,true,create);}
                        catch(IOException create){throw new RemoteDirectoryPendingException(current.AbsoluteUri,true,create);}
                    }
                    WaitForCreatedDirectory(current);
                }
            }
            prepared=true;
        }
        public Stream OpenTemporary(string name){throw new NotSupportedException("WebDAV 上传需要确定的分卷长度。");}
        private void ReportUploadFailure(string name,long length,TransferActivity activity,Exception error)
        {
            if(OperationCancellation.IsCancellationRequested||error is OperationCanceledException)return;
            var detail=error as RemoteTransferException??activity.Failure(error);
            connections.ReportUploadFailure("仓库："+Id+"；文件："+name+"；阶段："+activity.Stage+
                "；已发送："+activity.Bytes+(length<0?"":" / "+length)+" 字节；请求耗时："+
                (activity.ElapsedMilliseconds/1000.0).ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)+
                " 秒；等待上传回应："+(activity.ResponseWaitMilliseconds/1000.0).ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)+
                " 秒"+Environment.NewLine+detail);
        }
        public Stream OpenTemporary(string name,long length)
        {
            PrepareWrite();if(Stat(name).Exists)throw new IOException("拒绝覆盖已有备份对象。");if(Stat(name+".partial").Exists)throw new TemporaryObjectConflictException();
            var native=OpenNative(name,length);if(native!=null)return native;var lease=Request(ObjectUri(staging.Begin(name)),"PUT");var request=lease.Request;request.ContentType="application/octet-stream";request.ContentLength=length;request.Headers[HttpRequestHeader.IfNoneMatch]="*";
            var activity=new TransferActivity(request.Abort,idleTimeout,TimeSpan.FromSeconds(RemoteConnectionPolicy.UploadResponseSeconds));
            try
            {
                var stream=request.GetRequestStream();activity.Progress();activity.Stage="传输数据";
                return BandwidthStream.Wrap(new ResponseStream(stream,()=>{try{using(var response=Response(lease,false,activity)){} }catch(DavException error){if(error.Code==404||error.Code==409){prepared=false;rootCheckedUtc=DateTime.MinValue;}throw;}finally{DirectoryRefresh.InvalidateFolder(root.AbsoluteUri);}},request.Abort,length,lease,activity,error=>{lease.Failure(error);ReportUploadFailure(name,length,activity,error);}),Bandwidth,true,OperationCancellation,activity.RateWaiting);
            }
            catch(Exception error)
            {
                lease.Failure(error);ReportUploadFailure(name,length,activity,error);request.Abort();activity.Dispose();lease.Dispose();OperationCancellation.ThrowIfCancellationRequested();
                var failure=error as IOException??activity.Failure(error);
                if(failure is TemporaryObjectConflictException){staging.Rejected(name);throw failure;}
                throw new TransferReconciliationRequiredException("临时上传开始状态待核对，已保留原目标和本次临时名称记录。",failure);
            }
        }
        public void CommitTemporary(string name){if(CommitNative(name))return;string temporary=staging.Current(name);if(!UploadStaging.IsOwnedTemporary(name,temporary))throw new IOException("临时上传所有权记录无效，已保留。");DirectoryRefresh.InvalidateFolder(root.AbsoluteUri);try{using(var lease=Request(ObjectUri(temporary),"MOVE")){var request=lease.Request;request.Headers["Destination"]=ObjectUri(name).AbsoluteUri;request.Headers["Overwrite"]="F";using(var activity=new TransferActivity(request.Abort,idleTimeout)){activity.Stage="提交压缩包";try{using(var response=Response(lease,false,activity)){} }catch(Exception error){ReportUploadFailure(name,-1,activity,error);throw;} }}}finally{DirectoryRefresh.InvalidateFolder(root.AbsoluteUri);}}
        public void DeleteTemporary(string name){staging.Delete(name,Delete);}
        public RemoteObjectInfo StatFormal(string name){return StatCompletedNative(name,true)??Stat(name);}
        public void DiscardTemporary(string name){staging.Delete(name,temporary=>{DiscardNativeTemporary(temporary);DeleteRemoteFile(temporary);});}
        public void DeleteObject(string name){CheckRoot();Delete(name);}
        private void Delete(string name){CancelNativeBeforeDelete(name);DeleteRemoteFile(name);ForgetNativeServerTask(name);}
        private void DeleteRemoteFile(string name){DirectoryRefresh.InvalidateFolder(root.AbsoluteUri);try{using(var lease=Request(ObjectUri(name),"DELETE"))using(var response=Response(lease)){} }catch(DavException error){if(error.Code!=404)throw;}finally{DirectoryRefresh.InvalidateFolder(root.AbsoluteUri);}}
        public Stream OpenRead(string name){return OpenDownload(name,0,0,0,OperationCancellation,false).Stream;}
        public DownloadRange OpenReadRange(string name,long offset,long count,long totalSize,CancellationToken cancellation)
        {return OpenDownload(name,offset,count,totalSize,cancellation,true);}
        private DownloadRange OpenDownload(string name,long offset,long count,long totalSize,CancellationToken cancellation,bool ranged)
        {
            if(ranged&&(offset<0||count<=0||offset>totalSize-count))throw new ArgumentOutOfRangeException("offset");Uri uri=ObjectUri(name);
            for(int attempt=0;attempt<6;attempt++){
                cancellation.ThrowIfCancellationRequested();var lease=Request(uri,"GET",cancellation);var request=lease.Request;if(ranged)request.AddRange(offset,offset+count-1);request.Headers[HttpRequestHeader.AcceptEncoding]="identity";
                HttpWebResponse response;try{response=Response(lease,true);}catch{lease.Dispose();cancellation.ThrowIfCancellationRequested();throw;}int code=(int)response.StatusCode;
                if(code==200||code==206){
                    try{
                        if(code==206){var match=System.Text.RegularExpressions.Regex.Match(response.Headers["Content-Range"]??"",@"\Abytes ([0-9]+)-([0-9]+)/([0-9]+)\z",System.Text.RegularExpressions.RegexOptions.CultureInvariant|System.Text.RegularExpressions.RegexOptions.IgnoreCase);long first,last,total;
                            if(!match.Success||!long.TryParse(match.Groups[1].Value,out first)||!long.TryParse(match.Groups[2].Value,out last)||!long.TryParse(match.Groups[3].Value,out total)||first!=offset||last!=offset+count-1||total!=totalSize||response.ContentLength>=0&&response.ContentLength!=count||!string.IsNullOrEmpty(response.ContentEncoding)&&response.ContentEncoding!="identity")throw new IOException("WebDAV 返回的下载分段范围或长度不符，已拒绝拼接。");
                        }else if(ranged&&response.ContentLength>=0&&response.ContentLength!=totalSize)throw new IOException("下载文件长度已变化。");
                        var stream=BandwidthStream.Wrap(new ResponseStream(response.GetResponseStream(),response.Dispose,request.Abort,-1,lease,null,lease.Failure,response.ContentLength,ioToken:cancellation),Bandwidth,false,cancellation);
                        string version=response.Headers["ETag"]??response.Headers["Last-Modified"];
                        return new DownloadRange(code==206?new BoundedDownloadStream(stream,count,cancellation):stream,code==206,version);
                    }catch{request.Abort();response.Dispose();lease.Dispose();throw;}
                }
                string location=response.Headers["Location"];response.Dispose();lease.Dispose();Uri next;
                if((code!=301&&code!=302&&code!=303&&code!=307&&code!=308)||string.IsNullOrEmpty(location)||!Uri.TryCreate(uri,location,out next)||!string.IsNullOrEmpty(next.UserInfo))throw new IOException("WebDAV 下载重定向无效。");
                if(root.Scheme=="https"&&next.Scheme=="http"&&next.Authority.Equals(root.Authority,StringComparison.OrdinalIgnoreCase)){var builder=new UriBuilder(next){Scheme="https",Port=root.Port};next=builder.Uri;}
                if(next.Scheme!="https"&&!(root.Scheme=="http"&&next.Scheme=="http"&&next.Authority.Equals(root.Authority,StringComparison.OrdinalIgnoreCase)))throw new IOException("拒绝将 WebDAV 下载重定向到不安全的 HTTP 地址。");uri=next;
            }
            throw new IOException("WebDAV 下载重定向次数过多。");
        }
        public RemoteObjectInfo Stat(string name){RemotePath.Name(name);OperationCancellation.ThrowIfCancellationRequested();CheckNativeTemporary(name);var abandoned=NativeTask(name);if(abandoned!=null&&abandoned.State=="not-found")return new RemoteObjectInfo{Name=name,Exists=false};var completed=StatCompletedNative(name);if(completed!=null)return completed;CheckRoot();long size;bool folder;try{Probe(ObjectUri(name),0,out size,out folder);if(folder)throw new InvalidBackupObjectException("备份对象类型无效。");if(size<0)throw new IOException("WebDAV 未提供正确的文件大小。");return new RemoteObjectInfo{Name=name,Exists=true,Size=size};}catch(DavException error){if(error.Code!=404)throw;CheckRoot(true);return new RemoteObjectInfo{Name=name,Exists=false};}}
        public IList<RemoteFolder> ListFolders(){return ListFolders(CancellationToken.None);}
        public IList<RemoteFolder> ListFolders(CancellationToken cancellation){long size;bool folder;var result=Probe(root,1,out size,out folder,null,cancellation);cancellation.ThrowIfCancellationRequested();if(!folder)throw new IOException("远程路径不是目录。");return result;}
        public void CreateFolder(string name){using(var child=ForDirectory(RemotePath.Append(root.AbsoluteUri,name),OperationCancellation))child.PrepareWrite();}

        public StorageEntry InspectEntry(string name){CheckRoot();long size;bool directory;try{Probe(ObjectUri(name),0,out size,out directory);return new StorageEntry{Name=name,Location=ObjectUri(name).AbsoluteUri,Directory=directory,Size=directory?0:size};}catch(DavException error){if(error.Code!=404)throw;CheckRoot(true);return null;}}
        public void RemoveEntry(string name,bool directory){var item=InspectEntry(name);if(item==null)return;if(item.Directory!=directory)throw new IOException("文件类型已变化，请刷新后重试。");if(directory){using(var child=ForDirectory(item.Location,OperationCancellation))if(child.ListEntries(true).Count!=0)throw new IOException("目录仍有文件，已保留目录。");}Delete(name);}
        private void DeleteTaskEntry(Uri uri)
        {
            try{using(var lease=Request(uri,"DELETE"))using(var response=Response(lease)){
                if((int)response.StatusCode==202)throw new IOException("仓库已受理目录删除，请稍后重试确认结果。");
                if((int)response.StatusCode==207)using(var input=response.GetResponseStream())using(var reader=XmlReader.Create(input,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=8*1024*1024})){
                    var document=new XmlDocument{XmlResolver=null};document.Load(reader);var ns=new XmlNamespaceManager(document.NameTable);ns.AddNamespace("d","DAV:");var statuses=document.SelectNodes("//d:status",ns);if(statuses.Count==0)throw new IOException("仓库未返回完整的删除结果。");foreach(XmlNode status in statuses){var parts=status.InnerText.Trim().Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries);int code;if(parts.Length<2||!int.TryParse(parts[1],out code)||code<200||code>=300&&code!=404)throw new IOException("仓库未能完整删除任务目录："+status.InnerText);}
                }
            }}catch(DavException error){if(error.Code!=404)throw;}catch(XmlException error){throw new IOException("仓库返回的删除结果无效。",error);}
        }
        public void DeleteDirectoryTree(string name){DirectoryRefresh.InvalidateFolder(ObjectUri(name).AbsoluteUri,true);try{DeleteTaskEntry(new Uri(ObjectUri(name).AbsoluteUri.TrimEnd('/')+"/"));}finally{DirectoryRefresh.InvalidateFolder(root.AbsoluteUri);DirectoryRefresh.InvalidateFolder(ObjectUri(name).AbsoluteUri,true);}}
    }
}
