using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

// Rows that CoreCLR's emitter validates and keeps, but that `GetCustomAttributes` cannot read back: a `Guid` and a
// `TypeLibVersion` followed by a named-argument count of -1, which the emitter reads as signed and so as no named
// arguments, and which the reader reads as unsigned, 65535 named arguments that are not there; and an
// `AllowPartiallyTrustedCallers` whose blob is `FF FF`, which the emitter never parses. Real .NET throws
// `CustomAttributeFormatException` for each. Measured on .NET 10 and 11 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    static bool ThrowsFormat (Func<object[]> read)
    {
        try
        {
            read ();
            return false;
        }
        catch (CustomAttributeFormatException)
        {
            return true;
        }
    }

    static byte[] Cat (byte[] a, byte[] b, byte[] c)
    {
        var result = new byte[a.Length + b.Length + c.Length];
        a.CopyTo (result, 0);
        b.CopyTo (result, a.Length);
        c.CopyTo (result, a.Length + b.Length);
        return result;
    }

    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Unreadable"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("Unreadable");
        TypeBuilder t = module.DefineType ("T", TypeAttributes.Public);
        byte[] guid = Cat (new byte[] { 1, 0, 36 }, Encoding.ASCII.GetBytes ("01234567-89ab-cdef-0123-456789abcdef"), new byte[] { 0xFF, 0xFF });
        t.SetCustomAttribute (typeof (GuidAttribute).GetConstructor (new[] { typeof (string) }), guid);
        Type created = t.CreateType ();
        if (!ThrowsFormat (() => created.GetCustomAttributes (false))) return 1;

        AssemblyBuilder tlv = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("UnreadableVersion"), AssemblyBuilderAccess.Run);
        tlv.SetCustomAttribute (
            typeof (TypeLibVersionAttribute).GetConstructor (new[] { typeof (int), typeof (int) }),
            new byte[] { 1, 0, 1, 0, 0, 0, 2, 0, 0, 0, 0xFF, 0xFF });
        if (!ThrowsFormat (() => tlv.GetCustomAttributes (false))) return 2;

        AssemblyBuilder aptca = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("UnreadableTrust"), AssemblyBuilderAccess.Run);
        aptca.SetCustomAttribute (typeof (AllowPartiallyTrustedCallersAttribute).GetConstructor (Type.EmptyTypes), new byte[] { 0xFF, 0xFF });
        if (!ThrowsFormat (() => aptca.GetCustomAttributes (false))) return 3;
        return 0;
    }
}
