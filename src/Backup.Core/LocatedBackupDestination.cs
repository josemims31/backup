using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
namespace Backup.Core
{
    public interface ILocatedObjectReader
    {
        IEnumerable<string> ReadDirectories(string name,long size);
        Stream OpenReadAt(string name,string directory);
        void RememberReadDirectory(string name,string directory);
    }
    public sealed class ObjectStorageLayout
    {
        private sealed class DestinationState {internal readonly object Gate=new object();internal int Writers;}
        private readonly object gate=new object();private readonly Dictionary<string,DestinationState> states=new Dictionary<string,DestinationState>();
        private readonly BackupCatalog catalog;
        public ObjectStorageLayout(BackupCatalog value){catalog=value;}
        public static bool IsMissingPath(Exception error){return LocatedBackupDestination.Missing(error);}
        private DestinationState State(DestinationDefinition destination){string key=destination.Id+"|"+destination.Protocol+"|"+destination.Location;lock(gate){DestinationState state;if(!states.TryGetValue(key,out state)){state=new DestinationState();states.Add(key,state);}return state;}}
        public IManagedBackupDestination Wrap(DestinationDefinition destination,Func<string,IManagedBackupDestination> driver,bool findMoved=false,bool remember=true,IDictionary<string,IList<StorageEntry>> checkedInventory=null)
        {return Wrap(destination,driver,findMoved,remember,checkedInventory,null);}
        public IManagedBackupDestination Wrap(DestinationDefinition destination,Func<string,IManagedBackupDestination> driver,bool findMoved,bool remember,IDictionary<string,IList<StorageEntry>> checkedInventory,RestoreSearchScope restoreSearch)
        {return new LocatedBackupDestination(this,catalog,destination,driver,findMoved,remember,checkedInventory,restoreSearch);}
        internal IDisposable BeginWrite(DestinationDefinition destination,string name,Func<string,IManagedBackupDestination> driver,CancellationToken cancellation,out string directory)
        {
            var state=State(destination);lock(state.Gate)
            {
                cancellation.ThrowIfCancellationRequested();string owner=BackupObjectNaming.ObjectName(name)??name;
                directory=catalog.ObjectStorageDirectory(destination.Id,owner);
                bool pending=catalog.UploadStages(destination.Id,owner).Count>0;
                if(owner.StartsWith("catalog-",StringComparison.Ordinal))
                {if(directory==null||directory.Length>0&&!pending){directory="";catalog.RememberObjectDirectory(destination.Id,owner,directory);}}
                else
                {
                    bool node=owner.StartsWith("node-",StringComparison.Ordinal);var family=Regex.Match(owner,@"\A(?:data|node)-(?<tag>[A-Za-z0-9]{4})-[0-9]",RegexOptions.CultureInvariant);
                    string task=catalog.ObjectTaskOwner(owner),tag=task!=null&&task!="__catalog__"?catalog.EnsureTaskTag(task):family.Success?family.Groups["tag"].Value:"legacy",root=(node?"node-":"data-")+tag;
                    // Read hints can point anywhere; replacement writes use task
                    // buckets. An owned in-flight upload keeps its original path.
                    bool reassign=directory!=null&&!Regex.IsMatch(directory,@"\A"+Regex.Escape(root)+@"/[0-9]{6}\z")&&!pending;
                    if(directory==null||reassign)directory=catalog.ReserveBucketDirectory(destination.Id,owner,node?"node-buckets":"data-buckets",tag,root,reassign);
                }
                state.Writers++;return new WriteLease(state);
            }
        }
        private sealed class WriteLease:IDisposable
        {private DestinationState state;internal WriteLease(DestinationState value){state=value;}public void Dispose(){var value=Interlocked.Exchange(ref state,null);if(value!=null)lock(value.Gate){value.Writers--;Monitor.PulseAll(value.Gate);}}}
        // Physical adapters are associated with their directory by the wrapper;
        // no file-content requests are used to expand the directory tree.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IManagedBackupDestination,FolderName> folders=new System.Runtime.CompilerServices.ConditionalWeakTable<IManagedBackupDestination,FolderName>();
        private sealed class FolderName {internal string Value;}
        internal static void Associate(IManagedBackupDestination driver,string folder){lock(folders){folders.Remove(driver);folders.Add(driver,new FolderName{Value=folder});}}
        private static string Folder(IManagedBackupDestination driver){FolderName value;if(!folders.TryGetValue(driver,out value))throw new IOException("目录驱动缺少位置。");return value.Value;}
    }
    internal sealed class LocatedBackupDestination:IBackgroundUploadProgress,IManagedBackupDestination,IKnownLengthUploadDestination,ICancellableBackupDestination,IBandwidthDestination,IOwnedUploadDestination,IInterruptedUploadDestination,IRecoverableUploadDestination,IReadOnlyDestinationProbe,ILocatedObjectReader,IDisposable,ITemporaryUploadProbe,IAbandonedUploadDestination
    {
        private readonly ObjectStorageLayout layout;private readonly BackupCatalog catalog;private readonly DestinationDefinition destination;private readonly Func<string,IManagedBackupDestination> factory;private readonly bool search,remember;
        internal bool FindsMovedObjects{get{return search;}}
        private readonly IDictionary<string,IList<StorageEntry>> checkedInventory;
        private readonly Dictionary<string,IManagedBackupDestination> drivers=new Dictionary<string,IManagedBackupDestination>(StringComparer.Ordinal);private readonly Dictionary<string,IDisposable> writes=new Dictionary<string,IDisposable>(StringComparer.Ordinal);private readonly Dictionary<string,string> writeFolders=new Dictionary<string,string>(StringComparer.Ordinal);private readonly object gate=new object();
        private readonly Dictionary<string,string> foundFolders=new Dictionary<string,string>(StringComparer.Ordinal);private RestoreSearchIndex searchIndex;
        private readonly RestoreSearchScope restoreSearch;
        public string Id{get{return destination.Id;}}
        public CancellationToken OperationCancellation{get;set;}
        public TransferBandwidth Bandwidth{get;set;}
        public IUploadJournal UploadJournal{get;set;}
        public Action<TransferProgress> BackgroundProgress{get;set;}
        public bool BackgroundUploadActive{get{lock(gate)return drivers.Values.OfType<IBackgroundUploadProgress>().Any(d=>d.BackgroundUploadActive);}}
        internal LocatedBackupDestination(ObjectStorageLayout service,BackupCatalog value,DestinationDefinition target,Func<string,IManagedBackupDestination> driver,bool findMoved,bool rememberFound,IDictionary<string,IList<StorageEntry>> checkedEntries,RestoreSearchScope sharedSearch){layout=service;catalog=value;destination=target;factory=driver;search=findMoved;remember=rememberFound;checkedInventory=checkedEntries;restoreSearch=sharedSearch;}
        private IManagedBackupDestination Driver(string directory)
        {
            lock(gate){IManagedBackupDestination driver;if(!drivers.TryGetValue(directory,out driver)){driver=factory(ObjectStoragePath.Absolute(destination,directory));ObjectStorageLayout.Associate(driver,ObjectStoragePath.Absolute(destination,directory));drivers.Add(directory,driver);}var cancel=driver as ICancellableBackupDestination;if(cancel!=null)cancel.OperationCancellation=OperationCancellation;var limited=driver as IBandwidthDestination;if(limited!=null)limited.Bandwidth=Bandwidth;var owned=driver as IOwnedUploadDestination;if(owned!=null)owned.UploadJournal=UploadJournal;var background=driver as IBackgroundUploadProgress;if(background!=null)background.BackgroundProgress=BackgroundProgress;return driver;}
        }
        private static string Owner(string name){return BackupObjectNaming.ObjectName(name)??name;}
        private string DirectoryFor(string name){lock(gate){string folder;return writeFolders.TryGetValue(Owner(name),out folder)||foundFolders.TryGetValue(Owner(name),out folder)?folder:catalog.ObjectStorageDirectory(Id,Owner(name))??"";}}
        private void Finish(string name){lock(gate){IDisposable lease;if(writes.TryGetValue(name,out lease)){writes.Remove(name);writeFolders.Remove(name);foundFolders.Remove(Owner(name));searchIndex=null;lease.Dispose();}}}
        public void CheckReadableRoot(){((IReadOnlyDestinationProbe)Driver("")).CheckReadableRoot();}
        public void PrepareWrite(){Driver("").PrepareWrite();}
        private IManagedBackupDestination Writer(string name)
        {
            lock(gate)if(writes.ContainsKey(name))return Driver(writeFolders[name]);
            string folder;var lease=layout.BeginWrite(destination,name,Driver,OperationCancellation,out folder);
            lock(gate){writes.Add(name,lease);writeFolders.Add(name,folder);}try{var driver=Driver(folder);driver.PrepareWrite();return driver;}catch{Finish(name);throw;}
        }
        public Stream OpenTemporary(string name){try{return Writer(name).OpenTemporary(name);}catch{Finish(name);throw;}}
        public Stream OpenTemporary(string name,long length){try{var driver=Writer(name);var sized=driver as IKnownLengthUploadDestination;return sized==null?driver.OpenTemporary(name):sized.OpenTemporary(name,length);}catch{Finish(name);throw;}}
        public void CommitTemporary(string name){Driver(DirectoryFor(name)).CommitTemporary(name);}
        public RemoteObjectInfo StatTemporary(string name){var probe=Driver(DirectoryFor(name)) as ITemporaryUploadProbe;return probe==null?new RemoteObjectInfo{Name=name}:probe.StatTemporary(name);}
        public void DeleteTemporary(string name){try{Driver(DirectoryFor(name)).DeleteTemporary(name);}finally{Finish(name);}}
        public RemoteObjectInfo StatFormal(string name){var target=Driver(DirectoryFor(name));var abandoned=target as IAbandonedUploadDestination;return abandoned==null?target.Stat(name):abandoned.StatFormal(name);}
        public void DiscardTemporary(string name){try{var target=Driver(DirectoryFor(name));var abandoned=target as IAbandonedUploadDestination;if(abandoned!=null)abandoned.DiscardTemporary(name);else target.DeleteTemporary(name);}finally{Finish(name);}}
        public void RejectTemporary(string name){try{var owned=Driver(DirectoryFor(name)) as IOwnedUploadDestination;if(owned!=null)owned.RejectTemporary(name);}finally{Finish(name);}}
        public void SuspendTemporary(string name){try{var interrupted=Driver(DirectoryFor(name)) as IInterruptedUploadDestination;if(interrupted!=null)interrupted.SuspendTemporary(name);}finally{Finish(name);}}
        public bool HasInterruptedTemporary(string name){var interrupted=Driver(DirectoryFor(name)) as IInterruptedUploadDestination;return interrupted!=null&&interrupted.HasInterruptedTemporary(name);}
        public void ResumeTemporary(string name,string temporary){var recover=Writer(name) as IRecoverableUploadDestination;if(recover==null)throw new IOException("驱动不支持恢复上传。");recover.ResumeTemporary(name,temporary);}
        internal static bool Missing(Exception error){for(var current=error;current!=null;current=current.InnerException)if(current is FileNotFoundException||current is DirectoryNotFoundException||current.GetType().Name=="SftpPathNotFoundException"||current is RemoteTransferException&&((RemoteTransferException)current).StatusCode==404)return true;return false;}
        private RemoteObjectInfo StatAt(string name,string folder){try{var result=Driver(folder).Stat(name);result.Name=name;return result;}catch(IOException error){if(!Missing(error))throw;return new RemoteObjectInfo{Name=name};}}
        public RemoteObjectInfo Stat(string name){var expected=search?catalog.FindStoredObject(name):null;if(expected!=null){foreach(string folder in ReadDirectories(name,expected.Size)){lock(gate)foundFolders[name]=folder;return StatAt(name,folder);}return new RemoteObjectInfo{Name=name};}return StatAt(name,DirectoryFor(name));}
        public Stream OpenRead(string name){if(search&&!Stat(name).Exists)throw new FileNotFoundException("没有找到备份分卷："+name);return Driver(DirectoryFor(name)).OpenRead(name);}
        // Size filtering is for reads. A known damaged object must still be
        // removable at its recorded location before a verified replacement.
        public void DeleteObject(string name){try{Driver(DirectoryFor(name)).DeleteObject(name);}catch(IOException error){if(!Missing(error))throw;}}
        public IEnumerable<string> ReadDirectories(string name,long size)
        {
            string first=DirectoryFor(name);RemoteObjectInfo known=null;try{known=StatAt(name,first);}catch(IOException){if(!search)throw;}
            var seen=new HashSet<string>(StringComparer.Ordinal);if(known!=null&&known.Exists&&known.Size==size){seen.Add(first);yield return first;}
            if(!search)yield break;BuildSearchIndex();List<string> candidates;if(searchIndex.Locations.TryGetValue(SearchKey(name,size),out candidates)){foreach(string directory in candidates)if(seen.Add(directory))yield return directory;}
            else if(searchIndex.Errors.Count>0)throw new IOException("没有在可读取目录中找到匹配分卷；部分目录无法读取："+string.Join("；",searchIndex.Errors));
        }
        private string SearchKey(string name,long size){return (RepositoryModules.IsFileSystem(destination.Protocol)?name.ToUpperInvariant():name)+"|"+size;}
        private void BuildSearchIndex()
        {
            if(searchIndex!=null)return;searchIndex=restoreSearch==null?LoadSearchIndex():restoreSearch.Read(destination,LoadSearchIndex,OperationCancellation);
        }
        private RestoreSearchIndex LoadSearchIndex()
        {
            var result=new RestoreSearchIndex();var index=result.Locations;var pending=new Stack<string>();pending.Push("");var visited=new HashSet<string>(StringComparer.Ordinal);
            if(checkedInventory!=null){foreach(var folder in checkedInventory)foreach(var entry in folder.Value.Where(entry=>!entry.Directory&&entry.Size>=0)){string key=SearchKey(entry.Name,entry.Size);List<string> choices;if(!index.TryGetValue(key,out choices)){choices=new List<string>();index.Add(key,choices);}choices.Add(folder.Key);}return result;}
            while(pending.Count>0)
            {
                OperationCancellation.ThrowIfCancellationRequested();string directory=pending.Pop();if(!visited.Add(directory))continue;IList<StorageEntry> entries;
                try{var listing=Driver(directory) as IStorageDirectoryListing;if(listing==null)throw new IOException("驱动不支持按文件名查找。");entries=listing.ListEntries();}catch(IOException error){if(directory.Length==0)throw;if(!Missing(error))result.Errors.Add(directory+"："+error.Message);continue;}catch(UnauthorizedAccessException error){if(directory.Length==0)throw;result.Errors.Add(directory+"："+error.Message);continue;}
                if(remember)catalog.RememberListedObjectDirectories(Id,directory,entries);
                foreach(var entry in entries)
                {
                    if(entry.Directory){pending.Push(ObjectStoragePath.Join(directory,entry.Name));continue;}
                    if(entry.Size<0)continue;string key=SearchKey(entry.Name,entry.Size);List<string> choices;if(!index.TryGetValue(key,out choices)){choices=new List<string>();index.Add(key,choices);}choices.Add(directory);
                }
            }
            return result;
        }
        public Stream OpenReadAt(string name,string directory){return Driver(directory).OpenRead(name);}
        internal IManagedBackupDestination ReadSourceAt(string directory){return Driver(directory);}
        public void RememberReadDirectory(string name,string directory){lock(gate)foundFolders[name]=directory;if(remember)catalog.RememberObjectDirectory(Id,name,directory);}
        public void Dispose(){lock(gate){foreach(var lease in writes.Values)lease.Dispose();writes.Clear();writeFolders.Clear();foreach(var driver in drivers.Values)DestinationAdapterRegistry.DisposeAdapter(driver);drivers.Clear();}}
    }
}
