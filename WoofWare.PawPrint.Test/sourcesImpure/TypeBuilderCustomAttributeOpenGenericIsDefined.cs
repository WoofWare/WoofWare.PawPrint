using System;
using System.Reflection;
using System.Reflection.Emit;

// An emitted row whose constructor is one of an open generic attribute type, `GenAttribute<>`, which CoreCLR's emitter
// stores as it stores any other. Every read of the owner then fails: `IsDefined`, of any attribute type, throws
// `ArgumentException` while resolving the row's type without a generic context. Measured on .NET 10 and 11 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

[AttributeUsage (AttributeTargets.All)]
public class GenAttribute<T> : Attribute
{
    public GenAttribute (int n) { N = n; }
    public int N;
}

[AttributeUsage (AttributeTargets.All)]
public class TagAttribute : Attribute
{
}

public static class Program
{
    static bool ThrowsArgumentException (Func<bool> read)
    {
        try
        {
            read ();
            return false;
        }
        catch (Exception e)
        {
            return e.GetType () == typeof (ArgumentException);
        }
    }

    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("OpenGeneric"), AssemblyBuilderAccess.Run);
        TypeBuilder t = assembly.DefineDynamicModule ("OpenGeneric").DefineType ("T", TypeAttributes.Public);
        t.SetCustomAttribute (typeof (GenAttribute<>).GetConstructors ()[0], new byte[] { 1, 0, 1, 0, 0, 0, 0, 0 });
        Type tt = t.CreateType ();

        if (!ThrowsArgumentException (() => tt.IsDefined (typeof (TagAttribute), false)))
        {
            return 1;
        }

        if (!ThrowsArgumentException (() => tt.IsDefined (typeof (GenAttribute<>), false)))
        {
            return 2;
        }

        return 0;
    }
}
