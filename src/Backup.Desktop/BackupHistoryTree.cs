using System;
using System.Collections.Generic;
using System.Linq;
using Backup.Core;

namespace Backup.Desktop
{
    public static class BackupHistoryTree
    {
        public static List<BackupHistoryNode> Build(IEnumerable<FileHistoryRow> history, WorkspaceDefinition workspace, StorageTopology topology, IList<DestinationUsage> usage, ISet<string> visible)
        {
            var all = history.ToList();
            var stats = usage.ToDictionary(u => u.DestinationId);
            var result = new List<BackupHistoryNode>();
            foreach (var destination in topology.Destinations.Where(d => visible == null || visible.Contains(d.Id)))
            {
                var rows = all.Where(h => h.DestinationId == destination.Id).ToList();
                if (destination.Retired && rows.Count == 0)
                    continue;
                var leaf = topology.Groups.FirstOrDefault(g => g.Id == "single:" + destination.Id);
                DestinationUsage used;
                stats.TryGetValue(destination.Id, out used);
                var root = new BackupHistoryNode
                {
                    DestinationId = destination.Id,
                    PathPrefix = "",
                    IsWarehouseRoot = true,
                    Name = "仓库：" + (leaf == null ? destination.Id : Display.TargetLabel(topology, leaf)) + (destination.Retired ? "（已删除，待迁移）" : ""),
                    Details = destination.Location + "\n全局已用 " + Display.Bytes(used == null ? 0 : used.UsedBytes) + " / 上限 " + Display.Bytes(destination.MaxBytes) + "\n" + rows.Count + " 个文件版本"
                };
                var folders = new Dictionary<string, BackupHistoryNode>(StringComparer.OrdinalIgnoreCase);
                folders[""] = root;
                foreach (var row in rows.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ThenByDescending(r => r.ArchiveId))
                {
                    string[] parts = row.TaskId == "__catalog__" ? new[]
                    {
                        "catalog.db"
                    }

                    : row.Path.Split('\\');
                    string key = "";
                    var parent = root;
                    int start = 0;
                    if (parts.Length > 2 && parts[0] == "UNC")
                    {
                        key = "UNC\\" + parts[1] + "\\";
                        BackupHistoryNode host;
                        if (!folders.TryGetValue(key, out host))
                        {
                            host = new BackupHistoryNode
                            {
                                Name = parts[1] + "(smb)",
                                PathPrefix = key,
                                DestinationId = destination.Id,
                                Details = @"\\" + parts[1]
                            };
                            folders[key] = host;
                            parent.Children.Add(host);
                        }

                        parent = host;
                        start = 2;
                    }

                    for (int i = start; i < parts.Length - 1; i++)
                    {
                        key += parts[i] + "\\";
                        BackupHistoryNode folder;
                        if (!folders.TryGetValue(key, out folder))
                        {
                            folder = new BackupHistoryNode
                            {
                                Name = parts[i],
                                PathPrefix = key,
                                DestinationId = destination.Id,
                                Details = key
                            };
                            folders.Add(key, folder);
                            parent.Children.Add(folder);
                        }

                        parent = folder;
                    }

                    var task = workspace.Tasks.FirstOrDefault(t => t.Id == row.TaskId);
                    string state = HistoryStatus.File(row, new[] { row }, row.DestinationId);
                    var group = topology.Groups.FirstOrDefault(g => g.Id == row.GroupId);
                    string taskName = row.TaskId == "__catalog__" ? "数据库" : task == null ? row.TaskId : task.Name + (task.Deleted ? "（已删除）" : "");
                    parent.Children.Add(new BackupHistoryNode {
                        File = row,
                        ArchiveId = row.ArchiveId,
                        DestinationId = row.DestinationId,
                        Name = parts.Last(),
                        PathPrefix = row.Path,
                        TaskName = taskName,
                        State = state,
                        DetailsFactory = () => row.Path +
                        "\n任务：" +
                        taskName +
                        "\n状态：" +
                        state +
                        "\n大小：" +
                        Display.Bytes(row.Size) +
                        "\n压缩包：" +
                        row.ArchiveName +
                        "\n仓库：" +
                        destination.Location +
                        "\n写入仓库：" +
                        (group == null ? row.GroupId : Display.TargetName(group))
                    });
                }

                result.Add(root);
            }

            return result;
        }

        // Archive ids are globally unique. Avoid embedded NULs: the framework's
        // legacy string hash stops at NUL and makes large same-task histories quadratic.
        private static string FileKey(FileHistoryRow row)
        {
            return row.ArchiveId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + row.Path.ToUpperInvariant();
        }

