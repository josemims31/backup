using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Backup.Core
{
    public sealed class PendingUploadStage
    {
        public long ArchiveId;
        public string DestinationId, Name;
    }

    public interface IUploadJournal
    {
        void RegisterUploadStage(string destination, string name, string temporary);
        IList<string> UploadStages(string destination, string name);
        void ForgetUploadStage(string destination, string temporary);
    }

    public interface IOwnedUploadDestination
    {
        IUploadJournal UploadJournal { get; set; }

        void RejectTemporary(string name);
    }

    // Reports the owned temporary object of an in-flight upload, so an ambiguous
    // upload response can be resolved against what the destination actually stored.
    public interface ITemporaryUploadProbe
    {
        RemoteObjectInfo StatTemporary(string name);
    }

    public interface IInterruptedUploadDestination
    {
        void SuspendTemporary(string name);
        bool HasInterruptedTemporary(string name);
    }

    public interface IAbandonedUploadDestination
    {
        RemoteObjectInfo StatFormal(string name);
        void DiscardTemporary(string name);
    }

    public interface IRecoverableUploadDestination
    {
        void ResumeTemporary(string name, string temporary);
    }

    public sealed class UploadStaging
    {
        private readonly string destination;
        private readonly Dictionary<string, string> active = new Dictionary<string, string>();
        private readonly Dictionary<string, HashSet<string>> interrupted = new Dictionary<string, HashSet<string>>();
        public IUploadJournal Journal;
        public UploadStaging(string id)
        {
            destination = id;
        }

        public bool HasActive(string name)
        {
            return active.ContainsKey(name);
        }

        public string Begin(string name)
        {
            if (active.ContainsKey(name))
                throw new IOException("上一次临时上传尚未提交或核对。");
            string temporary = RemotePath.Name(name) + ".upload-" + Guid.NewGuid().ToString("N") + ".partial";
            if (Journal != null)
                Journal.RegisterUploadStage(destination, name, temporary);
            active.Add(name, temporary);
            return temporary;
        }

        public string Current(string name)
        {
            string value;
            if (!active.TryGetValue(name, out value))
                throw new IOException("没有属于此次上传的临时对象。");
            return value;
        }

        public void Rejected(string name)
        {
            string value;
            if (active.TryGetValue(name, out value))
            {
                if (Journal != null)
                    Journal.ForgetUploadStage(destination, value);
                active.Remove(name);
            }
        }

        // A MOVE reply is not final visibility. The verifying caller releases ownership.
        internal static bool IsOwnedTemporary(string name, string temporary)
        {
            string prefix = name + ".upload-";
            Guid token;
            return temporary.Length == prefix.Length + 40 && temporary.StartsWith(prefix, StringComparison.Ordinal) && temporary.EndsWith(".partial", StringComparison.Ordinal) &&
                Guid.TryParseExact(temporary.Substring(prefix.Length, 32), "N", out token);
        }

        public void Resume(string name, string temporary)
        {
            if (Journal == null || !Journal.UploadStages(destination, name).Contains(temporary))
                throw new IOException("没有此临时上传的所有权记录。");
            if (!IsOwnedTemporary(name, temporary))
                throw new IOException("临时上传所有权记录无效。");
            active[name] = temporary;
        }

        public void Suspend(string name)
        {
            string current;
            if (!active.TryGetValue(name, out current))
                return;
            HashSet<string> values;
            if (!interrupted.TryGetValue(name, out values))
            {
                values = new HashSet<string>();
                interrupted.Add(name, values);
            }

            values.Add(current);
            active.Remove(name);
        }

        public bool HasInterrupted(string name)
        {
            HashSet<string> values;
            return interrupted.TryGetValue(name, out values) && values.Count > 0;
        }

        public void Delete(string name, Action<string> remove)
        {
            var owned = new HashSet<string>(Journal == null ? new string[0] : Journal.UploadStages(destination, name));
            string current;
            if (active.TryGetValue(name, out current))
                owned.Add(current);
            HashSet<string> suspended;
            if (interrupted.TryGetValue(name, out suspended))
                owned.UnionWith(suspended);
            foreach (string temporary in owned)
            {
                // Only exact token names from this upload/journal are deletable; never infer
                // ownership from a familiar final name or from another *.partial file.
                if (!IsOwnedTemporary(name, temporary))
                    throw new IOException("临时上传所有权记录无效，已保留。");
                remove(temporary);
                if (Journal != null)
                    Journal.ForgetUploadStage(destination, temporary);
                if (current == temporary)
                    active.Remove(name);
                if (suspended != null)
                    suspended.Remove(temporary);
            }
        }
    }

    public sealed partial class BackupCatalog : IUploadJournal
    {
        public void RegisterUploadStage(string destination, string name, string temporary)
        {
            lock (gate)
                database.Execute("INSERT INTO upload_temporaries(destination_id,name,temporary_name) VALUES(?,?,?)", destination, name, temporary);
        }

        public IList<string> UploadStages(string destination, string name)
        {
            lock (gate)
                return database.Query("SELECT temporary_name FROM upload_temporaries WHERE destination_id=? AND name=?", destination, name).Select(r => (string)r[0]).ToList();
        }

        public void ForgetUploadStage(string destination, string temporary)
        {
            lock (gate)
                database.Execute("DELETE FROM upload_temporaries WHERE destination_id=? AND temporary_name=?", destination, temporary);
        }

        public IList<PendingUploadStage> PendingUploadStages()
        {
            lock (gate)
                return database.Query(("SELECT DISTINCT o.archive_id,t.destination_id,t.name FROM upload_temporaries t JOIN objects o ON o.name=t.name WHERE NOT EXISTS(SELECT 1 FROM warehouse_moves m " +
                    "WHERE m.destination_id=t.destination_id AND m.state<>'complete')"))
                    .Select(row => new PendingUploadStage {
                    ArchiveId = (long)row[0],
                    DestinationId = (string)row[1],
                    Name = (string)row[2]
                })
                    .ToList();
        }
    }
}
