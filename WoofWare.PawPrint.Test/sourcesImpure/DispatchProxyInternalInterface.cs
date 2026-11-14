using System;
using System.Reflection;

// `DispatchProxy.Create` over an *internal* interface. `DispatchProxyGenerator` asks `IsVisible` of
// the interface, finds it false, and so defines `IgnoresAccessChecksToAttribute` in its dynamic
// assembly and applies it there, naming this assembly, so that the proxy type may implement the
// interface. Measured on .NET 10 and 11 to exit 0, with the attribute on the proxy's assembly.
//
// A wrong `IsVisible` of true skips the attribute; the proxy might still run under an interpreter
// that does not check a dynamic type's access to the interface, so the guest checks the attribute
// itself (check 4).
//
// Returns 0 on success, or the number of the first check that failed.

internal interface ICalculator
{
    int Add (int a, int b);
    string Describe (string prefix, int n);
}

public class CalculatorProxy : DispatchProxy
{
    protected override object Invoke (MethodInfo targetMethod, object[] args)
    {
        switch (targetMethod.Name)
        {
            case "Add":
                return (int) args[0] + (int) args[1];
            case "Describe":
                return (string) args[0] + ":" + (int) args[1];
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

        if (calc.Describe ("pre", 42) != "pre:42")
        {
            return 3;
        }

        string ownName = typeof (Program).Assembly.GetName ().Name;
        bool found = false;

        foreach (CustomAttributeData attribute in calc.GetType ().Assembly.GetCustomAttributesData ())
        {
            if (attribute.AttributeType.FullName == "System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute"
                && attribute.ConstructorArguments.Count == 1
                && (string) attribute.ConstructorArguments[0].Value == ownName)
            {
                found = true;
            }
        }

        if (!found)
        {
            return 4;
        }

        return 0;
    }
}
