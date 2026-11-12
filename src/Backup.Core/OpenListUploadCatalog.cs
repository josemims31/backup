using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
namespace Backup.Core
{
    public sealed class OpenListUploadTask
    {
        public string DestinationId,Name,Temporary,ApiBase,Path,TaskId,State,Error;
        public long Size,ProgressTicks;public double Progress;
    }
    public interface IOpenListUploadJournal
    {
        OpenListUploadTask FindOpenListUpload(string destination,string temporary);
        void SaveOpenListUpload(OpenListUploadTask task);
    }
    public interface IBackgroundUploadProgress
    {
        bool BackgroundUploadActive{get;}
        Action<TransferProgress> BackgroundProgress{get;set;}
    }
    public sealed class OpenListTaskPendingException:System.IO.IOException
    {public OpenListTaskPendingException(string message,Exception inner=null):base(message,inner){}}
    public sealed class OpenListTaskQueryException:System.IO.IOException
    {public OpenListTaskQueryException(string message,Exception inner=null):base(message,inner){}}
    public sealed partial class BackupCatalog:IOpenListUploadJournal
    {
        private void InitializeOpenListUploads()
        {database.Execute("CREATE TABLE IF NOT EXISTS openlist_upload_tasks(destination_id TEXT NOT NULL,temporary_name TEXT NOT NULL,name TEXT NOT NULL,api_base TEXT NOT NULL,remote_path TEXT NOT NULL,task_id TEXT NOT NULL,expected_size INTEGER NOT NULL,progress TEXT NOT NULL,progress_ticks INTEGER NOT NULL,state TEXT NOT NULL,PRIMARY KEY(destination_id,temporary_name),FOREIGN KEY(destination_id,temporary_name) REFERENCES upload_temporaries(destination_id,temporary_name) ON DELETE CASCADE)");}
        public OpenListUploadTask FindOpenListUpload(string destination,string temporary)
        {lock(gate){var rows=database.Query("SELECT name,api_base,remote_path,task_id,expected_size,progress,progress_ticks,state FROM openlist_upload_tasks WHERE destination_id=? AND temporary_name=?",destination,temporary);if(rows.Count==0)return null;var r=rows[0];return new OpenListUploadTask{DestinationId=destination,Temporary=temporary,Name=(string)r[0],ApiBase=(string)r[1],Path=(string)r[2],TaskId=(string)r[3],Size=(long)r[4],Progress=double.Parse((string)r[5],CultureInfo.InvariantCulture),ProgressTicks=(long)r[6],State=(string)r[7]};}}
        public void SaveOpenListUpload(OpenListUploadTask task)
        {lock(gate){if(!UploadStaging.IsOwnedTemporary(task.Name,task.Temporary)||!UploadStages(task.DestinationId,task.Name).Contains(task.Temporary))throw new System.IO.IOException("OpenList 上传任务缺少临时文件所有权记录。");database.Execute("INSERT OR REPLACE INTO openlist_upload_tasks(destination_id,temporary_name,name,api_base,remote_path,task_id,expected_size,progress,progress_ticks,state) VALUES(?,?,?,?,?,?,?,?,?,?)",task.DestinationId,task.Temporary,task.Name,task.ApiBase,task.Path,task.TaskId??"",task.Size,task.Progress.ToString("R",CultureInfo.InvariantCulture),task.ProgressTicks,task.State??"submitting");}}
    }
}
