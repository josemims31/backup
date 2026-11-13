using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Backup.Updates;

namespace Backup.Desktop
{
#pragma warning disable 0649 // These fields are populated by the GitHub JSON response.

    [DataContract]
    internal sealed class GitHubAsset
    {
        [DataMember]
        public string name, browser_download_url, state, digest;
        [DataMember]
        public long size;
    }

    [DataContract]
    internal sealed class GitHubRelease
    {
        [DataMember]
        public string tag_name, body, html_url;
        [DataMember]
        public bool draft, prerelease;
        [DataMember]
        public GitHubAsset[] assets;
    }

#pragma warning restore 0649
    [DataContract]
    internal sealed class AvailableUpdate
    {
        [DataMember]
        public string Version, Notes, PageUrl, DownloadUrl, Sha256;
        [DataMember]
        public long Size;
    }

    [DataContract]
    internal sealed class UpdatePreferences
    {
        [DataMember]
        public int Mode = 1;
        [DataMember]
        public string LastAttemptUtc, DeferUntilUtc, IgnoredVersion, PreparedPath, PreparedVersion, Status, LastResultUtc, DownloadAttemptVersion, DownloadAttemptUtc;
        [DataMember]
        public bool InstallRequested;
        [DataMember]
        public AvailableUpdate Release;
        [OnDeserializing]
        private void Defaults(StreamingContext context)
        {
            Mode = 1;
        }
    }

    internal static class UpdatePolicy
    {
        internal static bool Due(UpdatePreferences state, DateTime now)
        {
            DateTime last;
            return state.Mode != 2 &&
                (!DateTime.TryParse(state.LastAttemptUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out last) || last.ToLocalTime().Date < now.ToLocalTime().Date);
        }

        internal static bool Deferred(UpdatePreferences state, DateTime now)
        {
            DateTime until;
            return DateTime.TryParse(state.DeferUntilUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out until) && until.ToUniversalTime() > now.ToUniversalTime();
        }

        internal static bool Newer(AvailableUpdate update, string current)
        {
            return update != null && UpdateProtocol.ParseVersion(update.Version) > UpdateProtocol.ParseVersion(current);
        }

        internal static bool ShowNotice(UpdatePreferences state, string current, DateTime now)
        {
            return Newer(state.Release, current) && state.Release.Version != state.IgnoredVersion && !Deferred(state, now);
        }
    }

    internal sealed class GitHubUpdateClient
    {
        private readonly Uri endpoint = new Uri(UpdateProtocol.ReleaseApi);
#if UPDATE_TEST
        internal GitHubUpdateClient(Uri fixture)
        {
            if (!fixture.IsLoopback)
                throw new ArgumentException("Only loopback fixtures are permitted.");
            endpoint = fixture;
        }

        internal Func<AvailableUpdate, CancellationToken, AvailableUpdate> PackageRefreshFixture;
#endif
        internal GitHubUpdateClient()
        {
        }

        private HttpWebRequest Request(Uri uri)
        {
            if (uri.Scheme != Uri.UriSchemeHttps 
#if UPDATE_TEST
            && !uri.IsLoopback
#endif
            )
                throw new IOException("更新请求必须使用 HTTPS。");
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.UserAgent = "Backup-fang523/" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            request.Accept = "application/vnd.github+json";
            request.Timeout = 20000;
            request.ReadWriteTimeout = 30000;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.AllowAutoRedirect = false;
            request.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            request.Headers[HttpRequestHeader.CacheControl] = "no-cache";
            request.ServerCertificateValidationCallback = delegate (
                object sender,
                System.Security.Cryptography.X509Certificates.X509Certificate certificate,
                System.Security.Cryptography.X509Certificates.X509Chain chain,
                SslPolicyErrors errors
            )
            {
                return errors == SslPolicyErrors.None;
            };
            return request;
        }

        private HttpWebResponse Open(Uri uri, CancellationToken token, out HttpWebRequest active)
        {
            active = null;
            for (int redirects = 0; redirects < 6; redirects++)
            {
                token.ThrowIfCancellationRequested();
                active = Request(uri);
                HttpWebResponse response;
                using (token.Register(active.Abort))
                    try
                    {
                        response = (HttpWebResponse)active.GetResponse();
                    }
                    catch (WebException)
                    {
                        token.ThrowIfCancellationRequested();
                        throw;
                    }

                int code = (int)response.StatusCode;
                if (code >= 300 && code <= 399)
                {
                    string location = response.Headers[HttpResponseHeader.Location];
                    response.Dispose();
                    Uri next;
                    if (string.IsNullOrEmpty(location) || !Uri.TryCreate(uri, location, out next) || next.Scheme != Uri.UriSchemeHttps 
#if UPDATE_TEST
                    && !next.IsLoopback
#endif
                    )
                        throw new IOException("更新服务器返回了无效的重定向。");
                    uri = next;
                    continue;
                }

                return response;
            }

            throw new IOException("更新请求重定向次数过多。");
        }

