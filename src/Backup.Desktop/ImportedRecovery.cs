using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        private static void ProtectImportedRecovery(BackupCatalog catalog)
        {
            var workspace=WorkspaceMigration.Load(catalog);
            foreach(var task in workspace.Tasks.Where(task=>!task.Deleted))task.RecoveryPendingConfirmation=true;
            catalog.SetSetting("workspace_v3",JsonSettings.Encode(workspace));
            var schedules=JsonSettings.Decode(catalog.GetSetting("backup_schedules_v1"),new List<BackupSchedule>());
            foreach(var plan in schedules){plan.Enabled=false;plan.PendingImportConfirmation=true;plan.LastResult="导入后已停用，请在恢复并核对数据后手动确认。";}
            catalog.SetSetting("backup_schedules_v1",JsonSettings.Encode(schedules));
            catalog.RemoveSetting(ScheduleRequestQueue.SettingKey);
        }
    }
    public sealed partial class MainWindowController
    {
#if UI_TEST
        public Func<string,bool> ImportedRecoveryConfirmation;
#endif
        private bool ConfirmImportedRecovery(string message,string title)
        {
#if UI_TEST
            if(ImportedRecoveryConfirmation!=null)return ImportedRecoveryConfirmation(message);
#endif
            return MessageBox.Show(Window,message,title,MessageBoxButton.OKCancel,MessageBoxImage.Warning,MessageBoxResult.Cancel)==MessageBoxResult.OK;
        }
        private bool ConfirmImportedTasks(IEnumerable<string> ids,string action)
        {
            var selected=new HashSet<string>(ids);var pending=Workspace.Tasks.Where(task=>selected.Contains(task.Id)&&task.RecoveryPendingConfirmation).ToList();
            if(pending.Count==0)return true;
            string message="以下任务来自导入的数据库：\n"+string.Join("、",pending.Select(task=>task.Name))+"\n\n请先完成数据恢复并核对本机源文件夹。继续后将按本机源文件判断删除、更新备份和清理失效包。\n确认已完成核对并继续"+action+"？";
            if(!ConfirmImportedRecovery(message,"确认导入任务"))return false;
            foreach(var task in pending)task.RecoveryPendingConfirmation=false;SaveWorkspace();return true;
        }
        private bool ConfirmImportedPlan(BackupSchedule plan,bool enable)
        {
            bool pending=plan.PendingImportConfirmation;
            if(!pending)return ConfirmImportedTasks(plan.TaskIds,enable?"启用计划":"立即运行");
            string message="确认"+(enable?"启用":"立即运行")+"导入的任务计划“"+plan.Name+"”？\n\n请先完成数据恢复并核对源文件夹与备份目标。继续后将按本机源文件判断删除、更新备份和清理失效包。";
            if(!ConfirmImportedRecovery(message,"确认导入任务计划"))return false;
            foreach(var task in Workspace.Tasks.Where(task=>plan.TaskIds.Contains(task.Id)))task.RecoveryPendingConfirmation=false;
            SaveWorkspace();plan.PendingImportConfirmation=false;plan.LastResult="已手动确认导入的任务计划。";
            return true;
        }
    }
}
