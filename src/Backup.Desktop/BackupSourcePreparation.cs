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
            return CompareUnchangedBackup(EligibleBackupSources(indexed).Select(item => new NodeFile { Path = item.ArchivePath, Size = item.Size, Modified = item.Modified }).ToList(), previous);
        }

        private DesktopBackupResult UnchangedTaskBackup(BackupTaskDefinition task, IList<NodeFile> current)
        {
            var latest = Catalog.GetNodes(task.Id).FirstOrDefault();
            return CompareUnchangedBackup(current, latest == null ? null : ReadNodeDatabase(latest));
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

        private readonly object preparedSourceGate = new object();
        private readonly Dictionary<string, Func<IList<MissingSourceCheck>>> preparedSourceChecks = new Dictionary<string, Func<IList<MissingSourceCheck>>>();
        private int ReconcileTaskBackupScope(BackupTaskDefinition task)
        {
            var workspace = WorkspaceMigration.Load(Catalog);
            var roots = workspace.Sources.Where(source => !source.Deleted && task.SourceIds.Contains(source.Id)).Select(source => source.Location).ToList();
            var exclusions = new BackupExclusions(Rules());
            var protectedRoots = ProtectedRoots();
            var removed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in Catalog.GetKnownSourceFiles(task.Id))
            {
                if (ActiveBackup != null)
                    ActiveBackup.Checkpoint();
                if (StopRequested)
                    return 0;
                string path = SourcePath(row);
                if (!roots.Any(root => Under(path, root)) || !task.Selection.Includes(path) || exclusions.Match(path) != null || protectedRoots.Any(root => Under(path, root)))
                    removed.Add(row.Path);
            }

            if (ActiveBackup != null)
                ActiveBackup.Checkpoint();
            if (StopRequested)
                return 0;
            // Removing a file from the task is a logical deletion. Keep its old package
            // held until the catalog publication/cleanup workflow has committed it.
            Catalog.MarkDeletedPaths(task.Id, removed, true);
            return removed.Count;
        }

        private IList<MissingSourceCheck> MissingSourceChecks(BackupTaskDefinition task, IList<SourceDefinition> sources, IEnumerable<SourceItem> indexed)
        {
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in indexed)
                present.Add(item.Path);
            var checks = new List<MissingSourceCheck>();
            foreach (var row in Catalog.GetKnownSourceFiles(task.Id))
            {
                if (ActiveBackup != null)
                    ActiveBackup.Checkpoint();
                string path = SourcePath(row);
                if (present.Contains(path))
                    continue;
                foreach (var source in sources)
                    if (path.StartsWith(source.Location.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        checks.Add(new MissingSourceCheck { Path = path, ArchivePath = row.Path, Root = source.Location });
                        break;
                    }
            }

            return checks;
        }

        private void PrepareMissingSourceChecks(BackupTaskDefinition task, IList<SourceDefinition> sources, IEnumerable<SourceItem> indexed)
        {
            lock (preparedSourceGate)
                preparedSourceChecks[task.Id] = () => task.RecoveryPendingConfirmation ? new List<MissingSourceCheck>() : MissingSourceChecks(task, sources, indexed);
        }

        private IList<MissingSourceCheck> TakeMissingSourceChecks(string taskId)
        {
            Func<IList<MissingSourceCheck>> checks;
            lock (preparedSourceGate)
            {
                if (!preparedSourceChecks.TryGetValue(taskId, out checks))
                    return new MissingSourceCheck[0];
                preparedSourceChecks.Remove(taskId);
            }

            return checks();
        }

        private void DiscardMissingSourceChecks(string taskId)
        {
            lock (preparedSourceGate)
                preparedSourceChecks.Remove(taskId);
        }

        private void ClearPreparedSourceChecks()
        {
            lock (preparedSourceGate)
                preparedSourceChecks.Clear();
        }

        private int ConfirmMissingSources(string taskId, IList<MissingSourceCheck> checks)
        {
            int confirmed = 0;
            var unavailable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var checkedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in checks)
            {
                if (ActiveBackup != null)
                    ActiveBackup.Checkpoint();
                if (StopRequested)
                    break;
                if (unavailable.Contains(item.Root))
                    continue;
                if (!checkedRoots.Contains(item.Root))
                {
                    try
                    {
                        if ((File.GetAttributes(item.Root) & FileAttributes.Directory) == 0)
                        {
                            unavailable.Add(item.Root);
                            continue;
                        }

                        checkedRoots.Add(item.Root);
                    }
                    catch (IOException)
                    {
                        unavailable.Add(item.Root);
                        continue;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        unavailable.Add(item.Root);
                        continue;
                    }
                }

                try
                {
                    File.GetAttributes(item.Path);
                }
                catch (FileNotFoundException)
                {
                    if (ConfirmDeleted(taskId, item.ArchivePath, item.Path, item.Root))
                        confirmed++;
                }
                catch (DirectoryNotFoundException)
                {
                    if (ConfirmDeleted(taskId, item.ArchivePath, item.Path, item.Root))
                        confirmed++;
                }
                catch (IOException)
                {
                    unavailable.Add(item.Root);
                }
                catch (UnauthorizedAccessException)
                {
                    unavailable.Add(item.Root);
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

            if (previous.Status == "complete" && !covered)
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
            WorkspaceDefinition workspace,
            ArchiveRootLayout layout,
            IDictionary<string, FileBackupView> prior,
            StorageTopology topology,
            int target,
            IDictionary<long, bool> coverage
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
                if (!ConfirmMissingBackupSource(original, taskId, sourceMissing, workspace))
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
                if (!ConfirmMissingBackupSource(original, taskId, sourceMissing, workspace))
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
            if (NeedsBackup(item, forceBackup, prior, topology, target, coverage, out attention))
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
            Action<long> adjustTotal
        )
        {
            var current = new List<SourceItem>();
            long bytes = 0;
            foreach (var group in planned)
                foreach (var original in group)
                {
                    var item = ReadBackupSource(original, result, taskId, forceBackup, sourceMissing, workspace, layout, prior, topology, target, coverage);
                    if (result.Stopped)
                        yield break;
                    adjustTotal((item == null ? 0 : item.Size) - original.Size);
                    if (item == null)
                        continue;
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

        private bool ConfirmMissingBackupSource(SourceItem source, string taskId, Action<SourceItem> sourceMissing, WorkspaceDefinition workspace)
        {
            ActiveBackup.Checkpoint();
            if (StopRequested)
                return false;
            ValidateMissingCompactionSource(source.Path, workspace);
            ActiveBackup.Checkpoint();
            if (StopRequested)
                return false;
            if (sourceMissing != null)
                sourceMissing(source);
            else
                Catalog.MarkDeleted(taskId, source.ArchivePath, true);
            return true;
        }
    }
}
