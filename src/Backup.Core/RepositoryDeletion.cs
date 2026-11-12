using System;
using System.IO;
using System.Linq;
namespace Backup.Core
{
    public sealed partial class BackupCatalog
    {
        public IDisposable ProtectWarehouseDeletion(string destination,string name)
        {
            lock(gate)
            {
                if(Scalar("SELECT count(*) FROM upload_temporaries WHERE destination_id=? AND (name=? OR temporary_name=?)",destination,name,name)>0||Scalar("SELECT count(*) FROM warehouse_move_objects o JOIN warehouse_moves m ON m.destination_id=o.destination_id WHERE o.destination_id=? AND o.name=? AND m.state<>'complete'",destination,name)>0)throw new IOException("此文件正在上传或迁移，已保留。");
                var rows=database.Query("SELECT DISTINCT o.archive_id FROM objects o JOIN object_copies c ON c.name=o.name WHERE o.name=? AND c.destination_id=? AND c.state<>'deleted'",name,destination);if(rows.Count==0)return null;long id=(long)rows[0][0];if(HasOutstandingTransfer(id)||IsCompactionArchive(id))throw new IOException("此备份包正在上传、核对或整理，已保留。");return AcquireArchiveLease(id);
            }
        }
        public void RecordWarehouseDeletion(string destination,string name)
        {
            Transaction(delegate{
                var rows=database.Query("SELECT o.archive_id,c.group_id,a.task_id FROM object_copies c JOIN objects o ON o.name=c.name JOIN archives a ON a.id=o.archive_id WHERE c.destination_id=? AND c.name=? AND c.state<>'deleted'",destination,name);if(rows.Count==0)return 0;
                foreach(var row in rows){long id=(long)row[0];RepositoryStatusChanged(CatalogChange.Archives,(string)row[2]);RepositoryDestinationsChanged(new[]{destination});database.Execute("UPDATE object_copies SET state='missing' WHERE destination_id=? AND name=? AND state<>'missing'",destination,name);database.Execute("UPDATE placements SET status='repair-required' WHERE archive_id=? AND group_id=? AND destination_id=? AND status IN ('complete','unavailable','repair-required') AND status<>'repair-required'",id,row[1],destination);database.Execute("UPDATE archives SET status='repair-required' WHERE id=? AND status IN ('complete','redundancy-shortfall')",id);}return 0;
            });
        }
    }
}
