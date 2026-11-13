using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backup.Core;

namespace Backup.Desktop
{
    internal sealed class SourceViewState
    {
        internal bool Valid, TreeFocus, TargetDirectory;
        internal string Folder, SelectedTree, Target;
        internal HashSet<string> Expanded, Known;
        internal BrowserScrollState Scroll;
        internal BrowserGridState Grid;
    }

    internal static class SourceTreeNavigation
    {
        internal static IEnumerable<FileTreeNode> Nodes(FileTreeWorkspace tree)
        {
            var pending = new Stack<FileTreeNode>(tree.Roots.Reverse());
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                yield return node;
                foreach (var child in node.Children.Reverse())
                    pending.Push(child);
            }
        }

        internal static FileTreeNode Find(FileTreeWorkspace tree, string path)
        {
            return tree.FindPath(path);
        }

        internal static FileTreeNode Parent(FileTreeWorkspace tree, string path)
        {
            while (!string.IsNullOrEmpty(path))
            {
                var node = Find(tree, path);
                if (node != null && node.Directory)
                    return node;
                path = Path.GetDirectoryName(path);
            }

            return tree.Roots.FirstOrDefault();
        }

        internal static void KeepEmptyFolder(FileTreeWorkspace next, SourceViewState state, IList<LoadedSource> sources, BackupExclusions exclusions, IList<string> protectedRoots, FileSelection selection)
        {
            if (!state.Valid || state.Target == null || Find(next, state.Target) != null)
                return;
            string path = state.TargetDirectory ? state.Target : Path.GetDirectoryName(state.Target);
            while (!string.IsNullOrEmpty(path))
            {
                if (Find(next, path) != null)
                    return;
                var scope = sources.Where(source => path.Equals(source.Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(source.Root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(source => source.Root.Length)
                    .FirstOrDefault();
                if (scope == null)
                    return;
                if (exclusions.Match(path) == null &&
                    !protectedRoots.Any(root => path.Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) &&
                    Directory.Exists(path))
                {
                    string root = scope.Root.TrimEnd('\\');
                    var parent = Find(next, root);
                    if (parent == null)
                    {
                        parent = new FileTreeNode
                        {
                            Name = scope.Root,
                            FullPath = root,
                            Directory = true,
                            IsExpanded = true
                        };
                        parent.SetEmptySelection(selection.Includes(root));
                        next.Roots.Add(parent);
                    }

                    string cursor = root;
                    foreach (string part in path.Substring(root.Length).Trim('\\').Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        cursor += "\\" + part;
                        var child = parent.Children.FirstOrDefault(node => node.Directory && node.FullPath.Equals(cursor, StringComparison.OrdinalIgnoreCase));
                        if (child == null)
                        {
                            child = new FileTreeNode
                            {
                                Name = part,
                                FullPath = cursor,
                                Directory = true,
                                Parent = parent
                            };
                            child.SetEmptySelection(selection.Includes(cursor));
                            parent.Children.Add(child);
                        }

                        parent = child;
                    }

                    return;
                }

                path = Path.GetDirectoryName(path);
            }
        }
    }
}
