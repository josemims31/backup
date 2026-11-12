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
        internal const string CompactionConfirmation="压缩包内可能含有本地已删除/已被覆盖的旧版文件\n将从本地读取最新文件重新整合打包上传，删除旧压缩包\n以此释放储存空间";
#if UI_TEST
        public Func<string,string,bool> RemoteTaskConfirmation;
#endif
        private bool ConfirmRemoteTaskAction(string title,string message)
        {
#if UI_TEST
            if(RemoteTaskConfirmation!=null)return RemoteTaskConfirmation(title,message);
#endif
            return MessageBox.Show(Window,message,title,MessageBoxButton.OKCancel,MessageBoxImage.Information)==MessageBoxResult.OK;
        }
        private BackupTaskDefinition SelectedRemoteTask()
        {
            if(!IsFolderHistoryView||Workspace==null)return null;string id=historyActiveTaskId;
            if(string.IsNullOrEmpty(id)){var node=Control<TreeView>("HistoryTree").SelectedItem as BackupHistoryNode;if(node==null||!node.IsTaskRoot)return null;id=node.NavigationScope;}
            return Workspace.Tasks.FirstOrDefault(task=>task.Id==id&&!task.Deleted);
        }
        private void UpdateRemoteTaskActions()
        {
            var visibility=IsFolderHistoryView?Visibility.Visible:Visibility.Collapsed;Control<FrameworkElement>("HistoryWarehouseFilterPanel").Visibility=visibility;Control<FrameworkElement>("HistoryTaskActions").Visibility=visibility;
            Button("CompactArchives").Visibility=IsFolderHistoryView?Visibility.Collapsed:Visibility.Visible;Button("CompactArchives").IsEnabled=!MutationBlocked;
            var task=SelectedRemoteTask();Button("CompactTaskArchives").IsEnabled=false;Button("DeleteBackupNode").IsEnabled=!MutationBlocked&&SelectedBackupNode!=null;Control<ComboBox>("HistoryNodeSelector").IsEnabled=!disposed&&!closeAfterWork;
            Button("CompactTaskArchives").ToolTip=task!=null&&task.RecoveryPendingConfirmation?"请先完成恢复并确认导入的任务，再清理失效文件。":"重新整合全部仓库的有效文件，完成后清理旧包。";
        }
        private Task CompactSelectedTaskAsync()
        {return RunOperationAsync(OperationKind.Compact,()=>CompactSelectedTaskCoreAsync());}
        private async Task CompactSelectedTaskCoreAsync()
        {
            var task=SelectedRemoteTask();if(busy||task==null)return;if(!ConfirmRemoteTaskAction("清理失效文件："+task.Name,CompactionConfirmation))return;
            var selected=JsonSettings.Decode(JsonSettings.Encode(task),new BackupTaskDefinition());Backend.BeginBackup();SetBusy(true,true,"正在清理失效文件："+selected.Name);Log("开始清理失效文件："+selected.Name);
            try
            {
                var progress=BackupReporter();var cleanup=CleanupReporter();var report=OperationReporter("");var result=await Task.Run(()=>Backend.CompactTaskArchives(selected,progress,cleanup,report));
                string message=result.Errors.Count>0?"操作未完成："+string.Join("；",result.Errors):result.OldArchives==0?"没有需要清理的失效文件。":"清理完成：整合 "+result.SourceFiles+" 个有效文件，生成 "+result.NewArchives+" 个压缩包，删除 "+result.Cleanup.DeletedArchives+" 个旧压缩包，释放 "+Display.Bytes(result.Cleanup.FreedBytes)+"。";
                SetOperationText(message,result.Errors.Count>0?OperationState.Failed:OperationState.Succeeded);Log(selected.Name+"："+message);OperationProgress(100,false);
            }
            finally{Backend.EndBackup();SetBusy(false,false,null);}
        }
    }
}
