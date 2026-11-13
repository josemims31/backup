using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Backup.Core;

namespace Backup.Desktop
{
    public static class RemoteFolderDialog
    {
        public static string Choose(Window owner, string initial, bool sftp, RemoteConnectionOptions options, RemoteFolderServices services = null, string warehouseId = null)
        {
            string normalized = NormalizeAddress(initial, sftp);
            return new RemoteFolderChooser(owner, normalized, sftp, options, services, warehouseId).Choose();
        }

        public static string NormalizeAddress(string value, bool sftp)
        {
            value = (value ?? "").Trim();
            if (value.Length > 0 && value.IndexOf("://", StringComparison.Ordinal) < 0)
                value = (sftp ? "sftp://" : "https://") + value;
            return RemotePath.Root(value, sftp ? "sftp" : "http").AbsoluteUri;
        }
    }

    internal sealed class RemoteFolderChooser
    {
        private readonly Window window;
        private readonly TextBox address, newName;
        private readonly Button go, up, create, choose;
        private readonly ListBox list;
        private readonly TextBlock status;
        private readonly StackPanel actions;
        private readonly bool sftp;
        private readonly RemoteConnectionOptions options;
        private readonly RemoteFolderServices services;
        private readonly string warehouseId;
        private readonly Dictionary<string, IManagedBackupDestination> profiles = new Dictionary<string, IManagedBackupDestination>();
        private CancellationTokenSource pending;
        private int generation, inputVersion;
        private bool closed, settingAddress, valid, creating;
        private Action refreshStatusColor;
        private string current, result;
        private OperationState lastState;
        public RemoteFolderChooser(Window owner, string initial, bool isSftp, RemoteConnectionOptions settings, RemoteFolderServices services = null, string warehouseId = null)
        {
            sftp = isSftp;
            options = settings;
            current = initial;
            this.services = services;
            this.warehouseId = warehouseId;
            window = new Window
            {
                Owner = owner,
                Title = "选择远程文件夹",
                Width = 690,
                Height = 520,
                MinWidth = 520,
                MinHeight = 360,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Resources = owner.Resources,
                Background = System.Windows.Media.Brushes.White
            };
            window.PreviewMouseDown += delegate
            {
                inputVersion++;
            };
            window.PreviewMouseWheel += delegate
            {
                inputVersion++;
            };
            window.PreviewKeyDown += delegate
            {
                inputVersion++;
            };
            var panel = new DockPanel
            {
                Margin = new Thickness(18)
            };
            window.Content = panel;
            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            DockPanel.SetDock(footer, Dock.Bottom);
            panel.Children.Add(footer);
            choose = new Button
            {
                Content = "确定",
                IsDefault = true,
                IsEnabled = false
            };
            var cancel = new Button
            {
                Content = "取消",
                IsCancel = true
            };
            footer.Children.Add(choose);
            footer.Children.Add(cancel);
            address = new TextBox
            {
                Text = initial,
                Margin = new Thickness(0, 0, 0, 8)
            };
            DockPanel.SetDock(address, Dock.Top);
            panel.Children.Add(address);
            actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 9)
            };
            DockPanel.SetDock(actions, Dock.Top);
            panel.Children.Add(actions);
            go = new Button
            {
                Content = "转到"
            };
            up = new Button
            {
                Content = "上一级"
            };
            newName = new TextBox
            {
                Width = 180,
                Margin = new Thickness(4, 0, 8, 0)
            };
            create = new Button
            {
                Content = "新建文件夹"
            };
            foreach (var item in new UIElement[]
            {
                go,
                up,
                newName,
                create
            }

            )
                actions.Children.Add(item);
            status = new TextBlock
            {
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Height = 20,
                Margin = new Thickness(0, 8, 0, 0)
            };
            status.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("Text") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.Self) });
            DockPanel.SetDock(status, Dock.Bottom);
            panel.Children.Add(status);
            list = new ListBox
            {
                DisplayMemberPath = "Name",
                BorderBrush = System.Windows.Media.Brushes.LightGray
            };
            panel.Children.Add(list);
            go.Click += async delegate
            {
                await LoadAsync(address.Text.Trim());
            };
            up.Click += async delegate
            {
                if (valid)
                    await LoadAsync(new Uri(new Uri(current), "../").AbsoluteUri);
            };
            list.MouseDoubleClick += async delegate
            {
                var folder = list.SelectedItem as RemoteFolder;
                if (valid && folder != null)
                    await LoadAsync(folder.Location);
            };
            create.Click += async delegate
            {
                await CreateAsync();
            };
            choose.Click += delegate
            {
                if (valid && pending == null && !creating)
                {
                    var selected = list.SelectedItem as RemoteFolder;
                    result = selected == null ? current : selected.Location;
                    window.DialogResult = true;
                }
            };
            address.TextChanged += delegate
            {
                if (settingAddress || closed)
                    return;
                CancelPending();
                valid = false;
                lastState = OperationState.None;
                status.Text = "地址已修改，请点击“转到”重新连接。";
                UpdateControls();
            };
            refreshStatusColor = OperationTextColors.BindState(window, status, () => pending != null || creating ? OperationState.Running : lastState == OperationState.Failed ? OperationState.Failed : valid ? OperationState.Succeeded : lastState);
            window.Loaded += async delegate
            {
                await LoadAsync(initial);
            };
            window.Closed += delegate
            {
                closed = true;
                CancelPending();
                foreach (var profile in profiles.Values)
                {
                    var disposable = profile as IDisposable;
                    if (disposable != null)
                        disposable.Dispose();
                }

                profiles.Clear();
            };
            UpdateControls();
        }

        public string Choose()
        {
            return window.ShowDialog() == true ? result : null;
        }

        private IRemoteFolderBrowser Browser(string url, CancellationToken cancellation)
        {
            if (services != null && services.Open != null)
            {
                var shared = services.Open(warehouseId, url, sftp, options, cancellation);
                if (shared != null)
                    return shared;
            }

            // Unsaved credentials/addresses use an isolated, dialog-owned profile.
            var uri = RemotePath.Root(url, sftp ? "sftp" : "http");
            string root = uri.GetLeftPart(UriPartial.Authority) + "/";
            IManagedBackupDestination profile;
            if (!profiles.TryGetValue(root, out profile))
            {
                profile = RemoteConnectionPolicy.Create("browse", sftp ? "sftp" : "webdav", root, options);
                profiles.Add(root, profile);
            }

            return (IRemoteFolderBrowser)((IRepositorySession)profile).ForDirectory(url, cancellation);
        }

        private void CancelPending()
        {
            generation++;
            var previous = pending;
            pending = null;
            if (previous != null)
                previous.Cancel();
        }

        private void UpdateControls()
        {
            if (closed)
                return;
            if (refreshStatusColor != null)
                refreshStatusColor();
            // Address correction and Go remain usable while a read-only listing is pending.
            actions.IsEnabled = !creating;
            address.IsReadOnly = creating;
            go.IsEnabled = !creating;
            bool ready = valid && pending == null && !creating;
            up.IsEnabled = create.IsEnabled = choose.IsEnabled = ready;
            list.IsEnabled = !creating && list.ItemsSource != null;
        }

        private static async Task<IList<RemoteFolder>> ReadAsync(IRemoteFolderBrowser browser, CancellationToken cancellation)
        {
            var worker = Task.Run(
                delegate
            {
                cancellation.ThrowIfCancellationRequested();
                var cancellable = browser as ICancellableRemoteFolderBrowser;
                return cancellable == null ? browser.ListFolders() : cancellable.ListFolders(cancellation);
            },
                cancellation
            );
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellation.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(worker, cancelled.Task) != worker)
                {
                    // Optional adapters can finish late; observe faults and never publish stale data.
                    ObserveFailure(worker);
                    cancellation.ThrowIfCancellationRequested();
                }

                return await worker;
            }
        }

        private static void ObserveFailure(Task task)
        {
            task.ContinueWith(failed =>
            {
                var ignored = failed.Exception;
            }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        private async Task LoadAsync(string url)
        {
            if (closed || creating)
                return;
            CancelPending();
            int request = ++generation;
            var cancellation = new CancellationTokenSource();
            pending = cancellation;
            valid = false;
            status.Text = "正在连接";
            UpdateControls();
            try
            {
                string normalized = RemoteFolderDialog.NormalizeAddress(url, sftp);
                IList<RemoteFolder> folders;
                var browser = Browser(normalized, cancellation.Token);
                try
                {
                    using (services != null && services.Read != null ? services.Read(cancellation) : null)
                        folders = await ReadAsync(browser, cancellation.Token);
                }
                finally
                {
                    var disposable = browser as IDisposable;
                    if (disposable != null)
                        disposable.Dispose();
                }

                if (closed || request != generation)
                    return;
                bool same = string.Equals(current, normalized, StringComparison.Ordinal);
                var selected = list.SelectedItem as RemoteFolder;
                var scroll = BrowserScrollState.Capture(list, item => ((RemoteFolder)item).Location);
                bool focused = list.IsKeyboardFocusWithin;
                int input = inputVersion;
                current = normalized;
                settingAddress = true;
                try
                {
                    address.Text = current;
                }
                finally
                {
                    settingAddress = false;
                }

                var rows = folders.OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                list.ItemsSource = rows;
                list.SelectedItem = same && selected != null ? rows.FirstOrDefault(folder => folder.Location == selected.Location) : null;
                valid = true;
                lastState = OperationState.Succeeded;
                status.Text = folders.Count + " 个子文件夹 · 双击进入";
                var restore = window.Dispatcher.BeginInvoke(
                    new Action(delegate
                {
                    if (closed || request != generation || input != inputVersion)
                        return;
                    list.UpdateLayout();
                    if (same)
                        scroll.Restore(list, item => ((RemoteFolder)item).Location);
                    if (focused && window.IsActive)
                    {
                        if (same && selected != null && list.SelectedItem == null)
                            address.Focus();
                        else
                        {
                            var item = list.SelectedItem == null ? null : list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) as ListBoxItem;
                            if (item != null)
                                item.Focus();
                            else
                                list.Focus();
                        }

                        if (same)
                            scroll.Restore(list, item => ((RemoteFolder)item).Location);
                    }
                }),
                    System.Windows.Threading.DispatcherPriority.Loaded
                );
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/RemoteFolderDialog.cs:LoadAsync", error);
                if (!closed && request == generation)
                {
                    lastState = OperationState.Failed;
                    status.Text = cancellation.IsCancellationRequested ? "连接超时，请检查地址后点击“转到”重试。" : "连接失败：" + error.Message;
                }
            }
            finally
            {
                if (pending == cancellation)
                    pending = null;
                cancellation.Dispose();
                if (!closed && request == generation)
                    UpdateControls();
            }
        }

        private async Task CreateAsync()
        {
            if (!valid || pending != null || creating)
                return;
            string name = newName.Text.Trim();
            if (name.Length == 0)
                return;
            creating = true;
            status.Text = "正在新建文件夹";
            UpdateControls();
            bool created = false;
            var cancellation = new CancellationTokenSource();
            pending = cancellation;
            try
            {
                RemotePath.Name(name);
                Func<Task> createFolder = async delegate
                {
                    var browser = Browser(current, cancellation.Token);
                    try
                    {
                        await Task.Run(() => browser.CreateFolder(name));
                        if (services != null && services.Created != null)
                            services.Created(warehouseId, current, sftp, options, name);
                    }
                    finally
                    {
                        var disposable = browser as IDisposable;
                        if (disposable != null)
                            disposable.Dispose();
                    }
                };
                if (services != null && services.Mutate != null)
                    await services.Mutate(createFolder);
                else
                    await createFolder();
                created = true;
                if (!closed)
                    newName.Clear();
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/RemoteFolderDialog.cs:CreateAsync", error);
                if (!closed)
                {
                    lastState = OperationState.Failed;
                    status.Text = error.Message;
                }
            }
            finally
            {
                if (pending == cancellation)
                    pending = null;
                cancellation.Dispose();
                creating = false;
                UpdateControls();
            }

            if (created && !closed)
                await LoadAsync(current);
        }
    }
}
