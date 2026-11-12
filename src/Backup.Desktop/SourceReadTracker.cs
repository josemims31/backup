using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
namespace Backup.Desktop
{
    internal class SourceReadObservation {public string Id,Location,Error;}
    // Freshness is shared by all backup triggers. It is runtime state, never catalog data.
    internal sealed class SourceReadTracker
    {
        private sealed class State {internal string Location,Error;internal long Generation,UseEpoch;}
        private readonly object gate=new object();private readonly Dictionary<string,State> states=new Dictionary<string,State>();
        private long generation,useEpoch;
        internal long Generation{get{return Interlocked.Read(ref generation);}}
        internal void MarkUsed(){Interlocked.Increment(ref useEpoch);}
        internal void Forget(string id){lock(gate)states.Remove(id);}
        internal void Record(IEnumerable<SourceReadObservation> results)
        {lock(gate){long next=Interlocked.Increment(ref generation),epoch=Interlocked.Read(ref useEpoch);foreach(var result in results)states[result.Id]=new State{Location=result.Location,Error=result.Error,Generation=next,UseEpoch=epoch};}}
        internal IList<string> Required(IEnumerable<SourceDefinition> sources,long acceptedGeneration)
        {lock(gate){long epoch=Interlocked.Read(ref useEpoch);return sources.Where(source=>{State value;return !states.TryGetValue(source.Id,out value)||value.Generation<=acceptedGeneration||value.UseEpoch!=epoch||!string.Equals(value.Location,source.Location,StringComparison.OrdinalIgnoreCase);}).Select(source=>source.Id).Distinct().ToList();}}
        internal void Validate(IEnumerable<SourceDefinition> definitions,IEnumerable<string> ids,Func<string,bool> loaded)
        {
            var sources=definitions.Where(s=>!s.Deleted).ToDictionary(s=>s.Id);var errors=new List<string>();lock(gate)foreach(string id in ids.Distinct())
            {
                SourceDefinition source;State state;bool found=sources.TryGetValue(id,out source),read=states.TryGetValue(id,out state);
                if(!found||!loaded(id)||!read||!string.Equals(state.Location,source.Location,StringComparison.OrdinalIgnoreCase)||state.Error!=null)errors.Add((found?source.Name:id)+"：文件列表尚未成功刷新"+(read&&state.Error!=null?"（"+state.Error+"）":""));
            }
            if(errors.Count>0)throw new IOException(string.Join("；",errors)+"。请先刷新文件列表后重试。");
        }
    }
}
