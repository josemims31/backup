using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        private static WorkspaceDefinition CopyWorkspace(WorkspaceDefinition workspace)
        {
            return JsonSettings.Decode(JsonSettings.Encode(workspace), new WorkspaceDefinition());
        }

        public WorkspaceDefinition DeleteTask(WorkspaceDefinition workspace, string id, bool keep, string manifestPath, Action<string> report = null)
        {
            if (!keep)
                return DeleteTaskDirectories(workspace, id, report);
            var draft = CopyWorkspace(workspace);
            var task = draft.Tasks.Single(t => t.Id == id && !t.Deleted);
            task.Deleted = true;
            if (keep && string.IsNullOrWhiteSpace(manifestPath))
                throw new ArgumentException("请选择待清理清单保存位置。");
            CommitSchedulePruning(
                draft,
                schedulesJson =>
            {
                if (keep)
                {
                    if (string.IsNullOrWhiteSpace(manifestPath))
                        throw new ArgumentException("请选择待清理清单保存位置。");
                    var definitions = Catalog.LoadTopology().Destinations.ToDictionary(d => d.Id);
                    using (var file = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(file, new UTF8Encoding(true)))
                    {
                        writer.WriteLine("待清理清单");
                        writer.WriteLine("任务：" + task.Name);
                        writer.WriteLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        writer.WriteLine("以下每行是一个备份文件的完整位置，请按需手动删除。不要删除原文件或整个仓库目录。");
                        writer.WriteLine("位置以生成清单时为准；部分传输文件可能尚未写入，也可能残留同名的 .partial 临时文件，清理前请核对。");
                        writer.WriteLine();
                        var metadata = new HashSet<long>(Catalog.NodeRevisions().Where(n => n.TaskId == id).Select(n => n.ArchiveId));
                        foreach (var archive in Catalog.ListArchives(true).Where(a => a.TaskId == id || metadata.Contains(a.ArchiveId)))
                            foreach (var copy in Catalog.GetCleanupManifestCopies(archive.ArchiveId).OrderBy(c => c.DestinationId).ThenBy(c => c.Object.Name))
                                writer.WriteLine(ObjectLocation(definitions[copy.DestinationId], copy.Object.Name));
                    }
                }

                task.Password = "";
                task.SourceIds.Clear();
                task.Selection = new FileSelection();
                if (!keep || !Catalog.TaskHasNodes(id) && !Catalog.ListArchives().Any(a => a.TaskId == id && a.Status != "cleaned"))
                    draft.Tasks.Remove(task);
                Catalog.RemoveTaskAndSave(id, keep, JsonSettings.Encode(draft), schedulesJson, JsonSettings.Encode(PendingRepairs().Where(r => r.TaskId != id).ToList()));
            }
            );
            return draft;
        }

        private static bool Under(string path, string root)
        {
            return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        public WorkspaceDefinition DeleteSource(WorkspaceDefinition workspace, string id)
        {
            var draft = CopyWorkspace(workspace);
            var removed = draft.Sources.Single(s => s.Id == id && !s.Deleted);
            removed.Deleted = true;
            var invalid = new List<FileReference>();
            foreach (var task in draft.Tasks.Where(t => !t.Deleted && t.SourceIds.Contains(id)))
            {
                task.SourceIds.Remove(id);
                var retained = draft.Sources.Where(s => !s.Deleted && task.SourceIds.Contains(s.Id)).ToList();
                foreach (var file in Catalog.GetKnownSourceFiles(task.Id))
                {
                    string path = SourcePath(file);
                    if (Under(path, removed.Location) && !retained.Any(s => Under(path, s.Location)))
                        invalid.Add(new FileReference { TaskId = task.Id, Path = file.Path });
                }
            }

            draft.Sources.RemoveAll(s => s.Deleted);
            Catalog.InvalidatePathsAndSave(invalid, JsonSettings.Encode(draft));
            Catalog.RemoveSourceCache(id);
            return draft;
        }

        private static string SourcePath(FileHistoryRow row)
        {
            if (!string.IsNullOrEmpty(row.SourcePath))
                return row.SourcePath;
            if (row.Path.StartsWith("UNC\\"))
                return @"\\" + row.Path.Substring(4);
            if (row.Path.Length > 2 && row.Path[1] == '\\')
                return row.Path.Substring(0, 1) + ":\\" + row.Path.Substring(2);
            return row.Path;
        }

        public static void ResolveRetiredTargets(WorkspaceDefinition workspace, StorageTopology topology, IList<DestinationUsage> usage = null, ISet<string> taskIds = null)
        {
            var stats = (usage ?? new DestinationUsage[0]).ToDictionary(u => u.DestinationId);
            foreach (var task in workspace.Tasks.Where(t => taskIds == null || taskIds.Contains(t.Id)))
            {
                int before = task.TargetIds.Distinct().Count(), desired = Math.Max(before, task.PendingTargetReplacement ? task.Copies : 0);
                var valid = task.TargetIds.Where(id => topology.Groups.Any(g => g.Id == id && !g.Retired) && topology.ResolveDestinations(id, false).Count > 0).Distinct().ToList();
                bool affected = valid.Count != before || task.PendingTargetReplacement;
                if (!affected && topology.Destinations.Any(d => d.Retired) && valid.Count > 0)
                    affected = !WorkspaceMigration.SatisfiesTargets(topology, valid);
                if (!affected)
                    continue;
                task.TargetIds = new List<string>();
                foreach (string id in valid)
                {
                    var next = task.TargetIds.Concat(new[] { id }).ToList();
                    if (WorkspaceMigration.SatisfiesTargets(topology, next))
                        task.TargetIds.Add(id);
                }

                var choices = topology.Groups.Where(g => !g.Retired && g.Id.StartsWith("single:") && !task.TargetIds.Contains(g.Id) && topology.ResolveDestinations(g.Id, false).Count > 0).OrderBy(g =>
                {
                    var d = topology.Destinations.Single(x => x.Id == g.DestinationIds.Single());
                    DestinationUsage used;
                    stats.TryGetValue(d.Id, out used);
                    return used == null ? 0 : ((decimal)used.UsedBytes + used.ReservedBytes) / d.MaxBytes;
                })
                    .ThenBy(g => g.Id, StringComparer.Ordinal);
                foreach (var choice in choices)
                {
                    if (WorkspaceMigration.PolicyCopies(topology, task.TargetIds) >= desired)
                        break;
                    var next = task.TargetIds.Concat(new[] { choice.Id }).ToList();
                    if (WorkspaceMigration.SatisfiesTargets(topology, next))
                        task.TargetIds.Add(choice.Id);
                }

                task.Copies = Math.Max(1, desired);
                task.PendingTargetReplacement = WorkspaceMigration.PolicyCopies(topology, task.TargetIds) < desired;
            }
        }

        public WorkspaceDefinition DeleteWarehouse(WorkspaceDefinition workspace, string id, bool immediate)
        {
            return DeleteWarehouse(workspace, id, immediate ? "immediate" : "next-backup");
        }

        public WorkspaceDefinition DeleteWarehouse(WorkspaceDefinition workspace, string id, string mode)
        {
            if (!new[]
            {
                "immediate",
                "next-backup",
                "delete",
                "keep"
            }.Contains(mode))
                throw new ArgumentException("删除方式无效。");
            var previousTopology = Catalog.LoadTopology();
            var topology = Catalog.LoadTopology();
            var draft = CopyWorkspace(workspace);
            var affected = new HashSet<string>();
            var removed = new HashSet<string>(topology.ResolveDestinations(id));
            foreach (var task in draft.Tasks.Where(t => !t.Deleted && t.TargetIds.Count > 0))
            {
                var policy = topology.Scope(task.TargetIds);
                if (task.TargetIds.Any(g => id.StartsWith("single:") ? topology.ResolveDestinations(g).Any(removed.Contains) : g == id))
                {
                    affected.Add(task.Id);
                    if (policy.PolicyRoots != null)
                    {
                        task.Copies = Math.Max(task.Copies, WorkspaceMigration.PolicyCopies(topology, task.TargetIds));
                        task.PendingTargetReplacement = true;
                    }
                }
            }

            var group = topology.Groups.Single(g => g.Id == id && !g.Retired);
            group.Retired = true;
            if (id.StartsWith("single:"))
                topology.Destinations.Single(d => d.Id == group.DestinationIds.Single()).Retired = true;
            ValidateDatabaseRetirement(previousTopology, topology);
            ResolveRetiredTargets(draft, topology, Catalog.GetUsage(), affected);
            if (mode == "immediate" && Catalog.ListArchives().Any(a => Catalog.NeedsPreservation(a.ArchiveId)) && !topology.Destinations.Any(d => !d.Retired && d.Enabled))
                throw new IOException("没有可迁移的替代仓库。请先添加仓库。");
            Catalog.RetireWarehouse(id, mode, JsonSettings.Encode(draft));
            return draft;
        }

        public CleanupResult DeleteDetachedObjects(Action<CleanupProgress> progress = null, ISet<long> archiveIds = null, string groupId = null)
        {
            var physical = WarehouseDestinations(groupId);
            var result = new CleanupResult();
            var objects = Catalog.GetDetachedObjects().Where(item => (archiveIds == null || archiveIds.Contains(item.ArchiveId)) &&
                (groupId == null ||
                (groupId.StartsWith("single:") ? physical.Contains(item.DestinationId) : item.GroupId == groupId)))
                .ToList();
            int completed = 0;
            var adapters = Adapters();
            var definitions = Catalog.LoadTopology().Destinations.ToDictionary(d => d.Id);
            using (adapters.BeginScope())
                foreach (var copy in objects)
                {
                    try
                    {
                        if (progress != null)
                            progress(new CleanupProgress { TotalObjects = objects.Count, DeletedObjects = completed, Message = "正在删除此仓库文件：" + copy.Object.Name });
                        var destination = adapters.Create(definitions[copy.DestinationId]) as IManagedBackupDestination;
                        if (destination == null)
                            throw new NotSupportedException("仓库不支持删除。");
                        destination.DeleteObject(copy.Object.Name);
                        if (destination.Stat(copy.Object.Name).Exists)
                            throw new IOException("备份对象未删除。");
                        destination.DeleteTemporary(copy.Object.Name);
                        Catalog.DetachedObjectDeleted(copy.Object.Name, copy.DestinationId);
                        result.DeletedObjects++;
                        result.FreedBytes += copy.Object.Size;
                        completed++;
                    }
                    catch (Exception e)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/LifecycleBackend.cs:DeleteDetachedObjects", e);
                        if (!(e is IOException) && !(e is UnauthorizedAccessException) && !(e is NotSupportedException))
                            throw;
                        Catalog.DetachedObjectError(copy.Object.Name, copy.DestinationId, e.Message);
                        result.Errors.Add(copy.Object.Name + "：" + e.Message);
                    }
                }

            result.PendingArchives = result.Errors.Count;
            LastCleanup = result;
            return result;
        }

        public IList<string> CollectGarbage(Action<string> report = null)
        {
            return new ArchiveMaintenance(Catalog, Adapters())
            {
                Report = report,
                Control = ActiveBackup
            }.CollectGarbage();
        }

        public CleanupResult CollectGarbageDetailed(Action<CleanupProgress> progress = null, string taskId = null)
        {
            LastCleanup = new ArchiveMaintenance(Catalog, Adapters())
            {
                Control = ActiveBackup
            }.CollectGarbageDetailed(progress, taskId == null ? (bool? )null : false, taskId);
            FinishOperationHistory(null, taskId);
            PruneDatabaseRecoveryCopies();
            return LastCleanup;
        }

        public IList<string> RunPendingMaintenance(
            WorkspaceDefinition workspace,
            Action<string> report = null,
            Action<CleanupProgress> cleanupProgress = null,
            string taskId = null,
            bool collectGarbage = true,
            string warehouseId = null
        )
        {
            var retirements = Catalog.GetRetirements().Where(r => r.State != "complete" && (r.Mode == "immediate" || r.Mode == "next-backup") &&
                (warehouseId == null || r.Kind == "group" && r.Id == warehouseId ||
                warehouseId.StartsWith("single:") &&
                r.Kind == "destination" &&
                r.Id == warehouseId.Substring(7)))
                .ToList();
            ISet<long> scope = taskId == null ? null : new HashSet<long>(Catalog.ListArchives(true).Where(archive => archive.TaskId == taskId).Select(archive => archive.ArchiveId));
            if (warehouseId != null)
            {
                var selected = new HashSet<long>(retirements.SelectMany(r => Catalog.GetRetirementArchives(r)));
                if (scope != null)
                    selected.IntersectWith(scope);
                scope = selected;
            }

            var affected = warehouseId == null 
                ? (taskId == null ? null : new HashSet<string>
            {
                taskId
            }

            ) 
                : new HashSet<string>(Catalog.ListArchives(true).Where(a => scope.Contains(a.ArchiveId)).Select(a => a.TaskId));
            ResolveRetiredTargets(workspace, Catalog.LoadTopology(), Catalog.GetUsage(), affected);
            Catalog.SetSetting("workspace_v3", JsonSettings.Encode(workspace));
            var physical = WarehouseDestinations(warehouseId);
            string groupScope = warehouseId == null || warehouseId.StartsWith("single:") ? null : warehouseId;
            var errors = new List<string>();
            var engine = new ArchiveMaintenance(Catalog, Adapters())
            {
                Report = report,
                Control = ActiveBackup,
                UploadProgress = ReportTransferNotice,
                RetiredDeletionDestinations = physical,
                RetiredDeletionGroupId = groupScope,
                BeforeRetiredDeletion = id => PublishMigrationCatalogs(new HashSet<long> { id }, null, report)
            };
            errors.AddRange(engine.ReconcileVolumeTransfers(null, scope));
            errors.AddRange(DeleteDetachedObjects(cleanupProgress, scope, warehouseId).Errors);
            LastCleanup = collectGarbage ? engine.CollectGarbageDetailed(cleanupProgress, taskId == null ? (bool? )null : false, taskId, scope, physical, groupScope) : new CleanupResult();
            var processed = new Dictionary<long, string>();
            foreach (var retirement in retirements)
            {
                var all = Catalog.GetRetirementArchives(retirement);
                var selected = all.Where(id => scope == null || scope.Contains(id)).ToList();
                if (taskId != null && selected.Count == 0)
                    continue;
                var failures = new List<string>();
                foreach (long id in selected)
                {
                    string failure;
                    if (!processed.TryGetValue(id, out failure))
                    {
                        failure = "";
                        try
                        {
                            if (!Catalog.NeedsPreservation(id))
                            {
                                if (Catalog.GetCopies(id).Any(copy => (physical == null || physical.Contains(copy.DestinationId)) && (groupScope == null || copy.GroupId == groupScope)))
                                    throw new IOException("失效归档仍在等待安全清理。");
                            }
                            else
                            {
                                var archive = Catalog.GetArchive(id);
                                var task = archive.TaskId == "__catalog__" 
                                    ? new BackupTaskDefinition
                                {
                                    Id = "__catalog__",
                                    TargetIds = DatabaseBackupTargets().ToList()
                                }

                                    : workspace.Tasks.SingleOrDefault(t => t.Id == archive.TaskId);
                                if (archive.TaskId == "__catalog__" && task.TargetIds.Count == 0)
                                    throw new IOException("数据库备份已关闭，未自动迁移数据库快照，旧包保留。");
                                if (task == null)
                                    throw new IOException("找不到原任务的容灾策略。");
                                if (task.PendingTargetReplacement)
                                    throw new IOException("替代仓库不足，旧副本保留。");
                                WorkspaceMigration.ValidateTargetPolicy(task, Catalog.LoadTopology());
                                engine.Migrate(id, Catalog.LoadTopology().Scope(task.TargetIds), task.RequiredCopies);
                            }

                            if (groupScope != null && Catalog.GetRetiredObjects(id).Any(copy => physical.Contains(copy.DestinationId) && string.IsNullOrEmpty(copy.GroupId)))
                                throw new IOException("旧副本的组合归属尚未确认，已保留；请通过处理待迁移核对。");
                        }
                        catch (Exception error)
                        {
                            global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/LifecycleBackend.cs:RunPendingMaintenance", error);
                            if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is NotSupportedException) && !(error is ArgumentException))
                                throw;
                            failure = "#" + id.ToString("D8") + "：" + error.Message;
                        }

                        processed[id] = failure;
                    }

                    if (failure.Length > 0)
                        failures.Add(failure);
                }

                // A task-scoped run cannot complete a shared retirement that still has other tasks.
                if (scope == null || all.All(scope.Contains))
                    Catalog.RetirementResult(retirement.Kind, retirement.Id, failures.Count == 0, string.Join("\n", failures));
                errors.AddRange(failures);
            }

            errors.AddRange(LastCleanup.Errors);
            FinishOperationHistory(scope, taskId);
            return errors.Distinct().ToList();
        }
    }
}
