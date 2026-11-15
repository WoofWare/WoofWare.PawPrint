using System;
using System.Reflection;
using System.Reflection.Emit;

// `CreateType` retried after it threw for a pseudo-attribute on the type's generic parameter. The first call defines
// the parameter, bakes its queued attribute and throws `ArgumentException`, leaving the type uncreated; the second
// defines the parameter again, since `RuntimeTypeBuilder.CreateTypeNoLock` marks the type created only after the bake,
// and throws again. Measured on .NET 10 and 11 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    static bool ThrowsInvalidAttribute (Action action)
    {
        try
        {
            action ();
            return false;
        }
        catch (Exception e)
        {
            return e.GetType () == typeof (ArgumentException) && e.Message == "Invalid custom attribute provided.";
        }
    }

    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Retry"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("Retry");
        TypeBuilder g = module.DefineType ("G", TypeAttributes.Public);
        GenericTypeParameterBuilder gp = g.DefineGenericParameters ("TT")[0];
        gp.SetCustomAttribute (new CustomAttributeBuilder (typeof (SerializableAttribute).GetConstructor (Type.EmptyTypes), new object[0]));

        if (!ThrowsInvalidAttribute (() => g.CreateType ())) return 1;
        if (g.IsCreated ()) return 2;
        if (!ThrowsInvalidAttribute (() => g.CreateType ())) return 3;
        if (g.IsCreated ()) return 4;
        return 0;
    }
}
