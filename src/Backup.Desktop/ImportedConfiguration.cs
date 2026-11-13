using System;
using System.Collections.Generic;
using System.Linq;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        private static void NormalizeImportedConfiguration(BackupCatalog catalog)
        {
            foreach (var item in catalog.ImportConfiguration())
            {
                string value = null;
                switch (item.Key)
                {
                    case "bandwidth_schedule_v1":
                        value = JsonSettings.Encode(JsonSettings.Decode(item.Value, new BandwidthSchedule()).ValidatedCopy());
                        break;
                    case "workspace_v3":
                        value = JsonSettings.Encode(JsonSettings.Decode(item.Value, new WorkspaceDefinition()));
                        break;
                    case "backup_schedules_v1":
                        value = JsonSettings.Encode(JsonSettings.Decode(item.Value, new List<BackupSchedule>()));
                        break;
                    case "confirmed_repairs_v1":
                        value = JsonSettings.Encode(JsonSettings.Decode(item.Value, new List<ConfirmedRepair>()));
                        break;
                    case "catalog_backup_targets_v1":
                        var targets = JsonSettings.Decode(item.Value, new List<string>()).Distinct(StringComparer.Ordinal).ToList();
                        if (targets.Count == 0)
                        {
                            catalog.RemoveSetting(item.Key);
                            continue;
                        }

                        value = JsonSettings.Encode(targets);
                        break;
                    case "exclusions":
                        var rules = BackupExclusions.NormalizeRules(JsonSettings.Decode(item.Value, new List<ExclusionRule>()));
                        new BackupExclusions(rules);
                        value = JsonSettings.Encode(rules);
                        break;
                }

                if (item.Key.StartsWith("connection_reference:", StringComparison.Ordinal))
                    value = EncodeConnectionReference(JsonSettings.Decode(item.Value, new RemoteConnectionOptions()));
                if (item.Key.StartsWith("archive_root_labels:", StringComparison.Ordinal))
                    value = JsonSettings.Encode(JsonSettings.Decode(item.Value, new Dictionary<string, string>()));
                if (item.Key.StartsWith("archive_number:", StringComparison.Ordinal))
                {
                    string stamp = item.Key.Substring("archive_number:".Length);
                    if (stamp.Length != 12 || stamp.Any(c => c < '0' || c > '9') || item.Value.Any(c => c < '0' || c > '9'))
                    {
                        catalog.RemoveSetting(item.Key);
                        continue;
                    }
                }

                if (value != null && value != item.Value)
                    catalog.SetSetting(item.Key, value);
            }

            catalog.NormalizeImportedWarehouseJson(json => JsonSettings.Encode(JsonSettings.Decode(json, new DestinationDefinition())));
            catalog.DetachImportedCatalogTasks();
        }
    }
}
