using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Backup.Updates
{
    public sealed class UpdateRollbackException:IOException
    {public UpdateRollbackException(string directory,Exception error):base("更新未完成；部分文件回滚待处理，原文件保留在："+directory,error){}}
    public static class UpdateInstaller
    {
        private static void Replace(string source,string target)
        {
            UpdateProtocol.NoLinks(target);Directory.CreateDirectory(Path.GetDirectoryName(target));string temporary=target+".update-"+Guid.NewGuid().ToString("N")+".tmp";
            try{File.Copy(source,temporary);if(File.Exists(target))File.Replace(temporary,target,null);else File.Move(temporary,target);}finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        private static IEnumerable<string> PreviousPaths(string installation,InstallationManifest next)
        {
            string manifestPath=Path.Combine(installation,UpdateProtocol.ManifestName);if(File.Exists(manifestPath))return UpdateProtocol.ReadManifest(installation).Files.Select(file=>UpdateProtocol.SafeRelativePath(file.Path)).ToList();
            // Bootstrap only known program files when updating an installation predating manifests.
            var result=new HashSet<string>(next.Files.Select(file=>UpdateProtocol.SafeRelativePath(file.Path)),StringComparer.OrdinalIgnoreCase);
            foreach(string name in new[]{"Backup.exe","Backup.Core.dll","Backup.exe.config","7za.dll","Everything64.dll","LICENSE-7ZIP.txt","LICENSE-EVERYTHING.txt"})result.Add(name);
            foreach(string name in new[]{"Renci.SshNet.dll","BouncyCastle.Cryptography.dll","Microsoft.Extensions.Logging.Abstractions.dll","Microsoft.Extensions.DependencyInjection.Abstractions.dll","System.Formats.Asn1.dll","System.Buffers.dll","System.Memory.dll","System.ValueTuple.dll","Microsoft.Bcl.AsyncInterfaces.dll","System.Threading.Tasks.Extensions.dll","System.Numerics.Vectors.dll","System.Runtime.CompilerServices.Unsafe.dll","Microsoft.Bcl.Cryptography.dll","THIRD-PARTY-SFTP.txt"})result.Add("repositories/sftp/"+name);
            return result;
        }
        private static void PruneEmptyParents(string installation,string path)
        {
            for(string parent=Path.GetDirectoryName(path);parent!=null&&!parent.Equals(installation,StringComparison.OrdinalIgnoreCase);parent=Path.GetDirectoryName(parent))
            {UpdateProtocol.NoLinks(parent);if(!Directory.Exists(parent)||Directory.EnumerateFileSystemEntries(parent).Any())break;Directory.Delete(parent);}
        }
        public static string SessionDirectory(string installation,string payload)
        {
            installation=Path.GetFullPath(installation).TrimEnd('\\','/');payload=Path.GetFullPath(payload);
            string updates=UpdateProtocol.UpdatesDirectory(installation),session=Path.GetDirectoryName(payload);
            Guid sessionId;if(!payload.StartsWith(updates+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(payload).Equals("payload",StringComparison.Ordinal)||!Path.GetDirectoryName(session).Equals(updates,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(session).StartsWith("session-",StringComparison.Ordinal)||!Guid.TryParseExact(Path.GetFileName(session).Substring(8),"N",out sessionId))throw new IOException("更新暂存目录不属于当前安装目录。");
            UpdateProtocol.NoLinks(payload);return session;
        }
        public static void Install(string installation,string payload,string expectedVersion)
        {
            installation=Path.GetFullPath(installation).TrimEnd('\\','/');payload=Path.GetFullPath(payload);UpdateProtocol.NoLinks(installation);UpdateProtocol.NoLinks(payload);
            if(installation.Equals(Path.GetPathRoot(installation).TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase)||!File.Exists(Path.Combine(installation,"Backup.exe")))throw new IOException("目标不是有效的软件安装目录。");
            string session=SessionDirectory(installation,payload);
            var manifest=UpdateProtocol.ReadManifest(payload);if(UpdateProtocol.ParseVersion(manifest.Version)!=UpdateProtocol.ParseVersion(expectedVersion))throw new IOException("更新版本不一致。");UpdateProtocol.VerifyFiles(payload,manifest);
            var next=new HashSet<string>(manifest.Files.Select(file=>UpdateProtocol.SafeRelativePath(file.Path)),StringComparer.OrdinalIgnoreCase);next.Add(UpdateProtocol.ManifestName);
            var previous=new HashSet<string>(PreviousPaths(installation,manifest),StringComparer.OrdinalIgnoreCase);previous.Add(UpdateProtocol.ManifestName);
            var all=new HashSet<string>(previous,StringComparer.OrdinalIgnoreCase);all.UnionWith(next);foreach(string relative in all){if(!UpdateProtocol.ManagedPath(relative))throw new IOException("软件文件清单越界。");UpdateProtocol.NoLinks(UpdateProtocol.FullPath(installation,relative));}
            string rollback=Path.Combine(session,"rollback");if(Directory.Exists(rollback))throw new IOException("本次更新已有回滚记录，已停止重复执行。");Directory.CreateDirectory(rollback);
            var existed=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var changed=new List<string>();
            // Back up only files that this update is allowed to replace or retire.
            foreach(string relative in all)
            {
                string target=UpdateProtocol.FullPath(installation,relative);if(!File.Exists(target))continue;string backup=UpdateProtocol.FullPath(rollback,relative);Directory.CreateDirectory(Path.GetDirectoryName(backup));File.Copy(target,backup);existed.Add(relative);
            }
            try
            {
                foreach(var file in manifest.Files)
                {string relative=UpdateProtocol.SafeRelativePath(file.Path);Replace(UpdateProtocol.FullPath(payload,relative),UpdateProtocol.FullPath(installation,relative));changed.Add(relative);}
                foreach(string relative in previous.Where(path=>!next.Contains(path)))
                {string target=UpdateProtocol.FullPath(installation,relative);if(File.Exists(target)){File.Delete(target);changed.Add(relative);}}
                Replace(Path.Combine(payload,UpdateProtocol.ManifestName),Path.Combine(installation,UpdateProtocol.ManifestName));changed.Add(UpdateProtocol.ManifestName);
                foreach(string relative in previous.Where(path=>!next.Contains(path)))PruneEmptyParents(installation,UpdateProtocol.FullPath(installation,relative));
            }
            catch(Exception error)
            {
                var failures=new List<Exception>();foreach(string relative in changed.Distinct(StringComparer.OrdinalIgnoreCase).Reverse())try
                {string target=UpdateProtocol.FullPath(installation,relative);if(existed.Contains(relative))Replace(UpdateProtocol.FullPath(rollback,relative),target);else if(File.Exists(target))File.Delete(target);}
                catch(Exception restore){failures.Add(restore);}
                if(failures.Count>0)throw new UpdateRollbackException(rollback,new AggregateException(new[]{error}.Concat(failures)));
                throw new IOException("更新未完成，原程序文件已恢复："+error.Message,error);
            }
        }
    }
}
