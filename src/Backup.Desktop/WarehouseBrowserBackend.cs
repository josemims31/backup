using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
#if UI_TEST
        public Action<string, string> TestWarehouseRead;
        public Action<string, string, IList<StorageEntry>> TestWarehouseListingResult;
        public Action<BackupNode> TestNodeDatabaseRead;
#endif
        private static bool SameFolder(string left, string right, bool filesystem)
        {
            return filesystem 
                ? StoragePath.NormalizeRoot(left).Equals(StoragePath.NormalizeRoot(right), StringComparison.OrdinalIgnoreCase) 
                : left.TrimEnd('/').Equals(right.TrimEnd('/'), StringComparison.Ordinal);
        }

        private IManagedBackupDestination OpenWarehouseLocation(string id, string folder, System.Threading.CancellationToken cancellation = default(System.Threading.CancellationToken))
        {
            return OpenWarehouseLocation(Catalog.LoadTopology().Destinations.Single(x => x.Id == id), folder, cancellation);
        }

        private IManagedBackupDestination OpenWarehouseLocation(DestinationDefinition d, string folder, System.Threading.CancellationToken cancellation)
        {
            if (!cancellation.CanBeCanceled)
                cancellation = OperationCancellation;
            cancellation.ThrowIfCancellationRequested();
            return RepositoryDirectory(d, folder, cancellation);
        }

        public IList<BackupHistoryNode> ListWarehouseFolder(string id, string folder)
        {
            return BuildWarehouseNodes(id, folder, ListWarehouseEntries(id, folder, OperationCancellation));
        }

        private IList<StorageEntry> ListWarehouseEntries(string id, string folder, System.Threading.CancellationToken cancellation, bool fresh = false)
        {
#if UI_TEST
            if (TestWarehouseRead != null)
                TestWarehouseRead(id, folder);
#endif
            if (!cancellation.CanBeCanceled)
                cancellation = OperationCancellation;
            cancellation.ThrowIfCancellationRequested();
            var destination = Catalog.LoadTopology().Destinations.Single(d => d.Id == id);
            RepositoryModules.CheckDirectory(destination.Protocol, destination.Location, folder);
            return RepositoryConnection(destination).Session.ListEntriesAt(folder, cancellation, fresh);
        }

        private IList<BackupHistoryNode> BuildWarehouseNodes(string id, string folder, IList<StorageEntry> entries)
        {
            var topology = Catalog.LoadTopology();
            var d = topology.Destinations.Single(x => x.Id == id);
            bool filesystem = RepositoryModules.IsFileSystem(d.Protocol);
            string relative = ObjectStoragePath.Relative(d, folder);
            var workspace = JsonSettings.Decode(Catalog.GetSetting("workspace_v3"), new WorkspaceDefinition());
            var expectedObjects = Catalog.WarehouseFolderObjects(id, relative);
            var requested = new HashSet<string>(entries.Where(item => !item.Directory).Select(item => item.Name).Concat(expectedObjects.Select(item => item.Name)), StringComparer.Ordinal);
            // Archive-level ownership survives a stopped writer even when no
            // volume ever reached registration. This is local metadata only.
            var problems = CachedWarehouseProblemArchives(id, workspace, topology);
            if (relative.Length == 0)
                foreach (var problem in problems)
                    requested.UnionWith(Catalog.GetObjects(problem.ArchiveId).Select(item => item.Name));
            var known = Catalog.GetWarehouseObjectsByName(id, requested).ToDictionary(o => o.Name, filesystem ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            if (filesystem)
            {
                var unmatched = requested.Where(name => !known.ContainsKey(name) && BackupObjectNaming.IsManaged(name)).ToList();
                foreach (var item in Catalog.GetWarehouseObjectsByName(id, unmatched, true))
                    known[item.Name] = item;
            }

            var incomplete = problems.Where(a => !Catalog.HasCompleteArchiveManifest(a.ArchiveId)).ToDictionary(a => a.ArchiveName, StringComparer.OrdinalIgnoreCase);
            var present = new HashSet<string>(entries.Select(item => item.Name), filesystem ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var expected = new HashSet<string>(expectedObjects.Select(o => o.Name), filesystem ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var missing = known.Values.Where(item => expected.Contains(item.Name) && !present.Contains(item.Name) && item.State != "retired" && item.State != "reconcile-required").ToList();
            var missingNames = new HashSet<string>(missing.Select(item => item.Name), filesystem ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var listed = entries.Concat(missing.Select(item => new StorageEntry { Name = item.Name, Location = ObjectLocation(d, item.Name), Size = item.Size }));
            var group = topology.Groups.FirstOrDefault(value => value.Id == "single:" + id);
            var result = new List<BackupHistoryNode>();
            foreach (var item in listed.OrderByDescending(x => x.Directory).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                StoredArchiveObject archive;
                bool registered = !item.Directory && known.TryGetValue(item.Name, out archive);
                archive = registered ? known[item.Name] : null;
                ArchiveSummary unfinished;
                bool incompleteManifest = !item.Directory && incomplete.TryGetValue(BackupObjectNaming.ArchiveName(item.Name) ?? "", out unfinished);
                if (incompleteManifest && !registered)
                {
                    unfinished = incomplete[BackupObjectNaming.ArchiveName(item.Name)];
                    archive = new StoredArchiveObject
                    {
                        Name = item.Name,
                        Size = -1,
                        ArchiveId = unfinished.ArchiveId,
                        TaskId = unfinished.TaskId,
                        State = "damaged",
                        ArchiveStatus = unfinished.Status
                    };
                    registered = true;
                }

                bool missingCopy = missingNames.Contains(item.Name);
                bool ownedTemporary = !item.Directory && !registered && BackupObjectNaming.IsTemporary(item.Name) && Catalog.UploadStages(id, BackupObjectNaming.ObjectName(item.Name)).Contains(item.Name);
                StoredArchiveObject temporaryOwner;
                bool truncated = ownedTemporary && known.TryGetValue(BackupObjectNaming.ObjectName(item.Name), out temporaryOwner) && temporaryOwner.State == "damaged";
                string state = item.Directory 
                    ? "文件夹" 
                    : ownedTemporary 
                    ? truncated ? "备份未完成" : "待确认" 
                    : !registered 
                    ? "未登记" 
                    : archive.ArchiveStatus == "failed" 
                    ? archive.State == "reconcile-required" ? "待确认" : "备份未完成" 
                    : incompleteManifest 
                    ? "损坏（分卷清单不完整）" 
                    : missingCopy 
                    ? "缺失" 
                    : item.Size < 0 
                    ? "大小未知（已登记）" 
                    : item.Size != archive.Size ? "待修复（大小不符）" : archive.State == "missing" ||
                    archive.State == "size-or-name-mismatch" ? "待核对" : HistoryStatus.Text(archive.State);
                var node = new BackupHistoryNode
                {
                    Entry = item,
                    BrowseLocation = folder,
                    DestinationId = id,
                    Name = item.Name,
                    PathPrefix = item.Location,
                    ArchiveId = archive == null ? 0 : archive.ArchiveId,
                    RecordedCopyState = archive == null ? null : archive.State,
                    IsMissingArchive = missingCopy,
                    ExpectedSize = archive == null ? 0 : archive.Size,
                    State = state,
                    Details = item.Location + "\n状态：" + state + (item.Directory ? "" : "\n大小：" + (item.Size < 0 ? "未知" : Display.Bytes(item.Size))) +
                        (!item.Directory && !registered ? (ownedTemporary ? "\n已登记本次临时上传的所有权；尚未计入备份版本。" : "\n本地数据库未登记此文件。") : "")
                };
                node.WarehouseNames = group == null ? id : group.Name;
                if (archive != null)
                    node.TaskName = PropertyTaskName(workspace, archive.TaskId);
                result.Add(node);
            }

            // Put the archive summary at the warehouse root so a missing data
            // directory cannot hide it. It is not a physical or downloadable file.
            if (relative.Length == 0)
                foreach (var archive in problems.OrderBy(a => a.ArchiveName, StringComparer.CurrentCultureIgnoreCase))
                {
                    if (result.Any(node => string.Equals(node.Name, archive.ArchiveName, filesystem ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                        continue;
                    var copies = known.Values.Where(copy => copy.ArchiveId == archive.ArchiveId).ToList();
                    bool noManifest = incomplete.ContainsKey(archive.ArchiveName);
                    string health = noManifest ? "damaged" : HistoryStatus.Combined(copies.Select(copy => copy.State));
                    if (!noManifest && copies.Count > 0 && !HistoryStatus.NeedsRepair(health))
                        continue;
                    string state = noManifest ? "损坏（分卷清单不完整）" : copies.Count == 0 ? "缺失（无副本登记）" : HistoryStatus.Text(health);
                    string location = filesystem ? Path.Combine(folder, archive.ArchiveName) : RemotePath.Append(folder, archive.ArchiveName);
                    result.Add(new BackupHistoryNode {
                        Entry = new StorageEntry { Name = archive.ArchiveName, Location = location, Size = -1 },
                        BrowseLocation = folder,
                        DestinationId = id,
                        Name = archive.ArchiveName,
                        PathPrefix = location,
                        ArchiveId = archive.ArchiveId,
                        RecordedCopyState = noManifest ? "damaged" : copies.Count == 0 ? "missing" : health,
                        IsMissingArchive = true,
                        ExpectedSize = -1,
                        State = state,
                        TaskName = PropertyTaskName(workspace, archive.TaskId),
                        WarehouseNames = group == null ? id : group.Name,
                        Details = location +
                        "\n状态：" +
                        state +
                        "\n现存备份版本仍引用此包；此行为异常包汇总占位，不代表根目录中存在实际文件。"
                    });
                }

            return result;
        }

        private sealed class WarehouseProblemSnapshot
        {
            internal long Stamp;
            internal IList<ArchiveSummary> Archives;
        }

        private readonly object warehouseProblemsGate = new object();
        private readonly Dictionary<string, WarehouseProblemSnapshot> warehouseProblems = new Dictionary<string, WarehouseProblemSnapshot>();
        private IList<ArchiveSummary> CachedWarehouseProblemArchives(string id, WorkspaceDefinition workspace, StorageTopology topology)
        {
            long stamp = Catalog.DataChangeStamp;
            lock (warehouseProblemsGate)
            {
                WarehouseProblemSnapshot cached;
                if (warehouseProblems.TryGetValue(id, out cached) && cached.Stamp == stamp)
                    return cached.Archives;
            }

            var result = WarehouseProblemArchives(id, workspace, topology);
            lock (warehouseProblemsGate)
            {
                if (warehouseProblems.Count > 64)
                    warehouseProblems.Clear();
                warehouseProblems[id] = new WarehouseProblemSnapshot
                {
                    Stamp = stamp,
                    Archives = result
                };
            }

            return result;
        }

        private IList<ArchiveSummary> WarehouseProblemArchives(string id, WorkspaceDefinition workspace, StorageTopology topology)
        {
            var tasks = workspace.Tasks.Where(task => !task.Deleted).ToDictionary(task => task.Id);
            var result = new List<ArchiveSummary>();
            var nodes = Catalog.GetNodes();
            foreach (var archive in Catalog.ProblemVersionArchives(id))
            {
                if (archive.TaskId.StartsWith("__node__:", StringComparison.Ordinal))
                    archive.TaskId = nodes.Where(node => node.ArchiveId == archive.ArchiveId && tasks.ContainsKey(node.TaskId)).Select(node => node.TaskId).FirstOrDefault() ?? archive.TaskId;
                if (!tasks.ContainsKey(archive.TaskId))
                    continue;
                if (!Catalog.TaskHasNodes(archive.TaskId))
                    continue;
                var locations = Catalog.VolumePlacements(archive.ArchiveId).Select(p => p.DestinationId).ToList();
                if (locations.Count == 0)
                    locations.AddRange(Catalog.GetReplicaPools(archive.ArchiveId).SelectMany(pool => pool.DestinationIds));
                if (locations.Count == 0)
                    foreach (string target in tasks[archive.TaskId].TargetIds.Where(target => topology.Groups.Any(g => g.Id == target)))
                        locations.AddRange(topology.ResolveDestinations(target, false));
                if (locations.Contains(id))
                    result.Add(archive);
            }

            return result;
        }

        public RestoredSelection MaterializeWarehouseFiles(IList<BackupHistoryNode> nodes)
        {
            return MaterializeDownload(nodes);
        }

        private RestoredSelection MaterializeRawWarehouseLeaves(IList<Tuple<BackupHistoryNode, string>> leaves, IList<string> directories)
        {
            if (leaves.Select(item => item.Item2).Distinct(StringComparer.OrdinalIgnoreCase).Count() != leaves.Count)
                throw new IOException("选中文件的下载路径重名，请分别下载。");
            var selection = new RestoredSelection
            {
                Bandwidth = Bandwidth,
                Cancellation = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(DownloadCancellation)
            };
            var archiveIds = leaves.Select(item => item.Item1.DestinationId).Distinct().SelectMany(id => Catalog.GetWarehouseObjects(id).Select(item => new { Key = id + "\0" + item.Name, Id = item.ArchiveId }))
                .GroupBy(item => item.Key)
                .ToDictionary(group => group.Key, group => group.First()
                .Id);
            selection.Directories.AddRange(directories.Distinct(StringComparer.OrdinalIgnoreCase));
            foreach (var leaf in leaves)
            {
                var item = leaf.Item1;
                string relative = leaf.Item2;
                long archiveId;
                archiveIds.TryGetValue(item.DestinationId + "\0" + item.Name, out archiveId);
                var source = OpenWarehouseLocation(item.DestinationId, item.BrowseLocation, selection.Cancellation.Token);
                RemoteObjectInfo info;
                try
                {
                    info = item.Entry.Size >= 0 
                        ? new RemoteObjectInfo
                    {
                        Name = item.Name,
                        Exists = true,
                        Size = item.Entry.Size
                    }

                        : source.Stat(item.Name);
                }
                finally
                {
                    var disposable = source as IDisposable;
                    if (disposable != null)
                        disposable.Dispose();
                }

                if (!info.Exists || item.Entry.Size >= 0 && info.Size != item.Entry.Size)
                    throw new IOException("仓库文件已变化，请刷新后重试：" + item.Name);
                selection.Entries.Add(new RestoredFile {
                    RelativePath = relative,
                    Size = info.Size,
                    Open = token => new ProducerReadStream(
                    info.Size,
                    (output, cancel) =>
                {
                    using (archiveId > 0 ? Catalog.AcquireArchiveLease(archiveId, true) : null)
                    {
                        IManagedBackupDestination adapter = null;
                        try
                        {
                            adapter = OpenWarehouseLocation(item.DestinationId, item.BrowseLocation, cancel);
                            long total = 0;
                            RetryAfterBandwidthPause(
                                () =>
                            {
                                var actual = adapter.Stat(item.Name);
                                if (!actual.Exists || actual.Size != info.Size)
                                    throw new IOException("仓库文件已变化，请刷新后重试。");
                                DownloadIO.CopyTo(
                                    adapter,
                                    item.Name,
                                    info.Size,
                                    (bytes, offset, count) =>
                                {
                                    cancel.ThrowIfCancellationRequested();
                                    Bandwidth.WaitForResume(cancel);
                                    output.Write(bytes, offset, count);
                                    total += count;
                                },
                                    Transfers,
                                    TransferMemory,
                                    cancel,
                                    total
                                );
                                if (total != info.Size)
                                    throw new IOException("仓库文件下载不完整。");
                                return true;
                            },
                                cancel
                            );
                        }
                        finally
                        {
                            var disposable = adapter as IDisposable;
                            if (disposable != null)
                                disposable.Dispose();
                        }
                    }
                },
                    token
                )
                });
            }

            return selection;
        }
    }
}