        public static List<BackupHistoryNode> BuildFolders(
            IEnumerable<FileHistoryRow> visible,
            WorkspaceDefinition workspace,
            StorageTopology topology,
            IEnumerable<FileHistoryRow> allReplicas = null,
            string destinationId = null,
            Func<FileHistoryRow, string> entryPath = null
        )
        {
            var rows = visible.ToList();
            var replicas = (allReplicas ?? rows).ToLookup(row => row.TaskId);
            var result = new List<BackupHistoryNode>();
            foreach (var task in rows.GroupBy(row => row.TaskId))
                result.AddRange(BuildTaskFolders(task, workspace, topology, replicas[task.Key], destinationId, entryPath));
            return result;
        }

        private static List<BackupHistoryNode> BuildTaskFolders(
            IEnumerable<FileHistoryRow> visible,
            WorkspaceDefinition workspace,
            StorageTopology topology,
            IEnumerable<FileHistoryRow> allReplicas,
            string destinationId,
            Func<FileHistoryRow, string> entryPath
        )
        {
            var rows = visible.Where(row => !row.GroupId.StartsWith("transfer:", StringComparison.Ordinal)).ToList();
            var available = (allReplicas ?? rows).Where(row => !row.GroupId.StartsWith("transfer:", StringComparison.Ordinal)).GroupBy(FileKey).ToDictionary(group => group.Key, group => group.OrderBy(row => row.PlacementStatus == "complete" ? 0 : 1)
                .ToList());
            var unique = rows.GroupBy(FileKey).Select(group => group.OrderBy(row => row.PlacementStatus == "complete" ? 0 : 1).First()).ToList();
            if (unique.Count == 0)
                return new List<BackupHistoryNode>();
            string taskId = unique[0].TaskId;
            var definition = workspace.Tasks.FirstOrDefault(task => task.Id == taskId);
            string rootName = taskId == "__catalog__" ? "数据库" : definition == null ? taskId : definition.Name;
            var root = new BackupHistoryNode
            {
                Name = "所属任务：" + rootName,
                TaskName = rootName,
                IsTaskRoot = true,
                NavigationScope = taskId,
                PathPrefix = "",
                Details = rootName + "\n" + unique.Count + " 个文件版本"
            };
            var folders = new Dictionary<string, BackupHistoryNode>(StringComparer.OrdinalIgnoreCase);
            folders[""] = root;
            var files = new Dictionary<BackupHistoryNode, List<FileHistoryRow>>();
            foreach (var row in unique)
            {
                string[] parts = row.TaskId == "__catalog__" 
                    ? new[]
                {
                    "catalog.db"
                }

                    : (entryPath == null ? row.Path : entryPath(row).Replace('/', '\\')).Split('\\');
                string key = "";
                var parent = root;
                int start = 0;
                if (entryPath == null && parts.Length > 2 && parts[0] == "UNC")
                {
                    key = "UNC\\" + parts[1] + "\\";
                    BackupHistoryNode host;
                    if (!folders.TryGetValue(key, out host))
                    {
                        host = new BackupHistoryNode
                        {
                            Name = parts[1] + "(smb)",
                            PathPrefix = key,
                            Details = @"\\" + parts[1]
                        };
                        folders.Add(key, host);
                        parent.Children.Add(host);
                    }

                    parent = host;
                    start = 2;
                }

                for (int index = start; index < parts.Length - 1; index++)
                {
                    key += parts[index] + "\\";
                    BackupHistoryNode folder;
                    if (!folders.TryGetValue(key, out folder))
                    {
                        string prefix = key;
                        if (entryPath != null && row.TaskId != "__catalog__")
                        {
                            var original = row.Path.Split('\\');
                            prefix = string.Join("\\", original.Take(Math.Max(0, original.Length - (parts.Length - index - 1)))) + "\\";
                        }

                        folder = new BackupHistoryNode
                        {
                            Name = parts[index],
                            PathPrefix = prefix,
                            Details = prefix
                        };
                        folders.Add(key, folder);
                        parent.Children.Add(folder);
                    }

                    parent = folder;
                }

                List<FileHistoryRow> contents;
                if (!files.TryGetValue(parent, out contents))
                {
                    contents = new List<FileHistoryRow>();
                    files.Add(parent, contents);
                }

                contents.Add(row);
            }

            var locations = new Dictionary<BackupHistoryNode, HashSet<string>>();
            var health = new Dictionary<BackupHistoryNode, HistoryStatusSummary>();
            foreach (var pair in files)
                locations[pair.Key] = new HashSet<string>(pair.Value.SelectMany(row => available[FileKey(row)]).Select(row => row.DestinationId));
            foreach (var pair in files)
                health[pair.Key] = HistoryStatus.Summarize(pair.Value.SelectMany(row => available[FileKey(row)]), destinationId);
            var order = new List<BackupHistoryNode>();
            var pending = new Stack<BackupHistoryNode>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                order.Add(node);
                foreach (var child in node.Children)
                    pending.Push(child);
            }

