using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Backup.Desktop
{
    public sealed partial class MainWindowController
    {
        private sealed class TaskHistoryDetails
        {
            internal long Stamp, Used;
            internal Task<string> Loading;
            internal string Text;
        }

        private readonly Dictionary<BackupHistoryNode, TaskHistoryDetails> taskHistoryDetails = new Dictionary<BackupHistoryNode, TaskHistoryDetails>();
        private long taskHistoryDetailsUse;
        private void TrimTaskHistoryDetails()
        {
            while (taskHistoryDetails.Count > 32 || taskHistoryDetails.Values.Sum(value => (long)(value.Text == null ? 0 : value.Text.Length)) > 524288)
                taskHistoryDetails.Remove(taskHistoryDetails.OrderBy(pair => pair.Value.Used).First().Key);
        }

        private async Task ShowTaskHistoryDetailsAsync(BackupHistoryNode node)
        {
            int generation = ++warehouseDetailsGeneration;
            TaskHistoryDetails cached = null;
            try
            {
                await Window.Dispatcher.InvokeAsync(delegate
                {
                }, DispatcherPriority.Background);
                if (disposed || generation != warehouseDetailsGeneration)
                    return;
                long stamp = Backend.Catalog.DataChangeStamp;
                if (!taskHistoryDetails.TryGetValue(node, out cached) || cached.Stamp != stamp)
                {
                    cached = new TaskHistoryDetails
                    {
                        Stamp = stamp
                    };
                    taskHistoryDetails[node] = cached;
                    cached.Loading = Task.Run(() => Backend.ReadHistoryProperties(node, false).DetailsText);
                }

                cached.Used = ++taskHistoryDetailsUse;
                TrimTaskHistoryDetails();
                if (cached.Text == null)
                    Text("HistoryDetails").Text = "";
                string text = cached.Text ?? await cached.Loading;
                cached.Text = text;
                TrimTaskHistoryDetails();
                // Publish the list before formatting a large property panel, and discard superseded selections.
                await Window.Dispatcher.InvokeAsync(
                    delegate
                {
                    if (!disposed && generation == warehouseDetailsGeneration)
                        Text("HistoryDetails").Text = text;
                },
                    DispatcherPriority.Background
                );
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/TaskHistoryNavigation.cs:ShowTaskHistoryDetailsAsync", error);
                TaskHistoryDetails current;
                if (taskHistoryDetails.TryGetValue(node, out current) && ReferenceEquals(current, cached))
                    taskHistoryDetails.Remove(node);
                if (!disposed && generation == warehouseDetailsGeneration)
                    Text("HistoryDetails").Text = "属性读取未完成：" + error.Message;
            }
        }
    }
}
