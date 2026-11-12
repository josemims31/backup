using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace Backup.Core
{
    // Reserve before I/O; Task results never retain buffers outside this cache.
    public sealed class MemoryReadCache:IDisposable
    {
        private sealed class Entry {public long Size;public byte[] Bytes;public Exception Error;public bool Done;public int Readers;public IDisposable Memory;}
        private readonly object gate=new object();private readonly long limit;
        private readonly Dictionary<string,Entry> entries=new Dictionary<string,Entry>();private readonly LinkedList<string> recent=new LinkedList<string>();
        private long bytes,prefetchRevision;private int active;private bool disposed;
        private readonly MemoryBudget shared;private readonly CancellationTokenSource ending=new CancellationTokenSource();private readonly CancellationToken lifetime;
        internal long PrefetchRevision{get{return Interlocked.Read(ref prefetchRevision);}}
#if UI_TEST
        public int TestPrefetchCalls;
#endif
        public long PeakBytes{get;private set;}
        public MemoryReadCache(long budget,MemoryBudget sharedBudget=null){limit=budget;shared=sharedBudget;lifetime=ending.Token;if(shared!=null)shared.Pressure+=Trim;}
        private void Remove(string key,Entry value){Entry current;if(!entries.TryGetValue(key,out current)||current!=value)return;bytes-=value.Size;entries.Remove(key);recent.Remove(key);if(value.Memory!=null){value.Memory.Dispose();value.Memory=null;}Interlocked.Increment(ref prefetchRevision);}
        private void Trim(){lock(gate){foreach(string key in recent.ToList()){var entry=entries[key];if(entry.Done&&entry.Readers==0)Remove(key,entry);}Monitor.PulseAll(gate);}}
        private static string Key(ObjectRecord item){return item.Name+"|"+item.Sha256;}
        private void Check(){if(disposed)throw new ObjectDisposedException("MemoryReadCache");}
        private bool MakeRoom(long size,string protect,ISet<string> upcoming=null)
        {
            if(size>limit)throw new IOException("分卷超过内存预算。");
            for(var node=recent.First;bytes+size>limit&&node!=null;){var next=node.Next;var value=entries[node.Value];if(node.Value!=protect&&(upcoming==null||!upcoming.Contains(node.Value))&&value.Done&&value.Readers==0)Remove(node.Value,value);node=next;}
            return bytes+size<=limit&&active<8;
        }
        private void Start(ObjectRecord item,Func<byte[]> load,CancellationToken cancellation)
        {
            string key=Key(item);var entry=new Entry{Size=item.Size};entries.Add(key,entry);recent.AddLast(key);bytes+=item.Size;PeakBytes=Math.Max(PeakBytes,bytes);active++;Interlocked.Increment(ref prefetchRevision);
            Task.Factory.StartNew(()=>{byte[] value=null;Exception error=null;IDisposable memory=null;try{using(var cancel=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime)){if(shared!=null)memory=shared.WaitReserve(item.Size,cancel.Token);cancel.Token.ThrowIfCancellationRequested();value=load();if(value.LongLength!=item.Size)throw new IOException("分卷长度已变化。");}}catch(Exception failure){error=failure;value=null;}
                lock(gate){active--;if(!disposed){entry.Bytes=value;entry.Error=error;entry.Memory=memory;entry.Done=true;memory=null;}if(memory!=null)memory.Dispose();Interlocked.Increment(ref prefetchRevision);Monitor.PulseAll(gate);}},CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        }
        public int Copy(ObjectRecord item,int source,byte[] target,int offset,int count,Func<byte[]> load,CancellationToken cancellation=default(CancellationToken))
        {
            string key=Key(item);lock(gate)while(true){Check();cancellation.ThrowIfCancellationRequested();Entry entry;if(!entries.TryGetValue(key,out entry)){if(!MakeRoom(item.Size,null)){Monitor.Wait(gate,100);continue;}Start(item,load,cancellation);entry=entries[key];}
                entry.Readers++;try{while(!entry.Done){Check();cancellation.ThrowIfCancellationRequested();Monitor.Wait(gate,100);}if(entry.Error!=null){var error=entry.Error;Remove(key,entry);Monitor.PulseAll(gate);if(error is OperationCanceledException){if(!cancellation.CanBeCanceled)throw error;cancellation.ThrowIfCancellationRequested();continue;}throw new IOException("下载分卷失败："+item.Name,error);}
                    recent.Remove(key);recent.AddLast(key);Buffer.BlockCopy(entry.Bytes,source,target,offset,count);return count;
                }finally{entry.Readers--;}}
        }
        public void Prefetch(IEnumerable<ObjectRecord> next,Func<ObjectRecord,byte[]> load,ObjectRecord current,CancellationToken cancellation=default(CancellationToken))
        {PrefetchSnapshot(next,load,current,cancellation);}
        internal long PrefetchSnapshot(IEnumerable<ObjectRecord> next,Func<ObjectRecord,byte[]> load,ObjectRecord current,CancellationToken cancellation)
        {lock(gate){Check();
#if UI_TEST
            TestPrefetchCalls++;
#endif
            // Preserve nearer entries while extending this window; prefetch must not
            // repeatedly evict the next volume just to load a more distant one.
            if(cancellation.IsCancellationRequested)return prefetchRevision;string currentKey=Key(current);var upcoming=new HashSet<string>(StringComparer.Ordinal);foreach(var item in next){string key=Key(item);if(entries.ContainsKey(key)){upcoming.Add(key);continue;}if(!MakeRoom(item.Size,currentKey,upcoming))break;var requested=item;Start(item,()=>load(requested),cancellation);upcoming.Add(key);}return prefetchRevision;
        }}
        public void WaitForPending(){lock(gate)while(active>0)Monitor.Wait(gate);}
        public void Dispose(){if(shared!=null)shared.Pressure-=Trim;lock(gate){if(disposed)return;disposed=true;foreach(var entry in entries.Values)if(entry.Memory!=null)entry.Memory.Dispose();entries.Clear();recent.Clear();bytes=0;Interlocked.Increment(ref prefetchRevision);Monitor.PulseAll(gate);}ending.Cancel();ending.Dispose();}
    }
}
