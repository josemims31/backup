using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
namespace Backup.Core
{
    public sealed partial class BackupCatalog
    {
        // The writer keeps volume 001 in memory until all source checks and native
        // archive finalization succeed. Its registration seals the expected manifest.
        public bool HasCompleteArchiveManifest(long id)
        {
            lock(gate){var archive=GetArchive(id);var objects=GetObjects(id);if(objects.Count==0||objects.Any(item=>item.Size<=0))return false;
                if(objects.Count==1&&objects[0].Name==archive.ArchiveName)return true;
                var numbers=new HashSet<int>();foreach(var item in objects){int number;if(!item.Name.StartsWith(archive.ArchiveName+".",StringComparison.Ordinal))return false;string suffix=item.Name.Substring(archive.ArchiveName.Length+1);if(!int.TryParse(suffix,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out number)||number<1||suffix!=number.ToString("D3",System.Globalization.CultureInfo.InvariantCulture)||!numbers.Add(number))return false;}return numbers.Contains(1)&&numbers.Max()==objects.Count;
            }
        }
        public IList<ArchiveSummary> InterruptedArchives(ISet<string> taskIds=null)
        {lock(gate)return ListArchives(true).Where(item=>(item.Status=="failed"||item.Status=="damaged")&&(taskIds==null||taskIds.Contains(item.TaskId))&&Scalar("SELECT count(*) FROM archive_leases WHERE archive_id=?",item.ArchiveId)==0&&!IsCompactionArchive(item.ArchiveId)&&Scalar("SELECT count(*) FROM archive_holds WHERE archive_id=? AND reason<>'interrupted-awaiting-inspection'",item.ArchiveId)==0).ToList();}
        public IList<StoredCopy> InterruptedCheckCopies(long id)
        {
            lock(gate){var copies=GetCopies(id).ToList();copies.AddRange(PendingVolumeTransfers().Where(item=>item.ArchiveId==id).Select(item=>new StoredCopy{GroupId=item.Placement.GroupId,DestinationId=item.Placement.DestinationId,Object=item.Object}));
                foreach(var stage in PendingUploadStages().Where(item=>item.ArchiveId==id)){var item=GetObjects(id).Single(value=>value.Name==stage.Name);copies.Add(new StoredCopy{DestinationId=stage.DestinationId,GroupId="",Object=item});}
                return copies.GroupBy(copy=>copy.DestinationId+"\0"+copy.Object.Name).Select(group=>group.First()).ToList();
            }
        }
        public void RecordInterruptedIntegrity(long id,IList<IntegrityFinding> findings)
        {
            Transaction(delegate{
                var archive=GetArchive(id);if(archive.Status!="failed"&&archive.Status!="damaged")return 0;
                if(Scalar("SELECT count(*) FROM archive_leases WHERE archive_id=?",id)>0||IsCompactionArchive(id))return 0;
                RepositoryStatusChanged(CatalogChange.Archives,archive.TaskId);
                var inspectedStates=IntegrityCopyStates(id);foreach(var finding in findings){string previous=inspectedStates.ContainsKey(finding.Destination+"\0"+finding.Name)?inspectedStates[finding.Destination+"\0"+finding.Name]:"present";string state=ObservedCopyState(finding,previous);database.Execute("UPDATE object_copies SET state=? WHERE name=? AND destination_id=? AND state<>'deleted' AND state<>?",state,finding.Name,finding.Destination,state);}
                bool confirmedDamage=Scalar("SELECT count(*) FROM object_copies c JOIN objects o ON o.name=c.name WHERE o.archive_id=? AND c.state IN ('content-damaged','size-or-name-mismatch')",id)>0;
                if(!HasCompleteArchiveManifest(id)&&!confirmedDamage){database.Execute("UPDATE archives SET status='failed' WHERE id=? AND status<>'failed'",id);return 0;}
                int valid=0;if(IsVolumeArchive(id)){int[] counts;var stats=VolumeStatistics(id);if(stats.TryGetValue(id,out counts))valid=counts[1];}
                else foreach(var group in GetCopies(id).GroupBy(copy=>copy.DestinationId))if(GetObjects(id).All(item=>group.Any(copy=>copy.Object.Name==item.Name)&&findings.Any(value=>value.Destination==group.Key&&value.Name==item.Name&&value.Status=="present-size-matched")))valid++;
                bool unavailable=findings.Any(item=>item.Status.StartsWith("unavailable",StringComparison.Ordinal));
                bool pending=PendingVolumeTransfers().Any(item=>item.ArchiveId==id&&findings.Any(value=>value.Destination==item.Placement.DestinationId&&value.Name==item.Object.Name&&value.Status=="present-size-matched")&&!GetCopies(id).Any(copy=>copy.DestinationId==item.Placement.DestinationId&&copy.Object.Name==item.Object.Name));
                if(HasReadableInspectedManifest(id)&&Scalar("SELECT count(*) FROM archive_cleanup WHERE archive_id=? AND state IN ('rollback','task-delete')",id)==0)
                {
                    if(!findings.Any(item=>item.RepairApplied)&&!findings.Any(item=>item.Status=="missing"||item.Status=="size-or-name-mismatch"||item.Status=="content-damaged")){foreach(var finding in findings){finding.RequiresManualRepairConfirmation=true;finding.RepairAction="correct-state";}return 0;}
                    bool damaged=findings.Any(item=>item.Status=="missing"||item.Status=="size-or-name-mismatch"||item.Status=="content-damaged")||Scalar("SELECT count(*) FROM object_copies c JOIN objects o ON o.name=c.name WHERE o.archive_id=? AND c.state IN ('missing','size-or-name-mismatch','content-damaged')",id)>0;string status=damaged?"repair-required":valid>=archive.TargetCopies?"complete":"redundancy-shortfall";
                    database.Execute("UPDATE archives SET status=? WHERE id=?",status,id);
                    database.Execute("DELETE FROM archive_holds WHERE archive_id=? AND reason='interrupted-awaiting-inspection'",id);
                    database.Execute("UPDATE placements SET status=CASE WHEN reserved>0 THEN 'reconcile-required' WHEN stored>0 THEN 'complete' ELSE 'failed' END WHERE archive_id=?",id);
                    // A newer backup or an explicit deletion in usable history wins.
                    database.Execute("UPDATE archive_files SET state=CASE WHEN EXISTS(SELECT 1 FROM archive_files n JOIN archives a ON a.id=n.archive_id WHERE a.task_id=? AND n.path=archive_files.path AND a.status IN ('complete','repair-required','redundancy-shortfall') AND a.id>?) THEN 'expired' WHEN EXISTS(SELECT 1 FROM archive_files n JOIN archives a ON a.id=n.archive_id WHERE a.task_id=? AND n.path=archive_files.path AND a.id<>? AND a.status IN ('complete','repair-required','redundancy-shortfall') AND n.state='deleted') AND NOT EXISTS(SELECT 1 FROM archive_files n JOIN archives a ON a.id=n.archive_id WHERE a.task_id=? AND n.path=archive_files.path AND a.id<>? AND a.status IN ('complete','repair-required','redundancy-shortfall') AND n.state='current') THEN 'expired' ELSE 'current' END WHERE archive_id=? AND state IN ('deleted','interrupted')",archive.TaskId,id,archive.TaskId,id,archive.TaskId,id,id);
                    if(valid>=archive.TargetCopies)ExpireOlderVersions(id);
                }
                else if(!unavailable&&!pending&&InterruptedCheckCopies(id).All(copy=>findings.Any(item=>item.Destination==copy.DestinationId&&item.Name==copy.Object.Name)))database.Execute("UPDATE archives SET status=? WHERE id=? AND status<>?",confirmedDamage?"damaged":"failed",id,confirmedDamage?"damaged":"failed");
                return 0;
            });
        }
        public void CompleteInterruptedCleanup(long id)
        {Transaction(delegate{if(GetArchive(id).Status!="failed"&&GetArchive(id).Status!="damaged")return 0;if(GetCopies(id).Count>0||PendingVolumeTransfers().Any(item=>item.ArchiveId==id)||PendingUploadStages().Any(item=>item.ArchiveId==id))throw new IOException("未完成归档仍有待清理对象。");RepositoryStatusChanged(CatalogChange.Archives,GetArchive(id).TaskId);database.Execute("DELETE FROM archive_holds WHERE archive_id=? AND reason='interrupted-awaiting-inspection'",id);database.Execute("UPDATE placements SET status='cleaned',reserved=0 WHERE archive_id=?",id);database.Execute("UPDATE archives SET status='cleaned' WHERE id=?",id);database.Execute("INSERT OR REPLACE INTO archive_cleanup(archive_id,state,error) VALUES(?,'complete','')",id);return 0;});}
        public ISet<string> CompletePoolCopies(long id)
        {
            lock(gate){var result=new HashSet<string>();if(!HasCompleteArchiveManifest(id)||Scalar("SELECT count(*) FROM archive_cleanup WHERE archive_id=? AND state IN ('rollback','task-delete')",id)>0)return result;int total=GetObjects(id).Count;
                var rows=database.Query("SELECT c.destination_id,c.name,c.pool_idx FROM object_copies c JOIN objects o ON o.name=c.name WHERE o.archive_id=? AND c.state='present'",id);
                if(IsVolumeArchive(id)){var pools=new HashSet<long>(rows.Where(row=>(long)row[2]>=0).GroupBy(row=>(long)row[2]).Where(group=>group.Select(row=>(string)row[1]).Distinct().Count()==total).Select(group=>group.Key));foreach(var row in rows)if(pools.Contains((long)row[2]))result.Add((string)row[0]+"\0"+(string)row[1]);}
                else foreach(var group in rows.GroupBy(row=>(string)row[0]).Where(group=>group.Select(row=>(string)row[1]).Distinct().Count()==total))foreach(var row in group)result.Add((string)row[0]+"\0"+(string)row[1]);return result;
            }
        }
        public void FinishInterruptedReplicaCleanup(long id)
        {Transaction(delegate{var archive=GetArchive(id);RepositoryStatusChanged(CatalogChange.Archives,archive.TaskId);database.Execute("UPDATE placements SET status=CASE WHEN reserved>0 THEN 'reconcile-required' WHEN stored>0 THEN 'complete' ELSE 'failed' END WHERE archive_id=?",id);if(IsVolumeArchive(id)){int[] counts;var stats=VolumeStatistics(id);int valid=stats.TryGetValue(id,out counts)?counts[1]:0;database.Execute("UPDATE archives SET status=? WHERE id=? AND status IN ('complete','repair-required','redundancy-shortfall')",valid>=archive.TargetCopies?"complete":"redundancy-shortfall",id);}return 0;});}
        public void InterruptedCleanupError(long id,string error)
        {lock(gate){database.Execute("INSERT OR IGNORE INTO archive_cleanup(archive_id,state,error) VALUES(?,'pending','')",id);database.Execute("UPDATE archive_cleanup SET error=? WHERE archive_id=?",error,id);SetSetting("last_upload_alert_v1","未完成归档清理待重试："+error);}}
        public bool CanInspectUploadTemporaries(long id){lock(gate)return CanReconcileVolume(id)&&!IsCompactionArchive(id)&&Scalar("SELECT count(*) FROM archive_holds WHERE archive_id=? AND reason='task-deleted-keep'",id)==0;}
        public ISet<long> TemporaryProblemArchives(ISet<string> taskIds=null)
        {lock(gate)return new HashSet<long>(database.Query("SELECT a.id,a.task_id FROM archives a JOIN archive_cleanup c ON c.archive_id=a.id WHERE c.state='temporary-orphans' AND a.status NOT IN ('failed','damaged','writing','cleaned')").Where(row=>taskIds==null||taskIds.Contains((string)row[1])).Select(row=>(long)row[0]));}
        public void RecordTemporaryProblem(long id,bool found)
        {Transaction(delegate{if(found){database.Execute("INSERT OR IGNORE INTO archive_cleanup(archive_id,state,error) VALUES(?,'temporary-orphans','')",id);database.Execute("UPDATE archive_cleanup SET state='temporary-orphans' WHERE archive_id=? AND state NOT IN ('rollback','task-delete','complete','temporary-orphans')",id);}else database.Execute("DELETE FROM archive_cleanup WHERE archive_id=? AND state='temporary-orphans'",id);RepositoryStatusChanged(CatalogChange.Archives,GetArchive(id).TaskId);return 0;});}
    }
    public sealed partial class ArchiveMaintenance
    {
        // Only owned, incomplete archives can enter this path. A known usable copy
        // always wins over cancellation and is never deleted here.
        public IList<string> DeleteInterruptedArchive(long id,CancellationToken cancellation=default(CancellationToken))
        {
            var errors=new List<string>();using(BeginOperation())using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation,Control==null?CancellationToken.None:Control.Token))
            {
                if(Control!=null)Control.Checkpoint(false);cancellation.ThrowIfCancellationRequested();
                // Foreground repair has no whole-archive deadline. Each remote
                // request already has its own timeout. Stop cleanup passes its
                // independently bounded CleanupToken from BackupControl.
                if(catalog.IsNodeArchive(id)){errors.Add("已被备份版本使用的归档不能按未完成上传删除，请检测完整性并从健康副本修复。");return errors;}
                var archive=catalog.GetArchive(id);if(catalog.PendingVolumeTransfers().Any(item=>item.ArchiveId==id&&item.AwaitingConfirmation)){errors.Add(archive.ArchiveName+"：仍有上传结果待确认，已保留原目标及临时文件，请稍后继续核对。");return errors;}bool whole=archive.Status=="failed"||archive.Status=="damaged";if(!whole&&!new[]{"complete","repair-required","redundancy-shortfall"}.Contains(archive.Status))return errors;
                if(whole&&!catalog.InterruptedArchives().Any(item=>item.ArchiveId==id)||catalog.IsCompactionArchive(id))return errors;
                if(whole&&catalog.HasCompleteArchiveManifest(id))
                {
                    var confirmed=new List<IntegrityFinding>();foreach(var pending in catalog.PendingVolumeTransfers().Where(item=>item.ArchiveId==id&&item.Phase=="committing"))
                    {
                        IManagedBackupDestination target=null;try{target=adapters.CreateTransfer(operationDefinitions[pending.Placement.DestinationId]) as IManagedBackupDestination;if(target==null)throw new NotSupportedException("仓库无法核对提交结果。");var cancel=target as ICancellableBackupDestination;if(cancel!=null)cancel.OperationCancellation=timeout.Token;var info=target.Stat(pending.Object.Name);if(!info.Exists)continue;if(info.Name!=pending.Object.Name||info.Size!=pending.Object.Size)continue;catalog.ResolveVolumeTransfer(pending,true);confirmed.Add(new IntegrityFinding{RepairApplied=true,Destination=pending.Placement.DestinationId,Name=pending.Object.Name,ExpectedSize=pending.Object.Size,ActualSize=pending.Object.Size,Status="present-size-matched"});}
                        catch(Exception error){cancellation.ThrowIfCancellationRequested();if(Control!=null)Control.Token.ThrowIfCancellationRequested();if(!(error is IOException)&&!(error is UnauthorizedAccessException)&&!(error is NotSupportedException)&&!(error is OperationCanceledException))throw;errors.Add(pending.Object.Name+" 提交结果待核对："+error.Message);}
                        finally{if(target!=null)adapters.Release(target);}
                    }
                    if(errors.Count>0){catalog.InterruptedCleanupError(id,string.Join("；",errors));return errors;}if(confirmed.Count>0){catalog.RecordInterruptedIntegrity(id,confirmed);archive=catalog.GetArchive(id);whole=archive.Status=="failed"||archive.Status=="damaged";}
                }
                var protect=catalog.CompletePoolCopies(id);if(whole&&protect.Count>0){errors.Add(archive.ArchiveName+"：发现完整副本，已保留，请重新检查。");return errors;}
                var copies=catalog.GetCopies(id);var transfers=catalog.PendingVolumeTransfers().Where(item=>item.ArchiveId==id).ToList();var stages=catalog.PendingUploadStages().Where(item=>item.ArchiveId==id).ToList();
                var selected=catalog.InterruptedCheckCopies(id).Where(item=>!protect.Contains(item.DestinationId+"\0"+item.Object.Name)||stages.Any(stage=>stage.DestinationId==item.DestinationId&&stage.Name==item.Object.Name)||transfers.Any(transfer=>transfer.Placement.DestinationId==item.DestinationId&&transfer.Object.Name==item.Object.Name)).ToList();if(selected.Count==0&&!whole)return errors;
                foreach(var item in selected)
                {
                    IManagedBackupDestination target=null;try{
                        timeout.Token.ThrowIfCancellationRequested();target=adapters.CreateTransfer(operationDefinitions[item.DestinationId]) as IManagedBackupDestination;if(target==null)throw new NotSupportedException("仓库不支持清理。");var cancellable=target as ICancellableBackupDestination;if(cancellable!=null)cancellable.OperationCancellation=timeout.Token;
                        bool owned=copies.Any(copy=>copy.DestinationId==item.DestinationId&&copy.Object.Name==item.Object.Name);var transfer=transfers.FirstOrDefault(value=>value.Placement.DestinationId==item.DestinationId&&value.Object.Name==item.Object.Name);
                        var info=target.Stat(item.Object.Name);
                        bool keep=protect.Contains(item.DestinationId+"\0"+item.Object.Name);
                        if(info.Exists&&!keep){if(!owned){if(transfer==null||transfer.Phase!="committing")throw new IOException("远程同名文件没有已提交的所有权记录，已保留。");if(!BackupObjectNaming.IsManaged(item.Object.Name))throw new IOException("文件名称不符合本软件备份命名规则，已保留。");}
                            target.DeleteObject(item.Object.Name);if(target.Stat(item.Object.Name).Exists)throw new IOException("未完成归档未删除。");if(owned)catalog.ObjectRemoved(item.Object.Name,item.DestinationId);
                        }else if(!info.Exists&&owned&&!keep)catalog.ObjectRemoved(item.Object.Name,item.DestinationId);
                        target.DeleteTemporary(item.Object.Name);if(transfer!=null)catalog.ResolveVolumeTransfer(transfer,keep);
                    }catch(Exception error){cancellation.ThrowIfCancellationRequested();if(Control!=null)Control.Token.ThrowIfCancellationRequested();if(!(error is IOException)&&!(error is UnauthorizedAccessException)&&!(error is NotSupportedException)&&!(error is OperationCanceledException))throw;errors.Add(item.Object.Name+" → "+item.DestinationId+"："+error.Message);}
                    finally{if(target!=null)adapters.Release(target);}
                }
                if(errors.Count==0){if(whole)catalog.CompleteInterruptedCleanup(id);else catalog.FinishInterruptedReplicaCleanup(id);}else catalog.InterruptedCleanupError(id,string.Join("；",errors));
            }return errors;
        }
    }
}
