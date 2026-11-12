using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace Backup.Updates
{
    public static class UpdatePackage
    {
        public static InstallationManifest Extract(string archive,string destination,string expectedVersion,CancellationToken cancellation)
        {
            UpdateProtocol.NoLinks(destination);if(Directory.Exists(destination))throw new IOException("更新暂存目录已存在。");Directory.CreateDirectory(destination);
            using(var zip=ZipFile.OpenRead(archive))
            {
                if(zip.Entries.Count>2200)throw new InvalidDataException("更新包文件数量超出限制。");
                var manifests=zip.Entries.Where(entry=>entry.Name.Equals(UpdateProtocol.ManifestName,StringComparison.OrdinalIgnoreCase)).ToList();
                if(manifests.Count!=1||manifests[0].Length>1048576)throw new InvalidDataException("更新包必须包含唯一的软件清单。");
                string prefix=manifests[0].FullName.Substring(0,manifests[0].FullName.Length-manifests[0].Name.Length);if(prefix.Length>0)UpdateProtocol.SafeRelativePath(prefix.TrimEnd('/'));
                InstallationManifest manifest;using(var reader=new StreamReader(manifests[0].Open(),Encoding.UTF8))manifest=UpdateProtocol.ReadJson<InstallationManifest>(reader.ReadToEnd());
                UpdateProtocol.ValidateManifest(manifest);if(UpdateProtocol.ParseVersion(manifest.Version)!=UpdateProtocol.ParseVersion(expectedVersion))throw new InvalidDataException("更新包版本与发布版本不一致。");
                var expected=manifest.Files.ToDictionary(file=>UpdateProtocol.SafeRelativePath(file.Path),StringComparer.OrdinalIgnoreCase);var found=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var entry in zip.Entries)
                {
                    cancellation.ThrowIfCancellationRequested();if(!entry.FullName.StartsWith(prefix,StringComparison.Ordinal))throw new InvalidDataException("更新包包含清单之外的路径。");
                    string relative=entry.FullName.Substring(prefix.Length);if(relative.Length==0)continue;if(entry.Name.Length==0){UpdateProtocol.SafeRelativePath(relative.TrimEnd('/'));continue;}
                    relative=UpdateProtocol.SafeRelativePath(relative);if(!found.Add(relative)||(entry.ExternalAttributes>>16&0xf000)==0xa000)throw new InvalidDataException("更新包包含重复路径或符号链接。");
                    if(relative.Equals(UpdateProtocol.ManifestName,StringComparison.OrdinalIgnoreCase))continue;
                    InstalledFile file;if(!expected.TryGetValue(relative,out file)||file.Size!=entry.Length)throw new InvalidDataException("更新包文件与清单不一致："+relative);
                    string target=UpdateProtocol.FullPath(destination,relative);Directory.CreateDirectory(Path.GetDirectoryName(target));using(var input=entry.Open())using(var output=File.Create(target))
                    {var buffer=new byte[65536];long count=0;int read;while((read=input.Read(buffer,0,buffer.Length))>0){cancellation.ThrowIfCancellationRequested();count+=read;if(count>file.Size)throw new InvalidDataException("更新文件大小不符。");output.Write(buffer,0,read);}if(count!=file.Size)throw new InvalidDataException("更新文件大小不符。");}
                }
                if(expected.Keys.Any(path=>!found.Contains(path)))throw new InvalidDataException("更新包文件不完整。");
                File.WriteAllText(Path.Combine(destination,UpdateProtocol.ManifestName),UpdateProtocol.Json(manifest),new UTF8Encoding(false));UpdateProtocol.VerifyFiles(destination,manifest);return manifest;
            }
        }
    }
}
