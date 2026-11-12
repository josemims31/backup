using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace Backup.Core
{
    public sealed class StorageTree
    {
        public string Destination,Kind,Tag,Directory;
        public int Height;
        public long NextSlot;
    }
    public sealed partial class BackupCatalog
    {
        private void InitializeStorageSchema()
        {
            database.Execute("CREATE TABLE IF NOT EXISTS storage_trees(destination_id TEXT NOT NULL REFERENCES destinations(id) ON DELETE CASCADE,kind TEXT NOT NULL,tag TEXT NOT NULL,directory TEXT NOT NULL,height INTEGER NOT NULL DEFAULT 0,next_slot INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(destination_id,kind,tag))");
            database.Execute("CREATE TABLE IF NOT EXISTS object_locations(name TEXT NOT NULL REFERENCES objects(name) ON DELETE CASCADE,destination_id TEXT NOT NULL REFERENCES destinations(id) ON DELETE CASCADE,directory TEXT NOT NULL,PRIMARY KEY(name,destination_id))");
            database.Execute("CREATE INDEX IF NOT EXISTS object_locations_folder ON object_locations(destination_id,directory)");
            database.Execute("CREATE TABLE IF NOT EXISTS storage_growth(destination_id TEXT NOT NULL REFERENCES destinations(id) ON DELETE CASCADE,kind TEXT NOT NULL,tag TEXT NOT NULL,phase INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(destination_id,kind,tag))");
        }
        public StorageTree GetStorageTree(string destination,string kind,string tag)
        {lock(gate){var rows=database.Query("SELECT directory,height,next_slot FROM storage_trees WHERE destination_id=? AND kind=? AND tag=?",destination,kind,tag);return rows.Count==0?null:new StorageTree{Destination=destination,Kind=kind,Tag=tag,Directory=(string)rows[0][0],Height=(int)(long)rows[0][1],NextSlot=(long)rows[0][2]};}}
        public IList<StorageTree> StorageTrees(string destination)
        {lock(gate)return database.Query("SELECT kind,tag,directory,height,next_slot FROM storage_trees WHERE destination_id=?",destination).Select(r=>new StorageTree{Destination=destination,Kind=(string)r[0],Tag=(string)r[1],Directory=(string)r[2],Height=(int)(long)r[3],NextSlot=(long)r[4]}).ToList();}
        public void AddStorageTree(StorageTree tree)
        {lock(gate)database.Execute("INSERT INTO storage_trees VALUES(?,?,?,?,?,?)",tree.Destination,tree.Kind,tree.Tag,tree.Directory,tree.Height,tree.NextSlot);}
        public long TakeStorageSlot(StorageTree tree)
        {return Transaction(delegate{long slot=Scalar("SELECT next_slot FROM storage_trees WHERE destination_id=? AND kind=? AND tag=?",tree.Destination,tree.Kind,tree.Tag);database.Execute("UPDATE storage_trees SET next_slot=? WHERE destination_id=? AND kind=? AND tag=?",checked(slot+1),tree.Destination,tree.Kind,tree.Tag);tree.NextSlot=slot+1;return slot;});}
        public void StorageCursor(StorageTree tree,long slot){lock(gate){database.Execute("UPDATE storage_trees SET next_slot=? WHERE destination_id=? AND kind=? AND tag=?",slot,tree.Destination,tree.Kind,tree.Tag);tree.NextSlot=slot;}}
        public IList<StorageTree> PendingStorageTrees(string destination)
        {lock(gate)return database.Query("SELECT t.kind,t.tag,t.directory,t.height,t.next_slot FROM storage_trees t JOIN storage_growth g ON g.destination_id=t.destination_id AND g.kind=t.kind AND g.tag=t.tag WHERE t.destination_id=?",destination).Select(r=>new StorageTree{Destination=destination,Kind=(string)r[0],Tag=(string)r[1],Directory=(string)r[2],Height=(int)(long)r[3],NextSlot=(long)r[4]}).ToList();}
        public string ObjectStorageDirectory(string destination,string name)
        {lock(gate){var rows=database.Query("SELECT directory FROM object_locations WHERE destination_id=? AND name=?",destination,name);return rows.Count==0?null:(string)rows[0][0];}}
        public IDictionary<string,string> ObjectStorageDirectories(string destination)
        {lock(gate)return database.Query("SELECT name,directory FROM object_locations WHERE destination_id=?",destination).ToDictionary(row=>(string)row[0],row=>(string)row[1],StringComparer.Ordinal);}
        public void RememberObjectDirectory(string destination,string name,string directory)
        {
            if(directory==null||directory.Split('/').Any(p=>p==".."||p=="."||p.Contains("\\")||p.Contains(":")))throw new IOException("备份对象路径无效。");
            lock(gate)database.Execute("INSERT OR REPLACE INTO object_locations(name,destination_id,directory) SELECT ?,?,? WHERE EXISTS(SELECT 1 FROM objects WHERE name=?)",name,destination,directory,name);
        }
        // Append-only placement: one allocation per object and destination. A new
        // bucket never changes an existing object's path or consults remote lists.
        public string ReserveBucketDirectory(string destination,string name,string kind,string tag,string root,bool reassign=false)
        {
            return Transaction(delegate{
                var existing=database.Query("SELECT directory FROM object_locations WHERE destination_id=? AND name=?",destination,name);
                if(existing.Count>0&&!reassign)return (string)existing[0][0];
                database.Execute("INSERT OR IGNORE INTO storage_trees(destination_id,kind,tag,directory,height,next_slot) VALUES(?,?,?,?,0,0)",destination,kind,tag,root);
                long slot=Scalar("SELECT next_slot FROM storage_trees WHERE destination_id=? AND kind=? AND tag=?",destination,kind,tag);
                string directory=ObjectStoragePath.Join(root,(slot/ObjectStoragePath.BucketFiles+1).ToString("D6",System.Globalization.CultureInfo.InvariantCulture));
                database.Execute("UPDATE storage_trees SET next_slot=? WHERE destination_id=? AND kind=? AND tag=?",checked(slot+1),destination,kind,tag);
                database.Execute("INSERT OR REPLACE INTO object_locations(name,destination_id,directory) SELECT ?,?,? WHERE EXISTS(SELECT 1 FROM objects WHERE name=?)",name,destination,directory,name);
                return directory;
            });
        }
        public ObjectRecord FindStoredObject(string name)
        {lock(gate){var rows=database.Query("SELECT size,sha256 FROM objects WHERE name=?",name);return rows.Count==0?null:new ObjectRecord{Name=name,Size=(long)rows[0][0],Sha256=(string)rows[0][1]};}}
        public string ObjectTaskOwner(string name)
        {
            lock(gate){var rows=database.Query("SELECT a.task_id,a.id FROM archives a JOIN objects o ON o.archive_id=a.id WHERE o.name=?",name);if(rows.Count==0)return null;string owner=(string)rows[0][0];if(owner.StartsWith("__node__:",StringComparison.Ordinal)){var tasks=database.Query("SELECT DISTINCT task_id FROM node_revisions WHERE archive_id=? ORDER BY task_id",rows[0][1]);return tasks.Count==0?null:(string)tasks[0][0];}return owner.StartsWith("__repack__:",StringComparison.Ordinal)?owner.Substring(11):owner;}
        }
        public int StorageGrowthPhase(StorageTree tree)
        {lock(gate){var rows=database.Query("SELECT phase FROM storage_growth WHERE destination_id=? AND kind=? AND tag=?",tree.Destination,tree.Kind,tree.Tag);return rows.Count==0?-1:(int)(long)rows[0][0];}}
        public void StorageGrowthPhase(StorageTree tree,int phase)
        {lock(gate)database.Execute("INSERT OR REPLACE INTO storage_growth VALUES(?,?,?,?)",tree.Destination,tree.Kind,tree.Tag,phase);}
        public bool HasStorageGrowth(){lock(gate)return Scalar("SELECT count(*) FROM storage_growth")>0;}
        // A complete inspection has already learned the real paths. Retire only
        // the old movement intent; keep all physical files and recorded paths.
        public void AcceptInspectedStorageGrowth(string destination,IEnumerable<IntegrityFinding> findings)
        {
            lock(gate){if(Scalar("SELECT count(*) FROM storage_growth WHERE destination_id=?",destination)==0||PendingUploadStages().Any(p=>p.DestinationId==destination)||PendingVolumeTransfers().Any(p=>p.Placement.DestinationId==destination))return;
                var matched=new HashSet<string>(findings.Where(f=>!f.ArchiveLevel&&f.Destination==destination&&f.Status=="present-size-matched"&&f.ActualSize==f.ExpectedSize).Select(f=>f.Name),StringComparer.Ordinal);
                if(GetWarehouseObjects(destination).Any(item=>!matched.Contains(item.Name)))return;
                database.Execute("DELETE FROM storage_growth WHERE destination_id=?",destination);
            }
        }
        public void CompleteStorageGrowth(StorageTree tree)
        {
            Transaction(delegate{
                string prefix=tree.Directory;int offset=prefix.Length+1;
                database.Execute("UPDATE object_locations SET directory=?||substr(directory,?) WHERE destination_id=? AND (directory=? OR substr(directory,1,?)=?)",prefix+"/00",offset,tree.Destination,prefix,prefix.Length+1,prefix+"/");
                database.Execute("UPDATE storage_trees SET directory=?||substr(directory,?) WHERE destination_id=? AND NOT (kind=? AND tag=?) AND (directory=? OR substr(directory,1,?)=?)",prefix+"/00",offset,tree.Destination,tree.Kind,tree.Tag,prefix,prefix.Length+1,prefix+"/");
                database.Execute("UPDATE storage_trees SET height=height+1 WHERE destination_id=? AND kind=? AND tag=?",tree.Destination,tree.Kind,tree.Tag);
                database.Execute("DELETE FROM storage_growth WHERE destination_id=? AND kind=? AND tag=?",tree.Destination,tree.Kind,tree.Tag);tree.Height++;
                var destination=LoadTopology().Destinations.Single(d=>d.Id==tree.Destination);DeleteInventorySubtree(tree.Destination,destination.Location,RepositoryModules.IsFileSystem(destination.Protocol));return 0;
            });
        }
        public IList<StoredArchiveObject> WarehouseFolderObjects(string destination,string directory)
        {
            lock(gate){var names=database.Query("SELECT name FROM object_locations WHERE destination_id=? AND directory=?",destination,directory).Select(row=>(string)row[0]).ToList();
                if(directory.Length==0)names.AddRange(database.Query("SELECT q.name FROM (SELECT name FROM object_copies WHERE destination_id=? AND state<>'deleted' UNION SELECT name FROM warehouse_detached WHERE destination_id=? AND state='pending' UNION SELECT name FROM retired_objects WHERE destination_id=? UNION SELECT name FROM volume_transfers WHERE destination_id=?) q WHERE NOT EXISTS(SELECT 1 FROM object_locations l WHERE l.name=q.name AND l.destination_id=?)",destination,destination,destination,destination,destination).Select(row=>(string)row[0]));
                return GetWarehouseObjectsByName(destination,names);}
        }
        public void RememberListedObjectDirectories(string destination,string directory,IEnumerable<StorageEntry> entries)
        {
            Transaction(delegate{foreach(var entry in entries.Where(e=>!e.Directory&&e.Size>=0))database.Execute("INSERT OR REPLACE INTO object_locations(name,destination_id,directory) SELECT o.name,?,? FROM objects o WHERE o.name=? AND o.size=? AND EXISTS(SELECT 1 FROM object_copies c WHERE c.name=o.name AND c.destination_id=? AND c.state<>'deleted' UNION ALL SELECT 1 FROM volume_transfers t WHERE t.name=o.name AND t.destination_id=?) AND coalesce((SELECT directory FROM object_locations l WHERE l.name=o.name AND l.destination_id=?),'')<>?",destination,directory,entry.Name,entry.Size,destination,destination,destination,directory);return 0;});
        }
    }
    public static class ObjectStoragePath
    {
        public const int BucketFiles=900;
        public const int Fanout=99;
        public static int Height(long slot){if(slot<0)throw new ArgumentOutOfRangeException("slot");int height=0;while(slot>=Fanout){slot/=Fanout;height++;}return height;}
        public static string Branch(long slot,int height)
        {var parts=new string[height];slot/=Fanout;for(int index=height-1;index>=0;index--){parts[index]=(slot%Fanout).ToString("D2",System.Globalization.CultureInfo.InvariantCulture);slot/=Fanout;}return string.Join("/",parts);}
        public static string Join(string left,string right){return string.IsNullOrEmpty(left)?right:string.IsNullOrEmpty(right)?left:left+"/"+right;}
        public static string Absolute(DestinationDefinition destination,string relative)
        {if(RepositoryModules.IsFileSystem(destination.Protocol))return string.IsNullOrEmpty(relative)?destination.Location:Path.Combine(destination.Location,relative.Replace('/','\\'));string value=destination.Location;foreach(string part in relative.Split(new[]{'/'},StringSplitOptions.RemoveEmptyEntries))value=RemotePath.Append(value,part);return value;}
        public static string Relative(DestinationDefinition destination,string folder)
        {
            string relative;if(RepositoryModules.IsFileSystem(destination.Protocol)){string root=StoragePath.NormalizeRoot(destination.Location),path=StoragePath.NormalizeRoot(folder);if(path.Equals(root,StringComparison.OrdinalIgnoreCase))return "";if(!path.StartsWith(root.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))throw new IOException("仓库目录越界。");relative=path.Substring(root.TrimEnd('\\').Length+1).Replace('\\','/');}
            else{var root=new Uri(RepositoryModules.NormalizeLocation(destination.Protocol,destination.Location,false));var path=new Uri(RepositoryModules.NormalizeLocation(destination.Protocol,folder,false));if(path.Scheme!=root.Scheme||path.Authority!=root.Authority||!path.AbsolutePath.StartsWith(root.AbsolutePath,StringComparison.Ordinal))throw new IOException("仓库目录越界。");relative=Uri.UnescapeDataString(root.MakeRelativeUri(path).ToString()).TrimEnd('/');}
            if(relative.Split('/').Any(p=>p==".."||p=="."||p.Contains("\\")||p.Contains(":")))throw new IOException("仓库目录无效。");return relative;
        }
    }
}
