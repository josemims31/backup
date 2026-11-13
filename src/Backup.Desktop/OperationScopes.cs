using System;
using System.Collections.Generic;
using System.Linq;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        public ISet<long> TaskOperationArchives(string taskId)
        {
            return new HashSet<long>(Catalog.ListArchives(true).Where(a => a.TaskId == taskId).Select(a => a.ArchiveId));
        }

        public ISet<long> SourceOperationArchives(WorkspaceDefinition workspace, string sourceId)
        {
            var source = workspace.Sources.Single(s => s.Id == sourceId && !s.Deleted);
            var ids = new HashSet<long>();
            foreach (var task in workspace.Tasks.Where(t => !t.Deleted && t.SourceIds.Contains(sourceId)))
            {
                var retained = workspace.Sources.Where(s => !s.Deleted && s.Id != sourceId && task.SourceIds.Contains(s.Id)).ToList();
                foreach (var file in Catalog.GetTaskArchiveFiles(task.Id).Where(f => Under(SourcePath(f), source.Location) && !retained.Any(s => Under(SourcePath(f), s.Location))))
                    ids.Add(file.ArchiveId);
            }

            return ids;
        }

        public CleanupResult CollectGarbageForArchives(ISet<long> ids, Action<CleanupProgress> progress = null)
        {
            if (ids == null)
                throw new ArgumentNullException("ids");
            try
            {
                LastCleanup = new ArchiveMaintenance(Catalog, Adapters())
                {
                    Control = ActiveBackup
                }.CollectGarbageDetailed(progress, null, null, ids);
            }
            finally
            {
                RefreshManagedArchiveInventory(ids);
            }

            FinishOperationHistory(ids);
            return LastCleanup;
        }

        private void FinishOperationHistory(ISet<long> ids = null, string taskId = null)
        {
            var pending = PendingRepairs();
            TrimRepairQueue(pending, taskId == null ? null : new HashSet<string> { taskId }, ids);
            SaveRepairQueue(pending);
            Catalog.PruneCleanedHistory(ids, new HashSet<long>(pending.Select(item => item.ArchiveId)), taskId);
        }

        private ISet<string> WarehouseDestinations(string groupId)
        {
            return groupId == null ? null : new HashSet<string>(Catalog.LoadTopology().ResolveDestinations(groupId));
        }

        public CleanupResult DeleteDetachedWarehouseObjects(string groupId, Action<CleanupProgress> progress = null)
        {
            return DeleteDetachedObjects(progress, null, groupId);
        }
    }
}
