using System;
using System.Collections.Generic;
using System.Linq;
namespace Backup.Core
{
    public sealed partial class BackupCatalog
    {
        // Explicit repair can release obsolete snapshot ownership. It must never delete
        // a remote object or discard a snapshot still associated with the current policy.
        public IList<long> UnregisterUnusedCatalogSnapshots(IList<string> activeRoots,bool inspectOnly=false,ISet<long> archiveIds=null)
        {
            return Transaction(delegate{
                var topology=LoadTopology();var roots=new HashSet<string>(activeRoots);var destinations=new HashSet<string>();
                foreach(string root in roots){if(!topology.Groups.Any(group=>group.Id==root&&!group.Retired))throw new ArgumentException("数据库备份仓库配置无效，请先在设置中检查。");destinations.UnionWith(topology.ResolveDestinations(root,false));}
                var owned=database.Query("SELECT o.archive_id,c.name,c.destination_id,o.size FROM object_copies c JOIN objects o ON o.name=c.name JOIN archives a ON a.id=o.archive_id WHERE a.task_id='__catalog__' AND c.state<>'deleted' UNION ALL SELECT r.archive_id,r.name,r.destination_id,r.size FROM retired_objects r JOIN archives a ON a.id=r.archive_id WHERE a.task_id='__catalog__' UNION ALL SELECT d.archive_id,d.name,d.destination_id,o.size FROM warehouse_detached d JOIN objects o ON o.name=d.name JOIN archives a ON a.id=d.archive_id WHERE a.task_id='__catalog__' AND d.state='pending' AND d.mode='keep'").GroupBy(row=>(long)row[0]).ToDictionary(group=>group.Key,group=>group.ToList());
                var coverage=database.Query("SELECT archive_id,group_id FROM catalog_coverage").GroupBy(row=>(long)row[0]).ToDictionary(group=>group.Key,group=>group.Select(row=>(string)row[1]).ToList());var changed=new List<long>();
                foreach(var archive in database.Query("SELECT a.id FROM archives a WHERE a.task_id='__catalog__' AND a.status IN ('complete','repair-required','redundancy-shortfall','snapshot-history') AND (EXISTS(SELECT 1 FROM archive_files f WHERE f.archive_id=a.id AND f.state='current') OR EXISTS(SELECT 1 FROM catalog_coverage c WHERE c.archive_id=a.id) OR EXISTS(SELECT 1 FROM objects o JOIN object_copies c ON c.name=o.name WHERE o.archive_id=a.id AND c.state<>'deleted') OR EXISTS(SELECT 1 FROM retired_objects r WHERE r.archive_id=a.id) OR EXISTS(SELECT 1 FROM warehouse_detached d WHERE d.archive_id=a.id AND d.mode='keep' AND d.state='pending'))"))
                {
                    long id=(long)archive[0];if(archiveIds!=null&&!archiveIds.Contains(id))continue;List<object[]> copies;List<string> covered;if(!owned.TryGetValue(id,out copies))copies=new List<object[]>();
                    if(copies.Any(row=>destinations.Contains((string)row[2]))||coverage.TryGetValue(id,out covered)&&covered.Any(roots.Contains))continue;
                    if(HasOutstandingTransfer(id)||GetReplicaStages(id).Count>0||IsCompactionArchive(id)||Scalar("SELECT count(*) FROM upload_temporaries t JOIN objects o ON o.name=t.name WHERE o.archive_id=?",id)>0||Scalar("SELECT count(*) FROM archive_leases WHERE archive_id=?",id)>0||Scalar("SELECT count(*) FROM archive_holds WHERE archive_id=?",id)>0||Scalar("SELECT count(*) FROM archive_cleanup WHERE archive_id=? AND state='pending'",id)>0||Scalar("SELECT count(*) FROM warehouse_detached WHERE archive_id=? AND mode='delete' AND state='pending'",id)>0||Scalar("SELECT count(*) FROM warehouse_move_objects o JOIN warehouse_moves m ON m.destination_id=o.destination_id JOIN objects f ON f.name=o.name WHERE f.archive_id=? AND m.state<>'complete'",id)>0)continue;
                    bool current=Scalar("SELECT count(*) FROM archive_files WHERE archive_id=? AND state='current'",id)>0;if(copies.Count==0&&!coverage.ContainsKey(id)&&!current)continue;
                    if(inspectOnly){changed.Add(id);continue;}
                    foreach(var destination in copies.GroupBy(row=>(string)row[2]))database.Execute("UPDATE destinations SET used_bytes=used_bytes-? WHERE id=?",destination.Sum(row=>(long)row[3]),destination.Key);
                    database.Execute("DELETE FROM object_copies WHERE name IN(SELECT name FROM objects WHERE archive_id=?)",id);
                    database.Execute("DELETE FROM retired_objects WHERE archive_id=?",id);database.Execute("DELETE FROM warehouse_detached WHERE archive_id=? AND mode='keep'",id);
                    database.Execute("UPDATE placements SET status='detached',stored=0,reserved=0 WHERE archive_id=? AND (status<>'detached' OR stored<>0 OR reserved<>0)",id);
                    database.Execute("UPDATE archive_files SET state='expired' WHERE archive_id=? AND state='current'",id);database.Execute("DELETE FROM catalog_coverage WHERE archive_id=?",id);
                    database.Execute("UPDATE archives SET status='snapshot-history' WHERE id=? AND status<>'snapshot-history'",id);
                    RepositoryStatusChanged(CatalogChange.Archives,"__catalog__");RepositoryDestinationsChanged(copies.Select(row=>(string)row[2]));changed.Add(id);
                }
                return (IList<long>)changed.AsReadOnly();
            });
        }
    }
}
