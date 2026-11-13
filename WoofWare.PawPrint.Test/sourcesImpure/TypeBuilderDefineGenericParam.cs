using System;
using System.Reflection;
using System.Reflection.Emit;

// A generic `TypeBuilder`'s parameters get their GenericParam rows from `TypeBuilder_DefineGenericParam`
// when `CreateType` runs, numbered in the order they are created; until then a parameter's token is 0.
// The created type is the generic type definition, its parameters report the builders' tokens, names,
// positions and attributes, and an instantiation of it runs. A covariant parameter of an interface loads.
// Measured on .NET 10 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Dyn"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("Dyn");

        // TypeDef 2, without parameters, created first.
        TypeBuilder plain = module.DefineType ("Plain", TypeAttributes.Public | TypeAttributes.Class);

        // TypeDef 3: `class Box<T, U> where U : class, new() { public T Value; public T Id (T x) => x; }`.
        TypeBuilder box = module.DefineType ("Box", TypeAttributes.Public | TypeAttributes.Class);
        GenericTypeParameterBuilder[] parameters = box.DefineGenericParameters ("T", "U");
        parameters[1].SetGenericParameterAttributes (
            GenericParameterAttributes.ReferenceTypeConstraint | GenericParameterAttributes.DefaultConstructorConstraint);
        box.DefineField ("Value", parameters[0], FieldAttributes.Public);
        MethodBuilder id = box.DefineMethod ("Id", MethodAttributes.Public, parameters[0], new Type[] { parameters[0] });
        ILGenerator il = id.GetILGenerator ();
        il.Emit (OpCodes.Ldarg_1);
        il.Emit (OpCodes.Ret);

        // TypeDef 4: `interface IOut<out V>`.
        TypeBuilder iout = module.DefineType ("IOut", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        GenericTypeParameterBuilder[] ioutParameters = iout.DefineGenericParameters ("V");
        ioutParameters[0].SetGenericParameterAttributes (GenericParameterAttributes.Covariant);

        if (parameters[0].MetadataToken != 0 || parameters[1].MetadataToken != 0 || ioutParameters[0].MetadataToken != 0)
        {
            return 1;
        }

        plain.CreateType ();
        Type created = box.CreateType ()!;

        if (parameters[0].MetadataToken != 0x2A000001 || parameters[1].MetadataToken != 0x2A000002)
        {
            return 2;
        }

        if (!created.IsGenericTypeDefinition || created.MetadataToken != 0x02000003)
        {
            return 3;
        }

        Type[] arguments = created.GetGenericArguments ();

        if (arguments.Length != 2)
        {
            return 4;
        }

        string[] names = { "T", "U" };
        GenericParameterAttributes[] attributes =
        {
            GenericParameterAttributes.None,
            GenericParameterAttributes.ReferenceTypeConstraint | GenericParameterAttributes.DefaultConstructorConstraint,
        };

        for (int i = 0; i < 2; i++)
        {
            if (arguments[i].MetadataToken != parameters[i].MetadataToken)
            {
                return 5 + 4 * i;
            }

            if (arguments[i].Name != names[i])
            {
                return 6 + 4 * i;
            }

            if (!arguments[i].IsGenericParameter || arguments[i].GenericParameterPosition != i)
            {
                return 7 + 4 * i;
            }

            if (arguments[i].GenericParameterAttributes != attributes[i])
            {
                return 8 + 4 * i;
            }
        }

        Type createdOut = iout.CreateType ()!;

        if (ioutParameters[0].MetadataToken != 0x2A000003)
        {
            return 13;
        }

        Type outArgument = createdOut.GetGenericArguments ()[0];

        if (outArgument.MetadataToken != 0x2A000003 || outArgument.GenericParameterAttributes != GenericParameterAttributes.Covariant)
        {
            return 14;
        }

        Type closed = created.MakeGenericType (typeof (int), typeof (object));
        object instance = Activator.CreateInstance (closed)!;

        if ((int) closed.GetMethod ("Id")!.Invoke (instance, new object[] { 42 })! != 42)
        {
            return 15;
        }

        return 0;
    }
}
