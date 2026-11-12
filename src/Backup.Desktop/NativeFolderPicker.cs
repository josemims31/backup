using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
namespace Backup.Desktop
{
    // Windows Common Item Dialog: native address bar, network locations and folder creation.
    public static class NativeFolderPicker
    {
        public const int Cancelled=unchecked((int)0x800704C7);
#if UI_TEST
        public static Action<IFileDialog> BeforeShow;
#endif
        [DllImport("shell32.dll",CharSet=CharSet.Unicode,PreserveSig=false)]
        private static extern void SHCreateItemFromParsingName(string path,IntPtr bindContext,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out IShellItem item);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="MessageBoxW")]
        private static extern int NativeMessageBox(IntPtr owner,string text,string title,uint flags);
        public static string Choose(Window owner,string initialFolder,string title,bool requireExplicitChoice=false)
        {
            var dialog=(IFileDialog)new FileOpenDialog();IShellItem result=null;uint cookie=0;bool advised=false;ExplicitFolderChoice choice=null;
            try{
                uint options;dialog.GetOptions(out options);dialog.SetOptions(options|0x20|0x40|0x800);dialog.SetTitle(title);dialog.SetOkButtonLabel("选择文件夹");
                // Always override Shell's last location, which may be a non-filesystem SMB server node.
                if(!TrySetFolder(dialog,initialFolder)&&!TrySetFolder(dialog,Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
                    TrySetFolder(dialog,Path.GetPathRoot(Environment.SystemDirectory));
                if(requireExplicitChoice){choice=new ExplicitFolderChoice(owner);dialog.Advise(choice,out cookie);advised=true;}
#if UI_TEST
                if(BeforeShow!=null)BeforeShow(dialog);
#endif
                int status=dialog.Show(owner==null?IntPtr.Zero:new WindowInteropHelper(owner).Handle);if(status==Cancelled)return null;Marshal.ThrowExceptionForHR(status);
                dialog.GetResult(out result);string path;result.GetDisplayName(0x80058000,out path);return path;
            }finally{if(advised)dialog.Unadvise(cookie);GC.KeepAlive(choice);if(result!=null)Marshal.ReleaseComObject(result);Marshal.ReleaseComObject(dialog);}
        }
        [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
        public sealed class ExplicitFolderChoice:IFileDialogEvents
        {
            private readonly Window owner;private bool opened,chosen;
            public ExplicitFolderChoice(Window window){owner=window;}
            public int OnFolderChange(IFileDialog dialog){if(opened)chosen=true;opened=true;return 0;}
            public int OnSelectionChange(IFileDialog dialog)
            {
                if(!opened)return 0;IShellItem selected=null,folder=null;
                try{dialog.GetCurrentSelection(out selected);dialog.GetFolder(out folder);uint attributes;selected.GetAttributes(0x60000000,out attributes);string path,current;selected.GetDisplayName(0x80058000,out path);folder.GetDisplayName(0x80058000,out current);if((attributes&0x60000000)==0x60000000&&!path.Equals(current,StringComparison.OrdinalIgnoreCase))chosen=true;}
                catch(COMException){}finally{if(selected!=null)Marshal.ReleaseComObject(selected);if(folder!=null)Marshal.ReleaseComObject(folder);}return 0;
            }
            public int OnFileOk(IFileDialog dialog)
            {
                if(chosen)return 0;
                IntPtr handle=IntPtr.Zero;try{((IOleWindow)dialog).GetWindow(out handle);}catch(COMException){if(owner!=null)handle=new WindowInteropHelper(owner).Handle;}
                NativeMessageBox(handle,"尚未选择目标文件夹。请选中一个文件夹，或进入目标文件夹后再点击“选择文件夹”。","请选择目标文件夹",0x40);return 1;
            }
            public int OnFolderChanging(IFileDialog dialog,IShellItem folder){return 0;}
            public int OnShareViolation(IFileDialog dialog,IShellItem item,out uint response){response=0;return 0;}
            public int OnTypeChange(IFileDialog dialog){return 0;}
            public int OnOverwrite(IFileDialog dialog,IShellItem item,out uint response){response=0;return 0;}
        }
        private static bool TrySetFolder(IFileDialog dialog,string path)
        {
            IShellItem folder=null;
            try{
                if(string.IsNullOrWhiteSpace(path)||!Path.IsPathRooted(path))return false;
                path=Path.GetFullPath(path);
                if(path.StartsWith(@"\\")&&path.Substring(2).Split(new[]{'\\'},StringSplitOptions.RemoveEmptyEntries).Length<2)return false;
                Guid iid=typeof(IShellItem).GUID;SHCreateItemFromParsingName(path,IntPtr.Zero,ref iid,out folder);
                const uint filesystemFolder=0x60000000;uint attributes;folder.GetAttributes(filesystemFolder,out attributes);
                if((attributes&filesystemFolder)!=filesystemFolder)return false;
                dialog.SetFolder(folder);return true;
            }catch(COMException){return false;}catch(ArgumentException){return false;}catch(NotSupportedException){return false;}
            finally{if(folder!=null)Marshal.ReleaseComObject(folder);}
        }
        [ComImport,Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"),ClassInterface(ClassInterfaceType.None)]private class FileOpenDialog{}
        [ComImport,Guid("00000114-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IOleWindow{void GetWindow(out IntPtr window);void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)]bool enter);}
        [ComImport,Guid("42f85136-db7e-439c-85f1-e4075d135fc8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IFileDialog
        {
            [PreserveSig]int Show(IntPtr owner);
            void SetFileTypes(uint count,IntPtr types);void SetFileTypeIndex(uint index);void GetFileTypeIndex(out uint index);
            void Advise(IFileDialogEvents events,out uint cookie);void Unadvise(uint cookie);
            void SetOptions(uint options);void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem folder);void SetFolder(IShellItem folder);void GetFolder(out IShellItem folder);void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)]string name);void GetFileName([MarshalAs(UnmanagedType.LPWStr)]out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)]string title);void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)]string text);void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)]string label);
            void GetResult(out IShellItem item);void AddPlace(IShellItem item,int placement);void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)]string extension);
            void Close(int status);void SetClientGuid(ref Guid guid);void ClearClientData();void SetFilter(IntPtr filter);
        }
        [ComImport,Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IShellItem
        {
            void BindToHandler(IntPtr context,ref Guid handler,ref Guid iid,out IntPtr result);void GetParent(out IShellItem parent);
            void GetDisplayName(uint format,[MarshalAs(UnmanagedType.LPWStr)]out string name);void GetAttributes(uint mask,out uint attributes);void Compare(IShellItem item,uint hint,out int order);
        }
        [ComImport,Guid("973510db-7d7f-452b-8975-74a85828d354"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IFileDialogEvents
        {
            [PreserveSig]int OnFileOk(IFileDialog dialog);[PreserveSig]int OnFolderChanging(IFileDialog dialog,IShellItem folder);[PreserveSig]int OnFolderChange(IFileDialog dialog);
            [PreserveSig]int OnSelectionChange(IFileDialog dialog);[PreserveSig]int OnShareViolation(IFileDialog dialog,IShellItem item,out uint response);[PreserveSig]int OnTypeChange(IFileDialog dialog);[PreserveSig]int OnOverwrite(IFileDialog dialog,IShellItem item,out uint response);
        }
    }
}
