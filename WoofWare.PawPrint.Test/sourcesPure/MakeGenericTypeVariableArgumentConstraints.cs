using System;
using System.Collections.Generic;
using System.IO;

public class NeedsStruct<T> where T : struct
{
}

public class NeedsClass<T> where T : class
{
}

public class NeedsNew<T> where T : new()
{
}

public class NeedsClassNew<T> where T : class, new()
{
}

public class NeedsDisposable<T> where T : IDisposable
{
}

public class NeedsException<T> where T : Exception
{
}

public class NeedsSame<A, B> where A : B
{
}

public class NeedsEquatable<T> where T : IEquatable<T>
{
}

public class NeedsObjects<T> where T : IEnumerable<object>
{
}

// The type parameters below are the arguments.
public class Unconstrained<U>
{
}

public class Struct<S> where S : struct
{
}

public class Class<C> where C : class
{
}

public class New<N> where N : new()
{
}

public class OfException<E> where E : Exception
{
}

public class OfEnum<En> where En : Enum
{
}

public class OfInterface<I> where I : IDisposable
{
}

public class OfStream<St> where St : Stream
{
}

public class OfEquatable<E> where E : IEquatable<E>
{
}

public class OfStrings<L> where L : IEnumerable<string>
{
}

public class RefLikeDisposable<R> where R : IDisposable, allows ref struct
{
}

public class ChainClass<S, R> where S : R where R : class
{
}

public class ChainException<S, R> where S : R where R : Exception
{
}

public class ChainNew<S, R> where S : R where R : new()
{
}

public class ChainDisposable<S, R> where S : R where R : IDisposable
{
}

public class Below<X, W> where X : W
{
}

public static class Program
{
    public static void StructMethod<Q>() where Q : struct
    {
    }

    // `MakeGenericType` with a type parameter as the argument, against a parameter that declares
    // constraints. CoreCLR decides it from what the argument's own constraints prove
    // (`TypeVarTypeDesc::SatisfiesConstraints`, typedesc.cpp): for `struct`, `class` and `new()`,
    // `SatisfiesSpecialConstraintRecursive`, under which `class` and `new()` do not carry through a
    // variable-to-variable constraint; for a general constraint, the argument or one of the types
    // and variables `GatherConstraintsRecursive` collects from its constraints must meet it. A type
    // variable is never refused as byref-like.
    //
    // Exit code is the index of the first failing check, so a failure names itself.
    public static int Main()
    {
        Type u = Formal(typeof(Unconstrained<>));
        Type s = Formal(typeof(Struct<>));
        Type c = Formal(typeof(Class<>));
        Type n = Formal(typeof(New<>));
        Type e = Formal(typeof(OfException<>));
        Type en = Formal(typeof(OfEnum<>));
        Type i = Formal(typeof(OfInterface<>));
        Type st = Formal(typeof(OfStream<>));
        Type q = typeof(Program).GetMethod("StructMethod")!.GetGenericArguments()[0];

        // `struct`.
        if (Admits(typeof(NeedsStruct<>), u)) return 1;
        if (!Admits(typeof(NeedsStruct<>), s)) return 2;
        if (Admits(typeof(NeedsStruct<>), c)) return 3;
        if (!Admits(typeof(NeedsStruct<>), q)) return 4;
        // `Enum` is a class, so a variable it constrains need not be a value type.
        if (Admits(typeof(NeedsStruct<>), en)) return 5;

        // `class`: proved by the flag, or by a class constraint other than `Object`, `ValueType`
        // and the classes deriving from `ValueType`.
        if (!Admits(typeof(NeedsClass<>), c)) return 6;
        if (!Admits(typeof(NeedsClass<>), e)) return 7;
        if (Admits(typeof(NeedsClass<>), en)) return 8;
        if (Admits(typeof(NeedsClass<>), i)) return 9;
        if (Admits(typeof(NeedsClass<>), s)) return 10;
        if (Admits(typeof(NeedsClass<>), u)) return 11;
        // A variable constraining the argument proves `class` by its class constraint, not its flag.
        if (Admits(typeof(NeedsClass<>), Formal(typeof(ChainClass<,>)))) return 12;
        if (!Admits(typeof(NeedsClass<>), Formal(typeof(ChainException<,>)))) return 13;

        // `new()`: proved by the flag, or by `struct`.
        if (!Admits(typeof(NeedsNew<>), n)) return 14;
        if (!Admits(typeof(NeedsNew<>), s)) return 15;
        if (Admits(typeof(NeedsNew<>), u)) return 16;
        if (Admits(typeof(NeedsNew<>), c)) return 17;
        // Not through a variable constraining the argument.
        if (Admits(typeof(NeedsNew<>), Formal(typeof(ChainNew<,>)))) return 18;
        if (Admits(typeof(NeedsClassNew<>), c)) return 19;

        // General constraints.
        if (!Admits(typeof(NeedsDisposable<>), i)) return 20;
        if (!Admits(typeof(NeedsDisposable<>), st)) return 21;
        if (Admits(typeof(NeedsDisposable<>), u)) return 22;
        if (!Admits(typeof(NeedsDisposable<>), Formal(typeof(ChainDisposable<,>)))) return 23;
        if (!Admits(typeof(NeedsException<>), Formal(typeof(ChainException<,>)))) return 24;
        if (!Admits(typeof(NeedsDisposable<>), Formal(typeof(RefLikeDisposable<>)))) return 25;

        // A constraint naming another parameter, read under the instantiation being made.
        if (!Admits(typeof(NeedsSame<,>), u, u)) return 26;
        if (Admits(typeof(NeedsSame<,>), u, c)) return 27;
        Type[] below = typeof(Below<,>).GetGenericArguments();
        if (!Admits(typeof(NeedsSame<,>), below[0], below[1])) return 28;
        if (Admits(typeof(NeedsSame<,>), below[1], below[0])) return 29;
        if (Admits(typeof(NeedsSame<,>), typeof(int), u)) return 30;

        // A constraint mentioning the argument itself.
        if (!Admits(typeof(NeedsEquatable<>), Formal(typeof(OfEquatable<>)))) return 31;
        if (Admits(typeof(NeedsEquatable<>), u)) return 32;

        // Variance.
        if (!Admits(typeof(NeedsObjects<>), Formal(typeof(OfStrings<>)))) return 33;

        // An admitted construction names the variable itself.
        Type made = typeof(NeedsStruct<>).MakeGenericType(s);
        if (!ReferenceEquals(made.GetGenericArguments()[0], s)) return 34;

        return 0;
    }

    static Type Formal(Type definition) => definition.GetGenericArguments()[0];

    // Whether `MakeGenericType` admits the instantiation. A refusal must be the `ArgumentException`
    // naming the violation, wrapping the loader's `TypeLoadException`; anything else escapes.
    static bool Admits(Type definition, params Type[] arguments)
    {
        try
        {
            definition.MakeGenericType(arguments);
            return true;
        }
        catch (ArgumentException ex) when (ex.InnerException is TypeLoadException)
        {
            return false;
        }
    }
}
