using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        private IEnumerable<SourceItem> EligibleBackupSources(IEnumerable<SourceItem> indexed)
        {
            var exclusions = new BackupExclusions(Rules());
            var protectedRoots = ProtectedRoots().Select(path => Path.GetFullPath(path).TrimEnd('\\')).ToArray();
            return indexed.GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).Where(item => exclusions.Match(item.Path) == null &&
                !protectedRoots.Any(root => Under(item.Path, root)));
        }

        private DesktopBackupResult UnchangedTaskBackup(BackupTaskDefinition task, IList<SourceItem> indexed)
        {
            BackupNode latest;
            NodeFileDatabase previous;
            return UnchangedTaskBackup(task, indexed, out latest, out previous);
        }

        private DesktopBackupResult UnchangedTaskBackup(BackupTaskDefinition task, IList<SourceItem> indexed, out BackupNode latest, out NodeFileDatabase previous)
        {
            latest = Catalog.GetNodes(task.Id).FirstOrDefault();
            previous = latest == null ? null : ReadNodeDatabase(latest);
            var unchanged = CompareUnchangedBackup(EligibleBackupSources(indexed).Select(item => new NodeFile { Path = item.ArchivePath, Size = item.Size, Modified = item.Modified }).ToList(), previous);
            return unchanged != null && TaskTargetsRecorded(task, latest, previous) ? unchanged : null;
        }

        private DesktopBackupResult UnchangedTaskBackup(BackupTaskDefinition task, IList<NodeFile> current)
        {
            var latest = Catalog.GetNodes(task.Id).FirstOrDefault();
            var previous = latest == null ? null : ReadNodeDatabase(latest);
            var unchanged = CompareUnchangedBackup(current, previous);
            return unchanged != null && TaskTargetsRecorded(task, latest, previous) ? unchanged : null;
        }

        private static DesktopBackupResult CompareUnchangedBackup(IList<NodeFile> current, NodeFileDatabase previous)
        {
            // Compare effective scope and metadata to the last successful version.
            if (previous == null || current.Count != previous.Files.Count)
                return null;
            var saved = previous.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var item in current)
            {
                NodeFile file;
                if (!saved.TryGetValue(item.Path, out file) || file.Path != item.Path || file.Size != item.Size || file.Modified != item.Modified)
                    return null;
            }

            return new DesktopBackupResult
            {
                NoChanges = true,
                SkippedUnchanged = current.Count,
                CatalogSkippedUnchanged = true,
                Sources = new List<SourceItem>(),
                ObservedSources = new List<SourceItem>()
            };
        }

        private sealed class MissingSourceCheck
        {
            internal string Path, ArchivePath, Root;
        }

        private sealed class PreparedSourceList
        {
            internal IList<SourceDefinition> Sources;
            internal IEnumerable<SourceItem> Indexed;
        }

        private readonly object preparedSourceGate = new object();
        private readonly Dictionary<string, PreparedSourceList> preparedSourceLists = new Dictionary<string, PreparedSourceList>();
        private void ReconcileBackupSources(BackupTaskDefinition task, PreparedSourceList prepared, DesktopBackupResult result, Action<DesktopProgress> progress)
        {
            var workspace = WorkspaceMigration.Load(Catalog);
            var roots = workspace.Sources.Where(source => !source.Deleted && task.SourceIds.Contains(source.Id)).Select(source => source.Location).ToList();
            var exclusions = new BackupExclusions(Rules());
            var protectedRoots = ProtectedRoots();
            var scannedRoots = prepared == null ? null : SourceCheckRoots(prepared.Sources);
            var indexed = prepared == null ? null : new HashSet<string>(prepared.Indexed.Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
            var removed = new HashSet<string>(StringComparer.Ordinal);
            var missing = new Dictionary<string, MissingSourceCheck>(StringComparer.Ordinal);
            var observed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in Catalog.GetKnownSourceFiles(task.Id))
            {
                if (ActiveBackup != null)
                    ActiveBackup.Checkpoint();
                if (StopRequested)
                    return;
                string path = SourcePath(row);
                if (!roots.Any(root => Under(path, root)) || !task.Selection.Includes(path) || exclusions.Match(path) != null || protectedRoots.Any(root => Under(path, root)))
                    removed.Add(row.Path);
                else if (indexed != null)
                {
                    if (indexed.Contains(path))
                        observed.Add(row.Path);
                    else
                        AddMissingSourceCheck(missing, row.Path, path, scannedRoots);
                }
            }

            var confirmed = ConfirmMissingSources(missing.Values.Where(item => item.Root != null &&
                !removed.Contains(item.ArchivePath) && !observed.Contains(item.ArchivePath)).ToList(), progress);
            if (ActiveBackup != null)
                ActiveBackup.Checkpoint();
            if (StopRequested)
                return;
            result.RemovedFromScope = removed.Count;
            result.ConfirmedDeleted += confirmed.Count;
            removed.UnionWith(confirmed);
            // Keep previous packages held until the new version is published.
            Catalog.MarkDeletedPaths(task.Id, removed, true);
        }

        private static IList<string> SourceCheckRoots(IEnumerable<SourceDefinition> sources)
        {
            return sources.Where(source => !source.Deleted && !string.IsNullOrWhiteSpace(source.Location))
                .Select(source => source.Location).OrderByDescending(root => root.Length).ToList();
        }

        private static void AddMissingSourceCheck(IDictionary<string, MissingSourceCheck> checks, string archivePath, string path, IList<string> roots)
        {
            string root = roots.FirstOrDefault(candidate => Under(path, candidate));
            MissingSourceCheck prior;
            if (!checks.TryGetValue(archivePath, out prior))
                checks.Add(archivePath, new MissingSourceCheck { Path = path, ArchivePath = archivePath, Root = root });
            else if (root == null || !string.Equals(path, prior.Path, StringComparison.OrdinalIgnoreCase))
                prior.Root = null;
        }

        private IList<MissingSourceCheck> MissingSourceChecks(BackupTaskDefinition task, IList<SourceDefinition> sources, IEnumerable<SourceItem> indexed)
        {
            var present = new HashSet<string>(indexed.Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
            var roots = SourceCheckRoots(sources);
            var checks = new Dictionary<string, MissingSourceCheck>(StringComparer.Ordinal);
            var observed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in Catalog.GetKnownSourceFiles(task.Id))
            {
                if (ActiveBackup != null)
                    ActiveBackup.Checkpoint();
                string path = SourcePath(row);
                if (present.Contains(path))
                    observed.Add(row.Path);
                else
                    AddMissingSourceCheck(checks, row.Path, path, roots);
            }
            return checks.Values.Where(item => item.Root != null && !observed.Contains(item.ArchivePath)).ToList();
        }

        private void PrepareSourceList(BackupTaskDefinition task, IList<SourceDefinition> sources, IEnumerable<SourceItem> indexed)
        {
            lock (preparedSourceGate)
                preparedSourceLists[task.Id] = new PreparedSourceList { Sources = sources, Indexed = indexed };
        }

        private PreparedSourceList TakePreparedSourceList(string taskId)
        {
            lock (preparedSourceGate)
            {
                PreparedSourceList prepared;
                preparedSourceLists.TryGetValue(taskId, out prepared);
                preparedSourceLists.Remove(taskId);
                return prepared;
            }
        }

        private void ClearPreparedSourceChecks()
        {
            lock (preparedSourceGate)
                preparedSourceLists.Clear();
        }

        private ISet<string> ConfirmMissingSources(IList<MissingSourceCheck> checks, Action<DesktopProgress> progress = null)
        {
            var confirmed = new HashSet<string>(StringComparer.Ordinal);
            if (checks.Count == 0)
                return confirmed;
            var directories = new SourceDirectoryChecks(this);
            Action<int> report = count =>
            {
                if (progress != null)
                    progress(new DesktopProgress {
                        Message = "正在核对源文件是否已删除 · " + count + " / " + checks.Count,
                        CheckedSourceDeletions = count,
                        TotalSourceDeletions = checks.Count
                    });
            };
            report(0);
            var interval = System.Diagnostics.Stopwatch.StartNew();
            for (int index = 0; index < checks.Count; index++)
            {
                OperationCancellation.ThrowIfCancellationRequested();
                if (ActiveBackup != null)
                    ActiveBackup.Checkpoint();
                if (StopRequested)
                    break;
                var item = checks[index];
                if (directories.IsMissing(item.Path, item.Root))
                    confirmed.Add(item.ArchivePath);
                if (index + 1 == checks.Count || interval.ElapsedMilliseconds >= 250)
                {
                    report(index + 1);
                    interval.Restart();
                }
            }
            return confirmed;
        }

