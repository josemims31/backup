using System;
using System.Collections.Generic;
using System.Linq;

namespace Backup.Core
{
    public sealed partial class BackupCatalog
    {
        private void InitializeRecoverySchema()
        {
            Transaction(delegate
            {
                database.Execute("CREATE TABLE IF NOT EXISTS recovery_revision(id INTEGER PRIMARY KEY CHECK(id=1),value INTEGER NOT NULL)");
                database.Execute("INSERT OR IGNORE INTO recovery_revision VALUES(1,1)");
                database.Execute("CREATE TABLE IF NOT EXISTS catalog_coverage(group_id TEXT PRIMARY KEY,archive_id INTEGER NOT NULL REFERENCES archives(id),revision INTEGER NOT NULL,task_id TEXT NOT NULL)");
                database.Execute("CREATE INDEX IF NOT EXISTS catalog_coverage_archive ON catalog_coverage(archive_id)");
                database.Execute("CREATE TABLE IF NOT EXISTS upload_temporaries(destination_id TEXT NOT NULL,name TEXT NOT NULL,temporary_name TEXT NOT NULL,PRIMARY KEY(destination_id,temporary_name))");
                database.Execute("CREATE INDEX IF NOT EXISTS upload_temporaries_object ON upload_temporaries(destination_id,name)");
                // Seed current 0.11 snapshots before changing the global retention rule.
                database.Execute("INSERT OR IGNORE INTO catalog_coverage(group_id,archive_id,revision,task_id) SELECT p.group_id,max(a.id),0,'__catalog__' FROM archive_pools p JOIN archives a ON a.id=p.archive_id WHERE a.task_id='__catalog__' AND a.status='complete' AND EXISTS(SELECT 1 FROM archive_files f WHERE f.archive_id=a.id AND f.state='current') GROUP BY p.group_id");
                // Database snapshots are global recovery records, including older snapshots.
                database.Execute("UPDATE catalog_coverage SET task_id='__catalog__' WHERE task_id<>'__catalog__'");
                database.Execute("DELETE FROM settings WHERE key LIKE 'catalog_snapshot_owner:%'");
                database.Execute("DELETE FROM settings WHERE key='catalog_target_task'");
                foreach(string table in new[]{"archives","object_copies","destinations","groups","group_members","group_children","group_modes","resource_retirements","archive_holds"})
                {
                    foreach(string action in new[]{"INSERT","UPDATE","DELETE"})
                    {
                        string row=action=="DELETE"?"OLD":"NEW",when="1";
                        if(table=="archives")when=row+".task_id<>'__catalog__'"+(action=="UPDATE"?" AND (OLD.status<>NEW.status OR OLD.target_copies<>NEW.target_copies)":"");
                        else if(table=="archive_files")when="EXISTS(SELECT 1 FROM archives a WHERE a.id="+row+".archive_id AND a.task_id<>'__catalog__')"+(action=="UPDATE"?" AND (OLD.state<>NEW.state OR OLD.path<>NEW.path OR OLD.size<>NEW.size OR OLD.modified<>NEW.modified)":"");
                        else if(table=="object_copies")when="EXISTS(SELECT 1 FROM objects o JOIN archives a ON a.id=o.archive_id WHERE o.name="+row+".name AND a.task_id<>'__catalog__')"+(action=="UPDATE"?" AND (OLD.state<>NEW.state OR OLD.group_id<>NEW.group_id OR OLD.pool_idx<>NEW.pool_idx)":"");
                        else if(table=="destinations"&&action=="UPDATE")when="OLD.location<>NEW.location OR OLD.protocol<>NEW.protocol OR OLD.max_bytes<>NEW.max_bytes OR OLD.enabled<>NEW.enabled";
                        database.Execute("CREATE TRIGGER IF NOT EXISTS recovery_"+table+"_"+action+" AFTER "+action+" ON "+table+" WHEN "+when+" BEGIN UPDATE recovery_revision SET value=value+1 WHERE id=1; END");
                    }
                }
                foreach(string action in new[]{"INSERT","UPDATE","DELETE"})
                {
                    string row=action=="DELETE"?"OLD":"NEW";
                    database.Execute("CREATE TRIGGER IF NOT EXISTS recovery_settings_"+action+" AFTER "+action+" ON settings WHEN ("+row+".key IN ('workspace_v3','exclusions','backup_password_v1') OR "+row+".key LIKE 'connection:%' OR "+row+".key LIKE 'connection_reference:%')"+(action=="UPDATE"?" AND OLD.value IS NOT NEW.value":"")+" BEGIN UPDATE recovery_revision SET value=value+1 WHERE id=1; END");
                }
                database.Execute("CREATE INDEX IF NOT EXISTS placements_active_reservation ON placements(destination_id) WHERE reserved>0");
                if((long)database.Query("PRAGMA user_version")[0][0]<6)database.Execute("PRAGMA user_version=6");
                return 0;
            });
        }
        public long RecoveryRevision{get{lock(gate)return Scalar("SELECT value FROM recovery_revision WHERE id=1");}}
        public void SaveCatalogBackupTargets(string json)
        {SaveConfiguration(new[]{new KeyValuePair<string,string>("catalog_backup_targets_v1",json)});}
        public void DetachImportedCatalogTasks()
        {lock(gate){database.Execute("UPDATE catalog_coverage SET task_id='__catalog__' WHERE task_id<>'__catalog__'");database.Execute("DELETE FROM settings WHERE key LIKE 'catalog_snapshot_owner:%'");database.Execute("DELETE FROM settings WHERE key='catalog_target_task'");}}
        public void TouchRecovery(){lock(gate)database.Execute("UPDATE recovery_revision SET value=value+1 WHERE id=1");}
        public bool HasCurrentCatalog(IEnumerable<string> roots,long revision)
        {return HasCurrentCatalog(roots,revision,null);}
        public bool HasCurrentCatalog(IEnumerable<string> roots,long revision,string password)
        {
            lock(gate)
            {
                var topology=LoadTopology();
                foreach(string root in roots.Distinct())
                {
                    var rows=database.Query("SELECT c.archive_id,a.password FROM catalog_coverage c JOIN archives a ON a.id=c.archive_id WHERE c.group_id=? AND c.revision=? AND a.status='complete'",root,revision);
                    if(rows.Count==0||password!=null&&!string.Equals((string)rows[0][1],password,StringComparison.Ordinal)||!CoversArchivePolicy((long)rows[0][0],topology.Scope(new[]{root}),1))return false;
                }
                return true;
            }
        }
        // Configuration-only edits do not require a new snapshot on an unchanged backup.
        // Archive ids are global: a later complete catalog contains earlier task archives.
        public bool TaskDataNeedsCatalog(string taskId,IEnumerable<string> roots)
        {
            lock(gate)
            {
                long newest=Scalar("SELECT coalesce(max(a.id),0) FROM archives a WHERE a.task_id<>'__catalog__' AND (? IS NULL OR a.task_id=?) AND a.status IN ('complete','repair-required','redundancy-shortfall') AND EXISTS(SELECT 1 FROM archive_files f WHERE f.archive_id=a.id AND f.state='current')",taskId,taskId);
                var topology=LoadTopology();
                foreach(string root in roots.Distinct())
                {
                    var rows=database.Query("SELECT c.archive_id,c.revision,a.status FROM catalog_coverage c JOIN archives a ON a.id=c.archive_id WHERE c.group_id=?",root);
                    if(rows.Count>0&&(long)rows[0][1]<0)return true;
                    if(newest==0)continue;
                    if(rows.Count==0||(long)rows[0][0]<newest||(string)rows[0][2]!="complete"||!CoversArchivePolicy((long)rows[0][0],topology.Scope(new[]{root}),1))return true;
                }
                return false;
            }
        }
        // Keep the previous recoverable snapshot address while recording an unfinished
        // publication. The existing revision field needs no extra settings or schema.
        public void RequireCatalogRetry(IEnumerable<string> roots)
        {lock(gate)database.ExecuteMany("UPDATE catalog_coverage SET revision=-1 WHERE group_id=? AND revision>=0",roots.Distinct().Select(root=>new object[]{root}));}
        public void PublishCatalogSnapshot(long archiveId,IEnumerable<string> roots,string taskId,long revision,IEnumerable<string> activeRoots=null)
        {
            Transaction(delegate
            {RepositoryStatusChanged(CatalogChange.Archives,"__catalog__");
                if(GetArchive(archiveId).Status!="complete")throw new InvalidOperationException("未完整提交的数据库快照不能替换上一份。");
                if(activeRoots!=null){var retained=new HashSet<string>(activeRoots);foreach(var row in database.Query("SELECT group_id FROM catalog_coverage"))if(!retained.Contains((string)row[0]))database.Execute("DELETE FROM catalog_coverage WHERE group_id=?",row[0]);}
                foreach(string root in roots.Distinct())database.Execute("INSERT OR REPLACE INTO catalog_coverage(group_id,archive_id,revision,task_id) VALUES(?,?,?,?)",root,archiveId,revision,taskId);
                database.Execute("UPDATE archive_files SET state='expired' WHERE state='current' AND archive_id IN(SELECT id FROM archives WHERE task_id='__catalog__' AND id<>?) AND archive_id NOT IN(SELECT archive_id FROM catalog_coverage)",archiveId);
                return 0;
            });
        }

    }
}
