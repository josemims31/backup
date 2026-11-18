using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        private sealed class InventorySnapshot
        {
            internal IDictionary<string, IList<StorageEntry>> Entries;
        }

        private sealed class MaintenanceSearchContext : IDisposable
        {
            private sealed class NodeRead
            {
                internal string Key;
                internal NodeFileDatabase Database;
                internal long Bytes;
            }

            private sealed class DirectoryRead
            {
                internal long Stamp;
                internal WarehouseDirectoryRead Snapshot;
            }

            private readonly object nodeReadGate = new object();
            private readonly Dictionary<string, NodeRead> nodeReads = new Dictionary<string, NodeRead>(StringComparer.Ordinal);
            private long nodeReadBytes;
            private readonly object readGate = new object();
            private readonly Dictionary<Tuple<string, string>, DirectoryRead> directoryReads = new Dictionary<Tuple<string, string>, DirectoryRead>();
            internal readonly RestoreSearchScope Search = new RestoreSearchScope();
            internal Func<DestinationDefinition, IDictionary<string, IList<StorageEntry>>> SearchInventory;
            private readonly ConcurrentDictionary<string, Lazy<InventorySnapshot>> inventories = new ConcurrentDictionary<string, Lazy<InventorySnapshot>>(StringComparer.Ordinal);
            internal IDictionary<string, IList<StorageEntry>> Inventory(DestinationDefinition destination, Func<IDictionary<string, IList<StorageEntry>>> read)
            {
                string key = destination.Id + "\n" + destination.Protocol + "\n" + destination.Location;
                return inventories.GetOrAdd(key, _ => new Lazy<InventorySnapshot>(() => new InventorySnapshot { Entries = read() }, LazyThreadSafetyMode.ExecutionAndPublication)).Value.Entries;
            }

            internal WarehouseDirectoryRead ReadDirectory(string id, string folder, long stamp, Func<InventoryFolderRecord> read)
            {
                var key = Tuple.Create(id, BackupCatalog.InventoryFolderKey(folder));
                lock (readGate)
                {
                    DirectoryRead previous;
                    if (directoryReads.TryGetValue(key, out previous) && previous.Stamp == stamp)
                        return previous.Snapshot;

                    var snapshot = DecodeDirectory(read(), previous == null ? null : previous.Snapshot);
                    directoryReads[key] = new DirectoryRead { Stamp = stamp, Snapshot = snapshot };
                    return snapshot;
                }
            }

            internal NodeFileDatabase ReadNode(BackupNode node, long limit, Func<NodeFileDatabase> read)
            {
                string key = HistoricalNodeKey(node);
                lock (nodeReadGate)
                {
                    NodeRead previous;
                    if (nodeReads.TryGetValue(node.Id, out previous))
                    {
                        if (previous.Key == key)
                            return previous.Database;
                        nodeReads.Remove(node.Id);
                        nodeReadBytes -= previous.Bytes;
                    }

                    if (read == null)
                        return null;
                    var files = read();
                    // Keep a bounded set of decoded metadata for this operation only.
                    if (node.DatabaseBytes > 0 && node.DatabaseBytes <= (limit - nodeReadBytes) / 3)
                    {
                        long bytes = node.DatabaseBytes * 3;
                        nodeReads[node.Id] = new NodeRead { Key = key, Database = files, Bytes = bytes };
                        nodeReadBytes += bytes;
                    }
                    return files;
                }
            }

            public void Dispose()
            {
                Search.Dispose();
                inventories.Clear();
                directoryReads.Clear();
                nodeReads.Clear();
                SearchInventory = null;
            }
        }

        private sealed class MaintenanceSearchLease : IDisposable
        {
            private Action finish;
            internal MaintenanceSearchLease(Action action)
            {
                finish = action;
            }

            public void Dispose()
            {
                var action = Interlocked.Exchange(ref finish, null);
                if (action != null)
                    action();
            }
        }

        private readonly AsyncLocal<MaintenanceSearchContext> maintenanceSearch = new AsyncLocal<MaintenanceSearchContext>();
        private IDisposable BeginMaintenanceSearch()
        {
            var previous = maintenanceSearch.Value;
            if (previous != null)
                return null;
            var current = new MaintenanceSearchContext();
            maintenanceSearch.Value = current;
            return new MaintenanceSearchLease(delegate
            {
                maintenanceSearch.Value = previous;
                current.Dispose();
            });
        }
    }
}
