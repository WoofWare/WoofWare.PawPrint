using System.Runtime.CompilerServices;
using System.Threading;

// A synchronized entry point holds its type's monitor, as any synchronized static method does,
// however the runtime enters it.
class Program
{
    [MethodImpl(MethodImplOptions.Synchronized)]
    static int Main(string[] args)
    {
        return Monitor.IsEntered(typeof(Program)) ? 0 : 1;
    }
}
