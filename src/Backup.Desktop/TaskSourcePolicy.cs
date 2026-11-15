using System;
using System.Collections.Generic;
using System.Linq;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        private sealed class TaskSourcePolicy
        {
            internal IList<string> Groups;
            internal StorageTopology Topology;
            internal int Required;
            internal bool Supplemental;
            internal bool HasAlternateArchives;
            internal readonly Dictionary<long, bool> Coverage = new Dictionary<long, bool>();
            internal string Key;
        }

        private static ISet<string> RecordedFileRoots(IEnumerable<long> archiveIds, IDictionary<long, ISet<string>> recorded)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (long id in archiveIds)
            {
                ISet<string> roots;
                if (recorded.TryGetValue(id, out roots))
                    result.UnionWith(roots);
            }
            return result;
        }

        private Func<SourceItem, TaskSourcePolicy> TaskSourcePolicies(BackupTaskDefinition task, NodeFileDatabase previous)
        {
            var files = previous == null ? new Dictionary<string, NodeFile>(StringComparer.OrdinalIgnoreCase) : previous.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            var recorded = Catalog.RecordedArchivePolicyRoots(task.Id);
            var topology = Catalog.LoadTopology();
            var usage = Catalog.GetUsage();
            var policies = new Dictionary<string, TaskSourcePolicy>(StringComparer.Ordinal);
            var selected = new Dictionary<string, Tuple<long, long, TaskSourcePolicy>>(StringComparer.Ordinal);
            return source =>
            {
                Tuple<long, long, TaskSourcePolicy> cached;
                if (selected.TryGetValue(source.ArchivePath, out cached) && cached.Item1 == source.Size && cached.Item2 == source.Modified)
                {
                    if (source.PriorBackupCopies == null && cached.Item3 != null && cached.Item3.Supplemental)
                        source.PriorBackupCopies = files[source.ArchivePath].Copies.ToList();
                    return cached.Item3;
                }
                NodeFile file;
                bool unchanged = files.TryGetValue(source.ArchivePath, out file) && file.Path == source.ArchivePath && file.Size == source.Size && file.Modified == source.Modified;
                var missing = unchanged ? task.TargetIds.Except(RecordedFileRoots(file.Copies.Select(copy => copy.ArchiveId), recorded), StringComparer.Ordinal).ToList() : new List<string>();
                bool supplemental = missing.Count > 0;
                var groups = supplemental ? missing : task.TargetIds;
                if (supplemental)
                    source.PriorBackupCopies = file.Copies.ToList();
                bool hasAlternatives = unchanged && file.Replicas != null && file.Replicas.Count > 0;
                string batchKey = supplemental ? string.Join("\n", groups.OrderBy(id => id, StringComparer.Ordinal)) : "";
                string key = hasAlternatives ? "alternates\n" + batchKey : batchKey;
                TaskSourcePolicy policy;
                if (!policies.TryGetValue(key, out policy))
                {
                    var scoped = topology.Scope(groups);
                    policy = new TaskSourcePolicy
                    {
                        Key = batchKey,
                        Groups = groups,
                        Topology = scoped,
                        Required = GroupAllocator.Plan(scoped, usage, 1, groups.Count, null).TargetCopies,
                        Supplemental = supplemental,
                        HasAlternateArchives = hasAlternatives
                    };
                    policies.Add(key, policy);
                }
                selected[source.ArchivePath] = Tuple.Create(source.Size, source.Modified, policy);
                return policy;
            };
        }

        private bool NeedsBackup(SourceItem item, bool forceBackup, IDictionary<string, FileBackupView> prior, StorageTopology topology,
            int target, IDictionary<long, bool> coverage, Func<SourceItem, TaskSourcePolicy> sourcePolicy, out bool attention)
        {
            if (sourcePolicy == null)
                return NeedsBackup(item, forceBackup, prior, topology, target, coverage, out attention);
            var policy = sourcePolicy(item);
            if (policy.Supplemental)
            {
                attention = false;
                return true;
            }
            FileBackupView previous;
            if (policy.HasAlternateArchives && prior.TryGetValue(item.ArchivePath, out previous) && previous.ValidCopies > 0)
            {
                attention = previous.Status != "complete" || previous.ValidCopies < previous.TargetCopies;
                return false;
            }
            return NeedsBackup(item, forceBackup, prior, policy.Topology, policy.Required, policy.Coverage, out attention);
        }
    }
}
