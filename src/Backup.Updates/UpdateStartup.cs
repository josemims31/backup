using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Backup.Updates
{
    public sealed class LegacyUpdateState {public string PreparedPath,PreparedVersion;}
    public sealed class UpdateStartup:IDisposable
    {
        private readonly string installation,version;private readonly string[] arguments;
        private UpdateTransaction transaction;private UpdateStartupRecord record;private Mutex mutex;
        private readonly ManualResetEvent cleanupFinished=new ManualResetEvent(false);
        private bool entered,completed;public bool ExitRequested{get;private set;}
        public bool Pending{get{return transaction!=null&&!completed;}}
        private UpdateStartup(string directory,string currentVersion,string[] args){installation=directory;version=currentVersion;arguments=args;}
        public static UpdateStartup Begin(string installation,string version,string[] args)
        {
            var startup=new UpdateStartup(installation,version,args);
            try{startup.Prepare();return startup;}catch{startup.Dispose();throw;}
        }
        private void Prepare()
        {
            transaction=UpdateTransactions.Active(installation);
            if(transaction==null){AdoptLegacy();transaction=UpdateTransactions.Active(installation);}
            if(transaction==null)return;
            if(transaction.State==UpdateTransactions.Confirmed||transaction.State==UpdateTransactions.RolledBack)
            {using(var gate=UpdateTransactions.Mutex(installation,false))if(UpdateTransactions.Enter(gate,TimeSpan.Zero))try{UpdateTransactions.CompleteTerminal(transaction);}finally{gate.ReleaseMutex();}transaction=null;return;}
            if(transaction.State!=UpdateTransactions.AwaitingStartup){Recover();return;}
            mutex=UpdateTransactions.Mutex(installation,true);entered=UpdateTransactions.Enter(mutex,TimeSpan.Zero);
            // Let the existing instance receive scheduled requests through its normal IPC path.
            if(!entered){transaction=null;return;}
            var previous=UpdateTransactions.Startup(transaction);
            if(previous!=null)
            {
                if(previous.Healthy){using(var gate=UpdateTransactions.Mutex(installation,false))if(UpdateTransactions.Enter(gate,TimeSpan.FromSeconds(10)))try{var active=UpdateTransactions.Active(installation);if(active!=null&&active.Id==transaction.Id)UpdateTransactions.Confirm(active);}finally{gate.ReleaseMutex();}transaction=null;return;}
                Recover();return;
            }
            try
            {
                var manifest=UpdateProtocol.ReadManifest(installation);
                if(UpdateProtocol.ParseVersion(manifest.Version)!=UpdateProtocol.ParseVersion(version)||UpdateProtocol.ParseVersion(version)!=UpdateProtocol.ParseVersion(transaction.Version))throw new IOException("更新后的程序版本不一致。");
                UpdateProtocol.VerifyFiles(installation,manifest);
            }
            catch(Exception error){transaction.Error=error.Message;UpdateTransactions.Save(transaction);Recover();return;}
            using(var process=Process.GetCurrentProcess())record=new UpdateStartupRecord{TransactionId=transaction.Id,ProcessId=process.Id,StartUtcTicks=process.StartTime.ToUniversalTime().Ticks,Arguments=arguments};
            UpdateProtocol.AtomicText(UpdateTransactions.StartupPath(transaction),UpdateProtocol.Json(record));
            using(UpdateTransactions.Launch(UpdateTransactions.RecoveryHelper(installation,transaction.Request.Payload),new[]{"--monitor",UpdateTransactions.JournalPath(transaction)},installation)){}
        }
        private void Recover()
        {
            using(var process=Process.GetCurrentProcess())
            using(UpdateTransactions.Launch(UpdateTransactions.RecoveryHelper(installation,transaction.Request.Payload),new[]{"--recover",UpdateTransactions.JournalPath(transaction),process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),UpdateProtocol.Json(arguments)},installation)){}
            ExitRequested=true;
        }
        private void AdoptLegacy()
        {
            // A pre-1.1.2 helper cannot write a journal. Adopt its completed install before loading Core.
            string statePath=Path.Combine(UpdateProtocol.UpdatesDirectory(installation),"state.json");if(!File.Exists(statePath))return;
            var state=UpdateTransactions.Read<LegacyUpdateState>(statePath,1048576);
            if(state==null||string.IsNullOrEmpty(state.PreparedPath)||string.IsNullOrEmpty(state.PreparedVersion)||UpdateProtocol.ParseVersion(state.PreparedVersion)!=UpdateProtocol.ParseVersion(version))return;
            using(var gate=UpdateTransactions.Mutex(installation,false))
            {
                if(!UpdateTransactions.Enter(gate,TimeSpan.Zero))return;
                try
                {
                    if(UpdateTransactions.Active(installation)!=null)return;
                    string session=UpdateInstaller.SessionDirectory(installation,state.PreparedPath),requestPath=Path.Combine(session,"request.json");
                    if(!File.Exists(requestPath)||File.Exists(Path.Combine(session,"transaction.json")))return;
                    var request=UpdateTransactions.Read<UpdateRequest>(requestPath,65536);
                    if(!UpdateTransactions.Normalize(request.Installation).Equals(UpdateTransactions.Normalize(installation),StringComparison.OrdinalIgnoreCase)||!UpdateTransactions.Normalize(request.Payload).Equals(UpdateTransactions.Normalize(state.PreparedPath),StringComparison.OrdinalIgnoreCase)||UpdateProtocol.ParseVersion(request.Version)!=UpdateProtocol.ParseVersion(version))throw new IOException("旧更新请求与安装目录不一致。");
                    var next=UpdateProtocol.ReadManifest(request.Payload);UpdateProtocol.VerifyFiles(request.Payload,next);
                    string rollback=Path.Combine(session,"rollback");var old=UpdateProtocol.ReadManifest(rollback);UpdateProtocol.VerifyFiles(rollback,old);
                    var value=new UpdateTransaction{Id=Guid.NewGuid().ToString("N"),Installation=UpdateTransactions.Normalize(installation),Session=session,Version=next.Version,State=UpdateTransactions.AwaitingStartup,Request=request};
                    var paths=old.Files.Select(file=>file.Path).Concat(next.Files.Select(file=>file.Path)).Concat(new[]{UpdateProtocol.ManifestName}).Distinct(StringComparer.OrdinalIgnoreCase);
                    foreach(string relative in paths){string path=UpdateProtocol.FullPath(rollback,relative);bool exists=File.Exists(path);value.Files.Add(new RollbackFile{Path=relative,Existed=exists,Size=exists?new FileInfo(path).Length:0,Sha256=exists?UpdateProtocol.Hash(path):null});}
                    UpdateTransactions.RecoveryHelper(installation,request.Payload);UpdateTransactions.Activate(value);UpdateTransactions.Result(value,false,true,"程序文件已安装，正在确认新版本启动。");
                }
                finally{gate.ReleaseMutex();}
            }
        }
        public void Complete(Action finished)
        {
            if(completed||ExitRequested)return;
            if(record!=null){record.Healthy=true;UpdateProtocol.AtomicText(UpdateTransactions.StartupPath(transaction),UpdateProtocol.Json(record));}
            completed=true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    using(var gate=UpdateTransactions.Mutex(installation,false))
                    {
                        if(!UpdateTransactions.Enter(gate,TimeSpan.FromSeconds(45)))return;
                        try
                        {
                            var active=UpdateTransactions.Active(installation);
                            if(active!=null&&active.State==UpdateTransactions.AwaitingStartup)
                            {var ack=UpdateTransactions.Startup(active);if(ack!=null&&ack.Healthy)UpdateTransactions.Confirm(active);}
                            else if(active!=null)UpdateTransactions.CompleteTerminal(active);
                            UpdateCache.Clean(installation,version);
                        }
                        finally{gate.ReleaseMutex();}
                    }
                }
                catch(Exception error){UpdateTransactions.Log(installation,error);}
                finally{cleanupFinished.Set();if(finished!=null)try{finished();}catch(Exception error){UpdateTransactions.Log(installation,error);}}
            });
        }
        public void Failed(Exception error)
        {
            if(!Pending)return;
            try{if(record!=null){record.Error=error.Message;UpdateProtocol.AtomicText(UpdateTransactions.StartupPath(transaction),UpdateProtocol.Json(record));}UpdateTransactions.Log(installation,error);}catch(Exception){}
        }
        public void Dispose(){if(completed)cleanupFinished.WaitOne(5000);if(entered){mutex.ReleaseMutex();entered=false;}if(mutex!=null){mutex.Dispose();mutex=null;}}
    }
}
