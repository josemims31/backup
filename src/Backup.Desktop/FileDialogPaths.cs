using System;
using System.IO;
using System.Windows;
using Backup.Core;
namespace Backup.Desktop
{
    public static partial class EditorDialogs
    {
#if UI_TEST
        public static Func<Window,Microsoft.Win32.FileDialog,bool?> FileDialogOverride;
#endif
        internal static string ChooseFile(Window owner,Microsoft.Win32.FileDialog dialog,RuntimeStateStore preferences,string key,string currentPath=null)
        {return ChooseFile(owner,dialog,preferences,PathHistory.Purpose(key),currentPath);}
        internal static string ChooseFile(Window owner,Microsoft.Win32.FileDialog dialog,RuntimeStateStore preferences,PathPurpose purpose,string currentPath=null)
        {
            var history=new PathHistory(preferences);string previous=!string.IsNullOrWhiteSpace(currentPath)?currentPath:history.Recall(purpose);
            if(!string.IsNullOrWhiteSpace(previous))try
            {
                string path=Path.GetFullPath(previous);dialog.InitialDirectory=Path.GetDirectoryName(path);
                if(string.IsNullOrEmpty(dialog.FileName))dialog.FileName=Path.GetFileName(path);
            }catch(ArgumentException){}catch(NotSupportedException){}catch(PathTooLongException){}
            dialog.RestoreDirectory=true;bool? accepted;
#if UI_TEST
            accepted=FileDialogOverride==null?dialog.ShowDialog(owner):FileDialogOverride(owner,dialog);
#else
            accepted=dialog.ShowDialog(owner);
#endif
            if(accepted!=true)return null;string selected=Path.GetFullPath(dialog.FileName);
            history.Remember(purpose,selected);return selected;
        }
    }
}
