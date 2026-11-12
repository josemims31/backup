using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Backup.Updates;

namespace Backup.Updater
{
    internal static class Program
    {
        private static void WaitForParent(int id,long ticks)
        {
            try{using(var parent=Process.GetProcessById(id)){if(parent.StartTime.ToUniversalTime().Ticks==ticks&&!parent.WaitForExit(600000))throw new IOException("软件尚未完全退出，未替换任何程序文件。");}}
            catch(ArgumentException){}
        }
        private static void Restart(UpdateRequest request,string[] arguments)
        {
            if(arguments==null&&request.ExitAfterUpdate)return;
            using(UpdateTransactions.Launch(Path.Combine(request.Installation,"Backup.exe"),arguments??(request.Minimized?new[]{"--minimized"}:new string[0]),request.Installation)){}
        }
        private static TimeSpan StartupTimeout
        {
            get
            {
#if UPDATE_TEST
                int seconds;if(int.TryParse(Environment.GetEnvironmentVariable("BACKUP_UPDATE_TEST_STARTUP_SECONDS"),out seconds)&&seconds>=1&&seconds<=300)return TimeSpan.FromSeconds(seconds);
#endif
                return TimeSpan.FromMinutes(5);
            }
        }
        private static bool Healthy(UpdateTransaction transaction)
        {var record=UpdateTransactions.Startup(transaction);return record!=null&&record.Healthy;}
        private static void StopCandidate(UpdateTransaction transaction,Process launched)
        {
            var record=UpdateTransactions.Startup(transaction);
            using(var candidate=record==null?launched:UpdateTransactions.Candidate(transaction,record))
            {
                if(candidate==null||candidate.HasExited)return;
                if(!UpdateTransactions.Normalize(candidate.MainModule.FileName).Equals(Path.Combine(UpdateTransactions.Normalize(transaction.Installation),"Backup.exe"),StringComparison.OrdinalIgnoreCase))throw new IOException("不能确认新版本进程身份，已保留恢复记录。");
                candidate.CloseMainWindow();if(!candidate.WaitForExit(5000)){candidate.Kill();if(!candidate.WaitForExit(10000))throw new IOException("新版本尚未退出，暂未回滚程序文件。");}
            }
        }
        private static void Restore(UpdateTransaction transaction,string reason,Process launched,string[] arguments)
        {
            if(Healthy(transaction)){UpdateTransactions.Confirm(transaction);return;}
            var record=UpdateTransactions.Startup(transaction);if(arguments==null&&record!=null)arguments=record.Arguments;
            StopCandidate(transaction,launched);
            using(var application=UpdateTransactions.Mutex(transaction.Installation,true))
            {
                if(!UpdateTransactions.Enter(application,TimeSpan.FromSeconds(30)))throw new IOException("软件目录仍在使用，回滚记录已保留。");
                try{UpdateInstaller.Rollback(transaction,reason);}finally{application.ReleaseMutex();}
            }
            Restart(transaction.Request,arguments);
        }
        private static int Monitor(UpdateTransaction transaction,Process launched,string[] arguments)
        {
            var elapsed=Stopwatch.StartNew();Process candidate=launched;
            try
            {
                while(true)
                {
                    var record=UpdateTransactions.Startup(transaction);
                    // A quick scheduled host may already have exited after acknowledging a healthy startup.
                    if(record!=null&&record.Healthy){UpdateTransactions.Confirm(transaction);return 0;}
                    if(record!=null&&!string.IsNullOrEmpty(record.Error))throw new IOException("新版本启动失败："+record.Error);
                    if(candidate==null&&record!=null)candidate=UpdateTransactions.Candidate(transaction,record);
                    if(candidate!=null&&candidate.HasExited||record!=null&&candidate==null)throw new IOException("新版本在完成启动确认前已退出。");
                    if(elapsed.Elapsed>=StartupTimeout)throw new IOException("新版本未在五分钟内完成启动确认。");
                    Thread.Sleep(200);
                }
            }
            catch(Exception error){if(Healthy(transaction)){UpdateTransactions.Confirm(transaction);return 0;}Restore(transaction,error.Message,candidate,arguments);return 1;}
            finally{if(candidate!=null)candidate.Dispose();}
        }
        public static int Main(string[] args)
        {
            UpdateRequest request=null;UpdateTransaction transaction=null;bool applicationEntered=false;string[] resume=null;bool recover=false,monitor=false;
            try
            {
                if(args.Length==1)
                {
                    string path=Path.GetFullPath(args[0]);request=UpdateTransactions.Read<UpdateRequest>(path,65536);
                    string session=UpdateInstaller.SessionDirectory(request.Installation,request.Payload);
                    if(!path.Equals(Path.Combine(session,"request.json"),StringComparison.OrdinalIgnoreCase))throw new IOException("更新请求路径不正确。");
                }
                else if(args.Length==2&&args[0]=="--monitor"||args.Length==5&&args[0]=="--recover")
                {
                    monitor=args[0]=="--monitor";recover=!monitor;
                    var hint=UpdateTransactions.Read<UpdateTransaction>(args[1],4194304);transaction=UpdateTransactions.ReadJournal(args[1],hint.Installation);request=transaction.Request;
                    if(recover){resume=UpdateProtocol.ReadJson<string[]>(args[4]);WaitForParent(int.Parse(args[2],System.Globalization.CultureInfo.InvariantCulture),long.Parse(args[3],System.Globalization.CultureInfo.InvariantCulture));}
                }
                else throw new ArgumentException("缺少更新或恢复请求。");
                using(var gate=UpdateTransactions.Mutex(request.Installation,false))
                {
                    if(!UpdateTransactions.Enter(gate,monitor?TimeSpan.Zero:TimeSpan.FromMinutes(10)))return monitor?0:1;
                    try
                    {
                        if(monitor||recover)
                        {
                            var active=UpdateTransactions.Active(request.Installation);
                            if(active==null){if(recover)Restart(request,resume);return 0;}
                            if(active.Id!=transaction.Id)throw new IOException("另一个更新正在进行，已保留当前恢复记录。");transaction=active;
                            if(transaction.State==UpdateTransactions.Confirmed){UpdateTransactions.Confirm(transaction);if(recover)Restart(request,resume);return 0;}
                            if(transaction.State==UpdateTransactions.RolledBack){UpdateTransactions.CompleteTerminal(transaction);if(recover)Restart(request,resume);return 1;}
                            if(transaction.State==UpdateTransactions.AwaitingStartup)
                            {
                                var record=UpdateTransactions.Startup(transaction);
                                if(record!=null&&record.Healthy){UpdateTransactions.Confirm(transaction);if(recover)Restart(request,resume);return 0;}
                                using(var candidate=UpdateTransactions.Candidate(transaction,record))
                                {if(record!=null&&candidate!=null&&string.IsNullOrEmpty(record.Error)&&string.IsNullOrEmpty(transaction.Error))return Monitor(transaction,null,resume);}
                                if(monitor&&record==null&&string.IsNullOrEmpty(transaction.Error))return Monitor(transaction,null,resume);
                            }
                            Restore(transaction,transaction.Error??"上次更新被中断，已恢复原程序文件。",null,resume);return 1;
                        }
                        WaitForParent(request.ParentProcessId,request.ParentStartUtcTicks);
                        using(var application=UpdateTransactions.Mutex(request.Installation,true))
                        {
                            if(!UpdateTransactions.Enter(application,TimeSpan.FromMinutes(10)))throw new IOException("软件目录仍在使用，未替换程序文件。");applicationEntered=true;
                            try{transaction=UpdateInstaller.Install(request);UpdateTransactions.Result(transaction,false,true,"程序文件已安装，等待新版本启动确认。");}finally{application.ReleaseMutex();}
                        }
                        if(request.ExitAfterUpdate)return 0;
                        Process launched=null;
                        try{launched=UpdateTransactions.Launch(Path.Combine(request.Installation,"Backup.exe"),request.Minimized?new[]{"--minimized"}:new string[0],request.Installation);}
                        catch(Exception error){Restore(transaction,"新版本启动失败："+error.Message,null,null);return 1;}
                        return Monitor(transaction,launched,null);
                    }
                    finally{gate.ReleaseMutex();}
                }
            }
            catch(Exception error)
            {
                if(request!=null)
                {
                    UpdateTransactions.Log(request.Installation,error);
                    try
                    {
                        UpdateProtocol.AtomicText(Path.Combine(UpdateProtocol.UpdatesDirectory(request.Installation),"result.json"),UpdateProtocol.Json(new UpdateResult{Success=false,Pending=UpdateTransactions.Active(request.Installation)!=null,Version=request.Version,Message=error.Message,TimeUtc=DateTime.UtcNow.ToString("o")}));
                        if(applicationEntered&&!(error is UpdateRollbackException)&&UpdateTransactions.Active(request.Installation)==null)Restart(request,null);
                    }
                    catch(Exception logError){UpdateTransactions.Log(request.Installation,logError);}
                }
                return 1;
            }
        }
    }
}
