using System;
using System.ComponentModel;
using System.Threading.Tasks;
namespace Backup.Desktop
{
    public sealed partial class MainWindowController
    {
        private Task shutdownDisposal;
#if UI_TEST
        public Action TestShutdownDisposal;
#endif
        private async void OnClosing(object sender,CancelEventArgs e)
        {
            if(shutdownDisposal!=null){if(!shutdownDisposal.IsCompleted)e.Cancel=true;return;}
            if(disposed)return;CancelBackgroundBrowsing();operations.Cancel();if(operationCancellation!=null)operationCancellation.Cancel();
            try{SaveScheduledRequestsForExit();}
            catch(Exception error){e.Cancel=true;closeAfterWork=false;if(scheduleRequests!=null)scheduleRequests.CancelClosing();ApplyCommittedChanges();Error(error);return;}
            if(MutationBlocked||scheduleDraining||scheduleExecuting){e.Cancel=true;closeAfterWork=true;Backend.StopBackupForExit();UpdateBackupButtons();SetOperationText("正在取消当前传输，释放内存后退出；已完成归档保留。");return;}
            try{SaveTaskControls();SaveWorkspace();SaveWindowLayout();}
            catch(Exception error){e.Cancel=true;closeAfterWork=false;if(scheduleRequests!=null)scheduleRequests.CancelClosing();ApplyCommittedChanges();Error(error);return;}
            e.Cancel=true;closeAfterWork=true;SetOperationText("正在关闭仓库连接和数据库…");
            try
            {
                if(BeginDispose()){var content=Window.Content as System.Windows.UIElement;if(content!=null)content.IsEnabled=false;shutdownDisposal=Task.Run((Action)DisposeRuntimeResources);await shutdownDisposal;}
            }
            catch(Exception error){RuntimeLog.Append(Backend.BaseDirectory,"ui-errors.log","退出资源释放未完成："+error);}
            finally{ScheduleRefreshClose();}
        }
        private bool BeginDispose()
        {
            if(disposed)return false;StopUploadNotifications();Backend.Bandwidth.Changed-=BandwidthChanged;if(scheduleRequests!=null)scheduleRequests.Seal();operations.Changed-=OperationAdmissionChanged;operations.Dispose();Backend.ApplicationChanged-=QueueRepositoryPresentation;
            Backend.OpenListFallbackChanged-=OpenListFallbackChanged;Backend.OpenListAuthorizationPrompt=null;Backend.ServerTrustPrompt=null;Backend.ServerTrust.Dispose();CancelBackgroundBrowsing();if(operationCancellation!=null)operationCancellation.Cancel();disposed=true;
            if(fileRefreshIdle!=null)fileRefreshIdle.TrySetResult(true);if(fileRefreshCompletion!=null){fileRefreshCompletion.TrySetResult(false);fileRefreshCompletion=null;}return true;
        }
        private void DisposeRuntimeResources()
        {
            try
            {
#if UI_TEST
                if(TestShutdownDisposal!=null)TestShutdownDisposal();
#endif
                if(scheduleRequestHost!=null)scheduleRequestHost.Dispose();
            }
            finally{try{if(scheduleJournal!=null)scheduleJournal.Dispose();}finally{Backend.Dispose();}}
        }
        public void Dispose(){if(BeginDispose())DisposeRuntimeResources();else if(shutdownDisposal!=null&&!shutdownDisposal.IsCompleted)shutdownDisposal.GetAwaiter().GetResult();}
    }
}
