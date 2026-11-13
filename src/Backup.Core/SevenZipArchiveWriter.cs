using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Backup.Core
{
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class ArchiveUpdateCallback : IArchiveUpdateCallback, ICryptoGetTextPassword2, IDisposable
    {
        private readonly IList<SourceItem> sources;
        private readonly string password;
        private SourceReadStream activeReader;
        public BackupControl Control;
        public Exception LastError;
        public ArchiveUpdateCallback(IList<SourceItem> sources, string password)
        {
            this.sources = sources;
            this.password = password;
        }

        public int SetTotal(ulong total)
        {
            return 0;
        }

        public int SetCompleted(IntPtr completed)
        {
            try
            {
                if (Control != null)
                    Control.SourceCheckpoint();
                return 0;
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/SevenZipArchiveWriter.cs:SetCompleted", error);
                LastError = error;
                return Marshal.GetHRForException(error);
            }
        }

        public int GetUpdateItemInfo(uint index, out int newData, out int newProps, out uint oldIndex)
        {
            newData = 1;
            newProps = 1;
            oldIndex = uint.MaxValue;
            return 0;
        }

        public int GetProperty(uint index, uint property, IntPtr value)
        {
            try
            {
                SourceItem item = sources[(int)index];
                PropVariant prop = new PropVariant();
                if (property == 3)
                    prop = PropVariant.Text(item.StoredPath);
                else if (property == 6)
                    prop = PropVariant.Bool(false);
                else if (property == 7)
                    prop = PropVariant.ULong((ulong)item.Size);
                else if (property == 12)
                    prop = PropVariant.Time(item.Modified);
                Marshal.StructureToPtr(prop, value, false);
                return 0;
            }
            catch (Exception e)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/SevenZipArchiveWriter.cs:GetProperty", e);
                LastError = e;
                return Marshal.GetHRForException(e);
            }
        }

        public int GetStream(uint index, out ISequentialInStream stream)
        {
            stream = null;
            try
            {
                if (Control != null)
                    Control.SourceCheckpoint();
                SourceItem item = sources[(int)index];
                if (item.Reader != null || item.ContentOpenCount != 0)
                    throw new IOException("Source reopen forbidden: " + item.Path);
                item.CheckUnchanged();
                if (activeReader != null)
                    activeReader.Dispose();
                item.Reader = new SourceReadStream(item);
                activeReader = item.Reader;
                stream = item.Reader;
                return 0;
            }
            catch (Exception e)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/SevenZipArchiveWriter.cs:GetStream", e);
                LastError = e;
                return Marshal.GetHRForException(e);
            }
        }

        public int SetOperationResult(int result)
        {
            try
            {
                if (activeReader != null)
                {
                    activeReader.Dispose();
                    activeReader = null;
                }

                return result == 0 ? 0 : unchecked((int)0x80004005);
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/SevenZipArchiveWriter.cs:SetOperationResult", error);
                LastError = error;
                return Marshal.GetHRForException(error);
            }
        }

        public int CryptoGetTextPassword2(out int defined, out string value)
        {
            defined = 1;
            value = password;
            return 0;
        }

        public void Dispose()
        {
            foreach (SourceItem item in sources)
            {
                if (item.Reader != null)
                    item.Reader.Dispose();
                item.ReadObserver = null;
                item.Control = null;
            }
        }
    }

    public sealed class SevenZipArchiveWriter
    {
        private readonly IntPtr library;
        private readonly Native.CreateObject create;
        public SevenZipArchiveWriter(string dll)
        {
            library = Native.LoadArchiveLibrary(dll);
            if (library == IntPtr.Zero)
                throw new IOException("Cannot load 7-Zip DLL: " + Marshal.GetLastWin32Error());
            IntPtr address = Native.GetProcAddress(library, "CreateObject");
            if (address == IntPtr.Zero)
                throw new IOException("7-Zip CreateObject export missing.");
            create = (Native.CreateObject)Marshal.GetDelegateForFunctionPointer(address, typeof(Native.CreateObject));
        }

        private static void SetProperties(IOutArchive archive, string method)
        {
            string[] names =
            {
                "0",
                "x",
                "s",
                "he",
                "mt"
            };
            PropVariant[] values =
            {
                PropVariant.Text(method),
                PropVariant.UInt(1),
                PropVariant.Bool(false),
                PropVariant.Bool(true),
                PropVariant.UInt(2)
            };
            IntPtr nameArray = Marshal.AllocHGlobal(names.Length * IntPtr.Size);
            IntPtr props = Marshal.AllocHGlobal(names.Length * 24);
            try
            {
                for (int i = 0; i < names.Length; i++)
                {
                    Marshal.WriteIntPtr(nameArray, i * IntPtr.Size, Marshal.StringToCoTaskMemUni(names[i]));
                    Marshal.StructureToPtr(values[i], IntPtr.Add(props, 24 * i), false);
                }

                Native.Check(((ISetProperties)archive).SetProperties(nameArray, props, (uint)names.Length));
            }
            finally
            {
                for (int i = 0; i < names.Length; i++)
                {
                    Marshal.FreeCoTaskMem(Marshal.ReadIntPtr(nameArray, i * IntPtr.Size));
                    Native.PropVariantClear(IntPtr.Add(props, 24 * i));
                }

                Marshal.FreeHGlobal(nameArray);
                Marshal.FreeHGlobal(props);
            }
        }

        public void Write(IList<SourceItem> sources, MemoryVolumeOutput output, string password, string method, BackupControl control = null)
        {
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Password cannot be empty.");
            IOutArchive archive;
            Guid classId = new Guid("23170F69-40C1-278A-1000-000110070000");
            Guid interfaceId = typeof(IOutArchive).GUID;
            Native.Check(create(ref classId, ref interfaceId, out archive));
            try
            {
                SetProperties(archive, method);
                using (ArchiveUpdateCallback callback = new ArchiveUpdateCallback(sources, password))
                {
                    callback.Control = control;
                    foreach (var source in sources)
                        source.Control = control;
                    int hr = archive.UpdateItems(output, (uint)sources.Count, callback);
                    if (callback.LastError is OperationCanceledException)
                        throw callback.LastError;
                    if (output.LastError is OperationCanceledException)
                        throw output.LastError;
                    foreach (var source in sources)
                        if (source.Reader != null && source.Reader.Error is OperationCanceledException)
                            throw source.Reader.Error;
                    if (callback.LastError != null)
                        throw new IOException("Source callback failed.", callback.LastError);
                    if (output.LastError != null)
                        throw new IOException("Archive output failed.", output.LastError);
                    Native.Check(hr);
                }

                foreach (SourceItem source in sources)
                {
                    source.CheckUnchanged();
                    // Some 7-Zip methods do not ask for a stream for an empty file.
                    if (source.Size == 0 && source.Reader == null)
                    {
                        source.Reader = new SourceReadStream(source);
                        source.Reader.Dispose();
                    }

                    if (source.Reader == null || source.Reader.BytesRead != source.Size)
                        throw new IOException("Source did not read exactly once to completion.");
                }

                output.Complete();
            }
            finally
            {
                Marshal.FinalReleaseComObject(archive);
            }
        }
    }
}
