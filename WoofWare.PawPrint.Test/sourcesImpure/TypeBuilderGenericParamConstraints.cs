using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;

// A generic `TypeBuilder`'s parameters' constraints: `TypeBuilder_DefineGenericParam` adds one
// GenericParamConstraint row for each constraint token, the interfaces' in the order they were set and
// then the base type's, and the created type's parameters report them in that order. The constraints
// name a TypeDef of the module, a TypeRef of the framework, and a TypeSpec naming the type's own
// parameter. An instantiation whose arguments satisfy them runs, calling through the constraint
// `T : IComparable<T>`; one whose arguments do not is refused. Measured on .NET 10 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    static bool SameTypes (Type[] actual, Type[] expected)
    {
        if (actual.Length != expected.Length)
        {
            return false;
        }

        for (int i = 0; i < actual.Length; i++)
        {
            if (actual[i] != expected[i])
            {
                return false;
            }
        }

        return true;
    }

    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Dyn"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("Dyn");

        // TypeDef 2: `interface IMark`.
        TypeBuilder mark = module.DefineType ("IMark", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);

        // TypeDef 3: `class Base : IMark { public Base () {} }`.
        TypeBuilder baseBuilder = module.DefineType ("Base", TypeAttributes.Public | TypeAttributes.Class, typeof (object), new Type[] { mark });
        baseBuilder.DefineDefaultConstructor (MethodAttributes.Public);

        // TypeDef 4:
        // class Box<T, U, V, W>
        //     where T : struct, IComparable<T>
        //     where U : Stream, IDisposable, IAsyncDisposable, new()
        //     where V : class, Base, IMark
        //     where W : T
        // { public int Compare (T a, T b) => a.CompareTo (b); }
        TypeBuilder box = module.DefineType ("Box", TypeAttributes.Public | TypeAttributes.Class);
        GenericTypeParameterBuilder[] parameters = box.DefineGenericParameters ("T", "U", "V", "W");
        GenericTypeParameterBuilder t = parameters[0];
        GenericTypeParameterBuilder u = parameters[1];
        GenericTypeParameterBuilder v = parameters[2];
        GenericTypeParameterBuilder w = parameters[3];

        Type comparableOfT = typeof (IComparable<>).MakeGenericType (t);
        t.SetGenericParameterAttributes (GenericParameterAttributes.NotNullableValueTypeConstraint);
        t.SetInterfaceConstraints (comparableOfT);

        // The base type is set first, and its row is still the last.
        u.SetBaseTypeConstraint (typeof (Stream));
        u.SetInterfaceConstraints (typeof (IDisposable), typeof (IAsyncDisposable));
        u.SetGenericParameterAttributes (GenericParameterAttributes.DefaultConstructorConstraint);

        v.SetBaseTypeConstraint (baseBuilder);
        v.SetInterfaceConstraints (mark);
        v.SetGenericParameterAttributes (GenericParameterAttributes.ReferenceTypeConstraint);

        w.SetBaseTypeConstraint (t);

        box.DefineDefaultConstructor (MethodAttributes.Public);
        MethodBuilder compare = box.DefineMethod ("Compare", MethodAttributes.Public, typeof (int), new Type[] { t, t });
        ILGenerator il = compare.GetILGenerator ();
        il.Emit (OpCodes.Ldarga_S, (byte) 1);
        il.Emit (OpCodes.Ldarg_2);
        il.Emit (OpCodes.Constrained, t);
        il.Emit (OpCodes.Callvirt, TypeBuilder.GetMethod (comparableOfT, typeof (IComparable<>).GetMethod ("CompareTo")!));
        il.Emit (OpCodes.Ret);

        Type createdMark = mark.CreateType ()!;
        Type createdBase = baseBuilder.CreateType ()!;
        Type created = box.CreateType ()!;

        Type[] arguments = created.GetGenericArguments ();

        if (arguments.Length != 4)
        {
            return 1;
        }

        for (int i = 0; i < 4; i++)
        {
            if (arguments[i].MetadataToken != 0x2A000001 + i)
            {
                return 2;
            }
        }

        // `T : IComparable<T>`, where the `T` the constraint names is the created type's own parameter.
        Type[] tConstraints = arguments[0].GetGenericParameterConstraints ();

        if (tConstraints.Length != 1
            || tConstraints[0].GetGenericTypeDefinition () != typeof (IComparable<>)
            || tConstraints[0].GetGenericArguments ()[0] != arguments[0])
        {
            return 3;
        }

        if (arguments[0].GenericParameterAttributes != GenericParameterAttributes.NotNullableValueTypeConstraint)
        {
            return 4;
        }

        if (!SameTypes (arguments[1].GetGenericParameterConstraints (), new Type[] { typeof (IDisposable), typeof (IAsyncDisposable), typeof (Stream) }))
        {
            return 5;
        }

        if (arguments[1].GenericParameterAttributes != GenericParameterAttributes.DefaultConstructorConstraint)
        {
            return 6;
        }

        if (!SameTypes (arguments[2].GetGenericParameterConstraints (), new Type[] { createdMark, createdBase }))
        {
            return 7;
        }

        if (arguments[2].GenericParameterAttributes != GenericParameterAttributes.ReferenceTypeConstraint)
        {
            return 8;
        }

        if (!SameTypes (arguments[3].GetGenericParameterConstraints (), new Type[] { arguments[0] }))
        {
            return 9;
        }

        if (arguments[3].GenericParameterAttributes != GenericParameterAttributes.None)
        {
            return 10;
        }

        Type closed = created.MakeGenericType (typeof (int), typeof (MemoryStream), createdBase, typeof (int));
        object instance = Activator.CreateInstance (closed)!;
        MethodInfo closedCompare = closed.GetMethod ("Compare")!;

        if ((int) closedCompare.Invoke (instance, new object[] { 3, 5 })! >= 0)
        {
            return 11;
        }

        if ((int) closedCompare.Invoke (instance, new object[] { 5, 5 })! != 0)
        {
            return 12;
        }

        return 0;
    }
}
