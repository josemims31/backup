using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Backup.Core
{
    public sealed class CatalogMergePlan
    {
        public readonly Dictionary<string, string> Tasks = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Destinations = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Groups = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> FallbackGroups = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public sealed partial class BackupCatalog
    {
        // Raw, signed node bytes keep their original archive numbers. Translate only
        // after validation, at the read boundary; remote packages are never rewritten.
        public NodeFileDatabase ReadRegisteredNodeDatabase(byte[] bytes, BackupNode node)
        {
            return TranslateImportedNode(NodeFileDatabase.Read(bytes, node.Id, node.Digest), node);
        }

        public NodeFileDatabase ReadRegisteredNodeDatabaseFile(string path, BackupNode node, MemoryBudget budget)
        {
            return TranslateImportedNode(NodeFileDatabase.ReadFile(path, budget, node.Id, node.Digest), node);
        }

        private NodeFileDatabase TranslateImportedNode(NodeFileDatabase files, BackupNode node)
        {
            lock (gate)
            {
                var map = ImportedNodeMap(node.Id, node.Digest);
                if (map != null)
                    foreach (var file in files.Files)
                    {
                        long id;
                        if (!map.TryGetValue(file.ArchiveId, out id))
                            throw new IOException("导入版本的压缩包编号对应关系缺失。");
                        file.ArchiveId = id;
                    }
            }

            return files;
        }

        private Dictionary<long, long> ImportedNodeMap(string id, string digest)
        {
            string key = GetSetting("import_node_map:" + id + ":" + digest);
            if (key == null)
                return null;
            string value = GetSetting("import_archive_map:" + key);
            if (value == null)
                throw new IOException("导入版本的压缩包编号对应关系缺失。");
            var result = new Dictionary<long, long>();
            foreach (string line in value.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(':');
                long source, target;
                if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out source) ||
                    !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out target) ||
                    source <= 0 ||
                    target <= 0 ||
                    result.ContainsKey(source))
                    throw new IOException("导入版本的压缩包编号对应关系无效。");
                result.Add(source, target);
            }

            return result;
        }

        private void SaveImportedNodeMap(BackupCatalog source, string id, string digest, IDictionary<long, long> archives, IDictionary<string, string> cache)
        {
            string origin = source.GetSetting("import_node_map:" + id + ":" + digest) ?? "";
            string cached;
            if (cache.TryGetValue(origin, out cached))
            {
                if (cached != null)
                    SaveSettingUnsafe("import_node_map:" + id + ":" + digest, cached);
                return;
            }

            var prior = source.ImportedNodeMap(id, digest);
            var map = prior == null ? archives.ToDictionary(p => p.Key, p => p.Value) : prior.Where(p => archives.ContainsKey(p.Value)).ToDictionary(p => p.Key, p => archives[p.Value]);
            if (map.All(p => p.Key == p.Value))
            {
                cache[origin] = null;
                return;
            }

            string value = string.Join("\n", map.OrderBy(p => p.Key).Select(p => p.Key.ToString(CultureInfo.InvariantCulture) + ":" + p.Value.ToString(CultureInfo.InvariantCulture)));
            string key = NodeFileDatabase.Hash(Encoding.UTF8.GetBytes(value));
            SaveSettingUnsafe("import_archive_map:" + key, value);
            SaveSettingUnsafe("import_node_map:" + id + ":" + digest, key);
            cache[origin] = key;
        }

        // Called only on the private candidate catalog made by database import.
        // Operational leases, temporary uploads, queues and source settings are not merged.
        public void DiscardImportOrigins()
        {
            lock (gate)
                database.Execute("DELETE FROM settings WHERE key GLOB 'import_identity:*'");
        }

        public void MergeBackupRecords(BackupCatalog source, CatalogMergePlan plan)
        {
            if (plan.Tasks.Count == 0)
                return;
            lock (source.gate)
                Transaction(delegate
                {
                    var nodes = source.GetNodes().Where(n => plan.Tasks.ContainsKey(n.TaskId)).ToList();
                    var nodeIds = new HashSet<string>(nodes.Select(n => n.Id), StringComparer.Ordinal);
                    var revisions = source.NodeRevisions().Where(n => plan.Tasks.ContainsKey(n.TaskId) && nodeIds.Contains(n.Id)).ToList();
                    var metadata = new HashSet<long>(nodes.Concat(revisions).Select(n => n.ArchiveId));
                    var usage = plan.Destinations.Values.Distinct().ToDictionary(id => id, id => Scalar("SELECT coalesce(sum(o.size),0) FROM object_copies c JOIN objects o ON o.name=c.name WHERE c.destination_id=?", id));
                    var archives = new Dictionary<long, long>();
                    foreach (var row in source.database.Query("SELECT id,name,budget,password,requested_copies,target_copies,status,created_utc,task_id FROM archives ORDER BY id"))
                    {
                        long old = (long)row[0];
                        string task = (string)row[8], mapped;
                        if (plan.Tasks.TryGetValue(task, out mapped))
                            task = mapped;
                        else if (task.StartsWith("__repack__:", StringComparison.Ordinal) && plan.Tasks.TryGetValue(task.Substring(11), out mapped))
                            task = "__repack__:" + mapped;
                        else if (!metadata.Contains(old))
                            continue;
                        var existing = database.Query("SELECT id,password,task_id FROM archives WHERE name=?", row[1]);
                        if (existing.Count > 0)
                        {
                            long current = (long)existing[0][0];
                            if ((string)existing[0][1] != (string)row[3] || (string)existing[0][2] != task || !SameImportedObjects(source, old, current))
                                throw new IOException("压缩包同名但内容或所属任务不同，未合并：" + row[1]);
                            archives.Add(old, current);
                            continue;
                        }

                        string state = (string)row[6];
                        if (state == "writing")
                            state = "failed";
                        database.Execute("INSERT INTO archives(name,budget,password,requested_copies,target_copies,status,created_utc,task_id) VALUES(?,?,?,?,?,?,?,?)", row[1], row[2], row[3], row[4], row[5], state, row[7], task);
                        archives.Add(old, database.LastId);
                    }

                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var row in source.database.Query("SELECT name,archive_id,size,sha256 FROM objects"))
                    {
                        long id;
                        if (!archives.TryGetValue((long)row[1], out id))
                            continue;
                        var prior = database.Query("SELECT archive_id,size,sha256 FROM objects WHERE name=?", row[0]);
                        if (prior.Count > 0 && ((long)prior[0][0] != id || (long)prior[0][1] != (long)row[2] || (string)prior[0][2] != (string)row[3]))
                            throw new IOException("分卷同名但内容不同，未合并：" + row[0]);
                        database.Execute("INSERT OR IGNORE INTO objects VALUES(?,?,?,?)", row[0], id, row[2], row[3]);
                        names.Add((string)row[0]);
                    }

                    CopyMergedRows(source, "archive_pools", new[] { "archive_id", "pool_idx", "group_id" }, archives, plan, names);
                    CopyMergedRows(source, "archive_pool_members", new[] { "archive_id", "pool_idx", "destination_id" }, archives, plan, names);
                    CopyMergedRows(source, "placements", new[] { "archive_id", "group_id", "destination_id", "reserved", "stored", "status", "error" }, archives, plan, names);
                    CopyMergedRows(source, "object_copies", new[] { "name", "destination_id", "group_id", "state", "pool_idx" }, archives, plan, names);
                    CopyMergedRows(source, "object_locations", new[] { "name", "destination_id", "directory" }, archives, plan, names);
                    CopyMergedRows(source, "node_archive_seals", new[] { "archive_id" }, archives, plan, names);
                    var existingNodes = GetNodes().ToDictionary(n => n.Id, StringComparer.Ordinal);
                    var mapCache = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var node in nodes)
                    {
                        BackupNode existing;
                        existingNodes.TryGetValue(node.Id, out existing);
                        if (existing != null)
                        {
                            if (existing.TaskId != plan.Tasks[node.TaskId])
                                throw new IOException("备份版本编号冲突，未合并：" + node.Id);
                            continue;
                        }

                        database.Execute(
                            "INSERT INTO backup_nodes(id,task_id,created_utc,archive_id,revision,digest,bytes,entry_path) VALUES(?,?,?,?,?,?,?,?)",
                            node.Id,
                            plan.Tasks[node.TaskId],
                            node.CreatedUtc.ToString("o"),
                            archives[node.ArchiveId],
                            node.Revision,
                            node.Digest,
                            node.DatabaseBytes,
                            node.EntryPath
                        );
                        SaveImportedNodeMap(source, node.Id, node.Digest, archives, mapCache);
                    }

                    foreach (var node in revisions)
                    {
                        database.Execute(
                            "INSERT OR IGNORE INTO node_revisions(node_id,archive_id,revision,digest,bytes,task_id,entry_path) VALUES(?,?,?,?,?,?,?)",
                            node.Id,
                            archives[node.ArchiveId],
                            node.Revision,
                            node.Digest,
                            node.DatabaseBytes,
                            plan.Tasks[node.TaskId],
                            node.EntryPath
                        );
                        // A retained revision can be read again if restored from a catalog.
                        if (GetSetting("import_node_map:" + node.Id + ":" + node.Digest) == null)
                            SaveImportedNodeMap(source, node.Id, node.Digest, archives, mapCache);
                    }

                    foreach (var row in source.database.Query("SELECT task_id,id,created_utc,revision,digest,bytes,source_archive,payload FROM local_backup_nodes"))
                    {
                        string task;
                        if (!plan.Tasks.TryGetValue((string)row[0], out task) || Scalar("SELECT count(*) FROM local_backup_nodes WHERE task_id=?", task) > 0)
                            continue;
                        byte[] bytes = row[7] as byte[];
                        long watermark = (long)row[6], mappedWatermark;
                        if (archives.TryGetValue(watermark, out mappedWatermark))
                            watermark = mappedWatermark;
                        else
                            watermark = archives.Count == 0 ? 0 : archives.Values.Max();
                        if (bytes != null)
                        {
                            var files = NodeFileDatabase.Read(bytes, (string)row[1], (string)row[4]);
                            foreach (var file in files.Files)
                            {
                                long id;
                                if (!archives.TryGetValue(file.ArchiveId, out id))
                                    throw new IOException("未完成版本引用的压缩包不存在，未合并。");
                                file.ArchiveId = id;
                            }

                            bytes = files.Serialize();
                        }

                        database.Execute(
                            "INSERT INTO local_backup_nodes VALUES(?,?,?,?,?,?,?,?)",
                            task,
                            row[1],
                            row[2],
                            row[3],
                            bytes == null ? "" : NodeFileDatabase.Hash(bytes),
                            bytes == null ? 0 : bytes.LongLength,
                            watermark,
                            bytes
                        );
                    }

                    var tags = new HashSet<string>(plan.Tasks.Keys.Select(id => source.GetSetting("archive_task_tag:" + id)).Where(tag => tag != null), StringComparer.Ordinal);
                    foreach (var row in source.database.Query("SELECT destination_id,kind,tag,directory,height,next_slot FROM storage_trees"))
                    {
                        string destination;
                        if (!plan.Destinations.TryGetValue((string)row[0], out destination) || !tags.Contains((string)row[2]))
                            continue;
                        database.Execute("INSERT OR IGNORE INTO storage_trees VALUES(?,?,?,?,?,?)", destination, row[1], row[2], row[3], row[4], row[5]);
                        database.Execute("UPDATE storage_trees SET next_slot=MAX(next_slot,?) WHERE destination_id=? AND kind=? AND tag=?", row[5], destination, row[1], row[2]);
                    }

                    // Account only for the merged physical objects; shared locations are counted once.
                    foreach (var before in usage)
                    {
                        long after = Scalar("SELECT coalesce(sum(o.size),0) FROM object_copies c JOIN objects o ON o.name=c.name WHERE c.destination_id=?", before.Key);
                        database.Execute("UPDATE destinations SET used_bytes=used_bytes+? WHERE id=?", Math.Max(0, after - before.Value), before.Key);
                    }

                    ClearNodeGarbageReview();
                    RepositoryStatusChanged(CatalogChange.Archives);
                    return 0;
                });
        }

        private bool SameImportedObjects(BackupCatalog source, long incoming, long current)
        {
            var left = source.database.Query("SELECT name,size,sha256 FROM objects WHERE archive_id=? ORDER BY name", incoming);
            var right = database.Query("SELECT name,size,sha256 FROM objects WHERE archive_id=? ORDER BY name", current);
            return left.Count == right.Count && left.Zip(right, (a, b) => a.SequenceEqual(b)).All(equal => equal);
        }

        private void CopyMergedRows(BackupCatalog source, string table, string[] columns, IDictionary<long, long> archives, CatalogMergePlan plan, ISet<string> names)
        {
            foreach (var input in source.database.Query("SELECT " + string.Join(",", columns) + " FROM " + table))
            {
                var row = (object[])input.Clone();
                bool include = true;
                for (int i = 0; i < columns.Length && include; i++)
                {
                    string mapped;
                    long id;
                    switch (columns[i])
                    {
                        case "archive_id":
                            include = archives.TryGetValue((long)row[i], out id);
                            row[i] = id;
                            break;
                        case "destination_id":
                            include = plan.Destinations.TryGetValue((string)row[i], out mapped);
                            row[i] = mapped;
                            break;
                        case "group_id":
                            include = plan.Groups.TryGetValue((string)row[i], out mapped);
                            if (!include)
                            {
                                int destination = Array.IndexOf(columns, "destination_id");
                                if (destination >= 0)
                                    include = plan.FallbackGroups.TryGetValue((string)input[destination], out mapped);
                                else if (table == "archive_pools")
                                    foreach (var member in source.database.Query("SELECT destination_id FROM archive_pool_members WHERE archive_id=? AND pool_idx=? ORDER BY destination_id", input[0], input[1]))
                                        if (plan.FallbackGroups.TryGetValue((string)member[0], out mapped))
                                        {
                                            include = true;
                                            break;
                                        }
                            }

                            row[i] = mapped;
                            break;
                        case "name":
                            include = names.Contains((string)row[i]);
                            break;
                        case "reserved":
                            row[i] = 0L;
                            break;
                    }
                }

                if (!include)
                    continue;
                if (table == "archive_pool_members" && Scalar("SELECT count(*) FROM archive_pools WHERE archive_id=? AND pool_idx=?", row[0], row[1]) == 0)
                    continue;
                database.Execute("INSERT OR IGNORE INTO " + table + "(" + string.Join(",", columns) + ") VALUES(" + string.Join(",", columns.Select(c => "?")) + ")", row);
            }
        }
    }
}
