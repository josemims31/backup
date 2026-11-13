using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class MainWindowController
    {
        private readonly BackupPipelineView uploadStatus = new BackupPipelineView();
        private int uploadStatusDirty, uploadStatusGeneration;
        private void InitializeUploadStatus()
        {
            Control<Grid>("NetworkSpeed").ToolTip = "本机与远程仓库之间的数据传输速度（Mbps），每秒更新；不含 OpenList 服务器到网盘的上传速度。";
            Backend.UploadProgressChanged += ReceiveUploadProgress;
            Text("UploadProgressText").SizeChanged += delegate
            {
                RenderUploadStatus();
            };
            Text("DownloadProgressText").SizeChanged += delegate
            {
                RenderDownloadStatus();
            };
        }

        private void RefreshUploadStatus()
        {
            if (busy && uploadStatusGeneration == operationGeneration)
            {
                if (Interlocked.Exchange(ref uploadStatusDirty, 0) != 0)
                {
                    RenderUploadStatus();
                    RenderDownloadStatus();
                }
                else
                    RefreshDownloadStatusVisibility();
            }
        }

        private void ReceiveUploadProgress(TransferProgress progress)
        {
            if (disposed || closeAfterWork || !busy || Window.Dispatcher.HasShutdownStarted)
                return;
            uploadStatus.Observe(progress);
            Interlocked.Exchange(ref uploadStatusGeneration, operationGeneration);
            Interlocked.Exchange(ref uploadStatusDirty, 1);
        }

        private void ResetUploadStatus()
        {
            uploadStatus.Clear();
            Interlocked.Exchange(ref pendingBackupProgress, null);
            Interlocked.Exchange(ref uploadStatusDirty, 0);
            foreach (string name in new[]
            {
                "UploadProgressText",
                "DownloadProgressText"
            }

            )
            {
                var text = Text(name);
                text.Inlines.Clear();
                text.ToolTip = null;
                text.Visibility = Visibility.Collapsed;
            }

            Control<ScrollViewer>("StatusTextScroll").Visibility = Visibility.Visible;
        }

        private static string StatusBytes(long value, long basis = -1)
        {
            string[] units =
            {
                "B",
                "KiB",
                "MiB",
                "GiB",
                "TiB"
            };
            double number = value, scale = basis < 0 ? value : basis;
            int unit = 0;
            while (scale >= 1024 && unit < units.Length - 1)
            {
                scale /= 1024;
                number /= 1024;
                unit++;
            }

            return number.ToString("0.00", CultureInfo.InvariantCulture) + " " + units[unit];
        }

        private static string UploadAmount(long written, long size)
        {
            return StatusBytes(written, size) + "/" + StatusBytes(size, size);
        }

        private static double UploadTextWidth(TextBlock text, string value, FontWeight? weight = null)
        {
            return new FormattedText(
                value,
                CultureInfo.CurrentUICulture,
                text.FlowDirection,
                new Typeface(text.FontFamily, text.FontStyle, weight ?? text.FontWeight, text.FontStretch),
                text.FontSize,
                Brushes.Black,
                VisualTreeHelper.GetDpi(text)
                .PixelsPerDip
            ).WidthIncludingTrailingWhitespace;
        }

        private void RefreshDownloadStatusVisibility()
        {
            var control = Backend.ActiveBackup;
            bool show = busy && !disposed && !closeAfterWork && operationState == OperationState.Running && uploadStatus.ActiveDownloads().Count > 0 && !Backend.Bandwidth.IsPaused &&
                (control == null ||
                !control.IsPaused &&
                !control.IsStopping &&
                !control.IsFinishing);
            Text("DownloadProgressText").Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            Control<ScrollViewer>("StatusTextScroll").Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        }

        private static string DownloadAmount(long written, long size)
        {
            return (written / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture) + " / " + (size / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture) + " MiB";
        }

        private void RenderDownloadStatus()
        {
            var text = Text("DownloadProgressText");
            var downloads = uploadStatus.ActiveDownloads();
            RefreshDownloadStatusVisibility();
            text.Inlines.Clear();
            if (!busy || downloads.Count == 0)
            {
                text.ToolTip = null;
                return;
            }

            text.ToolTip = string.Join("\n", downloads.Select(value => value.ObjectName + " ← " + (value.DestinationLabel ?? value.DestinationId) + " · " + DownloadAmount(value.Written, value.Size)));
            double available = Math.Max(0, text.ActualWidth - 2);
            if (available == 0)
                return;
            const string prefix = "正在下载：", gap = "    ", separator = " · ";
            double used = UploadTextWidth(text, prefix), separatorWidth = UploadTextWidth(text, separator, FontWeights.Bold);
            int visible = 0;
            text.Inlines.Add(new Run(prefix) { Foreground = Brushes.Black });
            foreach (var value in downloads)
            {
                string name = value.ObjectName ?? "", amount = DownloadAmount(value.Written, value.Size);
                double amountWidth = UploadTextWidth(text, amount);
                double reserved = Math.Max(UploadTextWidth(text, DownloadAmount(value.Size, value.Size)), UploadTextWidth(text, DownloadAmount(value.Size, value.Size).Replace('0', '8').Replace('1', '8')));
                double spacing = visible == 0 ? 0 : UploadTextWidth(text, gap), width = UploadTextWidth(text, name) + separatorWidth + reserved;
                if (used + spacing + width > available)
                {
                    if (visible > 0)
                        break;
                    double nameRoom = available - used - separatorWidth - reserved;
                    int low = 0, high = name.Length;
                    while (low < high)
                    {
                        int middle = (low + high + 1) / 2;
                        if (UploadTextWidth(text, name.Substring(0, middle) + "…") <= nameRoom)
                            low = middle;
                        else
                            high = middle - 1;
                    }

                    if (low > 0 && char.IsHighSurrogate(name[low - 1]))
                        low--;
                    name = low == 0 ? "…" : name.Substring(0, low) + "…";
                    width = UploadTextWidth(text, name) + separatorWidth + reserved;
                }

                if (visible > 0)
                    text.Inlines.Add(new Run(gap) { Foreground = Brushes.Black });
                text.Inlines.Add(new Run(name) { Foreground = Brushes.Black });
                text.Inlines.Add(new Run(separator) { Foreground = Brushes.Black, FontWeight = FontWeights.Bold });
                text.Inlines.Add(new Run(amount) { Foreground = Brushes.Green });
                text.Inlines.Add(new InlineUIContainer(new Border { Width = Math.Max(0, reserved - amountWidth), Height = 0 }) { BaselineAlignment = BaselineAlignment.Baseline });
                used += spacing + width;
                visible++;
            }
        }

        private void RenderUploadStatus()
        {
            var text = Text("UploadProgressText");
            var uploads = uploadStatus.ActiveUploads();
            text.Inlines.Clear();
            if (!busy || uploads.Count == 0)
            {
                text.Visibility = Visibility.Collapsed;
                text.ToolTip = null;
                return;
            }

            text.Visibility = Visibility.Visible;
            double available = Math.Max(0, text.ActualWidth - 2);
            if (available == 0)
                return;
            const string prefix = "正在上传：", gap = "    ", separator = " · ";
            double used = UploadTextWidth(text, prefix), separatorWidth = UploadTextWidth(text, separator, FontWeights.Bold);
            int visible = 0;
            text.Inlines.Add(new Run(prefix) { Foreground = Brushes.Black });
            text.ToolTip = string.Join(
                "\n",
                uploads.Select(value => value.ObjectName + separator + UploadAmount(value.Written, value.Size) +
                (string.IsNullOrEmpty(value.BackgroundState) ? "" : " · OpenList: " +
                value.BackgroundState +
                " " +
                UploadAmount(value.BackgroundWritten, value.BackgroundSize)))
            );
            foreach (var value in uploads)
            {
                string name = value.ObjectName ?? "", amount = UploadAmount(value.Written, value.Size);
                double amountWidth = UploadTextWidth(text, amount);
                string maximum = UploadAmount(value.Size, value.Size);
                double reserved = Math.Max(UploadTextWidth(text, maximum), UploadTextWidth(text, maximum.Replace('0', '8').Replace('1', '8')));
                double spacing = visible == 0 ? 0 : UploadTextWidth(text, gap), width = UploadTextWidth(text, name) + separatorWidth + reserved;
                if (used + spacing + width > available)
                {
                    if (visible > 0)
                        continue;
                    // Keep progress visible even if an unusually long name cannot fit.
                    double nameRoom = available - used - separatorWidth - reserved;
                    int low = 0, high = name.Length;
                    while (low < high)
                    {
                        int middle = (low + high + 1) / 2;
                        if (UploadTextWidth(text, name.Substring(0, middle) + "…") <= nameRoom)
                            low = middle;
                        else
                            high = middle - 1;
                    }

                    name = low == 0 ? "…" : name.Substring(0, low) + "…";
                    width = UploadTextWidth(text, name) + separatorWidth + reserved;
                }

                if (visible > 0)
                    text.Inlines.Add(new Run(gap) { Foreground = Brushes.Black });
                text.Inlines.Add(new Run(name) { Foreground = Brushes.Black });
                text.Inlines.Add(new Run(separator) { Foreground = Brushes.Black, FontWeight = FontWeights.Bold });
                text.Inlines.Add(new Run(amount) { Foreground = Brushes.Green });
                text.Inlines.Add(new InlineUIContainer(new Border { Width = Math.Max(0, reserved - amountWidth), Height = 0 }) { BaselineAlignment = BaselineAlignment.Baseline });
                used += spacing + width;
                visible++;
            }
        }
    }
}
