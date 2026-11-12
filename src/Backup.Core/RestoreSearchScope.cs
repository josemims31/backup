using System;
using System.Collections.Generic;
using System.Threading;

namespace Backup.Core
{
    internal sealed class RestoreSearchIndex
    {
        internal readonly Dictionary<string,List<string>> Locations=new Dictionary<string,List<string>>(StringComparer.Ordinal);
        internal readonly List<string> Errors=new List<string>();
    }
    // Metadata only, owned by one restore or maintenance operation. An index remembers absent
    // names; separate transfer adapters never need to repeat that warehouse walk.
    public sealed class RestoreSearchScope:IDisposable
    {
        private sealed class Entry
        {
            internal readonly object Gate=new object();internal bool Loading;internal RestoreSearchIndex Value;
        }
        private readonly object gate=new object();private readonly Dictionary<string,Entry> entries=new Dictionary<string,Entry>(StringComparer.Ordinal);
        private bool disposed;
        internal RestoreSearchIndex Read(DestinationDefinition destination,Func<RestoreSearchIndex> load,CancellationToken cancellation)
        {
            string key=destination.Id+"\n"+destination.Protocol+"\n"+destination.Location;Entry entry;
            lock(gate){if(disposed)throw new ObjectDisposedException("RestoreSearchScope");if(!entries.TryGetValue(key,out entry)){entry=new Entry();entries.Add(key,entry);}}
            while(true)
            {
                cancellation.ThrowIfCancellationRequested();
                lock(entry.Gate){if(entry.Value!=null)return entry.Value;if(entry.Loading){Monitor.Wait(entry.Gate,50);continue;}entry.Loading=true;}
                try
                {
                    var result=load();cancellation.ThrowIfCancellationRequested();
                    lock(entry.Gate){entry.Value=result;entry.Loading=false;Monitor.PulseAll(entry.Gate);}return result;
                }
                catch
                {
                    // A cancelled or failed walk is not proof that a file is absent.
                    lock(entry.Gate){entry.Loading=false;Monitor.PulseAll(entry.Gate);}throw;
                }
            }
        }
        public void Dispose(){lock(gate){disposed=true;entries.Clear();}}
    }
}
