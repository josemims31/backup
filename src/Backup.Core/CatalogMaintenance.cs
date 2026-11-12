using System;
using System.Collections.Generic;
using System.Linq;

namespace Backup.Core
{
    public sealed partial class BackupCatalog
    {
        public bool ArchiveHasCurrentFiles(long id){lock(gate)return Scalar("SELECT count(*) FROM archive_files WHERE archive_id=? AND state='current'",id)>0;}
        // Retain recent history. Only already-cleaned archives with no live references
        // are pruned; pending uploads, holds, recovery coverage and migrations win.
        public int PruneCleanedHistory(ISet<long> archiveIds=null,ISet<long> protectedIds=null,string taskId=null)
        {
            return Transaction(delegate
            {
                string scope="";
                if(archiveIds!=null){if(archiveIds.Count==0)return 0;scope+=" AND a.id IN ("+string.Join(",",archiveIds.Select(id=>id.ToString(System.Globalization.CultureInfo.InvariantCulture)))+")";}
                if(protectedIds!=null&&protectedIds.Count>0)scope+=" AND a.id NOT IN ("+string.Join(",",protectedIds.Select(id=>id.ToString(System.Globalization.CultureInfo.InvariantCulture)))+")";
                var ids=database.Query("SELECT a.id FROM archives a JOIN archive_cleanup c ON c.archive_id=a.id WHERE (? IS NULL OR a.task_id=?)"+scope+" AND a.status='cleaned' AND NOT EXISTS(SELECT 1 FROM backup_nodes n WHERE n.archive_id=a.id) AND NOT EXISTS(SELECT 1 FROM node_revisions n WHERE n.archive_id=a.id) AND c.state='complete' AND a.created_utc<? AND a.id NOT IN(SELECT id FROM archives WHERE status='cleaned' ORDER BY id DESC LIMIT 100) AND NOT EXISTS(SELECT 1 FROM archive_holds h WHERE h.archive_id=a.id) AND NOT EXISTS(SELECT 1 FROM archive_leases l WHERE l.archive_id=a.id) AND NOT EXISTS(SELECT 1 FROM catalog_coverage r WHERE r.archive_id=a.id) AND NOT EXISTS(SELECT 1 FROM placements p WHERE p.archive_id=a.id AND (p.reserved>0 OR p.stored>0)) AND NOT EXISTS(SELECT 1 FROM objects o JOIN object_copies p ON p.name=o.name WHERE o.archive_id=a.id AND p.state<>'deleted') AND NOT EXISTS(SELECT 1 FROM objects o JOIN volume_transfers t ON t.name=o.name WHERE o.archive_id=a.id) AND NOT EXISTS(SELECT 1 FROM objects o JOIN upload_temporaries t ON t.name=o.name WHERE o.archive_id=a.id) AND NOT EXISTS(SELECT 1 FROM replica_stages r WHERE r.archive_id=a.id) AND NOT EXISTS(SELECT 1 FROM retired_objects r WHERE r.archive_id=a.id) AND NOT EXISTS(SELECT 1 FROM warehouse_detached d WHERE d.archive_id=a.id) AND NOT EXISTS(SELECT 1 FROM objects o JOIN warehouse_move_objects m ON m.name=o.name WHERE o.archive_id=a.id) ORDER BY a.id LIMIT 100",taskId,taskId,DateTime.UtcNow.AddDays(-30).ToString("o")).Select(row=>(long)row[0]).ToList();
                foreach(long id in ids)
                {
                    database.Execute("DELETE FROM object_copies WHERE name IN(SELECT name FROM objects WHERE archive_id=?)",id);
                    database.Execute("DELETE FROM retirement_archives WHERE archive_id=?",id);
                    database.Execute("DELETE FROM archive_pool_members WHERE archive_id=?",id);
                    database.Execute("DELETE FROM archive_pools WHERE archive_id=?",id);
                    database.Execute("DELETE FROM objects WHERE archive_id=?",id);
                    database.Execute("DELETE FROM placements WHERE archive_id=?",id);
                    database.Execute("DELETE FROM archive_files WHERE archive_id=?",id);
                    database.Execute("DELETE FROM archive_cleanup WHERE archive_id=?",id);
                    database.Execute("DELETE FROM node_archive_seals WHERE archive_id=?",id);
                    database.Execute("DELETE FROM archives WHERE id=?",id);
                }
                // AUTOINCREMENT is intentionally not reset, including an empty catalog.
                return ids.Count;
            });
        }
    }
}
