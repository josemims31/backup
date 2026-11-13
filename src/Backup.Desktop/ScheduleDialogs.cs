using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Backup.Desktop
{
    public static partial class EditorDialogs
    {
        public static BackupSchedule Schedule(Window owner, WorkspaceDefinition workspace, BackupSchedule original)
        {
            var draft = original == null ? new BackupSchedule() : JsonSettings.Decode(JsonSettings.Encode(original), new BackupSchedule());
            StackPanel body;
            Button save;
            var window = Form(owner, original == null ? "添加任务计划" : "编辑任务计划", out body, out save);
            var name = Text(window, body, "Name", "名称", draft.Name);
            var frequency = new ComboBox
            {
                ItemsSource = new[]
                {
                    "每天",
                    "每周",
                    "每月"
                },
                SelectedIndex = draft.Frequency == "weekly" ? 1 : draft.Frequency == "monthly" ? 2 : 0
            };
            window.RegisterName("Frequency", frequency);
            body.Children.Add(frequency);
            var time = Text(window, body, "Time", "时间（24小时，HH:mm）", draft.Time);
            var weeklyPanel = new StackPanel();
            body.Children.Add(weeklyPanel);
            var days = Enumerable.Range(0, 7).Select(d => new ChoiceRow { Id = d.ToString(), Name = "周" + new[] { "日", "一", "二", "三", "四", "五", "六" }[d], Selected = draft.Days.Contains(d) }).ToList();
            weeklyPanel.Children.Add(new TextBlock { Text = "每周日期", Margin = new Thickness(0, 12, 0, 4) });
            var weekdayChoices = new WrapPanel();
            window.RegisterName("Days", weekdayChoices);
            weeklyPanel.Children.Add(weekdayChoices);
            foreach (var row in days)
            {
                var check = new CheckBox
                {
                    Content = row.Name,
                    IsChecked = row.Selected,
                    Margin = new Thickness(2, 7, 18, 7)
                };
                check.Checked += delegate
                {
                    row.Selected = true;
                };
                check.Unchecked += delegate
                {
                    row.Selected = false;
                };
                weekdayChoices.Children.Add(check);
            }

            var monthlyPanel = new StackPanel();
            body.Children.Add(monthlyPanel);
            var monthDay = Text(window, monthlyPanel, "MonthDay", "每月日期（1-31，无此日期的月份跳过）", draft.MonthDay.ToString());
            Action refreshPeriod = () =>
            {
                weeklyPanel.Visibility = frequency.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
                monthlyPanel.Visibility = frequency.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            };
            frequency.SelectionChanged += delegate
            {
                refreshPeriod();
            };
            refreshPeriod();
            var selection = new Grid
            {
                Margin = new Thickness(0, 8, 0, 0)
            };
            selection.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            selection.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.Children.Add(selection);
            var taskPanel = new StackPanel();
            selection.Children.Add(taskPanel);
            var actionPanel = new StackPanel
            {
                Margin = new Thickness(16, 0, 0, 0)
            };
            Grid.SetColumn(actionPanel, 1);
            selection.Children.Add(actionPanel);
            actionPanel.Children.Add(new TextBlock { Text = "执行操作", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 8) });
            var tasks = Checks(window, taskPanel, "Tasks", "关联任务", workspace.Tasks.Where(t => !t.Deleted).Select(t => new ChoiceRow { Id = t.Id, Name = t.Name, Selected = draft.TaskIds.Contains(t.Id) }));
            var backup = new CheckBox
            {
                Content = "备份",
                IsChecked = draft.Backup,
                Margin = new Thickness(0, 8, 0, 6)
            };
            var integrity = new CheckBox
            {
                Content = "检查完整性",
                IsChecked = draft.Integrity,
                Margin = new Thickness(0, 4, 0, 6),
                ToolTip = "检查后自动修复关联任务的异常备份包，无法修复的项目保留待重试。"
            };
            window.RegisterName("Backup", backup);
            window.RegisterName("Integrity", integrity);
            actionPanel.Children.Add(backup);
            actionPanel.Children.Add(integrity);
            body.Children.Add(new TextBlock {
                Text = "保存后为暂停状态，点击启用才会添加到 Windows 任务计划程序，多个计划会排队执行。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
                Foreground = System.Windows.Media.Brushes.SlateGray
            });
            Save(
                save,
                window,
                delegate
            {
                int day;
                if (!int.TryParse(monthDay.Text, out day))
                    throw new ArgumentException("每月日期无效。");
                draft.Name = name.Text.Trim();
                draft.Enabled = false;
                draft.Frequency = new[]
                {
                    "daily",
                    "weekly",
                    "monthly"
                }[frequency.SelectedIndex];
                draft.Time = time.Text.Trim();
                draft.MonthDay = day;
                draft.Days = days.Where(d => d.Selected).Select(d => int.Parse(d.Id)).ToList();
                draft.TaskIds = tasks.Where(t => t.Selected).Select(t => t.Id).ToList();
                draft.Backup = backup.IsChecked == true;
                draft.Integrity = integrity.IsChecked == true;
                WindowsIntegration.Validate(draft, workspace.Tasks.Where(t => !t.Deleted).Select(t => t.Id));
            }
            );
            return Show(window) ? draft : null;
        }
    }
}
