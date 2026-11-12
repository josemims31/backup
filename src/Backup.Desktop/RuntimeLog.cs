using System;
using System.IO;
using System.Text;
namespace Backup.Desktop
{
    internal static class RuntimeLog
    {
        private static readonly object gate=new object();
        public static void Append(string directory,string name,string message)
        {
            if(Path.GetFileName(name)!=name)throw new ArgumentException("Invalid log name.");
            lock(gate){string folder=Path.Combine(directory,"cache","logs");Directory.CreateDirectory(folder);string file=Path.Combine(folder,name),previous=file+".previous";
                if(File.Exists(file)&&new FileInfo(file).Length>=1024*1024){if(File.Exists(previous))File.Delete(previous);File.Move(file,previous);}
                if(message.Length>65536)message=message.Substring(0,65536);
                File.AppendAllText(file,DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+" "+message+Environment.NewLine,new UTF8Encoding(false));
            }
        }
    }
}
