using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Backup.Desktop
{
    public sealed partial class MainWindowController
    {
        private FileTreeNode previewParent;
        private void InitializeSelectionHeaders()
        {
            Control<CheckBox>("SelectAllTree").Click += delegate
            {
                if (MutationBlocked || !SourceViewCurrent || CurrentTask == null)
                    return;
                bool include = Tree.Roots.Any(n => n.IsChecked != true);
                foreach (var root in Tree.Roots)
                    Selection.Set(root.FullPath, true, include);
                ApplySelection();
            };
            Control<CheckBox>("SelectAllFiles").Click += delegate
            {
                if (!MutationBlocked && SourceViewCurrent && previewParent != null)
                    previewParent.IsChecked = previewParent.IsChecked != true;
            };
        }

        private void UpdateSelectionHeaders()
        {
            var all = Control<CheckBox>("SelectAllTree");
            all.IsChecked = Tree.Roots.Count == 0 || Tree.Roots.All(n => n.IsChecked == false) ? (bool? )false : Tree.Roots.All(n => n.IsChecked == true) ? true : (bool? )null;
            var files = Control<CheckBox>("SelectAllFiles");
            files.IsEnabled = !MutationBlocked && SourceViewCurrent && previewParent != null;
            files.IsChecked = previewParent != null ? previewParent.IsChecked : false;
        }

        private void EditSettings()
        {
            string old = Backend.Catalog.GetSetting("backup_password_v1") ?? Workspace.Tasks.Where(t => !t.Deleted && !t.UseCustomPassword).Select(t => t.Password).FirstOrDefault() ?? "";
            int memory;
            bool backupNow;
            Backup.Core.BandwidthSchedule bandwidth;
            string databasePassword;
            System.Collections.Generic.List<string> databaseTargets;
            string value = EditorDialogs.Settings(
                Window,
                old,
                Backend.ResolveDatabaseBackupPassword(),
                Backend.MemoryLimitMiB,
                out memory,
                Backend.Catalog.LoadTopology(),
                Backend.DatabaseBackupTargets(),
                out databaseTargets,
                out databasePassword,
                out backupNow,
                Backend.BandwidthSettings(),
                out bandwidth
            );
            if (value == null)
                return;
            RunConfiguration(delegate
            {
                AdoptWorkspace(Backend.SaveApplicationSettings(Workspace, value, databasePassword, memory, databaseTargets, bandwidth));
                ApplyCommittedChanges();
            });
            if (backupNow)
                StartDatabaseBackupNow();
        }

        private async void StartDatabaseBackupNow()
        {
            await GuardAsync(BackupDatabaseNowAsync);
        }

        public System.Threading.Tasks.Task BackupDatabaseNowAsync()
        {
            return RunOperationAsync(
                OperationKind.Backup,
                async delegate
            {
                BeginUploadNotifications();
                Backend.BeginBackup();
                SetBusy(true, true, "正在备份数据库…");
                var report = OperationReporter("");
                try
                {
                    var result = await System.Threading.Tasks.Task.Run(() => Backend.BackupDatabase(null, report, true));
                    DrainTransferNotifications();
                    bool pending = Backend.Catalog.HasOutstandingTransfer(result.Archive.ArchiveId);
                    Log((pending ? "数据库快照上传结果待确认：" : "数据库备份完成：") + result.Archive.ArchiveName, pending ? OperationState.Attention : OperationState.Succeeded);
                    SetOperationText((pending ? "数据库快照上传结果待确认：" : "数据库备份完成：") + result.Archive.ArchiveName, pending ? OperationState.Attention : OperationState.Succeeded);
                    OperationProgress(100, false);
                }
                finally
                {
                    Backend.EndBackup();
                    SetBusy(false, false, null);
                }
            }
            );
        }
    }
}
