using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
namespace Backup.Core
{
    internal sealed class DeferredUploadRetryException:IOException
    {internal readonly int Delay;internal DeferredUploadRetryException(int delay):base("等待下一次上传核对。"){Delay=delay;}}
    public sealed class UploadRetriesExhaustedException:IOException
    {public UploadRetriesExhaustedException(Exception error):base("已完成三轮重传，每轮失败后均核对三次，上传仍未成功。",error){}}
    internal sealed class UploadRetryState:IDisposable
    {
        internal static int IntervalMilliseconds=20000;
        internal static int CheckTimeoutMilliseconds=15000;
        internal int Retries,Checks;internal bool Checking,UnownedConflict,HasSent,BackgroundPending,CheckUncertain,DirectoryPending;internal Exception Error;
        private long nextCheck;
        internal void ScheduleCheck(){nextCheck=Stopwatch.GetTimestamp()+(long)Math.Ceiling(IntervalMilliseconds*(double)Stopwatch.Frequency/1000);}
        internal int CheckDelay{get{return (int)Math.Min(int.MaxValue,Math.Max(0,Math.Ceiling((nextCheck-Stopwatch.GetTimestamp())*1000.0/Stopwatch.Frequency)));}}
        private IDisposable claim;internal BackupCatalog Catalog;
        internal void Bind(IBackupDestination destination,string name)
        {if(claim!=null)return;var owned=destination as IOwnedUploadDestination;Catalog=owned==null?null:owned.UploadJournal as BackupCatalog;if(Catalog!=null)claim=Catalog.ClaimUploadRetry(destination.Id,name);}
        public void Dispose(){var value=Interlocked.Exchange(ref claim,null);if(value!=null)value.Dispose();}
    }
    public sealed partial class BackupCatalog
    {
        private readonly HashSet<string> managedUploadRetries=new HashSet<string>();
        internal IDisposable ClaimUploadRetry(string destination,string name)
        {string key=destination+"\0"+name;lock(gate){if(!managedUploadRetries.Add(key))throw new IOException("同一仓库的此分卷正在核对或重传。");}return new CatalogLease(delegate{lock(gate)managedUploadRetries.Remove(key);});}
        public bool OwnsUploadRetry(string destination,string name){lock(gate)return managedUploadRetries.Contains(destination+"\0"+name);}
        internal void RetainRetryUpload(string destination,string name)
        {lock(gate){var rows=database.Query("SELECT archive_id FROM objects WHERE name=?",name);if(rows.Count>0)MarkUploadConfirmationPending((long)rows[0][0],name,destination);}}
        public void MarkVolumeUploadFailed(ArchivePlan archive,ArchivePlacement place,ObjectRecord item,string error)
        {Transaction(delegate{
            if(Scalar("SELECT count(*) FROM volume_transfers WHERE name=? AND destination_id=? AND state='failed-upload'",item.Name,place.DestinationId)>0)return 0;
            database.Execute("UPDATE placements SET reserved=MAX(0,reserved-?),status=CASE WHEN reserved-?>0 THEN 'reconcile-required' ELSE 'repair-required' END,error=? WHERE archive_id=? AND group_id=? AND destination_id=?",item.Size,item.Size,error??"上传失败",archive.ArchiveId,place.GroupId,place.DestinationId);
            database.Execute("UPDATE volume_transfers SET state='failed-upload' WHERE name=? AND destination_id=?",item.Name,place.DestinationId);RepositoryStatusChanged(CatalogChange.Archives,archive.TaskId);return 0;
        });}
        private void ForgetFailedNewArchiveUnsafe(long id,string taskId)
        {
            if(IsNodeArchive(id)||Scalar("SELECT count(*) FROM objects o JOIN volume_transfers t ON t.name=o.name WHERE o.archive_id=? AND t.state='failed-upload'",id)==0)return;
            UnregisterUnusedArchiveUnsafe(id,taskId);
            database.Execute("UPDATE archives SET status='failed' WHERE id=?",id);
            database.Execute("INSERT OR REPLACE INTO archive_cleanup(archive_id,state,error) VALUES(?,'upload-failed','重传次数已耗尽，未形成完整可恢复备份，已移除本次引用。')",id);
        }
    }
    internal static class UploadVerificationRetry
    {
        internal static bool Check(IBackupDestination destination,ObjectRecord record,BackupControl control,Action<string> phase,out string detail,UploadRetryState state)
        {
            string result=null;state.BackgroundPending=false;state.CheckUncertain=false;
            Func<CancellationToken,bool> query=token=>{
                var cancellable=destination as ICancellableBackupDestination;var previous=cancellable==null?CancellationToken.None:cancellable.OperationCancellation;
                // BackupControl supplies a fresh I/O token. The adapter may still
                // hold the disposed token of the preceding upload attempt.
                var inherited=control==null?previous:CancellationToken.None;
                using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token,inherited)){
                    state.ScheduleCheck();
                    deadline.CancelAfter(UploadRetryState.CheckTimeoutMilliseconds);
                    try{if(cancellable!=null)cancellable.OperationCancellation=deadline.Token;bool confirmed=Probe(destination,record,control,phase,out result,state.UnownedConflict,deadline.Token);deadline.Token.ThrowIfCancellationRequested();return confirmed;}
                    catch(OpenListTaskPendingException pending){state.BackgroundPending=true;result=pending.Message;return false;}catch(OpenListTaskQueryException error){state.CheckUncertain=true;result="核对异常："+error.Message;return false;}catch(OperationCanceledException){token.ThrowIfCancellationRequested();inherited.ThrowIfCancellationRequested();if(!deadline.IsCancellationRequested)throw;result="本次核对超时，暂时无法确认文件是否存在";return false;}
                    finally{if(cancellable!=null)cancellable.OperationCancellation=previous;}
                }
            };
            bool found=control==null?query(CancellationToken.None):control.Transfer(query);detail=result;return found;
        }
        internal static bool Probe(IBackupDestination destination,ObjectRecord record,BackupControl control,Action<string> phase,out string detail,bool rejectFinal=false,CancellationToken cancellation=default(CancellationToken))
        {
            string observed="未找到正式文件";
            try{
                cancellation.ThrowIfCancellationRequested();
                var info=destination.Stat(record.Name);if(info.Exists){if(rejectFinal){detail="目标存在上传前的同名文件，保留原文件";return false;}if(info.Name==record.Name&&info.Size==record.Size){detail="正式文件名称和大小匹配";return true;}observed="正式文件名称或大小不符";}
                var owned=destination as IOwnedUploadDestination;var recover=destination as IRecoverableUploadDestination;
                var temporaries=owned==null||owned.UploadJournal==null?new List<string>():owned.UploadJournal.UploadStages(destination.Id,record.Name);
                foreach(string temporary in temporaries){
                    if(!UploadStaging.IsOwnedTemporary(record.Name,temporary))continue;cancellation.ThrowIfCancellationRequested();if(control!=null)control.Checkpoint(false);
                    var temp=destination.Stat(temporary);if(!temp.Exists)continue;
                    if(temp.Size!=record.Size){observed="临时文件尚未完整（"+temp.Size+" / "+record.Size+" 字节）";continue;}
                    if(recover==null){observed="完整临时文件等待提交";continue;}
                    cancellation.ThrowIfCancellationRequested();recover.ResumeTemporary(record.Name,temporary);if(phase!=null)phase("committing");destination.CommitTemporary(record.Name);
                    info=destination.Stat(record.Name);if(info.Exists&&info.Name==record.Name&&info.Size==record.Size){detail="完整临时文件已提交，名称和大小匹配";return true;}
                    observed="临时文件已提交，正式文件尚不可见";break;
                }
                if(temporaries.Count==0&&owned!=null&&owned.UploadJournal==null){var probe=destination as ITemporaryUploadProbe;if(probe!=null){var temp=probe.StatTemporary(record.Name);if(temp!=null&&temp.Exists&&temp.Size==record.Size){if(phase!=null)phase("committing");destination.CommitTemporary(record.Name);info=destination.Stat(record.Name);if(info.Exists&&info.Name==record.Name&&info.Size==record.Size){detail="完整临时文件已提交，名称和大小匹配";return true;}}}}
                detail=observed;return false;
            }catch(OpenListTaskPendingException){throw;}catch(OpenListTaskQueryException){throw;}catch(OperationCanceledException){throw;}
            catch(Exception error){if(!(error is IOException)&&!(error is UnauthorizedAccessException)&&!(error is NotSupportedException))throw;detail="暂时无法确认："+error.Message;return false;}
        }
        internal static void Suspend(IBackupDestination destination,string name)
        {var interrupted=destination as IInterruptedUploadDestination;if(interrupted!=null)interrupted.SuspendTemporary(name);}
        internal static void Wait(BackupControl control,int delay)
        {if(control==null){Thread.Sleep(delay);return;}if(control.Token.WaitHandle.WaitOne(delay))control.Checkpoint(false);control.Checkpoint();}
    }
}
