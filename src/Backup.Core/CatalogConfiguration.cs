using System.Collections.Generic;
using System;
using System.Linq;

namespace Backup.Core
{
    public sealed partial class BackupCatalog
    {
        private static bool SameTopology(StorageTopology left, StorageTopology right)
        {
            if (left.Destinations.Count != right.Destinations.Count || left.Groups.Count != right.Groups.Count)
                return false;
            var destinations = right.Destinations.ToDictionary(d => d.Id, StringComparer.Ordinal);
            foreach (var item in left.Destinations)
            {
                DestinationDefinition other;
                if (!destinations.TryGetValue(item.Id, out other) || item.Protocol != other.Protocol || item.Location != other.Location || item.MaxBytes != other.MaxBytes ||
                    item.Enabled != other.Enabled)
                    return false;
            }

            var groups = right.Groups.ToDictionary(g => g.Id, StringComparer.Ordinal);
            foreach (var item in left.Groups)
            {
                BackupGroupDefinition other;
                if (!groups.TryGetValue(item.Id, out other) || item.Name != other.Name || item.Notes != other.Notes || (item.Mode ?? "raid0") != (other.Mode ?? "raid0") ||
                    !item.DestinationIds.OrderBy(id => id, StringComparer.Ordinal)
                    .SequenceEqual(other.DestinationIds.OrderBy(id => id, StringComparer.Ordinal)) ||
                    !item.ChildGroupIds.OrderBy(id => id, StringComparer.Ordinal)
                    .SequenceEqual(other.ChildGroupIds.OrderBy(id => id, StringComparer.Ordinal)))
                    return false;
            }

            return true;
        }

        public void SaveConfiguration(IList<KeyValuePair<string, string>> values)
        {
            Transaction(delegate
            {
                SaveConfigurationUnsafe(values);
                return 0;
            });
        }

        private void SaveConfigurationUnsafe(IList<KeyValuePair<string, string>> values)
        {
            foreach (var item in values)
            {
                if (item.Value != null)
                    SaveSettingUnsafe(item.Key, item.Value);
                else if (GetSetting(item.Key) != null)
                {
                    database.Execute("DELETE FROM settings WHERE key=?", item.Key);
                    SettingChanged(item.Key);
                }
            }
        }
    }
}
