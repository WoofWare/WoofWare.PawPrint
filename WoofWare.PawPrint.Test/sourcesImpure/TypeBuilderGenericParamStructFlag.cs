using System;
using System.Reflection;
using System.Reflection.Emit;

// A generic `TypeBuilder`'s parameter with the `struct` flag (`NotNullableValueTypeConstraint`) and no
// GenericParamConstraint row naming `System.ValueType`, which Roslyn writes beside the flag and
// `TypeBuilder` does not: `GetGenericParameterConstraints` reports the rows the metadata holds, and no
// `ValueType`. Measured on .NET 10 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Dyn"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("Dyn");

        // `class S<A, B> where A : struct, IFormattable where B : struct`.
        TypeBuilder s = module.DefineType ("S", TypeAttributes.Public | TypeAttributes.Class);
        GenericTypeParameterBuilder[] parameters = s.DefineGenericParameters ("A", "B");
        parameters[0].SetGenericParameterAttributes (GenericParameterAttributes.NotNullableValueTypeConstraint);
        parameters[0].SetInterfaceConstraints (typeof (IFormattable));
        parameters[1].SetGenericParameterAttributes (GenericParameterAttributes.NotNullableValueTypeConstraint);
        Type created = s.CreateType ()!;
        Type[] arguments = created.GetGenericArguments ();

        Type[] a = arguments[0].GetGenericParameterConstraints ();

        if (a.Length != 1 || a[0] != typeof (IFormattable))
        {
            return 1;
        }

        if (arguments[1].GetGenericParameterConstraints ().Length != 0)
        {
            return 2;
        }

        return 0;
    }
}
