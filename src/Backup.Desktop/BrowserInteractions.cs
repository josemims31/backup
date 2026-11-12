using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
namespace Backup.Desktop
{
    public sealed partial class MainWindowController
    {
        private bool independentDownloadActive;private CancellationTokenSource independentDownloadCancellation;
        private bool CanDownload{get{return !disposed&&!closeAfterWork&&!independentDownloadActive&&(!MutationBlocked||backupBusy&&!repairingBackups&&(operations.ActiveKind==OperationKind.Backup||operations.ActiveKind==OperationKind.Schedule));}}
        private async Task RunDownloadAsync(Func<Task> action)
        {
            if(!CanDownload)throw new OperationBusyException();
            if(!MutationBlocked){await RunOperationAsync(OperationKind.Download,action);return;}
            independentDownloadActive=true;
            using(var cancellation=new CancellationTokenSource()){independentDownloadCancellation=cancellation;
                using(operations.Read(OperationKind.Download,cancellation))try{using(Backend.IndependentDownload(cancellation.Token))await action();}
                finally{independentDownloadActive=false;independentDownloadCancellation=null;}
            }
        }
        private BackupHistoryNode historyFolder;private Point dragStart;private bool dragArmed;
        private static T Ancestor<T>(DependencyObject item) where T:DependencyObject{while(item!=null&&!(item is T))item=VisualTreeHelper.GetParent(item);return item as T;}
        private void Context(FrameworkElement control,Action edit,Func<Task> delete){var menu=new ContextMenu();var editItem=new MenuItem{Header="编辑"};editItem.Click+=delegate{if(!MutationBlocked)Guard(edit);};menu.Items.Add(editItem);var deleteItem=new MenuItem{Header="删除"};deleteItem.Click+=async delegate{if(!MutationBlocked)await GuardAsync(delete);};menu.Items.Add(deleteItem);menu.Opened+=delegate{editItem.IsEnabled=!MutationBlocked;deleteItem.IsEnabled=!MutationBlocked;};control.ContextMenu=menu;}
        private void BindInteractions(){
            Button("RemoveTask").Click+=async delegate{await GuardAsync(RemoveTaskAsync);};Button("RunMaintenance").Click+=async delegate{await GuardAsync(RunMaintenanceAsync);};
            var selector=Control<ComboBox>("TaskSelector");Context(selector,()=>EditTask(CurrentTask),RemoveTaskAsync);
            Grid("SourceGrid").MouseDoubleClick+=delegate{if(!MutationBlocked&&Grid("SourceGrid").SelectedItem!=null)Guard(()=>EditSource(Grid("SourceGrid").SelectedItem as SourceDefinition));};
            Grid("WarehouseGrid").MouseDoubleClick+=delegate{if(!MutationBlocked)Guard(EditWarehouse);};Context(Grid("SourceGrid"),()=>EditSource(Grid("SourceGrid").SelectedItem as SourceDefinition),RemoveSourceAsync);Context(Grid("WarehouseGrid"),EditWarehouse,RemoveWarehouseAsync);
            foreach(string name in new[]{"SourceGrid","WarehouseGrid","HistoryPreview"})Grid(name).PreviewMouseRightButtonDown+=delegate(object sender,MouseButtonEventArgs e){var row=Ancestor<DataGridRow>(e.OriginalSource as DependencyObject);if(row!=null){var grid=(DataGrid)sender;if(!row.IsSelected){grid.UnselectAll();row.IsSelected=true;}row.Focus();}};
            var tree=Control<TreeView>("HistoryTree");tree.AddHandler(TreeViewItem.ExpandedEvent,new RoutedEventHandler(delegate(object s,RoutedEventArgs e){var item=e.OriginalSource as TreeViewItem;var node=item==null?null:item.DataContext as BackupHistoryNode;if(browserViewChanges==0&&node!=null&&node.Entry!=null&&!node.RawLoaded)BeginWarehouseFolder(node,false,false);}),true);tree.PreviewMouseLeftButtonUp+=delegate(object sender,MouseButtonEventArgs e){var item=Ancestor<TreeViewItem>(e.OriginalSource as DependencyObject);if(item==null)return;var expander=Ancestor<System.Windows.Controls.Primitives.ToggleButton>(e.OriginalSource as DependencyObject);if(expander==null){item.IsExpanded=true;item.IsSelected=true;var node=item.DataContext as BackupHistoryNode;if(node!=null&&(historyFolder!=node||node.Entry!=null&&!node.ArchiveContainer&&!node.RawLoaded))ShowHistoryFolder(node);if(node!=null)BeginViewedIntegrity(node);}};
            tree.PreviewMouseRightButtonDown+=delegate(object sender,MouseButtonEventArgs e){var item=Ancestor<TreeViewItem>(e.OriginalSource as DependencyObject);if(item!=null){item.IsSelected=true;item.Focus();}};
            var preview=Grid("HistoryPreview");preview.SelectionChanged+=delegate{var node=preview.SelectedItem as BackupHistoryNode;if(browserViewChanges==0&&node!=null){ShowHistoryDetails(node);if(node.ArchiveContainer)BeginViewedIntegrity(node);}};preview.MouseDoubleClick+=delegate{var node=preview.SelectedItem as BackupHistoryNode;if(node!=null&&node.IsDirectory){ShowHistoryFolder(node);BeginViewedIntegrity(node);}};
            tree.PreviewKeyDown+=delegate(object sender,KeyEventArgs e){var node=tree.SelectedItem as BackupHistoryNode;if(e.Key==Key.Right&&node!=null&&node.Entry!=null&&!node.ArchiveContainer&&!node.RawLoaded){node.IsExpanded=true;BeginWarehouseFolder(node);e.Handled=true;}};
            foreach(FrameworkElement control in new FrameworkElement[]{tree,preview}){
                var menu=new ContextMenu();control.ContextMenu=menu;PopulateHistoryMenu(menu,control==tree);menu.Opened+=delegate{PopulateHistoryMenu(menu,control==tree);};
                control.PreviewMouseLeftButtonDown+=delegate(object sender,MouseButtonEventArgs e){dragStart=e.GetPosition(control);dragArmed=Ancestor<DataGridRow>(e.OriginalSource as DependencyObject)!=null||Ancestor<TreeViewItem>(e.OriginalSource as DependencyObject)!=null;};
                control.PreviewMouseLeftButtonUp+=delegate{dragArmed=false;};control.PreviewMouseMove+=async delegate(object sender,MouseEventArgs e){if(!dragArmed||!CanDownload||e.LeftButton!=MouseButtonState.Pressed)return;Point point=e.GetPosition(control);if(Math.Abs(point.X-dragStart.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(point.Y-dragStart.Y)<SystemParameters.MinimumVerticalDragDistance)return;dragArmed=false;await GuardAsync(()=>DragHistoryAsync(control,control==tree));};
            }
        }
        private void PopulateHistoryMenu(ContextMenu menu,bool fromTree)
        {
            menu.Items.Clear();var nodes=SelectedHistory(fromTree);bool available=nodes.Count>0&&!nodes.Any(node=>node.IsArchiveBrowseBlocked);var download=new MenuItem{Header="下载",IsEnabled=CanDownload&&available};download.Click+=async delegate{await GuardAsync(()=>DownloadSelectedAsync(fromTree));};menu.Items.Add(download);
            if(available&&nodes.All(node=>node.Entry==null||node.ArchiveContainer))foreach(var choice in BackupHistoryTree.DownloadWarehouses(nodes,Backend.Catalog.LoadTopology(),Backend.Catalog.HasCompleteArchiveAt))
            {var item=new MenuItem{Header="下载("+choice.Name+")",Tag=choice.Id,ToolTip=choice.ToolTip,IsEnabled=CanDownload};string warehouse=choice.Id;item.Click+=async delegate{await GuardAsync(()=>DownloadSelectedAsync(fromTree,warehouse));};menu.Items.Add(item);}
            if(!IsFolderHistoryView&&nodes.Count>0&&nodes.All(DesktopBackend.IsRealWarehouseDeleteNode)){var delete=new MenuItem{Header="删除",IsEnabled=!MutationBlocked};delete.Click+=async delegate{var selected=SelectedHistory(fromTree);await GuardAsync(()=>DeleteWarehouseEntriesAsync(selected));};menu.Items.Add(delete);}
            if(IsFolderHistoryView&&SelectedBackupNode!=null&&nodes.Count>0){var delete=new MenuItem{Header="从此备份版本删除",IsEnabled=!MutationBlocked};delete.Click+=async delegate{var selected=SelectedHistory(fromTree);await GuardAsync(()=>DeleteSelectedNodeFilesAsync(selected));};menu.Items.Add(delete);}
            var properties=new MenuItem{Header="属性"};properties.Click+=async delegate{await GuardAsync(async delegate{var selected=SelectedHistory(fromTree);if(selected.Count>0){var node=selected[0];if(node.RawLoading!=null)await node.RawLoading;var sheet=await Task.Run(()=>Backend.ReadHistoryProperties(node));if(!disposed){warehouseDetailsGeneration++;Text("HistoryDetails").Text=sheet.DetailsText;EditorDialogs.Properties(Window,sheet);}}});};menu.Items.Add(properties);
        }
        private IList<BackupHistoryNode> SelectedHistory(bool fromTree){var result=fromTree?new[]{Control<TreeView>("HistoryTree").SelectedItem as BackupHistoryNode}.Where(n=>n!=null).ToList():Grid("HistoryPreview").SelectedItems.OfType<BackupHistoryNode>().ToList();return result;}
        private int historyNavigationGeneration;
        private Task historyFolderLoading=Task.FromResult(true);public Task HistoryFolderLoading{get{return historyFolderLoading;}}
        public void ShowHistoryFolder(BackupHistoryNode node)
        {if(node==null||!node.IsDirectory)return;if(node.Entry!=null&&!node.ArchiveContainer){BeginWarehouseFolder(node);return;}historyFolderLoading=ShowTaskFolderAsync(node);ObserveHistoryLoading(historyFolderLoading);}
        private async Task ShowTaskFolderAsync(BackupHistoryNode node,HistoryViewState previous=null)
        {
            int navigation=++historyNavigationGeneration;historyFolder=node;
            if(node.IsArchiveBrowseBlocked)
            {
                warehouseDetailsGeneration++;Text("HistoryPath").Text=node.Entry.Location;Text("HistoryPath").ToolTip=node.Entry.Location;
                Text("HistoryDetails").Text=node.Details+"\n"+node.ArchiveBrowseNotice+"\n属性中的包内文件来自数据库记录。";
                BindHistoryRows(node,new List<BackupHistoryNode>(),previous,node.ArchiveBrowseNotice);return;
            }
            warehouseDetailsGeneration++;
            List<BackupHistoryNode> children;if(!node.TryGetLoadedChildren(out children))
            {if(Grid("HistoryPreview").Items.Count==0)SetHistoryItems(new List<BackupHistoryNode>(),"正在读取本地目录");children=await Task.Run(()=>node.Children);}
            if(disposed||historyFolder!=node||navigation!=historyNavigationGeneration)return;var state=CaptureHistoryView(false);if(previous!=null&&previous.Valid&&previous.InputVersion==browserInputVersion)state.Scroll=previous.Scroll;
            Text("HistoryPath").Text=string.IsNullOrEmpty(node.PathPrefix)?node.Name:node.PathPrefix;Text("HistoryPath").ToolTip=Text("HistoryPath").Text;BindHistoryRows(node,children,state);if(Grid("HistoryPreview").SelectedItem==null)ShowHistoryDetails(node);
        }
        public Task DownloadToAsync(IList<BackupHistoryNode> nodes,string path,string warehouseId=null)
        {return RunDownloadAsync(()=>DownloadToCoreAsync(nodes,path,warehouseId));}
        private async Task DownloadToCoreAsync(IList<BackupHistoryNode> nodes,string path,string warehouseId=null)
        {
            if(nodes.Any(node=>node.IsArchiveBrowseBlocked))throw new IOException(nodes.First(node=>node.IsArchiveBrowseBlocked).ArchiveBrowseNotice);
            path=new PathHistory(scheduleJournal).Download(path);
            bool foreground=!independentDownloadActive&&operations.OwnsForeground;if(foreground)SetBusy(true,false,"正在下载文件…");else Log("开始下载："+path);
            Action<string> report=foreground?OperationReporter("正在下载："):null;
            try{var warnings=await Task.Run(()=>{using(var selection=Backend.MaterializeDownload(nodes,warehouseId,report)){DesktopBackend.CopyRestored(selection,path);return selection.Warnings;}});string message="下载完成："+path+(warnings.Count==0?"":"\n以下备份副本需检查或修复：\n"+string.Join("\n",warnings));if(foreground)SetOperationText(message,warnings.Count==0?OperationState.Succeeded:OperationState.Attention);else Log(message,warnings.Count==0?OperationState.Succeeded:OperationState.Attention);if(foreground)OperationProgress(100,false);}finally{if(foreground)SetBusy(false,false,null);}
        }
        private async Task DownloadSelectedAsync(bool fromTree,string warehouseId=null){var nodes=SelectedHistory(fromTree);if(nodes.Count==0||nodes.Any(node=>node.IsArchiveBrowseBlocked))return;string destination=ChooseDownloadDirectory("选择下载目录");if(destination!=null)await DownloadToAsync(nodes,destination,warehouseId);}
        private Task DragHistoryAsync(FrameworkElement source,bool fromTree)
        {return RunDownloadAsync(()=>DragHistoryCoreAsync(source,fromTree));}
        private async Task DragHistoryCoreAsync(FrameworkElement source,bool fromTree)
        {
            var nodes=SelectedHistory(fromTree);if(nodes.Count==0||nodes.Any(node=>node.IsArchiveBrowseBlocked))return;bool foreground=!independentDownloadActive&&operations.OwnsForeground;
            if(foreground)SetBusy(true,false,"拖到资源管理器的文件夹后松开，即可下载；按 Esc 取消。");var cancellation=independentDownloadCancellation==null?operationCancellation.Token:independentDownloadCancellation.Token;
            try
            {
                string path;
#if UI_TEST
                if(TestDropDestination!=null)path=TestDropDestination(nodes);else
#endif
                {
                var point=new VirtualFileDrop(nodes).Drag(()=>disposed||cancellation.IsCancellationRequested);
                if(!point.HasValue){if(foreground)SetOperationText("已取消拖放。",OperationState.Cancelled);return;}
                path=await DropFolderResolver.ResolveAsync(point.Value,cancellation);
                }
                if(path==null){if(foreground)SetOperationText("无法识别此目标，请拖到资源管理器的文件列表空白处或目标文件夹。",OperationState.Failed);return;}
                await DownloadToCoreAsync(nodes,path);
            }
            finally{if(foreground&&busy)SetBusy(false,false,null);}
        }
#if UI_TEST
        public Func<IList<BackupHistoryNode>,string> TestDropDestination;
#endif
        private void MaintenanceResult(IList<string> errors,string success,Backup.Core.CleanupResult cleanup=null){string result=success+(cleanup==null?"":"\n"+CleanupSummary(cleanup));if(errors.Count>0)result+="\n以下项目保留待重试：\n"+string.Join("\n",errors);SetOperationText(result,errors.Count>0?OperationState.Failed:cleanup!=null&&cleanup.PendingArchives>0?OperationState.Attention:OperationState.Succeeded);OperationProgress(100,false);Log(result);}
        public Task RemoveTaskAsync()
        {return RunOperationAsync(OperationKind.Configuration,()=>RemoveTaskCoreAsync());}
        private async Task RemoveTaskCoreAsync()
        {
            if(busy||CurrentTask==null)return;int? mode=EditorDialogs.DeleteChoice(Window,"删除任务："+CurrentTask.Name,"删除任务后不再执行此任务。关联计划会移除此任务；没有剩余任务的计划会一并删除。清理会直接删除此任务的 data-代号、node-代号目录和本地版本库；其他任务保留。选择保留时会导出待清理清单。",new[]{"清理已备份文件","不删除(导出清单)"});if(!mode.HasValue)return;
            string manifest=null;if(mode==1){var dialog=new Microsoft.Win32.SaveFileDialog{Title="导出待清理清单",Filter="文本清单|*.txt",DefaultExt=".txt",FileName="Backup-待清理-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".txt"};manifest=EditorDialogs.ChooseFile(Window,dialog,scheduleJournal,"ui_last_cleanup_export_v1");if(manifest==null)return;if(File.Exists(manifest))throw new IOException("请使用新的清单文件名。");}
            SaveTaskControls();string id=CurrentTask.Id;SetBusy(true,false,mode==1?"正在删除任务并保留备份…":"正在删除任务并清理备份…");var result=new Backup.Core.CleanupResult();
            try{var report=OperationReporter("");var next=await Task.Run(()=>Backend.DeleteTask(Workspace,id,mode==1,manifest,report));AdoptWorkspace(next);if(mode==0)result=Backend.LastCleanup;ApplyCommittedChanges();}finally{SetBusy(false,false,null); }
            MaintenanceResult(result.Errors,mode==1?"任务已删除；备份已保留。\n待清理清单："+manifest:result.Errors.Count==0?"任务、本地版本库及对应仓库目录已删除。":"任务已删除。未完成的目录清理将在手动修复完整性时重试。");
        }
        public Task RemoveSourceAsync()
        {return RunOperationAsync(OperationKind.Configuration,()=>RemoveSourceCoreAsync());}
        private async Task RemoveSourceCoreAsync()
        {
            if(busy)return;var source=Grid("SourceGrid").SelectedItem as SourceDefinition;if(source==null)return;int? mode=EditorDialogs.DeleteChoice(Window,"删除备份项："+source.Name,"将从所有任务移除此文件夹条目，文件标记为已删除。",new[]{"立即清理已备份文件","暂不清理"});if(!mode.HasValue)return;
            SaveTaskControls();var scope=Backend.SourceOperationArchives(Workspace,source.Id);string task=CurrentTask==null?null:CurrentTask.Id;SetBusy(true,false,mode==0?"正在删除备份项并清理失效的备份包…":"正在移除文件夹条目并标记已删除…");Backup.Core.CleanupResult result=null;
            try{Workspace=Backend.DeleteSource(Workspace,source.Id);loaded.Remove(source.Id);if(mode==0){var progress=CleanupReporter();result=await Task.Factory.StartNew(()=>Backend.CollectGarbageForArchives(scope,progress));}}finally{SetBusy(false,false,null);}
            MaintenanceResult(result==null?new List<string>():result.Errors,mode==0?"备份项已删除；仍有有效文件的压缩包已保留。":"备份项已删除，暂未清理；后续自动清理时仍会保留含有效文件的压缩包。",result);
        }
        public Task RemoveWarehouseAsync()
        {return RunOperationAsync(OperationKind.Maintenance,()=>RemoveWarehouseCoreAsync());}
        private async Task RemoveWarehouseCoreAsync()
        {
            if(busy)return;var row=Grid("WarehouseGrid").SelectedItem as WarehouseView;if(row==null)return;int? mode=EditorDialogs.DeleteChoice(Window,"删除仓库 / 组合："+row.Name,"立即迁移会先验证替代副本再删除旧副本。\n删除此仓库文件只清理数据库登记的备份对象，因此造成文件缺失需要手动修复。\n仅移除会保留文件在仓库。\n删除组合只处理写入该组合的副本，不清理成员中其他仓库的文件。",new[]{"立即迁移","删除此仓库文件(可能造成备份文件缺失)","仅移除(不删仓库文件)"});if(!mode.HasValue)return;
            SaveTaskControls();string task=CurrentTask==null?null:CurrentTask.Id;SetBusy(true,false,mode==0?"正在迁移备份并清理旧副本…":mode==1?"正在删除此仓库文件…":"正在移除仓库并保留文件…");IList<string> errors=new List<string>();Backup.Core.CleanupResult cleanup=null;
            try{Workspace=Backend.DeleteWarehouse(Workspace,row.Id,mode==0?"immediate":mode==1?"delete":"keep");if(mode==0){var progress=CleanupReporter();var messages=MaintenanceReporter();errors=await Task.Factory.StartNew(()=>Backend.RunPendingMaintenance(Workspace,messages,progress,null,true,row.Id));cleanup=Backend.LastCleanup;}else if(mode==1){var progress=CleanupReporter();cleanup=await Task.Factory.StartNew(()=>Backend.DeleteDetachedWarehouseObjects(row.Id,progress));errors=cleanup.Errors;}}finally{SetBusy(false,false,null);}
            MaintenanceResult(errors,mode==0?"仓库 / 组合已移除，迁移处理结束。":mode==1?"仓库 / 组合已移除。已删除 "+cleanup.DeletedObjects+" 个分卷文件，释放 "+Display.Bytes(cleanup.FreedBytes)+"；剩余仓库需手动确认修复。":"仓库 / 组合已移除，原文件保留，后续不会自动删除。",mode==0?cleanup:null);
        }
        public Task RunMaintenanceAsync()
        {return RunOperationAsync(OperationKind.Maintenance,()=>RunMaintenanceCoreAsync());}
        private async Task RunMaintenanceCoreAsync()
        {
            if(busy)return;string task=CurrentTask==null?null:CurrentTask.Id;SetBusy(true,false,"正在检查待迁移记录并清理失效备份…");IList<string> errors;Backup.Core.CleanupResult cleanup;
            try{var progress=CleanupReporter();var messages=MaintenanceReporter();errors=await Task.Factory.StartNew(()=>Backend.RunPendingMaintenance(Workspace,messages,progress));cleanup=Backend.LastCleanup;}finally{SetBusy(false,false,null);}
            MaintenanceResult(errors,"待迁移处理结束。",cleanup);
        }
    }
}
