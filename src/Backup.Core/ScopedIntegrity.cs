using System;
using System.Collections.Generic;
using System.Linq;

namespace Backup.Core
{
    public sealed partial class BackupCatalog
    {
        // Merge observations for the viewed objects with saved states. Other replicas
        // participate in the summary only; this method never contacts their warehouses.
        public void RecordScopedIntegrity(long id, IList<IntegrityFinding> observed)
        {
            lock (gate)
            {
                if (Scalar(
                    ("SELECT count(*) FROM archives a WHERE a.id=? AND a.status IN ('complete','repair-required','redundancy-shortfall') AND EXISTS(SELECT 1 FROM archive_files f " +
                    "WHERE f.archive_id=a.id AND f.state='current')"),
                    id
                ) == 0 ||
                    Scalar("SELECT count(*) FROM archive_leases WHERE archive_id=?", id) > 0 ||
                    IsCompactionArchive(id) ||
                    HasOutstandingTransfer(id))
                    return;
                var stored = database.Query("SELECT c.group_id,c.destination_id,c.name,c.state,o.size FROM object_copies c JOIN objects o ON o.name=c.name WHERE o.archive_id=? AND c.state<>'deleted'", id);
                var states = stored.ToDictionary(row => (string)row[1] + "\0" + (string)row[2], row => (string)row[3]);
                var samples = observed.ToDictionary(item => item.Destination + "\0" + item.Name);
                foreach (var group in GetVerifiableCopies(id).GroupBy(copy => copy.GroupId))
                {
                    if (!group.Any(copy =>
                    {
                        IntegrityFinding value;
                        string key = copy.DestinationId + "\0" + copy.Object.Name;
                        return samples.TryGetValue(key, out value) && states[key] != (value.Status == "present-size-matched" ? "present" : value.Status);
                    }))
                        continue;
                    var readings = group.Select(copy =>
                    {
                        string key = copy.DestinationId + "\0" + copy.Object.Name;
                        IntegrityFinding reading;
                        if (samples.TryGetValue(key, out reading))
                            return reading;
                        string state = states[key];
                        return new IntegrityFinding
                        {
                            Destination = copy.DestinationId,
                            Name = copy.Object.Name,
                            ExpectedSize = copy.Object.Size,
                            ActualSize = state == "present" ? copy.Object.Size : -1,
                            Status = state == "present" ? "present-size-matched" : state,
                            RequiresManualRepairConfirmation = state == "missing" || state == "size-or-name-mismatch" || state == "content-damaged"
                        };
                    })
                        .ToList();
                    RecordIntegrity(id, group.Key, readings);
                }
            }
        }
    }
}
