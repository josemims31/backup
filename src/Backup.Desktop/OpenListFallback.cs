using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed class OpenListFallbackNotice
    {
        public string Service, Username, CreatedUtc, Reason;
    }

    public sealed partial class DesktopBackend
    {
        private const string OpenListFallbackSetting = "openlist_webdav_fallback_v1";
        private readonly object authorizationFlightsGate = new object();
        private readonly Dictionary<string, TaskCompletionSource<bool>> authorizationFlights = new Dictionary<string, TaskCompletionSource<bool>>(StringComparer.Ordinal);
        public Func<string, RemoteConnectionOptions, CancellationToken, bool> OpenListAuthorizationPrompt;
        public event Action OpenListFallbackChanged;
        private List<OpenListFallbackNotice> OpenListFallbackNotices()
        {
            return JsonSettings.Decode(Catalog.GetSetting(OpenListFallbackSetting), new List<OpenListFallbackNotice>());
        }

        private bool OpenListFallbackActive(string location, string username)
        {
            string service = OpenListAuthorization.OpenListServiceAddress(location);
            return service != null && OpenListFallbackNotices().Any(n => n.Service == service && n.Username == username);
        }

        private readonly object fallbackSnapshotGate = new object();
        private long fallbackSnapshotStamp = long.MinValue;
        private Dictionary<string, OpenListFallbackNotice> fallbackSnapshot;
        private Dictionary<string, OpenListFallbackNotice> OpenListFallbackSnapshot(StorageTopology topology = null)
        {
            long stamp = Catalog.DataChangeStamp;
            lock (fallbackSnapshotGate)
            {
                if (fallbackSnapshot != null && fallbackSnapshotStamp == stamp)
                    return fallbackSnapshot;
            }

            var notices = OpenListFallbackNotices();
            var result = new Dictionary<string, OpenListFallbackNotice>(StringComparer.Ordinal);
            if (notices.Count > 0)
                foreach (var definition in (topology ?? Catalog.LoadTopology()).Destinations.Where(d => d.Protocol == "webdav"))
                {
                    string service = OpenListAuthorization.OpenListServiceAddress(definition.Location);
                    if (!notices.Any(n => n.Service == service))
                        continue;
                    try
                    {
                        var options = LoadConnection(definition.Id);
                        if (options.OpenListUploadOptimization == false)
                            continue;
                        var notice = notices.FirstOrDefault(n => n.Service == service && n.Username == options.Username);
                        if (notice != null)
                            result[definition.Id] = notice;
                    }
                    catch (IOException diagnosticFailure)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/OpenListFallback.cs:OpenListFallbackSnapshot", diagnosticFailure);
                    }
                }

            lock (fallbackSnapshotGate)
            {
                if (stamp == Catalog.DataChangeStamp)
                {
                    fallbackSnapshot = result;
                    fallbackSnapshotStamp = stamp;
                }
            }

            return result;
        }

        private OpenListFallbackNotice OpenListFallbackFor(string id)
        {
            OpenListFallbackNotice notice;
            return OpenListFallbackSnapshot().TryGetValue(id, out notice) ? notice : null;
        }

        private void PublishOpenListFallback()
        {
            foreach (var definition in Catalog.LoadTopology().Destinations.Where(d => d.Protocol == "webdav"))
                RepositoryConnectionChanged(definition.Id);
            var changed = OpenListFallbackChanged;
            if (changed != null)
                foreach (Action observer in changed.GetInvocationList())
                    try
                    {
                        observer();
                    }
                    catch (Exception error)
                    {
                        RuntimeLog.Append(BaseDirectory, "ui-errors.log", "授权回退提示刷新未完成：" + error.Message);
                    }
        }

        private void SaveOpenListFallback(string location, RemoteConnectionOptions options, string reason)
        {
            string service = OpenListAuthorization.OpenListServiceAddress(location);
            if (service == null)
                return;
            lock (authorizationFlightsGate)
            {
                var notices = OpenListFallbackNotices();
                notices.RemoveAll(n => n.Service == service && n.Username == options.Username);
                notices.Add(new OpenListFallbackNotice { Service = service, Username = options.Username, CreatedUtc = DateTime.UtcNow.ToString("o"), Reason = reason });
                Catalog.SetSetting(OpenListFallbackSetting, JsonSettings.Encode(notices));
            }

            RuntimeLog.Append(BaseDirectory, "upload-errors.log", "OpenList 授权未完成，已回退普通 WebDAV：" + service + " · " + reason);
            PublishOpenListFallback();
        }

        private void ClearOpenListFallback(RemoteConnectionOptions options)
        {
            if (string.IsNullOrEmpty(options.OpenListToken))
                return;
            bool changed = false;
            lock (authorizationFlightsGate)
            {
                var notices = OpenListFallbackNotices();
                changed = notices.RemoveAll(n => n.Service == options.OpenListApiBase && n.Username == options.Username) > 0;
                if (changed)
                {
                    if (notices.Count == 0)
                        Catalog.RemoveSetting(OpenListFallbackSetting);
                    else
                        Catalog.SetSetting(OpenListFallbackSetting, JsonSettings.Encode(notices));
                }
            }

            if (changed)
                PublishOpenListFallback();
        }

        public string OpenListFallbackSummary()
        {
            if (OpenListFallbackNotices().Count == 0)
                return null;
            var topology = Catalog.LoadTopology();
            var notices = OpenListFallbackSnapshot(topology);
            var names = topology.Destinations.Where(d => notices.ContainsKey(d.Id)).Select(d => topology.Groups.Where(g => g.Id == "single:" + d.Id).Select(g => g.Name).FirstOrDefault() ?? d.Id).ToList();
            return names.Count == 0 ? null : "OpenList 授权待恢复，已改用普通 WebDAV：" + string.Join("、", names) + "。请编辑仓库重新授权；已提交的文件继续核对，不重复上传。";
        }

        private bool RequestOpenListAuthorization(string location, RemoteConnectionOptions options)
        {
            var token = ActiveBackup == null ? OperationCancellation : ActiveBackup.Token;
            token.ThrowIfCancellationRequested();
            string previous = options.OpenListToken;
            LoadSharedOpenListAuthorization(location, options);
            if (previous != options.OpenListToken)
                try
                {
                    OpenListAuthorization.ValidateCachedOpenListAuthorization(location, options);
                    return true;
                }
                catch (OpenListAuthorizationException diagnosticFailure)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/OpenListFallback.cs:RequestOpenListAuthorization", diagnosticFailure);
                }

            if (OpenListFallbackActive(location, options.Username))
                return false;
            var prompt = OpenListAuthorizationPrompt;
            var legacy = repairOpenListAuthorization;
            if (prompt == null && legacy == null)
                return false;
            string key = OpenListSessionKey(OpenListAuthorization.OpenListServiceAddress(location), options.Username);
            TaskCompletionSource<bool> flight;
            bool leader = false;
            lock (authorizationFlightsGate)
            {
                if (OpenListFallbackActive(location, options.Username))
                    return false;
                previous = options.OpenListToken;
                LoadSharedOpenListAuthorization(location, options);
                if (previous != options.OpenListToken)
                    try
                    {
                        OpenListAuthorization.ValidateCachedOpenListAuthorization(location, options);
                        return true;
                    }
                    catch (OpenListAuthorizationException diagnosticFailure)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/OpenListFallback.cs:RequestOpenListAuthorization", diagnosticFailure);
                    }

                if (!authorizationFlights.TryGetValue(key, out flight))
                {
                    if (prompt == null)
                        lock (repairAuthorizationGate)
                        {
                            if (!repairAuthorizations.Add(key))
                                return false;
                        }

                    flight = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    authorizationFlights.Add(key, flight);
                    leader = true;
                }
            }

            if (leader)
            {
                try
                {
                    bool accepted = prompt != null ? prompt(location, options, token) : legacy(location, options);
                    token.ThrowIfCancellationRequested();
                    if (accepted)
                        SaveSharedOpenListAuthorization(options);
                    else if (prompt != null)
                        SaveOpenListFallback(location, options, "5 分钟内未完成授权，或已选择改用 WebDAV。");
                    flight.TrySetResult(accepted);
                }
                catch (OperationCanceledException)
                {
                    flight.TrySetCanceled();
                }
                catch (Exception error)
                {
                    try
                    {
                        if (prompt != null && !token.IsCancellationRequested)
                        {
                            SaveOpenListFallback(location, options, "重新授权未完成：" + error.Message);
                            flight.TrySetResult(false);
                        }
                        else
                            flight.TrySetException(error);
                    }
                    catch (Exception notificationError)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/OpenListFallback.cs:RequestOpenListAuthorization", notificationError);
                        flight.TrySetException(notificationError);
                    }
                }
                finally
                {
                    lock (authorizationFlightsGate)
                        authorizationFlights.Remove(key);
                }
            }

            int completed = WaitHandle.WaitAny(new[] { ((IAsyncResult)flight.Task).AsyncWaitHandle, token.WaitHandle });
            if (completed == 1)
                token.ThrowIfCancellationRequested();
            bool result = flight.Task.GetAwaiter().GetResult();
            if (result)
                LoadSharedOpenListAuthorization(location, options);
            return result;
        }
    }

    public sealed partial class MainWindowController
    {
        private void InitializeOpenListFallback()
        {
            Backend.OpenListAuthorizationPrompt = (location, options, token) => Window.Dispatcher.Invoke(new Func<Task<bool>>(() => EditorDialogs.AuthorizeOpenListDuringBackup(Window, location, options, OpenListServices, token)))
                .GetAwaiter()
                .GetResult();
            Backend.OpenListFallbackChanged += OpenListFallbackChanged;
            RefreshOpenListFallbackNotice();
        }

        private void OpenListFallbackChanged()
        {
            if (!disposed && !Window.Dispatcher.HasShutdownStarted)
                Window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!disposed)
                    {
                        RefreshOpenListFallbackNotice();
                        UpdateRepositoryConnectionViews();
                    }
                }));
        }

        private void RefreshOpenListFallbackNotice()
        {
            var banner = Control<Border>("OpenListFallbackBanner");
            var text = Control<TextBox>("OpenListFallbackNotice");
            if (banner == null || text == null)
                return;
            string notice = Backend.OpenListFallbackSummary();
            text.Text = notice ?? "";
            banner.Visibility = string.IsNullOrEmpty(notice) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
