using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed class RestoreRequest
    {
        public FileHistoryRow File;
        public string RelativePath;
        public string RequiredDestinationId;
        public string RequiredGroupId;
    }

    public sealed class RestoredFile
    {
        public string RelativePath;
        public long Size, Modified;
        public Func<CancellationToken, Stream> Open;
    }

    public sealed class RestoredSelection : IDisposable
    {
        public readonly List<string> Directories = new List<string>();
        public readonly List<RestoredFile> Entries = new List<RestoredFile>();
        internal Action<string> CopyTo;
        internal MemoryReadCache Cache;
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> RepairWarnings = new System.Collections.Concurrent.ConcurrentQueue<string>();
        public IList<string> Warnings
        {
            get
            {
                return RepairWarnings.Concat(Resources.OfType<RestoredSelection>().SelectMany(child => child.Warnings)).Distinct().ToList();
            }
        }

        internal CancellationTokenSource Cancellation;
        internal TransferBandwidth Bandwidth;
        internal readonly List<IDisposable> Resources = new List<IDisposable>();
        private readonly List<Stream> streams = new List<Stream>();
        private bool disposed;
        internal RestoredSelection()
        {
        }

        public RestoredSelection(IEnumerable<RestoredFile> files)
        {
            Cancellation = new CancellationTokenSource();
            Entries.AddRange(files);
            foreach (var file in Entries)
                DesktopBackend.SafeRestorePath(Path.GetPathRoot(AppDomain.CurrentDomain.BaseDirectory), file.RelativePath);
        }

        public Stream OpenFile(string relative)
        {
            lock (streams)
            {
                if (disposed)
                    throw new ObjectDisposedException("RestoredSelection");
                var file = Entries.Single(x => x.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
                var stream = file.Open(Cancellation.Token);
                streams.Add(stream);
                return stream;
            }
        }

        public byte[] ReadFileBytes(string relative)
        {
            using (var input = OpenFile(relative))
            using (var output = new MemoryStream())
            {
                if (input.Length > int.MaxValue)
                    throw new IOException("文件过大，请流式读取。");
                input.CopyTo(output, 64 * 1024);
                return output.ToArray();
            }
        }

        public void Dispose()
        {
            Stream[] pending;
            lock (streams)
            {
                if (disposed)
                    return;
                disposed = true;
                Cancellation.Cancel();
                pending = streams.ToArray();
                foreach (var stream in pending)
                    stream.Dispose();
                streams.Clear();
            }

            foreach (var child in Resources.OfType<RestoredSelection>())
                child.Cancellation.Cancel();
            if (Cache != null)
                Cache.Dispose();
            foreach (var resource in Resources)
                resource.Dispose();
            Resources.Clear();
            foreach (var stream in pending.OfType<ProducerReadStream>())
                stream.WaitForPending();
            if (Cache != null)
                Cache.WaitForPending();
            Cancellation.Dispose();
        }
    }

    public sealed partial class DesktopBackend
    {
        public static string SafeRestorePath(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
                relative.Split('\\', '/').Any(p => p.Length == 0 || p == "." || p == ".." ||
                p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                p.EndsWith(".") ||
                p.EndsWith(" ")))
                throw new IOException("清单包含无效恢复路径。");
            string boundary = RestoreDestination.Boundary(root);
            string path = Path.GetFullPath(boundary + relative);
            if (!path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
                throw new IOException("恢复路径越界。");
            return path;
        }

        private static void ValidateRestoreRequests(IList<RestoreRequest> requests)
        {
            if (requests.Count == 0)
                throw new ArgumentException("没有可下载的文件。");
            if (requests.Select(r => r.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != requests.Count)
                throw new IOException("所选版本恢复路径重名，请分别下载。");
            foreach (var request in requests)
                SafeRestorePath(Path.GetPathRoot(AppDomain.CurrentDomain.BaseDirectory), request.RelativePath);
        }

        public RestoredSelection Materialize(IList<RestoreRequest> requests, Action<string> report = null)
        {
            ValidateRestoreRequests(requests);
            var selection = new RestoredSelection
            {
                Bandwidth = Bandwidth,
                Cancellation = CancellationTokenSource.CreateLinkedTokenSource(DownloadCancellation),
                Cache = new MemoryReadCache(Math.Max(ArchiveObjectIO.MaximumVolumeBytes, MemoryLimitBytes - 192L * 1024 * 1024), TransferMemory)
            };
            var routing = new SmartDownloadRouting();
            var maintenance = maintenanceSearch.Value;
            var search = maintenance == null ? new RestoreSearchScope() : maintenance.Search;
            if (maintenance == null)
                selection.Resources.Add(search);
            foreach (var request in requests)
            {
                var item = request;
                selection.Entries.Add(new RestoredFile {
                    RelativePath = item.RelativePath,
                    Size = item.File.Size,
                    Modified = item.File.Modified,
                    Open = token => new ProducerReadStream(item.File.Size, (output, cancel) => ExtractSelection(new[] {
                    item
                }, null, output, selection.Cache, cancel, report, routing, selection.RepairWarnings.Enqueue, restoreSearch: search), token)
                });
            }

            selection.CopyTo = destination => RestoreToCore(requests, destination, report, selection.Cancellation.Token, selection.RepairWarnings.Enqueue, search);
            return selection;
        }

        public void ReleaseRestored(RestoredSelection selection)
        {
            selection.Dispose();
        }

        public void RestoreTo(
            IList<RestoreRequest> requests,
            string destination,
            Action<string> report = null,
            CancellationToken cancellation = default(CancellationToken),
            Action<string> warning = null
        )
        {
            using (var search = new RestoreSearchScope())
                RestoreToCore(requests, destination, report, cancellation, warning, search);
        }

        private void RestoreToCore(IList<RestoreRequest> requests, string destination, Action<string> report, CancellationToken cancellation, Action<string> warning, RestoreSearchScope restoreSearch)
        {
            RestoreDestination.Validate(destination);
            requests = RestorePathLayout.AlignRequests(requests, destination);
            RestoreDestination.CheckShares(destination, requests.Select(request => request.RelativePath));
            if (!cancellation.CanBeCanceled)
                cancellation = DownloadCancellation;
            ValidateRestoreRequests(requests);
            foreach (var request in requests)
            {
                string target = SafeRestorePath(destination, request.RelativePath);
                if (Directory.Exists(target))
                    throw new IOException("目标路径是文件夹，无法写入文件：" + target);
                CheckRestoreParents(target, destination);
            }

            using (var cache = new MemoryReadCache(Math.Max(ArchiveObjectIO.MaximumVolumeBytes, MemoryLimitBytes - 192L * 1024 * 1024), TransferMemory))
            using (var jobs = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
                var routing = new SmartDownloadRouting();
                var archives = requests.GroupBy(request => request.File.ArchiveId).Select(group => (IList<RestoreRequest>)group.ToList()).ToList();
                try
                {
                    System.Threading.Tasks.Parallel.ForEach(
                        archives,
                        new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = MemoryLimitMiB < 512 ? 1 : 2, CancellationToken = jobs.Token },
                        archive =>
                    {
                        try
                        {
                            ExtractSelection(archive, destination, null, cache, jobs.Token, report, routing, warning, restoreSearch: restoreSearch);
                        }
                        catch (OperationCanceledException)
                        {
                            if (jobs.IsCancellationRequested)
                                throw;
                            failures.Add(archive.First().File.ArchiveName + "：下载已中断。");
                        }
                        catch (Exception error)
                        {
                            global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/RestoreBackend.cs:RestoreToCore", error);
                            if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is NotSupportedException))
                                throw;
                            failures.Add(archive.First().File.ArchiveName + "：" + RestoreFailureMessage(error));
                        }
                    }
                    );
                }
                catch (AggregateException error)
                {
                    var errors = error.Flatten().InnerExceptions;
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors.FirstOrDefault(failure => !(failure is OperationCanceledException)) ?? errors[0]).Throw();
                }

                if (failures.Count > 0)
                    throw new IOException("部分文件下载失败，其他压缩包已继续处理：\n" + string.Join("\n", failures));
            }
        }

        private void ExtractSelection(
            IList<RestoreRequest> requests,
            string destination,
            Stream singleOutput,
            MemoryReadCache cache,
            CancellationToken cancellation,
            Action<string> report,
            SmartDownloadRouting routing,
            Action<string> warning = null,
            bool findMoved = true,
            ISet<string> allowedDestinations = null,
            RestoreSearchScope restoreSearch = null
        )
        {
            var definitions = Catalog.LoadTopology().Destinations.ToDictionary(d => d.Id);
            var registry = Adapters(findMoved, false, restoreSearch);
            long budget = Math.Max(ArchiveObjectIO.MaximumVolumeBytes, MemoryLimitBytes - 192L * 1024 * 1024);
            using (registry.BeginScope())
                foreach (var archive in requests.GroupBy(r => r.File.ArchiveId))
                    using (Catalog.AcquireArchiveLease(archive.Key, true))
                    using (var transfer = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var required = archive.Where(r => !string.IsNullOrEmpty(r.RequiredDestinationId)).Select(r => r.RequiredDestinationId).Distinct().ToList();
                        if (required.Count > 1)
                            throw new IOException("同一压缩包选择了不同的下载仓库，请分别下载。");
                        var groups = archive.Where(r => !string.IsNullOrEmpty(r.RequiredGroupId)).Select(r => r.RequiredGroupId).Distinct().ToList();
                        if (groups.Count > 1)
                            throw new IOException("同一压缩包选择了不同的下载仓库，请分别下载。");
                        var objects = Catalog.GetObjects(archive.Key);
                        var copies = Catalog.GetCopies(archive.Key).Where(copy => allowedDestinations == null || allowedDestinations.Contains(copy.DestinationId)).ToList();
                        var inspectedStates = Catalog.IntegrityCopyStates(archive.Key);
                        if (objects.Count == 0)
                            throw new IOException("没有已备份的分卷。");
                        var activeToken = transfer.Token;
                        Func<ObjectRecord, byte[]> load = item => ReadRestoreVolume(
                            archive.Key,
                            item,
                            copies.Where(copy => copy.Object.Name == item.Name),
                            definitions,
                            registry,
                            routing,
                            activeToken,
                            report,
                            warning,
                            required.FirstOrDefault(),
                            groups.FirstOrDefault(),
                            inspectedStates
                        );
                        Action<ObjectRecord, byte[], CancellationToken> fill = (item, buffer, token) => ReadRestoreVolumeInto(
                            archive.Key,
                            item,
                            copies.Where(copy => copy.Object.Name == item.Name),
                            definitions,
                            registry,
                            routing,
                            token,
                            report,
                            warning,
                            required.FirstOrDefault(),
                            groups.FirstOrDefault(),
                            inspectedStates,
                            buffer
                        );
                        try
                        {
                            using (Stream joined = cache == null ? new VerifiedVolumeStream(objects, load, budget) : VerifiedVolumeStream.FromCache(objects, cache, fill, budget, activeToken))
                            {
                                var sizes = archive.ToDictionary(r => r.File.StoredEntryPath, r => r.File.Size, StringComparer.OrdinalIgnoreCase);
                                var reader = new SevenZipArchiveReader(Path.Combine(BaseDirectory, "7za.dll"))
                                {
                                    PauseCheckpoint = Bandwidth.WaitForResume
                                };
                                if (singleOutput != null)
                                    reader.ExtractToStreams(joined, Catalog.GetArchivePassword(archive.Key), archive.ToDictionary(r => r.File.StoredEntryPath, r => singleOutput, StringComparer.OrdinalIgnoreCase), sizes, cancellation);
                                else
                                {
                                    var targets = archive.ToDictionary(r => r.File.StoredEntryPath, r => SafeRestorePath(destination, r.RelativePath), StringComparer.OrdinalIgnoreCase);
                                    foreach (string target in targets.Values)
                                        CheckRestoreParents(target, destination);
                                    reader.Extract(joined, Catalog.GetArchivePassword(archive.Key), targets, sizes, cancellation, true);
                                    foreach (var request in archive)
                                        File.SetLastWriteTimeUtc(targets[request.File.StoredEntryPath], DateTime.FromFileTimeUtc(request.File.Modified));
                                }
                            }
                        }
                        finally
                        {
                            transfer.Cancel();
                            if (cache != null)
                                cache.WaitForPending();
                        }
                    }
        }

        public static void CopyRestored(RestoredSelection selection, string destination)
        {
            if (selection.Bandwidth != null)
                selection.Bandwidth.WaitForResume(selection.Cancellation.Token);
            if (selection.CopyTo != null)
            {
                selection.CopyTo(destination);
                return;
            }

            RestoreDestination.Validate(destination);
            var map = RestorePathLayout.Align(selection.Entries.Select(entry => entry.RelativePath).Concat(selection.Directories), destination);
            RestoreDestination.CheckShares(destination, selection.Entries.Select(entry => map(entry.RelativePath)), selection.Directories.Select(map));
            foreach (string relative in selection.Directories)
            {
                if (map(relative).Length == 0)
                    continue;
                string folder = SafeRestorePath(destination, map(relative));
                if (File.Exists(folder))
                    throw new IOException("目标路径是文件，无法写入文件夹：" + folder);
                CheckRestoreParents(folder, destination);
            }

            foreach (var entry in selection.Entries)
            {
                string target = SafeRestorePath(destination, map(entry.RelativePath));
                if (Directory.Exists(target))
                    throw new IOException("目标路径是文件夹，无法写入文件：" + target);
                CheckRestoreParents(target, destination);
            }

            foreach (string relative in selection.Directories)
                if (map(relative).Length > 0)
                    ArchiveFileOutput.EnsureDirectory(SafeRestorePath(destination, map(relative)));
            var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
            try
            {
                System.Threading.Tasks.Parallel.ForEach(
                    selection.Entries,
                    new System.Threading.Tasks.ParallelOptions {
                    MaxDegreeOfParallelism = TransferScheduler.MaximumDownloads,
                    CancellationToken = selection.Cancellation.Token
                },
                    entry =>
                {
                    selection.Cancellation.Token.ThrowIfCancellationRequested();
                    if (selection.Bandwidth != null)
                        selection.Bandwidth.WaitForResume(selection.Cancellation.Token);
                    string target = SafeRestorePath(destination, map(entry.RelativePath));
                    CheckRestoreParents(target, destination);
                    bool created = false;
                    try
                    {
                        using (var input = selection.OpenFile(entry.RelativePath))
                        using (var output = ArchiveFileOutput.OpenFile(target, true))
                        {
                            created = true;
                            byte[] buffer = new byte[64 * 1024];
                            int count;
                            long total = 0;
                            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                selection.Cancellation.Token.ThrowIfCancellationRequested();
                                if (selection.Bandwidth != null)
                                    selection.Bandwidth.WaitForResume(selection.Cancellation.Token);
                                total += count;
                                if (total > entry.Size)
                                    throw new IOException("文件长度已变化。");
                                output.Write(buffer, 0, count);
                            }

                            if (total != entry.Size)
                                throw new IOException("文件下载不完整。");
                        }

                        if (entry.Modified > 0)
                            File.SetLastWriteTimeUtc(target, DateTime.FromFileTimeUtc(entry.Modified));
                    }
                    catch (OperationCanceledException)
                    {
                        if (created)
                            File.Delete(target);
                        throw;
                    }
                    catch (Exception error)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/RestoreBackend.cs:CopyRestored", error);
                        if (!(error is IOException) && !(error is UnauthorizedAccessException))
                            throw;
                        if (created)
                            File.Delete(target);
                        failures.Add(entry.RelativePath + "：" + RestoreFailureMessage(error));
                    }
                }
                );
            }
            catch (AggregateException error)
            {
                selection.Cancellation.Token.ThrowIfCancellationRequested();
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.Flatten().InnerExceptions.First()).Throw();
                throw;
            }

            if (failures.Count > 0)
                throw new IOException("部分文件下载失败，其他文件已继续处理：\n" + string.Join("\n", failures));
        }

        private static void CheckRestoreParents(string file, string root)
        {
            string limit = RestoreDestination.ParentBoundary(file, root);
            for (string parent = Path.GetDirectoryName(file); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            {
                if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("恢复目录不能经过链接或联接点。");
                if (parent.TrimEnd('\\').Equals(limit, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            throw new IOException("恢复目录越界。");
        }
    }
}