        internal static AvailableUpdate Interpret(string json)
        {
            var release = UpdateProtocol.ReadJson<GitHubRelease>(json);
            if (release == null || release.draft || release.prerelease)
                throw new InvalidDataException("发布信息不是正式版本。");
            string version = UpdateProtocol.ParseVersion(release.tag_name).ToString(3);
            var asset = (release.assets ?? new GitHubAsset[0]).FirstOrDefault(item => item != null && string.Equals(item.name, UpdateProtocol.AssetName, StringComparison.OrdinalIgnoreCase) &&
                item.state == "uploaded");
            var result = new AvailableUpdate
            {
                Version = version,
                Notes = (release.body ?? "").Trim(),
                PageUrl = UpdateProtocol.RepositoryUrl + "/releases/tag/" + Uri.EscapeDataString(release.tag_name)
            };
            if (asset != null)
            {
                Uri uri;
                if (!Uri.TryCreate(asset.browser_download_url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                    !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                    !uri.AbsolutePath.StartsWith("/josemims31/backup/releases/download/", StringComparison.OrdinalIgnoreCase) ||
                    asset.size < 1 ||
                    asset.size > 268435456)
                    throw new InvalidDataException("发布的 Windows 更新包地址或大小无效。");
                result.DownloadUrl = uri.AbsoluteUri;
                result.Size = asset.size;
                if (!string.IsNullOrEmpty(asset.digest))
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(asset.digest, @"\Asha256:[0-9a-fA-F]{64}\z"))
                        throw new InvalidDataException("发布的更新包校验信息无效。");
                    result.Sha256 = asset.digest.Substring(7);
                }
            }

            return result;
        }

        internal Task<AvailableUpdate> LatestAsync(CancellationToken token)
        {
            return Task.Run(() => ReadRelease(endpoint, token), token);
        }

