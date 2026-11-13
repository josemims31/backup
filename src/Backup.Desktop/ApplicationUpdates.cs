using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Backup.Core;
using Backup.Updates;

namespace Backup.Desktop
{
    public sealed partial class MainWindowController
    {
        private UpdatePreferences updatePreferences;
        private readonly GitHubUpdateClient updateClient = new GitHubUpdateClient();
        private readonly CancellationTokenSource updateLifetime = new CancellationTokenSource();
        private CancellationTokenSource updateDownloadCancellation;
        private DispatcherTimer updateTimer;
        private AboutWindow aboutWindow;
        private Task updateCheckTask;
        private bool updateDownloading, updateClosing, updateExitWaiting;
        private string updateHelperRequest;
        private string InstalledVersion
        {
            get
            {
                return Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            }
        }

        private string UpdateStatePath
        {
            get
            {
                return Path.Combine(UpdateProtocol.UpdatesDirectory(Backend.BaseDirectory), "state.json");
            }
        }

        private const string UpdateResumeKey = "software_update_ui_resume_v1";
#if UI_TEST
        internal Func<CancellationToken, Task<AvailableUpdate>> UpdateCheckFixture;
        internal Func<AvailableUpdate, CancellationToken, Task<string>> UpdateDownloadFixture;
        internal Action<bool> UpdateInstallFixture;
#endif
        private void InitializeApplicationUpdates()
        {
            Button("About").Click += delegate
            {
                OpenAbout();
            };
            Button("UpdateAvailable").Click += delegate
            {
                OpenAbout();
            };
            updatePreferences = new UpdatePreferences();
            try
            {
                string path = UpdateStatePath;
                UpdateProtocol.NoLinks(path);
                if (File.Exists(path))
                {
                    if (new FileInfo(path).Length > 1048576)
                        throw new IOException("更新设置文件过大。");
                    updatePreferences = UpdateProtocol.ReadJson<UpdatePreferences>(File.ReadAllText(path, System.Text.Encoding.UTF8)) ?? new UpdatePreferences();
                }

                if (updatePreferences.Mode < 0 || updatePreferences.Mode > 2)
                    updatePreferences.Mode = 1;
                if (updatePreferences.Release != null)
                    UpdateProtocol.ParseVersion(updatePreferences.Release.Version);
                if (!string.IsNullOrEmpty(updatePreferences.PreparedPath))
                    UpdateInstaller.SessionDirectory(Backend.BaseDirectory, updatePreferences.PreparedPath);
                if (!string.IsNullOrEmpty(updatePreferences.PreparedVersion) &&
                    UpdateProtocol.ParseVersion(updatePreferences.PreparedVersion) <= UpdateProtocol.ParseVersion(InstalledVersion))
                {
                    updatePreferences.PreparedPath = null;
                    updatePreferences.PreparedVersion = null;
                    updatePreferences.InstallRequested = false;
                }

                ReadUpdateResult();
                RestoreUpdateUiState();
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/ApplicationUpdates.cs:InitializeApplicationUpdates", error);
                updatePreferences = new UpdatePreferences
                {
                    Mode = 2,
                    Status = "读取更新设置未完成：" + error.Message
                };
                Log(updatePreferences.Status);
            }

            RefreshUpdatePresentation();
            operations.Changed += UpdateOperationChanged;
            updateTimer = new DispatcherTimer(DispatcherPriority.Background, Window.Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            updateTimer.Tick += async delegate
            {
                if (disposed || closeAfterWork || updateClosing)
                    return;
                await CheckForUpdatesAsync(false);
                TryApplyApplicationUpdate();
            };
            Window.Loaded += async delegate
            {
                updateTimer.Start();
                await CheckForUpdatesAsync(false);
                if (initialFileLists != null)
                    try
                    {
                        await initialFileLists;
                    }
                    catch (Exception diagnosticFailure)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/ApplicationUpdates.cs:InitializeApplicationUpdates", diagnosticFailure);
                    }

                TryApplyApplicationUpdate();
            };
            Window.Closed += delegate
            {
                LaunchPreparedUpdater();
            };
        }

