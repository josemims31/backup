using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
#if LOCAL_REPOSITORY
using FileSystemDestination = Backup.Core.LocalDirectoryDestination;

#else
using FileSystemDestination = Backup.Core.SmbDirectoryDestination;

#endif
namespace Backup.Core
{
#if LOCAL_REPOSITORY
    public sealed class LocalDirectoryDestination 
#else
    public sealed class SmbDirectoryDestination 
#endif
    : IRepositorySession,
        IStorageDirectoryListing,
        IStorageEntryRemoval,
        ICancellableBackupDestination,
        IOwnedUploadDestination,
        IInterruptedUploadDestination,
        IRecoverableUploadDestination,
        IReadOnlyDestinationProbe,
        IBandwidthDestination,
        ITemporaryUploadProbe,
        ITaskStorageRemoval,
        IRangedBackupDestination    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint GetDriveType(string root);
        private DirectoryRefreshCache directoryRefresh = new DirectoryRefreshCache();
        private string directoryScope;
        public string ConnectionState
        {
            get
            {
                return "";
            }
        }

        public string ConnectionError
        {
            get
            {
                return null;
            }
        }

        public event Action ConnectionStateChanged
        {
            add
            {
            }

            remove
            {
            }
        }

        public void Dispose()
        {
        }

        public void ShareDirectoryRefresh(DirectoryRefreshCache cache, string scope)
        {
            directoryRefresh = cache;
            directoryScope = scope;
        }

        public IManagedBackupDestination ForDirectory(string location, CancellationToken cancellation)
        {
            RepositoryModules.CheckDirectory("local", root, location);
            var next = new FileSystemDestination(Id, location)
            {
                OperationCancellation = cancellation,
                Bandwidth = Bandwidth,
                UploadJournal = UploadJournal
            };
            next.ShareDirectoryRefresh(directoryRefresh, directoryScope ?? Id);
            return next;
        }

        public IList<StorageEntry> ListEntriesAt(string location, CancellationToken cancellation, bool fresh)
        {
            using (var next = (FileSystemDestination)ForDirectory(location, cancellation))
            {
                if (fresh)
                    directoryRefresh.InvalidateFolder(location);
                return next.ListEntries();
            }
        }

        private bool NetworkPath
        {
            get
            {
                return root.StartsWith(@"\\") || GetDriveType(Path.GetPathRoot(root)) == 4;
            }
        }

        private readonly string root;
        private readonly UploadStaging staging;
        public IUploadJournal UploadJournal
        {
            get
            {
                return staging.Journal;
            }

            set
            {
                staging.Journal = value;
            }
        }

        public void RejectTemporary(string name)
        {
            staging.Rejected(name);
        }

        public RemoteObjectInfo StatTemporary(string name)
        {
            return Stat(staging.Current(name));
        }

        public void ResumeTemporary(string name, string temporary)
        {
            staging.Resume(name, temporary);
        }

        public void SuspendTemporary(string name)
        {
            staging.Suspend(name);
        }

        public bool HasInterruptedTemporary(string name)
        {
            return staging.HasInterrupted(name);
        }

