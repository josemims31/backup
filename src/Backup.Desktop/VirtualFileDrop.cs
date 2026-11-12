using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using FILETIME=System.Runtime.InteropServices.ComTypes.FILETIME;
namespace Backup.Desktop
{
    // Only drag intent is exchanged with Explorer. No file stream is supplied: the
    // native drag is cancelled on mouse release and our normal downloader owns the I/O.
    [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
    public sealed class VirtualFileDrop:System.Runtime.InteropServices.ComTypes.IDataObject
    {
        [StructLayout(LayoutKind.Sequential)]public struct DropPoint {public int X,Y;public IntPtr Window;}
        [StructLayout(LayoutKind.Sequential)]private struct ScreenPoint {public int X,Y;}
        [DllImport("user32.dll")]private static extern bool GetCursorPos(out ScreenPoint point);
        [DllImport("user32.dll")]private static extern IntPtr WindowFromPoint(ScreenPoint point);
        [DllImport("user32.dll")]private static extern IntPtr GetAncestor(IntPtr window,uint flag);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern uint RegisterClipboardFormat(string name);
        [DllImport("user32.dll")]private static extern IntPtr LoadCursor(IntPtr instance,IntPtr id);
        [DllImport("user32.dll")]private static extern IntPtr SetCursor(IntPtr cursor);
        [DllImport("kernel32.dll")]private static extern IntPtr GlobalAlloc(uint flags,UIntPtr bytes);
        [DllImport("kernel32.dll")]private static extern IntPtr GlobalLock(IntPtr handle);
        [DllImport("kernel32.dll")]private static extern bool GlobalUnlock(IntPtr handle);
        [DllImport("kernel32.dll")]private static extern IntPtr GlobalFree(IntPtr handle);
        [DllImport("ole32.dll")]private static extern int OleInitialize(IntPtr reserved);
        [DllImport("ole32.dll")]private static extern void OleUninitialize();
        [DllImport("ole32.dll")]private static extern int DoDragDrop([MarshalAs(UnmanagedType.Interface)]System.Runtime.InteropServices.ComTypes.IDataObject data,[MarshalAs(UnmanagedType.Interface)]IDropSource source,int effects,out int effect);
        [ComVisible(true),Guid("00000121-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDropSource{[PreserveSig]int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)]bool escape,int keys);[PreserveSig]int GiveFeedback(int effect);}
        [ComVisible(true),ClassInterface(ClassInterfaceType.None)]private sealed class Source:IDropSource
        {
            internal Func<bool> Cancelled;internal DropPoint? Target;
            public int QueryContinueDrag(bool escape,int keys){if(escape||Cancelled!=null&&Cancelled())return 0x40101;if((keys&1)==0){ScreenPoint point;if(GetCursorPos(out point))Target=new DropPoint{X=point.X,Y=point.Y,Window=GetAncestor(WindowFromPoint(point),2)};return 0x40101;}return 0;}
            public int GiveFeedback(int effect){SetCursor(LoadCursor(IntPtr.Zero,new IntPtr(32649)));return 0;}
        }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct Descriptor
        {public uint Flags;public Guid ClassId;public int Width,Height,X,Y;public uint Attributes;public FILETIME Created,Accessed,Modified;public uint SizeHigh,SizeLow;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Name;}
        private readonly BackupHistoryNode[] nodes;private readonly short group=unchecked((short)RegisterClipboardFormat("FileGroupDescriptorW"));
        public VirtualFileDrop(IEnumerable<BackupHistoryNode> selected){nodes=selected.ToArray();}
        public DropPoint? Drag(Func<bool> cancelled)
        {int initialized=OleInitialize(IntPtr.Zero);if(initialized<0)Marshal.ThrowExceptionForHR(initialized);try{var source=new Source{Cancelled=cancelled};int effect;int result=DoDragDrop(this,source,1,out effect);if(result<0)Marshal.ThrowExceptionForHR(result);return source.Target;}finally{OleUninitialize();}}
        public void GetData(ref FORMATETC format,out STGMEDIUM medium)
        {
            medium=new STGMEDIUM();int allowed=QueryGetData(ref format);if(allowed!=0)Marshal.ThrowExceptionForHR(allowed);int size=Marshal.SizeOf(typeof(Descriptor));IntPtr handle=GlobalAlloc(2,new UIntPtr((uint)checked(4+size*nodes.Length)));if(handle==IntPtr.Zero)throw new OutOfMemoryException();
            try{IntPtr memory=GlobalLock(handle);if(memory==IntPtr.Zero)throw new OutOfMemoryException();try{Marshal.WriteInt32(memory,nodes.Length);for(int index=0;index<nodes.Length;index++){var node=nodes[index];string name=new string((node.TreeName??node.Name??"下载").Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c).Take(259).ToArray());Marshal.StructureToPtr(new Descriptor{Flags=0x80000004u,Attributes=node.IsDirectory?16u:128u,Name=name},IntPtr.Add(memory,4+size*index),false);}}finally{GlobalUnlock(handle);}medium.tymed=TYMED.TYMED_HGLOBAL;medium.unionmember=handle;handle=IntPtr.Zero;}finally{if(handle!=IntPtr.Zero)GlobalFree(handle);}
        }
        public int QueryGetData(ref FORMATETC format){return format.cfFormat==group&&(format.tymed&TYMED.TYMED_HGLOBAL)!=0&&format.dwAspect==DVASPECT.DVASPECT_CONTENT?0:unchecked((int)0x80040064);}
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction){if(direction!=DATADIR.DATADIR_GET)throw new NotSupportedException();return new Formats(new FORMATETC{cfFormat=group,tymed=TYMED.TYMED_HGLOBAL,dwAspect=DVASPECT.DVASPECT_CONTENT,lindex=-1});}
        public void GetDataHere(ref FORMATETC f,ref STGMEDIUM m){throw new NotSupportedException();}public int GetCanonicalFormatEtc(ref FORMATETC input,out FORMATETC output){output=input;output.ptd=IntPtr.Zero;return 0x40000;}
        public void SetData(ref FORMATETC f,ref STGMEDIUM m,bool release){throw new NotSupportedException();}public int DAdvise(ref FORMATETC f,ADVF flags,IAdviseSink sink,out int id){id=0;return unchecked((int)0x80040003);}public void DUnadvise(int id){throw new NotSupportedException();}public int EnumDAdvise(out IEnumSTATDATA result){result=null;return unchecked((int)0x80040003);}
        [ComVisible(true),ClassInterface(ClassInterfaceType.None)]private sealed class Formats:IEnumFORMATETC
        {private readonly FORMATETC value;private bool read;internal Formats(FORMATETC f){value=f;}public int Next(int count,FORMATETC[] output,int[] fetched){int n=!read&&count>0?1:0;if(n>0){output[0]=value;read=true;}if(fetched!=null)fetched[0]=n;return n==count?0:1;}public int Skip(int count){bool had=read;read=true;return !had&&count==1?0:1;}public int Reset(){read=false;return 0;}public void Clone(out IEnumFORMATETC copy){copy=new Formats(value){read=read};}}
    }
}
