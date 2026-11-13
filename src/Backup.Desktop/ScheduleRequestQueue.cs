using System;
using System.Collections.Generic;
using System.Linq;

namespace Backup.Desktop
{
    public sealed class ScheduleRequest
    {
        public string RequestId, ScheduleId;
        public static ScheduleRequest Create(string schedule)
        {
            var value = new ScheduleRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                ScheduleId = schedule
            };
            value.Validate();
            return value;
        }

        public void Validate()
        {
            Guid id;
            if (!Guid.TryParseExact(RequestId, "N", out id) || !Guid.TryParseExact(ScheduleId, "N", out id))
                throw new ArgumentException("计划请求编号无效。");
        }
    }

    public sealed class ScheduleQueueState
    {
        public int Version = 1;
        public List<ScheduleRequest> Pending = new List<ScheduleRequest>();
        public List<string> Completed = new List<string>();
    }

    internal sealed class ScheduleRequestQueue
    {
        internal const string SettingKey = "schedule_request_queue_v1";
        private readonly object gate = new object();
        private List<ScheduleRequest> pending;
        private List<string> completed;
        private HashSet<string> recent;
        private readonly Action<string> write;
        private string saved;
        private bool closing, sealedQueue;
        internal ScheduleRequestQueue(string json, Action<string> persist)
        {
            write = persist;
            saved = json;
            var state = string.IsNullOrWhiteSpace(json) 
                ? new ScheduleQueueState() 
                : json.TrimStart().StartsWith("[", StringComparison.Ordinal) 
                ? new ScheduleQueueState
            {
                Pending = JsonSettings.Decode(json, new List<ScheduleRequest>())
            }

                : JsonSettings.Decode(json, new ScheduleQueueState());
            if (state.Version != 1 || state.Pending == null || state.Completed == null)
                throw new ArgumentException("计划请求队列格式无效。");
            foreach (var request in state.Pending)
                request.Validate();
            foreach (string value in state.Completed)
            {
                Guid id;
                if (!Guid.TryParseExact(value, "N", out id))
                    throw new ArgumentException("计划去重记录无效。");
            }

            completed = state.Completed.Distinct().Reverse().Take(256).Reverse().ToList();
            recent = new HashSet<string>(completed);
            pending = state.Pending.Where(request => !recent.Contains(request.RequestId)).GroupBy(request => request.RequestId).Select(group => Copy(group.First())).ToList();
        }

        private static ScheduleRequest Copy(ScheduleRequest request)
        {
            return new ScheduleRequest
            {
                RequestId = request.RequestId,
                ScheduleId = request.ScheduleId
            };
        }

        private void Commit(List<ScheduleRequest> next, List<string> done)
        {
            string json = JsonSettings.Encode(new ScheduleQueueState { Pending = next, Completed = done });
            if (json != saved)
            {
                write(json);
                saved = json;
            }

            pending = next;
            completed = done;
            recent = new HashSet<string>(done);
        }

        internal int Count
        {
            get
            {
                lock (gate)
                    return pending.Count;
            }
        }

        internal bool ContainsPending(string id)
        {
            lock (gate)
                return pending.Any(request => request.RequestId == id);
        }

        internal bool Enqueue(ScheduleRequest request)
        {
            request.Validate();
            lock (gate)
            {
                if (sealedQueue)
                    return false;
                if (recent.Contains(request.RequestId) || pending.Any(value => value.RequestId == request.RequestId))
                    return true;
                var next = new List<ScheduleRequest>(pending);
                next.Add(Copy(request));
                // Commit before acknowledging the pipe request or publishing it to RAM.
                Commit(next, new List<string>(completed));
                return true;
            }
        }

        internal ScheduleRequest Peek()
        {
            lock (gate)
                return closing || sealedQueue || pending.Count == 0 ? null : Copy(pending[0]);
        }

        internal void Complete(string requestId)
        {
            lock (gate)
            {
                if (closing || sealedQueue || !pending.Any(request => request.RequestId == requestId))
                    return;
                var next = pending.Where(request => request.RequestId != requestId).ToList();
                var done = completed.Where(id => id != requestId).Concat(new[] { requestId }).Reverse().Take(256).Reverse().ToList();
                // A failed write leaves the accepted request pending for recovery.
                Commit(next, done);
            }
        }

        internal void Checkpoint()
        {
            lock (gate)
                Commit(new List<ScheduleRequest>(pending), new List<string>(completed));
        }

        internal void BeginClosing()
        {
            lock (gate)
            {
                Checkpoint();
                closing = true;
            }
        }

        internal void CancelClosing()
        {
            lock (gate)
                if (!sealedQueue)
                    closing = false;
        }

        internal void Seal()
        {
            lock (gate)
            {
                Checkpoint();
                sealedQueue = true;
            }
        }
    }
}
