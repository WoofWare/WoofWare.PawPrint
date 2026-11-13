using System;
using System.Reflection;

public class Box<T>
{
}

public class Pair<TFirst, TSecond>
{
}

public ref struct RefBox<T>
{
}

public class Holder<U>
{
    public static void TakesBox(Box<U> box)
    {
    }

    public static void TakesMixed(Pair<int, U> pair)
    {
    }
}

public static class Program
{
    public static void Method<M>()
    {
    }

    // `MakeGenericType` accepts arguments that are themselves open: a type parameter, a method
    // type parameter, an open construction, or a generic type definition (which stands for its
    // typical instantiation). The result is an open constructed type, the same `Type` object a
    // signature naming that construction reflects as.
    //
    // Exit code is the index of the first failing check, so a failure names itself.
    public static int Main()
    {
        Type u = typeof(Holder<>).GetGenericArguments()[0];

        // A type parameter of another definition.
        Type boxU = typeof(Box<>).MakeGenericType(u);
        if (!boxU.IsGenericType) return 1;
        if (boxU.IsGenericTypeDefinition) return 2;
        if (!boxU.ContainsGenericParameters) return 3;
        if (!ReferenceEquals(boxU.GetGenericTypeDefinition(), typeof(Box<>))) return 4;
        Type[] boxUArgs = boxU.GetGenericArguments();
        if (boxUArgs.Length != 1) return 5;
        if (!ReferenceEquals(boxUArgs[0], u)) return 6;

        // The same `Type` as the signature of a parameter spelling `Box<U>`, and as a second request.
        if (!ReferenceEquals(boxU, ParameterType("TakesBox"))) return 7;
        if (!ReferenceEquals(boxU, typeof(Box<>).MakeGenericType(u))) return 8;

        // A definition applied to its own formals is the definition itself.
        Type t = typeof(Box<>).GetGenericArguments()[0];
        if (!ReferenceEquals(typeof(Box<>).MakeGenericType(t), typeof(Box<>))) return 9;

        // Closed and open arguments together, through the multi-argument buffer.
        Type mixed = typeof(Pair<,>).MakeGenericType(typeof(int), u);
        if (!ReferenceEquals(mixed, ParameterType("TakesMixed"))) return 10;
        Type[] mixedArgs = mixed.GetGenericArguments();
        if (mixedArgs[0] != typeof(int)) return 11;
        if (!ReferenceEquals(mixedArgs[1], u)) return 12;

        // A method type parameter.
        Type m = typeof(Program).GetMethod("Method")!.GetGenericArguments()[0];
        Type boxM = typeof(Box<>).MakeGenericType(m);
        if (!boxM.ContainsGenericParameters) return 13;
        if (!ReferenceEquals(boxM.GetGenericArguments()[0], m)) return 14;
        if (ReferenceEquals(boxM, boxU)) return 15;

        // An open construction as the argument.
        Type boxBoxU = typeof(Box<>).MakeGenericType(boxU);
        if (!ReferenceEquals(boxBoxU.GetGenericArguments()[0], boxU)) return 16;

        // A generic type definition as the argument: `Box<Pair<,>>` is `Box<Pair<TFirst, TSecond>>`.
        Type boxPair = typeof(Box<>).MakeGenericType(typeof(Pair<,>));
        if (!boxPair.ContainsGenericParameters) return 17;
        if (!ReferenceEquals(boxPair.GetGenericArguments()[0], typeof(Pair<,>))) return 18;

        // An open construction of a byref-like type is byref-like, and an unconstrained parameter
        // does not admit it. CoreCLR raises `TypeLoadException` at the load, and `MakeGenericType`
        // rewraps it as `ArgumentException` naming the refused argument.
        Type refBoxU = typeof(RefBox<>).MakeGenericType(u);
        if (!refBoxU.IsByRefLike) return 19;
        try
        {
            typeof(Box<>).MakeGenericType(refBoxU);
            return 20;
        }
        catch (ArgumentException e)
        {
            if (e.InnerException is not TypeLoadException) return 21;
        }

        // A type parameter is never byref-like itself, so it is admitted.
        Type refBoxBox = typeof(RefBox<>).MakeGenericType(boxU);
        if (!refBoxBox.IsByRefLike) return 22;

        return 0;
    }

    static Type ParameterType(string method) =>
        typeof(Holder<>).GetMethod(method)!.GetParameters()[0].ParameterType;
}
