using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Xml;
namespace Backup.Desktop
{
    public static partial class WindowsIntegration
    {
        private const string TaskNamespace="http://schemas.microsoft.com/windows/2004/02/mit/task";
        private static List<XmlElement> TaskElements(XmlNode node)
        {return node==null?new List<XmlElement>():node.ChildNodes.OfType<XmlElement>().Where(child=>child.NamespaceURI==TaskNamespace).ToList();}
        private static XmlElement TaskElement(XmlNode node,string name)
        {return TaskElements(node).SingleOrDefault(child=>child.LocalName==name);}
        private static string TaskValue(XmlNode node,string name,string defaultValue="")
        {var child=TaskElement(node,name);return child==null?defaultValue:child.InnerText.Trim();}
        private static bool? TaskBoolean(XmlNode node,string name,bool defaultValue)
        {var child=TaskElement(node,name);if(child==null)return defaultValue;try{return XmlConvert.ToBoolean(child.InnerText.Trim());}catch(FormatException){return null;}}
        private static bool TaskInteger(XmlNode node,string name,int expected,int defaultValue)
        {int value;return int.TryParse(TaskValue(node,name,defaultValue.ToString(CultureInfo.InvariantCulture)),NumberStyles.Integer,CultureInfo.InvariantCulture,out value)&&value==expected;}
        private static bool TaskDuration(XmlNode node,string name,TimeSpan expected,TimeSpan defaultValue)
        {var child=TaskElement(node,name);if(child==null)return expected==defaultValue;try{return XmlConvert.ToTimeSpan(child.InnerText.Trim())==expected;}catch(FormatException){return false;}catch(OverflowException){return false;}}
        private static bool TaskSet(XmlNode node,IEnumerable<string> expected)
        {var children=TaskElements(node);return children.All(child=>!TaskElements(child).Any())&&children.Count==children.Select(child=>child.LocalName).Distinct().Count()&&new HashSet<string>(children.Select(child=>child.LocalName),StringComparer.Ordinal).SetEquals(expected);}
        private static bool SameScheduleDirectory(string value,string expected)
        {
            try{return string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables((value??"").Trim().Trim('"'))).TrimEnd('\\','/'),Path.GetFullPath(expected).TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase);}
            catch(ArgumentException){return false;}catch(NotSupportedException){return false;}catch(IOException){return false;}
        }
        private static bool SameScheduleUser(string value)
        {using(var identity=WindowsIdentity.GetCurrent())return value==identity.User.Value||string.Equals(value,identity.Name,StringComparison.OrdinalIgnoreCase);}
        private static bool SameScheduleTime(string boundary,string scheduled)
        {
            try{DateTime start=XmlConvert.ToDateTime(boundary,XmlDateTimeSerializationMode.RoundtripKind);TimeSpan time;return start.Kind==DateTimeKind.Unspecified&&TimeSpan.TryParseExact(scheduled,@"hh\:mm",CultureInfo.InvariantCulture,out time)&&start.TimeOfDay==time;}
            catch(FormatException){return false;}catch(ArgumentException){return false;}
        }
        private static bool SameScheduleCycle(XmlNode trigger,BackupSchedule plan)
        {
            var types=TaskElements(trigger).Where(child=>child.LocalName.StartsWith("ScheduleBy",StringComparison.Ordinal)).ToList();if(types.Count!=1)return false;var cycle=types[0];
            if(plan.Frequency=="daily")return cycle.LocalName=="ScheduleByDay"&&TaskInteger(cycle,"DaysInterval",1,1);
            if(plan.Frequency=="weekly")return cycle.LocalName=="ScheduleByWeek"&&TaskInteger(cycle,"WeeksInterval",1,1)&&TaskSet(TaskElement(cycle,"DaysOfWeek"),plan.Days.Select(day=>((DayOfWeek)day).ToString()));
            if(plan.Frequency!="monthly"||cycle.LocalName!="ScheduleByMonth")return false;
            var days=TaskElements(TaskElement(cycle,"DaysOfMonth"));int dayValue;
            return days.Count==1&&days[0].LocalName=="Day"&&int.TryParse(days[0].InnerText.Trim(),NumberStyles.Integer,CultureInfo.InvariantCulture,out dayValue)&&dayValue==plan.MonthDay&&TaskSet(TaskElement(cycle,"Months"),new[]{"January","February","March","April","May","June","July","August","September","October","November","December"});
        }
        // Windows omits default values and normalizes set order. Compare behavior,
        // not XML serialization; omitted nondefault requirements still fail.
        private static string ScheduleDefinitionMismatch(WindowsScheduleRecord item,BackupSchedule plan,string executable)
        {
            var document=ReadTaskXml(item.Xml);var root=document.DocumentElement;
            if(root==null||root.LocalName!="Task"||root.NamespaceURI!=TaskNamespace)return "系统计划内容无法识别";
            var actions=TaskElement(root,"Actions");var triggers=TaskElement(root,"Triggers");
            var actionList=TaskElements(actions);var triggerList=TaskElements(triggers);
            if(actionList.Count!=1||triggerList.Count!=1||actionList[0].LocalName!="Exec"||triggerList[0].LocalName!="CalendarTrigger")return "系统计划包含额外动作或触发器";
            var action=actionList[0];var trigger=triggerList[0];var settings=TaskElement(root,"Settings");
            if(!IsCurrentExecutable(TaskValue(action,"Command"),executable)||!SameScheduleDirectory(TaskValue(action,"WorkingDirectory"),Path.GetDirectoryName(executable)))return "运行路径与当前安装目录不一致";
            if(TaskValue(action,"Arguments")!="--run-schedule "+plan.Id)return "启动参数与计划编号不一致";
            if(TaskBoolean(settings,"Enabled",true)!=true)return "系统计划被禁用，与软件状态不一致";
            if(!SameScheduleCycle(trigger,plan))return "系统计划周期与数据库不一致";
            if(TaskBoolean(trigger,"Enabled",true)!=true||!SameScheduleTime(TaskValue(trigger,"StartBoundary"),plan.Time)||TaskValue(trigger,"EndBoundary")!=""||TaskElement(trigger,"Repetition")!=null||!TaskDuration(trigger,"RandomDelay",TimeSpan.Zero,TimeSpan.Zero))return "系统计划时间或触发器状态不一致";
            var principals=TaskElements(TaskElement(root,"Principals"));
            if(principals.Count!=1||principals[0].LocalName!="Principal")return "系统计划的运行用户或执行规则不一致";
            var principal=principals[0];string context=actions.GetAttribute("Context");
            if(!SameScheduleUser(TaskValue(principal,"UserId"))||TaskValue(principal,"GroupId")!=""||TaskValue(principal,"LogonType")!="InteractiveToken"||TaskValue(principal,"RunLevel","LeastPrivilege")!="LeastPrivilege"||context.Length>0&&context!=principal.GetAttribute("id"))return "系统计划的运行用户或执行规则不一致";
            if(TaskBoolean(settings,"StartWhenAvailable",false)!=true||TaskValue(settings,"MultipleInstancesPolicy","IgnoreNew")!="Queue"||TaskBoolean(settings,"DisallowStartIfOnBatteries",true)!=false||TaskBoolean(settings,"StopIfGoingOnBatteries",true)!=false||TaskBoolean(settings,"RunOnlyIfIdle",false)!=false||TaskBoolean(settings,"RunOnlyIfNetworkAvailable",false)!=false||!TaskDuration(settings,"ExecutionTimeLimit",TimeSpan.Zero,TimeSpan.FromHours(72)))return "系统计划的运行用户或执行规则不一致";
            return null;
        }
    }
}
