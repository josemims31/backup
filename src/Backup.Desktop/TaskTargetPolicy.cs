using System;
using System.Collections.Generic;
using System.Linq;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        private sealed class TaskPolicyReferences
        {
            internal string Key;
            internal ISet<long> Archives;
        }

        private readonly Dictionary<string, TaskPolicyReferences> taskPolicyReferences = new Dictionary<string, TaskPolicyReferences>();
        private ISet<long> NodePolicyArchives(BackupNode node, NodeFileDatabase files = null)
        {
            string key = HistoricalNodeKey(node);
            TaskPolicyReferences cached;
            lock (taskPolicyReferences)
                if (taskPolicyReferences.TryGetValue(node.TaskId, out cached) && cached.Key == key)
                    return cached.Archives;
            files = files ?? ReadCachedNodeDatabase(node);
            if (files == null)
                return null;
            var ids = new HashSet<long>(files.Files.Select(file => file.ArchiveId).Concat(new[] { node.ArchiveId }));
            lock (taskPolicyReferences)
                taskPolicyReferences[node.TaskId] = new TaskPolicyReferences { Key = key, Archives = ids };
            return ids;
        }

        private bool TaskTargetsRecorded(BackupTaskDefinition task, BackupNode latest, NodeFileDatabase files)
        {
            if (latest == null || files == null || task.TargetIds.Count == 0 || task.PendingTargetReplacement)
                return false;
            var recorded = Catalog.RecordedArchivePolicyRoots(task.Id);
            return NodePolicyArchives(latest, files).All(id =>
            {
                ISet<string> roots;
                return recorded.TryGetValue(id, out roots) && roots.IsSupersetOf(task.TargetIds);
            });
        }

        private bool ArchiveTargetsRecorded(long archiveId, IEnumerable<string> roots)
        {
            if (roots == null)
                return true;
            var recorded = Catalog.GetReplicaPools(archiveId).Select(pool => pool.GroupId).ToList();
            if (recorded.Count == 0)
                recorded = Catalog.VolumePlacements(archiveId).Select(place => place.GroupId).ToList();
            return !roots.Except(recorded, StringComparer.Ordinal).Any();
        }

        private Dictionary<long, List<string>> LatestArchiveTargets(WorkspaceDefinition workspace, ISet<string> taskIds = null)
        {
            var result = new Dictionary<long, List<string>>();
            foreach (var task in workspace.Tasks.Where(task => !task.Deleted && !task.PendingTargetReplacement && task.TargetIds.Count > 0 &&
                (taskIds == null || taskIds.Contains(task.Id))))
            {
                var latest = Catalog.GetNodes(task.Id).FirstOrDefault();
                if (latest == null)
                    continue;
                // Checking targets must not download data or fetch a missing node
                // independently of the existing version-reference recovery step.
                var ids = NodePolicyArchives(latest) ?? new HashSet<long> { latest.ArchiveId };
                foreach (long id in ids)
                {
                    List<string> roots;
                    if (!result.TryGetValue(id, out roots))
                        result[id] = roots = new List<string>();
                    roots.AddRange(task.TargetIds.Where(root => !roots.Contains(root)));
                }
            }
            return result;
        }

        private void ReviewLatestTaskTargets(AllBackupCheckResult review)
        {
            var latestTargets = LatestArchiveTargets(WorkspaceMigration.Load(Catalog), review.TaskIds == null ? null : new HashSet<string>(review.TaskIds));
            if (latestTargets.Count == 0)
                return;
            var recorded = Catalog.RecordedArchivePolicyRoots(review.TaskIds != null && review.TaskIds.Count == 1 ? review.TaskIds[0] : null);
            var topology = Catalog.LoadTopology();
            foreach (var pair in latestTargets)
            {
                ISet<string> roots;
                // Missing ownership is handled by the existing manifest/reference
                // diagnostics; it is not evidence of a newly selected target.
                if (!recorded.TryGetValue(pair.Key, out roots) || roots.IsSupersetOf(pair.Value))
                    continue;
                var archive = Catalog.GetArchive(pair.Key);
                if (review.ObsoleteIds.Contains(pair.Key) || review.ArchiveTasks != null && !review.ArchiveTasks.Contains(archive.TaskId) ||
                    archive.Status != "complete" && archive.Status != "repair-required" && archive.Status != "redundancy-shortfall" ||
                    !Catalog.HasCompleteArchiveManifest(pair.Key) || Catalog.CoversArchivePolicy(pair.Key, topology.Scope(pair.Value), pair.Value.Count))
                    continue;
                Catalog.RecordInspectedArchiveProblem(pair.Key);
                review.Findings.RemoveAll(item => item.ArchiveId == pair.Key && item.ArchiveLevel);
                review.Findings.Add(new IntegrityFinding {
                    ArchiveId = pair.Key,
                    ArchiveLevel = true,
                    Destination = "",
                    Name = archive.ArchiveName,
                    Status = "redundancy-shortfall",
                    RepairAction = "replace",
                    RequiresManualRepairConfirmation = true
                });
            }
        }
    }
}
