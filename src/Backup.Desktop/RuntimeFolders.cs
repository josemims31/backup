using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Backup.Desktop
{
    public sealed class RuntimeFileMove
    {
        public string Source, Target;
    }

    internal static class RuntimeFolders
    {
        internal static string Prepare(string directory)
        {
            string cache = Path.Combine(Path.GetFullPath(directory), "cache");
            Directory.CreateDirectory(cache);
            if ((File.GetAttributes(cache) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("缓存目录不能是链接或联接点。");
            Environment.SetEnvironmentVariable("TEMP", cache);
            Environment.SetEnvironmentVariable("TMP", cache);
            return cache;
        }

        private static bool Under(string path, string root)
        {
            return Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private static bool NodeFiles(string path, string data)
        {
            if (!Under(path, data))
                return false;
            string folder = Path.GetFullPath(path).Substring(Path.GetFullPath(data).TrimEnd('\\').Length + 1).Split('\\')[0];
            return Regex.IsMatch(folder, @"\Anode-[A-Za-z0-9]{4}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static bool DatabaseFile(string name)
        {
            return name.Equals("catalog.db", StringComparison.OrdinalIgnoreCase) || name.Equals("catalog.db-wal", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("catalog.db-shm", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("catalog.db-journal", StringComparison.OrdinalIgnoreCase);
        }

        private static void Validate(RuntimeFileMove move, string data, string logs, string cache)
        {
            if ((!Under(move.Source, data) && !Under(move.Source, logs)) || !Under(move.Target, cache))
                throw new IOException("运行文件迁移路径越界。");
            if (NodeFiles(move.Source, data))
                throw new IOException("不能迁移正在使用的本地版本数据库目录。");
            if (DatabaseFile(Path.GetFileName(move.Source)))
                throw new IOException("不能迁移活动数据库事务文件。");
        }

        private static void Move(RuntimeFileMove move, string data, string logs, string cache)
        {
            Validate(move, data, logs, cache);
            bool source = File.Exists(move.Source) || Directory.Exists(move.Source), target = File.Exists(move.Target) || Directory.Exists(move.Target);
            if (!source && target)
                return;
            if (!source || target)
                throw new IOException("运行文件迁移状态待核对，已保留：" + move.Source);
            if ((File.GetAttributes(move.Source) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("运行文件为链接，已保留：" + move.Source);
            Directory.CreateDirectory(Path.GetDirectoryName(move.Target));
            for (string folder = Path.GetDirectoryName(move.Target); folder.StartsWith(cache, StringComparison.OrdinalIgnoreCase); folder = Path.GetDirectoryName(folder))
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("缓存子目录为链接，已保留原运行文件。");
            if (Directory.Exists(move.Source))
                Directory.Move(move.Source, move.Target);
            else
                File.Move(move.Source, move.Target);
        }

        private static void Plan(List<RuntimeFileMove> moves, string source, string target)
        {
            while (File.Exists(target) || Directory.Exists(target) || moves.Exists(move => move.Target.Equals(target, StringComparison.OrdinalIgnoreCase)))
                target += ".migrated-" + Guid.NewGuid().ToString("N");
            moves.Add(new RuntimeFileMove { Source = source, Target = target });
        }

        internal static void Migrate(string directory, string cache, Action<IList<RuntimeFileMove>> updateReferences)
        {
            string data = Path.Combine(directory, "data"), logs = Path.Combine(directory, "logs"), journal = Path.Combine(cache, "runtime-moves.db");
            var moves = new List<RuntimeFileMove>();
            // Resume a durable plan before discovering any new work.
            if (File.Exists(journal))
                using (var state = new Backup.Core.RuntimeStateStore(journal))
                    moves = JsonSettings.Decode(state.Get("moves"), new List<RuntimeFileMove>());
            // Older plans may contain the same incorrect move as discovery did.
            moves.RemoveAll(move => NodeFiles(move.Source, data));
            if (moves.Count == 0)
            {
                if (Directory.Exists(data))
                {
                    if ((File.GetAttributes(data) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("数据目录不能是链接或联接点。");
                    foreach (string item in Directory.GetFileSystemEntries(data))
                    {
                        string name = Path.GetFileName(item);
                        if (name.Equals("catalog.db", StringComparison.OrdinalIgnoreCase) || NodeFiles(item, data))
                            continue;
                        if (DatabaseFile(name))
                            throw new IOException("原数据库仍有未完成的事务恢复文件，已保留。");
                        string target = name.StartsWith("catalog-before-", StringComparison.Ordinal) || name.StartsWith("取消迁移待核对-", StringComparison.Ordinal) 
                            ? Path.Combine(cache, name) 
                            : name.EndsWith(".log", StringComparison.Ordinal) ||
                            name.EndsWith(".log.previous", StringComparison.Ordinal) 
                            ? Path.Combine(cache, "logs", name) 
                            : Path.Combine(cache, "legacy-data", name);
                        Plan(moves, item, target);
                    }
                }

                if (Directory.Exists(logs))
                {
                    if ((File.GetAttributes(logs) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("旧日志目录为链接，已保留。");
                    foreach (string item in Directory.GetFileSystemEntries(logs))
                        Plan(moves, item, Path.Combine(cache, "logs", Path.GetFileName(item)));
                }
            }

            if (moves.Count > 0)
            {
                using (var state = new Backup.Core.RuntimeStateStore(journal))
                {
                    state.Set("moves", JsonSettings.Encode(moves));
                    foreach (var move in moves)
                        Move(move, data, logs, cache);
                    updateReferences(moves);
                    state.Set("moves", "[]");
                }
            }

            if (File.Exists(journal))
                File.Delete(journal);
            if (Directory.Exists(logs) && Directory.GetFileSystemEntries(logs).Length == 0)
                Directory.Delete(logs);
        }
    }
}
