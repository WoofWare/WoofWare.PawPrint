using System;
using System.Reflection;
using System.Reflection.Emit;

// A dynamic type with two `DefineMethodOverride` rows naming one instance interface method, with
// different bodies. CoreCLR refuses the type at `CreateType` with `TypeLoadException`, saying the
// second body "is overriding a method that has been overridden"; only the exception's type is checked.
// Measured on .NET 10 to exit 0.
//
// Returns 0 if `CreateType` threw exactly `TypeLoadException`, 1 if it threw nothing, or 2 if it
// threw anything else.

public interface IInstance
{
    int N ();
}

public static class Program
{
    static MethodBuilder Body (TypeBuilder type, string name, int value)
    {
        MethodBuilder method = type.DefineMethod (
            name,
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Final
            | MethodAttributes.HideBySig,
            typeof (int),
            Type.EmptyTypes);
        ILGenerator il = method.GetILGenerator ();
        il.Emit (OpCodes.Ldc_I4, value);
        il.Emit (OpCodes.Ret);
        return method;
    }

    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Dyn"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("Dyn");
        TypeBuilder type = module.DefineType ("C", TypeAttributes.Public | TypeAttributes.Class);
        type.AddInterfaceImplementation (typeof (IInstance));
        type.DefineDefaultConstructor (MethodAttributes.Public);

        MethodInfo n = typeof (IInstance).GetMethod (nameof (IInstance.N))!;
        type.DefineMethodOverride (Body (type, "B1", 1), n);
        type.DefineMethodOverride (Body (type, "B2", 2), n);

        try
        {
            type.CreateType ();
            Console.WriteLine ("CreateType threw nothing");
            return 1;
        }
        catch (Exception e) when (e.GetType () == typeof (TypeLoadException))
        {
            Console.WriteLine ("CreateType threw TypeLoadException");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine ($"CreateType threw {e.GetType ()}");
            return 2;
        }
    }
}
