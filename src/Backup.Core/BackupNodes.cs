using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Backup.Core
{
    public sealed class BackupNode
    {
        public string Id, TaskId, Digest;
        public string EntryPath = "node.db";
        public DateTime CreatedUtc;
        public long ArchiveId, Revision, DatabaseBytes;
        public bool LocalOnly;
        public string DisplayTime
        {
            get
            {
                return (LocalOnly ? "未完成 · " : "") + CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            }
        }

        public override string ToString()
        {
            return DisplayTime + " · " + (LocalOnly ? "仅本地 · " : "") + Id.Substring(0, 6);
        }
    }

    public sealed class NodeFile
    {
        public long ArchiveId, Size, Modified;
        public string Path, SourcePath, EntryPath, ContentPath;
    }

    // A node contains only its identity and the effective file list. No task,
    // schedule, connection, placement or password tables are copied into it.
    public sealed class NodeFileDatabase
    {
        public string Id;
        public DateTime CreatedUtc;
        public long Revision;
        public readonly List<NodeFile> Files = new List<NodeFile>();
        public byte[] Serialize()
        {
            return NodeDatabaseCodec.Serialize(this);
        }

        public static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        public static NodeFileDatabase Read(byte[] bytes, string expectedId = null, string digest = null)
        {
            return NodeDatabaseCodec.Read(bytes, expectedId, digest);
        }

        public static NodeFileDatabase ReadFile(string path, MemoryBudget budget, string expectedId = null, string digest = null)
        {
            return NodeDatabaseCodec.ReadFile(path, budget, expectedId, digest);
        }
    }

    public static class SmartRecycle
    {
        // Rolling age boundaries; buckets follow local civil time. Weeks start
        // on Monday. UTC ordering also makes DST repeated hours deterministic.
        public static ISet<string> Retained(IEnumerable<BackupNode> nodes, DateTime nowUtc, int maximumYears, bool keepLatest = true, TimeZoneInfo zone = null)
        {
            if (maximumYears < 1 || maximumYears > 100)
                throw new ArgumentOutOfRangeException("maximumYears");
            return Retained(nodes, nowUtc, Cutoff(nowUtc, maximumYears, "year"), keepLatest, zone);
        }

        public static DateTime Cutoff(DateTime nowUtc, int value, string unit)
        {
            if (value < 1)
                throw new ArgumentOutOfRangeException("value");
            if (unit != "day" && unit != "month" && unit != "year")
                throw new ArgumentException("保存时间单位无效。");
            nowUtc = nowUtc.ToUniversalTime();
            try
            {
                return unit == "day" ? nowUtc.AddDays(-(double)value) : unit == "month" ? nowUtc.AddMonths(-value) : nowUtc.AddYears(-value);
            }
            catch (ArgumentOutOfRangeException)
            {
                return DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
            }
        }

        public static ISet<string> Retained(IEnumerable<BackupNode> nodes, DateTime nowUtc, DateTime? oldestAllowed, bool keepLatest = true, TimeZoneInfo zone = null)
        {
            nowUtc = nowUtc.ToUniversalTime();
            zone = zone ?? TimeZoneInfo.Local;
            DateTime hourly = nowUtc.AddHours(-24), daily = nowUtc.AddMonths(-1);
            var ordered = nodes.OrderBy(n => n.CreatedUtc).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
            var live = ordered.Where(n => !oldestAllowed.HasValue || n.CreatedUtc >= oldestAllowed.Value).ToList();
            var keep = new HashSet<string>();
            var buckets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in live)
            {
                DateTime local = TimeZoneInfo.ConvertTimeFromUtc(node.CreatedUtc.ToUniversalTime(), zone);
                string bucket;
                if (node.CreatedUtc >= hourly)
                    bucket = "H" + local.ToString("yyyyMMddHH", CultureInfo.InvariantCulture);
                else if (node.CreatedUtc >= daily)
                    bucket = "D" + local.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                else
                {
                    DateTime monday = local.Date.AddDays(-(((int)local.DayOfWeek + 6) % 7));
                    bucket = "W" + monday.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                }

                if (buckets.Add(bucket))
                    keep.Add(node.Id);
            }

            // The newest published version survives every automatic retention rule,
            // even when all versions exceed the saved-time limit. Unfinished local
            // views cannot replace this protection for the latest successful version.
            if (keepLatest)
            {
                var latest = ordered.LastOrDefault(n => !n.LocalOnly);
                if (latest != null)
                    keep.Add(latest.Id);
            }

            return keep;
        }
    }

    public sealed partial class BackupCatalog
    {
        private string nodeDataDirectory;
        public string NodeDirectory(string taskId)
        {
            if (nodeDataDirectory == null)
                throw new IOException("本地备份版本目录未设置。");
            string tag = EnsureTaskTag(taskId);
            if (!System.Text.RegularExpressions.Regex.IsMatch(tag, @"\A[A-Za-z0-9]{4}\z"))
                throw new IOException("任务代号无效，本地备份版本目录未创建。");
            return Path.Combine(nodeDataDirectory, "node-" + tag);
        }

        private void InitializeNodeFiles(string path)
        {
            nodeDataDirectory = path == null ? null : Path.GetDirectoryName(Path.GetFullPath(path));
            string files = path == null ? ":memory:" : Path.Combine(CacheDirectory, "working-files.db");
            if (path != null && File.Exists(files) && (File.GetAttributes(files) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("文件清单缓存不能是链接。");
            database.Execute("ATTACH DATABASE ? AS file_work", files);
            database.Execute("PRAGMA file_work.journal_mode=DELETE");
            database.Execute("PRAGMA file_work.synchronous=FULL");
            database.Execute(("CREATE TABLE IF NOT EXISTS file_work.archive_files(archive_id INTEGER NOT NULL,path TEXT NOT NULL,size INTEGER NOT NULL,modified INTEGER NOT NULL,state TEXT " +
                "NOT NULL DEFAULT 'current',source_path TEXT NOT NULL DEFAULT '',entry_path TEXT NOT NULL DEFAULT '',PRIMARY KEY(archive_id,path))"));
            if (!database.Query("PRAGMA file_work.table_info(archive_files)").Any(r => (string)r[1] == "content_path"))
                database.Execute("ALTER TABLE file_work.archive_files ADD COLUMN content_path TEXT NOT NULL DEFAULT ''");
            database.Execute("CREATE INDEX IF NOT EXISTS file_work.archive_files_path ON archive_files(path,archive_id)");
            database.Execute("CREATE INDEX IF NOT EXISTS file_work.archive_files_state ON archive_files(archive_id,state)");
            database.Execute("CREATE TABLE IF NOT EXISTS file_work.node_basis(task_id TEXT PRIMARY KEY,basis TEXT NOT NULL)");
            database.Execute("CREATE TEMP TABLE node_gc_allowed(archive_id INTEGER PRIMARY KEY)");
            database.Execute("CREATE TEMP TABLE node_metadata_delete(archive_id INTEGER PRIMARY KEY)");
        }

        private void InitializeNodeSchema()
        {
            Transaction(delegate
            {
                InitializeLocalNodeSchema();
                database.Execute(("CREATE TABLE IF NOT EXISTS backup_nodes(id TEXT PRIMARY KEY,task_id TEXT NOT NULL,created_utc TEXT NOT NULL,archive_id INTEGER NOT NULL REFERENCES " +
                    "archives(id),revision INTEGER NOT NULL,digest TEXT NOT NULL,bytes INTEGER NOT NULL)"));
                if (!database.Query("PRAGMA table_info(backup_nodes)").Any(r => (string)r[1] == "entry_path"))
                    database.Execute("ALTER TABLE backup_nodes ADD COLUMN entry_path TEXT NOT NULL DEFAULT 'node.db'");
                database.Execute("CREATE INDEX IF NOT EXISTS nodes_task ON backup_nodes(task_id,created_utc,id)");
                database.Execute(("CREATE TABLE IF NOT EXISTS node_revisions(node_id TEXT NOT NULL,archive_id INTEGER NOT NULL REFERENCES archives(id),revision INTEGER NOT NULL,digest TEXT NOT " +
                    "NULL,bytes INTEGER NOT NULL,task_id TEXT NOT NULL,entry_path TEXT NOT NULL DEFAULT 'node.db',PRIMARY KEY(node_id,archive_id))"));
                if (!database.Query("PRAGMA table_info(node_revisions)").Any(r => (string)r[1] == "entry_path"))
                {
                    database.Execute(("CREATE TABLE node_revisions_next(node_id TEXT NOT NULL,archive_id INTEGER NOT NULL REFERENCES archives(id),revision INTEGER NOT NULL,digest TEXT NOT NULL,bytes " +
                        "INTEGER NOT NULL,task_id TEXT NOT NULL,entry_path TEXT NOT NULL DEFAULT 'node.db',PRIMARY KEY(node_id,archive_id))"));
                    database.Execute("INSERT INTO node_revisions_next(node_id,archive_id,revision,digest,bytes,task_id) SELECT node_id,archive_id,revision,digest,bytes,task_id FROM node_revisions");
                    database.Execute("DROP TABLE node_revisions");
                    database.Execute("ALTER TABLE node_revisions_next RENAME TO node_revisions");
                }

                database.Execute("CREATE INDEX IF NOT EXISTS node_revisions_archive ON node_revisions(archive_id)");
                // A constant-size safety seal per physical archive, not a per-node
                // reference index. Published bytes must never enter incomplete cleanup.
                database.Execute("CREATE TABLE IF NOT EXISTS node_archive_seals(archive_id INTEGER PRIMARY KEY REFERENCES archives(id))");
                foreach (string action in new[]
                {
                    "INSERT",
                    "UPDATE",
                    "DELETE"
                }

                )
                    database.Execute("CREATE TRIGGER IF NOT EXISTS recovery_nodes_" + action + " AFTER " + action + " ON backup_nodes BEGIN UPDATE recovery_revision SET value=value+1 WHERE id=1; END");
                foreach (string action in new[]
                {
                    "INSERT",
                    "UPDATE",
                    "DELETE"
                }

                )
                    database.Execute("CREATE TEMP TRIGGER file_revision_" + action + " AFTER " + action + " ON file_work.archive_files BEGIN UPDATE recovery_revision SET value=value+1 WHERE id=1; END");
                return 0;
            });
        }

        public IList<BackupNode> GetNodes(string taskId = null)
        {
            lock (gate)
                return database.Query("SELECT id,task_id,created_utc,archive_id,revision,digest,bytes,entry_path FROM backup_nodes WHERE (? IS NULL OR task_id=?) ORDER BY created_utc DESC,id DESC", taskId, taskId)
                    .Select(r => new BackupNode {
                    Id = (string)r[0],
                    TaskId = (string)r[1],
                    CreatedUtc = DateTime.Parse((string)r[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                    .ToUniversalTime(),
                    ArchiveId = (long)r[3],
                    Revision = (long)r[4],
                    Digest = (string)r[5],
                    DatabaseBytes = (long)r[6],
                    EntryPath = (string)r[7]
                })
                    .ToList();
        }

        public BackupNode FindNode(string id)
        {
            lock (gate)
                return GetNodes().Single(n => n.Id == id);
        }

        public IList<BackupNode> NodeRevisions()
        {
            lock (gate)
                return database.Query(("SELECT r.node_id,r.task_id,a.created_utc,r.archive_id,r.revision,r.digest,r.bytes,r.entry_path FROM node_revisions r " +
                    "JOIN archives a ON a.id=r.archive_id WHERE a.status<>'cleaned'"))
                    .Select(r => new BackupNode {
                    Id = (string)r[0],
                    TaskId = (string)r[1],
                    CreatedUtc = DateTime.Parse((string)r[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                    .ToUniversalTime(),
                    ArchiveId = (long)r[3],
                    Revision = (long)r[4],
                    Digest = (string)r[5],
                    DatabaseBytes = (long)r[6],
                    EntryPath = (string)r[7]
                })
                    .ToList();
        }

        public void RegisterNodeRevision(BackupNode node)
        {
            lock (gate)
                database.Execute(
                    "INSERT OR REPLACE INTO node_revisions(node_id,archive_id,revision,digest,bytes,task_id,entry_path) VALUES(?,?,?,?,?,?,?)",
                    node.Id,
                    node.ArchiveId,
                    node.Revision,
                    node.Digest,
                    node.DatabaseBytes,
                    node.TaskId,
                    node.EntryPath
                );
        }

        public bool NodeMetadataShared(long archiveId, string nodeId)
        {
            lock (gate)
                return Scalar("SELECT count(*) FROM node_revisions WHERE archive_id=? AND node_id<>?", archiveId, nodeId) > 0;
        }

        public void ForgetRetiredNodeRevision(string nodeId, long archiveId)
        {
            Transaction(delegate
            {
                if (Scalar("SELECT count(*) FROM backup_nodes WHERE id=? AND archive_id=?", nodeId, archiveId) == 0)
                {
                    database.Execute("DELETE FROM node_revisions WHERE node_id=? AND archive_id=?", nodeId, archiveId);
                    ForgetNodeArchive(archiveId);
                    RepositoryStatusChanged(CatalogChange.Archives);
                }

                return 0;
            });
        }

        // Metadata deletion is logical even when a remote delete fails. Leftover
        // bytes have no ownership and are handled as unregistered managed files.
        private void ForgetNodeArchive(long id)
        {
            if (Scalar("SELECT count(*) FROM node_revisions WHERE archive_id=?", id) > 0 || Scalar("SELECT count(*) FROM backup_nodes WHERE archive_id=?", id) > 0)
                return;
            var archive = GetArchive(id);
            if (!archive.TaskId.StartsWith("__node__:", StringComparison.Ordinal))
                throw new InvalidOperationException();
            if (archive.Status == "writing" || Scalar("SELECT count(*) FROM archive_leases WHERE archive_id=?", id) > 0 || GetReplicaStages(id).Count > 0 || HasOutstandingTransfer(id) ||
                Scalar(
                ("SELECT count(*) FROM warehouse_move_objects o JOIN objects f ON f.name=o.name JOIN warehouse_moves m ON " +
                "m.destination_id=o.destination_id WHERE f.archive_id=? AND m.state<>'complete'"),
                id
            ) > 0)
                throw new IOException("备份版本数据库正在使用或传输，暂不能移除登记。");
            database.Execute(
                ("UPDATE destinations SET used_bytes=used_bytes-coalesce((SELECT sum(p.stored) FROM placements p WHERE p.archive_id=? AND " +
                "p.destination_id=destinations.id),0)-coalesce((SELECT sum(r.size) FROM retired_objects r WHERE r.archive_id=? AND r.destination_id=destinations.id),0)"),
                id,
                id
            );
            foreach (string table in new[]
            {
                "replica_intents",
                "volume_transfers",
                "upload_temporaries",
                "warehouse_move_objects",
                "object_copies"
            }

            )
                database.Execute("DELETE FROM " + table + " WHERE name IN(SELECT name FROM objects WHERE archive_id=?)", id);
            foreach (string table in new[]
            {
                "node_revisions",
                "node_archive_seals",
                "retirement_archives",
                "archive_pool_members",
                "archive_pools",
                "retired_objects",
                "warehouse_detached",
                "catalog_coverage",
                "archive_holds",
                "archive_cleanup",
                "placements",
                "archive_files"
            }

            )
                database.Execute("DELETE FROM " + table + " WHERE archive_id=?", id);
            database.Execute("DELETE FROM objects WHERE archive_id=?", id);
            database.Execute("DELETE FROM archives WHERE id=?", id);
        }

        public void PublishNode(BackupNode node, IEnumerable<long> fileArchives = null)
        {
            Transaction(delegate
            {
                if (GetArchive(node.ArchiveId).Status != "complete")
                    throw new IOException("备份版本数据库尚未完整上传，保留上一备份版本。");
                if (fileArchives != null)
                    database.ExecuteMany("INSERT OR IGNORE INTO node_archive_seals VALUES(?)", fileArchives.Distinct().Select(id => new object[] { id }));
                database.Execute(
                    "INSERT OR REPLACE INTO backup_nodes(id,task_id,created_utc,archive_id,revision,digest,bytes,entry_path) VALUES(?,?,?,?,?,?,?,?)",
                    node.Id,
                    node.TaskId,
                    node.CreatedUtc.ToUniversalTime()
                    .ToString("o"),
                    node.ArchiveId,
                    node.Revision,
                    node.Digest,
                    node.DatabaseBytes,
                    node.EntryPath
                );
                RegisterNodeRevision(node);
                RepositoryStatusChanged(CatalogChange.Archives, node.TaskId);
                return 0;
            });
        }

        public void ForgetNode(string id)
        {
            Transaction(delegate
            {
                var node = FindNode(id);
                var archives = database.Query("SELECT archive_id FROM node_revisions WHERE node_id=?", id).Select(r => (long)r[0]).Concat(new[] { node.ArchiveId }).Distinct().ToList();
                database.Execute("DELETE FROM backup_nodes WHERE id=?", id);
                database.Execute("DELETE FROM node_revisions WHERE node_id=?", id);
                foreach (long archive in archives)
                    ForgetNodeArchive(archive);
                RepositoryStatusChanged(CatalogChange.Archives, node.TaskId);
                return 0;
            });
        }

        public NodeFileDatabase CaptureNode(string taskId, string id, DateTime createdUtc)
        {
            lock (gate)
            {
                var result = new NodeFileDatabase
                {
                    Id = id,
                    CreatedUtc = createdUtc,
                    Revision = 1
                };
                result.Files.AddRange(database.Query(
                    ("WITH latest AS(SELECT f.path,max(f.archive_id) AS id FROM archive_files f JOIN archives a ON a.id=f.archive_id WHERE a.task_id=? AND f.state='current' AND " +
                    "a.status IN ('complete','redundancy-shortfall','repair-required') GROUP BY f.path) SELECT " +
                    "f.archive_id,f.path,f.size,f.modified,f.source_path,f.entry_path,f.content_path FROM archive_files f JOIN latest l ON l.id=f.archive_id AND l.path=f.path WHERE " +
                    "f.state='current' ORDER BY f.path"),
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
                    .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(f => f.ArchiveId)
                    .First()));
                return result;
            }
        }

        public string WorkingNodeBasis(string taskId)
        {
            lock (gate)
            {
                var r = database.Query("SELECT basis FROM file_work.node_basis WHERE task_id=?", taskId);
                return r.Count == 0 ? null : (string)r[0][0];
            }
        }

        public void SetWorkingNodeBasis(string taskId, string basis)
        {
            lock (gate)
                database.Execute("INSERT OR REPLACE INTO file_work.node_basis VALUES(?,?)", taskId, basis);
        }

        public void LoadWorkingNode(string taskId, NodeFileDatabase node)
        {
            Transaction(delegate
            {
                database.Execute("DELETE FROM archive_files WHERE archive_id IN(SELECT id FROM archives WHERE task_id=? AND status<>'writing')", taskId);
                if (node != null)
                    database.ExecuteMany(
                        "INSERT OR REPLACE INTO archive_files(archive_id,path,size,modified,source_path,entry_path,content_path) VALUES(?,?,?,?,?,?,?)",
                        node.Files.Select(f => new object[] {
                        f.ArchiveId,
                        f.Path,
                        f.Size,
                        f.Modified,
                        f.SourcePath,
                        f.EntryPath,
                        f.ContentPath ??
                        ""
                    })
                    );
                SetWorkingNodeBasis(taskId, node == null ? "" : node.Id + ":" + node.Revision);
                RepositoryStatusChanged(CatalogChange.Archives, taskId);
                return 0;
            });
        }

        public IList<FileHistoryRow> ReadNodeHistory(BackupNode node, NodeFileDatabase files)
        {
            lock (gate)
            {
                var rows = new List<FileHistoryRow>();
                foreach (var batch in files.Files.GroupBy(f => f.ArchiveId))
                {
                    var a = GetArchive(batch.Key);
                    if (a.TaskId != node.TaskId)
                        throw new IOException("备份版本引用了其他任务的数据包。");
                    var places = database.Query("SELECT group_id,destination_id,status FROM placements WHERE archive_id=? AND (stored>0 OR ?=1)", batch.Key, node.LocalOnly);
                    string status = a.Status;
                    if (node.LocalOnly && status == "failed")
                        status = HasCompleteArchiveManifest(batch.Key) && HasOutstandingTransfer(batch.Key) ? "reconcile-required" : "interrupted";
                    // Keep an unavailable entry visible even if no copy is left.
                    if (places.Count == 0)
                        places.Add(new object[] { "", "", "unavailable" });
                    foreach (var file in batch)
                        foreach (var p in places)
                            rows.Add(new FileHistoryRow {
                                ArchiveId = file.ArchiveId,
                                TaskId = node.TaskId,
                                Path = file.Path,
                                Size = file.Size,
                                Modified = file.Modified,
                                SourcePath = file.SourcePath,
                                EntryPath = file.EntryPath,
                                ContentPath = file.ContentPath,
                                ArchiveName = a.ArchiveName,
                                FileState = "current",
                                ArchiveStatus = status,
                                GroupId = (string)p[0],
                                DestinationId = (string)p[1],
                                PlacementStatus = (string)p[2]
                            });
                }

                return rows;
            }
        }

        // Data garbage is eligible only during a complete node-reference audit.
        // This temporary allow-list is empty after restart or any failed audit.
        private const string NodeGarbageScope = ("(a.task_id='__catalog__' OR (substr(a.task_id,1,9)='__node__:' AND NOT EXISTS(SELECT 1 FROM node_revisions r WHERE r.archive_id=a.id)) OR EXISTS(SELECT 1 FROM " +
            "archive_cleanup c WHERE c.archive_id=a.id AND c.state='rollback') OR EXISTS(SELECT 1 FROM temp.node_gc_allowed n WHERE n.archive_id=a.id) OR EXISTS(SELECT 1 " +
            "FROM temp.node_metadata_delete n WHERE n.archive_id=a.id))");
        public void ClearNodeGarbageReview()
        {
            lock (gate)
                database.Execute("DELETE FROM temp.node_gc_allowed");
        }

        public void AllowNodeGarbage(ISet<long> referenced, ISet<long> archiveIds = null)
        {
            Transaction(delegate
            {
                ClearNodeGarbageReview();
                foreach (var a in ListArchives(true).Where(a => a.TaskId != "__catalog__" && !a.TaskId.StartsWith("__node__:", StringComparison.Ordinal) && a.Status != "writing" &&
                    !referenced.Contains(a.ArchiveId) &&
                    (archiveIds == null ||
                    archiveIds.Contains(a.ArchiveId))))
                {
                    database.Execute("INSERT INTO temp.node_gc_allowed VALUES(?)", a.ArchiveId);
                    database.Execute("UPDATE archive_files SET state='expired' WHERE archive_id=?", a.ArchiveId);
                    database.Execute("DELETE FROM archive_holds WHERE archive_id=? AND (reason LIKE 'version-retire:%' OR reason='task-deleted-keep')", a.ArchiveId);
                }

                return 0;
            });
        }

        public void PrepareNodeMetadataDeletion(long archiveId)
        {
            lock (gate)
            {
                if (!GetArchive(archiveId).TaskId.StartsWith("__node__:", StringComparison.Ordinal))
                    throw new InvalidOperationException();
                database.Execute("INSERT OR IGNORE INTO temp.node_metadata_delete VALUES(?)", archiveId);
                database.Execute("UPDATE archive_files SET state='expired' WHERE archive_id=?", archiveId);
            }
        }

        public void EndNodeMetadataDeletion(long archiveId)
        {
            lock (gate)
                database.Execute("DELETE FROM temp.node_metadata_delete WHERE archive_id=?", archiveId);
        }

        public bool TaskHasNodes(string taskId)
        {
            lock (gate)
                return Scalar("SELECT count(*) FROM backup_nodes WHERE task_id=?", taskId) > 0 ||
                    Scalar("SELECT count(*) FROM local_backup_nodes WHERE task_id=? AND payload IS NOT NULL", taskId) > 0;
        }

        public bool IsNodeArchive(long id)
        {
            lock (gate)
                return Scalar("SELECT count(*) FROM node_archive_seals WHERE archive_id=?", id) > 0 || Scalar("SELECT count(*) FROM node_revisions WHERE archive_id=?", id) > 0;
        }
    }
}
