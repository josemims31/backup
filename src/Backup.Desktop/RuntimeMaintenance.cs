using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        public void PruneDatabaseRecoveryCopies()
        {
            string folder=CacheDirectory;
            if(!Directory.Exists(folder)||(File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)return;
            var owned=Directory.EnumerateFiles(folder,"catalog-before-*.db").Select(path=>new FileInfo(path)).Where(file=>(file.Attributes&FileAttributes.ReparsePoint)==0&&Regex.IsMatch(file.Name,@"^catalog-before-(update|import|move|v[0-9]+)-[0-9]{8}-?[0-9]{6}(-[a-f0-9]{6,8})?\.db$",RegexOptions.CultureInvariant)).ToList();
            foreach(var category in owned.GroupBy(file=>Regex.Match(file.Name,@"^catalog-before-([^-]+)-").Groups[1].Value))
            {
                if(category.Key=="move"&&Catalog.GetWarehouseMoves().Any(move=>move.State!="complete"))continue;
                foreach(var file in category.OrderByDescending(file=>file.LastWriteTimeUtc).ThenByDescending(file=>file.Name).Skip(3))
                try{if(!File.Exists(file.FullName+"-wal")&&!File.Exists(file.FullName+"-shm"))file.Delete();}catch(IOException){}catch(UnauthorizedAccessException){}
            }
        }
    }
}
