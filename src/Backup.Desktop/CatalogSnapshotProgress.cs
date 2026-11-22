using System;
using System.Collections.Generic;
using System.Linq;
using Backup.Core;

namespace Backup.Desktop
{
    internal sealed class CatalogSnapshotProgress
    {
        private readonly object gate = new object();
        private readonly IDictionary<string, string> repositoryNames;
        private readonly Action<string> report;
        private readonly SortedDictionary<string, string> transfers = new SortedDictionary<string, string>(StringComparer.Ordinal);
        private string archiveName, lastMessage;
        private bool compressing;

        internal CatalogSnapshotProgress(StorageTopology topology, Action<string> report)
        {
            this.report = report;
            repositoryNames = topology.Groups.Where(group => group.Id.StartsWith("single:", StringComparison.Ordinal))
                .ToDictionary(group => group.Id.Substring(7), group => group.Name);
        }

        private string RepositoryName(string id)
        {
            string name;
            return repositoryNames.TryGetValue(id, out name) && !string.IsNullOrEmpty(name) ? name : id;
        }

        private void Publish(string detail)
        {
            string message = "同步数据库快照 · " + detail;
            if (message == lastMessage)
                return;
            lastMessage = message;
            report(message);
        }

        internal void ReportPhase(string phase, string destination = null)
        {
            lock (gate)
                Publish(destination == null ? phase : RepositoryName(destination) + " · " + phase);
        }

        internal void Observe(TransferProgress item)
        {
            lock (gate)
            {
                if (item.State == "archive-compressing")
                {
                    archiveName = item.ObjectName;
                    compressing = true;
                }
                else if (item.State == "archive-ready" || item.State == "archive-aborted")
                    compressing = false;
                else
                {
                    if (string.IsNullOrEmpty(item.DestinationId) || string.IsNullOrEmpty(item.ObjectName))
                        return;
                    string key = item.DestinationId + "\0" + item.ObjectName;
                    if (item.State == "copy-complete")
                    {
                        if (!transfers.Remove(key))
                            return;
                    }
                    else
                    {
                        string phase = Describe(item);
                        if (phase == null)
                            return;
                        string detail = RepositoryName(item.DestinationId) + " · " + phase;
                        if (item.ObjectName != archiveName)
                            detail += " · " + item.ObjectName;
                        string previous;
                        if (transfers.TryGetValue(key, out previous) && previous == detail)
                            return;
                        transfers[key] = detail;
                    }
                }

                var stages = new List<string>();
                if (compressing)
                    stages.Add("正在加密压缩");
                stages.AddRange(transfers.Values);
                if (stages.Count == 0)
                    stages.Add("正在汇总副本结果");
                Publish((string.IsNullOrEmpty(archiveName) ? "" : archiveName + " · ") + string.Join(" | ", stages));
            }
        }

        private static string Describe(TransferProgress item)
        {
            switch (item.State)
            {
                case "preparing-destination": return "正在检查并准备仓库目录";
                case "checking-target": return "正在检查同名快照";
                case "queued-upload": return "正在等待上传队列";
                case "opening-upload": return "正在建立上传";
                case "uploading": return "正在发送快照";
                case "background-uploading": return string.IsNullOrEmpty(item.BackgroundState) ? "等待 OpenList 确认" : "OpenList · " + item.BackgroundState;
                case "committing-upload": return "正在提交快照文件";
                case "verifying-upload": return "正在核对正式文件名称和大小";
                case "waiting-destination": return "正在等待仓库就绪" + Detail(item.Error);
                case "confirmation-pending": return "上传结果待确认" + Detail(item.Error);
                case "checking-upload-result": return "正在核对上传结果 " + item.CheckAttempt + " / 3";
                case "upload-check-result": return item.CheckConfirmed ? "正在登记已确认的副本" : "上传核对 " + item.CheckAttempt + " / 3 未确认" + Detail(item.Error);
                case "retrying": return "正在重试上传 " + item.Attempt + " / " + (item.AttemptLimit - 1);
                case "retry-succeeded":
                case "confirmation-succeeded": return "正在登记已确认的副本";
                case "upload-failed":
                case "copy-missing": return "副本上传未完成" + Detail(item.Error);
                case "destination-unavailable": return "仓库暂不可用" + Detail(item.Error);
                default: return null;
            }
        }

        private static string Detail(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
                return "";
            string text = error.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return "（" + (text.Length > 100 ? text.Substring(0, 100) + "…" : text) + "）";
        }
    }
}
