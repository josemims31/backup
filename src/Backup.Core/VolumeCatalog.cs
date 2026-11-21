using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Backup.Core
{
    public sealed partial class BackupCatalog
    {
        private void InitializeVolumeSchema()
        {
            Transaction(delegate
            {
                if (!database.Query("PRAGMA table_info(object_copies)").Any(row => (string)row[1] == "pool_idx"))
                    database.Execute("ALTER TABLE object_copies ADD COLUMN pool_idx INTEGER NOT NULL DEFAULT -1");
                foreach (string sql in new[]
                {
                    ("CREATE TABLE IF NOT EXISTS archive_pools(archive_id INTEGER NOT NULL REFERENCES archives(id),pool_idx INTEGER NOT " +
                        "NULL,group_id TEXT NOT NULL REFERENCES groups(id),PRIMARY KEY(archive_id,pool_idx))"),
                    ("CREATE TABLE IF NOT EXISTS archive_pool_members(archive_id INTEGER NOT NULL,pool_idx INTEGER NOT NULL,destination_id TEXT NOT NULL REFERENCES " +
                        "destinations(id),PRIMARY KEY(archive_id,pool_idx,destination_id),FOREIGN KEY(archive_id,pool_idx) REFERENCES archive_pools(archive_id,pool_idx))"),
                    ("CREATE TABLE IF NOT EXISTS volume_transfers(name TEXT NOT NULL REFERENCES objects(name),destination_id TEXT NOT NULL REFERENCES destinations(id),pool_idx " +
                        "INTEGER NOT NULL,group_id TEXT NOT NULL REFERENCES groups(id),state TEXT NOT NULL,PRIMARY KEY(name,destination_id))"),
                    "CREATE INDEX IF NOT EXISTS object_copies_pool ON object_copies(name,pool_idx,state)"
                }

                )
                    database.Execute(sql);
                if ((long)database.Query("PRAGMA user_version")[0][0] < 5)
                    database.Execute("PRAGMA user_version=5");
                return 0;
            });
        }

        public bool IsVolumeArchive(long id)
        {
            lock (gate)
                return Scalar("SELECT count(*) FROM archive_pools WHERE archive_id=?", id) > 0;
        }

        public List<ReplicaPool> GetReplicaPools(long id)
        {
            lock (gate)
                return database.Query("SELECT pool_idx,group_id FROM archive_pools WHERE archive_id=? ORDER BY pool_idx", id).Select(row => new ReplicaPool {
                    Index = (int)(long)row[0],
                    GroupId = (string)row[1],
                    DestinationIds = database.Query("SELECT destination_id FROM archive_pool_members WHERE archive_id=? AND pool_idx=? ORDER BY destination_id", id, row[0])
                    .Select(member => (string)member[0])
                    .ToList()
                })
                    .ToList();
        }

        public IDictionary<long, ISet<string>> RecordedArchivePolicyRoots(string taskId = null)
        {
            lock (gate)
                return database.Query(
                    "WITH roots AS(SELECT archive_id,group_id FROM archive_pools UNION SELECT p.archive_id,p.group_id FROM placements p " +
                    "WHERE p.group_id NOT LIKE 'transfer:%' AND NOT EXISTS(SELECT 1 FROM archive_pools a WHERE a.archive_id=p.archive_id)) " +
                    "SELECT r.archive_id,r.group_id FROM roots r JOIN archives a ON a.id=r.archive_id WHERE ? IS NULL OR a.task_id=? " +
                    "OR EXISTS(SELECT 1 FROM backup_nodes n WHERE n.archive_id=r.archive_id AND n.task_id=?)",
                    taskId, taskId, taskId
                ).GroupBy(row => (long)row[0]).ToDictionary(
                    group => group.Key,
                    group => (ISet<string>)new HashSet<string>(group.Select(row => (string)row[1]), StringComparer.Ordinal)
                );
        }

        private void SavePools(long id, IList<ReplicaPool> pools)
        {
            database.Execute("DELETE FROM archive_pool_members WHERE archive_id=?", id);
            database.Execute("DELETE FROM archive_pools WHERE archive_id=?", id);
            foreach (var pool in pools)
            {
                database.Execute("INSERT INTO archive_pools(archive_id,pool_idx,group_id) VALUES(?,?,?)", id, pool.Index, pool.GroupId);
                foreach (string destination in pool.DestinationIds)
                    database.Execute("INSERT INTO archive_pool_members(archive_id,pool_idx,destination_id) VALUES(?,?,?)", id, pool.Index, destination);
            }
        }

        public ArchivePlan ReserveVolumeArchive(
            long budget,
            int copies,
            string password,
            IList<SourceItem> sources,
            Func<DestinationDefinition, bool> canWrite,
            string taskId,
            IList<string> groups,
            int volumeSize,
            string namingTaskId = null
        )
        {
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Password required.");
            return Transaction(delegate
            {
                var topology = LoadTopology().Scope(groups);
                if (topology.PolicyRoots == null)
                    topology.PolicyRoots = topology.Groups.Select(group => group.Id).ToList();
                var usage = GetUsage();
                var existing = sources.Any(source => source.PriorBackupCopies != null) ? ExistingReplicaDestinations(sources) : null;
                var plan = GroupAllocator.Plan(topology, usage, Math.Min(budget, volumeSize), copies,
                    existing == null ? canWrite : destination => !existing.Contains(destination.Id) && (canWrite == null || canWrite(destination)));
                if (!plan.StructurallyComplete || plan.Pools == null || plan.Pools.Count == 0 || plan.Placements.Count == 0)
                    throw new IOException("所选仓库不能接收一个分卷，请检查容量、状态和组合成员。");
                if (existing != null)
                    foreach (var pool in plan.Pools)
                    {
                        pool.DestinationIds.RemoveAll(existing.Contains);
                        if (pool.DestinationIds.Count == 0)
                            throw new IOException("新增备份目标与原仓库重叠，没有可单独接收补传的仓库。");
                    }
                var stats = usage.ToDictionary(value => value.DestinationId);
                var destinations = topology.Destinations.ToDictionary(value => value.Id);
                foreach (var pool in plan.Pools)
                {
                    decimal free = pool.DestinationIds.Where(id => destinations[id].Enabled && !destinations[id].Retired).Sum(id => Math.Max(0, (decimal)destinations[id].MaxBytes - stats[id].UsedBytes - stats[id].ReservedBytes));
                    if (free < budget)
                        throw new IOException("组合的剩余总容量不足以容纳当前归档。");
                }

                database.Execute(
                    "INSERT INTO archives(budget,password,requested_copies,target_copies,status,created_utc,task_id) VALUES(?,?,?,?,?,?,?)",
                    budget,
                    password,
                    copies,
                    plan.Pools.Count,
                    "writing",
                    DateTime.UtcNow.ToString("o"),
                    taskId
                );
                plan.ArchiveId = database.LastId;
                plan.TaskId = taskId;
                RepositoryStatusChanged(CatalogChange.Archives, taskId);
                plan.ArchiveName = NewArchiveName(taskId, plan.ArchiveId, namingTaskId);
                plan.BudgetBytes = budget;
                plan.Placements.Clear();
                database.Execute("UPDATE archives SET name=? WHERE id=?", plan.ArchiveName, plan.ArchiveId);
                database.ExecuteMany(
                    "INSERT INTO archive_files(archive_id,path,size,modified,source_path,entry_path) VALUES(?,?,?,?,?,?)",
                    sources.Select(source => new object[] {
                    plan.ArchiveId,
                    source.ArchivePath,
                    source.Size,
                    source.Modified,
                    source.Path,
                    source.EntryPath ??
                    ""
                })
                );
                SavePools(plan.ArchiveId, plan.Pools);
                SaveSupplementalFileBasis(plan.ArchiveId, sources);
                StageCompactionArchive(plan.ArchiveId, taskId);
                return plan;
            });
        }

        public List<ArchivePlacement> ReserveVolumeRoutes(ArchivePlan archive, ObjectRecord item, ISet<string> excluded, int? onlyPool = null)
        {
            return Transaction(delegate
            {
                RepositoryStatusChanged(CatalogChange.Archives, archive.TaskId);
                var pools = archive.Pools.Where(pool => !onlyPool.HasValue || pool.Index == onlyPool.Value).ToList();
                var topology = LoadTopology();
                var flat = new StorageTopology
                {
                    Destinations = topology.Destinations,
                    Groups = pools.Select(pool => new BackupGroupDefinition { Id = "pool:" + pool.Index, DestinationIds = pool.DestinationIds.ToList() }).ToList()
                };
                var planned = WarehouseMatching.Match(flat, GetUsage(), item.Size, pools.Count, d => !excluded.Contains(d.Id), false);
                var result = new List<ArchivePlacement>();
                foreach (var route in planned)
                {
                    int index = int.Parse(route.GroupId.Substring(5));
                    var pool = pools.Single(value => value.Index == index);
                    route.GroupId = pool.GroupId;
                    if (Scalar("SELECT count(*) FROM volume_transfers WHERE name=? AND destination_id=?", item.Name, route.DestinationId) > 0)
                        continue;
                    database.Execute("INSERT OR IGNORE INTO placements(archive_id,group_id,destination_id,reserved,status) VALUES(?,?,?,0,'writing')", archive.ArchiveId, route.GroupId, route.DestinationId);
                    database.Execute("UPDATE placements SET reserved=reserved+?,status='writing' WHERE archive_id=? AND group_id=? AND destination_id=?", item.Size, archive.ArchiveId, route.GroupId, route.DestinationId);
                    database.Execute("INSERT INTO volume_transfers(name,destination_id,pool_idx,group_id,state) VALUES(?,?,?,?,'checking')", item.Name, route.DestinationId, index, route.GroupId);
                    result.Add(route);
                }

                return result;
            });
        }

        public int VolumePool(string name, string destination)
        {
            lock (gate)
                return (int)Scalar("SELECT pool_idx FROM volume_transfers WHERE name=? AND destination_id=?", name, destination);
        }

        public IList<string> GetVolumeDestinations(string name)
        {
            lock (gate)
                return database.Query("SELECT destination_id FROM volume_transfers WHERE name=? UNION SELECT destination_id FROM object_copies WHERE name=? AND state<>'deleted'", name, name).Select(row => (string)row[0])
                    .ToList();
        }

        public List<ArchivePlacement> VolumePlacements(long id)
        {
            lock (gate)
                return database.Query("SELECT group_id,destination_id,reserved,stored,status,error FROM placements WHERE archive_id=?", id).Select(row => new ArchivePlacement {
                    GroupId = (string)row[0],
                    DestinationId = (string)row[1],
                    ReservedBytes = (long)row[2],
                    StoredBytes = (long)row[3],
                    Status = (string)row[4],
                    Error = (string)row[5]
                })
                    .ToList();
        }

        public void VolumePhase(string name, string destination, string phase)
        {
            lock (gate)
                database.Execute("UPDATE volume_transfers SET state=? WHERE name=? AND destination_id=? AND state<>?", phase, name, destination, phase);
        }

        public void CommitVolumeCopy(ArchivePlan archive, ArchivePlacement place, ObjectRecord item)
        {
            Transaction(delegate
            {
                int pool = VolumePool(item.Name, place.DestinationId);
                CommitObjectCopyUnsafe(archive, place, item, pool);
                database.Execute("DELETE FROM volume_transfers WHERE name=? AND destination_id=?", item.Name, place.DestinationId);
                return 0;
            });
        }

        public void FailVolumeCopy(ArchivePlan archive, ArchivePlacement place, ObjectRecord item, bool uncertain)
        {
            Transaction(delegate
            {
                RepositoryStatusChanged(CatalogChange.Archives, archive.TaskId);
                database.Execute(
                    ("UPDATE placements SET reserved=MAX(0,reserved-?),status=CASE WHEN ? THEN 'reconcile-required' WHEN reserved-?<=0 THEN CASE WHEN stored>0 THEN 'complete' ELSE " +
                    "'failed' END ELSE status END WHERE archive_id=? AND group_id=? AND destination_id=?"),
                    uncertain ? 0 : item.Size,
                    uncertain,
                    item.Size,
                    archive.ArchiveId,
                    place.GroupId,
                    place.DestinationId
                );
                if (!uncertain)
                    database.Execute("DELETE FROM volume_transfers WHERE name=? AND destination_id=?", item.Name, place.DestinationId);
                return 0;
            });
        }

        // This records the last failed attempt, not a permanent retry ban.
        public void MarkArchiveUnrepairable(long id, string reason)
        {
            lock (gate)
                database.Execute(
                    "INSERT OR REPLACE INTO archive_unrepairable(archive_id,reason,marked_utc) VALUES(?,?,?)",
                    id,
                    reason ?? "",
                    DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
                );
        }

        public void ClearArchiveUnrepairable(long id)
        {
            lock (gate)
                database.Execute("DELETE FROM archive_unrepairable WHERE archive_id=?", id);
        }

        public bool IsArchiveUnrepairable(long id)
        {
            lock (gate)
                return Scalar("SELECT count(*) FROM archive_unrepairable WHERE archive_id=?", id) > 0;
        }

        public ISet<long> UnrepairableArchiveIds()
        {
            lock (gate)
                return new HashSet<long>(database.Query("SELECT archive_id FROM archive_unrepairable").Select(row => (long)row[0]));
        }

        private Dictionary<long, int[]> VolumeStatistics(long? archiveId = null, string taskId = null)
        {
            string filter = archiveId.HasValue ? " WHERE p.archive_id=?" : taskId != null ? " WHERE a.task_id=?" : "";
            object[] arguments = archiveId.HasValue 
                ? new object[]
            {
                archiveId.Value
            }

                : taskId != null ? new object[]
            {
                taskId
            }

            : new object[0];
            var result = new Dictionary<long, int[]>();
            foreach (var row in database.Query(
                ("SELECT p.archive_id,p.pool_idx,count(DISTINCT o.name),count(DISTINCT CASE WHEN c.state<>'deleted' THEN c.name END),count(DISTINCT CASE WHEN c.state='present' " +
                "THEN c.name END) FROM archive_pools p JOIN archives a ON a.id=p.archive_id LEFT JOIN objects o ON o.archive_id=p.archive_id LEFT JOIN object_copies c ON " +
                "c.name=o.name AND c.pool_idx=p.pool_idx ") +
                filter +
                " GROUP BY p.archive_id,p.pool_idx",
                arguments
            ))
            {
                long id = (long)row[0], total = (long)row[2];
                int[] counts;
                if (!result.TryGetValue(id, out counts))
                {
                    counts = new int[2];
                    result[id] = counts;
                }

                if (total > 0 && (long)row[3] == total)
                    counts[0]++;
                if (total > 0 && (long)row[4] == total)
                    counts[1]++;
            }

            return result;
        }

        private void ApplyVolumeStatistics(ArchiveSummary archive, Dictionary<long, int[]> stats)
        {
            int[] counts;
            if (!stats.TryGetValue(archive.ArchiveId, out counts))
                return;
            bool finalized = new[]
            {
                "complete",
                "repair-required",
                "redundancy-shortfall"
            }.Contains(archive.Status);
            archive.CompleteCopies = finalized ? counts[0] : 0;
            archive.CurrentValidCopies = finalized ? counts[1] : 0;
        }

        public ArchiveSummary FinishVolumeArchive(ArchivePlan archive, bool success)
        {
            return Transaction(delegate
            {
                RepositoryStatusChanged(CatalogChange.Archives, archive.TaskId);
                database.Execute("UPDATE placements SET status=CASE WHEN reserved>0 THEN 'reconcile-required' WHEN stored>0 THEN 'complete' ELSE 'failed' END WHERE archive_id=?", archive.ArchiveId);
                var stats = VolumeStatistics(archive.ArchiveId);
                int[] counts;
                int complete = stats.TryGetValue(archive.ArchiveId, out counts) ? counts[1] : 0;
                success = HasReadableInspectedManifest(archive.ArchiveId);
                bool exhausted = Scalar("SELECT count(*) FROM volume_transfers t JOIN objects o ON o.name=t.name WHERE o.archive_id=? AND t.state='failed-upload'", archive.ArchiveId) > 0;
                string status = !success ? "failed" : exhausted ? "repair-required" : complete >= archive.TargetCopies ? "complete" : "redundancy-shortfall";
                database.Execute("UPDATE archives SET status=? WHERE id=?", status, archive.ArchiveId);
                if (exhausted)
                    database.Execute(
                        ("UPDATE placements SET status='repair-required' WHERE archive_id=? AND EXISTS(SELECT 1 FROM volume_transfers t JOIN objects o ON o.name=t.name WHERE " +
                        "o.archive_id=placements.archive_id AND t.destination_id=placements.destination_id AND t.state='failed-upload')"),
                        archive.ArchiveId
                    );
                if (!success)
                {
                    database.Execute("UPDATE archive_files SET state='interrupted' WHERE archive_id=? AND state NOT IN ('staged','supplementing')", archive.ArchiveId);
                    database.Execute("INSERT OR IGNORE INTO archive_holds(archive_id,reason) VALUES(?,'interrupted-awaiting-inspection')", archive.ArchiveId);
                    if (exhausted)
                        ForgetFailedNewArchiveUnsafe(archive.ArchiveId, archive.TaskId);
                }
                else
                {
                    database.Execute("DELETE FROM archive_holds WHERE archive_id=? AND reason='interrupted-awaiting-inspection'", archive.ArchiveId);
                    if (complete >= archive.TargetCopies)
                        ExpireOlderVersions(archive.ArchiveId);
                }

                return GetArchive(archive.ArchiveId);
            });
        }

        public bool CoversArchivePolicy(long id, StorageTopology policy, int copies)
        {
            lock (gate)
            {
                if (!IsVolumeArchive(id))
                    return GroupAllocator.CoversPolicy(policy, GetCompleteDestinations(id), copies);
                var objects = GetObjects(id);
                var stored = database.Query("SELECT c.name,c.destination_id FROM object_copies c JOIN objects o ON o.name=c.name WHERE o.archive_id=? AND c.state='present' AND c.pool_idx>=0", id).GroupBy(row => (string)row[0])
                    .ToDictionary(group => group.Key, group => new HashSet<string>(group.Select(row => (string)row[1])));
                return objects.Count > 0 && objects.All(item => stored.ContainsKey(item.Name) && GroupAllocator.CoversPolicy(policy, stored[item.Name], copies));
            }
        }

    }
}
