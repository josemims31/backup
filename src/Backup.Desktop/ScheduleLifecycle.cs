using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        private void CommitSchedulePruning(WorkspaceDefinition workspace, Action<string> commit)
        {
            var active = new HashSet<string>(workspace.Tasks.Where(task => !task.Deleted).Select(task => task.Id));
            var original = Schedules();
            var next = new List<BackupSchedule>();
            bool changed = false;
            foreach (var plan in original)
            {
                var retained = plan.TaskIds.Where(active.Contains).Distinct().ToList();
                if (retained.Count == 0)
                {
                    changed = true;
                    continue;
                }

                if (!retained.SequenceEqual(plan.TaskIds))
                    changed = true;
                var copy = JsonSettings.Decode(JsonSettings.Encode(plan), new BackupSchedule());
                copy.TaskIds = retained;
                next.Add(copy);
            }

            var keep = new HashSet<string>(next.Select(plan => plan.Id));
            var removed = new List<BackupSchedule>();
            try
            {
                // Windows actions contain only the plan ID. Shared plans need no trigger rewrite.
                foreach (var plan in original.Where(plan => !keep.Contains(plan.Id)))
                {
                    removed.Add(plan);
                    WindowsIntegration.DeleteSchedule(RepositoryId, plan.Id);
                }

                commit(changed ? JsonSettings.Encode(next) : null);
            }
            catch (Exception error)
            {
                var failures = new List<Exception>
                {
                    error
                };
                foreach (var plan in removed)
                    try
                    {
                        WindowsIntegration.RegisterSchedule(RepositoryId, plan, Path.Combine(BaseDirectory, "Backup.exe"));
                    }
                    catch (Exception rollback)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/ScheduleLifecycle.cs:CommitSchedulePruning", rollback);
                        failures.Add(rollback);
                    }

                if (failures.Count > 1)
                    throw new IOException("操作未完成，原任务数据已保留；部分 Windows 计划恢复失败，请重新启动软件后重试。", new AggregateException(failures));
                throw;
            }
        }

        public void ReconcileSchedules()
        {
            var workspace = WorkspaceMigration.Load(Catalog);
            var active = new HashSet<string>(workspace.Tasks.Where(t => !t.Deleted).Select(t => t.Id));
            foreach (var task in workspace.Tasks.Where(t => t.Deleted))
            {
                task.Password = "";
                task.SourceIds.Clear();
                task.Selection = new Backup.Core.FileSelection();
            }

            workspace.Tasks.RemoveAll(t => t.Deleted && !Catalog.TaskHasPreservedArchives(t.Id));
            workspace.Sources.RemoveAll(s => s.Deleted);
            CommitSchedulePruning(
                workspace,
                json =>
            {
                Catalog.SaveWorkspaceMaintenance(JsonSettings.Encode(workspace), json, JsonSettings.Encode(PendingRepairs().Where(r => r.TaskId == "__catalog__" || active.Contains(r.TaskId)).ToList()));
            }
            );
        }
    }
}