            foreach (var folder in order.AsEnumerable().Reverse())
            {
                HashSet<string> ids;
                if (!locations.TryGetValue(folder, out ids))
                {
                    ids = new HashSet<string>();
                    locations[folder] = ids;
                }

                HistoryStatusSummary summary;
                if (!health.TryGetValue(folder, out summary))
                {
                    summary = new HistoryStatusSummary();
                    health[folder] = summary;
                }

                foreach (var child in folder.Children)
                {
                    ids.UnionWith(locations[child]);
                    summary.Merge(health[child]);
                }

                folder.State = summary.Text;
                folder.WarehouseNames = string.Join(
                    "；",
                    ids.OrderBy(id => id).Select(id =>
                {
                    var leaf = topology.Groups.FirstOrDefault(group => group.Id == "single:" + id);
                    return leaf == null ? id : leaf.Name;
                })
                );
                folder.TaskName = rootName;
            }

            var tasks = workspace.Tasks.ToDictionary(task => task.Id);
            var destinations = topology.Destinations.ToDictionary(item => item.Id);
            var groups = topology.Groups.ToDictionary(item => item.Id);
            foreach (var folder in folders.Values)
            {
                var propertyFolder = folder;
                folder.PropertyFiles = () => unique.Where(row => string.IsNullOrEmpty(propertyFolder.PathPrefix) || row.Path.StartsWith(propertyFolder.PathPrefix, StringComparison.OrdinalIgnoreCase)).SelectMany(row => available[FileKey(row)]);
                var directories = folder.Children.OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase).ToList();
                List<FileHistoryRow> contents;
                if (!files.TryGetValue(folder, out contents))
                    contents = new List<FileHistoryRow>();
                var ordered = contents.OrderBy(row => row.Path, StringComparer.OrdinalIgnoreCase).ThenByDescending(row => row.ArchiveId).ToList();
                folder.SetLazyChildren(
                    directories,
                    () =>
                {
                    var children = new List<BackupHistoryNode>(directories);
                    foreach (var row in ordered)
                    {
                        var replicas = available[FileKey(row)];
                        BackupTaskDefinition task;
                        tasks.TryGetValue(row.TaskId, out task);
                        string taskName = row.TaskId == "__catalog__" ? "数据库" : task == null ? row.TaskId : task.Name + (task.Deleted ? "（已删除）" : "");
                        string state = HistoryStatus.File(row, replicas, destinationId);
                        var node = new BackupHistoryNode
                        {
                            File = row,
                            ArchiveId = row.ArchiveId,
                            Name = row.TaskId == "__catalog__" ? "catalog.db" : (entryPath == null ? row.Path : entryPath(row).Replace('/', '\\')).Split('\\').Last(),
                            PathPrefix = row.Path,
                            TaskName = taskName,
                            State = state,
                            Replicas = replicas,
                            WarehouseNames = string.Join(
                                "；",
                                replicas.Select(value =>
                            {
                                BackupGroupDefinition leaf;
                                groups.TryGetValue("single:" + value.DestinationId, out leaf);
                                return leaf == null ? value.DestinationId : leaf.Name;
                            })
                                .Distinct()
                            )
                        };
                        node.DetailsFactory = () => row.Path + "\n任务：" + taskName + "\n状态：" + node.State + (row.TaskId == "__catalog__" ? "\n快照时间：" + HistoryStatus.SnapshotTime(row) : "") + "\n大小：" +
                            node.SizeText +
                            "\n压缩包：" +
                            row.ArchiveName +
                            "\n关联仓库：\n" +
                            string.Join(
                            "\n",
                            replicas.GroupBy(value => value.GroupId)
                            .Select(group => WarehouseName(topology, group.Key) +
                            " → " +
                            string.Join(
                            "；",
                            group.Select(value =>
                        {
                            DestinationDefinition destination;
                            destinations.TryGetValue(value.DestinationId, out destination);
                            BackupGroupDefinition leaf;
                            groups.TryGetValue("single:" + value.DestinationId, out leaf);
                            return (leaf == null ? value.DestinationId : leaf.Name) + " · " + (destination == null ? "" : destination.Location) + " · " + HistoryStatus.Text(value.PlacementStatus);
                        })
                            .Distinct()
                        ))
                        );
                        children.Add(node);
                    }

                    return children;
                }
                );
            }

