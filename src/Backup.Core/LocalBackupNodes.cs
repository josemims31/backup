using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Backup.Core
{
    public sealed partial class BackupCatalog
    {
        // One durable unfinished view per task. Its file database lives locally
        // in the catalog and has no remote metadata archive or upload requirement.
        private void InitializeLocalNodeSchema()
        {
            database.Execute(("CREATE TABLE IF NOT EXISTS local_backup_nodes(task_id TEXT PRIMARY KEY,id TEXT NOT NULL UNIQUE,created_utc TEXT NOT NULL,revision INTEGER NOT NULL,digest TEXT " +
                "NOT NULL,bytes INTEGER NOT NULL,source_archive INTEGER NOT NULL,payload BLOB)"));
        }

        public IList<BackupNode> GetLocalBackupNodes(string taskId = null)
        {
            lock (gate)
                return database.Query(
                    ("SELECT id,task_id,created_utc,revision,digest,bytes FROM local_backup_nodes WHERE payload IS NOT NULL AND (? IS NULL OR " +
                    "task_id=?) ORDER BY created_utc DESC,id DESC"),
                    taskId,
                    taskId
                )
                    .Select(r => new BackupNode {
                    Id = (string)r[0],
                    TaskId = (string)r[1],
                    CreatedUtc = DateTime.Parse((string)r[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                    .ToUniversalTime(),
                    Revision = (long)r[3],
                    Digest = (string)r[4],
                    DatabaseBytes = (long)r[5],
                    LocalOnly = true
                })
                    .ToList();
        }

        public NodeFileDatabase ReadLocalBackupNode(string id)
        {
            lock (gate)
            {
                var rows = database.Query("SELECT payload,digest FROM local_backup_nodes WHERE id=? AND payload IS NOT NULL", id);
                if (rows.Count != 1)
                    throw new IOException("本地未完成备份清单已不存在。");
                return NodeFileDatabase.Read((byte[])rows[0][0], id, (string)rows[0][1]);
            }
        }

        public void SaveLocalBackupNode(string taskId, NodeFileDatabase files, long sourceArchive)
        {
            byte[] bytes = files.Serialize();
            string digest = NodeFileDatabase.Hash(bytes);
            Transaction(delegate
            {
                if (files.Files.Select(file => file.ArchiveId).Distinct().Any(id => new[] { "writing", "failed", "damaged", "cleaned" }.Contains(GetArchive(id).Status)))
                    throw new IOException("未完成的压缩包不能写入备份版本引用。");
                database.Execute(
                    "INSERT OR REPLACE INTO local_backup_nodes(task_id,id,created_utc,revision,digest,bytes,source_archive,payload) VALUES(?,?,?,?,?,?,?,?)",
                    taskId,
                    files.Id,
                    files.CreatedUtc.ToUniversalTime()
                    .ToString("o"),
                    files.Revision,
                    digest,
                    bytes.LongLength,
                    sourceArchive,
                    bytes
                );
                database.ExecuteMany("INSERT OR IGNORE INTO node_archive_seals VALUES(?)", files.Files.Select(f => f.ArchiveId).Distinct().Select(id => new object[] { id }));
                ClearNodeGarbageReview();
                RepositoryStatusChanged(CatalogChange.Archives, taskId);
                return 0;
            });
        }

        public void UpdateLocalBackupNode(string taskId, NodeFileDatabase files)
        {
            lock (gate)
            {
                long source = Scalar("SELECT source_archive FROM local_backup_nodes WHERE task_id=? AND id=? AND payload IS NOT NULL", taskId, files.Id);
                if (source == 0)
                    throw new IOException("本地未完成备份清单已不存在。");
                SaveLocalBackupNode(taskId, files, source);
            }
        }

        public void ReplaceUnfinishedArchive(BackupNode expected, long oldId, long newId)
        {
            Transaction(delegate
            {
                var current = GetLocalBackupNodes(expected.TaskId).SingleOrDefault();
                if (current == null || current.Id != expected.Id || current.Revision != expected.Revision || current.Digest != expected.Digest)
                    throw new IOException("未完成备份版本已变化，原包和新包均保留，等待重新检查。");
                var old = GetArchive(oldId);
                var replacement = GetArchive(newId);
                if (old.TaskId != expected.TaskId || replacement.TaskId != "__repack__:" + expected.TaskId || replacement.Status != "complete" || replacement.TargetCopies < old.TargetCopies ||
                    replacement.CurrentValidCopies < replacement.TargetCopies ||
                    !HasCompleteArchiveManifest(newId) ||
                    HasOutstandingTransfer(oldId) ||
                    HasOutstandingTransfer(newId) ||
                    PendingUploadStages()
                    .Any(s => s.ArchiveId == oldId ||
                    s.ArchiveId == newId) ||
                    Scalar("SELECT count(*) FROM archive_leases WHERE archive_id IN (?,?)", oldId, newId) > 0)
                    throw new IOException("重建分卷尚未全部确认，原包和版本保留。");
                var files = ReadLocalBackupNode(current.Id);
                var selected = files.Files.Where(f => f.ArchiveId == oldId).ToList();
                var written = database.Query("SELECT path,size,modified,entry_path FROM archive_files WHERE archive_id=?", newId).ToDictionary(r => (string)r[0], StringComparer.Ordinal);
                if (selected.Count == 0 || selected.Count != written.Count ||
                    selected.Any(f =>
                {
                    object[] row;
                    return !written.TryGetValue(f.Path, out row) || (long)row[1] != f.Size || (long)row[2] != f.Modified ||
                        (string)row[3] != (!string.IsNullOrEmpty(f.ContentPath) ? f.ContentPath : !string.IsNullOrEmpty(f.EntryPath) ? f.EntryPath : f.Path);
                }))
                    throw new IOException("重建文件与未完成版本清单不一致，原包和版本保留。");
                foreach (var file in selected)
                    file.ArchiveId = newId;
                files.Revision = checked(files.Revision + 1);
                byte[] bytes = files.Serialize();
                string digest = NodeFileDatabase.Hash(bytes);
                database.Execute("UPDATE archives SET task_id=? WHERE id=?", expected.TaskId, newId);
                database.Execute("UPDATE archive_files SET state='current' WHERE archive_id=?", newId);
                database.Execute(
                    "UPDATE local_backup_nodes SET revision=?,digest=?,bytes=?,payload=?,source_archive=MAX(source_archive,?) WHERE task_id=? AND id=?",
                    files.Revision,
                    digest,
                    bytes.LongLength,
                    bytes,
                    newId,
                    expected.TaskId,
                    current.Id
                );
                database.Execute("INSERT OR IGNORE INTO node_archive_seals VALUES(?)", newId);
                UnregisterUnusedArchiveUnsafe(oldId, expected.TaskId);
                ClearNodeGarbageReview();
                RepositoryStatusChanged(CatalogChange.Archives, expected.TaskId);
                return 0;
            });
        }

        public void ForgetLocalBackupNode(string taskId)
        {
            // Retain the small watermark so viewing or restarting cannot recreate
            // a version the user explicitly deleted, or one already published.
            Transaction(delegate
            {
                int before = database.ChangeStamp;
                database.Execute("UPDATE local_backup_nodes SET payload=NULL,bytes=0,digest='' WHERE task_id=? AND payload IS NOT NULL", taskId);
                if (database.ChangeStamp != before)
                {
                    ClearNodeGarbageReview();
                    RepositoryStatusChanged(CatalogChange.Archives, taskId);
                }

                return 0;
            });
        }

        public void RefreshLocalBackupNodes()
        {
            List<string> tasks;
            lock (gate)
                tasks = database.Query("SELECT DISTINCT task_id FROM archives WHERE substr(task_id,1,2)<>'__' AND status NOT IN ('writing','cleaned','snapshot-history')").Select(r => (string)r[0]).ToList();
            foreach (string task in tasks)
                RefreshLocalBackupNode(task);
        }

        public void RefreshLocalBackupNode(string taskId)
        {
            lock (gate)
            {
                PruneIncompleteLocalBackupNode(taskId);
                long published = Scalar("SELECT max(archive_id) FROM backup_nodes WHERE task_id=?", taskId);
                var prior = database.Query("SELECT id,revision,source_archive FROM local_backup_nodes WHERE task_id=?", taskId);
                long floor = Math.Max(published, prior.Count == 0 ? 0 : (long)prior[0][2]);
                long latest = database.Query(
                    ("SELECT a.id FROM archives a WHERE a.task_id=? AND a.id>? AND a.status IN ('complete','redundancy-shortfall','repair-required') AND EXISTS(SELECT 1 FROM " +
                    "archive_files f WHERE f.archive_id=a.id AND f.state='current') ORDER BY a.id DESC"),
                    taskId,
                    floor
                )
                    .Select(row => (long)row[0])
                    .FirstOrDefault(id => HasReadableInspectedManifest(id));
                if (latest == 0)
                    return;
                var created = DateTime.Parse((string)database.Query("SELECT created_utc FROM archives WHERE id=?", latest)[0][0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
                var files = new NodeFileDatabase
                {
                    Id = prior.Count == 0 ? Guid.NewGuid().ToString("N") : (string)prior[0][0],
                    CreatedUtc = created,
                    Revision = prior.Count == 0 ? 1 : checked((long)prior[0][1] + 1)
                };
                var existing = GetLocalBackupNodes(taskId).SingleOrDefault();
                var retained = existing == null ? new HashSet<long>() : new HashSet<long>(ReadLocalBackupNode(existing.Id).Files.Select(file => file.ArchiveId));
                var candidates = database.Query(
                    ("SELECT f.archive_id,f.path,f.size,f.modified,f.source_path,f.entry_path,f.content_path FROM archive_files f JOIN archives a ON a.id=f.archive_id WHERE " +
                    "a.task_id=? AND a.status IN ('complete','redundancy-shortfall','repair-required') AND f.state='current' ORDER BY f.archive_id DESC,f.path"),
                    taskId
                )
                    .Select(r => new NodeFile {
                    ArchiveId = (long)r[0],
                    Path = (string)r[1],
                    Size = (long)r[2],
                    Modified = (long)r[3],
                    SourcePath = (string)r[4],
                    EntryPath = (string)r[5],
                    ContentPath = (string)r[6]
                })
                    .ToList();
                var eligible = new HashSet<long>(candidates.Select(file => file.ArchiveId).Distinct().Where(id => retained.Contains(id) || published > 0 && IsNodeArchive(id) || HasReadableInspectedManifest(id)));
                files.Files.AddRange(candidates.Where(file => eligible.Contains(file.ArchiveId)).GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase));
                if (files.Files.Count > 0)
                    SaveLocalBackupNode(taskId, files, latest);
            }
        }

        private void PruneIncompleteLocalBackupNode(string taskId)
        {
            if (Scalar("SELECT count(*) FROM archives a JOIN node_archive_seals s ON s.archive_id=a.id WHERE a.task_id=? AND a.status IN ('writing','failed','damaged','cleaned')", taskId) == 0)
                return;
            var node = GetLocalBackupNodes(taskId).SingleOrDefault();
            if (node == null)
                return;
            var files = ReadLocalBackupNode(node.Id);
            var invalid = new HashSet<long>(files.Files.Select(file => file.ArchiveId).Distinct().Where(id => new[] { "writing", "failed", "damaged", "cleaned" }.Contains(GetArchive(id).Status)));
            if (invalid.Count == 0)
                return;
            files.Files.RemoveAll(file => invalid.Contains(file.ArchiveId));
            files.Revision = checked(files.Revision + 1);
            byte[] bytes = files.Files.Count == 0 ? null : files.Serialize();
            Transaction(delegate
            {
                if (files.Files.Count == 0)
                    database.Execute("DELETE FROM local_backup_nodes WHERE task_id=? AND payload IS NOT NULL", taskId);
                else
                    database.Execute(
                        "UPDATE local_backup_nodes SET revision=?,digest=?,bytes=?,payload=?,source_archive=? WHERE task_id=? AND id=?",
                        files.Revision,
                        NodeFileDatabase.Hash(bytes),
                        bytes.LongLength,
                        bytes,
                        files.Files.Max(file => file.ArchiveId),
                        taskId,
                        node.Id
                    );
                // With published history, release seals only after its normal
                // reference audit. Removing a bad local row never deletes data.
                if (GetNodes(taskId).Count == 0)
                    database.ExecuteMany("DELETE FROM node_archive_seals WHERE archive_id=?", invalid.Select(id => new object[] { id }));
                ClearNodeGarbageReview();
                RepositoryStatusChanged(CatalogChange.Archives, taskId);
                return 0;
            });
        }

        public ISet<long> UnregisterUnreferencedInterruptedArchives(ISet<long> referenced, ISet<string> taskIds = null)
        {
            return Transaction(delegate
            {
                var removed = new HashSet<long>();
                foreach (var archive in InterruptedArchives(taskIds).Where(a => !referenced.Contains(a.ArchiveId)))
                {
                    database.Execute("DELETE FROM node_archive_seals WHERE archive_id=?", archive.ArchiveId);
                    if (HasReadableInspectedManifest(archive.ArchiveId))
                        continue;
                    if (HasOutstandingTransfer(archive.ArchiveId) || PendingVolumeTransfers().Any(item => item.ArchiveId == archive.ArchiveId) ||
                        PendingUploadStages().Any(item => item.ArchiveId == archive.ArchiveId))
                        continue;
                    UnregisterUnusedArchiveUnsafe(archive.ArchiveId, archive.TaskId);
                    removed.Add(archive.ArchiveId);
                }

                return (ISet<long>)removed;
            });
        }

        public ISet<long> LocalBackupNodeReferences()
        {
            lock (gate)
            {
                var result = new HashSet<long>();
                foreach (var node in GetLocalBackupNodes())
                    foreach (long id in ReadLocalBackupNode(node.Id).Files.Select(f => f.ArchiveId).Distinct())
                    {
                        var archive = GetArchive(id);
                        if (archive.TaskId != node.TaskId || archive.Status == "cleaned")
                            throw new IOException("本地未完成备份清单含有无效的数据包引用，已保留数据并跳过清理。");
                        result.Add(id);
                    }

                return result;
            }
        }
    }
}
