using System;
using System.Collections.Generic;
using System.Linq;
namespace Backup.Core
{
    public sealed partial class BackupCatalog
    {
        private const string HoldRetiringSql="INSERT OR IGNORE INTO archive_holds(archive_id,reason) SELECT DISTINCT a.id,? FROM archives a JOIN archive_files f ON f.archive_id=a.id WHERE a.task_id=? AND f.state='current' AND (? IS NULL OR f.path=?) AND (? IS NULL OR a.id<? AND f.path IN(SELECT path FROM archive_files WHERE archive_id=?))";
        private void HoldRetiringVersions(string taskId,string path=null,long? replacement=null)
        {
            string reason="version-retire:"+taskId;
            database.Execute(HoldRetiringSql,reason,taskId,path,path,replacement,replacement,replacement);
        }
        private void HoldRetiringPaths(string taskId,IEnumerable<string> paths)
        {database.ExecuteMany(HoldRetiringSql,paths.Select(path=>new object[]{"version-retire:"+taskId,taskId,path,path,null,null,null}));}
        public void ReleasePublishedRetirements(string taskId)
        {lock(gate)database.Execute("DELETE FROM archive_holds WHERE reason IN (?,?)","version-retire:"+taskId,"compaction-retire:"+taskId);}
        public ISet<long> PendingVersionRetirements(string taskId)
        {lock(gate)return new HashSet<long>(database.Query("SELECT archive_id FROM archive_holds WHERE reason=?","version-retire:"+taskId).Select(row=>(long)row[0]));}
        public IList<long> PendingSnapshotRetirements(string taskId=null)
        {lock(gate)return database.Query("SELECT a.id FROM archives a JOIN archive_holds h ON h.archive_id=a.id WHERE (? IS NULL OR a.task_id=?) AND (h.reason LIKE 'version-retire:%' OR h.reason LIKE 'compaction-retire:%') AND NOT EXISTS(SELECT 1 FROM archive_files f WHERE f.archive_id=a.id AND f.state='current') ORDER BY a.id",taskId,taskId).Select(row=>(long)row[0]).ToList();}
    }
}
