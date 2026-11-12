using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Backup.Core;
namespace Backup.Desktop
{
    public sealed partial class MainWindowController
    {
        private int operationGeneration,operationTab;
        private System.Threading.CancellationTokenSource operationCancellation;
        private string currentUploadAlert;
        private OperationState operationState;
        private bool repairingBackups;
        private const string RepairStoppedNotice="修复已停止。已完成的修复保留，未完成项及待确认上传下次继续核对。";
        private void StopTransferWork(bool immediately)
        {
            var control=Backend.ActiveBackup;if(control==null||control.IsStopping)return;
            if(repairingBackups){SetOperationText("正在停止修复，取消当前下载和上传；已完成的修复保留。");Backend.StopBackup();if(operationCancellation!=null)operationCancellation.Cancel();UpdateBackupButtons();RefreshDownloadStatusVisibility();return;}
            if(immediately){Backend.StopBackup(true);UpdateBackupButtons();SetOperationText("正在取消读取和上传，释放缓存并清理未完成归档；已完成归档保留。");}
            else{Backend.StopAfterCurrentArchive();UpdateBackupButtons();SetOperationText(FinishingBackupText(null));}
        }
        private void PublishBackupResult(DesktopBackupResult result){if(closeAfterWork||disposed)return;DrainTransferNotifications();operationGeneration++;if(Tree.UpdateObservedSources(result.ObservedSources)>0)ShowSelectionSummary(Tree.SelectedFileCount,Tree.SelectedBytes);ShowUploadAlert(Backend.Catalog.GetSetting("last_upload_alert_v1"));Text("BackupMetrics").Text="已上传压缩包 "+result.UploadedFiles+" 个";Text("BackupMetrics").ToolTip="按成功上传的压缩文件计数，同名文件的多个仓库副本不重复计数。";UpdateBackupButtons();}
        private bool uploadAlertDismissed;
        private void DismissUploadAlert(){uploadAlertDismissed=true;Control<Border>("UploadFailureBanner").Visibility=Visibility.Collapsed;}
        private void ShowUploadAlert(string message)
        {
            message=PrepareVisibleUploadAlert(message);var banner=Control<Border>("UploadFailureBanner");
            if(currentUploadAlert==message){banner.Visibility=message.Length==0||uploadAlertDismissed?Visibility.Collapsed:Visibility.Visible;return;}
            currentUploadAlert=message;bool confirming=currentUploadAlert.Length>0&&currentUploadAlert.Split('\n').All(line=>line.Length==0||line.StartsWith("上传结果待确认",StringComparison.Ordinal)||line.StartsWith("原因",StringComparison.Ordinal)||line.StartsWith("上传失败，正在重试",StringComparison.Ordinal)||line.StartsWith("正在连接",StringComparison.Ordinal));
            banner.Background=(System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(confirming?"#FFF8E1":"#FFF1F0");
            banner.BorderBrush=confirming?OperationTextColors.Working:(System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#D92D20");
            var notice=Control<TextBox>("UploadFailureNotice");notice.Foreground=Text("WarehouseUploadFailure").Foreground=confirming?OperationTextColors.Working:(System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#B42318");
            notice.Text=message.Length<=16000?message:"提示较多，详细信息见日志。\n"+message.Substring(message.Length-16000);
            banner.Visibility=currentUploadAlert.Length==0||uploadAlertDismissed?Visibility.Collapsed:Visibility.Visible;
            Text("WarehouseUploadFailure").Text=message.Length==0?"":NoticeBlocks(message).Count+" 条上传提示，详情见备份任务日志。";
            Text("WarehouseUploadFailure").Visibility=message.Length==0?Visibility.Collapsed:Visibility.Visible;
        }
        private void RestoreUploadAlert(){NormalizeSavedUploadNotices();if(string.IsNullOrEmpty(currentUploadAlert)){string saved=Backend.Catalog.GetSetting("last_upload_alert_v1");if(string.IsNullOrEmpty(saved))saved=Backend.Catalog.GetSetting("last_upload_warnings_v1");ShowUploadAlert(saved);}}
        private void SetOperationText(string message,OperationState state=OperationState.Running,bool logFailure=true){if(state==OperationState.Running&&operations.IsActive&&BandwidthControlsCurrentWork&&Backend.Bandwidth.IsPaused){message=Backend.ActiveBackup!=null&&Backend.ActiveBackup.IsFinishing?"已停止读取；限速时段暂停上传，时段结束后继续上传已打包完整的压缩包。":"限速时段：已自动暂停全部备份、上传和下载，时段结束后自动继续。";state=OperationState.Attention;}operationState=state;Control<TextBox>("StatusText").Text=message;Control<TextBox>("StatusText").ToolTip=message==null?null:message.Length>500?message.Substring(0,500)+"…（详细信息见日志）":message;if(logFailure&&state==OperationState.Failed)LogAlertOnce(message??"",state);if(operationTab==1)Text("SourceNotice").Text=message;else if(operationTab==2)Text("WarehouseNotice").Text=message;ApplyOperationForeground();RefreshDownloadStatusVisibility();}
        private void OperationProgress(double percent,bool indeterminate){foreach(string name in new[]{"JobProgress","SourceProgress","WarehouseProgress"}){var bar=Control<ProgressBar>(name);bar.IsIndeterminate=indeterminate;bar.Value=Math.Max(0,Math.Min(100,percent));}}
        private Action<CleanupProgress> CleanupReporter(){int generation=operationGeneration;return value=>{if(Window.Dispatcher.HasShutdownStarted)return;Window.Dispatcher.BeginInvoke(new Action(delegate{if(!busy||disposed||closeAfterWork||generation!=operationGeneration||repairingBackups&&Backend.ActiveBackup!=null&&Backend.ActiveBackup.IsStopping)return;if(!repairingBackups){backupBusy=false;Button("StopAfterArchive").IsEnabled=false;Button("StopAfterArchive").Visibility=Visibility.Collapsed;}UpdateBackupButtons();
            string message="正在清理：备份包 "+value.CompletedArchives+" / "+value.TotalArchives+" · 已删除分卷副本 "+value.DeletedObjects+" / "+value.TotalObjects+" · 已释放 "+Display.Bytes(value.FreedBytes)+"\n"+value.Message;SetOperationText(message);OperationProgress(value.TotalObjects==0?(value.TotalArchives==0?0:100.0*value.CompletedArchives/value.TotalArchives):100.0*value.DeletedObjects/value.TotalObjects,value.TotalObjects==0&&value.TotalArchives==0);
        }),DispatcherPriority.Background);};}
        private Action<string> MaintenanceReporter(){return OperationReporter("正在处理迁移：");}
        private Action<string> OperationReporter(string prefix){int generation=operationGeneration;return message=>{if(Window.Dispatcher.HasShutdownStarted)return;Window.Dispatcher.BeginInvoke(new Action(delegate{if(!busy||disposed||closeAfterWork||generation!=operationGeneration)return;if(message.StartsWith("重试成功",StringComparison.Ordinal)){int separator=message.IndexOf("：",StringComparison.Ordinal);string subject=separator<0?message:message.Substring(separator+1);int split=subject.LastIndexOf(" · ",StringComparison.Ordinal);if(split>=0){string id=subject.Substring(0,split),name=subject.Substring(split+3);RemoveResolvedUploadNotice(id,TransferLabel(id),name);}LogAlertOnce(message,OperationState.Succeeded);return;}if(message.StartsWith("上传失败"))ShowUploadAlert(message);var control=Backend.ActiveBackup;if(backupBusy&&control!=null){if(control.IsPaused||control.IsStopping)return;if(Backend.StopRequested||control.IsFinishing){SetOperationText(FinishingBackupText(null));return;}}SetOperationText(prefix+message);OperationProgress(0,true);}),DispatcherPriority.Background);};}
        public static string CleanupSummary(CleanupResult result){if(result==null)return "";return (result.PendingArchives==0?"清理完成":"清理结束")+"：删除 "+result.DeletedArchives+" 个备份包、"+result.DeletedObjects+" 个分卷副本，释放 "+Display.Bytes(result.FreedBytes)+(result.PendingArchives==0?"。":"；"+result.PendingArchives+" 个备份包待重试。");}
    }
}
