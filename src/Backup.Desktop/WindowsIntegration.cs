using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Xml;
using Microsoft.Win32;
namespace Backup.Desktop
{
    public sealed class BackupSchedule
    {
        public string Id=Guid.NewGuid().ToString("N"),Frequency="daily",Time="02:00",LastRunUtc="";
        public string Name {get;set;}
        public string LastResult {get;set;}
        public BackupSchedule(){Name="新计划";LastResult="";}
        public bool Enabled=false,Backup=true,Integrity=true;
        public bool PendingImportConfirmation;
        public int MonthDay=1;public List<int> Days=new List<int>{1};public List<string> TaskIds=new List<string>();
        public string FrequencyText {get{return Frequency=="weekly"?"每周 "+string.Join("、",Days.Select(d=>new[]{"日","一","二","三","四","五","六"}[d]))+" "+Time:Frequency=="monthly"?"每月 "+MonthDay+" 日 "+Time:"每天 "+Time;}}
        public string ActionText {get{return (Backup?"备份":"")+(Backup&&Integrity?" + ":"")+(Integrity?"检查完整性":"");}}
        public string EnabledText {get{return Enabled&&!PendingImportConfirmation?"已启用":"已暂停";}}
    }
    public static partial class WindowsIntegration
    {
#if UI_TEST
        public static Action<string,string> RegisterOverride;
        public static Action<string> DeleteOverride;
#endif
        private static string Escape(string value){return SecurityElement.Escape(value??"");}
        public static string TaskName(string repository,string id){Guid parsed;if(!Guid.TryParseExact(repository,"N",out parsed)||!Guid.TryParseExact(id,"N",out parsed))throw new ArgumentException("计划编号无效。");return "Backup-"+repository+"-"+id;}
        public static void Validate(BackupSchedule plan,IEnumerable<string> taskIds)
        {
            Guid id;TimeSpan time;if(!Guid.TryParseExact(plan.Id,"N",out id)||string.IsNullOrWhiteSpace(plan.Name)||!TimeSpan.TryParseExact(plan.Time,@"hh\:mm",System.Globalization.CultureInfo.InvariantCulture,out time)||time.TotalDays>=1||!new[]{"daily","weekly","monthly"}.Contains(plan.Frequency)||plan.MonthDay<1||plan.MonthDay>31||plan.Days.Any(d=>d<0||d>6)||plan.Frequency=="weekly"&&plan.Days.Count==0||!plan.Backup&&!plan.Integrity||plan.TaskIds.Count==0||plan.TaskIds.Any(idValue=>!taskIds.Contains(idValue)))throw new ArgumentException("请填写有效名称和时间，选择周期、备份任务及至少一个执行动作。");
        }
        public static string ScheduleXml(BackupSchedule plan,string executable,DateTime now)
        {
            bool enabled=plan.Enabled&&!plan.PendingImportConfirmation;
            TimeSpan time=TimeSpan.ParseExact(plan.Time,@"hh\:mm",System.Globalization.CultureInfo.InvariantCulture);DateTime start=now.Date+time;if(start<=now)start=start.AddDays(1);
            if(plan.Frequency=="weekly")while(!plan.Days.Contains((int)start.DayOfWeek))start=start.AddDays(1);
            if(plan.Frequency=="monthly"){DateTime month=new DateTime(now.Year,now.Month,1);while(true){if(plan.MonthDay<=DateTime.DaysInMonth(month.Year,month.Month)){var day=month.AddDays(plan.MonthDay-1)+time;if(day>now){start=day;break;}}month=month.AddMonths(1);}}
            string trigger=plan.Frequency=="weekly"?"<ScheduleByWeek><WeeksInterval>1</WeeksInterval><DaysOfWeek>"+string.Join("",plan.Days.Distinct().Select(d=>"<"+((DayOfWeek)d)+"/>"))+"</DaysOfWeek></ScheduleByWeek>":plan.Frequency=="monthly"?"<ScheduleByMonth><DaysOfMonth><Day>"+plan.MonthDay+"</Day></DaysOfMonth><Months>"+string.Join("",new[]{"January","February","March","April","May","June","July","August","September","October","November","December"}.Select(m=>"<"+m+"/>"))+"</Months></ScheduleByMonth>":"<ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>";
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><RegistrationInfo><Description>"+Escape("Backup："+plan.Name)+"</Description></RegistrationInfo><Triggers><CalendarTrigger><StartBoundary>"+start.ToString("yyyy-MM-ddTHH:mm:ss",System.Globalization.CultureInfo.InvariantCulture)+"</StartBoundary><Enabled>"+enabled.ToString().ToLowerInvariant()+"</Enabled>"+trigger+"</CalendarTrigger></Triggers><Principals><Principal id=\"User\"><UserId>"+WindowsIdentity.GetCurrent().User.Value+"</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals><Settings><MultipleInstancesPolicy>Queue</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable><Enabled>"+enabled.ToString().ToLowerInvariant()+"</Enabled><ExecutionTimeLimit>PT0S</ExecutionTimeLimit></Settings><Actions Context=\"User\"><Exec><Command>"+Escape(executable)+"</Command><Arguments>--run-schedule "+plan.Id+"</Arguments><WorkingDirectory>"+Escape(Path.GetDirectoryName(executable))+"</WorkingDirectory></Exec></Actions></Task>";
        }
        public static void RegisterSchedule(string repository,BackupSchedule plan,string executable)
        {
            if(!plan.Enabled||plan.PendingImportConfirmation){DeleteSchedule(repository,plan.Id);return;}
            string name=TaskName(repository,plan.Id),xml=ScheduleXml(plan,executable,DateTime.Now);
#if UI_TEST
            if(RegisterOverride!=null){RegisterOverride(name,xml);return;}
#endif
            dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));object folder=null,registered=null;try{service.Connect();folder=service.GetFolder("\\");registered=((dynamic)folder).RegisterTask(name,xml,6,null,null,3,null);}finally{if(registered!=null)Marshal.ReleaseComObject(registered);if(folder!=null)Marshal.ReleaseComObject(folder);Marshal.ReleaseComObject(service);}
        }
        public static void DeleteSchedule(string repository,string id)
        {
            string name=TaskName(repository,id);
#if UI_TEST
            if(DeleteOverride!=null){DeleteOverride(name);return;}
#endif
            dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));object folder=null;try{service.Connect();folder=service.GetFolder("\\");try{((dynamic)folder).DeleteTask(name,0);}catch(Exception error){if(error.HResult!=unchecked((int)0x80070002))throw;}}finally{if(folder!=null)Marshal.ReleaseComObject(folder);Marshal.ReleaseComObject(service);}
        }
        public static bool HasCurrentSchedulePath(string repository,string id,string executable)
        {
#if UI_TEST
            if(RegisterOverride!=null)return true;
#endif
            dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));object folder=null,task=null,definition=null,actions=null,action=null;
            try{service.Connect();folder=service.GetFolder("\\");try{task=((dynamic)folder).GetTask(TaskName(repository,id));}catch(Exception error){if(error.HResult==unchecked((int)0x80070002))return false;throw;}definition=((dynamic)task).Definition;actions=((dynamic)definition).Actions;action=((dynamic)actions).Item(1);return IsCurrentExecutable((string)((dynamic)action).Path,executable);}
            finally{foreach(var value in new[]{action,actions,definition,task,folder})if(value!=null)Marshal.ReleaseComObject(value);Marshal.ReleaseComObject(service);}
        }
    }
}
