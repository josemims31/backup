using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace Backup.Desktop
{
    public sealed partial class MainWindowController
    {
        private readonly Dictionary<BackupHistoryNode,RepositoryFolderChange> deferredWarehouseChanges=new Dictionary<BackupHistoryNode,RepositoryFolderChange>();
        private static void MergeFolderChange(IDictionary<string,RepositoryFolderChange> changes,RepositoryFolderChange value)
        {string key=RepositoryRuntime.FolderKey(value.DestinationId,value.Folder);RepositoryFolderChange old;if(changes.TryGetValue(key,out old))changes[key]=new RepositoryFolderChange{DestinationId=value.DestinationId,Folder=value.Folder,ContentChanged=old.ContentChanged||value.ContentChanged,MetadataChanged=old.MetadataChanged||value.MetadataChanged,Version=Math.Max(old.Version,value.Version),Notice=value.Version>=old.Version?value.Notice:old.Notice};else changes[key]=value;}
        private async void DeferWarehouseChange(BackupHistoryNode node,RepositoryFolderChange change)
        {
            RepositoryFolderChange old;if(deferredWarehouseChanges.TryGetValue(node,out old)){deferredWarehouseChanges[node]=new RepositoryFolderChange{DestinationId=change.DestinationId,Folder=change.Folder,Version=Math.Max(old.Version,change.Version),ContentChanged=old.ContentChanged||change.ContentChanged,MetadataChanged=old.MetadataChanged||change.MetadataChanged,Notice=change.Version>=old.Version?change.Notice:old.Notice};return;}
            deferredWarehouseChanges[node]=change;int generation=warehouseBrowseGeneration;try{await node.RawLoading;}catch{}RepositoryFolderChange last;if(!deferredWarehouseChanges.TryGetValue(node,out last))return;deferredWarehouseChanges.Remove(node);if(!disposed&&generation==warehouseBrowseGeneration&&(last.Version>node.RawInventoryVersion||last.MetadataChanged&&node.RawCatalogStamp!=Backend.Catalog.DataChangeStamp))QueueRepositoryPresentation(new ApplicationChange(Backup.Core.CatalogChange.Inventory,new[]{last.DestinationId},folders:new[]{last}));
        }
        private async void PublishWarehouseChanges(ISet<string> ids,IList<RepositoryFolderChange> folders,bool archives)
        {try{await PublishWarehouseChangesAsync(ids,folders,archives);}catch(Exception error){if(!disposed)Log("本地仓库清单更新未完成："+error.Message);}}
        private async Task PublishWarehouseChangesAsync(ISet<string> ids,IList<RepositoryFolderChange> folders,bool archives)
        {
            bool visible=!IsFolderHistoryView&&Control<System.Windows.Controls.TabControl>("Tabs").SelectedIndex==3;var active=visible?WarehouseDirectory(historyFolder):null;if(warehouseRoots==null)return;int generation=warehouseBrowseGeneration;var state=visible?CaptureHistoryView():null;bool publish=false;var reloaded=new HashSet<BackupHistoryNode>();
            var loaded=HistoryDirectories(warehouseRoots).Where(node=>node.Entry!=null&&node.Entry.Directory&&!node.ArchiveContainer&&!string.IsNullOrEmpty(node.DestinationId)).ToLookup(node=>RepositoryRuntime.FolderKey(node.DestinationId,node.Entry.Location));
            foreach(var change in folders)
            {
                foreach(var node in loaded[RepositoryRuntime.FolderKey(change.DestinationId,change.Folder)]){
                if(change.Version<=node.RawInventoryVersion&&(!change.MetadataChanged||node.RawCatalogStamp==Backend.Catalog.DataChangeStamp))continue;if(node.RawLoading!=null){DeferWarehouseChange(node,change);continue;}
                if(change.ContentChanged||change.MetadataChanged){publish|=await LoadCachedWarehouseTreeAsync(node);reloaded.Add(node);if(disposed||generation!=warehouseBrowseGeneration)return;}node.RawInventoryVersion=Math.Max(node.RawInventoryVersion,change.Version);if(change.Notice!=null)node.RawInventoryNotice=change.Notice;if(historyFolder==node&&!change.ContentChanged&&!change.MetadataChanged)RefreshWarehouseNotice(node);
                }
            }
            if(archives&&active!=null&&(ids.Count==0||ids.Contains(active.DestinationId))&&!reloaded.Contains(active)&&active.RawCatalogStamp!=Backend.Catalog.DataChangeStamp)
            {
                if(active.RawLoading!=null)DeferWarehouseChange(active,new RepositoryFolderChange{DestinationId=active.DestinationId,Folder=active.Entry.Location,MetadataChanged=true});else{publish|=await LoadCachedWarehouseTreeAsync(active);if(disposed||generation!=warehouseBrowseGeneration)return;}
            }
            if(!publish||!visible)return;var folder=PublishHistoryRoots(warehouseRoots,state);historyFolder=folder;if(folder==null)return;if(folder.Entry!=null&&!folder.ArchiveContainer)BindWarehouseFolder(folder,state);else{historyFolderLoading=ShowTaskFolderAsync(folder,state);ObserveHistoryLoading(historyFolderLoading);}
        }
    }
}
