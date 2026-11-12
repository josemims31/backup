using System;
using System.Collections.Generic;
using System.Linq;
namespace Backup.Core
{
    public sealed class FileHistoryRow
    {
        public long ArchiveId,Size,Modified;
        public string TaskId,Path,SourcePath,EntryPath,ContentPath,ArchiveName,FileState,ArchiveStatus,GroupId,DestinationId,PlacementStatus;
        public string StoredEntryPath {get{return !string.IsNullOrEmpty(ContentPath)?ContentPath:!string.IsNullOrEmpty(EntryPath)?EntryPath:Path;}}
    }
    public sealed partial class BackupCatalog
    {
        private void InitializeTaskSchema(long version)
        {
            database.Transaction(delegate{
                database.Execute("CREATE TABLE IF NOT EXISTS group_children(group_id TEXT NOT NULL REFERENCES groups(id),child_id TEXT NOT NULL REFERENCES groups(id),PRIMARY KEY(group_id,child_id))");
                database.Execute("CREATE INDEX IF NOT EXISTS archives_task ON archives(task_id,id)");
                return 0;
            });
        }
        private void ExpireOlderVersions(long id)
        {
            string taskId=GetArchive(id).TaskId;if(taskId=="__catalog__"||IsCompactionArchive(id))return;
            HoldRetiringVersions(taskId,null,id);
            database.Execute("UPDATE archive_files SET state='expired' WHERE archive_id<? AND archive_id IN (SELECT id FROM archives WHERE task_id=(SELECT task_id FROM archives WHERE id=?)) AND path IN (SELECT path FROM archive_files WHERE archive_id=?) AND state='current'",id,id,id);
        }
        public void MarkDeleted(string taskId,string path,bool protectUntilSnapshot=false)
        {MarkDeletedPaths(taskId,new[]{path},protectUntilSnapshot);}
        public void MarkDeletedPaths(string taskId,IEnumerable<string> paths,bool protectUntilSnapshot=false)
        {
            var removed=paths.Distinct(StringComparer.Ordinal).ToList();if(removed.Count==0)return;
            Transaction(delegate{
                if(protectUntilSnapshot)HoldRetiringPaths(taskId,removed);
                database.ExecuteMany("UPDATE archive_files SET state='deleted' WHERE path=? AND archive_id IN (SELECT id FROM archives WHERE task_id=?) AND state='current'",removed.Select(path=>new object[]{path,taskId}));
                RepositoryStatusChanged(CatalogChange.Archives,taskId);return 0;
            });
        }
        public ISet<string> GetCompleteDestinations(long archiveId)
        {lock(gate)return new HashSet<string>(database.Query("SELECT destination_id FROM placements WHERE archive_id=? AND status='complete'",archiveId).Select(r=>(string)r[0]));}
        public IList<FileHistoryRow> GetKnownSourceFiles(string taskId)
        {lock(gate)return database.Query("SELECT f.path,f.source_path FROM archive_files f JOIN archives a ON a.id=f.archive_id WHERE a.task_id=? AND f.state='current' AND a.status IN ('complete','redundancy-shortfall','repair-required')",taskId)
            .Select(r=>new FileHistoryRow{Path=(string)r[0],SourcePath=(string)r[1]}).ToList();}
        public IList<StoredArchiveObject> GetWarehouseObjects(string destinationId,bool includeDeleted=false)
        {lock(gate)return ReadWarehouseObjects(destinationId,includeDeleted,null);}
        public IList<StoredArchiveObject> GetWarehouseObjectsByName(string destinationId,IEnumerable<string> objectNames,bool ignoreCase=false)
        {
            var names=objectNames.Distinct(StringComparer.Ordinal).ToList();var result=new List<StoredArchiveObject>();
            lock(gate){
                if(ignoreCase){var canonical=new HashSet<string>(StringComparer.Ordinal);for(int offset=0;offset<names.Count;offset+=200){var batch=names.Skip(offset).Take(200).ToList();foreach(var row in database.Query("SELECT name FROM objects WHERE name COLLATE NOCASE IN ("+string.Join(",",batch.Select(name=>"?"))+")",batch.Cast<object>().ToArray()))canonical.Add((string)row[0]);}names=canonical.ToList();}
                for(int offset=0;offset<names.Count;offset+=200)result.AddRange(ReadWarehouseObjects(destinationId,false,names.Skip(offset).Take(200).ToList()));
            }return result;
        }
        private IList<StoredArchiveObject> ReadWarehouseObjects(string destinationId,bool includeDeleted,IList<string> names)
        {
            string filter=names==null?"":" AND o.name IN ("+string.Join(",",names.Select(name=>"?"))+")";
            string sql="SELECT q.name,q.size,q.archive_id,coalesce(c.state,CASE WHEN t.state='failed-upload' THEN 'missing' WHEN t.state LIKE 'damaged-%' THEN 'damaged' WHEN t.state LIKE 'checked-%' THEN 'registration-mismatch' WHEN t.name IS NOT NULL AND p.status='unavailable' THEN 'unavailable' WHEN t.name IS NOT NULL THEN 'reconcile-required' ELSE 'retired' END),coalesce((SELECT group_concat(DISTINCT n.task_id) FROM node_revisions n WHERE n.archive_id=q.archive_id),a.task_id),a.status FROM (SELECT o.name,o.size,o.archive_id FROM objects o JOIN object_copies c ON c.name=o.name WHERE c.destination_id=? AND (?=1 OR c.state<>'deleted'){NAMES} UNION SELECT o.name,o.size,o.archive_id FROM objects o JOIN warehouse_detached d ON d.name=o.name WHERE d.destination_id=? AND d.state='pending'{NAMES} UNION SELECT o.name,o.size,o.archive_id FROM objects o JOIN retired_objects r ON r.name=o.name WHERE r.destination_id=?{NAMES} UNION SELECT o.name,o.size,o.archive_id FROM objects o JOIN volume_transfers t ON t.name=o.name WHERE t.destination_id=?{NAMES}) q JOIN archives a ON a.id=q.archive_id LEFT JOIN object_copies c ON c.name=q.name AND c.destination_id=? AND (?=1 OR c.state<>'deleted') LEFT JOIN volume_transfers t ON t.name=q.name AND t.destination_id=? LEFT JOIN placements p ON p.archive_id=q.archive_id AND p.group_id=t.group_id AND p.destination_id=t.destination_id".Replace("{NAMES}",filter);var args=new List<object>();
            if(names==null)args.AddRange(new object[]{destinationId,includeDeleted,destinationId,destinationId,destinationId,destinationId,includeDeleted,destinationId});
            else{
                args.Add(destinationId);args.Add(includeDeleted);args.AddRange(names.Cast<object>());
                for(int branch=0;branch<3;branch++){args.Add(destinationId);args.AddRange(names.Cast<object>());}
                args.AddRange(new object[]{destinationId,includeDeleted,destinationId});
            }
            return database.Query(sql,args.ToArray()).Select(r=>new StoredArchiveObject{Name=(string)r[0],Size=(long)r[1],ArchiveId=(long)r[2],State=(string)r[3],TaskId=(string)r[4],ArchiveStatus=(string)r[5]}).ToList();
        }
        public IList<ArchiveSummary> ProblemVersionArchives(string destinationId)
        {
            // Healthy sealed archives need no per-object browse work. Inspect
            // manifests only for known failures or an absent first/final volume.
            lock(gate)return database.Query("SELECT a.id FROM archives a LEFT JOIN node_archive_seals s ON s.archive_id=a.id WHERE (s.archive_id IS NOT NULL OR EXISTS(SELECT 1 FROM backup_nodes n WHERE n.archive_id=a.id)) AND a.status NOT IN ('cleaned','writing') AND (EXISTS(SELECT 1 FROM placements p WHERE p.archive_id=a.id AND p.destination_id=?) OR EXISTS(SELECT 1 FROM archive_pool_members m WHERE m.archive_id=a.id AND m.destination_id=?) OR (NOT EXISTS(SELECT 1 FROM placements p WHERE p.archive_id=a.id) AND NOT EXISTS(SELECT 1 FROM archive_pool_members m WHERE m.archive_id=a.id))) AND (a.status IN ('failed','damaged','repair-required','redundancy-shortfall') OR NOT EXISTS(SELECT 1 FROM objects o WHERE o.archive_id=a.id AND o.name IN (a.name,a.name||'.001')))",destinationId,destinationId).Select(row=>GetArchive((long)row[0])).ToList();
        }
        public void RecordWarehouseObjectProblems(string destinationId,IList<StorageEntry> entries,bool ignoreCase)
        {
            var listed=entries.GroupBy(entry=>entry.Name,ignoreCase?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal).ToDictionary(group=>group.Key,group=>group.First(),ignoreCase?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
            Transaction(delegate{
                var copies=database.Query("SELECT o.name,o.archive_id,c.group_id,c.state,p.status,a.status,o.size FROM object_copies c JOIN objects o ON o.name=c.name JOIN archives a ON a.id=o.archive_id JOIN placements p ON p.archive_id=a.id AND p.destination_id=c.destination_id AND p.group_id=c.group_id WHERE c.destination_id=? AND c.state<>'deleted' AND p.reserved=0 AND p.status IN ('complete','repair-required','unavailable') AND a.status IN ('complete','repair-required','redundancy-shortfall')",destinationId);
                foreach(var row in copies){
                    StorageEntry entry;string problem=!listed.TryGetValue((string)row[0],out entry)?"missing":entry.Directory||entry.Size>=0&&entry.Size!=(long)row[6]?"size-or-name-mismatch":null;if(problem==null)continue;
                    if((string)row[3]!=problem&&(string)row[3]!="content-damaged")database.Execute("UPDATE object_copies SET state=? WHERE name=? AND destination_id=?",problem,row[0],destinationId);
                    if((string)row[4]!="repair-required")database.Execute("UPDATE placements SET status='repair-required' WHERE archive_id=? AND group_id=? AND destination_id=?",row[1],row[2],destinationId);
                    if((string)row[5]!="repair-required")database.Execute("UPDATE archives SET status='repair-required' WHERE id=?",row[1]);
                }return 0;
            });
        }
        public IList<FileHistoryRow> GetFileHistory(string taskId,bool includeSystem=false)
        {
            string filter=taskId!=null?(includeSystem?" AND a.task_id IN (?,'__catalog__')":" AND a.task_id=?"):includeSystem?"":" AND a.task_id<>'__catalog__'";
            object[] arguments=taskId==null?new object[0]:new object[]{taskId};
            lock(gate)return database.Query("SELECT a.id,a.task_id,f.path,f.source_path,f.size,f.modified,a.name,f.state,a.status,p.group_id,p.destination_id,p.status,f.entry_path,f.content_path FROM archive_files f JOIN archives a ON a.id=f.archive_id JOIN placements p ON p.archive_id=a.id WHERE p.stored>0 AND f.state<>'staged'"+filter+" ORDER BY a.id,f.path",arguments)
                .Select(r=>new FileHistoryRow{ArchiveId=(long)r[0],TaskId=(string)r[1],Path=(string)r[2],SourcePath=(string)r[3],Size=(long)r[4],Modified=(long)r[5],ArchiveName=(string)r[6],FileState=(string)r[7],ArchiveStatus=(string)r[8],GroupId=(string)r[9],DestinationId=(string)r[10],PlacementStatus=(string)r[11],EntryPath=(string)r[12],ContentPath=(string)r[13]}).ToList();
        }
        public bool HasCompleteArchiveAt(long archiveId,string destination)
        {lock(gate)return Scalar("SELECT count(*) FROM archives WHERE id=? AND status IN ('complete','repair-required','redundancy-shortfall','snapshot-history')",archiveId)>0&&Scalar("SELECT count(*) FROM objects WHERE archive_id=?",archiveId)>0&&Scalar("SELECT count(*) FROM objects o WHERE o.archive_id=? AND NOT EXISTS(SELECT 1 FROM object_copies c WHERE c.name=o.name AND c.destination_id=? AND c.state='present')",archiveId,destination)==0;}
        public IList<FileHistoryRow> GetArchiveHistory(long archiveId)
        {lock(gate)return database.Query("SELECT a.id,a.task_id,f.path,f.source_path,f.size,f.modified,a.name,f.state,a.status,p.group_id,p.destination_id,p.status,f.entry_path,f.content_path FROM archive_files f JOIN archives a ON a.id=f.archive_id JOIN placements p ON p.archive_id=a.id WHERE a.id=? AND p.stored>0 AND f.state<>'staged' ORDER BY f.path",archiveId).Select(r=>new FileHistoryRow{ArchiveId=(long)r[0],TaskId=(string)r[1],Path=(string)r[2],SourcePath=(string)r[3],Size=(long)r[4],Modified=(long)r[5],ArchiveName=(string)r[6],FileState=(string)r[7],ArchiveStatus=(string)r[8],GroupId=(string)r[9],DestinationId=(string)r[10],PlacementStatus=(string)r[11],EntryPath=(string)r[12],ContentPath=(string)r[13]}).ToList();}
        private sealed class LatestViewsCache {internal long Stamp;internal IList<FileBackupView> Views;}
        public IList<FileBackupView> GetNodeFileViews(string taskId,NodeFileDatabase files)
        {
            lock(gate){var result=new List<FileBackupView>();foreach(var batch in files.Files.GroupBy(file=>file.ArchiveId)){
                var archive=GetArchive(batch.Key);if(archive.TaskId!=taskId)throw new System.IO.IOException("备份版本引用了其他任务的数据包。");
                if(!new[]{"complete","redundancy-shortfall","repair-required"}.Contains(archive.Status))continue;
                var places=database.Query("SELECT p.status,g.name||' → '||p.destination_id FROM placements p JOIN groups g ON g.id=p.group_id WHERE p.archive_id=? AND p.status IN ('complete','repair-required','unavailable','reconcile-required','failed')",batch.Key);
                string locations=string.Join("；",places.Where(row=>new[]{"complete","repair-required","unavailable"}.Contains((string)row[0])).Select(row=>(string)row[1]));
                int unavailable=places.Count(row=>(string)row[0]=="unavailable"),pending=places.Count(row=>(string)row[0]=="reconcile-required"),failed=places.Count(row=>(string)row[0]=="failed");
                foreach(var file in batch)result.Add(new FileBackupView{ArchivePath=file.Path,Size=file.Size,Modified=file.Modified,ArchiveId=archive.ArchiveId,ArchiveName=archive.ArchiveName,Status=archive.Status,TargetCopies=archive.TargetCopies,ValidCopies=archive.CurrentValidCopies,UnavailableCopies=unavailable,PendingCopies=pending,FailedCopies=failed,Locations=locations});
            }return result.AsReadOnly();}
        }
        private readonly Dictionary<string,LatestViewsCache> latestViews=new Dictionary<string,LatestViewsCache>();
        private long latestViewsStamp=long.MinValue;
        public IList<FileBackupView> GetLatestFileViews(string taskId)
        {
            lock(gate)
            {
                if(taskId==null)return new List<FileBackupView>().AsReadOnly();
                long stamp=DataChangeStamp;LatestViewsCache cached;
                if(latestViewsStamp!=stamp){latestViews.Clear();latestViewsStamp=stamp;}
                if(latestViews.TryGetValue(taskId,out cached)&&cached.Stamp==stamp)return cached.Views;
                // Choose the newest eligible current row within this task once per path.
                // Keep archive/row order for callers that fold path casing in memory.
                var result=database.Query("WITH latest AS (SELECT newer.path,max(newer.archive_id) AS archive_id FROM archive_files newer JOIN archives na ON na.id=newer.archive_id WHERE na.task_id=? AND newer.state='current' AND na.status IN ('complete','redundancy-shortfall','repair-required') GROUP BY newer.path) SELECT f.path,f.size,f.modified,a.id,a.name,a.status,a.target_copies,coalesce(p.valid,0),coalesce(p.locations,''),coalesce(p.unavailable,0),coalesce(p.pending,0),coalesce(p.failed,0) FROM archive_files f JOIN archives a ON a.id=f.archive_id JOIN latest ON latest.archive_id=a.id AND latest.path=f.path LEFT JOIN (SELECT p.archive_id,sum(CASE WHEN p.status='complete' THEN 1 ELSE 0 END) valid,group_concat(CASE WHEN p.status IN ('complete','repair-required','unavailable') THEN g.name || ' → ' || p.destination_id END,'；') locations,sum(CASE WHEN p.status='unavailable' THEN 1 ELSE 0 END) unavailable,sum(CASE WHEN p.status='reconcile-required' THEN 1 ELSE 0 END) pending,sum(CASE WHEN p.status='failed' THEN 1 ELSE 0 END) failed FROM placements p JOIN groups g ON g.id=p.group_id JOIN archives pa ON pa.id=p.archive_id WHERE pa.task_id=? AND p.status IN ('complete','repair-required','unavailable','reconcile-required','failed') GROUP BY p.archive_id) p ON p.archive_id=a.id WHERE a.task_id=? AND f.state='current' AND a.status IN ('complete','redundancy-shortfall','repair-required') ORDER BY a.id,f.rowid",taskId,taskId,taskId)
                    .Select(r=>new FileBackupView{ArchivePath=(string)r[0],Size=(long)r[1],Modified=(long)r[2],ArchiveId=(long)r[3],ArchiveName=(string)r[4],Status=(string)r[5],TargetCopies=(int)(long)r[6],ValidCopies=(int)(long)r[7],Locations=(string)r[8],UnavailableCopies=(int)(long)r[9],PendingCopies=(int)(long)r[10],FailedCopies=(int)(long)r[11]}).ToList();
                var stats=VolumeStatistics(null,taskId);foreach(var view in result){int[] counts;if(stats.TryGetValue(view.ArchiveId,out counts))view.ValidCopies=counts[1];}
                var views=result.AsReadOnly();latestViews[taskId]=new LatestViewsCache{Stamp=stamp,Views=views};return views;
            }
        }
    }
}
