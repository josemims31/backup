using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Backup.Core
{
    public sealed class InventoryFolderRecord
    {
        public string DestinationId, Folder, Json, CheckedUtc, Error;
    }

    public sealed class WarehouseMoveRecord
    {
        public string DestinationId, OriginalJson, TargetJson, OldConnection, NewConnection, TargetName, State, Error;
        public bool CatalogPublished;
    }

    public sealed class WarehouseMoveSession : IDisposable
    {
        public WarehouseMoveRecord Record;
        public IList<ObjectRecord> Objects;
        public IDisposable Lease;
        public void Dispose()
        {
            if (Lease != null)
            {
                Lease.Dispose();
                Lease = null;
            }
        }
    }

    public sealed partial class BackupCatalog
    {
        private void InitializeWarehouseState()
        {
            foreach (string sql in new[]
            {
                ("CREATE TABLE IF NOT EXISTS warehouse_moves(destination_id TEXT PRIMARY KEY,original_json TEXT NOT NULL,target_json TEXT NOT NULL,old_connection TEXT NOT " +
                    "NULL,new_connection TEXT NOT NULL,target_name TEXT NOT NULL,state TEXT NOT NULL,error TEXT NOT NULL)"),
                ("CREATE TABLE IF NOT EXISTS warehouse_move_objects(destination_id TEXT NOT NULL,name TEXT NOT NULL REFERENCES " +
                    "objects(name),state TEXT NOT NULL,PRIMARY KEY(destination_id,name))")
            }

            )
                database.Execute(sql);
            if (!database.Query("PRAGMA table_info(warehouse_moves)").Any(r => (string)r[1] == "catalog_published"))
                database.Execute("ALTER TABLE warehouse_moves ADD COLUMN catalog_published INTEGER NOT NULL DEFAULT 0");
        }

        public InventoryFolderRecord GetInventoryFolder(string id, string folder)
        {
            return runtimeCache.ReadInventory(id, folder);
        }

        public static string InventoryFolderKey(string folder)
        {
            if (folder.IndexOf("://", StringComparison.Ordinal) >= 0)
            {
                var uri = new Uri(folder, UriKind.Absolute);
                return uri.AbsoluteUri.TrimEnd('/') + "/";
            }

            return StoragePath.NormalizeRoot(folder);
        }

        public void SaveInventoryFolder(InventoryFolderRecord value)
        {
            runtimeCache.SaveInventory(new[] { value }, null, null, false);
        }

        public void SaveInventoryFolder(InventoryFolderRecord value, InventoryFolderRecord previous)
        {
            runtimeCache.SaveInventoryRecord(value, previous);
        }

        public void DeleteInventorySubtree(string id, string folder, bool filesystem)
        {
            runtimeCache.DeleteInventorySubtree(id, folder, filesystem);
        }

        public void PruneInventory(string id, string scan)
        {
            runtimeCache.SaveInventory(new InventoryFolderRecord[0], id, scan, true);
        }

        public void SaveInventorySnapshot(string id, IEnumerable<InventoryFolderRecord> records, string scan, bool complete)
        {
            runtimeCache.SaveInventory(records, id, scan, complete);
        }

        public void RelocateWarehouse(DestinationDefinition target, string name, IList<KeyValuePair<string, string>> settings)
        {
            Transaction(delegate
            {
                var old = LoadTopology().Destinations.Single(d => d.Id == target.Id);
                // Keep archive IDs, copies, tasks and upload journals. The new
                // location may intentionally contain only part of the repository.
                database.Execute("UPDATE destinations SET protocol=?,location=?,max_bytes=?,enabled=? WHERE id=?", target.Protocol, target.Location, target.MaxBytes, target.Enabled, target.Id);
                database.Execute("UPDATE groups SET name=? WHERE id=?", name, "single:" + target.Id);
                database.Execute("DELETE FROM warehouse_move_objects WHERE destination_id=?", target.Id);
                database.Execute("DELETE FROM warehouse_moves WHERE destination_id=?", target.Id);
                SaveConfigurationUnsafe(settings);
                ClearNodeGarbageReview();
                runtimeCache.DeleteInventorySubtree(target.Id, old.Location, RepositoryModules.IsFileSystem(old.Protocol));
                runtimeCache.DeleteInventorySubtree(target.Id, target.Location, RepositoryModules.IsFileSystem(target.Protocol));
                RepositoryStatusChanged(CatalogChange.Topology | CatalogChange.Connections);
                return 0;
            });
        }

        public IList<WarehouseMoveRecord> GetWarehouseMoves()
        {
            lock (gate)
                return database.Query("SELECT destination_id,original_json,target_json,old_connection,new_connection,target_name,state,error,catalog_published FROM warehouse_moves").Select(r => new WarehouseMoveRecord {
                    DestinationId = (string)r[0],
                    OriginalJson = (string)r[1],
                    TargetJson = (string)r[2],
                    OldConnection = (string)r[3],
                    NewConnection = (string)r[4],
                    TargetName = (string)r[5],
                    State = (string)r[6],
                    Error = (string)r[7],
                    CatalogPublished = (long)r[8] != 0
                })
                    .ToList();
        }

        public WarehouseMoveSession BeginWarehouseMove(WarehouseMoveRecord request)
        {
            return Transaction(delegate
            {
                RepositoryStatusChanged(CatalogChange.Topology);
                var prior = GetWarehouseMoves().FirstOrDefault(m => m.DestinationId == request.DestinationId && m.State != "complete");
                if (prior != null && (prior.TargetJson != request.TargetJson || prior.State == "published"))
                    throw new IOException("请先完成或重试当前仓库迁移，再修改路径。");
                if (Scalar("SELECT count(*) FROM placements WHERE destination_id=? AND (reserved>0 OR status IN ('writing','reconcile-required'))", request.DestinationId) > 0)
                    throw new IOException("仓库仍有正在上传或待核对的数据，请完成后重试。");
                var references = GetWarehouseObjects(request.DestinationId);
                var objects = references.GroupBy(o => o.ArchiveId).SelectMany(g =>
                {
                    var names = new HashSet<string>(g.Select(o => o.Name));
                    return GetObjects(g.Key).Where(o => names.Contains(o.Name));
                })
                    .ToList();
                var tokens = new List<string>();
                foreach (long id in references.Select(o => o.ArchiveId).Distinct())
                {
                    if (HasOutstandingTransfer(id) || GetReplicaStages(id).Count > 0 || Scalar("SELECT count(*) FROM archive_leases WHERE archive_id=?", id) > 0 ||
                        Scalar("SELECT count(*) FROM archive_cleanup WHERE archive_id=? AND state='pending'", id) > 0)
                        throw new IOException("备份包正在读取、迁移或清理，请稍后重试。");
                    string token = Guid.NewGuid().ToString("N");
                    tokens.Add(token);
                    using (var process = Process.GetCurrentProcess())
                        database.Execute("INSERT INTO archive_leases(archive_id,token,process_id,process_start) VALUES(?,?,?,?)", id, token, process.Id, process.StartTime.ToUniversalTime().Ticks);
                }

                if (prior == null)
                {
                    database.Execute("DELETE FROM warehouse_move_objects WHERE destination_id=?", request.DestinationId);
                    database.Execute(
                        ("INSERT OR REPLACE INTO warehouse_moves(destination_id,original_json,target_json,old_connection,new_connection,target_nam" +
                        "e,state,error) VALUES(?,?,?,?,?,?,'copying','')"),
                        request.DestinationId,
                        request.OriginalJson,
                        request.TargetJson,
                        request.OldConnection,
                        request.NewConnection,
                        request.TargetName
                    );
                }

                foreach (var item in objects)
                    database.Execute("INSERT OR IGNORE INTO warehouse_move_objects(destination_id,name,state) VALUES(?,?,'pending')", request.DestinationId, item.Name);
                var record = GetWarehouseMoves().Single(m => m.DestinationId == request.DestinationId);
                return new WarehouseMoveSession
                {
                    Record = record,
                    Objects = objects,
                    Lease = new CatalogLease(delegate
                    {
                        lock (gate)
                            foreach (string token in tokens)
                                database.Execute("DELETE FROM archive_leases WHERE token=?", token);
                    })
                };
            });
        }

        public string WarehouseMoveObjectState(string id, string name)
        {
            lock (gate)
            {
                var rows = database.Query("SELECT state FROM warehouse_move_objects WHERE destination_id=? AND name=?", id, name);
                return rows.Count == 0 ? null : (string)rows[0][0];
            }
        }

        public void WarehouseMoveObjectState(string id, string name, string state, string confirmedFolder = null)
        {
            Transaction(delegate
            {
                database.Execute("UPDATE warehouse_move_objects SET state=? WHERE destination_id=? AND name=?", state, id, name);
                if (confirmedFolder != null && (state == "copied" || state == "deleted"))
                {
                    var item = database.Query("SELECT size,archive_id FROM objects WHERE name=?", name);
                    if (item.Count > 0)
                        RepositoryObjectCommitted(id, name, (long)item[0][0], (long)item[0][1], state == "copied", confirmedFolder);
                }

                return 0;
            });
        }

        public IList<ObjectRecord> WarehouseMoveObjects(string id)
        {
            lock (gate)
                return database.Query("SELECT o.name,o.size,o.sha256 FROM warehouse_move_objects m JOIN objects o ON o.name=m.name WHERE m.destination_id=?", id).Select(r => new ObjectRecord {
                    Name = (string)r[0],
                    Size = (long)r[1],
                    Sha256 = (string)r[2]
                })
                    .ToList();
        }

        public void WarehouseMoveError(string id, string error)
        {
            Transaction(delegate
            {
                RepositoryStatusChanged(CatalogChange.Topology);
                database.Execute("UPDATE warehouse_moves SET error=? WHERE destination_id=? AND error<>?", error, id, error);
                return 0;
            });
        }

        public void UpdateMoveConnections(string id, string oldConnection, string newConnection)
        {
            Transaction(delegate
            {
                database.Execute("UPDATE warehouse_moves SET old_connection=?,new_connection=?,catalog_published=0 WHERE destination_id=?", oldConnection, newConnection, id);
                TouchRecovery();
                return 0;
            });
        }

        public void PublishWarehouseMove(DestinationDefinition target, string name, string connection, string reference)
        {
            Transaction(delegate
            {
                RepositoryStatusChanged(CatalogChange.Topology | CatalogChange.Connections);
                if (Scalar("SELECT count(*) FROM warehouse_moves WHERE destination_id=? AND state='copying'", target.Id) != 1 ||
                    Scalar("SELECT count(*) FROM warehouse_move_objects WHERE destination_id=? AND state<>'copied'", target.Id) != 0)
                    throw new IOException("新路径的备份尚未全部验证，禁止切换仓库。");
                var topology = LoadTopology();
                topology.Destinations.RemoveAll(d => d.Id == target.Id);
                topology.Destinations.Add(target);
                topology.Normalize();
                long used = Scalar("SELECT used_bytes FROM destinations WHERE id=?", target.Id);
                if (target.MaxBytes < used)
                    throw new IOException("新仓库容量限制不足。");
                database.Execute("UPDATE destinations SET protocol=?,location=?,max_bytes=?,enabled=? WHERE id=?", target.Protocol, target.Location, target.MaxBytes, target.Enabled, target.Id);
                database.Execute("UPDATE groups SET name=? WHERE id=?", name, "single:" + target.Id);
                database.Execute("UPDATE warehouse_moves SET state='published',error='' WHERE destination_id=?", target.Id);
                runtimeCache.DeleteInventory(target.Id, target.Location);
                foreach (var setting in new[]
                {
                    new[]
                    {
                        "connection:" + target.Id,
                        connection
                    },
                    new[]
                    {
                        "connection_reference:" + target.Id,
                        reference
                    }
                }

                )
                {
                    database.Execute("INSERT OR REPLACE INTO settings(key,value) VALUES(?,?)", setting[0], setting[1] ?? "");
                }

                return 0;
            });
        }

        public void MarkWarehouseMoveCatalogPublished(string id)
        {
            lock (gate)
                database.Execute("UPDATE warehouse_moves SET catalog_published=1 WHERE destination_id=? AND state='published'", id);
        }

        public void FinishWarehouseMove(string id)
        {
            Transaction(delegate
            {
                RepositoryStatusChanged(CatalogChange.Topology);
                database.Execute(
                    ("UPDATE warehouse_moves SET state='complete',error='' WHERE destination_id=? AND state<>'complete' AND catalog_published=1 AND NOT EXISTS(SELECT 1 FROM " +
                    "warehouse_move_objects WHERE destination_id=? AND state<>'deleted')"),
                    id,
                    id
                );
                return 0;
            });
        }

        public void CancelWarehouseMove(string id)
        {
            Transaction(delegate
            {
                RepositoryStatusChanged(CatalogChange.Topology);
                if (Scalar("SELECT count(*) FROM warehouse_moves WHERE destination_id=? AND state='copying'", id) != 1)
                    throw new IOException("只有尚未切换新路径的迁移可以取消。");
                if (Scalar("SELECT count(*) FROM archive_leases l JOIN objects o ON o.archive_id=l.archive_id JOIN warehouse_move_objects m ON m.name=o.name WHERE m.destination_id=?", id) > 0)
                    throw new IOException("迁移仍在执行，暂时不能取消。");
                database.Execute("UPDATE warehouse_moves SET state='complete',error='已取消；目标文件保留，按清单人工处理' WHERE destination_id=?", id);
                return 0;
            });
        }
    }
}
