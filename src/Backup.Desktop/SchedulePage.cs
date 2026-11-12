using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
namespace Backup.Desktop
{
    public sealed class ScheduleListRow
    {
        public BackupSchedule Plan {get;private set;}
        public ScheduleIssue Issue;
        public string RowKey{get{return Plan==null?"system:"+Issue.Key:Plan.Id;}}
        public System.Windows.Visibility RepairVisibility{get{return Issue==null||!Issue.CanRepair?System.Windows.Visibility.Collapsed:System.Windows.Visibility.Visible;}}
        public string RepairHint{get{return Issue==null?"":Issue.Reason+"\n"+Issue.RepairHint;}}
        public System.Windows.Media.Brush StatusColor{get{return Issue!=null&&Issue.Unassociated?System.Windows.Media.Brushes.SlateGray:Issue!=null||Plan==null||!Plan.Enabled||Plan.PendingImportConfirmation?System.Windows.Media.Brushes.Firebrick:System.Windows.Media.Brushes.ForestGreen;}}
        public string TaskNames {get;private set;}
        public string Name {get{return Plan==null?Issue.Name:Plan.Name;}}
        public string FrequencyText {get{return Plan==null?"Windows 系统计划":Plan.FrequencyText;}}
        public string ActionText {get{return Plan==null?"待处理":Plan.ActionText;}}
        public string EnabledText {get{return Plan==null?(Issue.Unassociated?"未关联":"异常"):Plan.EnabledText+(Issue==null?"":"（异常）");}}
        public string LastResult {get{return Issue==null?Plan.LastResult:Issue.Reason;}}
        public ScheduleListRow(ScheduleIssue issue){Issue=issue;TaskNames="无法关联当前数据库";}
        public ScheduleListRow(BackupSchedule plan,IDictionary<string,BackupTaskDefinition> tasks)
        {Plan=plan;TaskNames=string.Join("、",plan.TaskIds.Distinct().Select(id=>{BackupTaskDefinition task;return tasks.TryGetValue(id,out task)?task.Name+(task.Deleted?"（已删除）":""):"（任务不存在）";}));}
    }
    public sealed partial class MainWindowController
    {
        private BackupSchedule selectedSchedule;
        private bool manualScheduleRunning;
        private void InitializeSchedulePage()
        {
            Grid("ScheduleGrid").SelectionChanged+=delegate{var row=Grid("ScheduleGrid").SelectedItem as ScheduleListRow;selectedSchedule=row==null?null:row.Plan;UpdateScheduleRunButton();};
            Button("AddSchedule").Click+=delegate{if(!MutationBlocked)Guard(()=>EditSchedulePlan(null));};
            Button("EditSchedule").Click+=delegate{if(!MutationBlocked&&selectedSchedule!=null)Guard(()=>EditSchedulePlan(selectedSchedule));};
            Button("DeleteSchedule").Click+=async delegate{if(!MutationBlocked&&selectedSchedule!=null)await GuardAsync(async delegate{string id=selectedSchedule.Id;await RunScheduleMutationAsync(()=>Backend.DeleteSchedule(id));});};
            Button("ToggleSchedule").Click+=async delegate{await GuardAsync(ToggleSelectedScheduleAsync);};
            Grid("ScheduleGrid").AddHandler(System.Windows.Controls.Button.ClickEvent,new System.Windows.RoutedEventHandler(async delegate(object sender,System.Windows.RoutedEventArgs e){var button=e.OriginalSource as Button;var row=button==null?null:button.DataContext as ScheduleListRow;if(button==null||(string)button.Tag!="RepairSystemSchedule"||row==null||row.Issue==null||MutationBlocked)return;e.Handled=true;await GuardAsync(async delegate{await RunScheduleMutationAsync(()=>Backend.RepairSystemSchedule(row.Issue));});}));
            Button("RunScheduleNow").Click+=async delegate{await GuardAsync(RunSelectedScheduleAsync);};
            Grid("ScheduleGrid").MouseDoubleClick+=delegate{if(!MutationBlocked&&selectedSchedule!=null)Guard(()=>EditSchedulePlan(selectedSchedule));};
            Control<TabControl>("Tabs").SelectionChanged+=delegate(object sender,SelectionChangedEventArgs e){if(e.OriginalSource==Control<TabControl>("Tabs")&&!busy&&Control<TabControl>("Tabs").SelectedIndex==4){RefreshSchedulePage();ObserveScheduleInspection();}};
            RefreshSchedulePage();
        }
        public void RefreshSchedulePage(string selectedId=null)
        {
            string id=selectedId??(selectedSchedule==null?null:selectedSchedule.Id);var tasks=Workspace.Tasks.ToDictionary(t=>t.Id);var rows=Backend.Schedules().Select(plan=>new ScheduleListRow(plan,tasks)).ToList();foreach(var issue in scheduleIssues){var row=rows.FirstOrDefault(item=>item.Plan!=null&&item.Plan.Id==issue.PlanId&&item.Issue==null);if(row!=null)row.Issue=issue;else rows.Add(new ScheduleListRow(issue));}ReplaceGridRows(Grid("ScheduleGrid"),rows,row=>row.RowKey);Grid("ScheduleGrid").SelectedItem=rows.FirstOrDefault(row=>row.Plan!=null&&row.Plan.Id==id)??rows.FirstOrDefault();UpdateScheduleRunButton();
        }
        private void UpdateScheduleRunButton(){bool available=!MutationBlocked&&!manualScheduleRunning&&!scheduleExecuting&&selectedSchedule!=null;Button("RunScheduleNow").IsEnabled=available;Button("ToggleSchedule").IsEnabled=available;Button("ToggleSchedule").Content=selectedSchedule!=null&&selectedSchedule.Enabled?"暂停":"启用";Button("EditSchedule").IsEnabled=available;Button("DeleteSchedule").IsEnabled=available;Grid("ScheduleGrid").Tag=!MutationBlocked&&!scheduleExecuting;}
        public Task RunSelectedScheduleAsync()
        {return RunOperationAsync(OperationKind.Schedule,()=>RunSelectedScheduleCoreAsync());}
        private async Task RunSelectedScheduleCoreAsync()
        {
            if(busy||manualScheduleRunning||scheduleExecuting)return;if(selectedSchedule==null)throw new InvalidOperationException("请先选择任务计划。");string id=selectedSchedule.Id;manualScheduleRunning=true;UpdateScheduleRunButton();
            try{if(disposed)return;if(busy)throw new InvalidOperationException("已有操作正在执行，请完成后重试。");var saved=Backend.Schedules().FirstOrDefault(plan=>plan.Id==id);if(saved==null)throw new InvalidOperationException("所选计划已不存在，请刷新列表。");bool imported=saved.PendingImportConfirmation||Workspace.Tasks.Any(task=>saved.TaskIds.Contains(task.Id)&&task.RecoveryPendingConfirmation);if(!ConfirmImportedPlan(saved,false))return;if(imported){var plans=Backend.Schedules();var confirmed=plans.Single(item=>item.Id==saved.Id);confirmed.PendingImportConfirmation=false;confirmed.LastResult=saved.LastResult;Backend.Catalog.SaveScheduleConfiguration(JsonSettings.Encode(plans));}await ExecuteScheduleAsync(JsonSettings.Decode(JsonSettings.Encode(saved),new BackupSchedule()));}
            finally{manualScheduleRunning=false;if(!disposed)UpdateScheduleRunButton();}
        }
        private void UpdateScheduleResult(string id,string result){foreach(var row in Grid("ScheduleGrid").Items.OfType<ScheduleListRow>().Where(row=>row.Plan!=null&&row.Plan.Id==id))row.Plan.LastResult=result;Grid("ScheduleGrid").Items.Refresh();}
        private void EditSchedulePlan(BackupSchedule original)
        {var plan=EditorDialogs.Schedule(Window,Workspace,original);if(plan==null)return;RunConfiguration(delegate{Backend.SaveSchedule(plan);RefreshSchedulePage(plan.Id);ObserveScheduleInspection();});}
    }
}
