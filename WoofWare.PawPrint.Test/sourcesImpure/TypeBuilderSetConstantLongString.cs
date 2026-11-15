using System;
using System.Reflection;
using System.Reflection.Emit;

// A 32768-character string constant on a parameter, through `ParameterBuilder.SetConstant`, read back after
// `CreateType`. `TypeBuilder_SetConstantValue` is given the string NUL-terminated and reads it to its first NUL, with no
// bound on its length. Measured on .NET 10 and 11 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    public static int Main ()
    {
        var chars = new char[32768];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = (char) ('a' + i % 26);
        }

        string value = new string (chars);
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Long"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("Long");
        TypeBuilder t = module.DefineType ("L", TypeAttributes.Public | TypeAttributes.Abstract);
        MethodBuilder m = t.DefineMethod ("M", MethodAttributes.Public | MethodAttributes.Static, typeof (void), new[] { typeof (string) });
        m.GetILGenerator ().Emit (OpCodes.Ret);
        m.DefineParameter (1, ParameterAttributes.None, "p").SetConstant (value);
        ParameterInfo p = t.CreateType ().GetMethod ("M").GetParameters ()[0];
        if ((int) p.Attributes != 0x1000)
        {
            Console.WriteLine ("check 1: attributes 0x" + ((int) p.Attributes).ToString ("x4"));
            return 1;
        }

        object read = p.DefaultValue;
        if (!(read is string s) || s != value)
        {
            Console.WriteLine ("check 2: " + (read is string r ? "string of length " + r.Length : read == null ? "null" : read.GetType ().Name));
            return 2;
        }

        return 0;
    }
}
