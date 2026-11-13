using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        public List<BackupSchedule> Schedules()
        {
            return JsonSettings.Decode(Catalog.GetSetting("backup_schedules_v1"), new List<BackupSchedule>());
        }

        public void SaveSchedule(BackupSchedule schedule)
        {
            schedule.Enabled = false;
            WindowsIntegration.Validate(schedule, WorkspaceMigration.Load(Catalog).Tasks.Where(t => !t.Deleted).Select(t => t.Id));
            var plans = Schedules();
            var prior = plans.FirstOrDefault(p => p.Id == schedule.Id);
            string exe = Path.Combine(BaseDirectory, "Backup.exe");
            plans.RemoveAll(p => p.Id == schedule.Id);
            plans.Add(schedule);
            CommitScheduleChange(
                () => WindowsIntegration.RegisterSchedule(RepositoryId, schedule, exe),
                () =>
            {
                if (prior == null)
                    WindowsIntegration.DeleteSchedule(RepositoryId, schedule.Id);
                else
                    WindowsIntegration.RegisterSchedule(RepositoryId, prior, exe);
            },
                JsonSettings.Encode(plans)
            );
        }

        public void DeleteSchedule(string id)
        {
            var plans = Schedules();
            var prior = plans.Single(p => p.Id == id);
            plans.RemoveAll(p => p.Id == id);
            CommitScheduleChange(
                () => WindowsIntegration.DeleteSchedule(RepositoryId, id),
                () => WindowsIntegration.RegisterSchedule(RepositoryId, prior, Path.Combine(BaseDirectory, "Backup.exe")),
                JsonSettings.Encode(plans)
            );
        }

        private void CommitScheduleChange(Action changeWindows, Action rollbackWindows, string json)
        {
            try
            {
                changeWindows();
                Catalog.SaveScheduleConfiguration(json);
            }
            catch (Exception error)
            {
                try
                {
                    rollbackWindows();
                }
                catch (Exception rollback)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/Scheduling.cs:CommitScheduleChange", rollback);
                    throw new IOException("计划更新未完成，本地计划保持原状态；Windows 计划恢复失败，请在计划列表修复对应异常项。", new AggregateException(error, rollback));
                }

                throw;
            }
        }

        public void ScheduleResult(string id, string result)
        {
            var plans = Schedules();
            var plan = plans.FirstOrDefault(p => p.Id == id);
            if (plan == null)
                return;
            plan.LastRunUtc = DateTime.UtcNow.ToString("o");
            plan.LastResult = result;
            Catalog.SetSetting("backup_schedules_v1", JsonSettings.Encode(plans));
        }
    }

    public sealed partial class MainWindowController
    {
        private RuntimeStateStore scheduleJournal;
        private ScheduleRequestQueue scheduleRequests;
        private ScheduleRequestHost scheduleRequestHost;
        private bool scheduleDraining, scheduleExecuting, scheduledHost;
        public int ScheduledExitCode { get; private set; }

        private void InitializeRuntime()
        {
            InitializeSchedulePage();
            if (scheduleJournal == null)
                scheduleJournal = new RuntimeStateStore(Path.Combine(Backend.CacheDirectory, "schedule.db"));
            string queue = scheduleJournal.Get(ScheduleRequestQueue.SettingKey) ?? Backend.Catalog.GetSetting(ScheduleRequestQueue.SettingKey);
            scheduleRequests = new ScheduleRequestQueue(queue, json => scheduleJournal.Set(ScheduleRequestQueue.SettingKey, json));
            scheduleRequests.Checkpoint();
            Backend.Catalog.RemoveSetting(ScheduleRequestQueue.SettingKey);
            scheduleRequestHost = new ScheduleRequestHost(Backend.BaseDirectory, EnqueueScheduledRequest);
            Window.Loaded += async delegate
            {
                if (disposed)
                    return;
                if (!scheduledHost)
                    try
                    {
                        await operations.RunWhenIdleAsync(
                            OperationKind.Configuration,
                            delegate
                        {
                            if (!disposed)
                            {
                                Backend.ReconcileSchedules();
                                RefreshSchedulePage();
                            }

                            return Task.FromResult(true);
                        }
                        );
                        if (!disposed)
                            await InspectSchedulePageAsync();
                    }
                    catch (OperationCanceledException)
                    { // Expected cancellation, shutdown, or a deliberately rejected identity probe.
                    }
                    catch (Exception error)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/Scheduling.cs:InitializeRuntime", error);
                        if (!disposed)
                            Log("任务计划整理未完成：" + error.Message);
                    }

                BeginScheduledRequests();
            };
        }

        private void RuntimeAfterImport(string previousRepository, IList<BackupSchedule> previousPlans)
        {
            try
            {
                foreach (var plan in previousPlans)
                    WindowsIntegration.DeleteSchedule(previousRepository, plan.Id);
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/Scheduling.cs:RuntimeAfterImport", error);
                Log("旧 Windows 计划清理未完成：" + error.Message);
            }

            RefreshSchedulePage();
            ObserveScheduleInspection();
        }

        private void DispatchScheduledRequests()
        {
            if (!disposed && !Window.Dispatcher.HasShutdownStarted)
                Window.Dispatcher.BeginInvoke(new Action(BeginScheduledRequests));
        }

        public int PendingScheduledRequestCount
        {
            get
            {
                return scheduleRequests == null ? 0 : scheduleRequests.Count;
            }
        }

        public bool EnqueueScheduledRequest(ScheduleRequest request)
        {
            if (scheduleRequests == null)
                return false;
            bool accepted = scheduleRequests.Enqueue(request);
            if (accepted)
            {
                if (scheduleRequests.ContainsPending(request.RequestId))
                    scheduledReadGenerations.TryAdd(request.RequestId, sourceReadGeneration);
                DispatchScheduledRequests();
            }

            return accepted;
        }

        public void SaveScheduledRequestsForExit(bool closing = true)
        {
            if (scheduleRequests != null)
            {
                if (closing)
                    scheduleRequests.BeginClosing();
                else
                    scheduleRequests.Checkpoint();
            }
        }

        private async void BeginScheduledRequests()
        {
            if (disposed || closeAfterWork || scheduleDraining || scheduleExecuting || MutationBlocked || !Window.IsLoaded)
                return;
            scheduleDraining = true;
            try
            {
                if (disposed || closeAfterWork || busy)
                    return;
                while (!disposed && !closeAfterWork)
                {
                    var request = scheduleRequests.Peek();
                    if (request == null)
                        break;
                    try
                    {
                        await operations.RunWhenIdleAsync(
                            OperationKind.Schedule,
                            async delegate
                        {
                            if (disposed || closeAfterWork)
                                return;
                            var plan = Backend.Schedules().FirstOrDefault(p => p.Id == request.ScheduleId && p.Enabled && !p.PendingImportConfirmation);
                            if (plan != null)
                            {
                                long generation;
                                scheduledReadGenerations.TryGetValue(request.RequestId, out generation);
                                await ExecuteScheduleAsync(plan, generation);
                            }
                        }
                        );
                    }
                    catch (Exception error)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/Scheduling.cs:BeginScheduledRequests", error);
                        ScheduledExitCode = 1;
                        Log("定时请求未完成：" + error.Message);
                    }
                    finally
                    {
                        if (!closeAfterWork)
                        {
                            scheduleRequests.Complete(request.RequestId);
                            long removed;
                            scheduledReadGenerations.TryRemove(request.RequestId, out removed);
                        }
                    }
                }
            }
            catch (Exception error)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/Scheduling.cs:BeginScheduledRequests", error);
                ScheduledExitCode = 1;
                Log("计划请求队列未完成：" + error.Message);
            }
            finally
            {
                scheduleDraining = false;
                if (!disposed && (closeAfterWork || scheduledHost))
                    RequestApplicationExit();
            }
        }

        public void SetScheduledHost(bool value)
        {
            scheduledHost = value;
            if (value)
            {
                Window.WindowState = WindowState.Minimized;
                Window.ShowActivated = false;
            }
        }

        public void RequestApplicationExit()
        {
            Window.Close();
        }

