using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        // Rebuildable inspection evidence stays in memory. Recovery-critical copy
        // states and upload intents continue to use the existing catalog tables.
        private AllBackupCheckResult lastIntegrityReview;
        private IDictionary<string, IList<StorageEntry>> CheckedInventory(DestinationDefinition destination)
        {
            List<string> folders;
            var review = lastIntegrityReview;
            if (review == null || !review.Completed || !review.Directories.TryGetValue(destination.Id, out folders))
                return null;
            var result = new Dictionary<string, IList<StorageEntry>>();
            foreach (string folder in folders)
            {
                var record = Catalog.GetInventoryFolder(destination.Id, folder);
                if (record == null || !string.IsNullOrEmpty(record.Error))
                    return null;
                result[ObjectStoragePath.Relative(destination, folder)] = JsonSettings.Decode(record.Json, new List<StorageEntry>());
            }

            return result;
        }

        internal AllBackupCheckResult LastIntegrityReview
        {
            get
            {
                return lastIntegrityReview;
            }
        }

        private static string ReviewScope(IEnumerable<string> ids)
        {
            return ids == null ? "*" : string.Join("\n", ids.OrderBy(id => id, StringComparer.Ordinal));
        }

        private string ReviewTopology()
        {
            return JsonSettings.Encode(Catalog.LoadTopology());
        }

        private HashSet<string> IntegrityTaskIds(IEnumerable<string> ids)
        {
            if (ids == null)
                return null;
            var scope = new HashSet<string>(ids);
            foreach (var node in Catalog.GetNodes().Where(n => scope.Contains(n.TaskId)))
                scope.Add("__node__:" + node.Id);
            return scope;
        }

        private bool ReviewOwnsName(AllBackupCheckResult review, string name, IDictionary<string, ArchiveSummary> archives)
        {
            if (review.TaskIds == null)
                return true;
            string archive = BackupObjectNaming.ArchiveName(name);
            ArchiveSummary record;
            if (archive == null)
                return false;
            if (archives.TryGetValue(archive, out record))
                return review.ArchiveTasks.Contains(record.TaskId);
            if (archive.StartsWith("catalog-", StringComparison.OrdinalIgnoreCase))
                return review.TaskIds.Contains("__catalog__");
            return review.TaskIds.Any(id =>
            {
                string tag = Catalog.GetSetting("archive_task_tag:" + id);
                return !string.IsNullOrEmpty(tag) && archive.StartsWith("data-" + tag + "-", StringComparison.OrdinalIgnoreCase);
            });
        }

        private void CompleteIntegrityReview(AllBackupCheckResult review, IDictionary<string, Dictionary<string, InventoryFolderRecord>> snapshots)
        {
            review.ArchiveTasks = IntegrityTaskIds(review.TaskIds);
            review.Directories = snapshots.ToDictionary(pair => pair.Key, pair => pair.Value.Values.Select(value => value.Folder).Distinct().ToList());
            var archives = Catalog.ListArchives(true).Where(item => !string.IsNullOrEmpty(item.ArchiveName)).ToDictionary(item => item.ArchiveName, StringComparer.OrdinalIgnoreCase);
            var indexed = new ReviewFindingIndex(review.Findings);
            var byId = archives.Values.ToDictionary(item => item.ArchiveId);
            Func<long, bool> owns = id => review.ArchiveTasks == null || review.ArchiveTasks.Contains(byId.ContainsKey(id) ? byId[id].TaskId : Catalog.GetArchive(id).TaskId);
            var objects = new Dictionary<long, Dictionary<string, ObjectRecord>>();
            var stageNames = new HashSet<string>(Catalog.PendingUploadStages().SelectMany(stage => Catalog.UploadStages(stage.DestinationId, stage.Name).Select(name => stage.DestinationId + "\0" + name)), StringComparer.OrdinalIgnoreCase);
            var obsolete = (review.TaskIds == null || review.TaskIds.Contains("__catalog__")) 
                ? new HashSet<long>(Catalog.UnregisterUnusedCatalogSnapshots(DatabaseBackupTargets(), true)) 
                : new HashSet<long>();
            foreach (var definition in Catalog.LoadTopology().Destinations)
            {
                Dictionary<string, InventoryFolderRecord> folders;
                if (!snapshots.TryGetValue(definition.Id, out folders))
                    continue;
                bool local = RepositoryModules.IsFileSystem(definition.Protocol);
                var known = Catalog.GetWarehouseObjects(definition.Id).ToDictionary(item => item.Name, local ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                var decoded = folders.Where(pair => string.IsNullOrEmpty(pair.Value.Error)).ToDictionary(pair => pair.Key, pair => JsonSettings.Decode(pair.Value.Json, new List<StorageEntry>()));
                foreach (var pair in decoded)
                    foreach (var entry in pair.Value)
                    {
                        var folder = folders[pair.Key];
                        if (entry.Directory)
                            continue;
                        if (!BackupObjectNaming.IsManaged(entry.Name))
                        {
                            if (review.TaskIds == null)
                                review.ExternalFiles++;
                            continue;
                        }

                        if (!ReviewOwnsName(review, entry.Name, archives))
                            continue;
                        StoredArchiveObject registered;
                        if (known.TryGetValue(entry.Name, out registered))
                        {
                            if (!owns(registered.ArchiveId))
                                continue;
                            if (review.ObsoleteIds.Contains(registered.ArchiveId))
                            {
                                indexed.Add(new IntegrityFinding {
                                    ArchiveId = registered.ArchiveId,
                                    Destination = definition.Id,
                                    Folder = folder.Folder,
                                    Name = entry.Name,
                                    Status = "obsolete-archive",
                                    RepairAction = "delete-obsolete",
                                    RequiresManualRepairConfirmation = true
                                });
                                continue;
                            }

                            if (obsolete.Contains(registered.ArchiveId))
                            {
                                indexed.Add(new IntegrityFinding {
                                    ArchiveId = registered.ArchiveId,
                                    Destination = definition.Id,
                                    Folder = folder.Folder,
                                    Name = entry.Name,
                                    ExpectedSize = registered.Size,
                                    ActualSize = entry.Size,
                                    Status = "obsolete-registration",
                                    RepairAction = "remove-obsolete-registration",
                                    RequiresManualRepairConfirmation = true
                                });
                                continue;
                            }

                            if (indexed.ContainsName(definition.Id, entry.Name, local))
                                continue;
                            bool bad = entry.Size != registered.Size;
                            indexed.Add(new IntegrityFinding {
                                ArchiveId = registered.ArchiveId,
                                Destination = definition.Id,
                                Folder = folder.Folder,
                                Name = entry.Name,
                                ExpectedSize = registered.Size,
                                ActualSize = entry.Size,
                                RecordedStatus = registered.State,
                                Status = bad ? "size-or-name-mismatch" : "present-size-matched",
                                RepairAction = bad ? "replace" : registered.State == "retired" ? null : registered.State == "present" ? null : "correct-state",
                                RequiresManualRepairConfirmation = bad ||
                                registered.State != "retired" &&
                                registered.State != "present"
                            });
                            continue;
                        }

                        if (stageNames.Contains(definition.Id + "\0" + entry.Name))
                            continue;
                        string baseName = BackupObjectNaming.ArchiveName(entry.Name);
                        ArchiveSummary archive;
                        ObjectRecord expected = null;
                        bool canRegister = !BackupObjectNaming.IsTemporary(entry.Name) && archives.TryGetValue(baseName, out archive) && archive.Status != "cleaned";
                        archives.TryGetValue(baseName, out archive);
                        if (canRegister)
                        {
                            Dictionary<string, ObjectRecord> knownObjects;
                            if (!objects.TryGetValue(archive.ArchiveId, out knownObjects))
                            {
                                knownObjects = Catalog.GetObjects(archive.ArchiveId).ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
                                objects.Add(archive.ArchiveId, knownObjects);
                            }

                            canRegister = knownObjects.TryGetValue(entry.Name, out expected) && Catalog.CanRestoreInspectedCopy(archive.ArchiveId, expected.Name, definition.Id);
                        }

                        archives.TryGetValue(baseName, out archive);
                        if (archive != null && archive.Status != "cleaned" && !review.ObsoleteIds.Contains(archive.ArchiveId) && !Catalog.HasCompleteArchiveManifest(archive.ArchiveId))
                        {
                            indexed.Add(new IntegrityFinding {
                                ArchiveId = archive.ArchiveId,
                                Destination = definition.Id,
                                Folder = folder.Folder,
                                Name = entry.Name,
                                ExpectedSize = -1,
                                ActualSize = entry.Size,
                                Status = "manifest-incomplete",
                                RepairAction = "restore-manifest",
                                RequiresManualRepairConfirmation = true
                            });
                            continue;
                        }

                        indexed.Add(new IntegrityFinding {
                            ArchiveId = canRegister ? archive.ArchiveId : 0,
                            Destination = definition.Id,
                            Folder = folder.Folder,
                            Name = entry.Name,
                            ExpectedSize = canRegister ? expected.Size : -1,
                            ActualSize = entry.Size,
                            Status = canRegister &&
                            entry.Size == expected.Size ? "unregistered-copy" : "unregistered-managed",
                            RepairAction = canRegister &&
                            entry.Size == expected.Size ? "register-copy" : "delete-orphan",
                            RequiresManualRepairConfirmation = true
                        });
                    }

                // Detached and retired ownership also receives an explicit result
                // when its physical file has disappeared from a readable directory.
                var locations = Catalog.ObjectStorageDirectories(definition.Id);
                var absent = known.Values.Where(value => owns(value.ArchiveId) && !indexed.ContainsName(definition.Id, value.Name, local));
                foreach (var group in absent.GroupBy(value =>
                {
                    string directory;
                    return locations.TryGetValue(value.Name, out directory) ? ObjectStoragePath.Absolute(definition, directory) : ObjectDirectory(definition);
                }))
                {
                    InventoryFolderRecord objectFolder;
                    string key = local ? StoragePath.NormalizeRoot(group.Key).ToUpperInvariant() : group.Key.TrimEnd('/');
                    if (!folders.TryGetValue(key, out objectFolder) || !string.IsNullOrEmpty(objectFolder.Error))
                        continue;
                    var present = new HashSet<string>(decoded[key].Select(entry => entry.Name), local ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                    foreach (var item in group.Where(value => !present.Contains(value.Name)))
                        indexed.Add(new IntegrityFinding {
                            ArchiveId = item.ArchiveId,
                            Destination = definition.Id,
                            Folder = objectFolder.Folder,
                            Name = item.Name,
                            ExpectedSize = item.Size,
                            ActualSize = -1,
                            RecordedStatus = item.State,
                            Status = review.ObsoleteIds.Contains(item.ArchiveId) ? "obsolete-archive" : "missing",
                            RepairAction = review.ObsoleteIds.Contains(item.ArchiveId) ? "delete-obsolete" : item.State == "retired" ? "clear-absent-record" : "replace",
                            RequiresManualRepairConfirmation = true
                        });
                }
            }

            var dataFindings = indexed.Items.Where(item => !item.ArchiveLevel).ToLookup(item => item.ArchiveId);
            foreach (var archive in archives.Values.Where(item => owns(item.ArchiveId) && item.Status != "cleaned" && item.Status != "writing"))
            {
                if (review.ObsoleteIds.Contains(archive.ArchiveId))
                {
                    if (!indexed.HasArchiveLevel(archive.ArchiveId))
                        indexed.Add(new IntegrityFinding {
                            ArchiveId = archive.ArchiveId,
                            ArchiveLevel = true,
                            Destination = "",
                            Name = archive.ArchiveName,
                            Status = "obsolete-archive",
                            RepairAction = "delete-obsolete",
                            RequiresManualRepairConfirmation = true
                        });
                    continue;
                }

                var findings = dataFindings[archive.ArchiveId].ToList();
                bool retained = Catalog.ArchiveHasCurrentFiles(archive.ArchiveId) || Catalog.IsNodeArchive(archive.ArchiveId);
                if (retained && !Catalog.HasCompleteArchiveManifest(archive.ArchiveId) && archive.Status != "failed" && archive.Status != "damaged")
                {
                    Catalog.RecordInspectedArchiveProblem(archive.ArchiveId);
                    indexed.Add(new IntegrityFinding {
                        ArchiveId = archive.ArchiveId,
                        ArchiveLevel = true,
                        Destination = "",
                        Name = archive.ArchiveName,
                        Status = "manifest-incomplete",
                        RepairAction = "replace",
                        RequiresManualRepairConfirmation = true
                    });
                    continue;
                }

                if (retained && archive.Status == "complete" && (archive.CurrentValidCopies < archive.TargetCopies || !Catalog.HasReadableInspectedManifest(archive.ArchiveId)))
                {
                    Catalog.RecordInspectedArchiveProblem(archive.ArchiveId);
                    indexed.Add(new IntegrityFinding {
                        ArchiveId = archive.ArchiveId,
                        ArchiveLevel = true,
                        Destination = "",
                        Name = archive.ArchiveName,
                        Status = "redundancy-shortfall",
                        RepairAction = "replace",
                        RequiresManualRepairConfirmation = true
                    });
                    continue;
                }

                if (!new[]
                {
                    "complete",
                    "repair-required",
                    "redundancy-shortfall",
                    "failed",
                    "damaged",
                    "snapshot-history"
                }.Contains(archive.Status))
                {
                    Catalog.RecordInspectedArchiveProblem(archive.ArchiveId);
                    indexed.Add(new IntegrityFinding {
                        ArchiveId = archive.ArchiveId,
                        ArchiveLevel = true,
                        Destination = "",
                        Name = archive.ArchiveName,
                        Status = "state-mismatch",
                        RepairAction = "correct-archive-state",
                        RequiresManualRepairConfirmation = true
                    });
                    continue;
                }

                if (archive.Status == "failed" || archive.Status == "damaged")
                {
                    if (!indexed.HasArchiveLevel(archive.ArchiveId))
                        indexed.Add(new IntegrityFinding {
                            ArchiveId = archive.ArchiveId,
                            ArchiveLevel = true,
                            Destination = "",
                            Name = archive.ArchiveName,
                            Status = archive.Status == "damaged" ? "damaged" : Catalog.HasOutstandingTransfer(archive.ArchiveId) ? "reconcile-required" : "interrupted",
                            RepairAction = "finish-interrupted",
                            RequiresManualRepairConfirmation = true
                        });
                }
                else if ((archive.Status == "repair-required" || archive.Status == "redundancy-shortfall") &&
                    !findings.Any(item => item.RequiresManualRepairConfirmation ||
                    item.Status.StartsWith("unavailable", StringComparison.Ordinal)))
                    indexed.Add(new IntegrityFinding {
                        ArchiveId = archive.ArchiveId,
                        ArchiveLevel = true,
                        Destination = "",
                        Name = archive.ArchiveName,
                        Status = archive.Status,
                        RepairAction = archive.CurrentValidCopies >= archive.TargetCopies ? "correct-archive-state" : "replace",
                        RequiresManualRepairConfirmation = true
                    });
            }

            review.Findings.Clear();
            review.Findings.AddRange(indexed.Items);
            review.ManagedUnregistered = review.Findings.Count(item => item.RepairAction == "delete-orphan" || item.RepairAction == "register-copy");
            review.RegistrationAnomalies = review.Findings.Count(item => item.RepairAction == "remove-obsolete-registration" || item.RepairAction == "clear-absent-record");
            review.TaskScope = ReviewScope(review.TaskIds);
            review.Topology = ReviewTopology();
            review.CatalogStamp = Catalog.DataChangeStamp;
            review.RecoveryRevision = Catalog.RecoveryRevision;
            review.Completed = true;
            lastIntegrityReview = review;
        }

        // Keep replacement order while indexing each finding once per review.
        private sealed class ReviewFindingIndex
        {
            internal readonly LinkedList<IntegrityFinding> Items = new LinkedList<IntegrityFinding>();
            private readonly Dictionary<Tuple<string, string, bool>, Queue<LinkedListNode<IntegrityFinding>>> exact = new Dictionary<Tuple<string, string, bool>, Queue<LinkedListNode<IntegrityFinding>>>();
            private readonly Dictionary<string, HashSet<string>> names = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal), folded = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            private readonly Dictionary<long, int> levels = new Dictionary<long, int>();
            internal ReviewFindingIndex(IEnumerable<IntegrityFinding> findings)
            {
                foreach (var finding in findings)
                    Append(finding);
            }

            private void Append(IntegrityFinding value)
            {
                var key = Tuple.Create(value.Destination, value.Name, value.ArchiveLevel);
                Queue<LinkedListNode<IntegrityFinding>> nodes;
                if (!exact.TryGetValue(key, out nodes))
                    exact[key] = nodes = new Queue<LinkedListNode<IntegrityFinding>>();
                nodes.Enqueue(Items.AddLast(value));
                string destination = value.Destination ?? "";
                HashSet<string> set;
                if (!names.TryGetValue(destination, out set))
                    names[destination] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(value.Name);
                if (!folded.TryGetValue(destination, out set))
                    folded[destination] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(value.Name);
                if (value.ArchiveLevel)
                {
                    int count;
                    levels.TryGetValue(value.ArchiveId, out count);
                    levels[value.ArchiveId] = count + 1;
                }
            }

            internal bool ContainsName(string destination, string name, bool ignoreCase)
            {
                HashSet<string> set;
                return (ignoreCase ? folded : names).TryGetValue(destination ?? "", out set) && set.Contains(name);
            }

            internal bool HasArchiveLevel(long id)
            {
                int count;
                return levels.TryGetValue(id, out count) && count > 0;
            }

            internal void Add(IntegrityFinding value)
            {
                Queue<LinkedListNode<IntegrityFinding>> nodes;
                if (exact.TryGetValue(Tuple.Create(value.Destination, value.Name, value.ArchiveLevel), out nodes) && nodes.Count > 0)
                {
                    var old = nodes.Dequeue();
                    Items.Remove(old);
                    if (old.Value.ArchiveLevel)
                        levels[old.Value.ArchiveId]--;
                }

                Append(value);
            }
        }

        private AllBackupCheckResult ReviewCommittedInventory(AllBackupCheckResult original)
        {
            FlushRepositoryResults();
            var scope = IntegrityTaskIds(original.TaskIds);
            var result = new AllBackupCheckResult
            {
                TaskIds = original.TaskIds,
                ReferencedArchives = original.ReferencedArchives,
                ReferenceIdentity = original.ReferenceIdentity
            };
            result.Errors.AddRange(original.Errors);
            result.ObsoleteIds.UnionWith(Catalog.ObsoleteOwnedArchiveIds(scope));
            result.ObsoleteIds.UnionWith(original.ObsoleteIds.Where(id => Catalog.GetArchive(id).Status != "cleaned" && !Catalog.ArchiveHasCurrentFiles(id)));
            var snapshots = new Dictionary<string, Dictionary<string, InventoryFolderRecord>>();
            foreach (var pair in original.Directories)
            {
                var records = new Dictionary<string, InventoryFolderRecord>();
                var definition = Catalog.LoadTopology().Destinations.FirstOrDefault(item => item.Id == pair.Key);
                if (definition == null)
                    continue;
                bool local = RepositoryModules.IsFileSystem(definition.Protocol);
                var locations = Catalog.ObjectStorageDirectories(pair.Key);
                var folders = new HashSet<string>(pair.Value);
                foreach (var item in Catalog.GetWarehouseObjects(pair.Key).Where(item => scope == null || scope.Contains(item.TaskId)))
                {
                    string directory;
                    if (locations.TryGetValue(item.Name, out directory))
                        folders.Add(ObjectStoragePath.Absolute(definition, directory));
                }

                foreach (string folder in folders)
                {
                    var record = Catalog.GetInventoryFolder(pair.Key, folder);
                    if (record == null)
                        continue;
                    records[local ? StoragePath.NormalizeRoot(folder).ToUpperInvariant() : folder.TrimEnd('/')] = record;
                }

                snapshots[pair.Key] = records;
            }

            CompleteIntegrityReview(result, snapshots);
            return result;
        }

        private AllBackupCheckResult ReviewLocalInventory(ISet<string> taskIds)
        {
            if (taskIds == null)
                Catalog.RefreshLocalBackupNodes();
            else
                foreach (string task in taskIds)
                    Catalog.RefreshLocalBackupNode(task);
            var review = new AllBackupCheckResult
            {
                TaskIds = taskIds == null ? null : taskIds.ToList()
            };
            foreach (var definition in Catalog.LoadTopology().Destinations)
            {
                bool local = RepositoryModules.IsFileSystem(definition.Protocol);
                var folders = new HashSet<string>(local ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                var pending = new Stack<string>();
                pending.Push(ObjectDirectory(definition));
                while (pending.Count > 0)
                {
                    string folder = BackupCatalog.InventoryFolderKey(pending.Pop());
                    if (!folders.Add(folder))
                        continue;
                    var record = Catalog.GetInventoryFolder(definition.Id, folder);
                    if (record == null)
                        continue;
                    foreach (var entry in JsonSettings.Decode(record.Json, new List<StorageEntry>()).Where(item => item.Directory))
                        pending.Push(entry.Location);
                }

                review.Directories[definition.Id] = folders.ToList();
            }

            // No directory enumeration: repair starts with persisted observations
            // and rechecks only the objects that those observations marked.
            return ReviewCommittedInventory(review);
        }

        private RemoteObjectInfo RecheckReviewObject(IntegrityFinding finding, IDictionary<string, DestinationDefinition> definitions, bool currentLocation = false)
        {
            var definition = definitions[finding.Destination];
            string directory = Catalog.ObjectStorageDirectory(finding.Destination, BackupObjectNaming.ObjectName(finding.Name) ?? finding.Name);
            string folder = currentLocation && directory != null ? ObjectStoragePath.Absolute(definition, directory) : finding.Folder ?? ObjectStoragePath.Absolute(definition, directory ?? "");
            if (currentLocation)
                finding.Folder = folder;
            var adapter = OpenWarehouseLocation(definition, folder, OperationCancellation);
            try
            {
                return adapter.Stat(finding.Name);
            }
            finally
            {
                DestinationAdapterRegistry.DisposeAdapter(adapter);
            }
        }

        private static string IntegrityMismatch(IntegrityFinding finding)
        {
            string status = finding.Status ?? "", reason = status == "missing" 
                ? "未找到已登记的备份文件" 
                : status == "size-or-name-mismatch" 
                ? "文件名称或大小与登记不符" 
                : status == "content-damaged" 
                ? "下载时发现内容哈希不符，需要健康副本" 
                : status == "unregistered-managed" 
                ? "未登记文件，清理尚未完成" 
                : status == "unregistered-copy" 
                ? "文件名和大小匹配，但此仓库副本尚未登记" 
                : status == "manifest-incomplete" 
                ? "压缩包文件清单缺失或不完整" 
                : status == "obsolete-archive" 
                ? "已失效的压缩包尚未清理" 
                : status == "state-mismatch" 
                ? "压缩包登记状态尚未改正" 
                : status == "present-size-matched" ? "文件名和大小一致，但副本登记状态尚未更新" : status.StartsWith("unavailable", StringComparison.Ordinal) ? "仓库文件暂时无法查询：" +
                status : "登记状态仍需处理：" +
                status;
            if (!string.IsNullOrEmpty(finding.RecordedStatus))
                reason += "；登记副本状态：" + HistoryStatus.Text(finding.RecordedStatus);
            if (!finding.ArchiveLevel && finding.ExpectedSize >= 0)
                reason += "；登记大小：" + Display.Bytes(finding.ExpectedSize) + "，实际大小：" + (finding.ActualSize >= 0 ? Display.Bytes(finding.ActualSize) : "未取得");
            if (!string.IsNullOrEmpty(finding.Folder))
                reason += "；位置：" + finding.Folder;
            return reason;
        }

        private IList<string> RepairReviewFiles(AllBackupCheckResult review, Action<string> report)
        {
            var errors = DeleteUnregisteredTemporaryFiles(review, report).Errors.ToList();
            var unreferenced = ReviewedUnreferencedInterrupted(review);
            var corrections = new Dictionary<long, List<IntegrityFinding>>();
            var obsolete = new HashSet<long>(review.Findings.Where(item => item.RepairAction == "remove-obsolete-registration").Select(item => item.ArchiveId));
            var removed = obsolete.Count == 0 ? new HashSet<long>() : new HashSet<long>(Catalog.UnregisterUnusedCatalogSnapshots(DatabaseBackupTargets(), false, obsolete));
            Dictionary<string, DestinationDefinition> definitions = null;
            foreach (var finding in review.Findings.Where(item => (item.RepairAction == "delete-orphan" || item.RepairAction == "register-copy" || item.RepairAction == "correct-state" ||
                item.RepairAction == "clear-absent-record" ||
                item.RepairAction == "remove-obsolete-registration" ||
                item.Status.StartsWith("unavailable", StringComparison.Ordinal)) &&
                !(item.RepairAction == "delete-orphan" &&
                BackupObjectNaming.IsTemporary(item.Name)))
                .ToList())
            {
                OperationCancellation.ThrowIfCancellationRequested();
                if (ActiveBackup != null)
                    ActiveBackup.Checkpoint();
                try
                {
                    if (finding.RepairAction == "remove-obsolete-registration" && !removed.Contains(finding.ArchiveId))
                        continue;
                    if (definitions == null)
                        definitions = Catalog.LoadTopology().Destinations.ToDictionary(item => item.Id);
                    var definition = definitions[finding.Destination];
                    bool local = RepositoryModules.IsFileSystem(definition.Protocol);
                    string folder = finding.Folder ??
                        ObjectStoragePath.Absolute(definition, Catalog.ObjectStorageDirectory(finding.Destination, BackupObjectNaming.ObjectName(finding.Name) ?? finding.Name) ??
                        "");
                    var info = RecheckReviewObject(finding, definitions);
                    if (finding.Status.StartsWith("unavailable", StringComparison.Ordinal) && finding.ArchiveId > 0)
                    {
                        if (info.Exists && info.Name == finding.Name && info.Size == finding.ExpectedSize)
                            finding.RepairAction = "correct-state";
                        else
                        {
                            finding.Status = info.Exists ? "size-or-name-mismatch" : "missing";
                            finding.RepairAction = "replace";
                            finding.RequiresManualRepairConfirmation = true;
                            Catalog.ApplyCheckedStates(finding.ArchiveId, new[] { finding });
                            continue;
                        }
                    }

                    if (finding.RepairAction == "register-copy" && info.Exists && info.Size == finding.ExpectedSize && info.Name == finding.Name &&
                        Catalog.RestoreInspectedCopy(finding.ArchiveId, finding.Name, finding.Destination))
                    {
                        Catalog.RememberObjectDirectory(finding.Destination, finding.Name, ObjectStoragePath.Relative(definition, folder));
                        finding.RepairAction = "correct-state";
                    }

                    if (finding.RepairAction == "correct-state")
                    {
                        if (!info.Exists || info.Size != finding.ExpectedSize || info.Name != finding.Name)
                            continue;
                        List<IntegrityFinding> group;
                        if (!corrections.TryGetValue(finding.ArchiveId, out group))
                        {
                            group = new List<IntegrityFinding>();
                            corrections.Add(finding.ArchiveId, group);
                        }

                        group.Add(new IntegrityFinding {
                            ArchiveId = finding.ArchiveId,
                            Destination = finding.Destination,
                            Name = finding.Name,
                            ExpectedSize = finding.ExpectedSize,
                            ActualSize = info.Size,
                            Status = "present-size-matched",
                            RepairApplied = true
                        });
                        continue;
                    }

                    if (finding.RepairAction == "register-copy")
                    {
                        if (!info.Exists)
                            continue;
                        finding.RepairAction = "delete-orphan";
                    }

                    if (finding.RepairAction == "clear-absent-record")
                    {
                        if (!info.Exists)
                            Catalog.ClearAbsentInspectedRecord(finding.Name, finding.Destination);
                        continue;
                    }

                    // Re-evaluate ownership immediately before a destructive action.
                    // A successfully registered/committed late upload always wins.
                    if (Catalog.GetWarehouseObjects(finding.Destination).Any(item => item.Name == finding.Name))
                        continue;
                    var owner = Catalog.ListArchives(true).FirstOrDefault(a => a.ArchiveName == BackupObjectNaming.ArchiveName(finding.Name) && a.Status != "cleaned");
                    if (owner != null && !review.ObsoleteIds.Contains(owner.ArchiveId) && !Catalog.HasCompleteArchiveManifest(owner.ArchiveId))
                    {
                        if (unreferenced.Contains(owner.ArchiveId))
                            continue;
                        errors.Add(finding.Name + "：分卷清单缺失或不完整，已保留现有文件及版本引用。");
                        continue;
                    }

                    if (!BackupObjectNaming.IsManaged(finding.Name))
                        continue;
                    if (report != null)
                        report("正在清理无登记备份文件：" + finding.Name);
                    var request = new WarehouseDeleteRequest(finding.Destination, folder, finding.Name, EntryLocation(definition, folder, finding.Name), false, info.Size, 0);
                    errors.AddRange(DeleteWarehouseEntries(new[] { request }, report).Errors);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/IntegrityReview.cs:RepairReviewFiles", error);
                    if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is NotSupportedException) && !(error is ArgumentException))
                        throw;
                    finding.Detail = error.Message;
                    errors.Add(finding.Name + "：" + error.Message);
                }
            }

            foreach (var group in corrections)
                Catalog.ApplyCheckedStates(group.Key, group.Value);
            foreach (var finding in review.Findings.Where(item => item.ArchiveLevel && item.RepairAction == "correct-archive-state"))
            {
                var checkedCopies = review.Findings.Where(item => !item.ArchiveLevel && item.ArchiveId == finding.ArchiveId).ToList();
                if (checkedCopies.Count > 0)
                    Catalog.ApplyCheckedStates(finding.ArchiveId, checkedCopies);
            }

            return errors;
        }

        private IList<string> PrepareReviewedArchiveRepairs(AllBackupCheckResult review, WorkspaceDefinition workspace, Action<string> report)
        {
            var errors = new List<string>();
            var ids = new HashSet<long>(review.Findings.Where(item => item.ArchiveId > 0 && item.RequiresManualRepairConfirmation && item.RepairAction != "remove-obsolete-registration" &&
                item.RepairAction != "delete-obsolete")
                .Select(item => item.ArchiveId));
            if (ids.Count == 0)
                return errors;
            errors.AddRange(new ArchiveMaintenance(Catalog, Adapters()) { Control = ActiveBackup }.ReconcileVolumeTransfers(null, ids));
            // Unreferenced stopped outputs stay unfinished until orphan cleanup;
            // they must not become current files merely because repair was clicked.
            return errors;
        }

        private bool RepairChanged(long revision, long deletions)
        {
            return Catalog.RecoveryRevision != revision || System.Threading.Interlocked.Read(ref physicalDeletionSequence) != deletions;
        }

        private const string PendingRepairCatalog = "repair_catalog_pending_v1";
        public IList<string> RepairCheckedBackups(
            WorkspaceDefinition workspace,
            ISet<string> taskIds,
            Action<string> report = null,
            Action<DesktopProgress> progress = null,
            Action<CleanupProgress> cleanupProgress = null
        )
        {
            using (BeginMaintenanceSearch())
                return RepairCheckedBackupsCore(workspace, taskIds, report, progress, cleanupProgress);
        }

        private IList<string> RepairCheckedBackupsCore(WorkspaceDefinition workspace, ISet<string> taskIds, Action<string> report, Action<DesktopProgress> progress, Action<CleanupProgress> cleanupProgress)
        {
            long revision = Catalog.RecoveryRevision, deletions = System.Threading.Interlocked.Read(ref physicalDeletionSequence);
            bool synchronized = false;
            var catalogTargets = DatabaseBackupTargets();
            try
            {
                var rotationErrors = new List<string>();
                foreach (var task in workspace.Tasks.Where(t => (taskIds == null || taskIds.Contains(t.Id)) && Catalog.TaskHasNodes(t.Id)))
                    rotationErrors.AddRange(RecycleTaskNodes(task, DateTime.UtcNow, report));
                if (taskIds == null)
                    try
                    {
                        rotationErrors.AddRange(RetryRetiredNodeDatabases(report));
                    }
                    catch (IOException error)
                    {
                        rotationErrors.Add("旧备份版本数据库删除待重试：" + error.Message);
                    }

                bool taskDeletesAttempted;
                var taskDeleteErrors = RetryTaskDirectoryDeletes(taskIds, report, out taskDeletesAttempted);
                var temporaryErrors = DiscardInactiveUploadTemporaries(taskIds, report, OperationCancellation);
                Catalog.UnregisterAbandonedArchiveOutputs(taskIds, ActiveBackup == null ? null : new HashSet<long>(ActiveBackup.ArchiveIds));
                Catalog.UnregisterDeletedLocalNodeData(taskIds);
                var pendingRepairs = PendingRepairs();
                int pendingBefore = pendingRepairs.Count;
                TrimRepairQueue(pendingRepairs, taskIds);
                if (pendingRepairs.Count != pendingBefore)
                    SaveRepairQueue(pendingRepairs);
                var review = lastIntegrityReview;
                if (review == null || !review.Completed || review.TaskScope != ReviewScope(taskIds) || review.Topology != ReviewTopology())
                    review = ReviewLocalInventory(taskIds);
                else if (taskDeletesAttempted || review.RecoveryRevision != Catalog.RecoveryRevision)
                    review = ReviewCommittedInventory(review);
                if (review.ReferenceIdentity != ReviewReferenceIdentity())
                {
                    var referenceErrors = new List<string>();
                    if (InspectNodeReferences(taskIds, referenceErrors, report))
                    {
                        review.ReferencedArchives = new HashSet<long>(inspectedNodeArchives);
                        review.ReferenceIdentity = ReviewReferenceIdentity();
                    }
                    else
                        review.ReferencedArchives = null;
                    rotationErrors.AddRange(referenceErrors);
                }

                if (review.ReferencedArchives != null && taskIds == null && review.Errors.Count == 0 &&
                    !review.Findings.Any(item => item.Status.StartsWith("unavailable", StringComparison.Ordinal) ||
                    item.Status == "confirmation-pending") &&
                    Catalog.PendingVolumeTransfers()
                    .Count == 0 &&
                    Catalog.PendingUploadStages()
                    .Count == 0)
                    try
                    {
                        review.ObsoleteIds.UnionWith(MarkUnreferencedNodeData(review.ReferencedArchives));
                    }
                    catch (IOException error)
                    {
                        rotationErrors.Add("无引用数据核对未完成：" + error.Message);
                    }
                    catch (UnauthorizedAccessException error)
                    {
                        rotationErrors.Add("无引用数据核对未完成：" + error.Message);
                    }

                if (UnregisterReviewedInterrupted(review))
                    review = ReviewCommittedInventory(review);
                bool repairCatalog = review.Findings.Any(item => item.ArchiveId > 0 && item.RequiresManualRepairConfirmation && !review.ObsoleteIds.Contains(item.ArchiveId) &&
                    Catalog.GetArchive(item.ArchiveId)
                    .TaskId == "__catalog__");
                var errors = RepairReviewFiles(review, report).ToList();
                errors.AddRange(rotationErrors);
                errors.AddRange(taskDeleteErrors);
                errors.AddRange(temporaryErrors);
                QueuePendingUploadDamage(workspace, CheckPendingUploads(taskIds, true, review.ObsoleteIds, false, true));
                review.ObsoleteIds.RemoveWhere(id => Catalog.ArchiveHasCurrentFiles(id));
                if (review.ObsoleteIds.Count > 0 && review.ReferencedArchives != null && review.ReferenceIdentity == ReviewReferenceIdentity() && taskIds == null)
                    Catalog.AllowNodeGarbage(review.ReferencedArchives, review.ObsoleteIds);
                foreach (long id in review.ObsoleteIds)
                    try
                    {
                        errors.AddRange(new ArchiveMaintenance(Catalog, Adapters()) { Control = ActiveBackup, Report = report }.DeleteReviewedObsoleteArchive(id));
                    }
                    catch (Exception error)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/IntegrityReview.cs:RepairCheckedBackupsCore", error);
                        if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is NotSupportedException))
                            throw;
                        errors.Add(Catalog.GetArchive(id).ArchiveName + "：" + error.Message);
                    }

                if (review.ObsoleteIds.Count > 0)
                {
                    var queue = PendingRepairs();
                    int before = queue.Count;
                    TrimRepairQueue(queue, taskIds, review.ObsoleteIds);
                    if (queue.Count != before)
                        SaveRepairQueue(queue);
                }

                errors.AddRange(PrepareReviewedArchiveRepairs(review, workspace, report));
                var detailedArchives = new HashSet<long>(review.Findings.Where(item => !string.IsNullOrEmpty(item.Detail)).Select(item => item.ArchiveId));
                var repairIds = new HashSet<long>(review.Findings.Where(item => item.ArchiveId > 0 && item.RequiresManualRepairConfirmation && !review.ObsoleteIds.Contains(item.ArchiveId) &&
                    !detailedArchives.Contains(item.ArchiveId))
                    .Select(item => item.ArchiveId));
                errors.AddRange(RunConfirmedRepairs(workspace, report, IntegrityTaskIds(taskIds), progress, cleanupProgress, false, true, repairIds, false, true));
                // Reclassify entries exposed by intent cleanup or unregistration using
                // committed inventory changes, without repeating directory enumeration.
                UnregisterReviewedInterrupted(review);
                var residual = ReviewCommittedInventory(review);
                var attempted = new HashSet<string>(review.Findings.Where(item => item.RepairAction == "delete-orphan").Select(item => item.Destination + "\0" + item.Folder + "\0" + item.Name));
                var newOrphans = new AllBackupCheckResult
                {
                    TaskIds = review.TaskIds
                };
                newOrphans.Findings.AddRange(residual.Findings.Where(item => (item.RepairAction == "delete-orphan" || item.RepairAction == "register-copy") &&
                    !attempted.Contains(item.Destination + "\0" +
                    item.Folder +
                    "\0" +
                    item.Name)));
                errors.AddRange(RepairReviewFiles(newOrphans, report));
                // Reuse the directory inventory and operation-result notifications. Only
                // formerly anomalous names are statted; never repeat a full tree scan.
                FlushRepositoryResults();
                var currentReview = ReviewCommittedInventory(review);
                var unreferenced = ReviewedUnreferencedInterrupted(currentReview);
                var remaining = new List<string>();
                Dictionary<string, DestinationDefinition> recheckDefinitions = null;
                // A repaired pool may retain a different member. Verify current
                // registrations rather than requiring the retired location to exist.
                foreach (var finding in currentReview.Findings.Where(item => item.RequiresManualRepairConfirmation || item.Status.StartsWith("unavailable", StringComparison.Ordinal)))
                {
                    OperationCancellation.ThrowIfCancellationRequested();
                    try
                    {
                        if (finding.RepairAction == "delete-orphan" && BackupObjectNaming.IsTemporary(finding.Name))
                            continue;
                        if (finding.ArchiveId > 0)
                        {
                            var archive = Catalog.GetArchive(finding.ArchiveId);
                            if (archive.Status == "cleaned" || archive.Status == "snapshot-history")
                                continue;
                        }

                        if (finding.ArchiveLevel)
                        {
                            if (unreferenced.Contains(finding.ArchiveId))
                            {
                                remaining.Add(finding.Name + "：备份未完成，残留文件或上传记录仍待清理；不属于有效备份版本引用。");
                                continue;
                            }

                            if (finding.ArchiveId > 0)
                            {
                                var archive = Catalog.GetArchive(finding.ArchiveId);
                                if (archive.Status == "complete" && archive.CurrentValidCopies >= archive.TargetCopies)
                                    continue;
                                remaining.Add(finding.Name + "：已确认健康副本 " + archive.CurrentValidCopies + " / " + archive.TargetCopies + "；登记状态：" + HistoryStatus.Text(archive.Status));
                            }
                            else
                                remaining.Add(finding.Name + "：" + IntegrityMismatch(finding));
                            continue;
                        }

                        if (recheckDefinitions == null)
                            recheckDefinitions = Catalog.LoadTopology().Destinations.ToDictionary(item => item.Id);
                        var info = RecheckReviewObject(
                            finding,
                            recheckDefinitions,
                            finding.ArchiveId > 0 && finding.RepairAction != "delete-orphan" && finding.RepairAction != "remove-obsolete-registration" &&
                            finding.RepairAction != "clear-absent-record"
                        );
                        if (finding.RepairAction == "delete-orphan" || finding.RepairAction == "remove-obsolete-registration")
                        {
                            if (!info.Exists)
                                continue;
                        }
                        else if (finding.RepairAction == "clear-absent-record")
                        {
                            if (!info.Exists)
                                continue;
                        }
                        else if (info.Exists && info.Name == finding.Name && info.Size == finding.ExpectedSize)
                        {
                            if (finding.ArchiveId > 0)
                            {
                                string state;
                                var states = Catalog.IntegrityCopyStates(finding.ArchiveId);
                                if (states.TryGetValue(finding.Destination + "\0" + finding.Name, out state) && state == "present")
                                    continue;
                            }
                        }

                        finding.ActualSize = info.Exists ? info.Size : -1;
                        remaining.Add(finding.Name + "：" + IntegrityMismatch(finding));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/IntegrityReview.cs:RepairCheckedBackupsCore", error);
                        if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is NotSupportedException) && !(error is ArgumentException))
                            throw;
                        remaining.Add(finding.Name + "：" + error.Message);
                    }
                }

                foreach (string error in currentReview.Errors)
                    if (!errors.Contains(error))
                        errors.Add(error);
                errors.AddRange(remaining);
                var final = Catalog.RecoveryRevision == currentReview.RecoveryRevision ? currentReview : ReviewCommittedInventory(currentReview);
                foreach (var finding in final.Findings.Where(item => item.RequiresManualRepairConfirmation))
                    if (!errors.Any(message => message.Contains(finding.Name)))
                        errors.Add(finding.Name + "：" + IntegrityMismatch(finding));
                // Publish only after repair, cleanup and the last remote recheck.
                // A pending flag alone must not turn an unchanged repair into a sync.
                if (catalogTargets.Count > 0 && (RepairChanged(revision, deletions) || repairCatalog))
                    try
                    {
                        Catalog.SetSetting(PendingRepairCatalog, "1");
                        BackupDatabase(null, report);
                        Catalog.SetSetting(PendingRepairCatalog, "");
                        synchronized = true;
                        var queue = PendingRepairs();
                        TrimRepairQueue(queue, taskIds);
                        SaveRepairQueue(queue);
                        var retiredCatalogs = Catalog.ListArchives(true).Where(item => item.TaskId == "__catalog__" && (item.Status == "cleaned" || item.Status == "snapshot-history")).Select(item => item.ArchiveName)
                            .ToList();
                        errors.RemoveAll(message => retiredCatalogs.Any(name => message.StartsWith(name + "：", StringComparison.Ordinal)));
                        final = ReviewCommittedInventory(final);
                    }
                    catch (Exception error)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/IntegrityReview.cs:RepairCheckedBackupsCore", error);
                        if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is NotSupportedException) && !(error is ArgumentException))
                            throw;
                        errors.Add("修复后的数据库同步未完成：" + error.Message);
                    }

                lastIntegrityReview = final;
                return errors.Distinct().ToList();
            }
            finally
            {
                Catalog.ClearNodeGarbageReview();
                CleanupStoppedUploadTemporaries(report);
                if (!synchronized && catalogTargets.Count > 0 && RepairChanged(revision, deletions))
                    Catalog.SetSetting(PendingRepairCatalog, "1");
            }
        }
    }
}
