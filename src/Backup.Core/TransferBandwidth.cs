using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
namespace Backup.Core
{
    // One in-memory budget per application, shared by all protocol adapters and workers.
    public sealed class TransferBandwidth:IDisposable
    {
        private readonly object gate=new object();
        private readonly Func<DateTime> clock;
        private readonly Timer timer;
        private BandwidthSchedule schedule=new BandwidthSchedule();
        private BandwidthLimit limit=new BandwidthLimit();
        private DateTime evaluated=DateTime.MinValue,nextBoundary=DateTime.MaxValue;
        private CancellationTokenSource pause=new CancellationTokenSource();
        private readonly System.Collections.Generic.List<CancellationTokenSource> retired=new System.Collections.Generic.List<CancellationTokenSource>();
        private bool disposed;private long pauseVersion,uploadedBytes,downloadedBytes;
        private sealed class Budget{internal double Tokens;internal long Ticks=Stopwatch.GetTimestamp();}
        private readonly Budget upload=new Budget(),download=new Budget();
        public event Action Changed;
        public TransferBandwidth():this(()=>DateTime.Now){}
        public TransferBandwidth(Func<DateTime> localClock){clock=localClock??(()=>DateTime.Now);timer=new Timer(_=>Refresh(),null,Timeout.Infinite,Timeout.Infinite);}
        public long PauseVersion{get{Refresh();lock(gate)return pauseVersion;}}
        public long UploadedBytes{get{return Interlocked.Read(ref uploadedBytes);}}
        public long DownloadedBytes{get{return Interlocked.Read(ref downloadedBytes);}}
        internal void RecordBytes(int count,bool sending){if(count>0){if(sending)Interlocked.Add(ref uploadedBytes,count);else Interlocked.Add(ref downloadedBytes,count);}}
        public bool IsPaused{get{Refresh();lock(gate)return limit.Paused;}}
        public BandwidthLimit Current{get{Refresh();lock(gate)return new BandwidthLimit{UploadMbps=limit.UploadMbps,DownloadMbps=limit.DownloadMbps};}}
        internal bool Unrestricted(bool sending){Refresh();lock(gate){Check(CancellationToken.None);return !limit.Paused&&!(sending?limit.UploadMbps:limit.DownloadMbps).HasValue;}}
        public CancellationToken PauseToken{get{Refresh();lock(gate)return pause.Token;}}
        public void Apply(BandwidthSchedule value)
        {var copy=(value??new BandwidthSchedule()).ValidatedCopy();lock(gate){if(disposed)throw new ObjectDisposedException("TransferBandwidth");schedule=copy;evaluated=nextBoundary=DateTime.MinValue;}Refresh();}
        public void Refresh()
        {
            CancellationTokenSource interrupt=null;bool changed=false;
            lock(gate)
            {
                if(disposed)return;DateTime now=clock();if(now>=evaluated&&now<nextBoundary)return;
                var next=schedule.At(now);changed=!next.Same(limit);
                if(next.Paused&&!limit.Paused){pauseVersion++;interrupt=pause;}
                if(!next.Paused&&limit.Paused){retired.Add(pause);pause=new CancellationTokenSource();}
                if(changed){upload.Tokens=download.Tokens=0;upload.Ticks=download.Ticks=Stopwatch.GetTimestamp();}
                limit=next;evaluated=now;nextBoundary=schedule.NextBoundary(now);Monitor.PulseAll(gate);
                timer.Change(schedule.Periods.Count==0?Timeout.Infinite:(int)Math.Max(1,Math.Min(int.MaxValue,(nextBoundary-now).TotalMilliseconds)),Timeout.Infinite);
            }
            if(interrupt!=null)try{interrupt.Cancel();}catch(ObjectDisposedException){}
            if(changed){var observers=Changed;if(observers!=null)foreach(Action observer in observers.GetInvocationList())try{observer();}catch{}}
        }
        private void Check(CancellationToken token){token.ThrowIfCancellationRequested();if(disposed)throw new OperationCanceledException("传输调度已关闭。",token);}
        public void WaitForResume(CancellationToken token)
        {
            while(true){Refresh();lock(gate){Check(token);if(!limit.Paused)return;Monitor.Wait(gate,250);}}
        }
        private int TryAcquire(int count,bool sending,CancellationToken token,out int delay)
        {
            Refresh();lock(gate)
            {
                Check(token);delay=100;if(limit.Paused)return 0;decimal? mbps=sending?limit.UploadMbps:limit.DownloadMbps;
                if(!mbps.HasValue)return count;
                double rate=(double)mbps.Value*1000000/8;var budget=sending?upload:download;
                // Keep elapsed credit across timer overshoot; the individual IO
                // chunk stays small without capping the shared budget at one chunk.
                long now=Stopwatch.GetTimestamp();double chunkLimit=Math.Max(1,Math.Min(64*1024,rate/10)),capacity=Math.Max(chunkLimit*2,rate/10);
                budget.Tokens=Math.Min(capacity,budget.Tokens+(now-budget.Ticks)*rate/Stopwatch.Frequency);budget.Ticks=now;
                int chunk=(int)Math.Max(1,Math.Min(count,Math.Floor(chunkLimit)));
                if(budget.Tokens>=chunk){budget.Tokens-=chunk;return chunk;}
                delay=(int)Math.Max(1,Math.Min(100,Math.Ceiling((chunk-budget.Tokens)/rate*1000)));return 0;
            }
        }
        public int Acquire(int count,bool sending,CancellationToken token)
        {if(count<=0)return 0;while(true){int delay,n=TryAcquire(count,sending,token,out delay);if(n>0)return n;if(token.WaitHandle.WaitOne(delay))token.ThrowIfCancellationRequested();}}
        public async Task<int> AcquireAsync(int count,bool sending,CancellationToken token)
        {if(count<=0)return 0;while(true){int delay,n=TryAcquire(count,sending,token,out delay);if(n>0)return n;await Task.Delay(delay,token).ConfigureAwait(false);}}
        public void Dispose()
        {lock(gate){if(disposed)return;disposed=true;timer.Dispose();Monitor.PulseAll(gate);}pause.Cancel();pause.Dispose();foreach(var item in retired)item.Dispose();}
    }
    public interface IBandwidthDestination {TransferBandwidth Bandwidth{get;set;}}
}
