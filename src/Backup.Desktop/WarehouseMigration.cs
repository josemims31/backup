using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Backup.Core;
namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        public string CancelPendingWarehouseMove(string id){var move=Catalog.GetWarehouseMoves().Single(m=>m.DestinationId==id&&m.State=="copying");var target=JsonSettings.Decode(move.TargetJson,new DestinationDefinition());string manifest=Path.Combine(CacheDirectory,"取消迁移待核对-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,5)+".txt");using(var output=new StreamWriter(manifest,false,new System.Text.UTF8Encoding(true))){output.WriteLine("迁移已取消，原仓库仍使用旧路径。以下目标位置可能有迁移副本或临时文件，未自动删除，人工清理前请核对：");foreach(var item in Catalog.WarehouseMoveObjects(id)){output.WriteLine(ObjectLocation(target,item.Name));foreach(string temporary in Catalog.UploadStages(id,item.Name))output.WriteLine(ObjectLocation(target,temporary));}}foreach(var item in Catalog.WarehouseMoveObjects(id))foreach(string temporary in Catalog.UploadStages(id,item.Name))Catalog.ForgetUploadStage(id,temporary);Catalog.CancelWarehouseMove(id);return manifest;}
#if UI_TEST
        public Func<string,int,bool> MoveFailure;
        public Action<string> BeforeMoveOldDelete;
#endif
        private IManagedBackupDestination MoveAdapter(DestinationDefinition definition,RemoteConnectionOptions options,bool findMoved=false,bool remember=true)
        {
            var result=objectStorage.Wrap(definition,folder=>RepositoryDirectory(definition,folder,OperationCancellation,RepositoryProfile(definition,options)),findMoved,remember);
            ((IBandwidthDestination)result).Bandwidth=Bandwidth;((IOwnedUploadDestination)result).UploadJournal=Catalog;((ICancellableBackupDestination)result).OperationCancellation=OperationCancellation;return result;
        }
        private static void CheckMoveObject(IBackupDestination target,ObjectRecord item,RemoteObjectInfo observed=null)
        {
            var info=observed??target.Stat(item.Name);if(!info.Exists||info.Name!=item.Name||info.Size!=item.Size)throw new IOException("迁移文件的名称或大小不符，原文件保留："+item.Name);
        }
        public void MoveWarehouse(DestinationDefinition proposed,string name,RemoteConnectionOptions options,Action<string> report=null)
        {try{MoveWarehouseCore(proposed,name,options,report);}finally{InvalidateRepositoryConnections(topology:Catalog.LoadTopology());}}
        public void SwitchWarehouseLocation(DestinationDefinition proposed,string name,RemoteConnectionOptions options,Action<string> report=null)
        {
            var old=Catalog.LoadTopology().Destinations.Single(d=>d.Id==proposed.Id);proposed.Enabled=old.Enabled;
            if(proposed.MaxBytes<=0)throw new ArgumentException("容量限制必须大于零。");
            bool local=RepositoryModules.IsFileSystem(proposed.Protocol);proposed.Location=RepositoryModules.NormalizeLocation(proposed.Protocol,proposed.Location);
            options=options??(local?new RemoteConnectionOptions():LoadConnection(proposed.Id));RepositoryProfileEntry profile=RepositoryProfile(proposed,options);
            IManagedBackupDestination target=RepositoryDirectory(proposed,proposed.Location,OperationCancellation,profile);
            try{
                if(report!=null)report("正在检查新仓库连接…");((IReadOnlyDestinationProbe)target).CheckReadableRoot();
                Catalog.RelocateWarehouse(proposed,name,ConnectionConfiguration(proposed.Id,options));InvalidateRepositoryConnections(proposed.Id,keep:profile);
                if(report!=null)report("仓库位置已切换；已有文件登记保留，缺失文件由完整性检查报告。");
            }finally{DestinationAdapterRegistry.DisposeAdapter(target);}
        }
        private void MoveWarehouseCore(DestinationDefinition proposed,string name,RemoteConnectionOptions options,Action<string> report)
        {
            var move=Catalog.GetWarehouseMoves().FirstOrDefault(m=>m.DestinationId==proposed.Id&&m.State!="complete");
            if(move!=null&&move.State=="published"){var current=Catalog.LoadTopology().Destinations.Single(d=>d.Id==proposed.Id);if(current.Location!=proposed.Location||current.Protocol!=proposed.Protocol)throw new IOException("请先重试上次迁移的旧副本清理，再更改路径。");if(options!=null&&JsonSettings.Encode(options)!=JsonSettings.Encode(LoadConnection(proposed.Id))){SaveConnection(proposed.Id,options);Catalog.UpdateMoveConnections(proposed.Id,move.OldConnection,Catalog.GetSetting("connection:"+proposed.Id));}CleanupWarehouseMove(move,report);return;}
            var old=move==null?Catalog.LoadTopology().Destinations.Single(d=>d.Id==proposed.Id):JsonSettings.Decode(move.OriginalJson,new DestinationDefinition());proposed.Enabled=old.Enabled;
            var topology=Catalog.LoadTopology();topology.Destinations.RemoveAll(d=>d.Id==proposed.Id);topology.Destinations.Add(proposed);topology=ValidateStorageTopology(topology);proposed=topology.Destinations.Single(d=>d.Id==proposed.Id);var usage=Catalog.GetUsage().Single(u=>u.DestinationId==proposed.Id);if(proposed.MaxBytes<(decimal)usage.UsedBytes+usage.ReservedBytes)throw new IOException("容量限制不足以容纳已有备份和预留数据。");
            if(move==null&&RemotePath.IsSftpPortChange(old,proposed)){SaveDestinationDefinition(WorkspaceMigration.Load(Catalog),proposed,name,options);return;}
            bool local=RepositoryModules.IsFileSystem(old.Protocol);if(move==null&&((old.Protocol=="local"||old.Protocol=="smb")&&(proposed.Protocol=="local"||proposed.Protocol=="smb")||old.Protocol==proposed.Protocol)&&SameFolder(old.Location,proposed.Location,local)){proposed.Location=old.Location;proposed.Protocol=old.Protocol;var group=topology.Groups.FirstOrDefault(g=>g.Id=="single:"+old.Id);if(group!=null)group.Name=name;SaveTopology(topology);SaveConnection(old.Id,options??new RemoteConnectionOptions());return;}
            string oldConnection=move==null?Catalog.GetSetting("connection:"+old.Id)??"":move.OldConnection;
            var request=new WarehouseMoveRecord{DestinationId=old.Id,OriginalJson=JsonSettings.Encode(old),TargetJson=JsonSettings.Encode(proposed),OldConnection=oldConnection,NewConnection=ProtectConnection(options??new RemoteConnectionOptions()),TargetName=name};
            if(move==null)Catalog.CreateSnapshot(Path.Combine(CacheDirectory,"catalog-before-move-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,6)+".db"));
            using(var session=Catalog.BeginWarehouseMove(request)){IManagedBackupDestination source=null,target=null;try{
                source=MoveAdapter(old,UnprotectConnection(session.Record.OldConnection),true,false);target=MoveAdapter(proposed,options??new RemoteConnectionOptions());target.PrepareWrite();int done=0;
                foreach(var item in session.Objects){if(report!=null)report("迁移 "+(++done)+" / "+session.Objects.Count+"："+item.Name);var present=target.Stat(item.Name);
                    if(present.Exists){CheckMoveObject(target,item,present);Catalog.WarehouseMoveObjectState(old.Id,item.Name,"copied",proposed.Location);continue;}
                    if(target.Stat(item.Name+".partial").Exists)throw new IOException("新路径存在未登记的同名临时文件，拒绝覆盖："+item.Name);target.DeleteTemporary(item.Name);
                    byte[] bytes=ArchiveObjectIO.ReadVerified(source,item,null,Transfers);Catalog.WarehouseMoveObjectState(old.Id,item.Name,"writing");var uploader=new RetryUploader(new[]{target},RetryUploader.DefaultAttempts){Transfers=Transfers,Progress=p=>{if(report!=null&&(p.State=="upload-failed"||p.State=="retrying"||p.State=="retry-succeeded"))report(RetryUploader.FailureMessage(p));}};
#if UI_TEST
                    uploader.InjectFailure=(id,file,attempt,written)=>MoveFailure!=null&&MoveFailure(file,attempt);
#endif
                    uploader.Upload(item.Name,bytes,bytes.Length);Catalog.WarehouseMoveObjectState(old.Id,item.Name,"copied",proposed.Location);
                }
                // Source bytes are hashed during copying. Cleanup uses current
                // names and sizes instead of downloading both copies again.
                Catalog.PublishWarehouseMove(proposed,name,ProtectConnection(options??new RemoteConnectionOptions()),EncodeConnectionReference(options??new RemoteConnectionOptions()));
            }catch(Exception error){Catalog.WarehouseMoveError(old.Id,error.Message);throw;}finally{DestinationAdapterRegistry.DisposeAdapter(source);DestinationAdapterRegistry.DisposeAdapter(target);}}
            try{CleanupWarehouseMove(Catalog.GetWarehouseMoves().Single(m=>m.DestinationId==old.Id),report);}catch(Exception error){throw new IOException("新路径已验证并切换，旧副本清理待重试："+error.Message,error);}
        }
        public void CleanupWarehouseMove(WarehouseMoveRecord move,Action<string> report=null)
        {
            move=Catalog.GetWarehouseMoves().Single(item=>item.DestinationId==move.DestinationId);if(move.State!="published")return;
            var original=JsonSettings.Decode(move.OriginalJson,new DestinationDefinition());var targetDefinition=Catalog.LoadTopology().Destinations.Single(d=>d.Id==move.DestinationId);
            if(RemotePath.IsSftpPortChange(original,targetDefinition))throw new IOException("检测到旧版仅修改 SFTP 端口产生的迁移，已阻止旧地址文件清理，备份文件保留。");
            if(!move.CatalogPublished){var names=new HashSet<string>(Catalog.WarehouseMoveObjects(move.DestinationId).Select(item=>item.Name));var ids=new HashSet<long>(Catalog.GetWarehouseObjects(move.DestinationId).Where(item=>names.Contains(item.Name)).Select(item=>item.ArchiveId));PublishMigrationCatalogs(ids,move.DestinationId,report);Catalog.MarkWarehouseMoveCatalogPublished(move.DestinationId);}
            IManagedBackupDestination source=null,target=null;
            try{source=MoveAdapter(original,UnprotectConnection(move.OldConnection),true,false);target=MoveAdapter(targetDefinition,LoadConnection(move.DestinationId));foreach(var item in Catalog.WarehouseMoveObjects(move.DestinationId)){if(Catalog.WarehouseMoveObjectState(move.DestinationId,item.Name)=="deleted")continue;
                CheckMoveObject(target,item);var oldInfo=source.Stat(item.Name);if(oldInfo.Exists){CheckMoveObject(source,item,oldInfo);if(report!=null)report("新副本已确认，正在清理旧文件："+item.Name);
#if UI_TEST
                    if(BeforeMoveOldDelete!=null)BeforeMoveOldDelete(item.Name);
#endif
                    source.DeleteObject(item.Name);if(source.Stat(item.Name).Exists)throw new IOException("旧副本未删除："+item.Name);}Catalog.WarehouseMoveObjectState(move.DestinationId,item.Name,"deleted",original.Location);}Catalog.FinishWarehouseMove(move.DestinationId);
            }catch(Exception error){Catalog.WarehouseMoveError(move.DestinationId,error.Message);throw;}finally{DestinationAdapterRegistry.DisposeAdapter(source);DestinationAdapterRegistry.DisposeAdapter(target);}
        }
    }
    public sealed partial class MainWindowController
    {
        private void CancelSelectedPathMove(){RunConfiguration(CancelSelectedPathMoveCore);}
        private void CancelSelectedPathMoveCore(){if(busy)return;var row=Grid("WarehouseGrid").SelectedItem as WarehouseView;if(row==null||!row.Id.StartsWith("single:"))return;string manifest=Backend.CancelPendingWarehouseMove(row.Id.Substring(7));SetOperationText("路径迁移已取消，原仓库可继续使用。目标文件保留，待核对清单："+manifest,OperationState.Cancelled);Log("已取消仓库路径迁移："+row.Name);}
        private Task SaveDestinationFromEditorAsync(DestinationDefinition destination,string name,RemoteConnectionOptions options,bool migrate,Action<string> progress)
        {return RunOperationAsync(OperationKind.Configuration,()=>SaveDestinationFromEditorCoreAsync(destination,name,options,migrate,progress));}
        private async Task SaveDestinationFromEditorCoreAsync(DestinationDefinition destination,string name,RemoteConnectionOptions options,bool migrate,Action<string> progress)
        {
            var old=Backend.Catalog.LoadTopology().Destinations.FirstOrDefault(d=>d.Id==destination.Id);if(old==null||old.Location==destination.Location&&old.Protocol==destination.Protocol&&!Backend.Catalog.GetWarehouseMoves().Any(m=>m.DestinationId==destination.Id&&m.State!="complete")){AdoptWorkspace(Backend.SaveDestinationDefinition(Workspace,destination,name,options));ApplyCommittedChanges();return;}
            SetBusy(true,false,migrate?"正在迁移仓库到新路径…":"正在检查新仓库连接并切换位置…");var report=OperationReporter("");Action<string> notify=message=>{report(message);if(progress!=null)Window.Dispatcher.BeginInvoke(new Action(()=>progress(message)));};
            try{await Task.Run(()=>{if(migrate)Backend.MoveWarehouse(destination,name,options,notify);else Backend.SwitchWarehouseLocation(destination,name,options,notify);});SetOperationText(migrate?"仓库路径迁移完成：任务和备份记录已保留，新副本已验证，旧备份文件已清理。":"仓库位置已切换：未迁移文件，已有登记保留；缺失文件由完整性检查报告。",OperationState.Succeeded);Log((migrate?"仓库迁移完成：":"仓库位置已切换：")+name);OperationProgress(100,false);}finally{SetBusy(false,false,null);}
        }
    }
}
