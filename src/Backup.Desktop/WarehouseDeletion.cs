using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed class WarehouseDeleteRequest
    {
        public readonly string DestinationId, Folder, Name, Location;
        public readonly bool Directory;
        public readonly long Size, ArchiveId;
        internal WarehouseDeleteRequest(string id, string folder, string name, string location, bool directory, long size, long archive)
        {
            DestinationId = id;
            Folder = folder;
            Name = name;
            Location = location;
            Directory = directory;
            Size = size;
            ArchiveId = archive;
        }
    }

    public sealed class WarehouseDeleteResult
    {
        public int DeletedFiles, DeletedFolders, AlreadyMissing;
        public readonly List<string> Errors = new List<string>();
    }

    public sealed partial class DesktopBackend
    {
        private long physicalDeletionSequence;
        private void PhysicalDeletionCommitted()
        {
            System.Threading.Interlocked.Increment(ref physicalDeletionSequence);
        }

        public static bool IsRealWarehouseDeleteNode(BackupHistoryNode node)
        {
            if (node == null || node.Entry == null || node.IsWarehouseRoot || node.IsMissingArchive)
                return false;
            for (var parent = node.Parent; parent != null; parent = parent.Parent)
                if (parent.ArchiveContainer)
                    return false;
            return !string.IsNullOrEmpty(node.DestinationId) && !string.IsNullOrEmpty(node.BrowseLocation);
        }

        private static bool EntryWithin(DestinationDefinition destination, string location, string folder, bool allowEqual)
        {
            bool local = RepositoryModules.IsFileSystem(destination.Protocol);
            string path = local ? StoragePath.NormalizeRoot(location) : RepositoryModules.NormalizeLocation(destination.Protocol, location).TrimEnd('/');
            string root = local ? StoragePath.NormalizeRoot(folder) : RepositoryModules.NormalizeLocation(destination.Protocol, folder).TrimEnd('/');
            var comparison = local ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return allowEqual && path.Equals(root, comparison) || path.StartsWith(root.TrimEnd('\\', '/') + (local ? "\\" : "/"), comparison);
        }

        private static string EntryLocation(DestinationDefinition destination, string folder, string name)
        {
            RemotePath.Name(name);
            return RepositoryModules.IsFileSystem(destination.Protocol) ? Path.Combine(StoragePath.NormalizeRoot(folder), name) : RemotePath.Append(folder, name);
        }

        private static void ValidateDeleteRequest(DestinationDefinition destination, WarehouseDeleteRequest request)
        {
            string expected = EntryLocation(destination, request.Folder, request.Name);
            bool local = RepositoryModules.IsFileSystem(destination.Protocol);
            if (!EntryWithin(destination, request.Folder, destination.Location, true) || !EntryWithin(destination, request.Location, destination.Location, false) ||
                !SameFolder(expected, request.Location, local))
                throw new IOException("删除目标超出仓库范围，已取消。");
            if (local)
            {
                string path = StoragePath.NormalizeRoot(request.Folder), root = StoragePath.NormalizeRoot(destination.Location);
                while (true)
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("不能通过链接或联接点删除文件。");
                    if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
                        break;
                    path = Path.GetDirectoryName(path);
                    if (path == null)
                        throw new IOException("删除目标超出仓库范围。");
                }
            }
        }

        public IList<WarehouseDeleteRequest> CreateWarehouseDeleteRequests(IList<BackupHistoryNode> nodes)
        {
            if (nodes == null || nodes.Count == 0 || nodes.Any(node => !IsRealWarehouseDeleteNode(node)))
                throw new IOException("只能删除仓库中的真实文件或子文件夹，不能删除仓库根目录或压缩包内部文件。");
            var topology = Catalog.LoadTopology();
            var requests = new List<WarehouseDeleteRequest>();
            foreach (var node in nodes)
            {
                var destination = topology.Destinations.Single(d => d.Id == node.DestinationId && !d.Retired);
                var request = new WarehouseDeleteRequest(node.DestinationId, node.BrowseLocation, node.Entry.Name, node.Entry.Location, node.Entry.Directory, node.Entry.Size, node.ArchiveId);
                ValidateDeleteRequest(destination, request);
                requests.Add(request);
            }

            return requests.Where(item => !requests.Any(parent => parent != item && parent.Directory && parent.DestinationId == item.DestinationId &&
                EntryWithin(topology.Destinations.Single(d => d.Id == item.DestinationId), item.Location, parent.Location, false)))
                .GroupBy(item => item.DestinationId +
                "\n" +
                item.Location, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList()
                .AsReadOnly();
        }

        public WarehouseDeleteResult DeleteWarehouseEntries(IList<WarehouseDeleteRequest> requests, Action<string> progress = null)
        {
            if (requests == null || requests.Count == 0)
                throw new ArgumentException("请选择真实文件或子文件夹。");
            var definitions = Catalog.LoadTopology().Destinations.ToDictionary(d => d.Id);
            foreach (var request in requests)
            {
                if (!definitions.ContainsKey(request.DestinationId) || definitions[request.DestinationId].Retired)
                    throw new IOException("仓库已移除。");
                ValidateDeleteRequest(definitions[request.DestinationId], request);
            }

            var result = new WarehouseDeleteResult();
            var completed = new List<RepositoryObjectResult>();
            Func<WarehouseDeleteRequest, int, bool> remove = null;
            remove = (request, depth) =>
            {
                OperationCancellation.ThrowIfCancellationRequested();
                try
                {
                    if (depth > 64)
                        throw new IOException("目录层数超过删除上限，已保留。");
                    var destination = definitions[request.DestinationId];
                    ValidateDeleteRequest(destination, request);
                    using (var protection = !request.Directory ? Catalog.ProtectWarehouseDeletion(request.DestinationId, request.Name) : null)
                    {
                        var adapter = OpenWarehouseLocation(destination, request.Folder, OperationCancellation);
                        try
                        {
                            var deletion = adapter as IStorageEntryRemoval;
                            if (deletion == null)
                                throw new NotSupportedException("此仓库不支持删除。");
                            var actual = deletion.InspectEntry(request.Name);
                            if (actual != null && (actual.Directory != request.Directory || !actual.Directory && request.Size >= 0 && actual.Size >= 0 && actual.Size != request.Size))
                                throw new IOException("文件已变化，请刷新后重试。");
                            if (actual != null && actual.Directory)
                            {
                                var child = OpenWarehouseLocation(destination, request.Location, OperationCancellation);
                                IList<StorageEntry> entries;
                                try
                                {
                                    var listing = child as IStorageDirectoryListing;
                                    if (listing == null)
                                        throw new NotSupportedException("此仓库不支持读取目录。");
                                    entries = listing.ListEntries();
                                }
                                finally
                                {
                                    DestinationAdapterRegistry.DisposeAdapter(child);
                                }

                                bool complete = true;
                                foreach (var entry in entries)
                                {
                                    string location = EntryLocation(destination, request.Location, entry.Name);
                                    if (!remove(new WarehouseDeleteRequest(request.DestinationId, request.Location, entry.Name, location, entry.Directory, entry.Size, 0), depth + 1))
                                        complete = false;
                                }

                                if (!complete)
                                    return false;
                            }

                            OperationCancellation.ThrowIfCancellationRequested();
                            if (progress != null)
                                progress("正在删除：" + request.Location);
                            if (actual != null)
                                deletion.RemoveEntry(request.Name, request.Directory);
                            if (deletion.InspectEntry(request.Name) != null)
                                throw new IOException("目标仍然存在，未确认删除成功。");
                            completed.Add(new RepositoryObjectResult {
                                DestinationId = request.DestinationId,
                                Folder = request.Folder,
                                Name = request.Name,
                                Directory = request.Directory,
                                Exists = false,
                                IgnoreCase = RepositoryModules.IsFileSystem(destination.Protocol)
                            });
                            if (actual == null)
                                result.AlreadyMissing++;
                            else
                            {
                                PhysicalDeletionCommitted();
                                if (request.Directory)
                                    result.DeletedFolders++;
                                else
                                    result.DeletedFiles++;
                            }

                            if (!request.Directory)
                                try
                                {
                                    Catalog.RecordWarehouseDeletion(request.DestinationId, request.Name);
                                }
                                catch (IOException error)
                                {
                                    result.Errors.Add(request.Location + "：远程删除已确认，但本地备份状态登记未完成：" + error.Message);
                                    return false;
                                }

                            return true;
                        }
                        finally
                        {
                            DestinationAdapterRegistry.DisposeAdapter(adapter);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/WarehouseDeletion.cs:DeleteWarehouseEntries", error);
                    if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is NotSupportedException) && !(error is ArgumentException))
                        throw;
                    result.Errors.Add(request.Location + "：" + error.Message);
                    return false;
                }
            };
            try
            {
                foreach (var request in requests)
                    remove(request, 0);
            }
            finally
            {
                repositoryRuntime.EntriesCommitted(completed);
                FlushRepositoryResults();
            }

            return result;
        }

        public void ReportRemoteFolderCreated(string id, string folder, bool sftp, RemoteConnectionOptions options, string name)
        {
            if (id == null)
                return;
            var destination = Catalog.LoadTopology().Destinations.FirstOrDefault(d => d.Id == id && !d.Retired);
            if (destination == null || destination.Protocol != (sftp ? "sftp" : "webdav") || !EntryWithin(destination, folder, destination.Location, true) ||
                !RemoteConnectionPolicy.SameCredentials(LoadConnection(id), options))
                return;
            repositoryRuntime.EntryCommitted(new RepositoryObjectResult { DestinationId = id, Folder = folder, Name = RemotePath.Name(name), Exists = true, Directory = true });
        }
    }

    public sealed partial class MainWindowController
    {
        public Task DeleteWarehouseEntriesAsync(IList<BackupHistoryNode> nodes)
        {
            return RunOperationAsync(OperationKind.Delete, () => DeleteWarehouseEntriesCoreAsync(nodes));
        }

        private async Task DeleteWarehouseEntriesCoreAsync(IList<BackupHistoryNode> nodes)
        {
            if (IsFolderHistoryView)
                throw new IOException("请在仓库视图中选择真实文件或子文件夹。");
            var requests = Backend.CreateWarehouseDeleteRequests(nodes);
            string text = "将永久删除以下文件，不可撤销！\n\n" + string.Join("\n", requests.Select(item => item.Location));
            if (requests.Any(item => item.ArchiveId > 0))
                text += "\n\n已登记备份包删除后会标为待修复，后续备份可能自动重新生成。";
            if (!ConfirmRemoteTaskAction("删除仓库文件", text))
                return;
            SetBusy(true, false, "正在删除所选仓库文件…");
            try
            {
                var report = OperationReporter("");
                var result = await Task.Run(() => Backend.DeleteWarehouseEntries(requests, report));
                ApplyCommittedChanges();
                string message = "删除结束：已删除 " + result.DeletedFiles + " 个文件、" + result.DeletedFolders + " 个文件夹。" + (result.AlreadyMissing == 0 ? "" : " " + result.AlreadyMissing + " 项已不存在。");
                if (result.Errors.Count > 0)
                    message += "\n以下项目未完全完成：\n" + string.Join("\n", result.Errors);
                SetOperationText(message, result.Errors.Count == 0 ? OperationState.Succeeded : OperationState.Failed);
                Log(message);
                OperationProgress(100, false);
            }
            finally
            {
                SetBusy(false, false, null);
            }
        }
    }
}
