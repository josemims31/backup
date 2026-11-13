using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Backup.Core
{
    public sealed class RetirementRecord
    {
        public string Kind, Id, Mode, State, Error;
    }

    public sealed class RetiredObject
    {
        public long ArchiveId;
        public string DestinationId, GroupId;
        public ObjectRecord Object;
    }

    public sealed class ReplicaStage
    {
        public string ShadowGroupId, TargetGroupId, DestinationId;
        public long ArchiveId;
    }

    internal sealed class CatalogLease : IDisposable
    {
        private Action release;
        internal CatalogLease(Action value)
        {
            release = value;
        }

        public void Dispose()
        {
            var action = release;
            release = null;
            if (action != null)
                action();
        }
    }

    public sealed partial class BackupCatalog
    {
        private void InitializeOperationsSchema()
        {
            InitializeWarehouseState();
            Transaction(delegate
            {
                foreach (string sql in new[]
                {
                    ("CREATE TABLE IF NOT EXISTS resource_retirements(kind TEXT NOT NULL,id TEXT NOT NULL,mode TEXT NOT NULL,state TEXT NOT " +
                        "NULL,error TEXT NOT NULL DEFAULT '',PRIMARY KEY(kind,id))"),
                    "CREATE TABLE IF NOT EXISTS archive_holds(archive_id INTEGER PRIMARY KEY REFERENCES archives(id),reason TEXT NOT NULL)",
                    "CREATE TABLE IF NOT EXISTS archive_leases(archive_id INTEGER NOT NULL REFERENCES archives(id),token TEXT PRIMARY KEY,process_id INTEGER NOT NULL)",
                    "CREATE TABLE IF NOT EXISTS archive_cleanup(archive_id INTEGER PRIMARY KEY REFERENCES archives(id),state TEXT NOT NULL,error TEXT NOT NULL DEFAULT '')",
                    ("CREATE TABLE IF NOT EXISTS replica_stages(shadow_group TEXT PRIMARY KEY REFERENCES groups(id),archive_id INTEGER NOT NULL REFERENCES archives(id),target_group " +
                        "TEXT NOT NULL REFERENCES groups(id),destination_id TEXT NOT NULL REFERENCES destinations(id))"),
                    ("CREATE TABLE IF NOT EXISTS replica_intents(shadow_group TEXT NOT NULL REFERENCES replica_stages(shadow_group),name TEXT " +
                        "NOT NULL REFERENCES objects(name),PRIMARY KEY(shadow_group,name))"),
                    ("CREATE TABLE IF NOT EXISTS retired_objects(name TEXT NOT NULL REFERENCES objects(name),destination_id TEXT NOT NULL REFERENCES destinations(id),archive_id " +
                        "INTEGER NOT NULL REFERENCES archives(id),size INTEGER NOT NULL,PRIMARY KEY(name,destination_id))"),
                    ("CREATE TABLE IF NOT EXISTS retirement_archives(kind TEXT NOT NULL,id TEXT NOT NULL,archive_id INTEGER NOT NULL " +
                        "REFERENCES archives(id),PRIMARY KEY(kind,id,archive_id))"),
                    ("CREATE TABLE IF NOT EXISTS warehouse_detached(name TEXT NOT NULL REFERENCES objects(name),destination_id TEXT NOT NULL REFERENCES destinations(id),group_id " +
                        "TEXT NOT NULL,archive_id INTEGER NOT NULL REFERENCES archives(id),mode TEXT NOT NULL,state TEXT NOT NULL,error TEXT NOT NULL DEFAULT '',PRIMARY " +
                        "KEY(name,destination_id))"),
                    "CREATE INDEX IF NOT EXISTS archive_leases_archive ON archive_leases(archive_id)",
                    "CREATE INDEX IF NOT EXISTS retired_objects_archive ON retired_objects(archive_id)",
                    "CREATE INDEX IF NOT EXISTS warehouse_detached_archive ON warehouse_detached(archive_id)",
                    "CREATE INDEX IF NOT EXISTS warehouse_move_objects_name ON warehouse_move_objects(name)",
                    "SELECT 1"
                }

                )
                    database.Execute(sql);
                if (!database.Query("PRAGMA table_info(archive_leases)").Any(r => (string)r[1] == "process_start"))
                    database.Execute("ALTER TABLE archive_leases ADD COLUMN process_start INTEGER NOT NULL DEFAULT 0");
                if (!database.Query("PRAGMA table_info(archive_leases)").Any(r => (string)r[1] == "read_only"))
                    database.Execute("ALTER TABLE archive_leases ADD COLUMN read_only INTEGER NOT NULL DEFAULT 0");
                if (!database.Query("PRAGMA table_info(retired_objects)").Any(r => (string)r[1] == "group_id"))
                {
                    database.Execute("ALTER TABLE retired_objects ADD COLUMN group_id TEXT NOT NULL DEFAULT ''");
                }

                database.Execute(("UPDATE retired_objects SET group_id=(SELECT min(p.group_id) FROM placements p WHERE p.archive_id=retired_objects.archive_id AND " +
                    "p.destination_id=retired_objects.destination_id AND p.status='retired') WHERE group_id='' AND (SELECT count(DISTINCT p.group_id) FROM placements p WHERE " +
                    "p.archive_id=retired_objects.archive_id AND p.destination_id=retired_objects.destination_id AND p.status='retired')=1"));
                foreach (var row in database.Query("SELECT DISTINCT process_id,process_start FROM archive_leases"))
                {
                    int id = (int)(long)row[0];
                    long start = (long)row[1];
                    bool alive = true;
                    try
                    {
                        using (var process = Process.GetProcessById(id))
                            alive = !process.HasExited && (start == 0 || process.StartTime.ToUniversalTime().Ticks == start);
                    }
                    catch (ArgumentException)
                    {
                        alive = false;
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        alive = true;
                    }

                    if (!alive)
                        database.Execute("DELETE FROM archive_leases WHERE process_id=? AND process_start=?", id, start);
                }

                return 0;
            });
        }

        private void ApplyRetirements(StorageTopology topology)
        {
            foreach (var move in GetWarehouseMoves().Where(m => m.State == "copying"))
            {
                var destination = topology.Destinations.FirstOrDefault(d => d.Id == move.DestinationId);
                if (destination != null)
                    destination.Enabled = false;
            }

            foreach (var row in database.Query("SELECT kind,id FROM resource_retirements"))
            {
                string kind = (string)row[0], id = (string)row[1];
                if (kind == "destination")
                {
                    var d = topology.Destinations.FirstOrDefault(x => x.Id == id);
                    if (d != null)
                    {
                        d.Retired = true;
                        d.Enabled = false;
                    }
                }
                else if (kind == "group")
                {
                    var g = topology.Groups.FirstOrDefault(x => x.Id == id);
                    if (g != null)
                        g.Retired = true;
                }
            }

            foreach (var row in database.Query("SELECT shadow_group FROM replica_stages"))
            {
                var group = topology.Groups.FirstOrDefault(g => g.Id == (string)row[0]);
                if (group != null)
                    group.Retired = true;
            }
        }

        public IDisposable AcquireArchiveLease(long archiveId, bool sharedRead = false)
        {
            string token = Guid.NewGuid().ToString("N");
            Transaction(delegate
            {
                if (Scalar("SELECT count(*) FROM archive_leases WHERE archive_id=? AND (?=0 OR read_only=0)", archiveId, sharedRead) > 0 ||
                    Scalar("SELECT count(*) FROM archive_cleanup WHERE archive_id=? AND state IN ('pending','complete')", archiveId) > 0 ||
                    Scalar("SELECT count(*) FROM archives WHERE id=? AND status='writing'", archiveId) > 0)
                    throw new IOException("归档正在使用或清理，请稍后重试。");
                using (var process = Process.GetCurrentProcess())
                    database.Execute("INSERT INTO archive_leases(archive_id,token,process_id,process_start,read_only) VALUES(?,?,?,?,?)", archiveId, token, process.Id, process.StartTime.ToUniversalTime().Ticks, sharedRead);
                return 0;
            });
            return new CatalogLease(delegate
            {
                lock (gate)
                    database.Execute("DELETE FROM archive_leases WHERE token=?", token);
            });
        }

        public IList<ObjectRecord> GetObjects(long archiveId)
        {
            lock (gate)
                return database.Query("SELECT name,size,sha256 FROM objects WHERE archive_id=? ORDER BY length(name),name", archiveId).Select(r => new ObjectRecord {
                    Name = (string)r[0],
                    Size = (long)r[1],
                    Sha256 = (string)r[2]
                })
                    .ToList();
        }

        public void InvalidateTask(string taskId, bool hold)
        {
            Transaction(delegate
            {
                database.Execute("UPDATE archive_files SET state='deleted' WHERE archive_id IN (SELECT id FROM archives WHERE task_id=?)", taskId);
                if (hold)
                    database.Execute("INSERT OR IGNORE INTO archive_holds(archive_id,reason) SELECT id,'task-deleted-keep' FROM archives WHERE task_id=?", taskId);
                else
                    database.Execute("DELETE FROM archive_holds WHERE reason='interrupted-awaiting-inspection' AND archive_id IN(SELECT id FROM archives WHERE task_id=?)", taskId);
                return 0;
            });
        }

        private const string GarbageEligibility = ("NOT EXISTS(SELECT 1 FROM objects o JOIN upload_temporaries t ON t.name=o.name WHERE o.archive_id=a.id) AND a.status<>'writing' AND NOT EXISTS(SELECT 1 FROM " +
            "archive_files f WHERE f.archive_id=a.id AND f.state='current') AND NOT EXISTS(SELECT 1 FROM archive_holds h WHERE h.archive_id=a.id) AND NOT EXISTS(SELECT 1 " +
            "FROM archive_leases l WHERE l.archive_id=a.id) AND NOT EXISTS(SELECT 1 FROM placements p WHERE p.archive_id=a.id AND (p.reserved>0 OR p.status IN " +
            "('writing','reconcile-required'))) AND NOT EXISTS(SELECT 1 FROM archive_cleanup c WHERE c.archive_id=a.id AND c.state='complete') AND NOT EXISTS(SELECT 1 FROM " +
            "warehouse_move_objects m JOIN objects o ON o.name=m.name JOIN warehouse_moves w ON w.destination_id=m.destination_id WHERE o.archive_id=a.id AND " +
            "w.state<>'complete')");
        public IList<long> GetGarbageCandidates(bool? systemOnly = null, string taskId = null)
        {
            lock (gate)
                return database.Query(
                    "SELECT a.id FROM archives a WHERE " + NodeGarbageScope + " AND (? IS NULL OR (a.task_id='__catalog__')=?) AND (? IS NULL OR a.task_id=?) AND " + GarbageEligibility +
                    " ORDER BY a.id",
                    systemOnly,
                    systemOnly,
                    taskId,
                    taskId
                )
                    .Select(r => (long)r[0])
                    .ToList();
        }

        public IList<long> GetBlockedCleanup(bool? systemOnly = null, string taskId = null)
        {
            lock (gate)
                return database.Query(
                    ("SELECT a.id FROM archives a WHERE (? IS NULL OR (a.task_id='__catalog__')=?) AND (? IS NULL OR a.task_id=?) AND NOT EXISTS(SELECT 1 FROM archive_files f WHERE " +
                    "f.archive_id=a.id AND f.state='current') AND NOT EXISTS(SELECT 1 FROM archive_holds h WHERE h.archive_id=a.id) AND (a.status='writing' OR EXISTS(SELECT 1 FROM " +
                    "archive_leases l WHERE l.archive_id=a.id) OR EXISTS(SELECT 1 FROM placements p WHERE p.archive_id=a.id AND (p.reserved>0 OR p.status IN " +
                    "('writing','reconcile-required'))) OR EXISTS(SELECT 1 FROM warehouse_move_objects m JOIN objects o ON o.name=m.name JOIN warehouse_moves w ON " +
                    "w.destination_id=m.destination_id WHERE o.archive_id=a.id AND w.state<>'complete')) ORDER BY a.id"),
                    systemOnly,
                    systemOnly,
                    taskId,
                    taskId
                )
                    .Select(r => (long)r[0])
                    .ToList();
        }

        public bool BeginCleanup(long archiveId)
        {
            return Transaction(delegate
            {
                if (Scalar("SELECT count(*) FROM archives a WHERE a.id=? AND " + NodeGarbageScope + " AND " + GarbageEligibility, archiveId) == 0)
                    return false;
                database.Execute(
                    ("INSERT OR REPLACE INTO archive_cleanup(archive_id,state,error) VALUES(?,coalesce((SELECT CASE WHEN state IN ('rollback','task-delete') THEN state ELSE " +
                    "'pending' END FROM archive_cleanup WHERE archive_id=?),'pending'),'')"),
                    archiveId,
                    archiveId
                );
                return true;
            });
        }

        public bool TaskHasPreservedArchives(string taskId)
        {
            lock (gate)
                return TaskHasNodes(taskId) || Scalar("SELECT count(*) FROM archives a JOIN archive_holds h ON h.archive_id=a.id WHERE a.task_id=?", taskId) > 0;
        }

        public void CleanupError(long id, string error)
        {
            lock (gate)
                database.Execute("UPDATE archive_cleanup SET error=? WHERE archive_id=?", error, id);
        }

        public void ObjectRemoved(string name, string destinationId)
        {
            Transaction(delegate
            {
                var rows = database.Query("SELECT o.size,o.archive_id,c.group_id FROM object_copies c JOIN objects o ON o.name=c.name WHERE c.name=? AND c.destination_id=? AND c.state<>'deleted'", name, destinationId);
                if (rows.Count > 0)
                {
                    var r = rows[0];
                    database.Execute("UPDATE destinations SET used_bytes=used_bytes-? WHERE id=?", r[0], destinationId);
                    database.Execute("UPDATE placements SET stored=stored-? WHERE archive_id=? AND group_id=? AND destination_id=?", r[0], r[1], r[2], destinationId);
                    database.Execute("UPDATE object_copies SET state='deleted' WHERE name=? AND destination_id=?", name, destinationId);
                }

                var retired = database.Query("SELECT size,archive_id FROM retired_objects WHERE name=? AND destination_id=?", name, destinationId);
                if (retired.Count > 0)
                {
                    database.Execute("UPDATE destinations SET used_bytes=used_bytes-? WHERE id=?", retired[0][0], destinationId);
                    database.Execute("DELETE FROM retired_objects WHERE name=? AND destination_id=?", name, destinationId);
                }

                if (rows.Count > 0)
                    RepositoryObjectCommitted(destinationId, name, (long)rows[0][0], (long)rows[0][1], false);
                else if (retired.Count > 0)
                    RepositoryObjectCommitted(destinationId, name, (long)retired[0][0], (long)retired[0][1], false);
                return 0;
            });
        }

        public void FinishCleanup(long archiveId)
        {
            Transaction(delegate
            {
                if (Scalar("SELECT count(*) FROM object_copies c JOIN objects o ON o.name=c.name WHERE o.archive_id=? AND c.state<>'deleted'", archiveId) != 0 ||
                    Scalar("SELECT count(*) FROM retired_objects WHERE archive_id=?", archiveId) != 0)
                    throw new IOException("仍有待清理的归档对象。");
                database.Execute("UPDATE archive_cleanup SET state='complete',error='' WHERE archive_id=?", archiveId);
                database.Execute("UPDATE placements SET status='cleaned',reserved=0 WHERE archive_id=?", archiveId);
                database.Execute("UPDATE archives SET status='cleaned' WHERE id=?", archiveId);
                return 0;
            });
        }

        public void FinishPartialCleanup(long archiveId)
        {
            lock (gate)
                database.Execute("DELETE FROM archive_cleanup WHERE archive_id=? AND state='pending'", archiveId);
        }

        public IList<RetiredObject> GetRetiredObjects(long? archiveId = null)
        {
            lock (gate)
                return database.Query(
                    ("SELECT r.archive_id,r.destination_id,o.name,o.size,o.sha256,r.group_id FROM retired_objects r JOIN objects o ON " + "o.name=r.name WHERE (? IS NULL OR r.archive_id=?)"),
                    archiveId,
                    archiveId
                )
                    .Select(r => new RetiredObject {
                    ArchiveId = (long)r[0],
                    DestinationId = (string)r[1],
                    GroupId = (string)r[5],
                    Object = new ObjectRecord {
                    Name = (string)r[2],
                    Size = (long)r[3],
                    Sha256 = (string)r[4]
                }
                })
                    .ToList();
        }

        public IList<StoredCopy> GetCleanupManifestCopies(long archiveId)
        {
            lock (gate)
                return database.Query(
                    ("SELECT c.group_id,c.destination_id,o.name,o.size,o.sha256 FROM object_copies c JOIN objects o ON o.name=c.name WHERE o.archive_id=? AND c.state<>'deleted' " +
                    "UNION SELECT '',r.destination_id,o.name,o.size,o.sha256 FROM retired_objects r JOIN objects o ON o.name=r.name WHERE r.archive_id=? UNION SELECT " +
                    "p.group_id,p.destination_id,o.name,o.size,o.sha256 FROM placements p JOIN objects o ON o.archive_id=p.archive_id WHERE p.archive_id=? AND (p.reserved>0 OR " +
                    "p.status IN ('writing','reconcile-required'))"),
                    archiveId,
                    archiveId,
                    archiveId
                )
                    .Select(r => new StoredCopy {
                    GroupId = (string)r[0],
                    DestinationId = (string)r[1],
                    Object = new ObjectRecord {
                    Name = (string)r[2],
                    Size = (long)r[3],
                    Sha256 = (string)r[4]
                }
                })
                    .GroupBy(c => c.DestinationId +
                    "/" +
                    c.Object.Name)
                    .Select(g => g.First())
                    .ToList();
        }

        public void RetireWarehouse(string groupId, string mode, string workspace = null)
        {
            Transaction(delegate
            {
                var topology = LoadTopology();
                var group = topology.Groups.Single(g => g.Id == groupId);
                if (group.Id.StartsWith("transfer:"))
                    throw new ArgumentException("临时迁移仓库不能手工删除。");
                if (GetWarehouseMoves().Any(m => m.State != "complete" && topology.ResolveDestinations(groupId).Contains(m.DestinationId)))
                    throw new IOException("请先完成或取消仓库路径迁移，再移除仓库。");
                bool detach = mode == "delete" || mode == "keep";
                if (detach)
                    DetachWarehouseUnsafe(groupId, mode);
                database.Execute("INSERT OR REPLACE INTO resource_retirements(kind,id,mode,state,error) VALUES('group',?,?,?,'')", groupId, mode, detach ? "complete" : "pending");
                foreach (string destination in topology.ResolveDestinations(groupId))
                    database.Execute(
                        ("INSERT OR IGNORE INTO retirement_archives(kind,id,archive_id) SELECT 'group',?,archive_id FROM placements WHERE " +
                        "destination_id=? AND stored>0 AND (?=1 OR group_id=?)"),
                        groupId,
                        destination,
                        groupId.StartsWith("single:") ? 1 : 0,
                        groupId
                    );
                if (groupId.StartsWith("single:"))
                {
                    string destination = group.DestinationIds.Single();
                    database.Execute("UPDATE destinations SET enabled=0 WHERE id=?", destination);
                    database.Execute("INSERT OR REPLACE INTO resource_retirements(kind,id,mode,state,error) VALUES('destination',?,?,?,'')", destination, mode, detach ? "complete" : "pending");
                    database.Execute("INSERT OR IGNORE INTO retirement_archives(kind,id,archive_id) SELECT 'destination',?,archive_id FROM placements WHERE destination_id=? AND stored>0", destination, destination);
                }

                if (workspace != null)
                    SaveWorkspaceUnsafe(workspace);
                return 0;
            });
        }

        public IList<RetirementRecord> GetRetirements()
        {
            lock (gate)
                return database.Query("SELECT kind,id,mode,state,error FROM resource_retirements").Select(r => new RetirementRecord {
                    Kind = (string)r[0],
                    Id = (string)r[1],
                    Mode = (string)r[2],
                    State = (string)r[3],
                    Error = (string)r[4]
                })
                    .ToList();
        }

        public void RetirementResult(string kind, string id, bool complete, string error)
        {
            Transaction(delegate
            {
                RepositoryStatusChanged(CatalogChange.Topology);
                string state = complete ? "complete" : "pending", message = error ?? "";
                database.Execute("UPDATE resource_retirements SET state=?,error=? WHERE kind=? AND id=? AND (state<>? OR error<>?)", state, message, kind, id, state, message);
                return 0;
            });
        }

        public IList<long> GetRetirementArchives(RetirementRecord item)
        {
            lock (gate)
                return database.Query("SELECT archive_id FROM retirement_archives WHERE kind=? AND id=?", item.Kind, item.Id).Select(r => (long)r[0]).ToList();
        }

        public bool NeedsPreservation(long id)
        {
            lock (gate)
                return Scalar("SELECT count(*) FROM archive_files WHERE archive_id=? AND state='current'", id) > 0 || Scalar("SELECT count(*) FROM archive_holds WHERE archive_id=?", id) > 0;
        }

        public ArchivePlan ReserveReplica(long archiveId, string targetGroup, string destinationId, long budget, long existingCredit = 0)
        {
            return Transaction(delegate
            {
                long reservation = budget - existingCredit;
                if (reservation < 0)
                    throw new ArgumentException("Replica credit exceeds archive size.");
                var usage = GetUsage().Single(u => u.DestinationId == destinationId);
                var destination = LoadTopology().Destinations.Single(d => d.Id == destinationId);
                if (destination.Retired || !destination.Enabled || (decimal)usage.UsedBytes + usage.ReservedBytes + reservation > destination.MaxBytes)
                    throw new IOException("迁移目标容量不足或不再接收新数据。");
                string shadow = "transfer:" + Guid.NewGuid().ToString("N");
                database.Execute("INSERT INTO groups(id,name,notes) VALUES(?,?,'internal-transfer')", shadow, shadow);
                database.Execute("INSERT INTO group_members(destination_id,group_id) VALUES(?,?)", destinationId, shadow);
                database.Execute("INSERT INTO replica_stages(shadow_group,archive_id,target_group,destination_id) VALUES(?,?,?,?)", shadow, archiveId, targetGroup, destinationId);
                database.Execute("INSERT INTO placements(archive_id,group_id,destination_id,reserved,status) VALUES(?,?,?,?,'writing')", archiveId, shadow, destinationId, reservation);
                return new ArchivePlan
                {
                    ArchiveId = archiveId,
                    ArchiveName = GetArchive(archiveId).ArchiveName,
                    BudgetBytes = budget,
                    Placements = new List<ArchivePlacement>
                    {
                        new ArchivePlacement
                        {
                            GroupId = shadow,
                            DestinationId = destinationId,
                            ReservedBytes = reservation,
                            Status = "writing"
                        }
                    }
                };
            });
        }

        public void ReplicaIntent(string shadow, string name)
        {
            lock (gate)
                database.Execute("INSERT OR IGNORE INTO replica_intents(shadow_group,name) VALUES(?,?)", shadow, name);
        }

        public IList<ReplicaStage> GetReplicaStages(long archiveId)
        {
            lock (gate)
                return database.Query("SELECT shadow_group,target_group,destination_id FROM replica_stages WHERE archive_id=?", archiveId).Select(r => new ReplicaStage {
                    ArchiveId = archiveId,
                    ShadowGroupId = (string)r[0],
                    TargetGroupId = (string)r[1],
                    DestinationId = (string)r[2]
                })
                    .ToList();
        }

        public IList<ObjectRecord> GetReplicaIntents(string shadow)
        {
            lock (gate)
                return database.Query("SELECT o.name,o.size,o.sha256 FROM replica_intents i JOIN objects o ON o.name=i.name WHERE i.shadow_group=?", shadow).Select(r => new ObjectRecord {
                    Name = (string)r[0],
                    Size = (long)r[1],
                    Sha256 = (string)r[2]
                })
                    .ToList();
        }

        private void DiscardReplicaStageUnsafe(string shadow)
        {
            database.Execute("DELETE FROM replica_intents WHERE shadow_group=?", shadow);
            database.Execute("DELETE FROM object_copies WHERE group_id=?", shadow);
            database.Execute("DELETE FROM placements WHERE group_id=?", shadow);
            database.Execute("DELETE FROM replica_stages WHERE shadow_group=?", shadow);
            database.Execute("DELETE FROM group_members WHERE group_id=?", shadow);
            database.Execute("DELETE FROM group_modes WHERE group_id=?", shadow);
            database.Execute("DELETE FROM groups WHERE id=?", shadow);
        }

        public void DiscardReplicaStage(string shadow)
        {
            Transaction(delegate
            {
                DiscardReplicaStageUnsafe(shadow);
                return 0;
            });
        }

        public void PublishReplicaPlan(long archiveId, ArchivePlan desired)
        {
            Transaction(delegate
            {
                if (HasOutstandingTransfer(archiveId))
                    throw new IOException("原备份仍有未完成的传输，暂不迁移。");
                var objects = GetObjects(archiveId);
                var copies = GetCopies(archiveId);
                var present = new HashSet<Tuple<string, string>>(copies.Select(copy => Tuple.Create(copy.DestinationId, copy.Object.Name)));
                foreach (var placement in desired.Placements)
                    foreach (var item in objects)
                        if (!present.Contains(Tuple.Create(placement.DestinationId, item.Name)))
                            throw new IOException("新副本不完整，禁止移除旧副本。");
                var keep = new HashSet<string>(desired.Placements.Select(p => p.DestinationId));
                foreach (var copy in copies.Where(c => !keep.Contains(c.DestinationId)))
                    database.Execute("INSERT OR IGNORE INTO retired_objects(name,destination_id,archive_id,size,group_id) VALUES(?,?,?,?,?)", copy.Object.Name, copy.DestinationId, archiveId, copy.Object.Size, copy.GroupId);
                database.Execute("DELETE FROM object_copies WHERE name IN(SELECT name FROM objects WHERE archive_id=?)", archiveId);
                database.Execute("UPDATE placements SET status='retired',stored=0,reserved=0 WHERE archive_id=?", archiveId);
                long total = objects.Sum(o => o.Size);
                foreach (var placement in desired.Placements)
                {
                    database.Execute(
                        "INSERT OR REPLACE INTO placements(archive_id,group_id,destination_id,reserved,stored,status,error) VALUES(?,?,?,0,?,'complete','')",
                        archiveId,
                        placement.GroupId,
                        placement.DestinationId,
                        total
                    );
                    foreach (var item in objects)
                        database.Execute("INSERT INTO object_copies(name,destination_id,group_id,state) VALUES(?,?,?,'present')", item.Name, placement.DestinationId, placement.GroupId);
                }

                foreach (var stage in GetReplicaStages(archiveId))
                    DiscardReplicaStageUnsafe(stage.ShadowGroupId);
                database.Execute("UPDATE archives SET target_copies=?,status='complete' WHERE id=?", desired.TargetCopies, archiveId);
                return 0;
            });
        }

        public ArchivePlan ResumeReplica(ReplicaStage stage)
        {
            lock (gate)
            {
                var row = database.Query("SELECT reserved,stored FROM placements WHERE archive_id=? AND group_id=?", stage.ArchiveId, stage.ShadowGroupId).Single();
                return new ArchivePlan
                {
                    ArchiveId = stage.ArchiveId,
                    ArchiveName = GetArchive(stage.ArchiveId).ArchiveName,
                    Placements = new List<ArchivePlacement>
                    {
                        new ArchivePlacement
                        {
                            GroupId = stage.ShadowGroupId,
                            DestinationId = stage.DestinationId,
                            ReservedBytes = (long)row[0],
                            StoredBytes = (long)row[1],
                            Status = "writing"
                        }
                    }
                };
            }
        }
    }
}
