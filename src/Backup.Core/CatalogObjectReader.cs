using System;
using System.Collections.Generic;
using System.Linq;

namespace Backup.Core
{
    // Connection-local warehouse object queries run under the catalog facade's
    // existing gate. This reader owns no transaction or remote session.
    internal sealed class CatalogObjectReader
    {
        private readonly SqliteDatabase database;
        internal CatalogObjectReader(SqliteDatabase value)
        {
            database = value;
        }

        internal IList<StoredArchiveObject> ReadByName(string destinationId, IEnumerable<string> objectNames, bool ignoreCase = false)
        {
            var names = objectNames.Distinct(StringComparer.Ordinal).ToList();
            var result = new List<StoredArchiveObject>();
            {
                if (ignoreCase)
                {
                    var canonical = new HashSet<string>(StringComparer.Ordinal);
                    for (int offset = 0; offset < names.Count; offset += 200)
                    {
                        var batch = names.Skip(offset).Take(200).ToList();
                        foreach (var row in database.Query("SELECT name FROM objects WHERE name COLLATE NOCASE IN (" + string.Join(",", batch.Select(name => "?")) + ")", batch.Cast<object>().ToArray()))
                            canonical.Add((string)row[0]);
                    }

                    names = canonical.ToList();
                }

                for (int offset = 0; offset < names.Count; offset += 200)
                    result.AddRange(Read(destinationId, false, names.Skip(offset).Take(200).ToList()));
            }

            return result;
        }

        internal IList<StoredArchiveObject> Read(string destinationId, bool includeDeleted, IList<string> names)
        {
            string filter = names == null ? "" : " AND o.name IN (" + string.Join(",", names.Select(name => "?")) + ")";
            string sql = ("SELECT q.name,q.size,q.archive_id,coalesce(c.state,CASE WHEN t.state='failed-upload' THEN 'missing' WHEN t.state LIKE 'damaged-%' THEN 'damaged' WHEN t.state " +
                "LIKE 'checked-%' THEN 'registration-mismatch' WHEN t.name IS NOT NULL AND p.status='unavailable' THEN 'unavailable' WHEN t.name IS NOT NULL THEN " +
                "'reconcile-required' ELSE 'retired' END),coalesce((SELECT group_concat(DISTINCT n.task_id) FROM node_revisions n WHERE " +
                "n.archive_id=q.archive_id),a.task_id),a.status FROM (SELECT o.name,o.size,o.archive_id FROM objects o JOIN object_copies c ON c.name=o.name WHERE " +
                "c.destination_id=? AND (?=1 OR c.state<>'deleted'){NAMES} UNION SELECT o.name,o.size,o.archive_id FROM objects o JOIN warehouse_detached d ON d.name=o.name " +
                "WHERE d.destination_id=? AND d.state='pending'{NAMES} UNION SELECT o.name,o.size,o.archive_id FROM objects o JOIN retired_objects r ON r.name=o.name WHERE " +
                "r.destination_id=?{NAMES} UNION SELECT o.name,o.size,o.archive_id FROM objects o JOIN volume_transfers t ON t.name=o.name WHERE t.destination_id=?{NAMES}) q " +
                "JOIN archives a ON a.id=q.archive_id LEFT JOIN object_copies c ON c.name=q.name AND c.destination_id=? AND (?=1 OR c.state<>'deleted') LEFT JOIN " +
                "volume_transfers t ON t.name=q.name AND t.destination_id=? LEFT JOIN placements p ON p.archive_id=q.archive_id AND p.group_id=t.group_id AND " +
                "p.destination_id=t.destination_id").Replace("{NAMES}", filter);
            var args = new List<object>();
            if (names == null)
                args.AddRange(new object[] { destinationId, includeDeleted, destinationId, destinationId, destinationId, destinationId, includeDeleted, destinationId });
            else
            {
                args.Add(destinationId);
                args.Add(includeDeleted);
                args.AddRange(names.Cast<object>());
                for (int branch = 0; branch < 3; branch++)
                {
                    args.Add(destinationId);
                    args.AddRange(names.Cast<object>());
                }

                args.AddRange(new object[] { destinationId, includeDeleted, destinationId });
            }

            return database.Query(sql, args.ToArray()).Select(r => new StoredArchiveObject {
                Name = (string)r[0],
                Size = (long)r[1],
                ArchiveId = (long)r[2],
                State = (string)r[3],
                TaskId = (string)r[4],
                ArchiveStatus = (string)r[5]
            })
                .ToList();
        }
    }
}
