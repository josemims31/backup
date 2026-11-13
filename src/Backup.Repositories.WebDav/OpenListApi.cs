using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Backup.Core
{
#pragma warning disable 0649 // Response fields are assigned by DataContractJsonSerializer.

    [DataContract]
    internal sealed class OpenListReply<T>
    {
        [DataMember(Name = "code", IsRequired = true)]
        public int Code;
        [DataMember(Name = "message")]
        public string Message;
        [DataMember(Name = "data")]
        public T Data;
    }

    [DataContract]
    internal sealed class OpenListVersion
    {
        [DataMember(Name = "version")]
        public string Version;
    }

    [DataContract]
    internal sealed class OpenListLogin
    {
        [DataMember(Name = "username")]
        public string Username;
        [DataMember(Name = "password")]
        public string Password;
        [DataMember(Name = "otp_code")]
        public string Otp;
    }

    [DataContract]
    internal sealed class OpenListToken
    {
        [DataMember(Name = "token")]
        public string Token;
    }

    [DataContract]
    internal sealed class OpenListUser
    {
        [DataMember(Name = "username")]
        public string Username;
    }

    [DataContract]
    internal sealed class OpenListUploadReply
    {
        [DataMember(Name = "task")]
        public OpenListTaskInfo Task;
    }

    [DataContract]
    internal sealed class OpenListTaskInfo
    {
        [DataMember(Name = "id")]
        public string Id;
        [DataMember(Name = "name")]
        public string Name;
        [DataMember(Name = "state")]
        public object State;
        [DataMember(Name = "progress")]
        public double Progress;
        [DataMember(Name = "error")]
        public string Error;
        [DataMember(Name = "total_bytes")]
        public long Size;
        internal int HttpStatus, ApiCode;
        internal string RawState
        {
            get
            {
                return State == null ? "缺失" : Convert.ToString(State, CultureInfo.InvariantCulture);
            }
        }

        internal string ProgressState
        {
            get
            {
                switch (RawState.Trim().ToLowerInvariant())
                {
                    case "0":
                    case "pending":
                        return "等待";
                    case "1":
                    case "running":
                        return "上传中";
                    case "2":
                    case "succeeded":
                        return "完成";
                    case "3":
                    case "canceling":
                    case "cancelling":
                        return "正在取消";
                    case "4":
                    case "canceled":
                    case "cancelled":
                        return "已取消";
                    case "5":
                    case "errored":
                        return "异常，等待重试";
                    case "6":
                    case "failing":
                        return "正在处理失败结果";
                    case "7":
                    case "failed":
                        return "失败";
                    case "8":
                    case "waitingretry":
                    case "waiting_retry":
                    case "waiting-retry":
                        return "等待重试";
                    case "9":
                    case "beforeretry":
                    case "before_retry":
                    case "before-retry":
                        return "即将重试";
                    default:
                        return null;
                }
            }
        }

        internal string Status
        {
            get
            {
                string s = RawState.Trim().ToLowerInvariant();
                switch (s)
                {
                    case "2":
                    case "succeeded":
                        return "succeeded";
                    case "4":
                    case "canceled":
                    case "cancelled":
                        return "canceled";
                    case "7":
                    case "failed":
                        return "failed";
                    case "3":
                    case "canceling":
                    case "cancelling":
                        return "canceling";
                    case "0":
                    case "pending":
                    case "1":
                    case "running":
                    case "5":
                    case "errored":
                    case "6":
                    case "failing":
                    case "8":
                    case "waitingretry":
                    case "waiting_retry":
                    case "waiting-retry":
                    case "9":
                    case "beforeretry":
                    case "before_retry":
                    case "before-retry":
                        return "running";
                    default:
                        return "unknown";
                }
            }
        }

        internal string Diagnostic
        {
            get
            {
                return "OpenList HTTP " + (HttpStatus > 0 ? HttpStatus.ToString(CultureInfo.InvariantCulture) : "未取得") + "，code=" +
                    (ApiCode > 0 ? ApiCode.ToString(CultureInfo.InvariantCulture) : "未取得") +
                    "，state=" +
                    RawState +
                    (string.IsNullOrEmpty(Error) ? "" : "；错误：" +
                    Error);
            }
        }
    }

    [DataContract]
    internal sealed class OpenListRename
    {
        [DataMember(Name = "path")]
        public string Path;
        [DataMember(Name = "name")]
        public string Name;
    }

    [DataContract]
    internal sealed class OpenListListRequest
    {
        [DataMember(Name = "path")]
        public string Path;
        [DataMember(Name = "password")]
        public string Password = "";
        [DataMember(Name = "page")]
        public int Page = 1;
        [DataMember(Name = "per_page")]
        public int PerPage = 10000;
        [DataMember(Name = "refresh")]
        public bool Refresh = true;
    }

    [DataContract]
    internal sealed class OpenListFileInfo
    {
        [DataMember(Name = "name")]
        public string Name;
        [DataMember(Name = "size")]
        public long Size;
        [DataMember(Name = "is_dir")]
        public bool Directory;
    }

    [DataContract]
    internal sealed class OpenListFileList
    {
        [DataMember(Name = "content")]
        public List<OpenListFileInfo> Content;
        [DataMember(Name = "total")]
        public int Total;
    }

    internal sealed class OpenListApiException : IOException
    {
        internal readonly int Code;
        internal OpenListApiException(int code, string message, int httpStatus = 0) : base("OpenList 接口失败（HTTP " + (httpStatus > 0 ? httpStatus.ToString(CultureInfo.InvariantCulture) : "未取得") + "，code=" + code +
            "）：" +
            message)
        {
            Code = code;
        }
    }

    public sealed partial class WebDavDestination
    {
        private sealed class OpenListCapability
        {
            internal readonly object Gate = new object();
            internal bool Detected, Available, Authorized;
            internal string ApiBase;
            internal Func<string, RemoteConnectionOptions, bool> RenewAuthorization;
            internal Func<bool> WebDavFallback;
            internal DirectoryRefreshCache Directories = new DirectoryRefreshCache();
            internal string DirectoryScope;
        }

        public void ShareDirectoryRefresh(DirectoryRefreshCache cache, string scope)
        {
            openListCapability.Directories = cache;
            openListCapability.DirectoryScope = scope;
        }

        private DirectoryRefreshCache DirectoryRefresh
        {
            get
            {
                return openListCapability.Directories;
            }
        }

        private string DirectoryScope
        {
            get
            {
                return openListCapability.DirectoryScope ?? Id;
            }
        }

        public Func<string, RemoteConnectionOptions, bool> RenewOpenListAuthorization
        {
            get
            {
                return openListCapability.RenewAuthorization;
            }

            set
            {
                openListCapability.RenewAuthorization = value;
            }
        }

        public Func<bool> OpenListWebDavFallback
        {
            get
            {
                return openListCapability.WebDavFallback;
            }

            set
            {
                openListCapability.WebDavFallback = value;
            }
        }

        private bool UsingOpenListWebDavFallback
        {
            get
            {
                var fallback = OpenListWebDavFallback;
                return fallback != null && fallback();
            }
        }

        private readonly OpenListCapability openListCapability;
        private object openListGate
        {
            get
            {
                return openListCapability.Gate;
            }
        }

        private bool openListDetected
        {
            get
            {
                return openListCapability.Detected;
            }

            set
            {
                openListCapability.Detected = value;
            }
        }

        private bool openListAvailable
        {
            get
            {
                return openListCapability.Available;
            }

            set
            {
                openListCapability.Available = value;
            }
        }

        private string openListBase
        {
            get
            {
                return openListCapability.ApiBase;
            }

            set
            {
                openListCapability.ApiBase = value;
            }
        }

        internal static int OpenListPollMilliseconds = 2000, OpenListStallMilliseconds = 360000, OpenListMetadataMilliseconds = 10000;
        internal static int OpenListLoginCooldownMilliseconds = 300000;
        private static readonly object loginCooldownGate = new object();
        private static readonly Dictionary<string, DateTime> loginCooldowns = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly object rejectedTokenGate = new object();
        private static readonly Dictionary<string, string> rejectedTokens = new Dictionary<string, string>(StringComparer.Ordinal);
        private static string OpenListTokenKey(string apiBase, string username, string token)
        {
            using (var hash = SHA256.Create())
                return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(apiBase + "\0" + username + "\0" + token)));
        }

        // Only a server-confirmed 401 rejects a token. Folder permission errors,
        // timeouts and unknown replies do not invalidate a shared account.
        private void RejectOpenListToken(string apiBase, string token, OpenListAuthorizationException error)
        {
            if (!error.TokenRejected || string.IsNullOrEmpty(token))
                return;
            lock (rejectedTokenGate)
                rejectedTokens[OpenListTokenKey(apiBase, connection.Username, token)] = error.Message;
            lock (openListGate)
                if (connection.OpenListToken == token)
                    openListCapability.Authorized = false;
        }

        public static void ValidateCachedOpenListAuthorization(string location, RemoteConnectionOptions options)
        {
            if (options == null || options.OpenListUploadOptimization == false || string.IsNullOrEmpty(options.OpenListToken))
                return;
            string apiBase = OpenListServiceAddress(location), detail;
            if (apiBase == null || options.OpenListApiBase != apiBase || options.OpenListUsername != options.Username)
                return;
            lock (rejectedTokenGate)
                if (!rejectedTokens.TryGetValue(OpenListTokenKey(apiBase, options.Username, options.OpenListToken), out detail))
                    return;
            throw new OpenListAuthorizationException(false, detail, true);
        }

        private void AcceptOpenListToken()
        {
            lock (rejectedTokenGate)
                rejectedTokens.Remove(OpenListTokenKey(openListBase, connection.Username, connection.OpenListToken));
        }

        private bool RenewRejectedOpenListAuthorization()
        {
            var renew = RenewOpenListAuthorization;
            if (renew == null)
                return false;
            OperationCancellation.ThrowIfCancellationRequested();
            RemoteConnectionOptions options;
            lock (openListGate)
                options = new RemoteConnectionOptions
                {
                    Username = connection.Username,
                    Password = connection.Password,
                    OpenListUploadOptimization = connection.OpenListUploadOptimization,
                    OpenListApiBase = connection.OpenListApiBase,
                    OpenListUsername = connection.OpenListUsername,
                    OpenListToken = connection.OpenListToken
                };
            // Authorization may require a UI dialog. Never hold the connection
            // gate while waiting for it, or replay a submitted payload here.
            if (!renew(root.AbsoluteUri, options))
                return false;
            OperationCancellation.ThrowIfCancellationRequested();
            UseOpenListAuthorization(options);
            return true;
        }

        private static OpenListAuthorizationException OpenListHttpAuthorizationFailure(int status)
        {
            return new OpenListAuthorizationException(false, "OpenList HTTP " + status + "：" + (status == 401 ? "授权已失效，请编辑同一 OpenList 服务、同一账号的任一仓库并保存以重新授权；原上传记录已保留。" : "没有查询权限，请检查账号权限。") + "未取得 code/state。", status == 401);
        }

        private static string OpenListBaseFor(Uri uri)
        {
            string path = uri.AbsolutePath;
            int at = path.IndexOf("/dav/", StringComparison.Ordinal);
            if (at < 0)
                return null;
            return uri.GetLeftPart(UriPartial.Authority) + path.Substring(0, at) + "/api/";
        }

        public static string OpenListServiceAddress(string location)
        {
            return OpenListBaseFor(RemotePath.Root(location, "http"));
        }

        public void UseOpenListAuthorization(RemoteConnectionOptions authorized)
        {
            if (authorized == null || string.IsNullOrEmpty(authorized.OpenListToken) || authorized.OpenListApiBase != OpenListBaseFor(root) ||
                authorized.OpenListUsername != connection.Username)
                return;
            lock (openListGate)
            {
                if (connection.OpenListToken == authorized.OpenListToken && connection.OpenListApiBase == authorized.OpenListApiBase)
                    return;
                connection.OpenListToken = authorized.OpenListToken;
                connection.OpenListApiBase = authorized.OpenListApiBase;
                connection.OpenListUsername = authorized.OpenListUsername;
                openListCapability.Authorized = false;
                DirectoryRefresh.Clear();
            }
        }

        private static string OpenListFilePath(Uri uri, string apiBase)
        {
            string prefix = new Uri(apiBase).AbsolutePath;
            prefix = prefix.Substring(0, prefix.Length - 4) + "dav/";
            if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
                throw new IOException("OpenList 文件路径不属于当前仓库。");
            return "/" + Uri.UnescapeDataString(uri.AbsolutePath.Substring(prefix.Length)).TrimStart('/');
        }

        private WebDavRequest OpenListRequest(string apiBase, string endpoint, string method, bool authorized, CancellationToken cancellation)
        {
            string expected = OpenListBaseFor(root);
            if (expected == null || !string.Equals(expected, apiBase, StringComparison.Ordinal))
                throw new IOException("OpenList 接口地址与当前仓库不一致，已保留原上传记录。");
            var lease = Request(new Uri(new Uri(apiBase), endpoint), method, cancellation);
            var request = lease.Request;
            request.Headers.Remove(HttpRequestHeader.Authorization);
            request.Timeout = OpenListMetadataMilliseconds;
            request.ReadWriteTimeout = OpenListMetadataMilliseconds;
            if (authorized)
            {
                if (connection.OpenListApiBase != apiBase || string.IsNullOrEmpty(connection.OpenListToken) || connection.OpenListUsername != connection.Username)
                {
                    lease.Dispose();
                    throw new OpenListAuthorizationException(false);
                }

                request.Headers[HttpRequestHeader.Authorization] = connection.OpenListToken;
            }

            return lease;
        }

        private static OpenListReply<T> ReadOpenList<T>(Stream input, CancellationToken token, int httpStatus = 0)
        {
            try
            {
                using (var memory = new MemoryStream())
                {
                    var buffer = new byte[8192];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        if (memory.Length + read > 8 * 1024 * 1024)
                            throw new IOException("OpenList 响应过大（HTTP " + httpStatus + "，未取得有效 code/state）。");
                        memory.Write(buffer, 0, read);
                    }

                    memory.Position = 0;
                    try
                    {
                        return (OpenListReply<T>)new DataContractJsonSerializer(typeof(OpenListReply<T>)).ReadObject(memory);
                    }
                    catch (SerializationException error)
                    {
                        throw new IOException("OpenList 返回无效 JSON（HTTP " + httpStatus + "，未取得有效 code/state）。", error);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (WebException error)
            {
                token.ThrowIfCancellationRequested();
                throw new IOException("OpenList 读取回应失败（HTTP " + httpStatus + "，未取得有效 code/state）：" + error.Status, error);
            }
            catch (IOException error)
            {
                token.ThrowIfCancellationRequested();
                if (error.Message.StartsWith("OpenList", StringComparison.Ordinal))
                    throw;
                throw new IOException("OpenList 读取回应失败（HTTP " + httpStatus + "，未取得有效 code/state）：" + error.Message, error);
            }
        }

        private static T OpenListResult<T>(OpenListReply<T> reply, int httpStatus = 0)
        {
            if (reply == null)
                throw new IOException("OpenList 返回空回应（HTTP " + httpStatus + "，未取得有效 code/state）。");
            string detail = "OpenList HTTP " + httpStatus + "，code=" + reply.Code + "：" + (reply.Message ?? "请求未完成");
            if (reply.Code == 401 || reply.Code == 403 || reply.Code == 402)
                throw new OpenListAuthorizationException(
                    reply.Code == 402,
                    detail + "；" +
                    (reply.Code == 402 ? "需要二步验证码。" : reply.Code == 401 ? "授权已失效，请编辑同一 OpenList 服务、同一账号的任一仓库并保存以重新授权；原上传记录已保留。" : "没有查询权限，请检查账号权限。"),
                    reply.Code == 401 ||
                    reply.Code == 402
                );
            if (reply.Code != 200)
                throw new OpenListApiException(reply.Code, reply.Message ?? "请求未完成", httpStatus);
            object value = reply.Data;
            if (value == null && (typeof(T) == typeof(OpenListTaskInfo) || typeof(T) == typeof(List<OpenListTaskInfo>)))
                throw new OpenListTaskQueryException("OpenList HTTP " + httpStatus + "，code=200：任务信息为空，未取得 state。");
            var info = value as OpenListTaskInfo;
            if (info != null)
            {
                info.HttpStatus = httpStatus;
                info.ApiCode = reply.Code;
            }

            var upload = value as OpenListUploadReply;
            if (upload != null && upload.Task != null)
            {
                upload.Task.HttpStatus = httpStatus;
                upload.Task.ApiCode = reply.Code;
            }

            var list = value as List<OpenListTaskInfo>;
            if (list != null)
                foreach (var task in list)
                    if (task != null)
                    {
                        task.HttpStatus = httpStatus;
                        task.ApiCode = reply.Code;
                    }

            return reply.Data;
        }

        private T OpenListCall<T>(string apiBase, string endpoint, string method, object body, bool authorized, CancellationToken token)
        {
            try
            {
                return OpenListCallOnce<T>(apiBase, endpoint, method, body, authorized, token);
            }
            catch (OpenListAuthorizationException error)
            {
                if (!authorized || endpoint == "me" || !error.TokenRejected && !error.RequiresTwoFactor && !string.IsNullOrEmpty(connection.OpenListToken) ||
                    !RenewRejectedOpenListAuthorization())
                    throw;
                return OpenListCallOnce<T>(apiBase, endpoint, method, body, true, token);
            }
        }

        private T OpenListCallOnce<T>(string apiBase, string endpoint, string method, object body, bool authorized, CancellationToken token)
        {
            using (var lease = OpenListRequest(apiBase, endpoint, method, authorized, token))
            {
                var request = lease.Request;
                string requestToken = authorized ? request.Headers[HttpRequestHeader.Authorization] : null;
                try
                {
                    if (body != null)
                    {
                        byte[] bytes;
                        using (var memory = new MemoryStream())
                        {
                            new DataContractJsonSerializer(body.GetType()).WriteObject(memory, body);
                            bytes = memory.ToArray();
                        }

                        try
                        {
                            request.ContentType = "application/json; charset=utf-8";
                            request.ContentLength = bytes.Length;
                            using (var stream = request.GetRequestStream())
                                stream.Write(bytes, 0, bytes.Length);
                        }
                        finally
                        {
                            Array.Clear(bytes, 0, bytes.Length);
                        }
                    }
                    else if (method == "POST")
                        request.ContentLength = 0;
                    using (var response = Response(lease))
                    using (var input = response.GetResponseStream())
                        return OpenListResult(ReadOpenList<T>(input, token, (int)response.StatusCode), (int)response.StatusCode);
                }
                catch (OpenListAuthorizationException error)
                {
                    if (authorized)
                        RejectOpenListToken(apiBase, requestToken, error);
                    throw;
                }
                catch (DavException error)
                {
                    if (error.Code <= 0)
                    {
                        var network = error.InnerException as WebException;
                        throw new IOException("OpenList 连接失败或查询超时（" + (network == null ? error.Message : network.Status.ToString()) + "，未取得 HTTP/code/state）。", error);
                    }

                    if (error.Code == 401 || error.Code == 403)
                    {
                        var failure = OpenListHttpAuthorizationFailure(error.Code);
                        if (authorized)
                            RejectOpenListToken(apiBase, requestToken, failure);
                        throw failure;
                    }

                    throw new IOException("OpenList HTTP " + error.Code + "：接口请求失败，未取得有效 code/state。", error);
                }
                catch (WebException error)
                {
                    lease.Failure(error);
                    lease.CheckCancellation();
                    throw new IOException("OpenList 连接失败或查询超时（" + error.Status + "，未取得 HTTP/code/state）。", error);
                }
            }
        }

        private bool DetectOpenList()
        {
            lock (openListGate)
            {
                if (openListDetected)
                    return openListAvailable;
                string candidate = OpenListBaseFor(root);
                if (candidate == null)
                {
                    openListDetected = true;
                    return false;
                }

                try
                {
                    var version = OpenListCall<OpenListVersion>(candidate, "public/settings", "GET", null, false, OperationCancellation);
                    openListAvailable = version != null && version.Version != null && Regex.IsMatch(version.Version, @"\Av4\.\d+\.\d+.*\(Commit:.*Frontend:");
                    if (openListAvailable)
                        openListBase = candidate;
                    openListDetected = true;
                    return openListAvailable;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (IOException)
                {
                    OperationCancellation.ThrowIfCancellationRequested();
                    openListDetected = true;
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    openListDetected = true;
                    return false;
                }
            }
        }

        private void EnsureOpenListAuthorization(string otp = null)
        {
            try
            {
                EnsureOpenListAuthorizationCore(otp);
            }
            catch (OpenListAuthorizationException error)
            {
                if (otp != null || !error.TokenRejected && !error.RequiresTwoFactor && !string.IsNullOrEmpty(connection.OpenListToken) || !RenewRejectedOpenListAuthorization())
                    throw;
                EnsureOpenListAuthorizationCore();
            }
        }

        private void EnsureOpenListAuthorizationCore(string otp = null)
        {
            lock (openListGate)
            {
                if (otp == null)
                    ValidateCachedOpenListAuthorization(root.AbsoluteUri, connection);
                if (connection.OpenListApiBase == openListBase && connection.OpenListUsername == connection.Username && !string.IsNullOrEmpty(connection.OpenListToken))
                {
                    if (openListCapability.Authorized)
                        return;
                    try
                    {
                        var user = OpenListCall<OpenListUser>(openListBase, "me", "GET", null, true, OperationCancellation);
                        if (user != null && user.Username == connection.Username)
                        {
                            openListCapability.Authorized = true;
                            AcceptOpenListToken();
                            return;
                        }
                    }
                    catch (OpenListAuthorizationException)
                    {
                        if (otp == null)
                            throw;
                    }

                    connection.OpenListToken = "";
                }

                // Login is only submitted after the user confirms the authorization
                // form. Missing/invalid tokens during backup never trigger password
                // or empty-OTP probes, including the normal upload retry loop.
                if (otp == null)
                    throw new OpenListAuthorizationException(false);
                lock (loginCooldownGate)
                {
                    DateTime until;
                    if (loginCooldowns.TryGetValue(openListBase, out until) && until > DateTime.UtcNow)
                        throw new OpenListLoginThrottledException();
                }

                OpenListToken result;
                try
                {
                    result = OpenListCall<OpenListToken>(openListBase, "auth/login", "POST", new OpenListLogin { Username = connection.Username, Password = connection.Password, Otp = otp }, false, OperationCancellation);
                }
                catch (OpenListApiException error)
                {
                    if (error.Code != 429)
                        throw;
                    lock (loginCooldownGate)
                        loginCooldowns[openListBase] = DateTime.UtcNow.AddMilliseconds(OpenListLoginCooldownMilliseconds);
                    throw new OpenListLoginThrottledException();
                }
                catch (DavException error)
                {
                    if (error.Code != 429)
                        throw;
                    lock (loginCooldownGate)
                        loginCooldowns[openListBase] = DateTime.UtcNow.AddMilliseconds(OpenListLoginCooldownMilliseconds);
                    throw new OpenListLoginThrottledException();
                }

                if (result == null || string.IsNullOrEmpty(result.Token))
                    throw new OpenListAuthorizationException(false);
                connection.OpenListToken = result.Token;
                connection.OpenListApiBase = openListBase;
                connection.OpenListUsername = connection.Username;
                openListCapability.Authorized = true;
                AcceptOpenListToken();
            }
        }

        // Called explicitly when saving a warehouse. No token is ever stored in the
        // upload journal: the desktop encrypts the complete connection settings.
        public static bool AuthorizeOpenList(string location, RemoteConnectionOptions options, string otp = null, CancellationToken cancellation = default(CancellationToken))
        {
            if (options.OpenListUploadOptimization == false)
                return false;
            using (var driver = new WebDavDestination("authorization", location, options))
            {
                driver.OperationCancellation = cancellation;
                if (!driver.DetectOpenList())
                    return false;
                driver.EnsureOpenListAuthorization(otp);
                return true;
            }
        }
    }
}
