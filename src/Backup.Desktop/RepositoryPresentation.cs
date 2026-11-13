using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Backup.Core;

namespace Backup.Desktop
{
    internal static class RepositoryConnectionColors
    {
        internal static Brush Color(string state)
        {
            return state == "已连接" 
                ? Brushes.ForestGreen 
                : state == "正在连接" ? OperationTextColors.Working : state == "连接失败" || state == "无openlist优化" || DesktopBackend.IsServerTrustState(state) ? Brushes.Firebrick : Brushes.Gray;
        }
    }

    public sealed partial class WarehouseView
    {
        private string connectionState;
        public string ConnectionState
        {
            get
            {
                return connectionState;
            }

            set
            {
                if (connectionState == value)
                    return;
                connectionState = value;
                Changed("ConnectionState");
                Changed("ConnectionForeground");
            }
        }

        public Brush ConnectionForeground
        {
            get
            {
                return RepositoryConnectionColors.Color(ConnectionState);
            }
        }

        private string connectionDetails;
        public string ConnectionDetails
        {
            get
            {
                return connectionDetails;
            }

            set
            {
                if (connectionDetails == value)
                    return;
                connectionDetails = value;
                Changed("ConnectionDetails");
            }
        }
    }

    public sealed partial class BackupHistoryNode
    {
        private string connectionState;
        public string ConnectionState
        {
            get
            {
                return IsWarehouseRoot ? connectionState : "";
            }

            set
            {
                if (connectionState == value)
                    return;
                connectionState = value;
                Changed("ConnectionState");
                Changed("ConnectionForeground");
            }
        }

        public Brush ConnectionForeground
        {
            get
            {
                return RepositoryConnectionColors.Color(ConnectionState);
            }
        }

        private string connectionDetails;
        public string ConnectionDetails
        {
            get
            {
                return connectionDetails;
            }

            set
            {
                if (connectionDetails == value)
                    return;
                connectionDetails = value;
                Changed("ConnectionDetails");
            }
        }
    }

    public sealed partial class MainWindowController
    {
        private readonly object presentationGate = new object();
        private readonly HashSet<string> repositoryUpdates = new HashSet<string>(), sourceUpdates = new HashSet<string>(), taskUpdates = new HashSet<string>();
        private CatalogChange presentationChanges;
        private bool allTaskUpdates;
        private int repositoryUpdatePosted;
        private readonly Dictionary<string, RepositoryFolderChange> folderUpdates = new Dictionary<string, RepositoryFolderChange>();
        private long presentedCatalogStamp = long.MinValue;
        private bool managersDirty;
        private void InitializeRepositoryPresentation()
        {
            Backend.ApplicationChanged += QueueRepositoryPresentation;
            UpdateRepositoryConnectionViews();
        }

        private void QueueRepositoryPresentation(ApplicationChange change)
        {
            if (disposed)
                return;
            lock (presentationGate)
            {
                repositoryUpdates.UnionWith(change.Repositories);
                sourceUpdates.UnionWith(change.Sources);
                presentationChanges |= change.Changes;
                foreach (var folder in change.Folders)
                    MergeFolderChange(folderUpdates, folder);
                if ((change.Changes & CatalogChange.Archives) != 0)
                {
                    if (change.Tasks == null)
                        allTaskUpdates = true;
                    else
                        taskUpdates.UnionWith(change.Tasks);
                }
            }

            if (!closeAfterWork && Interlocked.Exchange(ref repositoryUpdatePosted, 1) == 0)
                try
                {
                    Window.Dispatcher.BeginInvoke(
                        new Action(delegate
                    {
                        try
                        {
                            ApplyRepositoryPresentation();
                        }
                        catch (Exception error)
                        {
                            global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/RepositoryPresentation.cs:QueueRepositoryPresentation", error);
                            if (!disposed)
                                Log("本地仓库视图更新未完成：" + error.Message);
                        }
                    }),
                        DispatcherPriority.Background
                    );
                }
                catch (InvalidOperationException diagnosticFailure)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/RepositoryPresentation.cs:QueueRepositoryPresentation", diagnosticFailure);
                }
        }

