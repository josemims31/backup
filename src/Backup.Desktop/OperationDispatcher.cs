using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace Backup.Desktop
{
    internal enum OperationKind { Backup,Schedule,Refresh,Check,Repair,Compact,Download,Import,Maintenance,Configuration,Browse,Delete }
    internal sealed class OperationBusyException:InvalidOperationException{internal OperationBusyException():base("当前任务尚未结束。") {}}
    public enum OperationState { None,Running,Succeeded,Attention,Failed,Cancelled }
    internal sealed class OperationContext:IDisposable
    {
        internal readonly Guid Id=Guid.NewGuid();internal readonly OperationKind Kind;
        internal readonly CancellationTokenSource Cancellation=new CancellationTokenSource();
        internal OperationContext(OperationKind kind){Kind=kind;}
        public void Dispose(){Cancellation.Dispose();}
    }
    // Admission belongs to the application, not to an individual page. Read modules
    // keep their own scoped/coalesced work; foreground workflows share one owner.
    internal sealed class OperationDispatcher:IDisposable
    {
        private readonly object gate=new object();private readonly AsyncLocal<OperationContext> current=new AsyncLocal<OperationContext>();
        private readonly HashSet<ReadLease> readers=new HashSet<ReadLease>();
        private OperationContext foreground;private TaskCompletionSource<bool> idle,downloadsIdle;private bool closed;
        internal event Action Changed;
        internal bool IsActive{get{lock(gate)return foreground!=null;}}
        internal OperationKind? ActiveKind{get{lock(gate)return foreground==null?(OperationKind?)null:foreground.Kind;}}
        internal OperationContext Current{get{lock(gate)return foreground!=null&&foreground==current.Value?current.Value:null;}}
        internal bool OwnsForeground{get{lock(gate)return foreground!=null&&foreground==current.Value;}}
        private bool HasDownloads{get{return readers.Any(reader=>reader.Kind==OperationKind.Download);}}
        internal Task WhenIdle(){lock(gate){if(foreground==null&&!HasDownloads)return Task.FromResult(true);if(idle==null)idle=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);return idle.Task;}}
        internal Task WhenDownloadsIdle(){lock(gate){if(!HasDownloads)return Task.FromResult(true);if(downloadsIdle==null)downloadsIdle=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);return downloadsIdle.Task;}}
        private void Notify(){var handler=Changed;if(handler!=null)handler();}
        internal async Task RunAsync(OperationKind kind,Func<Task> work)
        {await RunAsync(kind,async delegate{await work();return true;});}
        internal void Run(OperationKind kind,Action work){RunAsync(kind,delegate{work();return Task.FromResult(true);}).GetAwaiter().GetResult();}
        // Transfer limits are the only live configuration here; they do not mutate task/archive policy.
        internal void ConfigureRuntime(Action update){lock(gate){if(closed)throw new ObjectDisposedException("OperationDispatcher");}update();}
        internal async Task<T> RunAsync<T>(OperationKind kind,Func<Task<T>> work)
        {
            OperationContext previous=current.Value,owner;
            lock(gate){if(closed)throw new ObjectDisposedException("OperationDispatcher");if(foreground!=null){if(foreground!=previous)throw new OperationBusyException();owner=null;}else{if(HasDownloads)throw new OperationBusyException();owner=new OperationContext(kind);foreground=owner;}}
            if(owner==null){if(kind==OperationKind.Check||kind==OperationKind.Repair||kind==OperationKind.Compact||kind==OperationKind.Delete||kind==OperationKind.Import||kind==OperationKind.Maintenance||kind==OperationKind.Configuration)await WhenDownloadsIdle();previous.Cancellation.Token.ThrowIfCancellationRequested();return await work();}
            current.Value=owner;
            try{Notify();owner.Cancellation.Token.ThrowIfCancellationRequested();return await work();}
            finally
            {
                current.Value=previous;TaskCompletionSource<bool> ready=null;lock(gate){foreground=null;if(!HasDownloads){ready=idle;idle=null;}}owner.Dispose();if(ready!=null)ready.TrySetResult(true);Notify();
            }
        }
        internal async Task RunWhenIdleAsync(OperationKind kind,Func<Task> work)
        {
            if(OwnsForeground){await RunAsync(kind,work);return;}
            while(true){await WhenIdle();bool entered=false;try{await RunAsync(kind,async delegate{entered=true;await work();});return;}catch(OperationBusyException){if(entered)throw;}}
        }
        internal IDisposable Read(OperationKind kind,CancellationTokenSource cancellation=null)
        {
            ReadLease value;lock(gate){if(closed)throw new ObjectDisposedException("OperationDispatcher");value=new ReadLease(this,kind,cancellation);readers.Add(value);}if(kind==OperationKind.Download)Notify();return value;
        }
        private void ReleaseRead(ReadLease reader){TaskCompletionSource<bool> ready=null,downloadReady=null;lock(gate){readers.Remove(reader);if(!HasDownloads){downloadReady=downloadsIdle;downloadsIdle=null;if(foreground==null){ready=idle;idle=null;}}}if(downloadReady!=null)downloadReady.TrySetResult(true);if(ready!=null)ready.TrySetResult(true);if(reader.Kind==OperationKind.Download)Notify();}
        private sealed class ReadLease:IDisposable
        {private OperationDispatcher owner;internal readonly OperationKind Kind;internal readonly CancellationTokenSource Cancellation;internal ReadLease(OperationDispatcher o,OperationKind k,CancellationTokenSource cancellation){owner=o;Kind=k;Cancellation=cancellation;}public void Dispose(){var old=Interlocked.Exchange(ref owner,null);if(old!=null)old.ReleaseRead(this);}}
        internal void Cancel(){var values=new List<CancellationTokenSource>();lock(gate){if(foreground!=null)values.Add(foreground.Cancellation);foreach(var reader in readers)if(reader.Cancellation!=null)values.Add(reader.Cancellation);}foreach(var value in values)try{value.Cancel();}catch(ObjectDisposedException){}}
        public void Dispose(){lock(gate)closed=true;Cancel();}
    }
    public sealed partial class MainWindowController
    {
        private readonly OperationDispatcher operations=new OperationDispatcher();
        private void CheckOperationAdmission(){if(disposed)throw new ObjectDisposedException("MainWindowController");if((busy||independentDownloadActive)&&!operations.OwnsForeground)throw new OperationBusyException();}
        private Task RunOperationAsync(OperationKind kind,Func<Task> action){CheckOperationAdmission();return operations.RunAsync(kind,()=>{Backend.ServerTrust.BeginAttempt();return action();});}
        private Task<T> RunOperationAsync<T>(OperationKind kind,Func<Task<T>> action){CheckOperationAdmission();return operations.RunAsync(kind,()=>{Backend.ServerTrust.BeginAttempt();return action();});}
        private void RunConfiguration(Action action){CheckOperationAdmission();operations.Run(OperationKind.Configuration,action);}
        private bool MutationBlocked{get{return busy||operations.IsActive||independentDownloadActive;}}
        private void OperationAdmissionChanged()
        {
            if(disposed)return;Backend.SetRepositoryActivity(operations.IsActive||independentDownloadActive);if(!operations.IsActive)try{ApplyCommittedChanges();}catch(Exception error){Log("界面状态更新未完成："+error.Message);}RefreshOperationAvailability();if(!operations.IsActive&&!independentDownloadActive){DispatchScheduledRequests();if(closeAfterWork)ScheduleRefreshClose();}
        }
        private void RefreshOperationAvailability()
        {
            bool blocked=MutationBlocked;
            foreach(string name in new[]{"SourceActions","WarehouseActions","BackupOptions","EditExclusions","ScheduleActions"})Control<System.Windows.FrameworkElement>(name).IsEnabled=!blocked;
            Button("Settings").IsEnabled=!disposed&&!closeAfterWork;
            foreach(string name in new[]{"AddTask","EditTask","RemoveTask","RefreshSources","ImportCatalog","CheckIntegrity","RepairBackups"})Button(name).IsEnabled=!blocked;
            Control<System.Windows.Controls.ComboBox>("TaskSelector").IsEnabled=!blocked||backupBusy;
            UpdateSelectionControls();UpdateScheduleRunButton();UpdateRemoteTaskActions();
        }
    }
}