        private void ReadUpdateResult()
        {
            string path = Path.Combine(UpdateProtocol.UpdatesDirectory(Backend.BaseDirectory), "result.json");
            if (!File.Exists(path))
                return;
            UpdateProtocol.NoLinks(path);
            if (new FileInfo(path).Length > 65536)
                return;
            var result = UpdateTransactions.Read<UpdateResult>(path, 65536);
            if (result == null || result.TimeUtc == updatePreferences.LastResultUtc)
                return;
            updatePreferences.LastResultUtc = result.TimeUtc;
            updatePreferences.Status = result.Message;
            updatePreferences.InstallRequested = false;
            updatePreferences.PreparedPath = null;
            updatePreferences.PreparedVersion = null;
            SaveUpdatePreferences();
        }

        internal void ApplicationStartupConfirmed()
        {
            if (disposed)
                return;
            try
            {
                ReadUpdateResult();
                RefreshUpdatePresentation();
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/ApplicationUpdates.cs:ApplicationStartupConfirmed", error);
                Log("读取更新结果未完成：" + error.Message);
            }
        }

        private void SaveUpdatePreferences()
        {
            UpdateProtocol.NoLinks(UpdateStatePath);
            UpdateProtocol.AtomicText(UpdateStatePath, UpdateProtocol.Json(updatePreferences));
        }

        private void UpdateStatus(string message)
        {
            updatePreferences.Status = message;
            try
            {
                SaveUpdatePreferences();
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/ApplicationUpdates.cs:UpdateStatus", error);
                updatePreferences.Status = message + "；更新设置保存未完成：" + error.Message;
            }

            RefreshUpdatePresentation();
        }

        private bool HasNewUpdate
        {
            get
            {
                return UpdatePolicy.Newer(updatePreferences.Release, InstalledVersion);
            }
        }

        private void RefreshUpdatePresentation()
        {
            if (disposed)
                return;
            bool known = HasNewUpdate, notice = UpdatePolicy.ShowNotice(updatePreferences, InstalledVersion, DateTime.UtcNow);
            var button = Button("UpdateAvailable");
            button.Visibility = notice ? Visibility.Visible : Visibility.Collapsed;
            button.ToolTip = known ? "发现新版本 " + updatePreferences.Release.Version + "；点击查看更新内容。\n" + updatePreferences.Status : null;
            if (aboutWindow == null)
                return;
            var display = new AboutDisplay
            {
                CurrentVersion = InstalledVersion,
                Status = updatePreferences.Status ?? "尚未检查更新",
                UpdateAvailable = known,
                UpdateMode = updatePreferences.Mode,
                Checking = updateCheckTask != null && !updateCheckTask.IsCompleted,
                Downloading = updateDownloading,
                CanInstall = known && !string.IsNullOrEmpty(updatePreferences.Release.DownloadUrl),
                InstallLabel = updateDownloading ? "正在下载…" : updatePreferences.InstallRequested && !string.IsNullOrEmpty(updatePreferences.PreparedPath) ? "等待任务结束" : "立即更新"
            };
            if (updatePreferences.Release != null && UpdateProtocol.ParseVersion(updatePreferences.Release.Version) >= UpdateProtocol.ParseVersion(InstalledVersion) &&
                !string.IsNullOrWhiteSpace(updatePreferences.Release.Notes))
                display.Notes = updatePreferences.Release.Notes;
            aboutWindow.Apply(display);
        }

        private void OpenAbout()
        {
            if (disposed || updateClosing)
                return;
            if (aboutWindow != null)
            {
                aboutWindow.Activate();
                return;
            }

            aboutWindow = new AboutWindow(Window, new AboutDisplay { CurrentVersion = InstalledVersion, UpdateMode = updatePreferences.Mode });
            aboutWindow.CheckRequested += async delegate
            {
                await CheckForUpdatesAsync(true);
            };
            aboutWindow.InstallRequested += async delegate
            {
                await RequestImmediateUpdateAsync();
            };
            aboutWindow.LaterRequested += delegate
            {
                DeferApplicationUpdate();
            };
            aboutWindow.IgnoreRequested += delegate
            {
                DisableUpdateNotices();
            };
            aboutWindow.ModeChanged += ChangeUpdateMode;
            try
            {
                RefreshUpdatePresentation();
                aboutWindow.ShowDialog();
            }
            finally
            {
                aboutWindow = null;
            }
        }

