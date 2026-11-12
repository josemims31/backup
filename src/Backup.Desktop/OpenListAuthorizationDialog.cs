using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Backup.Core;
namespace Backup.Desktop
{
    public static partial class EditorDialogs
    {
        internal static async Task AuthorizeOpenList(Window owner,string location,RemoteConnectionOptions settings,OpenListAuthorizationServices services=null)
        {if(!await AuthorizeOpenListCore(owner,location,settings,services,null,CancellationToken.None))throw new IOException("已取消 OpenList 授权，仓库设置尚未保存。");}
        internal static int OpenListAuthorizationIdleMilliseconds=300000;
        internal static Task<bool> AuthorizeOpenListDuringBackup(Window owner,string location,RemoteConnectionOptions settings,OpenListAuthorizationServices services,CancellationToken token)
        {return AuthorizeOpenListCore(owner,location,settings,services,OpenListAuthorizationIdleMilliseconds,token);}
        private static async Task<bool> AuthorizeOpenListCore(Window owner,string location,RemoteConnectionOptions settings,OpenListAuthorizationServices services,int? idleMilliseconds,CancellationToken token)
        {
            if(settings.OpenListUploadOptimization==false)return true;
            if(services!=null&&services.Load!=null)services.Load(location,settings);
            string initialError=null;
            try{bool detected=await System.Threading.Tasks.Task.Run(()=>OpenListAuthorization.AuthorizeOpenList(location,settings,null,token));if(detected&&services!=null&&services.Save!=null)services.Save(settings);return true;}
            catch(OpenListLoginThrottledException error){if(!idleMilliseconds.HasValue)throw;initialError=error.Message;}
            catch(OpenListAuthorizationException){}
            token.ThrowIfCancellationRequested();StackPanel body;Button save;var dialog=Form(idleMilliseconds.HasValue&&(owner==null||!owner.IsVisible||owner.WindowState==WindowState.Minimized)?null:owner,"OpenList 授权",out body,out save);save.Content="授权";
            body.Children.Add(new TextBlock{Text="未启用2FA时，验证码留空即可。已启用时请输入当前验证码。同一 OpenList 服务、同一账号的仓库共享授权，令牌将加密保存，后续备份和重启可复用。",TextWrapping=TextWrapping.Wrap});
            var countdown=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0),Foreground=System.Windows.Media.Brushes.Firebrick};if(idleMilliseconds.HasValue){body.Children.Add(countdown);((Button)dialog.FindName("Cancel")).Content="改用 WebDAV";dialog.ShowInTaskbar=true;dialog.Topmost=true;dialog.WindowStartupLocation=WindowStartupLocation.CenterScreen;}
            var code=Text(dialog,body,"TwoFactorCode","二步验证码（未启用2FA可留空）","");code.MaxLength=6;
            var status=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,BorderThickness=new Thickness(0),MaxHeight=110,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Foreground=System.Windows.Media.Brushes.Firebrick,Margin=new Thickness(0,8,0,0)};body.Children.Add(status);dialog.RegisterName("AuthorizationStatus",status);
            status.Text=initialError??"";bool saving=false,authorized=false,closed=false,expired=false;var idle=Stopwatch.StartNew();var login=CancellationTokenSource.CreateLinkedTokenSource(token);
            dialog.Closing+=delegate(object sender,System.ComponentModel.CancelEventArgs e){if(saving&&!expired&&!token.IsCancellationRequested)e.Cancel=true;};dialog.Closed+=delegate{closed=true;login.Cancel();};code.TextChanged+=delegate{idle.Restart();};
            DispatcherTimer timer=null;if(idleMilliseconds.HasValue){timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(Math.Max(10,Math.Min(1000,idleMilliseconds.Value/4)))};timer.Tick+=delegate{long remaining=idleMilliseconds.Value-idle.ElapsedMilliseconds;countdown.Text="OpenList 授权待恢复；"+Math.Max(0,(int)Math.Ceiling(remaining/1000.0))+" 秒无输入后改用普通 WebDAV。已提交的文件不会重复上传。";if(remaining<=0){expired=true;login.Cancel();dialog.Close();}};countdown.Text="5 分钟无输入将自动改用普通 WebDAV。已提交的文件继续核对，不重复上传。";timer.Start();}
            var cancellation=token.Register(()=>{try{dialog.Dispatcher.BeginInvoke(new Action(()=>{if(!closed)dialog.Close();}));}catch(InvalidOperationException){}});
            save.Click+=async delegate{
                if(saving)return;
                string value=code.Text.Trim();if(value.Length!=0&&(value.Length!=6||Array.Exists(value.ToCharArray(),c=>c<'0'||c>'9'))){status.Text="验证码请输入6位数字；未启用2FA时请留空。";return;}
                idle.Restart();saving=true;save.IsEnabled=false;((Button)dialog.FindName("Cancel")).IsEnabled=false;status.Text="正在授权…";
                try{await System.Threading.Tasks.Task.Run(()=>OpenListAuthorization.AuthorizeOpenList(location,settings,value,login.Token));if(closed||expired||token.IsCancellationRequested)return;if(services!=null&&services.Save!=null)services.Save(settings);authorized=true;saving=false;dialog.DialogResult=true;}
                catch(OpenListLoginThrottledException error){if(!closed)status.Text=error.Message;}
                catch(OpenListAuthorizationException error){if(!closed)status.Text=error.RequiresTwoFactor?"验证码错误或已过期，请输入刚刷新的验证码后手动重试。":"OpenList 账号或密码不正确，请核对后手动重试；也可改用 WebDAV。";}
                catch(Exception error){if(!closed)status.Text=error.Message;}
                finally{value=null;saving=false;if(!closed){save.IsEnabled=true;((Button)dialog.FindName("Cancel")).IsEnabled=true;}else login.Dispose();}
            };
            try{bool result=Show(dialog)&&authorized;token.ThrowIfCancellationRequested();return result;}finally{if(timer!=null)timer.Stop();cancellation.Dispose();code.Clear();if(!saving)login.Dispose();}
        }
    }
}
