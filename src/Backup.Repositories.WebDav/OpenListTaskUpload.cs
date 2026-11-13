using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;

namespace Backup.Core
{
    public sealed partial class WebDavDestination : IBackgroundUploadProgress
    {
        private bool backgroundActive;
        public bool BackgroundUploadActive
        {
            get
            {
                return backgroundActive;
            }
        }

        public Action<TransferProgress> BackgroundProgress { get; set; }

        private IOpenListUploadJournal NativeJournal
        {
            get
            {
                return staging.Journal as IOpenListUploadJournal;
            }
        }

        private OpenListUploadTask NativeTask(string temporary)
        {
            return NativeJournal == null ? null : NativeJournal.FindOpenListUpload(Id, temporary);
        }

        private void SaveNative(OpenListUploadTask task)
        {
            if (NativeJournal == null)
                throw new IOException("OpenList 上传任务需要本地上传日志。");
            NativeJournal.SaveOpenListUpload(task);
        }

        private IEnumerable<OpenListUploadTask> NativeTasks(string name)
        {
            var names = staging.Journal == null ? new List<string>() : staging.Journal.UploadStages(Id, name);
            return names.Select(NativeTask).Where(t => t != null).ToList();
        }

        private void ValidateNative(OpenListUploadTask task)
        {
            if (task.DestinationId != Id || !UploadStaging.IsOwnedTemporary(task.Name, task.Temporary) || task.ApiBase != OpenListBaseFor(root) ||
                task.Path != OpenListFilePath(ObjectUri(task.Temporary), task.ApiBase))
                throw new OpenListTaskPendingException("OpenList 原上传任务的位置与当前仓库不一致，已保留原任务，禁止重复上传。");
        }

        private void NativeProgress(OpenListUploadTask task, string state)
        {
            var report = BackgroundProgress;
            if (report != null)
                report(new TransferProgress {
                    DestinationId = Id,
                    ObjectName = task.Name,
                    Size = task.Size,
                    Written = (long)Math.Min(task.Size, Math.Max(0, task.Size * task.Progress / 100)),
                    State = "background-uploading",
                    TransferStage = "OpenList 上传到云端",
                    BackgroundState = state
                });
        }

        private void ObserveNative(OpenListUploadTask task, OpenListTaskInfo info)
        {
            if (info == null)
                throw new OpenListTaskQueryException("OpenList 返回空任务信息，未取得 state。");
            if (string.IsNullOrEmpty(info.Id) || !string.IsNullOrEmpty(task.TaskId) && task.TaskId != info.Id || info.Name == null ||
                !info.Name.StartsWith("upload " + task.Temporary + " to [", StringComparison.Ordinal) ||
                info.Size != task.Size)
                throw new OpenListTaskQueryException(info.Diagnostic + "；任务编号、名称或大小与原上传不一致，已保留原记录。");
            if (info.Status == "unknown")
            {
                if (string.IsNullOrEmpty(task.TaskId))
                {
                    task.TaskId = info.Id;
                    task.State = "query-error";
                    SaveNative(task);
                }

                throw new OpenListTaskQueryException(info.Diagnostic + "；任务状态缺失或无法识别。");
            }

            if (double.IsNaN(info.Progress) || double.IsInfinity(info.Progress))
                throw new OpenListTaskQueryException(info.Diagnostic + "；任务进度无效。");
            double value = Math.Max(0, Math.Min(100, info.Progress));
            string state = task.State == "renaming" && info.Status == "succeeded" ? "renaming" : info.Status == "running" && task.State == "canceling" ? "canceling" : info.Status;
            bool changed = task.TaskId != info.Id || task.State != state || value != task.Progress;
            if (task.TaskId != info.Id || value > task.Progress)
                task.ProgressTicks = DateTime.UtcNow.Ticks;
            task.Progress = value;
            task.Error = info.Diagnostic;
            task.TaskId = info.Id;
            task.State = state;
            if (changed)
            {
                SaveNative(task);
                if (state == "succeeded")
                    DirectoryRefresh.InvalidateFolder(root.AbsoluteUri);
            }

            NativeProgress(task, info.ProgressState);
        }

