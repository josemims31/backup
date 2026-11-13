using System;
using System.Linq;
using System.Threading.Tasks;

namespace Backup.Desktop
{
    public sealed partial class MainWindowController
    {
        public Task CompactAllArchivesAsync()
        {
            return RunOperationAsync(OperationKind.Compact, CompactAllArchivesCoreAsync);
        }

        private async Task CompactAllArchivesCoreAsync()
        {
            if (busy)
                return;
            Backend.BeginBackup();
            SetBusy(true, true, "正在全局整理压缩包和版本数据库…");
            try
            {
                var report = OperationReporter("");
                var cleanup = CleanupReporter();
                var result = await Task.Run(() => Backend.CompactAllArchives(report, cleanup));
                string message = "整理完成：数据包 " + result.DataPackages + " → " + result.NewDataPackages + "，版本数据库包 " + result.DatabasePackages + " → " + result.NewDatabasePackages + "，更新 " + result.Versions +
                    " 个备份版本。\n" +
                    CleanupSummary(result.Cleanup);
                if (result.Versions == 0 && result.Errors.Count == 0)
                    message = "没有需要合并的小压缩包。";
                if (result.Errors.Count > 0)
                    message += "\n以下项目保留待处理：\n" + string.Join("\n", result.Errors);
                historyDirty = true;
                ApplyCommittedChanges();
                RebuildHistory();
                SetOperationText(message, result.Errors.Count == 0 ? OperationState.Succeeded : OperationState.Attention);
                Log(message);
                OperationProgress(100, false);
            }
            finally
            {
                Backend.EndBackup();
                SetBusy(false, false, null);
            }
        }
    }
}
