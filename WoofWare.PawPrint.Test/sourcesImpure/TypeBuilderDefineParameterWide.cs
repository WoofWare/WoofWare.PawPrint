using System;
using System.Reflection;
using System.Reflection.Emit;

// `MethodBuilder.DefineParameter` at positions that do not fit the Param row's two-byte Sequence column, on a method
// of 65537 parameters: CoreCLR stores the low sixteen bits, so position 65537 is parameter 1 and 65536 the return
// value; positions 256 and 65535 tell sixteen bits from eight. The test's terminal-state check reads the Sequence
// column. Measured on .NET 10 and 11 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    static ModuleBuilder NewModule (string name) =>
        AssemblyBuilder.DefineDynamicAssembly (new AssemblyName (name), AssemblyBuilderAccess.Run).DefineDynamicModule (name);

    /// The token `TypeBuilder_SetParamInfo` handed back, which `RuntimeParameterBuilder` keeps privately.
    static int Token (ParameterBuilder parameter) =>
        (int) parameter.GetType ().GetField ("_token", BindingFlags.NonPublic | BindingFlags.Instance).GetValue (parameter);

    static bool Is (ParameterInfo p, int position, string name, int attributes, int token) =>
        p != null && p.Position == position && p.Name == name && (int) p.Attributes == attributes && p.MetadataToken == token;

    static bool Define (MethodBuilder method, int position, string name, int rid) =>
        Token (method.DefineParameter (position, ParameterAttributes.None, name)) == (0x08000000 | rid);

    static int Wide ()
    {
        ModuleBuilder module = NewModule ("Edge");
        TypeBuilder e = module.DefineType ("E", TypeAttributes.Public);
        Type[] types = new Type[65537];
        for (int k = 0; k < types.Length; k++)
        {
            types[k] = typeof (byte);
        }

        MethodBuilder wide = e.DefineMethod ("Wide", MethodAttributes.Public | MethodAttributes.Static, typeof (void), types);
        wide.GetILGenerator ().Emit (OpCodes.Ret);
        if (!Define (wide, 65537, "last", 1)
            || !Define (wide, 65536, "nextToLast", 2)
            || !Define (wide, 256, "p256", 3)
            || !Define (wide, 65535, "p65535", 4))
        {
            return 1;
        }

        MethodInfo created = e.CreateType ().GetMethod ("Wide");
        ParameterInfo[] ps = created.GetParameters ();
        if (ps.Length != 65537
            || !Is (ps[0], 0, "last", 0, 0x08000001)
            || !Is (ps[255], 255, "p256", 0, 0x08000003)
            || !Is (ps[65534], 65534, "p65535", 0, 0x08000004)
            || !Is (ps[65535], 65535, null, 0, 0x08000000)
            || !Is (ps[65536], 65536, null, 0, 0x08000000)
            || !Is (created.ReturnParameter, -1, "nextToLast", 0, 0x08000002))
        {
            return 2;
        }

        return 0;
    }

    public static int Main () => Wide ();
}