            root.ContentState = health[root].Text;
            if (!health[root].HasCurrent)
                root.State = "无当前有效文件";
            return new List<BackupHistoryNode>
            {
                root
            };
        }

        public static string WarehouseName(StorageTopology topology, string id)
        {
            var group = topology.Groups.FirstOrDefault(g => g.Id == id);
            return group == null ? id : Display.TargetName(group);
        }

        public static IList<ChoiceRow> DownloadWarehouses(IEnumerable<BackupHistoryNode> selected, StorageTopology topology, Func<long, string, bool> complete = null)
        {
            var nodes = selected.ToList();
            if (nodes.Any(node => node.IsArchiveBrowseBlocked))
                return new List<ChoiceRow>();
            var files = nodes.SelectMany(node => node.IsDirectory ? Descendants(node).Where(file => file.File != null && file.File.FileState == "current") : new[] { node }).Where(node => node.File != null)
                .ToList();
            var candidates = files.SelectMany(node => node.Replicas.Count > 0 ? (IEnumerable<FileHistoryRow>)node.Replicas : new[] { node.File }).Select(row => row.DestinationId).Distinct();
            var result = new List<ChoiceRow>();
            foreach (string id in candidates)
            {
                if (files.Count == 0 ||
                    !files.All(node => (node.Replicas.Count > 0 ? node.Replicas : new List<FileHistoryRow> { node.File }).Any(row => row.DestinationId == id &&
                    row.PlacementStatus == "complete")) ||
                    complete != null &&
                    files.Select(node => node.ArchiveId)
                    .Distinct()
                    .Any(archive => !complete(archive, id)))
                    continue;
                var destination = topology.Destinations.FirstOrDefault(value => value.Id == id);
                if (destination == null)
                    continue;
                var leaf = topology.Groups.FirstOrDefault(group => group.Id == "single:" + id);
                result.Add(new ChoiceRow { Id = "destination:" + id, Name = leaf == null ? id : leaf.Name, ToolTip = destination.Location });
            }

            return result.OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static IEnumerable<BackupHistoryNode> Descendants(BackupHistoryNode node)
        {
            foreach (var child in node.Children)
            {
                yield return child;
                foreach (var nested in Descendants(child))
                    yield return nested;
            }
        }

        private static RestoreRequest Request(BackupHistoryNode node, string relative, string warehouseId)
        {
            var row = node.File;
            bool physical = warehouseId != null && warehouseId.StartsWith("destination:", StringComparison.Ordinal);
            string id = physical ? warehouseId.Substring(12) : warehouseId;
            if (warehouseId != null)
            {
                var replicas = node.Replicas.Count > 0 ? (IEnumerable<FileHistoryRow>)node.Replicas : new[]
                {
                    node.File
                };
                row = replicas.FirstOrDefault(r => physical ? r.DestinationId == id : r.GroupId == id);
                if (row == null)
                    throw new System.IO.IOException("所选仓库不包含全部选定文件：" + node.Name);
            }

            return new RestoreRequest
            {
                File = row,
                RelativePath = relative,
                RequiredDestinationId = physical ? id : null,
                RequiredGroupId = physical ? null : warehouseId
            };
        }

        public static IList<RestoreRequest> Requests(IEnumerable<BackupHistoryNode> selected, string warehouseId = null, WorkspaceDefinition workspace = null, Func<string, IDictionary<string, string>> rootLabels = null)
        {
            var nodes = selected.ToList();
            if (nodes.Any(node => node.IsArchiveBrowseBlocked))
                throw new System.IO.IOException(nodes.First(node => node.IsArchiveBrowseBlocked).ArchiveBrowseNotice);
            var layout = new RestorePathLayout(workspace, rootLabels);
            var result = new List<RestoreRequest>();
            foreach (var node in nodes)
            {
                if (!node.IsDirectory)
                {
                    result.Add(Request(node, node.Name, warehouseId));
                    continue;
                }

                string prefix = node.PathPrefix ?? "";
                foreach (var file in Descendants(node).Where(c => !c.IsDirectory && c.File.FileState == "current" && c.File.ArchiveStatus != "failed" && c.File.ArchiveStatus != "writing").GroupBy(c => c.File.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(c => c.ArchiveId)
                    .First()))
                    result.Add(Request(file, layout.FolderRelative(file.File, prefix), warehouseId));
            }

            return result.GroupBy(r => r.RelativePath, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(r => r.File.ArchiveId).First()).ToList();
        }
    }
}
