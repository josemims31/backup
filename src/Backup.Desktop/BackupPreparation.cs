using System;
using System.Collections.Generic;
using System.Linq;
using Backup.Core;
namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        // Prepare only the selected task. Repairs are a separate explicit operation.
        public IList<string> PrepareTaskBackup(WorkspaceDefinition workspace,BackupTaskDefinition task,IList<SourceDefinition> sources,IList<SourceItem> indexed,Action<string> report=null,Action<CleanupProgress> cleanup=null)
        {
            if(task==null||task.Deleted||task.RecoveryPendingConfirmation)throw new InvalidOperationException("任务不存在或导入后尚未确认。");
            var active=workspace.Tasks.SingleOrDefault(t=>t.Id==task.Id&&!t.Deleted);if(active==null||active.RecoveryPendingConfirmation)throw new InvalidOperationException("任务不存在或导入后尚未确认。");
            ResolveRetiredTargets(workspace,Catalog.LoadTopology(),Catalog.GetUsage(),new HashSet<string>{task.Id});Catalog.SetSetting("workspace_v3",JsonSettings.Encode(workspace));
            task.TargetIds=active.TargetIds.ToList();task.PendingTargetReplacement=active.PendingTargetReplacement;task.Copies=active.Copies;
            if(ActiveBackup!=null)ActiveBackup.Checkpoint();
            // Defer deletion-candidate construction until source changes are known; probes run after the
            // requested data batches, before catalog publication and old-object cleanup.
            PrepareMissingSourceChecks(task,sources,indexed);
            return new List<string>();
        }
    }
}
