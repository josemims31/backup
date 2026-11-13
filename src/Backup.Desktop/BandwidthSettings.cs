using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        internal const string BandwidthSettingsKey = "bandwidth_schedule_v1";
        public TransferBandwidth Bandwidth { get; private set; }

        public BandwidthSchedule BandwidthSettings()
        {
            return JsonSettings.Decode(Catalog.GetSetting(BandwidthSettingsKey), new BandwidthSchedule()).ValidatedCopy();
        }

        private void InitializeBandwidth()
        {
            Bandwidth = new TransferBandwidth();
            Bandwidth.Apply(BandwidthSettings());
        }

        private void ReloadBandwidth()
        {
            Bandwidth.Apply(BandwidthSettings());
        }

        public void SaveBandwidthSettings(BandwidthSchedule value)
        {
            value = (value ?? new BandwidthSchedule()).ValidatedCopy();
            Catalog.SaveConfiguration(new[] { new KeyValuePair<string, string>(BandwidthSettingsKey, value.Periods.Count == 0 ? null : JsonSettings.Encode(value)) });
            Bandwidth.Apply(value);
        }

        private T RetryAfterBandwidthPause<T>(Func<T> read, System.Threading.CancellationToken token)
        {
            long version = Bandwidth.PauseVersion;
            while (true)
            {
                Bandwidth.WaitForResume(token);
                try
                {
                    return read();
                }
                catch (System.IO.IOException)
                {
                    token.ThrowIfCancellationRequested();
                    long next = Bandwidth.PauseVersion;
                    if (next == version)
                        throw;
                    version = next;
                }
            }
        }
    }

    public sealed partial class MainWindowController
    {
        private bool ratePauseShown;
        private bool BandwidthControlsCurrentWork
        {
            get
            {
                return !disposed && !closeAfterWork && (operationCancellation == null || !operationCancellation.IsCancellationRequested) &&
                    (Backend.ActiveBackup != null && !Backend.ActiveBackup.IsStopping ||
                    operations.ActiveKind == OperationKind.Download ||
                    independentDownloadActive);
            }
        }

        private void OpenSettings()
        {
            if (disposed || closeAfterWork)
                return;
            if (!MutationBlocked)
            {
                EditSettings();
                return;
            }

            var selected = EditorDialogs.BandwidthOnly(Window, Backend.BandwidthSettings());
            if (selected != null)
                operations.ConfigureRuntime(() => Backend.SaveBandwidthSettings(selected));
        }

        private void InitializeBandwidthPresentation()
        {
            ratePauseShown = Backend.Bandwidth.Current.Paused;
            Backend.Bandwidth.Changed += BandwidthChanged;
        }

        private void BandwidthChanged()
        {
            if (disposed || Window.Dispatcher.HasShutdownStarted)
                return;
            Window.Dispatcher.BeginInvoke(new Action(delegate
            {
                if (disposed || closeAfterWork)
                    return;
                bool paused = Backend.Bandwidth.Current.Paused;
                bool changed = ratePauseShown != paused;
                ratePauseShown = paused;
                if (changed && operations.IsActive && BandwidthControlsCurrentWork)
                {
                    Log(paused ? "限速时段：已自动暂停全部备份、上传和下载，时段结束后自动继续。" : "限速暂停已结束，自动继续任务；手动暂停的任务保持暂停。", paused ? OperationState.Attention : OperationState.Succeeded);
                    SetOperationText(paused ? "限速时段：已自动暂停全部备份、上传和下载，时段结束后自动继续。" : Backend.ActiveBackup != null && Backend.ActiveBackup.IsManuallyPaused ? "已手动暂停，内存缓存保留，可继续任务。" : "限速暂停已结束，继续任务。");
                    UpdateBackupButtons();
                }
            }));
        }
    }

    public static partial class EditorDialogs
    {
        internal static BandwidthSchedule BandwidthOnly(Window owner, BandwidthSchedule current)
        {
            StackPanel body;
            Button save;
            var window = Form(owner, "限速设置", out body, out save, true);
            body.Children.Add(new TextBlock { Text = "任务运行期间可调整限速，其他设置请在任务结束后修改。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray });
            var editor = new BandwidthSettingsEditor(window, body, current);
            BandwidthSchedule result = null;
            Save(save, window, delegate
            {
                result = editor.Read();
            });
            return Show(window) ? result : null;
        }
    }

    internal sealed class BandwidthSettingsEditor
    {
        private sealed class Row
        {
            internal Border Container;
            internal CheckBox Every;
            internal CheckBox[] Days;
            internal TextBox Start, End, Upload, Download;
        }

        private readonly StackPanel rows = new StackPanel();
        private readonly List<Row> values = new List<Row>();
        private readonly Window window;
        internal BandwidthSettingsEditor(Window owner, StackPanel body, BandwidthSchedule saved)
        {
            window = owner;
            body.Children.Add(new TextBlock { Text = "限速设置", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 8) });
            body.Children.Add(new TextBlock { Text = "所有仓库合计限速，留空不限速，设为 0会同时暂停上传和下载，时段结束后自动继续。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 8) });
            window.RegisterName("BandwidthRows", rows);
            body.Children.Add(rows);
            var add = new Button
            {
                Content = "添加时段",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 5, 0, 0)
            };
            window.RegisterName("AddBandwidthPeriod", add);
            body.Children.Add(add);
            add.Click += delegate
            {
                Add(new BandwidthPeriod { StartMinute = 9 * 60, EndMinute = 12 * 60 });
            };
            foreach (var period in (saved ?? new BandwidthSchedule()).Periods)
                Add(period);
        }

        private static TextBox Input(string value, int width)
        {
            return new TextBox
            {
                Text = value,
                Width = width,
                Padding = new Thickness(5),
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        private static TextBlock Label(string value)
        {
            return new TextBlock
            {
                Text = value,
                Margin = new Thickness(7, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private void Add(BandwidthPeriod period)
        {
            var row = new Row
            {
                Days = new CheckBox[7]
            };
            var panel = new StackPanel();
            row.Container = new Border
            {
                Child = panel,
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9),
                Margin = new Thickness(0, 0, 0, 8),
                Tag = row
            };
            var days = new WrapPanel
            {
                Margin = new Thickness(0, 0, 0, 9)
            };
            panel.Children.Add(days);
            row.Every = new CheckBox
            {
                Content = "每天",
                IsChecked = period.Days == 127,
                Margin = new Thickness(0, 0, 10, 0)
            };
            days.Children.Add(row.Every);
            bool updating = false;
            string[] names =
            {
                "周日",
                "周一",
                "周二",
                "周三",
                "周四",
                "周五",
                "周六"
            };
            foreach (int index in new[]
            {
                1,
                2,
                3,
                4,
                5,
                6,
                0
            }

            )
            {
                int day = index;
                var check = new CheckBox
                {
                    Content = names[day],
                    IsChecked = (period.Days & (1 << day)) != 0,
                    Margin = new Thickness(0, 0, 9, 0)
                };
                row.Days[day] = check;
                days.Children.Add(check);
                RoutedEventHandler update = delegate
                {
                    if (updating)
                        return;
                    updating = true;
                    row.Every.IsChecked = row.Days.All(value => value != null && value.IsChecked == true);
                    updating = false;
                };
                check.Checked += update;
                check.Unchecked += update;
            }

            RoutedEventHandler every = delegate
            {
                if (updating)
                    return;
                updating = true;
                foreach (var check in row.Days)
                    check.IsChecked = row.Every.IsChecked == true;
                updating = false;
            };
            row.Every.Checked += every;
            row.Every.Unchecked += every;
            var fields = new WrapPanel();
            panel.Children.Add(fields);
            row.Start = Input(Time(period.StartMinute), 61);
            row.End = Input(Time(period.EndMinute), 61);
            row.Start.MaxLength = row.End.MaxLength = 5;
            row.Upload = Input(Rate(period.UploadMbps), 63);
            row.Download = Input(Rate(period.DownloadMbps), 63);
            row.Start.ToolTip = "开始时间，格式 HH:mm";
            row.End.ToolTip = "结束时间，支持 24:00";
            row.Upload.ToolTip = row.Download.ToolTip = "留空不限速，0 暂停全部任务";
            fields.Children.Add(row.Start);
            fields.Children.Add(Label("–"));
            fields.Children.Add(row.End);
            fields.Children.Add(Label("上传"));
            fields.Children.Add(row.Upload);
            fields.Children.Add(Label("Mbps"));
            fields.Children.Add(Label("下载"));
            fields.Children.Add(row.Download);
            fields.Children.Add(Label("Mbps"));
            var remove = new Button
            {
                Content = "删除",
                Padding = new Thickness(7, 4, 7, 4),
                Margin = new Thickness(8, 0, 0, 0)
            };
            fields.Children.Add(remove);
            remove.Click += delegate
            {
                rows.Children.Remove(row.Container);
                values.Remove(row);
            };
            values.Add(row);
            rows.Children.Add(row.Container);
            row.Container.BringIntoView();
        }

        private static string Time(int minute)
        {
            return (minute / 60).ToString("00") + ":" + (minute % 60).ToString("00");
        }

        private static string Rate(decimal? value)
        {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "";
        }

        private static int Minute(string text, bool end)
        {
            var parts = text.Trim().Split(':');
            int hour, minute;
            if (parts.Length != 2 || parts[0].Length < 1 || parts[0].Length > 2 || parts[1].Length != 2 ||
                !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hour) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minute) ||
                minute > 59 ||
                hour > 23 &&
                !(end &&
                hour == 24 &&
                minute == 0))
                throw new ArgumentException("时间请使用 HH:mm 格式；结束时间支持 24:00。");
            return hour * 60 + minute;
        }

        private static decimal? Limit(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            decimal value;
            if (!decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) || value < 0)
                throw new ArgumentException("限速值请填写大于或等于 0 的数字，单位为 Mbps；留空不限速。");
            return value;
        }

        internal BandwidthSchedule Read()
        {
            var result = new BandwidthSchedule();
            foreach (var row in values)
                result.Periods.Add(new BandwidthPeriod {
                    Days = Enumerable.Range(0, 7).Where(day => row.Days[day].IsChecked == true).Sum(day => 1 << day),
                    StartMinute = Minute(row.Start.Text, false),
                    EndMinute = Minute(row.End.Text, true),
                    UploadMbps = Limit(row.Upload.Text),
                    DownloadMbps = Limit(row.Download.Text)
                });
            return result.ValidatedCopy();
        }
    }
}