        private async Task CheckForUpdatesAsync(bool manual)
        {
            if (disposed || updateClosing)
                return;
            if (updateCheckTask != null && !updateCheckTask.IsCompleted)
            {
                await updateCheckTask;
                return;
            }

            if (!manual && !UpdatePolicy.Due(updatePreferences, DateTime.UtcNow))
            {
                await ContinueAutomaticUpdateAsync();
                return;
            }

            updateCheckTask = CheckForUpdatesCoreAsync();
            RefreshUpdatePresentation();
            try
            {
                await updateCheckTask;
            }
            finally
            {
                updateCheckTask = null;
                if (!disposed)
                    RefreshUpdatePresentation();
            }

            if (manual)
                updatePreferences.DownloadAttemptUtc = null;
            await ContinueAutomaticUpdateAsync();
            CompleteDeferredUpdateExit();
        }

        private async Task CheckForUpdatesCoreAsync()
        {
            try
            {
                updatePreferences.LastAttemptUtc = DateTime.UtcNow.ToString("o");
                UpdateStatus("正在检查更新…");
                AvailableUpdate release;
#if UI_TEST
                release = UpdateCheckFixture == null ? await updateClient.LatestAsync(updateLifetime.Token) : await UpdateCheckFixture(updateLifetime.Token);
#else
                release = await updateClient.LatestAsync(updateLifetime.Token);
#endif
                if (disposed || updateClosing)
                    return;
                updatePreferences.Release = release;
                UpdateStatus(release == null 
                    ? "尚未发布可安装的更新" 
                    : !HasNewUpdate ? "当前已是最新版本 " + InstalledVersion : string.IsNullOrEmpty(release.DownloadUrl) ? "发现新版本 " + release.Version + "，尚未提供 Windows 更新包" : "发现新版本：" +
                    release.Version);
            }
            catch (OperationCanceledException)
            { // Expected cancellation, shutdown, or a deliberately rejected identity probe.
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/ApplicationUpdates.cs:CheckForUpdatesCoreAsync", error);
                if (!disposed)
                {
                    UpdateStatus("检查更新未完成：" + error.Message);
                    Log(updatePreferences.Status);
                }
            }
        }

        private async Task ContinueAutomaticUpdateAsync()
        {
            if (disposed || updateClosing || updatePreferences.Mode != 1 || !HasNewUpdate || updatePreferences.Release.Version == updatePreferences.IgnoredVersion ||
                UpdatePolicy.Deferred(updatePreferences, DateTime.UtcNow) ||
                string.IsNullOrEmpty(updatePreferences.Release.DownloadUrl))
                return;
            if (updatePreferences.InstallRequested && updatePreferences.PreparedVersion == updatePreferences.Release.Version && !string.IsNullOrEmpty(updatePreferences.PreparedPath))
            {
                TryApplyApplicationUpdate();
                return;
            }

            DateTime attempt;
            if (updatePreferences.DownloadAttemptVersion == updatePreferences.Release.Version &&
                DateTime.TryParse(updatePreferences.DownloadAttemptUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out attempt) &&
                attempt.ToLocalTime()
                .Date >= DateTime.Now.Date)
                return;
            updatePreferences.InstallRequested = true;
            await PrepareApplicationUpdateAsync();
        }

        private async Task RequestImmediateUpdateAsync()
        {
            if (updateDownloading || updateClosing)
                return;
            if (!HasNewUpdate)
                await CheckForUpdatesAsync(true);
            if (disposed || !HasNewUpdate || string.IsNullOrEmpty(updatePreferences.Release.DownloadUrl))
                return;
            updatePreferences.IgnoredVersion = null;
            updatePreferences.DeferUntilUtc = null;
            updatePreferences.InstallRequested = true;
            await PrepareApplicationUpdateAsync();
        }

