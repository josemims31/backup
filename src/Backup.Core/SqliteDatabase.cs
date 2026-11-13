using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;

namespace Backup.Core
{
    // Windows SQLite is isolated here; no provider package or global installation is required.
    internal sealed class SqliteDatabase : IDisposable
    {
        private readonly object statementsGate = new object();
        private SqliteStatementCache statements;
#if UI_TEST
        internal long PreparedStatementsCreated;
#endif
        private SqliteStatementCache Statements
        {
            get
            {
                lock (statementsGate)
                {
                    if (statements == null)
                        statements = new SqliteStatementCache(Prepare, sqlite3_reset, sqlite3_clear_bindings, value => sqlite3_finalize(value));
                    return statements;
                }
            }
        }

        private IntPtr Prepare(string sql)
        {
            IntPtr value = IntPtr.Zero;
            byte[] text = Encoding.UTF8.GetBytes(sql + "\0");
            try
            {
#if UI_TEST
                System.Threading.Interlocked.Increment(ref PreparedStatementsCreated);
#endif
                Check(sqlite3_prepare_v2(handle, text, text.Length, out value, IntPtr.Zero));
                return value;
            }
            catch (global::System.Exception diagnosticFailure)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/SqliteDatabase.cs:Prepare", diagnosticFailure);
                if (value != IntPtr.Zero)
                    sqlite3_finalize(value);
                throw;
            }
        }

        private void ClearStatements()
        {
            lock (statementsGate)
                if (statements != null)
                    statements.Clear();
        }

        private IntPtr handle;
        private IDisposable importedMemory;
        private FileStream importFile;
        private SqliteDatabase()
        {
        }

