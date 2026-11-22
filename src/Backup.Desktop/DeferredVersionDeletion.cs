using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        public void DeleteBackupNodeLocally(string id)
        {
            Catalog.DeleteVersionLocally(id);
        }

        private ISet<long> ReadDeferredVersionArchives(DeferredVersionDeletion deletion, IEnumerable<BackupNode> revisions, Action<string> report)
        {
            if (deletion.DataArchives != null)
                return deletion.DataArchives;

            var node = revisions.FirstOrDefault(revision => revision.ArchiveId == deletion.NodeArchiveId);
            if (node == null)
                throw new IOException("无法核实已删除版本 " + deletion.NodeId + " 的原数据包引用，已保留仓库文件。");
            var ids = new HashSet<long>(ReadMaintenanceNodeDatabase(node, report: report, deferredDeletion: true).ArchiveIds);
            Catalog.RecordDeferredVersionArchives(deletion.NodeId, ids);
            return ids;
        }

        private ISet<string> RepairDeferredVersionDeletions(ISet<string> taskIds, Action<string> report, IList<string> errors)
        {
            var pending = Catalog.DeferredVersionDeletions().Where(item => taskIds == null || taskIds.Contains(item.TaskId)).ToList();
            var ready = new HashSet<string>(pending.Where(item => item.CleanupComplete).Select(item => item.TaskId), StringComparer.Ordinal);
            var cleanup = pending.Where(item => !item.CleanupComplete).GroupBy(item => item.TaskId).ToList();
            if (cleanup.Count == 0)
                return ready;

            var revisions = Catalog.NodeRevisions().ToLookup(node => node.Id, StringComparer.Ordinal);
            var stages = new HashSet<long>(Catalog.PendingUploadStages().Select(stage => stage.ArchiveId));
            var maintenance = new ArchiveMaintenance(Catalog, Adapters()) { Control = ActiveBackup, Report = report };
            foreach (var taskDeletions in cleanup)
            {
                OperationCancellation.ThrowIfCancellationRequested();
                string task = taskDeletions.Key;
                int before = errors.Count;
                try
                {
                    long limit = taskDeletions.Max(item => item.ArchiveLimit);
                    var candidates = new HashSet<long>(Catalog.ArchiveIdsForTask(task).Where(id => id <= limit));
                    var referenced = new HashSet<long>();
                    if (candidates.Count > 0)
                    {
                        Catalog.RefreshLocalBackupNode(task);
                        if (Catalog.TaskHasNodes(task))
                        {
                            var removed = new HashSet<long>();
                            foreach (var deletion in taskDeletions)
                            {
                                OperationCancellation.ThrowIfCancellationRequested();
                                removed.UnionWith(ReadDeferredVersionArchives(deletion, revisions[deletion.NodeId], report).Where(id => id <= deletion.ArchiveLimit));
                            }
                            candidates.IntersectWith(removed);
                            if (candidates.Count > 0)
                            {
                                if (!InspectNodeReferences(new HashSet<string> { task }, errors, report))
                                    continue;
                                referenced.UnionWith(inspectedNodeArchives);
                                candidates.ExceptWith(referenced);
                            }
                        }
                        else
                            foreach (var deletion in taskDeletions.Where(item => item.DataArchives == null))
                                Catalog.RecordDeferredVersionArchives(deletion.NodeId, candidates.Where(id => id <= deletion.ArchiveLimit));
                    }
                    else
                        foreach (var deletion in taskDeletions.Where(item => item.DataArchives == null))
                            Catalog.RecordDeferredVersionArchives(deletion.NodeId, candidates);

                    foreach (var deletion in taskDeletions)
                    {
                        var metadata = revisions[deletion.NodeId].ToList();
                        if (metadata.Any(node => stages.Contains(node.ArchiveId) || Catalog.HasOutstandingTransfer(node.ArchiveId)))
                        {
                            errors.Add("版本 " + deletion.NodeId + "：仍有上传待核对，已保留版本文件清理记录。");
                            continue;
                        }
                        var failures = DeleteNodeMetadata(metadata, report, retainFailures: true);
                        foreach (string error in failures)
                            errors.Add(error);
                        if (failures.Count == 0 && deletion.NodeArchiveId > 0)
                            File.Delete(NodePath(new BackupNode { Id = deletion.NodeId, TaskId = task }));
                    }

                    foreach (long id in candidates.Where(id => stages.Contains(id) || Catalog.HasOutstandingTransfer(id)).ToList())
                    {
                        errors.Add(Catalog.GetArchive(id).ArchiveName + "：仍有上传待核对，已保留数据包清理记录。");
                        candidates.Remove(id);
                    }
                    if (candidates.Count > 0)
                    {
                        Catalog.AllowNodeGarbage(referenced, candidates);
                        foreach (long id in candidates)
                        {
                            OperationCancellation.ThrowIfCancellationRequested();
                            foreach (string error in maintenance.DeleteReviewedObsoleteArchive(id))
                                errors.Add(error);
                        }
                    }
                    if (errors.Count == before)
                    {
                        Catalog.MarkDeferredVersionCleanupComplete(task);
                        ready.Add(task);
                    }
                }
                catch (Exception error)
                {
                    if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is NotSupportedException))
                        throw;
                    RuntimeDiagnostics.Record("DesktopBackend:RepairDeferredVersionDeletions", error);
                    errors.Add("已删除版本的仓库文件待清理 · " + error.Message);
                }
                finally
                {
                    Catalog.ClearNodeGarbageReview();
                }
            }
            return ready;
        }
    }
}
