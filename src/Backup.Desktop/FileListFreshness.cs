using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Backup.Desktop
{
    public sealed partial class MainWindowController
    {
        private readonly SourceReadTracker sourceReads = new SourceReadTracker();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> scheduledReadGenerations = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        private long sourceReadGeneration
        {
            get
            {
                return sourceReads.Generation;
            }
        }

        private void RecordSourceReadStates(IList<RefreshedFolder> results)
        {
            sourceReads.Record(results);
        }

        private void ValidateSourceReadStates(IEnumerable<string> ids)
        {
            sourceReads.Validate(Workspace.Sources, ids, loaded.ContainsKey);
        }

        private async Task EnsureTaskFileListsAsync(IEnumerable<string> taskIds, long acceptedGeneration)
        {
            await WaitForFileListsAsync();
            var tasks = taskIds == null ? null : new HashSet<string>(taskIds);
            var ids = tasks == null 
                ? Workspace.Sources.Where(source => !source.Deleted).Select(source => source.Id).ToList() 
                : Workspace.Tasks.Where(task => !task.Deleted && tasks.Contains(task.Id)).SelectMany(task => task.SourceIds)
                .Distinct()
                .ToList();
            var refresh = sourceReads.Required(Workspace.Sources.Where(s => !s.Deleted && ids.Contains(s.Id)), acceptedGeneration);
            if (refresh.Count > 0)
                await RequestFileRefreshAsync(refresh);
            if (operations.Current != null)
                operations.Current.Cancellation.Token.ThrowIfCancellationRequested();
        // A failed read keeps the old UI data, but cannot certify a complete backup.
        // Each task validates its own sources so another healthy task can still run.
        }
    }
}
