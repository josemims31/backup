using System;
using System.Collections.Generic;
using System.Linq;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        // Identity metadata only: version databases have already been read once
        // by the check. Never reuse its references after a version was changed.
        private string ReviewReferenceIdentity()
        {
            return string.Join("\n", Catalog.GetNodes().Concat(Catalog.GetLocalBackupNodes()).Select(node => node.TaskId + ":" + HistoricalNodeKey(node)).OrderBy(value => value, StringComparer.Ordinal));
        }

        private HashSet<long> ReviewedUnreferencedInterrupted(AllBackupCheckResult review)
        {
            if (review.ReferencedArchives == null || review.ReferenceIdentity != ReviewReferenceIdentity())
                return new HashSet<long>();
            return new HashSet<long>(Catalog.InterruptedArchives(IntegrityTaskIds(review.TaskIds)).Where(archive => !review.ReferencedArchives.Contains(archive.ArchiveId)).Select(archive => archive.ArchiveId));
        }

        private bool UnregisterReviewedInterrupted(AllBackupCheckResult review)
        {
            if (review.ReferencedArchives == null || review.ReferenceIdentity != ReviewReferenceIdentity())
                return false;
            var removed = Catalog.UnregisterUnreferencedInterruptedArchives(review.ReferencedArchives, IntegrityTaskIds(review.TaskIds));
            if (removed.Count == 0)
                return false;
            review.Findings.RemoveAll(finding => removed.Contains(finding.ArchiveId));
            review.ObsoleteIds.ExceptWith(removed);
            return true;
        }
    }
}
