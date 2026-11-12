using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace Backup.Desktop
{
    internal static class OperationTextColors
    {
        internal static readonly Brush Working=Brushes.DarkGoldenrod,Completed=Brushes.ForestGreen,Failed=Brushes.Firebrick;
        internal static Brush StateColor(OperationState state,Brush normal)
        {switch(state){case OperationState.Running:case OperationState.Attention:return Working;case OperationState.Succeeded:return Completed;case OperationState.Failed:return Failed;case OperationState.Cancelled:return Brushes.SlateGray;default:return normal;}}
        internal static Action BindState(Window owner,TextBlock text,Func<OperationState> state)
        {Brush normal=text.Foreground;var property=DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty,typeof(TextBlock));Action refresh=()=>text.Foreground=StateColor(state(),normal);EventHandler changed=delegate{refresh();};property.AddValueChanged(text,changed);owner.Closed+=delegate{property.RemoveValueChanged(text,changed);};refresh();return refresh;}
        internal static Action Bind(Window owner,TextBlock text,Func<bool> running=null,Func<bool> completed=null)
        {
            Brush normal=text.Foreground;var property=DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty,typeof(TextBlock));
            Action refresh=()=>text.Foreground=Color(text.Text,running!=null&&running(),completed!=null&&completed(),normal);
            EventHandler changed=delegate{refresh();};property.AddValueChanged(text,changed);owner.Closed+=delegate{property.RemoveValueChanged(text,changed);};refresh();return refresh;
        }
        internal static Brush Color(string text,bool running,bool completed,Brush normal)
        {
            text=text??"";if(text.Length==0)return normal;
            if(text.Contains("数据库快照未完成")||text.Contains("数据库同步未完成")||text.Contains("数据库快照待重试")||text.Contains("最终数据库快照待同步")||text.StartsWith("计划执行结束：",StringComparison.Ordinal))return Failed;
            if(System.Text.RegularExpressions.Regex.IsMatch(text,@"(?<!\d)[1-9]\d*\s*(项待(?:确认)?修复|个待修复)")||text.Contains("操作未完成")||text.Contains("检查未完成")||text.Contains("修复未完成")||text.Contains("以下项目待重试"))return Failed;
            if(System.Text.RegularExpressions.Regex.IsMatch(text,@"(?<!\d)[1-9]\d*\s*(项暂不可访问|项待核对|项待确认|个目录暂不可访问|个目录读取失败|个备份包待重试|个压缩包副本不足)"))return Working;
            if(text.Contains("副本状态需处理"))return Working;
            if(new[]{"操作未完成","检查未完成","连接失败","连接超时","文件列表刷新未完成","无法","原凭据无法","上传失败","失败","未完成","压缩包已丢失","压缩包大小与备份记录不符","压缩包未完整备份"}.Any(text.StartsWith)||text.Contains(" · 包不完整"))return Failed;
            if(text.StartsWith("正在")||text.StartsWith("准备")||text.StartsWith("备份中")||text.StartsWith("已请求暂停")||text.StartsWith("已请求停止")||text.Contains(" · 正在备份"))return Working;
            if(completed||new[]{"重试成功","已完成","已下载","下载完成","已保存","已刷新","索引读取完成","检查完成","连接检查完成","恢复完成","清理完成","清理结束","修复结束","已确认的副本修复完成","数据库导入完成","数据库备份完成","计划执行完成","仓库路径迁移完成","当前任务备份完成","备份结束","没有新增或变动文件","任务已删除","拖放下载已结束","仓库文件下载完成"}.Any(text.StartsWith)||text.EndsWith(" · 已完成"))return Completed;
            return running?Working:normal;
        }
    }
    public sealed partial class MainWindowController
    {
        private readonly List<Action> statusColorRefreshers=new List<Action>();
        private readonly Dictionary<string,Brush> operationNormalColors=new Dictionary<string,Brush>();
        private void ApplyOperationForeground(){foreach(var pair in operationNormalColors)((DependencyObject)Window.FindName(pair.Key)).SetValue(System.Windows.Documents.TextElement.ForegroundProperty,OperationTextColors.StateColor(operationState,pair.Value));}
        private void InitializeStatusColors()
        {
            foreach(string name in new[]{"StatusText","SourceNotice","WarehouseNotice"})operationNormalColors[name]=(Brush)((DependencyObject)Window.FindName(name)).GetValue(System.Windows.Documents.TextElement.ForegroundProperty);
            statusColorRefreshers.Add(OperationTextColors.BindState(Window,Text("HistoryCheckSummary"),()=>historySummaryState));ApplyOperationForeground();
            foreach(string name in new[]{"HistoryEmptyNotice","HistoryDetails","FileDetails"})statusColorRefreshers.Add(OperationTextColors.Bind(Window,Text(name)));
        }
        private void RefreshStatusColors(){ApplyOperationForeground();foreach(var refresh in statusColorRefreshers)refresh();}
    }
    public sealed class OperationForegroundConverter:System.Windows.Data.IValueConverter
    {
        public object Convert(object value,Type target,object parameter,System.Globalization.CultureInfo culture){return HistoryStatus.Color(value as string);}
        public object ConvertBack(object value,Type target,object parameter,System.Globalization.CultureInfo culture){throw new NotSupportedException();}
    }
}