        private void ApplyRepositoryPresentation()
        {
            Interlocked.Exchange(ref repositoryUpdatePosted, 0);
            if (disposed || closeAfterWork || operations.ActiveKind == OperationKind.Import)
                return;
            HashSet<string> ids, sources, tasks;
            List<RepositoryFolderChange> folders;
            CatalogChange changes;
            lock (presentationGate)
            {
                ids = new HashSet<string>(repositoryUpdates);
                sources = new HashSet<string>(sourceUpdates);
                tasks = allTaskUpdates ? null : new HashSet<string>(taskUpdates);
                folders = folderUpdates.Values.ToList();
                changes = presentationChanges;
                repositoryUpdates.Clear();
                sourceUpdates.Clear();
                taskUpdates.Clear();
                folderUpdates.Clear();
                allTaskUpdates = false;
                presentationChanges = CatalogChange.None;
            }

            if (changes == CatalogChange.None)
                return;
            bool workspace = (changes & CatalogChange.Workspace) != 0, topology = (changes & CatalogChange.Topology) != 0, archives = (changes & CatalogChange.Archives) != 0;
            bool sourceFiles = (changes & CatalogChange.Sources) != 0, rules = (changes & CatalogChange.Rules) != 0;
            if (workspace || topology)
                InvalidateTaskHistory();
            else if (archives)
                InvalidateTaskHistory(tasks);
            if ((changes & CatalogChange.Settings) != 0 && !backupBusy)
                RecordSavedUploadNoticeHistory();
            if (workspace)
            {
                string json = Backend.Catalog.GetSetting("workspace_v3");
                AdoptWorkspace(JsonSettings.Encode(Workspace) != json ? JsonSettings.Decode(json, new WorkspaceDefinition()) : Workspace);
                foreach (string key in selectionDrafts.Keys.Where(key => !Workspace.Tasks.Any(t => !t.Deleted && t.Id == key)).ToList())
                    selectionDrafts.Remove(key);
                foreach (string key in loaded.Keys.Where(key => !Workspace.Sources.Any(s => !s.Deleted && s.Id == key && string.Equals(s.Location, loaded[key].Root, StringComparison.OrdinalIgnoreCase))).ToList())
                {
                    loaded.Remove(key);
                    sourceReads.Forget(key);
                    Backend.ForgetSourceSnapshot(key);
                }

                LoadTaskControls();
            }

            if (sourceFiles)
                foreach (var source in Workspace.Sources.Where(s => !s.Deleted && (sources.Count == 0 || sources.Contains(s.Id))))
                {
                    LoadedSource value;
                    if (Backend.TrySourceSnapshot(source.Id, out value) && string.Equals(value.Root, source.Location, StringComparison.OrdinalIgnoreCase))
                        loaded[source.Id] = value;
                }

            bool changedArchives = archives && Backend.Catalog.DataChangeStamp != presentedCatalogStamp;
            if (workspace || topology)
            {
                historyDirty = true;
                RefreshCatalog(true, false);
            }
            else if (changedArchives)
            {
                historyDirty = true;
                managersDirty = true;
                if (tasks == null || CurrentTask != null && tasks.Contains(CurrentTask.Id))
                    RefreshCatalog(Control<TabControl>("Tabs").SelectedIndex == 2, false);
                else
                {
                    if (Control<TabControl>("Tabs").SelectedIndex == 2)
                        UpdateManagers();
                    if (historySummaryScope == null || tasks.Overlaps(historySummaryScope))
                        RefreshHistoryCheckSummary();
                }
            }

            if (workspace || topology || rules || sourceFiles && !FileListsRefreshing && (sources.Count == 0 || CurrentTask != null && CurrentTask.SourceIds.Any(sources.Contains)))
                RebuildTree();
            if ((changes & CatalogChange.Schedules) != 0)
                RefreshSchedulePage();
            if (workspace || topology || archives && (changedArchives || ids.Count > 0) || (changes & CatalogChange.Inventory) != 0)
            {
                browserViewChanges++;
                try
                {
                    if (workspace || topology || changedArchives)
                        historyDirty = true;
                    if (Control<TabControl>("Tabs").SelectedIndex == 3)
                    {
                        if (IsFolderHistoryView)
                        {
                            if (workspace || topology || changedArchives)
                                RebuildHistory();
                        }
                        else if (workspace || topology || warehouseRoots == null)
                            RebuildHistory();
                        else
                            PublishWarehouseChanges(ids, folders, changedArchives);
                    }

                    if (!workspace && !topology && warehouseRoots != null && folders.Count > 0 && (Control<TabControl>("Tabs").SelectedIndex != 3 || IsFolderHistoryView))
                        PublishWarehouseChanges(ids, folders, false);
                }
                finally
                {
                    Window.Dispatcher.BeginInvoke(new Action(() => browserViewChanges--), DispatcherPriority.Loaded);
                }
            }

            if ((changes & CatalogChange.Connections) != 0 || topology)
                UpdateRepositoryConnectionViews();
        }

        private void AdoptWorkspace(WorkspaceDefinition value, string selectedId = null)
        {
            string id = selectedId ?? (CurrentTask == null ? null : CurrentTask.Id);
            Workspace = value;
            CurrentTask = Workspace.Tasks.FirstOrDefault(t => !t.Deleted && t.Id == id) ?? Workspace.Tasks.FirstOrDefault(t => !t.Deleted);
        }

