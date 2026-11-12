using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Backup.Core;
namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
#if UI_TEST
        public Action<string> TestBackupPublicationPoint;
#endif
        private void BackupPublicationPoint(string name)
        {
#if UI_TEST
            if(TestBackupPublicationPoint!=null)TestBackupPublicationPoint(name);
#endif
            if(ActiveBackup!=null)ActiveBackup.Checkpoint();
            if(StopRequested)throw new OperationCanceledException("已请求停止，未开始后续数据库同步或清理。");
        }
        // An unchanged task has no publication, rotation or remote catalog work.
        public void PublishTaskBackup(BackupTaskDefinition task,DesktopBackupResult result,Action<string> report=null,Action<CleanupProgress> cleanup=null)
        {
            if(result.NoChanges)return;
            if(result.Stopped||result.PendingConfirmations>0||result.Archives.Any(a=>a.Archive.CurrentValidCopies<a.Archive.TargetCopies))throw new IOException("本次备份尚未完整完成，未生成新备份版本，已有备份版本继续保留。");
            bool owns=ActiveBackup==null;if(owns)BeginBackup();var targets=DatabaseBackupTargets();result.CatalogBackupDisabled=targets.Count==0;
            var touched=new HashSet<long>(result.Archives.Select(a=>a.Archive.ArchiveId));
            try
            {
                BackupPublicationPoint("before-snapshot");
                var files=Catalog.CaptureNode(task.Id,Guid.NewGuid().ToString("N"),DateTime.UtcNow);
                result.Node=UploadNode(task,files,report);touched.Add(result.Node.ArchiveId);
                Catalog.SetWorkingNodeBasis(task.Id,result.Node.Id+":"+result.Node.Revision);
                Catalog.ForgetLocalBackupNode(task.Id);
                Catalog.ReleasePublishedRetirements(task.Id);
                // Publish once after the new version and rotation are committed.
                DateTime recycleAt=DateTime.UtcNow;
                Catalog.ReleaseCompactionRetirements(task.Id,Catalog.PendingCompactionRetirements(task.Id));
                BackupPublicationPoint("before-delete");
                var errors=RecycleTaskNodes(task,recycleAt,report,false);
                if(errors.Count>0){result.CatalogBackupError="部分备份版本轮换待重试："+string.Join("；",errors);if(report!=null)report(result.CatalogBackupError);}
                LastCleanup=null;
                var pending=PendingRepairs();TrimRepairQueue(pending,new HashSet<string>{task.Id});SaveRepairQueue(pending);
                BackupPublicationPoint("before-final-snapshot");
                var final=BackupDatabase(task,report);if(final!=null)result.CatalogArchive=final;
                if(report!=null)report("备份版本已保存："+result.Node+"。数据包空间将在修复完整性时回收。");
            }
            catch{Catalog.RequireCatalogRetry(targets);throw;}
            finally{try{RefreshBackupResultState(result);RefreshManagedArchiveInventory(touched);}finally{if(owns)EndBackup();}}
        }
    }
}
