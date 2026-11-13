using System;
using System.Runtime.CompilerServices;

// A synchronized entry point whose type's static constructor throws. The runtime takes the type's
// monitor, runs the initialiser from Main's prologue inside the region the monitor guards, and
// releases the monitor as the TypeInitializationException leaves Main unhandled; none of Main's
// body runs.
class Program
{
    static Program()
    {
        throw new InvalidOperationException("thrown by the entry type's static constructor");
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    static int Main(string[] args)
    {
        return 1;
    }
}
