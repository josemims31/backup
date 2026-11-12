using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace Backup.Core
{
    public sealed class VolumeAssignment {public ObjectRecord Object;public ArchivePlacement Placement;public int Pool;}
    public sealed class PendingVolumeTransfer {public long ArchiveId;public ObjectRecord Object;public ArchivePlacement Placement;public int Pool;public string Phase;public bool AwaitingConfirmation,ConfirmationDamaged,MetadataMatched,UploadFailed;}
    public sealed partial class BackupCatalog
    {
        internal void MarkMatchedVolumeCopy(string name,string destinationId)
        {lock(gate)database.Execute("UPDATE object_copies SET state='present' WHERE name=? AND destination_id=? AND state NOT IN ('present','deleted','content-damaged')",name,destinationId);}
        internal bool HasUnstartedVolume(string name,string destinationId)
        {lock(gate)return Scalar("SELECT count(*) FROM volume_transfers WHERE name=? AND destination_id=? AND state='checking'",name,destinationId)>0;}
        public ArchivePlan PlanVolumeMigration(long id,StorageTopology targets,int copies)
        {
            lock(gate){if(targets.PolicyRoots==null)targets.PolicyRoots=targets.Groups.Where(g=>!g.Retired).Select(g=>g.Id).ToList();
                var usage=GetUsage();var byDestination=usage.ToDictionary(value=>value.DestinationId);foreach(var copy in GetCopies(id))byDestination[copy.DestinationId].UsedBytes-=copy.Object.Size;
                var objects=GetObjects(id);if(!HasCompleteArchiveManifest(id))throw new IOException("压缩包分卷清单缺失或不完整，已保留现有文件及版本引用。");var plan=GroupAllocator.Plan(targets,usage,objects.Max(o=>o.Size),copies,null);
                if(!plan.StructurallyComplete||plan.Pools==null||plan.Pools.Count==0)throw new IOException("替代仓库不足，旧分卷保留。");
                plan.ArchiveId=id;plan.ArchiveName=GetArchive(id).ArchiveName;plan.Placements.Clear();return plan;
            }
        }
        public List<VolumeAssignment> ChooseVolumeAssignments(ArchivePlan plan,ObjectRecord item,ISet<string> reusable)
        {
            lock(gate){var flat=new StorageTopology{Destinations=LoadTopology().Destinations,Groups=plan.Pools.Select(p=>new BackupGroupDefinition{Id="pool:"+p.Index,DestinationIds=p.DestinationIds.ToList()}).ToList()};
                var routes=WarehouseMatching.Match(flat,GetUsage(),item.Size,plan.Pools.Count,null,false,reusable);
                if(routes.Count!=plan.Pools.Count)throw new IOException("没有足够容量修复所有分卷副本，旧分卷保留。");
                return routes.Select(p=>{int index=int.Parse(p.GroupId.Substring(5));p.GroupId=plan.Pools.Single(pool=>pool.Index==index).GroupId;return new VolumeAssignment{Object=item,Placement=p,Pool=index};}).ToList();
            }
        }
        public void ReserveStagedVolume(ArchivePlan plan,VolumeAssignment route)
        {
            Transaction(delegate{var item=route.Object;var place=route.Placement;var d=LoadTopology().Destinations.Single(value=>value.Id==place.DestinationId);var usage=GetUsage().Single(u=>u.DestinationId==d.Id);
                if((decimal)usage.UsedBytes+usage.ReservedBytes+item.Size>d.MaxBytes)throw new IOException("仓库容量不足。");
                database.Execute("INSERT OR IGNORE INTO placements(archive_id,group_id,destination_id,reserved,status) VALUES(?,?,?,0,'writing')",plan.ArchiveId,place.GroupId,place.DestinationId);
                database.Execute("UPDATE placements SET reserved=reserved+?,status='writing' WHERE archive_id=? AND group_id=? AND destination_id=?",item.Size,plan.ArchiveId,place.GroupId,place.DestinationId);
                database.Execute("INSERT INTO volume_transfers(name,destination_id,pool_idx,group_id,state) VALUES(?,?,?,?,'checking')",item.Name,place.DestinationId,route.Pool,place.GroupId);return 0;});
        }
        public void CommitStagedVolume(ArchivePlan plan,VolumeAssignment route)
        {Transaction(delegate{CommitObjectCopyUnsafe(plan,route.Placement,route.Object,-1);database.Execute("DELETE FROM volume_transfers WHERE name=? AND destination_id=?",route.Object.Name,route.Placement.DestinationId);database.Execute("UPDATE placements SET status='complete' WHERE archive_id=? AND group_id=? AND destination_id=? AND reserved=0",plan.ArchiveId,route.Placement.GroupId,route.Placement.DestinationId);return 0;});}
        public void PublishVolumeMigration(ArchivePlan plan,IList<VolumeAssignment> assignments)
        {
            Transaction(delegate{
                if(HasOutstandingTransfer(plan.ArchiveId))throw new IOException("分卷传输仍待核对，旧布局保留。");
                var objects=GetObjects(plan.ArchiveId);var counts=assignments.GroupBy(item=>item.Object.Name).ToDictionary(group=>group.Key,group=>group.Count());if(objects.Any(o=>!counts.ContainsKey(o.Name)||counts[o.Name]!=plan.Pools.Count))throw new IOException("新分卷布局不完整。");
                foreach(var a in assignments)if(Scalar("SELECT count(*) FROM object_copies WHERE name=? AND destination_id=? AND state='present'",a.Object.Name,a.Placement.DestinationId)!=1)throw new IOException("新分卷未完整验证，禁止移除旧副本。");
                var retained=new HashSet<Tuple<string,string>>(assignments.Select(item=>Tuple.Create(item.Object.Name,item.Placement.DestinationId)));
                foreach(var old in GetCopies(plan.ArchiveId).Where(c=>!retained.Contains(Tuple.Create(c.Object.Name,c.DestinationId))))database.Execute("INSERT OR IGNORE INTO retired_objects(name,destination_id,archive_id,size,group_id) VALUES(?,?,?,?,?)",old.Object.Name,old.DestinationId,plan.ArchiveId,old.Object.Size,old.GroupId);
                database.Execute("DELETE FROM object_copies WHERE name IN(SELECT name FROM objects WHERE archive_id=?)",plan.ArchiveId);
                database.Execute("UPDATE placements SET stored=0,reserved=0,status='retired' WHERE archive_id=?",plan.ArchiveId);SavePools(plan.ArchiveId,plan.Pools);
                foreach(var a in assignments){database.Execute("INSERT INTO object_copies(name,destination_id,group_id,state,pool_idx) VALUES(?,?,?,'present',?)",a.Object.Name,a.Placement.DestinationId,a.Placement.GroupId,a.Pool);
                    database.Execute("INSERT OR IGNORE INTO placements(archive_id,group_id,destination_id,reserved,status) VALUES(?,?,?,0,'complete')",plan.ArchiveId,a.Placement.GroupId,a.Placement.DestinationId);
                    database.Execute("UPDATE placements SET stored=stored+?,status='complete' WHERE archive_id=? AND group_id=? AND destination_id=?",a.Object.Size,plan.ArchiveId,a.Placement.GroupId,a.Placement.DestinationId);}
                database.Execute("UPDATE archives SET target_copies=?,status='complete' WHERE id=?",plan.Pools.Count,plan.ArchiveId);return 0;
            });
        }
        public IList<PendingVolumeTransfer> PendingVolumeTransfers()
        {lock(gate)return database.Query("SELECT o.archive_id,o.name,o.size,o.sha256,t.destination_id,t.group_id,t.pool_idx,t.state,p.status FROM volume_transfers t JOIN objects o ON o.name=t.name JOIN placements p ON p.archive_id=o.archive_id AND p.group_id=t.group_id AND p.destination_id=t.destination_id").Select(r=>{string state=(string)r[7];bool pending=state.StartsWith("pending-",StringComparison.Ordinal),damaged=state.StartsWith("damaged-",StringComparison.Ordinal),matched=state.StartsWith("checked-",StringComparison.Ordinal);return new PendingVolumeTransfer{ArchiveId=(long)r[0],Object=new ObjectRecord{Name=(string)r[1],Size=(long)r[2],Sha256=(string)r[3]},Placement=new ArchivePlacement{DestinationId=(string)r[4],GroupId=(string)r[5],Status=(string)r[8]},Pool=(int)(long)r[6],Phase=pending||damaged||matched?state.Substring(8):state,AwaitingConfirmation=pending,ConfirmationDamaged=damaged,MetadataMatched=matched,UploadFailed=state=="failed-upload"};}).ToList();}
        public bool CanReconcileVolume(long id){lock(gate)return Scalar("SELECT count(*) FROM archives WHERE id=? AND status='writing'",id)==0&&Scalar("SELECT count(*) FROM archive_leases WHERE archive_id=?",id)==0;}
        public void ResolveVolumeTransfer(PendingVolumeTransfer transfer,bool committed)
        {
            Transaction(delegate{var item=transfer.Object;var place=transfer.Placement;
                if(Scalar("SELECT count(*) FROM volume_transfers WHERE name=? AND destination_id=?",item.Name,place.DestinationId)==0)return 0;
                bool known=Scalar("SELECT count(*) FROM object_copies WHERE name=? AND destination_id=? AND state<>'deleted'",item.Name,place.DestinationId)>0;
                if(!known){if(committed){if(transfer.UploadFailed)database.Execute("UPDATE placements SET reserved=reserved+? WHERE archive_id=? AND group_id=? AND destination_id=?",item.Size,transfer.ArchiveId,place.GroupId,place.DestinationId);database.Execute("UPDATE placements SET status='writing' WHERE archive_id=? AND group_id=? AND destination_id=?",transfer.ArchiveId,place.GroupId,place.DestinationId);var plan=new ArchivePlan{ArchiveId=transfer.ArchiveId,TaskId=GetArchive(transfer.ArchiveId).TaskId};CommitObjectCopyUnsafe(plan,place,item,IsVolumeArchive(transfer.ArchiveId)?transfer.Pool:-1);}else if(!transfer.UploadFailed)database.Execute("UPDATE placements SET reserved=MAX(0,reserved-?) WHERE archive_id=? AND group_id=? AND destination_id=?",item.Size,transfer.ArchiveId,place.GroupId,place.DestinationId);}
                if(known&&committed&&transfer.Phase=="repair-committing"){database.Execute("UPDATE object_copies SET state='present' WHERE name=? AND destination_id=? AND state<>'present'",item.Name,place.DestinationId);RepositoryObjectCommitted(place.DestinationId,item.Name,item.Size,transfer.ArchiveId,true);}
                database.Execute("DELETE FROM volume_transfers WHERE name=? AND destination_id=?",item.Name,place.DestinationId);
                database.Execute("UPDATE placements SET status=CASE WHEN reserved=0 THEN CASE WHEN stored>0 THEN 'complete' ELSE 'failed' END WHEN EXISTS(SELECT 1 FROM volume_transfers t JOIN objects o ON o.name=t.name WHERE o.archive_id=placements.archive_id AND t.destination_id=placements.destination_id AND t.state LIKE 'pending-%') THEN 'reconcile-required' WHEN EXISTS(SELECT 1 FROM volume_transfers t JOIN objects o ON o.name=t.name WHERE o.archive_id=placements.archive_id AND t.destination_id=placements.destination_id AND t.state LIKE 'checked-%') THEN 'repair-required' ELSE 'writing' END WHERE archive_id=? AND group_id=? AND destination_id=?",transfer.ArchiveId,place.GroupId,place.DestinationId);RefreshConfirmedVolumeArchiveUnsafe(transfer.ArchiveId);return 0;});
        }
        // Called only by the exclusive desktop host at startup, never by read-only observers.
        public void AbandonInterruptedVolumeArchives()
        {Transaction(delegate{database.Execute("UPDATE archive_files SET state='interrupted' WHERE state<>'staged' AND archive_id IN(SELECT id FROM archives WHERE status='writing' AND id IN(SELECT archive_id FROM archive_pools))");database.Execute("UPDATE archives SET status='failed' WHERE status='writing' AND id IN(SELECT archive_id FROM archive_pools)");database.Execute("UPDATE placements SET status='failed' WHERE archive_id IN(SELECT id FROM archives WHERE status='failed') AND reserved=0 AND status='writing'");database.Execute("INSERT OR IGNORE INTO archive_holds(archive_id,reason) SELECT id,'interrupted-awaiting-inspection' FROM archives WHERE status IN ('failed','damaged')");return 0;});}
    }
    public sealed partial class ArchiveMaintenance
    {
        public IList<string> ReconcileVolumeTransfers(string taskId=null,ISet<long> archiveIds=null)
        {using(BeginOperation()){
            var errors=new List<string>();foreach(var pending in catalog.PendingVolumeTransfers().Where(p=>(archiveIds==null||archiveIds.Contains(p.ArchiveId))&&(taskId==null||catalog.GetArchive(p.ArchiveId).TaskId==taskId)))try{
                if(!catalog.CanReconcileVolume(pending.ArchiveId))continue;
                if(pending.AwaitingConfirmation){errors.Add(pending.Object.Name+(pending.Placement.Status=="unavailable"?"：仓库暂不可访问，尚无法核对上传结果。":"：上传结果仍未核对，请稍后运行完整性检查。"));continue;}
                var target=Open(pending.Placement.DestinationId);bool known=catalog.GetCopies(pending.ArchiveId).Any(c=>c.Object.Name==pending.Object.Name&&c.DestinationId==pending.Placement.DestinationId);
                if(known){target.DeleteTemporary(pending.Object.Name);catalog.ResolveVolumeTransfer(pending,true);continue;}
                if(pending.ConfirmationDamaged){if(target.Stat(pending.Object.Name).Exists)target.DeleteObject(pending.Object.Name);target.DeleteTemporary(pending.Object.Name);catalog.ResolveVolumeTransfer(pending,false);continue;}
                bool committed=false;if(pending.Phase!="checking"){var info=target.Stat(pending.Object.Name);if(info.Exists){if(catalog.HasCompleteArchiveManifest(pending.ArchiveId)&&info.Name==pending.Object.Name&&info.Size==pending.Object.Size)committed=true;else if(BackupObjectNaming.IsManaged(pending.Object.Name)){target.DeleteObject(pending.Object.Name);if(target.Stat(pending.Object.Name).Exists)throw new IOException("未完成归档的删除尚未生效。");}else throw new InvalidBackupObjectException("未完成上传的文件名不符合本软件规则，已保留。");}}
                if(pending.Phase!="checking")target.DeleteTemporary(pending.Object.Name);catalog.ResolveVolumeTransfer(pending,committed);
            }catch(IOException e){errors.Add(pending.Object.Name+"："+e.Message);}catch(UnauthorizedAccessException e){errors.Add(pending.Object.Name+"："+e.Message);}
            var confirming=new HashSet<string>(catalog.PendingVolumeTransfers().Where(item=>item.AwaitingConfirmation).Select(item=>item.Placement.DestinationId+"\0"+item.Object.Name));
            foreach(var pending in catalog.PendingUploadStages().Where(p=>!confirming.Contains(p.DestinationId+"\0"+p.Name)&&(archiveIds==null||archiveIds.Contains(p.ArchiveId))&&(taskId==null||catalog.GetArchive(p.ArchiveId).TaskId==taskId)))try{if(catalog.CanReconcileVolume(pending.ArchiveId))Open(pending.DestinationId).DeleteTemporary(pending.Name);}catch(IOException error){errors.Add(pending.Name+" 临时上传待核对："+error.Message);}catch(UnauthorizedAccessException error){errors.Add(pending.Name+" 临时上传待核对："+error.Message);}
            return errors;
        }}
        private void MigrateVolumes(long id,StorageTopology targets,int copies)
        {
            if(catalog.HasOutstandingTransfer(id))throw new IOException("分卷上传仍待核对，暂不迁移。");
            var plan=catalog.PlanVolumeMigration(id,targets,copies);var assignments=new List<VolumeAssignment>();var copiesByObject=catalog.GetCopies(id).ToLookup(copy=>copy.Object.Name);var states=catalog.IntegrityCopyStates(id);
            RepairObjects(catalog.GetObjects(id),item=>{
                var existing=copiesByObject[item.Name].ToList();Exception unavailable;var matched=MatchedCopies(item,existing,out unavailable);var available=new HashSet<string>(matched.Where(destination=>!states.ContainsKey(destination+"\0"+item.Name)||states[destination+"\0"+item.Name]!="content-damaged"));
                foreach(string destination in available)catalog.MarkMatchedVolumeCopy(item.Name,destination);
                if(matched.Count==0){if(unavailable!=null)throw new IOException("分卷暂时无法读取，保留原副本和修复记录，请稍后重试："+item.Name,unavailable);throw new NoReadableReplicaException();}
                IDisposable memory=null;var prepared=new List<Tuple<VolumeAssignment,IManagedBackupDestination>>();var preparationErrors=new List<Exception>();
                var ready=new Dictionary<string,IManagedBackupDestination>();var failed=new Dictionary<string,Exception>();
                try{byte[] bytes=null;List<VolumeAssignment> routes;lock(migrationGate)routes=available.Count==0?new List<VolumeAssignment>():catalog.ChooseVolumeAssignments(plan,item,available);
                    PrepareRepairWrites(routes.Where(route=>!available.Contains(route.Placement.DestinationId)).Select(route=>route.Placement.DestinationId),ready,failed);
                    if(available.Count==0||routes.Any(route=>!available.Contains(route.Placement.DestinationId))){memory=repairMemory.WaitReserve(item.Size,RepairCancellation);bytes=ReadRepairSource(id,item,available,matched);}
                    // Selection and reservation share the same lock so simultaneous
                    // downloads cannot all claim the last free space in a pool.
                    lock(migrationGate){routes=catalog.ChooseVolumeAssignments(plan,item,available);
                        foreach(var route in routes){if(available.Contains(route.Placement.DestinationId)){assignments.Add(route);continue;}
                            if(bytes==null)throw new IOException("修复目标已变化，请重新核对。");IManagedBackupDestination target=null;bool retained=false;
                            try{target=TakePreparedRepairWrite(route.Placement.DestinationId,ready,failed);var known=existing.FirstOrDefault(c=>c.DestinationId==route.Placement.DestinationId);
                                if(known==null&&(target.Stat(item.Name).Exists||target.Stat(item.Name+".partial").Exists))throw new IOException("目标已有未登记的同名对象，拒绝覆盖。");
                                if(known!=null){target.DeleteObject(item.Name);catalog.ObjectRemoved(item.Name,known.DestinationId);}
                                catalog.ReserveStagedVolume(plan,route);prepared.Add(Tuple.Create(route,target));retained=true;
                            }catch(Exception error){if(!(error is IOException)&&!(error is UnauthorizedAccessException)&&!(error is NotSupportedException))throw;preparationErrors.Add(error);}finally{if(!retained&&target!=null)adapters.Release(target);}
                        }
                    }
                    RepairParallel(prepared,5,write=>{var route=write.Item1;var target=write.Item2;bool uploaded=false;
                        try{catalog.VolumePhase(item.Name,route.Placement.DestinationId,"temporary");
                            ReportObject("正在上传修复分卷：",item,route.Placement.DestinationId);var uploader=new RetryUploader(new[]{(IBackupDestination)target},RetryUploader.DefaultAttempts){Transfers=adapters.Transfers,Progress=p=>{ReportUploadProgress(p);if(Report!=null&&(p.State=="upload-failed"||p.State=="retrying"||p.State=="retry-succeeded"))Report(RetryUploader.FailureMessage(p));},InjectFailure=InjectFailure,Control=Control,PhaseChanged=phase=>catalog.VolumePhase(item.Name,route.Placement.DestinationId,phase)};uploader.Upload(item,bytes,bytes.Length);uploaded=true;catalog.CommitStagedVolume(plan,route);ReportUploadProgress(new TransferProgress{DestinationId=route.Placement.DestinationId,ObjectName=item.Name,State="copy-complete",Written=item.Size,Size=item.Size});
                            lock(migrationGate)assignments.Add(route);ReportObject("已补齐分卷副本：",item,route.Placement.DestinationId);
                        }catch(Exception e){bool uncertain=uploaded||catalog.UploadStages(route.Placement.DestinationId,item.Name).Count>0||e is TransferReconciliationRequiredException;catalog.FailVolumeCopy(plan,route.Placement,item,uncertain);throw;}
                    });
                    if(preparationErrors.Count>0)throw preparationErrors[0];
                }finally{foreach(var write in prepared){if(catalog.HasUnstartedVolume(item.Name,write.Item1.Placement.DestinationId))catalog.FailVolumeCopy(plan,write.Item1.Placement,item,false);adapters.Release(write.Item2);}foreach(var target in ready.Values)adapters.Release(target);if(memory!=null)memory.Dispose();}
            });
            catalog.PublishVolumeMigration(plan,assignments);RemoveRetired(id,true);
        }
    }
}
