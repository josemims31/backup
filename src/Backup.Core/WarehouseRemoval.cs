using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Backup.Core
{
    public sealed partial class BackupCatalog
    {
        // A detached copy has independent ownership: migration/GC cannot delete a kept object.
        private void DetachWarehouseUnsafe(string groupId, string mode)
        {
            bool single = groupId.StartsWith("single:");
            string destination = single ? LoadTopology().Groups.Single(g => g.Id == groupId).DestinationIds.Single() : "";
            var places = database.Query("SELECT archive_id,group_id,destination_id FROM placements WHERE (?=1 AND destination_id=?) OR (?=0 AND group_id=?)", single ? 1 : 0, destination, single ? 1 : 0, groupId);
            var ids = places.Select(r => (long)r[0]).Distinct().ToList();
            foreach (long id in ids)
                if (HasOutstandingTransfer(id) || GetReplicaStages(id).Count > 0 || Scalar("SELECT count(*) FROM archive_leases WHERE archive_id=?", id) > 0 ||
                    GetArchive(id).Status == "writing")
                    throw new IOException("备份包正在读取、上传或等待核对，请处理完成后再删除仓库。");
            foreach (var place in places)
            {
                long id = (long)place[0];
                string group = (string)place[1], physical = (string)place[2];
                database.Execute(
                    ("INSERT OR IGNORE INTO warehouse_detached(name,destination_id,group_id,archive_id,mode,state) SELECT c.name,c.destination_id,c.group_id,o.archive_id,?,'pending' " +
                    "FROM object_copies c JOIN objects o ON o.name=c.name WHERE o.archive_id=? AND c.group_id=? AND c.destination_id=? AND c.state<>'deleted'"),
                    mode,
                    id,
                    group,
                    physical
                );
                database.Execute("DELETE FROM object_copies WHERE group_id=? AND destination_id=? AND name IN(SELECT name FROM objects WHERE archive_id=?)", group, physical, id);
                database.Execute("UPDATE placements SET status='detached',stored=0,reserved=0 WHERE archive_id=? AND group_id=? AND destination_id=?", id, group, physical);
                database.Execute("UPDATE archives SET status='repair-required' WHERE id=? AND status<>'cleaned'", id);
            }

            if (single)
            {
                database.Execute(
                    ("INSERT OR IGNORE INTO warehouse_detached(name,destination_id,group_id,archive_id,mode,state) SELECT " +
                    "name,destination_id,?,archive_id,?,'pending' FROM retired_objects WHERE destination_id=?"),
                    groupId,
                    mode,
                    destination
                );
                database.Execute("DELETE FROM retired_objects WHERE destination_id=?", destination);
            }
        }

        public IList<RetiredObject> GetDetachedObjects(bool pendingDeletionOnly = true)
        {
            lock (gate)
                return database.Query(
                    ("SELECT d.archive_id,d.destination_id,o.name,o.size,o.sha256,d.group_id FROM warehouse_detached d JOIN objects o ON " +
                    "o.name=d.name WHERE (?=0 OR d.mode='delete') AND d.state='pending'"),
                    pendingDeletionOnly ? 1 : 0
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

        public void DetachedObjectDeleted(string name, string destination)
        {
            Transaction(delegate
            {
                if (Scalar("SELECT count(*) FROM warehouse_detached WHERE name=? AND destination_id=? AND mode='delete' AND state='pending'", name, destination) == 0)
                    return 0;
                database.Execute("UPDATE destinations SET used_bytes=used_bytes-(SELECT size FROM objects WHERE name=?) WHERE id=?", name, destination);
                database.Execute("UPDATE warehouse_detached SET state='deleted',error='' WHERE name=? AND destination_id=?", name, destination);
                var item = database.Query("SELECT size,archive_id FROM objects WHERE name=?", name);
                if (item.Count > 0)
                    RepositoryObjectCommitted(destination, name, (long)item[0][0], (long)item[0][1], false);
                return 0;
            });
        }

        public void DetachedObjectError(string name, string destination, string error)
        {
            lock (gate)
                database.Execute("UPDATE warehouse_detached SET error=? WHERE name=? AND destination_id=?", error, name, destination);
        }

        public IList<FileHistoryRow> GetArchivedFiles(long id)
        {
            lock (gate)
                return database.Query(("SELECT a.id,a.task_id,f.path,f.source_path,f.size,f.modified,a.name,f.state,a.status,f.entry_path FROM archive_files f " +
                    "JOIN archives a ON a.id=f.archive_id WHERE a.id=?"), id)
                    .Select(r => new FileHistoryRow {
                    ArchiveId = (long)r[0],
                    TaskId = (string)r[1],
                    Path = (string)r[2],
                    SourcePath = (string)r[3],
                    Size = (long)r[4],
                    Modified = (long)r[5],
                    ArchiveName = (string)r[6],
                    FileState = (string)r[7],
                    ArchiveStatus = (string)r[8],
                    EntryPath = (string)r[9]
                })
                    .ToList();
        }
    }
}
