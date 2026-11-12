using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace Backup.Core
{
    public sealed class ArchiveContentMismatchException:IOException
    {public ArchiveContentMismatchException(string name):base("压缩包内容校验失败："+name){}}
    public sealed partial class BackupCatalog
    {
        public IDictionary<string,string> IntegrityCopyStates(long id)
        {lock(gate)return database.Query("SELECT c.destination_id,c.name,c.state FROM object_copies c JOIN objects o ON o.name=c.name WHERE o.archive_id=? AND c.state<>'deleted'",id).ToDictionary(row=>(string)row[0]+"\0"+(string)row[1],row=>(string)row[2]);}
        public void RecordInspectedArchiveProblem(long id)
        {Transaction(delegate{database.Execute("UPDATE archives SET status='repair-required' WHERE id=? AND status NOT IN ('writing','cleaned','failed','damaged','repair-required') AND NOT EXISTS(SELECT 1 FROM archive_leases WHERE archive_id=?)",id,id);RepositoryStatusChanged(CatalogChange.Archives,GetArchive(id).TaskId);return 0;});}
        // Metadata can correct an observation after repair, but cannot disprove a
        // content failure discovered while restoring the same immutable object.
        private string ObservedCopyState(IntegrityFinding finding,string previous)
        {
            finding.RecordedStatus=previous;finding.ObservedStatus=finding.Status;
            if(previous=="content-damaged"&&!finding.ContentVerified){
                if(finding.Status=="present-size-matched")finding.Status="content-damaged";if(!finding.Status.StartsWith("unavailable",StringComparison.Ordinal)){finding.RequiresManualRepairConfirmation=true;finding.RepairAction="replace";}return previous;
            }
            if(finding.Status=="present-size-matched"&&previous!="present"&&!finding.RepairApplied){
                if(previous=="missing"||previous=="size-or-name-mismatch"||previous!=null&&previous.StartsWith("unavailable",StringComparison.Ordinal)){
                    finding.RequiresManualRepairConfirmation=false;finding.RepairAction=null;
                }else{finding.RequiresManualRepairConfirmation=true;finding.RepairAction="correct-state";return previous;}
            }
            return finding.Status=="present-size-matched"?"present":finding.Status;
        }
        public void RecordDownloadedContentDamage(long id,string destination,string name)
        {
            Transaction(delegate{
                if(Scalar("SELECT count(*) FROM object_copies c JOIN objects o ON o.name=c.name WHERE o.archive_id=? AND c.name=? AND c.destination_id=? AND c.state<>'deleted'",id,name,destination)==0)return 0;
                database.Execute("UPDATE object_copies SET state='content-damaged' WHERE name=? AND destination_id=? AND state<>'content-damaged'",name,destination);
                database.Execute("UPDATE placements SET status='repair-required' WHERE archive_id=? AND destination_id=? AND status NOT IN ('writing','detached','cleaned','repair-required')",id,destination);
                database.Execute("UPDATE archives SET status='repair-required' WHERE id=? AND status IN ('complete','redundancy-shortfall')",id);
                RepositoryStatusChanged(CatalogChange.Archives,GetArchive(id).TaskId);RepositoryDestinationsChanged(new[]{destination});return 0;
            });
        }
        public void RecordDownloadedContentVerified(long id,string destination,ObjectRecord item)
        {
            lock(gate){var states=IntegrityCopyStates(id);string state;if(!states.TryGetValue(destination+"\0"+item.Name,out state)||state!="content-damaged")return;
                ApplyCheckedStates(id,new[]{new IntegrityFinding{ArchiveId=id,Destination=destination,Name=item.Name,ExpectedSize=item.Size,ActualSize=item.Size,Status="present-size-matched",ContentVerified=true,RepairApplied=true}});
            }
        }
        public bool CanRestoreInspectedCopy(long id,string name,string destination)
        {
            lock(gate){if(Scalar("SELECT count(*) FROM objects WHERE archive_id=? AND name=?",id,name)==0||HasOutstandingTransfer(id)||IsCompactionArchive(id)||GetArchive(id).Status=="cleaned")return false;
                return Scalar("SELECT count(*) FROM placements p WHERE p.archive_id=? AND p.destination_id=? AND p.status NOT IN ('detached','retired','cleaned') AND (NOT EXISTS(SELECT 1 FROM archive_pools WHERE archive_id=p.archive_id) OR EXISTS(SELECT 1 FROM archive_pool_members m JOIN archive_pools a ON a.archive_id=m.archive_id AND a.pool_idx=m.pool_idx WHERE m.archive_id=p.archive_id AND m.destination_id=p.destination_id AND a.group_id=p.group_id))",id,destination)>0;
            }
        }
        public bool RestoreInspectedCopy(long id,string name,string destination)
        {
            return Transaction(delegate{
                if(!CanRestoreInspectedCopy(id,name,destination))return false;
                if(Scalar("SELECT count(*) FROM object_copies WHERE name=? AND destination_id=? AND state<>'deleted'",name,destination)>0)return true;
                var rows=database.Query("SELECT p.group_id,coalesce((SELECT min(a.pool_idx) FROM archive_pools a JOIN archive_pool_members m ON m.archive_id=a.archive_id AND m.pool_idx=a.pool_idx WHERE a.archive_id=p.archive_id AND a.group_id=p.group_id AND m.destination_id=p.destination_id),-1) FROM placements p WHERE p.archive_id=? AND p.destination_id=? AND p.status NOT IN ('detached','retired','cleaned') ORDER BY p.group_id",id,destination);
                if(rows.Count==0)return false;long size=GetObjects(id).Single(item=>item.Name==name).Size;string group=(string)rows[0][0];
                database.Execute("INSERT OR REPLACE INTO object_copies(name,destination_id,group_id,state,pool_idx) VALUES(?,?,?,'present',?)",name,destination,group,rows[0][1]);
                database.Execute("UPDATE destinations SET used_bytes=used_bytes+? WHERE id=?",size,destination);
                database.Execute("UPDATE placements SET stored=stored+?,status=CASE WHEN reserved>0 THEN status ELSE 'complete' END WHERE archive_id=? AND group_id=? AND destination_id=?",size,id,group,destination);
                RepositoryStatusChanged(CatalogChange.Archives,GetArchive(id).TaskId);RepositoryObjectCommitted(destination,name,size,id,true);return true;
            });
        }
        public void ApplyCheckedStates(long id,IList<IntegrityFinding> findings)
        {
            foreach(var finding in findings)finding.RepairApplied=true;
            if(InterruptedArchives().Any(item=>item.ArchiveId==id)){RecordInterruptedIntegrity(id,findings);return;}
            foreach(var group in GetCopies(id).GroupBy(copy=>copy.GroupId)){
                var states=IntegrityCopyStates(id);var observed=findings.ToDictionary(item=>item.Destination+"\0"+item.Name);
                var merged=group.Select(copy=>{IntegrityFinding value;if(observed.TryGetValue(copy.DestinationId+"\0"+copy.Object.Name,out value))return value;string state=states[copy.DestinationId+"\0"+copy.Object.Name];return new IntegrityFinding{ArchiveId=id,Destination=copy.DestinationId,Name=copy.Object.Name,ExpectedSize=copy.Object.Size,ActualSize=state=="present"?copy.Object.Size:-1,Status=state=="present"?"present-size-matched":state,RequiresManualRepairConfirmation=state=="missing"||state=="size-or-name-mismatch"||state=="content-damaged",RepairApplied=true};}).ToList();
                RecordIntegrity(id,group.Key,merged);
            }
        }
        public void PrepareInterruptedRepack(long id)
        {
            Transaction(delegate{
                var archive=GetArchive(id);if(!InterruptedArchives().Any(item=>item.ArchiveId==id)||HasOutstandingTransfer(id)||PendingUploadStages().Any(item=>item.ArchiveId==id))return 0;
                database.Execute("UPDATE archive_files SET state='current' WHERE archive_id=? AND state='interrupted' AND NOT EXISTS(SELECT 1 FROM archive_files n JOIN archives a ON a.id=n.archive_id WHERE a.task_id=? AND n.path=archive_files.path AND a.id<>? AND n.state='current' AND a.status<>'cleaned')",id,archive.TaskId,id);
                database.Execute("DELETE FROM archive_holds WHERE archive_id=? AND reason='interrupted-awaiting-inspection'",id);
                database.Execute("UPDATE archives SET status='repair-required' WHERE id=?",id);
                database.Execute("UPDATE placements SET status=CASE WHEN stored>0 THEN 'repair-required' ELSE 'failed' END WHERE archive_id=? AND reserved=0",id);
                RepositoryStatusChanged(CatalogChange.Archives,archive.TaskId);return 0;
            });
        }
        public bool HasReadableInspectedManifest(long id)
        {lock(gate)return HasCompleteArchiveManifest(id)&&Scalar("SELECT count(*) FROM objects o WHERE o.archive_id=? AND NOT EXISTS(SELECT 1 FROM object_copies c WHERE c.name=o.name AND c.state='present')",id)==0;}
        public void ClearAbsentInspectedRecord(string name,string destination)
        {
            ObjectRemoved(name,destination);Transaction(delegate{
                var detached=database.Query("SELECT archive_id FROM warehouse_detached WHERE name=? AND destination_id=? AND state='pending'",name,destination);
                if(detached.Count>0){long size=Scalar("SELECT size FROM objects WHERE name=?",name);database.Execute("UPDATE destinations SET used_bytes=MAX(0,used_bytes-?) WHERE id=?",size,destination);database.Execute("UPDATE warehouse_detached SET state='deleted',error='' WHERE name=? AND destination_id=?",name,destination);RepositoryObjectCommitted(destination,name,size,(long)detached[0][0],false);}return 0;
            });
        }
    }
}
