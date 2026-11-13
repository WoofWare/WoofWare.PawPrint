using System;
using System.Runtime.CompilerServices;

// An exception nothing catches leaves a synchronized method: the run ends with it unhandled.
class Program
{
    [MethodImpl(MethodImplOptions.Synchronized)]
    static void Throws()
    {
        throw new InvalidOperationException("unhandled, from a synchronized method");
    }

    static void Main(string[] args)
    {
        Throws();
    }
}
