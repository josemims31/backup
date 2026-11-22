using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Backup.Core
{
    public sealed class DeferredVersionDeletion
    {
        public string NodeId, TaskId;
        public long NodeArchiveId, ArchiveLimit;
        public ISet<long> DataArchives;
        public bool CleanupComplete;
    }

    public sealed partial class BackupCatalog
    {
        private void InitializeDeferredVersionDeletions()
        {
            database.Execute("CREATE TABLE IF NOT EXISTS deferred_version_deletions(node_id TEXT PRIMARY KEY,task_id TEXT NOT NULL,node_archive INTEGER NOT NULL,archive_limit INTEGER NOT NULL,data_archives TEXT,cleanup_complete INTEGER NOT NULL DEFAULT 0)");
            database.Execute("CREATE INDEX IF NOT EXISTS deferred_version_deletions_task ON deferred_version_deletions(task_id,cleanup_complete,archive_limit)");
        }

        private static string EncodeArchiveIds(IEnumerable<long> archives)
        {
            return string.Join(",", archives.Distinct().Select(id => id.ToString(CultureInfo.InvariantCulture)));
        }

        public BackupNode DeleteVersionLocally(string id)
        {
            return Transaction(delegate
            {
                var local = GetLocalBackupNodes().FirstOrDefault(item => item.Id == id);
                var node = local ?? GetNodes().FirstOrDefault(item => item.Id == id);
                if (node == null)
                    return null;

                long limit = Scalar("SELECT coalesce(max(id),0) FROM archives WHERE task_id=?", node.TaskId);
                string references = null;
                if (local != null)
                {
                    try
                    {
                        references = EncodeArchiveIds(ReadLocalBackupNode(id).ArchiveIds);
                    }
                    catch (System.IO.IOException error)
                    {
                        RuntimeDiagnostics.Record("BackupCatalog:DeleteVersionLocally", error);
                    }
                    // Reserve a fresh identity for the next unfinished version.
                    database.Execute("UPDATE local_backup_nodes SET id=?,payload=NULL,bytes=0,digest='' WHERE id=?", Guid.NewGuid().ToString("N"), id);
                    database.Execute("UPDATE recovery_revision SET value=value+1 WHERE id=1");
                    SetWorkingNodeBasis(node.TaskId, "pending-refresh");
                }
                else
                {
                    string latest = (string)database.Query("SELECT id FROM backup_nodes WHERE task_id=? ORDER BY created_utc DESC,id DESC LIMIT 1", node.TaskId)[0][0];
                    // Keep the revision and its physical copies for repair after restart.
                    RegisterNodeRevision(node);
                    database.Execute("DELETE FROM backup_nodes WHERE id=?", id);
                    if (latest == id)
                    {
                        SetWorkingNodeBasis(node.TaskId, "pending-refresh");
                        RememberVersionDeletionFloor(node.TaskId, node.ArchiveId);
                    }
                }

                if (!TaskHasNodes(node.TaskId))
                    RememberVersionDeletionFloor(node.TaskId, limit);
                database.Execute("INSERT OR REPLACE INTO deferred_version_deletions(node_id,task_id,node_archive,archive_limit,data_archives,cleanup_complete) VALUES(?,?,?,?,?,0)",
                    id, node.TaskId, node.ArchiveId, limit, references);
                ClearNodeGarbageReview();
                RepositoryStatusChanged(CatalogChange.Archives, node.TaskId);
                return node;
            });
        }

        public IList<DeferredVersionDeletion> DeferredVersionDeletions()
        {
            lock (gate)
                return database.Query("SELECT node_id,task_id,node_archive,archive_limit,data_archives,cleanup_complete FROM deferred_version_deletions").Select(row => new DeferredVersionDeletion
                {
                    NodeId = (string)row[0],
                    TaskId = (string)row[1],
                    NodeArchiveId = (long)row[2],
                    ArchiveLimit = (long)row[3],
                    DataArchives = row[4] == null ? null : new HashSet<long>(((string)row[4]).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(value => long.Parse(value, CultureInfo.InvariantCulture))),
                    CleanupComplete = (long)row[5] != 0
                }).ToList();
        }

        public void RecordDeferredVersionArchives(string id, IEnumerable<long> archives)
        {
            lock (gate)
                database.Execute("UPDATE deferred_version_deletions SET data_archives=? WHERE node_id=? AND data_archives IS NULL", EncodeArchiveIds(archives), id);
        }

        public bool CanReadDeferredVersion(BackupNode node)
        {
            lock (gate)
                return Scalar("SELECT count(*) FROM deferred_version_deletions d JOIN node_revisions r ON r.node_id=d.node_id AND r.archive_id=d.node_archive " +
                    "WHERE d.cleanup_complete=0 AND d.node_id=? AND d.task_id=? AND d.node_archive=? AND r.revision=? AND r.digest=? AND r.bytes=? AND r.entry_path=?",
                    node.Id, node.TaskId, node.ArchiveId, node.Revision, node.Digest, node.DatabaseBytes, node.EntryPath) != 0;
        }

        public ISet<long> DeferredVersionMetadataIds()
        {
            lock (gate)
                return new HashSet<long>(database.Query("SELECT DISTINCT r.archive_id FROM deferred_version_deletions d JOIN node_revisions r ON r.node_id=d.node_id " +
                    "WHERE NOT EXISTS(SELECT 1 FROM backup_nodes n WHERE n.archive_id=r.archive_id)").Select(row => (long)row[0]));
        }

        public void MarkDeferredVersionCleanupComplete(string taskId)
        {
            lock (gate)
                database.Execute("UPDATE deferred_version_deletions SET cleanup_complete=1 WHERE task_id=? AND cleanup_complete=0", taskId);
        }

        public void FinishDeferredVersionDeletions(IEnumerable<string> tasks)
        {
            Transaction(delegate
            {
                foreach (string task in tasks)
                    database.Execute("DELETE FROM deferred_version_deletions WHERE task_id=? AND cleanup_complete=1", task);
                return 0;
            });
        }

        // Archives belong to exactly one task. With no remaining version of
        // that task, its data has no version references. Unregister it locally;
        // leave all physical files for explicit orphan cleanup during repair.
        public void UnregisterVersionlessTaskData(string taskId, long maximumArchive = long.MaxValue)
        {
            Transaction(delegate
            {
                if (TaskHasNodes(taskId) || taskId.StartsWith("__", StringComparison.Ordinal))
                    return 0;
                foreach (long id in database.Query(
                    ("SELECT id FROM archives a WHERE task_id=? AND id<=? AND status NOT IN ('writing','cleaned') AND NOT EXISTS(SELECT 1 FROM archive_leases l WHERE " +
                    "l.archive_id=a.id) AND NOT EXISTS(SELECT 1 FROM replica_stages s WHERE s.archive_id=a.id)"),
                    taskId,
                    maximumArchive
                )
                    .Select(r => (long)r[0])
                    .ToList())
                {
                    if (IsCompactionArchive(id))
                        continue;
                    UnregisterUnusedArchiveUnsafe(id, taskId);
                }

                return 0;
            });
        }

        private void UnregisterUnusedArchiveUnsafe(long id, string taskId)
        {
            database.Execute(
                ("UPDATE destinations SET used_bytes=used_bytes-coalesce((SELECT sum(p.stored) FROM placements p WHERE p.archive_id=? AND " +
                "p.destination_id=destinations.id),0)-coalesce((SELECT sum(r.size) FROM retired_objects r WHERE r.archive_id=? AND r.destination_id=destinations.id),0)"),
                id,
                id
            );
            foreach (string table in new[]
            {
                "volume_transfers",
                "upload_temporaries",
                "replica_intents",
                "warehouse_move_objects"
            }

            )
                database.Execute("DELETE FROM " + table + " WHERE name IN(SELECT name FROM objects WHERE archive_id=?)", id);
            database.Execute("DELETE FROM object_copies WHERE name IN(SELECT name FROM objects WHERE archive_id=?)", id);
            foreach (string table in new[]
            {
                "retired_objects",
                "warehouse_detached",
                "archive_holds",
                "catalog_coverage",
                "node_archive_seals"
            }

            )
                database.Execute("DELETE FROM " + table + " WHERE archive_id=?", id);
            database.Execute("UPDATE archive_files SET state='expired' WHERE archive_id=? AND state<>'expired'", id);
            database.Execute("UPDATE placements SET stored=0,reserved=0,status='cleaned',error='' WHERE archive_id=?", id);
            database.Execute("UPDATE archives SET status='cleaned' WHERE id=?", id);
            database.Execute("INSERT OR REPLACE INTO archive_cleanup(archive_id,state,error) VALUES(?,'complete','')", id);
            RepositoryStatusChanged(CatalogChange.Archives, taskId);
        }

        // Rollback outputs, unadopted repacks and unpublished node databases
        // are not backup versions.
        // A published/local-version seal or any active writer/lease wins over cleanup.
        public void UnregisterAbandonedArchiveOutputs(ISet<string> tasks = null, ISet<long> activeArchives = null)
        {
            Transaction(delegate
            {
                var rows = database.Query(("SELECT a.id,a.task_id FROM archives a WHERE a.status NOT IN ('writing','cleaned') AND (substr(a.task_id,1,11)='__repack__:' OR " +
                    "substr(a.task_id,1,9)='__node__:' OR EXISTS(SELECT 1 FROM archive_cleanup c WHERE c.archive_id=a.id AND c.state='rollback')) AND NOT EXISTS(SELECT 1 FROM " +
                    "archive_leases l WHERE l.archive_id=a.id) AND NOT EXISTS(SELECT 1 FROM replica_stages s WHERE s.archive_id=a.id) AND NOT EXISTS(SELECT 1 FROM " +
                    "node_archive_seals s WHERE s.archive_id=a.id) AND NOT EXISTS(SELECT 1 FROM node_revisions n WHERE n.archive_id=a.id) AND NOT EXISTS(SELECT 1 FROM backup_nodes " +
                    "n WHERE n.archive_id=a.id)"));
                foreach (var row in rows)
                {
                    long id = (long)row[0];
                    string owner = (string)row[1];
                    if (owner.StartsWith("__repack__:", StringComparison.Ordinal))
                        owner = owner.Substring(11);
                    if (tasks != null && !tasks.Contains(owner) || activeArchives != null && activeArchives.Contains(id) || IsCompactionArchive(id))
                        continue;
                    UnregisterUnusedArchiveUnsafe(id, owner);
                }

                return 0;
            });
        }

        public void MarkLastVersionDeleted(string taskId)
        {
            Transaction(delegate
            {
                if (TaskHasNodes(taskId))
                    return 0;
                long source = Scalar("SELECT max(id) FROM archives WHERE task_id=? AND status<>'writing'", taskId);
                RememberVersionDeletionFloor(taskId, source);
                return 0;
            });
            UnregisterVersionlessTaskData(taskId);
        }

        private void RememberVersionDeletionFloor(string taskId, long archiveId)
        {
            database.Execute(
                "INSERT OR IGNORE INTO local_backup_nodes(task_id,id,created_utc,revision,digest,bytes,source_archive,payload) VALUES(?,?,?,1,'',0,?,NULL)",
                taskId, Guid.NewGuid().ToString("N"), DateTime.UtcNow.ToString("o"), archiveId);
            database.Execute("UPDATE local_backup_nodes SET source_archive=? WHERE task_id=? AND source_archive<?", archiveId, taskId, archiveId);
        }

        // Also upgrades local versions explicitly deleted with 0.14.20. Only
        // their recorded range is abandoned; newer, unfinished backups survive.
        public void UnregisterDeletedLocalNodeData(ISet<string> tasks = null)
        {
            List<object[]> rows;
            lock (gate)
                rows = database.Query("SELECT n.task_id,n.source_archive FROM local_backup_nodes n WHERE n.payload IS NULL AND NOT EXISTS(SELECT 1 FROM deferred_version_deletions d WHERE d.task_id=n.task_id)");
            foreach (var row in rows)
                if (tasks == null || tasks.Contains((string)row[0]))
                    UnregisterVersionlessTaskData((string)row[0], (long)row[1]);
        }
    }
}
