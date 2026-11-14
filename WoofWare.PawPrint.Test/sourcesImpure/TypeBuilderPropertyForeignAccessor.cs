using System;
using System.Reflection;
using System.Reflection.Emit;

// A property whose accessor is a method of another type of the same module. `PropertyBuilder.SetGetMethod` checks
// only that the property's own type is not created, and `TypeBuilder_DefineMethodSemantics` writes the row whatever
// type the method belongs to. Reflection over the property's type then resolves the accessor against that type:
// `RuntimeType.GetMethodBase` throws `ArgumentException` for a method of an unrelated type, and accepts one of a type
// the property's type derives from. Measured on .NET 10 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    static MethodBuilder Constant (TypeBuilder type, string name, int value)
    {
        MethodBuilder method = type.DefineMethod (
            name,
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            typeof (int),
            Type.EmptyTypes);
        ILGenerator il = method.GetILGenerator ();
        il.Emit (OpCodes.Ldc_I4, value);
        il.Emit (OpCodes.Ret);
        return method;
    }

    public static int Main ()
    {
        ModuleBuilder module =
            AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Foreign"), AssemblyBuilderAccess.Run).DefineDynamicModule ("Foreign");

        // FB is created before its method becomes FA's accessor.
        TypeBuilder fa = module.DefineType ("FA", TypeAttributes.Public);
        TypeBuilder fb = module.DefineType ("FB", TypeAttributes.Public);
        MethodBuilder foreignGet = Constant (fb, "get_Foreign", 1);
        fb.CreateType ();
        fa.DefineProperty ("Foreign", PropertyAttributes.None, typeof (int), Type.EmptyTypes).SetGetMethod (foreignGet);
        Type faCreated = fa.CreateType ();

        try
        {
            faCreated.GetProperty ("Foreign");
            return 1;
        }
        catch (ArgumentException)
        {
        }

        // A base type's method as the accessor of the derived type's property.
        TypeBuilder baseType = module.DefineType ("Base", TypeAttributes.Public);
        MethodBuilder baseGet = Constant (baseType, "get_FromBase", 9);
        Type baseCreated = baseType.CreateType ();
        TypeBuilder derived = module.DefineType ("Derived", TypeAttributes.Public, baseCreated);
        derived.DefineProperty ("FromBase", PropertyAttributes.None, typeof (int), Type.EmptyTypes).SetGetMethod (baseGet);
        Type derivedCreated = derived.CreateType ();

        PropertyInfo fromBase = derivedCreated.GetProperty (
            "FromBase",
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        MethodInfo getter = fromBase.GetGetMethod ();

        if (getter.DeclaringType != baseCreated || getter.Name != "get_FromBase")
        {
            return 2;
        }

        if ((int) fromBase.GetValue (Activator.CreateInstance (derivedCreated)) != 9)
        {
            return 3;
        }

        return 0;
    }
}
