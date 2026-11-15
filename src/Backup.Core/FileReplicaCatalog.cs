using System;
using System.Collections.Generic;
using System.Linq;

namespace Backup.Core
{
    public sealed partial class BackupCatalog
    {
        private static List<FileBackupView> MergeFileReplicaViews(IList<FileBackupView> views)
        {
            var result = new List<FileBackupView>();
            foreach (var group in views.GroupBy(view => view.ArchivePath, StringComparer.OrdinalIgnoreCase))
            {
                var copies = group.GroupBy(view => view.ArchiveId).Select(items => items.First()).ToList();
                var first = copies[0];
                if (copies.Count > 1)
                {
                    first.TargetCopies = copies.Sum(view => view.TargetCopies);
                    first.ValidCopies = copies.Sum(view => view.ValidCopies);
                    first.UnavailableCopies = copies.Sum(view => view.UnavailableCopies);
                    first.PendingCopies = copies.Sum(view => view.PendingCopies);
                    first.FailedCopies = copies.Sum(view => view.FailedCopies);
                    first.Locations = string.Join("；", copies.Select(view => view.Locations).Where(value => !string.IsNullOrEmpty(value)).Distinct());
                    if (first.ValidCopies < first.TargetCopies && first.Status == "complete")
                        first.Status = "repair-required";
                }
                result.Add(first);
            }
            return result;
        }

        private void MergeWorkingReplicaViews(string taskId, List<FileBackupView> views)
        {
            var rows = database.Query("SELECT r.archive_id,r.path,r.replica_id FROM file_work.file_replicas r JOIN archives a ON a.id=r.archive_id " +
                "JOIN archive_files f ON f.archive_id=r.archive_id AND f.path=r.path WHERE a.task_id=? AND f.state='current'", taskId);
            if (rows.Count == 0)
                return;
            var originals = views.ToDictionary(view => view.ArchiveId + "\n" + view.ArchivePath, StringComparer.Ordinal);
            var replicas = new Dictionary<long, ArchiveSummary>();
            var locations = new Dictionary<long, string>();
            var groups = LoadTopology().Groups.ToDictionary(group => group.Id, group => group.Name);
            foreach (var row in rows)
            {
                FileBackupView primary;
                if (!originals.TryGetValue(row[0] + "\n" + (string)row[1], out primary))
                    continue;
                long id = (long)row[2];
                ArchiveSummary archive;
                if (!replicas.TryGetValue(id, out archive))
                {
                    replicas[id] = archive = GetArchive(id);
                    locations[id] = string.Join("；", VolumePlacements(id).Where(place => place.StoredBytes > 0).Select(place =>
                        (groups.ContainsKey(place.GroupId) ? groups[place.GroupId] : place.GroupId) + " → " + place.DestinationId).Distinct());
                }
                views.Add(new FileBackupView { ArchiveId = id, ArchiveName = archive.ArchiveName, ArchivePath = primary.ArchivePath, Size = primary.Size, Modified = primary.Modified,
                    TargetCopies = archive.TargetCopies, ValidCopies = archive.CurrentValidCopies, Status = archive.Status,
                    Locations = locations[id] });
            }
            var merged = MergeFileReplicaViews(views);
            views.Clear();
            views.AddRange(merged);
        }

        private void SaveWorkingReplicas(NodeFileDatabase node)
        {
            database.ExecuteMany("INSERT OR REPLACE INTO file_work.file_replicas VALUES(?,?,?,?,?)",
                node.Files.Where(file => file.Replicas != null).SelectMany(file => file.Replicas.Select(replica => new object[] {
                    file.ArchiveId, file.Path, replica.ArchiveId, replica.EntryPath ?? "", replica.ContentPath ?? ""
                })));
            database.ExecuteMany("INSERT OR REPLACE INTO archive_files(archive_id,path,size,modified,state,source_path,entry_path,content_path) VALUES(?,?,?,?,'expired',?,?,?)",
                node.Files.Where(file => file.Replicas != null).SelectMany(file => file.Replicas.Select(replica => new object[] {
                    replica.ArchiveId, file.Path, file.Size, file.Modified, file.SourcePath ?? "", replica.EntryPath ?? "", replica.ContentPath ?? ""
                })));
        }

        private void AttachWorkingReplicas(NodeFileDatabase node, string taskId)
        {
            var rows = database.Query("SELECT r.archive_id,r.path,r.replica_id,r.entry_path,r.content_path FROM file_work.file_replicas r " +
                "JOIN archives a ON a.id=r.archive_id JOIN archive_files f ON f.archive_id=r.archive_id AND f.path=r.path WHERE a.task_id=? AND f.state='current'", taskId);
            if (rows.Count == 0)
                return;
            var files = node.Files.ToDictionary(file => file.ArchiveId + "\n" + file.Path, StringComparer.Ordinal);
            foreach (var row in rows)
            {
                NodeFile file;
                if (!files.TryGetValue(row[0] + "\n" + (string)row[1], out file))
                    continue;
                if (file.Replicas == null)
                    file.Replicas = new List<NodeFile>();
                file.Replicas.Add(new NodeFile { ArchiveId = (long)row[2], Path = file.Path, Size = file.Size, Modified = file.Modified,
                    SourcePath = file.SourcePath, EntryPath = (string)row[3], ContentPath = (string)row[4] });
            }
        }

        private void SaveSupplementalFileBasis(long archiveId, IList<SourceItem> sources)
        {
            if (!sources.Any(source => source.PriorBackupCopies != null))
                return;
            // Persist the link with the upload reservation so a shutdown cannot
            // turn a newly confirmed package into a replacement for its peers.
            database.ExecuteMany("INSERT INTO file_work.file_replicas VALUES(?,?,?,?,?)",
                sources.Where(source => source.PriorBackupCopies != null).SelectMany(source => source.PriorBackupCopies.Select(copy => new object[] {
                    archiveId, source.ArchivePath, copy.ArchiveId, copy.EntryPath ?? "", copy.ContentPath ?? ""
                })));
            database.Execute("UPDATE archive_files SET state='supplementing' WHERE archive_id=? AND path IN(SELECT path FROM file_work.file_replicas WHERE archive_id=?)",
                archiveId, archiveId);
        }

        private HashSet<string> ExistingReplicaDestinations(IList<SourceItem> sources)
        {
            var destinations = new HashSet<string>(StringComparer.Ordinal);
            foreach (long id in sources.Where(source => source.PriorBackupCopies != null)
                .SelectMany(source => source.PriorBackupCopies).Select(copy => copy.ArchiveId).Distinct())
                destinations.UnionWith(database.Query(
                    "SELECT destination_id FROM archive_pool_members WHERE archive_id=? UNION SELECT destination_id FROM placements WHERE archive_id=? AND stored>0",
                    id, id).Select(row => (string)row[0]));
            return destinations;
        }

        private void ActivateSupplementalFiles(long archiveId)
        {
            database.Execute(
                "UPDATE archive_files SET state=CASE WHEN EXISTS(SELECT 1 FROM archive_files newer JOIN archives a ON a.id=newer.archive_id " +
                "WHERE newer.path=archive_files.path AND a.id>? AND a.task_id=(SELECT task_id FROM archives WHERE id=?) AND " +
                "newer.state IN ('current','deleted') AND a.status IN ('complete','repair-required','redundancy-shortfall')) " +
                "THEN 'expired' ELSE 'current' END WHERE archive_id=? AND state='supplementing'",
                archiveId, archiveId, archiveId);
        }
    }
}
