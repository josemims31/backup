using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
namespace Backup.Core
{
    [ComVisible(true),Guid("23170F69-40C1-278A-0000-000300030000"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInStream { [PreserveSig]int Read(IntPtr data,uint size,IntPtr processed);[PreserveSig]int Seek(long offset,uint origin,IntPtr position); }
    [ComVisible(true),Guid("23170F69-40C1-278A-0000-000500100000"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICryptoGetTextPassword {[PreserveSig]int CryptoGetTextPassword([MarshalAs(UnmanagedType.BStr)]out string password);}
    [ComVisible(true),Guid("23170F69-40C1-278A-0000-000600100000"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IArchiveOpenCallback {[PreserveSig]int SetTotal(IntPtr files,IntPtr bytes);[PreserveSig]int SetCompleted(IntPtr files,IntPtr bytes);}
    [ComVisible(true),Guid("23170F69-40C1-278A-0000-000600200000"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IArchiveExtractCallback {
        [PreserveSig]int SetTotal(ulong total);[PreserveSig]int SetCompleted(IntPtr completed);
        [PreserveSig]int GetStream(uint index,[MarshalAs(UnmanagedType.Interface)]out ISequentialOutStream stream,int mode);
        [PreserveSig]int PrepareOperation(int mode);[PreserveSig]int SetOperationResult(int result);
    }
    [ComImport,Guid("23170F69-40C1-278A-0000-000600600000"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInArchive {
        [PreserveSig]int Open([MarshalAs(UnmanagedType.Interface)]IInStream stream,IntPtr max,[MarshalAs(UnmanagedType.Interface)]IArchiveOpenCallback callback);
        [PreserveSig]int Close();[PreserveSig]int GetNumberOfItems(out uint count);[PreserveSig]int GetProperty(uint index,uint property,IntPtr value);
        [PreserveSig]int Extract(IntPtr indices,uint count,int test,[MarshalAs(UnmanagedType.Interface)]IArchiveExtractCallback callback);
        [PreserveSig]int GetArchiveProperty(uint property,IntPtr value);[PreserveSig]int GetNumberOfProperties(out uint count);
        [PreserveSig]int GetPropertyInfo(uint index,[MarshalAs(UnmanagedType.BStr)]out string name,out uint property,out ushort type);
        [PreserveSig]int GetNumberOfArchiveProperties(out uint count);
        [PreserveSig]int GetArchivePropertyInfo(uint index,[MarshalAs(UnmanagedType.BStr)]out string name,out uint property,out ushort type);
    }
    [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
    public sealed class ArchiveInputStream:IInStream {
        private readonly Stream stream;private readonly byte[] buffer=new byte[1024*1024];public Exception Error;
        public ArchiveInputStream(Stream value){stream=value;}
        public int Read(IntPtr data,uint size,IntPtr processed){try{int count=stream.Read(buffer,0,(int)Math.Min(size,(uint)buffer.Length));Marshal.Copy(buffer,0,data,count);if(processed!=IntPtr.Zero)Marshal.WriteInt32(processed,count);return 0;}catch(Exception e){Error=e;return Marshal.GetHRForException(e);}}
        public int Seek(long offset,uint origin,IntPtr position){try{long value=stream.Seek(offset,(SeekOrigin)origin);if(position!=IntPtr.Zero)Marshal.WriteInt64(position,value);return 0;}catch(Exception e){Error=e;return Marshal.GetHRForException(e);}}
    }
    [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
    public sealed class ArchiveFileOutput:ISequentialOutStream,IDisposable {
        private Stream stream;private byte[] buffer=new byte[64*1024];private readonly bool leaveOpen;private readonly long maximum;private readonly CancellationToken cancellation;public Exception Error;public long Bytes;public Action<CancellationToken> PauseCheckpoint;
        public ArchiveFileOutput(string path,long limit=long.MaxValue,CancellationToken token=default(CancellationToken)):this(path,limit,token,false){}
        public ArchiveFileOutput(string path,long limit,CancellationToken token,bool overwrite){stream=OpenFile(path,overwrite);maximum=limit;cancellation=token;}
        public static void EnsureDirectory(string path)
        {
            string full=Path.GetFullPath(path),root=Path.GetPathRoot(full);
            if(full.StartsWith(@"\\",StringComparison.Ordinal)&&!Directory.Exists(root))throw new IOException("NAS 共享目录不存在或无访问权限，请先创建并开放该共享目录："+root);
            if(!Directory.Exists(full))Directory.CreateDirectory(full);
        }
        public static FileStream OpenFile(string path,bool overwrite)
        {
            EnsureDirectory(Path.GetDirectoryName(path));
            if(overwrite)try{var attributes=File.GetAttributes(path);if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("目标文件是链接，无法直接覆盖："+path);if((attributes&FileAttributes.ReadOnly)!=0)File.SetAttributes(path,attributes&~FileAttributes.ReadOnly);}catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
            return new FileStream(path,overwrite?FileMode.Create:FileMode.CreateNew,FileAccess.Write,FileShare.None);
        }
        public ArchiveFileOutput(Stream target,long limit,CancellationToken token){stream=target;leaveOpen=true;maximum=limit;cancellation=token;}
        public int Write(IntPtr data,uint size,IntPtr processed){try{cancellation.ThrowIfCancellationRequested();if(Bytes+size>maximum)throw new IOException("解压内容超过数据库清单长度。");int done=0;while(done<size){cancellation.ThrowIfCancellationRequested();if(PauseCheckpoint!=null)PauseCheckpoint(cancellation);int n=(int)Math.Min(size-done,(uint)buffer.Length);Marshal.Copy(IntPtr.Add(data,done),buffer,0,n);stream.Write(buffer,0,n);done+=n;Bytes+=n;}if(processed!=IntPtr.Zero)Marshal.WriteInt32(processed,done);return 0;}catch(Exception e){Error=e;return Marshal.GetHRForException(e);}}
        public void Dispose(){try{if(stream!=null&&!leaveOpen)stream.Dispose();}finally{stream=null;buffer=null;}}
    }
    [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
    public sealed class ArchiveReadCallback:IArchiveOpenCallback,IArchiveExtractCallback,ICryptoGetTextPassword,IDisposable {
        private readonly string password;public Dictionary<uint,string> Targets=new Dictionary<uint,string>();public Dictionary<uint,Stream> StreamTargets=new Dictionary<uint,Stream>();public Dictionary<uint,long> Sizes=new Dictionary<uint,long>();public CancellationToken Cancellation;public Dictionary<uint,ArchiveFileOutput> Outputs=new Dictionary<uint,ArchiveFileOutput>();public Exception Error;private ArchiveFileOutput current;
        public bool Completed,Overwrite;public Action<CancellationToken> PauseCheckpoint;
        public ArchiveReadCallback(string value){password=value;}
        public int SetTotal(IntPtr a,IntPtr b){return 0;}public int SetCompleted(IntPtr a,IntPtr b){return 0;}public int SetTotal(ulong n){return 0;}public int SetCompleted(IntPtr n){return 0;}public int CryptoGetTextPassword(out string value){value=password;return 0;}public int PrepareOperation(int mode){return 0;}
        public int GetStream(uint index,out ISequentialOutStream output,int mode){output=null;try{Cancellation.ThrowIfCancellationRequested();if(PauseCheckpoint!=null)PauseCheckpoint(Cancellation);string path;Stream target;if(mode==0&&Targets.TryGetValue(index,out path)){current=StreamTargets.TryGetValue(index,out target)?new ArchiveFileOutput(target,Sizes[index],Cancellation):new ArchiveFileOutput(path,Sizes[index],Cancellation,Overwrite);current.PauseCheckpoint=PauseCheckpoint;Outputs.Add(index,current);output=current;}return 0;}catch(Exception e){Error=e;return Marshal.GetHRForException(e);}}
        public int SetOperationResult(int result){if(current!=null){current.Dispose();if(current.Error!=null)Error=current.Error;current=null;}if(result!=0)Error=new IOException("7-Zip 解压校验失败："+result);return Error==null?0:unchecked((int)0x80004005);}
        public void Dispose(){foreach(var output in Outputs.Values)output.Dispose();if(!Completed&&StreamTargets.Count==0)foreach(uint index in Outputs.Keys)try{File.Delete(Targets[index]);}catch(IOException){}catch(UnauthorizedAccessException){}}
    }
    public sealed class SevenZipArchiveReader {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int CreateInput(ref Guid cls,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out IInArchive archive);
        private readonly CreateInput create;
        public Action<CancellationToken> PauseCheckpoint;
        public SevenZipArchiveReader(string dll){IntPtr module=Native.LoadArchiveLibrary(dll);if(module==IntPtr.Zero)throw new IOException("无法载入 7-Zip。");create=(CreateInput)Marshal.GetDelegateForFunctionPointer(Native.GetProcAddress(module,"CreateObject"),typeof(CreateInput));}
        public void Extract(string archivePath,string password,IDictionary<string,string> targets,IDictionary<string,long> sizes){
            using(var file=File.OpenRead(archivePath))Extract(file,password,targets,sizes);
        }
        public void Extract(Stream file,string password,IDictionary<string,string> targets,IDictionary<string,long> sizes){ExtractCore(file,password,targets,sizes,null,CancellationToken.None);}
        public void Extract(Stream file,string password,IDictionary<string,string> targets,IDictionary<string,long> sizes,CancellationToken cancellation){ExtractCore(file,password,targets,sizes,null,cancellation);}
        public void Extract(Stream file,string password,IDictionary<string,string> targets,IDictionary<string,long> sizes,CancellationToken cancellation,bool overwrite){ExtractCore(file,password,targets,sizes,null,cancellation,overwrite);}
        public void ExtractToStreams(Stream file,string password,IDictionary<string,Stream> outputs,IDictionary<string,long> sizes,CancellationToken cancellation)
        {ExtractCore(file,password,outputs.ToDictionary(p=>p.Key,p=>p.Key,StringComparer.OrdinalIgnoreCase),sizes,outputs,cancellation);}
        public ReadSession OpenSession(Stream file,string password,CancellationToken cancellation=default(CancellationToken))
        {return CreateSession(file,password,cancellation,null);}
        private ReadSession CreateSession(Stream file,string password,CancellationToken cancellation,ISet<string> selected)
        {
            if(file==null||!file.CanRead||!file.CanSeek)throw new ArgumentException("Archive input must be readable and seekable.");
            cancellation.ThrowIfCancellationRequested();Guid cls=new Guid("23170F69-40C1-278A-1000-000110070000"),iid=typeof(IInArchive).GUID;IInArchive archive;Native.Check(create(ref cls,ref iid,out archive));
            return new ReadSession(archive,file,password,PauseCheckpoint,cancellation,selected);
        }
        private void ExtractCore(Stream file,string password,IDictionary<string,string> targets,IDictionary<string,long> sizes,IDictionary<string,Stream> streams,CancellationToken cancellation,bool overwrite=false)
        {
            var expected=sizes.ToDictionary(pair=>targets[pair.Key],pair=>pair.Value,StringComparer.OrdinalIgnoreCase);
            using(var session=CreateSession(file,password,cancellation,new HashSet<string>(targets.Keys,StringComparer.OrdinalIgnoreCase)))session.Extract(targets,sizes,streams,cancellation,overwrite,expected);
        }
        // A caller may retain this session for several files from one archive.
        // The session is exclusive; callers must never extract concurrently from it.
        public sealed class ReadSession:IDisposable
        {
            private IInArchive archive;private readonly ArchiveInputStream input;private readonly ArchiveReadCallback opening;private readonly string password;private readonly Action<CancellationToken> pause;
            private readonly Dictionary<string,List<uint>> entries=new Dictionary<string,List<uint>>(StringComparer.OrdinalIgnoreCase);
            internal ReadSession(IInArchive value,Stream file,string secret,Action<CancellationToken> checkpoint,CancellationToken cancellation,ISet<string> selected)
            {
                archive=value;password=secret;pause=checkpoint;input=new ArchiveInputStream(file);opening=new ArchiveReadCallback(password){Cancellation=cancellation,PauseCheckpoint=pause};
                try{Native.Check(archive.Open(input,IntPtr.Zero,opening));uint count;Native.Check(archive.GetNumberOfItems(out count));IntPtr prop=Marshal.AllocHGlobal(24);
                    try{for(uint i=0;i<count;i++){cancellation.ThrowIfCancellationRequested();Marshal.StructureToPtr(new PropVariant(),prop,false);Native.Check(archive.GetProperty(i,3,prop));var property=(PropVariant)Marshal.PtrToStructure(prop,typeof(PropVariant));string path=property.Type==8?Marshal.PtrToStringBSTR(property.Pointer):"";Native.PropVariantClear(prop);if(selected!=null&&!selected.Contains(path))continue;List<uint> indices;if(!entries.TryGetValue(path,out indices))entries[path]=indices=new List<uint>();indices.Add(i);}}finally{Marshal.FreeHGlobal(prop);}
                }catch{Dispose();throw;}
            }
            public void ExtractToStreams(IDictionary<string,Stream> outputs,IDictionary<string,long> sizes,CancellationToken cancellation)
            {Extract(outputs.ToDictionary(pair=>pair.Key,pair=>pair.Key,StringComparer.OrdinalIgnoreCase),sizes,outputs,cancellation,false);}
            internal void Extract(IDictionary<string,string> targets,IDictionary<string,long> sizes,IDictionary<string,Stream> streams,CancellationToken cancellation,bool overwrite,IDictionary<string,long> expected=null)
            {
                if(archive==null)throw new ObjectDisposedException("ReadSession");cancellation.ThrowIfCancellationRequested();if(expected==null)expected=sizes.ToDictionary(pair=>targets[pair.Key],pair=>pair.Value,StringComparer.OrdinalIgnoreCase);
                using(var callback=new ArchiveReadCallback(password){Cancellation=cancellation,Overwrite=overwrite,PauseCheckpoint=pause}){
                    foreach(var target in targets){List<uint> found;if(!entries.TryGetValue(target.Key,out found))throw new IOException("压缩包中缺少所选文件。");if(found.Count!=1)throw new IOException("归档中存在重复路径。");uint index=found[0];callback.Targets.Add(index,target.Value);callback.Sizes.Add(index,sizes[target.Key]);if(streams!=null)callback.StreamTargets.Add(index,streams[target.Key]);}
                    var indices=callback.Targets.Keys.OrderBy(index=>index).ToArray();IntPtr array=Marshal.AllocHGlobal(indices.Length*4);input.Error=null;
                    try{for(int i=0;i<indices.Length;i++)Marshal.WriteInt32(array,i*4,(int)indices[i]);int hr=archive.Extract(array,(uint)indices.Length,0,callback);if(callback.Error!=null)throw callback.Error;if(input.Error!=null)throw input.Error;Native.Check(hr);}finally{Marshal.FreeHGlobal(array);}
                    foreach(var pair in callback.Targets){ArchiveFileOutput output;if(!callback.Outputs.TryGetValue(pair.Key,out output)||output.Bytes!=expected[pair.Value])throw new IOException("解压文件长度不一致。");}
                    cancellation.ThrowIfCancellationRequested();callback.Completed=true;
                }
            }
            public void Dispose(){var value=archive;if(value==null)return;archive=null;try{value.Close();}finally{Marshal.FinalReleaseComObject(value);opening.Dispose();entries.Clear();}}
        }
    }
}
