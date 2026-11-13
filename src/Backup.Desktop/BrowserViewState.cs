using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Backup.Core;

namespace Backup.Desktop
{
    internal sealed class BrowserScrollState
    {
        internal double Horizontal, Vertical;
        internal string Anchor;
        internal static T Visual<T>(DependencyObject root)
            where T : DependencyObject
        {
            if (root == null)
                return null;
            var match = root as T;
            if (match != null)
                return match;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                match = Visual<T>(VisualTreeHelper.GetChild(root, i));
                if (match != null)
                    return match;
            }

            return null;
        }

        internal static BrowserScrollState Capture(ItemsControl control, Func<object, string> key = null)
        {
            var scroll = Visual<ScrollViewer>(control);
            var state = new BrowserScrollState();
            if (scroll == null)
                return state;
            state.Horizontal = scroll.HorizontalOffset;
            state.Vertical = scroll.VerticalOffset;
            int index = (int)Math.Floor(state.Vertical);
            if (key != null && scroll.CanContentScroll && index >= 0 && index < control.Items.Count)
                state.Anchor = key(control.Items[index]);
            return state;
        }

        internal void Restore(ItemsControl control, Func<object, string> key = null)
        {
            var scroll = Visual<ScrollViewer>(control);
            if (scroll == null)
                return;
            double offset = Vertical;
            if (Anchor != null && key != null && scroll.CanContentScroll)
                for (int i = 0; i < control.Items.Count; i++)
                    if (key(control.Items[i]) == Anchor)
                    {
                        offset = i + Vertical - Math.Floor(Vertical);
                        break;
                    }

            scroll.ScrollToHorizontalOffset(Horizontal);
            scroll.ScrollToVerticalOffset(offset);
        }
    }

    internal sealed class BrowserGridState
    {
        internal string[] Selected = new string[0];
        internal string Current;
        internal int Column;
        internal bool Focused;
        internal BrowserScrollState Scroll;
        internal SortDescription[] Sorts;
        internal static BrowserGridState Capture(DataGrid grid, Func<object, string> key)
        {
            return new BrowserGridState
            {
                Selected = grid.SelectedItems.Cast<object>().Select(key).Where(value => value != null).ToArray(),
                Current = key(grid.CurrentCell.IsValid ? grid.CurrentCell.Item : grid.SelectedItem),
                Column = Math.Max(0, grid.Columns.IndexOf(grid.CurrentColumn)),
                Focused = grid.IsKeyboardFocusWithin,
                Scroll = BrowserScrollState.Capture(grid, key),
                Sorts = grid.Items.SortDescriptions.ToArray()
            };
        }

        internal bool Restore(DataGrid grid, Func<object, string> key)
        {
            if (!grid.Items.SortDescriptions.SequenceEqual(Sorts))
                using (grid.Items.DeferRefresh())
                {
                    grid.Items.SortDescriptions.Clear();
                    foreach (var sort in Sorts)
                        grid.Items.SortDescriptions.Add(sort);
                }

            var rows = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (object item in grid.Items)
            {
                string id = key(item);
                if (id != null && !rows.ContainsKey(id))
                    rows.Add(id, item);
            }

            var selected = new List<object>();
            var seen = new HashSet<object>();
            foreach (string id in Selected)
            {
                object row;
                if (rows.TryGetValue(id, out row) && seen.Add(row))
                {
                    selected.Add(row);
                    if (grid.SelectionMode == DataGridSelectionMode.Single)
                        break;
                }
            }

            if (!grid.SelectedItems.Cast<object>().SequenceEqual(selected))
            {
                grid.UnselectAll();
                if (grid.SelectionMode == DataGridSelectionMode.Single)
                    grid.SelectedItem = selected.FirstOrDefault();
                else if (selected.Count == grid.Items.Count && selected.SequenceEqual(grid.Items.Cast<object>()))
                    grid.SelectAll();
                else
                    foreach (object row in selected)
                        grid.SelectedItems.Add(row);
            }

            object current;
            if (Current != null && rows.TryGetValue(Current, out current) && grid.Columns.Count > 0)
                grid.CurrentCell = new DataGridCellInfo(current, grid.Columns[Math.Min(Column, grid.Columns.Count - 1)]);
            return Current != null && !rows.ContainsKey(Current);
        }

        internal void Focus(DataGrid grid)
        {
            if (!Focused)
                return;
            grid.UpdateLayout();
            var row = grid.CurrentCell.IsValid ? grid.ItemContainerGenerator.ContainerFromItem(grid.CurrentCell.Item) as DataGridRow : null;
            var cell = row == null ? null : FindCell(row, grid.CurrentColumn);
            if (cell != null)
                cell.Focus();
            else
                grid.Focus();
        }

        private static DataGridCell FindCell(DependencyObject root, DataGridColumn column)
        {
            var cell = root as DataGridCell;
            if (cell != null && cell.Column == column)
                return cell;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                cell = FindCell(VisualTreeHelper.GetChild(root, i), column);
                if (cell != null)
                    return cell;
            }

            return null;
        }
    }

    public sealed partial class MainWindowController
    {
        private int browserViewChanges, browserInputVersion, sourceViewVersion, historyViewVersion;
        private string displayedSourceContext, historyTreeRepository, historyItemsFolder, historyItemsRepository;
        private readonly Dictionary<DataGrid, int> gridViewVersions = new Dictionary<DataGrid, int>();
        private string SourceViewContext
        {
            get
            {
                return Backend.RepositoryId + "|" + (CurrentTask == null ? "" : CurrentTask.Id);
            }
        }

        private bool SourceViewCurrent
        {
            get
            {
                return displayedSourceContext == SourceViewContext;
            }
        }

        private void InitializeBrowserViewState()
        {
            Window.PreviewMouseDown += delegate
            {
                browserInputVersion++;
            };
            Window.PreviewMouseWheel += delegate
            {
                browserInputVersion++;
            };
            Window.PreviewKeyDown += delegate
            {
                browserInputVersion++;
            };
        }

        private static string SourceKey(object item)
        {
            var node = item as FileTreeNode;
            return node == null ? null : node.FullPath.ToUpperInvariant();
        }

        private static IEnumerable<FileTreeNode> SourceNodes(FileTreeWorkspace tree)
        {
            return SourceTreeNavigation.Nodes(tree);
        }

        private static FileTreeNode SourceNode(FileTreeWorkspace tree, string path)
        {
            return SourceTreeNavigation.Find(tree, path);
        }

        private static FileTreeNode SourceParent(FileTreeWorkspace tree, string path)
        {
            return SourceTreeNavigation.Parent(tree, path);
        }

        private SourceViewState CaptureSourceView()
        {
            var tree = Control<TreeView>("SourceTree");
            var selected = tree.SelectedItem as FileTreeNode;
            var grid = Grid("FileGrid");
            var target = grid.SelectedItem as FileTreeNode ?? selected ?? previewParent;
            var directories = SourceNodes(Tree).Where(node => node.Directory).ToList();
            return new SourceViewState
            {
                Valid = SourceViewCurrent,
                Folder = previewParent == null ? null : previewParent.FullPath,
                SelectedTree = selected == null ? null : selected.FullPath,
                Target = target == null ? null : target.FullPath,
                TargetDirectory = target != null && target.Directory,
                TreeFocus = tree.IsKeyboardFocusWithin,
                Known = new HashSet<string>(directories.Select(node => node.FullPath), StringComparer.OrdinalIgnoreCase),
                Expanded = new HashSet<string>(directories.Where(node => node.IsExpanded).Select(node => node.FullPath), StringComparer.OrdinalIgnoreCase),
                Scroll = BrowserScrollState.Capture(tree),
                Grid = BrowserGridState.Capture(grid, SourceKey)
            };
        }

        // Only the active, newly empty directory needs a filesystem existence check; no directory scan.
        private static void KeepSourceNavigationFolder(FileTreeWorkspace next, SourceViewState state, IList<LoadedSource> sources, BackupExclusions exclusions, IList<string> protectedRoots, FileSelection selection)
        {
            SourceTreeNavigation.KeepEmptyFolder(next, state, sources, exclusions, protectedRoots, selection);
        }

        private static TreeViewItem TreeContainer(ItemsControl owner, object target)
        {
            foreach (object item in owner.Items)
            {
                var container = owner.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
                if (container == null)
                    continue;
                if (ReferenceEquals(item, target))
                    return container;
                if (container.IsExpanded)
                {
                    var nested = TreeContainer(container, target);
                    if (nested != null)
                        return nested;
                }
            }

            return null;
        }

        private static TreeViewItem RealizeTreeItem(TreeView tree, object target)
        {
            if (target == null)
                return null;
            var path = new List<object>();
            for (object node = target; node != null;)
            {
                path.Add(node);
                var source = node as FileTreeNode;
                var history = node as BackupHistoryNode;
                node = source != null ? (object)source.Parent : history == null ? null : history.Parent;
            }

            path.Reverse();
            ItemsControl owner = tree;
            TreeViewItem result = null;
            foreach (object node in path)
            {
                owner.ApplyTemplate();
                owner.UpdateLayout();
                result = owner.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
                if (result == null)
                {
                    var panel = BrowserScrollState.Visual<VirtualizingStackPanel>(owner);
                    int index = owner.Items.IndexOf(node);
                    if (panel != null && index >= 0)
                    {
                        panel.BringIndexIntoViewPublic(index);
                        owner.UpdateLayout();
                        result = owner.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
                    }
                }

                if (result == null)
                    return null;
                if (!ReferenceEquals(node, target))
                    result.IsExpanded = true;
                owner = result;
            }

            return result;
        }

        private void PublishSourceView(SourceViewState state)
        {
            var tree = Control<TreeView>("SourceTree");
            var grid = Grid("FileGrid");
            var folder = state.Valid ? SourceNode(Tree, state.Folder) : null;
            bool lost = state.Valid && state.Target != null && SourceNode(Tree, state.Target) == null;
            if (folder == null)
                folder = state.Valid ? SourceParent(Tree, state.Target ?? state.Folder) : Tree.Roots.FirstOrDefault();
            var selected = state.Valid ? SourceNode(Tree, state.SelectedTree) : null;
            if (lost)
                selected = SourceParent(Tree, state.Target);
            browserViewChanges++;
            try
            {
                foreach (var node in SourceNodes(Tree))
                {
                    if (node.SelectionChanged == null)
                        node.SelectionChanged = (item, include) =>
                        {
                            if (!MutationBlocked && SourceViewCurrent)
                                ChangeSelection(item, include);
                        };
                    if (state.Valid && state.Known.Contains(node.FullPath))
                        node.IsExpanded = state.Expanded.Contains(node.FullPath);
                    node.IsSelected = ReferenceEquals(node, selected);
                }

                if (lost)
                    for (var parent = selected == null ? null : selected.Parent; parent != null; parent = parent.Parent)
                        parent.IsExpanded = true;
                tree.ItemsSource = Tree.Roots;
                previewParent = folder;
                grid.ItemsSource = folder == null ? null : folder.Children;
                if (state.Valid && folder != null && folder.FullPath.Equals(state.Folder, StringComparison.OrdinalIgnoreCase))
                    state.Grid.Restore(grid, SourceKey);
                ShowSelectionSummary(Tree.SelectedFileCount, Tree.SelectedBytes);
                UpdateSelectionHeaders();
                if (folder != null)
                    ShowFileDetails(grid.SelectedItem as FileTreeNode ?? folder);
                else
                    Text("FileDetails").Text = "没有匹配的文件；空文件夹和排除项不显示。";
            }
            finally
            {
                browserViewChanges--;
            }

            int version = ++sourceViewVersion, input = browserInputVersion;
            Window.Dispatcher.BeginInvoke(
                new Action(delegate
            {
                if (disposed || version != sourceViewVersion || input != browserInputVersion)
                    return;
                tree.UpdateLayout();
                grid.UpdateLayout();
                if (state.Valid)
                {
                    state.Scroll.Restore(tree);
                    if (folder != null && folder.FullPath.Equals(state.Folder, StringComparison.OrdinalIgnoreCase))
                        state.Grid.Scroll.Restore(grid, SourceKey);
                }

                var target = selected ?? folder;
                var container = state.Valid && target != null && (lost || state.TreeFocus || selected != null) ? TreeContainer(tree, target) ?? RealizeTreeItem(tree, target) : null;
                if (lost && container != null)
                    container.BringIntoView();
                if (Window.IsActive && state.Valid)
                {
                    if (lost && (state.TreeFocus || state.Grid.Focused) || state.TreeFocus)
                    {
                        if (container != null)
                            container.Focus();
                        else
                            tree.Focus();
                    }
                    else
                        state.Grid.Focus(grid);
                    if (!lost)
                        state.Scroll.Restore(tree);
                    if (folder != null && folder.FullPath.Equals(state.Folder, StringComparison.OrdinalIgnoreCase))
                        state.Grid.Scroll.Restore(grid, SourceKey);
                }
            }),
                DispatcherPriority.Loaded
            );
        }

        private void ReplaceGridRows<T>(DataGrid grid, IList<T> rows, Func<T, string> key)
        {
            if (grid.ItemsSource != null && grid.ItemsSource.Cast<object>().SequenceEqual(rows.Cast<object>()))
                return;
            Func<object, string> identify = item => item is T ? key((T)item) : null;
            var state = BrowserGridState.Capture(grid, identify);
            browserViewChanges++;
            try
            {
                grid.ItemsSource = rows;
                state.Restore(grid, identify);
            }
            finally
            {
                browserViewChanges--;
            }

            int version;
            gridViewVersions.TryGetValue(grid, out version);
            gridViewVersions[grid] = ++version;
            int expected = version, input = browserInputVersion;
            Window.Dispatcher.BeginInvoke(
                new Action(delegate
            {
                int current;
                if (disposed || input != browserInputVersion || !gridViewVersions.TryGetValue(grid, out current) || current != expected)
                    return;
                grid.UpdateLayout();
                state.Scroll.Restore(grid, identify);
                if (Window.IsActive)
                    state.Focus(grid);
                state.Scroll.Restore(grid, identify);
            }),
                DispatcherPriority.Loaded
            );
        }

        private static string EntryKey(BackupHistoryNode node)
        {
            string location = node.Entry.Location ?? "";
            if (location.IndexOf("://", StringComparison.Ordinal) < 0)
                location = location.ToUpperInvariant();
            return "E|" + node.DestinationId + "|" + location.TrimEnd('\\', '/');
        }

        private static string HistoryKey(object value)
        {
            var node = value as BackupHistoryNode;
            if (node == null)
                return null;
            if (node.Entry != null)
                return EntryKey(node);
            var parent = node;
            while (parent.Parent != null && parent.Entry == null)
                parent = parent.Parent;
            string scope = parent.Entry != null ? EntryKey(parent) : "T|" + parent.NavigationScope;
            return scope +
                (node.File == null ? "|D|" + (node.PathPrefix ?? "").ToUpperInvariant() : "|F|" + node.File.TaskId + "|" + node.ArchiveId + "|" + node.File.Path.ToUpperInvariant());
        }

        private static IEnumerable<BackupHistoryNode> HistoryDirectories(IEnumerable<BackupHistoryNode> roots)
        {
            var pending = new Stack<BackupHistoryNode>(roots.Reverse());
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                yield return node;
                foreach (var child in node.DirectoryChildren.Reverse())
                    pending.Push(child);
            }
        }

        private static TreeViewItem HistoryTreeContainer(TreeView tree, BackupHistoryNode node)
        {
            var path = new Stack<BackupHistoryNode>();
            for (var parent = node; parent != null; parent = parent.Parent)
                path.Push(parent);
            ItemsControl owner = tree;
            TreeViewItem item = null;
            while (path.Count > 0)
            {
                item = owner.ItemContainerGenerator.ContainerFromItem(path.Pop()) as TreeViewItem;
                if (item == null)
                    return null;
                owner = item;
            }

            return item;
        }

        private List<BackupHistoryNode> HistoryRoots()
        {
            var roots = Control<TreeView>("HistoryTree").ItemsSource as IEnumerable<BackupHistoryNode>;
            return roots == null ? new List<BackupHistoryNode>() : roots.ToList();
        }

        private sealed class HistoryViewState
        {
            internal bool Valid, RowsValid, TreeFocus;
            internal int InputVersion;
            internal string Folder, TreeSelection, RowsFolder;
            internal List<string> Ancestors;
            internal HashSet<string> Expanded, Known;
            internal BrowserScrollState Scroll;
            internal BrowserGridState Grid;
            internal BackupHistoryNode TreeNode;
        }

        private HistoryViewState CaptureHistoryView(bool includeTree = true)
        {
            var tree = Control<TreeView>("HistoryTree");
            var ancestors = new List<string>();
            for (var node = historyFolder; node != null; node = node.Parent)
                ancestors.Add(HistoryKey(node));
            var directories = includeTree ? HistoryDirectories(HistoryRoots()).ToList() : new List<BackupHistoryNode>();
            return new HistoryViewState
            {
                Valid = historyTreeRepository == Backend.RepositoryId,
                RowsValid = historyItemsRepository == Backend.RepositoryId,
                InputVersion = browserInputVersion,
                Folder = HistoryKey(historyFolder),
                TreeSelection = HistoryKey(tree.SelectedItem),
                TreeNode = tree.SelectedItem as BackupHistoryNode,
                RowsFolder = historyItemsFolder,
                Ancestors = ancestors,
                TreeFocus = tree.IsKeyboardFocusWithin,
                Known = new HashSet<string>(directories.Select(node => HistoryKey(node)), StringComparer.Ordinal),
                Expanded = new HashSet<string>(directories.Where(node => node.IsExpanded).Select(node => HistoryKey(node)), StringComparer.Ordinal),
                Scroll = BrowserScrollState.Capture(tree),
                Grid = BrowserGridState.Capture(Grid("HistoryPreview"), HistoryKey)
            };
        }

        private BackupHistoryNode PublishHistoryRoots(IList<BackupHistoryNode> roots, HistoryViewState state)
        {
            var all = HistoryDirectories(roots).ToList();
            var nodes = new Dictionary<string, BackupHistoryNode>(StringComparer.Ordinal);
            foreach (var node in all)
                if (!nodes.ContainsKey(HistoryKey(node)))
                    nodes.Add(HistoryKey(node), node);
            BackupHistoryNode folder = null, selected = null;
            if (state.Valid)
            {
                foreach (string key in state.Ancestors)
                    if (nodes.TryGetValue(key, out folder))
                        break;
                if (state.TreeSelection != null)
                    nodes.TryGetValue(state.TreeSelection, out selected);
            }

            if (folder == null && IsFolderHistoryView)
                folder = roots.FirstOrDefault();
            bool fallback = state.Valid && state.Folder != null && !nodes.ContainsKey(state.Folder);
            if (folder != null && selected == null && state.Valid && (fallback || state.TreeSelection != null))
                selected = folder;
            browserViewChanges++;
            try
            {
                foreach (var node in all)
                {
                    string key = HistoryKey(node);
                    if (state.Valid && state.Known.Contains(key))
                        node.IsExpanded = state.Expanded.Contains(key);
                    node.IsSelected = ReferenceEquals(node, selected);
                }

                if (fallback)
                    for (var parent = selected == null ? null : selected.Parent; parent != null; parent = parent.Parent)
                        parent.IsExpanded = true;
                var tree = Control<TreeView>("HistoryTree");
                if (tree.ItemsSource == null || !tree.ItemsSource.Cast<object>().SequenceEqual(roots.Cast<object>()))
                    tree.ItemsSource = roots;
                historyTreeRepository = Backend.RepositoryId;
            }
            finally
            {
                browserViewChanges--;
            }

            state.TreeNode = selected;
            return folder;
        }

        private void BindHistoryRows(BackupHistoryNode folder, IList<BackupHistoryNode> rows, HistoryViewState state = null, string empty = "文件夹是空的")
        {
            state = state ?? CaptureHistoryView(false);
            var grid = Grid("HistoryPreview");
            var tree = Control<TreeView>("HistoryTree");
            string key = HistoryKey(folder);
            bool same = state.RowsValid && state.RowsFolder == key;
            bool lost = false;
            browserViewChanges++;
            try
            {
                if (!ReferenceEquals(grid.ItemsSource, rows))
                    grid.ItemsSource = rows;
                historyItemsFolder = key;
                historyItemsRepository = Backend.RepositoryId;
                if (same)
                    lost = state.Grid.Restore(grid, HistoryKey);
                Text("HistoryEmptyNotice").Text = empty;
                Text("HistoryEmptyNotice").Visibility = rows == null || rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (lost && folder != null)
                    folder.IsSelected = true;
            }
            finally
            {
                browserViewChanges--;
            }

            UpdateRemoteTaskActions();
            var selected = grid.SelectedItem as BackupHistoryNode;
            if (selected != null)
                ShowHistoryDetails(selected);
            var treeTarget = lost ? folder : state.TreeNode ?? folder;
            int version = ++historyViewVersion, input = browserInputVersion;
            Window.Dispatcher.BeginInvoke(
                new Action(delegate
            {
                if (disposed || version != historyViewVersion || input != browserInputVersion)
                    return;
                browserViewChanges++;
                try
                {
                    tree.UpdateLayout();
                    grid.UpdateLayout();
                    if (state.Valid)
                        state.Scroll.Restore(tree);
                    if (same)
                        state.Grid.Scroll.Restore(grid, HistoryKey);
                    bool needsTree = lost || state.TreeFocus || state.Grid.Focused && !same || treeTarget != null && treeTarget.IsSelected;
                    var item = treeTarget == null || !needsTree ? null : HistoryTreeContainer(tree, treeTarget) ?? RealizeTreeItem(tree, treeTarget);
                    if ((lost || !same) && item != null)
                        item.BringIntoView();
                    if (Window.IsActive)
                    {
                        if (state.TreeFocus || state.Grid.Focused && (!same || lost))
                        {
                            if (item != null)
                                item.Focus();
                            else
                                tree.Focus();
                        }
                        else if (same)
                            state.Grid.Focus(grid);
                        if (state.Valid && same && !lost)
                            state.Scroll.Restore(tree);
                        if (same)
                            state.Grid.Scroll.Restore(grid, HistoryKey);
                    }
                }
                finally
                {
                    browserViewChanges--;
                }
            }),
                DispatcherPriority.Loaded
            );
        }
    }
}
