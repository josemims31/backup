using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Backup.Core
{
    public sealed class SourceItem
    {
        public string Path;
        public string ArchivePath;
        public string EntryPath;
        public string StoredPath {get{return string.IsNullOrEmpty(EntryPath)?ArchivePath:EntryPath;}}
        public long Size;
        public long Modified;
        public int ContentOpenCount;
        public SourceReadStream Reader;
        public BackupControl Control;
        public bool VerifyContentDuringRead = true;
        public Action<long> ReadObserver;
        internal byte[] MemoryContent;
        internal Func<Stream> MemoryStreamFactory;
        public bool IsMemory {get{return MemoryContent!=null||MemoryStreamFactory!=null;}}
        public static SourceItem FromSnapshot(DatabaseMemorySnapshot snapshot,string archivePath){var item=FromMemory(new byte[0],archivePath);item.MemoryContent=null;item.MemoryStreamFactory=snapshot.OpenRead;item.Size=snapshot.Length;return item;}
        public static bool SameMetadata(SourceItem first,SourceItem second){return string.Equals(first.Path,second.Path,StringComparison.Ordinal)&&first.Size==second.Size&&first.Modified==second.Modified;}
        public static SourceItem FromMemory(byte[] content,string archivePath){if(content==null||string.IsNullOrWhiteSpace(archivePath)||Array.IndexOf(archivePath.Split('\\','/'),"..")>=0||archivePath.IndexOfAny(new[]{':','\0'})>=0||archivePath.StartsWith("/")||archivePath.StartsWith("\\"))throw new ArgumentException("无效的内存文件。");return new SourceItem{Path="",ArchivePath=archivePath,Size=content.Length,Modified=DateTime.UtcNow.ToFileTimeUtc(),MemoryContent=content,VerifyContentDuringRead=false};}
        public static SourceItem FromStream(Func<Stream> open,long size,string archivePath)
        {if(open==null||size<0)throw new ArgumentException("无效的备份内容流。");var source=FromMemory(new byte[0],archivePath);source.MemoryContent=null;source.MemoryStreamFactory=open;source.Size=size;return source;}
        public static SourceItem FromFile(string path, string archivePath)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(archivePath)) throw new ArgumentException("Source and archive path required.");
            FileInfo f = new FileInfo(path);
            f.Refresh();if(!f.Exists){File.GetAttributes(path);throw new IOException("源路径不是可读取的文件："+path);}
            if ((f.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse-point sources are not supported by this prototype.");
            if (archivePath.StartsWith("\\") || archivePath.StartsWith("/") || archivePath.Contains(":") || archivePath.Contains("\0") || Array.IndexOf(archivePath.Split('\\', '/'), "..") >= 0)
                throw new ArgumentException("Unsafe archive path.");
            return new SourceItem { Path = f.FullName, ArchivePath = archivePath, Size = f.Length,
                Modified = f.LastWriteTimeUtc.ToFileTimeUtc() };
        }
        public void CheckUnchanged()
        {
            if(IsMemory)return;
            FileInfo f = new FileInfo(Path);
            if (!f.Exists || (f.Attributes & FileAttributes.ReparsePoint) != 0 || f.Length != Size || f.LastWriteTimeUtc.ToFileTimeUtc() != Modified)
                throw new IOException("Source metadata changed: " + Path);
        }
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class SourceReadStream : ISequentialInStream, IDisposable
    {
        private Stream file;
        private SHA256 hash;
        private Action<long> observer;
        private BackupControl control;
        private BackupIoLease sourceIo;private long initialPosition;
        public Exception Error;
        private byte[] buffer = new byte[64 * 1024];
        private bool disposed;
        public long BytesRead;
        public long ReadCalls;
        public string Sha256;
        public const string AccessMode = "FileAccess.Read; FileShare.ReadWrite|Delete; SequentialScan";
        public SourceReadStream(SourceItem item)
        {
            hash = item.VerifyContentDuringRead ? SHA256.Create() : null;
            observer = item.ReadObserver;control=item.Control;
            // The only source-content open in the prototype. No write/delete access is requested.
            file = item.IsMemory?(item.MemoryStreamFactory==null?(Stream)new MemoryStream(item.MemoryContent,false):item.MemoryStreamFactory()):new FileStream(item.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, buffer.Length, FileOptions.SequentialScan | FileOptions.Asynchronous);
            item.ContentOpenCount++;
            if(file.CanSeek)initialPosition=file.Position;
        }
        public int Read(IntPtr data, uint size, IntPtr processed)
        {
            try
            {
                if(disposed)throw new ObjectDisposedException("SourceReadStream");
                int amount=(int)Math.Min((uint)buffer.Length,size);int n;
                if(control==null)n=file.Read(buffer,0,amount);
                else while(true){
                    if(sourceIo!=null&&sourceIo.Token.IsCancellationRequested&&sourceIo.PauseInterrupted){sourceIo.Dispose();sourceIo=null;}
                    if(sourceIo==null){sourceIo=control.BeginIo(true);}control.SourceCheckpoint();
                    long position=initialPosition+BytesRead;
                    try{n=file.ReadAsync(buffer,0,amount,sourceIo.Token).GetAwaiter().GetResult();control.SourceCheckpoint();break;}
                    catch(Exception){if(!sourceIo.PauseInterrupted)throw;sourceIo.Dispose();sourceIo=null;control.SourceCheckpoint();if(!file.CanSeek)throw new IOException("源流无法在暂停后恢复读取。");file.Position=position;}
                }
                if (n > 0)
                {
                    Marshal.Copy(buffer, 0, data, n);
                    // Verification-only inline digest: never reopen the source to validate restoration.
                    if (hash != null) hash.TransformBlock(buffer, 0, n, buffer, 0);
                }
                BytesRead += n; ReadCalls++;
                if (observer != null && n > 0) observer(n);
                if (processed != IntPtr.Zero) Marshal.WriteInt32(processed, n);
                return 0;
            }
            catch (Exception e) { Error=e;if (processed != IntPtr.Zero) Marshal.WriteInt32(processed, 0); return Marshal.GetHRForException(e); }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if(file!=null)file.Dispose();if(sourceIo!=null){sourceIo.Dispose();sourceIo=null;}
                if(hash!=null)
                {
                    hash.TransformFinalBlock(new byte[0],0,0);
                    Sha256=BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
                }
            }
            finally
            {
                if(hash!=null)hash.Dispose();
                file=null;hash=null;buffer=null;observer=null;control=null;
            }
        }
    }
    public interface IFileCatalogSource
    {
        IList<SourceItem> Query(string root);
    }
    public sealed class EverythingCatalogSource : IFileCatalogSource
    {
        private static readonly object queryGate = new object();
        // Explicit metadata allowlist: full path/name, size, and modification time.
        private const uint FileListRequestFlags=4|0x10|0x40;
        private const int InitialPathCapacity=260,MaximumPathCapacity=32768;
        // Frees only this client's query strings/result buffers; never touches Everything's index.
        [DllImport("Everything64.dll")] private static extern void Everything_Reset();
        // Query configuration affects this SDK client's request, not Everything's stored index/configuration.
        [DllImport("Everything64.dll", CharSet=CharSet.Unicode)] private static extern void Everything_SetSearchW(string search);
        [DllImport("Everything64.dll")] private static extern void Everything_SetRequestFlags(uint flags);
        [DllImport("Everything64.dll")] private static extern void Everything_SetSort(uint sort);
        [DllImport("Everything64.dll")] private static extern bool Everything_QueryW(bool wait);
        [DllImport("Everything64.dll")] private static extern uint Everything_GetNumResults();
        [DllImport("Everything64.dll")] private static extern uint Everything_GetLastError();
        [DllImport("Everything64.dll", CharSet=CharSet.Unicode)] private static extern uint Everything_GetResultFullPathNameW(uint index, StringBuilder path, uint size);
        [DllImport("Everything64.dll")] private static extern bool Everything_GetResultSize(uint index, out long size);
        [DllImport("Everything64.dll")] private static extern bool Everything_GetResultDateModified(uint index, out long time);
        [DllImport("Everything64.dll")] private static extern bool Everything_IsDBLoaded();
        [DllImport("Everything64.dll")] private static extern uint Everything_GetMajorVersion();
        [DllImport("Everything64.dll")] private static extern uint Everything_GetMinorVersion();
        [DllImport("Everything64.dll")] private static extern uint Everything_GetRevision();
        [DllImport("Everything64.dll")] private static extern uint Everything_GetBuildNumber();
        public string Version { get { return string.Format("{0}.{1}.{2}.{3}", Everything_GetMajorVersion(), Everything_GetMinorVersion(), Everything_GetRevision(), Everything_GetBuildNumber()); } }
        public IList<SourceItem> Query(string root)
        { lock (queryGate) { try { return QueryCore(root); } finally { Everything_Reset(); } } }
        private IList<SourceItem> QueryCore(string root)
        {
            root = System.IO.Path.GetFullPath(root).TrimEnd('\\') + "\\";
            if (!Everything_IsDBLoaded()) throw new IOException("Everything 未安装、未运行或索引尚未加载。请先安装并运行 Everything，等待索引加载完成后刷新文件列表。");
            Everything_SetSearchW("file: \"" + root + "\"");
            Everything_SetRequestFlags(FileListRequestFlags);
            Everything_SetSort(1);
            if (!Everything_QueryW(true)) throw new IOException("Everything 查询失败（错误码：" + Everything_GetLastError() + "）。请确认已安装并运行 Everything，等待索引加载完成后刷新文件列表。");
            List<SourceItem> items = new List<SourceItem>();
            StringBuilder path = new StringBuilder(InitialPathCapacity);uint count=Everything_GetNumResults();
            Func<uint,StringBuilder,uint,uint> readPath=Everything_GetResultFullPathNameW;
            for (uint i = 0; i < count; i++)
            {
                string full = ReadResultPath(i,path,readPath);
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                long size, modified;
                if (!Everything_GetResultSize(i, out size) || !Everything_GetResultDateModified(i, out modified))
                    throw new IOException("Everything 未返回文件大小或修改时间，请等待索引加载完成后刷新文件列表。");
                items.Add(new SourceItem { Path = full, ArchivePath = "share\\" + full.Substring(root.Length), Size = size,
                    Modified = modified });
            }
            return items;
        }
        private static string ReadResultPath(uint index,StringBuilder shortPath,Func<uint,StringBuilder,uint,uint> read)
        {
            shortPath.Clear();uint copied=read(index,shortPath,(uint)shortPath.Capacity);
            if(copied==0)throw new IOException("Everything 未返回有效的完整路径，保留上次文件列表。");
            if(copied<shortPath.Capacity-1)return shortPath.ToString();
            // A full buffer may be truncated or exactly full. Ask for its actual size
            // before allocating; the normal 260-character buffer stays small for the
            // next file even after a very long path.
            uint needed=read(index,null,0);
            if(needed>=MaximumPathCapacity)throw new PathTooLongException("索引路径超过 Windows 路径长度上限，保留上次文件列表。");
            if(needed<copied||needed==0)throw new IOException("Everything 返回的路径长度无效，保留上次文件列表。");
            if(needed==copied)return shortPath.ToString();
            var fullPath=new StringBuilder((int)needed+1);uint received=read(index,fullPath,(uint)fullPath.Capacity);
            if(received!=needed||fullPath.Length!=needed)throw new IOException("Everything 返回的路径不完整，保留上次文件列表。");
            return fullPath.ToString();
        }
    }
}