        private bool trackSaves, pendingSave;
        private long lastSavedTicks;
        internal DateTime LastSavedUtc
        {
            get
            {
                long ticks = System.Threading.Interlocked.Read(ref lastSavedTicks);
                return ticks == 0 ? DateTime.MinValue : new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_get_autocommit(IntPtr database);
        private void TrackSaved(int before, string sql)
        {
            if (!trackSaves)
                return;
            if (sql.TrimStart().StartsWith("ROLLBACK", StringComparison.OrdinalIgnoreCase))
                pendingSave = false;
            else if (ChangeStamp != before)
                pendingSave = true;
            if (pendingSave && sqlite3_get_autocommit(handle) != 0)
            {
                System.Threading.Interlocked.Exchange(ref lastSavedTicks, DateTime.UtcNow.Ticks);
                pendingSave = false;
            }
        }

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int sqlite3_open_v2(byte[] path, out IntPtr database, int flags, string vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int sqlite3_open16(string path, out IntPtr database);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr database);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_errmsg16(IntPtr database);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_busy_timeout(IntPtr database, int ms);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_prepare_v2(IntPtr database, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_null(IntPtr statement, int index);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_blob(IntPtr statement, int index, byte[] value, int bytes, IntPtr destructor);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int sqlite3_bind_text16(IntPtr statement, int index, string value, int bytes, IntPtr destructor);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_reset(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_clear_bindings(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_total_changes(IntPtr database);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_count(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_type(IntPtr statement, int index);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern long sqlite3_column_int64(IntPtr statement, int index);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text16(IntPtr statement, int index);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_blob(IntPtr statement, int index);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_bytes(IntPtr statement, int index);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern long sqlite3_last_insert_rowid(IntPtr database);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern IntPtr sqlite3_backup_init(IntPtr destination, string destinationName, IntPtr source, string sourceName);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_backup_step(IntPtr backup, int pages);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_backup_finish(IntPtr backup);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern IntPtr sqlite3_serialize(IntPtr database, string name, out long size, uint flags);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void sqlite3_free(IntPtr value);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_malloc64(ulong size);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int sqlite3_deserialize(IntPtr database, string schema, IntPtr data, long size, long capacity, uint flags);
        internal SqliteDatabase(string path, bool catalog = false, bool exclusive = true)
        {
            bool memory = path == ":memory:";
            if (!memory)
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            trackSaves = catalog && !memory;
            if (catalog && !memory &&
                ((File.GetAttributes(Path.GetDirectoryName(Path.GetFullPath(path))) & FileAttributes.ReparsePoint) != 0 ||
                File.Exists(path) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                throw new IOException("数据库目录或文件不能是链接或联接点。");
            string vfs = null;
            if (catalog && !memory)
            {
                path = Path.GetFullPath(path);
                string relocated = CacheFileSystem.Register(path, ApplicationFiles.CacheForCatalog(path));
                foreach (string file in new[]
                {
                    path,
                    relocated + "-wal"
                }

                )
                    if (File.Exists(file))
                        lastSavedTicks = Math.Max(lastSavedTicks, File.GetLastWriteTimeUtc(file).Ticks);
                bool legacy = File.Exists(path + "-wal") || File.Exists(path + "-shm") || File.Exists(path + "-journal");
                if (legacy)
                {
                    if (File.Exists(relocated + "-wal") || File.Exists(relocated + "-journal"))
                        throw new IOException("检测到两套数据库恢复文件，已保留，请先核对后再启动。");
                    using (var old = new SqliteDatabase(path, false, false))
                    {
                        old.Query("SELECT count(*) FROM sqlite_master");
                        var checkpoint = old.Query("PRAGMA wal_checkpoint(TRUNCATE)");
                        if (checkpoint.Count > 0 && (long)checkpoint[0][0] != 0)
                            throw new IOException("原数据库仍被其他程序使用，无法迁移事务文件。");
                        if ((string)old.Query("PRAGMA journal_mode=DELETE")[0][0] != "delete")
                            throw new IOException("无法完成原数据库事务文件迁移。");
                    }
                }

                vfs = CacheFileSystem.Name;
            }

            int code = sqlite3_open_v2(Encoding.UTF8.GetBytes((memory ? path : Path.GetFullPath(path)) + "\0"), out handle, 6 | 0x10000, vfs);
            if (code != 0)
            {
                string message = handle == IntPtr.Zero ? "Unable to open SQLite catalog." : Marshal.PtrToStringUni(sqlite3_errmsg16(handle));
                if (handle != IntPtr.Zero)
                {
                    sqlite3_close(handle);
                    handle = IntPtr.Zero;
                }

                throw new IOException(message);
            }

            try
            {
                Check(sqlite3_busy_timeout(handle, 5000));
                if (exclusive)
                    Execute("PRAGMA locking_mode=EXCLUSIVE");
                Execute("PRAGMA temp_store=MEMORY");
            }
            catch (global::System.Exception diagnosticFailure)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/SqliteDatabase.cs:SqliteDatabase", diagnosticFailure);
                Dispose();
                throw;
            }
        }

        private void Check(int code)
        {
            if (code != 0)
                throw new IOException("SQLite error " + code + ": " + Marshal.PtrToStringUni(sqlite3_errmsg16(handle)));
        }

        internal static SqliteDatabase FromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 100 || Encoding.ASCII.GetString(bytes, 0, 16) != "SQLite format 3\0")
                throw new IOException("所选文件不是 SQLite 数据库。");
            var result = new SqliteDatabase(":memory:");
            IntPtr data = IntPtr.Zero;
            try
            {
                data = sqlite3_malloc64((ulong)bytes.Length + 20);
                if (data == IntPtr.Zero)
                    throw new OutOfMemoryException();
                Marshal.Copy(bytes, 0, data, bytes.Length);
                // Serialization is a complete database image, not a live WAL file.
                Marshal.WriteByte(data, 18, 1);
                Marshal.WriteByte(data, 19, 1);
                for (int i = 0; i < 20; i++)
                    Marshal.WriteByte(data, bytes.Length + i, 0);
                int code = sqlite3_deserialize(result.handle, "main", data, bytes.Length, bytes.Length + 20, 3);
                data = IntPtr.Zero;
                result.Check(code);
                return result;
            }
            catch (global::System.Exception diagnosticFailure)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/SqliteDatabase.cs:FromBytes", diagnosticFailure);
                result.Dispose();
                throw;
            }
            finally
            {
                if (data != IntPtr.Zero)
                    sqlite3_free(data);
            }
        }

        internal void ReplaceWith(SqliteDatabase source)
        {
            ClearStatements();
            IntPtr backup = sqlite3_backup_init(handle, "main", source.handle, "main");
            if (backup == IntPtr.Zero)
                throw new IOException("数据库导入事务初始化失败。");
            try
            {
                int code = sqlite3_backup_step(backup, -1);
                if (code != 101)
                    Check(code);
            }
            finally
            {
                Check(sqlite3_backup_finish(backup));
            }

            if (trackSaves)
                System.Threading.Interlocked.Exchange(ref lastSavedTicks, DateTime.UtcNow.Ticks);
        }

        internal List<object[]> Query(string sql, params object[] values)
        {
            if (handle == IntPtr.Zero)
                throw new ObjectDisposedException("SqliteDatabase");
            IntPtr statement = IntPtr.Zero;
            SqliteStatementCache.Lease lease = null;
            int before = trackSaves ? ChangeStamp : 0;
            try
            {
                string command = sql.TrimStart();
                if (command.StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase) || command.StartsWith("DROP ", StringComparison.OrdinalIgnoreCase) ||
                    command.StartsWith("ALTER ", StringComparison.OrdinalIgnoreCase) ||
                    command.StartsWith("VACUUM", StringComparison.OrdinalIgnoreCase) ||
                    command.StartsWith("ATTACH ", StringComparison.OrdinalIgnoreCase) ||
                    command.StartsWith("DETACH ", StringComparison.OrdinalIgnoreCase))
                    ClearStatements();
                bool retain = values.Length > 0 && sql.Length <= 4096 && command.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase);
                if (retain)
                {
                    lease = Statements.Rent(sql, true);
                    statement = lease.Statement;
                }
                else
                {
                    // Keep noncached commands on their original direct lifetime.
                    statement = Prepare(sql);
                }
                for (int i = 0; i < values.Length; i++)
                {
                    BindValue(statement, i + 1, values[i]);
                }

                var rows = new List<object[]>();
                int status;
                while ((status = sqlite3_step(statement)) == 100)
                {
                    var row = new object[sqlite3_column_count(statement)];
                    for (int i = 0; i < row.Length; i++)
                    {
                        row[i] = ReadColumn(statement, i);
                    }

                    rows.Add(row);
                }

                if (status != 101)
                    Check(status);
                TrackSaved(before, sql);
                return rows;
            }
            finally
            {
                if (lease != null)
                    lease.Dispose();
                else if (statement != IntPtr.Zero)
                    sqlite3_finalize(statement);
            }
        }

        internal void Execute(string sql, params object[] values)
        {
            Query(sql, values);
        }

        private void BindValue(IntPtr statement, int index, object value)
        {
            if (value == null)
                Check(sqlite3_bind_null(statement, index));
            else if (value is string)
                Check(sqlite3_bind_text16(statement, index, (string)value, -1, new IntPtr(-1)));
            else if (value is byte[])
            {
                var bytes = (byte[])value;
                Check(sqlite3_bind_blob(statement, index, bytes.Length == 0 ? new byte[1] : bytes, bytes.Length, new IntPtr(-1)));
            }
            else
                Check(sqlite3_bind_int64(statement, index, Convert.ToInt64(value)));
        }

        private static object ReadColumn(IntPtr statement, int index)
        {
            int type = sqlite3_column_type(statement, index);
            if (type == 5)
                return null;
            if (type == 1)
                return sqlite3_column_int64(statement, index);
            if (type == 4)
            {
                int length = sqlite3_column_bytes(statement, index);
                var bytes = new byte[length];
                if (length > 0)
                    Marshal.Copy(sqlite3_column_blob(statement, index), bytes, 0, length);
                return bytes;
            }

            return Marshal.PtrToStringUni(sqlite3_column_text16(statement, index));
        }

        // Used for import copies so a large archive_files table never becomes a second
        // managed list of all its rows. The source image and target are both in memory.
        internal IEnumerable<object[]> StreamQuery(string sql, params object[] values)
        {
            IntPtr statement = IntPtr.Zero;
            try
            {
                byte[] text = Encoding.UTF8.GetBytes(sql + "\0");
                Check(sqlite3_prepare_v2(handle, text, text.Length, out statement, IntPtr.Zero));
                for (int index = 0; index < values.Length; index++)
                {
                    BindValue(statement, index + 1, values[index]);
                }

                int status;
                while ((status = sqlite3_step(statement)) == 100)
                {
                    var row = new object[sqlite3_column_count(statement)];
                    for (int index = 0; index < row.Length; index++)
                    {
                        row[index] = ReadColumn(statement, index);
                    }

                    yield return row;
                }

                if (status != 101)
                    Check(status);
            }
            finally
            {
                if (statement != IntPtr.Zero)
                    sqlite3_finalize(statement);
            }
        }

        internal int ChangeStamp
        {
            get
            {
                return sqlite3_total_changes(handle);
            }
        }

        internal void ExecuteMany(string sql, IEnumerable<object[]> rows)
        {
            IntPtr statement = IntPtr.Zero;
            int before = trackSaves ? ChangeStamp : 0;
            try
            {
                byte[] text = Encoding.UTF8.GetBytes(sql + "\0");
                Check(sqlite3_prepare_v2(handle, text, text.Length, out statement, IntPtr.Zero));
                foreach (var values in rows)
                {
                    for (int i = 0; i < values.Length; i++)
                    {
                        BindValue(statement, i + 1, values[i]);
                    }

                    int status = sqlite3_step(statement);
                    if (status != 101)
                        Check(status);
                    Check(sqlite3_reset(statement));
                    Check(sqlite3_clear_bindings(statement));
                }
            }
            finally
            {
                if (statement != IntPtr.Zero)
                    sqlite3_finalize(statement);
                TrackSaved(before, sql);
            }
        }

        internal long LastId
        {
            get
            {
                return sqlite3_last_insert_rowid(handle);
            }
        }

        internal T Transaction<T>(Func<T> action)
        {
            Execute("BEGIN IMMEDIATE");
            try
            {
                T result = action();
                Execute("COMMIT");
                return result;
            }
            catch (global::System.Exception diagnosticFailure)
            {
                global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/SqliteDatabase.cs:Transaction", diagnosticFailure);
                Execute("ROLLBACK");
                throw;
            }
        }

        internal void Snapshot(string destination)
        {
            if (File.Exists(destination))
                throw new IOException("Snapshot destination already exists.");
            using (var target = new SqliteDatabase(destination))
            {
                IntPtr backup = sqlite3_backup_init(target.handle, "main", handle, "main");
                if (backup == IntPtr.Zero)
                    throw new IOException("SQLite snapshot initialization failed.");
                try
                {
                    int result = sqlite3_backup_step(backup, -1);
                    if (result != 101)
                        Check(result);
                }
                finally
                {
                    target.Check(sqlite3_backup_finish(backup));
                }
            }
        }

        internal DatabaseMemorySnapshot SnapshotMemory(MemoryBudget budget, Action<SqliteDatabase> prepare = null)
        {
            long pages = (long)Query("PRAGMA page_count")[0][0], pageSize = (long)Query("PRAGMA page_size")[0][0];
            using (budget.Reserve(checked(pages * (pageSize + 128))))
            using (var target = new SqliteDatabase(":memory:"))
            {
                IntPtr backup = sqlite3_backup_init(target.handle, "main", handle, "main");
                if (backup == IntPtr.Zero)
                    throw new IOException("SQLite 内存快照初始化失败。");
                try
                {
                    int code = sqlite3_backup_step(backup, -1);
                    if (code != 101)
                        Check(code);
                }
                finally
                {
                    target.Check(sqlite3_backup_finish(backup));
                }

                if (prepare != null)
                    prepare(target);
                long image = (long)target.Query("PRAGMA page_count")[0][0] * (long)target.Query("PRAGMA page_size")[0][0];
                IDisposable reservation = budget.Reserve(image);
                IntPtr data = IntPtr.Zero;
                try
                {
                    long size;
                    try
                    {
                        data = sqlite3_serialize(target.handle, "main", out size, 0);
                    }
                    catch (EntryPointNotFoundException)
                    {
                        throw new NotSupportedException("此 Windows SQLite 不支持内存快照导出。");
                    }

                    if (data == IntPtr.Zero || size <= 0 || size > image)
                        throw new IOException("SQLite 内存快照导出失败。");
                    Marshal.WriteByte(data, 18, 1);
                    Marshal.WriteByte(data, 19, 1);
                    var result = new DatabaseMemorySnapshot(data, size, reservation);
                    data = IntPtr.Zero;
                    reservation = null;
                    return result;
                }
                finally
                {
                    if (data != IntPtr.Zero)
                        sqlite3_free(data);
                    if (reservation != null)
                        reservation.Dispose();
                }
            }
        }

        internal static SqliteDatabase OpenImportReadOnly(string path, MemoryBudget budget)
        {
            var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = new byte[20];
            try
            {
                if (guard.Read(header, 0, header.Length) != header.Length || Encoding.ASCII.GetString(header, 0, 16) != "SQLite format 3\0")
                    throw new IOException("所选文件不是 SQLite 数据库。");
                var result = new SqliteDatabase();
                try
                {
                    // Like the former detached image, import reads the frozen main
                    // file without attaching a neighboring WAL or recovering a journal.
                    string filename = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?immutable=1";
                    int code = sqlite3_open_v2(Encoding.UTF8.GetBytes(filename + "\0"), out result.handle, 1 | 0x40 | 0x10000, null);
                    if (code != 0)
                    {
                        result.Dispose();
                        guard.Dispose();
                        guard = null;
                        return FromFileMemory(path, budget);
                    }

                    result.Check(sqlite3_busy_timeout(result.handle, 5000));
                    result.Execute("PRAGMA temp_store=MEMORY");
                    result.Execute("PRAGMA cache_size=-2048");
                    result.importedMemory = budget.Reserve(2L * 1024 * 1024);
                    result.importFile = guard;
                    guard = null;
                    return result;
                }
                catch (global::System.Exception diagnosticFailure)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/SqliteDatabase.cs:OpenImportReadOnly", diagnosticFailure);
                    result.Dispose();
                    throw;
                }
            }
            finally
            {
                if (guard != null)
                    guard.Dispose();
            }
        }

        internal static SqliteDatabase FromFileMemory(string path, MemoryBudget budget, string expectedDigest = null)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var hash = expectedDigest == null ? null : SHA256.Create())
            {
                long length = input.Length;
                if (length < 100)
                    throw new IOException("所选文件不是 SQLite 数据库。");
                IDisposable reservation = budget.Reserve(checked(length + 20));
                IntPtr data = IntPtr.Zero;
                SqliteDatabase result = null;
                try
                {
                    byte[] buffer = new byte[65536];
                    data = sqlite3_malloc64((ulong)length + 20);
                    if (data == IntPtr.Zero)
                        throw new OutOfMemoryException();
                    long offset = 0;
                    while (offset < length)
                    {
                        int count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, length - offset));
                        if (count == 0)
                            throw new IOException("数据库文件读取不完整。");
                        if (hash != null)
                            hash.TransformBlock(buffer, 0, count, null, 0);
                        Marshal.Copy(buffer, 0, new IntPtr(data.ToInt64() + offset), count);
                        offset += count;
                    }

