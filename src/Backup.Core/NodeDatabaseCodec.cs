using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Backup.Core
{
    // Detached node image serialization/validation is independent of task state
    // and catalog transactions. Both byte and file readers use the same checks.
    internal static class NodeDatabaseCodec
    {
        internal static byte[] Serialize(NodeFileDatabase node)
        {
            using (var db = new SqliteDatabase(":memory:"))
            {
                bool replicas = node.Files.Any(file => file.Replicas != null && file.Replicas.Count > 0);
                // Older readers must fail closed instead of dropping extra references.
                db.Execute(replicas ? "PRAGMA user_version=3" : "PRAGMA user_version=2");
                db.Execute("CREATE TABLE node(id TEXT PRIMARY KEY,created_utc TEXT NOT NULL,revision INTEGER NOT NULL)");
                db.Execute(("CREATE TABLE files(path TEXT PRIMARY KEY,archive_id INTEGER NOT NULL,size INTEGER NOT NULL,modified INTEGER NOT NULL,source_path TEXT NOT NULL,entry_path TEXT " +
                    "NOT NULL,content_path TEXT NOT NULL DEFAULT '')"));
                if (replicas)
                    db.Execute("CREATE TABLE replicas(path TEXT NOT NULL REFERENCES files(path),archive_id INTEGER NOT NULL,entry_path TEXT NOT NULL,content_path TEXT NOT NULL,PRIMARY KEY(path,archive_id))");
                db.Transaction(delegate
                {
                    db.Execute("INSERT INTO node VALUES(?,?,?)", node.Id, node.CreatedUtc.ToUniversalTime().ToString("o"), node.Revision);
                    db.ExecuteMany("INSERT INTO files VALUES(?,?,?,?,?,?,?)", node.Files.Select(f => new object[] {
                        f.Path,
                        f.ArchiveId,
                        f.Size,
                        f.Modified,
                        f.SourcePath ?? "",
                        f.EntryPath ?? "",
                        f.ContentPath ??
                        ""
                    }));
                    if (replicas)
                        db.ExecuteMany("INSERT INTO replicas VALUES(?,?,?,?)", node.Files.Where(file => file.Replicas != null).SelectMany(file => file.Replicas.Select(replica => new object[] {
                            file.Path, replica.ArchiveId, replica.EntryPath ?? "", replica.ContentPath ?? ""
                        })));
                    return 0;
                });
                return db.SnapshotBytes();
            }
        }

        internal static NodeFileDatabase Read(byte[] bytes, string expectedId, string digest)
        {
            if (digest != null && !string.Equals(NodeFileDatabase.Hash(bytes), digest, StringComparison.Ordinal))
                throw new IOException("备份版本数据库校验失败。");
            using (var db = SqliteDatabase.FromBytes(bytes))
                return ReadDatabase(db, expectedId);
        }

        internal static NodeFileDatabase ReadFile(string path, MemoryBudget budget, string expectedId, string digest)
        {
            using (var db = SqliteDatabase.FromFileMemory(path, budget, digest))
                return ReadDatabase(db, expectedId);
        }

        private static NodeFileDatabase ReadDatabase(SqliteDatabase db, string expectedId)
        {
            long version = (long)db.Query("PRAGMA user_version")[0][0];
            if (version < 1 || version > 3)
                throw new IOException("备份版本数据库版本不支持。");
            var check = db.Query("PRAGMA quick_check");
            if (check.Count != 1 || (string)check[0][0] != "ok")
                throw new IOException("备份版本数据库不完整。");
            var header = db.Query("SELECT id,created_utc,revision FROM node");
            if (header.Count != 1)
                throw new IOException("备份版本数据库标识无效。");
            Guid id;
            DateTime created;
            string key = header[0][0] as string;
            if (!Guid.TryParseExact(key, "N", out id) || expectedId != null && key != expectedId ||
                !DateTime.TryParse(header[0][1] as string, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out created) ||
                !(header[0][2] is long) ||
                (long)header[0][2] < 1)
                throw new IOException("备份版本数据库标识无效。");
            var node = new NodeFileDatabase
            {
                Id = key,
                CreatedUtc = created.ToUniversalTime(),
                Revision = (long)header[0][2]
            };
            foreach (var row in db.StreamQuery("SELECT path,archive_id,size,modified,source_path,entry_path," + (version == 1 ? "''" : "content_path") + " FROM files ORDER BY path"))
            {
                if (!(row[0] is string) || string.IsNullOrEmpty((string)row[0]) || !(row[1] is long) || (long)row[1] <= 0 || !(row[2] is long) || (long)row[2] < 0 || !(row[3] is long) ||
                    !(row[4] is string) ||
                    !(row[5] is string))
                    throw new IOException("备份版本文件记录无效。");
                if (!(row[6] is string))
                    throw new IOException("备份版本文件内容路径无效。");
                node.Files.Add(new NodeFile {
                    Path = (string)row[0],
                    ArchiveId = (long)row[1],
                    Size = (long)row[2],
                    Modified = (long)row[3],
                    SourcePath = (string)row[4],
                    EntryPath = (string)row[5],
                    ContentPath = (string)row[6]
                });
            }

            if (node.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != node.Files.Count)
                throw new IOException("备份版本中有重复文件。");
            if (version == 3)
            {
                var files = node.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
                foreach (var row in db.StreamQuery("SELECT path,archive_id,entry_path,content_path FROM replicas ORDER BY path,archive_id"))
                {
                    NodeFile file;
                    if (!(row[0] is string) || !files.TryGetValue((string)row[0], out file) || !(row[1] is long) || (long)row[1] <= 0 ||
                        (long)row[1] == file.ArchiveId || !(row[2] is string) || !(row[3] is string))
                        throw new IOException("备份版本副本记录无效。");
                    if (file.Replicas == null)
                        file.Replicas = new List<NodeFile>();
                    file.Replicas.Add(new NodeFile { Path = file.Path, ArchiveId = (long)row[1], Size = file.Size, Modified = file.Modified,
                        SourcePath = file.SourcePath, EntryPath = (string)row[2], ContentPath = (string)row[3] });
                }
            }
            return node;
        }
    }
}
