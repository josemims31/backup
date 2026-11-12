using System.Collections.Generic;
namespace Backup.Core
{
    public sealed class StorageEntry
    {
        public string Name,Location;
        public bool Directory;
        public long Size;
    }
    // Metadata enumeration is independent of payload upload, download and catalog ownership.
    public interface IStorageDirectoryListing {IList<StorageEntry> ListEntries();}
    public interface IStorageEntryRemoval
    {
        StorageEntry InspectEntry(string name);
        void RemoveEntry(string name,bool directory);
    }
    public interface ITaskStorageRemoval
    {
        void DeleteDirectoryTree(string name);
    }
    public interface IStorageEntryMover
    {
        void MoveEntry(string name,string targetFolder,string targetName,bool directory);
    }
    public sealed class StoredArchiveObject {public string Name,State,TaskId,ArchiveStatus;public long Size,ArchiveId;}
}
