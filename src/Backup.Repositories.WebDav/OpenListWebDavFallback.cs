using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace Backup.Core
{
    public sealed partial class WebDavDestination
    {
        private IList<StorageEntry> FallbackDirectory()
        {
            return DirectoryRefresh.Read(DirectoryScope,root.AbsoluteUri,true,()=>{
                long size;bool folder;var entries=new List<StorageEntry>();Probe(root,1,out size,out folder,entries);if(!folder)throw new IOException("WebDAV 仓库不是文件夹。");return entries;
            },OperationCancellation);
        }
        private static RemoteObjectInfo FallbackFile(IList<StorageEntry> entries,string name)
        {
            var matches=entries.Where(e=>e.Name==name).ToList();if(matches.Count>1)throw new OpenListTaskPendingException("当前目录存在同名文件，保留原上传记录继续核对。");
            if(matches.Count==0)return new RemoteObjectInfo{Name=name,Exists=false};
            if(matches[0].Directory||matches[0].Size<0)throw new OpenListTaskPendingException("仓库未提供可靠的文件类型或大小，保留原上传记录。");
            return new RemoteObjectInfo{Name=name,Exists=true,Size=matches[0].Size};
        }
        private string RefreshNativeViaWebDav(OpenListUploadTask task)
        {
            ValidateNative(task);var entries=FallbackDirectory();var formal=FallbackFile(entries,task.Name);
            if(formal.Exists)
            {
                if(formal.Size!=task.Size)throw new OpenListTaskPendingException("正式文件大小与原上传不一致，保留原任务，禁止重复上传。");
                if(task.State!="renaming"){task.State="renaming";SaveNative(task);}return "succeeded";
            }
            // A known terminal outcome retains the existing retry rules. Merely
            // losing API authorization never creates such an outcome.
            if(task.State=="not-found")return task.State;
            var temporary=FallbackFile(entries,task.Temporary);
            if(temporary.Exists&&temporary.Size==task.Size)
            {if(task.State!="succeeded"){task.State="succeeded";SaveNative(task);}return "succeeded";}
            if(task.State=="failed"||task.State=="canceled")return task.State;
            NativeProgress(task,"授权待恢复，正在通过 WebDAV 核对原文件");return "running";
        }
        private bool CommitNativeViaWebDav(string name,OpenListUploadTask task)
        {
            if(RefreshNativeViaWebDav(task)!="succeeded")throw new OpenListTaskPendingException("原 OpenList 文件仍待完成，保留原任务，通过 WebDAV 继续核对，不重复上传。");
            var formal=FallbackFile(FallbackDirectory(),name);
            if(formal.Exists){if(formal.Size!=task.Size)throw new BackupObjectConflictException();return true;}
            // The caller performs its existing same-directory WebDAV rename.
            return false;
        }
    }
}
