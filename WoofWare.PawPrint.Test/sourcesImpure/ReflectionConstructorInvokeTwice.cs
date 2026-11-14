using System;
using System.Reflection;

public sealed class Counter
{
    public static int Constructed;

    public readonly int Value;
    public readonly int Doubled;

    public Counter(int value)
    {
        Value = value;
        Doubled = value * 2;
        Constructed++;
    }
}

public class Program
{
    // `ConstructorInfo.Invoke` through one `ConstructorInfo`, twice, with dynamic code supported.
    //
    // CoreLib's reflection invoker runs a constructor's first reflective invocation through the
    // runtime's own invoker, and from the second onwards through a `DynamicMethod` it emits
    // (`InvokerEmitUtil`), whose body constructs the object with `newobj` of the reflected
    // constructor. The constructor is this guest's own, so that which CoreLib type the invoker
    // constructs cannot change what this guest exercises.
    //
    // The test harness registers this case with the dynamic-code switch set to "true", which is
    // also real .NET's default.
    //
    // Returns 0 on success, or the number of the first check that failed.
    public static int Main(string[] args)
    {
        ConstructorInfo ctor = typeof(Counter).GetConstructor(new[] { typeof(int) });

        if (ctor == null)
        {
            return 1;
        }

        object first = ctor.Invoke(new object[] { 3 });
        object second = ctor.Invoke(new object[] { 7 });

        Counter a = first as Counter;

        if (a == null)
        {
            return 2;
        }

        Counter b = second as Counter;

        if (b == null)
        {
            return 3;
        }

        if (ReferenceEquals(a, b))
        {
            return 4;
        }

        if (a.Value != 3 || a.Doubled != 6)
        {
            return 5;
        }

        if (b.Value != 7 || b.Doubled != 14)
        {
            return 6;
        }

        if (Counter.Constructed != 2)
        {
            return 7;
        }

        return 0;
    }
}
