using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
namespace Backup.Core
{
    public sealed partial class BackupCatalog
    {
        // Progress metadata only. The deletion list is always reread after acquiring
        // the archive cleanup lease; this count does not grant ownership of any bytes.
        internal int CountCleanupObjects(IList<long> archiveIds,ISet<string> destinations,string groupId)
        {
            lock(gate){int count=0;
                for(int offset=0;offset<archiveIds.Count;offset+=256){
                    string ids=string.Join(",",archiveIds.Skip(offset).Take(256).Select(id=>id.ToString(CultureInfo.InvariantCulture)));
                    string sql="SELECT destination_id,group_id,sum(total) FROM (SELECT c.destination_id,c.group_id,count(*) AS total FROM object_copies c JOIN objects o ON c.name=o.name WHERE o.archive_id IN ("+ids+") AND c.state<>'deleted' GROUP BY c.destination_id,c.group_id UNION ALL SELECT r.destination_id,r.group_id,count(*) AS total FROM retired_objects r JOIN objects o ON o.name=r.name WHERE r.archive_id IN ("+ids+") GROUP BY r.destination_id,r.group_id) GROUP BY destination_id,group_id";
                    foreach(var row in database.StreamQuery(sql))if((destinations==null||destinations.Contains((string)row[0]))&&(groupId==null||groupId==(string)row[1]))count=checked(count+(int)(long)row[2]);
                }
                return count;
            }
        }
        internal bool HasCleanupObjects(long id)
        {lock(gate)return Scalar("SELECT EXISTS(SELECT 1 FROM object_copies c JOIN objects o ON o.name=c.name WHERE o.archive_id=? AND c.state<>'deleted') OR EXISTS(SELECT 1 FROM retired_objects WHERE archive_id=?)",id,id)>0;}
    }
}