        private async Task PrepareApplicationUpdateAsync()
        {
            if (updateDownloading || updateClosing || disposed)
                return;
            if (updatePreferences.PreparedVersion == updatePreferences.Release.Version && !string.IsNullOrEmpty(updatePreferences.PreparedPath) &&
                File.Exists(Path.Combine(updatePreferences.PreparedPath, UpdateProtocol.ManifestName)))
            {
                UpdateStatus("更新包已就绪，将在当前任务完成后更新。");
                TryApplyApplicationUpdate();
                return;
            }

            var release = updatePreferences.Release;
            updateDownloading = true;
            updateDownloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token);
            updatePreferences.DownloadAttemptVersion = release.Version;
            updatePreferences.DownloadAttemptUtc = DateTime.UtcNow.ToString("o");
            try
            {
                UpdateStatus("正在下载更新包…");
                string payload;
#if UI_TEST
                payload = UpdateDownloadFixture == null 
                    ? await updateClient.DownloadAsync(release, Backend.BaseDirectory, ReportApplicationUpdateDownload, updateDownloadCancellation.Token) 
                    : await UpdateDownloadFixture(release, updateDownloadCancellation.Token);
#else
                payload = await updateClient.DownloadAsync(release, Backend.BaseDirectory, ReportApplicationUpdateDownload, updateDownloadCancellation.Token);
#endif
                if (disposed)
                    return;
                updatePreferences.PreparedPath = payload;
                updatePreferences.PreparedVersion = release.Version;
                UpdateStatus("更新包已校验，将在当前任务完成后更新。");
            }
            catch (OperationCanceledException)
            {
                if (!disposed)
                    UpdateStatus("更新下载已取消。");
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/ApplicationUpdates.cs:PrepareApplicationUpdateAsync", error);
                if (!disposed)
                {
                    updatePreferences.InstallRequested = false;
                    UpdateStatus("更新包准备未完成：" + error.Message);
                    Log(updatePreferences.Status);
                }
            }
            finally
            {
                updateDownloading = false;
                updateDownloadCancellation.Dispose();
                updateDownloadCancellation = null;
                if (!disposed)
                {
                    RefreshUpdatePresentation();
                    TryApplyApplicationUpdate();
                    CompleteDeferredUpdateExit();
                }
            }
        }

        private void ReportApplicationUpdateDownload(long done, long total)
        {
            if (disposed || Window.Dispatcher.HasShutdownStarted)
                return;
            Window.Dispatcher.BeginInvoke(
                new Action(delegate
            {
                if (disposed || !updateDownloading)
                    return;
                updatePreferences.Status = "正在下载更新包：" + (done / 1048576.0).ToString("0.00", CultureInfo.CurrentCulture) + " / " + (total / 1048576.0).ToString("0.00", CultureInfo.CurrentCulture) + " MiB";
                RefreshUpdatePresentation();
            }),
                DispatcherPriority.Background
            );
        }

        private void ChangeUpdateMode(int mode)
        {
            if (mode < 0 || mode > 2)
                return;
            updatePreferences.Mode = mode;
            if (mode != 1)
            {
                updatePreferences.InstallRequested = false;
                if (updateDownloadCancellation != null)
                    updateDownloadCancellation.Cancel();
            }

            if (mode == 1)
                updatePreferences.DownloadAttemptUtc = null;
            UpdateStatus(mode == 2 ? "已关闭自动更新检查。" : mode == 0 ? "已设置为自动检查，有新版本时显示提示。" : "已设置为自动检查并安装。");
            if (mode != 2)
                ObserveUpdateCheck();
        }

        private async void ObserveUpdateCheck()
        {
            await CheckForUpdatesAsync(false);
        }

