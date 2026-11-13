using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Backup.Core
{
    public sealed class TaskDeletionMetadata
    {
        public string TaskId, Tag;
        public bool SharedMetadata;
        public List<long> ArchiveIds = new List<long>();
        public List<string> NodeIds = new List<string>();
        public List<string> DestinationIds = new List<string>();
    }

    public sealed partial class BackupCatalog
    {
        public TaskDeletionMetadata CaptureTaskDeletion(string taskId)
        {
            lock (gate)
            {
                var result = new TaskDeletionMetadata
                {
                    TaskId = taskId,
                    Tag = EnsureTaskTag(taskId)
                };
                if (!Regex.IsMatch(result.Tag, @"\A[A-Za-z0-9]{4}\z") ||
                    Scalar("SELECT count(*) FROM settings WHERE key GLOB 'archive_task_tag:*' AND key<>? AND value=? COLLATE NOCASE", "archive_task_tag:" +
                    taskId, result.Tag) > 0)
                    throw new InvalidOperationException("任务代号无效或重复，未删除任何文件。");
                result.NodeIds.AddRange(database.Query("SELECT id FROM backup_nodes WHERE task_id=? UNION SELECT node_id FROM node_revisions WHERE task_id=? UNION SELECT id FROM local_backup_nodes WHERE task_id=?", taskId, taskId, taskId)
                    .Select(r => (string)r[0]));
                var related = database.Query(
                    "SELECT id FROM archives WHERE task_id=? OR task_id=? OR task_id=? OR name GLOB ? OR name GLOB ? UNION SELECT archive_id FROM node_revisions WHERE task_id=?",
                    taskId,
                    "__repack__:" +
                    taskId,
                    "__node__:" +
                    taskId,
                    "data-" +
                    result.Tag +
                    "-*",
                    "node-" +
                    result.Tag +
                    "-*",
                    taskId
                )
                    .Select(r => (long)r[0])
                    .ToList();
                foreach (long id in related)
                {
                    if (Scalar("SELECT count(*) FROM node_revisions WHERE archive_id=? AND task_id<>?", id, taskId) > 0 ||
                        Scalar("SELECT count(*) FROM backup_nodes WHERE archive_id=? AND task_id<>?", id, taskId) > 0)
                    {
                        result.SharedMetadata = true;
                        continue;
                    }

                    if (Scalar("SELECT count(*) FROM archive_leases WHERE archive_id=?", id) > 0)
                        throw new InvalidOperationException("请先停止正在使用此任务备份的操作。");
                    result.ArchiveIds.Add(id);
                    result.DestinationIds.AddRange(database.Query(("SELECT destination_id FROM placements WHERE archive_id=? UNION SELECT destination_id FROM object_locations WHERE name " +
                        "IN(SELECT name FROM objects WHERE archive_id=?)"), id, id)
                        .Select(r => (string)r[0]));
                }

                result.DestinationIds = result.DestinationIds.Distinct().ToList();
                return result;
            }
        }

        public void ForgetTaskBackupsAndSave(TaskDeletionMetadata task, string workspace, string schedules, string repairs, string pendingDeletes)
        {
            Transaction(delegate
            {
                foreach (long id in task.ArchiveIds)
                    if (Scalar("SELECT count(*) FROM archive_leases WHERE archive_id=?", id) > 0)
                        throw new InvalidOperationException("请先停止正在使用此任务备份的操作。");
                database.Execute("DELETE FROM backup_nodes WHERE task_id=?", task.TaskId);
                database.Execute("DELETE FROM node_revisions WHERE task_id=?", task.TaskId);
                database.Execute("DELETE FROM local_backup_nodes WHERE task_id=?", task.TaskId);
                foreach (long id in task.ArchiveIds)
                {
                    var stages = database.Query("SELECT shadow_group FROM replica_stages WHERE archive_id=?", id).Select(r => (string)r[0]).ToList();
                    UnregisterUnusedArchiveUnsafe(id, task.TaskId);
                    foreach (string shadow in stages)
                        DiscardReplicaStageUnsafe(shadow);
                    database.Execute("DELETE FROM archive_files WHERE archive_id=?", id);
                }

                database.Execute("DELETE FROM file_work.node_basis WHERE task_id=?", task.TaskId);
                database.Execute("DELETE FROM storage_growth WHERE tag=?", task.Tag);
                database.Execute("DELETE FROM storage_trees WHERE tag=?", task.Tag);
                SaveSettingUnsafe("workspace_v3", workspace);
                if (schedules != null)
                    SaveSettingUnsafe("backup_schedules_v1", schedules);
                SaveSettingUnsafe("confirmed_repairs_v1", repairs);
                SaveSettingUnsafe("task_directory_deletes_v1", pendingDeletes);
                // Reserve its code while remote remnants or deletion retries can
                // still exist, so a new task can never inherit these directories.
                RepositoryStatusChanged(CatalogChange.Archives | CatalogChange.Workspace, task.TaskId);
                return 0;
            });
        }
    }
}
