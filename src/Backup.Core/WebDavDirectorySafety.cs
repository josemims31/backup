using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Backup.Core
{
    public sealed class RemoteDirectoryPendingException : IOException
    {
        public RemoteDirectoryPendingException(string location, bool creation, Exception inner = null) : base((creation ? "目录创建结果待确认，保留原目标继续核对：" : "原仓库目录暂时不可见，保留原目标继续核对：") + location, inner)
        {
        }
    }

    public sealed partial class BackupCatalog
    {
        private static string WebDavDirectoryKey(string kind, string destination, string location)
        {
            using (var hash = SHA256.Create())
                return "webdav_directory_v1:" + kind + ":" + destination + ":" + Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(location)));
        }

        internal string WebDavRepositoryLocation(string destination)
        {
            lock (gate)
            {
                var rows = database.Query("SELECT location FROM destinations WHERE id=? AND protocol='webdav'", destination);
                return rows.Count == 0 ? null : (string)rows[0][0];
            }
        }

        internal bool WebDavRepositoryWasUsed(string destination, string location)
        {
            lock (gate)
            {
                if (GetSetting(WebDavDirectoryKey("known", destination, location)) == "1")
                    return true;
                var rows = database.Query("SELECT location,used_bytes FROM destinations WHERE id=? AND protocol='webdav'", destination);
                if (rows.Count == 0)
                    return false;
                if (RemotePath.Root((string)rows[0][0], "http").AbsoluteUri != location)
                    return false;
                if ((long)rows[0][1] > 0 || Scalar("SELECT EXISTS(SELECT 1 FROM object_copies WHERE destination_id=?)", destination) > 0)
                    return true;
                var inventory = GetInventoryFolder(destination, location);
                return inventory != null && string.IsNullOrEmpty(inventory.Error) && !string.IsNullOrEmpty(inventory.CheckedUtc);
            }
        }

        internal void RememberWebDavRepository(string destination, string location)
        {
            lock (gate)
                database.Execute("INSERT OR IGNORE INTO settings(key,value) VALUES(?,'1')", WebDavDirectoryKey("known", destination, location));
        }

        internal bool ClaimWebDavDirectoryCreation(string destination, string location)
        {
            lock (gate)
                return database.Transaction(delegate
                {
                    string key = WebDavDirectoryKey("pending", destination, location);
                    if (GetSetting(key) != null)
                        return false;
                    database.Execute("INSERT INTO settings(key,value) VALUES(?,'1')", key);
                    return true;
                });
        }

        internal bool WebDavDirectoryCreationPending(string destination, string location)
        {
            lock (gate)
                return GetSetting(WebDavDirectoryKey("pending", destination, location)) != null;
        }

        internal void ForgetWebDavDirectoryCreation(string destination, string location)
        {
            lock (gate)
                database.Execute("DELETE FROM settings WHERE key=?", WebDavDirectoryKey("pending", destination, location));
        }
    }
}
