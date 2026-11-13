using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Backup.Desktop
{
    public sealed partial class MainWindowController
    {
        private void BindCopyableMessages()
        {
            BindLargeHistoryDetails();
            foreach (string name in new[]
            {
                "HistoryDetails",
                "HistoryCheckSummary"
            }

            )
            {
                var text = Text(name);
                text.Focusable = true;
                var menu = new ContextMenu();
                var copy = new MenuItem
                {
                    Header = "复制全部"
                };
                copy.Click += delegate
                {
                    CopyMessage(text.Text);
                };
                menu.Items.Add(copy);
                text.ContextMenu = menu;
                text.MouseLeftButtonDown += delegate
                {
                    text.Focus();
                };
                text.PreviewKeyDown += delegate (object sender, KeyEventArgs e)
                {
                    if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                    {
                        CopyMessage(text.Text);
                        e.Handled = true;
                    }
                };
            }
        }

        private void BindLargeHistoryDetails()
        {
            var text = Text("HistoryDetails");
            var shortView = Control<ScrollViewer>("HistoryDetailsScroll");
            var longView = Control<TextBox>("HistoryDetailsLong");
            var holder = (Grid)shortView.Parent;
            var border = (Border)holder.Parent;
            holder.Children.Clear();
            border.Child = shortView;
            longView.SetBinding(System.Windows.Controls.Control.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { Source = text });
            var property = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
            Action refresh = delegate
            {
                string value = text.Text ?? "";
                bool large = value.Length > 4096;
                UIElement current = large ? (UIElement)longView : shortView;
                if (!ReferenceEquals(border.Child, current))
                    border.Child = current;
                shortView.Visibility = large ? Visibility.Collapsed : Visibility.Visible;
                longView.Visibility = large ? Visibility.Visible : Visibility.Collapsed;
                if (large)
                {
                    if (longView.Text != value)
                        longView.Text = value;
                }
                else if (longView.Text.Length > 0)
                    longView.Clear();
            };
            EventHandler changed = delegate
            {
                refresh();
            };
            property.AddValueChanged(text, changed);
            Window.Closed += delegate
            {
                property.RemoveValueChanged(text, changed);
            };
            var menu = new ContextMenu();
            var copy = new MenuItem
            {
                Header = "复制全部"
            };
            copy.Click += delegate
            {
                CopyMessage(text.Text);
            };
            menu.Items.Add(copy);
            longView.ContextMenu = menu;
            refresh();
        }

        private void CopyMessage(string message)
        {
            if (!string.IsNullOrEmpty(message))
                Guard(() => Clipboard.SetText(message));
        }
    }
}
