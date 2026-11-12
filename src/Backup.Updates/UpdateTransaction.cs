using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace Backup.Updates
{
    public sealed class RollbackFile {public string Path,Sha256;public bool Existed;public long Size;}
    public sealed class UpdateTransaction
    {
        public int Format=1;public string Id,Installation,Session,Version,State,Error;
        public UpdateRequest Request;public List<RollbackFile> Files=new List<RollbackFile>();
    }
    public sealed class UpdateTransactionPointer {public string Session,Id;}
    public sealed class UpdateStartupRecord
    {
        public string TransactionId,Error;public int ProcessId;public long StartUtcTicks;
        public bool Healthy;public string[] Arguments;
    }
    public static class UpdateTransactions
    {
        public const string Preparing="preparing",Installing="installing",AwaitingStartup="awaiting-startup",Confirmed="confirmed",RollingBack="rolling-back",RolledBack="rolled-back",RecoveryFailed="recovery-failed";
        public static string JournalPath(UpdateTransaction transaction){return Path.Combine(transaction.Session,"transaction.json");}
        public static string StartupPath(UpdateTransaction transaction){return Path.Combine(transaction.Session,"startup.json");}
        public static string ActivePath(string installation){return Path.Combine(UpdateProtocol.UpdatesDirectory(installation),"active.json");}
        public static string Normalize(string path){return Path.GetFullPath(path).TrimEnd('\\','/');}
        public static T Read<T>(string path,int limit)
        {
            UpdateProtocol.NoLinks(path);
            for(int attempt=0;;attempt++)try
            {
                // Metadata is atomically replaced; readers must not block its rename/delete commit.
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                {if(stream.Length>limit)throw new IOException("更新恢复记录过大。");using(var reader=new StreamReader(stream,Encoding.UTF8))return UpdateProtocol.ReadJson<T>(reader.ReadToEnd());}
            }
            catch(IOException error){int code=error.HResult&0xffff;if(attempt>=4||code!=32&&code!=33)throw;Thread.Sleep(20);}
        }
        public static UpdateTransaction ReadJournal(string path,string installation)
        {
            var value=Read<UpdateTransaction>(path,4194304);Validate(value,installation);
            if(!Path.GetFullPath(path).Equals(JournalPath(value),StringComparison.OrdinalIgnoreCase))throw new IOException("更新恢复记录路径不正确。");return value;
        }
        public static UpdateTransaction Active(string installation)
        {
            string path=ActivePath(installation);if(!File.Exists(path))return null;
            try
            {
            var pointer=Read<UpdateTransactionPointer>(path,65536);if(pointer==null||string.IsNullOrEmpty(pointer.Session))throw new IOException("更新恢复指针无效。");
            UpdateInstaller.SessionDirectory(installation,Path.Combine(pointer.Session,"payload"));
            var value=ReadJournal(Path.Combine(pointer.Session,"transaction.json"),installation);if(value.Id!=pointer.Id)throw new IOException("更新恢复记录不匹配。");return File.Exists(path)?value:null;
            }
            catch(FileNotFoundException){if(!File.Exists(path))return null;throw;}
            catch(DirectoryNotFoundException){if(!File.Exists(path))return null;throw;}
        }
        public static void Validate(UpdateTransaction value,string installation)
        {
            Guid id;
            if(value==null||value.Format!=1||!Guid.TryParseExact(value.Id??"","N",out id)||!Normalize(value.Installation).Equals(Normalize(installation),StringComparison.OrdinalIgnoreCase))throw new IOException("更新恢复记录无效。");
            string session=UpdateInstaller.SessionDirectory(installation,Path.Combine(value.Session,"payload"));
            if(!Normalize(value.Session).Equals(session,StringComparison.OrdinalIgnoreCase)||value.Request==null||!Normalize(value.Request.Installation).Equals(Normalize(installation),StringComparison.OrdinalIgnoreCase)||!Normalize(value.Request.Payload).Equals(Path.Combine(session,"payload"),StringComparison.OrdinalIgnoreCase)||UpdateProtocol.ParseVersion(value.Version)!=UpdateProtocol.ParseVersion(value.Request.Version))throw new IOException("更新恢复目标不一致。");
            if(!new[]{Preparing,Installing,AwaitingStartup,Confirmed,RollingBack,RolledBack,RecoveryFailed}.Contains(value.State)||value.Files==null||value.Files.Count>4001)throw new IOException("更新恢复状态无效。");
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var file in value.Files)
                if(file==null||!UpdateProtocol.ManagedPath(file.Path)||!names.Add(UpdateProtocol.SafeRelativePath(file.Path))||file.Size<0||file.Existed&&(file.Sha256==null||file.Sha256.Length!=64||file.Sha256.Any(c=>!Uri.IsHexDigit(c))))throw new IOException("更新回滚清单无效。");
            if(value.State!=Preparing&&value.Files.Count==0)throw new IOException("更新回滚清单为空。");
        }
        public static void Save(UpdateTransaction value){Validate(value,value.Installation);UpdateProtocol.AtomicText(JournalPath(value),UpdateProtocol.Json(value));}
        public static void Activate(UpdateTransaction value)
        {
            if(File.Exists(ActivePath(value.Installation)))throw new IOException("上次更新尚未确认，请先完成更新恢复。");
            Save(value);UpdateProtocol.AtomicText(ActivePath(value.Installation),UpdateProtocol.Json(new UpdateTransactionPointer{Session=value.Session,Id=value.Id}));
        }
        public static void Deactivate(UpdateTransaction value)
        {
            var active=Active(value.Installation);if(active==null)return;if(active.Id!=value.Id)throw new IOException("另一个更新正在使用恢复记录。");File.Delete(ActivePath(value.Installation));
        }
        public static void Result(UpdateTransaction value,bool success,bool pending,string message)
        {UpdateProtocol.AtomicText(Path.Combine(UpdateProtocol.UpdatesDirectory(value.Installation),"result.json"),UpdateProtocol.Json(new UpdateResult{Success=success,Pending=pending,Version=value.Version,Message=message,TimeUtc=DateTime.UtcNow.ToString("o")}));}
        public static Mutex Mutex(string installation,bool application)
        {
            string key=Path.GetFullPath(installation).TrimEnd('\\')+"\\",hash;
            using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key.ToUpperInvariant()))).Replace("-","");
            var security=new MutexSecurity();security.SetAccessRuleProtection(true,false);
            security.AddAccessRule(new MutexAccessRule(WindowsIdentity.GetCurrent().User,MutexRights.FullControl,AccessControlType.Allow));
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),MutexRights.FullControl,AccessControlType.Allow));
            bool created;return new Mutex(false,"Global\\"+(application?"BackupUI-":"BackupUpdate-")+hash,out created,security);
        }
        public static bool Enter(Mutex mutex,TimeSpan timeout){try{return mutex.WaitOne(timeout);}catch(AbandonedMutexException){return true;}}
        public static bool Matches(string path,long size,string hash)
        {return File.Exists(path)&&new FileInfo(path).Length==size&&UpdateProtocol.Hash(path).Equals(hash,StringComparison.OrdinalIgnoreCase);}
        public static void CopyDurable(string source,string target)
        {
            UpdateProtocol.NoLinks(source);UpdateProtocol.NoLinks(target);Directory.CreateDirectory(Path.GetDirectoryName(target));
            using(var input=File.OpenRead(source))using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None)){input.CopyTo(output);output.Flush(true);}
        }
        public static string RecoveryHelper(string installation,string payload)
        {
            string session=UpdateInstaller.SessionDirectory(installation,payload),directory=Path.Combine(session,"recovery");UpdateProtocol.NoLinks(directory);Directory.CreateDirectory(directory);
            var manifest=UpdateProtocol.ReadManifest(payload);
            foreach(string name in new[]{"Backup.Updater.exe","Backup.Updater.exe.config"})
            {
                var entry=manifest.Files.Single(file=>file.Path.Equals("updater/"+name,StringComparison.OrdinalIgnoreCase));string source=UpdateProtocol.FullPath(payload,entry.Path),target=Path.Combine(directory,name);
                if(!Matches(source,entry.Size,entry.Sha256))throw new IOException("更新恢复程序校验失败。");
                if(!Matches(target,entry.Size,entry.Sha256)){if(File.Exists(target))File.Delete(target);CopyDurable(source,target);}
            }
            return Path.Combine(directory,"Backup.Updater.exe");
        }
        public static UpdateStartupRecord Startup(UpdateTransaction value)
        {
            string path=StartupPath(value);if(!File.Exists(path))return null;var record=Read<UpdateStartupRecord>(path,65536);
            if(record==null||record.TransactionId!=value.Id||record.ProcessId<=0||record.StartUtcTicks<=0||record.Arguments!=null&&(record.Arguments.Length>16||record.Arguments.Any(arg=>arg==null||arg.Length>4096)))throw new IOException("更新启动确认记录无效。");return record;
        }
        public static Process Candidate(UpdateTransaction value,UpdateStartupRecord record)
        {
            if(record==null)return null;Process process=null;bool matched=false;
            try
            {
                process=Process.GetProcessById(record.ProcessId);
                if(!process.HasExited&&process.StartTime.ToUniversalTime().Ticks==record.StartUtcTicks&&Normalize(process.MainModule.FileName).Equals(Path.Combine(Normalize(value.Installation),"Backup.exe"),StringComparison.OrdinalIgnoreCase)){matched=true;return process;}
                return null;
            }
            catch(ArgumentException){return null;}catch(InvalidOperationException){return null;}
            catch(System.ComponentModel.Win32Exception){if(process!=null&&process.HasExited)return null;throw;}
            finally{if(process!=null&&!matched)process.Dispose();}
        }
        public static void Confirm(UpdateTransaction value)
        {
            value.State=Confirmed;value.Error=null;Save(value);Result(value,true,false,"软件已更新至 "+value.Version);Deactivate(value);
        }
        public static void CompleteTerminal(UpdateTransaction value)
        {
            if(value.State==Confirmed)Confirm(value);
            else if(value.State==RolledBack){Result(value,false,false,"更新未完成，原程序文件已恢复："+value.Error);Deactivate(value);}
        }
        public static string Quote(string value)
        {
            // Windows command-line quoting, including a trailing backslash before the quote.
            var result=new StringBuilder("\"");int slashes=0;
            foreach(char c in value){if(c=='\\'){slashes++;continue;}if(c=='\"'){result.Append('\\',slashes*2+1);result.Append(c);}else{result.Append('\\',slashes);result.Append(c);}slashes=0;}
            result.Append('\\',slashes*2);return result.Append('"').ToString();
        }
        public static Process Launch(string executable,string[] args,string directory)
        {return Process.Start(new ProcessStartInfo(executable,string.Join(" ",(args??new string[0]).Select(Quote))){WorkingDirectory=directory,UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});}
        public static void Log(string installation,Exception error)
        {try{string path=Path.Combine(UpdateProtocol.UpdatesDirectory(installation),"updater.log");UpdateProtocol.NoLinks(path);Directory.CreateDirectory(Path.GetDirectoryName(path));File.AppendAllText(path,DateTime.UtcNow.ToString("o")+" "+error+Environment.NewLine);}catch(Exception){}}
    }
}
