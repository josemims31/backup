using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Backup.Core
{
    // Supplemental diagnostics never alter cancellation, cleanup or native
    // callback results. Repeated failures are bounded by context and type.
    public static class RuntimeDiagnostics
    {
        private static readonly object gate = new object();
        private static readonly Dictionary<string, DateTime> reported = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private static Action<string, Exception> writer;
        [ThreadStatic]
        private static bool writing;
        public static void Configure(Action<string, Exception> sink)
        {
            lock (gate)
            {
                writer = sink;
                reported.Clear();
            }
        }

        public static void Record(string context, Exception error)
        {
            if (error == null || error is OperationCanceledException || writing)
                return;
            writing = true;
            try
            {
                Action<string, Exception> sink;
                string key = context + "|" + error.GetType().FullName;
                DateTime now = DateTime.UtcNow;
                lock (gate)
                {
                    DateTime previous;
                    if (reported.TryGetValue(key, out previous) && now - previous < TimeSpan.FromSeconds(30))
                        return;
                    if (reported.Count >= 256)
                        reported.Clear();
                    reported[key] = now;
                    sink = writer;
                }

                if (sink != null)
                    sink(context, error);
                else
                    TraceFallback(context, error);
            }
            catch (Exception failure)
            {
                TraceFallback("Diagnostic writer", failure);
            }
            finally
            {
                writing = false;
            }
        }

        private static void TraceFallback(string context, Exception error)
        {
            foreach (TraceListener listener in Trace.Listeners)
                try
                {
                    listener.WriteLine(context + ": " + error);
                }
                catch (Exception failure)
                {
                    Debugger.Log(0, "Backup diagnostics", failure.Message);
                }
        }
    }
}