                    if (hash != null)
                    {
                        hash.TransformFinalBlock(new byte[0], 0, 0);
                        if (!BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant().Equals(expectedDigest, StringComparison.Ordinal))
                            throw new IOException("备份版本数据库校验失败。");
                    }

                    byte[] header = new byte[16];
                    Marshal.Copy(data, header, 0, 16);
                    if (Encoding.ASCII.GetString(header) != "SQLite format 3\0")
                        throw new IOException("所选文件不是 SQLite 数据库。");
                    Marshal.WriteByte(data, 18, 1);
                    Marshal.WriteByte(data, 19, 1);
                    for (int i = 0; i < 20; i++)
                        Marshal.WriteByte(new IntPtr(data.ToInt64() + length), i, 0);
                    result = new SqliteDatabase(":memory:");
                    int code = sqlite3_deserialize(result.handle, "main", data, length, length + 20, 3);
                    data = IntPtr.Zero;
                    result.Check(code);
                    result.importedMemory = reservation;
                    reservation = null;
                    return result;
                }
                catch (global::System.Exception diagnosticFailure)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/SqliteDatabase.cs:FromFileMemory", diagnosticFailure);
                    if (result != null)
                        result.Dispose();
                    throw;
                }
                finally
                {
                    if (data != IntPtr.Zero)
                        sqlite3_free(data);
                    if (reservation != null)
                        reservation.Dispose();
                }
            }
        }

        internal byte[] SnapshotBytes(Action<SqliteDatabase> prepare = null, long maximumBytes = long.MaxValue)
        {
            if ((decimal)(long)Query("PRAGMA page_count")[0][0] * (long)Query("PRAGMA page_size")[0][0] > maximumBytes)
                throw new NotSupportedException("数据库快照超过内存导出预算。");
            using (var target = new SqliteDatabase(":memory:"))
            {
                IntPtr backup = sqlite3_backup_init(target.handle, "main", handle, "main");
                if (backup == IntPtr.Zero)
                    throw new IOException("SQLite 内存快照初始化失败。");
                try
                {
                    int result = sqlite3_backup_step(backup, -1);
                    if (result != 101)
                        Check(result);
                }
                finally
                {
                    target.Check(sqlite3_backup_finish(backup));
                }

                if (prepare != null)
                    prepare(target);
                long size;
                IntPtr data;
                try
                {
                    data = sqlite3_serialize(target.handle, "main", out size, 0);
                }
                catch (EntryPointNotFoundException)
                {
                    throw new NotSupportedException("此 Windows SQLite 不支持内存快照导出。");
                }

                if (data == IntPtr.Zero)
                    throw new IOException("SQLite 内存快照导出失败。");
                try
                {
                    if (size > maximumBytes)
                        throw new NotSupportedException("数据库快照超过内存导出预算。");
                    if (size <= 0)
                        throw new IOException("SQLite 快照为空。");
                    var bytes = new byte[(int)size];
                    Marshal.Copy(data, bytes, 0, bytes.Length);
                    return bytes;
                }
                finally
                {
                    sqlite3_free(data);
                }
            }
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero)
                return;
            lock (statementsGate)
            {
                if (statements != null)
                {
                    statements.Dispose();
                    statements = null;
                }
            }

            int result = sqlite3_close(handle);
            if (result != 0)
                Check(result);
            handle = IntPtr.Zero;
            if (importedMemory != null)
            {
                importedMemory.Dispose();
                importedMemory = null;
            }

            if (importFile != null)
            {
                importFile.Dispose();
                importFile = null;
            }
        }
    }
}
