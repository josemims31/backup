using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Backup.Core;
namespace Backup.Desktop
{
    public sealed partial class MainWindowController : IDisposable
    {
        public Window Window {get;private set;}
        public DesktopBackend Backend {get;private set;}
        public WorkspaceDefinition Workspace {get;private set;}
        public BackupTaskDefinition CurrentTask {get;private set;}
        public FileTreeWorkspace Tree {get;private set;}
        public FileSelection Selection {get{return SelectionDraft();}}
        public DesktopBackupResult LastBackup {get;private set;}
        public string LastError {get;private set;}
        public bool Busy {get{return busy;}}
        private bool busy,backupBusy,loading,closeAfterWork,disposed,historyDirty=true;
        private string historyTaskId;
        private Dictionary<string,LoadedSource> loaded=new Dictionary<string,LoadedSource>();
        private Dictionary<string,FileBackupView> views=new Dictionary<string,FileBackupView>(StringComparer.OrdinalIgnoreCase);
        private IList<FileHistoryRow> history=new List<FileHistoryRow>();
        public T Control<T>(string name) where T:class{return Window.FindName(name) as T;}
        private Button Button(string name){return Control<Button>(name);}
        private TextBlock Text(string name){return Control<TextBlock>(name);}
        private DataGrid Grid(string name){return Control<DataGrid>(name);}
        public MainWindowController(string baseDirectory)
        {
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Backup.Desktop.MainWindow.xaml"))Window=(Window)XamlReader.Load(stream);
            Text("ApplicationVersion").Text=Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            Window.Icon=ApplicationIcon.Image;
            foreach(string grid in new[]{"SourceGrid","WarehouseGrid","FileGrid","HistoryPreview","ScheduleGrid"})foreach(var column in Grid(grid).Columns.OfType<DataGridTextColumn>())
            {
                var style=new Style(typeof(TextBlock));var binding=column.Binding as System.Windows.Data.Binding;
                if(binding!=null)style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,new System.Windows.Data.Binding(grid=="WarehouseGrid"?"HoverText":binding.Path.Path)));
                style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));
                if(grid=="ScheduleGrid"&&binding!=null&&binding.Path.Path=="EnabledText")style.Setters.Add(new Setter(TextBlock.ForegroundProperty,new System.Windows.Data.Binding("StatusColor")));
                else if(grid=="WarehouseGrid"&&binding!=null&&binding.Path.Path=="ConnectionState"){style.Setters.Add(new Setter(TextBlock.ForegroundProperty,new System.Windows.Data.Binding("ConnectionForeground")));style.Setters.Add(new Setter(TextBlock.ToolTipProperty,new System.Windows.Data.Binding("ConnectionDetails")));}
                else if(grid=="HistoryPreview"&&binding!=null&&binding.Path.Path=="State")style.Setters.Add(new Setter(TextBlock.ForegroundProperty,new System.Windows.Data.Binding("StateForeground")));
                else if(binding!=null&&(binding.Path.Path=="BackupStatus"||binding.Path.Path=="State"))style.Setters.Add(new Setter(TextBlock.ForegroundProperty,new System.Windows.Data.Binding(binding.Path.Path){Converter=new OperationForegroundConverter()}));
                column.ElementStyle=style;
            }
            Backend=new DesktopBackend(baseDirectory);Backend.TransferNotice+=ReceiveTransferNotice;InitializeUploadStatus();Button("CloseUploadFailure").Click+=delegate{DismissUploadAlert();};InitializeBandwidthPresentation();InitializeUiPreferences();Control<ComboBox>("HistoryView").SelectedIndex=UiPreference("history_view_v3")=="warehouses"?1:0;Workspace=WorkspaceMigration.Load(Backend.Catalog);Tree=new FileTreeWorkspace();foreach(string diagnostic in RepositoryModules.Diagnostics)Log("仓库模块未加载："+diagnostic);
            Control<TreeView>("SourceTree").ItemsSource=Tree.Roots;
            Control<ComboBox>("TaskSelector").SelectionChanged+=async delegate{if(loading)return;var task=Control<ComboBox>("TaskSelector").SelectedItem as BackupTaskDefinition;if(task!=null)await GuardAsync(()=>SwitchTaskAsync(task.Id));};
            Button("AddTask").Click+=delegate{Guard(()=>EditTask(null));};Button("EditTask").Click+=delegate{Guard(()=>EditTask(CurrentTask));};
            Button("RefreshSources").Click+=async delegate{await GuardAsync(RefreshTaskAsync);};
            Button("AddSource").Click+=delegate{Guard(()=>EditSource(null));};Button("EditSource").Click+=delegate{Guard(()=>EditSource(Grid("SourceGrid").SelectedItem as SourceDefinition));};
            Button("RemoveSource").Click+=async delegate{await GuardAsync(RemoveSourceAsync);};
            Button("AddDestination").Click+=delegate{Guard(()=>{Backend.ServerTrust.BeginAttempt();EditorDialogs.Destination(Window,SaveDestination,null,null,Backend.LoadConnection,Backend.SaveConnection,SaveDestinationFromEditorAsync,scheduleJournal,ConnectionBrowserServices,OpenListServices);});};
            Button("AddWarehouse").Click+=delegate{Guard(()=>EditorDialogs.Warehouse(Window,Backend.Catalog.LoadTopology(),null,SaveWarehouse));};Button("AddMirrorWarehouse").Click+=delegate{Guard(()=>EditorDialogs.Warehouse(Window,Backend.Catalog.LoadTopology(),null,SaveWarehouse,"raid1"));};
            Button("CancelPathMove").Click+=delegate{Guard(CancelSelectedPathMove);};Grid("WarehouseGrid").SelectionChanged+=delegate{var selected=Grid("WarehouseGrid").SelectedItem as WarehouseView;Button("CancelPathMove").Visibility=selected!=null&&Backend.Catalog.GetWarehouseMoves().Any(m=>m.State=="copying"&&m.DestinationId==selected.Id.Substring(selected.Id.StartsWith("single:")?7:0))?Visibility.Visible:Visibility.Collapsed;};Button("EditWarehouse").Click+=delegate{Guard(EditWarehouse);};Button("RemoveWarehouse").Click+=async delegate{await GuardAsync(RemoveWarehouseAsync);};
            Button("SaveSelection").Click+=delegate{Guard(SaveSelection);};Button("ResetSelection").Click+=delegate{Guard(ResetSelection);};InitializeSelectionHeaders();Button("Settings").Click+=delegate{Guard(OpenSettings);};
            Button("EditExclusions").Click+=delegate{Guard(delegate{var rules=ExclusionDialog.Edit(Window,Backend.Rules());if(rules!=null)RunConfiguration(delegate{Backend.SaveRules(rules);ApplyCommittedChanges();Log("排除规则已保存；不会删除已有备份。");});});};
            Control<TreeView>("SourceTree").SelectedItemChanged+=delegate(object sender,RoutedPropertyChangedEventArgs<object> e){if(browserViewChanges==0)ShowNode(e.NewValue as FileTreeNode);};
            Grid("FileGrid").SelectionChanged+=delegate{if(browserViewChanges==0)ShowFileDetails(Grid("FileGrid").SelectedItem as FileTreeNode);};
            Grid("FileGrid").MouseDoubleClick+=EnterBackupFolder;
            Grid("FileGrid").AddHandler(Mouse.PreviewMouseDownEvent,new MouseButtonEventHandler(ToggleCheckbox),true);
            Button("StartBackup").Click+=async delegate{await GuardAsync(StartBackupAsync);};Button("StopAfterArchive").Click+=delegate{StopTransferWork(false);};
            Button("PauseBackup").Click+=delegate{var control=Backend.ActiveBackup;if(control==null||control.IsStopping||Backend.StopRequested||control.IsFinishing)return;if(control.IsManuallyPaused){control.Resume();Button("PauseBackup").Content="暂停";SetOperationText(repairingBackups?"已继续修复。":"已继续备份。");}else{control.Pause();Button("PauseBackup").Content="继续";SetOperationText("已暂停读取和上传；内存缓存保留，可继续任务。");}};
            Button("StopNow").Click+=delegate{StopTransferWork(true);};
            Button("CheckIntegrity").Click+=async delegate{await GuardAsync(CheckIntegrityAsync);};
            Button("CompactArchives").Click+=async delegate{await GuardAsync(CompactAllArchivesAsync);};
            Control<ComboBox>("HistoryView").SelectionChanged+=delegate{if(!loading){AdvanceBrowserView();SaveUiPreference("history_view_v3",IsFolderHistoryView?"folders":"warehouses");UpdateRemoteTaskActions();RebuildHistory();}};
            Control<ComboBox>("StatusWarehouseFilter").SelectionChanged+=delegate{if(!loading)RebuildHistory(true);};
            Control<TreeView>("HistoryTree").SelectedItemChanged+=delegate(object sender,RoutedPropertyChangedEventArgs<object> e){var node=e.NewValue as BackupHistoryNode;if(browserViewChanges==0){if(IsFolderHistoryView)SwitchHistoryTask(node);if(node!=null&&historyFolder!=node)ShowHistoryFolder(node);BeginViewedIntegrity(node);UpdateRemoteTaskActions();}};
            Control<TabControl>("Tabs").SelectionChanged+=delegate(object sender,SelectionChangedEventArgs e){if(e.OriginalSource!=Control<TabControl>("Tabs")||loading)return;int page=Control<TabControl>("Tabs").SelectedIndex;if(page==3)RebuildHistory();else{AdvanceBrowserView();if(page==2&&managersDirty)UpdateManagers();}};
            var warehouseFilter=Control<ComboBox>("StatusWarehouseFilter");var hintStyle=new Style(typeof(ComboBoxItem));hintStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,new System.Windows.Data.Binding("ToolTip")));warehouseFilter.ItemContainerStyle=hintStyle;warehouseFilter.SetBinding(FrameworkElement.ToolTipProperty,new System.Windows.Data.Binding("SelectedItem.ToolTip"){Source=warehouseFilter});InitializeWindowLayout();InitializeBrowserViewState();BindRecoveryActions();BindNodeActions();BindInteractions();Window.Closing+=OnClosing;Window.Closed+=delegate{Dispose();};
            BindCopyableMessages();UpdateManagers();string selectedTask=UiPreference(SelectedTaskPreference);CurrentTask=Workspace.Tasks.FirstOrDefault(t=>!t.Deleted&&t.Id==selectedTask)??Workspace.Tasks.FirstOrDefault(t=>!t.Deleted);LoadTaskControls();RefreshCatalog();
            Window.Loaded+=async delegate{initialFileLists=InitializeFileListsAsync();await GuardAsync(()=>initialFileLists);};
            InitializeRuntime();InitializeStatusColors();InitializeDatabaseSavedTime();InitializeRepositoryPresentation();InitializeOpenListFallback();InitializeServerTrustPrompt();operations.Changed+=OperationAdmissionChanged;UpdateRemoteTaskActions();var exitMenu=new ContextMenu();var exitItem=new MenuItem{Header="退出软件"};exitItem.Click+=delegate{RequestApplicationExit();};exitMenu.Items.Add(exitItem);Button("Settings").ContextMenu=exitMenu;
            InitializeApplicationUpdates();Log("就绪");
        }
        private void Guard(Action action){try{action();}catch(Exception error){Error(error);}}
        private async Task GuardAsync(Func<Task> action){try{await action();}catch(OperationCanceledException){if(!disposed){SetOperationText("操作已取消。",OperationState.Cancelled);OperationProgress(0,false);}}catch(Exception error){if(!disposed)Error(error);}}
        private void Error(Exception error){LastError=error.Message;SetOperationText("操作未完成："+error.Message,OperationState.Failed);OperationProgress(0,false);Log(error.ToString());
#if UI_TEST
throw new InvalidOperationException("UI action failed: "+error.Message,error);
#else
MessageBox.Show(Window,error.Message,"Backup",MessageBoxButton.OK,MessageBoxImage.Information);
#endif
}
        public void SaveWorkspace(){RunConfiguration(()=>Backend.SaveWorkspaceDefinition(Workspace));}
        private void SaveTaskControls()
        {if(CurrentTask==null||CurrentTask.UseCustomPassword)return;string password=Backend.Catalog.GetSetting("backup_password_v1");if(password!=null)CurrentTask.Password=password;}
        private void LoadTaskControls()
        {
            RememberSelectedTask();
            loading=true;try{Control<ComboBox>("TaskSelector").ItemsSource=Workspace.Tasks.Where(t=>!t.Deleted).ToList();Control<ComboBox>("TaskSelector").SelectedItem=CurrentTask;
                var topology=Backend.Catalog.LoadTopology();Text("TaskSummary").Text=CurrentTask==null?"先添加文件夹和仓库，再新建任务。":"待备份："+string.Join("；",Workspace.Sources.Where(s=>CurrentTask.SourceIds.Contains(s.Id)).Select(s=>s.Name))+"   →   仓库："+string.Join("；",topology.Groups.Where(g=>CurrentTask.TargetIds.Contains(g.Id)).Select(Display.TargetName));
            }finally{loading=false;}
        }
        public async Task SwitchTaskAsync(string id)
        {
            if(MutationBlocked&&!backupBusy)throw new InvalidOperationException("请等待当前操作结束。");CurrentTask=Workspace.Tasks.Single(t=>t.Id==id&&!t.Deleted);InvalidateFileTree();LoadTaskControls();RefreshCatalog();
            if(CurrentTask.SourceIds.Any(s=>!loaded.ContainsKey(s))&&!busy)await RefreshTaskAsync();else await PrepareCurrentTreeAsync();
        }
        private void EditTask(BackupTaskDefinition original)
        {
            SaveTaskControls();var result=EditorDialogs.Task(Window,Workspace,Backend.Catalog.LoadTopology(),original,delegate(BackupTaskDefinition value){var draft=JsonSettings.Decode(JsonSettings.Encode(Workspace),new WorkspaceDefinition());draft.Tasks.RemoveAll(t=>t.Id==value.Id);draft.Tasks.Add(value);var topology=Backend.Catalog.LoadTopology();WorkspaceMigration.Validate(draft,topology);WorkspaceMigration.ValidateTargetPolicy(value,topology);});
            if(result==null)return;if(original!=null&&JsonSettings.Encode(original)==JsonSettings.Encode(result)){Log("已保存任务："+result.Name+"。");return;}
            RunConfiguration(delegate{
            FileSelection pending=null,existing;if(original!=null&&selectionDrafts.TryGetValue(original.Id,out existing)){pending=CopySelection(existing);foreach(var source in Workspace.Sources.Where(x=>result.SourceIds.Contains(x.Id)&&!original.SourceIds.Contains(x.Id)))pending.Set(source.Location,true,true);}
            var nextWorkspace=JsonSettings.Decode(JsonSettings.Encode(Workspace),new WorkspaceDefinition());nextWorkspace.Tasks.RemoveAll(t=>t.Id==result.Id);nextWorkspace.Tasks.Add(result);Backend.SaveWorkspaceDefinition(nextWorkspace);AdoptWorkspace(nextWorkspace,result.Id);if(pending!=null)selectionDrafts[result.Id]=pending;ApplyCommittedChanges();
            var added=original==null?result.SourceIds:result.SourceIds.Except(original.SourceIds).ToList();Log("已保存任务："+result.Name+"。"+(added.Count>0?"正在后台刷新文件列表。":""));if(added.Count>0)StartAutomaticFileRefresh(added);});
        }
        private void EditSource(SourceDefinition original)
        {
            var result=EditorDialogs.Source(Window,original,delegate(SourceDefinition source){if(Workspace.Sources.Any(s=>!s.Deleted&&s.Id!=source.Id&&StoragePath.NormalizeRoot(s.Location).Equals(source.Location,StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("该目录已存在。");});
            if(result==null)return;
            if(original!=null&&!original.Location.Equals(result.Location,StringComparison.OrdinalIgnoreCase)&&Workspace.Tasks.Any(t=>t.SourceIds.Contains(original.Id)))throw new ArgumentException("已有任务引用此文件夹；请先添加其他文件夹再修改任务。");
            bool changed=original==null||!original.Location.Equals(result.Location,StringComparison.OrdinalIgnoreCase);if(!changed&&original.Name==result.Name)return;RunConfiguration(delegate{var draft=JsonSettings.Decode(JsonSettings.Encode(Workspace),new WorkspaceDefinition());draft.Sources.RemoveAll(s=>s.Id==result.Id);draft.Sources.Add(result);Backend.SaveWorkspaceDefinition(draft);AdoptWorkspace(draft);if(changed)loaded.Remove(result.Id);ApplyCommittedChanges();if(changed)StartAutomaticFileRefresh(new[]{result.Id});});
        }
        public void SaveDestination(DestinationDefinition destination,string name)
        {
            RunConfiguration(delegate{AdoptWorkspace(Backend.SaveDestinationDefinition(Workspace,destination,name));ApplyCommittedChanges();});
        }
        public void SaveWarehouse(BackupGroupDefinition warehouse)
        {
            RunConfiguration(delegate{AdoptWorkspace(Backend.SaveWarehouseDefinition(Workspace,warehouse));ApplyCommittedChanges();});
        }
        private void EditWarehouse()
        {
            Backend.ServerTrust.BeginAttempt();
            var row=Grid("WarehouseGrid").SelectedItem as WarehouseView;if(row==null)return;var topology=Backend.Catalog.LoadTopology();var group=topology.Groups.Single(g=>g.Id==row.Id);
            if(group.Id.StartsWith("single:")){var destination=topology.Destinations.Single(d=>d.Id==group.DestinationIds.Single());EditorDialogs.Destination(Window,SaveDestination,destination,group.Name,Backend.LoadConnection,Backend.SaveConnection,SaveDestinationFromEditorAsync,scheduleJournal,ConnectionBrowserServices,OpenListServices);}
            else EditorDialogs.Warehouse(Window,topology,group,SaveWarehouse);
        }
        public void UpdateManagers()
        {
            managersDirty=false;
            var topology=Backend.Catalog.LoadTopology();var stats=Backend.Catalog.GetUsage().ToDictionary(u=>u.DestinationId);var definitions=topology.Destinations.ToDictionary(d=>d.Id);var rows=new List<WarehouseView>();
            foreach(var group in topology.Groups.Where(g=>!g.Retired)){var ids=topology.ResolveDestinations(group.Id,false);long used=0,reserved=0;decimal capacity=0;foreach(string id in ids){DestinationUsage u;if(stats.TryGetValue(id,out u)){used+=u.UsedBytes;reserved+=u.ReservedBytes;}capacity+=definitions[id].MaxBytes;}
                bool leaf=group.Id.StartsWith("single:");rows.Add(new WarehouseView{Id=group.Id,Name=group.Name,Kind=leaf?Display.DestinationType(definitions[ids.Single()]):"["+(group.Mode??"raid0")+"组]",HoverText=Display.TargetHint(topology,group),Members=leaf?definitions[ids.Single()].Location:string.Join("；",group.DestinationIds.Select(id=>topology.Groups.FirstOrDefault(g=>g.Id=="single:"+id)).Where(g=>g!=null).Select(g=>g.Name).Concat(group.ChildGroupIds.Select(id=>Display.TargetName(topology.Groups.Single(g=>g.Id==id))))),Usage=Display.Bytes(used)+" / "+Display.Bytes((long)Math.Min(long.MaxValue,capacity))});}
            ReplaceGridRows(Grid("SourceGrid"),Workspace.Sources.Where(s=>!s.Deleted).ToList(),source=>source.Id);ReplaceGridRows(Grid("WarehouseGrid"),rows,row=>row.Id);
            loading=true;try{string groupId=(Control<ComboBox>("StatusWarehouseFilter").SelectedItem as ChoiceRow??new ChoiceRow()).Id;
                
                var groups=new[]{new ChoiceRow{Id="",Name="全部"}}.Concat(rows.Select(r=>new ChoiceRow{Id=r.Id,Name=Display.TargetLabel(topology,topology.Groups.Single(g=>g.Id==r.Id)),ToolTip=Display.TargetHint(topology,topology.Groups.Single(g=>g.Id==r.Id))})).ToList();Control<ComboBox>("StatusWarehouseFilter").ItemsSource=groups;Control<ComboBox>("StatusWarehouseFilter").SelectedItem=groups.FirstOrDefault(g=>g.Id==groupId)??groups[0];
            }finally{loading=false;}
            var pending=Backend.Catalog.GetRetirements().Where(r=>r.Kind=="group"&&r.State!="complete").ToList();Text("WarehousePendingNotice").Text=pending.Count==0?"":pending.Count+" 个项目等待迁移 / 清理。点击处理待迁移可重试。"+string.Join("；",pending.Where(r=>!string.IsNullOrEmpty(r.Error)).Select(r=>r.Error));int pendingDelete=Backend.Catalog.GetDetachedObjects().Count;if(pendingDelete>0)Text("WarehousePendingNotice").Text+=" "+pendingDelete+" 个备份文件待删除，可点击处理待迁移重试。";int moving=Backend.Catalog.GetWarehouseMoves().Count(m=>m.State!="complete");if(moving>0)Text("WarehousePendingNotice").Text+=" "+moving+" 个仓库路径迁移未完成，请编辑原仓库重试。";string uploadWarning=Backend.Catalog.GetSetting("last_upload_warnings_v1");if(!string.IsNullOrEmpty(uploadWarning))Text("WarehousePendingNotice").Text+="\n上次备份有成员上传失败，可能暂时分摊不均；下次备份会重新尝试：\n"+uploadWarning;UpdateRepositoryConnectionViews();RestoreUploadAlert();Text("StorageHint").Text=Workspace.Tasks.Count(t=>!t.Deleted)+" 个任务 · "+topology.Destinations.Count(d=>!d.Retired)+" 个仓库";
        }
        public async Task RefreshTaskAsync()
        {
            if(CurrentTask!=null)await RequestFileRefreshAsync(CurrentTask.SourceIds);
        }
        private void RebuildTree()
        {
            InvalidateFileTree();treePreparation=PrepareCurrentTreeAsync();ObserveTreePreparation(treePreparation);
        }
        private async void ObserveTreePreparation(Task work){try{await work;}catch(Exception error){if(!disposed&&ReferenceEquals(work,treePreparation)){Log("文件列表更新未完成，已保留现有内容："+error.Message);if(!MutationBlocked)SetOperationText("文件列表更新未完成："+error.Message,OperationState.Failed);}}}
        public void ApplySelection()
        {Tree.Apply(Selection,views);ShowSelectionSummary(Tree.SelectedFileCount,Tree.SelectedBytes);UpdateSelectionHeaders();}
        private void ChangeSelection(FileTreeNode node,bool include)
        {if(MutationBlocked)return;Selection.Set(node.FullPath,node.Directory,include);Tree.ChangeSelection(node,include);ShowSelectionSummary(Tree.SelectedFileCount,Tree.SelectedBytes);UpdateSelectionHeaders();}
        private void ShowNode(FileTreeNode node)
        {if(node==null)return;previewParent=node.Directory?node:node.Parent;Grid("FileGrid").ItemsSource=node.Directory?node.Children:node.Parent==null?new ObservableCollection<FileTreeNode>{node}:node.Parent.Children;UpdateSelectionHeaders();if(!node.Directory)Grid("FileGrid").SelectedItem=node;ShowFileDetails(node);}
        private void ShowFileDetails(FileTreeNode node)
        {
            if(node==null)return;string detail=node.Directory?node.FullPath+" · 共"+node.SelectedCount+"/"+node.FileCount+"个文件":node.FullPath+"\n"+node.BackupStatus;if(!node.Directory&&node.ArchiveId>0){detail+=" · "+node.ArchiveName+" · "+node.CopiesText+" 份";var definitions=Backend.Catalog.LoadTopology().Destinations.ToDictionary(d=>d.Id);foreach(var copy in Backend.Catalog.GetCopies(node.ArchiveId).Take(3))detail+="\n"+Backend.ObjectLocation(definitions[copy.DestinationId],copy.Object.Name);}Text("FileDetails").Text=detail;Text("FileDetails").ToolTip=detail;Text("FileDetails").TextWrapping=node.Directory?TextWrapping.NoWrap:TextWrapping.Wrap;
        }
        private void ToggleCheckbox(object sender,MouseButtonEventArgs e)
        {if(MutationBlocked||!SourceViewCurrent||e.ChangedButton!=MouseButton.Left)return;DependencyObject item=e.OriginalSource as DependencyObject;while(item!=null&&!(item is DataGridCell))item=VisualTreeHelper.GetParent(item);var cell=item as DataGridCell;if(cell==null||!(cell.Column is DataGridCheckBoxColumn))return;var node=cell.DataContext as FileTreeNode;if(node==null)return;Grid("FileGrid").SelectedItem=node;node.IsChecked=node.IsChecked!=true;e.Handled=true;}
        public void RefreshCatalog(bool updateManagers=true,bool updateHistory=true)
        {var next=ReadFileViews(CurrentTask==null?null:CurrentTask.Id);if(!ReferenceEquals(next,views))historyDirty=true;views=next;presentedCatalogStamp=fileViewsStamp;if(updateManagers)UpdateManagers();RefreshHistoryCheckSummary();Tree.RefreshBackups(views);ShowSelectionSummary(Tree.SelectedFileCount,Tree.SelectedBytes);UpdateSelectionHeaders();if(updateHistory&&Control<TabControl>("Tabs").SelectedIndex==3&&historyDirty)RebuildHistory();}
        private void ShowHistoryDetails(BackupHistoryNode node)
        {
            ShowWarehouseEntry(node);
        }
        private bool IsFolderHistoryView {get{return Control<ComboBox>("HistoryView").SelectedIndex==0;}}
        private int historyBuildGeneration;
        private sealed class TaskHistorySnapshot {internal string Identity;internal IList<FileHistoryRow> Rows;internal long Used;}
        private readonly object taskHistoryGate=new object();
        private readonly Dictionary<string,TaskHistorySnapshot> taskHistorySnapshots=new Dictionary<string,TaskHistorySnapshot>();
        private long taskHistoryUse;private int taskHistoryGeneration;
        private void InvalidateTaskHistory(ISet<string> tasks=null)
        {lock(taskHistoryGate){taskHistoryGeneration++;if(tasks==null)taskHistorySnapshots.Clear();else foreach(string task in tasks)taskHistorySnapshots.Remove(task);}}
        private IList<FileHistoryRow> ReadTaskHistory(BackupNode node,string warehouseId,out bool missing)
        {
            string identity=node.Id+":"+node.ArchiveId+":"+node.Revision+":"+node.Digest+":"+node.LocalOnly;int generation;
            lock(taskHistoryGate){generation=taskHistoryGeneration;TaskHistorySnapshot cached;if(taskHistorySnapshots.TryGetValue(node.TaskId,out cached)&&cached.Identity==identity){cached.Used=++taskHistoryUse;missing=false;return cached.Rows;}}
            var rows=Backend.ReadNodeFilesForBrowse(node.Id,warehouseId,out missing);if(!missing){long limit=Math.Max(1024*1024,Math.Min(32L*1024*1024,Backend.MemoryLimitBytes/16));
                lock(taskHistoryGate)if(generation==taskHistoryGeneration){taskHistorySnapshots.Remove(node.TaskId);if(rows.Count*256L<=limit){while(taskHistorySnapshots.Count>0&&taskHistorySnapshots.Values.Sum(value=>value.Rows.Count*256L)>limit-rows.Count*256L)taskHistorySnapshots.Remove(taskHistorySnapshots.OrderBy(pair=>pair.Value.Used).First().Key);taskHistorySnapshots[node.TaskId]=new TaskHistorySnapshot{Identity=identity,Rows=rows,Used=++taskHistoryUse};}}
            }return rows;
        }
        private Task historyLoading=Task.FromResult(true);public Task HistoryLoading{get{return historyLoading;}private set{historyLoading=value;}}
        public void RebuildHistory(bool readSelectedWarehouse=false){HistoryLoading=RebuildHistoryAsync(readSelectedWarehouse);ObserveHistoryLoading(HistoryLoading);}
        private async void ObserveHistoryLoading(Task work)
        {try{await work;}catch(OperationCanceledException){}catch(Exception error){if(!disposed&&(ReferenceEquals(work,HistoryLoading)||ReferenceEquals(work,HistoryFolderLoading)))Text("HistoryDetails").Text="本地清单读取未完成："+error.Message;}}
        private async Task RebuildHistoryAsync(bool readSelectedWarehouse)
        {
            int generation=++historyBuildGeneration;UpdateRemoteTaskActions();
            if(!IsFolderHistoryView){await RebuildWarehouseBrowserAsync(readSelectedWarehouse);return;}
            warehouseDetailsGeneration++;
            var historyTasks=Workspace.Tasks.Where(value=>!value.Deleted||Backend.Catalog.TaskHasNodes(value.Id)).ToList();
            var warehouse=Control<ComboBox>("StatusWarehouseFilter").SelectedItem as ChoiceRow;
            if(historyActiveTaskId==null||!historyTasks.Any(value=>value.Id==historyActiveTaskId))historyActiveTaskId=CurrentTask!=null&&historyTasks.Any(value=>value.Id==CurrentTask.Id)?CurrentTask.Id:historyTasks.Select(value=>value.Id).FirstOrDefault();RefreshNodeSelector(historyActiveTaskId);var selectedNode=SelectedBackupNode;string selectedNodeId=selectedNode==null?null:selectedNode.Id;UpdateRemoteTaskActions();
            var topology=Backend.Catalog.LoadTopology();
            var workspace=JsonSettings.Decode(JsonSettings.Encode(Workspace),new WorkspaceDefinition());
            HashSet<string> members=warehouse==null||string.IsNullOrEmpty(warehouse.Id)?null:new HashSet<string>(topology.ResolveDestinations(warehouse.Id));
            bool reload=historyDirty||historyTaskId!=historyActiveTaskId||historyNodeId!=selectedNodeId;
            var previous=history;
            if(Grid("HistoryPreview").Items.Count==0)SetHistoryItems(new List<BackupHistoryNode>(),"正在读取本地清单");
            IList<FileHistoryRow> rows=null;bool needsWarehouse=false;string warehouseId=warehouse==null?null:warehouse.Id;var cancellation=browsingCancellation.Token;
            var roots=await Task.Run(()=>
            {
                cancellation.ThrowIfCancellationRequested();
                if(reload){var collected=new List<FileHistoryRow>();foreach(var historyTask in historyTasks){var taskNodes=Backend.BackupNodesForBrowse(historyTask.Id);var taskNode=historyTask.Id==historyActiveTaskId?selectedNode:taskNodes.FirstOrDefault();if(taskNode==null)continue;bool missing;var part=ReadTaskHistory(taskNode,warehouseId,out missing);if(missing){needsWarehouse=true;continue;}collected.AddRange(part);}rows=collected;}
                else rows=previous;
                var visible=rows.Where(h=>members==null||members.Contains(h.DestinationId));
                return BackupHistoryTree.BuildFolders(visible,workspace,topology,rows,null,Backend.HistoryEntryPaths(workspace));
            });
            if(disposed||generation!=historyBuildGeneration||!IsFolderHistoryView)return;
            history=rows;historyTaskId=historyActiveTaskId;historyNodeId=selectedNodeId;historyDirty=needsWarehouse;
            var state=CaptureHistoryView();var folder=PublishHistoryRoots(roots,state);
            if(folder!=null){historyFolderLoading=ShowTaskFolderAsync(folder,state);await historyFolderLoading;}else{historyFolder=null;Text("HistoryPath").Text=needsWarehouse?"请选择仓库读取版本清单":"暂无已备份文件";Text("HistoryDetails").Text=needsWarehouse?"当前版本没有可用的本地清单；请选择仓库后读取，只访问所选目标。":"当前筛选下没有备份记录。";BindHistoryRows(null,new List<BackupHistoryNode>(),state,needsWarehouse?"请选择仓库读取版本清单":"文件夹是空的");}
        }
        public Task StartBackupAsync()
        {return RunOperationAsync(OperationKind.Backup,()=>StartBackupCoreAsync());}
        private async Task StartBackupCoreAsync()
        {
            long acceptedGeneration=sourceReadGeneration;await WaitForFileListsAsync();if(SelectionDirty)throw new InvalidOperationException("勾选有未保存的更改，请先点击保存或重置。");
            if(busy)throw new InvalidOperationException("当前任务尚未结束。");if(CurrentTask==null)throw new ArgumentException("请先新建任务。");SaveTaskControls();SaveWorkspace();
            await EnsureTaskFileListsAsync(new[]{CurrentTask.Id},acceptedGeneration);
            var files=Tree.SelectedFiles().Select(n=>n.Source).ToList();if(!ConfirmImportedTasks(new[]{CurrentTask.Id},"备份"))return;
            var task=JsonSettings.Decode(JsonSettings.Encode(CurrentTask),new BackupTaskDefinition());
            ValidateSourceReadStates(task.SourceIds);sourceReads.MarkUsed();BeginUploadNotifications();Backend.BeginBackup();SetBusy(true,false,"准备备份："+task.Name);Control<ProgressBar>("JobProgress").Value=0;Log("开始任务："+task.Name);
            try{
                var cleanupProgress=CleanupReporter();var maintenanceProgress=OperationReporter("");var sources=Workspace.Sources.Where(s=>!s.Deleted&&task.SourceIds.Contains(s.Id)&&loaded.ContainsKey(s.Id)).ToList();var indexed=sources.SelectMany(s=>loaded[s.Id].Files).ToList();var pending=await Task.Run(()=>Backend.PrepareTaskBackup(Workspace,task,sources,indexed,maintenanceProgress,cleanupProgress));await Task.Run(()=>Backend.ActiveBackup.Checkpoint());
                SetBusy(true,true,"正在备份："+task.Name);cleanupProgress=CleanupReporter();var backupProgress=BackupReporter();LastBackup=await Task.Run(()=>Backend.Backup(task,files,backupProgress,cleanupProgress));if(closeAfterWork)return;
                if(LastBackup.NoChanges){string completed="任务完成：本地文件和备份清单均无变化。";SetOperationText(completed,OperationState.Succeeded);OperationProgress(100,false);Log(task.Name+"："+completed);return;}
                PublishBackupResult(LastBackup);
                if(LastBackup.ReadyForPublication){SetBusy(true,true,"正在同步加密数据库备份…");var catalogReport=OperationReporter("");try{await Task.Run(()=>Backend.PublishTaskBackup(task,LastBackup,catalogReport,cleanupProgress));}catch(IOException e){LastBackup.CatalogBackupError=e.Message;Log("文件备份已处理；数据库快照待重试："+e.Message);}}
                bool shortfall=LastBackup.CatalogBackupError!=null||pending.Count>0||Backend.LastCleanup!=null&&Backend.LastCleanup.PendingArchives>0||LastBackup.AttentionUnchanged>0||LastBackup.PendingConfirmations>0||LastBackup.Archives.Any(a=>a.Archive.Status!="complete");
                string message=LastBackup.Stopped?StoppedBackupNotice():LastBackup.Archives.Any(item=>item.Archive.Status=="failed")?"备份结束，存在上传失败；未形成完整可恢复备份的归档已移除本次引用。":LastBackup.Archives.Count==0?(LastBackup.RemovedFromScope>0||LastBackup.ConfirmedDeleted>0?"备份清单已更新，未读取源文件内容。":"没有新增或变动文件，未读取源文件内容。")+(shortfall?" 副本状态需处理。":""):shortfall?"备份结束，部分副本状态需处理。":"当前任务备份完成。";
                message+=(LastBackup.Node==null?"":" 备份版本："+LastBackup.Node+"。");message+=" "+CleanupSummary(Backend.LastCleanup)+(LastBackup.CatalogArchive!=null?" 数据库："+LastBackup.CatalogArchive.Archive.ArchiveName:LastBackup.Stopped?"":!LastBackup.ReadyForPublication?" 本次未生成新的仓库版本数据库。":LastBackup.CatalogBackupError!=null?" 数据库快照未完成。":LastBackup.CatalogBackupDisabled?" 共享数据库备份已关闭，备份版本数据库已保存到任务仓库。":LastBackup.CatalogSkippedUnchanged?" 文件未变化，未上传数据库快照。":" 数据库快照沿用已有完整版本。");
                if(LastBackup.ConfirmedDeleted>0)message+=" 已确认 "+LastBackup.ConfirmedDeleted+" 个源文件已删除。";
                if(LastBackup.RemovedFromScope>0)message+=" 已将 "+LastBackup.RemovedFromScope+" 个不再属于当前备份范围的文件移出备份清单。";
                if(LastBackup.PendingConfirmations>0)message+=" "+LastBackup.PendingConfirmations+" 个分卷上传结果待确认，可通过检查完整性继续核对。";
                if(LastBackup.CatalogBackupError!=null)message+="\n数据库同步未完成："+LastBackup.CatalogBackupError;
                if(pending.Count>0)message+="\n操作未完成："+string.Join("；",pending);
                if(LastBackup.UploadWarnings.Count>0)message+="\n部分仓库上传失败，本轮改用其他成员，容量可能暂时不均；下次备份重试：\n"+string.Join("\n",LastBackup.UploadWarnings);SetOperationText(message,LastBackup.Stopped?OperationState.Cancelled:LastBackup.CatalogBackupError!=null||pending.Count>0?OperationState.Failed:shortfall||LastBackup.UploadWarnings.Count>0?OperationState.Attention:OperationState.Succeeded);Log(task.Name+"："+message+" 跳过未变化 "+LastBackup.SkippedUnchanged+" 个。");OperationProgress(100,false);
            }catch(OperationCanceledException){SetOperationText(StoppedBackupNotice(),OperationState.Cancelled);OperationProgress(0,false);Log(Control<TextBox>("StatusText").Text);}
            finally{Backend.EndBackup();SetBusy(false,false,null);}
        }
        private Action<DesktopProgress> BackupReporter(){int generation=operationGeneration;return progress=>PostBackupProgress(progress,generation);}
        private static string FinishingBackupText(long? buffered){return "已停止读取，继续上传已打包完整的压缩包；可点击“立即停止”取消上传并清理未完成归档。";}
        private string StoppedBackupNotice(){if(Backend.ActiveBackup!=null&&Backend.ActiveBackup.DeleteIncompleteOnStop)return (Backend.Catalog.GetSetting("last_upload_alert_v1")??"").Contains("清理待重试")?"备份已立即停止。已完成归档保留；部分未完成归档清理待重试。":"备份已立即停止。未完成归档已清理，已完成归档保留。";return "备份已停止。版本只记录已完整备份的数据；未完成包不计入版本，上传结果待确认的保留原目标继续核对，无引用残留在修复完整性时清理。";}
        private sealed class BackupProgressSnapshot {internal DesktopProgress Progress;internal int Generation;}
        private BackupProgressSnapshot pendingBackupProgress;
        private void PostBackupProgress(DesktopProgress progress,int generation)
        {if(Window.Dispatcher.HasShutdownStarted)return;System.Threading.Interlocked.Exchange(ref pendingBackupProgress,new BackupProgressSnapshot{Progress=progress,Generation=generation});}
        private void RefreshBackupProgress()
        {var snapshot=System.Threading.Interlocked.Exchange(ref pendingBackupProgress,null);if(snapshot==null)return;var progress=snapshot.Progress;int generation=snapshot.Generation;if(disposed||closeAfterWork||!backupBusy||generation!=operationGeneration)return;var control=Backend.ActiveBackup;if(repairingBackups&&control!=null&&control.IsStopping)return;Control<ProgressBar>("JobProgress").Value=progress.TotalBytes==0?0:Math.Min(99,100.0*progress.ReadBytes/progress.TotalBytes);if(!string.IsNullOrEmpty(progress.UploadAlert)&&progress.UploadAlert.StartsWith("上传结果待确认",StringComparison.Ordinal))ShowUploadAlert(progress.UploadAlert);if(control!=null&&!control.IsStopping&&(Backend.StopRequested||control.IsFinishing))SetOperationText("已停止读取 · "+progress.Message);else if(control==null||!control.IsPaused&&!control.IsStopping)SetOperationText(progress.Message);Text("BackupMetrics").Text="已上传压缩包 "+progress.UploadedVolumes+" 个 · 缓冲 "+StatusBytes(progress.BufferedBytes)+" / "+StatusBytes(progress.MemoryLimit)+" · 已读 "+StatusBytes(progress.ReadBytes)+" / "+StatusBytes(progress.TotalBytes);Text("BackupMetrics").ToolTip="按成功上传的压缩文件计数，同名文件的多个仓库副本不重复计数。";Text("BackupMetrics").Visibility=Visibility.Visible;}
        private void UpdateBackupButtons(){bool visible=backupBusy&&Backend.ActiveBackup!=null;bool stopping=visible&&Backend.ActiveBackup.IsStopping;bool finishing=visible&&(Backend.StopRequested||Backend.ActiveBackup.IsFinishing);Text("BackupMetrics").Visibility=visible&&!string.IsNullOrEmpty(Text("BackupMetrics").Text)?Visibility.Visible:Visibility.Collapsed;foreach(string name in new[]{"StopAfterArchive","PauseBackup","StopNow"}){Button(name).Visibility=visible?Visibility.Visible:Visibility.Collapsed;Button(name).IsEnabled=visible&&!stopping&&(name=="StopNow"||!finishing);}Button("PauseBackup").Content=visible&&Backend.ActiveBackup.IsManuallyPaused?"继续":"暂停";}
        public Task<IList<IntegrityFinding>> CheckIntegrityAsync(){return CheckAllBackupsAsync();}
        private void SetBusy(bool value,bool backingUp,string message)
        {
            DrainTransferNotifications();bool wasBusy=busy;busy=value;backupBusy=value&&backingUp;
            if(value!=wasBusy)ResetUploadStatus();if(value&&!wasBusy){Text("BackupMetrics").Text="";Text("BackupMetrics").ToolTip=null;uploadAlertDismissed=false;transferIssues.Clear();resolvedUploadSubjects.Clear();loggedTransferNotices.Clear();loggedAlertMessages.Clear();operationCancellation=operations.Current==null?new System.Threading.CancellationTokenSource():System.Threading.CancellationTokenSource.CreateLinkedTokenSource(operations.Current.Cancellation.Token);Backend.OperationCancellation=operationCancellation.Token;}
            if(!value&&operationCancellation!=null){Backend.OperationCancellation=System.Threading.CancellationToken.None;operationCancellation.Dispose();operationCancellation=null;}
            if(value){operationGeneration++;operationTab=backingUp?0:Control<TabControl>("Tabs").SelectedIndex;if(!wasBusy){Text("SourceNotice").Text="";Text("WarehouseNotice").Text="";}OperationProgress(0,!backingUp);SetOperationText(message??"正在处理…");}
            foreach(string name in new[]{"SourceActions","WarehouseActions","BackupOptions","SelectAllTree","EditExclusions","ScheduleActions"})((UIElement)Window.FindName(name)).IsEnabled=!value;
            // Browsing is independent of the running task; only mutations and competing jobs are locked.
            foreach(string name in new[]{"TaskToolbar","StatusToolbar","SourceTree","FileGrid","HistoryTree","HistoryPreview","ScheduleGrid"})((UIElement)Window.FindName(name)).IsEnabled=true;
            foreach(string name in new[]{"AddTask","EditTask","RemoveTask","RefreshSources","ImportCatalog","CheckIntegrity","RepairBackups"})Button(name).IsEnabled=!value;
            Control<ComboBox>("TaskSelector").IsEnabled=!value||backupBusy;Control<TreeView>("SourceTree").Tag=!value;Grid("FileGrid").IsReadOnly=value;Control<CheckBox>("SelectAllFiles").IsEnabled=!value&&previewParent!=null;
            UpdateBackupButtons();
            Control<ProgressBar>("SourceProgress").Visibility=value&&operationTab==1?Visibility.Visible:Visibility.Collapsed;Control<ProgressBar>("WarehouseProgress").Visibility=value&&operationTab==2?Visibility.Visible:Visibility.Collapsed;
            if(!value){if(!string.IsNullOrEmpty(currentUploadAlert))Backend.Catalog.SetSetting("last_upload_alert_v1",currentUploadAlert);operationGeneration++;foreach(string name in new[]{"JobProgress","SourceProgress","WarehouseProgress"})Control<ProgressBar>(name).IsIndeterminate=false;}
            UpdateSelectionControls();UpdateScheduleRunButton();RefreshStatusColors();UpdateRemoteTaskActions();
            RefreshOperationAvailability();
            if(!value&&fileRefreshIdle!=null){var idle=fileRefreshIdle;fileRefreshIdle=null;idle.TrySetResult(true);}
            if(!value&&closeAfterWork)Window.Dispatcher.BeginInvoke(new Action(()=>Window.Close()));else if(!value)DispatchScheduledRequests();
        }
        private void Log(string message,OperationState state=OperationState.None){AppendTaskLog(message,state);}
    }
}
