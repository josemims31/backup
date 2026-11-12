using System;
using System.Collections.Generic;
using System.Linq;
using Backup.Core;
namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        private static string DatabasePolicyKey(StorageTopology topology,IList<string> targets)
        {
            var rows=new List<string>();var seen=new HashSet<string>();var pending=new Stack<string>(targets.OrderBy(id=>id,StringComparer.Ordinal));
            while(pending.Count>0){string id=pending.Pop();if(!seen.Add(id))continue;var group=topology.Groups.FirstOrDefault(g=>g.Id==id);if(group==null){rows.Add(id+":missing");continue;}rows.Add(id+":"+group.Mode+":"+group.Retired);foreach(string member in group.DestinationIds.OrderBy(v=>v,StringComparer.Ordinal)){var d=topology.Destinations.FirstOrDefault(v=>v.Id==member);rows.Add(id+":"+member+":"+(d==null?"missing":d.Enabled+":"+d.Retired));}foreach(string child in group.ChildGroupIds.OrderBy(v=>v,StringComparer.Ordinal)){rows.Add(id+">"+child);pending.Push(child);}}
            rows.Sort(StringComparer.Ordinal);return string.Join("\n",rows);
        }
        private void ValidateDatabasePolicyChange(StorageTopology previous,StorageTopology next)
        {var targets=DatabaseBackupTargets();if(targets.Count>0&&DatabasePolicyKey(previous,targets)!=DatabasePolicyKey(next,targets))ValidateDatabaseBackupTargets(targets,next);}
        private void ValidateDatabaseRetirement(StorageTopology previous,StorageTopology next)
        {var targets=DatabaseBackupTargets();if(targets.Count>0&&DatabasePolicyKey(previous,targets)!=DatabasePolicyKey(next,targets))throw new ArgumentException("此仓库或组合正用于数据库备份，请先在设置中调整数据库备份仓库，再移除。");}
        public void SaveWorkspaceDefinition(WorkspaceDefinition workspace)
        {WorkspaceMigration.Validate(workspace,Catalog.LoadTopology());foreach(var task in workspace.Tasks)Catalog.EnsureTaskTag(task.Id);Catalog.SetSetting("workspace_v3",JsonSettings.Encode(workspace));}
        public WorkspaceDefinition SaveApplicationSettings(WorkspaceDefinition workspace,string password,int memory,IEnumerable<string> selected)
        {return SaveApplicationSettings(workspace,password,ResolveDatabaseBackupPassword(),memory,selected);}
        public WorkspaceDefinition SaveApplicationSettings(WorkspaceDefinition workspace,string password,string databasePassword,int memory,IEnumerable<string> selected)
        {return SaveApplicationSettings(workspace,password,databasePassword,memory,selected,BandwidthSettings());}
        public WorkspaceDefinition SaveApplicationSettings(WorkspaceDefinition workspace,string password,string databasePassword,int memory,IEnumerable<string> selected,BandwidthSchedule bandwidth)
        {
            bandwidth=(bandwidth??new BandwidthSchedule()).ValidatedCopy();
            var targets=selected.Distinct(StringComparer.Ordinal).ToList();ValidateDatabaseBackupTargets(targets);password=password??"";databasePassword=databasePassword??"";
            if(password.Length>99)throw new ArgumentException("备份密码长度为1~99位。");
            if(databasePassword.Length>99||targets.Count>0&&databasePassword.Length==0)throw new ArgumentException("数据库备份需要数据库密码，密码长度为1~99位。");
            if(memory<256||memory>65536)throw new ArgumentException("内存限制应为 256–65536 MiB。");
            var draft=CopyWorkspace(workspace);foreach(var task in draft.Tasks.Where(t=>!t.Deleted&&!t.UseCustomPassword))task.Password=password;WorkspaceMigration.Validate(draft,Catalog.LoadTopology());foreach(var task in draft.Tasks)Catalog.EnsureTaskTag(task.Id);
            Catalog.SaveConfiguration(new[]{new KeyValuePair<string,string>(DatabaseBackupTargetsKey,targets.Count==0?null:JsonSettings.Encode(targets)),new KeyValuePair<string,string>("backup_password_v1",password),new KeyValuePair<string,string>(DatabaseBackupPasswordKey,databasePassword),new KeyValuePair<string,string>("backup_memory_mib_v1",memory.ToString(System.Globalization.CultureInfo.InvariantCulture)),new KeyValuePair<string,string>("workspace_v3",JsonSettings.Encode(draft)),new KeyValuePair<string,string>(BandwidthSettingsKey,bandwidth.Periods.Count==0?null:JsonSettings.Encode(bandwidth))});ReloadBandwidth();return draft;
        }
        public WorkspaceDefinition SaveDestinationDefinition(WorkspaceDefinition workspace,DestinationDefinition destination,string name,RemoteConnectionOptions options=null)
        {
            var previous=Catalog.LoadTopology();var next=Catalog.LoadTopology();next.Destinations.RemoveAll(d=>d.Id==destination.Id);next.Destinations.Add(destination);
            var leaf=next.Groups.FirstOrDefault(g=>g.Id=="single:"+destination.Id);if(leaf==null)next.Groups.Add(new BackupGroupDefinition{Id="single:"+destination.Id,Name=name,DestinationIds=new List<string>{destination.Id}});else leaf.Name=name;
            next=ValidateStorageTopology(next);ValidateDatabasePolicyChange(previous,next);var draft=CopyWorkspace(workspace);ResolveRetiredTargets(draft,next,Catalog.GetUsage());WorkspaceMigration.Validate(draft,next);
            var values=new List<KeyValuePair<string,string>>{new KeyValuePair<string,string>("workspace_v3",JsonSettings.Encode(draft))};string oldConnection=Catalog.GetSetting("connection:"+destination.Id);if(options!=null)values.AddRange(ConnectionConfiguration(destination.Id,options));
            Catalog.SaveTopologyAndConfiguration(next,values);InvalidateRepositoryConnections(topology:next);if(options!=null&&oldConnection!=Catalog.GetSetting("connection:"+destination.Id))InvalidateRepositoryConnections(destination.Id);return draft;
        }
        public WorkspaceDefinition SaveWarehouseDefinition(WorkspaceDefinition workspace,BackupGroupDefinition warehouse)
        {
            var previous=Catalog.LoadTopology();var next=Catalog.LoadTopology();next.Groups.RemoveAll(g=>g.Id==warehouse.Id);next.Groups.Add(warehouse);next=ValidateStorageTopology(next);
            if(!WorkspaceMigration.SatisfiesTargets(next,new[]{warehouse.Id}))throw new ArgumentException("组合成员重叠或不可用，无法满足备份要求。");
            var draft=CopyWorkspace(workspace);WorkspaceMigration.Validate(draft,next);foreach(var task in draft.Tasks.Where(t=>!t.Deleted&&t.TargetIds.Count>0))WorkspaceMigration.ValidateTargetPolicy(task,next);ValidateDatabasePolicyChange(previous,next);
            Catalog.SaveTopologyAndConfiguration(next,new[]{new KeyValuePair<string,string>("workspace_v3",JsonSettings.Encode(draft))});InvalidateRepositoryConnections(topology:next);return draft;
        }
        private IList<KeyValuePair<string,string>> ConnectionConfiguration(string id,RemoteConnectionOptions options)
        {
            string encrypted=Catalog.GetSetting("connection:"+id);bool same=false;
            try{same=encrypted!=null&&JsonSettings.Encode(UnprotectConnection(encrypted))==JsonSettings.Encode(options);}catch(System.Security.Cryptography.CryptographicException){}catch(FormatException){}
            var result=new List<KeyValuePair<string,string>>{new KeyValuePair<string,string>("connection:"+id,same?encrypted:ProtectConnection(options)),new KeyValuePair<string,string>("connection_reference:"+id,EncodeConnectionReference(options))};var shared=SharedOpenListAuthorizationSetting(options);if(shared.HasValue)result.Add(shared.Value);return result;
        }
    }
}
