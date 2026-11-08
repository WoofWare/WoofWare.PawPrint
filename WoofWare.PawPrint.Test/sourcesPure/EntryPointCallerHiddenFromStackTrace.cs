using System;
using System.Diagnostics;
using System.Reflection;

// What a live stack trace taken in `Main`, or below it, reports beneath `Main`: nothing, on
// either runtime.
//
// On .NET 10 the VM calls `Main` from native code. On .NET 11 it calls `Main` through CoreLib's
// `Environment.CallEntryPoint`, a managed frame that `Assembly.GetCallingAssembly` does see
// (`AssemblyGetCallingAssemblyFromEntryPoint.cs`), but which CoreCLR's capture leaves out
// (debugdebugger.cpp's `GetStackFramesCallback`). Measured on 10.0.12 and on 11.0.0-rc.1:
// `new StackFrame(1)` in `Main` names no method, and `new StackTrace()` ends at `Main`.
public class Program
{
    public static int Main (string[] args)
    {
        MethodBase beneathMain = new StackFrame (1).GetMethod ();
        if (beneathMain != null)
            return 1;

        StackTrace live = new StackTrace ();
        MethodBase outermost = live.GetFrame (live.FrameCount - 1).GetMethod ();
        if (outermost.Name != "Main" || outermost.DeclaringType != typeof (Program))
            return 2;

        StackTrace fromInner = InnerTrace ();
        MethodBase outermostFromInner = fromInner.GetFrame (fromInner.FrameCount - 1).GetMethod ();
        if (outermostFromInner.Name != "Main" || outermostFromInner.DeclaringType != typeof (Program))
            return 3;

        return 0;
    }

    static StackTrace InnerTrace ()
    {
        return new StackTrace ();
    }
}
