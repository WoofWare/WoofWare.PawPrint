using System;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;

public class Program
{
    // The `MethodBase` of a captured frame belonging to a `Reflection.Emit` method is the
    // `DynamicMethod` object itself. `StackFrameHelper.GetMethodBase` reaches
    // `RuntimeType.GetMethodBase`, which for a method with no metadata asks
    // `RuntimeMethodHandle.GetResolver` for the `DynamicResolver` the method was created with, and
    // answers that resolver's `GetDynamicMethod()`.
    //
    // `new StackTrace(e)` passes `fNeedFileInfo: false`, so nothing here looks for symbols.
    //
    // Returns 0 on success, or the number of the first check that failed.

    public static int Main(string[] args)
    {
        // `ldarg.0; throw`: token-free, so the exception is chosen by the caller (see
        // DynamicMethodStackTrace.cs).
        DynamicMethod thrower =
            new DynamicMethod("Thrower", typeof(int), new Type[] { typeof(object) }, typeof(Program).Module);
        ILGenerator il = thrower.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Throw);

        Func<object, int> f = (Func<object, int>) thrower.CreateDelegate(typeof(Func<object, int>));

        StackTrace trace;

        try
        {
            f(new InvalidOperationException("raised from a dynamic method"));
            return 1;
        }
        catch (InvalidOperationException e)
        {
            trace = new StackTrace(e);
        }

        if (trace.FrameCount != 2) return 2;

        MethodBase inner = trace.GetFrame(0).GetMethod();
        if (inner == null) return 3;
        if (!ReferenceEquals(inner, thrower)) return 4;
        if (inner.DeclaringType != null) return 5;
        if (inner.Name != "Thrower") return 6;

        ParameterInfo[] parameters = inner.GetParameters();
        if (parameters.Length != 1) return 7;
        if (parameters[0].ParameterType != typeof(object)) return 8;

        MethodBase outer = trace.GetFrame(1).GetMethod();
        if (outer == null) return 9;
        if (outer.Name != "Main") return 10;
        if (outer.DeclaringType != typeof(Program)) return 11;

        // A second capture of the same frame answers the same object.
        StackTrace again;
        try
        {
            f(new InvalidOperationException("again"));
            return 12;
        }
        catch (InvalidOperationException e)
        {
            again = new StackTrace(e);
        }

        if (!ReferenceEquals(again.GetFrame(0).GetMethod(), thrower)) return 13;

        return 0;
    }
}
