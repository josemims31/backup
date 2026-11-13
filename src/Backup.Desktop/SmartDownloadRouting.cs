using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Backup.Core;

namespace Backup.Desktop
{
    internal sealed class SmartDownloadRouting
    {
        private sealed class Performance
        {
            public int Active, Failures;
            public double BytesPerSecond;
        }

        private readonly object gate = new object();
        private readonly Dictionary<string, Performance> stats = new Dictionary<string, Performance>();
        private Performance Get(string id)
        {
            Performance value;
            if (!stats.TryGetValue(id, out value))
            {
                value = new Performance();
                stats[id] = value;
            }

            return value;
        }

        public byte[] Read(IEnumerable<StoredCopy> available, Func<StoredCopy, byte[]> read, string preferredDestination = null, string preferredGroup = null, string objectName = null)
        {
            var candidates = available.ToList();
            Exception last = null;
            bool damaged = false;
            var failures = new List<string>();
            bool empty = candidates.Count == 0;
            while (candidates.Count > 0)
            {
                StoredCopy copy;
                lock (gate)
                {
                    copy = candidates.OrderBy(item => preferredDestination != null && item.DestinationId != preferredDestination || preferredGroup != null && item.GroupId != preferredGroup ? 1 : 0).ThenBy(item => Get(item.DestinationId)
                        .Failures * 4 +
                        Get(item.DestinationId)
                        .Active)
                        .ThenBy(item => Get(item.DestinationId)
                        .BytesPerSecond > 0 ? item.Object.Size / Get(item.DestinationId)
                        .BytesPerSecond : 1)
                        .First();
                    Get(copy.DestinationId).Active++;
                }

                candidates.Remove(copy);
                var watch = Stopwatch.StartNew();
                try
                {
                    var bytes = read(copy);
                    lock (gate)
                    {
                        var value = Get(copy.DestinationId);
                        double speed = bytes.LongLength / Math.Max(.001, watch.Elapsed.TotalSeconds);
                        value.BytesPerSecond = value.BytesPerSecond == 0 ? speed : (value.BytesPerSecond + speed) / 2;
                        value.Failures = 0;
                    }

                    return bytes;
                }
                catch (ArchiveContentMismatchException error)
                {
                    last = error;
                    damaged = true;
                    failures.Add(copy.DestinationId + "：压缩包损坏，两次校验均不符");
                    lock (gate)
                        Get(copy.DestinationId).Failures++;
                }
                catch (IOException error)
                {
                    last = error;
                    failures.Add(copy.DestinationId + "：" + error.Message);
                    lock (gate)
                        Get(copy.DestinationId).Failures++;
                }
                catch (UnauthorizedAccessException error)
                {
                    last = error;
                    failures.Add(copy.DestinationId + "：" + error.Message);
                    lock (gate)
                        Get(copy.DestinationId).Failures++;
                }
                finally
                {
                    lock (gate)
                        Get(copy.DestinationId).Active--;
                }
            }

            throw new IOException((damaged || empty ? "压缩包损坏或缺少可用副本：" : "无法读取备份分卷：") + (objectName ?? "") + "。" + string.Join("；", failures), last);
        }
    }
}
