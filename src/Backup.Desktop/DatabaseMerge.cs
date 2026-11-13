using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed class DatabaseImportSelection
    {
        public bool Merge;
        public List<string> TaskIds = new List<string>(), WarehouseIds = new List<string>(), ScheduleIds = new List<string>();
    }

    public sealed class DatabaseImportPreview
    {
        public List<ChoiceRow> Tasks = new List<ChoiceRow>(), Warehouses = new List<ChoiceRow>(), Schedules = new List<ChoiceRow>();
    }

    public sealed partial class DesktopBackend
    {
        private BackupCatalog OpenDatabaseImport(string input, MemoryBudget memory, bool configurationOnly = false)
        {
            var catalog = configurationOnly ? BackupCatalog.PreviewImportFile(input, memory) : BackupCatalog.FromImportFileMemory(input, memory, true);
            try
            {
                if (string.IsNullOrEmpty(catalog.GetSetting("repository_id")))
                    throw new IOException("所选文件不是可恢复的 Backup 数据库。");
                WorkspaceMigration.Load(catalog);
                catalog.LoadTopology().Normalize();
                return catalog;
            }
            catch (global::System.Exception diagnosticFailure)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/DatabaseMerge.cs:OpenDatabaseImport", diagnosticFailure);
                catalog.Dispose();
                throw;
            }
        }

        public DatabaseImportPreview PreviewDatabaseImport(string input)
        {
            using (var source = OpenDatabaseImport(input, new MemoryBudget(MemoryLimitBytes), true))
            {
                var result = new DatabaseImportPreview();
                var topology = source.LoadTopology();
                result.Tasks = WorkspaceMigration.Load(source).Tasks.Where(t => !t.Deleted).Select(t => new ChoiceRow { Id = t.Id, Name = t.Name, Selected = true }).ToList();
                result.Warehouses = topology.Destinations.Where(d => !d.Retired).Select(d => new ChoiceRow {
                    Id = d.Id,
                    Name = topology.Groups.Where(g => g.Id == "single:" + d.Id).Select(g => g.Name)
                    .FirstOrDefault() ??
                    d.Id,
                    ToolTip = d.Location,
                    Selected = true
                })
                    .ToList();
                result.Schedules = JsonSettings.Decode(source.GetSetting("backup_schedules_v1"), new List<BackupSchedule>()).Select(p => new ChoiceRow {
                    Id = p.Id,
                    Name = p.Name,
                    ToolTip = p.FrequencyText,
                    Selected = true
                })
                    .ToList();
                return result;
            }
        }

        public string MergeDatabase(string input, DatabaseImportSelection selection)
        {
            if (selection == null || !selection.Merge)
                throw new ArgumentException("请选择合并导入。");
            if (selection.TaskIds.Count + selection.WarehouseIds.Count + selection.ScheduleIds.Count == 0)
                throw new ArgumentException("请至少勾选一个导入项目。");
            var memory = new MemoryBudget(MemoryLimitBytes);
            using (var guard = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var source = OpenDatabaseImport(input, memory, true))
            {
                byte[] current = Catalog.CreateSnapshotBytes();
                using (memory.Reserve(checked(current.LongLength * 3)))
                using (var candidate = BackupCatalog.FromMemory(current))
                {
                    ApplyDatabaseMerge(candidate, source, selection, tasks => BackupCatalog.ReadMergeImportFile(input, memory, tasks));
                    candidate.ValidateIntegrity();
                    string rollback = Path.Combine(CacheDirectory, "catalog-before-import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".db");
                    Catalog.CreateSnapshot(rollback);
                    lastIntegrityReview = null;
                    repositoryRuntime.Reset(delegate
                    {
                        Catalog.ReplaceWith(candidate);
                        sourceSnapshots.Clear();
                        ClearBrowseNodeSnapshots();
                        InvalidateRepositoryConnections();
                        Catalog.ClearRuntimeCaches();
                    });
                    return rollback;
                }
            }
        }

        private static string AvailableImportId(string preferred, IEnumerable<string> used)
        {
            return used.Contains(preferred) ? Guid.NewGuid().ToString("N") : preferred;
        }

        private static string AvailableImportName(string preferred, IEnumerable<string> used)
        {
            var names = new HashSet<string>(used, StringComparer.CurrentCultureIgnoreCase);
            if (!names.Contains(preferred))
                return preferred;
            for (int i = 2;; i++)
            {
                string value = preferred + " (" + i + ")";
                if (!names.Contains(value))
                    return value;
            }
        }

        private static bool SameImportLocation(DestinationDefinition left, DestinationDefinition right)
        {
            return DatabaseImportComparer.DestinationKey(left) == DatabaseImportComparer.DestinationKey(right);
        }

        private static void ApplyDatabaseMerge(BackupCatalog target, BackupCatalog source, DatabaseImportSelection selection, Func<ISet<string>, BackupCatalog> records)
        {
            target.DiscardImportOrigins();
            var incoming = WorkspaceMigration.Load(source);
            var workspace = WorkspaceMigration.Load(target);
            var oldTopology = source.LoadTopology();
            var topology = target.LoadTopology();
            var plan = new CatalogMergePlan();
            var schedules = JsonSettings.Decode(target.GetSetting("backup_schedules_v1"), new List<BackupSchedule>());
            var importedSchedules = JsonSettings.Decode(source.GetSetting("backup_schedules_v1"), new List<BackupSchedule>());
            if (selection.TaskIds.Except(incoming.Tasks.Where(t => !t.Deleted).Select(t => t.Id)).Any() || selection.WarehouseIds.Except(oldTopology.Destinations.Select(d => d.Id)).Any() ||
                selection.ScheduleIds.Except(importedSchedules.Select(p => p.Id))
                .Any())
                throw new IOException("所选数据库的导入项目已变化，请重新选择。");
            foreach (var destination in oldTopology.Destinations)
            {
                var existing = topology.Destinations.FirstOrDefault(d => SameImportLocation(d, destination));
                if (existing != null)
                {
                    plan.Destinations[destination.Id] = existing.Id;
                    continue;
                }

                if (!selection.WarehouseIds.Contains(destination.Id))
                    continue;
                string id = AvailableImportId(destination.Id, topology.Destinations.Select(d => d.Id));
                topology.Destinations.Add(new DestinationDefinition { Id = id, Protocol = destination.Protocol, Location = destination.Location, MaxBytes = destination.MaxBytes, Enabled = destination.Enabled });
                plan.Destinations[destination.Id] = id;
                foreach (string prefix in new[]
                {
                    "connection:",
                    "connection_reference:"
                }

                )
                {
                    string value = source.GetSetting(prefix + destination.Id);
                    if (value != null)
                        target.SetSetting(prefix + id, value);
                }
            }

            // Groups are included only when all of their members are selected or
            // already present. Never silently bring in an unchecked repository.
            var remaining = oldTopology.Groups.ToList();
            bool advanced = true;
            while (advanced)
            {
                advanced = false;
                foreach (var group in remaining.ToList())
                {
                    if (group.DestinationIds.Any(id => !plan.Destinations.ContainsKey(id)) || group.ChildGroupIds.Any(id => !plan.Groups.ContainsKey(id)))
                        continue;
                    var members = group.DestinationIds.Select(id => plan.Destinations[id]).ToList();
                    var children = group.ChildGroupIds.Select(id => plan.Groups[id]).ToList();
                    var existing = topology.Groups.FirstOrDefault(g => (g.Mode ?? "raid0") == (group.Mode ?? "raid0") && new HashSet<string>(g.DestinationIds).SetEquals(members) &&
                        new HashSet<string>(g.ChildGroupIds).SetEquals(children));
                    string mergedGroupId;
                    if (existing != null)
                        mergedGroupId = existing.Id;
                    else
                    {
                        mergedGroupId = group.Id.StartsWith("single:", StringComparison.Ordinal) && members.Count == 1 ? "single:" + members[0] : AvailableImportId(group.Id, topology.Groups.Select(g => g.Id));
                        if (topology.Groups.Any(g => g.Id == mergedGroupId))
                            throw new IOException("仓库组合编号冲突，未合并：" + group.Name);
                        topology.Groups.Add(new BackupGroupDefinition {
                            Id = mergedGroupId,
                            Name = AvailableImportName(group.Name, topology.Groups.Select(g => g.Name)),
                            Notes = group.Notes,
                            Mode = group.Mode,
                            DestinationIds = members,
                            ChildGroupIds = children
                        });
                    }

                    plan.Groups[group.Id] = mergedGroupId;
                    remaining.Remove(group);
                    advanced = true;
                }
            }

            try
            {
                topology = topology.Normalize();
            }
            catch (ArgumentException error)
            {
                throw new IOException("导入仓库与现有仓库的目录或组合冲突，未合并：" + error.Message, error);
            }

            foreach (var pair in plan.Destinations)
            {
                var group = topology.Groups.Where(g => g.DestinationIds.Contains(pair.Value)).OrderBy(g => g.DestinationIds.Count + g.ChildGroupIds.Count).FirstOrDefault();
                if (group != null)
                    plan.FallbackGroups[pair.Key] = group.Id;
            }

            target.SaveTopology(topology);
            var sources = new Dictionary<string, string>(StringComparer.Ordinal);
            var taskLinks = new Dictionary<string, string>(StringComparer.Ordinal);
            var taskKeys = workspace.Tasks.Where(t => !t.Deleted).ToDictionary(t => t.Id, t => DatabaseImportComparer.TaskKey(t, workspace, topology), StringComparer.Ordinal);
            foreach (var original in incoming.Tasks.Where(t => !t.Deleted && selection.TaskIds.Contains(t.Id)))
            {
                var targets = original.TargetIds.Where(plan.Groups.ContainsKey).Select(key => plan.Groups[key]).Distinct().ToList();
                string signature = DatabaseImportComparer.TaskKey(original, incoming, topology, targets);
                var match = workspace.Tasks.FirstOrDefault(t => !t.Deleted && taskKeys[t.Id] == signature);
                if (match != null)
                {
                    taskLinks[original.Id] = match.Id;
                    continue;
                }

                foreach (string sourceId in original.SourceIds)
                {
                    if (sources.ContainsKey(sourceId))
                        continue;
                    var item = incoming.Sources.SingleOrDefault(s => s.Id == sourceId && !s.Deleted);
                    if (item == null)
                        throw new IOException("任务的来源文件夹记录缺失，未合并：" + original.Name);
                    var existing = workspace.Sources.FirstOrDefault(s => !s.Deleted && DatabaseImportComparer.SameSource(s, item));
                    if (existing != null)
                        sources[sourceId] = existing.Id;
                    else
                    {
                        string mapped = AvailableImportId(item.Id, workspace.Sources.Select(s => s.Id));
                        workspace.Sources.Add(new SourceDefinition { Id = mapped, Name = item.Name, Location = item.Location });
                        sources[sourceId] = mapped;
                    }
                }

                var task = JsonSettings.Decode(JsonSettings.Encode(original), new BackupTaskDefinition());
                task.Id = AvailableImportId(original.Id, workspace.Tasks.Select(t => t.Id));
                task.Name = AvailableImportName(task.Name, workspace.Tasks.Select(t => t.Name));
                task.RecoveryPendingConfirmation = true;
                task.SourceIds = original.SourceIds.Select(key => sources[key]).Distinct().ToList();
                task.TargetIds = targets;
                task.PendingTargetReplacement = false;
                // Preserve the task's effective password without changing this
                // installation's default password or other global preferences.
                if (!task.UseCustomPassword)
                {
                    string password = source.GetSetting("backup_password_v1") ?? task.Password ?? "";
                    if (password != (target.GetSetting("backup_password_v1") ?? ""))
                    {
                        task.UseCustomPassword = !string.IsNullOrEmpty(password);
                        task.Password = password;
                    }
                }

                string tag = source.GetSetting("archive_task_tag:" + original.Id);
                if (tag != null)
                {
                    if (workspace.Tasks.Any(t => t.Id != task.Id && string.Equals(target.GetSetting("archive_task_tag:" + t.Id), tag, StringComparison.OrdinalIgnoreCase)))
                        throw new IOException("任务代号与现有任务重复，未合并：" + original.Name + "（" + tag + "）。原备份包保持不变。");
                    target.SetSetting("archive_task_tag:" + task.Id, tag);
                }

                workspace.Tasks.Add(task);
                plan.Tasks[original.Id] = task.Id;
                taskLinks[original.Id] = task.Id;
                taskKeys[task.Id] = signature;
                string labels = source.GetSetting("archive_root_labels:" + original.Id);
                if (labels != null)
                    target.SetSetting("archive_root_labels:" + task.Id, labels);
            }

            // Existing tasks can satisfy a selected schedule, but unchecked tasks
            // are never imported implicitly.
            var scheduledTasks = new HashSet<string>(importedSchedules.Where(p => selection.ScheduleIds.Contains(p.Id)).SelectMany(p => p.TaskIds), StringComparer.Ordinal);
            foreach (var task in incoming.Tasks.Where(t => !t.Deleted && scheduledTasks.Contains(t.Id) && !taskLinks.ContainsKey(t.Id)))
            {
                string signature = DatabaseImportComparer.TaskKey(task, incoming, topology, task.TargetIds.Where(plan.Groups.ContainsKey).Select(key => plan.Groups[key]));
                var match = workspace.Tasks.FirstOrDefault(t => !t.Deleted && taskKeys[t.Id] == signature);
                if (match != null)
                    taskLinks[task.Id] = match.Id;
            }

            foreach (var original in importedSchedules.Where(p => selection.ScheduleIds.Contains(p.Id)))
            {
                var schedule = JsonSettings.Decode(JsonSettings.Encode(original), new BackupSchedule());
                schedule.Id = AvailableImportId(original.Id, schedules.Select(p => p.Id));
                schedule.Name = AvailableImportName(schedule.Name, schedules.Select(p => p.Name));
                schedule.TaskIds = original.TaskIds.Where(taskLinks.ContainsKey).Select(id => taskLinks[id]).Distinct().ToList();
                schedule.Enabled = false;
                schedule.PendingImportConfirmation = true;
                schedule.LastRunUtc = "";
                schedule.LastResult = schedule.TaskIds.Count == original.TaskIds.Count ? "导入后已停用，请核对后手动启用。" : "导入后已停用，部分关联任务未导入，请编辑计划后启用。";
                if (schedule.TaskIds.Count == 0)
                    throw new IOException("定时任务“" + original.Name + "”没有可关联的任务，请同时勾选它使用的任务后重新合并。");
                if (schedules.Any(p => DatabaseImportComparer.SameSchedule(p, schedule)))
                    continue;
                schedules.Add(schedule);
            }

            WorkspaceMigration.Validate(workspace, topology);
            if (plan.Tasks.Count > 0)
                using (var history = records(new HashSet<string>(plan.Tasks.Keys, StringComparer.Ordinal)))
                    target.MergeBackupRecords(history, plan);
            target.SetSetting("workspace_v3", JsonSettings.Encode(workspace));
            target.SetSetting("backup_schedules_v1", JsonSettings.Encode(schedules));
        }
    }
}
