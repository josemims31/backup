using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class MainWindowController
    {
        private string historyNodeId;
        private string historyActiveTaskId;
        // The task view has no task selector: every task is a tree root.
        // Selecting a node of another task makes it the active task and
        // selects its latest version; the version selector still switches
        // versions within the active task.
        private void SwitchHistoryTask(BackupHistoryNode node)
        {
            if (node == null)
                return;
            string taskId = null;
            for (var current = node; current != null; current = current.Parent)
            {
                if (current.IsTaskRoot)
                {
                    taskId = current.NavigationScope;
                    break;
                }

                if (current.File != null && !string.IsNullOrEmpty(current.File.TaskId))
                {
                    taskId = current.File.TaskId;
                    break;
                }
            }

            if (string.IsNullOrEmpty(taskId) || taskId == "__catalog__" || taskId == historyActiveTaskId)
                return;
            historyActiveTaskId = taskId;
            RefreshNodeSelector(taskId);
            historyDirty = true;
            RebuildHistory();
        }

        private BackupNode SelectedBackupNode
        {
            get
            {
                return Control<ComboBox>("HistoryNodeSelector").SelectedItem as BackupNode;
            }
        }

        private void RefreshNodeSelector(string taskId)
        {
            var selector = Control<ComboBox>("HistoryNodeSelector");
            var selected = selector.SelectedItem as BackupNode;
            var nodes = taskId == null ? new List<BackupNode>() : Backend.BackupNodesForBrowse(taskId);
            bool wasLoading = loading;
            loading = true;
            try
            {
                selector.ItemsSource = nodes;
                selector.SelectedItem = selected == null ? nodes.FirstOrDefault() : nodes.FirstOrDefault(n => n.Id == selected.Id) ?? nodes.FirstOrDefault();
            }
            finally
            {
                loading = wasLoading;
            }
        }

        private void BindNodeActions()
        {
            Control<ComboBox>("HistoryNodeSelector").SelectionChanged += delegate
            {
                if (!loading)
                {
                    historyDirty = true;
                    UpdateRemoteTaskActions();
                    RebuildHistory();
                }
            };
            Button("DeleteBackupNode").Click += async delegate
            {
                await GuardAsync(DeleteSelectedNodeAsync);
            };
        }

        private Task DeleteSelectedNodeAsync()
        {
            return RunOperationAsync(OperationKind.Delete, DeleteSelectedNodeCoreAsync);
        }

        private async Task DeleteSelectedNodeCoreAsync()
        {
            var node = SelectedBackupNode;
            if (node == null || busy)
                return;
            if (!ConfirmRemoteTaskAction("删除备份版本", "将从本地移除所选备份版本 " + node + "。\n运行修复完整性时清理仓库中无引用的文件，并同步已配置的数据库快照。"))
                return;
            SetBusy(true, false, "正在移除本地备份版本…");
            try
            {
                await Task.Run(() => Backend.DeleteBackupNodeLocally(node.Id));
                historyDirty = true;
                ApplyCommittedChanges();
                if (SelectedBackupNode != null && SelectedBackupNode.Id == node.Id)
                    RebuildHistory();
                Log("已删除备份版本：" + node);
                SetOperationText("备份版本已从本地移除。仓库文件清理和数据库快照同步将在修复完整性时处理。", OperationState.Succeeded);
            }
            finally
            {
                SetBusy(false, false, null);
            }
        }

        private Task DeleteSelectedNodeFilesAsync(IList<BackupHistoryNode> nodes)
        {
            return RunOperationAsync(OperationKind.Delete, () => DeleteSelectedNodeFilesCoreAsync(nodes));
        }

        private async Task DeleteSelectedNodeFilesCoreAsync(IList<BackupHistoryNode> nodes)
        {
            var node = SelectedBackupNode;
            if (node == null || busy || !IsFolderHistoryView)
                return;
            var requests = BackupHistoryTree.Requests(nodes, null, Workspace, Backend.ReadArchiveRootLabels);
            var paths = requests.Select(r => r.File.Path).Distinct(StringComparer.Ordinal).ToList();
            if (paths.Count == 0)
                throw new IOException("请选择备份版本内的文件或目录。");
            if (!ConfirmRemoteTaskAction("从备份版本删除文件", "从所选备份版本移除 " + paths.Count + " 个文件。\n不改变其他备份版本、源文件或任务勾选。数据包在修复完整性时按剩余引用回收。"))
                return;
            Backend.BeginBackup();
            SetBusy(true, true, "正在更新所选备份版本数据库…");
            try
            {
                var report = OperationReporter("");
                await Task.Run(() => Backend.DeleteNodeFiles(node.Id, paths, report));
                historyDirty = true;
                ApplyCommittedChanges();
                RebuildHistory();
                SetOperationText("文件已从所选备份版本移除。", OperationState.Succeeded);
                Log("备份版本 " + node + " 已移除 " + paths.Count + " 个文件。");
            }
            finally
            {
                Backend.EndBackup();
                SetBusy(false, false, null);
            }
        }
    }
}
