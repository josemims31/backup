using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Backup.Core;

namespace Backup.Desktop
{
    // Local node paths, atomic cache writes and legacy-local recovery have no
    // repository/network dependency. Registration remains owned by the catalog.
    internal sealed class LocalNodeCacheStore
    {
        private readonly BackupCatalog catalog;
        private readonly string cacheDirectory;
        private readonly Func<MemoryBudget> memory;
        private readonly Func<long> memoryLimit;
        private readonly object recoveryGate = new object();
        private List<string> movedFolders;
        internal LocalNodeCacheStore(BackupCatalog value, string cache, Func<MemoryBudget> budget, Func<long> limit)
        {
            catalog = value;
            cacheDirectory = cache;
            memory = budget;
            memoryLimit = limit;
        }

        internal bool Uses(BackupCatalog value)
        {
            return ReferenceEquals(catalog, value);
        }

        internal string PathFor(BackupNode node)
        {
            Guid value;
            if (!Guid.TryParseExact(node.Id, "N", out value))
                throw new IOException("备份版本标识无效。");
            string directory = catalog.NodeDirectory(node.TaskId);
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("备份版本目录不能是链接。");
            string path = Path.Combine(directory, node.Id + ".db");
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("备份版本数据库不能是链接。");
            return path;
        }

        internal void Store(BackupNode node, byte[] bytes)
        {
            using (var input = new MemoryStream(bytes, false))
                Store(node, input);
        }

        private void Store(BackupNode node, Stream input)
        {
            string path = PathFor(node);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string stage = path + ".new-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output, 65536);
                    output.Flush(true);
                }

                if (File.Exists(path))
                    File.Replace(stage, path, null);
                else
                    File.Move(stage, path);
            }
            finally
            {
                if (File.Exists(stage))
                    File.Delete(stage);
            }
        }

        internal NodeFileDatabase Read(BackupNode node)
        {
            string path = PathFor(node);
            if (File.Exists(path))
                try
                {
                    return catalog.ReadRegisteredNodeDatabaseFile(path, node, memory());
                }
                catch (IOException diagnosticFailure)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/LocalNodeCacheStore.cs:Read", diagnosticFailure);
                }

            return Recover(node);
        }

        private List<string> MovedFolders()
        {
            if (movedFolders != null)
                return movedFolders;
            movedFolders = new List<string>();
            string root = Path.Combine(cacheDirectory, "legacy-data");
            if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                return movedFolders;
            foreach (string folder in Directory.EnumerateDirectories(root))
                if (Regex.IsMatch(Path.GetFileName(folder), @"\Anodes(?:\.migrated-[0-9a-f]{32})*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
                    (File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0)
                    movedFolders.Add(folder);
            return movedFolders;
        }

        internal NodeFileDatabase Recover(BackupNode node)
        {
            if (node.DatabaseBytes <= 0 || node.DatabaseBytes > int.MaxValue || node.DatabaseBytes > memoryLimit() / 3)
                return null;
            lock (recoveryGate)
            {
                List<string> folders;
                try
                {
                    folders = MovedFolders();
                }
                catch (IOException)
                {
                    return null;
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }

                foreach (string folder in folders)
                {
                    try
                    {
                        if (!Directory.Exists(folder) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                            continue;
                        var source = new FileInfo(Path.Combine(folder, node.Id + ".db"));
                        if (!source.Exists || (source.Attributes & FileAttributes.ReparsePoint) != 0 || source.Length != node.DatabaseBytes)
                            continue;
                        // Keep this original frozen while validation and the optional
                        // cache copy run; no whole managed database array is needed.
                        using (var guard = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            var files = catalog.ReadRegisteredNodeDatabaseFile(source.FullName, node, memory());
                            if (files.Revision != node.Revision)
                                continue;
                            if (!catalog.GetNodes(node.TaskId).Any(value => value.Id == node.Id && value.Digest == node.Digest && value.Revision == node.Revision))
                                return null;
                            try
                            {
                                Store(node, guard);
                            }
                            catch (IOException diagnosticFailure)
                            {
                                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/LocalNodeCacheStore.cs:Recover", diagnosticFailure);
                            }
                            catch (UnauthorizedAccessException diagnosticFailure)
                            {
                                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/LocalNodeCacheStore.cs:Recover", diagnosticFailure);
                            }

                            return files;
                        }
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }
                }

                return null;
            }
        }
    }
}