        private void DeferApplicationUpdate()
        {
            updatePreferences.InstallRequested = false;
            updatePreferences.DeferUntilUtc = DateTime.Now.Date.AddDays(1).ToUniversalTime().ToString("o");
            if (updateDownloadCancellation != null)
                updateDownloadCancellation.Cancel();
            UpdateStatus("本次更新已推迟，下一天再提示。");
        }

        private void DisableUpdateNotices()
        {
            if (HasNewUpdate)
                updatePreferences.IgnoredVersion = updatePreferences.Release.Version;
            updatePreferences.Mode = 2;
            updatePreferences.InstallRequested = false;
            if (updateDownloadCancellation != null)
                updateDownloadCancellation.Cancel();
            UpdateStatus("已设置为不提示更新，并关闭自动更新检查。");
        }

        private void UpdateOperationChanged()
        {
            if (!disposed && !Window.Dispatcher.HasShutdownStarted)
                Window.Dispatcher.BeginInvoke(
                    new Action(delegate
                {
                    TryApplyApplicationUpdate();
                    CompleteDeferredUpdateExit();
                }),
                    DispatcherPriority.Background
                );
        }

        private bool UpdateWorkIdle
        {
            get
            {
                return !disposed && !updateClosing && !busy && !operations.IsActive && operations.WhenIdle().IsCompleted && !independentDownloadActive && !scheduleDraining && !scheduleExecuting &&
                    PendingScheduledRequestCount == 0 &&
                    (initialFileLists == null ||
                    initialFileLists.IsCompleted) &&
                    !Window.OwnedWindows.Cast<Window>()
                    .Any(window => window.IsVisible &&
                    !ReferenceEquals(window, aboutWindow));
            }
        }

        private bool UpdateCanApply
        {
            get
            {
                return !updateDownloading && UpdateWorkIdle;
            }
        }

        private void TryApplyApplicationUpdate()
        {
            if (closeAfterWork || !updatePreferences.InstallRequested || string.IsNullOrEmpty(updatePreferences.PreparedPath))
                return;
            if (UpdateCanApply)
                StartApplicationUpdate(scheduledHost);
        }

        private bool PrepareUpdateForExit()
        {
            if (updatePreferences == null)
                return false;
            if (scheduledHost && UpdateWorkIdle &&
                ((updateDownloading && updatePreferences.InstallRequested) || (updatePreferences.Mode != 2 && updateCheckTask != null && !updateCheckTask.IsCompleted)))
            {
                updateExitWaiting = true;
                UpdateStatus("任务已完成，正在完成软件更新检查，完成后退出。");
                return true;
            }

            if (!UpdateCanApply || !updatePreferences.InstallRequested || string.IsNullOrEmpty(updatePreferences.PreparedPath))
                return false;
            return StartApplicationUpdate(true);
        }

        private void CompleteDeferredUpdateExit()
        {
            if (!updateExitWaiting || disposed || updateDownloading || updateCheckTask != null && !updateCheckTask.IsCompleted)
                return;
            if (updateClosing)
            {
                updateExitWaiting = false;
                return;
            }

            if (!UpdateWorkIdle)
                return;
            updateExitWaiting = false;
            Window.Dispatcher.BeginInvoke(new Action(() => Window.Close()));
        }

