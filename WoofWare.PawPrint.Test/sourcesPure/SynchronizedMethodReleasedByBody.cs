using System;
using System.Runtime.CompilerServices;
using System.Threading;

// A synchronized method whose body releases its own monitor. The runtime's exit helper before the
// `ret` then finds the monitor not held by the thread, and raises SynchronizationLockException,
// which leaves the method for its caller.
class Holder
{
    public bool ReleasedInBody;

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void ReleaseOwnMonitor()
    {
        Monitor.Exit(this);
        ReleasedInBody = !Monitor.IsEntered(this);
    }
}

class Program
{
    static int Main(string[] args)
    {
        var h = new Holder();
        try
        {
            h.ReleaseOwnMonitor();
            return 1;
        }
        catch (SynchronizationLockException)
        {
        }

        if (!h.ReleasedInBody)
        {
            return 2;
        }

        if (Monitor.IsEntered(h))
        {
            return 3;
        }

        return 0;
    }
}