        private AvailableUpdate ReadRelease(Uri address, CancellationToken token)
        {
            try
            {
                HttpWebRequest active;
                using (var response = Open(address, token, out active))
                using (token.Register(active.Abort))
                using (var stream = response.GetResponseStream())
                using (var memory = new MemoryStream())
                {
                    var buffer = new byte[16384];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        if (memory.Length + read > 2097152)
                            throw new InvalidDataException("发布信息过大。");
                        memory.Write(buffer, 0, read);
                    }

                    return Interpret(Encoding.UTF8.GetString(memory.ToArray()));
                }
            }
            catch (WebException error)
            {
                token.ThrowIfCancellationRequested();
                using (var response = error.Response as HttpWebResponse)
                {
                    if (response != null && response.StatusCode == HttpStatusCode.NotFound)
                        return null;
                    if (response != null && (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429))
                        throw new IOException("GitHub 暂时限制了更新查询，请稍后手动重试。", error);
                }

                throw new IOException("无法连接 GitHub 检查更新：" + error.Message, error);
            }
        }

        private sealed class PackageMetadataMismatch : IOException
        {
            internal PackageMetadataMismatch(string message) : base(message)
            {
            }
        }

        private AvailableUpdate RefreshPackage(AvailableUpdate release, CancellationToken token)
        {
#if UPDATE_TEST
            if (PackageRefreshFixture != null)
                return PackageRefreshFixture(release, token);
#endif
            string prefix = UpdateProtocol.RepositoryUrl + "/releases/tag/";
            if (string.IsNullOrEmpty(release.PageUrl) || !release.PageUrl.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidDataException("更新版本的发布地址无效，请点击“检查更新”后重试。");
            string tag = Uri.UnescapeDataString(release.PageUrl.Substring(prefix.Length));
            if (UpdateProtocol.ParseVersion(tag) != UpdateProtocol.ParseVersion(release.Version))
                throw new InvalidDataException("更新版本与发布地址不一致，请点击“检查更新”后重试。");
            // Refresh the selected tag, never silently switch a pending download to a different release.
            return ReadRelease(new Uri("https://api.github.com/repos/josemims31/backup/releases/tags/" + Uri.EscapeDataString(tag)), token);
        }

        private void DownloadArchive(Uri uri, AvailableUpdate release, string archive, Action<long, long> progress, CancellationToken token)
        {
            HttpWebRequest active;
            using (var response = Open(uri, token, out active))
            using (token.Register(active.Abort))
            using (var input = response.GetResponseStream())
            {
                if (response.ContentLength > 268435456)
                    throw new InvalidDataException("更新包大小超出允许上限。");
                if (response.ContentLength >= 0 && response.ContentLength != release.Size)
                    throw new PackageMetadataMismatch("更新包大小与登记值不一致（登记：" + release.Size + " 字节，服务器返回：" + response.ContentLength + " 字节）。");
                using (var output = File.Create(archive))
                {
                    var buffer = new byte[65536];
                    long count = 0;
                    int read;
                    var report = Stopwatch.StartNew();
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        count += read;
                        if (count > 268435456)
                            throw new InvalidDataException("更新包大小超出允许上限。");
                        if (count > release.Size)
                            throw new PackageMetadataMismatch("更新包大小超出登记值（登记：" + release.Size + " 字节，已接收：" + count + " 字节）。");
                        output.Write(buffer, 0, read);
                        if (progress != null && report.ElapsedMilliseconds >= 1000)
                        {
                            progress(count, release.Size);
                            report.Restart();
                        }
                    }

                    if (count != release.Size)
                        throw new PackageMetadataMismatch("更新包尚未下载完整（登记：" + release.Size + " 字节，实际：" + count + " 字节）。");
                }
            }

            if (!string.IsNullOrEmpty(release.Sha256) && !UpdateProtocol.Hash(archive).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new PackageMetadataMismatch("更新包 SHA-256 校验失败。");
        }

        internal Task<string> DownloadAsync(AvailableUpdate release, string installation, Action<long, long> progress, CancellationToken token)
        {
            return Task.Run(
                delegate
            {
                Uri uri;
                if (release == null || string.IsNullOrEmpty(release.DownloadUrl) || !Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out uri))
                    throw new InvalidDataException("此版本尚未提供 Windows 更新包。");
                if ((uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                    !uri.AbsolutePath.StartsWith("/josemims31/backup/releases/download/", StringComparison.OrdinalIgnoreCase)) 
#if UPDATE_TEST
                &&
                    !uri.IsLoopback
#endif
                )
                    throw new InvalidDataException("更新包必须来自项目的 GitHub Release。");
                string session = Path.Combine(UpdateProtocol.UpdatesDirectory(installation), "session-" + Guid.NewGuid().ToString("N"));
                UpdateProtocol.NoLinks(session);
                Directory.CreateDirectory(session);
                string archive = Path.Combine(session, "update.zip"), payload = Path.Combine(session, "payload");
                try
                {
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        timeout.CancelAfter(TimeSpan.FromMinutes(20));
                        var selected = release;
                        for (int attempt = 0;; attempt++)
                            try
                            {
                                DownloadArchive(uri, selected, archive, progress, timeout.Token);
                                break;
                            }
                            catch (PackageMetadataMismatch error)
                            {
                                if (attempt != 0)
                                    throw new IOException(error.Message + " 请点击“检查更新”后重试。", error);
                                var refreshed = RefreshPackage(selected, timeout.Token);
                                if (refreshed == null || UpdateProtocol.ParseVersion(refreshed.Version) != UpdateProtocol.ParseVersion(selected.Version) || string.IsNullOrEmpty(refreshed.DownloadUrl) ||
                                    !string.Equals(refreshed.DownloadUrl, selected.DownloadUrl, StringComparison.Ordinal) ||
                                    refreshed.Size < 1 ||
                                    refreshed.Size > 268435456 ||
                                    !string.IsNullOrEmpty(selected.Sha256) &&
                                    string.IsNullOrEmpty(refreshed.Sha256))
                                    throw new IOException("该版本更新包的发布信息已失效，请点击“检查更新”后重试。", error);
                                if (refreshed.Size == selected.Size && string.Equals(refreshed.Sha256, selected.Sha256, StringComparison.OrdinalIgnoreCase))
                                    throw new IOException(error.Message + " 发布信息未变化，请稍后重新检查更新。", error);
                                selected = refreshed;
                                release.Size = selected.Size;
                                release.Sha256 = selected.Sha256;
                            }

                        UpdatePackage.Extract(archive, payload, selected.Version, timeout.Token);
                        token.ThrowIfCancellationRequested();
                    }

                    File.Delete(archive);
                    return payload;
                }
                catch
                {
                    try
                    {
                        UpdateProtocol.NoLinks(session);
                        Directory.Delete(session, true);
                    }
                    catch (Exception diagnosticFailure)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/GitHubUpdates.cs:DownloadAsync", diagnosticFailure);
                    }

                    token.ThrowIfCancellationRequested();
                    throw;
                }
            },
                token
            );
        }
    }
}
