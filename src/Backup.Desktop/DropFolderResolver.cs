using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
namespace Backup.Desktop
{
    internal static class DropFolderResolver
    {
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(IntPtr window,StringBuilder name,int count);
        [DllImport("user32.dll")]private static extern IntPtr GetAncestor(IntPtr window,uint flag);
        [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr window);
        [StructLayout(LayoutKind.Sequential)]private struct Rectangle {public int Left,Top,Right,Bottom;}
        [DllImport("user32.dll")]private static extern bool GetWindowRect(IntPtr window,out Rectangle rectangle);
        private static bool AtPoint(IntPtr window,VirtualFileDrop.DropPoint point)
        {Rectangle r;return IsWindowVisible(window)&&GetWindowRect(window,out r)&&point.X>=r.Left&&point.X<r.Right&&point.Y>=r.Top&&point.Y<r.Bottom;}
        private static string ClassName(IntPtr window){var text=new StringBuilder(128);GetClassName(window,text,text.Capacity);return text.ToString();}
        private static void Release(object value){if(value!=null&&Marshal.IsComObject(value))try{Marshal.ReleaseComObject(value);}catch(ArgumentException){}}
        internal static bool DirectoryPath(string path)
        {try{string server;return !string.IsNullOrWhiteSpace(path)&&(RestoreDestination.TryServerRoot(path,out server)||Path.IsPathRooted(path)&&Directory.Exists(path));}catch(ArgumentException){return false;}catch(NotSupportedException){return false;}catch(PathTooLongException){return false;}}
        internal static async Task<string> ResolveAsync(VirtualFileDrop.DropPoint point,CancellationToken cancellation)
        {
            var done=new TaskCompletionSource<string>();var worker=new Thread(()=>{try{done.TrySetResult(Resolve(point));}catch(Exception error){done.TrySetException(error);}}){IsBackground=true,Name="Download folder lookup"};worker.SetApartmentState(ApartmentState.STA);worker.Start();
            var completed=await Task.WhenAny(done.Task,Task.Delay(5000,cancellation));cancellation.ThrowIfCancellationRequested();return completed==done.Task?await done.Task:null;
        }
        private static string Resolve(VirtualFileDrop.DropPoint point)
        {
            if(point.Window==IntPtr.Zero||!AtPoint(point.Window,point))return null;
            string kind=ClassName(point.Window),folder=null;object shell=null,windows=null,view=null,document=null,currentFolder=null,currentItem=null;
            if(kind!="Progman"&&kind!="WorkerW"&&kind!="CabinetWClass"&&kind!="ExploreWClass")return null;
            try
            {
                shell=Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                if(kind=="Progman"||kind=="WorkerW")folder=Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                else
                {
                    windows=((dynamic)shell).Windows();int count=(int)((dynamic)windows).Count;
                    for(int index=0;index<count;index++)
                    {
                        object candidate=null;
                        try{candidate=((dynamic)windows).Item(index);if(candidate==null)continue;var handle=new IntPtr(Convert.ToInt64(((dynamic)candidate).HWND));if(GetAncestor(handle,2)!=point.Window)continue;document=((dynamic)candidate).Document;currentFolder=((dynamic)document).Folder;currentItem=((dynamic)currentFolder).Self;folder=(string)((dynamic)currentItem).Path;view=candidate;candidate=null;break;}
                        catch(COMException){}finally{Release(candidate);}
                    }
                }
                if(!DirectoryPath(folder))return null;
                var element=AutomationElement.FromPoint(new Point(point.X,point.Y));
                for(int depth=0;element!=null&&depth<20;depth++,element=TreeWalker.ControlViewWalker.GetParent(element))
                {
                    var type=element.Current.ControlType;
                    if(type==ControlType.List||type==ControlType.DataGrid||element.Current.ClassName=="UIItemsView"||element.Current.AutomationId=="ItemsView")return folder;
                    if(type!=ControlType.ListItem&&type!=ControlType.DataItem&&type!=ControlType.TreeItem)continue;
                    // A sidebar label alone does not identify its path. Never guess a
                    // same-named child of the open folder as the drop destination.
                    if(type==ControlType.TreeItem){string help=element.Current.HelpText;return DirectoryPath(help)?Path.GetFullPath(help):null;}
                    string name=element.Current.Name;
                    if(currentFolder!=null)
                    {
                        object item=null;try{item=((dynamic)currentFolder).ParseName(name);if(item!=null&&(bool)((dynamic)item).IsFolder){string path=(string)((dynamic)item).Path;if(DirectoryPath(path))return Path.GetFullPath(path);}return null;}finally{Release(item);}
                    }
                    string child=Path.Combine(folder,name);return DirectoryPath(child)?Path.GetFullPath(child):null;
                }
                return null;
            }
            catch(ElementNotAvailableException){return null;}catch(InvalidOperationException){return null;}catch(COMException){return null;}
            finally{Release(currentItem);Release(currentFolder);Release(document);Release(view);Release(windows);Release(shell);}
        }
    }
}
