using System;
using System.Collections.Generic;
using System.Linq;

namespace Backup.Core
{
    public sealed partial class BackupCatalog
    {
        // Archives belong to exactly one task. With no remaining version of
        // that task, its data has no version references. Unregister it locally;
        // leave all physical files for explicit orphan cleanup during repair.
        public void UnregisterVersionlessTaskData(string taskId,long maximumArchive=long.MaxValue)
        {
            Transaction(delegate{
                if(TaskHasNodes(taskId)||taskId.StartsWith("__",StringComparison.Ordinal))return 0;
                foreach(long id in database.Query("SELECT id FROM archives a WHERE task_id=? AND id<=? AND status NOT IN ('writing','cleaned') AND NOT EXISTS(SELECT 1 FROM archive_leases l WHERE l.archive_id=a.id) AND NOT EXISTS(SELECT 1 FROM replica_stages s WHERE s.archive_id=a.id)",taskId,maximumArchive).Select(r=>(long)r[0]).ToList())
                {
                    if(IsCompactionArchive(id))continue;
                    UnregisterUnusedArchiveUnsafe(id,taskId);
                }
                return 0;
            });
        }
        private void UnregisterUnusedArchiveUnsafe(long id,string taskId)
        {
            database.Execute("UPDATE destinations SET used_bytes=used_bytes-coalesce((SELECT sum(p.stored) FROM placements p WHERE p.archive_id=? AND p.destination_id=destinations.id),0)-coalesce((SELECT sum(r.size) FROM retired_objects r WHERE r.archive_id=? AND r.destination_id=destinations.id),0)",id,id);
            foreach(string table in new[]{"volume_transfers","upload_temporaries","replica_intents","warehouse_move_objects"})database.Execute("DELETE FROM "+table+" WHERE name IN(SELECT name FROM objects WHERE archive_id=?)",id);
            database.Execute("DELETE FROM object_copies WHERE name IN(SELECT name FROM objects WHERE archive_id=?)",id);
            foreach(string table in new[]{"retired_objects","warehouse_detached","archive_holds","catalog_coverage","node_archive_seals"})database.Execute("DELETE FROM "+table+" WHERE archive_id=?",id);
            database.Execute("UPDATE archive_files SET state='expired' WHERE archive_id=? AND state<>'expired'",id);
            database.Execute("UPDATE placements SET stored=0,reserved=0,status='cleaned',error='' WHERE archive_id=?",id);
            database.Execute("UPDATE archives SET status='cleaned' WHERE id=?",id);
            database.Execute("INSERT OR REPLACE INTO archive_cleanup(archive_id,state,error) VALUES(?,'complete','')",id);
            RepositoryStatusChanged(CatalogChange.Archives,taskId);
        }
        // Rollback outputs, unadopted repacks and unpublished node databases
        // are not backup versions.
        // A published/local-version seal or any active writer/lease wins over cleanup.
        public void UnregisterAbandonedArchiveOutputs(ISet<string> tasks=null,ISet<long> activeArchives=null)
        {
            Transaction(delegate{
                var rows=database.Query("SELECT a.id,a.task_id FROM archives a WHERE a.status NOT IN ('writing','cleaned') AND (substr(a.task_id,1,11)='__repack__:' OR substr(a.task_id,1,9)='__node__:' OR EXISTS(SELECT 1 FROM archive_cleanup c WHERE c.archive_id=a.id AND c.state='rollback')) AND NOT EXISTS(SELECT 1 FROM archive_leases l WHERE l.archive_id=a.id) AND NOT EXISTS(SELECT 1 FROM replica_stages s WHERE s.archive_id=a.id) AND NOT EXISTS(SELECT 1 FROM node_archive_seals s WHERE s.archive_id=a.id) AND NOT EXISTS(SELECT 1 FROM node_revisions n WHERE n.archive_id=a.id) AND NOT EXISTS(SELECT 1 FROM backup_nodes n WHERE n.archive_id=a.id)");
                foreach(var row in rows){long id=(long)row[0];string owner=(string)row[1];if(owner.StartsWith("__repack__:",StringComparison.Ordinal))owner=owner.Substring(11);
                    if(tasks!=null&&!tasks.Contains(owner)||activeArchives!=null&&activeArchives.Contains(id)||IsCompactionArchive(id))continue;
                    UnregisterUnusedArchiveUnsafe(id,owner);
                }return 0;
            });
        }
        public void MarkLastVersionDeleted(string taskId)
        {
            Transaction(delegate{
                if(TaskHasNodes(taskId))return 0;
                long source=Scalar("SELECT max(id) FROM archives WHERE task_id=? AND status<>'writing'",taskId);
                database.Execute("INSERT OR IGNORE INTO local_backup_nodes(task_id,id,created_utc,revision,digest,bytes,source_archive,payload) VALUES(?,?,?,1,'',0,?,NULL)",taskId,Guid.NewGuid().ToString("N"),DateTime.UtcNow.ToString("o"),source);
                database.Execute("UPDATE local_backup_nodes SET source_archive=MAX(source_archive,?) WHERE task_id=?",source,taskId);return 0;
            });
            UnregisterVersionlessTaskData(taskId);
        }
        // Also upgrades local versions explicitly deleted with 0.14.20. Only
        // their recorded range is abandoned; newer, unfinished backups survive.
        public void UnregisterDeletedLocalNodeData(ISet<string> tasks=null)
        {
            List<object[]> rows;lock(gate)rows=database.Query("SELECT task_id,source_archive FROM local_backup_nodes WHERE payload IS NULL");
            foreach(var row in rows)if(tasks==null||tasks.Contains((string)row[0]))UnregisterVersionlessTaskData((string)row[0],(long)row[1]);
        }
    }
}
