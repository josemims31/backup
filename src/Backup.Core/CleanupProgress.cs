using System.Collections.Generic;

namespace Backup.Core
{
    public sealed class CleanupProgress
    {
        public string Message;
        public int CompletedArchives, TotalArchives, DeletedObjects, TotalObjects;
        public long FreedBytes;
    }

    public sealed class CleanupResult
    {
        public int DeletedArchives, DeletedObjects, PendingArchives;
        public long FreedBytes;
        public List<string> Errors = new List<string>();
    }
}
