using System;

// The entry type's static constructor throws. The runtime runs it from Main's prologue, so the
// TypeInitializationException escapes Main unhandled, and none of Main's body runs.
class Program
{
    static Program()
    {
        throw new InvalidOperationException("thrown by the entry type's static constructor");
    }

    static int Main(string[] args)
    {
        return 1;
    }
}
