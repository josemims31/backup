using System;
using System.IO;
using System.Linq;

namespace Backup.Core
{
    public sealed partial class BackupCatalog
    {
        public PendingVolumeTransfer BeginDownloadedReplicaRepair(long id, string destination, ObjectRecord item)
        {
            return Transaction(delegate
            {
                var rows = database.Query(
                    ("SELECT c.group_id,c.pool_idx FROM object_copies c JOIN objects o ON o.name=c.name WHERE o.archive_id=? AND c.name=? AND " +
                    "c.destination_id=? AND c.state='content-damaged'"),
                    id,
                    item.Name,
                    destination
                );
                if (rows.Count == 0 || Scalar("SELECT count(*) FROM volume_transfers WHERE name=? AND destination_id=?", item.Name, destination) > 0)
                    return null;
                if (GetArchive(id).Status == "writing" || IsCompactionArchive(id) || Scalar("SELECT count(*) FROM archive_leases WHERE archive_id=? AND read_only=0", id) > 0)
                    throw new IOException("压缩包正在更新，暂不能覆盖损坏副本。");
                string group = (string)rows[0][0];
                int pool = (int)(long)rows[0][1];
                // The corrupt object's existing capacity remains charged until its
                // replacement is confirmed. No unowned temporary names are used.
                database.Execute("INSERT INTO volume_transfers(name,destination_id,pool_idx,group_id,state) VALUES(?,?,?,?,'pending-repair-writing')", item.Name, destination, pool, group);
                RepositoryStatusChanged(CatalogChange.Archives, GetArchive(id).TaskId);
                return new PendingVolumeTransfer
                {
                    ArchiveId = id,
                    Object = item,
                    Placement = new ArchivePlacement
                    {
                        DestinationId = destination,
                        GroupId = group
                    },
                    Pool = pool,
                    Phase = "repair-writing",
                    AwaitingConfirmation = true
                };
            });
        }
    }
}
