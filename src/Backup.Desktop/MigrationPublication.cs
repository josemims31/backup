using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backup.Core;
namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        private void PublishMigrationCatalogs(ISet<long> archiveIds,string destinationId,Action<string> report,bool synchronize=true)
        {
            var workspace=WorkspaceMigration.Load(Catalog);var taskIds=new HashSet<string>(Catalog.ListArchives(true).Where(a=>archiveIds.Contains(a.ArchiveId)&&a.TaskId!="__catalog__").Select(a=>a.TaskId));
            if(workspace.Tasks.Any(task=>taskIds.Contains(task.Id)&&task.PendingTargetReplacement))throw new IOException("迁移后目标仓库不足，旧副本保留。");
            if(!synchronize)return;
            if(report!=null)report("正在同步迁移后的数据库记录…");BackupDatabase(null,report);
        }
    }
}
