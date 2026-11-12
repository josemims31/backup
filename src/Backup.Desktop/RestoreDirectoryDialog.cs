using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Backup.Desktop
{
    public static partial class EditorDialogs
    {
        public static string RestoreDirectory(Window owner,string title,string initialPath="")
        {
            StackPanel body;Button confirm;var window=Form(owner,title,out body,out confirm);confirm.Content="确定";
            var location=Text(window,body,"Location","目录路径",initialPath);
            var browse=new Button{Content="选择文件夹",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,5,0,3),Padding=new Thickness(9,5,9,5)};window.RegisterName("Browse",browse);body.Children.Add(browse);
            browse.Click+=delegate{string selected=NativeFolderPicker.Choose(window,location.Text,"选择文件夹",true);if(selected!=null)location.Text=selected;};
            body.Children.Add(new TextBlock{Text=@"可输入本地路径、SMB共享目录，例如：\\10.10.10.32\  或 D:\新建文件夹",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0),Foreground=System.Windows.Media.Brushes.SlateGray});
            string result=null;Save(confirm,window,delegate{
                string value=location.Text.Trim();if(value.Length>=2&&value[0]=='"'&&value[value.Length-1]=='"')value=value.Substring(1,value.Length-2);
                if(value.Length==0)throw new IOException("请输入或选择目标路径。");RestoreDestination.Validate(value);string server;
                result=RestoreDestination.TryServerRoot(value,out server)?server:Path.GetFullPath(value);
            });
            window.Loaded+=delegate{location.Focus();if(location.Text.Length>0)location.SelectAll();};return Show(window)?result:null;
        }
    }
}
