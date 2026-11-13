using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backup.Core;

namespace Backup.Desktop
{
    // Catalog paths remain stable for incremental comparisons. Only archive entries and
    // restored paths use the selected source folders as their top-level directories.
    internal sealed class ArchiveRootLayout
    {
        private readonly List<KeyValuePair<string, string>> roots = new List<KeyValuePair<string, string>>();
        internal readonly Dictionary<string, string> Labels;
        internal ArchiveRootLayout(IEnumerable<string> locations, IDictionary<string, string> labels = null)
        {
            Labels = labels == null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(labels, StringComparer.OrdinalIgnoreCase);
            var paths = locations.Select(path => Path.GetFullPath(path).TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
            var used = new HashSet<string>(Labels.Values, StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                string leaf = Path.GetFileName(path);
                if (string.IsNullOrEmpty(leaf) || leaf.EndsWith(":"))
                    leaf = path.Substring(0, 1).ToUpperInvariant();
                string name;
                if (!Labels.TryGetValue(path, out name))
                {
                    name = leaf;
                    int number = 1;
                    while (!used.Add(name))
                        name = leaf + "-" + (number++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Labels[path] = name;
                }

                roots.Add(new KeyValuePair<string, string>(path, name));
            }

            roots = roots.OrderByDescending(pair => pair.Key.Length).ToList();
        }

        internal string RootFor(string path)
        {
            return roots.Where(pair => path.Equals(pair.Key, StringComparison.OrdinalIgnoreCase) || path.StartsWith(pair.Key + "\\", StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key)
                .FirstOrDefault();
        }

        internal string Relative(string path, string fallback)
        {
            var pair = roots.FirstOrDefault(value => path.StartsWith(value.Key + "\\", StringComparison.OrdinalIgnoreCase));
            return pair.Key == null ? fallback : pair.Value + path.Substring(pair.Key.Length);
        }

        internal static ArchiveRootLayout ForTask(WorkspaceDefinition workspace, BackupTaskDefinition task, IDictionary<string, string> labels = null)
        {
            return new ArchiveRootLayout(task == null ? new string[0] : workspace.Sources.Where(source => task.SourceIds.Contains(source.Id)).Select(source => source.Location), labels);
        }
    }

    internal sealed class RestorePathLayout
    {
        private readonly Dictionary<string, ArchiveRootLayout> tasks = new Dictionary<string, ArchiveRootLayout>();
        internal RestorePathLayout(WorkspaceDefinition workspace, Func<string, IDictionary<string, string>> rootLabels = null)
        {
            if (workspace != null)
                foreach (var task in workspace.Tasks)
                    tasks[task.Id] = ArchiveRootLayout.ForTask(workspace, task, rootLabels == null ? null : rootLabels(task.Id));
        }

        internal string Relative(FileHistoryRow file)
        {
            if (file.TaskId == "__catalog__")
                return "catalog.db";
            if (!string.IsNullOrEmpty(file.EntryPath))
                return file.EntryPath;
            string fallback = LegacyRelative(file.Path);
            ArchiveRootLayout layout;
            return tasks.TryGetValue(file.TaskId ?? "", out layout) ? layout.Relative(Original(file), fallback) : fallback;
        }

        internal string FolderRelative(FileHistoryRow file, string prefix)
        {
            if (string.IsNullOrEmpty(prefix) || file.TaskId == "__catalog__")
                return Relative(file);
            string trimmed = prefix.TrimEnd('\\');
            var parts = trimmed.Split('\\');
            if (parts[0] == "UNC" && parts.Length <= 2 || parts.Length == 1)
                return Relative(file);
            ArchiveRootLayout layout;
            string root = tasks.TryGetValue(file.TaskId ?? "", out layout) ? layout.RootFor(Original(file)) : null;
            if (root != null && Display.ArchivePath(root + "\\").TrimEnd('\\').Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                return Relative(file);
            string parent = trimmed.Substring(0, trimmed.LastIndexOf('\\') + 1);
            return file.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? file.Path.Substring(parent.Length) : Relative(file);
        }

        internal static string Original(FileHistoryRow file)
        {
            if (!string.IsNullOrEmpty(file.SourcePath))
                return file.SourcePath;
            if (file.Path.StartsWith("UNC\\", StringComparison.OrdinalIgnoreCase))
                return @"\\" + file.Path.Substring(4);
            if (file.Path.Length > 2 && file.Path[1] == '\\')
                return file.Path.Substring(0, 1) + ":\\" + file.Path.Substring(2);
            return file.Path;
        }

        private static string LegacyRelative(string path)
        {
            var parts = path.Split('\\');
            return parts.Length > 3 && parts[0] == "UNC" ? string.Join("\\", parts.Skip(2)) : parts.Length > 1 && parts[0].Length == 1 ? string.Join("\\", parts.Skip(1)) : path;
        }

        internal static Func<string, string> Align(IEnumerable<string> relatives, string destination)
        {
            string server;
            if (RestoreDestination.TryServerRoot(destination, out server))
                return path => path;
            string folder = Path.GetFileName(Path.GetFullPath(destination).TrimEnd('\\'));
            var paths = relatives.ToList();
            bool trim = folder.Length > 0 && paths.Any(path => path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase)) &&
                paths.All(path => path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(folder +
                "\\", StringComparison.OrdinalIgnoreCase));
            return path => !trim ? path : path.Equals(folder, StringComparison.OrdinalIgnoreCase) ? "" : path.Substring(folder.Length + 1);
        }

        internal static IList<RestoreRequest> AlignRequests(IList<RestoreRequest> requests, string destination)
        {
            string server;
            if (RestoreDestination.TryServerRoot(destination, out server))
                return requests.Select(request => new RestoreRequest {
                    File = request.File,
                    RelativePath = RestoreDestination.ServerRelative(request),
                    RequiredDestinationId = request.RequiredDestinationId,
                    RequiredGroupId = request.RequiredGroupId
                })
                    .ToList();
            var relative = Align(requests.Select(request => request.RelativePath), destination);
            return requests.Select(request => new RestoreRequest {
                File = request.File,
                RelativePath = relative(request.RelativePath),
                RequiredDestinationId = request.RequiredDestinationId,
                RequiredGroupId = request.RequiredGroupId
            })
                .ToList();
        }
    }

    public sealed partial class DesktopBackend
    {
        internal Func<FileHistoryRow, string> HistoryEntryPaths(WorkspaceDefinition workspace)
        {
            RestorePathLayout legacy = null;
            return row =>
            {
                if (!string.IsNullOrEmpty(row.EntryPath))
                    return row.EntryPath;
                if (legacy == null)
                    legacy = new RestorePathLayout(workspace, ReadArchiveRootLabels);
                return legacy.Relative(row);
            };
        }

        internal IDictionary<string, string> ReadArchiveRootLabels(string taskId)
        {
            return JsonSettings.Decode(Catalog.GetSetting("archive_root_labels:" + taskId), new Dictionary<string, string>());
        }

        private ArchiveRootLayout BackupRootLayout(BackupTaskDefinition task)
        {
            var labels = ReadArchiveRootLabels(task.Id);
            var layout = ArchiveRootLayout.ForTask(WorkspaceMigration.Load(Catalog), task, labels);
            if (layout.Labels.Count != labels.Count)
                Catalog.SetSetting("archive_root_labels:" + task.Id, JsonSettings.Encode(layout.Labels));
            return layout;
        }
    }
}