        private OpenListTaskInfo QueryNative(OpenListUploadTask task)
        {
            ValidateNative(task);
            if (!string.IsNullOrEmpty(task.TaskId))
            {
                try
                {
                    return OpenListCall<OpenListTaskInfo>(task.ApiBase, "task/upload/info?tid=" + Uri.EscapeDataString(task.TaskId), "POST", null, true, OperationCancellation);
                }
                catch (OpenListApiException error)
                {
                    if (error.Code != 404)
                        throw;
                    task.State = "not-found";
                    task.Error = error.Message + "；原上传任务已不存在，只核对正式文件，旧临时文件不再提交。";
                    SaveNative(task);
                    return null;
                }
            }

            // Only an interrupted submission needs task-list discovery. The owned
            // random temporary name identifies the intent even if its reply was lost.
            var pending = OpenListCall<List<OpenListTaskInfo>>(task.ApiBase, "task/upload/undone", "GET", null, true, OperationCancellation) ?? new List<OpenListTaskInfo>();
            var done = OpenListCall<List<OpenListTaskInfo>>(task.ApiBase, "task/upload/done", "GET", null, true, OperationCancellation) ?? new List<OpenListTaskInfo>();
            var matches = pending.Concat(done).Where(t => t != null && t.Name != null && t.Name.StartsWith("upload " + task.Temporary + " to [", StringComparison.Ordinal)).GroupBy(t => t.Id, StringComparer.Ordinal)
                .Select(g => g.Last())
                .ToList();
            if (matches.Count > 1)
                throw new OpenListTaskQueryException(matches[0].Diagnostic + "；同一临时文件存在多个任务，保留原记录并核对文件。");
            if (matches.Count == 1)
                return matches[0];
            // No task ID was ever received and both task lists returned valid
            // success responses. File checks still run before any retransmission.
            task.State = "not-found";
            task.Error = "OpenList HTTP 200，code=200：任务列表未找到原上传任务，未取得 state。";
            SaveNative(task);
            return null;
        }

        private static bool NativeStalled(OpenListUploadTask task)
        {
            long now = DateTime.UtcNow.Ticks;
            return task.ProgressTicks > 0 && now >= task.ProgressTicks && (now - task.ProgressTicks) / TimeSpan.TicksPerMillisecond >= OpenListStallMilliseconds;
        }

