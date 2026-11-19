using System;
using System.Reflection;
using System.Reflection.Emit;

// `Type.GetInterfaces()` on a generic definition made by `TypeBuilder`: `DynG<T>`, given `IC<T>` and
// `IPair<T, int>` by `AddInterfaceImplementation`, answers its interface map, each declared interface
// followed by its own parents, over `DynG`'s own variable; `DynG<string>` answers the same over
// `string`. The emitter writes only the two rows, so `IA<T>` and `IB` come from expanding `IC<T>`.
// Measured on .NET 10 and 11 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public interface IA<T> { }
public interface IB { }
public interface IC<T> : IA<T>, IB { }
public interface IPair<A, B> { }

public static class Program
{
    static string Render (Type[] types)
    {
        string[] names = new string[types.Length];
        for (int i = 0; i < types.Length; i++) names[i] = types[i].ToString ();
        return "[" + string.Join (", ", names) + "]";
    }

    static int Check (int code, Type receiver, params Type[] expected)
    {
        Type[] actual = receiver.GetInterfaces ();
        bool same = actual.Length == expected.Length;
        for (int i = 0; same && i < actual.Length; i++)
        {
            same = ReferenceEquals (actual[i], expected[i]);
        }

        if (same) return 0;
        Console.WriteLine ("check " + code + ", " + receiver + ": expected " + Render (expected) + ", got " + Render (actual));
        return code;
    }

    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Dyn"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("Dyn");

        TypeBuilder dynG = module.DefineType ("DynG", TypeAttributes.Public | TypeAttributes.Class);
        GenericTypeParameterBuilder[] parameters = dynG.DefineGenericParameters ("T");
        dynG.AddInterfaceImplementation (typeof (IC<>).MakeGenericType (parameters[0]));
        dynG.AddInterfaceImplementation (typeof (IPair<,>).MakeGenericType (parameters[0], typeof (int)));

        Type created = dynG.CreateType ()!;
        if (!created.IsGenericTypeDefinition) return 1;

        Type t = created.GetGenericArguments ()[0];
        int r;
        if ((r = Check (
                2,
                created,
                typeof (IC<>).MakeGenericType (t),
                typeof (IA<>).MakeGenericType (t),
                typeof (IB),
                typeof (IPair<,>).MakeGenericType (t, typeof (int)))) != 0) return r;

        if ((r = Check (
                3,
                created.MakeGenericType (typeof (string)),
                typeof (IC<string>),
                typeof (IA<string>),
                typeof (IB),
                typeof (IPair<string, int>))) != 0) return r;

        return 0;
    }
}
