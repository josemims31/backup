using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
namespace Backup.Desktop
{
    public sealed class ScheduleIssue
    {
        public string PlanId,Reason,Key;public WindowsScheduleRecord SystemTask;
        public bool CanRepair=true,Unassociated;
        public string Name{get{return SystemTask==null?"系统计划缺失":SystemTask.DisplayName;}}
        public string RepairHint{get{return SystemTask!=null&&PlanId==null?"删除这个无法关联数据库的系统计划；不会自动重建或启用。":"按数据库中已保存的启用/暂停状态修复这个系统计划。";}}
    }
    public sealed partial class DesktopBackend
    {
        public IList<ScheduleIssue> InspectSystemSchedules()
        {
            string executable=Path.Combine(BaseDirectory,"Backup.exe");var plans=Schedules().ToDictionary(plan=>plan.Id,StringComparer.OrdinalIgnoreCase);var system=WindowsIntegration.ReadSystemSchedules();var issues=new List<ScheduleIssue>();var found=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var item in system.Where(item=>item.Related))
            {
                BackupSchedule plan=null;bool own=string.Equals(item.RepositoryId,RepositoryId,StringComparison.OrdinalIgnoreCase)&&item.PlanId!=null&&plans.TryGetValue(item.PlanId,out plan)&&WindowsIntegration.IsCurrentUser(item)&&!WindowsIntegration.IsOtherInstalledExecutable(item.Executable,executable);
                if(!own)
                {
                    bool local=WindowsIntegration.IsCurrentExecutable(item.Executable,executable)&&WindowsIntegration.IsCurrentUser(item);
                    issues.Add(new ScheduleIssue{Key=item.Path,SystemTask=item,CanRepair=local,Unassociated=!local,Reason=local?"当前安装目录遗留、数据库未登记的系统计划":"其他安装目录或归属不明，未关联当前数据库"});continue;
                }
                string canonical="\\"+WindowsIntegration.TaskName(RepositoryId,plan.Id);string problem=!string.Equals(item.Path,canonical,StringComparison.OrdinalIgnoreCase)?"旧版或重复的系统计划":!plan.Enabled||plan.PendingImportConfirmation?"计划已暂停，但系统中仍有注册记录":WindowsIntegration.ScheduleMismatch(item,plan,executable);
                if(string.Equals(item.Path,canonical,StringComparison.OrdinalIgnoreCase))found.Add(plan.Id);
                if(problem!=null)issues.Add(new ScheduleIssue{Key=item.Path,PlanId=plan.Id,SystemTask=item,Reason=problem});
            }
            foreach(var plan in plans.Values.Where(plan=>plan.Enabled&&!plan.PendingImportConfirmation&&!found.Contains(plan.Id)))issues.Add(new ScheduleIssue{Key="missing:"+plan.Id,PlanId=plan.Id,Reason="软件中已启用，但 Windows 系统计划不存在"});
            return issues;
        }
        public void RepairSystemSchedule(ScheduleIssue issue)
        {
            if(issue==null)throw new ArgumentException("请选择异常计划。");if(!issue.CanRepair)throw new InvalidOperationException("此计划不属于当前安装的异常记录，不能在这里修改。");
            var current=InspectSystemSchedules().FirstOrDefault(item=>item.Key==issue.Key);if(current==null)return;if(!current.CanRepair)throw new IOException("系统计划归属已变化，请重新检测。");issue=current;
            if(issue.SystemTask!=null)WindowsIntegration.DeleteSystemSchedule(issue.SystemTask);
            var plan=Schedules().FirstOrDefault(item=>item.Id==issue.PlanId);if(plan!=null&&plan.Enabled&&!plan.PendingImportConfirmation)WindowsIntegration.RegisterSchedule(RepositoryId,plan,Path.Combine(BaseDirectory,"Backup.exe"));
        }
        public void SetScheduleEnabled(string id,bool enabled)
        {
            var plans=Schedules();var plan=plans.Single(item=>item.Id==id);var previous=JsonSettings.Decode(JsonSettings.Encode(plan),new BackupSchedule());
            if(enabled&&(plan.PendingImportConfirmation||WorkspaceMigration.Load(Catalog).Tasks.Any(task=>plan.TaskIds.Contains(task.Id)&&task.RecoveryPendingConfirmation)))throw new InvalidOperationException("导入的任务计划尚未手动确认。");
            WindowsIntegration.Validate(plan,WorkspaceMigration.Load(Catalog).Tasks.Where(task=>!task.Deleted).Select(task=>task.Id));plan.Enabled=enabled;
            CommitScheduleChange(()=>WindowsIntegration.RegisterSchedule(RepositoryId,plan,Path.Combine(BaseDirectory,"Backup.exe")),()=>WindowsIntegration.RegisterSchedule(RepositoryId,previous,Path.Combine(BaseDirectory,"Backup.exe")),JsonSettings.Encode(plans));
        }
    }
    public sealed partial class MainWindowController
    {
        private IList<ScheduleIssue> scheduleIssues=new List<ScheduleIssue>();private int scheduleInspectionGeneration;
        private async void ObserveScheduleInspection(){if(!scheduledHost&&!scheduleExecuting)await InspectSchedulePageAsync();}
        private Task RunScheduleMutationAsync(Action mutation,string selectedId=null)
        {return RunOperationAsync(OperationKind.Configuration,()=>RunScheduleMutationCoreAsync(mutation,selectedId));}
        private async Task RunScheduleMutationCoreAsync(Action mutation,string selectedId=null)
        {
            if(busy)return;SetBusy(true,false,"正在更新 Windows 任务计划…");
            try
            {
                Exception failure=null;try{await Task.Run(mutation);}catch(Exception error){failure=error;}
                RefreshSchedulePage(selectedId);await InspectSchedulePageAsync();if(failure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                int pending=scheduleIssues.Count(issue=>issue.CanRepair);SetOperationText("已完成任务计划更新。"+(pending==0?"":" 系统计划："+pending+" 项待修复。"),pending==0?OperationState.Succeeded:OperationState.Failed);OperationProgress(100,false);
            }
            finally{SetBusy(false,false,null);}
        }
        private async Task InspectSchedulePageAsync()
        {
            int generation=++scheduleInspectionGeneration;try{IList<ScheduleIssue> issues;using(operations.Read(OperationKind.Check))issues=await Task.Run(()=>Backend.InspectSystemSchedules());if(disposed||generation!=scheduleInspectionGeneration)return;scheduleIssues=issues;RefreshSchedulePage();}
            catch(Exception error){if(!disposed&&generation==scheduleInspectionGeneration)Log("系统任务计划检测未完成："+error.Message);}
        }
        private Task ToggleSelectedScheduleAsync()
        {return RunOperationAsync(OperationKind.Configuration,()=>ToggleSelectedScheduleCoreAsync());}
        private async Task ToggleSelectedScheduleCoreAsync()
        {
            if(busy||selectedSchedule==null)return;var plan=Backend.Schedules().Single(item=>item.Id==selectedSchedule.Id);bool enabled=!plan.Enabled;
            if(enabled&&!ConfirmImportedPlan(plan,true))return;
            // Confirmation only changes imported safety flags; ordinary enable has no dialog.
            if(enabled&&plan.PendingImportConfirmation)throw new InvalidOperationException("请先确认导入计划。");
            if(enabled){var saved=Backend.Schedules();var original=saved.Single(item=>item.Id==plan.Id);if(original.PendingImportConfirmation){original.PendingImportConfirmation=false;original.LastResult=plan.LastResult;Backend.Catalog.SaveScheduleConfiguration(JsonSettings.Encode(saved));}}
            await RunScheduleMutationAsync(()=>Backend.SetScheduleEnabled(plan.Id,enabled),plan.Id);
        }
    }
}
