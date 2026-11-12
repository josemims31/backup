using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace Backup.Desktop
{
    public static class ScheduleRequestTransport
    {
        public static string MutexName(string directory){return "Global\\BackupUI-"+Hash(Path.GetFullPath(directory).TrimEnd('\\')+"\\");}
        public static Mutex CreateInstanceMutex(string directory)
        {
            var security=new MutexSecurity();security.SetAccessRuleProtection(true,false);
            security.AddAccessRule(new MutexAccessRule(WindowsIdentity.GetCurrent().User,MutexRights.FullControl,AccessControlType.Allow));
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),MutexRights.FullControl,AccessControlType.Allow));
            bool created;return new Mutex(false,MutexName(directory),out created,security);
        }
        internal static string PipeName(string directory){return "BackupSchedule-"+Hash(Path.GetFullPath(directory).TrimEnd('\\')+"\\|"+WindowsIdentity.GetCurrent().User.Value);}
        private static string Hash(string value){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value.ToUpperInvariant()))).Replace("-","");}
        internal static async Task<byte[]> Read(Stream stream,int count,CancellationToken token){var result=new byte[count];int offset=0;while(offset<count){int n=await stream.ReadAsync(result,offset,count-offset,token).ConfigureAwait(false);if(n==0)throw new IOException("计划请求连接已关闭。");offset+=n;}return result;}
        public static bool TrySend(string directory,ScheduleRequest request)
        {
            request.Validate();try{using(var pipe=new NamedPipeClientStream(".",PipeName(directory),PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification)){
                pipe.Connect(500);using(var timeout=new CancellationTokenSource(3000))using(timeout.Token.Register(pipe.Dispose)){
                    byte[] bytes=Encoding.ASCII.GetBytes(request.RequestId+request.ScheduleId);pipe.WriteAsync(bytes,0,bytes.Length,timeout.Token).GetAwaiter().GetResult();pipe.Flush();return Read(pipe,1,timeout.Token).GetAwaiter().GetResult()[0]==1;
                }
            }}catch(IOException){return false;}catch(TimeoutException){return false;}catch(OperationCanceledException){return false;}catch(ObjectDisposedException){return false;}catch(UnauthorizedAccessException){return false;}
        }
    }
    internal sealed class ScheduleRequestHost:IDisposable
    {
        private readonly object gate=new object();private readonly string name;private readonly Func<ScheduleRequest,bool> receive;
        private readonly Task listener;private NamedPipeServerStream current;private bool stopped;
        internal ScheduleRequestHost(string directory,Func<ScheduleRequest,bool> callback){name=ScheduleRequestTransport.PipeName(directory);receive=callback;listener=Task.Run((Func<Task>)Listen);}
        private async Task Listen()
        {
            while(true){lock(gate)if(stopped)return;
                try{var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,PipeAccessRights.FullControl,AccessControlType.Allow));
                    using(var pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,256,256,security)){
                        lock(gate){if(stopped)return;current=pipe;}
                        try{await pipe.WaitForConnectionAsync().ConfigureAwait(false);using(var timeout=new CancellationTokenSource(5000))using(timeout.Token.Register(pipe.Dispose)){
                            var bytes=await ScheduleRequestTransport.Read(pipe,64,timeout.Token).ConfigureAwait(false);string message=Encoding.ASCII.GetString(bytes);bool accepted=false;
                            try{var request=new ScheduleRequest{RequestId=message.Substring(0,32),ScheduleId=message.Substring(32,32)};request.Validate();accepted=receive(request);}catch(ArgumentException){}catch(IOException){}
                            await pipe.WriteAsync(new[]{accepted?(byte)1:(byte)0},0,1,timeout.Token).ConfigureAwait(false);await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
                        }}finally{lock(gate)if(current==pipe)current=null;}
                    }
                }catch(IOException){}catch(ObjectDisposedException){}catch(OperationCanceledException){}catch(UnauthorizedAccessException){}
                lock(gate)if(stopped)return;
                await Task.Delay(25).ConfigureAwait(false);
            }
        }
        public void Dispose(){NamedPipeServerStream pipe;lock(gate){if(stopped)return;stopped=true;pipe=current;}if(pipe!=null)pipe.Dispose();listener.GetAwaiter().GetResult();}
    }
}
