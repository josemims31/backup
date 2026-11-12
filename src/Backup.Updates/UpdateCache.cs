using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Backup.Updates
{
    public sealed class UpdateCleanupReceipt {public string Installation,Version;}
    public static class UpdateCache
    {
        private static List<string> OwnedTree(string session)
        {
            var entries=new List<string>();var pending=new Stack<string>();pending.Push(session);
            while(pending.Count>0)
            {
                string directory=pending.Pop();UpdateProtocol.NoLinks(directory);
                foreach(string entry in Directory.GetFileSystemEntries(directory))
                {
                    UpdateProtocol.NoLinks(entry);string relative=entry.Substring(session.Length+1).Replace('\\','/');int slash=relative.IndexOf('/');string top=slash<0?relative:relative.Substring(0,slash);
                    bool folder=Directory.Exists(entry);
                    if(slash<0)
                    {
                        if(folder?!new[]{"payload","rollback","helper","recovery"}.Contains(top):!new[]{"request.json","transaction.json","startup.json","cleanup.json","update.zip"}.Any(name=>relative==name||relative.StartsWith(name+".",StringComparison.Ordinal)&&relative.EndsWith(".tmp",StringComparison.Ordinal)))throw new IOException("更新缓存含有非更新文件，已保留："+relative);
                    }
                    else if(top=="payload"||top=="rollback")
                    {if(!folder&&!UpdateProtocol.ManagedPath(relative.Substring(slash+1)))throw new IOException("更新缓存路径不在程序清单范围内。");}
                    else if(folder||!(relative.Substring(slash+1)=="Backup.Updater.exe"||relative.Substring(slash+1)=="Backup.Updater.exe.config"))throw new IOException("更新恢复目录包含未知文件。");
                    entries.Add(entry);if(folder)pending.Push(entry);
                }
            }
            return entries;
        }
        private static void DeleteOwnedSession(string session)
        {
            var entries=OwnedTree(session);string receipt=Path.Combine(session,"cleanup.json");
            // Keep the cleanup receipt until everything else is gone, so a locked helper can be retried.
            foreach(string entry in entries.Where(path=>!Directory.Exists(path)&&path!=receipt).OrderBy(path=>Path.GetFileName(path)=="transaction.json"?1:0))
            {UpdateProtocol.NoLinks(entry);File.Delete(entry);}
            foreach(string directory in entries.Where(Directory.Exists).OrderByDescending(path=>path.Length)){UpdateProtocol.NoLinks(directory);Directory.Delete(directory);}
            File.Delete(receipt);Directory.Delete(session);
        }
        public static void Clean(string installation,string currentVersion)
        {
            string updates=UpdateProtocol.UpdatesDirectory(installation);if(!Directory.Exists(updates))return;
            var active=UpdateTransactions.Active(installation);string activeSession=active==null?null:active.Session;
            bool verified=false;
            foreach(string session in Directory.GetDirectories(updates,"session-*",SearchOption.TopDirectoryOnly))
            {
                try
                {
                    UpdateInstaller.SessionDirectory(installation,Path.Combine(session,"payload"));if(session.Equals(activeSession,StringComparison.OrdinalIgnoreCase))continue;
                    string receiptPath=Path.Combine(session,"cleanup.json"),journal=Path.Combine(session,"transaction.json");
                    if(File.Exists(receiptPath))
                    {var receipt=UpdateTransactions.Read<UpdateCleanupReceipt>(receiptPath,65536);if(receipt==null||!UpdateTransactions.Normalize(receipt.Installation).Equals(UpdateTransactions.Normalize(installation),StringComparison.OrdinalIgnoreCase)||UpdateProtocol.ParseVersion(receipt.Version)>UpdateProtocol.ParseVersion(currentVersion))continue;}
                    else
                    {
                        if(File.Exists(journal))
                        {var value=UpdateTransactions.ReadJournal(journal,installation);if(value.State!=UpdateTransactions.Confirmed&&value.State!=UpdateTransactions.RolledBack)continue;}
                        else
                        {
                            string requestPath=Path.Combine(session,"request.json");if(!File.Exists(requestPath))continue;
                            var request=UpdateTransactions.Read<UpdateRequest>(requestPath,65536);
                            if(request==null||!UpdateTransactions.Normalize(request.Installation).Equals(UpdateTransactions.Normalize(installation),StringComparison.OrdinalIgnoreCase)||!UpdateTransactions.Normalize(request.Payload).Equals(Path.Combine(session,"payload"),StringComparison.OrdinalIgnoreCase)||UpdateProtocol.ParseVersion(request.Version)>UpdateProtocol.ParseVersion(currentVersion))continue;
                            var payload=UpdateProtocol.ReadManifest(request.Payload);if(UpdateProtocol.ParseVersion(payload.Version)!=UpdateProtocol.ParseVersion(request.Version))continue;UpdateProtocol.VerifyFiles(request.Payload,payload);
                            string rollback=Path.Combine(session,"rollback");UpdateProtocol.VerifyFiles(rollback,UpdateProtocol.ReadManifest(rollback));
                        }
                        OwnedTree(session);
                        if(!verified){var manifest=UpdateProtocol.ReadManifest(installation);if(UpdateProtocol.ParseVersion(manifest.Version)!=UpdateProtocol.ParseVersion(currentVersion))return;UpdateProtocol.VerifyFiles(installation,manifest);verified=true;}
                        UpdateProtocol.AtomicText(receiptPath,UpdateProtocol.Json(new UpdateCleanupReceipt{Installation=installation,Version=currentVersion}));
                    }
                    for(int attempt=0;attempt<3;attempt++)try{DeleteOwnedSession(session);break;}catch(IOException){if(attempt==2)throw;Thread.Sleep(500);}catch(UnauthorizedAccessException){if(attempt==2)throw;Thread.Sleep(500);}
                }
                catch(Exception error){UpdateTransactions.Log(installation,error);}
            }
        }
    }
}
