using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace Backup.Core
{
    public sealed partial class BackupCatalog
    {
        public string EnsureTaskTag(string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId))
                throw new ArgumentException("任务标识不能为空。");
            lock (gate)
            {
                string existing = GetSetting("archive_task_tag:" + taskId);
                if (existing != null)
                    return existing;
            }

            return Transaction(() => EnsureTaskTagUnsafe(taskId));
        }

        private string EnsureTaskTagUnsafe(string taskId)
        {
            string key = "archive_task_tag:" + taskId, tag = GetSetting(key);
            if (tag != null)
                return tag;
            var used = new HashSet<string>(database.Query("SELECT value FROM settings WHERE key GLOB 'archive_task_tag:*'").Select(r => (string)r[0]), StringComparer.OrdinalIgnoreCase);
            const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
            byte[] bytes = new byte[4];
            using (var random = RandomNumberGenerator.Create())
                do
                {
                    random.GetBytes(bytes);
                    uint value = BitConverter.ToUInt32(bytes, 0);
                    var code = new char[4];
                    for (int index = 0; index < 4; index++)
                    {
                        code[index] = alphabet[(int)(value % 62)];
                        value /= 62;
                    }

                    tag = new string (code);
                }
                while (used.Contains(tag));
            database.Execute("INSERT INTO settings(key,value) VALUES(?,?)", key, tag);
            return tag;
        }
    }
}
