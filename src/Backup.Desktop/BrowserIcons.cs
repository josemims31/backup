using System;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace Backup.Desktop
{
    internal static class BrowserIcons
    {
        internal static readonly BitmapSource Repository=Load("repository.png");
        internal static readonly BitmapSource Archive=Load("archive.png");
        internal static readonly BitmapSource Folder=Load("folder.png");
        internal static readonly BitmapSource Task=Load("task.png");
        private static readonly string[] ArchiveExtensions={".7z",".zip",".rar",".tar",".gz",".bz2",".xz",".tgz",".tbz2",".txz"};
        internal static bool IsArchiveName(string name)
        {
            if(string.IsNullOrEmpty(name))return false;
            foreach(string extension in ArchiveExtensions)if(name.EndsWith(extension,StringComparison.OrdinalIgnoreCase))return true;
            int dot=name.LastIndexOf('.');if(dot<0||dot==name.Length-1)return false;
            for(int index=dot+1;index<name.Length;index++)if(name[index]<'0'||name[index]>'9')return false;
            string stem=name.Substring(0,dot);return stem.EndsWith(".7z",StringComparison.OrdinalIgnoreCase)||stem.EndsWith(".zip",StringComparison.OrdinalIgnoreCase)||stem.EndsWith(".rar",StringComparison.OrdinalIgnoreCase);
        }
        private static BitmapSource Load(string name)
        {
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Backup.Desktop.BrowserIcons."+name))
            {
                if(stream==null)throw new InvalidOperationException("远程文件图标资源缺失："+name);
                var image=BitmapFrame.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);image.Freeze();return image;
            }
        }
    }
}
