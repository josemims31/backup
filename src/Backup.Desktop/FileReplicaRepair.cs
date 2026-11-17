using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        private readonly object fileReplicaPeerGate = new object();
        private readonly Dictionary<long, Dictionary<string, HashSet<long>>> inspectedFileReplicaPeers = new Dictionary<long, Dictionary<string, HashSet<long>>>();
        private string fileReplicaPeerIdentity;
        private string currentFileReplicaPeerIdentity;
        private long fileReplicaPeerStamp = long.MinValue;
        private readonly Dictionary<long, bool> readableFileReplicaPeers = new Dictionary<long, bool>();
        private void RememberFileReplicaPeers(NodeFileDatabase files)
        {
            lock (fileReplicaPeerGate)
            {
                foreach (var file in files.Files.Where(file => file.Replicas != null && file.Replicas.Count > 0))
                    foreach (var copy in file.Copies)
                        if (!inspectedFileReplicaPeers.ContainsKey(copy.ArchiveId))
                            inspectedFileReplicaPeers[copy.ArchiveId] = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
                if (inspectedFileReplicaPeers.Count == 0)
                    return;
                foreach (var file in files.Files)
                    foreach (var copy in file.Copies)
                    {
                        Dictionary<string, HashSet<long>> required;
                        if (!inspectedFileReplicaPeers.TryGetValue(copy.ArchiveId, out required))
                            continue;
                        string key = RepackFileKey(copy);
                        HashSet<long> peers;
                        if (!required.TryGetValue(key, out peers))
                            required[key] = peers = new HashSet<long>();
                        peers.UnionWith(file.Copies.Where(other => other.ArchiveId != copy.ArchiveId).Select(other => other.ArchiveId));
                    }
            }
        }

        internal bool CanRebuildFileArchiveFromPeers(long id)
        {
            List<long[]> required;
            string identity;
            lock (fileReplicaPeerGate)
            {
                Dictionary<string, HashSet<long>> files;
                if (!inspectedFileReplicaPeers.TryGetValue(id, out files) || files.Count == 0)
                    return false;
                required = files.Values.Select(values => values.ToArray()).ToList();
                identity = fileReplicaPeerIdentity;
            }
            if (identity == null)
                return false;
            long stamp = Catalog.DataChangeStamp;
            bool refresh;
            lock (fileReplicaPeerGate)
                refresh = fileReplicaPeerStamp != stamp;
            if (refresh)
            {
                string currentIdentity = ReviewReferenceIdentity();
                lock (fileReplicaPeerGate)
                {
                    currentFileReplicaPeerIdentity = currentIdentity;
                    fileReplicaPeerStamp = stamp;
                    readableFileReplicaPeers.Clear();
                }
            }
            lock (fileReplicaPeerGate)
                if (identity != currentFileReplicaPeerIdentity)
                    return false;
            return required.All(peers => peers.Any(peer =>
            {
                bool healthy;
                lock (fileReplicaPeerGate)
                    if (readableFileReplicaPeers.TryGetValue(peer, out healthy))
                        return healthy;
                healthy = Catalog.HasReadableInspectedManifest(peer);
                lock (fileReplicaPeerGate)
                    readableFileReplicaPeers[peer] = healthy;
                return healthy;
            }));
        }

        private bool RebuildFileReplicaArchive(long id, BackupTaskDefinition task, Action<string> report)
        {
            if (Catalog.HasOutstandingTransfer(id) || Catalog.GetLocalBackupNodes(task.Id).Any(node => Catalog.ReadLocalBackupNode(node.Id).ArchiveIds.Contains(id)))
                return false;
            var states = new List<RepackNode>();
            var selected = new Dictionary<string, NodeFile>(StringComparer.Ordinal);
            var alternatives = new Dictionary<string, List<NodeFile>>(StringComparer.Ordinal);
            var referenceErrors = new List<string>();
            bool complete = InspectNodeReferences(new HashSet<string> { task.Id }, referenceErrors, report, (node, files) =>
            {
                states.Add(new RepackNode { Node = node, References = new HashSet<long>(files.ArchiveIds) });
                foreach (var file in files.Files)
                    foreach (var copy in file.Copies.Where(copy => copy.ArchiveId == id))
                    {
                        string key = RepackFileKey(copy);
                        NodeFile prior;
                        if (selected.TryGetValue(key, out prior) && (prior.Size != copy.Size || prior.Modified != copy.Modified))
                            throw new IOException("副本文件清单不一致，已保留原版本。");
                        selected[key] = copy;
                        List<NodeFile> values;
                        if (!alternatives.TryGetValue(key, out values))
                            alternatives[key] = values = new List<NodeFile>();
                        values.AddRange(file.Copies.Where(other => other.ArchiveId != id));
                    }
            });
            if (!complete)
                throw new IOException(string.Join("；", referenceErrors));
            if (selected.Count == 0 || !alternatives.Values.Any(values => values.Count > 0))
                return false;
            var readable = new Dictionary<long, bool>();
            var peers = new Dictionary<string, NodeFile>(StringComparer.Ordinal);
            foreach (var pair in selected)
            {
                foreach (var copy in alternatives[pair.Key])
                {
                    bool healthy;
                    if (!readable.TryGetValue(copy.ArchiveId, out healthy))
                        readable[copy.ArchiveId] = healthy = Catalog.HasReadableInspectedManifest(copy.ArchiveId);
                    if (healthy)
                    {
                        peers[pair.Key] = copy;
                        break;
                    }
                }
                if (!peers.ContainsKey(pair.Key))
                    return false;
            }
            RepackCheckpoint("repair-file-replica");
            int volume = (int)Math.Min(PreferredVolumeMiB * 1048576L, OperationMemory.AvailableBytes / 4);
            volume -= volume % 4096;
            if (volume < 4096)
                throw new IOException("内存不足，已保留原版本。");
            var policy = OriginalRepackPolicy(Catalog.GetArchive(id));
            var replacement = new Dictionary<string, RepackedFile>(StringComparer.Ordinal);
            var touched = new HashSet<long>();
            var result = new WarehouseCompactionResult();
            using (var cache = new MemoryReadCache(Math.Max(ArchiveObjectIO.MaximumVolumeBytes, volume), OperationMemory))
            using (var readers = new RepackArchiveReaders(this, cache, report))
            {
                var sources = new List<SourceItem>();
                var ordered = selected.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
                foreach (var pair in ordered)
                {
                    string entry = RepackEntryPath(pair.Value);
                    var peer = peers[pair.Key];
                    var request = new RestoreRequest { RelativePath = "file", File = new FileHistoryRow { ArchiveId = peer.ArchiveId, Path = peer.Path,
                        EntryPath = peer.EntryPath, ContentPath = peer.ContentPath, Size = peer.Size, Modified = peer.Modified } };
                    var source = SourceItem.FromStream(() => new ProducerReadStream(request.File.Size, (output, cancel) => readers.Extract(request, output, cancel), readers.Cancellation), peer.Size, entry);
                    source.Modified = peer.Modified;
                    sources.Add(source);
                }
                if (report != null)
                    report("正在从健康文件副本重建压缩包：" + Catalog.GetArchive(id).ArchiveName);
                var written = WriteRepackedArchive(sources, policy, "__repack__:" + task.Id + ":" + Guid.NewGuid().ToString("N"), volume, report, task.Id);
                VerifyRepackedArchive(written.Archive.ArchiveId, report);
                Catalog.AdoptRepackedData(written.Archive.ArchiveId, task.Id);
                touched.Add(written.Archive.ArchiveId);
                for (int index = 0; index < ordered.Count; index++)
                    replacement[ordered[index].Key] = new RepackedFile { ArchiveId = written.Archive.ArchiveId, EntryPath = sources[index].StoredPath };
            }
            foreach (var state in states.Where(state => state.References.Contains(id)))
            {
                RepackNodeBatch(new[] { state }, OriginalRepackPolicy(Catalog.GetArchive(state.Node.ArchiveId)), replacement, new HashSet<long> { id }, result, touched, volume, report);
                if (result.Errors.Count > 0)
                    throw new IOException(string.Join("；", result.Errors));
            }
            var references = new HashSet<long>(states.SelectMany(state => state.References));
            if (references.Contains(id))
                throw new IOException("副本清单仍引用原包，已保留原文件。");
            Catalog.AllowNodeGarbage(references, new HashSet<long> { id });
            try
            {
                var errors = new ArchiveMaintenance(Catalog, Adapters()) { Control = ActiveBackup, Report = report }.DeleteReviewedObsoleteArchive(id);
                if (errors.Count > 0)
                    throw new IOException(string.Join("；", errors));
            }
            finally
            {
                Catalog.ClearNodeGarbageReview();
            }
            return true;
        }
    }
}
