using System;
using System.Collections.Generic;
using System.Linq;
using Backup.Core;

namespace Backup.Desktop
{
    // Presentation only: processing text and active upload snapshots stay separate.
    internal sealed class BackupPipelineView
    {
        private sealed class Upload
        {
            internal string Name, Stage, Destination, Label, BackgroundState;
            internal long Written, Size, Order, BackgroundWritten, BackgroundSize;
        }

        private readonly object gate = new object();
        private readonly Dictionary<string, Upload> uploads = new Dictionary<string, Upload>();
        private string archive;
        private long order;
        private readonly Dictionary<string, Upload> downloads = new Dictionary<string, Upload>();
        private long downloadOrder;
        internal void Clear()
        {
            lock (gate)
            {
                uploads.Clear();
                downloads.Clear();
                archive = null;
                order = downloadOrder = 0;
            }
        }

        internal IList<TransferProgress> ActiveUploads()
        {
            lock (gate)
                return uploads.Values.OrderBy(value => value.Order).Where(value => new[] { "opening-upload", "uploading", "committing-upload", "verifying-upload" }.Contains(value.Stage)).Select(value => new TransferProgress {
                    ObjectName = value.Name,
                    Written = value.Written,
                    Size = value.Size,
                    State = value.Stage,
                    TransferStage = value.Label,
                    BackgroundState = value.BackgroundState,
                    BackgroundWritten = value.BackgroundWritten,
                    BackgroundSize = value.BackgroundSize
                })
                    .ToList();
        }

        internal IList<TransferProgress> ActiveDownloads()
        {
            lock (gate)
                return downloads.Values.OrderBy(value => value.Order).Select(value => new TransferProgress {
                    ObjectName = value.Name,
                    DestinationId = value.Destination,
                    DestinationLabel = value.Label,
                    Written = value.Written,
                    Size = value.Size,
                    State = value.Stage
                })
                    .ToList();
        }

        internal void Observe(TransferProgress progress)
        {
            lock (gate)
            {
                if (progress.State == "archive-compressing")
                {
                    archive = progress.ObjectName;
                    return;
                }

                if (progress.State == "archive-ready" || progress.State == "archive-aborted")
                {
                    if (archive == progress.ObjectName)
                        archive = null;
                    return;
                }

                if (string.IsNullOrEmpty(progress.DestinationId) || string.IsNullOrEmpty(progress.ObjectName))
                    return;
                string key = progress.DestinationId + "\0" + progress.ObjectName;
                if (progress.State == "background-uploading")
                {
                    Upload cloud;
                    if (!uploads.TryGetValue(key, out cloud))
                    {
                        if (string.IsNullOrEmpty(progress.BackgroundState))
                            return;
                        cloud = new Upload
                        {
                            Name = progress.ObjectName,
                            Order = ++order,
                            Stage = "opening-upload",
                            Size = progress.Size
                        };
                        uploads.Add(key, cloud);
                    }

                    cloud.BackgroundState = progress.BackgroundState;
                    cloud.BackgroundWritten = progress.Written;
                    cloud.BackgroundSize = progress.Size;
                    return;
                }

                if (progress.State == "download-finished")
                {
                    downloads.Remove(key);
                    return;
                }

                if (progress.State == "download-started" || progress.State == "downloading")
                {
                    Upload download;
                    if (!downloads.TryGetValue(key, out download))
                    {
                        download = new Upload
                        {
                            Name = progress.ObjectName,
                            Destination = progress.DestinationId,
                            Order = ++downloadOrder
                        };
                        downloads.Add(key, download);
                    }

                    download.Label = progress.DestinationLabel;
                    download.Stage = progress.State;
                    if (progress.Size > 0)
                        download.Size = progress.Size;
                    download.Written = progress.Written;
                    return;
                }

                if (new[]
                {
                    "copy-complete",
                    "destination-unavailable",
                    "upload-cancelled",
                    "confirmation-pending",
                    "confirmation-succeeded",
                    "upload-failed",
                    "copy-missing",
                    "archive-upload-failed",
                    "retry-succeeded"
                }.Contains(progress.State))
                {
                    uploads.Remove(key);
                    return;
                }

                if (!new[]
                {
                    "waiting-destination",
                    "preparing-destination",
                    "checking-target",
                    "opening-upload",
                    "uploading",
                    "committing-upload",
                    "verifying-upload"
                }.Contains(progress.State))
                    return;
                Upload value;
                if (!uploads.TryGetValue(key, out value))
                {
                    value = new Upload
                    {
                        Name = progress.ObjectName,
                        Order = ++order
                    };
                    uploads.Add(key, value);
                }

                value.Stage = progress.State;
                value.Label = progress.TransferStage;
                if (progress.Size > 0)
                    value.Size = progress.Size;
                if (progress.State == "opening-upload")
                {
                    value.Written = 0;
                    value.BackgroundState = null;
                }
                else if (progress.State == "uploading")
                    value.Written = progress.Written;
            }
        }

        internal string Text(string fallback)
        {
            lock (gate)
                return string.IsNullOrEmpty(archive) ? fallback : "正在打包 " + archive;
        }
    }
}
