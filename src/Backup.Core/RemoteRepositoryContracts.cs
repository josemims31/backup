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
    public sealed class RemoteConnectionOptions
    {
        public string Username = "";
        public string Password = "";
        public string ServerFingerprint = "";
        [System.Runtime.Serialization.IgnoreDataMember]
        public RemoteServerTrust ServerTrust;
        public string AuthenticationMode = "password";
        public string PrivateKeyPath = "";
        public string PrivateKeyPassphrase = "";
        public bool? OpenListUploadOptimization;
        public string OpenListApiBase = "", OpenListToken = "", OpenListUsername = "";
    }

    public sealed class RemoteFolder
    {
        public string Name { get; set; }
        public string Location { get; set; }
    }

    public sealed class RemoteCertificateException : IOException
    {
        public RemoteCertificateException() : base("WebDAV 服务器证书未通过验证，请核对证书或固定指纹。")
        {
        }
    }

    public interface IRemoteFolderBrowser
    {
        IList<RemoteFolder> ListFolders();
        void CreateFolder(string name);
    }

    public interface ICancellableRemoteFolderBrowser : IRemoteFolderBrowser
    {
        IList<RemoteFolder> ListFolders(CancellationToken cancellation);
    }

    public static class RemotePath
    {
        public static bool IsSftpPortChange(DestinationDefinition original, DestinationDefinition proposed)
        {
            if (original == null || proposed == null || original.Id != proposed.Id || original.Protocol != "sftp" || proposed.Protocol != "sftp")
                return false;
            try
            {
                var left = Root(original.Location, "sftp");
                var right = Root(proposed.Location, "sftp");
                // A port selects the connection endpoint. It does not change the
                // registered directory or authorize deleting objects through the old endpoint.
                return left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase) && left.AbsolutePath.Equals(right.AbsolutePath, StringComparison.Ordinal);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        public static Uri Root(string location, string scheme)
        {
            Uri uri;
            if (!Uri.TryCreate(location, UriKind.Absolute, out uri) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
                uri.Scheme != scheme &&
                !(scheme == "http" &&
                uri.Scheme == "https"))
                throw new ArgumentException("远程地址无效，请勿在地址内填写密码、查询串或片段。");
            return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
        }

        public static string Name(string name)
        {
            if (string.IsNullOrEmpty(name) || name == "." || name == ".." || name.IndexOfAny(new[] { '/', '\\', ':', '\0' }) >= 0)
                throw new ArgumentException("无效的远程文件名。");
            return name;
        }

        public static string Append(string location, string name)
        {
            return new Uri(new Uri(location.TrimEnd('/') + "/"), Uri.EscapeDataString(Name(name))).AbsoluteUri;
        }

        public static string Fingerprint(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
