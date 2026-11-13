using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        private bool RebuildUnfinishedArchiveFromSources(long id, BackupTaskDefinition task, Action<string> report)
        {
            var node = Catalog.GetLocalBackupNodes(task.Id).SingleOrDefault();
            if (node == null)
                return false;
            var files = Catalog.ReadLocalBackupNode(node.Id);
            var selected = files.Files.Where(file => file.ArchiveId == id).ToList();
            if (selected.Count == 0)
                return false;
            // Reuse this inspection's reference evidence. Direct callers may use
            // verified local node caches, but must never guess about unread history.
            var historicalNodes = Catalog.GetNodes(task.Id);
            string signature = HistoricalSignature(historicalNodes), inspected;
            ISet<long> references;
            if (!inspectedHistoricalSignatures.TryGetValue(task.Id, out inspected) || inspected != signature)
            {
                references = new HashSet<long>();
                foreach (var historical in historicalNodes)
                {
                    var history = ReadCachedNodeDatabase(historical);
                    if (history == null)
                        throw new IOException("无法确认历史版本引用，已保留原包和未完成版本，请先检查完整性。");
                    references.UnionWith(history.Files.Select(file => file.ArchiveId));
                }

                inspectedHistoricalArchives[task.Id] = references;
                inspectedHistoricalSignatures[task.Id] = signature;
            }
            else
                references = inspectedHistoricalArchives[task.Id];
            if (references.Contains(id))
                return false;
            if (Catalog.HasOutstandingTransfer(id) || Catalog.PendingUploadStages().Any(stage => stage.ArchiveId == id))
                return false;
            var states = Catalog.IntegrityCopyStates(id).Values;
            if (Catalog.HasCompleteArchiveManifest(id) && (Catalog.HasReadableInspectedManifest(id) || states.Any(state => state.StartsWith("unavailable", StringComparison.Ordinal))))
                return false;
            var archive = Catalog.GetArchive(id);
            if (archive.Status == "cleaned" || archive.Status == "writing")
                return false;
            var sources = new List<SourceItem>();
            foreach (var file in selected)
            {
                if (string.IsNullOrEmpty(file.SourcePath))
                    throw new IOException("备份清单缺少源路径，已保留原包及版本引用：" + file.Path);
                SourceItem source;
                try
                {
                    source = SourceItem.FromFile(file.SourcePath, file.Path);
                }
                catch (IOException error)
                {
                    throw new IOException("无法读取原源文件，已保留原包及版本引用：" + file.SourcePath + "；" + error.Message, error);
                }

                if (source.Size != file.Size || source.Modified != file.Modified)
                    throw new IOException("源文件大小或修改时间已变化，不能替换版本中的原文件，已保留原包及版本引用：" + file.SourcePath);
                source.EntryPath = !string.IsNullOrEmpty(file.ContentPath) ? file.ContentPath : !string.IsNullOrEmpty(file.EntryPath) ? file.EntryPath : file.Path;
                sources.Add(source);
            }

            RepackPolicy policy;
            if (Catalog.GetReplicaPools(id).Count > 0 || Catalog.VolumePlacements(id).Count > 0)
                policy = OriginalRepackPolicy(archive);
            else
                policy = new RepackPolicy
                {
                    Roots = task.TargetIds.ToList(),
                    Copies = archive.TargetCopies,
                    Password = Catalog.GetArchivePassword(id)
                };
            if (policy.Roots.Count == 0 || task.PendingTargetReplacement)
                throw new IOException("未完成备份缺少可用的原仓库策略，已保留原包和版本。");
            WorkspaceMigration.ValidateTargetPolicy(task, Catalog.LoadTopology());
            if (report != null)
                report("正在从未变化的本地源文件重建数据包：" + archive.ArchiveName);
            var uploaded = WriteRepackedArchive(sources, policy, "__repack__:" + task.Id, PreferredVolumeMiB * 1024 * 1024, report, task.Id);
            VerifyRepackedArchive(uploaded.Archive.ArchiveId, report);
            foreach (var source in sources)
                source.CheckUnchanged();
            // The catalog switches the local-only version in one transaction,
            // after all copies pass content verification. A stop leaves old refs.
            Catalog.ReplaceUnfinishedArchive(node, id, uploaded.Archive.ArchiveId);
            LastRepairSources.AddRange(sources);
            Catalog.ClearArchiveUnrepairable(id);
            if (report != null)
                report("数据包已重建，本地备份清单已更新：" + archive.ArchiveName + " → " + uploaded.Archive.ArchiveName);
            return true;
        }
    }
}
