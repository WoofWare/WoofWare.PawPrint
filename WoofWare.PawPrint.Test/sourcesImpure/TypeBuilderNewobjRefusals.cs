using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;

// A `newobj` in the body of a `TypeBuilder` method, whose token is the emit scope's own metadata,
// naming what CoreCLR's JIT will not construct: a static method (InvalidProgramException), an
// abstract method (`Stream.Flush`: BadImageFormatException, "Bad IL format.") and an abstract
// class's public constructor (InvalidOperationException), beside a constructor that constructs.
// The JIT raises each refusal as it compiles the method, so each refused body starts with its
// `newobj` and has no handler: PawPrint raises at the instruction instead, and nothing the method
// does first could show the difference. Measured on .NET 10 and 11 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public sealed class Plain
{
    public readonly int V;

    public Plain (int v)
    {
        V = v;
    }
}

public static class Holder
{
    public static object Stat ()
    {
        return "static";
    }
}

public abstract class Abs
{
    public int V;

    public Abs ()
    {
        V = 9;
    }
}

public static class Program
{
    static int Expect<T> (Func<object> call, string message, int ifReturned, int ifWrong) where T : Exception
    {
        try
        {
            call ();
            return ifReturned;
        }
        catch (Exception e)
        {
            return e.GetType () == typeof (T) && e.Message == message ? 0 : ifWrong;
        }
    }

    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Dyn"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("Dyn");
        TypeBuilder type = module.DefineType ("T", TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed);

        void Define (string name, Action<ILGenerator> emit)
        {
            MethodBuilder method = type.DefineMethod (name, MethodAttributes.Public | MethodAttributes.Static, typeof (object), Type.EmptyTypes);
            ILGenerator il = method.GetILGenerator ();
            emit (il);
            il.Emit (OpCodes.Ret);
        }

        Define ("Ctor", il =>
        {
            il.Emit (OpCodes.Ldc_I4, 41);
            il.Emit (OpCodes.Newobj, typeof (Plain).GetConstructor (new[] { typeof (int) })!);
        });

        Define ("Static", il => il.Emit (OpCodes.Newobj, typeof (Holder).GetMethod ("Stat")!));
        Define ("AbstractMethod", il => il.Emit (OpCodes.Newobj, typeof (Stream).GetMethod ("Flush", Type.EmptyTypes)!));
        Define ("AbstractClass", il => il.Emit (OpCodes.Newobj, typeof (Abs).GetConstructor (Type.EmptyTypes)!));

        Type? created = type.CreateType ();

        if (created is null)
        {
            return 1;
        }

        Func<object> Bind (string name) =>
            (Func<object>) created.GetMethod (name)!.CreateDelegate (typeof (Func<object>));

        object plain = Bind ("Ctor") ();

        if (!(plain is Plain p) || p.V != 41)
        {
            return 2;
        }

        int r = Expect<InvalidProgramException> (Bind ("Static"), "Common Language Runtime detected an invalid program.", 3, 4);

        if (r != 0)
        {
            return r;
        }

        r = Expect<BadImageFormatException> (Bind ("AbstractMethod"), "Bad IL format.", 5, 6);

        if (r != 0)
        {
            return r;
        }

        r = Expect<InvalidOperationException> (Bind ("AbstractClass"), "Instances of abstract classes cannot be created.", 7, 8);

        if (r != 0)
        {
            return r;
        }

        return 0;
    }
}
