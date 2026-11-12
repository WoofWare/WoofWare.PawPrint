using System.Reflection;

// `MethodInfo.Invoke` of a virtual method whose own row names no body (`extern` with no
// `DllImport`, so neither IL nor an implementation flag), on an instance of a type overriding it.
// Reflection dispatches virtually before anything runs, so the override's body runs and the
// declaration's missing one is never asked for: real .NET returns the override's result.
//
// The exit code is the index of the first check that failed; 0 means all passed.
public class Base
{
#pragma warning disable CS0626
    public virtual extern int M ();
#pragma warning restore CS0626
}

public class Derived : Base
{
    public override int M () => 7;
}

public class Program
{
    public static int Main (string[] args)
    {
        MethodInfo m = typeof (Base).GetMethod ("M");
        if ((int) m.Invoke (new Derived (), null) != 7) return 1;
        if ((int) m.Invoke (new Derived (), null) != 7) return 2;
        return 0;
    }
}
