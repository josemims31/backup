using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        private static IList<FileHistoryRow> RestoreCopies(RestoreRequest request)
        {
            return new[] { request.File }.Concat(request.File.AlternativeCopies ?? new FileHistoryRow[0]).Where(file =>
                file.Path == request.File.Path && file.Size == request.File.Size && file.Modified == request.File.Modified &&
                (string.IsNullOrEmpty(request.RequiredDestinationId) || file.DestinationId == request.RequiredDestinationId) &&
                (string.IsNullOrEmpty(request.RequiredGroupId) || file.GroupId == request.RequiredGroupId) &&
                (request.ExcludedArchives == null || !request.ExcludedArchives.Contains(file.ArchiveId)))
                .OrderBy(file => file.PlacementStatus == "complete" ? 0 : 1)
                .GroupBy(file => file.ArchiveId).Select(group => group.First()).ToList();
        }

        private static RestoreRequest WithRestoreCopy(RestoreRequest request, FileHistoryRow file, ISet<long> excluded = null)
        {
            return new RestoreRequest { File = file, RelativePath = request.RelativePath, RequiredDestinationId = request.RequiredDestinationId,
                RequiredGroupId = request.RequiredGroupId, ExcludedArchives = excluded ?? request.ExcludedArchives };
        }

        private sealed class ReplicaReadStream : Stream
        {
            private readonly IList<FileHistoryRow> copies;
            private readonly Func<FileHistoryRow, Stream> open;
            private readonly CancellationToken cancellation;
            private Stream current;
            private long position;
            private int index;
            private bool disposed;
            internal ReplicaReadStream(IList<FileHistoryRow> values, Func<FileHistoryRow, Stream> factory, CancellationToken token)
            { copies = values; open = factory; cancellation = token; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (disposed) throw new ObjectDisposedException("ReplicaReadStream");
                if (buffer == null) throw new ArgumentNullException("buffer");
                if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException();
                if (count == 0) return 0;
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        if (current == null)
                        {
                            current = open(copies[index]);
                            // If a different encoding is needed after a partial read,
                            // discard the already delivered plaintext, never append it twice.
                            long skip = position;
                            var scratch = skip == 0 ? null : new byte[65536];
                            while (skip > 0)
                            {
                                cancellation.ThrowIfCancellationRequested();
                                int n = current.Read(scratch, 0, (int)Math.Min(skip, scratch.Length));
                                if (n == 0) throw new IOException("备份副本长度不一致。");
                                skip -= n;
                            }
                        }
                        int read = current.Read(buffer, offset, count);
                        if (read == 0 && position < Length) throw new IOException("备份副本未完整读取。");
                        position += read;
                        return read;
                    }
                    catch (Exception error)
                    {
                        if (!(error is IOException) && !(error is UnauthorizedAccessException)) throw;
                        cancellation.ThrowIfCancellationRequested();
                        if (++index >= copies.Count) throw;
                        if (current != null) current.Dispose();
                        current = null;
                    }
                }
            }
            protected override void Dispose(bool disposing)
            { if (!disposed && disposing && current != null) current.Dispose(); disposed = true; base.Dispose(disposing); }
            public override bool CanRead { get { return !disposed; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return copies[0].Size; } }
            public override long Position { get { return position; } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }
}
