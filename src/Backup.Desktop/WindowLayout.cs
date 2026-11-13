using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace Backup.Desktop
{
    public sealed class SavedColumn
    {
        public double Value;
        public int Unit;
    }

    public sealed class WindowLayoutState
    {
        public double Width = 1240, Height = 830, Left = double.NaN, Top = double.NaN;
        public bool Maximized;
        public double SourceWidth = 280, HistoryWidth = 260, LogHeight = 92, DetailsHeight = 120;
        public Dictionary<string, List<SavedColumn>> Columns = new Dictionary<string, List<SavedColumn>>();
    }

    public sealed partial class MainWindowController
    {
#if UI_TEST
        public int TabTransitionCount { get; private set; }

#endif
        private void InitializeWindowLayout()
        {
            WindowLayoutState state;
            try
            {
                state = JsonSettings.Decode(UiPreference("ui_layout_v1"), new WindowLayoutState());
            }
            catch (global::System.Exception diagnosticFailure)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/WindowLayout.cs:InitializeWindowLayout", diagnosticFailure);
                state = new WindowLayoutState();
            }

            var screen = SystemParameters.WorkArea;
            Window.MinWidth = Math.Min(Window.MinWidth, screen.Width);
            Window.MinHeight = Math.Min(Window.MinHeight, screen.Height);
            Window.Width = Clamp(state.Width, Window.MinWidth, Math.Max(Window.MinWidth, screen.Width));
            Window.Height = Clamp(state.Height, Window.MinHeight, Math.Max(Window.MinHeight, screen.Height));
            if (!double.IsNaN(state.Left) && !double.IsNaN(state.Top))
            {
                Window.WindowStartupLocation = WindowStartupLocation.Manual;
                Window.Left = Clamp(state.Left, screen.Left, Math.Max(screen.Left, screen.Right - Window.Width));
                Window.Top = Clamp(state.Top, screen.Top, Math.Max(screen.Top, screen.Bottom - Window.Height));
            }

            Control<ColumnDefinition>("SourceTreeColumn").Width = new GridLength(Clamp(state.SourceWidth, 160, Window.Width * .65));
            Control<ColumnDefinition>("HistoryTreeColumn").Width = new GridLength(Clamp(state.HistoryWidth, 160, Window.Width * .65));
            Control<RowDefinition>("TaskLogRow").Height = new GridLength(Clamp(state.LogHeight, 60, Window.Height * .35));
            Control<RowDefinition>("HistoryDetailsRow").Height = new GridLength(Clamp(state.DetailsHeight, 65, Window.Height * .4));
            if (state.Columns != null)
                foreach (var entry in state.Columns)
                {
                    var grid = Control<DataGrid>(entry.Key);
                    if (grid == null || entry.Value == null || entry.Key == "HistoryPreview" && UiPreference("history_columns_v2") != "1")
                        continue;
                    for (int i = 0; i < Math.Min(grid.Columns.Count, entry.Value.Count); i++)
                    {
                        var column = entry.Value[i];
                        if (column.Value > 0 && !double.IsInfinity(column.Value) && column.Unit >= 0 && column.Unit <= 4)
                            grid.Columns[i].Width = new DataGridLength(Math.Min(1500, column.Value), (DataGridLengthUnitType)column.Unit);
                    }
                }

            Grid("FileGrid").Columns[0].MinWidth = 132;
            if (state.Maximized)
                Window.WindowState = WindowState.Maximized;
            Control<Thumb>("LogResizer").DragDelta += delegate (object s, DragDeltaEventArgs e)
            {
                var row = Control<RowDefinition>("TaskLogRow");
                row.Height = new GridLength(Clamp(row.ActualHeight - e.VerticalChange, 60, Window.ActualHeight * .4));
            };
            Control<Thumb>("HistoryResizer").DragDelta += delegate (object s, DragDeltaEventArgs e)
            {
                var row = Control<RowDefinition>("HistoryDetailsRow");
                row.Height = new GridLength(Clamp(row.ActualHeight - e.VerticalChange, 65, Window.ActualHeight * .5));
            };
            var tabs = Control<TabControl>("Tabs");
            object previousPage = tabs.SelectedItem;
            int transitionGeneration = 0;
            tabs.SelectionChanged += delegate (object s, SelectionChangedEventArgs e)
            {
                // TabItem.IsMouseOver also includes its logical page content. A page transition
                // must be driven by an actual tab selection, never a nested selector's event.
                if (e.OriginalSource != tabs || ReferenceEquals(previousPage, tabs.SelectedItem))
                    return;
                var previous = previousPage as TabItem;
                var oldContent = previous == null ? null : previous.Content as FrameworkElement;
                if (oldContent != null)
                    oldContent.BeginAnimation(UIElement.OpacityProperty, null);
                previousPage = tabs.SelectedItem;
                int generation = ++transitionGeneration;
#if UI_TEST
                TabTransitionCount++;
#endif
                if (!SystemParameters.ClientAreaAnimation)
                    return;
                var selected = tabs.SelectedItem as TabItem;
                var content = selected == null ? null : selected.Content as FrameworkElement;
                if (content == null)
                    return;
                var animation = new DoubleAnimation(.65, 1, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new QuadraticEase
                    {
                        EasingMode = EasingMode.EaseOut
                    },
                    FillBehavior = FillBehavior.Stop
                };
                animation.Completed += delegate
                {
                    if (generation == transitionGeneration && ReferenceEquals(previousPage, selected))
                        content.BeginAnimation(UIElement.OpacityProperty, null);
                };
                content.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
            };
        }

        private static double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return min;
            return Math.Max(min, Math.Min(max, value));
        }

        private void SaveWindowLayout()
        {
            SaveUiPreference("history_columns_v2", "1");
            var bounds = Window.WindowState == WindowState.Normal ? new Rect(Window.Left, Window.Top, Window.ActualWidth, Window.ActualHeight) : Window.RestoreBounds;
            if (bounds.IsEmpty || double.IsInfinity(bounds.Width) || bounds.Width <= 0)
                bounds = new Rect(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Top, Window.Width, Window.Height);
            var state = new WindowLayoutState
            {
                Width = bounds.Width,
                Height = bounds.Height,
                Left = bounds.Left,
                Top = bounds.Top,
                Maximized = Window.WindowState == WindowState.Maximized,
                SourceWidth = Control<ColumnDefinition>("SourceTreeColumn").Width.Value,
                HistoryWidth = Control<ColumnDefinition>("HistoryTreeColumn").Width.Value,
                LogHeight = Control<RowDefinition>("TaskLogRow").Height.Value,
                DetailsHeight = Control<RowDefinition>("HistoryDetailsRow").Height.Value
            };
            foreach (string name in new[]
            {
                "SourceGrid",
                "WarehouseGrid",
                "FileGrid",
                "HistoryPreview",
                "ScheduleGrid"
            }

            )
                state.Columns[name] = Grid(name).Columns.Select(c => new SavedColumn { Value = c.Width.Value, Unit = (int)c.Width.UnitType }).ToList();
            SaveUiPreference("ui_layout_v1", JsonSettings.Encode(state));
        }
    }
}
