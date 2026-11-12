using System;
using System.Collections.Generic;
using System.Threading;
namespace Backup.Core
{
    internal sealed class SourceReadCounter:IDisposable
    {
        private readonly IList<SourceItem> sources;private readonly Action<long>[] observers;private long bytes;
        internal long Bytes{get{return Interlocked.Read(ref bytes);}}
        internal SourceReadCounter(IList<SourceItem> value)
        {
            sources=value;observers=new Action<long>[sources.Count];
            for(int index=0;index<sources.Count;index++){var previous=sources[index].ReadObserver;observers[index]=previous;sources[index].ReadObserver=count=>{Interlocked.Add(ref bytes,count);if(previous!=null)previous(count);};}
        }
        public void Dispose(){for(int index=0;index<sources.Count;index++)sources[index].ReadObserver=observers[index];}
    }
}
