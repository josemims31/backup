using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Backup.Core;
namespace Backup.Desktop
{
    public sealed partial class MainWindowController
    {
        private readonly HashSet<string> fileRefreshPending=new HashSet<string>();
        private readonly Dictionary<string,string> fileRefreshActive=new Dictionary<string,string>();
        private TaskCompletionSource<bool> fileRefreshCompletion,fileRefreshIdle;
        private bool fileRefreshRequested;
        private int treeBuildGeneration;
        public bool FileListsRefreshing {get{return fileRefreshCompletion!=null;}}
#if UI_TEST
        public Func<string,IList<SourceItem>> FileListQuery;
#endif
        private sealed class RefreshedFolder:SourceReadObservation
        {
            public IList<SourceItem> Files;public LoadedSource Previous;public bool Changed=true;
        }
        private Task initialFileLists=Task.FromResult(true);
        private Task treePreparation=Task.FromResult(true);
        public async Task WaitForFileListsAsync(){if(!initialFileLists.IsCompleted)await initialFileLists;while(fileRefreshCompletion!=null)await fileRefreshCompletion.Task;await treePreparation;}
        public Task RefreshAllFileListsAsync(){return RequestFileRefreshAsync(Workspace.Sources.Where(s=>!s.Deleted).Select(s=>s.Id));}
        public async Task RefreshStatusAsync(){await CheckAllBackupsAsync();}
        private async void StartAutomaticFileRefresh(IEnumerable<string> ids)
        {
            try{await RequestFileRefreshAsync(ids);}catch(Exception error){if(!disposed){Log("后台刷新未完成："+error.Message);if(!busy)SetOperationText("文件列表刷新未完成："+error.Message,OperationState.Failed);}}
        }
        private Task RequestFileRefreshAsync(IEnumerable<string> ids)
        {
            if(disposed)return Task.FromResult(false);
            foreach(string id in ids){string active;var source=Workspace.Sources.FirstOrDefault(s=>s.Id==id&&!s.Deleted);if(source!=null&&(!fileRefreshActive.TryGetValue(id,out active)||!string.Equals(active,source.Location,StringComparison.OrdinalIgnoreCase)))fileRefreshPending.Add(id);}if(fileRefreshPending.Count>0)fileRefreshRequested=true;
            if(fileRefreshCompletion!=null)return fileRefreshCompletion.Task;
            fileRefreshCompletion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);var task=fileRefreshCompletion.Task;
            if(!busy){SetOperationText("正在获取Everything索引数据");Control<ProgressBar>("JobProgress").IsIndeterminate=true;}
            Window.Dispatcher.BeginInvoke(new Action(async delegate{await ProcessFileRefreshesAsync();}),DispatcherPriority.Background);
            return task;
        }
        private async Task WaitForRefreshIdleAsync()
        {
            while(busy&&!disposed){if(fileRefreshIdle==null)fileRefreshIdle=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);await fileRefreshIdle.Task;}
        }
        private async Task ProcessFileRefreshesAsync()
        {
            var completion=fileRefreshCompletion;if(completion==null||disposed)return;Exception failure=null;
            try{
                while(fileRefreshRequested&&!disposed){
                    var ids=new HashSet<string>(fileRefreshPending);fileRefreshPending.Clear();fileRefreshRequested=false;
                    var requests=Workspace.Sources.Where(s=>!s.Deleted&&ids.Contains(s.Id)).Select(s=>{LoadedSource prior;loaded.TryGetValue(s.Id,out prior);return new RefreshedFolder{Id=s.Id,Location=s.Location,Previous=prior};}).ToList();foreach(var request in requests)fileRefreshActive[request.Id]=request.Location;
                    // IPC queries and sorting/tree construction never execute on the WPF dispatcher.
                    using(operations.Read(OperationKind.Refresh))await Task.Run(delegate{foreach(var request in requests)try{
#if UI_TEST
                        request.Files=FileListQuery==null?new EverythingCatalogSource().Query(request.Location):FileListQuery(request.Location);
#else
                        request.Files=new EverythingCatalogSource().Query(request.Location);
#endif
                        request.Changed=!DesktopBackend.SameFileList(request.Previous,request.Location,request.Files);
                    }catch(Exception error){if(!(error is System.IO.IOException)&&!(error is UnauthorizedAccessException))throw;request.Error=error.Message;}});
                    await WaitForRefreshIdleAsync();if(disposed)break;
                    var accepted=requests.Where(r=>Workspace.Sources.Any(s=>!s.Deleted&&s.Id==r.Id&&s.Location.Equals(r.Location,StringComparison.OrdinalIgnoreCase))).ToList();
                    RecordSourceReadStates(accepted);
                    var successful=accepted.Where(r=>r.Error==null).ToList();
                    SetBusy(true,false,"正在更新文件列表…");
                    try{
                        foreach(var result in successful.Where(result=>result.Changed))loaded[result.Id]=new LoadedSource{Root=result.Location,Files=result.Files};
                        await Task.Run(delegate{foreach(var result in successful.Where(result=>result.Changed))Backend.SaveFileList(result.Id,loaded[result.Id],result.Previous);});
                        ApplyCommittedChanges();await PrepareCurrentTreeAsync();
                        operationGeneration++;string message="索引读取完成：已刷新 "+successful.Count+" 个文件夹"+(CurrentTask==null?"":"，当前任务 "+Tree.AllFiles.Count+" 个文件，排除 "+Tree.ExcludedCount+" 项")+"。";
                        var errors=accepted.Where(r=>r.Error!=null).ToList();if(errors.Count>0)message+=" "+errors.Count+" 个文件夹刷新失败，保留上次列表："+string.Join("；",errors.Select(r=>r.Location+"："+r.Error));
                        SetOperationText(message,errors.Count>0?OperationState.Failed:OperationState.Succeeded);OperationProgress(100,false);Log(message);
                    }finally{fileRefreshActive.Clear();SetBusy(false,false,null);}
                }
            }catch(Exception error){failure=error;}
            finally{
                fileRefreshCompletion=null;
                fileRefreshActive.Clear();
                if(failure==null)completion.TrySetResult(true);else completion.TrySetException(failure);
                if(!disposed&&!busy)Control<ProgressBar>("JobProgress").IsIndeterminate=false;
                if(!disposed&&closeAfterWork)ScheduleRefreshClose();
            }
        }
        private void ScheduleRefreshClose(){Window.Dispatcher.BeginInvoke(new Action(()=>Window.Close()));}
        private void InvalidateFileTree()
        {
            treeBuildGeneration++;UpdateSelectionControls();
        }
        private string preparedTreeKey;
        private FileTreeWorkspace preparedTree;
        private List<LoadedSource> preparedSources=new List<LoadedSource>();
        private string fileViewsTaskId;
        private long fileViewsStamp=long.MinValue;
        private Dictionary<string,FileBackupView> fileViewsSnapshot;
        private async Task PrepareCurrentTreeAsync()
        {
            var task=CurrentTask;
            var sources=task==null?new List<LoadedSource>():task.SourceIds.Where(loaded.ContainsKey).Select(id=>loaded[id]).ToList();
            var rules=Backend.Rules();var protectedRoots=Backend.ProtectedRoots();
            string key=SourceViewContext+"|"+JsonSettings.Encode(rules)+"|"+string.Join("|",protectedRoots);
            if(preparedTree==Tree&&preparedTreeKey==key&&preparedSources.SequenceEqual(sources))
            {
                Tree.RefreshBackups(views);ShowSelectionSummary(Tree.SelectedFileCount,Tree.SelectedBytes);UpdateSelectionHeaders();return;
            }
            int generation=++treeBuildGeneration;string identity=Selection.Identity();var selection=CopySelection(Selection);
            var next=new FileTreeWorkspace();var nextViews=ReadFileViews(task==null?null:task.Id);
            await Task.Run(delegate{
                next.Build(sources,new BackupExclusions(rules),protectedRoots,delegate(FileTreeNode node,bool include){if(MutationBlocked||CurrentTask==null||task==null||CurrentTask.Id!=task.Id||Tree!=next)return;ChangeSelection(node,include);});next.Apply(selection,nextViews);
            });
            if(disposed||closeAfterWork||generation!=treeBuildGeneration||CurrentTask!=task)return;
            if(identity!=Selection.Identity()){await PrepareCurrentTreeAsync();return;}
            var state=CaptureSourceView();
            if(state.Valid&&state.Target!=null&&SourceNode(next,state.Target)==null){await Task.Run(()=>KeepSourceNavigationFolder(next,state,sources,new BackupExclusions(rules),protectedRoots,selection));if(disposed||closeAfterWork||generation!=treeBuildGeneration||CurrentTask!=task)return;state=CaptureSourceView();}
            if(identity!=Selection.Identity()){await PrepareCurrentTreeAsync();return;}
            Tree=next;views=nextViews;preparedTree=next;preparedTreeKey=key;preparedSources=sources;displayedSourceContext=SourceViewContext;PublishSourceView(state);
        }
        private Dictionary<string,FileBackupView> ReadFileViews(string taskId)
        {
            long stamp=Backend.Catalog.DataChangeStamp;
            if(fileViewsSnapshot!=null&&fileViewsTaskId==taskId&&fileViewsStamp==stamp)return fileViewsSnapshot;
            var result=(taskId==null?new List<FileBackupView>():Backend.LatestFileViewsForDisplay(taskId)).GroupBy(v=>v.ArchivePath,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.First(),StringComparer.OrdinalIgnoreCase);
            fileViewsTaskId=taskId;fileViewsStamp=stamp;fileViewsSnapshot=result;return result;
        }
    }
}
