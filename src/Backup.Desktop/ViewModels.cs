using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Backup.Core;

namespace Backup.Desktop
{
    public abstract class NotifyModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Changed(string property) { var handler=PropertyChanged; if(handler!=null) handler(this,new PropertyChangedEventArgs(property)); }
    }
    public static class Display
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]private static extern uint GetDriveType(string root);
        public static string FilesystemProtocol(string path){return path.StartsWith(@"\\")||GetDriveType(Path.GetPathRoot(path))==4?"smb":"local";}
        public static string DestinationType(DestinationDefinition destination){return destination.Protocol=="local"?(destination.Location.StartsWith(@"\\")?"smb":"本地"):destination.Protocol.ToLowerInvariant();}
        public static string TargetLabel(StorageTopology topology,BackupGroupDefinition group){return group.Id.StartsWith("single:")?"["+DestinationType(topology.Destinations.Single(d=>d.Id==group.DestinationIds.Single()))+"] "+group.Name:"["+(group.Mode??"raid0")+"组] "+group.Name;}
        public static string TargetHint(StorageTopology topology,BackupGroupDefinition group){return group.Id.StartsWith("single:")?topology.Destinations.Single(d=>d.Id==group.DestinationIds.Single()).Location:string.IsNullOrWhiteSpace(group.Notes)?null:group.Notes;}
        public static string TargetName(BackupGroupDefinition group){return group.Id.StartsWith("single:")?group.Name:"["+(group.Mode??"raid0")+"组] "+group.Name;}
        public static string Bytes(long value)
        {
            string[] units={"B","KiB","MiB","GiB","TiB"}; double n=value; int i=0;
            while(n>=1024 && i<units.Length-1){n/=1024;i++;}
            return n.ToString(i==0?"0":"0.##")+" "+units[i];
        }
        public static string Status(string value)
        {
            if(string.IsNullOrEmpty(value))return "";
            if(value.StartsWith("unavailable:",StringComparison.Ordinal))return "暂不可访问";
            switch(value){case "damaged":return "损坏";case "complete":return "已完成";case "writing":return "备份中";case "failed":return "失败";
                case "redundancy-shortfall":return "副本不足";case "repair-required":return "待修复";
                case "snapshot-history":return "历史快照";case "reconcile-required":return "待确认";
                case "unavailable":return "暂不可访问";case "cleaned":return "已清理";
                case "deleted":return "已删除";case "expired":return "旧版";case "pending":return "待处理";
                case "present":return "存在";case "missing":return "缺失";
                case "present-size-matched":return "名称与大小一致";case "size-or-name-mismatch":return "名称或大小不符";
                case "retired":return "已移除";case "copying":return "迁移中";case "published":return "已切换";
                default:return "未知状态";}
        }
        public static string ArchivePath(string path)
        {
            path=Path.GetFullPath(path);
            return path.StartsWith(@"\\") ? "UNC\\"+path.TrimStart('\\') : path.Substring(0,1).ToUpperInvariant()+"\\"+path.Substring(3);
        }
    }
    public sealed class LoadedSource
    {
        public string Root;
        public IList<SourceItem> Files;
    }
    public sealed class FileTreeNode : NotifyModel
    {
        public string Name {get;set;}
        public string FullPath {get;set;}
        public bool Directory {get;set;}
        public SourceItem Source {get;set;}
        public FileTreeNode Parent {get;set;}
        public ObservableCollection<FileTreeNode> Children {get;private set;}
        public long TotalBytes {get;private set;}
        public int FileCount {get;private set;}
        public int SelectedCount {get;private set;}
        private bool expanded,current;
        public bool IsExpanded {get{return expanded;}set{if(expanded==value)return;expanded=value;Changed("IsExpanded");}}
        public bool IsSelected {get{return current;}set{if(current==value)return;current=value;Changed("IsSelected");}}
        public Action<FileTreeNode,bool> SelectionChanged;
        private bool selected,emptySelected;
        private FileBackupView backup;
        public FileTreeNode(){Children=new ObservableCollection<FileTreeNode>();}
        public bool? IsChecked
        {
            get { return Directory ? FileCount==0 ? (bool?)emptySelected : SelectedCount==0 ? (bool?)false : SelectedCount==FileCount ? (bool?)true : null : selected; }
            set { if(SelectionChanged!=null) SelectionChanged(this,value??true); }
        }
        public string SizeText {get{return Display.Bytes(TotalBytes);}}
        public string Icon {get{return Directory ? "▣" : "▪";}}
        public string BackupStatus
        {
            get
            {
                if(Directory)return FileCount+" 个文件";
                if(backup==null)return "待备份";
                if(backup.Size!=Source.Size || backup.Modified!=Source.Modified)return "已变化，待备份";
                return HistoryStatus.Source(backup);
            }
        }
        public string CopiesText {get{return Directory || backup==null?"":backup.ValidCopies+" / "+backup.TargetCopies;}}
        public string Locations {get{return Directory || backup==null?"":backup.Locations;}}
        public string ArchiveName {get{return Directory || backup==null?"":backup.ArchiveName;}}
        public long ArchiveId {get{return backup==null?0:backup.ArchiveId;}}
        public void Apply(FileSelection selection,IDictionary<string,FileBackupView> backups)
        {
            bool? oldCheck=IsChecked;long oldBytes=TotalBytes;int oldCount=FileCount;
            if(Directory)
            {
                TotalBytes=0;FileCount=0;SelectedCount=0;
                foreach(var child in Children){child.Apply(selection,backups);TotalBytes+=child.TotalBytes;FileCount+=child.FileCount;SelectedCount+=child.SelectedCount;}
                emptySelected=selection.Includes(FullPath);
            }
            else
            {
                selected=selection.Includes(FullPath);TotalBytes=Source.Size;FileCount=1;SelectedCount=selected?1:0;
                FileBackupView next;backups.TryGetValue(Source.ArchivePath,out next);UpdateBackup(next);
            }
            if(oldCheck!=IsChecked)Changed("IsChecked");
            if(oldBytes!=TotalBytes)Changed("SizeText");
            if(oldCount!=FileCount)Changed("BackupStatus");
        }
        internal bool SetCheckedValue(bool include){if(selected==include)return false;selected=include;SelectedCount=include?1:0;return true;}
        internal void SetEmptySelection(bool include){emptySelected=include;}
        internal void AdjustSelectedCount(int delta){SelectedCount+=delta;}
        internal void AdjustTotalBytes(long delta){if(delta==0)return;TotalBytes+=delta;Changed("SizeText");}
        internal long ApplyObservedSource(SourceItem value)
        {long delta=value.Size-TotalBytes;Source.Size=value.Size;Source.Modified=value.Modified;AdjustTotalBytes(delta);Changed("BackupStatus");return delta;}
        internal void NotifySelection(){Changed("IsChecked");}
        internal void UpdateBackup(FileBackupView value)
        {
            if(FileBackupView.Same(backup,value))return;
            backup=value;
            foreach(string property in new[]{"BackupStatus","CopiesText","Locations","ArchiveName"})Changed(property);
        }
        public IEnumerable<FileTreeNode> Files()
        {
            if(!Directory){yield return this;yield break;}
            foreach(var child in Children)foreach(var file in child.Files())yield return file;
        }
    }
    public sealed class FileTreeWorkspace
    {
        public ObservableCollection<FileTreeNode> Roots=new ObservableCollection<FileTreeNode>();
        public List<FileTreeNode> AllFiles=new List<FileTreeNode>();
        public int ExcludedCount;
        private readonly Dictionary<string,List<FileTreeNode>> filesByPath=new Dictionary<string,List<FileTreeNode>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string,List<FileTreeNode>> filesByArchive=new Dictionary<string,List<FileTreeNode>>(StringComparer.OrdinalIgnoreCase);
        private IDictionary<string,FileBackupView> priorBackups=new Dictionary<string,FileBackupView>();
        public int SelectedFileCount {get;private set;}
        public long SelectedBytes {get;private set;}
        private static void Index(Dictionary<string,List<FileTreeNode>> lookup,string key,FileTreeNode file){List<FileTreeNode> nodes;if(!lookup.TryGetValue(key,out nodes)){nodes=new List<FileTreeNode>();lookup.Add(key,nodes);}nodes.Add(file);}

        public void Build(IEnumerable<LoadedSource> loaded,BackupExclusions exclusions,IEnumerable<string> protectedRoots,Action<FileTreeNode,bool> selectionChanged)
        {
            Roots.Clear();AllFiles.Clear();ExcludedCount=0;filesByPath.Clear();filesByArchive.Clear();SelectedFileCount=0;SelectedBytes=0;
            var protectedPaths=protectedRoots.Select(p=>Path.GetFullPath(p).TrimEnd('\\')).ToArray();
            foreach(var source in loaded.OrderBy(x=>x.Root,StringComparer.OrdinalIgnoreCase))
            {
                string scope=source.Root.TrimEnd('\\');
                var root=new FileTreeNode{Name=source.Root,FullPath=scope,Directory=true,IsExpanded=true,SelectionChanged=selectionChanged};
                var folders=new Dictionary<string,FileTreeNode>(StringComparer.OrdinalIgnoreCase); folders[scope]=root;string excludedPrefix=null;
                foreach(var item in source.Files.OrderBy(x=>x.Path,StringComparer.OrdinalIgnoreCase))
                {
                    string path=Path.GetFullPath(item.Path);
                    // Use exact casing for the shortcut: Regex rules may override IgnoreCase.
                    if(excludedPrefix!=null&&path.StartsWith(excludedPrefix,StringComparison.Ordinal)){ExcludedCount++;continue;}
                    string prefix;if(exclusions.Match(path,out prefix)!=null){excludedPrefix=prefix;ExcludedCount++;continue;}
                    string protectedPath=protectedPaths.FirstOrDefault(p=>path.Equals(p,StringComparison.OrdinalIgnoreCase)||path.StartsWith(p+"\\",StringComparison.OrdinalIgnoreCase));
                    if(protectedPath!=null){excludedPrefix=path.Length>protectedPath.Length?path.Substring(0,protectedPath.Length+1):null;ExcludedCount++;continue;}
                    if(!path.StartsWith(scope+"\\",StringComparison.OrdinalIgnoreCase))continue;
                    string directory=Path.GetDirectoryName(path); var missing=new Stack<string>(); string cursor=directory.TrimEnd('\\');
                    while(!folders.ContainsKey(cursor)){missing.Push(cursor);cursor=Path.GetDirectoryName(cursor);if(string.IsNullOrEmpty(cursor))throw new IOException("索引路径超出文件夹范围。");cursor=cursor.TrimEnd('\\');}
                    FileTreeNode parent=folders[cursor];
                    while(missing.Count>0)
                    {
                        string folder=missing.Pop();var node=new FileTreeNode{Name=Path.GetFileName(folder),FullPath=folder,Directory=true,Parent=parent,SelectionChanged=selectionChanged};
                        parent.Children.Add(node);folders[folder]=node;parent=node;
                    }
                    item.ArchivePath=Display.ArchivePath(path);
                    var file=new FileTreeNode{Name=Path.GetFileName(path),FullPath=path,Directory=false,Source=item,Parent=parent,SelectionChanged=selectionChanged};
                    parent.Children.Add(file);AllFiles.Add(file);Index(filesByPath,path,file);Index(filesByArchive,item.ArchivePath,file);
                }
                if(root.Children.Count>0)Roots.Add(root);
            }
        }
        public void Apply(FileSelection selection,IDictionary<string,FileBackupView> backups)
        {
            foreach(var root in Roots)root.Apply(selection,backups);
            SelectedFileCount=0;SelectedBytes=0;
            foreach(var nodes in filesByPath.Values)if(nodes[0].IsChecked==true){SelectedFileCount++;SelectedBytes+=nodes[0].Source.Size;}
            priorBackups=backups;
        }
        public void ChangeSelection(FileTreeNode node,bool include)
        {
            if(node.Directory&&node.FileCount==0){node.SetEmptySelection(include);node.NotifySelection();return;}
            var changed=new HashSet<FileTreeNode>();
            foreach(var file in node.Files().GroupBy(value=>value.FullPath,StringComparer.OrdinalIgnoreCase).Select(group=>group.First()))
            {
                var duplicates=filesByPath[file.FullPath];bool previous=duplicates[0].IsChecked==true;
                foreach(var duplicate in duplicates)if(duplicate.SetCheckedValue(include))
                {
                    changed.Add(duplicate);
                    for(var parent=duplicate.Parent;parent!=null;parent=parent.Parent){parent.AdjustSelectedCount(include?1:-1);changed.Add(parent);}
                }
                if(previous!=include){SelectedFileCount+=include?1:-1;SelectedBytes+=include?file.Source.Size:-file.Source.Size;}
            }
            foreach(var value in changed)value.NotifySelection();
        }
        public void RefreshBackups(IDictionary<string,FileBackupView> backups)
        {
            if(ReferenceEquals(priorBackups,backups))return;
            foreach(var pair in backups){FileBackupView old;if(!priorBackups.TryGetValue(pair.Key,out old)||!FileBackupView.Same(old,pair.Value)){List<FileTreeNode> nodes;if(filesByArchive.TryGetValue(pair.Key,out nodes))foreach(var node in nodes)node.UpdateBackup(pair.Value);}}
            foreach(string key in priorBackups.Keys)if(!backups.ContainsKey(key)){List<FileTreeNode> nodes;if(filesByArchive.TryGetValue(key,out nodes))foreach(var node in nodes)node.UpdateBackup(null);}
            priorBackups=backups;
        }
        public int UpdateObservedSources(IEnumerable<SourceItem> observed)
        {
            if(observed==null)return 0;int updated=0;var folders=new Dictionary<FileTreeNode,long>();
            foreach(var value in observed)
            {
                List<FileTreeNode> nodes;if(!filesByPath.TryGetValue(value.Path,out nodes)||!nodes.Any(node=>node.Source.Size!=value.Size||node.Source.Modified!=value.Modified||node.TotalBytes!=value.Size))continue;
                if(nodes[0].IsChecked==true)SelectedBytes+=value.Size-nodes[0].TotalBytes;
                foreach(var node in nodes){long delta=node.ApplyObservedSource(value);if(delta!=0)for(var parent=node.Parent;parent!=null;parent=parent.Parent){long total;folders.TryGetValue(parent,out total);folders[parent]=total+delta;}}
                updated++;
            }
            foreach(var pair in folders)pair.Key.AdjustTotalBytes(pair.Value);return updated;
        }
        public List<FileTreeNode> SelectedFiles(){return AllFiles.Where(x=>x.IsChecked==true).GroupBy(x=>x.FullPath,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).ToList();}
    }
    public sealed class GroupRow : NotifyModel
    {
        public string Id {get;set;}
        private string name;
        public string Name {get{return name;}set{name=value;Changed("Name");}}
        public string Notes {get;set;}
        private string members,usage;
        public string Members {get{return members;}set{members=value;Changed("Members");}}
        public string Usage {get{return usage;}set{usage=value;Changed("Usage");}}
    }
    public sealed class DestinationRow
    {
        public string Id {get;set;}
        public string Protocol {get;set;}
        public string Location {get;set;}
        public string CapacityGiB {get;set;}
        public string GroupId {get;set;}
        public bool Enabled {get;set;}
        public string Usage {get;set;}
    }
    public sealed class ArchiveRow
    {
        public long Id {get;set;}
        public string Name {get;set;}
        public string Status {get;set;}
        public string Copies {get;set;}
        public string Size {get;set;}
        public string Locations {get;set;}
    }
    public sealed class RuleRow
    {
        public bool Enabled {get;set;}
        public string Kind {get;set;}
        public string Pattern {get;set;}
        public string Description {get;set;}
        public static RuleRow From(ExclusionRule rule){return new RuleRow{Enabled=rule.Enabled,Kind=rule.Kind=="Regex"?"正则匹配":"普通匹配(支持通配符)",Pattern=rule.Pattern,Description=rule.Description};}
        public ExclusionRule ToRule(){return new ExclusionRule{Enabled=Enabled,Kind=Kind=="正则匹配"?"Regex":"Wildcard",Pattern=Pattern,Description=Description};}
    }
}
