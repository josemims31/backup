using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace Backup.Core
{
    // Compile the selected storage expressions into alternative sets of replica pools.
    // A stripe chooses one pool; a mirror requires every member, recursively.
    internal static class RaidPolicy
    {
        private sealed class Lane {internal string Owner;internal List<string> Members;}
        private static IEnumerable<List<Lane>> Product(IEnumerable<List<Lane>> left,IEnumerable<List<Lane>> right)
        {var values=right.Take(8193).ToList();if(values.Count>8192)throw new IOException("组合嵌套过于复杂，请简化组合结构。");int count=0;foreach(var a in left)foreach(var b in values){if(++count>8192)throw new IOException("组合嵌套过于复杂，请简化组合结构。");yield return a.Concat(b).ToList();}}
        private static IEnumerable<List<Lane>> Leaf(string owner,IEnumerable<string> members)
        {yield return new List<Lane>{new Lane{Owner=owner,Members=members.Distinct().ToList()}};}
        private static IEnumerable<List<Lane>> Build(StorageTopology topology,string id,string owner)
        {
            var group=topology.Groups.Single(g=>g.Id==id);if(group.Retired)yield break;
            if(!topology.ContainsMirror(id)){foreach(var value in Leaf(owner,topology.ResolveDestinations(id,false)))yield return value;yield break;}
            var members=group.DestinationIds.Select(d=>Leaf(owner,new[]{d})).Concat(group.ChildGroupIds.Select(child=>Build(topology,child,owner))).ToList();
            if(group.Mode=="raid1"){
                IEnumerable<List<Lane>> layouts=new[]{new List<Lane>()};foreach(var member in members)layouts=Product(layouts,member);foreach(var layout in layouts)yield return layout;
            }else{
                var singles=new List<string>();foreach(var member in members)foreach(var layout in member){if(layout.Count==1)singles.AddRange(layout[0].Members);else yield return layout;}if(singles.Count>0)foreach(var value in Leaf(owner,singles))yield return value;
            }
        }
        internal static ArchivePlan Plan(StorageTopology topology,IList<DestinationUsage> usage,long budget,Func<DestinationDefinition,bool> available,bool coverage,ISet<string> existing)
        {
            IEnumerable<List<Lane>> layouts=new[]{new List<Lane>()};foreach(string root in topology.PolicyRoots)layouts=Product(layouts,Build(topology,root,root));
            ArchivePlan best=null;decimal bestFraction=-1,bestCost=decimal.MaxValue;int count=0;var stats=usage.ToDictionary(u=>u.DestinationId);var definitions=topology.Destinations.ToDictionary(d=>d.Id);
            foreach(var layout in layouts){if(++count>8192)throw new IOException("组合嵌套产生过多分配方案，请简化组合结构。");if(layout.Count==0)continue;
                var flat=new StorageTopology{Destinations=topology.Destinations,Groups=layout.Select((lane,i)=>new BackupGroupDefinition{Id="lane:"+i,DestinationIds=lane.Members}).ToList()};
                if(flat.Groups.Any(g=>g.DestinationIds.Count==0)||WarehouseMatching.Match(flat,usage,budget,layout.Count,null,true).Count!=layout.Count)continue;
                var matches=WarehouseMatching.Match(flat,usage,budget,layout.Count,available,coverage,existing);decimal fraction=(decimal)matches.Count/layout.Count,cost=0;
                foreach(var owner in matches.GroupBy(p=>layout[int.Parse(p.GroupId.Substring(5))].Owner)){decimal value=0;foreach(var match in owner){DestinationUsage u;stats.TryGetValue(match.DestinationId,out u);value+=((u==null?0:(decimal)u.UsedBytes+u.ReservedBytes)+budget)/definitions[match.DestinationId].MaxBytes+(existing!=null&&existing.Contains(match.DestinationId)?-100:0);}cost+=value/owner.Count();}
                if(fraction<bestFraction||fraction==bestFraction&&cost>=bestCost)continue;
                foreach(var match in matches)match.GroupId=layout[int.Parse(match.GroupId.Substring(5))].Owner;
                best=new ArchivePlan{BudgetBytes=budget,RequestedCopies=topology.PolicyRoots.Count,TargetCopies=layout.Count,MissingCopies=layout.Count-matches.Count,Placements=matches,Pools=layout.Select((lane,index)=>new ReplicaPool{Index=index,GroupId=lane.Owner,DestinationIds=lane.Members.ToList()}).ToList()};bestFraction=fraction;bestCost=cost;
            }
            return best??new ArchivePlan{BudgetBytes=budget,RequestedCopies=topology.PolicyRoots.Count,StructurallyComplete=false};
        }
    }
    public sealed partial class BackupCatalog
    {
        private void InitializeRaidSchema()
        {
            Transaction(delegate{
                database.Execute("CREATE TABLE IF NOT EXISTS group_modes(group_id TEXT PRIMARY KEY REFERENCES groups(id),mode TEXT NOT NULL)");
return 0;
            });
        }
    }
}
