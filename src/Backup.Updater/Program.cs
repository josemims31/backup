using System;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Backup.Updates;

namespace Backup.Updater
{
    internal static class Program
    {
        private static Mutex InstallationMutex(string installation)
        {
            string key=Path.GetFullPath(installation).TrimEnd('\\')+"\\",hash;using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key.ToUpperInvariant()))).Replace("-","");
            var security=new MutexSecurity();security.SetAccessRuleProtection(true,false);security.AddAccessRule(new MutexAccessRule(WindowsIdentity.GetCurrent().User,MutexRights.FullControl,AccessControlType.Allow));security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),MutexRights.FullControl,AccessControlType.Allow));bool created;return new Mutex(false,"Global\\BackupUI-"+hash,out created,security);
        }
        private static void WaitForParent(UpdateRequest request)
        {
            try{using(var parent=Process.GetProcessById(request.ParentProcessId)){if(parent.StartTime.ToUniversalTime().Ticks!=request.ParentStartUtcTicks)return;if(!parent.WaitForExit(600000))throw new IOException("软件尚未完全退出，未替换任何程序文件。");}}
            catch(ArgumentException){}
        }
        private static void Restart(UpdateRequest request)
        {if(!request.ExitAfterUpdate)Process.Start(new ProcessStartInfo(Path.Combine(request.Installation,"Backup.exe"),request.Minimized?"--minimized":""){WorkingDirectory=request.Installation,UseShellExecute=false});}
        public static int Main(string[] args)
        {
            UpdateRequest request=null;string resultPath=null;bool entered=false,restartAllowed=true;Mutex mutex=null;int exitCode=1;
            try
            {
                if(args.Length!=1)throw new ArgumentException("缺少更新请求。");string requestPath=Path.GetFullPath(args[0]);UpdateProtocol.NoLinks(requestPath);if(new FileInfo(requestPath).Length>65536)throw new IOException("更新请求过大。");request=UpdateProtocol.ReadJson<UpdateRequest>(File.ReadAllText(requestPath,Encoding.UTF8));
                string updates=UpdateProtocol.UpdatesDirectory(request.Installation);if(!requestPath.StartsWith(updates+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!Path.GetDirectoryName(requestPath).Equals(Path.GetDirectoryName(request.Payload),StringComparison.OrdinalIgnoreCase))throw new IOException("更新请求路径不正确。");
                resultPath=Path.Combine(updates,"result.json");WaitForParent(request);mutex=InstallationMutex(request.Installation);try{entered=mutex.WaitOne(TimeSpan.FromMinutes(10));}catch(AbandonedMutexException){entered=true;}if(!entered)throw new IOException("软件目录仍在使用，未替换程序文件。");
                UpdateInstaller.Install(request.Installation,request.Payload,request.Version);UpdateProtocol.AtomicText(resultPath,UpdateProtocol.Json(new UpdateResult{Success=true,Version=request.Version,Message="软件已更新至 "+request.Version,TimeUtc=DateTime.UtcNow.ToString("o")}));exitCode=0;
            }
            catch(Exception error)
            {restartAllowed=!(error is UpdateRollbackException);if(resultPath!=null)try{UpdateProtocol.AtomicText(resultPath,UpdateProtocol.Json(new UpdateResult{Success=false,Version=request==null?null:request.Version,Message=error.Message,TimeUtc=DateTime.UtcNow.ToString("o")}));}catch(Exception){} }
            finally{if(entered)mutex.ReleaseMutex();if(mutex!=null)mutex.Dispose();}
            // Preserve scheduled-host exit and minimized state; a rolled-back interactive app can reopen.
            if(request!=null&&resultPath!=null&&entered&&restartAllowed)try{Restart(request);}catch(Exception error){try{File.AppendAllText(Path.Combine(Path.GetDirectoryName(resultPath),"updater.log"),DateTime.UtcNow.ToString("o")+" "+error+Environment.NewLine);}catch(Exception){}}
            return exitCode;
        }
    }
}