        private void ApplyCommittedChanges()
        {
            Backend.FlushRepositoryResults();
            ApplyRepositoryPresentation();
        }

        private string ConnectionStateFor(StorageTopology topology, IEnumerable<string> ids, IDictionary<string, Tuple<string, string>> connections)
        {
            var states = topology.Destinations.Where(d => (!RepositoryModules.IsFileSystem(d.Protocol)) && ids.Contains(d.Id)).Select(d => connections[d.Id].Item1).ToList();
            return states.Count == 0 
                ? "" 
                : states.Any(DesktopBackend.IsServerTrustState) 
                ? states.First(DesktopBackend.IsServerTrustState) 
                : states.Contains("无openlist优化") ? "无openlist优化" : states.Contains("已连接") ? "已连接" : states.Contains("正在连接") ? "正在连接" : states.Contains("连接失败") ? "连接失败" : "空闲";
        }

        private void UpdateRepositoryConnectionViews()
        {
            if (disposed)
                return;
            var topology = Backend.Catalog.LoadTopology();
            var connections = Backend.RepositoryConnectionSnapshot(topology);
            foreach (var row in Grid("WarehouseGrid").Items.OfType<WarehouseView>())
            {
                var ids = topology.Groups.Any(group => group.Id == row.Id) ? topology.ResolveDestinations(row.Id, false) : new List<string>();
                string state = ConnectionStateFor(topology, ids, connections);
                row.ConnectionState = ids.Count == 0 ? "" : row.Id.StartsWith("single:") || state == "无openlist优化" || DesktopBackend.IsServerTrustState(state) ? state : "N/A";
                row.ConnectionDetails = DesktopBackend.IsServerTrustState(state) 
                    ? string.Join("\n", ids.Where(id => DesktopBackend.IsServerTrustState(connections[id].Item1)).Select(id => connections[id].Item2)) 
                    : row.ConnectionState == "无openlist优化" 
                    ? string.Join("\n", ids.Where(id => connections[id].Item1 == "无openlist优化")
                    .Select(id => connections[id].Item2)) 
                    : row.ConnectionState == "连接失败" 
                    ? (connections.ContainsKey(row.Id.StartsWith("single:") ? row.Id.Substring(7) : row.Id) 
                    ? connections[row.Id.StartsWith("single:") ? row.Id.Substring(7) : row.Id].Item2 
                    : null) 
                    : row.ConnectionState;
            }

            if (warehouseRoots != null)
                foreach (var root in warehouseRoots)
                {
                    root.ConnectionState = ConnectionStateFor(topology, new[] { root.DestinationId }, connections);
                    root.ConnectionDetails = root.ConnectionState == "连接失败" || root.ConnectionState == "无openlist优化" || DesktopBackend.IsServerTrustState(root.ConnectionState) 
                        ? connections[root.DestinationId].Item2 
                        : root.ConnectionState;
                }
        }

        private bool RepositoryTopologyChanged()
        {
            var topology = Backend.Catalog.LoadTopology();
            var choice = Control<ComboBox>("StatusWarehouseFilter").SelectedItem as ChoiceRow;
            var allowed = choice == null || string.IsNullOrEmpty(choice.Id) 
                ? null 
                : new HashSet<string>(topology.Groups.Any(group => group.Id == choice.Id) ? (IEnumerable<string>)topology.ResolveDestinations(choice.Id, false) : new string[0]);
            var expected = topology.Destinations.Where(d => !d.Retired && (allowed == null || allowed.Contains(d.Id))).ToList();
            return expected.Count != warehouseRoots.Count || expected.Any(d => !warehouseRoots.Any(node => node.DestinationId == d.Id && node.Entry.Location == d.Location));
        }

        private void EnterBackupFolder(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || !SourceViewCurrent)
                return;
            var row = Ancestor<DataGridRow>(e.OriginalSource as DependencyObject);
            var cell = Ancestor<DataGridCell>(e.OriginalSource as DependencyObject);
            var node = row == null ? null : row.Item as FileTreeNode;
            if (node == null || !node.Directory || cell != null && cell.Column is DataGridCheckBoxColumn)
                return;
            for (var parent = node.Parent; parent != null; parent = parent.Parent)
                parent.IsExpanded = true;
            node.IsExpanded = true;
            node.IsSelected = true;
            ShowNode(node);
            var tree = Control<TreeView>("SourceTree");
            var item = RealizeTreeItem(tree, node);
            if (item != null)
                item.BringIntoView();
            e.Handled = true;
        }
    }
}
