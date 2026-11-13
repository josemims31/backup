using System;
using System.Collections.Generic;
using System.Threading;

namespace Backup.Core
{
    // A lease prevents bind/step/reset interleaving on a reused statement.
    // Only bounded, parameterized SELECTs are retained; batch writes keep their
    // existing ExecuteMany lifetime and transaction boundaries.
    internal sealed class SqliteStatementCache : IDisposable
    {
        internal sealed class Entry
        {
            internal string Sql;
            internal IntPtr Statement;
            internal bool Used, Retired;
            internal LinkedListNode<Entry> Order;
        }

        internal sealed class Lease : IDisposable
        {
            private SqliteStatementCache owner;
            private readonly Entry entry;
            internal IntPtr Statement
            {
                get
                {
                    return entry.Statement;
                }
            }

            internal Lease(SqliteStatementCache value, Entry item)
            {
                owner = value;
                entry = item;
            }

            public void Dispose()
            {
                var value = Interlocked.Exchange(ref owner, null);
                if (value != null)
                    value.Release(entry);
            }
        }

        private readonly object gate = new object();
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly LinkedList<Entry> recent = new LinkedList<Entry>();
        private readonly Func<string, IntPtr> prepare;
        private readonly Func<IntPtr, int> reset, clear;
        private readonly Action<IntPtr> finalize;
        private bool disposed;
        internal SqliteStatementCache(Func<string, IntPtr> create, Func<IntPtr, int> restart, Func<IntPtr, int> bindings, Action<IntPtr> release)
        {
            prepare = create;
            reset = restart;
            clear = bindings;
            finalize = release;
        }

        internal Lease Rent(string sql, bool retain)
        {
            lock (gate)
            {
                if (disposed)
                    throw new ObjectDisposedException("SqliteStatementCache");
                Entry entry;
                if (retain && entries.TryGetValue(sql, out entry) && !entry.Used)
                {
                    entry.Used = true;
                    recent.Remove(entry.Order);
                    recent.AddLast(entry.Order);
                    return new Lease(this, entry);
                }

                if (retain && entries.ContainsKey(sql))
                    retain = false;
                if (retain && entries.Count == 32)
                {
                    var oldest = recent.First;
                    while (oldest != null && oldest.Value.Used)
                        oldest = oldest.Next;
                    if (oldest == null)
                        retain = false;
                    else
                        Retire(oldest.Value);
                }

                entry = new Entry
                {
                    Sql = sql,
                    Statement = prepare(sql),
                    Used = true,
                    Retired = !retain
                };
                if (retain)
                {
                    entry.Order = recent.AddLast(entry);
                    entries.Add(sql, entry);
                }

                return new Lease(this, entry);
            }
        }

        private void Retire(Entry entry)
        {
            entry.Retired = true;
            entries.Remove(entry.Sql);
            if (entry.Order != null)
            {
                recent.Remove(entry.Order);
                entry.Order = null;
            }

            if (!entry.Used)
                finalize(entry.Statement);
        }

        private void Release(Entry entry)
        {
            lock (gate)
            {
                if (entry.Retired || disposed)
                {
                    finalize(entry.Statement);
                    entry.Used = false;
                    return;
                }

                int result = reset(entry.Statement), bindings = clear(entry.Statement);
                entry.Used = false;
                // An errored evaluation never re-enters the cache. Its original
                // exception is preserved rather than replaced by cleanup errors.
                if (result != 0 || bindings != 0)
                    Retire(entry);
            }
        }

        internal void Clear()
        {
            lock (gate)
            {
                foreach (var entry in new List<Entry>(entries.Values))
                    Retire(entry);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                    return;
                disposed = true;
                Clear();
            }
        }
    }
}
