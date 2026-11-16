using System;
using System.Runtime.CompilerServices;
using System.Threading;

// A synchronized method whose body releases its own monitor and then throws. As that exception
// leaves the body, the fault handler the JIT wraps round it calls the runtime's exit helper, which
// finds the monitor not held by the thread and raises SynchronizationLockException in its place.
// The helper's call is outside every clause of the method's own, although the method's program
// counter is then inside them: its catch does not see the new exception, and its finally, which
// ran once as the first exception left, does not run again.
class Holder
{
    public int FinallyRuns;

    [MethodImpl(MethodImplOptions.Synchronized)]
    public int ReleaseThenThrow()
    {
        try
        {
            try
            {
                Monitor.Exit(this);
                throw new InvalidOperationException();
            }
            finally
            {
                FinallyRuns++;
            }
        }
        catch (SynchronizationLockException)
        {
            return 1;
        }
    }
}

class Program
{
    static int Main(string[] args)
    {
        var h = new Holder();
        try
        {
            h.ReleaseThenThrow();
            return 2;
        }
        catch (SynchronizationLockException)
        {
        }
        catch (InvalidOperationException)
        {
            return 3;
        }

        if (h.FinallyRuns != 1)
        {
            return 4;
        }

        if (Monitor.IsEntered(h))
        {
            return 5;
        }

        return 0;
    }
}
