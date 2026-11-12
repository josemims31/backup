using System;
using System.Reflection;
using System.Windows.Media.Imaging;
namespace Backup.Desktop
{
    internal static class ApplicationIcon
    {
        private static readonly BitmapFrame image=Load();
        public static BitmapFrame Image {get{return image;}}
        private static BitmapFrame Load()
        {
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Backup.Desktop.AppIcon.ico")){
                if(stream==null)throw new InvalidOperationException("应用图标资源缺失。");
                var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
                foreach(var frame in decoder.Frames)frame.Freeze();
                return decoder.Frames[0];
            }
        }
    }
}
