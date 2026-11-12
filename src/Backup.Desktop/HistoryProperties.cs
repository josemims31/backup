using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Backup.Core;
namespace Backup.Desktop
{
    public sealed partial class BackupHistoryNode
    {
        // Read metadata only when properties are requested; never materialize lazy tree children.
        internal Func<IEnumerable<FileHistoryRow>> PropertyFiles;
        internal string ContentState;
        public bool NeedsRepair {get{return HistoryStatus.NeedsRepair(State);}}
        public Brush NameForeground {get{return IsMissingArchive||Entry!=null&&!Entry.Directory&&State!=null&&State.StartsWith("损坏",StringComparison.Ordinal)?OperationTextColors.Failed:NeedsRepair?Brushes.Blue:Brushes.Black;}}
        public Brush StateForeground {get{return HistoryStatus.Color(State);}}
        public string ArchiveBrowseNotice {get{return HistoryStatus.BrowseNotice(this);}}
        public bool IsArchiveBrowseBlocked {get{return ArchiveBrowseNotice!=null;}}
    }
    internal sealed class HistoryPropertyLine
    {
        internal string Label,Text;
        internal Brush Foreground;
        internal bool Private;
    }
    internal sealed class HistoryPropertySheet
    {
        internal string Title;
        internal readonly List<HistoryPropertyLine> Lines=new List<HistoryPropertyLine>();
        internal void Add(string label,string text,Brush color=null,bool privateValue=false){Lines.Add(new HistoryPropertyLine{Label=label,Text=text??"",Foreground=color??Brushes.Black,Private=privateValue});}
        internal string DetailsText {get{return string.Join("\n",Lines.Where(line=>!line.Private).Select(line=>line.Label+line.Text));}}
    }
    public sealed partial class DesktopBackend
    {
        private static string PropertyWarehouseName(StorageTopology topology,string id)
        {var group=topology.Groups.FirstOrDefault(value=>value.Id=="single:"+id);return group==null?id:group.Name;}
        private static string PropertyTaskName(WorkspaceDefinition workspace,string id)
        {if(id!=null&&id.Contains(","))return string.Join("；",id.Split(',').Select(value=>PropertyTaskName(workspace,value)));var task=workspace.Tasks.FirstOrDefault(value=>value.Id==id);return id=="__catalog__"?"数据库":task==null?id:task.Name+(task.Deleted?"（已删除）":"");}
        private IList<StorageEntry> PropertyEntries(BackupHistoryNode node)
        {
            if(node.RawLoaded)return node.Children.Where(child=>child.Entry!=null&&!child.IsMissingArchive).Select(child=>child.Entry).ToList();
            var cached=Catalog.GetInventoryFolder(node.DestinationId,node.Entry.Location);return cached==null?null:JsonSettings.Decode(cached.Json,new List<StorageEntry>());
        }
        private string PropertyFolderSize(BackupHistoryNode node)
        {
            var entries=PropertyEntries(node);if(entries==null)return "未知";
            if(entries.Any(entry=>entry.Directory||entry.Size<0))return "未知（仅显示当前目录）";return Display.Bytes(entries.Sum(entry=>entry.Size));
        }
        private static void PropertyWarehouses(HistoryPropertySheet sheet,IEnumerable<FileHistoryRow> files,StorageTopology topology)
        {
            sheet.Add("关联仓库：","");
            bool first=true;
            foreach(var group in files.Where(row=>!string.IsNullOrEmpty(row.DestinationId)).GroupBy(row=>new{row.DestinationId,row.ArchiveId}).OrderBy(group=>PropertyWarehouseName(topology,group.Key.DestinationId),StringComparer.CurrentCultureIgnoreCase).ThenBy(group=>group.Key.ArchiveId))
            {
                string state=HistoryStatus.Summarize(group,group.Key.DestinationId).Text;
                sheet.Add("",PropertyWarehouseName(topology,group.Key.DestinationId)+" · "+group.First().ArchiveName+" · "+state,HistoryStatus.Color(state));first=false;
            }
            if(first)sheet.Add("","无");
        }
        private void PropertyPasswords(HistoryPropertySheet sheet,IEnumerable<long> ids,bool includePasswords)
        {
            if(!includePasswords)return;var archives=ids.Where(id=>id>0).Distinct().ToList();
            foreach(long id in archives)sheet.Add("解压密码：",(archives.Count>1?Catalog.GetArchive(id).ArchiveName+" · ":"")+(Catalog.GetArchivePassword(id)??""),privateValue:true);
        }
        internal HistoryPropertySheet ReadHistoryProperties(BackupHistoryNode node,bool includePasswords=true)
        {
            var topology=Catalog.LoadTopology();var workspace=JsonSettings.Decode(Catalog.GetSetting("workspace_v3"),new WorkspaceDefinition());var sheet=new HistoryPropertySheet{Title="备份属性"};
            if(node.IsWarehouseRoot)
            {
                sheet.Title="仓库属性";
                var tasks=workspace.Tasks.Where(task=>!task.Deleted&&task.TargetIds.Any(id=>topology.Groups.Any(group=>group.Id==id)&&topology.ResolveDestinations(id,false).Contains(node.DestinationId))).Select(task=>task.Name).Distinct().ToList();
                if(DatabaseBackupTargets().Any(id=>topology.Groups.Any(group=>group.Id==id)&&topology.ResolveDestinations(id,false).Contains(node.DestinationId)))tasks.Add("数据库");
                sheet.Add("关联任务：",tasks.Count==0?"无":string.Join("；",tasks));var destination=topology.Destinations.FirstOrDefault(value=>value.Id==node.DestinationId);sheet.Add("路径：",destination==null?node.PathPrefix:destination.Location);
                var entries=node.Entry==null?null:PropertyEntries(node);string counts="尚未读取";
                if(entries!=null){var known=new HashSet<string>(Catalog.GetWarehouseObjects(node.DestinationId).Select(item=>item.Name),destination!=null&&(RepositoryModules.IsFileSystem(destination.Protocol))?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);var files=entries.Where(entry=>!entry.Directory).ToList();counts=files.Count(entry=>known.Contains(entry.Name))+"/"+files.Count;}
                sheet.Add("有效文件/总文件：",counts);return sheet;
            }
            if(node.Entry!=null&&!node.Entry.Directory&&!node.ArchiveContainer)
            {
                sheet.Title="文件属性："+node.Name;sheet.Add("路径：",node.Entry.Location);sheet.Add("大小：",node.SizeText);sheet.Add("状态：",node.State,HistoryStatus.Color(node.State));sheet.Add("所在仓库：",PropertyWarehouseName(topology,node.DestinationId));if(node.IsMissingArchive&&node.Entry.Size<0)sheet.Add("说明：","现存备份版本仍引用此包；此行为异常包汇总占位，不代表根目录中存在实际文件。");PropertyPasswords(sheet,new[]{node.ArchiveId},includePasswords);return sheet;
            }
            if(node.ArchiveContainer)
            {
                sheet.Title="压缩包属性："+node.Name;
                if(node.ArchiveId==0){sheet.Add("大小：",node.SizeText);sheet.Add("关联仓库：","");sheet.Add("",PropertyWarehouseName(topology,node.DestinationId)+" · 未登记");sheet.Add("包内文件：","未登记，暂无本地清单");return sheet;}
                sheet.Add("大小：",node.SizeText);
                PropertyPasswords(sheet,new[]{node.ArchiveId},includePasswords);
                sheet.Add("关联仓库：","");bool first=true;
                var placements=Catalog.VolumePlacements(node.ArchiveId).Where(place=>!place.GroupId.StartsWith("transfer:",StringComparison.Ordinal)).ToList();
                foreach(var group in placements.GroupBy(place=>place.DestinationId).OrderBy(group=>PropertyWarehouseName(topology,group.Key),StringComparer.CurrentCultureIgnoreCase))
                {string status=group.Key==node.DestinationId?(HistoryStatus.LocalProblem(node)??HistoryStatus.Combined(group.Select(place=>place.Status))):HistoryStatus.Combined(group.Select(place=>place.Status));string text=HistoryStatus.Text(status);sheet.Add("",PropertyWarehouseName(topology,group.Key)+" · "+text,HistoryStatus.Color(text));first=false;}
                if(first)sheet.Add("","无");
                string health=HistoryStatus.LocalProblem(node)??HistoryStatus.Combined(placements.Where(place=>place.DestinationId==node.DestinationId).Select(place=>place.Status));
                var files=Catalog.GetArchivedFiles(node.ArchiveId);sheet.Add("包内文件：",files.Count+" 个"+(node.IsMissingArchive?"（数据库记录）":""));foreach(var file in files){string state=HistoryStatus.File(file,health);sheet.Add("",(string.IsNullOrEmpty(file.EntryPath)?file.Path:file.EntryPath)+" · "+Display.Bytes(file.Size)+" · "+state+(file.TaskId=="__catalog__"?" · 快照时间："+HistoryStatus.SnapshotTime(file):""),HistoryStatus.Color(state));}return sheet;
            }
            var rows=(node.PropertyFiles!=null?node.PropertyFiles():node.Replicas.Count>0?node.Replicas:node.File==null?Enumerable.Empty<FileHistoryRow>():new[]{node.File}).ToList();
            string statusText=node.State;if(string.IsNullOrEmpty(statusText))statusText=rows.Count==0?"文件夹":HistoryStatus.Summarize(rows).Text;
            if(node.IsTaskRoot)sheet.Title="任务属性";else sheet.Add("文件名：",node.Name,HistoryStatus.NeedsRepair(statusText)?Brushes.Blue:Brushes.Black);
            var taskNames=rows.Select(row=>PropertyTaskName(workspace,row.TaskId)).Distinct().ToList();sheet.Add("任务：",taskNames.Count>0?string.Join("；",taskNames):string.IsNullOrEmpty(node.TaskName)?"无":node.TaskName);sheet.Add("状态：",statusText,HistoryStatus.Color(statusText));
            if(node.File!=null&&node.File.TaskId=="__catalog__")sheet.Add("快照时间：",HistoryStatus.SnapshotTime(node.File));
            sheet.Add("大小：",node.File!=null?Display.Bytes(node.File.Size):rows.Count>0?Display.Bytes(rows.GroupBy(row=>new{row.ArchiveId,row.Path}).Sum(group=>group.First().Size)):node.Entry!=null&&node.Entry.Directory?PropertyFolderSize(node):node.SizeText);
            if(node.Entry!=null){sheet.Add("关联仓库：","");sheet.Add("",PropertyWarehouseName(topology,node.DestinationId)+" · "+node.State,HistoryStatus.Color(node.State));}else PropertyWarehouses(sheet,rows,topology);
            PropertyPasswords(sheet,rows.Select(row=>row.ArchiveId),includePasswords);
            return sheet;
        }
    }
    public static partial class EditorDialogs
    {
        internal static void Properties(Window owner,HistoryPropertySheet sheet)
        {
            StackPanel body;Button close;var window=Form(owner,sheet.Title,out body,out close);close.Content="关闭";
            var document=new FlowDocument{PagePadding=new Thickness(8),FontFamily=window.FontFamily,FontSize=window.FontSize};
            foreach(var line in sheet.Lines){var paragraph=new Paragraph{Margin=new Thickness(string.IsNullOrEmpty(line.Label)?14:0,0,0,8)};if(!string.IsNullOrEmpty(line.Label))paragraph.Inlines.Add(new Run(line.Label));paragraph.Inlines.Add(new Run(line.Text){Foreground=line.Foreground});document.Blocks.Add(paragraph);}
            body.Children.Add(new RichTextBox{Document=document,IsReadOnly=true,MinHeight=140,MaxHeight=540,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Background=Brushes.White,BorderBrush=Brushes.LightGray});Save(close,window,delegate{});Show(window);
        }
    }
}
