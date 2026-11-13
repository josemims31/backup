using System;
using System.IO;
using System.Linq;

namespace Backup.Core
{
    public sealed partial class BackupCatalog
    {
        public bool CanRepackArchive(long id)
        {
            lock (gate)
                return Scalar(
                    ("SELECT count(*) FROM archives a WHERE a.id=? AND a.status='complete' AND NOT EXISTS(SELECT 1 FROM archive_leases l WHERE l.archive_id=a.id) AND NOT " +
                    "EXISTS(SELECT 1 FROM replica_stages s WHERE s.archive_id=a.id) AND NOT EXISTS(SELECT 1 FROM archive_holds h WHERE h.archive_id=a.id AND h.reason NOT LIKE " +
                    "'version-retire:%' AND h.reason<>'task-deleted-keep') AND NOT EXISTS(SELECT 1 FROM placements p WHERE p.archive_id=a.id AND (p.reserved>0 OR " +
                    "p.status<>'complete')) AND NOT EXISTS(SELECT 1 FROM objects o JOIN volume_transfers t ON t.name=o.name WHERE o.archive_id=a.id) AND NOT EXISTS(SELECT 1 FROM " +
                    "objects o JOIN upload_temporaries t ON t.name=o.name WHERE o.archive_id=a.id) AND NOT EXISTS(SELECT 1 FROM objects o JOIN warehouse_move_objects m ON " +
                    "m.name=o.name JOIN warehouse_moves w ON w.destination_id=m.destination_id WHERE o.archive_id=a.id AND w.state<>'complete')"),
                    id
                ) == 1;
        }

        // New data is written under a temporary task identity. A crash before
        // publication therefore cannot make historical files current sources.
        public void AdoptRepackedData(long id, string taskId)
        {
            Transaction(delegate
            {
                var archive = GetArchive(id);
                if (!archive.TaskId.StartsWith("__repack__:", StringComparison.Ordinal) || archive.Status != "complete" || archive.CurrentValidCopies < archive.TargetCopies ||
                    HasOutstandingTransfer(id))
                    throw new IOException("整理后的数据包尚未完整上传，旧包保留。");
                database.Execute("UPDATE archives SET task_id=? WHERE id=?", taskId, id);
                database.Execute("UPDATE archive_files SET state='staged' WHERE archive_id=?", id);
                RepositoryStatusChanged(CatalogChange.Archives, taskId);
                return 0;
            });
        }

        public void StageRepackedArchive(long id)
        {
            lock (gate)
                database.Execute("UPDATE archive_files SET state='staged' WHERE archive_id=?", id);
        }
    }
}
