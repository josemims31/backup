using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Backup.Desktop
{
    public static partial class EditorDialogs
    {
        internal static Window CreateDatabaseImportDialog(Window owner,DatabaseImportPreview preview,out Func<DatabaseImportSelection> read)
        {
            StackPanel body;Button save;var window=Form(owner,"导入数据库",out body,out save,true);window.Width=900;save.Content="导入";
            var modes=new StackPanel{Orientation=Orientation.Horizontal};body.Children.Add(modes);
            var replace=new RadioButton{Name="ImportReplace",Content="覆盖",IsChecked=true,Margin=new Thickness(0,0,25,10)};
            var merge=new RadioButton{Name="ImportMerge",Content="合并",Margin=new Thickness(0,0,0,10)};window.RegisterName(replace.Name,replace);window.RegisterName(merge.Name,merge);modes.Children.Add(replace);modes.Children.Add(merge);
            var notice=new TextBlock{Text="覆盖当前数据库，保留现有覆盖导入方式。",Margin=new Thickness(0,0,0,12),TextWrapping=TextWrapping.Wrap};body.Children.Add(notice);
            var choices=new Grid{Visibility=Visibility.Collapsed};body.Children.Add(choices);
            var lists=new[]{preview.Tasks,preview.Warehouses,preview.Schedules};var titles=new[]{"任务","仓库","定时任务"};
            for(int i=0;i<lists.Length;i++)
            {
                choices.ColumnDefinitions.Add(new ColumnDefinition());var column=new StackPanel{Margin=new Thickness(i==0?0:10,0,0,0)};Grid.SetColumn(column,i);choices.Children.Add(column);
                var rows=lists[i];var all=new CheckBox{Content=titles[i]+"（全选）",IsChecked=true,Margin=new Thickness(0,0,0,8)};column.Children.Add(all);
                var list=new ListBox{Name="ImportItems"+i,ItemsSource=rows,MinHeight=190,MaxHeight=320,BorderThickness=new Thickness(1)};window.RegisterName(list.Name,list);
                var template=new DataTemplate();var checkbox=new FrameworkElementFactory(typeof(CheckBox));checkbox.SetBinding(CheckBox.IsCheckedProperty,new Binding("Selected"){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});checkbox.SetBinding(CheckBox.ContentProperty,new Binding("Name"));checkbox.SetBinding(CheckBox.ToolTipProperty,new Binding("ToolTip"));checkbox.SetValue(CheckBox.MarginProperty,new Thickness(3,4,3,4));template.VisualTree=checkbox;list.ItemTemplate=template;column.Children.Add(list);
                all.Click+=delegate{foreach(var row in rows)row.Selected=all.IsChecked==true;list.Items.Refresh();};
                if(rows.Count==0)column.Children.Add(new TextBlock{Text="无可导入项目",Margin=new Thickness(4,8,0,0)});
            }
            Action refresh=()=>{choices.Visibility=merge.IsChecked==true?Visibility.Visible:Visibility.Collapsed;notice.Text=merge.IsChecked==true?"选择需要合并的项目。现有内容保留；导入的定时任务需手动确认后启用。":"覆盖当前数据库，保留现有覆盖导入方式。";};replace.Checked+=delegate{refresh();};merge.Checked+=delegate{refresh();};
            Func<DatabaseImportSelection> selection=()=>new DatabaseImportSelection{Merge=merge.IsChecked==true,TaskIds=preview.Tasks.Where(r=>r.Selected).Select(r=>r.Id).ToList(),WarehouseIds=preview.Warehouses.Where(r=>r.Selected).Select(r=>r.Id).ToList(),ScheduleIds=preview.Schedules.Where(r=>r.Selected).Select(r=>r.Id).ToList()};read=selection;
            save.Click+=delegate{var selected=selection();if(selected.Merge&&selected.TaskIds.Count+selected.WarehouseIds.Count+selected.ScheduleIds.Count==0){MessageBox.Show(window,"请至少勾选一个导入项目。","导入数据库",MessageBoxButton.OK,MessageBoxImage.Information);return;}window.DialogResult=true;};return window;
        }
        public static DatabaseImportSelection DatabaseImport(Window owner,DatabaseImportPreview preview)
        {
            Func<DatabaseImportSelection> read;var window=CreateDatabaseImportDialog(owner,preview,out read);return Show(window)?read():null;
        }
    }
}
