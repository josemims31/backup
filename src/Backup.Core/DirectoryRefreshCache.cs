using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;

namespace Backup.Core
{
    public sealed class DirectoryEntries : ReadOnlyCollection<StorageEntry>
    {
        public DateTime CheckedUtc { get; private set; }

        internal DirectoryEntries(IList<StorageEntry> values, DateTime checkedUtc) : base(values)
        {
            CheckedUtc = checkedUtc;
        }
    }

    public sealed class DirectoryRefreshException : IOException
    {
        public DateTime CheckedUtc { get; private set; }

        internal DirectoryRefreshException(Exception error, DateTime checkedUtc) : base(error.Message, error)
        {
            CheckedUtc = checkedUtc;
        }
    }

    // One refresh flight per credential scope and exact folder. This stores only
    // directory metadata, never backup bytes, ownership or successful-copy state.
    public sealed class DirectoryRefreshCache
    {
        internal static int MinimumIntervalMilliseconds = 2000, FailureIntervalMilliseconds = 20000;
        private sealed class Entry
        {
            internal readonly object Gate = new object();
            internal string Folder;
            internal bool Loading, Native;
            internal int Users;
            internal long Version, PublishedVersion;
            internal IList<StorageEntry> Values;
            internal Exception Error;
            internal DateTime CheckedUtc, Until, Next, LastUse;
        }

        private readonly object gate = new object();
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static string Folder(string value)
        {
            return value.Contains("://") ? new Uri(value.TrimEnd('/') + "/").AbsoluteUri.TrimEnd('/') : StoragePath.NormalizeRoot(value).TrimEnd('\\').ToUpperInvariant();
        }

        private Entry Get(string scope, string folder)
        {
            string path = Folder(folder), key = scope + "\n" + path;
            lock (gate)
            {
                Entry result;
                if (!entries.TryGetValue(key, out result))
                {
                    result = new Entry
                    {
                        Folder = path
                    };
                    entries.Add(key, result);
                }

                result.LastUse = DateTime.UtcNow;
                result.Users++;
                return result;
            }
        }

        private void Release(Entry entry)
        {
            lock (gate)
            {
                entry.Users--;
                if (entries.Count > 64)
                    foreach (string key in entries.Where(pair => pair.Value.Users == 0).OrderBy(pair => pair.Value.LastUse).Take(entries.Count - 64).Select(pair => pair.Key).ToList())
                        entries.Remove(key);
            }
        }

        private static DirectoryEntries Copy(Entry entry)
        {
            return new DirectoryEntries(entry.Values.Select(item => new StorageEntry { Name = item.Name, Location = item.Location, Directory = item.Directory, Size = item.Size }).ToList(), entry.CheckedUtc);
        }

        public IList<StorageEntry> Read(string scope, string folder, bool native, Func<IList<StorageEntry>> load, CancellationToken cancellation)
        {
            var entry = Get(scope, folder);
            try
            {
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    long version;
                    lock (entry.Gate)
                    {
                        DateTime now = DateTime.UtcNow;
                        if (entry.Error != null && now < entry.Until)
                            throw new DirectoryRefreshException(entry.Error, entry.CheckedUtc);
                        if (entry.Values != null && entry.PublishedVersion == entry.Version && now < entry.Until && (!native || entry.Native))
                            return Copy(entry);
                        if (entry.Loading || now < entry.Next)
                        {
                            Monitor.Wait(entry.Gate, Math.Max(1, Math.Min(100, entry.Loading ? 100 : (int)Math.Ceiling((entry.Next - now).TotalMilliseconds))));
                            continue;
                        }

                        entry.Loading = true;
                        version = entry.Version;
                        entry.CheckedUtc = now;
                    }

                    try
                    {
                        var values = load();
                        cancellation.ThrowIfCancellationRequested();
                        if (values == null || values.Any(item => item == null || string.IsNullOrEmpty(item.Name)))
                            throw new IOException("仓库返回的目录清单不完整。");
                        lock (entry.Gate)
                        {
                            entry.Loading = false;
                            entry.Next = DateTime.UtcNow.AddMilliseconds(MinimumIntervalMilliseconds);
                            Monitor.PulseAll(entry.Gate);
                            if (version != entry.Version)
                                continue;
                            entry.Values = values.Select(item => new StorageEntry { Name = item.Name, Location = item.Location, Directory = item.Directory, Size = item.Size }).ToList();
                            entry.Native = native;
                            entry.PublishedVersion = version;
                            entry.Error = null;
                            entry.Until = entry.Next;
                            return Copy(entry);
                        }
                    }
                    catch (Exception error)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/DirectoryRefreshCache.cs:Read", error);
                        lock (entry.Gate)
                        {
                            entry.Loading = false;
                            entry.Next = DateTime.UtcNow.AddMilliseconds(MinimumIntervalMilliseconds);
                            if (!(error is OperationCanceledException) && (error is IOException || error is UnauthorizedAccessException || error is NotSupportedException))
                            {
                                entry.Values = null;
                                entry.Error = error;
                                entry.Until = entry.Next = DateTime.UtcNow.AddMilliseconds(FailureIntervalMilliseconds);
                            }

                            Monitor.PulseAll(entry.Gate);
                        }

                        throw;
                    }
                }
            }
            finally
            {
                Release(entry);
            }
        }

        public void InvalidateFolder(string folder, bool descendants = false)
        {
            string path = Folder(folder);
            List<Entry> selected;
            lock (gate)
                selected = entries.Values.Where(entry => entry.Folder == path ||
                    descendants &&
                    (entry.Folder.StartsWith(path + "/", StringComparison.Ordinal) ||
                    entry.Folder.StartsWith(path + "\\", StringComparison.Ordinal)))
                    .ToList();
            foreach (var entry in selected)
                lock (entry.Gate)
                {
                    entry.Version++;
                    entry.Values = null;
                    Monitor.PulseAll(entry.Gate);
                }
        }

        public void Clear()
        {
            List<Entry> old;
            lock (gate)
                old = entries.Values.ToList();
            foreach (var entry in old)
                lock (entry.Gate)
                {
                    entry.Version++;
                    entry.Values = null;
                    entry.Error = null;
                    entry.Until = entry.Next = DateTime.MinValue;
                    Monitor.PulseAll(entry.Gate);
                }
        }
    }
}
