using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace Backup.Core
{
    public sealed partial class BackupCatalog
    {
        private static readonly string[] ImportSettings={"repository_id","workspace_v3","exclusions","backup_password_v1","backup_memory_mib_v1","bandwidth_schedule_v1","backup_schedules_v1","confirmed_repairs_v1","catalog_backup_targets_v1","catalog_backup_password_v1","catalog_sequence_floor","recovery_snapshot_v1","last_upload_warnings_v1","last_upload_alert_v1"};
        private static readonly string[] ImportSettingPrefixes={"connection:","connection_reference:","archive_root_labels:","archive_task_tag:","archive_number:","import_node_map:","import_archive_map:"};
        private static readonly string[] MergeImportSettings={"repository_id","workspace_v3","backup_password_v1","backup_schedules_v1"};
        private static readonly string[] MergeImportPrefixes={"connection:","connection_reference:","archive_root_labels:","archive_task_tag:","import_node_map:","import_archive_map:"};
        private static readonly HashSet<string> MergeImportTables=new HashSet<string>(new[]{"settings","destinations","groups","group_members","group_modes","group_children","resource_retirements","archives","placements","objects","object_copies","archive_pools","archive_pool_members","backup_nodes","node_revisions","node_archive_seals","local_backup_nodes","storage_trees","object_locations"},StringComparer.Ordinal);
        private static string SqlName(string value){return "\""+value.Replace("\"","\"\"")+"\"";}
        public IList<KeyValuePair<string,string>> ImportConfiguration()
        {lock(gate)return database.Query("SELECT key,value FROM settings ORDER BY key").Select(row=>new KeyValuePair<string,string>((string)row[0],(string)row[1])).ToList();}
        // Copy into the application's own schema, never execute the incoming schema.
        // Unsupported tables/columns/indexes/triggers/views and settings are not carried across.
        public static BackupCatalog FromImportFileMemory(string path,MemoryBudget budget)
        {return FromImportFileMemory(path,budget,false);}
        public static BackupCatalog FromImportFileMemory(string path,MemoryBudget budget,bool mergeOnly)
        {return ReadImportFile(path,budget,mergeOnly,false,null);}
        public static BackupCatalog PreviewImportFile(string path,MemoryBudget budget)
        {return ReadImportFile(path,budget,true,true,null);}
        public static BackupCatalog ReadMergeImportFile(string path,MemoryBudget budget,ISet<string> tasks)
        {return ReadImportFile(path,budget,true,false,tasks);}
        private static BackupCatalog ReadImportFile(string path,MemoryBudget budget,bool mergeOnly,bool configurationOnly,ISet<string> tasks)
        {
            using(var source=SqliteDatabase.OpenImportReadOnly(path,budget))
            {
                long version=(long)source.Query("PRAGMA user_version")[0][0];
                if(version!=8&&version!=9&&version!=10)throw new IOException("仅支持独立备份版本数据库格式（版本 8、9 或 10）的共享数据库，请勿导入旧版或单个备份版本数据库。");
                var sourceTables=new HashSet<string>(source.Query("SELECT name FROM sqlite_master WHERE type='table'").Select(row=>(string)row[0]),StringComparer.OrdinalIgnoreCase);
                foreach(string required in new[]{"destinations","groups","settings","group_members","archives","placements","objects","object_copies","backup_nodes","node_revisions"})if(!sourceTables.Contains(required))throw new IOException("数据库缺少必要的数据表："+required);
                if(!configurationOnly){var integrity=source.Query("PRAGMA quick_check");if(integrity.Count!=1||(string)integrity[0][0]!="ok")throw new IOException("导入数据库完整性检查未通过。");}
                if(tasks!=null){
                    source.Execute("CREATE TEMP TABLE selected_import_tasks(id TEXT PRIMARY KEY)");source.ExecuteMany("INSERT INTO selected_import_tasks VALUES(?)",tasks.Select(id=>new object[]{id}));
                    source.Execute("CREATE TEMP TABLE selected_import_archives(id INTEGER PRIMARY KEY)");
                    source.Execute("INSERT INTO selected_import_archives SELECT a.id FROM archives a WHERE a.task_id IN (SELECT id FROM selected_import_tasks) OR (substr(a.task_id,1,11)='__repack__:' AND substr(a.task_id,12) IN (SELECT id FROM selected_import_tasks)) OR a.id IN (SELECT archive_id FROM backup_nodes WHERE task_id IN (SELECT id FROM selected_import_tasks)) OR a.id IN (SELECT archive_id FROM node_revisions WHERE task_id IN (SELECT id FROM selected_import_tasks))");
                }
                long bytes=checked((long)source.Query("PRAGMA page_count")[0][0]*((long)source.Query("PRAGMA page_size")[0][0]+128));
                var reservation=new ImportMemoryLease(budget,configurationOnly||tasks!=null?Math.Min(bytes,4L*1024*1024):bytes);SqliteDatabase target=null;BackupCatalog result=null;
                try
                {
                    List<object[]> schema;var columns=new Dictionary<string,List<string>>(StringComparer.Ordinal);var integerColumns=new Dictionary<string,HashSet<string>>(StringComparer.Ordinal);
                    using(var model=new BackupCatalog(new SqliteDatabase(":memory:"),null))
                    {
                        schema=model.database.Query("SELECT type,name,sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY type,name");
                        foreach(var table in schema.Where(row=>(string)row[0]=="table"))
                        {
                            var info=model.database.Query("PRAGMA table_info("+SqlName((string)table[1])+")");columns[(string)table[1]]=info.Select(row=>(string)row[1]).ToList();integerColumns[(string)table[1]]=new HashSet<string>(info.Where(row=>((string)row[2]).IndexOf("INT",StringComparison.OrdinalIgnoreCase)>=0).Select(row=>(string)row[1]));
                        }
                    }
                    target=new SqliteDatabase(":memory:");target.Execute("PRAGMA foreign_keys=OFF");
                    foreach(var table in schema.Where(row=>(string)row[0]=="table"))target.Execute((string)table[2]);
                    target.Transaction(delegate
                    {
                        foreach(var table in columns)
                        {
                            if(!sourceTables.Contains(table.Key)||mergeOnly&&!MergeImportTables.Contains(table.Key)||configurationOnly&&!new[]{"settings","destinations","groups","group_members","group_modes","group_children","resource_retirements"}.Contains(table.Key))continue;
                            var available=new HashSet<string>(source.Query("PRAGMA table_info("+SqlName(table.Key)+")").Select(row=>(string)row[1]),StringComparer.OrdinalIgnoreCase);
                            var selected=table.Value.Where(available.Contains).ToList();
                            foreach(string missing in table.Value.Where(name=>!available.Contains(name)))
                            {
                                if(!new[]{"archive_files.entry_path","object_copies.pool_idx","archive_leases.process_start","archive_leases.read_only","warehouse_moves.catalog_published","retired_objects.group_id","backup_nodes.entry_path","node_revisions.entry_path"}.Contains(table.Key+"."+missing))throw new IOException("数据库缺少必要字段："+table.Key+"."+missing);
                            }
                            string list=string.Join(",",selected.Select(SqlName));var args=new List<object>();string filter="";
                            if(table.Key=="settings")
                            {
                                var settings=mergeOnly?MergeImportSettings:ImportSettings;var prefixes=mergeOnly?MergeImportPrefixes:ImportSettingPrefixes;
                                args.AddRange(settings.Cast<object>());var tests=new List<string>{"key IN ("+string.Join(",",settings.Select(key=>"?"))+")"};
                                foreach(string prefix in prefixes){tests.Add("substr(key,1,"+prefix.Length+")=? AND length(key)>"+prefix.Length);args.Add(prefix);}
                                filter=" WHERE ("+string.Join(" OR ",tests)+") AND NOT (key IN ('last_upload_alert_v1','last_upload_warnings_v1') AND value='') AND NOT (key IN ('backup_schedules_v1','confirmed_repairs_v1') AND value='[]')";
                            }
                            if(tasks!=null&&table.Key!="settings"){
                                if(new[]{"backup_nodes","node_revisions","local_backup_nodes"}.Contains(table.Key))filter=" WHERE task_id IN (SELECT id FROM selected_import_tasks)";
                                else if(table.Key=="archives")filter=" WHERE id IN (SELECT id FROM selected_import_archives)";
                                else if(new[]{"objects","placements","archive_pools","archive_pool_members","node_archive_seals"}.Contains(table.Key))filter=" WHERE archive_id IN (SELECT id FROM selected_import_archives)";
                                else if(new[]{"object_copies","object_locations"}.Contains(table.Key))filter=" WHERE name IN (SELECT name FROM objects WHERE archive_id IN (SELECT id FROM selected_import_archives))";
                                else if(table.Key=="storage_trees")filter=" WHERE tag IN (SELECT value FROM settings WHERE key IN (SELECT 'archive_task_tag:'||id FROM selected_import_tasks))";
                            }
                            var integers=new HashSet<int>(selected.Select((name,index)=>new{name,index}).Where(item=>integerColumns[table.Key].Contains(item.name)).Select(item=>item.index));
                            var binary=new HashSet<int>(selected.Select((name,index)=>new{name,index}).Where(item=>table.Key=="local_backup_nodes"&&item.name=="payload").Select(item=>item.index));
                            target.ExecuteMany("INSERT INTO "+SqlName(table.Key)+"("+list+") VALUES("+string.Join(",",selected.Select(name=>"?"))+")",ReadImportRows(source,"SELECT "+list+" FROM "+SqlName(table.Key)+filter,args.ToArray(),integers,binary));
                            reservation.Grow(target);
                        }
                        if(sourceTables.Contains("sqlite_sequence"))
                        {
                            var sequence=source.Query("SELECT max(seq) FROM sqlite_sequence WHERE name='archives' COLLATE NOCASE");
                            if(sequence.Count>0&&sequence[0][0]!=null)
                            {
                                long floor=Convert.ToInt64(sequence[0][0]);
                                target.Execute("UPDATE sqlite_sequence SET seq=MAX(seq,?) WHERE name='archives'",floor);
                                if(target.Query("SELECT seq FROM sqlite_sequence WHERE name='archives'").Count==0)target.Execute("INSERT INTO sqlite_sequence(name,seq) VALUES('archives',?)",floor);
                            }
                        }
                        return 0;
                    });
                    foreach(var item in schema.Where(row=>(string)row[0]!="table"))target.Execute((string)item[2]);
                    reservation.Grow(target);
                    target.Execute("PRAGMA user_version="+version);target.Execute("PRAGMA foreign_keys=ON");
                    result=new BackupCatalog(target,null);target=null;result.ValidateIntegrity();result.importedCatalogMemory=reservation;reservation=null;return result;
                }
                catch{if(result!=null)result.Dispose();else if(target!=null)target.Dispose();throw;}
                finally{if(reservation!=null)reservation.Dispose();}
            }
        }
        private IDisposable importedCatalogMemory;
        private sealed class ImportMemoryLease:IDisposable
        {
            private readonly MemoryBudget budget;private readonly List<IDisposable> leases=new List<IDisposable>();private long bytes;
            internal ImportMemoryLease(MemoryBudget value,long initial){budget=value;leases.Add(budget.Reserve(initial));bytes=initial;}
            internal void Grow(SqliteDatabase database){long needed=checked((long)database.Query("PRAGMA page_count")[0][0]*((long)database.Query("PRAGMA page_size")[0][0]+128));if(needed>bytes){leases.Add(budget.Reserve(needed-bytes));bytes=needed;}}
            public void Dispose(){foreach(var lease in leases)lease.Dispose();leases.Clear();}
        }
        private static IEnumerable<object[]> ReadImportRows(SqliteDatabase source,string sql,object[] args,ISet<int> integers,ISet<int> binary)
        {
            foreach(var row in source.StreamQuery(sql,args))
            {
                for(int i=0;i<row.Length;i++)if(row[i] is byte[]&&!binary.Contains(i))throw new IOException("数据库已知字段中包含无法识别的二进制类型。");
                foreach(int index in integers)if(row[index]!=null&&!(row[index] is long))throw new IOException("数据库已知整数字段的类型无效，已保留当前数据库。");
                yield return row;
            }
        }
        public void NormalizeImportedWarehouseJson(Func<string,string> normalize)
        {
            Transaction(delegate{
                foreach(var row in database.Query("SELECT destination_id,original_json,target_json FROM warehouse_moves"))
                {
                    string original=normalize((string)row[1]),target=normalize((string)row[2]);
                    if(original!=(string)row[1]||target!=(string)row[2])database.Execute("UPDATE warehouse_moves SET original_json=?,target_json=? WHERE destination_id=?",original,target,row[0]);
                }
                return 0;
            });
        }
    }
}
