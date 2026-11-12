using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Backup.Core
{
    public static class ApplicationFiles
    {
        public static string CacheForCatalog(string path)
        {
            string folder=Path.GetDirectoryName(Path.GetFullPath(path));
            if(string.Equals(Path.GetFileName(folder),"data",StringComparison.OrdinalIgnoreCase))folder=Path.GetDirectoryName(folder);
            string cache=Path.Combine(folder,"cache");
            Directory.CreateDirectory(cache);
            if((File.GetAttributes(cache)&FileAttributes.ReparsePoint)!=0)throw new IOException("缓存目录不能是链接或联接点。");
            return cache;
        }
    }

    // Keep the main database at its public data path. Only SQLite's owned sidecars
    // are redirected. Native Windows file locking, syncing and recovery stay intact.
    internal static class CacheFileSystem
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Vfs
        {
            public int Version,FileSize,MaxPath;
            public IntPtr Next,Name,AppData,Open,Delete,Access,FullPath,DlOpen,DlError,DlSym,DlClose,Randomness,Sleep,Time,LastError,Time64,SetCall,GetCall,NextCall;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OpenFile(IntPtr vfs,IntPtr name,IntPtr file,int flags,IntPtr outputFlags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeleteFile(IntPtr vfs,IntPtr name,int sync);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AccessFile(IntPtr vfs,IntPtr name,int flags,out int result);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Ansi)] private static extern IntPtr sqlite3_vfs_find(string name);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] private static extern int sqlite3_vfs_register(IntPtr vfs,int makeDefault);
        private static readonly object gate=new object();
        private static readonly Dictionary<string,string> roots=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string,IntPtr> names=new Dictionary<string,IntPtr>(StringComparer.Ordinal);
        private static IntPtr nativeVfs,registered;
        private static OpenFile originalOpen,open;
        private static DeleteFile originalDelete,delete;
        private static AccessFile originalAccess,access;
        internal const string Name="backup-cache-v1";

        internal static string Register(string database,string cache)
        {
            database=Path.GetFullPath(database);
            // The cache moves with the installation. Do not include the absolute
            // install path in the journal name, including after an interrupted run.
            string folder=Path.Combine(cache,"sqlite");
            Directory.CreateDirectory(folder);
            if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)throw new IOException("SQLite 缓存目录不能是链接。");
            lock(gate)
            {
                EnsureRegistered();
                roots[database]=Path.Combine(folder,Path.GetFileName(database));
            }
            return Path.Combine(folder,Path.GetFileName(database));
        }
        private static void EnsureRegistered()
        {
            if(registered!=IntPtr.Zero)return;
            nativeVfs=sqlite3_vfs_find(null);
            if(nativeVfs==IntPtr.Zero)throw new IOException("Windows SQLite 文件接口不可用。");
            var value=(Vfs)Marshal.PtrToStructure(nativeVfs,typeof(Vfs));
            if(value.Version<1||value.Version>3)throw new IOException("不支持的 SQLite 文件接口版本。");
            originalOpen=(OpenFile)Marshal.GetDelegateForFunctionPointer(value.Open,typeof(OpenFile));
            originalDelete=(DeleteFile)Marshal.GetDelegateForFunctionPointer(value.Delete,typeof(DeleteFile));
            originalAccess=(AccessFile)Marshal.GetDelegateForFunctionPointer(value.Access,typeof(AccessFile));
            open=Open;delete=Delete;access=Access;
            value.Next=IntPtr.Zero;value.Name=Marshal.StringToHGlobalAnsi(Name);
            value.Open=Marshal.GetFunctionPointerForDelegate(open);value.Delete=Marshal.GetFunctionPointerForDelegate(delete);value.Access=Marshal.GetFunctionPointerForDelegate(access);
            registered=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Vfs)));
            Marshal.StructureToPtr(value,registered,false);
            if(sqlite3_vfs_register(registered,0)!=0)throw new IOException("无法注册 SQLite 缓存文件接口。");
        }
        private static string Mapped(IntPtr name)
        {
            if(name==IntPtr.Zero)return null;
            int length=0;while(Marshal.ReadByte(name,length)!=0){if(++length>65536)throw new IOException("SQLite 路径过长。");}
            byte[] bytes=new byte[length];Marshal.Copy(name,bytes,0,length);
            string path=Encoding.UTF8.GetString(bytes);
            foreach(string suffix in new[]{"-wal","-journal","-shm"})
            {
                if(!path.EndsWith(suffix,StringComparison.OrdinalIgnoreCase))continue;
                string target;lock(gate)if(roots.TryGetValue(path.Substring(0,path.Length-suffix.Length),out target))return target+suffix;
            }
            return null;
        }
        private static IntPtr Utf8(string value)
        {
            // Some VFS implementations retain the filename until xClose. These
            // few deterministic sidecar names live as long as the registered VFS.
            lock(gate){IntPtr result;if(names.TryGetValue(value,out result))return result;byte[] bytes=Encoding.UTF8.GetBytes(value+"\0\0\0");result=Marshal.AllocHGlobal(bytes.Length);Marshal.Copy(bytes,0,result,bytes.Length);names[value]=result;return result;}
        }
        private static int Open(IntPtr vfs,IntPtr name,IntPtr file,int flags,IntPtr output)
        {
            IntPtr mapped=IntPtr.Zero;
            try{string path=Mapped(name);if(path!=null)mapped=Utf8(path);return originalOpen(nativeVfs,mapped==IntPtr.Zero?name:mapped,file,flags,output);}
            catch{return 14;}
        }
        private static int Delete(IntPtr vfs,IntPtr name,int sync)
        {
            IntPtr mapped=IntPtr.Zero;
            try{string path=Mapped(name);if(path!=null)mapped=Utf8(path);return originalDelete(nativeVfs,mapped==IntPtr.Zero?name:mapped,sync);}
            catch{return 10;}
        }
        private static int Access(IntPtr vfs,IntPtr name,int flags,out int result)
        {
            result=0;IntPtr mapped=IntPtr.Zero;
            try{string path=Mapped(name);if(path!=null)mapped=Utf8(path);return originalAccess(nativeVfs,mapped==IntPtr.Zero?name:mapped,flags,out result);}
            catch{return 10;}
        }
    }
}
