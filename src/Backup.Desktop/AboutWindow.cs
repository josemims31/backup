using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Backup.Desktop
{
    internal sealed class AboutDisplay
    {
        internal string CurrentVersion=Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
        internal string Status="尚未发布可安装的更新";
        internal string Notes="• 更新中断后自动恢复程序文件。\n• 新版本启动失败时自动回滚。\n• 确认新版本正常启动后清理更新缓存。";
        internal bool UpdateAvailable=false;
        internal bool Checking,Downloading,CanInstall=true;
        internal string InstallLabel="立即更新";
        internal int UpdateMode=1;
    }

    internal sealed class AboutWindow : Window
    {
        internal readonly Button CheckUpdate,InstallUpdate,RemindLater,IgnoreVersion;
        internal readonly ComboBox UpdateMode;
        internal readonly TextBlock UpdateStatus;
        internal readonly TextBox ReleaseNotes;
        private bool applyingDisplay;
        internal event Action CheckRequested,InstallRequested,LaterRequested,IgnoreRequested;
        internal event Action<int> ModeChanged;
        private static Brush Color(string value){return (Brush)new BrushConverter().ConvertFromString(value);}
        private static TextBlock Label(string text,double size=13,Brush foreground=null)
        {return new TextBlock{Text=text,FontSize=size,Foreground=foreground??Color("#20344A"),VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap};}
        private static Button ActionButton(string text,int width)
        {return new Button{Content=text,Width=width,MinHeight=34,Margin=new Thickness(0,0,8,0)};}
        private static void Row(Grid grid,UIElement element,int row,int column=0)
        {Grid.SetRow(element,row);Grid.SetColumn(element,column);grid.Children.Add(element);}
        private static void Link(Grid grid,int row,string label,string address)
        {
            var caption=Label(label,13,Color("#6C7888"));Row(grid,caption,row);
            var link=new Hyperlink(new Run(address)){NavigateUri=new Uri(address),Foreground=Color("#0067B8"),TextDecorations=null};
            link.RequestNavigate+=delegate(object sender,System.Windows.Navigation.RequestNavigateEventArgs e){Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri){UseShellExecute=true});e.Handled=true;};
            var text=new TextBlock{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,4,0,4)};text.Inlines.Add(link);Row(grid,text,row,1);
        }
        internal AboutWindow(Window owner,AboutDisplay display)
        {
            Title="关于 Backup-fang523";Width=660;Height=665;MinWidth=620;MinHeight=620;Owner=owner;WindowStartupLocation=WindowStartupLocation.CenterOwner;
            Background=Brushes.White;FontFamily=owner.FontFamily;FontSize=13;ResizeMode=ResizeMode.NoResize;WindowStyle=WindowStyle.SingleBorderWindow;ShowInTaskbar=false;Icon=owner.Icon;
            UseLayoutRounding=true;SnapsToDevicePixels=true;Resources.MergedDictionaries.Add(owner.Resources);
            var root=new Grid{Margin=new Thickness(24,20,24,20)};
            foreach(var size in new[]{GridLength.Auto,GridLength.Auto,GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto})root.RowDefinitions.Add(new RowDefinition{Height=size});
            Content=root;
            var header=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,18)};
            header.Children.Add(new TextBlock{Text="Backup-fang523",FontSize=25,FontWeight=FontWeights.SemiBold,Foreground=Color("#20344A")});
            header.Children.Add(new TextBlock{Text="当前版本 "+display.CurrentVersion,Foreground=Color("#6C7888"),VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(16,0,0,4)});Row(root,header,0);
            var links=new Grid{Margin=new Thickness(0,0,0,16)};links.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(72)});links.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});links.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});links.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            Link(links,0,"博客", "https://www.pidexe.com/");Link(links,1,"GitHub", "https://github.com/josemims31/backup");Row(root,links,1);
            UpdateStatus=Label(display.Status,14,display.UpdateAvailable?Color("#0067B8"):Color("#56677B"));UpdateStatus.FontWeight=display.UpdateAvailable?FontWeights.SemiBold:FontWeights.Normal;
            var status=new Border{Background=Color("#F5F8FC"),BorderBrush=Color("#DFE5ED"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(4),Padding=new Thickness(13,12,13,12),Margin=new Thickness(0,0,0,18),Child=UpdateStatus};Row(root,status,2);
            var notesTitle=Label("本次更新内容",14);notesTitle.FontWeight=FontWeights.SemiBold;notesTitle.Margin=new Thickness(0,0,0,9);Row(root,notesTitle,3);
            ReleaseNotes=new TextBox{Text=display.Notes,IsReadOnly=true,IsUndoEnabled=false,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalContentAlignment=VerticalAlignment.Top,Background=Brushes.White,Foreground=Color("#20344A"),BorderBrush=Color("#DFE5ED"),Padding=new Thickness(13,12,13,12),Margin=new Thickness(0,0,0,18)};Row(root,ReleaseNotes,4);
            var preferences=new StackPanel{Margin=new Thickness(0,0,0,20)};
            var modeLabel=Label("更新设置",14);modeLabel.FontWeight=FontWeights.SemiBold;modeLabel.Margin=new Thickness(0,0,0,9);preferences.Children.Add(modeLabel);
            UpdateMode=new ComboBox{Width=228,MinHeight=34,HorizontalAlignment=HorizontalAlignment.Left,SelectedIndex=-1,Margin=new Thickness(0)};foreach(string choice in new[]{"自动检查","自动检查并安装","不检查更新"})UpdateMode.Items.Add(choice);UpdateMode.SelectedIndex=display.UpdateMode;preferences.Children.Add(UpdateMode);
            var help=Label("每天最多自动检查一次；有任务运行时，完成后再更新。",12,Color("#6C7888"));help.Margin=new Thickness(0,9,0,0);preferences.Children.Add(help);Row(root,preferences,5);
            var footer=new StackPanel{Orientation=Orientation.Horizontal};
            CheckUpdate=ActionButton("检查更新",96);InstallUpdate=ActionButton("立即更新",96);InstallUpdate.IsEnabled=display.UpdateAvailable;
            if(display.UpdateAvailable){InstallUpdate.Background=Color("#0078D4");InstallUpdate.Foreground=Brushes.White;InstallUpdate.BorderBrush=Color("#0078D4");}
            RemindLater=ActionButton("下次再问",92);IgnoreVersion=ActionButton("不提示更新",104);RemindLater.Visibility=IgnoreVersion.Visibility=display.UpdateAvailable?Visibility.Visible:Visibility.Collapsed;
            footer.Children.Add(CheckUpdate);footer.Children.Add(InstallUpdate);footer.Children.Add(RemindLater);footer.Children.Add(IgnoreVersion);Row(root,footer,6);
            CheckUpdate.Click+=delegate{if(CheckRequested!=null)CheckRequested();};InstallUpdate.Click+=delegate{if(InstallRequested!=null)InstallRequested();};RemindLater.Click+=delegate{if(LaterRequested!=null)LaterRequested();};IgnoreVersion.Click+=delegate{if(IgnoreRequested!=null)IgnoreRequested();};
            PreviewKeyDown+=delegate(object sender,System.Windows.Input.KeyEventArgs e){if(e.Key==System.Windows.Input.Key.Escape&&!UpdateMode.IsDropDownOpen){e.Handled=true;Close();}};
            UpdateMode.SelectionChanged+=delegate{if(!applyingDisplay&&ModeChanged!=null)ModeChanged(UpdateMode.SelectedIndex);};
            Apply(display);
        }
        internal void Apply(AboutDisplay display)
        {
            applyingDisplay=true;try{
                UpdateStatus.Text=display.Status;UpdateStatus.Foreground=display.UpdateAvailable?Color("#0067B8"):Color("#56677B");UpdateStatus.FontWeight=display.UpdateAvailable?FontWeights.SemiBold:FontWeights.Normal;
                if(ReleaseNotes.Text!=display.Notes)ReleaseNotes.Text=display.Notes;UpdateMode.SelectedIndex=display.UpdateMode;
                CheckUpdate.IsEnabled=!display.Checking;CheckUpdate.Content=display.Checking?"正在检查…":"检查更新";
                InstallUpdate.IsEnabled=display.UpdateAvailable&&display.CanInstall&&!display.Downloading;InstallUpdate.Content=display.InstallLabel;
                InstallUpdate.Background=display.UpdateAvailable?Color("#0078D4"):Color("#F3F3F3");InstallUpdate.Foreground=display.UpdateAvailable?Brushes.White:Color("#202020");InstallUpdate.BorderBrush=display.UpdateAvailable?Color("#0078D4"):Color("#CFCFCF");
                RemindLater.Visibility=IgnoreVersion.Visibility=display.UpdateAvailable?Visibility.Visible:Visibility.Collapsed;
            }finally{applyingDisplay=false;}
        }
    }
}
