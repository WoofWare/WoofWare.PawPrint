// `Monitor.Exit` by a thread that does not own the monitor raises `SynchronizationLockException`,
// whether the monitor is free or held by another thread, and leaves the monitor as it was.
//
// The worker runs entirely between the main thread's Start and Join, inside the main thread's
// `lock`, so nothing here depends on scheduling order.
using System.Threading;

public static class MonitorExitNotOwned
{
    // COR_E_SYNCHRONIZATIONLOCK
    const int ExpectedHResult = unchecked((int)0x80131518);

    static readonly object held = new object ();

    static int workerResult = -1;

    static void Worker ()
    {
        try
        {
            Monitor.Exit (held);
            workerResult = 1;
        }
        catch (SynchronizationLockException e)
        {
            workerResult = e.HResult == ExpectedHResult ? 0 : 2;
        }
    }

    public static int Main (string[] args)
    {
        object free = new object ();

        try
        {
            Monitor.Exit (free);
            return 1;
        }
        catch (SynchronizationLockException e)
        {
            if (e.HResult != ExpectedHResult)
            {
                return 2;
            }
        }

        // The failed exit left the monitor usable.
        lock (free)
        {
            if (!Monitor.IsEntered (free))
            {
                return 3;
            }
        }

        lock (held)
        {
            Thread t = new Thread (Worker);
            t.Start ();
            t.Join ();

            if (workerResult != 0)
            {
                return 10 + workerResult;
            }

            // The worker's failed exit did not release the main thread's hold.
            if (!Monitor.IsEntered (held))
            {
                return 4;
            }
        }

        if (Monitor.IsEntered (held))
        {
            return 5;
        }

        return 0;
    }
}
