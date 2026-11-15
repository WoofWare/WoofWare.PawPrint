using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

// Two enums that CoreCLR's `Enum_GetValuesAndNames` reads and PawPrint's enum reader does not yet. `EMis`, an
// `EnumBuilder` over `int` whose second literal `DefineLiteral ("B", 2L)` is refused: `DefineLiteral` defines the field
// before `SetConstant` refuses the value, so `B` is a static literal field with no constant, which CoreCLR lists with the
// value 0. `EChar`, an `EnumBuilder` over `char`. Measured on .NET 10 and 11 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    static int Fail (int check, string saw)
    {
        Console.WriteLine ("check " + check + ": " + saw);
        return check;
    }

    static string ShowException (Exception e)
    {
        if (e == null) return "no exception";
        string param = e is ArgumentException ae ? " ParamName " + (ae.ParamName ?? "null") : "";
        return e.GetType ().FullName + " \"" + e.Message + "\"" + param;
    }

    static Exception Catch (Action action)
    {
        try
        {
            action ();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    static string Names (Type t) => string.Join (",", Enum.GetNames (t));

    /// Each value of `t`'s `GetValues` as its name and its `"D"` format.
    static string EnumValues (Type t)
    {
        var parts = new List<string> ();
        foreach (object v in Enum.GetValues (t))
        {
            parts.Add (v.GetType () == t ? v.ToString () + ":" + ((Enum) v).ToString ("D") : "not " + t.Name + ": " + v);
        }

        return string.Join (",", parts);
    }

    // Checks 1 to 5.
    static int Refused (ModuleBuilder module)
    {
        EnumBuilder e = module.DefineEnum ("EMis", TypeAttributes.Public, typeof (int));
        e.DefineLiteral ("A", 1);
        Exception x = Catch (() => e.DefineLiteral ("B", 2L));
        if (x == null || x.GetType () != typeof (ArgumentException) || x.Message != "Constant does not match the defined type." || ((ArgumentException) x).ParamName != null)
        {
            return Fail (1, ShowException (x));
        }

        e.DefineLiteral ("C", 3);
        Type t = e.CreateType ();

        // `B` has no constant, so it reads as 0 and sorts first.
        if (Names (t) != "B,A,C") return Fail (2, Names (t));
        if (EnumValues (t) != "B:0,A:1,C:3") return Fail (3, EnumValues (t));
        FieldInfo b = t.GetField ("B");
        x = Catch (() => b.GetRawConstantValue ());
        if ((int) b.Attributes != 0x0056 || x == null || x.GetType () != typeof (NotSupportedException) || x.Message != "Literal value was not found.")
        {
            return Fail (4, "attributes 0x" + ((int) b.Attributes).ToString ("x4") + " GetRawConstantValue threw " + ShowException (x));
        }

        string zero = Enum.ToObject (t, 0).ToString ();
        bool defined = Enum.IsDefined (t, Enum.ToObject (t, 0));
        if (zero != "B" || !defined) return Fail (5, zero + " " + defined);
        return 0;
    }

    // Checks 10 to 14.
    static int Char (ModuleBuilder module)
    {
        EnumBuilder e = module.DefineEnum ("EChar", TypeAttributes.Public, typeof (char));
        e.DefineLiteral ("A", 'a');
        e.DefineLiteral ("B", 'z');
        Type t = e.CreateType ();

        if (Enum.GetUnderlyingType (t) != typeof (char)) return Fail (10, Enum.GetUnderlyingType (t).Name);
        if (Names (t) != "A,B") return Fail (11, Names (t));
        // The `"D"` format of a `char` enum is the character, not its number.
        if (EnumValues (t) != "A:a,B:z") return Fail (12, EnumValues (t));
        FieldInfo a = t.GetField ("A");
        object raw = a.GetRawConstantValue ();
        if ((int) a.Attributes != 0x8056 || !(raw is char c) || c != 'a')
        {
            return Fail (13, "attributes 0x" + ((int) a.Attributes).ToString ("x4") + " GetRawConstantValue " + raw);
        }

        string z = Enum.ToObject (t, (int) 'z').ToString ();
        bool defined = Enum.IsDefined (t, Enum.ToObject (t, (int) 'z'));
        bool undefined = Enum.IsDefined (t, Enum.ToObject (t, (int) 'q'));
        if (z != "B" || !defined || undefined) return Fail (14, z + " " + defined + " " + undefined);
        return 0;
    }

    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Enums"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("Enums");
        int result = Refused (module);
        if (result != 0) return result;
        return Char (module);
    }
}
