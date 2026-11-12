using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Backup.Core
{
    public interface IRangedBackupDestination
    {
        DownloadRange OpenReadRange(string name,long offset,long count,long totalSize,CancellationToken cancellation);
    }
    public sealed class DownloadRange:IDisposable
    {
        public readonly Stream Stream;public readonly bool Partial;public readonly string Version;
        public DownloadRange(Stream stream,bool partial,string version=null){Stream=stream;Partial=partial;Version=version;}
        public void Dispose(){Stream.Dispose();}
    }
    // Range streams never read beyond their assigned interval. In particular an
    // SFTP seek does not turn into a download of the rest of the source file.
    internal sealed class BoundedDownloadStream:Stream
    {
        private readonly Stream inner;private readonly long length;private long position;
        private readonly CancellationToken token;private readonly CancellationTokenRegistration cancellation;
        internal BoundedDownloadStream(Stream source,long count,CancellationToken cancel,Action abort=null)
        {inner=source;length=count;token=cancel;if(abort!=null)cancellation=cancel.Register(abort);}
        public override bool CanRead{get{return true;}}public override bool CanWrite{get{return false;}}public override bool CanSeek{get{return false;}}
        public override long Length{get{return length;}}public override long Position{get{return position;}set{throw new NotSupportedException();}}
        public override int Read(byte[] bytes,int offset,int count)
        {return ReadAsync(bytes,offset,count,token).GetAwaiter().GetResult();}
        public override async Task<int> ReadAsync(byte[] bytes,int offset,int count,CancellationToken cancellationToken)
        {token.ThrowIfCancellationRequested();cancellationToken.ThrowIfCancellationRequested();if(position==length||count==0)return 0;int read=await inner.ReadAsync(bytes,offset,(int)Math.Min(count,length-position),token).ConfigureAwait(false);if(read==0)throw new EndOfStreamException("下载分段提前结束。");position+=read;return read;}
        public override void Flush(){}public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}public override void SetLength(long value){throw new NotSupportedException();}public override void Write(byte[] bytes,int offset,int count){throw new NotSupportedException();}
        protected override void Dispose(bool disposing){if(disposing)try{inner.Dispose();}finally{cancellation.Dispose();}base.Dispose(disposing);}
    }
    public static class DownloadIO
    {
        private const int MinimumPiece=64*1024,CopyChunk=64*1024;
        internal static int AssistDelayMilliseconds=2000;
        public static void ReadInto(IManagedBackupDestination source,string name,long size,byte[] buffer,TransferScheduler scheduler=null,CancellationToken cancellation=default(CancellationToken),Action<long> progress=null,BackupControl control=null)
        {
            if(size<0||size>buffer.LongLength)throw new ArgumentOutOfRangeException("size");if(size==0){if(progress!=null)progress(0);return;}
            using(var operation=new ReadOperation(source,name,size,0,(int)size,buffer,scheduler??TransferScheduler.Shared,cancellation,progress,null,control))operation.Run();
        }
        // Raw downloads use a bounded ordered window, including very large files.
        // Known archive volumes instead fill their already reserved buffer above.
        public static void CopyTo(IManagedBackupDestination source,string name,long size,Action<byte[],int,int> write,TransferScheduler scheduler=null,MemoryBudget memory=null,CancellationToken cancellation=default(CancellationToken),long startOffset=0,Action<long> progress=null,BackupControl control=null)
        {
            if(size<0||startOffset<0||startOffset>size)throw new ArgumentOutOfRangeException("size");if(startOffset==size)return;
            long window=Math.Min(size-startOffset,memory==null?64L*1024*1024:Math.Max(1024*1024,Math.Min(64L*1024*1024,memory.LimitBytes/8)));
            using(memory==null?null:memory.WaitReserve(window,cancellation)){
                var buffer=new byte[(int)window];long position=startOffset;string version=null;
                while(position<size){cancellation.ThrowIfCancellationRequested();int count=(int)Math.Min(window,size-position);long current=position;
                    using(var operation=new ReadOperation(source,name,size,position,count,buffer,scheduler??TransferScheduler.Shared,cancellation,n=>{if(progress!=null)progress(current+n);},input=>CopySequential(input,size,current,write,cancellation,progress,control),control,version)){if(operation.Run())return;version=operation.Version;}
                    for(int offset=0;offset<count;){cancellation.ThrowIfCancellationRequested();int chunk=Math.Min(CopyChunk,count-offset);write(buffer,offset,chunk);offset+=chunk;}position+=count;
                }
            }
        }
        private static void CopySequential(Stream input,long size,long skip,Action<byte[],int,int> write,CancellationToken token,Action<long> progress,BackupControl control=null)
        {
            var bytes=new byte[CopyChunk];long total=0;
            while(true){token.ThrowIfCancellationRequested();if(control!=null)control.Checkpoint(false);int read=input.ReadAsync(bytes,0,bytes.Length,token).GetAwaiter().GetResult();if(read==0)break;if(total+read>size)throw new IOException("下载文件长度已变化。");int ignored=(int)Math.Min(read,Math.Max(0,skip-total));if(read>ignored)write(bytes,ignored,read-ignored);total+=read;if(progress!=null)progress(Math.Max(skip,total));}
            if(total!=size)throw new EndOfStreamException("文件下载不完整。");
        }
        private sealed class ReadOperation:IDisposable
        {
            private sealed class Part
            {internal long End,Position,StartedAt;internal bool Done,Started;internal int Active,Launched;internal readonly List<Attempt> Attempts=new List<Attempt>();}
            private sealed class Attempt
            {internal Part Part;internal long Start,End;internal bool First,Finished;internal CancellationTokenSource Cancellation;internal Task Task;}
            private readonly object gate=new object();private readonly IManagedBackupDestination source;private readonly string name;private readonly long size,start;private readonly byte[] buffer;
            private readonly TransferScheduler scheduler;private readonly CancellationToken token;private readonly CancellationTokenSource lifetime;private readonly Action<long> progress;private readonly Action<Stream> fallback;private readonly BackupControl control;
            private readonly List<Part> parts=new List<Part>();private readonly List<Attempt> attempts=new List<Attempt>();private long received;private int mode;private string version;private Exception failure;
            internal string Version{get{lock(gate)return version;}}
            internal ReadOperation(IManagedBackupDestination value,string file,long total,long offset,int count,byte[] bytes,TransferScheduler transfers,CancellationToken cancellation,Action<long> report,Action<Stream> sequential,BackupControl backup,string expectedVersion=null)
            {
                source=value;name=file;size=total;start=offset;buffer=bytes;scheduler=transfers;token=cancellation;progress=report;fallback=sequential;control=backup;version=expectedVersion;lifetime=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                int number=Math.Min(TransferScheduler.MaximumDownloads,Math.Max(1,count/(256*1024)));long piece=((count+(long)number-1)/number+MinimumPiece-1)/MinimumPiece*MinimumPiece;
                for(long position=offset;position<offset+count;position+=piece)parts.Add(new Part{Position=position,End=Math.Min(offset+count,position+piece)});
            }
            internal bool Run()
            {
                try{
                    lock(gate){if(progress!=null)progress(0);StartAttempt(parts[0],true,false);}
                    while(true){token.ThrowIfCancellationRequested();if(control!=null)control.Checkpoint(false);lock(gate){if(failure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();if(parts.All(part=>part.Done))break;
                            if(mode==1){foreach(var part in parts.Where(part=>!part.Started).ToList())StartAttempt(part,false,false);Assist();}
                            Monitor.Wait(gate,100);}}
                    Drain();return mode==2&&fallback!=null;
                }
                finally{lifetime.Cancel();Drain();}
            }
            private void StartAttempt(Part part,bool first,bool spare)
            {
                var attempt=new Attempt{Part=part,Start=part.Position,End=part.End,First=first,Cancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)};
                var task=scheduler.QueueDownload(this,source.Id,cancel=>Read(attempt,cancel),attempt.Cancellation.Token,error=>Ended(attempt,error),spare);
                if(task==null){attempt.Cancellation.Dispose();return;}attempt.Task=task;attempts.Add(attempt);part.Attempts.Add(attempt);part.Started=true;part.Active++;part.Launched++;
            }
            private void Assist()
            {
                var limited=source as IBandwidthDestination;if(limited!=null&&limited.Bandwidth!=null&&limited.Bandwidth.IsPaused)return;
                if(!scheduler.HasSpareDownloadCapacity)return;
                long now=Stopwatch.GetTimestamp();var part=parts.Where(value=>!value.Done&&value.Active>0&&value.StartedAt>0&&(now-value.StartedAt)*1000.0/Stopwatch.Frequency>=AssistDelayMilliseconds).OrderByDescending(value=>value.End-value.Position).FirstOrDefault();if(part==null)return;
                long remaining=part.End-part.Position;
                if(remaining>=MinimumPiece*2){long split=part.Position+((remaining/2+MinimumPiece-1)/MinimumPiece)*MinimumPiece;if(split>=part.End)return;
                    var next=new Part{Position=split,End=part.End};
                    // A split owns disjoint bytes. The old stream is closed as soon
                    // as it reaches the shortened interval, without waiting for EOF.
                    long previousEnd=part.End;part.End=split;parts.Add(next);StartAttempt(next,false,true);if(!next.Started){parts.Remove(next);part.End=previousEnd;}
                }else if(part.Active==1&&part.Launched<4)StartAttempt(part,false,true);
            }
            private void Read(Attempt attempt,CancellationToken cancellation)
            {
                lock(gate)if(attempt.Part.StartedAt==0)attempt.Part.StartedAt=Stopwatch.GetTimestamp();
                var ranged=source as IRangedBackupDestination;
                using(var input=ranged==null?new DownloadRange(source.OpenRead(name),false):ranged.OpenReadRange(name,attempt.Start,attempt.End-attempt.Start,size,cancellation)){
                    lock(gate)if(version!=null&&input.Version!=null&&version!=input.Version)throw new IOException("文件在分段下载期间已变化，请刷新后重试。");
                    if(!input.Partial){if(!attempt.First)throw new IOException("仓库未按请求返回下载分段，已保留原文件。");lock(gate){mode=2;Monitor.PulseAll(gate);}
                        if(fallback!=null)fallback(input.Stream);else{long written=0;CopySequential(input.Stream,size,0,(bytes,offset,count)=>{Buffer.BlockCopy(bytes,offset,buffer,(int)written,count);written+=count;if(progress!=null)progress(written);},cancellation,null,control);}
                        lock(gate){foreach(var part in parts)part.Done=true;Monitor.PulseAll(gate);}return;
                    }
                    lock(gate){if(attempt.First){version=input.Version??version;mode=1;Monitor.PulseAll(gate);}}
                    var bytesRead=new byte[CopyChunk];long position=attempt.Start;
                    while(true){cancellation.ThrowIfCancellationRequested();if(control!=null)control.Checkpoint(false);int count;lock(gate){if(attempt.Part.Done)break;count=(int)Math.Min(bytesRead.Length,attempt.Part.End-position);if(count<=0)break;}
                        int read=input.Stream.ReadAsync(bytesRead,0,count,cancellation).GetAwaiter().GetResult();if(read==0)throw new EndOfStreamException("下载分段提前结束。");List<CancellationTokenSource> cancel=null;
                        lock(gate){var part=attempt.Part;int ignored=(int)Math.Min(read,Math.Max(0,part.Position-position));int useful=(int)Math.Max(0,Math.Min(read,part.End-position)-ignored);
                            if(!part.Done&&useful>0&&position+ignored==part.Position){Buffer.BlockCopy(bytesRead,ignored,buffer,(int)(part.Position-start),useful);part.Position+=useful;received+=useful;if(progress!=null)progress(received);}
                            position+=read;if(part.Position>=part.End&&!part.Done){part.Done=true;cancel=part.Attempts.Where(other=>!other.Finished&&other!=attempt).Select(other=>other.Cancellation).ToList();}Monitor.PulseAll(gate);}
                        if(cancel!=null)foreach(var other in cancel)other.Cancel();
                    }
                    if(position<attempt.End)attempt.Cancellation.Cancel();
                }
            }
            private void Ended(Attempt attempt,Exception error)
            {
                lock(gate){attempt.Finished=true;var part=attempt.Part;part.Active--;
                    if(!part.Done&&!lifetime.IsCancellationRequested){if(error==null)error=new EndOfStreamException("下载分段未完成。");if(part.Active==0){if(part.Launched<4)try{StartAttempt(part,attempt.First&&mode==0,false);}catch(Exception next){failure=next;}else failure=error;}}
                    Monitor.PulseAll(gate);}
            }
            private void Drain(){Attempt[] jobs;lock(gate)jobs=attempts.ToArray();foreach(var job in jobs)if(job.Task!=null)try{job.Task.GetAwaiter().GetResult();}catch{}}
            public void Dispose(){lifetime.Cancel();Drain();foreach(var attempt in attempts)attempt.Cancellation.Dispose();lifetime.Dispose();}
        }
    }
}
