using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed class RepositoryFolderChange
    {
        public string DestinationId, Folder, Notice;
        public bool ContentChanged, MetadataChanged;
        public long Version;
    }

    public sealed class ApplicationChange
    {
        public CatalogChange Changes { get; private set; }
        public ISet<string> Repositories { get; private set; }
        public ISet<string> Sources { get; private set; }
        public ISet<string> Tasks { get; private set; }
        public IList<RepositoryFolderChange> Folders { get; private set; }

        internal ApplicationChange(
            CatalogChange changes,
            IEnumerable<string> repositories = null,
            IEnumerable<string> sources = null,
            IEnumerable<string> tasks = null,
            IEnumerable<RepositoryFolderChange> folders = null
        )
        {
            Changes = changes;
            Repositories = new HashSet<string>(repositories ?? new string[0]);
            Sources = new HashSet<string>(sources ?? new string[0]);
            Tasks = tasks == null ? null : new HashSet<string>(tasks);
            Folders = (folders ?? new RepositoryFolderChange[0]).ToList().AsReadOnly();
        }
    }

    internal sealed class RepositoryRuntime : IDisposable
    {
        internal sealed class ReadTicket
        {
            internal long Sequence, Generation, Observation;
            internal string Id, Folder;
            internal bool Invalidated;
        }

        private sealed class Committed
        {
            internal long Sequence;
            internal RepositoryCommit Batch;
        }

        private sealed class Stamped
        {
            internal long Sequence;
            internal RepositoryObjectResult Result;
        }

        private readonly BackupCatalog catalog;
        private readonly DirectoryRefreshCache directories;
        private readonly object gate = new object();
        private readonly ConcurrentQueue<Committed> queue = new ConcurrentQueue<Committed>();
        private readonly List<Stamped> recent = new List<Stamped>();
        private readonly HashSet<ReadTicket> reads = new HashSet<ReadTicket>();
        private readonly Timer timer;
        private bool closed, observationsBlocked;
        private long generation, lastRecoveryRevision, resultSequence, observationVersion;
        private CatalogChange localChanges;
        private readonly HashSet<string> localSources = new HashSet<string>();
        private readonly Dictionary<string, RepositoryFolderChange> localFolders = new Dictionary<string, RepositoryFolderChange>(StringComparer.Ordinal);
        private long folderVersion;
        private readonly Dictionary<string, long> folderVersions = new Dictionary<string, long>(StringComparer.Ordinal);
        private ApplicationChange pendingPresentation;
        private static ApplicationChange Combine(ApplicationChange left, ApplicationChange right)
        {
            if (left == null)
                return right;
            if (right == null)
                return left;
            bool all = (left.Changes & CatalogChange.Archives) != 0 && left.Tasks == null || (right.Changes & CatalogChange.Archives) != 0 && right.Tasks == null;
            var folders = new Dictionary<string, RepositoryFolderChange>();
            foreach (var item in left.Folders.Concat(right.Folders))
            {
                string key = FolderKey(item.DestinationId, item.Folder);
                RepositoryFolderChange old;
                if (folders.TryGetValue(key, out old))
                    folders[key] = new RepositoryFolderChange
                    {
                        DestinationId = item.DestinationId,
                        Folder = item.Folder,
                        Version = Math.Max(old.Version, item.Version),
                        ContentChanged = old.ContentChanged || item.ContentChanged,
                        MetadataChanged = old.MetadataChanged || item.MetadataChanged,
                        Notice = item.Version >= old.Version ? item.Notice : old.Notice
                    };
                else
                    folders[key] = item;
            }

            return new ApplicationChange(
                left.Changes | right.Changes,
                left.Repositories.Concat(right.Repositories),
                left.Sources.Concat(right.Sources),
                all ? null : (left.Tasks ?? new HashSet<string>()).Concat(right.Tasks ??
                new HashSet<string>()),
                folders.Values
            );
        }

        private int pumpScheduled;
        internal event Action<ApplicationChange> Changed;
        internal Exception LastError;
        internal RepositoryRuntime(BackupCatalog value, DirectoryRefreshCache refresh = null)
        {
            catalog = value;
            directories = refresh;
            lastRecoveryRevision = catalog.RecoveryRevision;
            timer = new Timer(_ => Pump(), null, Timeout.Infinite, Timeout.Infinite);
            catalog.RepositoryCommitted += Enqueue;
        }

        private void Invalidate(IList<RepositoryObjectResult> results)
        {
            if (directories == null)
                return;
            foreach (var result in results)
            {
                directories.InvalidateFolder(result.Folder);
                if (result.Directory && !result.Exists)
                    directories.InvalidateFolder(result.IgnoreCase ? Path.Combine(result.Folder, result.Name) : RemotePath.Append(result.Folder, result.Name), true);
            }
        }

        private void Enqueue(RepositoryCommit batch)
        {
            Invalidate(batch.Results);
            if ((batch.Changes & (CatalogChange.Archives | CatalogChange.Topology | CatalogChange.Workspace | CatalogChange.Connections)) != 0 || batch.Results.Count > 0)
                Interlocked.Increment(ref observationVersion);
            queue.Enqueue(new Committed { Sequence = Interlocked.Increment(ref resultSequence), Batch = batch });
            ArmPump(80);
        }

        internal void SetActivity(bool active)
        {
            lock (gate)
            {
                if (observationsBlocked == active)
                    return;
                observationsBlocked = active;
                Interlocked.Increment(ref observationVersion);
            }
        }

        internal void Notify(CatalogChange changes, IEnumerable<string> sources = null)
        {
            lock (gate)
            {
                if (closed)
                    return;
                localChanges |= changes;
                if (sources != null)
                    localSources.UnionWith(sources);
            }

            ArmPump(80);
        }

        private void ArmPump(int delay)
        {
            if (Interlocked.Exchange(ref pumpScheduled, 1) == 0)
                try
                {
                    timer.Change(delay, Timeout.Infinite);
                }
                catch (ObjectDisposedException)
                { // Expected cancellation, shutdown, or a deliberately rejected identity probe.
                }
        }

        internal static string FolderKey(string id, string folder)
        {
            string path = BackupCatalog.InventoryFolderKey(folder);
            return id + "\n" + (path.Contains("://") ? path : path.ToUpperInvariant());
        }

        private void FolderChanged(string id, string folder, bool content, InventoryFolderRecord record)
        {
            string key = FolderKey(id, folder);
            RepositoryFolderChange old;
            if (localFolders.TryGetValue(key, out old))
                content |= old.ContentChanged;
            long version = ++folderVersion;
            folderVersions[key] = version;
            localFolders[key] = new RepositoryFolderChange
            {
                DestinationId = id,
                Folder = folder,
                ContentChanged = content,
                Version = version,
                Notice = DesktopBackend.InventoryNotice(record)
            };
            localChanges |= CatalogChange.Inventory;
        }

        private static void Apply(IList<StorageEntry> entries, RepositoryObjectResult result)
        {
            var comparer = result.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            string location = result.IgnoreCase ? Path.Combine(result.Folder, result.Name) : RemotePath.Append(result.Folder, result.Name);
            var existing = entries.Where(entry => comparer.Equals(entry.Name, result.Name)).ToList();
            if (result.Exists && existing.Count == 1 && existing[0].Name == result.Name && existing[0].Directory == result.Directory && existing[0].Size == result.Size &&
                existing[0].Location == location)
                return;
            for (int index = entries.Count - 1; index >= 0; index--)
                if (comparer.Equals(entries[index].Name, result.Name))
                    entries.RemoveAt(index);
            if (result.Exists)
                entries.Add(new StorageEntry { Name = result.Name, Size = result.Size, Location = location, Directory = result.Directory });
        }

        private static bool SameEntries(IList<StorageEntry> left, IList<StorageEntry> right)
        {
            if (left.Count != right.Count)
                return false;
            var lookup = new Dictionary<string, StorageEntry>(StringComparer.Ordinal);
            foreach (var item in left)
            {
                if (lookup.ContainsKey(item.Name))
                    return false;
                lookup.Add(item.Name, item);
            }

            foreach (var item in right)
            {
                StorageEntry old;
                if (!lookup.TryGetValue(item.Name, out old) || old.Directory != item.Directory || old.Size != item.Size || old.Location != item.Location)
                    return false;
                lookup.Remove(item.Name);
            }

            return lookup.Count == 0;
        }

        private InventoryFolderRecord SaveEntries(string id, string folder, IList<StorageEntry> entries, string checkedUtc = null, string error = null)
        {
            var old = catalog.GetInventoryFolder(id, folder);
            var previous = old == null ? new List<StorageEntry>() : JsonSettings.Decode(old.Json, new List<StorageEntry>()) ?? new List<StorageEntry>();
            return SaveEntries(id, folder, entries, old, previous, checkedUtc, error);
        }

        private InventoryFolderRecord SaveEntries(string id, string folder, IList<StorageEntry> entries, InventoryFolderRecord old, IList<StorageEntry> previous, string checkedUtc = null, string error = null)
        {
            bool content = old == null || !SameEntries(previous, entries);
            var next = new InventoryFolderRecord
            {
                DestinationId = id,
                Folder = folder,
                Json = content ? JsonSettings.Encode(entries) : old.Json,
                CheckedUtc = checkedUtc ?? (old == null ? "" : old.CheckedUtc),
                Error = error ?? (old == null ? "" : old.Error)
            };
            if (old == null || content || old.CheckedUtc != next.CheckedUtc || old.Error != next.Error)
            {
                catalog.SaveInventoryFolder(next, old);
                FolderChanged(id, folder, content, next);
            }

            return next;
        }

        private void ApplyResults(IList<Stamped> results)
        {
            var deleted = results.Select(item => item.Result).Where(item => item.Directory && !item.Exists).ToList();
            foreach (var stamped in results)
                if (reads.Count > 0)
                    recent.Add(stamped);
            foreach (var group in results.GroupBy(value => FolderKey(value.Result.DestinationId, value.Result.Folder)))
            {
                if (deleted.Any(item => InsideDeletedFolder(group.First().Result.DestinationId, group.First().Result.Folder, item)))
                    continue;
                var first = group.First().Result;
                var old = catalog.GetInventoryFolder(first.DestinationId, first.Folder);
                var entries = old == null ? new List<StorageEntry>() : JsonSettings.Decode(old.Json, new List<StorageEntry>()) ?? new List<StorageEntry>();
                var previous = entries.ToList();
                foreach (var stamped in group)
                    Apply(entries, stamped.Result);
                SaveEntries(first.DestinationId, first.Folder, entries, old, previous);
            }

            foreach (var item in deleted.Where(item => !deleted.Any(parent => parent != item && InsideDeletedFolder(item.DestinationId, item.Folder, parent))))
            {
                string path = item.IgnoreCase ? Path.Combine(item.Folder, item.Name) : RemotePath.Append(item.Folder, item.Name);
                catalog.DeleteInventorySubtree(item.DestinationId, path, item.IgnoreCase);
                foreach (var read in reads.Where(read => InsideDeletedFolder(read.Id, read.Folder, item)))
                    read.Invalidated = true;
            }
        }

        private static bool InsideDeletedFolder(string id, string folder, RepositoryObjectResult deletion)
        {
            if (id != deletion.DestinationId)
                return false;
            string path = deletion.IgnoreCase ? Path.Combine(deletion.Folder, deletion.Name) : RemotePath.Append(deletion.Folder, deletion.Name);
            path = path.TrimEnd('\\', '/');
            string value = folder.TrimEnd('\\', '/');
            var comparison = deletion.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return value.Equals(path, comparison) || value.StartsWith(path + (deletion.IgnoreCase ? "\\" : "/"), comparison);
        }

        private ApplicationChange FlushLocked()
        {
            var ids = new HashSet<string>();
            var tasks = new HashSet<string>();
            var results = new List<Stamped>();
            CatalogChange kinds = localChanges;
            bool untyped = false, allTasks = false;
            Committed item;
            var drained = new List<Committed>();
            try
            {
                while (queue.TryDequeue(out item))
                    drained.Add(item);
                foreach (var committed in drained.OrderBy(value => value.Sequence))
                {
                    var batch = committed.Batch;
                    kinds |= batch.Changes;
                    untyped |= batch.Changes == CatalogChange.None;
                    if ((batch.Changes & CatalogChange.Archives) != 0 || batch.Results.Count > 0 || batch.Important && batch.Changes == CatalogChange.None)
                    {
                        kinds |= CatalogChange.Archives;
                        if (batch.Tasks == null)
                            allTasks = true;
                        else
                            tasks.UnionWith(batch.Tasks);
                    }

                    if (batch.Destinations != null)
                        ids.UnionWith(batch.Destinations);
                    foreach (var result in batch.Results)
                    {
                        ids.Add(result.DestinationId);
                        results.Add(new Stamped { Sequence = committed.Sequence, Result = result });
                    }
                }

                ApplyResults(results);
                if (drained.Count > 0)
                {
                    long revision = catalog.RecoveryRevision;
                    if (untyped && revision != lastRecoveryRevision)
                    {
                        kinds |= CatalogChange.Archives;
                        allTasks = true;
                    }

                    lastRecoveryRevision = revision;
                }

                kinds |= localChanges;
                ids.UnionWith(localFolders.Values.Select(folder => folder.DestinationId));
                var change = Combine(pendingPresentation, new ApplicationChange(kinds, ids, localSources, allTasks ? null : tasks, localFolders.Values));
                pendingPresentation = null;
                localChanges = CatalogChange.None;
                localSources.Clear();
                localFolders.Clear();
                return change;
            }
            catch (global::System.Exception diagnosticFailure)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/RepositoryRuntime.cs:FlushLocked", diagnosticFailure);
                foreach (var failed in drained)
                    queue.Enqueue(failed);
                ArmPump(500);
                throw;
            }
        }

        private void Publish(ApplicationChange change)
        {
            if (change.Changes == CatalogChange.None)
                return;
            var observers = Changed;
            if (observers != null)
                observers(change);
        }

        private void Pump()
        {
            Interlocked.Exchange(ref pumpScheduled, 0);
            try
            {
                Flush();
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/RepositoryRuntime.cs:Pump", error);
                LastError = error;
            }
            finally
            {
                if (!queue.IsEmpty)
                    ArmPump(100);
            }
        }

        internal void Flush()
        {
            ApplicationChange change;
            lock (gate)
            {
                if (closed)
                    return;
                change = FlushLocked();
            }

            Publish(change);
        }

        internal void ConnectionChanged(string id)
        {
            Publish(new ApplicationChange(CatalogChange.Connections, new[] { id }));
        }

        internal ReadTicket BeginRead(string id, string folder)
        {
            lock (gate)
            {
                if (closed)
                    throw new ObjectDisposedException("RepositoryRuntime");
                var ticket = new ReadTicket
                {
                    Id = id,
                    Folder = BackupCatalog.InventoryFolderKey(folder),
                    Sequence = Interlocked.Read(ref resultSequence),
                    Generation = generation,
                    Observation = Interlocked.Read(ref observationVersion)
                };
                reads.Add(ticket);
                return ticket;
            }
        }

        internal bool CommitObservation(ReadTicket ticket, Action publish, CancellationToken cancellation = default(CancellationToken))
        {
            lock (gate)
                return catalog.ApplyObservation(
                    () => !closed && !ticket.Invalidated && !cancellation.IsCancellationRequested && ticket.Generation == generation && !observationsBlocked &&
                    ticket.Observation == Interlocked.Read(ref observationVersion),
                    publish
                );
        }

        internal IList<StorageEntry> CompleteRead(ReadTicket ticket, IList<StorageEntry> values, string checkedUtc = null)
        {
            long ignored;
            return CompleteRead(ticket, values, out ignored, checkedUtc);
        }

        internal IList<StorageEntry> CompleteRead(ReadTicket ticket, IList<StorageEntry> values, out long version, string checkedUtc = null)
        {
            InventoryFolderRecord ignored;
            return CompleteRead(ticket, values, out version, out ignored, checkedUtc);
        }

        internal IList<StorageEntry> CompleteRead(ReadTicket ticket, IList<StorageEntry> values, out long version, out InventoryFolderRecord record, string checkedUtc = null)
        {
            ApplicationChange changed = null;
            IList<StorageEntry> entries = values.ToList();
            try
            {
                lock (gate)
                {
                    if (closed || ticket.Invalidated || ticket.Generation != generation)
                        throw new OperationCanceledException();
                    changed = FlushLocked();
                    if (ticket.Invalidated)
                        throw new OperationCanceledException();
                    foreach (var change in recent.Where(change => change.Sequence > ticket.Sequence && FolderKey(change.Result.DestinationId, change.Result.Folder) == FolderKey(ticket.Id, ticket.Folder)))
                        Apply(entries, change.Result);
                    record = SaveEntries(ticket.Id, ticket.Folder, entries, checkedUtc ?? DateTime.UtcNow.ToString("o"), "");
                    folderVersions.TryGetValue(FolderKey(ticket.Id, ticket.Folder), out version);
                }

                return entries;
            }
            finally
            {
                if (changed != null)
                    Publish(changed);
                ArmPump(80);
            }
        }

        internal void FailRead(ReadTicket ticket, string error, string checkedUtc = null)
        {
            lock (gate)
            {
                if (closed || ticket.Invalidated || ticket.Generation != generation)
                    throw new OperationCanceledException();
                var old = catalog.GetInventoryFolder(ticket.Id, ticket.Folder);
                var next = new InventoryFolderRecord
                {
                    DestinationId = ticket.Id,
                    Folder = ticket.Folder,
                    Json = old == null ? "[]" : old.Json,
                    CheckedUtc = checkedUtc ?? DateTime.UtcNow.ToString("o"),
                    Error = error
                };
                catalog.SaveInventoryFolder(next, old);
                FolderChanged(ticket.Id, ticket.Folder, false, next);
            }

            ArmPump(80);
        }

        internal void EntryCommitted(RepositoryObjectResult result)
        {
            EntriesCommitted(new[] { result });
        }

        internal void EntriesCommitted(IList<RepositoryObjectResult> results)
        {
            if (results.Count == 0)
                return;
            Invalidate(results);
            lock (gate)
            {
                if (closed)
                    throw new ObjectDisposedException("RepositoryRuntime");
                pendingPresentation = FlushLocked();
                long sequence = Interlocked.Increment(ref resultSequence);
                ApplyResults(results.Select(result => new Stamped { Result = result, Sequence = sequence }).ToList());
            }

            ArmPump(80);
        }

        internal void PruneInventory(string id, string scan)
        {
            lock (gate)
            {
                if (!reads.Any(read => read.Id == id))
                    catalog.PruneInventory(id, scan);
            }
        }

        internal void EndRead(ReadTicket ticket)
        {
            lock (gate)
            {
                reads.Remove(ticket);
                Trim();
            }
        }

        private void Trim()
        {
            if (reads.Count == 0)
                recent.Clear();
            else
            {
                long minimum = reads.Min(read => read.Sequence);
                recent.RemoveAll(change => change.Sequence <= minimum);
            }
        }

        internal void Reset(Action replace = null)
        {
            lock (gate)
            {
                generation++;
                Interlocked.Increment(ref observationVersion);
                reads.Clear();
                recent.Clear();
                if (directories != null)
                    directories.Clear();
                if (replace != null)
                    replace();
                lastRecoveryRevision = catalog.RecoveryRevision;
                localChanges = CatalogChange.None;
                localSources.Clear();
                localFolders.Clear();
                folderVersions.Clear();
                pendingPresentation = null;
                Committed ignored;
                while (queue.TryDequeue(out ignored))
                {
                }
            }

            Publish(new ApplicationChange(CatalogChange.Workspace | CatalogChange.Topology | CatalogChange.Archives | CatalogChange.Rules | CatalogChange.Settings | CatalogChange.Schedules | CatalogChange.Inventory));
        }

        public void Dispose()
        {
            catalog.RepositoryCommitted -= Enqueue;
            timer.Dispose();
            Flush();
            lock (gate)
                closed = true;
        }
    }

    public sealed partial class DesktopBackend
    {
        private readonly DirectoryRefreshCache directoryRefresh = new DirectoryRefreshCache();
        private RepositoryRuntime repositoryRuntime;
        public event Action<ApplicationChange> ApplicationChanged;
        private void InitializeRepositoryRuntime()
        {
            repositoryRuntime = new RepositoryRuntime(Catalog, directoryRefresh);
            repositoryRuntime.Changed += change =>
            {
                var observers = ApplicationChanged;
                if (observers != null)
                    observers(change);
            };
        }

        public void FlushRepositoryResults()
        {
            if (repositoryRuntime != null)
                repositoryRuntime.Flush();
        }

        internal void SetRepositoryActivity(bool active)
        {
            if (repositoryRuntime != null)
                repositoryRuntime.SetActivity(active);
        }

        private void RepositoryConnectionChanged(string id)
        {
            if (repositoryRuntime != null)
                repositoryRuntime.ConnectionChanged(id);
        }
    }
}
