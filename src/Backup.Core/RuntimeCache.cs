using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Backup.Core
{
    public sealed class CachedSourceList
    {
        public string Root;
        public IList<SourceItem> Files;
    }
    // Durable queue state is intentionally separate from the regenerable cache.
    public sealed class RuntimeStateStore:IDisposable
    {
        private readonly object gate=new object();private readonly SqliteDatabase database;
        public RuntimeStateStore(string path){database=new SqliteDatabase(path);database.Execute("PRAGMA journal_mode=WAL");database.Execute("PRAGMA synchronous=FULL");database.Execute("CREATE TABLE IF NOT EXISTS state(key TEXT PRIMARY KEY,value TEXT NOT NULL)");}
        public string Get(string key){lock(gate){var rows=database.Query("SELECT value FROM state WHERE key=?",key);return rows.Count==0?null:(string)rows[0][0];}}
        public void Set(string key,string value){lock(gate){if(Get(key)==value)return;database.Transaction(delegate{database.Execute("INSERT OR REPLACE INTO state(key,value) VALUES(?,?)",key,value);return 0;});}}
        public void Dispose(){lock(gate)database.Dispose();}
    }
    internal sealed class RuntimeCache:IDisposable
    {
        private readonly object gate=new object();private readonly SqliteDatabase database;
        internal RuntimeCache(string path)
        {
            database=new SqliteDatabase(path);
            database.Execute("PRAGMA journal_mode=WAL");database.Execute("PRAGMA synchronous=NORMAL");
            // A new cache namespace avoids depending on obsolete NOT NULL columns in
            // older rebuildable caches. Runtime code neither reads nor cleans them.
            database.Execute("CREATE TABLE IF NOT EXISTS sources_v2(id TEXT PRIMARY KEY,root TEXT NOT NULL)");
            database.Execute("CREATE TABLE IF NOT EXISTS source_files_v2(source_id TEXT NOT NULL,path_key TEXT NOT NULL,path TEXT NOT NULL,archive_path TEXT NOT NULL,size INTEGER NOT NULL,modified INTEGER NOT NULL,PRIMARY KEY(source_id,path_key))");
            database.Execute("CREATE TABLE IF NOT EXISTS inventory(destination_id TEXT NOT NULL,folder TEXT NOT NULL,json TEXT NOT NULL,checked_utc TEXT NOT NULL,error TEXT NOT NULL,PRIMARY KEY(destination_id,folder))");
        }
        internal CachedSourceList ReadSource(string id,string root)
        {
            lock(gate)
            {
                var header=database.Query("SELECT root FROM sources_v2 WHERE id=?",id);
                if(header.Count==0||!string.Equals(root,(string)header[0][0],StringComparison.OrdinalIgnoreCase))return null;
                return new CachedSourceList{Root=(string)header[0][0],Files=database.Query("SELECT path,archive_path,size,modified FROM source_files_v2 WHERE source_id=? ORDER BY path_key",id).Select(row=>new SourceItem{Path=(string)row[0],ArchivePath=(string)row[1],Size=(long)row[2],Modified=(long)row[3],VerifyContentDuringRead=false}).ToList()};
            }
        }
        internal bool SaveSource(string id,string root,IList<SourceItem> files,CachedSourceList previous)
        {
            var old=previous!=null&&string.Equals(root,previous.Root,StringComparison.OrdinalIgnoreCase)?previous.Files.ToDictionary(file=>file.Path,StringComparer.OrdinalIgnoreCase):null;
            var upserts=new List<object[]>();
            foreach(var file in files)
            {
                SourceItem prior;
                if(old==null||!old.TryGetValue(file.Path,out prior)||(!SourceItem.SameMetadata(file,prior)||file.ArchivePath!=prior.ArchivePath))upserts.Add(new object[]{id,file.Path.ToUpperInvariant(),file.Path,file.ArchivePath,file.Size,file.Modified});
                if(old!=null)old.Remove(file.Path);
            }
            if(old!=null&&old.Count==0&&upserts.Count==0)return false;
            lock(gate)return database.Transaction(delegate
            {
                if(old==null)database.Execute("DELETE FROM source_files_v2 WHERE source_id=?",id);
                else database.ExecuteMany("DELETE FROM source_files_v2 WHERE source_id=? AND path_key=?",old.Keys.Select(path=>new object[]{id,path.ToUpperInvariant()}));
                database.ExecuteMany("INSERT OR REPLACE INTO source_files_v2(source_id,path_key,path,archive_path,size,modified) VALUES(?,?,?,?,?,?)",upserts);
                if(old==null)database.Execute("INSERT OR REPLACE INTO sources_v2(id,root) VALUES(?,?)",id,root);
                return true;
            });
        }
        internal InventoryFolderRecord ReadInventory(string id,string folder)
        {
            lock(gate){string key=BackupCatalog.InventoryFolderKey(folder);var rows=database.Query("SELECT folder,json,checked_utc,error FROM inventory WHERE destination_id=? AND folder=?",id,key);if(rows.Count==0){bool remote=key.Contains("://");string trim=remote?"/":"\\/";rows=database.Query("SELECT folder,json,checked_utc,error FROM inventory WHERE destination_id=? AND rtrim(folder,?)=?"+(remote?" COLLATE BINARY":" COLLATE NOCASE")+" LIMIT 1",id,trim,key.TrimEnd(remote?new[]{'/'}:new[]{'\\','/'}));}return rows.Count==0?null:new InventoryFolderRecord{DestinationId=id,Folder=(string)rows[0][0],Json=(string)rows[0][1],CheckedUtc=(string)rows[0][2],Error=(string)rows[0][3]};}
        }
        internal void SaveInventory(IEnumerable<InventoryFolderRecord> records,string id,string scan,bool complete)
        {
            lock(gate)database.Transaction(delegate
            {
                foreach(var value in records)WriteInventory(value,ReadInventory(value.DestinationId,value.Folder));
                if(complete)database.Execute("DELETE FROM inventory WHERE destination_id=? AND checked_utc<>?",id,scan);
                return 0;
            });
        }
        internal void DeleteInventory(string id,string keepFolder=null){lock(gate){if(keepFolder==null)database.Execute("DELETE FROM inventory WHERE destination_id=?",id);else database.Execute("DELETE FROM inventory WHERE destination_id=? AND folder<>?",id,keepFolder);}}
        internal void SaveInventoryRecord(InventoryFolderRecord value,InventoryFolderRecord old)
        {
            lock(gate)database.Transaction(delegate{WriteInventory(value,old);return 0;});
        }
        private void WriteInventory(InventoryFolderRecord value,InventoryFolderRecord old)
        {string key=BackupCatalog.InventoryFolderKey(value.Folder),error=value.Error??"";if(old==null||old.Folder!=key){database.Execute("INSERT OR REPLACE INTO inventory(destination_id,folder,json,checked_utc,error) VALUES(?,?,?,?,?)",value.DestinationId,key,value.Json,value.CheckedUtc,error);if(old!=null)database.Execute("DELETE FROM inventory WHERE destination_id=? AND folder=?",old.DestinationId,old.Folder);}else if(old.Json!=value.Json)database.Execute("UPDATE inventory SET json=?,checked_utc=?,error=? WHERE destination_id=? AND folder=?",value.Json,value.CheckedUtc,error,value.DestinationId,key);else if(old.CheckedUtc!=value.CheckedUtc||old.Error!=error)database.Execute("UPDATE inventory SET checked_utc=?,error=? WHERE destination_id=? AND folder=?",value.CheckedUtc,error,value.DestinationId,key);}
        internal void DeleteInventorySubtree(string id,string folder,bool filesystem)
        {
            string trim=filesystem?"\\/":"/",path=folder.TrimEnd(filesystem?new[]{'\\','/'}:new[]{'/'}),prefix=path+(filesystem?"\\":"/");string comparison=filesystem?" COLLATE NOCASE":" COLLATE BINARY";
            lock(gate)database.Execute("DELETE FROM inventory WHERE destination_id=? AND (rtrim(folder,?)=?"+comparison+" OR substr(rtrim(folder,?),1,?)=?"+comparison+")",id,trim,path,trim,prefix.Length,prefix);
        }
        internal void DeleteSource(string id){lock(gate)database.Transaction(delegate{database.Execute("DELETE FROM source_files_v2 WHERE source_id=?",id);database.Execute("DELETE FROM sources_v2 WHERE id=?",id);return 0;});}
        internal void Clear(){lock(gate)database.Transaction(delegate{database.Execute("DELETE FROM source_files_v2");database.Execute("DELETE FROM sources_v2");database.Execute("DELETE FROM inventory");return 0;});}
        public void Dispose(){lock(gate)database.Dispose();}
    }
    public sealed partial class BackupCatalog
    {
        private RuntimeCache runtimeCache;
        private bool needsCacheCompaction;
        public string CacheDirectory {get;private set;}
        private long replacementEpoch;
        public long DataChangeStamp {get{lock(gate)return ((long)database.ChangeStamp<<32)^replacementEpoch;}}
        private void InitializeRuntimeCache(string path)
        {
            CacheDirectory=path==null?null:ApplicationFiles.CacheForCatalog(path);
            runtimeCache=new RuntimeCache(path==null?":memory:":Path.Combine(CacheDirectory,"metadata.db"));
        }
        private void MigrateInventoryCache()
        {
            if(Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='warehouse_inventory'")==0)return;
            if(CacheDirectory!=null)
            {
                var rows=database.Query("SELECT destination_id,folder,json,checked_utc,error FROM warehouse_inventory");
                runtimeCache.SaveInventory(rows.Select(row=>new InventoryFolderRecord{DestinationId=(string)row[0],Folder=(string)row[1],Json=(string)row[2],CheckedUtc=(string)row[3],Error=(string)row[4]}),null,null,false);
                needsCacheCompaction=rows.Count>0;
            }
            database.Execute("DROP TABLE warehouse_inventory");
        }
        public CachedSourceList ReadSourceCache(string id,string root){return runtimeCache.ReadSource(id,root);}
        public bool SaveSourceCache(string id,string root,IList<SourceItem> files,CachedSourceList previous){return runtimeCache.SaveSource(id,root,files,previous);}
        public void RemoveSourceCache(string id){runtimeCache.DeleteSource(id);}
        public IList<KeyValuePair<string,string>> LegacySourceCaches(){lock(gate)return database.Query("SELECT key,value FROM settings WHERE key LIKE 'source_files_v1:%'").Select(row=>new KeyValuePair<string,string>((string)row[0],(string)row[1])).ToList();}
        public void RemoveLegacySourceCaches(){lock(gate)database.Execute("DELETE FROM settings WHERE key LIKE 'source_files_v1:%'");}
        public void Compact(){lock(gate)database.Execute("VACUUM");}
        public void CompactAfterCacheMigration(bool migratedSources){lock(gate)if(needsCacheCompaction||migratedSources){database.Execute("VACUUM");needsCacheCompaction=false;}}
        public void ClearRuntimeCaches(){runtimeCache.Clear();}
        private static void PrepareRecoverySnapshot(SqliteDatabase copy)
        {
            copy.Execute("DELETE FROM settings WHERE key LIKE 'source_files_v1:%' OR key='schedule_request_queue_v1'");
            copy.Execute("DROP TABLE IF EXISTS warehouse_inventory");
            copy.Execute("DELETE FROM archive_leases WHERE token LIKE 'compaction:%'");
            copy.Execute("INSERT OR REPLACE INTO settings(key,value) VALUES('recovery_snapshot_v1','1')");
        }
        public DatabaseMemorySnapshot CreateRecoverySnapshot(MemoryBudget budget){lock(gate)return database.SnapshotMemory(budget,PrepareRecoverySnapshot);}
        public static BackupCatalog FromFileMemory(string path,MemoryBudget budget){return new BackupCatalog(SqliteDatabase.FromFileMemory(path,budget),null);}
    }
}
