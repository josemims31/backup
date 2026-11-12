using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Backup.Core
{
    public sealed class StoredCopy
    {
        public string GroupId;
        public string DestinationId;
        public ObjectRecord Object;
    }
    public sealed partial class BackupCatalog : IDisposable
    {
        private readonly object gate = new object();
        private readonly SqliteDatabase database;
        public DateTime LastSavedUtc {get{return database.LastSavedUtc;}}
        public BackupCatalog(string path):this(new SqliteDatabase(path,true),path){}
        public static BackupCatalog FromMemory(byte[] bytes){return new BackupCatalog(SqliteDatabase.FromBytes(bytes),null);}
        private BackupCatalog(SqliteDatabase storage,string path)
        {
            database=storage;
            try
            {
                InitializeRuntimeCache(path);
                long version = (long)database.Query("PRAGMA user_version")[0][0];
                if(version!=0&&version!=8&&version!=9&&version!=10)throw new IOException("此版本采用独立备份版本数据库，不支持旧数据库。请在新的软件目录重新配置并重建备份，保留旧目录以便恢复。");
                InitializeNodeFiles(path);
                foreach (string sql in new[] {
                    "PRAGMA foreign_keys=ON", "PRAGMA journal_mode=WAL", "PRAGMA synchronous=FULL",
                    "CREATE TABLE IF NOT EXISTS destinations(id TEXT PRIMARY KEY,protocol TEXT NOT NULL,location TEXT NOT NULL,max_bytes INTEGER NOT NULL CHECK(max_bytes>0),used_bytes INTEGER NOT NULL DEFAULT 0 CHECK(used_bytes>=0),enabled INTEGER NOT NULL)",
                    "CREATE TABLE IF NOT EXISTS groups(id TEXT PRIMARY KEY,name TEXT NOT NULL,notes TEXT NOT NULL)",
                    "CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT NOT NULL)",
                    "CREATE TABLE IF NOT EXISTS archive_unrepairable(archive_id INTEGER PRIMARY KEY,reason TEXT NOT NULL DEFAULT '',marked_utc TEXT NOT NULL DEFAULT '')",
                    "CREATE TABLE IF NOT EXISTS group_members(destination_id TEXT NOT NULL REFERENCES destinations(id),group_id TEXT NOT NULL REFERENCES groups(id),PRIMARY KEY(destination_id,group_id))",
                    "CREATE TABLE IF NOT EXISTS archives(id INTEGER PRIMARY KEY AUTOINCREMENT,name TEXT UNIQUE,budget INTEGER NOT NULL,password TEXT NOT NULL,requested_copies INTEGER NOT NULL,target_copies INTEGER NOT NULL,status TEXT NOT NULL,created_utc TEXT NOT NULL,task_id TEXT NOT NULL DEFAULT 'legacy')",
                    "CREATE TABLE IF NOT EXISTS placements(archive_id INTEGER NOT NULL REFERENCES archives(id),group_id TEXT NOT NULL REFERENCES groups(id),destination_id TEXT NOT NULL REFERENCES destinations(id),reserved INTEGER NOT NULL CHECK(reserved>=0),stored INTEGER NOT NULL DEFAULT 0,status TEXT NOT NULL,error TEXT NOT NULL DEFAULT '',PRIMARY KEY(archive_id,group_id,destination_id))",
                    "CREATE INDEX IF NOT EXISTS placements_destination ON placements(destination_id,status)",
                    "CREATE TABLE IF NOT EXISTS objects(name TEXT PRIMARY KEY,archive_id INTEGER NOT NULL REFERENCES archives(id),size INTEGER NOT NULL,sha256 TEXT NOT NULL)",
                    "CREATE INDEX IF NOT EXISTS objects_archive ON objects(archive_id)",
                    "CREATE TABLE IF NOT EXISTS object_copies(name TEXT NOT NULL REFERENCES objects(name),destination_id TEXT NOT NULL REFERENCES destinations(id),group_id TEXT NOT NULL REFERENCES groups(id),state TEXT NOT NULL,PRIMARY KEY(name,destination_id))",
                    "PRAGMA foreign_keys=ON" }) database.Execute(sql);
                InitializeTaskSchema(version);
                InitializeOperationsSchema();
                InitializeRaidSchema();
                InitializeVolumeSchema();
                InitializeRecoverySchema();
                InitializeOpenListUploads();
                InitializeNodeSchema();
                InitializeStorageSchema();
                database.Execute("PRAGMA user_version=10");
                RecoverInterruptedCompactions();
                MigrateInventoryCache();
                if(GetSetting("recovery_snapshot_v1")=="1")Transaction(delegate{
                    // The exporting archive is created after its snapshot. Older catalog packages may
                    // already have been collected; their snapshot-era rows are historical, not losses.
                    database.Execute("UPDATE archive_files SET state='expired' WHERE archive_id IN(SELECT id FROM archives WHERE task_id='__catalog__')");
                    database.Execute("UPDATE placements SET status='snapshot-history' WHERE archive_id IN(SELECT id FROM archives WHERE task_id='__catalog__') AND status IN ('complete','repair-required','unavailable')");
                    database.Execute("UPDATE archives SET status='snapshot-history' WHERE task_id='__catalog__' AND status IN ('complete','repair-required','redundancy-shortfall')");
                    database.Execute("DELETE FROM settings WHERE key='recovery_snapshot_v1'");return 0;
                });
                long floor;if(long.TryParse(GetSetting("catalog_sequence_floor"),out floor))AdvanceArchiveSequence(floor);
            }
            catch { if(runtimeCache!=null)runtimeCache.Dispose();database.Dispose(); throw; }
        }
        private T Transaction<T>(Func<T> action) { return RepositoryTransaction(action); }
        private long Scalar(string sql, params object[] args)
        { var rows = database.Query(sql, args); return rows.Count == 0 || rows[0][0] == null ? 0 : Convert.ToInt64(rows[0][0]); }
        public void SaveTopology(StorageTopology configuration)
        {SaveTopologyAndConfiguration(configuration,new KeyValuePair<string,string>[0]);}
        public void SaveTopologyAndConfiguration(StorageTopology configuration,IList<KeyValuePair<string,string>> values)
        {
            StorageTopology next = configuration.Normalize();
            Transaction(delegate
            {
                StorageTopology old = LoadTopology();
                if(SameTopology(old,next)){SaveConfigurationUnsafe(values);return 0;}
                var nextDest = next.Destinations.ToDictionary(x => x.Id, StringComparer.Ordinal);

                foreach (DestinationDefinition member in old.Destinations)
                {
                    long stored = Scalar("SELECT count(*) FROM object_copies WHERE destination_id=?", member.Id);
                    long reserved = Scalar("SELECT coalesce(sum(reserved),0) FROM placements WHERE destination_id=?", member.Id);
                    DestinationDefinition replacement;
                    if (!nextDest.TryGetValue(member.Id, out replacement))
                    {
                        if (stored != 0 || reserved != 0 || Scalar("SELECT count(*) FROM placements WHERE destination_id=?", member.Id) != 0)
                            throw new InvalidOperationException("Destination removal requires migration first: " + member.Id);
                        continue;
                    }
                    long used = Scalar("SELECT used_bytes FROM destinations WHERE id=?", member.Id);
                    if (replacement.MaxBytes < (decimal)used + reserved) throw new InvalidOperationException("Quota is below used and reserved capacity.");
                    bool changed = member.Protocol != replacement.Protocol || member.Location != replacement.Location;
                    bool connectionOnly = RemotePath.IsSftpPortChange(member,replacement);
                    if(changed&&connectionOnly&&(reserved!=0||Scalar("SELECT count(*) FROM placements WHERE destination_id=? AND status IN ('writing','reconcile-required')",member.Id)!=0||Scalar("SELECT count(*) FROM archive_leases l JOIN placements p ON p.archive_id=l.archive_id WHERE p.destination_id=?",member.Id)!=0||Scalar("SELECT count(*) FROM warehouse_moves WHERE destination_id=? AND state<>'complete'",member.Id)!=0))
                        throw new InvalidOperationException("仓库仍有正在上传、读取或迁移的数据，请完成后再修改 SFTP 端口。");
                    if (changed && !connectionOnly && (stored != 0 || reserved != 0 || Scalar("SELECT count(*) FROM placements WHERE destination_id=?", member.Id) != 0))
                        throw new InvalidOperationException("此仓库已有备份或分配记录，不能直接修改驱动或路径。请先添加新仓库并迁移。");
                }
                foreach (BackupGroupDefinition group in old.Groups.Where(g => !next.Groups.Any(n => n.Id == g.Id)))
                    if (Scalar("SELECT count(*) FROM placements WHERE group_id=?", group.Id) != 0)
                        throw new InvalidOperationException("Group removal requires migration first.");
                foreach (DestinationDefinition member in next.Destinations)
                {
                    database.Execute("INSERT OR IGNORE INTO destinations(id,protocol,location,max_bytes,enabled) VALUES(?,?,?,?,?)", member.Id, member.Protocol, member.Location, member.MaxBytes, member.Enabled);
                    database.Execute("UPDATE destinations SET protocol=?,location=?,max_bytes=?,enabled=? WHERE id=?", member.Protocol, member.Location, member.MaxBytes, member.Enabled, member.Id);
                }
                foreach (BackupGroupDefinition group in next.Groups)
                {
                    database.Execute("INSERT OR IGNORE INTO groups(id,name,notes) VALUES(?,?,?)", group.Id, group.Name, group.Notes);
                    database.Execute("UPDATE groups SET name=?,notes=? WHERE id=?", group.Name, group.Notes, group.Id);
                    database.Execute("INSERT OR REPLACE INTO group_modes(group_id,mode) VALUES(?,?)",group.Id,group.Mode??"raid0");
                }
                database.Execute("DELETE FROM group_children");
                database.Execute("DELETE FROM group_members");
                foreach (BackupGroupDefinition group in next.Groups)
                    foreach (string member in group.DestinationIds) database.Execute("INSERT INTO group_members(destination_id,group_id) VALUES(?,?)", member, group.Id);
                foreach(var group in next.Groups)foreach(string child in group.ChildGroupIds)database.Execute("INSERT INTO group_children(group_id,child_id) VALUES(?,?)",group.Id,child);
                foreach (DestinationDefinition member in old.Destinations.Where(x => !nextDest.ContainsKey(x.Id))) database.Execute("DELETE FROM destinations WHERE id=?", member.Id);
                foreach (BackupGroupDefinition group in old.Groups.Where(x => !next.Groups.Any(n => n.Id == x.Id))){database.Execute("DELETE FROM group_modes WHERE group_id=?",group.Id);database.Execute("DELETE FROM groups WHERE id=?", group.Id);}
                RepositoryStatusChanged(CatalogChange.Topology);SaveConfigurationUnsafe(values);
                return 0;
            });
        }
        public StorageTopology LoadTopology()
        {
            lock (gate)
            {
                var topology = new StorageTopology();
                foreach (object[] row in database.Query("SELECT id,protocol,location,max_bytes,enabled FROM destinations ORDER BY id"))
                    topology.Destinations.Add(new DestinationDefinition { Id = (string)row[0], Protocol = (string)row[1], Location = (string)row[2], MaxBytes = (long)row[3], Enabled = (long)row[4] != 0 });
                foreach (object[] row in database.Query("SELECT g.id,g.name,g.notes,coalesce(m.mode,'raid0') FROM groups g LEFT JOIN group_modes m ON m.group_id=g.id ORDER BY g.id"))
                {
                    var group = new BackupGroupDefinition { Id = (string)row[0], Name = (string)row[1], Notes = (string)row[2],Mode=(string)row[3] };
                    foreach (object[] member in database.Query("SELECT destination_id FROM group_members WHERE group_id=? ORDER BY destination_id", group.Id)) group.DestinationIds.Add((string)member[0]);
                    foreach(object[] child in database.Query("SELECT child_id FROM group_children WHERE group_id=? ORDER BY child_id",group.Id))group.ChildGroupIds.Add((string)child[0]);
                    topology.Groups.Add(group);
                }
                ApplyRetirements(topology);
                return topology;
            }
        }
        public IList<DestinationUsage> GetUsage()
        {
            lock (gate)
                return database.Query("SELECT d.id,d.used_bytes,coalesce((SELECT sum(p.reserved) FROM placements p WHERE p.destination_id=d.id AND p.reserved>0),0) FROM destinations d ORDER BY d.id")
                    .Select(r => new DestinationUsage { DestinationId = (string)r[0], UsedBytes = (long)r[1], ReservedBytes = (long)r[2] }).ToList();
        }
        public ArchivePlan ReserveArchive(long budget, int requestedCopies, string password, IList<SourceItem> sources, Func<DestinationDefinition, bool> canWrite)
        {return ReserveArchive(budget,requestedCopies,password,sources,canWrite,"legacy",null);}
        public ArchivePlan ReserveArchive(long budget,int requestedCopies,string password,IList<SourceItem> sources,Func<DestinationDefinition,bool> canWrite,string taskId,IList<string> groupIds,string namingTaskId=null)
        {
            if (string.IsNullOrEmpty(password)) throw new ArgumentException("Password required.");
            return Transaction(delegate
            {
                var scope=groupIds==null?LoadTopology():LoadTopology().Scope(groupIds);ArchivePlan plan = GroupAllocator.Plan(scope, GetUsage(), budget, requestedCopies, canWrite);
                if(groupIds!=null&&(!plan.StructurallyComplete||scope.PolicyRoots==null&&plan.TargetCopies!=requestedCopies))throw new IOException("所选目标无法保存全部份数，请检查容量上限与组合成员。 ");
                if (plan.TargetCopies == 0 || plan.Placements.Count == 0) throw new IOException("No group has an available member with sufficient capacity.");
                database.Execute("INSERT INTO archives(budget,password,requested_copies,target_copies,status,created_utc,task_id) VALUES(?,?,?,?,?,?,?)", budget, password,
                    requestedCopies, plan.TargetCopies, "writing", DateTime.UtcNow.ToString("o"),taskId??"legacy");
                plan.ArchiveId = database.LastId;plan.TaskId=taskId??"legacy";RepositoryStatusChanged(CatalogChange.Archives,plan.TaskId);
                plan.ArchiveName = NewArchiveName(taskId,plan.ArchiveId,namingTaskId);
                database.Execute("UPDATE archives SET name=? WHERE id=?", plan.ArchiveName, plan.ArchiveId);
                database.ExecuteMany("INSERT INTO archive_files(archive_id,path,size,modified,source_path,entry_path) VALUES(?,?,?,?,?,?)",sources.Select(source=>new object[]{plan.ArchiveId,source.ArchivePath,source.Size,source.Modified,source.Path,source.EntryPath??""}));
                database.ExecuteMany("INSERT INTO placements(archive_id,group_id,destination_id,reserved,status) VALUES(?,?,?,?,?)",plan.Placements.Select(place=>new object[]{plan.ArchiveId,place.GroupId,place.DestinationId,place.ReservedBytes,place.Status}));
                StageCompactionArchive(plan.ArchiveId,taskId);return plan;
            });
        }
        public void RegisterObject(ArchivePlan plan, ObjectRecord item)
        {
            Transaction(delegate
            {
                RepositoryStatusChanged(CatalogChange.Archives,plan.TaskId);
                bool validName = item.Name == plan.ArchiveName ||
                    (item.Name.StartsWith(plan.ArchiveName + ".", StringComparison.Ordinal) &&
                     item.Name.Substring(plan.ArchiveName.Length + 1).Length >= 3 &&
                     item.Name.Substring(plan.ArchiveName.Length + 1).All(char.IsDigit));
                if (!validName || item.Size <= 0 || string.IsNullOrEmpty(item.Sha256)) throw new ArgumentException("Invalid archive object.");
                if (Scalar("SELECT count(*) FROM objects WHERE name=?", item.Name) != 0) throw new InvalidOperationException("Archive object already registered.");
                long current = Scalar("SELECT coalesce(sum(size),0) FROM objects WHERE archive_id=?", plan.ArchiveId);
                if ((decimal)current + item.Size > plan.BudgetBytes) throw new IOException("Archive exceeded reserved capacity; no further object was uploaded.");
                database.Execute("INSERT INTO objects(name,archive_id,size,sha256) VALUES(?,?,?,?)", item.Name, plan.ArchiveId, item.Size, item.Sha256);
                return 0;
            });
        }
        public void CommitObjectCopy(ArchivePlan plan,ArchivePlacement place,ObjectRecord item,int poolIndex=-1)
        {Transaction(delegate{CommitObjectCopyUnsafe(plan,place,item,poolIndex);return 0;});}
        private void CommitObjectCopyUnsafe(ArchivePlan plan,ArchivePlacement place,ObjectRecord item,int poolIndex)
        {
                database.Execute("DELETE FROM object_copies WHERE name=? AND destination_id=? AND state='deleted'",item.Name,place.DestinationId);
                if (Scalar("SELECT count(*) FROM object_copies WHERE name=? AND destination_id=?", item.Name, place.DestinationId) != 0)
                    throw new InvalidOperationException("Duplicate object copy.");
                long remaining = Scalar("SELECT reserved FROM placements WHERE archive_id=? AND group_id=? AND destination_id=? AND status IN ('writing','reconcile-required')", plan.ArchiveId, place.GroupId,place.DestinationId);
                if (remaining < item.Size) throw new IOException("Placement reservation exhausted.");
                database.Execute("INSERT INTO object_copies(name,destination_id,group_id,state,pool_idx) VALUES(?,?,?,'present',?)", item.Name, place.DestinationId, place.GroupId,poolIndex);
                database.Execute("UPDATE destinations SET used_bytes=used_bytes+? WHERE id=?", item.Size, place.DestinationId);
                database.Execute("UPDATE placements SET reserved=reserved-?,stored=stored+? WHERE archive_id=? AND group_id=? AND destination_id=?", item.Size, item.Size, plan.ArchiveId, place.GroupId,place.DestinationId);
                RepositoryObjectCommitted(place.DestinationId,item.Name,item.Size,plan.ArchiveId,true,taskId:plan.TaskId);
        }
        public void FailPlacement(ArchivePlan plan, ArchivePlacement place, string error, bool retainReservation = false)
        {
            string status = retainReservation ? "reconcile-required" : "failed";
            lock (gate) database.Execute("UPDATE placements SET status=?,reserved=CASE WHEN ? THEN reserved ELSE 0 END,error=? WHERE archive_id=? AND group_id=? AND destination_id=?",
                status, retainReservation, error, plan.ArchiveId, place.GroupId,place.DestinationId);
            place.Status = status; if (!retainReservation) place.ReservedBytes = 0; place.Error = error;
        }
        public ArchiveSummary FinishArchive(ArchivePlan plan, bool sourceSucceeded)
        {
            return Transaction(delegate
            {RepositoryStatusChanged(CatalogChange.Archives,plan.TaskId);
                long objects = Scalar("SELECT count(*) FROM objects WHERE archive_id=?", plan.ArchiveId);
                foreach (ArchivePlacement place in plan.Placements)
                {
                    if (place.Status == "reconcile-required") continue;
                    long copied = Scalar("SELECT count(*) FROM object_copies c JOIN objects o ON o.name=c.name WHERE o.archive_id=? AND c.group_id=? AND c.destination_id=? AND c.state='present'", plan.ArchiveId, place.GroupId,place.DestinationId);
                    string status = sourceSucceeded && objects > 0 && copied == objects && place.Status == "writing" ? "complete" : "failed";
                    database.Execute("UPDATE placements SET status=?,reserved=0 WHERE archive_id=? AND group_id=? AND destination_id=?", status, plan.ArchiveId, place.GroupId,place.DestinationId);
                    place.Status = status; place.ReservedBytes = 0;
                }
                long complete = Scalar("SELECT count(*) FROM placements WHERE archive_id=? AND status='complete'", plan.ArchiveId);
                string archiveStatus = !sourceSucceeded || complete == 0 ? "failed" : complete >= plan.TargetCopies ? "complete" : "redundancy-shortfall";
                database.Execute("UPDATE archives SET status=? WHERE id=?", archiveStatus, plan.ArchiveId);
                if(sourceSucceeded&&complete>=plan.TargetCopies)ExpireOlderVersions(plan.ArchiveId);
                return GetArchive(plan.ArchiveId);
            });
        }
        public ArchiveSummary GetArchive(long id)
        {
            lock (gate)
            {
                var rows = database.Query("SELECT id,name,status,target_copies,task_id FROM archives WHERE id=?", id);
                if (rows.Count != 1) throw new ArgumentException("Unknown archive.");
                object[] row = rows[0];
                int complete = (int)Scalar("SELECT count(*) FROM placements WHERE archive_id=? AND status IN ('complete','repair-required','unavailable')", id);
                int valid = (int)Scalar("SELECT count(*) FROM placements WHERE archive_id=? AND status='complete'", id);
                var summary=new ArchiveSummary { TaskId=(string)row[4], ArchiveId = id, ArchiveName = (string)row[1], Status = (string)row[2], TargetCopies = (int)(long)row[3],
                    CompleteCopies = complete, CurrentValidCopies = valid, UnresolvedTransfers = (int)Scalar("SELECT count(*) FROM placements WHERE archive_id=? AND status='reconcile-required'", id),
                    ActualBytes = Scalar("SELECT coalesce(sum(size),0) FROM objects WHERE archive_id=?", id) };ApplyVolumeStatistics(summary,VolumeStatistics(id));return summary;
            }
        }
        public IList<ArchiveSummary> ListArchives(bool includeSystem=false)
        {lock(gate){var result=database.Query("SELECT a.id,a.name,a.status,a.target_copies,a.task_id,coalesce(p.complete,0),coalesce(p.valid,0),coalesce(p.unresolved,0),coalesce(o.bytes,0) FROM archives a LEFT JOIN (SELECT archive_id,sum(CASE WHEN status IN ('complete','repair-required','unavailable') THEN 1 ELSE 0 END) complete,sum(CASE WHEN status='complete' THEN 1 ELSE 0 END) valid,sum(CASE WHEN status='reconcile-required' THEN 1 ELSE 0 END) unresolved FROM placements GROUP BY archive_id) p ON p.archive_id=a.id LEFT JOIN (SELECT archive_id,sum(size) bytes FROM objects GROUP BY archive_id) o ON o.archive_id=a.id WHERE (? OR a.task_id<>'__catalog__') ORDER BY a.id",includeSystem).Select(r=>new ArchiveSummary{ArchiveId=(long)r[0],ArchiveName=(string)r[1],Status=(string)r[2],TargetCopies=(int)(long)r[3],TaskId=(string)r[4],CompleteCopies=(int)(long)r[5],CurrentValidCopies=(int)(long)r[6],UnresolvedTransfers=(int)(long)r[7],ActualBytes=(long)r[8]}).ToList();var stats=VolumeStatistics();foreach(var archive in result)ApplyVolumeStatistics(archive,stats);return result;}}
        public void ValidateIntegrity(){lock(gate){var rows=database.Query("PRAGMA quick_check");if(rows.Count!=1||(string)rows[0][0]!="ok"||database.Query("PRAGMA foreign_key_check").Count!=0)throw new IOException("数据库完整性检查未通过。");}}
        public string GetArchivePassword(long id)
        {
            lock (gate)
            {
                var rows = database.Query("SELECT password FROM archives WHERE id=?", id);
                if (rows.Count != 1) throw new ArgumentException("Unknown archive.");
                return (string)rows[0][0];
            }
        }
        public IList<StoredCopy> GetCopies(long id)
        {
            lock (gate)
                return database.Query("SELECT c.group_id,c.destination_id,o.name,o.size,o.sha256 FROM object_copies c JOIN objects o ON c.name=o.name WHERE o.archive_id=? AND c.state<>'deleted' ORDER BY c.group_id,o.name", id)
                    .Select(r => new StoredCopy { GroupId = (string)r[0], DestinationId = (string)r[1], Object = new ObjectRecord { Name = (string)r[2], Size = (long)r[3], Sha256 = (string)r[4] } }).ToList();
        }
        public IList<StoredCopy> GetVerifiableCopies(long id)
        {
            lock (gate)
                return database.Query("SELECT c.group_id,c.destination_id,o.name,o.size,o.sha256 FROM object_copies c JOIN objects o ON c.name=o.name JOIN placements p ON p.archive_id=o.archive_id AND p.group_id=c.group_id AND p.destination_id=c.destination_id WHERE o.archive_id=? AND c.state<>'deleted' AND (p.status IN ('complete','repair-required','unavailable') OR c.pool_idx>=0 AND (SELECT status FROM archives WHERE id=o.archive_id) IN ('complete','repair-required','redundancy-shortfall')) ORDER BY c.group_id,o.name", id)
                    .Select(r => new StoredCopy { GroupId = (string)r[0], DestinationId = (string)r[1], Object = new ObjectRecord { Name = (string)r[2], Size = (long)r[3], Sha256 = (string)r[4] } }).ToList();
        }
        public void RecordIntegrity(long id, string groupId, IList<IntegrityFinding> findings)
        {
            Transaction(delegate
            {
                RepositoryStatusChanged(CatalogChange.Archives,(string)database.Query("SELECT task_id FROM archives WHERE id=?",id).Single()[0]);
                RepositoryDestinationsChanged(findings.Select(f=>f.Destination));
                if(IsVolumeArchive(id)){RecordVolumeIntegrity(id,groupId,findings);return 0;}
                long expected = Scalar("SELECT count(*) FROM object_copies c JOIN objects o ON c.name=o.name JOIN placements p ON p.archive_id=o.archive_id AND p.group_id=c.group_id AND p.destination_id=c.destination_id WHERE o.archive_id=? AND c.group_id=? AND c.state<>'deleted'", id, groupId);
                if (expected != findings.Count) throw new InvalidOperationException("Incomplete integrity observation.");
                var inspectedStates=IntegrityCopyStates(id);
                foreach (IntegrityFinding finding in findings)
                {
                    if (Scalar("SELECT count(*) FROM object_copies c JOIN objects o ON c.name=o.name WHERE o.archive_id=? AND c.group_id=? AND c.destination_id=? AND c.name=?", id, groupId, finding.Destination, finding.Name) != 1)
                        throw new InvalidOperationException("Integrity observation does not match a catalog copy.");
                    string state=ObservedCopyState(finding,inspectedStates[finding.Destination+"\0"+finding.Name]);database.Execute("UPDATE object_copies SET state=? WHERE name=? AND destination_id=? AND state<>?",state,finding.Name,finding.Destination,state);
                }
                foreach(var destination in findings.GroupBy(f=>f.Destination)){
                    string state=destination.Any(f=>f.RequiresManualRepairConfirmation)?"repair-required":destination.Any(f=>f.Status.StartsWith("unavailable",StringComparison.Ordinal))?"unavailable":"complete";
                    database.Execute("UPDATE placements SET status=? WHERE archive_id=? AND group_id=? AND destination_id=? AND (status IN ('complete','repair-required','unavailable') OR ?=1 AND status NOT IN ('detached','retired','cleaned')) AND status<>?",state,id,groupId,destination.Key,findings.Any(item=>item.RepairApplied),state);
                }
                long target = Scalar("SELECT target_copies FROM archives WHERE id=?", id);
                long complete = Scalar("SELECT count(*) FROM placements WHERE archive_id=? AND status='complete'", id);
                string status = Scalar("SELECT count(*) FROM placements WHERE archive_id=? AND status='repair-required'", id) != 0 ? "repair-required" : complete >= target ? "complete" : "redundancy-shortfall";
                database.Execute("UPDATE archives SET status=? WHERE id=? AND status IN ('complete','repair-required','redundancy-shortfall') AND status<>?", status, id,status);
                return 0;
            });
        }
        public void CreateSnapshot(string path) { lock (gate) database.Snapshot(path); }
        public void ReplaceWith(BackupCatalog source){lock(gate)lock(source.gate){database.ReplaceWith(source.database);database.Execute("DELETE FROM file_work.archive_files");database.Execute("DELETE FROM file_work.node_basis");ClearNodeGarbageReview();replacementEpoch++;latestViews.Clear();}}
        public byte[] CreateSnapshotBytes(){lock(gate)return database.SnapshotBytes();}
        public byte[] CreateRecoverySnapshotBytes(long maximumBytes=long.MaxValue){lock(gate)return database.SnapshotBytes(PrepareRecoverySnapshot,maximumBytes);}
        public void CreateRecoverySnapshot(string path){lock(gate){database.Snapshot(path);using(var copy=new SqliteDatabase(path))copy.Execute("INSERT OR REPLACE INTO settings(key,value) VALUES('recovery_snapshot_v1','1')");}}
        private string NewArchiveName(string taskId,long id,string namingTaskId=null)
        {
            string sequence=NextArchiveNumber()+".7z";
            if(taskId=="__catalog__")return "catalog-"+sequence;
            bool node=taskId.StartsWith("__node__:",StringComparison.Ordinal);
            if(node&&namingTaskId==null)return "node-"+sequence;
            string owner=namingTaskId??(taskId.StartsWith("__repack__:",StringComparison.Ordinal)?taskId.Substring(11):taskId??"legacy");
            return (node?"node-":"data-")+EnsureTaskTagUnsafe(owner)+"-"+sequence;
        }
        public long NextArchiveSequence(){lock(gate)return Math.Max(Scalar("SELECT coalesce(max(id),0) FROM archives"),Scalar("SELECT seq FROM sqlite_sequence WHERE name='archives'"))+1;}
        private void AdvanceArchiveSequence(long floor){if(floor<0)throw new IOException("数据库快照中的压缩包编号无效。");Transaction(delegate{long current=Scalar("SELECT seq FROM sqlite_sequence WHERE name='archives'");if(current<floor){if(Scalar("SELECT count(*) FROM sqlite_sequence WHERE name='archives'")==0)database.Execute("INSERT INTO sqlite_sequence(name,seq) VALUES('archives',?)",floor);else database.Execute("UPDATE sqlite_sequence SET seq=? WHERE name='archives'",floor);}return 0;});}
        private bool SaveSettingUnsafe(string key,string value)
        {
            string current=GetSetting(key);
            // Absence already means empty for these optional states. The same
            // write policy applies inside lifecycle/schedule transactions.
            if((value==""&&(key=="last_upload_alert_v1"||key=="last_upload_warnings_v1"))||(value=="[]"&&(key=="backup_schedules_v1"||key=="confirmed_repairs_v1")))
            {if(current==null)return false;database.Execute("DELETE FROM settings WHERE key=?",key);SettingChanged(key);return true;}
            if(current==value)return false;
            database.Execute("INSERT OR IGNORE INTO settings(key,value) VALUES(?,?)",key,value);
            database.Execute("UPDATE settings SET value=? WHERE key=?",value,key);SettingChanged(key);return true;
        }
        public void SetSetting(string key,string value){Transaction(delegate{SaveSettingUnsafe(key,value);return 0;});}
        public string GetSetting(string key)
        {
            lock (gate) { var rows=database.Query("SELECT value FROM settings WHERE key=?",key); return rows.Count==0 ? null : (string)rows[0][0]; }
        }
        public void RemoveSetting(string key){SaveConfiguration(new[]{new KeyValuePair<string,string>(key,null)});}
        // The legacy global view intentionally spans tasks and all file states.
        public IList<FileBackupView> GetLatestFileViews()
        {
            lock(gate)
                return database.Query("WITH latest AS (SELECT newer.path,max(newer.archive_id) AS archive_id FROM archive_files newer JOIN archives na ON na.id=newer.archive_id WHERE na.status IN ('complete','redundancy-shortfall','repair-required') GROUP BY newer.path) SELECT f.path,f.size,f.modified,a.id,a.name,a.status,a.target_copies,coalesce(p.valid,0),coalesce(p.locations,'') FROM archive_files f JOIN archives a ON a.id=f.archive_id JOIN latest ON latest.archive_id=a.id AND latest.path=f.path LEFT JOIN (SELECT archive_id,sum(CASE WHEN p.status='complete' THEN 1 ELSE 0 END) valid,group_concat(g.name || ' → ' || p.destination_id,'；') locations FROM placements p JOIN groups g ON g.id=p.group_id WHERE p.status IN ('complete','repair-required','unavailable') GROUP BY archive_id) p ON p.archive_id=a.id WHERE a.status IN ('complete','redundancy-shortfall','repair-required') ORDER BY f.rowid")
                    .Select(r=>new FileBackupView {ArchivePath=(string)r[0],Size=(long)r[1],Modified=(long)r[2],ArchiveId=(long)r[3],ArchiveName=(string)r[4],Status=(string)r[5],TargetCopies=(int)(long)r[6],ValidCopies=(int)(long)r[7],Locations=(string)r[8]}).ToList();
        }
        public void Dispose() { lock (gate){if(runtimeCache!=null)runtimeCache.Dispose();database.Dispose();if(importedCatalogMemory!=null){importedCatalogMemory.Dispose();importedCatalogMemory=null;}} }
    }
}
