using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Backup.Core
{
    public static class ArchiveBatchPlanner
    {
        public static IList<IList<SourceItem>> Create(IList<SourceItem> sources, long limitBytes)
        {
            if (limitBytes <= 0 || sources == null) throw new ArgumentException("Sources and a positive size limit required.");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var archivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SourceItem source in sources)
                if (source == null || source.Size < 0 || !paths.Add(Path.GetFullPath(source.Path)) || !archivePaths.Add(source.ArchivePath))
                    throw new ArgumentException("Duplicate source/archive path or invalid source.");
            var batches = new List<IList<SourceItem>>();
            var current = new List<SourceItem>(); long currentBytes = 0;
            foreach (SourceItem source in sources.OrderBy(x => Path.GetDirectoryName(x.ArchivePath), StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.ArchivePath, StringComparer.OrdinalIgnoreCase))
            {
                if (source.Size > limitBytes)
                {
                    // Keep the pending small-file batch so small files surrounding a large file stay together.
                    batches.Add(new[] { source }); continue;
                }
                if (current.Count != 0 && currentBytes > limitBytes - source.Size)
                { batches.Add(current); current = new List<SourceItem>(); currentBytes = 0; }
                current.Add(source); currentBytes += source.Size;
            }
            if (current.Count != 0) batches.Add(current);
            return batches;
        }
    }
}
