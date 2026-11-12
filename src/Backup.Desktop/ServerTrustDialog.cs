using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Backup.Core;
namespace Backup.Desktop
{
    public static partial class EditorDialogs
    {
        internal static Task<bool> ConfirmServerTrust(Window owner,RemoteServerTrustNotice notice,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();var identity=notice.Identity;bool ssh=identity.Kind=="ssh";
            StackPanel body;Button accept;var dialog=Form(owner,ssh?"SFTP 服务器指纹确认":"WebDAV 服务器证书确认",out body,out accept,true);
            dialog.Width=740;dialog.RegisterName("ServerTrustDialog",dialog);accept.Content="信任并保存";accept.IsDefault=false;
            var cancel=(Button)dialog.FindName("Cancel");cancel.IsDefault=true;
            body.Children.Add(new TextBlock{Text=notice.Changed?(ssh?"服务器指纹已变化，请核对新旧指纹。":"已信任的服务器证书已变化，请核对新旧指纹。"):(ssh?"首次连接此服务器，请核对主机指纹。":"服务器证书未通过系统验证，请核对证书及具体原因。"),Foreground=Brushes.Firebrick,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)});
            var text=new StringBuilder();text.AppendLine("服务器："+identity.Address);text.AppendLine("算法："+identity.Algorithm);
            if(notice.Previous!=null)text.AppendLine("原指纹："+notice.Previous.Fingerprint);text.AppendLine("当前指纹："+identity.Fingerprint);
            if(!ssh){text.AppendLine("证书主体："+identity.Subject);text.AppendLine("签发者："+identity.Issuer);text.AppendLine("有效期："+identity.NotBefore.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")+" 至 "+identity.NotAfter.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));}
            if(!string.IsNullOrEmpty(identity.Errors))text.AppendLine("验证信息："+identity.Errors);
            var details=new TextBox{Text=text.ToString().TrimEnd(),IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MaxHeight=340,Padding=new Thickness(9)};dialog.RegisterName("ServerTrustDetails",details);body.Children.Add(details);
            body.Children.Add(new TextBlock{Text=!ssh&&identity.PolicyErrors==0?"新证书已通过系统验证。核对并确认后恢复系统证书验证，正常续期无需再次确认。":"请通过服务器控制台或已可信的管理通道核对。接受后仅信任当前服务器及该指纹；后续发生变化仍需重新确认。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)});
            bool accepted=false,closed=false;accept.Click+=delegate{accepted=true;dialog.DialogResult=true;};dialog.Closed+=delegate{closed=true;};
            dialog.Loaded+=delegate{cancel.Focus();};
            if(owner==null||!owner.IsVisible||owner.WindowState==WindowState.Minimized){dialog.Owner=null;dialog.ShowInTaskbar=true;dialog.Topmost=true;dialog.WindowStartupLocation=WindowStartupLocation.CenterScreen;}
            using(token.Register(()=>{try{dialog.Dispatcher.BeginInvoke(new Action(()=>{if(!closed)dialog.Close();}));}catch(InvalidOperationException){}})){
                bool result=Show(dialog)&&accepted;token.ThrowIfCancellationRequested();return System.Threading.Tasks.Task.FromResult(result);
            }
        }
    }
    public sealed partial class MainWindowController
    {
        private readonly SemaphoreSlim serverTrustPrompts=new SemaphoreSlim(1,1);
        private void InitializeServerTrustPrompt()
        {
            Backend.ServerTrustPrompt=(notice,token)=>{serverTrustPrompts.Wait(token);try{return Window.Dispatcher.Invoke(new Func<Task<bool>>(()=>{
                token.ThrowIfCancellationRequested();var active=Application.Current==null?null:Application.Current.Windows.OfType<Window>().FirstOrDefault(item=>item.IsActive);
                return EditorDialogs.ConfirmServerTrust(active??Window,notice,token);
            })).GetAwaiter().GetResult();}finally{serverTrustPrompts.Release();}};
        }
    }
}
