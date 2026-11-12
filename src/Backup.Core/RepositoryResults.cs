using System;
using System.Collections.Generic;
namespace Backup.Core
{
    [Flags]public enum CatalogChange { None=0,Archives=1,Topology=2,Workspace=4,Rules=8,Settings=16,Schedules=32,Sources=64,Connections=128,Inventory=256 }
    public sealed class RepositoryObjectResult
    {
        public string DestinationId,Folder,Name;public long Size,ArchiveId;public bool Exists,IgnoreCase,Directory;
    }
    public sealed class RepositoryCommit
    {
        public long Sequence;public bool Important;public IList<RepositoryObjectResult> Results;public CatalogChange Changes;public IList<string> Tasks,Destinations;
    }
    public sealed partial class BackupCatalog
    {
        // Observers only enqueue immutable committed batches; they must not call back into the catalog.
        public event Action<RepositoryCommit> RepositoryCommitted;
        private bool pendingRepositoryStatus;
        private CatalogChange pendingChanges;
        private HashSet<string> pendingTasks;private bool pendingAllTasks;
        private HashSet<string> pendingDestinations;
        private void RepositoryDestinationsChanged(IEnumerable<string> ids){if(pendingDestinations!=null)pendingDestinations.UnionWith(ids);}
        private void RepositoryStatusChanged(CatalogChange changes=CatalogChange.Archives,string taskId=null){pendingRepositoryStatus=true;pendingChanges|=changes;if((changes&CatalogChange.Archives)!=0&&pendingTasks!=null){if(string.IsNullOrEmpty(taskId))pendingAllTasks=true;else pendingTasks.Add(taskId);}}
        private void SettingChanged(string key)
        {
            if(key=="workspace_v3")pendingChanges|=CatalogChange.Workspace;
            else if(key=="exclusions")pendingChanges|=CatalogChange.Rules;
            else if(key=="backup_schedules_v1")pendingChanges|=CatalogChange.Schedules;
            else if(key=="confirmed_repairs_v1")pendingChanges|=CatalogChange.Archives;
            else if(key.StartsWith("connection:",StringComparison.Ordinal)||key.StartsWith("connection_reference:",StringComparison.Ordinal))pendingChanges|=CatalogChange.Connections;
            else if(key=="backup_password_v1"||key=="backup_memory_mib_v1"||key=="catalog_backup_targets_v1")pendingChanges|=CatalogChange.Settings;
            else pendingChanges|=CatalogChange.Settings;
            if(key=="catalog_backup_targets_v1"||key=="catalog_backup_password_v1")database.Execute("UPDATE recovery_revision SET value=value+1 WHERE id=1");
        }
        private List<RepositoryObjectResult> pendingRepositoryResults;private long repositorySequence;
        public long RepositorySequence{get{return System.Threading.Interlocked.Read(ref repositorySequence);}}
        public bool ApplyObservation(Func<bool> current,Action publish){lock(gate){if(!current())return false;publish();return true;}}
        private T RepositoryTransaction<T>(Func<T> action)
        {
            lock(gate)
            {
                if(RepositoryCommitted==null)return database.Transaction(action);
                var pending=new List<RepositoryObjectResult>();var tasks=new HashSet<string>();var destinations=new HashSet<string>();pendingRepositoryResults=pending;pendingTasks=tasks;pendingDestinations=destinations;pendingAllTasks=false;pendingRepositoryStatus=false;pendingChanges=CatalogChange.None;bool important=false,allTasks;CatalogChange changes;int before=database.ChangeStamp;T result;
                try{result=database.Transaction(action);important=pendingRepositoryStatus;changes=pendingChanges;allTasks=pendingAllTasks;}finally{pendingRepositoryResults=null;pendingTasks=null;pendingDestinations=null;pendingRepositoryStatus=false;pendingChanges=CatalogChange.None;pendingAllTasks=false;}
                if(database.ChangeStamp!=before)
                {
                    var batch=new RepositoryCommit{Sequence=++repositorySequence,Important=important,Results=pending.AsReadOnly(),Changes=changes,Tasks=allTasks||tasks.Count==0?null:new List<string>(tasks).AsReadOnly(),Destinations=new List<string>(destinations).AsReadOnly()};var observers=RepositoryCommitted;
                    if(observers!=null)foreach(Action<RepositoryCommit> observer in observers.GetInvocationList())try{observer(batch);}catch{}
                }
                return result;
            }
        }
        private void RepositoryObjectCommitted(string destination,string name,long size,long archiveId,bool exists,string folder=null,string taskId=null)
        {
            if(pendingRepositoryResults==null)return;var rows=database.Query("SELECT protocol,location FROM destinations WHERE id=?",destination);if(rows.Count==0)return;
            string protocol=(string)rows[0][0];string effectiveFolder=folder??ObjectStoragePath.Absolute(new DestinationDefinition{Id=destination,Protocol=protocol,Location=(string)rows[0][1]},ObjectStorageDirectory(destination,BackupObjectNaming.ObjectName(name)??name)??"");bool filesystem=RepositoryModules.IsFileSystem(protocol);pendingRepositoryResults.Add(new RepositoryObjectResult{DestinationId=destination,Folder=effectiveFolder,Name=name,Size=size,ArchiveId=archiveId,Exists=exists,IgnoreCase=filesystem});RepositoryStatusChanged(CatalogChange.Archives,taskId);
        }
    }
}
