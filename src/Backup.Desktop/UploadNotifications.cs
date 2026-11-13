using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Backup.Core;

namespace Backup.Desktop
{
    internal static class UploadNoticeText
    {
        private static readonly string[] Headers =
        {
            "上传失败",
            "重试成功",
            "上传结果待确认",
            "上传结果已确认",
            "文件缺失",
            "未完成归档",
            "正在重试",
            "正在连接"
        };
        internal static IList<string> Blocks(string message)
        {
            var result = new List<string>();
            var block = new List<string>();
            foreach (string line in (message ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                if (Headers.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal)) && block.Count > 0)
                {
                    result.Add(string.Join("\n", block).Trim('\n'));
                    block.Clear();
                }

                block.Add(line);
            }

            if (block.Count > 0)
                result.Add(string.Join("\n", block).Trim('\n'));
            return result.Where(item => item.Length > 0).ToList();
        }

        internal static string Subject(string block)
        {
            string line = block.Split('\n')[0].Trim();
            int split = line.IndexOf('：');
            if (split < 0)
                return line;
            string header = line.Substring(0, split), body = line.Substring(split + 1).Trim();
            foreach (string suffix in new[]
            {
                " · 原因：",
                " · 结果："
            }

            )
            {
                int detail = body.IndexOf(suffix, StringComparison.Ordinal);
                if (detail >= 0)
                    body = body.Substring(0, detail);
            }

            if (Headers.Any(prefix => header.StartsWith(prefix, StringComparison.Ordinal)))
                return body;
            int next = body.IndexOf('：');
            return next < 0 ? body : header + " · " + body.Substring(0, next);
        }

