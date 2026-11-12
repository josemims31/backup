using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Backup.Core;
namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        // Task selections restore logical files; warehouse selections download
        // the physical bytes from that destination without decoding archives.
        public RestoredSelection MaterializeDownload(IList<BackupHistoryNode> nodes,string warehouseId=null,Action<string> report=null)
        {
            if(nodes.Count==0)throw new IOException("请选择要下载的文件。");if(nodes.Any(node=>node.IsArchiveBrowseBlocked))throw new IOException(nodes.First(node=>node.IsArchiveBrowseBlocked).ArchiveBrowseNotice);
            if(nodes.Any(node=>node.IsMissingArchive))throw new IOException("所选压缩包为缺失记录，仓库中没有可直接下载的完整文件。请先修复完整性，或从任务视图选择其他健康副本恢复。");
            var workspace=WorkspaceMigration.Load(Catalog);
            var raw=new List<Tuple<BackupHistoryNode,string>>();var directories=new List<string>();var requests=new List<RestoreRequest>();
            Action<BackupHistoryNode,string,int> visit=null;visit=(node,prefix,depth)=>
            {
                DownloadCancellation.ThrowIfCancellationRequested();if(depth>64)throw new IOException("目录层数超过下载上限。");
                string relative=Path.Combine(prefix,node.IsWarehouseRoot?node.TreeName:node.Name);
                if(node.Entry.Directory){directories.Add(relative);foreach(var entry in ListWarehouseEntries(node.DestinationId,node.Entry.Location,DownloadCancellation))visit(new BackupHistoryNode{Entry=entry,Name=entry.Name,DestinationId=node.DestinationId,BrowseLocation=node.Entry.Location},relative,depth+1);}
                else raw.Add(Tuple.Create(node,relative));
            };
            foreach(var node in nodes){if(node.Entry==null||node.ArchiveContainer)requests.AddRange(BackupHistoryTree.Requests(new[]{node},warehouseId,workspace,ReadArchiveRootLabels));else visit(node,"",0);}
            requests=requests.GroupBy(item=>item.RelativePath,StringComparer.OrdinalIgnoreCase).Select(group=>group.OrderByDescending(item=>item.File.ArchiveId).First()).ToList();
            var children=new List<RestoredSelection>();
            try
            {
                if(requests.Count>0)children.Add(Materialize(requests,report));if(raw.Count>0||directories.Count>0)children.Add(MaterializeRawWarehouseLeaves(raw,directories));
                if(children.Count==0)throw new IOException("所选备份包中没有当前有效文件。");
                if(children.Count==1)return children[0];
                var entries=children.SelectMany(child=>child.Entries).ToList();if(entries.GroupBy(item=>item.RelativePath,StringComparer.OrdinalIgnoreCase).Any(group=>group.Count()>1))throw new IOException("下载路径重名，请分别下载。");
                var selection=new RestoredSelection{Bandwidth=Bandwidth,Cancellation=CancellationTokenSource.CreateLinkedTokenSource(DownloadCancellation)};selection.Directories.AddRange(children.SelectMany(child=>child.Directories).Distinct(StringComparer.OrdinalIgnoreCase));
                foreach(var child in children){selection.Resources.Add(child);foreach(var file in child.Entries){var owner=child;var entry=file;selection.Entries.Add(new RestoredFile{RelativePath=entry.RelativePath,Size=entry.Size,Modified=entry.Modified,Open=token=>owner.OpenFile(entry.RelativePath)});}}
                selection.CopyTo=destination=>{var errors=new List<string>();foreach(var child in children){selection.Cancellation.Token.ThrowIfCancellationRequested();try{CopyRestored(child,destination);}catch(IOException error){errors.Add(error.Message);}catch(UnauthorizedAccessException error){errors.Add(error.Message);}}if(errors.Count>0)throw new IOException(string.Join("\n",errors));};return selection;
            }
            catch{foreach(var child in children)child.Dispose();throw;}
        }
    }
}
