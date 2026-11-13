using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;

namespace Backup.Desktop
{
    // Files already have an index in the workspace. Keep only directory paths
    // here, and track late empty-folder additions without rescanning the tree.
    internal sealed class SourceTreePathIndex
    {
        private readonly ObservableCollection<FileTreeNode> roots;
        private readonly IDictionary<string, List<FileTreeNode>> files;
        private readonly Dictionary<string, List<FileTreeNode>> directories = new Dictionary<string, List<FileTreeNode>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ObservableCollection<FileTreeNode>, List<FileTreeNode>> watched = new Dictionary<ObservableCollection<FileTreeNode>, List<FileTreeNode>>();
        internal SourceTreePathIndex(ObservableCollection<FileTreeNode> nodes, IDictionary<string, List<FileTreeNode>> fileIndex)
        {
            roots = nodes;
            files = fileIndex;
            roots.CollectionChanged += RootsChanged;
            Rebuild();
        }

        internal bool Owns(ObservableCollection<FileTreeNode> value)
        {
            return ReferenceEquals(roots, value);
        }

        private void Rebuild()
        {
            foreach (var collection in watched.Keys)
                collection.CollectionChanged -= ChildrenChanged;
            watched.Clear();
            directories.Clear();
            files.Clear();
            foreach (var root in roots)
                Attach(root);
        }

        private void Attach(FileTreeNode node)
        {
            if (!node.Directory)
            {
                List<FileTreeNode> matches;
                if (!files.TryGetValue(node.FullPath, out matches))
                {
                    matches = new List<FileTreeNode>();
                    files.Add(node.FullPath, matches);
                }

                if (!matches.Contains(node))
                    matches.Add(node);
                return;
            }

            List<FileTreeNode> values;
            if (!directories.TryGetValue(node.FullPath, out values))
            {
                values = new List<FileTreeNode>();
                directories.Add(node.FullPath, values);
            }

            values.Add(node);
            if (watched.ContainsKey(node.Children))
                return;
            var children = node.Children.ToList();
            watched.Add(node.Children, children);
            node.Children.CollectionChanged += ChildrenChanged;
            foreach (var child in children)
                Attach(child);
        }

        private void Detach(FileTreeNode node)
        {
            if (!node.Directory)
            {
                List<FileTreeNode> matches;
                if (files.TryGetValue(node.FullPath, out matches))
                {
                    matches.Remove(node);
                    if (matches.Count == 0)
                        files.Remove(node.FullPath);
                }

                return;
            }

            List<FileTreeNode> values;
            if (directories.TryGetValue(node.FullPath, out values))
            {
                values.Remove(node);
                if (values.Count == 0)
                    directories.Remove(node.FullPath);
            }

            List<FileTreeNode> children;
            if (watched.TryGetValue(node.Children, out children))
            {
                watched.Remove(node.Children);
                node.Children.CollectionChanged -= ChildrenChanged;
                foreach (var child in children)
                    Detach(child);
            }
        }

        private void RootsChanged(object sender, NotifyCollectionChangedEventArgs args)
        {
            Changed(args);
        }

        private void ChildrenChanged(object sender, NotifyCollectionChangedEventArgs args)
        {
            var collection = (ObservableCollection<FileTreeNode>)sender;
            List<FileTreeNode> children;
            if (!watched.TryGetValue(collection, out children))
                return;
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                // A folder reset only invalidates that subtree. Keep every
                // unrelated path and workspace index entry intact.
                foreach (var child in children)
                    Detach(child);
                children.Clear();
                foreach (var child in collection)
                {
                    Attach(child);
                    children.Add(child);
                }
                return;
            }
            if (args.Action == NotifyCollectionChangedAction.Move)
                return;
            Changed(args);
            if (args.OldItems != null)
                foreach (FileTreeNode child in args.OldItems)
                    children.Remove(child);
            if (args.NewItems != null)
                foreach (FileTreeNode child in args.NewItems)
                    children.Add(child);
        }

        private void Changed(NotifyCollectionChangedEventArgs args)
        {
            if (args.Action == NotifyCollectionChangedAction.Move)
                return;
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                Rebuild();
                return;
            }

            if (args.OldItems != null)
                foreach (FileTreeNode node in args.OldItems)
                    Detach(node);
            if (args.NewItems != null)
                foreach (FileTreeNode node in args.NewItems)
                    Attach(node);
        }

        private int[] Position(FileTreeNode node)
        {
            var path = new List<int>();
            for (var current = node; current.Parent != null; current = current.Parent)
            {
                int index = current.Parent.Children.IndexOf(current);
                if (index < 0)
                    return null;
                path.Add(index);
            }

            var root = node;
            while (root.Parent != null)
                root = root.Parent;
            int first = roots.IndexOf(root);
            if (first < 0)
                return null;
            path.Add(first);
            path.Reverse();
            return path.ToArray();
        }

        private static bool Earlier(int[] left, int[] right)
        {
            if (right == null)
                return true;
            for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
            {
                if (left[i] != right[i])
                    return left[i] < right[i];
            }

            return left.Length < right.Length;
        }

        internal FileTreeNode Find(string path)
        {
            if (path == null)
                return null;
            List<FileTreeNode> folders, entries;
            directories.TryGetValue(path, out folders);
            files.TryGetValue(path, out entries);
            if (folders == null && entries != null && entries.Count == 1)
                return entries[0];
            if (entries == null && folders != null && folders.Count == 1)
                return folders[0];
            FileTreeNode selected = null;
            int[] selectedPosition = null;
            foreach (var node in (folders ?? Enumerable.Empty<FileTreeNode>()).Concat(entries ?? Enumerable.Empty<FileTreeNode>()))
            {
                var position = Position(node);
                if (position != null && Earlier(position, selectedPosition))
                {
                    selected = node;
                    selectedPosition = position;
                }
            }

            return selected;
        }
    }
}
