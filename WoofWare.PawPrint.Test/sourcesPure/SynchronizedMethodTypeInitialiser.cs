using System;
using System.Runtime.CompilerServices;
using System.Threading;

// The first call of a synchronized static method of a type with a static constructor takes the
// type's monitor before the type initialiser runs: CoreCLR's JIT puts its initialisation check
// inside the region it guards with the monitor. So the initialiser runs holding the monitor, a
// caller's filter sees it held while a failed initialiser's exception leaves the method, and the
// caller's catch sees it released.
class Initialised
{
    public static readonly bool HeldInInitialiser;

    static Initialised()
    {
        HeldInInitialiser = Monitor.IsEntered(typeof(Initialised));
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public static bool Body()
    {
        return HeldInInitialiser && Monitor.IsEntered(typeof(Initialised));
    }
}

class FailsToInitialise
{
    public static readonly int Never;

    static FailsToInitialise()
    {
        throw new InvalidOperationException("from a type initialiser");
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public static void Body()
    {
    }
}

class Program
{
    static int Main(string[] args)
    {
        if (!Initialised.Body() || Monitor.IsEntered(typeof(Initialised)))
        {
            return 1;
        }

        bool filterSawHeld = false;
        bool catchSawHeld = true;
        try
        {
            FailsToInitialise.Body();
            return 2;
        }
        catch (TypeInitializationException) when (filterSawHeld = Monitor.IsEntered(typeof(FailsToInitialise)))
        {
            catchSawHeld = Monitor.IsEntered(typeof(FailsToInitialise));
        }

        if (!filterSawHeld)
        {
            return 3;
        }

        if (catchSawHeld || Monitor.IsEntered(typeof(FailsToInitialise)))
        {
            return 4;
        }

        return 0;
    }
}
