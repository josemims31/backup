using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace Backup.Desktop
{
    internal static class Program
    {
        private static Application CreateApplication()
        {
#if UI_TEST
            RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
#endif
            return new Application{ShutdownMode=ShutdownMode.OnMainWindowClose};
        }
        [STAThread]
        public static int Main(string[] args)
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
                        app.SessionEnding+=delegate(object sender,SessionEndingCancelEventArgs e){try{controller.SaveScheduledRequestsForExit(false);}catch(Exception error){RuntimeLog.Append(baseDir,"ui-errors.log",error.ToString());e.Cancel=true;}};if(args.Contains("--minimized")){controller.Window.WindowState=WindowState.Minimized;controller.Window.ShowActivated=false;}app.MainWindow=controller.Window;
                        app.DispatcherUnhandledException+=delegate(object sender,System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
                        {RuntimeLog.Append(baseDir,"ui-errors.log",e.Exception.ToString());MessageBox.Show(e.Exception.Message,"Backup");e.Handled=true;};
                        int result=app.Run(controller.Window);return controller.ScheduledExitCode==0?result:1;}
                    }
                    finally{mutex.ReleaseMutex();}
                }
            }
            catch(Exception e){if(args.Length==2&&args[0]=="--run-schedule"){RuntimeLog.Append(AppDomain.CurrentDomain.BaseDirectory,"schedule-runs.log",e.ToString());return 1;}MessageBox.Show(e.ToString(),"Backup 启动失败",MessageBoxButton.OK,MessageBoxImage.Error);return 1;}
        }
    }
}
