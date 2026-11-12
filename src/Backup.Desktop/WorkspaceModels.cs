using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backup.Core;
namespace Backup.Desktop
{
    public sealed class SourceDefinition
    {
        public string Id {get;set;}
        public string Name {get;set;}
        public string Location {get;set;}
        public bool Deleted {get;set;}
        public override string ToString(){return Name+" · "+Location;}
    }
    public sealed class BackupTaskDefinition
    {
        public string Id {get;set;}
        public string Name {get;set;}
        public string Password {get;set;}
        public BackupTaskDefinition(){Password="";}
        public bool UseCustomPassword;
        public bool RecoveryPendingConfirmation;
        public bool Deleted,PendingTargetReplacement;
        public int Copies=1;
        public int RetentionYears=1;
        // Keep the old serialized field readable for existing task settings.
        public int RetentionValue;
        public string RetentionUnit;
        public int SavedTimeValue{get{return string.IsNullOrEmpty(RetentionUnit)?Math.Max(1,RetentionYears):RetentionValue;}}
        public string SavedTimeUnit{get{return string.IsNullOrEmpty(RetentionUnit)?"year":RetentionUnit;}}
        public int RequiredCopies {get{return TargetIds.Distinct().Count();}}
        public List<string> SourceIds=new List<string>();
        public List<string> TargetIds=new List<string>();
        public FileSelection Selection=new FileSelection();
        public override string ToString(){return Name;}
    }
    public sealed class WorkspaceDefinition
    {
        public List<SourceDefinition> Sources=new List<SourceDefinition>();
        public List<BackupTaskDefinition> Tasks=new List<BackupTaskDefinition>();
    }
    public sealed partial class WarehouseView : NotifyModel
    {
        public string HoverText {get;set;}
        public string Id {get;set;}
        public string Name {get;set;}
        public string Kind {get;set;}
        public string Members {get;set;}
        public string Usage {get;set;}
        public string Location {get;set;}
        public bool Deleted {get;set;}
        public override string ToString(){return Name;}
    }
    public sealed class ChoiceRow
    {
        public string ToolTip {get;set;}
        public bool Selected {get;set;}
        public string Id {get;set;}
        public string Name {get;set;}
        public override string ToString(){return Name;}
    }
    public sealed partial class BackupHistoryNode : NotifyModel
    {
        public StorageEntry Entry {get;set;}
        public string BrowseLocation {get;set;}
        public bool RawLoaded {get;set;}
        public System.Threading.Tasks.Task RawLoading {get;set;}
        internal int RawLoadingGeneration,RawRequestVersion;
        internal int RawReadInputVersion=-1;internal long RawInventoryVersion,RawCatalogStamp;internal string RawInventoryNotice;
        internal string DirectoryReadNotice;
        internal BackupHistoryNode Parent;
        internal string NavigationScope;
        public long ExpectedSize {get;set;}
        public long ArchiveId {get;set;}
        public string DestinationId {get;set;}
        public string Name {get;set;}
        private string details;
        public Func<string> DetailsFactory;
        public string Details{get{if(details==null&&DetailsFactory!=null){details=DetailsFactory();DetailsFactory=null;}return details;}set{details=value;DetailsFactory=null;}}
        public string ArchiveDetails {get;set;}
        private bool expanded;public bool IsExpanded {get{return expanded;}set{expanded=IsArchiveBrowseBlocked?false:value;Changed("IsExpanded");}}
        private bool current;public bool IsSelected {get{return current;}set{if(current==value)return;current=value;Changed("IsSelected");}}
        private string state;
        public string State {get{return state;}set{if(state==value)return;state=value;Changed("State");Changed("NeedsRepair");Changed("NameForeground");Changed("StateForeground");Changed("ArchiveBrowseNotice");Changed("IsArchiveBrowseBlocked");Changed("DirectoryChildren");}}
        public string PathPrefix {get;set;}
        public string TaskName {get;set;}
        public string WarehouseNames {get;set;}
        public bool ArchiveContainer {get;set;}
        public bool IsWarehouseRoot {get;set;}
        public bool IsTaskRoot {get;set;}
        public bool IsMissingArchive {get;set;}
        public string RecordedCopyState {get;set;}
        public System.Windows.Media.ImageSource TreeIcon {get{return IsWarehouseRoot?BrowserIcons.Repository:IsTaskRoot?BrowserIcons.Task:ArchiveContainer?BrowserIcons.Archive:BrowserIcons.Folder;}}
        public System.Windows.Media.ImageSource ListIcon {get{return IsDirectory?TreeIcon:BrowserIcons.IsArchiveName(Name)?BrowserIcons.Archive:null;}}
        public string TreeName {get{return IsTaskRoot?TaskName:IsWarehouseRoot&&Name!=null&&(Name.StartsWith("仓库：",StringComparison.Ordinal)||Name.StartsWith("仓库:",StringComparison.Ordinal))?Name.Substring(3):Name;}}
        public FileHistoryRow File {get;set;}
        public List<FileHistoryRow> Replicas {get;set;}
        public bool IsDirectory {get{return ArchiveContainer||(Entry==null?File==null:Entry.Directory);}}
        public string Icon {get{return IsDirectory?"▸":"";}}
        public string SizeText {get{return Entry==null?(File==null?"":Display.Bytes(File.Size)):Entry.Directory?"":Entry.Size<0?"未知":Display.Bytes(Entry.Size);}}
        public string VersionText {get{return Entry!=null?(ArchiveId==0?"":"#"+ArchiveId.ToString("D8")):File==null?"":"#"+File.ArchiveId.ToString("D8");}}
        public string ModifiedText {get{return File==null?"":DateTime.FromFileTimeUtc(File.Modified).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");}}
        public IEnumerable<BackupHistoryNode> DirectoryChildren {get{if(IsArchiveBrowseBlocked)yield break;foreach(var child in directoryNodes??Children.Where(c=>c.IsDirectory)){child.Parent=this;yield return child;}}}
        private readonly object childrenGate=new object();private Func<List<BackupHistoryNode>> childrenFactory;private List<BackupHistoryNode> directoryNodes;
        private List<BackupHistoryNode> children;
        internal bool TryGetLoadedChildren(out List<BackupHistoryNode> value)
        {
            value=null;if(!System.Threading.Monitor.TryEnter(childrenGate))return false;
            try{if(childrenFactory!=null)return false;value=children;return true;}finally{System.Threading.Monitor.Exit(childrenGate);}
        }
        public List<BackupHistoryNode> Children {get{lock(childrenGate){if(childrenFactory!=null){children=childrenFactory();childrenFactory=null;foreach(var child in children)child.Parent=this;}return children;}}set{lock(childrenGate){children=value;childrenFactory=null;directoryNodes=null;if(children!=null)foreach(var child in children)child.Parent=this;}Changed("Children");Changed("DirectoryChildren");}}
        public void SetLazyChildren(List<BackupHistoryNode> directories,Func<List<BackupHistoryNode>> load){lock(childrenGate){directoryNodes=directories;childrenFactory=load;children=null;}}
        public BackupHistoryNode(){Children=new List<BackupHistoryNode>();Replicas=new List<FileHistoryRow>();}
        internal void UpdateMetadata(BackupHistoryNode value)
        {
            Entry=value.Entry;Name=value.Name;BrowseLocation=value.BrowseLocation;DestinationId=value.DestinationId;PathPrefix=value.PathPrefix;ArchiveId=value.ArchiveId;ExpectedSize=value.ExpectedSize;State=value.State;TaskName=value.TaskName;WarehouseNames=value.WarehouseNames;Details=value.Details;DetailsFactory=value.DetailsFactory;ArchiveDetails=null;
            IsWarehouseRoot=value.IsWarehouseRoot;IsTaskRoot=value.IsTaskRoot;IsMissingArchive=value.IsMissingArchive;RecordedCopyState=value.RecordedCopyState;
            foreach(string property in new[]{"Name","State","SizeText","TaskName","WarehouseNames","Details","VersionText","TreeIcon","TreeName","ListIcon","NameForeground","StateForeground"})Changed(property);
        }
    }
    public static class WorkspaceMigration
    {
        public static WorkspaceDefinition Load(BackupCatalog catalog)
        {
            string json=catalog.GetSetting("workspace_v3");if(json!=null){var saved=JsonSettings.Decode(json,new WorkspaceDefinition());bool compact=saved.Tasks.Any(t=>t.Deleted)||saved.Sources.Any(s=>s.Deleted);if(compact){
                var removedSources=new HashSet<string>(saved.Sources.Where(s=>s.Deleted).Select(s=>s.Id));foreach(var task in saved.Tasks){task.SourceIds.RemoveAll(removedSources.Contains);if(task.Deleted){task.Password="";task.SourceIds.Clear();task.Selection=new FileSelection();}}
                saved.Tasks.RemoveAll(t=>t.Deleted&&!catalog.TaskHasPreservedArchives(t.Id));saved.Sources.RemoveAll(s=>s.Deleted);string next=JsonSettings.Encode(saved);if(next!=json)catalog.SetSetting("workspace_v3",next);
            }return saved;}
            var workspace=new WorkspaceDefinition();catalog.SetSetting("workspace_v3",JsonSettings.Encode(workspace));return workspace;
        }
        public static void Validate(WorkspaceDefinition workspace,StorageTopology topology)
        {
            topology.Normalize();if(workspace.Sources.Any(s=>string.IsNullOrWhiteSpace(s.Id)||string.IsNullOrWhiteSpace(s.Name)||!Path.IsPathRooted(s.Location)) || workspace.Sources.Select(s=>s.Id).Distinct().Count()!=workspace.Sources.Count)throw new ArgumentException("名称、目录或标识无效。");
            if(workspace.Sources.Where(s=>!s.Deleted).Select(s=>StoragePath.NormalizeRoot(s.Location)).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=workspace.Sources.Count(s=>!s.Deleted))throw new ArgumentException("目录已存在。");
            if(workspace.Tasks.Select(t=>t.Id).Distinct().Count()!=workspace.Tasks.Count)throw new ArgumentException("任务标识重复。");
            foreach(var task in workspace.Tasks){if(task.Deleted)continue;if(string.IsNullOrWhiteSpace(task.Name))throw new ArgumentException("任务名称无效。");
                if(task.SavedTimeValue<1||!new[]{"day","month","year"}.Contains(task.SavedTimeUnit))throw new ArgumentException("保存时间请输入正整数，并选择天、月或年。");
                if(task.UseCustomPassword&&(string.IsNullOrEmpty(task.Password)||task.Password.Length>99))throw new ArgumentException("任务的自定义密码长度应为 1~99 位。");
                if(task.SourceIds.Any(id=>!workspace.Sources.Any(s=>s.Id==id&&!s.Deleted))||task.TargetIds.Any(id=>!topology.Groups.Any(g=>g.Id==id&&!g.Retired)))throw new ArgumentException("任务引用了不存在的文件夹或仓库。");
                task.Selection.Normalize();}
        }
        public static bool SatisfiesTargets(StorageTopology topology,IList<string> targets){if(targets.Count==0)return false;var scope=topology.Scope(targets);var plan=GroupAllocator.Plan(scope,new DestinationUsage[0],1,targets.Count,null);return plan.StructurallyComplete&&plan.TargetCopies>0&&(scope.PolicyRoots!=null||plan.TargetCopies==targets.Count);}
        public static int PolicyCopies(StorageTopology topology,IList<string> targets){return targets.Count==0?0:GroupAllocator.Plan(topology.Scope(targets),new DestinationUsage[0],1,targets.Count,null).TargetCopies;}
        public static void ValidateTargetPolicy(BackupTaskDefinition task,StorageTopology topology){if(task.RequiredCopies==0)return;if(!SatisfiesTargets(topology,task.TargetIds))throw new ArgumentException("所选仓库或组合存在重叠、已移除成员，无法满足全部备份要求。请调整成员。");task.Copies=PolicyCopies(topology,task.TargetIds);}
    }
}
