using System;
using System.Runtime.InteropServices;

namespace Backup.Core
{
    // Interface layout follows the public 7-Zip SDK headers. No COM registration is used.
    [ComVisible(true), Guid("23170F69-40C1-278A-0000-000300010000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISequentialInStream
    {
        [PreserveSig]
        int Read(IntPtr data, uint size, IntPtr processedSize);
    }

    [ComVisible(true), Guid("23170F69-40C1-278A-0000-000300020000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISequentialOutStream
    {
        [PreserveSig]
        int Write(IntPtr data, uint size, IntPtr processedSize);
    }

    [ComVisible(true), Guid("23170F69-40C1-278A-0000-000300040000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOutStream
    {
        [PreserveSig]
        int Write(IntPtr data, uint size, IntPtr processedSize);
        [PreserveSig]
        int Seek(long offset, uint origin, IntPtr newPosition);
        [PreserveSig]
        int SetSize(ulong size);
    }

    [ComVisible(true), Guid("23170F69-40C1-278A-0000-000600800000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IArchiveUpdateCallback
    {
        [PreserveSig]
        int SetTotal(ulong total);
        [PreserveSig]
        int SetCompleted(IntPtr completed);
        [PreserveSig]
        int GetUpdateItemInfo(uint index, out int newData, out int newProps, out uint indexInArchive);
        [PreserveSig]
        int GetProperty(uint index, uint property, IntPtr value);
        [PreserveSig]
        int GetStream(uint index, [MarshalAs(UnmanagedType.Interface)] out ISequentialInStream stream);
        [PreserveSig]
        int SetOperationResult(int result);
    }

    [ComVisible(true), Guid("23170F69-40C1-278A-0000-000500110000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICryptoGetTextPassword2
    {
        [PreserveSig]
        int CryptoGetTextPassword2(out int defined, [MarshalAs(UnmanagedType.BStr)] out string password);
    }

    [ComImport, Guid("23170F69-40C1-278A-0000-000600A00000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOutArchive
    {
        [PreserveSig]
        int UpdateItems([MarshalAs(UnmanagedType.Interface)] ISequentialOutStream stream, uint items, [MarshalAs(UnmanagedType.Interface)] IArchiveUpdateCallback callback);
        [PreserveSig]
        int GetFileTimeType(out uint type);
    }

    [ComImport, Guid("23170F69-40C1-278A-0000-000600030000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISetProperties
    {
        [PreserveSig]
        int SetProperties(IntPtr names, IntPtr values, uint count);
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PropVariant
    {
        [FieldOffset(0)]
        public ushort Type;
        [FieldOffset(8)]
        public ulong Number;
        [FieldOffset(8)]
        public IntPtr Pointer;
        public static PropVariant Text(string value)
        {
            return new PropVariant
            {
                Type = 8,
                Pointer = Marshal.StringToBSTR(value)
            };
        }

        public static PropVariant Bool(bool value)
        {
            return new PropVariant
            {
                Type = 11,
                Number = value ? 65535UL : 0
            };
        }

        public static PropVariant UInt(uint value)
        {
            return new PropVariant
            {
                Type = 19,
                Number = value
            };
        }

        public static PropVariant ULong(ulong value)
        {
            return new PropVariant
            {
                Type = 21,
                Number = value
            };
        }

        public static PropVariant Time(long value)
        {
            return new PropVariant
            {
                Type = 64,
                Number = (ulong)value
            };
        }
    }

    internal static class Native
    {
        private static readonly System.Collections.Generic.Dictionary<string, IntPtr> archiveLibraries = new System.Collections.Generic.Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        internal static IntPtr LoadArchiveLibrary(string path)
        {
            path = System.IO.Path.GetFullPath(path);
            lock (archiveLibraries)
            {
                IntPtr module;
                if (!archiveLibraries.TryGetValue(path, out module))
                {
                    module = LoadLibrary(path);
                    if (module == IntPtr.Zero)
                        throw new System.IO.IOException("Cannot load 7-Zip DLL: " + Marshal.GetLastWin32Error());
                    archiveLibraries.Add(path, module);
                }

                return module;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        internal static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("ole32.dll")]
        internal static extern int PropVariantClear(IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int CreateObject(ref Guid classId, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IOutArchive archive);
        internal static void Check(int hr)
        {
            if (hr != 0)
                Marshal.ThrowExceptionForHR(hr);
        }
    }
}
