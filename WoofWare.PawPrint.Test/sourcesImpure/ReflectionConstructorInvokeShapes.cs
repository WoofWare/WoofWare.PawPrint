using System;
using System.Reflection;

// Each shape of constructor that reflection invokes through an emitted invoke stub, invoked three
// times with a different argument each time.
//
// CoreLib's reflection invoker runs a constructor's first reflective invocation through the
// runtime's own invoker, and from the second onwards through a `DynamicMethod` it emits
// (`InvokerEmitUtil`) when dynamic code is supported. That stub's body pushes the arguments,
// `newobj`s the reflected constructor and boxes a value type's result. So the second and third
// invocations of each constructor below run the stub: through `Obj4Args` for up to four arguments,
// `ObjSpanArgs` for five, and through `ConstructorInvoker` and `Activator.CreateInstance` as well
// as `ConstructorInfo.Invoke`. Each section uses a constructor no other section has invoked, so
// that each starts from the runtime's invoker. Measured on .NET 10 and 11 to exit 0.
//
// The test harness registers this case with the dynamic-code switch set to "true", which is also
// real .NET's default.
//
// Returns 0 on success, or the number of the first check that failed.

public sealed class OneInt
{
    public readonly int V;

    public OneInt (int v)
    {
        V = v;
    }
}

public struct Pair
{
    public int V;
    public long W;

    public Pair (int v)
    {
        V = v;
        W = v * 10L;
    }
}

public sealed class Gen<T>
{
    public readonly T V;

    public Gen (T v)
    {
        V = v;
    }
}

public sealed class Five
{
    public readonly int Sum;

    public Five (int a, int b, int c, int d, int e)
    {
        Sum = a + 10 * b + 100 * c + 1000 * d + 10000 * e;
    }
}

public sealed class Throws
{
    public Throws (int x)
    {
        throw new InvalidOperationException ("boom " + x);
    }
}

public sealed class WithCctor
{
    public static string Log = "";

    static WithCctor ()
    {
        Log += "cctor;";
    }

    public WithCctor (int x)
    {
        Log += "ctor" + x + ";";
    }
}

public sealed class ByInvoker
{
    public readonly int V;

    public ByInvoker (int v)
    {
        V = v;
    }
}

public sealed class ByActivator
{
    public readonly int V;

    public ByActivator (int v)
    {
        V = v;
    }
}

public static class Program
{
    sealed class Hidden
    {
        public readonly int V;

        private Hidden (int v)
        {
            V = v;
        }
    }

    // Invokes `ctor` three times, with `args (i)` for i = 1, 2, 3, and returns `check + i` for the
    // first invocation whose result `ok (i, result)` rejects, or 0.
    static int Thrice (int check, ConstructorInfo? ctor, Func<int, object[]> args, Func<int, object, bool> ok)
    {
        if (ctor is null)
        {
            return check;
        }

        for (int i = 1; i <= 3; i++)
        {
            if (!ok (i, ctor.Invoke (args (i))))
            {
                return check + i;
            }
        }

        return 0;
    }

    public static int Main ()
    {
        int r = Thrice (10, typeof (OneInt).GetConstructor (new[] { typeof (int) }), i => new object[] { i },
            (i, o) => o is OneInt c && c.V == i);

        if (r != 0)
        {
            return r;
        }

        r = Thrice (20, typeof (Pair).GetConstructor (new[] { typeof (int) }), i => new object[] { i },
            (i, o) => o is Pair p && p.V == i && p.W == i * 10L);

        if (r != 0)
        {
            return r;
        }

        r = Thrice (30, typeof (Gen<int>).GetConstructor (new[] { typeof (int) }), i => new object[] { 100 + i },
            (i, o) => o is Gen<int> g && g.V == 100 + i);

        if (r != 0)
        {
            return r;
        }

        r = Thrice (40, typeof (Gen<string>).GetConstructor (new[] { typeof (string) }), i => new object[] { "s" + i },
            (i, o) => o is Gen<string> g && g.V == "s" + i);

        if (r != 0)
        {
            return r;
        }

        r = Thrice (50,
            typeof (Five).GetConstructor (new[] { typeof (int), typeof (int), typeof (int), typeof (int), typeof (int) }),
            i => new object[] { i, 2, 3, 4, 5 },
            (i, o) => o is Five f && f.Sum == 54320 + i);

        if (r != 0)
        {
            return r;
        }

        ConstructorInfo? throws = typeof (Throws).GetConstructor (new[] { typeof (int) });

        if (throws is null)
        {
            return 60;
        }

        for (int i = 1; i <= 3; i++)
        {
            try
            {
                throws.Invoke (new object[] { i });
                return 60 + i;
            }
            catch (TargetInvocationException e)
            {
                if (!(e.InnerException is InvalidOperationException inner) || inner.Message != "boom " + i)
                {
                    return 63 + i;
                }
            }
        }

        r = Thrice (70, typeof (WithCctor).GetConstructor (new[] { typeof (int) }), i => new object[] { i },
            (i, o) => o is WithCctor);

        if (r != 0)
        {
            return r;
        }

        if (WithCctor.Log != "cctor;ctor1;ctor2;ctor3;")
        {
            return 74;
        }

        r = Thrice (80,
            typeof (Hidden).GetConstructor (BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof (int) }, null),
            i => new object[] { i },
            (i, o) => o is Hidden h && h.V == i);

        if (r != 0)
        {
            return r;
        }

        ConstructorInfo? byInvoker = typeof (ByInvoker).GetConstructor (new[] { typeof (int) });

        if (byInvoker is null)
        {
            return 90;
        }

        ConstructorInvoker invoker = ConstructorInvoker.Create (byInvoker);

        for (int i = 1; i <= 3; i++)
        {
            if (!(invoker.Invoke (i) is ByInvoker b) || b.V != i)
            {
                return 90 + i;
            }
        }

        for (int i = 1; i <= 3; i++)
        {
            if (!(Activator.CreateInstance (typeof (ByActivator), i) is ByActivator a) || a.V != i)
            {
                return 100 + i;
            }
        }

        return 0;
    }
}
