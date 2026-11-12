using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backup.Core;

namespace Backup.Desktop
{
    // Import compares configuration only. No source enumeration, content reads,
    // network connections or persistent import identities are needed.
    internal static class DatabaseImportComparer
    {
        private static string Pack(string value){return value.Length+":"+value;}
        private static string FileAddress(string path)
        {
            string full=Path.GetFullPath(path);
            if(full.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase))full=@"\\"+full.Substring(8);
            else if(full.StartsWith(@"\\?\",StringComparison.OrdinalIgnoreCase)&&full.Length>5&&full[5]==':')full=full.Substring(4);
            return Path.GetFullPath(full).TrimEnd('\\','/').ToUpperInvariant();
        }
        internal static bool SameSource(SourceDefinition left,SourceDefinition right)
        {return FileAddress(left.Location)==FileAddress(right.Location);}
        internal static string DestinationKey(DestinationDefinition value)
        {
            string protocol=(value.Protocol??"").ToLowerInvariant();
            if(RepositoryModules.IsFileSystem(protocol))
            {
                string path=FileAddress(value.Location);return (protocol=="local"||protocol=="smb"?(path.StartsWith(@"\\",StringComparison.Ordinal)?"smb":"local"):protocol)+Pack(path);
            }
            var uri=new Uri(value.Location,UriKind.Absolute);
            int port=protocol=="sftp"&&uri.Port<1?22:uri.Port;
            return protocol+Pack(uri.Scheme.ToLowerInvariant())+Pack(uri.IdnHost.ToLowerInvariant())+":"+port+Pack(Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/'))+Pack(uri.Query);
        }
        private static string Parent(string path)
        {int slash=path.LastIndexOf('\\');return slash<=0?null:path.Substring(0,slash);}
        private static bool UnderRoot(string path,ISet<string> roots)
        {for(string current=path;current!=null;current=Parent(current))if(roots.Contains(current))return true;return false;}
        private static bool DirectorySelected(string path,IDictionary<string,SelectionRule> rules)
        {
            for(string current=path;current!=null;current=Parent(current))
            {SelectionRule rule;if(rules.TryGetValue(current,out rule)&&rule.Directory)return rule.Include;}return false;
        }
        private static string SelectionKey(BackupTaskDefinition task,WorkspaceDefinition workspace)
        {
            var roots=new HashSet<string>(StringComparer.Ordinal);var locations=new List<string>();
            foreach(string id in task.SourceIds.Distinct())
            {
                var source=workspace.Sources.SingleOrDefault(s=>s.Id==id&&!s.Deleted);
                if(source==null)throw new IOException("任务的来源文件夹记录缺失："+task.Name);locations.Add(FileAddress(source.Location));
            }
            foreach(string path in locations.Distinct().OrderBy(p=>p.Length).ThenBy(p=>p,StringComparer.Ordinal))if(!UnderRoot(path,roots))roots.Add(path);
            var rules=new Dictionary<string,SelectionRule>(StringComparer.Ordinal);
            foreach(var rule in (task.Selection??new FileSelection()).Rules??new List<SelectionRule>())
            {
                string path=FileAddress(rule.Path);if(!rules.ContainsKey(path))rules[path]=new SelectionRule{Path=path,Directory=rule.Directory,Include=rule.Include};
            }
            var entries=new List<string>();
            foreach(string path in roots)if(DirectorySelected(path,rules))entries.Add("D"+Pack(path)+"1");
            foreach(var rule in rules.Values.Where(r=>UnderRoot(r.Path,roots)))
            {
                if(rule.Directory&&roots.Contains(rule.Path))continue;
                bool inherited=DirectorySelected(rule.Directory?Parent(rule.Path):rule.Path,rules);
                if(rule.Include!=inherited)entries.Add((rule.Directory?"D":"F")+Pack(rule.Path)+(rule.Include?"1":"0"));
            }
            return string.Join("",entries.OrderBy(entry=>entry,StringComparer.Ordinal).Select(Pack));
        }
        internal static string TaskKey(BackupTaskDefinition task,WorkspaceDefinition workspace,StorageTopology topology,IEnumerable<string> targets=null)
        {
            var definitions=topology.Destinations.ToDictionary(d=>d.Id,StringComparer.Ordinal);var groups=topology.Groups.ToDictionary(g=>g.Id,StringComparer.Ordinal);var keys=new Dictionary<string,string>(StringComparer.Ordinal);
            Func<string,string> groupKey=null;groupKey=id=>{
                string key;if(keys.TryGetValue(id,out key))return key;BackupGroupDefinition group;if(!groups.TryGetValue(id,out group))throw new IOException("任务引用的仓库组合不存在："+task.Name);
                var members=group.DestinationIds.Select(d=>"D"+DestinationKey(definitions[d])).Concat(group.ChildGroupIds.Select(child=>groupKey(child))).Distinct(StringComparer.Ordinal).OrderBy(s=>s,StringComparer.Ordinal).ToList();
                key=members.Count==1?members[0]:"G"+Pack(group.Mode??"raid0")+string.Join("",members.Select(Pack));keys[id]=key;return key;
            };
            return Pack(SelectionKey(task,workspace))+string.Join("",(targets??task.TargetIds).Select(groupKey).Distinct(StringComparer.Ordinal).OrderBy(key=>key,StringComparer.Ordinal).Select(Pack));
        }
        internal static bool SameSchedule(BackupSchedule left,BackupSchedule right)
        {
            return left.Frequency==right.Frequency&&left.Time==right.Time&&left.Backup==right.Backup&&left.Integrity==right.Integrity&&
                (left.Frequency!="monthly"||left.MonthDay==right.MonthDay)&&(left.Frequency!="weekly"||new HashSet<int>(left.Days).SetEquals(right.Days))&&new HashSet<string>(left.TaskIds,StringComparer.Ordinal).SetEquals(right.TaskIds);
        }
    }
}
