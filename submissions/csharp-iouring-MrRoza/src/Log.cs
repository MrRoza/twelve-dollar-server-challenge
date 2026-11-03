// Trace logging that only exists when built with LOG=1 (TRACE_LOG). Without it, [Conditional] makes the
// compiler drop every call site *including its arguments*, so string formatting costs nothing.
using System.Diagnostics;

namespace Twelve;

static class Log
{
    [Conditional("TRACE_LOG")]
    public static void Trace(string msg) => Console.Error.WriteLine(msg);

    /// <summary>Always on: startup, fatal errors and rare events only.</summary>
    public static void Info(string msg) => Console.Error.WriteLine(msg);
}
