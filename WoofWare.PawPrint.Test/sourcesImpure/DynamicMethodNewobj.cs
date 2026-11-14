using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;

// A `newobj` in a `DynamicMethod`'s body, whose operand is an entry of the method's `DynamicScope`
// rather than a metadata token: a reflected constructor, which constructs, and each method CoreCLR's
// JIT refuses to construct with. The refusals are a static method (InvalidProgramException), a
// `DynamicMethod` (static, so the same), an abstract method (BadImageFormatException, "Bad IL
// format."), a static abstract interface member (abstract, so the same), and an abstract class's
// public constructor, of a non-generic class and of a generic one at an instantiation
// (InvalidOperationException). A `newobj` of a `DynamicMethod` mints it before refusing it, so an
// empty one gives the mint's own exception instead and is left unminted.
//
// The JIT raises each refusal as it compiles the method, so each refused body starts with its
// `newobj` and has no handler: PawPrint raises at the instruction instead, and nothing the method
// does first could show the difference. Every method is made with `skipVisibility`, so that no
// access check could stand in front of a refusal. Measured on .NET 10 and 11 to exit 0.
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

public struct Pair
{
    public int V;
    public long W;

    public Pair (int v)
    {
        V = v;
        W = v * 10L;
    }
}

public sealed class Gen<T>
{
    public readonly T V;

    public Gen (T v)
    {
        V = v;
    }
}

public sealed class Throws
{
    public Throws (int x)
    {
        throw new InvalidOperationException ("boom " + x);
    }
}

public static class Holder
{
    public static object Stat ()
    {
        return "static";
    }

    public static object One (int x)
    {
        return x;
    }
}

public interface IMake
{
    static abstract object Make ();
}

public abstract class Abs
{
    public int V;

    public Abs ()
    {
        V = 9;
    }
}

public abstract class AbsGen<T>
{
    public T? V;

    public AbsGen ()
    {
    }
}

public static class Program
{
    const string InvalidProgram = "Common Language Runtime detected an invalid program.";

    static Func<object> Dyn (string name, Action<ILGenerator> emit)
    {
        DynamicMethod method = new DynamicMethod (name, typeof (object), Type.EmptyTypes, typeof (Program).Module, true);
        ILGenerator il = method.GetILGenerator ();
        emit (il);
        il.Emit (OpCodes.Ret);
        return (Func<object>) method.CreateDelegate (typeof (Func<object>));
    }

    // Builds and calls the method, and returns 0 if it raised exactly `T` with `message`,
    // `ifReturned` if it returned, and `ifWrong` if it raised anything else.
    static int Expect<T> (string name, Action<ILGenerator> emit, string message, int ifReturned, int ifWrong) where T : Exception
    {
        try
        {
            Dyn (name, emit) ();
            return ifReturned;
        }
        catch (Exception e)
        {
            return e.GetType () == typeof (T) && e.Message == message ? 0 : ifWrong;
        }
    }