        private string RefreshNative(OpenListUploadTask task)
        {
            if (UsingOpenListWebDavFallback)
                return RefreshNativeViaWebDav(task);
            // A confirmed terminal failure cannot reuse OpenList's cached stream.
            // Keep checking owned remote files, then send the retained local bytes
            // as a new task. Removal of the old server task must not block that retry.
            if (task.State == "failed" || task.State == "canceled" || task.State == "not-found")
            {
                OperationCancellation.ThrowIfCancellationRequested();
                ValidateNative(task);
                return task.State;
            }

            bool queried = false;
            try
            {
                var info = QueryNative(task);
                if (info == null)
                    return task.State;
                ObserveNative(task, info);
                queried = true;
                // A driver can remain at the same percentage while preparing or
                // transferring a chunk. Only a server terminal state permits retry;
                // lack of progress must never cancel a live task automatically.
                return task.State;
            }
            catch (OpenListAuthorizationException error)
            {
                if (UsingOpenListWebDavFallback)
                    return RefreshNativeViaWebDav(task);
                throw new OpenListTaskQueryException("OpenList 查询异常：" + error.Message, error);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (OpenListTaskPendingException)
            {
                throw;
            }
            catch (OpenListTaskQueryException)
            {
                throw;
            }
            catch (IOException error)
            {
                OperationCancellation.ThrowIfCancellationRequested();
                throw new OpenListTaskQueryException("OpenList 查询异常：" + error.Message, error);
            }
            finally
            {
                if (!queried)
                    NativeProgress(task, null);
            }
        }

        private void WaitNative(OpenListUploadTask task)
        {
            try
            {
                while (true)
                {
                    OperationCancellation.ThrowIfCancellationRequested();
                    string state;
                    try
                    {
                        state = RefreshNative(task);
                    }
                    catch (OpenListTaskPendingException)
                    {
                        if (NativeStalled(task))
                            throw;
                        state = "running";
                    }

                    if (state == "succeeded")
                        return;
                    if (state == "failed" || state == "canceled" || state == "not-found")
                        throw new IOException(string.IsNullOrEmpty(task.Error) ? "OpenList 后台上传失败或取消，进入三次核对。" : task.Error);
                    // Keep the existing upload lane while the server owns this
                    // task; otherwise slow workers could accumulate new tasks.
                    int delay = UsingOpenListWebDavFallback || NativeStalled(task) ? UploadRetryState.IntervalMilliseconds : OpenListPollMilliseconds;
                    if (OperationCancellation.WaitHandle.WaitOne(delay))
                        OperationCancellation.ThrowIfCancellationRequested();
                }
            }
            catch (OperationCanceledException)
            {
                // The client can stop immediately. Keep the journal until a later
                // check confirms the server's cancellation or completed object.
                if (!string.IsNullOrEmpty(task.TaskId))
                    try
                    {
                        using (var stop = new CancellationTokenSource(3000))
                        {
                            OpenListCall<object>(task.ApiBase, "task/upload/cancel?tid=" + Uri.EscapeDataString(task.TaskId), "POST", null, true, stop.Token);
                            task.State = "canceling";
                            SaveNative(task);
                        }
                    }
                    catch (IOException diagnosticFailure)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Repositories.WebDav/OpenListTaskUpload.cs:WaitNative", diagnosticFailure);
                    }
                    catch (OperationCanceledException)
                    { // Expected cancellation, shutdown, or a deliberately rejected identity probe.
                    }

                throw;
            }
        }

        private Stream ResumeNative(string name, long length)
        {
            foreach (var task in NativeTasks(name))
            {
                if (task.Size != length)
                    throw new OpenListTaskPendingException("原 OpenList 上传任务大小与当前文件不一致，已保留原任务。");
                string state = task.State == "succeeded" || task.State == "renaming" ? task.State : RefreshNative(task);
                if (state == "not-found")
                {
                    if (Stat(name).Exists)
                        throw new IOException("正式文件已存在，正在核对名称和大小。");
                    continue;
                }

                backgroundActive = true;
                if (state != "succeeded" && state != "renaming" && state != "failed" && state != "canceled")
                    WaitNative(task);
                if (UsingOpenListWebDavFallback)
                {
                    var formal = FallbackFile(FallbackDirectory(), name);
                    if (formal.Exists && formal.Size == length)
                    {
                        staging.Resume(name, task.Temporary);
                        return new CompletedNativeStream(length);
                    }
                }

                // A failed task can still have left a complete cloud object. Reuse
                // only that owned object; never ask the failed task to run again.
                var existing = Stat(task.Temporary);
                if (!existing.Exists || existing.Size != length)
                    continue;
                staging.Resume(name, task.Temporary);
                return new CompletedNativeStream(length);
            }

            return null;
        }

