using System;
using System.Collections.Generic;
using System.Threading;

namespace Backup.Core
{
    // A cache reuses at most its original eight loader threads. Payload network
    // concurrency is still exclusively controlled by TransferScheduler.
    internal sealed class BackgroundReadQueue
    {
        private readonly object gate = new object();
        private readonly Queue<Action> pending = new Queue<Action>();
        private int workers, waiting;
        private bool closed;
        internal void Enqueue(Action action)
        {
            lock (gate)
            {
                if (closed)
                    throw new ObjectDisposedException("BackgroundReadQueue");
                if (pending.Count + 1 > waiting && workers < 8)
                {
                    workers++;
                    try
                    {
                        new Thread(Work)
                        {
                            IsBackground = true,
                            Name = "Backup volume reader"
                        }.Start();
                    }
                    catch (Exception error)
                    {
                        RuntimeDiagnostics.Record("BackgroundReadQueue:start", error);
                        workers--;
                        throw;
                    }
                }

                pending.Enqueue(action);
                Monitor.PulseAll(gate);
            }
        }

        private void Work()
        {
            while (true)
            {
                Action action;
                lock (gate)
                {
                    while (pending.Count == 0 && !closed)
                    {
                        waiting++;
                        try
                        {
                            Monitor.Wait(gate);
                        }
                        finally
                        {
                            waiting--;
                        }
                    }

                    if (pending.Count == 0)
                    {
                        workers--;
                        return;
                    }

                    action = pending.Dequeue();
                }

                try
                {
                    action();
                }
                catch (Exception error)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Core/BackgroundReadQueue.cs:Work", error);
                    System.Diagnostics.Trace.TraceError("BackgroundReadQueue: " + error);
                }
            }
        }

        internal void Complete()
        {
            lock (gate)
            {
                closed = true;
                Monitor.PulseAll(gate);
            }
        }
    }
}
