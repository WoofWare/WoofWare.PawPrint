using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

// Rows that CoreCLR's emitter validates and keeps, but that `GetCustomAttributes` cannot read back, each alone on its
// owner. The emitter reads a named-argument count as signed and only when two bytes are left, reads a `U2` for the two
// enum-typed constructors, and never parses a blob of `AllowPartiallyTrustedCallers`; the reader reads the count as
// unsigned, the enum as four bytes, and every blob. Real .NET throws `CustomAttributeFormatException` for each:
// a `Guid` and a `TypeLibVersion` followed by the count `FF FF` (65535 named arguments that are not there); an
// `AllowPartiallyTrustedCallers` blob `FF FF`; `ClassInterface(ClassInterfaceType)` with `01 00 01 00 FF FF DE AD`
// (the count `DE AD`), with five or six bytes (the enum cut short), and with `01 00 01 00 00 00 DE AD BE EF` (the
// count `DE AD`, then two bytes that are no named argument); and a `Guid` followed by one byte. Measured on .NET 10
// and 11 to exit 0. The same rows are kept, and their data read, by `TypeBuilderValidatedPseudoAttributes.cs`.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    static readonly ConstructorInfo ClassInterface = typeof (ClassInterfaceAttribute).GetConstructor (new[] { typeof (ClassInterfaceType) });
    static readonly ConstructorInfo Guid = typeof (GuidAttribute).GetConstructor (new[] { typeof (string) });

    static int count;

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

    static byte[] GuidThen (params byte[] tail)
    {
        byte[] text = Encoding.ASCII.GetBytes ("01234567-89ab-cdef-0123-456789abcdef");
        var result = new byte[3 + text.Length + tail.Length];
        result[0] = 1;
        result[2] = 36;
        text.CopyTo (result, 3);
        tail.CopyTo (result, 3 + text.Length);
        return result;
    }

    /// Whether reading back the attributes of a type holding only this row throws as real .NET does.
    static bool TypeRowUnreadable (ConstructorInfo ctor, byte[] blob)
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Unreadable" + (count++)), AssemblyBuilderAccess.Run);
        TypeBuilder t = assembly.DefineDynamicModule ("M").DefineType ("T", TypeAttributes.Public);
        t.SetCustomAttribute (ctor, blob);
        Type created = t.CreateType ();
        return ThrowsFormat (() => created.GetCustomAttributes (false));
    }

    /// Whether reading back the attributes of an assembly holding only this row throws as real .NET does.
    static bool AssemblyRowUnreadable (ConstructorInfo ctor, byte[] blob)
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Unreadable" + (count++)), AssemblyBuilderAccess.Run);
        assembly.SetCustomAttribute (ctor, blob);
        return ThrowsFormat (() => assembly.GetCustomAttributes (false));
    }

    public static int Main ()
    {
        if (!TypeRowUnreadable (Guid, GuidThen (0xFF, 0xFF))) return 1;
        ConstructorInfo tlv = typeof (TypeLibVersionAttribute).GetConstructor (new[] { typeof (int), typeof (int) });
        if (!AssemblyRowUnreadable (tlv, new byte[] { 1, 0, 1, 0, 0, 0, 2, 0, 0, 0, 0xFF, 0xFF })) return 2;
        ConstructorInfo aptca = typeof (AllowPartiallyTrustedCallersAttribute).GetConstructor (Type.EmptyTypes);
        if (!AssemblyRowUnreadable (aptca, new byte[] { 0xFF, 0xFF })) return 3;
        if (!TypeRowUnreadable (ClassInterface, new byte[] { 1, 0, 1, 0, 0xFF, 0xFF, 0xDE, 0xAD })) return 4;
        if (!TypeRowUnreadable (ClassInterface, new byte[] { 1, 0, 1, 0, 0 })) return 5;
        if (!TypeRowUnreadable (ClassInterface, new byte[] { 1, 0, 1, 0, 0xFF })) return 6;
        if (!TypeRowUnreadable (ClassInterface, new byte[] { 1, 0, 1, 0, 0, 0, 0xDE, 0xAD, 0xBE, 0xEF })) return 7;
        if (!TypeRowUnreadable (Guid, GuidThen (7))) return 8;
        return 0;
    }
}
