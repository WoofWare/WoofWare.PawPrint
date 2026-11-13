using System.Runtime.CompilerServices;
using System.Threading;

// A synchronized entry point whose type has a static constructor: the runtime runs the type
// initialiser from Main's prologue, inside the region the type's monitor guards, so the
// initialiser runs holding it.
class Program
{
    static readonly bool HeldInInitialiser;

    static Program()
    {
        HeldInInitialiser = Monitor.IsEntered(typeof(Program));
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    static int Main(string[] args)
    {
        return HeldInInitialiser ? 0 : 1;
    }
}
