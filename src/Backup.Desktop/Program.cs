using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Backup.Updates;

namespace Backup.Desktop
{
    internal static class Program
    {
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int MessageBox(IntPtr owner,string text,string title,uint type);
        [STAThread]
        public static int Main(string[] args)
        {
            string directory=AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                // This entry point loads no Core or WPF types before interrupted-update recovery.
                using(var startup=UpdateStartup.Begin(directory,Assembly.GetExecutingAssembly().GetName().Version.ToString(),args))
                {if(startup.ExitRequested)return 0;try{return DesktopEntry.Run(args,startup);}catch(Exception error){if(startup.Pending){startup.Failed(error);return 1;}throw;}}
            }
            catch(Exception error){UpdateTransactions.Log(directory,error);MessageBox(IntPtr.Zero,error.ToString(),"Backup 启动失败",0x10);return 1;}
        }
    }
    internal static class DesktopEntry
    {
        private static Application CreateApplication()
        {
#if UI_TEST
            RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
#endif
            return new Application{ShutdownMode=ShutdownMode.OnMainWindowClose};
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Run(string[] args,UpdateStartup startup)
        {
#if UI_TEST
            AppContext.SetSwitch("Switch.System.Windows.Media.ShouldRenderEvenWhenNoDisplayDevicesAreAvailable",true);
#endif
            try
            {
#if UI_TEST
                if(args.Length==2 && args[0]=="--ui-smoke")return GuiSmoke.Run(CreateApplication(),args[1]);
#endif
                string baseDir=AppDomain.CurrentDomain.BaseDirectory;RuntimeFolders.Prepare(baseDir);bool scheduled=args.Length==2&&args[0]=="--run-schedule";ScheduleRequest request=scheduled?ScheduleRequest.Create(args[1]):null;
                using(var mutex=ScheduleRequestTransport.CreateInstanceMutex(baseDir))
                {
                    bool entered=false;var wait=System.Diagnostics.Stopwatch.StartNew();
                    while(true){try{entered=mutex.WaitOne(0);}catch(AbandonedMutexException){entered=true;}if(entered)break;if(!scheduled)break;
                        if(ScheduleRequestTransport.TrySend(baseDir,request))return 0;
                        if(wait.Elapsed>TimeSpan.FromSeconds(30)){RuntimeLog.Append(baseDir,"schedule-runs.log","计划请求未交付：运行中的软件暂未响应。");return 1;}Thread.Sleep(100);
                    }
                    if(!entered){MessageBox.Show("这个程序目录的图形界面已经在运行。","Backup");return 0;}
                    try
                    {
                        var app=CreateApplication();using(var controller=new MainWindowController(baseDir)){controller.SetScheduledHost(scheduled);if(request!=null)controller.EnqueueScheduledRequest(request);
                        bool loaded=false,startupFailed=false;
                        Action confirmed=delegate{if(!controller.Window.Dispatcher.HasShutdownStarted)controller.Window.Dispatcher.BeginInvoke(new Action(controller.ApplicationStartupConfirmed));};
                        controller.Window.Loaded+=delegate{loaded=true;controller.Window.Dispatcher.BeginInvoke(new Action(delegate{if(!startupFailed)try{startup.Complete(confirmed);}catch(Exception error){startupFailed=true;startup.Failed(error);app.Shutdown(1);}}),System.Windows.Threading.DispatcherPriority.ApplicationIdle);};
                        app.SessionEnding+=delegate(object sender,SessionEndingCancelEventArgs e){try{controller.SaveScheduledRequestsForExit(false);}catch(Exception error){RuntimeLog.Append(baseDir,"ui-errors.log",error.ToString());e.Cancel=true;}};if(args.Contains("--minimized")){controller.Window.WindowState=WindowState.Minimized;controller.Window.ShowActivated=false;}app.MainWindow=controller.Window;
                        app.DispatcherUnhandledException+=delegate(object sender,System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
                        {RuntimeLog.Append(baseDir,"ui-errors.log",e.Exception.ToString());if(startup.Pending){startupFailed=true;startup.Failed(e.Exception);app.Shutdown(1);}else MessageBox.Show(e.Exception.Message,"Backup");e.Handled=true;};
                        int result=app.Run(controller.Window);if(loaded&&!startupFailed)startup.Complete(null);else startup.Failed(new IOException("主窗口未完成启动。"));return controller.ScheduledExitCode==0?result:1;}
                    }
                    finally{mutex.ReleaseMutex();}
                }
            }
            catch(Exception e){if(startup.Pending){startup.Failed(e);return 1;}if(args.Length==2&&args[0]=="--run-schedule"){RuntimeLog.Append(AppDomain.CurrentDomain.BaseDirectory,"schedule-runs.log",e.ToString());return 1;}MessageBox.Show(e.ToString(),"Backup 启动失败",MessageBoxButton.OK,MessageBoxImage.Error);return 1;}
        }
    }
}
