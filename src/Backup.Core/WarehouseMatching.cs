using System;
using System.Collections.Generic;
using System.Linq;

namespace Backup.Core
{
    internal static class WarehouseMatching
    {
        private sealed class Edge
        {
            internal int To, Reverse, Capacity;
            internal decimal Cost;
            internal string Group, Destination;
        }

        internal static List<ArchivePlacement> Match(
            StorageTopology topology,
            IList<DestinationUsage> usage,
            long budget,
            int limit,
            Func<DestinationDefinition, bool> available,
            bool structural,
            ISet<string> existing = null
        )
        {
            var stats = usage.ToDictionary(x => x.DestinationId);
            var definitions = topology.Destinations.ToDictionary(x => x.Id);
            var groups = topology.Groups.Where(g => !g.Retired).OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
            var destinations = topology.Destinations.OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
            int count = groups.Count + destinations.Count + 2, sink = count - 1;
            var graph = Enumerable.Range(0, count).Select(x => new List<Edge>()).ToArray();
            Action<int, int, decimal, string, string> add = (from, to, cost, g, d) =>
            {
                var forward = new Edge
                {
                    To = to,
                    Reverse = graph[to].Count,
                    Capacity = 1,
                    Cost = cost,
                    Group = g,
                    Destination = d
                };
                var reverse = new Edge
                {
                    To = from,
                    Reverse = graph[from].Count,
                    Capacity = 0,
                    Cost = -cost
                };
                graph[from].Add(forward);
                graph[to].Add(reverse);
            };
            for (int g = 0; g < groups.Count; g++)
            {
                add(0, g + 1, 0, null, null);
                var leaves = topology.ResolveDestinations(groups[g].Id);
                decimal groupUsed = 0, groupCapacity = 0;
                foreach (string id in leaves)
                {
                    DestinationUsage u;
                    stats.TryGetValue(id, out u);
                    groupUsed += u == null ? 0 : (decimal)u.UsedBytes + u.ReservedBytes;
                    groupCapacity += definitions[id].MaxBytes;
                }

                decimal groupRatio = (groupUsed + budget) / groupCapacity;
                foreach (string id in leaves)
                {
                    var d = definitions[id];
                    DestinationUsage u;
                    stats.TryGetValue(id, out u);
                    decimal next = (u == null ? 0 : (decimal)u.UsedBytes + u.ReservedBytes) + budget;
                    if (u != null && (u.UsedBytes < 0 || u.ReservedBytes < 0))
                        throw new ArgumentException("Negative usage.");
                    bool reuse = existing != null && existing.Contains(id);
                    if (d.Retired || available != null && !available(d) || !structural && (!d.Enabled && !reuse || !reuse && next > d.MaxBytes))
                        continue;
                    int index = destinations.FindIndex(x => x.Id == id);
                    add(g + 1, groups.Count + index + 1, structural ? 0 : (reuse ? -100m : 0m) + groupRatio + next / d.MaxBytes / 1000000m, groups[g].Id, id);
                }
            }

            for (int d = 0; d < destinations.Count; d++)
                add(groups.Count + d + 1, sink, 0, null, null);
            for (int flow = 0; flow < limit; flow++)
            {
                var distance = Enumerable.Repeat(decimal.MaxValue, count).ToArray();
                var previous = new int[count];
                var edgeIndex = new int[count];
                distance[0] = 0;
                for (int pass = 0; pass < count - 1; pass++)
                {
                    bool changed = false;
                    for (int from = 0; from < count; from++)
                        if (distance[from] != decimal.MaxValue)
                            for (int e = 0; e < graph[from].Count; e++)
                            {
                                var edge = graph[from][e];
                                if (edge.Capacity > 0 && distance[edge.To] > distance[from] + edge.Cost)
                                {
                                    distance[edge.To] = distance[from] + edge.Cost;
                                    previous[edge.To] = from;
                                    edgeIndex[edge.To] = e;
                                    changed = true;
                                }
                            }

                    if (!changed)
                        break;
                }

                if (distance[sink] == decimal.MaxValue)
                    break;
                for (int to = sink; to != 0; to = previous[to])
                {
                    var edge = graph[previous[to]][edgeIndex[to]];
                    edge.Capacity--;
                    graph[to][edge.Reverse].Capacity++;
                }
            }

            var placements = new List<ArchivePlacement>();
            for (int g = 0; g < groups.Count; g++)
                foreach (var edge in graph[g + 1])
                    if (edge.Group != null && edge.Capacity == 0)
                        placements.Add(new ArchivePlacement { GroupId = edge.Group, DestinationId = edge.Destination, ReservedBytes = budget, Status = "writing" });
            return placements;
        }
    }
}
