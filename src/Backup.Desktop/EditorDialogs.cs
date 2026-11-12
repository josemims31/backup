using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Backup.Core;
namespace Backup.Desktop
{
    public static partial class EditorDialogs
    {

#if UI_TEST
        public static Action<Window> BeforeShow;
#endif
        private static Window Form(Window owner,string title,out StackPanel body,out Button save,bool wheelScroll=false)
        {
            var window=new Window{Owner=owner,Title=title,Width=640,SizeToContent=SizeToContent.Height,MaxHeight=820,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize,Background=System.Windows.Media.Brushes.White,FontFamily=new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI"),FontSize=13};
            if(owner!=null)window.Resources=owner.Resources;
            NameScope.SetNameScope(window,new NameScope());
            var panel=new DockPanel{Margin=new Thickness(20)};window.Content=panel;var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,15,0,0)};DockPanel.SetDock(buttons,Dock.Bottom);panel.Children.Add(buttons);
            save=new Button{Content="保存",Padding=new Thickness(20,7,20,7),IsDefault=true};window.RegisterName("Save",save);buttons.Children.Add(save);
            var cancel=new Button{Content="取消",Padding=new Thickness(20,7,20,7),Margin=new Thickness(10,0,0,0),IsCancel=true};window.RegisterName("Cancel",cancel);buttons.Children.Add(cancel);
            body=new StackPanel();var scroll=new ScrollViewer{Content=body,MaxHeight=650,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};panel.Children.Add(scroll);if(wheelScroll)EnableFormWheelScrolling(scroll);return window;
        }
        private static void EnableFormWheelScrolling(ScrollViewer outer)
        {
            outer.PreviewMouseWheel+=delegate(object sender,MouseWheelEventArgs e)
            {
                if(e.Handled||e.Delta==0)return;
                for(var source=e.OriginalSource as DependencyObject;source!=null;source=source is Visual||source is System.Windows.Media.Media3D.Visual3D?VisualTreeHelper.GetParent(source):LogicalTreeHelper.GetParent(source))
                {
                    var scroll=source as ScrollViewer;
                    if(scroll!=null&&scroll.ScrollableHeight>0&&(e.Delta<0?scroll.VerticalOffset<scroll.ScrollableHeight:scroll.VerticalOffset>0))
                    {
                        e.Handled=true;scroll.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice,e.Timestamp,e.Delta){RoutedEvent=Mouse.MouseWheelEvent,Source=scroll});return;
                    }
                    if(ReferenceEquals(source,outer))break;
                }
            };
        }
        private static TextBox Text(Window window,StackPanel body,string name,string label,string value)
        {body.Children.Add(new TextBlock{Text=label,Margin=new Thickness(0,8,0,4)});var text=new TextBox{Text=value??"",Padding=new Thickness(7,5,7,5)};window.RegisterName(name,text);body.Children.Add(text);return text;}
        private static void Browse(StackPanel body,TextBox text,bool create)
        {
            var button=new Button{Content="选择文件夹",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,5,0,3),Padding=new Thickness(9,5,9,5)};
            button.Click+=delegate{string selected=NativeFolderPicker.Choose(Window.GetWindow(text),text.Text,"选择文件夹");if(selected!=null)text.Text=selected;};body.Children.Add(button);
        }
        private static List<ChoiceRow> Checks(Window window,StackPanel body,string name,string label,IEnumerable<ChoiceRow> values)
        {
            body.Children.Add(new TextBlock{Text=label,Margin=new Thickness(0,12,0,4),FontWeight=FontWeights.SemiBold});var list=new StackPanel();window.RegisterName(name,list);
            var rows=values.ToList();foreach(var row in rows){var check=new CheckBox{Content=row.Name,IsChecked=row.Selected,Margin=new Thickness(2,5,0,5),Tag=row,ToolTip=row.ToolTip};check.Checked+=delegate{row.Selected=true;};check.Unchecked+=delegate{row.Selected=false;};list.Children.Add(check);}
            body.Children.Add(new ScrollViewer{Content=list,MaxHeight=185,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});return rows;
        }
        private static bool Show(Window window)
        {

#if UI_TEST
            if(BeforeShow!=null)BeforeShow(window);
#endif
            return window.ShowDialog()==true;
        }
        private static void Save(Button button,Window window,Action apply)
        {button.Click+=delegate{try{apply();window.DialogResult=true;}catch(Exception error){
#if UI_TEST
throw new InvalidOperationException("Dialog validation failed: "+error.Message,error);
#else
MessageBox.Show(window,error.Message,"请检查设置",MessageBoxButton.OK,MessageBoxImage.Information);
#endif
}};}
        public static SourceDefinition Source(Window owner,SourceDefinition original,Action<SourceDefinition> validate)
        {
            StackPanel body;Button save;var window=Form(owner,original==null?"添加待备份":"编辑待备份",out body,out save);
            var name=Text(window,body,"Name","名称",original==null?"":original.Name);var location=Text(window,body,"Location","完整路径(支持smb)",original==null?"":original.Location);Browse(body,location,false);
            SourceDefinition result=null;Save(save,window,delegate{string path=StoragePath.NormalizeRoot(location.Text.Trim());if(!Path.IsPathRooted(location.Text.Trim()))throw new ArgumentException("请选择完整目录。");
                result=new SourceDefinition{Id=original==null?"source-"+Guid.NewGuid().ToString("N"):original.Id,Name=string.IsNullOrWhiteSpace(name.Text)?Path.GetFileName(path.TrimEnd('\\')):name.Text.Trim(),Location=path};if(string.IsNullOrEmpty(result.Name))result.Name=path;validate(result);});return Show(window)?result:null;
        }
        public static void Destination(Window owner,Action<DestinationDefinition,string> apply,DestinationDefinition original=null,string originalName=null,Func<string,RemoteConnectionOptions> loadConnection=null,Action<string,RemoteConnectionOptions> saveConnection=null,Func<DestinationDefinition,string,RemoteConnectionOptions,bool,Action<string>,System.Threading.Tasks.Task> applyAsync=null,RuntimeStateStore preferences=null,RemoteFolderServices browsers=null,OpenListAuthorizationServices authorization=null)
        {
            StackPanel body;Button save;var window=Form(owner,original==null?"添加仓库":"编辑仓库",out body,out save);
            var modules=RepositoryModules.Available.Where(module=>module.Protocol!="smb").ToList();
            string initial=original==null?"local":original.Protocol=="smb"?"local":original.Protocol;
            if(original!=null&&!modules.Any(module=>module.Protocol==initial))modules.Add(new RepositoryModuleInfo(initial,initial+"（模块未加载）",RepositoryModules.IsFileSystem(initial)?RepositoryPathKind.FileSystem:RepositoryPathKind.Uri,RepositoryAuthentication.Password,"http","https"));
            body.Children.Add(new TextBlock{Text="驱动"});var type=new ComboBox{ItemsSource=modules.Select(module=>module.Protocol=="local"?"本地/SMB":module.DisplayName).ToList(),SelectedIndex=Math.Max(0,modules.FindIndex(module=>module.Protocol==initial)),Padding=new Thickness(7),Margin=new Thickness(0,5,0,4)};window.RegisterName("Type",type);body.Children.Add(type);
            Func<RepositoryModuleInfo> selectedModule=()=>{if(type.SelectedIndex<0||type.SelectedIndex>=modules.Count)throw new ArgumentException("没有可用的仓库模块，请检查 repositories 文件夹。");return modules[type.SelectedIndex];};
            Func<bool> filesystem=()=>selectedModule().PathKind==RepositoryPathKind.FileSystem;
            Func<bool> ssh=()=>selectedModule().Authentication==RepositoryAuthentication.Ssh;
            Func<string,string> normalize=path=>selectedModule().Protocol=="webdav"||selectedModule().Protocol=="sftp"?RemoteFolderDialog.NormalizeAddress(path,selectedModule().Protocol=="sftp"):RepositoryModules.NormalizeLocation(selectedModule().Protocol,path);
            var name=Text(window,body,"Name","名称（留空使用文件夹名称）",originalName);var pathPanel=new StackPanel();var location=Text(window,pathPanel,"Location","完整路径",original==null?"":original.Location);var locationLabel=(TextBlock)pathPanel.Children[0];window.RegisterName("LocationLabel",locationLabel);Browse(pathPanel,location,true);var localBrowse=(UIElement)pathPanel.Children[pathPanel.Children.Count-1];
            var remote=new StackPanel();body.Children.Add(remote);var options=new RemoteConnectionOptions();bool credentialsUnavailable=false;try{if(original!=null&&loadConnection!=null)options=loadConnection(original.Id);}catch(RemoteCredentialsUnavailableException error){credentialsUnavailable=true;options=error.Reference;}catch(IOException){credentialsUnavailable=true;}
            var username=Text(window,remote,"Username","账号",options.Username);
            var authPanel=new StackPanel();remote.Children.Add(authPanel);authPanel.Children.Add(new TextBlock{Text="SFTP 登录方式",Margin=new Thickness(0,8,0,4)});var authentication=new ComboBox{ItemsSource=new[]{"密码登录","私钥登录"},SelectedIndex=options.AuthenticationMode=="private-key"?1:0};window.RegisterName("Authentication",authentication);authPanel.Children.Add(authentication);
            remote.Children.Remove(authPanel);body.Children.Insert(body.Children.IndexOf(type)+1,authPanel);
            var passwordPanel=new StackPanel();remote.Children.Add(passwordPanel);passwordPanel.Children.Add(new TextBlock{Text="密码",Margin=new Thickness(0,8,0,4)});var remotePassword=new PasswordBox{Password=options.Password??"",Padding=new Thickness(8,6,8,6)};window.RegisterName("RemotePassword",remotePassword);passwordPanel.Children.Add(remotePassword);
            var openList=new CheckBox{Content="openlist上传优化(首次登录需要输入2fa)",IsChecked=options.OpenListUploadOptimization!=false,Margin=new Thickness(0,8,0,4),ToolTip="自动识别 OpenList 并查询后台上传进度；普通 WebDAV 使用原上传方式。授权令牌由当前 Windows 用户加密保存。"};window.RegisterName("OpenListUploadOptimization",openList);remote.Children.Add(openList);
            var keyPanel=new StackPanel();remote.Children.Add(keyPanel);var privateKey=Text(window,keyPanel,"PrivateKeyPath","私钥文件（OpenSSH / PEM / PPK）",options.PrivateKeyPath);var selectKey=new Button{Content="选择私钥文件",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,5,0,3)};keyPanel.Children.Add(selectKey);selectKey.Click+=delegate{var picker=new Microsoft.Win32.OpenFileDialog{Title="选择 SFTP 私钥文件",Filter="私钥文件|*.*",CheckFileExists=true};string selected=ChooseFile(window,picker,preferences,"ui_last_private_key_v1",privateKey.Text);if(selected!=null)privateKey.Text=selected;};keyPanel.Children.Add(new TextBlock{Text="私钥口令（无口令则留空）",Margin=new Thickness(0,8,0,4)});var keyPassphrase=new PasswordBox{Password=options.PrivateKeyPassphrase??"",Padding=new Thickness(8,6,8,6)};window.RegisterName("PrivateKeyPassphrase",keyPassphrase);keyPanel.Children.Add(keyPassphrase);
            body.Children.Add(pathPanel);var remoteActions=new StackPanel();body.Children.Add(remoteActions);
            var browseRemote=new Button{Content="检查连接 / 选择远程文件夹",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,9,0,5)};remoteActions.Children.Add(browseRemote);var remoteNotice=new TextBlock{Text=credentialsUnavailable?"原凭据无法解密，请重新填写账号。":"凭据由当前windows用户加密保存。私钥保留在原处，数据库只记录私钥路径和私钥密码。",TextWrapping=TextWrapping.Wrap,Foreground=System.Windows.Media.Brushes.SlateGray};remoteActions.Children.Add(remoteNotice);
            Action refresh=()=>{bool fs=filesystem(),enabled=selectedModule().Authentication!=RepositoryAuthentication.None,sftp=ssh(),key=sftp&&authentication.SelectedIndex==1;locationLabel.Text=fs?"完整路径":"服务器";remote.Visibility=enabled?Visibility.Visible:Visibility.Collapsed;remoteActions.Visibility=fs?Visibility.Collapsed:Visibility.Visible;localBrowse.Visibility=fs?Visibility.Visible:Visibility.Collapsed;openList.Visibility=selectedModule().Protocol=="webdav"?Visibility.Visible:Visibility.Collapsed;authPanel.Visibility=sftp?Visibility.Visible:Visibility.Collapsed;passwordPanel.Visibility=key?Visibility.Collapsed:Visibility.Visible;keyPanel.Visibility=key?Visibility.Visible:Visibility.Collapsed;};type.SelectionChanged+=delegate{refresh();};authentication.SelectionChanged+=delegate{refresh();};if(modules.Count>0)refresh();
            location.LostKeyboardFocus+=delegate{if(type.SelectedIndex<0||filesystem()||string.IsNullOrWhiteSpace(location.Text))return;try{location.Text=normalize(location.Text);}catch(ArgumentException){}};
            OperationState remoteNoticeState=credentialsUnavailable?OperationState.Failed:OperationState.None;OperationTextColors.BindState(window,remoteNotice,()=>remoteNoticeState);
            Func<RemoteConnectionOptions> readSettings=()=>{bool key=ssh()&&authentication.SelectedIndex==1;return new RemoteConnectionOptions{Username=username.Text.Trim(),Password=key?"":remotePassword.Password,AuthenticationMode=key?"private-key":"password",PrivateKeyPath=key?privateKey.Text.Trim():"",PrivateKeyPassphrase=key?keyPassphrase.Password:"",OpenListUploadOptimization=openList.IsChecked==true,OpenListToken=username.Text.Trim()==options.Username&&remotePassword.Password==options.Password?options.OpenListToken:"",OpenListApiBase=options.OpenListApiBase,OpenListUsername=options.OpenListUsername};};
            browseRemote.Click+=async delegate{try{var settings=readSettings();string protocol=selectedModule().Protocol,address=normalize(location.Text);browseRemote.IsEnabled=false;
                if(protocol=="webdav"||protocol=="sftp"){string chosen=RemoteFolderDialog.Choose(window,address,protocol=="sftp",settings,browsers,original==null?null:original.Id);if(chosen!=null){location.Text=chosen;remoteNoticeState=OperationState.Succeeded;remoteNotice.Text="连接检查完成。保存后按选定子目录备份。";}else{remoteNoticeState=OperationState.Cancelled;remoteNotice.Text="已取消连接检查，可修改服务器后重试。";}}
                else{await System.Threading.Tasks.Task.Run(()=>{using(var session=(IRepositorySession)RepositoryModules.Create("connection-test",protocol,address,settings)){var probe=session as IReadOnlyDestinationProbe;if(probe==null)throw new NotSupportedException("仓库模块未提供只读连接检查。");probe.CheckReadableRoot();}});remoteNoticeState=OperationState.Succeeded;remoteNotice.Text="连接检查完成。";}
            }catch(Exception e){remoteNoticeState=OperationState.Failed;remoteNotice.Text=e.Message;}finally{browseRemote.IsEnabled=true;}};
            var capacity=Text(window,body,"Capacity","容量限制 GiB", original==null?"100":((decimal)original.MaxBytes/(1024L*1024*1024)).ToString(CultureInfo.InvariantCulture));
            var migrateFiles=new CheckBox{Content="更换地址时自动迁移已有备份",IsChecked=true,Margin=new Thickness(0,8,0,4),Visibility=original==null?Visibility.Collapsed:Visibility.Visible,ToolTip="取消勾选后只检查新位置的连接并切换，不复制、校验或删除备份文件；缺失文件由完整性检查报告。"};window.RegisterName("MigrateWarehouseFiles",migrateFiles);body.Children.Add(migrateFiles);
            Func<Tuple<DestinationDefinition,string,RemoteConnectionOptions>> readDraft=delegate{decimal gib;if(!decimal.TryParse(capacity.Text,NumberStyles.Number,CultureInfo.InvariantCulture,out gib)||gib<=0||gib>long.MaxValue/(1024M*1024*1024))throw new ArgumentException("容量限制必须大于零。");
                string path=normalize(location.Text.Trim());var module=selectedModule();RepositoryModules.Require(module.Protocol);
                if(ssh()&&string.IsNullOrWhiteSpace(username.Text))throw new ArgumentException(selectedModule().Protocol=="sftp"?"请填写 SFTP 账号。":"请填写 SSH 账号。");if(ssh()&&authentication.SelectedIndex==1&&(!Path.IsPathRooted(privateKey.Text.Trim())||!File.Exists(privateKey.Text.Trim())))throw new ArgumentException(selectedModule().Protocol=="sftp"?"请选择可读取的 SFTP 私钥文件。":"请选择可读取的 SSH 私钥文件。");var destination=new DestinationDefinition{Id=original==null?"d-"+Guid.NewGuid().ToString("N").Substring(0,10):original.Id,Protocol=module.Protocol=="local"?(original!=null&&(original.Protocol=="local"||original.Protocol=="smb")&&path.Equals(original.Location,StringComparison.OrdinalIgnoreCase)?original.Protocol:Display.FilesystemProtocol(path)):module.Protocol,Location=path,MaxBytes=(long)(gib*1024*1024*1024),Enabled=original==null||original.Enabled};
                string title=string.IsNullOrWhiteSpace(name.Text)?(!filesystem()?Uri.UnescapeDataString(new Uri(path).AbsolutePath.TrimEnd('/').Split('/').Last()):Path.GetFileName(path.TrimEnd('\\'))):name.Text.Trim();return Tuple.Create(destination,string.IsNullOrEmpty(title)?path:title,readSettings());};
            if(applyAsync==null){save.Click+=async delegate{try{var draft=readDraft();save.IsEnabled=false;if(draft.Item1.Protocol=="webdav")await AuthorizeOpenList(window,draft.Item1.Location,draft.Item3,authorization);apply(draft.Item1,draft.Item2);if(saveConnection!=null&&selectedModule().Authentication!=RepositoryAuthentication.None)saveConnection(draft.Item1.Id,draft.Item3);window.DialogResult=true;}catch(Exception error){MessageBox.Show(window,error.Message,"请检查设置",MessageBoxButton.OK,MessageBoxImage.Information);}finally{save.IsEnabled=true;}};}
            else{bool saving=false;var progress=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0)};body.Children.Add(progress);OperationTextColors.BindState(window,progress,()=>saving?OperationState.Running:OperationState.None);window.RegisterName("MigrationProgress",progress);window.Closing+=delegate(object sender,System.ComponentModel.CancelEventArgs e){if(saving)e.Cancel=true;};save.Click+=async delegate{try{var draft=readDraft();saving=true;save.IsEnabled=false;((Button)window.FindName("Cancel")).IsEnabled=false;body.IsEnabled=false;if(draft.Item1.Protocol=="webdav")await AuthorizeOpenList(window,draft.Item1.Location,draft.Item3,authorization);await applyAsync(draft.Item1,draft.Item2,draft.Item3,migrateFiles.IsChecked==true,message=>progress.Text=message);saving=false;window.DialogResult=true;}catch(Exception error){
#if UI_TEST
throw new InvalidOperationException("Warehouse save failed: "+error.Message,error);
#else
MessageBox.Show(window,error.Message,"仓库未完成保存",MessageBoxButton.OK,MessageBoxImage.Information);
#endif
}finally{saving=false;save.IsEnabled=true;((Button)window.FindName("Cancel")).IsEnabled=true;body.IsEnabled=true;}};}
            Show(window);
        }
        public static void Warehouse(Window owner,StorageTopology topology,BackupGroupDefinition original,Action<BackupGroupDefinition> apply,string mode="raid0")
        {
            StackPanel body;Button save;var window=Form(owner,original==null?"新建"+mode+"组":"编辑"+(original.Mode??"raid0")+"组",out body,out save);var name=Text(window,body,"Name",(original==null?mode:original.Mode??"raid0")+"组名称",original==null?"":original.Name);var notes=Text(window,body,"Notes","备注",original==null?"":original.Notes);
            var selected=Checks(window,body,"Members",(original==null?mode:original.Mode??"raid0")=="raid1"?"每个成员保存1份(支持嵌套)":"按容量占用比例分摊(支持嵌套)",topology.Groups.Where(g=>!g.Retired&&(original==null||g.Id!=original.Id)).Select(g=>new ChoiceRow{Id=g.Id,Name=Display.TargetLabel(topology,g),ToolTip=Display.TargetHint(topology,g),Selected=original!=null&&(original.ChildGroupIds.Contains(g.Id)||g.DestinationIds.Any(id=>original.DestinationIds.Contains(id))&&g.Id.StartsWith("single:"))}));
            Save(save,window,delegate{if(string.IsNullOrWhiteSpace(name.Text)||!selected.Any(r=>r.Selected))throw new ArgumentException("请填写名称并至少选择一个成员。");apply(new BackupGroupDefinition{Id=original==null?"warehouse-"+Guid.NewGuid().ToString("N"):original.Id,Name=name.Text.Trim(),Notes=notes.Text.Trim(),Mode=original==null?mode:original.Mode??"raid0",ChildGroupIds=selected.Where(r=>r.Selected).Select(r=>r.Id).ToList()});});Show(window);
        }
        public static BackupTaskDefinition Task(Window owner,WorkspaceDefinition workspace,StorageTopology topology,BackupTaskDefinition original,Action<BackupTaskDefinition> validate)
        {
            StackPanel body;Button save;var window=Form(owner,original==null?"新建任务":"编辑任务",out body,out save);var name=Text(window,body,"Name","任务名称",original==null?"新任务":original.Name);
            body.Children.Add(new TextBlock{Text="备份密码",Margin=new Thickness(0,12,0,6),FontWeight=FontWeights.SemiBold});
            var passwordOptions=new StackPanel{Orientation=Orientation.Horizontal};body.Children.Add(passwordOptions);
            var defaultPassword=new RadioButton{Content="默认密码",GroupName="TaskPasswordMode",IsChecked=original==null||!original.UseCustomPassword,Margin=new Thickness(0,0,20,0),ToolTip="使用设置中的备份密码"};
            var customPassword=new RadioButton{Content="自定义密码",GroupName="TaskPasswordMode",IsChecked=original!=null&&original.UseCustomPassword};
            window.RegisterName("DefaultPassword",defaultPassword);window.RegisterName("CustomPassword",customPassword);passwordOptions.Children.Add(defaultPassword);passwordOptions.Children.Add(customPassword);
            var password=new TextBox{Text=original!=null&&original.UseCustomPassword?original.Password??"":"",MaxLength=99,Padding=new Thickness(8),Margin=new Thickness(0,8,0,0),IsEnabled=customPassword.IsChecked==true};window.RegisterName("TaskPassword",password);body.Children.Add(password);
            defaultPassword.Checked+=delegate{password.IsEnabled=false;};customPassword.Checked+=delegate{password.IsEnabled=true;};
            body.Children.Add(new TextBlock{Text="默认密码使用设置中的备份密码；自定义密码仅用于本任务后续新增备份包，密码长度1~99位。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0),Foreground=System.Windows.Media.Brushes.SlateGray});
            var sources=Checks(window,body,"Sources","待备份文件夹（可多选）",workspace.Sources.Where(s=>!s.Deleted).Select(s=>new ChoiceRow{Id=s.Id,Name=s.ToString(),Selected=original!=null&&original.SourceIds.Contains(s.Id)}));
            var targets=Checks(window,body,"Targets","备份仓库(raid1)",topology.Groups.Where(g=>!g.Retired&&topology.ResolveDestinations(g.Id,false).Count>0).Select(g=>new ChoiceRow{Id=g.Id,Name=Display.TargetLabel(topology,g),ToolTip=Display.TargetHint(topology,g),Selected=original!=null&&original.TargetIds.Contains(g.Id)}));
            body.Children.Add(new TextBlock{Text="保存时间",Margin=new Thickness(0,8,0,4)});
            var retentionRow=new StackPanel{Orientation=Orientation.Horizontal};body.Children.Add(retentionRow);
            var retentionValue=new TextBox{Text=(original==null?1:original.SavedTimeValue).ToString(),Width=84,Padding=new Thickness(7,5,7,5),MaxLength=10};window.RegisterName("RetentionValue",retentionValue);retentionRow.Children.Add(retentionValue);
            string initialUnit=original==null?"year":original.SavedTimeUnit;var retentionUnits=new List<RadioButton>();
            foreach(var unit in new[]{new[]{"day","天"},new[]{"month","月"},new[]{"year","年"}}){var radio=new RadioButton{Content=unit[1],Tag=unit[0],GroupName="RetentionUnit",IsChecked=initialUnit==unit[0],VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(14,0,0,0)};window.RegisterName("RetentionUnit_"+unit[0],radio);retentionUnits.Add(radio);retentionRow.Children.Add(radio);}
            body.Children.Add(new TextBlock{Text="智能轮换：保留过去24小时的每小时最早版本、过去1个月的每天最早版本、更旧的版本保留周最早版本(周一开始)。每个任务的最新成功备份版本始终保留，不受保存时间和自动轮换、清理规则影响，仅可手动删除；其他超出保存时间的备份将在修复完整性时删除。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0),Foreground=System.Windows.Media.Brushes.SlateGray});
            BackupTaskDefinition result=null;Save(save,window,delegate{if(string.IsNullOrWhiteSpace(name.Text))throw new ArgumentException("任务名称无效。");
                int retention;if(!int.TryParse(retentionValue.Text,out retention)||retention<1||!retentionUnits.Any(r=>r.IsChecked==true))throw new ArgumentException("保存时间请输入正整数，并选择天、月或年。");
                if(!sources.Any(s=>s.Selected)||!targets.Any(t=>t.Selected))throw new ArgumentException("请至少选择一个文件夹和一个仓库或组合。");
                if(customPassword.IsChecked==true&&(string.IsNullOrEmpty(password.Text)||password.Text.Length>99))throw new ArgumentException("自定义密码长度应为 1~99 位。");
                result=original==null?new BackupTaskDefinition{Id="task-"+Guid.NewGuid().ToString("N")}:JsonSettings.Decode(JsonSettings.Encode(original),new BackupTaskDefinition());
                result.UseCustomPassword=customPassword.IsChecked==true;result.Password=result.UseCustomPassword?password.Text:original==null||original.UseCustomPassword?"":original.Password;
                result.RetentionValue=retention;result.RetentionUnit=(string)retentionUnits.Single(r=>r.IsChecked==true).Tag;if(result.RetentionUnit=="year")result.RetentionYears=retention;
                result.Name=name.Text.Trim();result.SourceIds=sources.Where(s=>s.Selected).Select(s=>s.Id).ToList();result.TargetIds=targets.Where(t=>t.Selected).Select(t=>t.Id).ToList();result.Copies=Math.Max(1,result.RequiredCopies);result.PendingTargetReplacement=false;
                foreach(var source in workspace.Sources.Where(s=>result.SourceIds.Contains(s.Id)&&(original==null||!original.SourceIds.Contains(s.Id))))result.Selection.Set(source.Location,true,true);
                validate(result);});return Show(window)?result:null;
        }
        public static string Settings(Window owner,string current){int ignored;return Settings(owner,current,1024,out ignored);}
        public static string Settings(Window owner,string current,int memoryMiB,out int selectedMemory)
        {System.Collections.Generic.List<string> ignored;return Settings(owner,current,memoryMiB,out selectedMemory,null,new string[0],out ignored);}
        public static string Settings(Window owner,string current,int memoryMiB,out int selectedMemory,StorageTopology topology,System.Collections.Generic.IEnumerable<string> savedTargets,out System.Collections.Generic.List<string> selectedTargets)
        {string ignored;return Settings(owner,current,current,memoryMiB,out selectedMemory,topology,savedTargets,out selectedTargets,out ignored);}
        public static string Settings(Window owner,string current,string currentDatabase,int memoryMiB,out int selectedMemory,StorageTopology topology,System.Collections.Generic.IEnumerable<string> savedTargets,out System.Collections.Generic.List<string> selectedTargets,out string selectedDatabasePassword)
        {bool ignored;return Settings(owner,current,currentDatabase,memoryMiB,out selectedMemory,topology,savedTargets,out selectedTargets,out selectedDatabasePassword,out ignored);}
        public static string Settings(Window owner,string current,string currentDatabase,int memoryMiB,out int selectedMemory,StorageTopology topology,System.Collections.Generic.IEnumerable<string> savedTargets,out System.Collections.Generic.List<string> selectedTargets,out string selectedDatabasePassword,out bool backupNow)
        {BandwidthSchedule ignored;return Settings(owner,current,currentDatabase,memoryMiB,out selectedMemory,topology,savedTargets,out selectedTargets,out selectedDatabasePassword,out backupNow,new BandwidthSchedule(),out ignored);}
        public static string Settings(Window owner,string current,string currentDatabase,int memoryMiB,out int selectedMemory,StorageTopology topology,IEnumerable<string> savedTargets,out List<string> selectedTargets,out string selectedDatabasePassword,out bool backupNow,BandwidthSchedule savedBandwidth,out BandwidthSchedule selectedBandwidth)
        {
            selectedBandwidth=savedBandwidth;backupNow=false;bool immediate=false;selectedMemory=memoryMiB;selectedTargets=savedTargets.ToList();selectedDatabasePassword=currentDatabase;int parsed=memoryMiB;StackPanel body;Button save;var window=Form(owner,"设置",out body,out save,true);
            var label=new TextBlock{Text="备份密码",ToolTip="不影响已有备份文件",Margin=new Thickness(0,8,0,6)};body.Children.Add(label);var password=new TextBox{Text=current,Padding=new Thickness(8),ToolTip="不影响已有备份文件"};window.RegisterName("BackupPassword",password);body.Children.Add(password);
            body.Children.Add(new TextBlock{Text="用于后续新增备份包，不影响已有备份文件，密码长度1~99位。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0),Foreground=System.Windows.Media.Brushes.SlateGray});
            var databasePassword=Text(window,body,"DatabasePassword","数据库密码",currentDatabase);databasePassword.ToolTip="用于后续新增数据库备份包，不影响已有备份文件，密码长度1~99位。";
            body.Children.Add(new TextBlock{Text="内存限制 (MiB)",Margin=new Thickness(0,18,0,6)});var memory=new TextBox{Text=memoryMiB.ToString(),Padding=new Thickness(8)};window.RegisterName("MemoryLimitMiB",memory);body.Children.Add(memory);
            body.Children.Add(new TextBlock{Text="默认 2048（2 GiB）；范围 256–65536。缓冲满后等候上传释放空间。修改后下次备份生效。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0),Foreground=System.Windows.Media.Brushes.SlateGray});
            var selected=new System.Collections.Generic.List<ChoiceRow>();
            if(topology!=null)
            {
                var choices=topology.Groups.Where(g=>!g.Retired&&topology.ResolveDestinations(g.Id,false).Count>0).Select(g=>new ChoiceRow{Id=g.Id,Name=Display.TargetLabel(topology,g),ToolTip=Display.TargetHint(topology,g),Selected=savedTargets.Contains(g.Id)}).ToList();
                choices.AddRange(savedTargets.Where(id=>!choices.Any(row=>row.Id==id)).Select(id=>new ChoiceRow{Id=id,Name="（已移除）"+id,Selected=true,ToolTip="请取消选择或补选其他仓库。"}));
                selected=Checks(window,body,"DatabaseTargets","数据库备份仓库（独立于普通任务）",choices);
                body.Children.Add(new TextBlock{Text="每次任务完成后自动备份数据库，同时备份到所有选中的仓库。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0),Foreground=System.Windows.Media.Brushes.SlateGray});
            }
            var bandwidthBody=new StackPanel();var bandwidthEditor=new BandwidthSettingsEditor(window,bandwidthBody,savedBandwidth);BandwidthSchedule parsedBandwidth=savedBandwidth;
            Action validate=delegate{
                parsedBandwidth=bandwidthEditor.Read();
                if(!int.TryParse(memory.Text,out parsed)||parsed<256||parsed>65536)throw new ArgumentException("内存限制应为 256–65536 MiB。");
                var targets=selected.Where(row=>row.Selected).Select(row=>row.Id).ToList();
                if(password.Text.Length>99)throw new ArgumentException("备份密码长度为1~99位。");
                if(databasePassword.Text.Length>99||targets.Count>0&&databasePassword.Text.Length==0)throw new ArgumentException("数据库备份需要数据库密码，密码长度为1~99位。");
                if(topology!=null&&targets.Count>0){if(targets.Any(id=>!topology.Groups.Any(g=>g.Id==id&&!g.Retired)))throw new ArgumentException("请取消已移除的数据库仓库。");WorkspaceMigration.ValidateTargetPolicy(new BackupTaskDefinition{Id="__catalog__",TargetIds=targets},topology);}
            };
            Save(save,window,validate);
            if(topology!=null){var now=new Button{Content="立即备份数据库",Padding=new Thickness(12,6,12,6),Margin=new Thickness(0,10,0,0),HorizontalAlignment=HorizontalAlignment.Left,ToolTip="保存当前设置并立即备份数据库。"};window.RegisterName("BackupDatabaseNow",now);body.Children.Add(now);Action update=()=>now.IsEnabled=selected.Any(row=>row.Selected);foreach(var check in ((StackPanel)window.FindName("DatabaseTargets")).Children.OfType<CheckBox>()){check.Checked+=delegate{update();};check.Unchecked+=delegate{update();};}update();Save(now,window,delegate{validate();immediate=true;});}
            body.Children.Add(bandwidthBody);if(!Show(window))return null;selectedBandwidth=parsedBandwidth;backupNow=immediate;selectedMemory=parsed;selectedTargets=topology==null?savedTargets.ToList():selected.Where(row=>row.Selected).Select(row=>row.Id).ToList();selectedDatabasePassword=databasePassword.Text;return password.Text;
        }
        public static int? DeleteChoice(Window owner,string title,string message,string[] options){StackPanel body;Button save;var window=Form(owner,title,out body,out save);save.Content="确认删除";body.Children.Add(new TextBlock{Text=message,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)});var mode=new ComboBox{ItemsSource=options,SelectedIndex=0,Padding=new Thickness(8)};window.RegisterName("DeleteMode",mode);body.Children.Add(mode);Save(save,window,delegate{});return Show(window)?(int?)mode.SelectedIndex:null;}
    }
}
