using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Backup.Core
{
    public static class StoragePath
    {
        public static string NormalizeRoot(string path)
        {
            if (path != null && path.Length == 2 && path[1] == ':')
                path += "\\";
            string full = Path.GetFullPath(path);
            return full.Length == 3 && full[1] == ':' ? full : full.TrimEnd('\\', '/');
        }
    }

    public sealed class DestinationDefinition
    {
        public string Id;
        public string Protocol;
        public string Location;
        public long MaxBytes;
        public bool Enabled = true;
        public bool Retired;
    }

    public sealed class BackupGroupDefinition
    {
        public string Id;
        public string Name;
        public string Notes;
        public string Mode = "raid0";
        public bool Retired;
        public List<string> DestinationIds = new List<string>();
        public List<string> ChildGroupIds = new List<string>();
    }

    public sealed class StorageTopology
    {
        public List<DestinationDefinition> Destinations = new List<DestinationDefinition>();
        public List<BackupGroupDefinition> Groups = new List<BackupGroupDefinition>();
        public List<string> PolicyRoots;
        public StorageTopology Normalize()
        {
            if (Destinations == null || Groups == null)
                throw new ArgumentException("Destination/group lists required.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var locations = new List<string>();
            var remoteLocations = new List<Uri>();
            var normalized = new StorageTopology();
            foreach (DestinationDefinition item in Destinations)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id) || string.IsNullOrWhiteSpace(item.Protocol) || string.IsNullOrWhiteSpace(item.Location) ||
                    item.MaxBytes <= 0)
                    throw new ArgumentException("Destination IDs must be unique and capacity positive.");
                string protocol = item.Protocol.ToLowerInvariant();
                string location = item.Location;
                if (RepositoryModules.IsFileSystem(protocol))
                {
                    location = StoragePath.NormalizeRoot(location);
                    foreach (string prior in locations)
                        if (string.Equals(prior, location, StringComparison.OrdinalIgnoreCase) || prior.StartsWith(location.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ||
                            location.StartsWith(prior.TrimEnd('\\') +
                            "\\", StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException("Filesystem destinations must not overlap.");
                    locations.Add(location);
                }

                if (!RepositoryModules.IsFileSystem(protocol) && (protocol == "webdav" || protocol == "sftp" || RepositoryModules.Find(protocol) != null))
                {
                    location = RepositoryModules.NormalizeLocation(protocol, location, false);
                    Uri uri;
                    if (!Uri.TryCreate(location, UriKind.Absolute, out uri))
                        throw new ArgumentException("仓库地址无效。");
                    string decoded = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');
                    if (decoded.IndexOf('\\') >= 0 || decoded.IndexOf('\0') >= 0)
                        throw new ArgumentException("远程目录包含无效路径。");
                    foreach (var prior in remoteLocations)
                        if (prior.Scheme == uri.Scheme && prior.Authority.Equals(uri.Authority, StringComparison.OrdinalIgnoreCase))
                        {
                            string old = Uri.UnescapeDataString(prior.AbsolutePath).TrimEnd('/');
                            if (old == decoded || old.StartsWith(decoded + "/", StringComparison.Ordinal) || decoded.StartsWith(old + "/", StringComparison.Ordinal))
                                throw new ArgumentException("远程仓库目录不能重叠。");
                        }

                    remoteLocations.Add(uri);
                    location = uri.AbsoluteUri;
                }

                normalized.Destinations.Add(new DestinationDefinition { Id = item.Id, Protocol = protocol, Location = location, MaxBytes = item.MaxBytes, Enabled = item.Enabled, Retired = item.Retired });
            }

            var groupIds = new HashSet<string>(StringComparer.Ordinal);
            var assigned = new HashSet<string>(StringComparer.Ordinal);
            foreach (BackupGroupDefinition group in Groups)
            {
                if (group == null || string.IsNullOrWhiteSpace(group.Id) || !groupIds.Add(group.Id) || group.DestinationIds == null || group.ChildGroupIds == null ||
                    group.DestinationIds.Count +
                    group.ChildGroupIds.Count == 0)
                    throw new ArgumentException("Group IDs must be unique and groups cannot be empty.");
                if (group.Mode != null && group.Mode != "raid0" && group.Mode != "raid1")
                    throw new ArgumentException("无效的组合类型。");
                var copy = new BackupGroupDefinition
                {
                    Id = group.Id,
                    Name = group.Name ?? group.Id,
                    Notes = group.Notes ?? "",
                    Mode = group.Mode ?? "raid0",
                    Retired = group.Retired
                };
                foreach (string id in group.DestinationIds)
                {
                    if (!ids.Contains(id) || copy.DestinationIds.Contains(id))
                        throw new ArgumentException("Unknown or duplicate destination reference.");
                    assigned.Add(id);
                    copy.DestinationIds.Add(id);
                }

                copy.ChildGroupIds.AddRange(group.ChildGroupIds.Distinct());
                normalized.Groups.Add(copy);
            }

            foreach (DestinationDefinition item in normalized.Destinations.Where(x => !assigned.Contains(x.Id)))
            {
                string id = "single:" + item.Id;
                if (!groupIds.Add(id))
                    throw new ArgumentException("Group ID conflicts with an implicit singleton group.");
                normalized.Groups.Add(new BackupGroupDefinition { Id = id, Name = item.Id, Notes = "", DestinationIds = new List<string> { item.Id } });
            }

            foreach (var group in normalized.Groups)
                normalized.ResolveDestinations(group.Id);
            normalized.PolicyRoots = PolicyRoots == null ? null : PolicyRoots.ToList();
            return normalized;
        }

        public List<string> ResolveDestinations(string groupId)
        {
            return ResolveDestinations(groupId, true);
        }

        public List<string> ResolveDestinations(string groupId, bool includeRetired)
        {
            var groups = Groups.ToDictionary(g => g.Id, StringComparer.Ordinal);
            var leaves = new HashSet<string>(StringComparer.Ordinal);
            var active = new HashSet<string>();
            var visited = new HashSet<string>();
            Action<string> visit = null;
            visit = id =>
            {
                BackupGroupDefinition group;
                if (!groups.TryGetValue(id, out group))
                    throw new ArgumentException("Unknown warehouse: " + id);
                if (!includeRetired && group.Retired)
                    return;
                if (!active.Add(id))
                    throw new ArgumentException("仓库不能循环引用。");
                if (visited.Add(id))
                {
                    foreach (string member in group.DestinationIds)
                        if (includeRetired || Destinations.Any(d => d.Id == member && !d.Retired))
                            leaves.Add(member);
                    foreach (string child in group.ChildGroupIds)
                        visit(child);
                }

                active.Remove(id);
            };
            visit(groupId);
            return leaves.OrderBy(x => x, StringComparer.Ordinal).ToList();
        }

        public StorageTopology Scope(IEnumerable<string> groupIds)
        {
            var full = Normalize();
            var result = new StorageTopology();
            var members = new HashSet<string>();
            var selected = groupIds.Distinct().ToList();
            if (selected.Any(id => !full.Groups.Any(g => g.Id == id)))
                throw new ArgumentException("所选仓库不存在。");
            if (selected.Any(id => full.ContainsMirror(id)))
            {
                full.PolicyRoots = selected;
                return full;
            }

            foreach (string id in selected)
            {
                var group = full.Groups.Single(g => g.Id == id);
                if (group.Retired)
                    continue;
                var leaves = full.ResolveDestinations(id, false);
                if (leaves.Count == 0)
                    continue;
                members.UnionWith(leaves);
                result.Groups.Add(new BackupGroupDefinition { Id = id, Name = group.Name, Notes = group.Notes, DestinationIds = leaves });
            }

            result.Destinations.AddRange(full.Destinations.Where(d => members.Contains(d.Id)));
            return result;
        }

        internal bool ContainsMirror(string id)
        {
            var group = Groups.Single(g => g.Id == id);
            return group.Mode == "raid1" || group.ChildGroupIds.Any(ContainsMirror);
        }
    }

    public sealed class DestinationUsage
    {
        public string DestinationId;
        public long UsedBytes;
        public long ReservedBytes;
    }

    public sealed class ArchivePlacement
    {
        public string GroupId;
        public string DestinationId;
        public long ReservedBytes;
        public long StoredBytes;
        public string Status;
        public string Error;
    }

    public sealed class ArchivePlan
    {
        public string TaskId;
        public long ArchiveId;
        public string ArchiveName;
        public long BudgetBytes;
        public int RequestedCopies;
        public int TargetCopies;
        public int MissingCopies;
        public bool StructurallyComplete = true;
        public List<ArchivePlacement> Placements = new List<ArchivePlacement>();
        public List<ReplicaPool> Pools;
    }

    public sealed class ArchiveSummary
    {
        public long ArchiveId;
        public string ArchiveName;
        public string Status;
        public int TargetCopies;
        public int CompleteCopies;
        public int CurrentValidCopies;
        public int UnresolvedTransfers;
        public long ActualBytes;
        public string TaskId;
    }

    public static class GroupAllocator
    {
        public static int CoveredCopies(StorageTopology topology, ISet<string> destinations)
        {
            var normalized = topology.Normalize();
            if (normalized.PolicyRoots != null)
                return RaidPolicy.Plan(normalized, new DestinationUsage[0], 1, d => destinations.Contains(d.Id), true, null).Placements.Count;
            return WarehouseMatching.Match(normalized, new DestinationUsage[0], 1, normalized.Groups.Count, d => destinations.Contains(d.Id), true).Count;
        }

        public static bool CoversPolicy(StorageTopology topology, ISet<string> destinations, int requestedCopies)
        {
            var normalized = topology.Normalize();
            if (normalized.PolicyRoots == null)
                return CoveredCopies(normalized, destinations) >= requestedCopies;
            var plan = RaidPolicy.Plan(normalized, new DestinationUsage[0], 1, d => destinations.Contains(d.Id), true, null);
            return plan.StructurallyComplete && plan.MissingCopies == 0;
        }

        public static ArchivePlan Plan(
            StorageTopology configuration,
            IList<DestinationUsage> usage,
            long budgetBytes,
            int requestedCopies,
            Func<DestinationDefinition, bool> canWrite,
            ISet<string> existing = null
        )
        {
            if (budgetBytes <= 0 || requestedCopies <= 0)
                throw new ArgumentException("Budget and copies must be positive.");
            StorageTopology topology = configuration.Normalize();
            if (topology.PolicyRoots != null)
                return RaidPolicy.Plan(topology, usage, budgetBytes, canWrite, false, existing);
            int target = WarehouseMatching.Match(topology, usage, budgetBytes, requestedCopies, null, true).Count;
            var plan = new ArchivePlan
            {
                BudgetBytes = budgetBytes,
                RequestedCopies = requestedCopies,
                TargetCopies = target
            };
            plan.Placements = WarehouseMatching.Match(topology, usage, budgetBytes, target, canWrite, false, existing);
            plan.MissingCopies = target - plan.Placements.Count;
            return plan;
        }

        public static long EstimateArchiveBudget(IList<SourceItem> sources)
        {
            checked
            {
                long bytes = sources.Sum(x => x.Size);
                return bytes + bytes / 100 + 1024 * 1024 + sources.Sum(x => 512L + x.ArchivePath.Length * 4L);
            }
        }
    }
}
