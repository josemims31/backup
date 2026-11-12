using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Backup.Core
{
    public sealed class ArchiveCompactionSession : IDisposable
    {
        internal BackupCatalog Catalog;
        internal readonly string Token="compaction:"+Guid.NewGuid().ToString("N");
        internal string TaskId;
        internal List<long> OldIds,NewIds=new List<long>();
        internal bool Published;
        public IList<long> NewArchives {get{return NewIds.AsReadOnly();}}
        public void Publish(ISet<string> deletedPaths,StorageTopology policy,int copies)
        {Catalog.PublishCompaction(this,deletedPaths,policy,copies);}
        public void Dispose(){var value=Catalog;Catalog=null;if(value!=null)value.EndCompaction(this);}
    }
    public sealed partial class BackupCatalog
    {
        private ArchiveCompactionSession compaction;
        private void CompactionLease(long id,string token)
        {
            using(var process=Process.GetCurrentProcess())database.Execute("INSERT INTO archive_leases(archive_id,token,process_id,process_start,read_only) VALUES(?,?,?,?,0)",id,token+":"+id,process.Id,process.StartTime.ToUniversalTime().Ticks);
        }
        public IList<FileHistoryRow> GetTaskArchiveFiles(string taskId)
        {
            lock(gate)return database.Query("SELECT a.id,a.name,a.status,f.path,f.source_path,f.entry_path,f.size,f.modified,f.state FROM archives a JOIN archive_files f ON f.archive_id=a.id WHERE a.task_id=? AND a.status<>'cleaned' AND f.state<>'staged' ORDER BY a.id,f.path",taskId)
                .Select(r=>new FileHistoryRow{TaskId=taskId,ArchiveId=(long)r[0],ArchiveName=(string)r[1],ArchiveStatus=(string)r[2],Path=(string)r[3],SourcePath=(string)r[4],EntryPath=(string)r[5],Size=(long)r[6],Modified=(long)r[7],FileState=(string)r[8]}).ToList();
        }
        public bool IsCompactionArchive(long id)
        {lock(gate)return Scalar("SELECT count(*) FROM archive_holds WHERE archive_id=? AND reason LIKE 'compaction-new:%'",id)>0;}
        public ISet<long> PendingCompactionRetirements(string taskId)
        {lock(gate)return new HashSet<long>(database.Query("SELECT archive_id FROM archive_holds WHERE reason=?","compaction-retire:"+taskId).Select(r=>(long)r[0]));}
        public ArchiveCompactionSession BeginCompaction(string taskId,IEnumerable<long> archiveIds)
        {
            return Transaction(delegate{
                if(compaction!=null)throw new IOException("文件整合正在进行。");
                var session=new ArchiveCompactionSession{Catalog=this,TaskId=taskId,OldIds=archiveIds.Distinct().ToList()};
                foreach(long id in session.OldIds)
                {
                    if(Scalar("SELECT count(*) FROM archives WHERE id=? AND task_id=? AND status NOT IN ('writing','cleaned')",id,taskId)!=1||HasOutstandingTransfer(id)
                        ||Scalar("SELECT count(*) FROM archive_leases WHERE archive_id=?",id)>0||Scalar("SELECT count(*) FROM replica_stages WHERE archive_id=?",id)>0
                        ||Scalar("SELECT count(*) FROM archive_holds WHERE archive_id=? AND reason NOT IN (?,?)",id,"compaction-retire:"+taskId,"version-retire:"+taskId)>0
                        ||Scalar("SELECT count(*) FROM placements WHERE archive_id=? AND (reserved>0 OR status IN ('writing','reconcile-required'))",id)>0
                        ||Scalar("SELECT count(*) FROM objects o JOIN upload_temporaries t ON t.name=o.name WHERE o.archive_id=?",id)>0
                        ||Scalar("SELECT count(*) FROM objects o JOIN warehouse_move_objects m ON m.name=o.name JOIN warehouse_moves w ON w.destination_id=m.destination_id WHERE o.archive_id=? AND w.state<>'complete'",id)>0)
                        throw new IOException("压缩包仍在使用、传输或保留中，请先处理后重试：#"+id);
                    database.Execute("INSERT OR IGNORE INTO archive_holds(archive_id,reason) VALUES(?,?)",id,"compaction-old:"+session.Token);
                    CompactionLease(id,session.Token);
                }
                compaction=session;return session;
            });
        }
        // Called inside the same reservation transaction, before any upload can start.
        private void StageCompactionArchive(long id,string taskId)
        {
            if(compaction==null||compaction.TaskId!=taskId)return;
            database.Execute("UPDATE archive_files SET state='staged' WHERE archive_id=?",id);
            database.Execute("INSERT INTO archive_holds(archive_id,reason) VALUES(?,?)",id,"compaction-new:"+compaction.Token);
            CompactionLease(id,compaction.Token);compaction.NewIds.Add(id);
        }
        internal void PublishCompaction(ArchiveCompactionSession session,ISet<string> deletedPaths,StorageTopology policy,int copies)
        {
            Transaction(delegate{
                if(compaction!=session||session.Published)throw new InvalidOperationException("整合会话已结束。");RepositoryStatusChanged(CatalogChange.Archives,session.TaskId);
                var newPaths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(long id in session.NewIds)
                {
                    var archive=GetArchive(id);
                    if(archive.Status!="complete"||archive.CurrentValidCopies<archive.TargetCopies||!CoversArchivePolicy(id,policy,copies)||HasOutstandingTransfer(id))throw new IOException("新压缩包未完整上传，旧包保留："+archive.ArchiveName);
                    foreach(var row in database.Query("SELECT path FROM archive_files WHERE archive_id=? AND state='staged'",id))newPaths.Add((string)row[0]);
                }
                var oldIds=new HashSet<long>(session.OldIds);
                foreach(var file in GetLatestFileViews(session.TaskId).Where(f=>oldIds.Contains(f.ArchiveId)))
                    if(!deletedPaths.Contains(file.ArchivePath)&&!newPaths.Contains(file.ArchivePath))throw new IOException("有效文件尚未重新备份，旧包保留："+file.ArchivePath);
                foreach(long id in session.NewIds){database.Execute("UPDATE archive_files SET state='current' WHERE archive_id=? AND state='staged'",id);database.Execute("DELETE FROM archive_holds WHERE archive_id=? AND reason=?",id,"compaction-new:"+session.Token);}
                foreach(long id in session.NewIds)ExpireOlderVersions(id);
                foreach(long id in session.OldIds)
                {
                    var retiring=database.Query("SELECT path FROM archive_files WHERE archive_id=? AND state='current'",id);
                    database.ExecuteMany("UPDATE archive_files SET state=? WHERE archive_id=? AND path=?",retiring.Select(row=>new object[]{deletedPaths.Contains((string)row[0])?"deleted":"expired",id,row[0]}));
                    database.Execute("UPDATE archive_holds SET reason=? WHERE archive_id=?","compaction-retire:"+session.TaskId,id);
                }
                database.Execute("DELETE FROM archive_leases WHERE token LIKE ?",session.Token+":%");session.Published=true;compaction=null;return 0;
            });
        }
        internal void EndCompaction(ArchiveCompactionSession session)
        {
            if(session.Published)return;
            Transaction(delegate{
                foreach(long id in session.NewIds)
                {
                    database.Execute("UPDATE archive_files SET state='deleted' WHERE archive_id=?",id);
                    database.Execute("UPDATE archives SET status='failed' WHERE id=?",id);
                    database.Execute("INSERT OR REPLACE INTO archive_cleanup(archive_id,state,error) VALUES(?,'rollback','')",id);
                    database.Execute("DELETE FROM archive_holds WHERE archive_id=? AND reason=?",id,"compaction-new:"+session.Token);
                }
                foreach(long id in session.OldIds)database.Execute("DELETE FROM archive_holds WHERE archive_id=? AND reason=?",id,"compaction-old:"+session.Token);
                database.Execute("DELETE FROM archive_leases WHERE token LIKE ?",session.Token+":%");if(compaction==session)compaction=null;return 0;
            });
        }
        public void ReleaseCompactionRetirements(string taskId,IEnumerable<long> archiveIds)
        {Transaction(delegate{database.ExecuteMany("DELETE FROM archive_holds WHERE archive_id=? AND reason=?",archiveIds.Select(id=>new object[]{id,"compaction-retire:"+taskId}));return 0;});}
        private void RecoverInterruptedCompactions()
        {
            Transaction(delegate{
                foreach(var row in database.Query("SELECT h.archive_id,h.reason FROM archive_holds h WHERE (h.reason LIKE 'compaction-new:%' OR h.reason LIKE 'compaction-old:%') AND NOT EXISTS(SELECT 1 FROM archive_leases l WHERE l.archive_id=h.archive_id)"))
                {
                    long id=(long)row[0];if(((string)row[1]).StartsWith("compaction-new:",StringComparison.Ordinal))
                    {database.Execute("UPDATE archive_files SET state='deleted' WHERE archive_id=?",id);database.Execute("UPDATE archives SET status='failed' WHERE id=?",id);database.Execute("INSERT OR REPLACE INTO archive_cleanup(archive_id,state,error) VALUES(?,'rollback','')",id);}
                    database.Execute("DELETE FROM archive_holds WHERE archive_id=?",id);
                }
                return 0;
            });
        }
        public IDisposable AcquireCleanupLease(long id)
        {
            string token="cleanup:"+Guid.NewGuid().ToString("N");
            bool acquired=Transaction(delegate{if(Scalar("SELECT count(*) FROM archives a WHERE a.id=? AND "+NodeGarbageScope+" AND "+GarbageEligibility,id)==0)return false;database.Execute("INSERT OR REPLACE INTO archive_cleanup(archive_id,state,error) VALUES(?,coalesce((SELECT CASE WHEN state IN ('rollback','task-delete') THEN state ELSE 'pending' END FROM archive_cleanup WHERE archive_id=?),'pending'),'')",id,id);CompactionLease(id,token);return true;});
            return acquired?new CatalogLease(delegate{lock(gate)database.Execute("DELETE FROM archive_leases WHERE token=?",token+":"+id);}):null;
        }
    }
}
