using System;
using System.IO;
using System.Globalization;
using System.Diagnostics;
using System.Windows.Threading;
using System.Windows.Media;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class MainWindowController
    {
        private void InitializeUiPreferences()
        {
            scheduleJournal = new RuntimeStateStore(Path.Combine(Backend.CacheDirectory, "schedule.db"));
            foreach (string key in new[]
            {
                "history_view_v3",
                "ui_layout_v1",
                "history_columns_v2"
            }

            )
            {
                string legacy = Backend.Catalog.GetSetting(key);
                if (legacy == null)
                    continue;
                if (scheduleJournal.Get(key) == null)
                    scheduleJournal.Set(key, legacy);
                Backend.Catalog.RemoveSetting(key);
            }
        }

        private string UiPreference(string key)
        {
            return scheduleJournal.Get(key);
        }

        private void SaveUiPreference(string key, string value)
        {
            scheduleJournal.Set(key, value);
        }

        private string SelectedTaskPreference
        {
            get
            {
                return "ui_selected_task_v1:" + Backend.RepositoryId;
            }
        }

        private void RememberSelectedTask()
        {
            string id = CurrentTask == null ? "" : CurrentTask.Id;
            if (UiPreference(SelectedTaskPreference) != id)
                SaveUiPreference(SelectedTaskPreference, id);
        }

        private string ChooseDownloadDirectory(string title)
        {
            string previous = new PathHistory(scheduleJournal).Recall(PathPurpose.Download) ?? "";
            return EditorDialogs.RestoreDirectory(Window, title, previous);
        }

        private bool databaseSavedDisplayInitialized;
        private DateTime databaseSavedDisplayUtc, databaseSavedDisplayLocal;
        private DateTimeFormatInfo databaseSavedDisplayFormat;
        private Calendar databaseSavedDisplayCalendar;
        private string databaseSavedDisplaySeparator;
        private long transferDisplayTicks, transferDisplayUpload, transferDisplayDownload;
        private Brush transferIdleForeground;
        private void RefreshTransferSpeed()
        {
            long ticks = Stopwatch.GetTimestamp(), upload = Backend.Bandwidth.UploadedBytes, download = Backend.Bandwidth.DownloadedBytes;
            double seconds = (ticks - transferDisplayTicks) / (double)Stopwatch.Frequency;
            long uploadRate = transferDisplayTicks == 0 || seconds <= 0 ? 0 : (long)Math.Max(0, (upload - transferDisplayUpload) / seconds), downloadRate = transferDisplayTicks == 0 || seconds <= 0 ? 0 : (long)Math.Max(0, (download - transferDisplayDownload) / seconds);
            double uploadMbps = Math.Round(uploadRate * 8.0 / 1000000, 2, MidpointRounding.AwayFromZero), downloadMbps = Math.Round(downloadRate * 8.0 / 1000000, 2, MidpointRounding.AwayFromZero);
            string sending = "↑ " + uploadMbps.ToString("0.00", CultureInfo.CurrentCulture) + " Mbps", receiving = "↓ " + downloadMbps.ToString("0.00", CultureInfo.CurrentCulture) + " Mbps";
            var uploadText = Text("UploadSpeed");
            var downloadText = Text("DownloadSpeed");
            if (transferIdleForeground == null)
                transferIdleForeground = uploadText.Foreground;
            if (uploadText.Text != sending)
                uploadText.Text = sending;
            if (downloadText.Text != receiving)
                downloadText.Text = receiving;
            Brush uploadColor = uploadMbps > 0 ? OperationTextColors.Completed : transferIdleForeground, downloadColor = downloadMbps > 0 ? OperationTextColors.Completed : transferIdleForeground;
            if (uploadText.Foreground != uploadColor)
                uploadText.Foreground = uploadColor;
            if (downloadText.Foreground != downloadColor)
                downloadText.Foreground = downloadColor;
            transferDisplayTicks = ticks;
            transferDisplayUpload = upload;
            transferDisplayDownload = download;
        }

        private void RefreshDatabaseSavedTime()
        {
            DateTime saved = Backend.Catalog.LastSavedUtc, local = saved == DateTime.MinValue ? saved : saved.ToLocalTime();
            var format = CultureInfo.CurrentCulture.DateTimeFormat;
            var calendar = format.Calendar;
            string separator = format.TimeSeparator;
            if (databaseSavedDisplayInitialized && saved == databaseSavedDisplayUtc && local == databaseSavedDisplayLocal && ReferenceEquals(format, databaseSavedDisplayFormat) &&
                ReferenceEquals(calendar, databaseSavedDisplayCalendar) &&
                separator == databaseSavedDisplaySeparator)
                return;
            string text = "数据库上次保存：" + (saved == DateTime.MinValue ? "尚未保存" : local.ToString("yyyy-MM-dd HH:mm:ss", format));
            var label = Text("DatabaseSaved");
            if (label.Text != text)
                label.Text = text;
            databaseSavedDisplayUtc = saved;
            databaseSavedDisplayLocal = local;
            databaseSavedDisplayFormat = format;
            databaseSavedDisplayCalendar = calendar;
            databaseSavedDisplaySeparator = separator;
            databaseSavedDisplayInitialized = true;
        }

        private void InitializeDatabaseSavedTime()
        {
            RefreshDatabaseSavedTime();
            RefreshTransferSpeed();
            var timer = new DispatcherTimer(DispatcherPriority.Background, Window.Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            timer.Tick += delegate
            {
                if (!disposed)
                {
                    RefreshDatabaseSavedTime();
                    RefreshTransferSpeed();
                    RefreshBackupProgress();
                    RefreshUploadStatus();
                }
            };
            Window.Closed += delegate
            {
                timer.Stop();
            };
            timer.Start();
        }
    }
}