#if UI_TEST
        public Func<SourceItem, SourceItem> TestSourceMetadata;
        public Action<string> TestSourceMetadataRead;
#endif
        private SourceItem ReadSourceMetadata(SourceItem original)
        {
#if UI_TEST
            if (TestSourceMetadataRead != null)
                TestSourceMetadataRead(original.Path);
            if (TestSourceMetadata != null)
                return TestSourceMetadata(original);
#endif
            return SourceItem.FromFile(original.Path, original.ArchivePath);
        }

        private bool NeedsBackup(SourceItem item, bool forceBackup, IDictionary<string, FileBackupView> prior, StorageTopology topology, int target, IDictionary<long, bool> coverage, out bool attention)
        {
            attention = false;
            FileBackupView previous;
            if (forceBackup || !prior.TryGetValue(item.ArchivePath, out previous) || previous.ArchivePath != item.ArchivePath || previous.Size != item.Size ||
                previous.Modified != item.Modified)
                return true;
            // New current content may be backed up again, but never overwrite the
            // identity of a historical archive whose healthy copies have been lost.
            if (previous.ValidCopies == 0)
                return true;
            bool covered;
            if (!coverage.TryGetValue(previous.ArchiveId, out covered))
            {
                covered = Catalog.CoversArchivePolicy(previous.ArchiveId, topology, target);
                coverage[previous.ArchiveId] = covered;
            }

            if (!covered && (previous.Status == "complete" || !ArchiveTargetsRecorded(previous.ArchiveId,
                topology.PolicyRoots ?? topology.Groups.Where(group => !group.Retired).Select(group => group.Id))))
                return true;
            attention = previous.Status != "complete" || !covered;
            return false;
        }

        private SourceItem ReadBackupSource(
            SourceItem original,
            DesktopBackupResult result,
            string taskId,
            bool forceBackup,
            Action<SourceItem> sourceMissing,
            ArchiveRootLayout layout,
            IDictionary<string, FileBackupView> prior,
            StorageTopology topology,
            int target,
            IDictionary<long, bool> coverage,
            Func<SourceItem, TaskSourcePolicy> sourcePolicy,
            SourceDirectoryChecks missingDirectories,
            IList<string> sourceRoots
        )
        {
            var control = ActiveBackup;
            control.Checkpoint();
            if (StopRequested)
            {
                result.Stopped = true;
                return null;
            }

            SourceItem item;
            try
            {
                item = ReadSourceMetadata(original);
            }
            catch (FileNotFoundException)
            {
                if (forceBackup && sourceMissing == null)
                    throw;
                if (!ConfirmMissingBackupSource(original, taskId, sourceMissing, missingDirectories, sourceRoots))
                {
                    result.Stopped = true;
                    return null;
                }

                result.ConfirmedDeleted++;
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                if (forceBackup && sourceMissing == null)
                    throw;
                if (!ConfirmMissingBackupSource(original, taskId, sourceMissing, missingDirectories, sourceRoots))
                {
                    result.Stopped = true;
                    return null;
                }

                result.ConfirmedDeleted++;
                return null;
            }

            control.Checkpoint();
            item.EntryPath = layout == null ? original.EntryPath : layout.Relative(item.Path, item.ArchivePath);
            item.VerifyContentDuringRead = false;
            result.ObservedSources.Add(item);
            bool attention;
            if (NeedsBackup(item, forceBackup, prior, topology, target, coverage, sourcePolicy, out attention))
                return item;
            result.SkippedUnchanged++;
            if (attention)
                result.AttentionUnchanged++;
            return null;
        }

        private IEnumerable<IList<SourceItem>> ReadBackupBatches(
            IList<IList<SourceItem>> planned,
            long limit,
            DesktopBackupResult result,
            string taskId,
            bool forceBackup,
            Action<SourceItem> sourceMissing,
            WorkspaceDefinition workspace,
            ArchiveRootLayout layout,
            IDictionary<string, FileBackupView> prior,
            StorageTopology topology,
            int target,
            IDictionary<long, bool> coverage,
            Action<long> adjustTotal,
            Func<SourceItem, TaskSourcePolicy> sourcePolicy
        )
        {
            var current = new List<SourceItem>();
            var missingDirectories = new SourceDirectoryChecks(this);
            var sourceRoots = SourceCheckRoots(workspace.Sources);
            long bytes = 0;
            string currentPolicy = null;
            foreach (var group in planned)
                foreach (var original in group)
                {
                    var item = ReadBackupSource(original, result, taskId, forceBackup, sourceMissing, layout, prior, topology, target, coverage, sourcePolicy, missingDirectories, sourceRoots);
                    if (result.Stopped)
                        yield break;
                    adjustTotal((item == null ? 0 : item.Size) - original.Size);
                    if (item == null)
                        continue;
                    string key = sourcePolicy == null ? "" : sourcePolicy(item).Key;
                    if (current.Count > 0 && key != currentPolicy)
                    {
                        yield return current;
                        current = new List<SourceItem>();
                        bytes = 0;
                    }
                    currentPolicy = key;
                    if (item.Size > limit)
                    {
                        yield return new[]
                        {
                            item
                        };
                        continue;
                    }

                    if (current.Count > 0 && bytes > limit - item.Size)
                    {
                        yield return current;
                        current = new List<SourceItem>();
                        bytes = 0;
                    }

                    current.Add(item);
                    bytes += item.Size;
                }

            if (current.Count > 0)
                yield return current;
        }

        private bool ConfirmMissingBackupSource(SourceItem source, string taskId, Action<SourceItem> sourceMissing, SourceDirectoryChecks directories, IList<string> sourceRoots)
        {
            ActiveBackup.Checkpoint();
            if (StopRequested)
                return false;
            bool missing = directories.IsMissing(source.Path, sourceRoots.FirstOrDefault(root => Under(source.Path, root)));
            ActiveBackup.Checkpoint();
            if (StopRequested)
                return false;
            if (!missing)
                throw new IOException("无法确认源文件已删除，旧包保留：" + source.Path);
            if (sourceMissing != null)
                sourceMissing(source);
            else
                Catalog.MarkDeleted(taskId, source.ArchivePath, true);
            return true;
        }
    }
}