        private bool StartApplicationUpdate(bool exitAfter)
        {
            try
            {
#if UI_TEST
                if (UpdateInstallFixture != null)
                {
                    UpdateInstallFixture(exitAfter);
                    return true;
                }

#endif
                string payload = updatePreferences.PreparedPath;
                var manifest = UpdateProtocol.ReadManifest(payload);
                if (UpdateProtocol.ParseVersion(manifest.Version) <= UpdateProtocol.ParseVersion(InstalledVersion))
                    throw new InvalidDataException("更新包不是更新的版本。");
                string session = UpdateInstaller.SessionDirectory(Backend.BaseDirectory, payload);
                UpdateTransactions.RecoveryHelper(Backend.BaseDirectory, payload);
                using (var process = Process.GetCurrentProcess())
                {
                    var request = new UpdateRequest
                    {
                        Installation = Backend.BaseDirectory,
                        Payload = payload,
                        Version = manifest.Version,
                        ParentProcessId = process.Id,
                        ParentStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                        ExitAfterUpdate = exitAfter,
                        Minimized = Window.WindowState == WindowState.Minimized
                    };
                    updateHelperRequest = Path.Combine(session, "request.json");
                    UpdateProtocol.AtomicText(updateHelperRequest, UpdateProtocol.Json(request));
                }

                SaveUiPreference(
                    UpdateResumeKey,
                    JsonSettings.Encode(new UpdateUiResume {
                    Tab = Control<TabControl>("Tabs").SelectedIndex,
                    Drafts = selectionDrafts.ToDictionary(pair => pair.Key, pair => CopySelection(pair.Value))
                })
                );
                updateClosing = true;
                if (aboutWindow != null)
                    aboutWindow.Close();
                Window.Dispatcher.BeginInvoke(new Action(() => Window.Close()));
                return true;
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/ApplicationUpdates.cs:StartApplicationUpdate", error);
                updatePreferences.InstallRequested = false;
                updateHelperRequest = null;
                UpdateStatus("更新启动未完成：" + error.Message);
                Log(updatePreferences.Status);
                return false;
            }
        }

        private void RestoreUpdateUiState()
        {
            string json = UiPreference(UpdateResumeKey);
            if (string.IsNullOrEmpty(json))
                return;
            var resume = JsonSettings.Decode(json, new UpdateUiResume());
            foreach (var pair in resume.Drafts ?? new Dictionary<string, FileSelection>())
                if (Workspace.Tasks.Any(task => !task.Deleted && task.Id == pair.Key))
                    selectionDrafts[pair.Key] = CopySelection(pair.Value);
            SaveUiPreference(UpdateResumeKey, "");
            Window.Loaded += delegate
            {
                if (resume.Tab >= 0 && resume.Tab < Control<TabControl>("Tabs").Items.Count)
                    Control<TabControl>("Tabs").SelectedIndex = resume.Tab;
            };
        }

        private void CancelApplicationUpdateClose()
        {
            if (!updateClosing)
                return;
            updateClosing = false;
            updateHelperRequest = null;
            updatePreferences.InstallRequested = false;
            try
            {
                SaveUiPreference(UpdateResumeKey, "");
            }
            catch (Exception diagnosticFailure)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/ApplicationUpdates.cs:CancelApplicationUpdateClose", diagnosticFailure);
            }

            UpdateStatus("软件尚未退出，本次自动更新已暂缓。");
        }

        private void StopApplicationUpdates()
        {
            if (updateTimer != null)
                updateTimer.Stop();
            operations.Changed -= UpdateOperationChanged;
            updateLifetime.Cancel();
        }

        private void LaunchPreparedUpdater()
        {
            if (!updateClosing || string.IsNullOrEmpty(updateHelperRequest))
                return;
            try
            {
                string helper = Path.Combine(Path.GetDirectoryName(updateHelperRequest), "recovery", "Backup.Updater.exe");
                Process.Start(new ProcessStartInfo(helper, "\"" + updateHelperRequest + "\"") {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Backend.BaseDirectory
                });
            }
            catch (Exception error)
            {
                try
                {
                    UpdateProtocol.AtomicText(
                        Path.Combine(UpdateProtocol.UpdatesDirectory(Backend.BaseDirectory), "result.json"),
                        UpdateProtocol.Json(new UpdateResult {
                        Success = false,
                        Message = "更新程序启动失败：" +
                        error.Message,
                        Version = updatePreferences.PreparedVersion,
                        TimeUtc = DateTime.UtcNow.ToString("o")
                    })
                    );
                }
                catch (Exception diagnosticFailure)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/ApplicationUpdates.cs:LaunchPreparedUpdater", diagnosticFailure);
                }
            }
        }
    }

    public sealed class UpdateUiResume
    {
        public int Tab;
        public Dictionary<string, FileSelection> Drafts;
    }
}
