using System;
using System.Diagnostics;
using System.Reflection;

// The runtime runs the entry type's static constructor from Main's prologue, so a stack trace
// taken inside it has the static constructor innermost and Main outermost. Between them real
// .NET has frames of its own class-initialisation helpers, which this does not check.
//
// Exit codes: 0 when the trace has that shape; otherwise the number of the first check that
// failed.
class Program
{
    static readonly int s_result;

    static Program()
    {
        var trace = new StackTrace();
        if (trace.FrameCount < 2)
        {
            s_result = 1;
            return;
        }

        MethodBase innermost = trace.GetFrame(0).GetMethod();
        if (innermost == null || innermost.Name != ".cctor" || innermost.DeclaringType != typeof(Program))
        {
            s_result = 2;
            return;
        }

        MethodBase outermost = trace.GetFrame(trace.FrameCount - 1).GetMethod();
        if (outermost == null || outermost.Name != "Main" || outermost.DeclaringType != typeof(Program))
        {
            s_result = 3;
            return;
        }

        s_result = 0;
    }

    static int Main(string[] args)
    {
        return s_result;
    }
}
