using System;
using System.Reflection;
using System.Reflection.Emit;

// Emitted rows whose blobs CoreCLR's emitter stores as given and do not parse, instantiated through
// `GetCustomAttributes`: a bad prolog, a truncated string, and bytes after the arguments. Each is refused with
// `CustomAttributeFormatException`. Measured on .NET 10 and 11 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

[AttributeUsage (AttributeTargets.All)]
public class TagAttribute : Attribute
{
    public TagAttribute (string s) { S = s; }
    public string S;
}

public static class Program
{
    static bool ThrowsFormatException (Type t)
    {
        try
        {
            t.GetCustomAttributes (false);
            return false;
        }
        catch (Exception e)
        {
            return e.GetType () == typeof (CustomAttributeFormatException);
        }
    }

    static Type WithBlob (ModuleBuilder module, string name, byte[] blob)
    {
        TypeBuilder t = module.DefineType (name, TypeAttributes.Public);
        t.SetCustomAttribute (typeof (TagAttribute).GetConstructor (new[] { typeof (string) }), blob);
        return t.CreateType ();
    }

    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("MalformedInstance"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("MalformedInstance");

        if (!ThrowsFormatException (WithBlob (module, "BadProlog", new byte[] { 2, 0, 0, 0 })))
        {
            return 1;
        }

        if (!ThrowsFormatException (WithBlob (module, "Truncated", new byte[] { 1, 0, 9, (byte) 'a' })))
        {
            return 2;
        }

        if (!ThrowsFormatException (WithBlob (module, "Trailing", new byte[] { 1, 0, 1, (byte) 'a', 0, 0, 7, 7, 7 })))
        {
            return 3;
        }

        return 0;
    }
}
