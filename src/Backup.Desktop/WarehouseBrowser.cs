using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class MainWindowController
    {
        private int warehouseBrowseGeneration, warehouseDetailsGeneration;
        private List<BackupHistoryNode> warehouseRoots;
        private readonly Dictionary<string, Task<WarehouseFolderSnapshot>> warehouseReads = new Dictionary<string, Task<WarehouseFolderSnapshot>>();
        private string warehouseBrowserKey;
        private System.Threading.CancellationTokenSource browsingCancellation = new System.Threading.CancellationTokenSource();
        private void AdvanceBrowserView()
        {
            viewedIntegrityGeneration++;
            warehouseBrowseGeneration++;
            warehouseDetailsGeneration++;
            historyBuildGeneration++;
        }

        private void CancelBackgroundBrowsing()
        {
            var previous = browsingCancellation;
            browsingCancellation = new System.Threading.CancellationTokenSource();
            previous.Cancel();
            warehouseReads.Clear();
            AdvanceBrowserView();
        }

        private async Task RebuildWarehouseBrowserAsync(bool readSelectedWarehouse = false)
        {
            string id = "";
            var topology = Backend.Catalog.LoadTopology();
            var members = string.IsNullOrEmpty(id) ? null : new HashSet<string>(topology.ResolveDestinations(id, false));
            var destinations = topology.Destinations.Where(d => !d.Retired && (members == null || members.Contains(d.Id))).ToList();
            string key = id + "|" + string.Join("|", destinations.Select(d => d.Id + ":" + d.Protocol + ":" + d.Location + ":" + topology.Groups.Single(g => g.Id == "single:" + d.Id).Name));
            var state = CaptureHistoryView();
            int generation = warehouseBrowseGeneration;
            if (key != warehouseBrowserKey || warehouseRoots == null)
            {
                bool filterChanged = warehouseBrowserKey == null || !warehouseBrowserKey.StartsWith(id + "|", StringComparison.Ordinal);
                var previous = warehouseRoots ?? new List<BackupHistoryNode>();
                AdvanceBrowserView();
                generation = warehouseBrowseGeneration;
                warehouseBrowserKey = key;
                warehouseRoots = new List<BackupHistoryNode>();
                foreach (var d in destinations)
                {
                    var next = new BackupHistoryNode
                    {
                        Entry = new StorageEntry
                        {
                            Name = d.Id,
                            Location = d.Location,
                            Directory = true
                        },
                        IsWarehouseRoot = true,
                        Name = "仓库：" + Display.TargetLabel(topology, topology.Groups.Single(g => g.Id == "single:" + d.Id)),
                        DestinationId = d.Id,
                        PathPrefix = d.Location,
                        BrowseLocation = d.Location,
                        Details = d.Location
                    };
                    var retained = previous.FirstOrDefault(node => HistoryKey(node) == HistoryKey(next));
                    if (retained != null)
                    {
                        retained.UpdateMetadata(next);
                        next = retained;
                    }

                    warehouseRoots.Add(next);
                    if (!RepositoryModules.IsFileSystem(d.Protocol))
                    {
                        next.ConnectionState = Backend.RepositoryConnectionState(d.Id);
                        next.ConnectionDetails = next.ConnectionState == "连接失败" ? Backend.RepositoryConnectionError(d.Id) : next.ConnectionState;
                    }
                }

                if (filterChanged && !id.StartsWith("single:", StringComparison.Ordinal))
                {
                    state.Folder = null;
                    state.TreeSelection = null;
                    state.Ancestors.Clear();
                }

                PublishHistoryRoots(warehouseRoots, state);
                historyFolder = null;
            }

            if (disposed || IsFolderHistoryView || warehouseBrowserKey != key || generation != warehouseBrowseGeneration)
                return;
            var folder = PublishHistoryRoots(warehouseRoots, state) ?? (id.StartsWith("single:", StringComparison.Ordinal) ? warehouseRoots.FirstOrDefault() : null);
            historyFolder = folder;
            if (folder == null)
            {
                ShowUnselectedWarehouse(state);
                return;
            }

            if (readSelectedWarehouse)
            {
                BindWarehouseFolder(folder, state);
                await OpenWarehouseFolderAsync(folder, true, false);
                return;
            }

            await LoadCachedWarehouseTreeAsync(folder);
            if (disposed || IsFolderHistoryView || generation != warehouseBrowseGeneration)
                return;
            BindWarehouseFolder(folder, state);
        }

        private void ShowUnselectedWarehouse(HistoryViewState state)
        {
            Text("HistoryPath").Text = "请选择仓库或目录";
            Text("HistoryPath").ToolTip = null;
            Text("HistoryDetails").Text = "请在左侧目录树选择仓库或目录；只读取所选目标的当前目录。";
            BindHistoryRows(null, new List<BackupHistoryNode>(), state, "请选择仓库或目录");
        }

        private async Task<bool> LoadCachedWarehouseTreeAsync(BackupHistoryNode node)
        {
            int generation = warehouseBrowseGeneration, request = node.RawRequestVersion;
            var snapshot = await Task.Run(() =>
            {
                var cached = Backend.Catalog.GetInventoryFolder(node.DestinationId, node.Entry.Location);
                return cached == null 
                    ? null 
                    : new WarehouseFolderSnapshot
                {
                    Nodes = Backend.ReadCachedWarehouseFolder(node.DestinationId, node.Entry.Location, cached),
                    Notice = DesktopBackend.InventoryNotice(cached),
                    CatalogStamp = Backend.Catalog.DataChangeStamp
                };
            });
            if (snapshot == null || disposed || generation != warehouseBrowseGeneration || request != node.RawRequestVersion)
                return false;
            ApplyWarehouseChildren(node, snapshot.Nodes);
            node.RawCatalogStamp = snapshot.CatalogStamp;
            node.RawInventoryNotice = snapshot.Notice;
            return true;
        }

        private static BackupHistoryNode WarehouseDirectory(BackupHistoryNode node)
        {
            for (; node != null; node = node.Parent)
                if (node.Entry != null && node.Entry.Directory && !node.ArchiveContainer)
                    return node;
            return null;
        }

        private async void BeginWarehouseFolder(BackupHistoryNode node, bool refresh = false, bool navigate = true)
        {
            try
            {
                await OpenWarehouseFolderAsync(node, refresh, navigate);
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/WarehouseBrowser.cs:BeginWarehouseFolder", error);
                if (!disposed && !IsFolderHistoryView)
                    Text("HistoryDetails").Text = "仓库读取未完成：" + error.Message;
            }
        }

        public Task OpenWarehouseFolderAsync(BackupHistoryNode node, bool refresh = false, bool navigate = true, int? inputVersion = null)
        {
            if (node == null || node.Entry == null || !node.Entry.Directory || disposed || IsFolderHistoryView || warehouseRoots == null ||
                !warehouseRoots.Any(root => root.DestinationId == node.DestinationId))
                return Task.FromResult(true);
            if (navigate && historyFolder != node)
                AdvanceBrowserView();
            warehouseDetailsGeneration++;
            if (navigate)
            {
                historyNavigationGeneration++;
                historyFolder = node;
            }

            if (node.RawLoading != null && node.RawLoadingGeneration == warehouseBrowseGeneration)
            {
                node.RawReadInputVersion = inputVersion ?? browserInputVersion;
                if (navigate)
                    ShowWarehouseReadingNotice(node);
                return node.RawLoading;
            }

            if (node.RawLoaded && !refresh)
            {
                if (navigate)
                    BindWarehouseFolder(node);
                return Task.FromResult(true);
            }

            node.RawReadInputVersion = inputVersion ?? browserInputVersion;
            node.RawLoadingGeneration = warehouseBrowseGeneration;
            int request = ++node.RawRequestVersion;
            var read = WarehouseRead(node.DestinationId, node.Entry.Location);
            node.RawLoading = LoadWarehouseFolderAsync(node, warehouseBrowseGeneration, request, read);
            return node.RawLoading;
        }

        // Only explicitly requested folders enter this map. Navigation changes
        // presentation; a read keeps its original folder and finishes into cache.
        private Task<WarehouseFolderSnapshot> WarehouseRead(string id, string folder)
        {
            string key = RepositoryRuntime.FolderKey(id, folder);
            Task<WarehouseFolderSnapshot> read;
            if (warehouseReads.TryGetValue(key, out read) && !read.IsCompleted)
                return read;
            read = ReadWarehouseFolderAsync(id, folder, browsingCancellation);
            warehouseReads[key] = read;
            ForgetWarehouseReadAsync(key, read);
            return read;
        }

        private async Task<WarehouseFolderSnapshot> ReadWarehouseFolderAsync(string id, string folder, System.Threading.CancellationTokenSource lifetime)
        {
            using (operations.Read(OperationKind.Browse, lifetime))
                return await Task.Run(() => Backend.RefreshWarehouseFolderSnapshot(id, folder, lifetime.Token, false));
        }

        private async void ForgetWarehouseReadAsync(string key, Task<WarehouseFolderSnapshot> read)
        {
            try
            {
                await read;
            }
            catch (global::System.Exception diagnosticFailure)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/WarehouseBrowser.cs:ForgetWarehouseReadAsync", diagnosticFailure);
            }
            finally
            {
                Task<WarehouseFolderSnapshot> current;
                if (warehouseReads.TryGetValue(key, out current) && ReferenceEquals(current, read))
                    warehouseReads.Remove(key);
            }
        }

        private const string WarehouseReadingText = "正在读取当前目录";
        private void HighlightWarehouseReadingNotice()
        {
            var details = Text("HistoryDetails");
            string value = details.Text;
            int offset = value.LastIndexOf("\n" + WarehouseReadingText, StringComparison.Ordinal);
            if (offset < 0)
                return;
            offset++;
            details.Inlines.Clear();
            details.Inlines.Add(new Run(value.Substring(0, offset)));
            details.Inlines.Add(new Run(WarehouseReadingText) { Foreground = OperationTextColors.Working });
            details.Inlines.Add(new Run(value.Substring(offset + WarehouseReadingText.Length)));
        }

        private void ShowWarehouseReadingNotice(BackupHistoryNode node)
        {
            if (!disposed && !IsFolderHistoryView && historyFolder == node)
            {
                Text("HistoryDetails").Text = node.Entry.Location + "\n" + WarehouseReadingText + "\n保留现有清单，读取完成后更新内容。";
                HighlightWarehouseReadingNotice();
            }
        }

        private async Task LoadWarehouseFolderAsync(BackupHistoryNode node, int generation, int request, Task<WarehouseFolderSnapshot> read)
        {
            node.DirectoryReadNotice = WarehouseReadingText;
            ShowWarehouseReadingNotice(node);
            var cancellation = browsingCancellation.Token;
            try
            {
                await Task.Yield();
                if (!disposed && generation == warehouseBrowseGeneration)
                {
                    bool cached = await LoadCachedWarehouseTreeAsync(node);
                    if (cached && !disposed && generation == warehouseBrowseGeneration && historyFolder == node)
                    {
                        var before = CaptureHistoryView();
                        PublishHistoryRoots(warehouseRoots ?? HistoryRoots(), before);
                        BindWarehouseFolder(node, before);
                    }
                }

                var snapshot = await read;
                if (disposed || cancellation.IsCancellationRequested || generation != warehouseBrowseGeneration || request != node.RawRequestVersion)
                    return;
                var state = CaptureHistoryView();
                ApplyWarehouseChildren(node, snapshot.Nodes);
                node.RawInventoryVersion = snapshot.Version;
                node.RawCatalogStamp = snapshot.CatalogStamp;
                node.RawInventoryNotice = snapshot.Notice;
                node.RawLoaded = true;
                node.DirectoryReadNotice = "当前目录读取完成";
                if (!IsFolderHistoryView)
                {
                    var folder = PublishHistoryRoots(warehouseRoots ?? HistoryRoots(), state);
                    historyFolder = folder;
                    if (folder != null)
                    {
                        if (folder.Entry != null && !folder.ArchiveContainer)
                            BindWarehouseFolder(folder, state);
                        else
                            await ShowTaskFolderAsync(folder, state);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (!disposed && generation == warehouseBrowseGeneration && request == node.RawRequestVersion)
                {
                    node.DirectoryReadNotice = "当前目录读取已取消，已保留现有清单。";
                    if (!IsFolderHistoryView && historyFolder == node)
                        Text("HistoryDetails").Text = node.Entry.Location + "\n" + node.DirectoryReadNotice;
                }
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/WarehouseBrowser.cs:LoadWarehouseFolderAsync", error);
                if (disposed || generation != warehouseBrowseGeneration || request != node.RawRequestVersion)
                    return;
                node.RawLoaded = true;
                node.DirectoryReadNotice = "当前目录读取失败；仓库暂不可访问，已保留现有清单：" + error.Message;
                var state = CaptureHistoryView();
                if (!IsFolderHistoryView)
                {
                    var folder = PublishHistoryRoots(warehouseRoots ?? HistoryRoots(), state);
                    historyFolder = folder;
                    if (folder != null)
                    {
                        if (folder.Entry != null && !folder.ArchiveContainer)
                            BindWarehouseFolder(folder, state);
                        else
                        {
                            historyFolderLoading = ShowTaskFolderAsync(folder, state);
                            ObserveHistoryLoading(historyFolderLoading);
                        }
                    }
                }

                if (historyFolder == node)
                {
                    if (Grid("HistoryPreview").Items.Count == 0)
                        SetHistoryItems(node.Children, "暂时无法读取文件夹");
                    Text("HistoryDetails").Text = node.Entry.Location + "\n" + node.DirectoryReadNotice;
                }
            }
            finally
            {
                if (request == node.RawRequestVersion)
                    node.RawLoading = null;
            }
        }

        private void ApplyWarehouseChildren(BackupHistoryNode node, IList<BackupHistoryNode> children)
        {
            var old = node.Children.Where(child => child.Entry != null && !child.ArchiveContainer).GroupBy(child => HistoryKey(child)).ToDictionary(group => group.Key, group => group.First());
            var next = new List<BackupHistoryNode>();
            foreach (var child in children)
            {
                BackupHistoryNode retained;
                if (child.Entry != null && !child.ArchiveContainer && old.TryGetValue(HistoryKey(child), out retained) && retained.Entry.Directory == child.Entry.Directory)
                {
                    retained.UpdateMetadata(child);
                    next.Add(retained);
                }
                else
                    next.Add(child);
            }

            node.Children = next;
        }

        private void SetHistoryItems(IList<BackupHistoryNode> nodes, string empty = "文件夹是空的")
        {
            BindHistoryRows(historyFolder, nodes, null, empty);
        }

        private void RefreshWarehouseNotice(BackupHistoryNode node)
        {
            Text("HistoryPath").Text = node.Entry.Location;
            Text("HistoryPath").ToolTip = node.Entry.Location;
            ShowWarehouseEntry(node);
            if (node.RawLoadingGeneration == warehouseBrowseGeneration && node.DirectoryReadNotice == WarehouseReadingText)
            {
                Text("HistoryDetails").Text += "\n" + WarehouseReadingText;
                HighlightWarehouseReadingNotice();
            }
        }

        private void BindWarehouseFolder(BackupHistoryNode node, HistoryViewState state = null)
        {
            RefreshWarehouseNotice(node);
            BindHistoryRows(node, node.Children, state);
        }

        private void ShowWarehouseEntry(BackupHistoryNode node)
        {
            WarehouseDetailsLoading = node.Entry == null ? ShowTaskHistoryDetailsAsync(node) : ShowWarehouseEntryAsync(node);
        }

        private Task warehouseDetailsLoading = Task.FromResult(true);
        public Task WarehouseDetailsLoading
        {
            get
            {
                return warehouseDetailsLoading;
            }

            private set
            {
                warehouseDetailsLoading = value;
            }
        }

        private Task ShowWarehouseEntryAsync(BackupHistoryNode node)
        {
            warehouseDetailsGeneration++;
            Text("HistoryDetails").Text = Backend.ReadHistoryProperties(node, false).DetailsText;
            return Task.FromResult(true);
        }

        public async Task RefreshWarehouseBrowserAsync()
        {
            if (warehouseRoots == null || warehouseBrowserKey == null)
            {
                await RebuildWarehouseBrowserAsync(true);
                return;
            }

            var node = WarehouseDirectory(historyFolder);
            if (node != null)
                await OpenWarehouseFolderAsync(node, true, false);
        }
    }
}
