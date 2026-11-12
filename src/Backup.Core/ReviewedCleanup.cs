using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace Backup.Core
{
    public sealed partial class BackupCatalog
    {
        public ISet<long> ObsoleteOwnedArchiveIds(ISet<string> tasks=null)
        {
            lock(gate)return new HashSet<long>(database.Query("SELECT a.id,a.task_id FROM archives a WHERE "+NodeGarbageScope+" AND a.status NOT IN ('writing','failed','damaged','cleaned') AND NOT EXISTS(SELECT 1 FROM archive_files f WHERE f.archive_id=a.id AND f.state='current') AND (EXISTS(SELECT 1 FROM objects o JOIN object_copies c ON c.name=o.name WHERE o.archive_id=a.id AND c.state<>'deleted') OR EXISTS(SELECT 1 FROM retired_objects r WHERE r.archive_id=a.id) OR EXISTS(SELECT 1 FROM warehouse_detached d WHERE d.archive_id=a.id AND d.state='pending') OR EXISTS(SELECT 1 FROM objects o JOIN volume_transfers t ON t.name=o.name WHERE o.archive_id=a.id) OR EXISTS(SELECT 1 FROM objects o JOIN upload_temporaries t ON t.name=o.name WHERE o.archive_id=a.id) OR EXISTS(SELECT 1 FROM archive_cleanup c WHERE c.archive_id=a.id AND c.state<>'complete'))").Where(row=>tasks==null||tasks.Contains((string)row[1])).Select(row=>(long)row[0]));
        }
        public IDisposable AcquireReviewedCleanup(long id)
        {
            string token="review-cleanup:"+Guid.NewGuid().ToString("N");
            Transaction(delegate{
                if(Scalar("SELECT count(*) FROM archives a WHERE a.id=? AND "+NodeGarbageScope,id)==0)throw new IOException("数据包只能在完整性检查确认所有备份版本引用后回收。");
                if(ArchiveHasCurrentFiles(id)||GetArchive(id).Status=="writing"||IsCompactionArchive(id)||GetReplicaStages(id).Count>0||Scalar("SELECT count(*) FROM archive_leases WHERE archive_id=?",id)>0||Scalar("SELECT count(*) FROM warehouse_move_objects o JOIN objects f ON f.name=o.name JOIN warehouse_moves m ON m.destination_id=o.destination_id WHERE f.archive_id=? AND m.state<>'complete'",id)>0)throw new IOException("压缩包仍有有效文件、正在使用或迁移，未执行旧包清理。");
                CompactionLease(id,token);return 0;
            });return new CatalogLease(delegate{lock(gate)database.Execute("DELETE FROM archive_leases WHERE token=?",token+":"+id);});
        }
        public void FinishReviewedCleanup(long id)
        {
            Transaction(delegate{
                if(GetCopies(id).Count>0||GetRetiredObjects(id).Count>0||GetDetachedObjects(false).Any(item=>item.ArchiveId==id)||PendingVolumeTransfers().Any(item=>item.ArchiveId==id)||PendingUploadStages().Any(item=>item.ArchiveId==id))throw new IOException("压缩包仍有未完成的删除结果，已保留重试记录。");
                database.Execute("DELETE FROM archive_holds WHERE archive_id=?",id);database.Execute("DELETE FROM catalog_coverage WHERE archive_id=?",id);
                database.Execute("UPDATE archive_files SET state='expired' WHERE archive_id=? AND state='current'",id);
                database.Execute("UPDATE placements SET status='cleaned',reserved=0,stored=0 WHERE archive_id=? AND (status<>'cleaned' OR reserved<>0 OR stored<>0)",id);
                database.Execute("UPDATE archives SET status='cleaned' WHERE id=? AND status<>'cleaned'",id);
                database.Execute("INSERT OR IGNORE INTO archive_cleanup(archive_id,state,error) VALUES(?,'complete','')",id);database.Execute("UPDATE archive_cleanup SET state='complete',error='' WHERE archive_id=? AND (state<>'complete' OR error<>'')",id);
                RepositoryStatusChanged(CatalogChange.Archives,GetArchive(id).TaskId);return 0;
            });
        }
    }
    public sealed partial class ArchiveMaintenance
    {
        // The explicit user policy removes obsolete versions without testing their
        // health. Every deletion is nevertheless confirmed before releasing state.
        public IList<string> DeleteReviewedObsoleteArchive(long id)
        {
            var errors=new List<string>();if(catalog.GetArchive(id).Status=="cleaned")return errors;
            using(BeginOperation())using(catalog.AcquireReviewedCleanup(id)){
                var copies=catalog.GetCleanupManifestCopies(id).Concat(catalog.GetDetachedObjects(false).Where(item=>item.ArchiveId==id).Select(item=>new StoredCopy{DestinationId=item.DestinationId,GroupId=item.GroupId,Object=item.Object})).GroupBy(item=>item.DestinationId+"\0"+item.Object.Name).Select(group=>group.First()).ToList();
                var transfers=catalog.PendingVolumeTransfers().Where(item=>item.ArchiveId==id).ToList();var readable=new HashSet<string>();
                foreach(var copy in copies)try{
                    if(Control!=null)Control.Checkpoint(false);var target=Open(copy.DestinationId);if(!readable.Contains(copy.DestinationId)){var probe=target as IReadOnlyDestinationProbe;if(probe!=null)probe.CheckReadableRoot();readable.Add(copy.DestinationId);}
                    if(Report!=null)Report("正在删除旧版或已失效压缩包："+copy.Object.Name);
                    target.DeleteObject(copy.Object.Name);if(target.Stat(copy.Object.Name).Exists)throw new IOException("远程删除尚未生效。");target.DeleteTemporary(copy.Object.Name);
                    catalog.ClearAbsentInspectedRecord(copy.Object.Name,copy.DestinationId);
                    foreach(var pending in transfers.Where(item=>item.Object.Name==copy.Object.Name&&item.Placement.DestinationId==copy.DestinationId))catalog.ResolveVolumeTransfer(pending,false);
                }catch(OperationCanceledException){throw;}catch(Exception error){if(!(error is IOException)&&!(error is UnauthorizedAccessException)&&!(error is NotSupportedException))throw;errors.Add(copy.Object.Name+" → "+copy.DestinationId+"："+error.Message);}
                if(errors.Count==0)catalog.FinishReviewedCleanup(id);
            }return errors;
        }
    }
}
