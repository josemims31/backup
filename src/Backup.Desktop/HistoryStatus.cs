using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using Backup.Core;
namespace Backup.Desktop
{
    // Health is a single primary state. Version markers qualify historical rows only.
    internal static class HistoryStatus
    {
        private static string Normalize(string value){return string.IsNullOrEmpty(value)?"unknown":value.StartsWith("unavailable:",StringComparison.Ordinal)?"unavailable":value;}
        private static int Rank(string value)
        {
            switch(Normalize(value)){
                case "content-damaged":case "damaged":case "failed":return 0;case "missing":return 1;case "size-or-name-mismatch":return 2;case "registration-mismatch":case "repair-required":return 3;
                case "reconcile-required":return 4;case "writing":case "interrupted":return 5;case "unavailable":return 6;case "redundancy-shortfall":return 7;
                case "pending":return 8;case "unknown":return 9;case "retired":return 10;case "cleaned":return 11;case "complete":case "present":case "present-size-matched":case "snapshot-history":return 12;default:return 9;
            }
        }
        internal static string Worse(string left,string right){return Rank(left)<=Rank(right)?Normalize(left):Normalize(right);}
        internal static string Combined(IEnumerable<string> states)
        {string result=null;foreach(string value in states)result=result==null?Normalize(value):Worse(result,value);return result??"unknown";}
        internal static string Text(string value)
        {
            switch(Normalize(value)){
                case "complete":case "present":case "present-size-matched":case "snapshot-history":return "正常";
                case "registration-mismatch":return "待修复（登记不一致）";case "repair-required":return "待修复";case "missing":return "缺失";case "size-or-name-mismatch":return "待修复（大小不符）";
                case "content-damaged":case "damaged":return "损坏";case "failed":case "interrupted":return "备份未完成";case "writing":return "备份中";case "reconcile-required":return "待确认";
                case "unavailable":return "暂不可访问";case "redundancy-shortfall":return "副本不足";case "pending":return "待处理";
                case "retired":return "已移除";case "cleaned":return "已清理";default:return "待核对";
            }
        }
        internal static bool NeedsRepair(string value)
        {return value=="registration-mismatch"||value=="未登记备份包"||value=="content-damaged"||value=="damaged"||value=="failed"||value=="interrupted"||value=="repair-required"||value=="missing"||value=="size-or-name-mismatch"||!string.IsNullOrEmpty(value)&&(value.StartsWith("损坏",StringComparison.Ordinal)||value.StartsWith("备份未完成",StringComparison.Ordinal)||value.Contains("待修复")||value.Contains("待确认修复")||value.Contains("缺失")||value.Contains("副本异常"));}
        internal static Brush Color(string value)
        {
            value=value??"";
            if(NeedsRepair(value)||value=="failed"||value.Contains("备份失败")||value.Contains("包不完整")||value=="失败"||value=="备份未完成")return OperationTextColors.Failed;
            if(value=="正常"||value=="complete"||value=="已完成")return OperationTextColors.Completed;
            if(new[]{"writing","reconcile-required","unavailable","redundancy-shortfall","pending"}.Contains(value)||new[]{"备份中","待确认","待核对","暂不可访问","副本不足","待处理","待备份","已变化，待备份","整理中"}.Any(value.StartsWith))return OperationTextColors.Working;
            if(value=="旧版"||value=="已删除"||value=="已失效"||value=="已清理"||value=="已移除"||value=="未登记"||value=="无备份记录"||value=="无当前有效文件")return Brushes.SlateGray;
            return OperationTextColors.Color(value,false,false,Brushes.Black);
        }
        internal static string Code(FileHistoryRow row,bool overall)
        {
            string code=Normalize(row.PlacementStatus);
            if(row.ArchiveStatus=="damaged"||row.ArchiveStatus=="failed"||row.ArchiveStatus=="writing"||row.ArchiveStatus=="interrupted"||row.ArchiveStatus=="reconcile-required")return row.ArchiveStatus;
            if(overall&&code=="failed")code="repair-required";
            if(overall&&(row.ArchiveStatus=="repair-required"||row.ArchiveStatus=="redundancy-shortfall"))code=Worse(code,row.ArchiveStatus);
            return code;
        }
        internal static string File(FileHistoryRow row,string health)
        {
            if(row.FileState=="staged")return "整理中";
            if(row.ArchiveStatus=="damaged"||row.ArchiveStatus=="failed"||row.ArchiveStatus=="writing"||row.ArchiveStatus=="interrupted"||row.ArchiveStatus=="reconcile-required")health=row.ArchiveStatus;
            string status=Text(health),version=row.FileState=="expired"?"旧版":row.FileState=="deleted"?"已删除":"";
            if(version.Length==0||health=="failed"||health=="writing")return status;
            return Rank(health)==12?version:status+"（"+version+"）";
        }
        internal static string File(FileHistoryRow row,IEnumerable<FileHistoryRow> replicas,string destinationId=null)
        {return File(row,Combined(replicas.Where(value=>destinationId==null||value.DestinationId==destinationId).Select(value=>Code(value,destinationId==null))));}
        internal static HistoryStatusSummary Summarize(IEnumerable<FileHistoryRow> rows,string destinationId=null)
        {var result=new HistoryStatusSummary();foreach(var row in rows)if(destinationId==null||row.DestinationId==destinationId)result.Include(row,Code(row,destinationId==null));return result;}
        internal static string LocalProblem(BackupHistoryNode node)
        {
            if(node.IsMissingArchive)return "missing";if(node.Entry==null||node.ArchiveId==0||node.Entry.Directory)return null;
            if(node.RecordedCopyState!=null)return node.RecordedCopyState=="present"?null:Normalize(node.RecordedCopyState);
            return node.Entry.Size<0?"reconcile-required":node.Entry.Size!=node.ExpectedSize?"size-or-name-mismatch":null;
        }
        internal static string BrowseNotice(BackupHistoryNode node)
        {
            if(!node.ArchiveContainer)return null;
            if(node.State=="整理中")return "压缩包正在重新整合，完成后可查看包内文件。";
            if(node.State=="待确认"||LocalProblem(node)=="reconcile-required")return "压缩包上传结果待确认，确认完成后可查看和下载。";
            if(LocalProblem(node)=="registration-mismatch")return "压缩包名称和大小已核对，备份登记尚未同步，请运行修复备份完整性。";
            if(node.IsMissingArchive)return "压缩包已丢失，无法显示包内文件。\n可查看属性，或从其他仓库下载。";
            if(LocalProblem(node)=="size-or-name-mismatch")return "压缩包大小与备份记录不符，无法显示包内文件。\n请先修复，或从其他仓库下载。";
            if(node.State=="损坏"||node.State=="备份失败"||node.State=="备份未完成")return "压缩包未完整备份，无法显示包内文件。\n可查看属性，或重新执行备份任务。";
            return null;
        }
        internal static string Source(FileBackupView value)
        {
            if(value.Status=="failed"||value.Status=="writing")return Text(value.Status);
            if(value.Status=="repair-required"||value.FailedCopies>0)return "待修复";
            if(value.PendingCopies>0)return "待确认";if(value.UnavailableCopies>0)return "暂不可访问";
            return value.ValidCopies<value.TargetCopies?"副本不足":Text(value.Status);
        }
        internal static string SnapshotTime(FileHistoryRow row){return DateTime.FromFileTimeUtc(row.Modified).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");}
    }
    internal sealed class HistoryStatusSummary
    {
        private bool any,current,expired;
        private string all="complete",active="complete";
        internal void Include(FileHistoryRow row,string health)
        {any=true;all=HistoryStatus.Worse(all,health);if(row.FileState!="expired"&&row.FileState!="deleted"){current=true;active=HistoryStatus.Worse(active,health);}if(row.FileState=="expired")expired=true;}
        internal void Merge(HistoryStatusSummary other)
        {any|=other.any;current|=other.current;expired|=other.expired;all=HistoryStatus.Worse(all,other.all);active=HistoryStatus.Worse(active,other.active);}
        internal string Text {get{return !any?"无备份记录":current?HistoryStatus.Text(active):HistoryStatus.File(new FileHistoryRow{FileState=expired?"expired":"deleted"},all);}}
        internal bool HasCurrent {get{return current;}}
    }
}
