using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;

// `MakeGenericType` checks the constraints of a created dynamic generic type as it does any other's: an
// argument breaking a TypeRef, a TypeDef or a TypeSpec constraint is refused with `ArgumentException`,
// and one satisfying it is taken. Each type has one parameter, because with several, CoreCLR's
// `ValidateGenericArguments` checks them in the typical type's context and names the first parameter
// whose constraint mentions another. Measured on .NET 10 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    // 0 if `argument` is refused with `ArgumentException`, 1 if it is taken, 2 for another exception.
    static int Refused (Type definition, Type argument)
    {
        try
        {
            definition.MakeGenericType (argument);
            return 1;
        }
        catch (Exception e) when (e.GetType () == typeof (ArgumentException))
        {
            return 0;
        }
        catch (Exception)
        {
            return 2;
        }
    }

    static Type Constrained (ModuleBuilder module, string name, Type? interfaceConstraint, Type? baseConstraint)
    {
        TypeBuilder builder = module.DefineType (name, TypeAttributes.Public | TypeAttributes.Class);
        GenericTypeParameterBuilder parameter = builder.DefineGenericParameters ("T")[0];

        if (interfaceConstraint != null)
        {
            parameter.SetInterfaceConstraints (interfaceConstraint);
        }

        if (baseConstraint != null)
        {
            parameter.SetBaseTypeConstraint (baseConstraint);
        }

        return builder.CreateType ()!;
    }

    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Dyn"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("Dyn");

        TypeBuilder markBuilder = module.DefineType ("IMark", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        TypeBuilder baseBuilder = module.DefineType ("Base", TypeAttributes.Public | TypeAttributes.Class, typeof (object), new Type[] { markBuilder });
        baseBuilder.DefineDefaultConstructor (MethodAttributes.Public);
        markBuilder.CreateType ();
        Type baseType = baseBuilder.CreateType ()!;

        // A TypeRef constraint: `Disposer<T> where T : IDisposable`.
        Type disposer = Constrained (module, "Disposer", typeof (IDisposable), null);

        if (Refused (disposer, typeof (MemoryStream)) != 1)
        {
            return 1;
        }

        if (Refused (disposer, typeof (object)) != 0)
        {
            return 2;
        }

        // TypeDef constraints: `Marked<T> where T : IMark` and `Derived<T> where T : Base`.
        Type marked = Constrained (module, "Marked", markBuilder, null);

        if (Refused (marked, baseType) != 1)
        {
            return 3;
        }

        if (Refused (marked, typeof (string)) != 0)
        {
            return 4;
        }

        Type derived = Constrained (module, "Derived", null, baseBuilder);

        if (Refused (derived, baseType) != 1)
        {
            return 5;
        }

        if (Refused (derived, typeof (object)) != 0)
        {
            return 6;
        }

        // A TypeSpec constraint naming the type's own parameter: `Ordered<T> where T : IComparable<T>`.
        TypeBuilder orderedBuilder = module.DefineType ("Ordered", TypeAttributes.Public | TypeAttributes.Class);
        GenericTypeParameterBuilder t = orderedBuilder.DefineGenericParameters ("T")[0];
        t.SetInterfaceConstraints (typeof (IComparable<>).MakeGenericType (t));
        Type ordered = orderedBuilder.CreateType ()!;

        if (Refused (ordered, typeof (int)) != 1)
        {
            return 7;
        }

        if (Refused (ordered, typeof (object)) != 0)
        {
            return 8;
        }

        return 0;
    }
}
