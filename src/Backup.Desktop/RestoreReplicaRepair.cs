using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        private static string RestoreFailureMessage(Exception error)
        {
            var messages = new List<string>();
            for (var current = error; current != null; current = current.InnerException)
                if (!string.IsNullOrWhiteSpace(current.Message) && !messages.Contains(current.Message))
                    messages.Add(current.Message);
            return string.Join("；", messages);
        }

#if UI_TEST
        public Action<string, string, int> TestRestoreVolumeAttempt;
#endif
        private byte[] ReadRestoreVolume(
            long archiveId,
            ObjectRecord item,
            IEnumerable<StoredCopy> available,
            IDictionary<string, DestinationDefinition> definitions,
            DestinationAdapterRegistry registry,
            SmartDownloadRouting routing,
            CancellationToken cancellation,
            Action<string> report,
            Action<string> warning,
            string preferredDestination,
            string preferredGroup,
            IDictionary<string, string> inspectedStates
        )
        {
            if (item.Size <= 0 || item.Size > ArchiveObjectIO.MaximumVolumeBytes)
                throw new IOException("恢复分卷超过内存传输上限。");
            var buffer = new byte[(int)item.Size];
            ReadRestoreVolumeInto(archiveId, item, available, definitions, registry, routing, cancellation, report, warning, preferredDestination, preferredGroup, inspectedStates, buffer);
            return buffer;
        }

        private void ReadRestoreVolumeInto(
            long archiveId,
            ObjectRecord item,
            IEnumerable<StoredCopy> available,
            IDictionary<string, DestinationDefinition> definitions,
            DestinationAdapterRegistry registry,
            SmartDownloadRouting routing,
            CancellationToken cancellation,
            Action<string> report,
            Action<string> warning,
            string preferredDestination,
            string preferredGroup,
            IDictionary<string, string> inspectedStates,
            byte[] buffer
        )
        {
            if (item.Size <= 0 || item.Size > ArchiveObjectIO.MaximumVolumeBytes)
                throw new IOException("恢复分卷超过内存传输上限。");
            if (buffer == null || buffer.LongLength != item.Size)
                throw new IOException("恢复分卷缓冲长度无效。");
            routing.Read(
                available,
                copy =>
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    IManagedBackupDestination adapter = null;
                    try
                    {
#if UI_TEST
                        if (TestRestoreVolumeAttempt != null)
                            TestRestoreVolumeAttempt(item.Name, copy.DestinationId, attempt);
#endif
                        adapter = registry.CreateTransfer(definitions[copy.DestinationId]) as IManagedBackupDestination;
                        if (adapter == null)
                            throw new NotSupportedException("仓库不支持下载。");
                        var cancel = adapter as ICancellableBackupDestination;
                        if (cancel != null)
                            cancel.OperationCancellation = cancellation;
                        if (report != null)
                            report((attempt == 0 ? "读取并校验 " : "校验不符，重新下载 ") + item.Name + " ← " + copy.DestinationId);
                        ReadLocatedRestoreVolume(adapter, item, buffer, cancellation);
                        string previous;
                        if (inspectedStates.TryGetValue(copy.DestinationId + "\0" + item.Name, out previous) && previous == "content-damaged")
                            Catalog.RecordDownloadedContentVerified(archiveId, copy.DestinationId, item);
                        return buffer;
                    }
                    catch (ArchiveContentMismatchException)
                    {
                        if (attempt == 0)
                            continue;
                        Catalog.RecordDownloadedContentDamage(archiveId, copy.DestinationId, item.Name);
                        FlushRepositoryResults();
                        throw;
                    }
                    finally
                    {
                        if (adapter != null)
                            registry.Release(adapter);
                    }
                }

                throw new IOException("分卷下载未完成。");
            },
                preferredDestination,
                preferredGroup,
                item.Name
            );
        }

        private void ReadLocatedRestoreVolume(IManagedBackupDestination adapter, ObjectRecord item, byte[] buffer, CancellationToken cancellation)
        {
            RetryAfterBandwidthPause(() =>
            {
                ArchiveObjectIO.ReadVerifiedInto(adapter, item, buffer, Transfers, null, cancellation);
                return true;
            }, cancellation);
        }
    }
}