        private Stream OpenNative(string name, long length)
        {
            backgroundActive = false;
            var resumed = ResumeNative(name, length);
            if (resumed != null)
                return resumed;
            if (UsingOpenListWebDavFallback || NativeJournal == null || connection.OpenListUploadOptimization == false || !DetectOpenList())
                return null;
            try
            {
                EnsureOpenListAuthorization();
            }
            catch (OpenListAuthorizationException)
            {
                if (UsingOpenListWebDavFallback)
                    return null;
                throw;
            }

            string temporary = staging.Begin(name);
            var task = new OpenListUploadTask
            {
                DestinationId = Id,
                Name = name,
                Temporary = temporary,
                ApiBase = openListBase,
                Path = OpenListFilePath(ObjectUri(temporary), openListBase),
                TaskId = "",
                Size = length,
                ProgressTicks = DateTime.UtcNow.Ticks,
                State = "submitting"
            };
            SaveNative(task);
            var lease = OpenListRequest(openListBase, "fs/put", "PUT", true, OperationCancellation);
            var request = lease.Request;
            string requestToken = request.Headers[HttpRequestHeader.Authorization];
            request.Timeout = Timeout.Infinite;
            request.ReadWriteTimeout = Timeout.Infinite;
            request.ContentType = "application/octet-stream";
            request.ContentLength = length;
            request.Headers["File-Path"] = Uri.EscapeDataString(task.Path);
            request.Headers["As-Task"] = "true";
            request.Headers["Overwrite"] = "false";
            var activity = new TransferActivity(request.Abort, idleTimeout, TimeSpan.FromSeconds(RemoteConnectionPolicy.UploadResponseSeconds));
            try
            {
                var output = request.GetRequestStream();
                activity.Progress();
                activity.Stage = "传输数据";
                backgroundActive = true;
                long sent = 0, lastProgress = 0;
                return BandwidthStream.Wrap(
                    new ResponseStream(
                    output,
                    () =>
                {
                    try
                    {
                        using (var response = Response(lease, false, activity))
                        using (var input = response.GetResponseStream())
                        {
                            var reply = OpenListResult(ReadOpenList<OpenListUploadReply>(input, OperationCancellation, (int)response.StatusCode), (int)response.StatusCode);
                            if (reply == null || reply.Task == null)
                                throw new OpenListTaskQueryException("OpenList HTTP " + (int)response.StatusCode + "，code=200：上传回应缺少 task，未取得任务编号和 state，进入三次核对。");
                            ObserveNative(task, reply.Task);
                        }
                    }
                    catch (OpenListAuthorizationException error)
                    {
                        RejectOpenListToken(task.ApiBase, requestToken, error);
                        if (error.TokenRejected)
                            RenewRejectedOpenListAuthorization();
                        throw new OpenListTaskQueryException(error.Message, error);
                    }
                    catch (DavException error)
                    {
                        if (error.Code != 401 && error.Code != 403)
                            throw;
                        var failure = OpenListHttpAuthorizationFailure(error.Code);
                        RejectOpenListToken(task.ApiBase, requestToken, failure);
                        if (failure.TokenRejected)
                            RenewRejectedOpenListAuthorization();
                        throw new OpenListTaskQueryException(failure.Message, failure);
                    }

                    // The request's response timer ends here. The independent
                    // cloud stage polls more slowly after six idle minutes.
                    activity.Dispose();
                    if (task.State != "succeeded")
                        WaitNative(task);
                },
                    request.Abort,
                    length,
                    lease,
                    activity,
                    error =>
                {
                    lease.Failure(error);
                    ReportUploadFailure(name, length, activity, error);
                },
                    -1,
                    OperationCancellation
                ),
                    Bandwidth,
                    true,
                    OperationCancellation,
                    activity.RateWaiting,
                    true,
                    bytes =>
                {
                    sent += bytes;
                    long now = Stopwatch.GetTimestamp();
                    if (sent != length && lastProgress != 0 && now - lastProgress < Stopwatch.Frequency / 8)
                        return;
                    lastProgress = now;
                    var report = BackgroundProgress;
                    if (report != null)
                        report(new TransferProgress { DestinationId = Id, ObjectName = name, Written = sent, Size = length, State = "uploading", TransferStage = "发送至 OpenList" });
                }
                );
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Repositories.WebDav/OpenListTaskUpload.cs:OpenNative", error);
                lease.Failure(error);
                request.Abort();
                activity.Dispose();
                lease.Dispose();
                OperationCancellation.ThrowIfCancellationRequested();
                throw new OpenListTaskPendingException("OpenList 上传提交状态尚待核对，已保存原任务记录。", error);
            }
        }