    static bool Minted (DynamicMethod method)
    {
        return typeof (DynamicMethod).GetField ("_methodHandle", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue (method) != null;
    }

    public static int Main ()
    {
        object plain = Dyn ("Plain", il =>
        {
            il.Emit (OpCodes.Ldc_I4, 41);
            il.Emit (OpCodes.Newobj, typeof (Plain).GetConstructor (new[] { typeof (int) })!);
        }) ();

        if (!(plain is Plain p) || p.V != 41)
        {
            return 1;
        }

        object pair = Dyn ("Pair", il =>
        {
            il.Emit (OpCodes.Ldc_I4_5);
            il.Emit (OpCodes.Newobj, typeof (Pair).GetConstructor (new[] { typeof (int) })!);
            il.Emit (OpCodes.Box, typeof (Pair));
        }) ();

        if (!(pair is Pair s) || s.V != 5 || s.W != 50)
        {
            return 2;
        }

        object genInt = Dyn ("GenInt", il =>
        {
            il.Emit (OpCodes.Ldc_I4_6);
            il.Emit (OpCodes.Newobj, typeof (Gen<int>).GetConstructor (new[] { typeof (int) })!);
        }) ();

        if (!(genInt is Gen<int> gi) || gi.V != 6)
        {
            return 3;
        }

        object genString = Dyn ("GenString", il =>
        {
            il.Emit (OpCodes.Ldstr, "g");
            il.Emit (OpCodes.Newobj, typeof (Gen<string>).GetConstructor (new[] { typeof (string) })!);
        }) ();

        if (!(genString is Gen<string> gs) || gs.V != "g")
        {
            return 4;
        }

        object str = Dyn ("String", il =>
        {
            il.Emit (OpCodes.Ldc_I4, (int) 'y');
            il.Emit (OpCodes.Ldc_I4_3);
            il.Emit (OpCodes.Newobj, typeof (string).GetConstructor (new[] { typeof (char), typeof (int) })!);
        }) ();

        if (!(str is string text) || text != "yyy")
        {
            return 5;
        }

        object caught = Dyn ("Caught", il =>
        {
            LocalBuilder result = il.DeclareLocal (typeof (object));
            il.BeginExceptionBlock ();
            il.Emit (OpCodes.Ldc_I4_1);
            il.Emit (OpCodes.Newobj, typeof (Throws).GetConstructor (new[] { typeof (int) })!);
            il.Emit (OpCodes.Stloc, result);
            il.BeginCatchBlock (typeof (InvalidOperationException));
            il.Emit (OpCodes.Callvirt, typeof (Exception).GetProperty ("Message")!.GetGetMethod ()!);
            il.Emit (OpCodes.Stloc, result);
            il.EndExceptionBlock ();
            il.Emit (OpCodes.Ldloc, result);
        }) ();

        if (!(caught is string message) || message != "boom 1")
        {
            return 6;
        }

        int r = Expect<InvalidProgramException> ("OpenGeneric", il =>
        {
            il.Emit (OpCodes.Ldnull);
            il.Emit (OpCodes.Newobj, typeof (Gen<>).GetConstructors ()[0]);
        }, InvalidProgram, 7, 8);

        if (r != 0)
        {
            return r;
        }

        DynamicMethod target = new DynamicMethod ("target", typeof (void), Type.EmptyTypes, typeof (Program).Module, true);
        target.GetILGenerator ().Emit (OpCodes.Ret);

        r = Expect<InvalidProgramException> ("OfDynamicMethod", il => il.Emit (OpCodes.Newobj, target), InvalidProgram, 9, 10);

        if (r != 0)
        {
            return r;
        }

        if (!Minted (target))
        {
            return 11;
        }

        DynamicMethod empty = new DynamicMethod ("emptytarget", typeof (void), Type.EmptyTypes, typeof (Program).Module, true);
        empty.GetILGenerator ();

        r = Expect<InvalidOperationException> ("OfEmptyDynamicMethod", il => il.Emit (OpCodes.Newobj, empty),
            "Method 'emptytarget' does not have a method body.", 12, 13);

        if (r != 0)
        {
            return r;
        }

        if (Minted (empty))
        {
            return 14;
        }

        r = Expect<InvalidProgramException> ("Static", il => il.Emit (OpCodes.Newobj, typeof (Holder).GetMethod ("Stat")!),
            InvalidProgram, 15, 16);

        if (r != 0)
        {
            return r;
        }

        // Its argument is not pushed: the refusal is the method's, not the stack's.
        r = Expect<InvalidProgramException> ("StaticUnpushed", il => il.Emit (OpCodes.Newobj, typeof (Holder).GetMethod ("One")!),
            InvalidProgram, 17, 18);

        if (r != 0)
        {
            return r;
        }

        r = Expect<BadImageFormatException> ("AbstractMethod",
            il => il.Emit (OpCodes.Newobj, typeof (Stream).GetMethod ("Flush", Type.EmptyTypes)!),
            "Bad IL format.", 19, 20);

        if (r != 0)
        {
            return r;
        }

        r = Expect<BadImageFormatException> ("StaticAbstract", il => il.Emit (OpCodes.Newobj, typeof (IMake).GetMethod ("Make")!),
            "Bad IL format.", 21, 22);

        if (r != 0)
        {
            return r;
        }

        r = Expect<InvalidOperationException> ("AbstractClass",
            il => il.Emit (OpCodes.Newobj, typeof (Abs).GetConstructor (Type.EmptyTypes)!),
            "Instances of abstract classes cannot be created.", 23, 24);

        if (r != 0)
        {
            return r;
        }

        r = Expect<InvalidOperationException> ("AbstractGenericClass",
            il => il.Emit (OpCodes.Newobj, typeof (AbsGen<int>).GetConstructor (Type.EmptyTypes)!),
            "Instances of abstract classes cannot be created.", 25, 26);

        if (r != 0)
        {
            return r;
        }

        return 0;
    }
}
