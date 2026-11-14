using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

// An ordinary attribute added to a created type, to one of its methods, and to a parameter of that method: CoreCLR
// writes each row, and reflection over the created type shows it at once, through the objects obtained before the
// attribute was added. Measured on .NET 10 and 11 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

[AttributeUsage (AttributeTargets.All, AllowMultiple = true)]
public class TagAttribute : Attribute
{
    public TagAttribute (string s) { S = s; }
    public string S;
}

public static class Program
{
    static CustomAttributeBuilder Tag (string s) =>
        new CustomAttributeBuilder (typeof (TagAttribute).GetConstructor (new[] { typeof (string) }), new object[] { s });

    static string Tags (object[] attributes)
    {
        var values = new List<string> ();
        foreach (object attribute in attributes)
        {
            values.Add (((TagAttribute) attribute).S);
        }

        return string.Join (",", values);
    }

    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("AfterCreate"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("AfterCreate");
        TypeBuilder t = module.DefineType ("T", TypeAttributes.Public);
        t.SetCustomAttribute (Tag ("type-before"));
        MethodBuilder m = t.DefineMethod ("M", MethodAttributes.Public | MethodAttributes.Static, typeof (void), new[] { typeof (int) });
        m.GetILGenerator ().Emit (OpCodes.Ret);
        ParameterBuilder p = m.DefineParameter (1, ParameterAttributes.None, "x");
        Type tt = t.CreateType ();
        MethodInfo method = tt.GetMethod ("M");
        ParameterInfo parameter = method.GetParameters ()[0];

        // The first call after creation: under PawPrint, the created-owner stop.
        t.SetCustomAttribute (Tag ("type-after"));
        m.SetCustomAttribute (Tag ("method-after"));
        p.SetCustomAttribute (Tag ("param-after"));

        if (Tags (tt.GetCustomAttributes (false)) != "type-before,type-after")
        {
            return 1;
        }

        if (Tags (method.GetCustomAttributes (false)) != "method-after")
        {
            return 2;
        }

        if (Tags (parameter.GetCustomAttributes (false)) != "param-after")
        {
            return 3;
        }

        if (tt.GetCustomAttributesData ().Count != 2 || method.GetCustomAttributesData ().Count != 1)
        {
            return 4;
        }

        return 0;
    }
}
