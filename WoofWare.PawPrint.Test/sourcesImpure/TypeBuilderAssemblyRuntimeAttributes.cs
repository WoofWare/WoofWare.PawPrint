using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

// Attributes on an `AssemblyBuilder` that CoreCLR acts on as `TypeBuilder_DefineCustomAttribute` returns
// (`UpdateRuntimeStateForAssemblyCustomAttribute`). A friend attribute whose name the friend cache cannot parse throws
// out of the QCall after the row is written, and every later friend attribute on that assembly throws the same and is
// written too; types can still be created. The exception's type for an empty name differs between the runtimes.
// `DebuggableAttribute` is folded into the debugger bits whatever its blob, with no exception. Measured on .NET 10 and
// 11 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    static int count;

    static readonly ConstructorInfo InternalsVisibleTo =
        typeof (InternalsVisibleToAttribute).GetConstructor (new[] { typeof (string) });

    static byte[] Str (string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes (s);
        var blob = new byte[bytes.Length + 5];
        blob[0] = 1;
        blob[2] = (byte) bytes.Length;
        Array.Copy (bytes, 0, blob, 3, bytes.Length);
        return blob;
    }

    /// Applies `blob` as a friend attribute to a fresh assembly, then a well-formed one; whether each threw an exception
    /// satisfying `expected`, both rows were written, and a type can then be created.
    static bool Friend (byte[] blob, Func<Exception, bool> expected, bool firstReadable)
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("F" + (count++)), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("m");

        foreach (byte[] each in new[] { blob, Str ("Other") })
        {
            try
            {
                assembly.SetCustomAttribute (InternalsVisibleTo, each);
                return false;
            }
            catch (Exception e)
            {
                if (!expected (e))
                {
                    return false;
                }
            }
        }

        if (firstReadable && assembly.GetCustomAttributesData ().Count != 2)
        {
            return false;
        }

        module.DefineType ("T").CreateType ();
        return true;
    }

    public static int Main ()
    {
        bool net10 = Environment.Version.Major == 10;

        Func<Exception, bool> malformed = e => e.GetType () == typeof (BadImageFormatException) && e.Message == "Malformed custom attribute header.";

        if (!Friend (new byte[] { 2, 0, 1, (byte) 'F', 0, 0 }, malformed, false))
        {
            return 1;
        }

        if (!Friend (new byte[] { 1, 0, 9, (byte) 'F' }, malformed, false))
        {
            return 2;
        }

        if (!Friend (new byte[0], malformed, false))
        {
            return 3;
        }

        if (!Friend (new byte[] { 1, 0, 0xFF, 0, 0 }, malformed, true))
        {
            return 4;
        }

        if (!Friend (Str ("Friend, Version=1.0.0.0"), e => e.GetType () == typeof (COMException) && e.HResult == unchecked ((int) 0x801311E5), true))
        {
            return 5;
        }

        if (!Friend (Str (""), e => e.GetType () == (net10 ? typeof (ArgumentException) : typeof (FileLoadException)), true))
        {
            return 6;
        }

        AssemblyBuilder plain = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Plain"), AssemblyBuilderAccess.Run);
        plain.DefineDynamicModule ("m");
        plain.SetCustomAttribute (InternalsVisibleTo, Str ("Friend"));
        plain.SetCustomAttribute (InternalsVisibleTo, Str ("Friend2"));
        if (plain.GetCustomAttributesData ().Count != 2)
        {
            return 7;
        }

        // `DebuggableAttribute(bool, bool)` with a blob shorter than its arguments, then with none: accepted.
        ConstructorInfo debuggable = typeof (DebuggableAttribute).GetConstructor (new[] { typeof (bool), typeof (bool) });
        AssemblyBuilder debug = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Debug"), AssemblyBuilderAccess.Run);
        debug.DefineDynamicModule ("m");
        debug.SetCustomAttribute (debuggable, new byte[] { 1, 0, 0, 0 });
        debug.SetCustomAttribute (debuggable, new byte[0]);

        // Both rows are written, and reflection cannot read the first.
        try
        {
            debug.GetCustomAttributesData ();
            return 8;
        }
        catch (CustomAttributeFormatException)
        {
        }

        return 0;
    }
}
