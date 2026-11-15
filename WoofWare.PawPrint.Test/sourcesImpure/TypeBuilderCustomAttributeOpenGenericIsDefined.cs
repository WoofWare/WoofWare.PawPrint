using System;
using System.Reflection;
using System.Reflection.Emit;

// An emitted row whose constructor is one of an open generic attribute type, `GenAttribute<>`, which CoreCLR's emitter
// stores as it stores any other: a MemberRef whose parent is the TypeSpec `GenAttribute<!0>`. Every read of the owner's
// attributes then fails, whatever attribute type it asks about.
//
// The queries that filter by attribute type resolve the row's type with `Module.ResolveType` and no generic context, and
// throw `ArgumentException` wrapping the `BadImageFormatException` of `ModuleHandle.ResolveType`. `GetCustomAttributesData`
// resolves the row's constructor with `ModuleHandle.ResolveMethodHandle` instead, and throws that
// `BadImageFormatException` unwrapped. Measured on .NET 10 and 11 to exit 0.
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
    const int BadImageFormatHResult = unchecked ((int)0x8007000B);

    static bool IsMissingContext (Exception e) =>
        e != null && e.GetType () == typeof (BadImageFormatException) && e.HResult == BadImageFormatHResult && e.InnerException == null;

    static bool ThrowsWrappedMissingContext (Func<object> read)
    {
        try
        {
            read ();
            return false;
        }
        catch (Exception e)
        {
            return e.GetType () == typeof (ArgumentException) && IsMissingContext (e.InnerException);
        }
    }

    static bool ThrowsMissingContext (Func<object> read)
    {
        try
        {
            read ();
            return false;
        }
        catch (Exception e)
        {
            return IsMissingContext (e);
        }
    }

    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("OpenGeneric"), AssemblyBuilderAccess.Run);
        TypeBuilder t = assembly.DefineDynamicModule ("OpenGeneric").DefineType ("T", TypeAttributes.Public);
        t.SetCustomAttribute (typeof (GenAttribute<>).GetConstructors ()[0], new byte[] { 1, 0, 1, 0, 0, 0, 0, 0 });
        Type tt = t.CreateType ();

        if (!ThrowsWrappedMissingContext (() => tt.IsDefined (typeof (TagAttribute), false)))
        {
            return 1;
        }

        if (!ThrowsWrappedMissingContext (() => tt.IsDefined (typeof (GenAttribute<>), false)))
        {
            return 2;
        }

        if (!ThrowsWrappedMissingContext (() => tt.IsDefined (typeof (TagAttribute), true)))
        {
            return 3;
        }

        if (!ThrowsWrappedMissingContext (() => tt.GetCustomAttributes (false)))
        {
            return 4;
        }

        if (!ThrowsWrappedMissingContext (() => tt.GetCustomAttributes (typeof (TagAttribute), false)))
        {
            return 5;
        }

        if (!ThrowsWrappedMissingContext (() => Attribute.GetCustomAttribute (tt, typeof (TagAttribute))))
        {
            return 6;
        }

        if (!ThrowsWrappedMissingContext (() => Attribute.IsDefined (tt, typeof (TagAttribute))))
        {
            return 7;
        }

        if (!ThrowsMissingContext (() => tt.GetCustomAttributesData ()))
        {
            return 8;
        }

        return 0;
    }
}
