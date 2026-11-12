using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Backup.Updates
{
    public sealed class InstalledFile {public string Path;public long Size;public string Sha256;}
    public sealed class InstallationManifest
    {
        public int Format=1;public string Product="Backup-fang523";public string Version;public List<InstalledFile> Files=new List<InstalledFile>();
    }
    public sealed class UpdateRequest
    {
        public string Installation,Payload,Version;public int ParentProcessId;public long ParentStartUtcTicks;public bool ExitAfterUpdate,Minimized;
    }
    public sealed class UpdateResult {public bool Success;public string Version,Message,TimeUtc;}
    public static class UpdateProtocol
    {
        public const string ManifestName="installation-manifest.json";
        public const string AssetName="Backup-fang523-win-x64.zip";
        public const string RepositoryUrl="https://github.com/josemims31/backup";
        public const string ReleaseApi="https://api.github.com/repos/josemims31/backup/releases/latest";
        public static Version ParseVersion(string value)
        {
            value=(value??"").Trim();if(value.StartsWith("v",StringComparison.OrdinalIgnoreCase))value=value.Substring(1);
            if(!Regex.IsMatch(value,@"\A\d+\.\d+\.\d+(?:\.\d+)?\z"))throw new InvalidDataException("发布版本号无效。");
            Version parsed;if(!System.Version.TryParse(value,out parsed))throw new InvalidDataException("发布版本号无效。");
            return new Version(parsed.Major,parsed.Minor,Math.Max(0,parsed.Build),Math.Max(0,parsed.Revision));
        }
        public static string Json<T>(T value)
        {using(var stream=new MemoryStream()){new DataContractJsonSerializer(typeof(T)).WriteObject(stream,value);return Encoding.UTF8.GetString(stream.ToArray());}}
        public static T ReadJson<T>(string json)
        {using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(json)))return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);}
        public static string Hash(string file)
        {using(var sha=SHA256.Create())using(var stream=File.OpenRead(file))return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
        public static void AtomicText(string path,string text)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{File.WriteAllText(temporary,text,new UTF8Encoding(false));if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);}finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        public static string SafeRelativePath(string value)
        {
            value=(value??"").Replace('\\','/');
            if(value.Length==0||value.Length>220||value.StartsWith("/",StringComparison.Ordinal)||value.IndexOf(':')>=0||value.IndexOf('\0')>=0)throw new InvalidDataException("更新包包含无效路径。");
            foreach(string part in value.Split('/'))if(part.Length==0||part=="."||part==".."||part.EndsWith(".",StringComparison.Ordinal)||part.EndsWith(" ",StringComparison.Ordinal)||part.IndexOfAny(System.IO.Path.GetInvalidFileNameChars())>=0||Regex.IsMatch(part,@"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)",RegexOptions.IgnoreCase))throw new InvalidDataException("更新包包含无效路径。");
            return value;
        }
        public static bool ManagedPath(string value)
        {
            value=SafeRelativePath(value);string[] roots={"Backup.exe","Backup.Core.dll","Backup.exe.config","7za.dll","Everything64.dll","LICENSE","NOTICE","THIRD_PARTY_NOTICES.md","LICENSE-7ZIP.txt","LICENSE-EVERYTHING.txt",ManifestName};
            return roots.Contains(value,StringComparer.OrdinalIgnoreCase)||new[]{"repositories/","updater/","licenses/"}.Any(prefix=>value.StartsWith(prefix,StringComparison.OrdinalIgnoreCase));
        }
        public static void ValidateManifest(InstallationManifest manifest)
        {
            if(manifest==null||manifest.Format!=1||manifest.Product!="Backup-fang523"||manifest.Files==null||manifest.Files.Count<5||manifest.Files.Count>2000)throw new InvalidDataException("更新包的软件清单无效。");
            ParseVersion(manifest.Version);var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
            foreach(var file in manifest.Files)
            {
                if(file==null||!ManagedPath(file.Path)||file.Path.Equals(ManifestName,StringComparison.OrdinalIgnoreCase)||!paths.Add(SafeRelativePath(file.Path))||file.Size<0||file.Size>268435456||!Regex.IsMatch(file.Sha256??"",@"\A[0-9a-fA-F]{64}\z"))throw new InvalidDataException("更新包的软件清单包含无效或重复文件。");
                total+=file.Size;if(total>536870912)throw new InvalidDataException("更新包解压后的大小超出限制。");
            }
            foreach(string name in new[]{"Backup.exe","Backup.Core.dll","Backup.exe.config","7za.dll","Everything64.dll","updater/Backup.Updater.exe","updater/Backup.Updater.exe.config","repositories/local/Backup.Repository.Local.dll","repositories/smb/Backup.Repository.Smb.dll","repositories/webdav/Backup.Repository.WebDav.dll","repositories/sftp/Backup.Repository.Sftp.dll"})
                if(!paths.Contains(name))throw new InvalidDataException("更新包缺少必要文件："+name);
        }
        public static string FullPath(string root,string relative)
        {
            root=System.IO.Path.GetFullPath(root).TrimEnd('\\','/');relative=SafeRelativePath(relative);
            string full=System.IO.Path.GetFullPath(System.IO.Path.Combine(root,relative.Replace('/',System.IO.Path.DirectorySeparatorChar)));
            if(!full.StartsWith(root+System.IO.Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("更新文件路径越界。");
            return full;
        }
        public static void NoLinks(string path)
        {
            path=System.IO.Path.GetFullPath(path);for(string current=path;!string.IsNullOrEmpty(current);current=System.IO.Path.GetDirectoryName(current))
                if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("更新路径不能包含链接或联接点。");
        }
        public static InstallationManifest ReadManifest(string directory)
        {
            string path=System.IO.Path.Combine(directory,ManifestName);NoLinks(path);if(new FileInfo(path).Length>1048576)throw new InvalidDataException("软件清单过大。");
            var manifest=ReadJson<InstallationManifest>(File.ReadAllText(path,Encoding.UTF8));ValidateManifest(manifest);return manifest;
        }
        public static void VerifyFiles(string directory,InstallationManifest manifest)
        {
            ValidateManifest(manifest);NoLinks(directory);foreach(var file in manifest.Files){string path=FullPath(directory,file.Path);NoLinks(path);if(!File.Exists(path)||new FileInfo(path).Length!=file.Size||!Hash(path).Equals(file.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("更新文件校验失败："+file.Path);}
            string executable=FullPath(directory,"Backup.exe");if(ParseVersion(System.Diagnostics.FileVersionInfo.GetVersionInfo(executable).FileVersion)!=ParseVersion(manifest.Version))throw new InvalidDataException("更新包版本与主程序不一致。");
        }
        public static string UpdatesDirectory(string installation)
        {string path=System.IO.Path.Combine(System.IO.Path.GetFullPath(installation),"cache","updates");NoLinks(path);return path;}
    }
}
