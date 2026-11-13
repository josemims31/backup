using System;
using System.Collections.Generic;
using System.Linq;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        internal const string DatabaseBackupTargetsKey = "catalog_backup_targets_v1";
        internal const string DatabaseBackupPasswordKey = "catalog_backup_password_v1";
        public string ResolveDatabaseBackupPassword()
        {
            return Catalog.GetSetting(DatabaseBackupPasswordKey) ?? Catalog.GetSetting("backup_password_v1") ?? "";
        }

        private void MigrateSnapshotRepairOwners()
        {
            string json = Catalog.GetSetting("confirmed_repairs_v1");
            if (string.IsNullOrEmpty(json))
                return;
            var repairs = JsonSettings.Decode(json, new List<ConfirmedRepair>());
            bool changed = false;
            foreach (var repair in repairs.Where(item => item.TaskId != "__catalog__"))
                try
                {
                    if (Catalog.GetArchive(repair.ArchiveId).TaskId == "__catalog__")
                    {
                        repair.TaskId = "__catalog__";
                        changed = true;
                    }
                }
                catch (ArgumentException diagnosticFailure)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/DatabaseBackupSettings.cs:MigrateSnapshotRepairOwners", diagnosticFailure);
                }

            if (changed)
                Catalog.SetSetting("confirmed_repairs_v1", JsonSettings.Encode(repairs));
        }

        public IList<string> DatabaseBackupTargets()
        {
            return JsonSettings.Decode(Catalog.GetSetting(DatabaseBackupTargetsKey), new List<string>()).Distinct(StringComparer.Ordinal).ToList();
        }

        private void ReconcileDatabaseBackupRegistrations(ISet<string> taskIds, Action<string> report)
        {
            if (taskIds != null && !taskIds.Contains("__catalog__"))
                return;
            OperationCancellation.ThrowIfCancellationRequested();
            var removed = new HashSet<long>(Catalog.UnregisterUnusedCatalogSnapshots(DatabaseBackupTargets()));
            if (removed.Count == 0)
                return;
            var queue = PendingRepairs();
            var retained = queue.Where(item => item.TaskId != "__catalog__" || !removed.Contains(item.ArchiveId)).ToList();
            if (retained.Count != queue.Count)
                SaveRepairQueue(retained);
            if (report != null)
                report("已取消 " + removed.Count + " 个历史数据库快照的登记，远程文件保持原样。");
        }

        public void SaveDatabaseBackupTargets(IEnumerable<string> selected)
        {
            var targets = selected.Distinct(StringComparer.Ordinal).ToList();
            ValidateDatabaseBackupTargets(targets);
            Catalog.SaveCatalogBackupTargets(targets.Count == 0 ? null : JsonSettings.Encode(targets));
        }

        private void ValidateDatabaseBackupTargets(IList<string> targets)
        {
            ValidateDatabaseBackupTargets(targets, Catalog.LoadTopology());
        }

        private void ValidateDatabaseBackupTargets(IList<string> targets, StorageTopology topology)
        {
            if (targets.Count == 0)
                return;
            if (targets.Any(id => !topology.Groups.Any(group => group.Id == id && !group.Retired) || topology.ResolveDestinations(id, false).Count == 0))
                throw new ArgumentException("数据库备份仓库已移除或不可用，请在设置中重新选择。");
            WorkspaceMigration.ValidateTargetPolicy(new BackupTaskDefinition { Id = "__catalog__", TargetIds = targets.ToList() }, topology);
        }
    }
}