        private void CheckNativeTemporary(string name)
        {
            var task = NativeTask(name);
            if (task == null || task.State == "succeeded" || task.State == "renaming")
                return;
            string state = RefreshNative(task);
            if (state == "running" || state == "submitting" || state == "canceling")
                throw new OpenListTaskPendingException("OpenList 仍在处理此文件，保留原任务继续核对。");
        }

        private RemoteObjectInfo StatCompletedNative(string name, bool formalOnly = false)
        {
            var task = NativeTask(name) ?? NativeTasks(name).FirstOrDefault(value => formalOnly || value.State == "succeeded" || value.State == "renaming" || value.State == "not-found");
            if (task == null || !formalOnly && task.State != "succeeded" && task.State != "renaming" && task.State != "not-found")
                return null;
            ValidateNative(task);
            string context = task.State == "not-found" ? "OpenList 原上传任务已不存在" : task.State == "succeeded" || task.State == "renaming" ? "OpenList 已完成上传" : "OpenList 正在核对正式文件";
            if (UsingOpenListWebDavFallback)
                return FallbackFile(FallbackDirectory(), name);
            try
            {
                var entries = DirectoryRefresh.Read(
                    DirectoryScope,
                    root.AbsoluteUri,
                    true,
                    () =>
                {
                    var listing = OpenListCall<OpenListFileList>(task.ApiBase, "fs/list", "POST", new OpenListListRequest { Path = task.Path.Substring(0, task.Path.LastIndexOf('/')) }, true, OperationCancellation);
                    if (listing == null || listing.Content == null || listing.Total < 0 || listing.Total > listing.Content.Count ||
                        listing.Content.Any(value => value == null || string.IsNullOrEmpty(value.Name)))
                        throw new OpenListTaskQueryException("OpenList 当前目录返回的文件清单不完整，保留原上传记录。");
                    return listing.Content.Where(value => value.Name != "." && value.Name != ".." && value.Name.IndexOfAny(new[] { '/', '\\', ':', '\0' }) < 0).Select(value => new StorageEntry {
                        Name = value.Name,
                        Location = RemotePath.Append(root.AbsoluteUri, value.Name),
                        Directory = value.Directory,
                        Size = value.Size
                    })
                        .ToList();
                },
                    OperationCancellation
                );
                var files = entries.Where(value => value.Name == name).ToList();
                if (files.Count > 1)
                    throw new OpenListTaskQueryException(context + "，但当前目录存在多个同名文件，保留原上传记录。");
                if (files.Count == 0)
                    return new RemoteObjectInfo
                    {
                        Name = name,
                        Exists = false
                    };
                if (files[0].Directory || files[0].Size < 0)
                    throw new OpenListTaskQueryException(context + "，但文件类型或大小无效，保留原上传记录。");
                return new RemoteObjectInfo
                {
                    Name = name,
                    Exists = true,
                    Size = files[0].Size
                };
            }
            catch (OpenListAuthorizationException error)
            {
                if (UsingOpenListWebDavFallback)
                    return FallbackFile(FallbackDirectory(), name);
                throw new OpenListTaskQueryException(context + "，正在核对当前目录：" + error.Message, error);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (OpenListTaskQueryException)
            {
                throw;
            }
            catch (IOException error)
            {
                throw new OpenListTaskQueryException(context + "，正在核对当前目录：" + error.Message, error);
            }
        }

        private bool CommitNative(string name)
        {
            string temporary = staging.Current(name);
            var task = NativeTask(temporary);
            if (task == null)
                return false;
            if (UsingOpenListWebDavFallback)
                return CommitNativeViaWebDav(name, task);
            CheckNativeTemporary(temporary);
            if (task.State == "not-found")
                throw new IOException("原上传任务已不存在，旧临时文件不再提交，请清理后重新上传。");
            var existing = Stat(name);
            if (existing.Exists)
            {
                if (task.State == "renaming" && existing.Size == task.Size)
                    return true;
                throw new BackupObjectConflictException();
            }

            task.State = "renaming";
            SaveNative(task);
            DirectoryRefresh.InvalidateFolder(root.AbsoluteUri);
            try
            {
                OpenListCall<object>(task.ApiBase, "fs/rename", "POST", new OpenListRename { Path = task.Path, Name = name }, true, OperationCancellation);
                return true;
            }
            finally
            {
                DirectoryRefresh.InvalidateFolder(root.AbsoluteUri);
            }
        }

        private void CancelNativeBeforeDelete(string temporary)
        {
            var task = NativeTask(temporary);
            if (task == null)
                return;
            string state = RefreshNative(task);
            if (state != "running" && state != "submitting" && state != "canceling")
                return;
            if (state != "canceling" && !string.IsNullOrEmpty(task.TaskId))
            {
                OpenListCall<object>(task.ApiBase, "task/upload/cancel?tid=" + Uri.EscapeDataString(task.TaskId), "POST", null, true, OperationCancellation);
                task.State = "canceling";
                SaveNative(task);
            }

            CheckNativeTemporary(temporary);
        }

        private void DiscardNativeTemporary(string temporary)
        {
            if (UsingOpenListWebDavFallback)
                return;
            var task = NativeTask(temporary);
            if (task == null)
                return;
            ValidateNative(task);
            if (string.IsNullOrEmpty(task.TaskId) || task.State == "succeeded" || task.State == "renaming" || task.State == "failed" || task.State == "canceled" ||
                task.State == "not-found")
                return;
            // Explicit stopped-upload cleanup does not inspect or commit temporary
            // bytes. Cancel only the exact recorded task, then delete its object.
            try
            {
                OpenListCall<object>(task.ApiBase, "task/upload/cancel?tid=" + Uri.EscapeDataString(task.TaskId), "POST", null, true, OperationCancellation);
            }
            catch (OpenListApiException error)
            {
                if (error.Code != 404)
                    throw;
            }
        }

        private void ForgetNativeServerTask(string temporary)
        {
            if (UsingOpenListWebDavFallback)
                return;
            var task = NativeTask(temporary);
            if (task == null || string.IsNullOrEmpty(task.TaskId))
                return;
            if (task.State == "running" || task.State == "submitting" || task.State == "canceling")
                return;
            try
            {
                OpenListCall<object>(task.ApiBase, "task/upload/delete?tid=" + Uri.EscapeDataString(task.TaskId), "POST", null, true, OperationCancellation);
            }
            catch (IOException diagnosticFailure)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Repositories.WebDav/OpenListTaskUpload.cs:ForgetNativeServerTask", diagnosticFailure);
            }
        }

        private void FinishNative(string name)
        {
            if (staging.HasActive(name))
                ForgetNativeServerTask(staging.Current(name));
        }

        private sealed class CompletedNativeStream : Stream
        {
            private readonly long size;
            private long written;
            internal CompletedNativeStream(long value)
            {
                size = value;
            }

            public override bool CanRead
            {
                get
                {
                    return false;
                }
            }

            public override bool CanSeek
            {
                get
                {
                    return false;
                }
            }

            public override bool CanWrite
            {
                get
                {
                    return true;
                }
            }

            public override long Length
            {
                get
                {
                    return size;
                }
            }

            public override long Position
            {
                get
                {
                    return written;
                }

                set
                {
                    throw new NotSupportedException();
                }
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (buffer == null || offset < 0 || count < 0 || offset > buffer.Length - count || written + count > size)
                    throw new ArgumentException("上传大小不一致。");
                written += count;
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && written != size)
                    throw new IOException("原上传任务已完成，但当前分卷长度不一致。");
                base.Dispose(disposing);
            }
        }
    }
}
