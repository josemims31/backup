using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
namespace Backup.Core
{
    // Seekable 7-Zip input over independently verified remote volumes. No joined archive on disk.
    public sealed class VerifiedVolumeStream:Stream
    {
        private readonly IList<ObjectRecord> objects;
        private readonly long[] offsets;
        private readonly Func<ObjectRecord,byte[]> load;
        private Func<ObjectRecord,int,byte[],int,int,int> sharedCopy;
        private Func<int,int,byte[],int,int,int> indexedCopy;
        private readonly long budget;
        private readonly Dictionary<int,byte[]> cache=new Dictionary<int,byte[]>();
        private readonly LinkedList<int> recent=new LinkedList<int>();
        private long position,bytes;
        public long PeakBytes{get;private set;}
        public VerifiedVolumeStream(IList<ObjectRecord> volumes,Func<ObjectRecord,byte[]> loader,long memoryBudget)
        {
            objects=volumes;load=loader;budget=memoryBudget;offsets=new long[objects.Count+1];
            for(int i=0;i<objects.Count;i++){if(objects[i].Size<=0||objects[i].Size>budget)throw new IOException("恢复分卷超过内存预算。");offsets[i+1]=checked(offsets[i]+objects[i].Size);}
        }
        public VerifiedVolumeStream(IList<ObjectRecord> volumes,Func<ObjectRecord,int,byte[],int,int,int> copy,long memoryBudget):this(volumes,(Func<ObjectRecord,byte[]>)null,memoryBudget){sharedCopy=copy;}
        public static VerifiedVolumeStream FromCache(IList<ObjectRecord> volumes,MemoryReadCache cache,Func<ObjectRecord,byte[]> loader,long memoryBudget,CancellationToken cancellation=default(CancellationToken))
        {
            var reader=new CachedReader{Objects=volumes,Cache=cache,Load=loader,Cancellation=cancellation};
            var stream=new VerifiedVolumeStream(volumes,(Func<ObjectRecord,byte[]>)null,memoryBudget);stream.indexedCopy=reader.Copy;return stream;
        }
        private sealed class CachedReader
        {
            internal IList<ObjectRecord> Objects;internal MemoryReadCache Cache;internal Func<ObjectRecord,byte[]> Load;internal CancellationToken Cancellation;
            private int loadIndex=-1,prefetchIndex=-1;private long revision=-1;private Func<byte[]> currentLoad;
            private IEnumerable<ObjectRecord> Next(int index){for(int next=index+1;next<Objects.Count&&next-index<=7;next++)yield return Objects[next];}
            internal int Copy(int index,int part,byte[] target,int offset,int count)
            {
                var item=Objects[index];if(loadIndex!=index){loadIndex=index;var requested=item;currentLoad=()=>Load(requested);}
                int copied=Cache.Copy(item,part,target,offset,count,currentLoad,Cancellation);
                if(prefetchIndex!=index||revision!=Cache.PrefetchRevision){
                    // Capture the revision under the cache lock. A completion/eviction
                    // after it returns must remain visible to the next read.
                    revision=Cache.PrefetchSnapshot(Next(index),Load,item,Cancellation);prefetchIndex=index;
                }
                return copied;
            }
        }
        public override int Read(byte[] buffer,int offset,int count)
        {
            if(buffer==null||offset<0||count<0||offset>buffer.Length-count)throw new ArgumentException("Invalid read buffer.");int done=0;
            while(count>0&&position<Length){int index=Array.BinarySearch(offsets,position);if(index<0)index=~index-1;byte[] volume;
                if(indexedCopy!=null){int indexedPart=(int)(position-offsets[index]);int copied=indexedCopy(index,indexedPart,buffer,offset,(int)Math.Min(count,objects[index].Size-indexedPart));if(copied<=0)throw new IOException("内存分卷读取无进展。");position+=copied;offset+=copied;done+=copied;count-=copied;continue;}
                if(sharedCopy!=null){int sharedPart=(int)(position-offsets[index]);int copied=sharedCopy(objects[index],sharedPart,buffer,offset,(int)Math.Min(count,objects[index].Size-sharedPart));if(copied<=0)throw new IOException("内存分卷读取无进展。");position+=copied;offset+=copied;done+=copied;count-=copied;continue;}
                if(!cache.TryGetValue(index,out volume)){while(bytes+objects[index].Size>budget&&recent.Count>0){int old=recent.First.Value;recent.RemoveFirst();bytes-=cache[old].Length;cache.Remove(old);}volume=load(objects[index]);if(volume.LongLength!=objects[index].Size)throw new IOException("恢复分卷长度无效。");cache[index]=volume;bytes+=volume.Length;PeakBytes=Math.Max(PeakBytes,bytes);}
                recent.Remove(index);recent.AddLast(index);int part=(int)(position-offsets[index]);int n=Math.Min(count,volume.Length-part);Buffer.BlockCopy(volume,part,buffer,offset,n);position+=n;offset+=n;done+=n;count-=n;
            }return done;
        }
        public override long Seek(long offset,SeekOrigin origin){long next=checked((origin==SeekOrigin.Begin?0:origin==SeekOrigin.Current?position:origin==SeekOrigin.End?Length:throwOrigin())+offset);if(next<0)throw new IOException("Invalid archive seek.");return position=next;}
        private long throwOrigin(){throw new ArgumentException("Invalid seek origin.");}
        public override long Position{get{return position;}set{if(value<0)throw new ArgumentException("Invalid position.");position=value;}}
        public override long Length{get{return offsets[offsets.Length-1];}}
        public override bool CanRead{get{return true;}}public override bool CanSeek{get{return true;}}public override bool CanWrite{get{return false;}}
        public override void Flush(){}public override void Write(byte[] b,int o,int n){throw new NotSupportedException();}public override void SetLength(long size){throw new NotSupportedException();}
        protected override void Dispose(bool disposing){if(disposing){cache.Clear();recent.Clear();bytes=0;indexedCopy=null;}base.Dispose(disposing);}
    }
}
