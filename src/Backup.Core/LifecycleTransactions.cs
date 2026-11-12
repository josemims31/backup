using System;
using System.Collections.Generic;
using System.Linq;
namespace Backup.Core
{
    public sealed class FileReference {public string TaskId,Path;}
    public sealed partial class BackupCatalog
    {
        private void SaveWorkspaceUnsafe(string json)
        {SaveSettingUnsafe("workspace_v3",json);}
        public void RemoveTaskAndSave(string taskId,bool keep,string json)
        {RemoveTaskAndSave(taskId,keep,json,null);}
        public void RemoveTaskAndSave(string taskId,bool keep,string json,string schedulesJson)
        {RemoveTaskAndSave(taskId,keep,json,schedulesJson,null);}
        public void RemoveTaskAndSave(string taskId,bool keep,string json,string schedulesJson,string repairsJson)
        {Transaction(delegate{RepositoryStatusChanged(CatalogChange.Archives,taskId);database.Execute("DELETE FROM archive_holds WHERE archive_id IN(SELECT id FROM archives WHERE task_id=?) AND reason IN (?,?)",taskId,"version-retire:"+taskId,"compaction-retire:"+taskId);database.Execute("UPDATE archive_files SET state='deleted' WHERE archive_id IN(SELECT id FROM archives WHERE task_id=?)",taskId);if(keep)database.Execute("INSERT OR IGNORE INTO archive_holds(archive_id,reason) SELECT id,'task-deleted-keep' FROM archives WHERE task_id=? AND status<>'cleaned'",taskId);else{database.Execute("DELETE FROM archive_holds WHERE reason='interrupted-awaiting-inspection' AND archive_id IN(SELECT id FROM archives WHERE task_id=?)",taskId);database.Execute("INSERT OR REPLACE INTO archive_cleanup(archive_id,state,error) SELECT id,'task-delete','' FROM archives WHERE task_id=? AND status IN ('failed','damaged')",taskId);}SaveWorkspaceUnsafe(json);if(repairsJson!=null)SaveSettingUnsafe("confirmed_repairs_v1",repairsJson);if(schedulesJson!=null)SaveSettingUnsafe("backup_schedules_v1",schedulesJson);return 0;});}
        public void SaveWorkspaceMaintenance(string workspace,string schedules,string repairs)
        {Transaction(delegate{SaveWorkspaceUnsafe(workspace);if(schedules!=null)SaveSettingUnsafe("backup_schedules_v1",schedules);SaveSettingUnsafe("confirmed_repairs_v1",repairs);return 0;});}
        public void InvalidatePathsAndSave(IList<FileReference> files,string json)
        {Transaction(delegate{foreach(string taskId in files.Select(f=>f.TaskId).Distinct())RepositoryStatusChanged(CatalogChange.Archives,taskId);if(files.Count>0)database.ExecuteMany("UPDATE archive_files SET state='deleted' WHERE path=? AND archive_id IN(SELECT id FROM archives WHERE task_id=?) AND state IN ('current','expired')",files.Select(file=>new object[]{file.Path,file.TaskId}));SaveWorkspaceUnsafe(json);return 0;});}
        public void RetirementWorkspaceSaved(string json){Transaction(delegate{SaveWorkspaceUnsafe(json);return 0;});}
        public bool HasOutstandingTransfer(long id)
        {lock(gate)return Scalar("SELECT count(*) FROM placements p WHERE p.archive_id=? AND (p.reserved>0 OR p.status IN ('writing','reconcile-required')) AND NOT EXISTS(SELECT 1 FROM replica_stages s WHERE s.shadow_group=p.group_id)",id)>0;}
        public void AdoptReplicaObject(ArchivePlan stage,ObjectRecord item)
        {Transaction(delegate{
            string destination=stage.Placements[0].DestinationId,shadow=stage.Placements[0].GroupId;var rows=database.Query("SELECT group_id FROM object_copies WHERE name=? AND destination_id=? AND state<>'deleted'",item.Name,destination);
            if(rows.Count==0)throw new InvalidOperationException("No registered copy to adopt.");string old=(string)rows[0][0];if(old==shadow)return 0;
            database.Execute("UPDATE placements SET stored=stored-? WHERE archive_id=? AND group_id=? AND destination_id=?",item.Size,stage.ArchiveId,old,destination);database.Execute("UPDATE placements SET stored=stored+? WHERE archive_id=? AND group_id=?",item.Size,stage.ArchiveId,shadow);database.Execute("UPDATE object_copies SET group_id=?,state='present' WHERE name=? AND destination_id=?",shadow,item.Name,destination);return 0;
        });}
        public void ReserveReplacementObject(ArchivePlan stage,ObjectRecord item)
        {Transaction(delegate{
            string destination=stage.Placements[0].DestinationId;var rows=database.Query("SELECT group_id FROM object_copies WHERE name=? AND destination_id=? AND state<>'deleted'",item.Name,destination);if(rows.Count==0)return 0;
            database.Execute("UPDATE destinations SET used_bytes=used_bytes-? WHERE id=?",item.Size,destination);database.Execute("UPDATE placements SET stored=stored-? WHERE archive_id=? AND group_id=? AND destination_id=?",item.Size,stage.ArchiveId,rows[0][0],destination);database.Execute("UPDATE object_copies SET state='deleted' WHERE name=? AND destination_id=?",item.Name,destination);database.Execute("UPDATE placements SET reserved=reserved+? WHERE archive_id=? AND group_id=?",item.Size,stage.ArchiveId,stage.Placements[0].GroupId);return 0;
        });}
    }
}
