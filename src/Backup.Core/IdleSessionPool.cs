using System;
using System.Collections.Generic;

namespace Backup.Core
{
    // The session owner supplies synchronization and the existing idle timer.
    // Renting never creates a connection; returning only retains healthy ones.
    public sealed class IdleSessionPool<T>
        where T : class
    {
        private readonly List<T> sessions = new List<T>();
        private readonly int capacity;
        public IdleSessionPool(int maximum)
        {
            if (maximum < 1)
                throw new ArgumentOutOfRangeException("maximum");
            capacity = maximum;
        }

        public int Count
        {
            get
            {
                return sessions.Count;
            }
        }

        public T Take()
        {
            if (sessions.Count == 0)
                return null;
            int last = sessions.Count - 1;
            T value = sessions[last];
            sessions.RemoveAt(last);
            return value;
        }

        public T Return(T value)
        {
            if (value == null)
                throw new ArgumentNullException("value");
            if (sessions.Contains(value))
                return null;
            T retired = null;
            if (sessions.Count == capacity)
            {
                retired = sessions[0];
                sessions.RemoveAt(0);
            }

            sessions.Add(value);
            return retired;
        }

        public bool Remove(T value)
        {
            return sessions.Remove(value);
        }

        public T[] Drain()
        {
            T[] result = sessions.ToArray();
            sessions.Clear();
            return result;
        }
    }
}
