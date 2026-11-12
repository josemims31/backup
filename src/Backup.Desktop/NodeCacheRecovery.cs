using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Backup.Core;
namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        private readonly object nodeRecoveryGate=new object();
        private List<string> movedNodeFolders;
        private List<string> MovedNodeFolders()
        {
            if(movedNodeFolders!=null)return movedNodeFolders;
            movedNodeFolders=new List<string>();string root=Path.Combine(CacheDirectory,"legacy-data");
            if(!Directory.Exists(root)||(File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)return movedNodeFolders;
            foreach(string folder in Directory.EnumerateDirectories(root))
                if(Regex.IsMatch(Path.GetFileName(folder),@"\Anodes(?:\.migrated-[0-9a-f]{32})*\z",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant)&&(File.GetAttributes(folder)&FileAttributes.ReparsePoint)==0)movedNodeFolders.Add(folder);
            return movedNodeFolders;
        }
        // Only a requested, still-registered version can recover its own local
        // cache. Historical copies remain untouched and no repository is opened.
        private NodeFileDatabase RecoverMovedNodeCache(BackupNode node)
        {
            if(node.DatabaseBytes<=0||node.DatabaseBytes>int.MaxValue||node.DatabaseBytes>MemoryLimitBytes/3)return null;
            lock(nodeRecoveryGate)
            {
                List<string> folders;try{folders=MovedNodeFolders();}catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}
                foreach(string folder in folders)
                {
                    byte[] bytes;NodeFileDatabase files;
                    try
                    {
                        if(!Directory.Exists(folder)||(File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)continue;
                        var source=new FileInfo(Path.Combine(folder,node.Id+".db"));
                        if(!source.Exists||(source.Attributes&FileAttributes.ReparsePoint)!=0||source.Length!=node.DatabaseBytes)continue;
                        bytes=File.ReadAllBytes(source.FullName);files=Catalog.ReadRegisteredNodeDatabase(bytes,node);if(files.Revision!=node.Revision)continue;
                    }
                    catch(IOException){continue;}catch(UnauthorizedAccessException){continue;}
                    if(!Catalog.GetNodes(node.TaskId).Any(value=>value.Id==node.Id&&value.Digest==node.Digest&&value.Revision==node.Revision))return null;
                    try{CacheNode(node,bytes);}catch(IOException){}catch(UnauthorizedAccessException){}
                    return files;
                }
                return null;
            }
        }
    }
}
