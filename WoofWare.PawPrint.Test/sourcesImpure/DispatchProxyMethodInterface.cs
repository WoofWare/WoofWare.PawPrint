using System;
using System.Reflection;

// `DispatchProxy.Create` over an interface with methods only: `System.Reflection.DispatchProxy`,
// the framework's own consumer of `TypeBuilder`, emits a type implementing `ICalculator` whose
// every method forwards to
// `CalculatorProxy.Invoke`, which computes its answer from the `MethodInfo` and arguments it is
// handed. Measured on .NET 10 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public interface ICalculator
{
    int Add (int a, int b);
    string Describe (string prefix, int n);
    long Combine (long a, int b, short c);
    void Record (int x);
}

public class CalculatorProxy : DispatchProxy
{
    public static int Recorded;
    public static int Calls;

    protected override object Invoke (MethodInfo targetMethod, object[] args)
    {
        Calls++;

        if (targetMethod.DeclaringType != typeof (ICalculator))
        {
            throw new InvalidOperationException ("unexpected declaring type " + targetMethod.DeclaringType);
        }

        if (targetMethod.GetParameters ().Length != args.Length)
        {
            throw new InvalidOperationException ("argument count mismatch for " + targetMethod.Name);
        }

        switch (targetMethod.Name)
        {
            case "Add":
                return (int) args[0] + (int) args[1];
            case "Describe":
                return (string) args[0] + ":" + targetMethod.Name + ":" + (int) args[1];
            case "Combine":
                return (long) args[0] * 1000 + (int) args[1] * 10 + (short) args[2];
            case "Record":
                Recorded = (int) args[0];
                return null;
            default:
                throw new InvalidOperationException ("unexpected method " + targetMethod.Name);
        }
    }
}

public static class Program
{
    public static int Main ()
    {
        ICalculator calc = DispatchProxy.Create<ICalculator, CalculatorProxy> ();

        if (!(calc is CalculatorProxy))
        {
            return 1;
        }

        if (calc.Add (3, 4) != 7)
        {
            return 2;
        }

        if (calc.Add (-10, 4) != -6)
        {
            return 3;
        }

        if (calc.Describe ("pre", 42) != "pre:Describe:42")
        {
            return 4;
        }

        if (calc.Combine (5L, 6, (short) 7) != 5067L)
        {
            return 5;
        }

        calc.Record (99);

        if (CalculatorProxy.Recorded != 99)
        {
            return 6;
        }

        if (CalculatorProxy.Calls != 5)
        {
            return 7;
        }

        return 0;
    }
}
