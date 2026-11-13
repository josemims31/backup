using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Backup.Core
{
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class MemoryVolumeOutput : ISequentialOutStream, IOutStream, IDisposable
    {
        private readonly byte[] first;
        private readonly VolumeBufferPool pool;
        private readonly BackupControl control;
        private VolumeBuffer firstOwner, currentOwner;
        private byte[] current;
        private readonly int volumeSize;
        private readonly string archiveName;
        private readonly IArchiveObjectSink uploader;
        private int currentIndex = 1;
        private int currentCount;
        private long position;
        private long length;
        private bool completed;
        public long PeakVolumeBufferBytes;
        public long UploadedBeforeArchiveCompletion;
        public long BackwardSeeks;
        public readonly List<string> SeekTrace = new List<string>();
        public Exception LastError;
        public long Length
        {
            get
            {
                return length;
            }
        }

        public MemoryVolumeOutput(int volumeSize, string archiveName, IArchiveObjectSink uploader) : this(volumeSize, archiveName, uploader, null, null)
        {
        }

        public MemoryVolumeOutput(int volumeSize, string archiveName, IArchiveObjectSink uploader, VolumeBufferPool buffers, BackupControl operation)
        {
            pool = buffers;
            control = operation;
            if (volumeSize < 4096)
                throw new ArgumentException("Volume too small.");
            this.volumeSize = volumeSize;
            this.archiveName = archiveName;
            this.uploader = uploader;
            if (pool == null)
                first = new byte[volumeSize];
            else
            {
                firstOwner = pool.Rent();
                first = firstOwner.Bytes;
            }

            PeakVolumeBufferBytes = volumeSize;
        }

        private string Name(int index)
        {
            return archiveName + "." + (index + 1).ToString("D3");
        }

        private void FlushCurrent()
        {
            if (currentCount == 0)
                return;
            if (pool == null)
                uploader.Upload(Name(currentIndex), current, currentCount);
            else
            {
                var owned = currentOwner;
                currentOwner = null;
                current = null;
                ((IOwnedArchiveObjectSink)uploader).UploadOwned(Name(currentIndex), owned, currentCount);
                PeakVolumeBufferBytes = Math.Max(PeakVolumeBufferBytes, pool.PeakBytes);
            }

            if (!completed)
                UploadedBeforeArchiveCompletion += currentCount;
            currentCount = 0;
            currentIndex++;
        }

        public int Write(IntPtr data, uint size, IntPtr processed)
        {
            int done = 0;
            try
            {
                while (done < size)
                {
                    if (control != null)
                        control.SourceCheckpoint();
                    int index = checked((int)(position / volumeSize));
                    int offset = (int)(position % volumeSize);
                    int n = (int)Math.Min(size - done, volumeSize - offset);
                    byte[] target;
                    if (index == 0)
                        target = first;
                    else
                    {
                        if (index != currentIndex)
                            throw new IOException("Attempt to rewrite an already-uploaded volume.");
                        if (current == null)
                        {
                            if (pool == null)
                            {
                                current = new byte[volumeSize];
                                PeakVolumeBufferBytes += volumeSize;
                            }
                            else
                            {
                                currentOwner = pool.Rent();
                                current = currentOwner.Bytes;
                                PeakVolumeBufferBytes = Math.Max(PeakVolumeBufferBytes, pool.PeakBytes);
                            }
                        }

                        target = current;
                    }

                    Marshal.Copy(IntPtr.Add(data, done), target, offset, n);
                    done += n;
                    position += n;
                    length = Math.Max(length, position);
                    if (index != 0)
                    {
                        currentCount = Math.Max(currentCount, offset + n);
                        if (currentCount == volumeSize)
                            FlushCurrent();
                    }
                }

                if (processed != IntPtr.Zero)
                    Marshal.WriteInt32(processed, done);
                return 0;
            }
            catch (Exception e)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/MemoryVolumeOutput.cs:Write", e);
                LastError = e;
                if (processed != IntPtr.Zero)
                    Marshal.WriteInt32(processed, done);
                return Marshal.GetHRForException(e);
            }
        }

        public int Seek(long offset, uint origin, IntPtr result)
        {
            try
            {
                if (origin > 2)
                    throw new IOException("Invalid seek origin.");
                long next = checked((origin == 0 ? 0 : origin == 1 ? position : length) + offset);
                if (next < 0 || next > length)
                    throw new IOException("Out-of-range archive seek.");
                if (next < position)
                    BackwardSeeks++;
                SeekTrace.Add(position + " -> " + next + " (origin " + origin + ")");
                if (next >= volumeSize && next != length && next / volumeSize != currentIndex)
                    throw new IOException("Seek into committed volume.");
                position = next;
                if (result != IntPtr.Zero)
                    Marshal.WriteInt64(result, next);
                return 0;
            }
            catch (Exception e)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/MemoryVolumeOutput.cs:Seek", e);
                LastError = e;
                return Marshal.GetHRForException(e);
            }
        }

        public int SetSize(ulong size)
        {
            if (size == (ulong)length)
                return 0;
            LastError = new IOException("Archive resizing is unsupported; do not publish this archive.");
            return unchecked((int)0x80004001);
        }

        public void Complete()
        {
            if (completed)
                throw new InvalidOperationException("Already completed.");
            completed = true;
            if (control != null)
                control.SourceCheckpoint();
            if (length <= volumeSize)
            {
                if (pool == null)
                    uploader.Upload(archiveName, first, (int)length);
                else
                {
                    var owned = firstOwner;
                    firstOwner = null;
                    ((IOwnedArchiveObjectSink)uploader).UploadOwned(archiveName, owned, (int)length);
                }
            }
            else
            {
                FlushCurrent();
                if (pool == null)
                    uploader.Upload(Name(0), first, volumeSize);
                else
                {
                    var owned = firstOwner;
                    firstOwner = null;
                    ((IOwnedArchiveObjectSink)uploader).UploadOwned(Name(0), owned, volumeSize);
                }
            }
        }

        public void Dispose()
        {
            if (firstOwner != null)
            {
                firstOwner.Dispose();
                firstOwner = null;
            }

            if (currentOwner != null)
            {
                currentOwner.Dispose();
                currentOwner = null;
            }
        }
    }
}
