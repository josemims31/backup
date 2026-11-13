using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Backup.Core;

namespace Backup.Desktop
{
    public static class ExclusionDialog
    {
#if UI_TEST
        public static Action<Window> BeforeShow;
#endif
        public static List<ExclusionRule> Edit(Window owner, List<ExclusionRule> original)
        {
            var rows = new ObservableCollection<RuleRow>(original.Select(RuleRow.From));
            var window = new Window
            {
                Owner = owner,
                Title = "排除规则",
                Width = 820,
                Height = 490,
                MinWidth = 680,
                MinHeight = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                FontFamily = owner.FontFamily,
                FontSize = 13
            };
            NameScope.SetNameScope(window, new NameScope());
            var panel = new DockPanel
            {
                Margin = new Thickness(16)
            };
            window.Content = panel;
            var hint = new TextBlock
            {
                Text = "同时匹配文件和目录名称，匹配目录时排除其全部内容；普通匹配支持 * 和 ?，正则匹配保留原表达式。空文件夹不保存。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            DockPanel.SetDock(hint, Dock.Top);
            panel.Children.Add(hint);
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 12, 0, 0)
            };
            DockPanel.SetDock(buttons, Dock.Bottom);
            panel.Children.Add(buttons);
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                ItemsSource = rows,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                RowHeight = 31
            };
            window.RegisterName("RuleGrid", grid);
            var check = new FrameworkElementFactory(typeof(CheckBox));
            check.SetBinding(CheckBox.IsCheckedProperty, new Binding("Enabled") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            check.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            grid.Columns.Add(new DataGridTemplateColumn { Header = "启用", CellTemplate = new DataTemplate { VisualTree = check }, Width = new DataGridLength(52) });
            var types = new FrameworkElementFactory(typeof(ComboBox));
            types.SetValue(ItemsControl.ItemsSourceProperty, new[] { "普通匹配(支持通配符)", "正则匹配" });
            types.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, new Binding("Kind") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            grid.Columns.Add(new DataGridTemplateColumn { Header = "类型", CellTemplate = new DataTemplate { VisualTree = types }, Width = new DataGridLength(205) });
            foreach (string field in new[]
            {
                "Pattern",
                "Description"
            }

            )
            {
                var input = new FrameworkElementFactory(typeof(TextBox));
                input.SetValue(Control.BorderThicknessProperty, new Thickness(0));
                input.SetValue(Control.PaddingProperty, new Thickness(4));
                input.SetBinding(TextBox.TextProperty, new Binding(field) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
                grid.Columns.Add(new DataGridTemplateColumn {
                    Header = field == "Pattern" ? "匹配内容" : "说明",
                    CellTemplate = new DataTemplate { VisualTree = input },
                    Width = field == "Pattern" ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(170)
                });
            }

            panel.Children.Add(grid);
            Action<string, Action> add = delegate (string title, Action action)
            {
                var b = new Button
                {
                    Content = title,
                    Padding = new Thickness(13, 7, 13, 7),
                    Margin = new Thickness(0, 0, 8, 0)
                };
                b.Click += delegate
                {
                    action();
                };
                buttons.Children.Add(b);
            };
            add("新增", () => rows.Add(new RuleRow { Enabled = true, Kind = "普通匹配(支持通配符)", Pattern = "", Description = "" }));
            add("删除", () =>
            {
                var row = grid.SelectedItem as RuleRow;
                if (row != null)
                    rows.Remove(row);
            });
            add(
                "恢复默认",
                () =>
            {
                rows.Clear();
                foreach (var row in BackupExclusions.Defaults())
                    rows.Add(RuleRow.From(row));
            }
            );
            List<ExclusionRule> result = null;
            add(
                "保存",
                () =>
            {
                try
                {
                    grid.CommitEdit(DataGridEditingUnit.Cell, true);
                    grid.CommitEdit(DataGridEditingUnit.Row, true);
                    var rules = rows.Select(r => r.ToRule()).ToList();
                    new BackupExclusions(rules);
                    result = rules;
                    window.DialogResult = true;
                }
                catch (Exception e)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/ExclusionDialog.cs:Edit", e);
                    MessageBox.Show(window, e.Message, "规则无效", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            );
            add("取消", () => window.DialogResult = false);
#if UI_TEST
            if (BeforeShow != null)
                BeforeShow(window);
#endif
            window.ShowDialog();
            return result;
        }
    }
}