#if UI_TEST
        public Func<BackupSchedule, Task> ScheduleExecutionGate;
#endif
        public Task ExecuteScheduleAsync(BackupSchedule plan, long? acceptedGeneration = null)
        {
            return RunOperationAsync(OperationKind.Schedule, () => ExecuteScheduleCoreAsync(plan, acceptedGeneration));
        }

        private async Task ExecuteScheduleCoreAsync(BackupSchedule plan, long? acceptedGeneration = null)
        {
            long readGeneration = acceptedGeneration ?? sourceReadGeneration;
            if (plan.PendingImportConfirmation || Workspace.Tasks.Any(task => plan.TaskIds.Contains(task.Id) && task.RecoveryPendingConfirmation))
                throw new InvalidOperationException("导入的任务计划尚未手动确认，已阻止执行。");
            if (busy || scheduleExecuting)
                throw new InvalidOperationException("已有操作正在执行，任务计划将排队。");
            scheduleExecuting = true;
            try
            {
#if UI_TEST
                if (ScheduleExecutionGate != null)
                    await ScheduleExecutionGate(plan);
#endif
                if (plan.Backup)
                    await EnsureTaskFileListsAsync(plan.TaskIds, readGeneration);
                if (closeAfterWork)
                    return;
                var tasks = Workspace.Tasks.Where(t => !t.Deleted && plan.TaskIds.Contains(t.Id)).Select(t => JsonSettings.Decode(JsonSettings.Encode(t), new BackupTaskDefinition())).ToList();
                var errors = new List<string>();
                if (tasks.Count == 0)
                    errors.Add("没有可执行的备份任务，请编辑计划。");
                Log("任务计划开始：" + plan.Name);
                Backend.ScheduleResult(plan.Id, "执行中");
                bool stopped = false;
                if (plan.Backup)
                {
                    sourceReads.MarkUsed();
                    Backend.BeginBackup();
                }

                try
                {
                    if (plan.Backup)
                        foreach (var task in tasks)
                            try
                            {
                                SetBusy(true, true, "任务计划：等待执行 " + task.Name + "…");
                                await Task.Run(() => Backend.ActiveBackup.Checkpoint());
                                SetBusy(true, false, "任务计划：读取 " + task.Name + " 的已保存勾选…");
                                var sources = Workspace.Sources.Where(s => !s.Deleted && task.SourceIds.Contains(s.Id)).ToList();
                                var cleanup = CleanupReporter();
                                var maintenance = OperationReporter("");
                                ValidateSourceReadStates(task.SourceIds);
                                var files = sources.SelectMany(source => loaded[source.Id].Files).GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
                                foreach (var item in files)
                                    item.ArchivePath = Display.ArchivePath(item.Path);
                                var selected = files.Where(f => task.Selection.Includes(f.Path)).ToList();
                                await Task.Run(() =>
                                {
                                    var pending = Backend.PrepareTaskBackup(Workspace, task, sources, files, maintenance, cleanup);
                                    if (pending.Count > 0)
                                        errors.AddRange(pending);
                                });
                                SetBusy(true, true, "定时备份：" + task.Name);
                                var backupProgress = BackupReporter();
                                var result = await Task.Run(() => Backend.Backup(task, selected, backupProgress, cleanup));
                                if (closeAfterWork)
                                {
                                    stopped = true;
                                    errors.Add(task.Name + "：已停止。");
                                    break;
                                }

                                if (result.NoChanges)
                                {
                                    Log(task.Name + "：任务完成，本地文件和备份清单均无变化。");
                                    continue;
                                }

                                PublishBackupResult(result);
                                maintenance = OperationReporter("");
                                if (result.Archives.Any(a => a.Archive.Status != "complete") || result.AttentionUnchanged > 0)
                                    errors.Add(task.Name + "：部分副本需处理。");
                                if (result.ReadyForPublication)
                                {
                                    SetBusy(true, true, "任务计划：备份数据库…");
                                    await Task.Run(() => Backend.PublishTaskBackup(task, result, maintenance, cleanup));
                                    if (result.CatalogBackupError != null)
                                        errors.Add(task.Name + "：" + result.CatalogBackupError);
                                    if (Backend.LastCleanup != null && Backend.LastCleanup.PendingArchives > 0)
                                        errors.Add(task.Name + "：旧备份包清理待重试。");
                                }

                                if (result.Stopped)
                                {
                                    stopped = true;
                                    errors.Add(task.Name + "：已按要求停止。");
                                    break;
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                stopped = true;
                                errors.Add(task.Name + "：已停止。");
                                break;
                            }
                            catch (Exception error)
                            {
                                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/Scheduling.cs:ExecuteScheduleCoreAsync", error);
                                errors.Add(task.Name + "：" + error.Message);
                                if (Backend.ActiveBackup != null && Backend.ActiveBackup.IsStopping)
                                {
                                    stopped = true;
                                    break;
                                }
                            }
                            finally
                            {
                                if (busy)
                                    SetBusy(false, false, null);
                            }
                }
                finally
                {
                    Backend.EndBackup();
                }

                if (!stopped && !closeAfterWork && plan.Integrity && tasks.Count > 0)
                    try
                    {
                        errors.AddRange(await CheckAndRepairScheduledBackupsAsync(tasks.Select(t => t.Id).ToList()));
                    }
                    catch (OperationCanceledException)
                    {
                        stopped = true;
                        errors.Add("检查与修复：已停止。");
                    }
                    catch (Exception error)
                    {
                        global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/Scheduling.cs:ExecuteScheduleCoreAsync", error);
                        errors.Add("检查与修复：" + error.Message);
                    }

                string summary = stopped ? "计划已停止：" + string.Join("；", errors) : errors.Count == 0 ? "计划执行完成" : "计划执行结束：" + string.Join("；", errors);
                if (errors.Count > 0)
                    ScheduledExitCode = 1;
                Backend.ScheduleResult(plan.Id, summary);
                Log(plan.Name + "：" + summary);
                SetOperationText(summary, stopped ? OperationState.Cancelled : errors.Count == 0 ? OperationState.Succeeded : OperationState.Failed);
                RuntimeLog.Append(Backend.BaseDirectory, "schedule-runs.log", plan.Name + "：" + summary);
            }
            finally
            {
                scheduleExecuting = false;
                if (!disposed)
                    UpdateScheduleRunButton();
                if (closeAfterWork && !scheduleDraining && !busy && !disposed)
                    ScheduleRefreshClose();
            }
        }
    }
}
