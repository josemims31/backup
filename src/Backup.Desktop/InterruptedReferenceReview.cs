using System;
using System.Collections.Generic;
using System.Linq;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        // Version identities invalidate previously read reference evidence without
        // opening any version database during an integrity check.
        private string ReviewReferenceIdentity()
        {
            return string.Join("\n", Catalog.GetNodes().Concat(Catalog.GetLocalBackupNodes()).Select(node => node.TaskId + ":" + HistoricalNodeKey(node)).OrderBy(value => value, StringComparer.Ordinal));
        }

    }
}
