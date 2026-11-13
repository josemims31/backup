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
            internal readonly RestoreSearchScope Search = new RestoreSearchScope();
            internal Func<DestinationDefinition, IDictionary<string, IList<StorageEntry>>> SearchInventory;
            private readonly ConcurrentDictionary<string, Lazy<InventorySnapshot>> inventories = new ConcurrentDictionary<string, Lazy<InventorySnapshot>>(StringComparer.Ordinal);
            internal IDictionary<string, IList<StorageEntry>> Inventory(DestinationDefinition destination, Func<IDictionary<string, IList<StorageEntry>>> read)
            {
                string key = destination.Id + "\n" + destination.Protocol + "\n" + destination.Location;
                return inventories.GetOrAdd(key, _ => new Lazy<InventorySnapshot>(() => new InventorySnapshot { Entries = read() }, LazyThreadSafetyMode.ExecutionAndPublication)).Value.Entries;
            }

            public void Dispose()
            {
                Search.Dispose();
                inventories.Clear();
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
