using System;
using System.Collections.Generic;
using System.Linq;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed class TaskPolicyReferenceSnapshot
    {
        public string Key;
        public List<long[]> FileArchives = new List<long[]>();
    }

    public sealed partial class DesktopBackend
    {
        private sealed class TaskPolicyReferences
        {
            internal string Key;
            internal ISet<long> Archives;
            internal IList<long[]> FileArchives;
        }

        private readonly Dictionary<string, TaskPolicyReferences> taskPolicyReferences = new Dictionary<string, TaskPolicyReferences>();
        private TaskPolicyReferences NodePolicyReferences(BackupNode node, NodeFileDatabase files = null)
        {
            string key = HistoricalNodeKey(node);
            TaskPolicyReferences cached;
            lock (taskPolicyReferences)
                if (taskPolicyReferences.TryGetValue(node.TaskId, out cached) && cached.Key == key)
                    return cached;
            string setting = "latest_node_policy_refs_v1:" + node.TaskId;
            bool verified = files != null;
            if (files == null)
            {
                var snapshot = JsonSettings.Decode(Catalog.GetSetting(setting), new TaskPolicyReferenceSnapshot());
                if (snapshot.Key == key && snapshot.FileArchives != null && snapshot.FileArchives.All(ids => ids != null && ids.Length > 0))
                {
                    cached = new TaskPolicyReferences
                    {
                        Key = key,
                        Archives = new HashSet<long>(snapshot.FileArchives.SelectMany(ids => ids).Concat(new[] { node.ArchiveId })),
                        FileArchives = snapshot.FileArchives
                    };
                    lock (taskPolicyReferences)
                        taskPolicyReferences[node.TaskId] = cached;
                    return cached;
                }
                // Older catalogs can reuse their working-file metadata. This reads
                // neither a version database nor archive content during a check.
                files = Catalog.CaptureNode(node.TaskId, node.Id, node.CreatedUtc);
            }
            // Unchanged files in the same packages share a policy calculation.
            var primaryOnly = new HashSet<long>();
            var combinations = new Dictionary<string, long[]>(StringComparer.Ordinal);
            foreach (var file in files.Files)
            {
                if (file.Replicas == null || file.Replicas.Count == 0)
                {
                    primaryOnly.Add(file.ArchiveId);
                    continue;
                }
                var ids = new[] { file.ArchiveId }.Concat(file.Replicas.Select(copy => copy.ArchiveId).OrderBy(id => id)).ToArray();
                combinations[string.Join(",", ids)] = ids;
            }
            var fileArchives = primaryOnly.Select(id => new[] { id }).Concat(combinations.Values).ToList();
            var references = new TaskPolicyReferences
            {
                Key = key,
                Archives = new HashSet<long>(fileArchives.SelectMany(ids => ids).Concat(new[] { node.ArchiveId })),
                FileArchives = fileArchives
            };
            if (verified)
            {
                lock (taskPolicyReferences)
                    taskPolicyReferences[node.TaskId] = references;
                var latest = Catalog.GetNodes(node.TaskId).FirstOrDefault();
                if (latest != null && HistoricalNodeKey(latest) == key)
                {
                    string encoded = JsonSettings.Encode(new TaskPolicyReferenceSnapshot { Key = key, FileArchives = fileArchives });
                    if (Catalog.GetSetting(setting) != encoded)
                        Catalog.SetSetting(setting, encoded);
                }
            }
            return references;
        }

        private bool TaskTargetsRecorded(BackupTaskDefinition task, BackupNode latest, NodeFileDatabase files)
        {
            if (latest == null || files == null || task.TargetIds.Count == 0 || task.PendingTargetReplacement)
                return false;
            var recorded = Catalog.RecordedArchivePolicyRoots(task.Id);
            var references = NodePolicyReferences(latest, files);
            ISet<string> roots;
            return recorded.TryGetValue(latest.ArchiveId, out roots) && roots.IsSupersetOf(task.TargetIds) &&
                references.FileArchives.All(ids => RecordedFileRoots(ids, recorded).IsSupersetOf(task.TargetIds));
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
            var recorded = Catalog.RecordedArchivePolicyRoots();
            foreach (var task in workspace.Tasks.Where(task => !task.Deleted && !task.PendingTargetReplacement && task.TargetIds.Count > 0 &&
                (taskIds == null || taskIds.Contains(task.Id))))
            {
                var latest = Catalog.GetNodes(task.Id).FirstOrDefault();
                if (latest == null)
                    continue;
                var references = NodePolicyReferences(latest);
                var ids = references == null ? new HashSet<long> { latest.ArchiveId } : references.Archives;
                foreach (long id in ids)
                {
                    List<string> roots;
                    if (!result.TryGetValue(id, out roots))
                        result[id] = roots = new List<string>();
                    ISet<string> own;
                    if (recorded.TryGetValue(id, out own))
                        roots.AddRange(own.Where(root => !roots.Contains(root)));
                }
                var metadata = result[latest.ArchiveId];
                metadata.AddRange(task.TargetIds.Where(root => !metadata.Contains(root)));
                if (references != null)
                    foreach (var fileArchives in references.FileArchives)
                    {
                        var missing = task.TargetIds.Except(RecordedFileRoots(fileArchives, recorded), StringComparer.Ordinal);
                        var roots = result[fileArchives[0]];
                        roots.AddRange(missing.Where(root => !roots.Contains(root)));
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
