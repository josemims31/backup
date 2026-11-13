using System;
using System.Collections.Generic;
using System.Linq;

namespace Backup.Core
{
    public sealed class BackupIntegritySummary
    {
        public int Copies, RepairCopies, UnavailableCopies, PendingCopies, IncompleteArchives;
    }

    public sealed partial class BackupCatalog
    {
        public ISet<long> CurrentArchiveIds()
        {
            lock (gate)
                return new HashSet<long>(database.Query("SELECT DISTINCT archive_id FROM archive_files WHERE state='current'").Select(row => (long)row[0]));
        }

        public ISet<long> ArchiveIdsForTask(string taskId, bool includeCleaned = false)
        {
            lock (gate)
                return new HashSet<long>(database.Query("SELECT id FROM archives WHERE task_id=? AND (?=1 OR status<>'cleaned')", taskId, includeCleaned).Select(row => (long)row[0]));
        }

        private ISet<long> LocalRepairArchives(ISet<string> observedObjects)
        {
            // Only confirmed absence/damage can require a local rebuild. Unknown
            // connections and unfinished uploads are not evidence of lost data.
            var rows = database.Query(("SELECT o.archive_id,o.name FROM objects o JOIN archives a ON a.id=o.archive_id WHERE a.status IN " +
                "('complete','repair-required','redundancy-shortfall','failed','damaged') AND NOT EXISTS(SELECT 1 FROM object_copies c WHERE c.name=o.name AND c.state NOT IN " +
                "('missing','size-or-name-mismatch','content-damaged','deleted') AND EXISTS(SELECT 1 FROM placements p WHERE p.archive_id=a.id AND p.group_id=c.group_id AND " +
                "p.destination_id=c.destination_id)) AND NOT EXISTS(SELECT 1 FROM volume_transfers t WHERE t.name=o.name AND t.state<>'failed-upload' AND t.state NOT LIKE " +
                "'checked-%' AND t.state NOT LIKE 'damaged-%') AND NOT EXISTS(SELECT 1 FROM upload_temporaries t WHERE t.name=o.name)"));
            return new HashSet<long>(rows.Where(row => observedObjects == null || !observedObjects.Contains((string)row[1])).Select(row => (long)row[0]).Distinct().Where(HasCompleteArchiveManifest));
        }

        public BackupIntegritySummary GetRecordedIntegritySummary(ISet<string> taskIds = null, ISet<string> observedObjects = null)
        {
            lock (gate)
            {
                var result = new BackupIntegritySummary();
                var localRepair = LocalRepairArchives(observedObjects);
                var abnormalUploads = new HashSet<long>(database.Query("SELECT DISTINCT o.archive_id FROM volume_transfers t JOIN objects o ON o.name=t.name WHERE t.state LIKE 'damaged-%' OR t.state LIKE 'checked-%'")
                    .Select(row => (long)row[0]));
                // Retained historical versions use expired working-file rows too.
                // Count every active registered archive, not just current files.
                var rows = database.Query(("SELECT a.task_id,count(c.name),sum(CASE WHEN c.state IN ('missing','size-or-name-mismatch','content-damaged') THEN 1 ELSE 0 END),sum(CASE WHEN c.state LIKE " +
                    "'unavailable%' THEN 1 ELSE 0 END),0,a.status,a.id FROM archives a LEFT JOIN objects o ON o.archive_id=a.id LEFT JOIN object_copies c ON c.name=o.name AND " +
                    "c.state<>'deleted' AND EXISTS(SELECT 1 FROM placements p WHERE p.archive_id=a.id AND p.group_id=c.group_id AND p.destination_id=c.destination_id) WHERE " +
                    "a.status IN ('complete','repair-required','redundancy-shortfall') GROUP BY a.id"));
                foreach (var row in rows)
                {
                    if (taskIds != null && !taskIds.Contains(row[0] as string))
                        continue;
                    int repair = Convert.ToInt32(row[2]), unavailable = Convert.ToInt32(row[3]), pending = Convert.ToInt32(row[4]);
                    long id = (long)row[6];
                    bool local = localRepair.Contains(id);
                    if (local)
                    {
                        result.IncompleteArchives++;
                        repair = 0;
                    }
                    else if (((string)row[5] == "repair-required" || (string)row[5] == "redundancy-shortfall" && unavailable + pending == 0) && repair == 0 && !abnormalUploads.Contains(id))
                        repair = 1;
                    result.Copies += Convert.ToInt32(row[1]);
                    result.RepairCopies += repair;
                    result.UnavailableCopies += unavailable;
                    result.PendingCopies += pending;
                }

                foreach (var archive in InterruptedArchives(taskIds))
                {
                    int unavailable = (int)Scalar("SELECT count(*) FROM object_copies c JOIN objects o ON o.name=c.name WHERE o.archive_id=? AND c.state LIKE 'unavailable%'", archive.ArchiveId);
                    result.UnavailableCopies += unavailable;
                    if (localRepair.Contains(archive.ArchiveId) && (IsNodeArchive(archive.ArchiveId) || ArchiveHasCurrentFiles(archive.ArchiveId)))
                        result.IncompleteArchives++;
                    else if (archive.Status == "damaged")
                        result.RepairCopies++;
                    else if (unavailable == 0)
                        result.PendingCopies++;
                    result.Copies += GetCopies(archive.ArchiveId).Count;
                }

                result.RepairCopies += TemporaryProblemArchives(taskIds).Count(id => !localRepair.Contains(id));
                foreach (var row in database.Query(("SELECT a.task_id,t.state,count(*),p.status,a.id FROM volume_transfers t JOIN objects o ON o.name=t.name JOIN archives a ON a.id=o.archive_id JOIN placements p " +
                    "ON p.archive_id=a.id AND p.group_id=t.group_id AND p.destination_id=t.destination_id WHERE a.status<>'cleaned' AND t.state<>'checking' AND (t.state LIKE " +
                    "'pending-%' OR t.state LIKE 'damaged-%' OR t.state LIKE 'checked-%' OR a.status<>'writing' AND NOT EXISTS(SELECT 1 FROM archive_leases l WHERE " +
                    "l.archive_id=a.id)) AND NOT EXISTS(SELECT 1 FROM object_copies c WHERE c.name=t.name AND c.destination_id=t.destination_id AND c.state<>'deleted') GROUP BY " +
                    "a.id,t.state,p.status")))
                {
                    if (taskIds != null && !taskIds.Contains((string)row[0]))
                        continue;
                    int count = Convert.ToInt32(row[2]);
                    result.Copies += count;
                    if (((string)row[1]).StartsWith("damaged-", StringComparison.Ordinal) || ((string)row[1]).StartsWith("checked-", StringComparison.Ordinal))
                    {
                        if (!localRepair.Contains((long)row[4]))
                            result.RepairCopies += count;
                    }
                    else if ((string)row[3] == "unavailable")
                        result.UnavailableCopies += count;
                    else
                        result.PendingCopies += count;
                }

                return result;
            }
        }
    }
}
