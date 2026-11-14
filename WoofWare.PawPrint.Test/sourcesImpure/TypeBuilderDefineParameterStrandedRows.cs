using System;
using System.Reflection;
using System.Reflection.Emit;

// What real .NET does once `MethodBuilder.DefineParameter` has stranded MethodDef rows. CoreCLR's
// `CMiniMdRW::FixParamSequence` starts the `ParamPtr` table when a parameter slides ahead of an earlier one of its
// method, and, unlike the other way the table starts, leaves the `ParamList` of every later MethodDef row as
// `END_OF_TABLE`. Each module has the methods `X(int, int)`, `Y(int, int)` and `Z(int)`, and gives some of them rows
// first by reading a token: `ReturnAfterParam`, a later `DefineParameter` on the slid method throws;
// `SlideNoLaterUse`, the last stranded method's parameters cannot be read; `StrandThenNewMethod`, the slid method's
// run swallows the parameter of a method tokenised afterwards. Measured on .NET 10 and 11 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    const int IndexNotFound = unchecked ((int) 0x80131124);

    static ModuleBuilder NewModule (string name) =>
        AssemblyBuilder.DefineDynamicAssembly (new AssemblyName (name), AssemblyBuilderAccess.Run).DefineDynamicModule (name);

    /// The token `TypeBuilder_SetParamInfo` handed back, which `RuntimeParameterBuilder` keeps privately.
    static int Token (ParameterBuilder parameter) =>
        (int) parameter.GetType ().GetField ("_token", BindingFlags.NonPublic | BindingFlags.Instance).GetValue (parameter);

    static MethodBuilder Static (TypeBuilder type, string name, params Type[] parameters)
    {
        MethodBuilder method = type.DefineMethod (name, MethodAttributes.Public | MethodAttributes.Static, typeof (int), parameters);
        ILGenerator il = method.GetILGenerator ();
        il.Emit (OpCodes.Ldc_I4_7);
        il.Emit (OpCodes.Ret);
        return method;
    }

    static MethodBuilder[] ThreeMethods (TypeBuilder type) =>
        new[]
        {
            Static (type, "X", typeof (int), typeof (int)),
            Static (type, "Y", typeof (int), typeof (int)),
            Static (type, "Z", typeof (int)),
        };

    static bool Define (MethodBuilder method, int position, string name, int rid) =>
        Token (method.DefineParameter (position, ParameterAttributes.None, name)) == (0x08000000 | rid);

    /// Whether `action` throws `BadImageFormatException` "Index not found".
    static bool IndexNotFoundFrom (Action action)
    {
        try
        {
            action ();
            return false;
        }
        catch (BadImageFormatException e)
        {
            return e.HResult == IndexNotFound;
        }
    }

    static bool Is (ParameterInfo p, int position, string name, int token) =>
        p != null && p.Position == position && p.Name == name && p.MetadataToken == token;

    static int ReturnAfterParam ()
    {
        TypeBuilder t = NewModule ("ReturnAfterParam").DefineType ("T", TypeAttributes.Public);
        MethodBuilder[] ms = ThreeMethods (t);
        if (ms[1].MetadataToken != 0x06000002)
        {
            return 1;
        }

        if (!Define (ms[0], 1, "a", 1) || !Define (ms[0], 0, "r", 2))
        {
            return 2;
        }

        if (!IndexNotFoundFrom (() => ms[0].DefineParameter (2, ParameterAttributes.None, "again")))
        {
            return 3;
        }

        return 0;
    }

    static int SlideNoLaterUse ()
    {
        TypeBuilder t = NewModule ("SlideNoLaterUse").DefineType ("T", TypeAttributes.Public);
        MethodBuilder[] ms = ThreeMethods (t);
        if (ms[2].MetadataToken != 0x06000003)
        {
            return 10;
        }

        if (!Define (ms[0], 2, "b", 1) || !Define (ms[0], 1, "a", 2))
        {
            return 11;
        }

        Type c = t.CreateType ();
        ParameterInfo[] x = c.GetMethod ("X").GetParameters ();
        if (x.Length != 2 || !Is (x[0], 0, "a", 0x08000002) || !Is (x[1], 1, "b", 0x08000001))
        {
            return 12;
        }

        ParameterInfo[] y = c.GetMethod ("Y").GetParameters ();
        if (y.Length != 2 || !Is (y[0], 0, null, 0x08000000) || !Is (y[1], 1, null, 0x08000000))
        {
            return 13;
        }

        if (!IndexNotFoundFrom (() => c.GetMethod ("Z").GetParameters ()))
        {
            return 14;
        }

        return 0;
    }

    static int StrandThenNewMethod ()
    {
        TypeBuilder t = NewModule ("StrandThenNewMethod").DefineType ("T", TypeAttributes.Public);
        MethodBuilder[] ms = ThreeMethods (t);
        if (ms[1].MetadataToken != 0x06000002)
        {
            return 20;
        }

        if (!Define (ms[0], 2, "b", 1) || !Define (ms[0], 1, "a", 2) || !Define (ms[2], 1, "z", 3))
        {
            return 21;
        }

        Type c = t.CreateType ();
        if (!Is (c.GetMethod ("X").GetParameters ()[0], 0, "z", 0x08000003))
        {
            return 22;
        }

        return 0;
    }

    public static int Main ()
    {
        Func<int>[] modules = { ReturnAfterParam, SlideNoLaterUse, StrandThenNewMethod };
        foreach (Func<int> module in modules)
        {
            int result = module ();
            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    }
}
