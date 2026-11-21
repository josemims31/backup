using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Backup.Core
{
    public sealed class IntegrityObservation
    {
        public long ArchiveId;
        public string GroupId;
        public IList<IntegrityFinding> Findings;
    }

    public sealed partial class BackupCatalog
    {
        public ILookup<long, StoredCopy> GetIntegrityCopies(IEnumerable<long> archiveIds)
        {
            var ids = archiveIds.Distinct().ToArray();
            var copies = new List<KeyValuePair<long, StoredCopy>>();
            lock (gate)
                for (int start = 0; start < ids.Length; start += 256)
                {
                    var arguments = ids.Skip(start).Take(256).Cast<object>().ToArray();
                    var parameters = string.Join(",", Enumerable.Repeat("?", arguments.Length));
                    var rows = database.Query("SELECT o.archive_id,c.group_id,c.destination_id,o.name,o.size FROM objects o JOIN object_copies c ON c.name=o.name " +
                        "WHERE o.archive_id IN (" + parameters + ") AND c.state<>'deleted' ORDER BY o.archive_id,c.group_id,o.name", arguments);
                    foreach (var row in rows)
                        copies.Add(new KeyValuePair<long, StoredCopy>((long)row[0], new StoredCopy {
                            GroupId = (string)row[1],
                            DestinationId = (string)row[2],
                            Object = new ObjectRecord { Name = (string)row[3], Size = (long)row[4] }
                        }));
                }
            return copies.ToLookup(copy => copy.Key, copy => copy.Value);
        }

        public void RecordIntegrity(long id, string groupId, IList<IntegrityFinding> findings)
        {
            RecordIntegrityBatch(new[] { new IntegrityObservation { ArchiveId = id, GroupId = groupId, Findings = findings } }, CancellationToken.None);
        }

        public void RecordIntegrityBatch(IList<IntegrityObservation> observations, CancellationToken cancellation)
        {
            if (observations.Count == 0)
                return;
            Transaction(delegate
            {
                foreach (var archive in observations.GroupBy(item => item.ArchiveId))
                {
                    cancellation.ThrowIfCancellationRequested();
                    RecordArchiveIntegrity(archive.Key, archive);
                }
                cancellation.ThrowIfCancellationRequested();
                return 0;
            });
        }

        private void RecordArchiveIntegrity(long id, IEnumerable<IntegrityObservation> observations)
        {
            var archive = database.Query("SELECT task_id,status,target_copies,EXISTS(SELECT 1 FROM archive_pools p WHERE p.archive_id=a.id) FROM archives a WHERE id=?", id).Single();
            RepositoryStatusChanged(CatalogChange.Archives, (string)archive[0]);
            bool volume = (long)archive[3] != 0;
            var copies = database.Query("SELECT c.destination_id,c.name,c.group_id,c.state FROM objects o JOIN object_copies c ON c.name=o.name " +
                "WHERE o.archive_id=? AND c.state<>'deleted'", id).ToDictionary(row => (string)row[0] + "\0" + (string)row[1], StringComparer.Ordinal);
            foreach (var observation in observations)
                RecordIntegrityGroup(id, volume, copies, observation);

            string previous = (string)archive[1];
            if (previous != "complete" && previous != "repair-required" && previous != "redundancy-shortfall")
                return;
            long complete;
            bool damaged;
            if (volume)
            {
                complete = VolumeStatistics(id)[id][1];
                damaged = copies.Values.Any(row => (string)row[3] == "missing" || (string)row[3] == "size-or-name-mismatch" || (string)row[3] == "content-damaged") ||
                    Scalar("SELECT EXISTS(SELECT 1 FROM volume_transfers t JOIN objects o ON o.name=t.name WHERE o.archive_id=? AND " +
                        "(t.state LIKE 'checked-%' OR t.state LIKE 'damaged-%'))", id) != 0;
            }
            else
            {
                var placements = database.Query("SELECT status,count(*) FROM placements WHERE archive_id=? GROUP BY status", id)
                    .ToDictionary(row => (string)row[0], row => (long)row[1]);
                placements.TryGetValue("complete", out complete);
                damaged = placements.ContainsKey("repair-required");
            }

            string status = damaged ? "repair-required" : complete >= (long)archive[2] ? "complete" : "redundancy-shortfall";
            if (status != previous)
                database.Execute("UPDATE archives SET status=? WHERE id=?", status, id);
        }

        private void RecordIntegrityGroup(long id, bool volume, IDictionary<string, object[]> copies, IntegrityObservation observation)
        {
            string group = observation.GroupId;
            var findings = observation.Findings;
            if (!volume && Scalar("SELECT count(*) FROM object_copies c JOIN objects o ON o.name=c.name JOIN placements p ON p.archive_id=o.archive_id " +
                "AND p.group_id=c.group_id AND p.destination_id=c.destination_id WHERE o.archive_id=? AND c.group_id=? AND c.state<>'deleted'", id, group) != findings.Count)
                throw new InvalidOperationException("Incomplete integrity observation.");
            RepositoryDestinationsChanged(findings.Select(finding => finding.Destination));
            var changes = new List<object[]>();
            foreach (var finding in findings)
            {
                object[] copy;
                if (!copies.TryGetValue(finding.Destination + "\0" + finding.Name, out copy) || (string)copy[2] != group)
                    throw new IOException("完整性记录与备份副本不匹配。");
                string previous = (string)copy[3];
                string state = ObservedCopyState(finding, previous);
                if (state == previous)
                    continue;
                changes.Add(new object[] { state, finding.Name, finding.Destination });
                copy[3] = state;
            }
            if (changes.Count > 0)
                database.ExecuteMany("UPDATE object_copies SET state=? WHERE name=? AND destination_id=?", changes);

            bool repaired = findings.Any(finding => finding.RepairApplied);
            string predicate = volume ? "reserved=0 AND (status IN ('complete','repair-required','unavailable','failed')" :
                "(status IN ('complete','repair-required','unavailable')";
            database.ExecuteMany("UPDATE placements SET status=? WHERE archive_id=? AND group_id=? AND destination_id=? AND " +
                predicate + " OR ?=1 AND status NOT IN ('detached','retired','cleaned')) AND status<>?",
                findings.GroupBy(finding => finding.Destination).Select(member =>
                {
                    string state = member.Any(finding => finding.RequiresManualRepairConfirmation) ? "repair-required" :
                        member.Any(finding => finding.Status.StartsWith("unavailable", StringComparison.Ordinal)) ? "unavailable" : "complete";
                    return new object[] { state, id, group, member.Key, repaired, state };
                }));
        }
    }
}
