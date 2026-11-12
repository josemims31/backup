using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
namespace Backup.Desktop
{
    public sealed partial class MainWindowController
    {
        private void BindRecoveryActions()
        {
            Button("CompactTaskArchives").Click+=async delegate{await GuardAsync(CompactSelectedTaskAsync);};Button("ImportCatalog").Click+=async delegate{await GuardAsync(ImportDatabaseAsync);};Button("RepairBackups").Click+=async delegate{await GuardAsync(RepairBackupsAsync);};
        }
        private async Task ImportDatabaseAsync()
        {
            if(MutationBlocked)return;var picker=new Microsoft.Win32.OpenFileDialog{Title="选择已用 7-Zip 解密的 catalog.db",Filter="Backup 数据库|*.db|所有文件|*.*"};string input=EditorDialogs.ChooseFile(Window,picker,scheduleJournal,"ui_last_catalog_import_v1");if(input==null)return;
            await RunOperationAsync(OperationKind.Import,async delegate{
                DatabaseImportPreview preview;SetBusy(true,false,"正在读取导入数据库…");try{preview=await Task.Run(()=>Backend.PreviewDatabaseImport(input));}finally{SetBusy(false,false,null);}
                var selection=EditorDialogs.DatabaseImport(Window,preview);if(selection==null)return;
                if(!selection.Merge&&MessageBox.Show(Window,"导入后将切换到该数据库的任务和备份记录，当前数据库将会备份至./cache\n导入后自动刷新显示，保留备份记录\n任务计划自动停用，需手动启用\n换电脑时请重新填写仓库的账号密码","确认导入数据库",MessageBoxButton.OKCancel,MessageBoxImage.Question)!=MessageBoxResult.OK)return;
                await ImportDatabaseAndRefreshCoreAsync(input,selection);
            });
        }
        public Task ImportDatabaseAndRefreshAsync(string input,DatabaseImportSelection selection=null)
        {return RunOperationAsync(OperationKind.Import,()=>ImportDatabaseAndRefreshCoreAsync(input,selection));}
        private async Task ImportDatabaseAndRefreshCoreAsync(string input,DatabaseImportSelection selection=null)
        {
            if(busy)throw new InvalidOperationException("请等待当前操作结束。");
            await WaitForFileListsAsync();
            SaveTaskControls();SaveWorkspace();string previousRepository=Backend.RepositoryId,previousTask=CurrentTask==null?null:CurrentTask.Id;var previousPlans=Backend.Schedules();string rollback=null;
            SetBusy(true,false,"正在校验并导入数据库…");
            try
            {
                bool merge=selection!=null&&selection.Merge;CancelBackgroundBrowsing();rollback=await Task.Run(()=>merge?Backend.MergeDatabase(input,selection):Backend.ImportDatabase(input));
                Workspace=WorkspaceMigration.Load(Backend.Catalog);selectionDrafts.Clear();loaded.Clear();historyTaskId=null;historyDirty=true;
                fileViewsSnapshot=null;fileViewsStamp=long.MinValue;CurrentTask=Workspace.Tasks.FirstOrDefault(t=>!t.Deleted&&t.Id==previousTask)??Workspace.Tasks.FirstOrDefault(t=>!t.Deleted);InvalidateFileTree();
                if(merge){RefreshSchedulePage();ObserveScheduleInspection();}else RuntimeAfterImport(previousRepository,previousPlans);LoadTaskControls();RefreshCatalog();
                Log(merge?"数据库已合并；现有内容已保留，导入的任务计划等待手动确认。":"数据库已导入；任务计划已停用，等待手动确认。正在刷新文件列表，备份记录将保留。");
            }
            finally{SetBusy(false,false,null);}
            await RefreshAllFileListsAsync();
            SetOperationText((selection!=null&&selection.Merge?"数据库合并完成；现有内容已保留，导入的任务计划等待手动确认。原数据库副本：":"数据库导入完成；任务计划等待手动确认，备份记录已保留。原数据库副本：")+rollback,OperationState.Succeeded);OperationProgress(100,false);
        }
        private Task RepairBackupsAsync()
        {return RunOperationAsync(OperationKind.Repair,()=>RepairBackupsCoreAsync());}
        private async Task RepairBackupsCoreAsync()
        {
            if(busy)return;
            var scope=new System.Collections.Generic.HashSet<string>(Workspace.Tasks.Where(t=>!t.Deleted).Select(t=>t.Id));
            scope.Add("__catalog__");
            string publication=Backend.DatabaseBackupTargets().Count>0?"修复发生实际变更后统一同步数据库快照；没有变更则不上传。":"修复变更保存在本地数据库。";
            if(!ConfirmRemoteTaskAction("确认修复备份","将按检查结果修复全部保留版本及数据库备份，并清理无登记备份文件。\n历史版本从健康备份副本修复，不用当前源文件替换。已删除且没有保留版本的任务不重新备份。\n暂不可访问、上传待确认或没有健康副本的项目保留并报告原因。\n"+publication))return;
            if(!ConfirmImportedTasks(scope,"修复备份"))return;
            Backend.BeginBackup();repairingBackups=true;SetBusy(true,true,"正在重新整合并修复异常备份包…");var report=OperationReporter("正在修复：");
            try{var progress=BackupReporter();var cleanup=CleanupReporter();var errors=await Task.Run(()=>Backend.RepairCheckedBackups(Workspace,null,report,progress,cleanup));Backend.ActiveBackup.Checkpoint(false);ApplyCommittedChanges();int failures=errors.Count(error=>!error.Contains("上传结果待确认")&&!error.Contains("暂不可访问")&&!error.Contains("上传结果仍未核对")&&!error.Contains("暂时无法核对"));PublishHistoryCheckSummary(null,0,failures);SetOperationText(errors.Count==0?"已确认的副本修复完成。":failures==0?"部分上传结果暂时无法核对：\n"+string.Join("\n",errors):"操作未完成：以下项目待重试：\n"+string.Join("\n",errors),errors.Count==0?OperationState.Succeeded:failures==0?OperationState.Attention:OperationState.Failed);OperationProgress(100,false);}
            catch(OperationCanceledException){ApplyCommittedChanges();SetHistoryCheckText("修复已停止，未完成项目保留待下次核对。",OperationState.Attention);SetOperationText(RepairStoppedNotice,OperationState.Cancelled);OperationProgress(0,false);}
            finally{repairingBackups=false;Backend.EndBackup();SetBusy(false,false,null);}
        }
    }
}