        internal static string Remove(string text, string id, string label, string name)
        {
            return string.Join(
                "\n",
                Blocks(text).Where(block =>
            {
                string line = block.Split('\n')[0], subject = Subject(block);
                return subject != id + " · " + name && subject != label + " · " + name && !line.StartsWith(label + "：" + name + "：", StringComparison.Ordinal) &&
                    !line.StartsWith(id + "：" + name + "：", StringComparison.Ordinal);
            })
            );
        }
    }

    public sealed partial class DesktopBackend
    {
        public event Action<TransferProgress> TransferNotice;
        public event Action<TransferProgress> UploadProgressChanged;
        private static bool IsTransferNotice(TransferProgress value)
        {
            return new[]
            {
                "retrying",
                "retry-succeeded",
                "destination-unavailable",
                "upload-failed",
                "confirmation-pending",
                "confirmation-succeeded",
                "upload-check-result",
                "copy-missing",
                "archive-upload-failed",
                "temporary-discarded"
            }.Contains(value.State);
        }

        private void ReportTransferNotice(TransferProgress value)
        {
            var progress = UploadProgressChanged;
            if (progress != null)
                foreach (Action<TransferProgress> observer in progress.GetInvocationList())
                    try
                    {
                        observer(value);
                    }
                    catch (global::System.Exception diagnosticFailure)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/UploadNotifications.cs:ReportTransferNotice", diagnosticFailure);
                    }

            if (!IsTransferNotice(value))
                return;
            var handler = TransferNotice;
            if (handler != null)
                foreach (Action<TransferProgress> observer in handler.GetInvocationList())
                    try
                    {
                        observer(value);
                    }
                    catch (global::System.Exception diagnosticFailure)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/UploadNotifications.cs:ReportTransferNotice", diagnosticFailure);
                    }
        }
    }

    public sealed partial class MainWindowController
    {
        private readonly Dictionary<string, string> transferIssues = new Dictionary<string, string>();
        private readonly HashSet<string> resolvedUploadSubjects = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> loggedTransferNotices = new HashSet<string>();
        private readonly HashSet<string> loggedAlertMessages = new HashSet<string>(), expiredAlertMessages = new HashSet<string>();
        private readonly Dictionary<string, DateTime> alertDeadlines = new Dictionary<string, DateTime>();
        private DispatcherTimer uploadNoticeTimer;
        private bool savedUploadNoticesNormalized;
        internal const int UploadNoticeMilliseconds = 10000;
#if UI_TEST
        public int TestUploadNoticeMilliseconds = UploadNoticeMilliseconds;
#endif
        private readonly System.Collections.Concurrent.ConcurrentQueue<KeyValuePair<int, TransferProgress>> transferNoticeQueue = new System.Collections.Concurrent.ConcurrentQueue<KeyValuePair<int, TransferProgress>>();
        private int transferNoticePosted;
        private int taskLogCharacters;
        private bool taskLogViewportBound, taskLogAppending, taskLogFollowLatest = true;
        private static bool AtTaskLogEnd(RichTextBox log)
        {
            return log.ExtentHeight - log.ViewportHeight - log.VerticalOffset <= 2;
        }

        private static IList<string> NoticeBlocks(string message)
        {
            return UploadNoticeText.Blocks(message);
        }

        private static string ActiveAlertOnly(string message)
        {
            return string.Join("\n", NoticeBlocks(message).Where(block => !block.StartsWith("重试成功", StringComparison.Ordinal) && !block.StartsWith("上传结果已确认", StringComparison.Ordinal)));
        }

        private void LogAlertOnce(string message, OperationState state)
        {
            if (loggedAlertMessages.Add(message))
                Log(message, state);
        }

        private string PrepareVisibleUploadAlert(string message)
        {
            var visible = new List<string>();
            DateTime now = DateTime.UtcNow;
            // Transfer notices are newer than queued progress snapshots. Keep their
            // current results, and never revive a subject already confirmed here.
            var active = new HashSet<string>(transferIssues.Values.Select(UploadNoticeText.Subject), StringComparer.Ordinal);
            foreach (string key in transferIssues.Keys)
            {
                int split = key.IndexOf('|');
                if (split >= 0)
                    active.Add(key.Substring(0, split) + " · " + key.Substring(split + 1));
            }

            var blocks = NoticeBlocks(message).Where(block => !active.Contains(UploadNoticeText.Subject(block)) && !resolvedUploadSubjects.Contains(UploadNoticeText.Subject(block))).Concat(transferIssues.Values)
                .Distinct()
                .ToList();
            foreach (string block in blocks)
            {
                if (block.StartsWith("重试成功", StringComparison.Ordinal) || block.StartsWith("上传结果已确认", StringComparison.Ordinal))
                {
                    LogAlertOnce(block, OperationState.Succeeded);
                    continue;
                }

                DateTime deadline;
                if (alertDeadlines.TryGetValue(block, out deadline) && deadline <= now)
                {
                    expiredAlertMessages.Add(block);
                    alertDeadlines.Remove(block);
                }

                if (expiredAlertMessages.Contains(block))
                    continue;
                bool confirming = block.StartsWith("上传结果待确认", StringComparison.Ordinal);
                LogAlertOnce(block, confirming ? OperationState.Attention : OperationState.Failed);
                if (!confirming && !alertDeadlines.ContainsKey(block))
                {
                    int duration = UploadNoticeMilliseconds;
#if UI_TEST
                    duration = TestUploadNoticeMilliseconds;
#endif
                    alertDeadlines[block] = now.AddMilliseconds(duration);
                }

                visible.Add(block);
            }

            if (alertDeadlines.Count > 0)
            {
                if (uploadNoticeTimer == null)
                {
                    uploadNoticeTimer = new DispatcherTimer(DispatcherPriority.Background, Window.Dispatcher);
                    uploadNoticeTimer.Tick += delegate
                    {
                        ExpireUploadNotices();
                    };
                    Window.Closed += delegate
                    {
                        uploadNoticeTimer.Stop();
                    };
                }

                uploadNoticeTimer.Stop();
                double next = Math.Max(1, (alertDeadlines.Values.Min() - now).TotalMilliseconds);
                uploadNoticeTimer.Interval = TimeSpan.FromMilliseconds(next);
                uploadNoticeTimer.Start();
            }
            else if (uploadNoticeTimer != null)
                uploadNoticeTimer.Stop();
            return string.Join("\n", visible);
        }

        private void ExpireUploadNotices()
        {
            if (uploadNoticeTimer != null)
                uploadNoticeTimer.Stop();
            if (disposed)
                return;
            DateTime now = DateTime.UtcNow;
            foreach (string key in alertDeadlines.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToList())
            {
                expiredAlertMessages.Add(key);
                alertDeadlines.Remove(key);
            }

            foreach (string key in transferIssues.Where(pair => expiredAlertMessages.Contains(pair.Value)).Select(pair => pair.Key).ToList())
                transferIssues.Remove(key);
            ShowUploadAlert(currentUploadAlert);
        }

        private void AppendTaskLog(string message, OperationState state = OperationState.None)
        {
            var log = Control<RichTextBox>("TaskLog");
            if (!taskLogViewportBound)
            {
                taskLogViewportBound = true;
                log.PreviewMouseWheel += delegate (object sender, System.Windows.Input.MouseWheelEventArgs change)
                {
                    if (change.Delta > 0 && log.VerticalOffset > 0)
                        taskLogFollowLatest = false;
                };
                log.AddHandler(
                    ScrollViewer.ScrollChangedEvent,
                    new ScrollChangedEventHandler(delegate (object sender, ScrollChangedEventArgs change)
                {
                    if (taskLogAppending || !log.IsVisible || change.ExtentHeightChange != 0 || change.ViewportHeightChange != 0 || change.VerticalChange == 0)
                        return;
                    taskLogFollowLatest = log.Selection.IsEmpty && AtTaskLogEnd(log);
                    if (taskLogFollowLatest)
                        log.CaretPosition = log.Document.ContentEnd;
                })
                );
            }

            if (!log.Selection.IsEmpty)
                taskLogFollowLatest = false;
            string value = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            int offset = 0;
            taskLogAppending = true;
            try
            {
                while (offset < value.Length)
                {
                    int count = Math.Min(8000, value.Length - offset);
                    if (offset + count < value.Length && char.IsHighSurrogate(value[offset + count - 1]))
                        count--;
                    string text = value.Substring(offset, count);
                    offset += count;
                    var paragraph = new Paragraph(new Run(text))
                    {
                        Margin = new Thickness(0),
                        Foreground = OperationTextColors.StateColor(state, System.Windows.Media.Brushes.Black)
                    };
                    paragraph.Tag = text.Length + Environment.NewLine.Length;
                    log.Document.Blocks.Add(paragraph);
                    taskLogCharacters += (int)paragraph.Tag;
                    if (taskLogCharacters > 64000)
                        while (taskLogCharacters > 48000 && log.Document.Blocks.FirstBlock != null)
                        {
                            var first = log.Document.Blocks.FirstBlock;
                            taskLogCharacters -= (int)first.Tag;
                            log.Document.Blocks.Remove(first);
                        }
                }

                // WPF restores the caret on focus; scrolling alone leaves it on an
                // old paragraph. Move it only while following, preserving selections.
                if (taskLogFollowLatest && log.Selection.IsEmpty)
                {
                    log.CaretPosition = log.Document.ContentEnd;
                    log.ScrollToEnd();
                }
            }
            finally
            {
                taskLogAppending = false;
            }
        }

        private string TransferLabel(string id)
        {
            var group = Backend.Catalog.LoadTopology().Groups.FirstOrDefault(item => item.Id == "single:" + id);
            return group == null ? id : group.Name;
        }

        private void ReceiveTransferNotice(TransferProgress value)
        {
            if (Window.Dispatcher.HasShutdownStarted || disposed)
                return;
            transferNoticeQueue.Enqueue(new KeyValuePair<int, TransferProgress>(operationGeneration, value));
            if (System.Threading.Interlocked.Exchange(ref transferNoticePosted, 1) == 0)
                Window.Dispatcher.BeginInvoke(new Action(DrainTransferNotifications), DispatcherPriority.Send);
        }

        private void DrainTransferNotifications()
        {
            System.Threading.Interlocked.Exchange(ref transferNoticePosted, 0);
            KeyValuePair<int, TransferProgress> queued;
            while (transferNoticeQueue.TryDequeue(out queued))
            {
                var value = queued.Value;
                bool resolved = value.State == "temporary-discarded" || value.State == "retry-succeeded" || value.State == "confirmation-succeeded" ||
                    value.State == "upload-check-result" &&
                    value.CheckConfirmed;
                if (disposed || closeAfterWork || !busy && !resolved || queued.Key != operationGeneration)
                    continue;
                string key = value.DestinationId + "|" + value.ObjectName, label = TransferLabel(value.DestinationId);
                if (value.State == "temporary-discarded")
                {
                    transferIssues.Remove(key);
                    RemoveResolvedUploadNotice(value.DestinationId, label, value.ObjectName);
                    continue;
                }

                string message = (value.State == "destination-unavailable" ? "上传失败：" + value.DestinationId + " · " + value.ObjectName + "\n原因：" + value.Error : RetryUploader.FailureMessage(value)).Replace(value.DestinationId +
                    " · ", label +
                    " · ");
                string logKey = value.State + "|" + key + "|" + value.Attempt + "|" + value.CheckAttempt;
                if (resolved)
                {
                    transferIssues.Remove(key);
                    RemoveResolvedUploadNotice(value.DestinationId, label, value.ObjectName);
                    if (loggedTransferNotices.Add(logKey))
                    {
                        loggedAlertMessages.Add(message);
                        if (value.State == "upload-check-result" || !value.CheckConfirmed)
                            Log(message, OperationState.Succeeded);
                    }
                }
                else if (value.State == "retrying" || value.State == "confirmation-pending" || value.State == "upload-check-result")
                {
                    if (value.State == "confirmation-pending")
                    {
                        resolvedUploadSubjects.Remove(value.DestinationId + " · " + value.ObjectName);
                        resolvedUploadSubjects.Remove(label + " · " + value.ObjectName);
                        transferIssues[key] = message;
                    }

                    if (loggedTransferNotices.Add(logKey))
                    {
                        loggedAlertMessages.Add(message);
                        Log(message, OperationState.Attention);
                    }

                    if (value.State == "confirmation-pending")
                        ShowUploadAlert(string.Join("\n", transferIssues.Values));
                }
                else
                {
                    resolvedUploadSubjects.Remove(value.DestinationId + " · " + value.ObjectName);
                    resolvedUploadSubjects.Remove(label + " · " + value.ObjectName);
                    expiredAlertMessages.Remove(message);
                    alertDeadlines.Remove(message);
                    if (loggedTransferNotices.Add(logKey))
                    {
                        loggedAlertMessages.Add(message);
                        Log(message, OperationState.Failed);
                    }

                    transferIssues[key] = message;
                    ShowUploadAlert(string.Join("\n", transferIssues.Values));
                }
            }
        }

        private static string RemoveResolvedNotice(string text, string id, string label, string name)
        {
            return UploadNoticeText.Remove(text, id, label, name);
        }

        private void RemoveResolvedUploadNotice(string id, string label, string name)
        {
            resolvedUploadSubjects.Add(id + " · " + name);
            resolvedUploadSubjects.Add(label + " · " + name);
            transferIssues.Remove(id + "|" + name);
            string message = RemoveResolvedNotice(currentUploadAlert, id, label, name);
            foreach (string block in NoticeBlocks(currentUploadAlert).Except(NoticeBlocks(message)).ToList())
                alertDeadlines.Remove(block);
            foreach (string key in new[]
            {
                "last_upload_alert_v1",
                "last_upload_warnings_v1"
            }

            )
            {
                string previous = Backend.Catalog.GetSetting(key);
                if (string.IsNullOrEmpty(previous))
                    continue;
                string next = RemoveResolvedNotice(previous, id, label, name);
                if (next != previous)
                    Backend.Catalog.SetSetting(key, next);
            }

            ShowUploadAlert(message);
        }

        private void BeginUploadNotifications()
        {
            uploadAlertDismissed = false;
            transferIssues.Clear();
            resolvedUploadSubjects.Clear();
            loggedTransferNotices.Clear();
            loggedAlertMessages.Clear();
            expiredAlertMessages.Clear();
            alertDeadlines.Clear();
            ShowUploadAlert("");
            Backend.Catalog.SetSetting("last_upload_alert_v1", "");
        }

        private void NormalizeSavedUploadNotices()
        {
            if (savedUploadNoticesNormalized)
                return;
            savedUploadNoticesNormalized = true;
            foreach (string key in new[]
            {
                "last_upload_alert_v1",
                "last_upload_warnings_v1"
            }

            )
            {
                string previous = Backend.Catalog.GetSetting(key);
                if (string.IsNullOrEmpty(previous))
                    continue;
                foreach (string block in NoticeBlocks(previous))
                {
                    bool success = block.StartsWith("重试成功", StringComparison.Ordinal) || block.StartsWith("上传结果已确认", StringComparison.Ordinal);
                    LogAlertOnce(block, success ? OperationState.Succeeded : OperationState.Failed);
                    expiredAlertMessages.Add(block);
                }

                string next = ActiveAlertOnly(previous).Trim('\n', '\r');
                if (next != previous)
                    Backend.Catalog.SetSetting(key, next);
            }
        }

        private void RecordSavedUploadNoticeHistory()
        {
            foreach (string key in new[]
            {
                "last_upload_alert_v1",
                "last_upload_warnings_v1"
            }

            )
                foreach (string block in NoticeBlocks(Backend.Catalog.GetSetting(key)))
                {
                    bool active = NoticeBlocks(currentUploadAlert).Contains(block) || transferIssues.Values.Contains(block);
                    if (!active)
                    {
                        LogAlertOnce(block, block.StartsWith("重试成功", StringComparison.Ordinal) ? OperationState.Succeeded : OperationState.Failed);
                        expiredAlertMessages.Add(block);
                    }
                }
        }

        private void StopUploadNotifications()
        {
            if (uploadNoticeTimer != null)
                uploadNoticeTimer.Stop();
            Backend.TransferNotice -= ReceiveTransferNotice;
            Backend.UploadProgressChanged -= ReceiveUploadProgress;
            KeyValuePair<int, TransferProgress> ignored;
            while (transferNoticeQueue.TryDequeue(out ignored))
            {
            }
        }
    }
}