        public string Id { get; private set; }
        public System.Threading.CancellationToken OperationCancellation { get; set; }
        public TransferBandwidth Bandwidth { get; set; }

#if LOCAL_REPOSITORY
        public LocalDirectoryDestination(
#else
        public SmbDirectoryDestination(
#endif
        string id, string path)
        {
            root = StoragePath.NormalizeRoot(path);
            Id = id;
            staging = new UploadStaging(id);
        // Creation is metadata-only. Root/link validation is performed by
        // each actual operation, without blocking other session profiles.
        }

#if LOCAL_REPOSITORY
        public LocalDirectoryDestination(
#else
        public SmbDirectoryDestination(
#endif
        string id, string path, string approvedTestRoot)
        {
            root = StoragePath.NormalizeRoot(path);
            string approved = System.IO.Path.GetFullPath(approvedTestRoot).TrimEnd('\\');
            if (!root.StartsWith(approved + "\\", StringComparison.OrdinalIgnoreCase) || !File.Exists(System.IO.Path.Combine(approved, ".backup-test-owner")))
                throw new ArgumentException("Destination is outside the owned test directory.");
            if ((File.GetAttributes(approved) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Test root cannot be a reparse point.");
            if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Destination cannot be a reparse point.");
            Id = id;
            staging = new UploadStaging(id);
        }

        private string Resolve(string name)
        {
            if (string.IsNullOrEmpty(name) || System.IO.Path.GetFileName(name) != name || name.Contains(":") || name == "." || name == "..")
                throw new ArgumentException("Unsafe object name.");
            return System.IO.Path.Combine(root, name);
        }

        private void CheckRoot()
        {
            OperationCancellation.ThrowIfCancellationRequested();
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Destination cannot be a reparse point.");
        }

        public void CheckReadableRoot()
        {
            CheckRoot();
            if (!Directory.Exists(root))
                throw new IOException("仓库目录暂不可访问。");
        }

        public IList<StorageEntry> ListEntries()
        {
            return directoryRefresh.Read(directoryScope ?? Id, root, false, ReadEntries, OperationCancellation);
        }

        private IList<StorageEntry> ReadEntries()
        {
            CheckRoot();
            var result = new List<StorageEntry>();
            foreach (var item in new DirectoryInfo(root).EnumerateFileSystemInfos())
            {
                OperationCancellation.ThrowIfCancellationRequested();
                bool directory = (item.Attributes & FileAttributes.Directory) != 0;
                result.Add(new StorageEntry { Name = item.Name, Location = item.FullName, Directory = directory, Size = directory ? 0 : ((FileInfo)item).Length });
            }

            return result;
        }

        public void PrepareWrite()
        {
            Directory.CreateDirectory(root);
            CheckRoot();
        }

        public Stream OpenRead(string name)
        {
            CheckRoot();
            string path = Resolve(name);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Backup object cannot be a reparse point.");
            return BandwidthStream.Wrap(
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan),
                Bandwidth,
                false,
                OperationCancellation,
                measureNetwork: NetworkPath
            );
        }

        public DownloadRange OpenReadRange(string name, long offset, long count, long totalSize, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            CheckRoot();
            string path = Resolve(name);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Backup object cannot be a reparse point.");
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
            try
            {
                if (stream.Length != totalSize || offset < 0 || count <= 0 || offset > totalSize - count)
                    throw new IOException("下载文件长度已变化。");
                stream.Seek(offset, SeekOrigin.Begin);
                return new DownloadRange(
                    new BoundedDownloadStream(BandwidthStream.Wrap(stream, Bandwidth, false, cancellation, measureNetwork: NetworkPath), count, cancellation, stream.Dispose),
                    true,
                    File.GetLastWriteTimeUtc(path)
                    .Ticks.ToString()
                );
            }
            catch (global::System.Exception diagnosticFailure)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Repositories.FileSystem/FileSystemDestination.cs:OpenReadRange", diagnosticFailure);
                stream.Dispose();
                throw;
            }
        }

        public void DeleteObject(string name)
        {
            CheckRoot();
            string path = Resolve(name);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Backup object cannot be a reparse point.");
            File.Delete(path);
            directoryRefresh.InvalidateFolder(root);
        }

        public StorageEntry InspectEntry(string name)
        {
            CheckRoot();
            string path = Resolve(name);
            try
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("不能删除链接或联接点。");
                bool directory = (attrs & FileAttributes.Directory) != 0;
                return new StorageEntry
                {
                    Name = name,
                    Location = path,
                    Directory = directory,
                    Size = directory ? 0 : new FileInfo(path).Length
                };
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        public void RemoveEntry(string name, bool directory)
        {
            var entry = InspectEntry(name);
            if (entry == null)
                return;
            if (entry.Directory != directory)
                throw new IOException("文件类型已变化，请刷新后重试。");
            if (directory)
                Directory.Delete(Resolve(name), false);
            else
                File.Delete(Resolve(name));
            directoryRefresh.InvalidateFolder(root);
            if (directory)
                directoryRefresh.InvalidateFolder(Resolve(name), true);
        }

        private string TaskDeletePath(string name)
        {
            string folder = Path.GetFullPath(root).TrimEnd('\\') + "\\", target = Path.GetFullPath(Resolve(name));
            if (!target.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                throw new IOException("任务删除目标超出仓库目录。");
            return target;
        }

        public void DeleteDirectoryTree(string name)
        {
            string target = TaskDeletePath(name);
            try
            {
                CheckRoot();
                Directory.Delete(target, true);
            }
            catch (DirectoryNotFoundException diagnosticFailure)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Repositories.FileSystem/FileSystemDestination.cs:DeleteDirectoryTree", diagnosticFailure);
            }
            finally
            {
                directoryRefresh.InvalidateFolder(root);
                directoryRefresh.InvalidateFolder(target, true);
            }
        }

        public Stream OpenTemporary(string name)
        {
            string target = Resolve(name);
            Directory.CreateDirectory(root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Destination cannot be a reparse point.");
            if (File.Exists(target))
                throw new IOException("Refusing to overwrite immutable object: " + name);
            // Never truncate an existing link or a temporary file still owned by another writer.
            string temporary = Resolve(staging.Begin(name));
            try
            {
                return BandwidthStream.Wrap(
                    new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous),
                    Bandwidth,
                    true,
                    OperationCancellation,
                    measureNetwork: NetworkPath
                );
            }
            catch (global::System.Exception diagnosticFailure)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Repositories.FileSystem/FileSystemDestination.cs:OpenTemporary", diagnosticFailure);
                staging.Rejected(name);
                throw;
            }
        }

        private void CheckTemporary(string path)
        {
            CheckRoot();
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Temporary backup object cannot be a reparse point.");
        }

        public void CommitTemporary(string name)
        {
            string path = Resolve(staging.Current(name));
            CheckTemporary(path);
            try
            {
                File.Move(path, Resolve(name));
                directoryRefresh.InvalidateFolder(root);
            }
            catch (IOException)
            {
                if (File.Exists(Resolve(name)))
                    throw new BackupObjectConflictException();
                throw;
            }
        }

        public void DeleteTemporary(string name)
        {
            staging.Delete(
                name,
                temporary =>
            {
                string path = Resolve(temporary);
                CheckTemporary(path);
                if (File.Exists(path))
                    File.Delete(path);
            }
            );
        }

        public RemoteObjectInfo Stat(string name)
        {
            // FileInfo.Exists hides network and permission errors. Check the parent and use throwing APIs.
            CheckRoot();
            string path = Resolve(name);
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidBackupObjectException("Backup object cannot be a reparse point.");
                if ((attributes & FileAttributes.Directory) != 0)
                    throw new InvalidBackupObjectException("An object path is a directory.");
                FileInfo file = new FileInfo(path);
                return new RemoteObjectInfo
                {
                    Name = file.Name,
                    Exists = true,
                    Size = file.Length
                };
            }
            catch (FileNotFoundException)
            {
                return new RemoteObjectInfo
                {
                    Name = name,
                    Exists = false,
                    Size = 0
                };
            }
        }
    }
}
