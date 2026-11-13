using System;

public class FirstAllowsRef<A, B> where A : allows ref struct
{
}

public class SecondAllowsRef<A, B> where B : allows ref struct
{
}

public class Box<T>
{
}

public class Holder<U>
{
}

namespace Sample
{
    public class FirstAllowsRef<A, B> where A : allows ref struct
    {
    }

    public class Outer
    {
        public class Inner<A, B> where A : allows ref struct
        {
        }
    }
}

public static class Program
{
    // `Generics::CheckInstantiation` (generics.cpp), which CoreCLR's class loader runs before it
    // creates any instantiation: walking the arguments in order, it admits the instantiation at the
    // first type variable, and refuses it at an earlier byref, `void`, pointer, function pointer or
    // `TypedReference`. `MakeGenericType` screens all but `TypedReference` out itself, and .NET 11's
    // VM treats `TypedReference` as an ordinary byref-like value type, so the refusals are .NET 10's
    // alone. Each instantiation here satisfies every constraint, so `MakeGenericType` rethrows the
    // loader's `TypeLoadException` as it is.
    //
    // Exit code is the index of the first failing check, so a failure names itself.
    public static int Main()
    {
        bool refusesTypedReference = Environment.Version.Major < 11;
        Type u = typeof(Holder<>).GetGenericArguments()[0];
        Type tr = typeof(TypedReference);

        // Closed.
        if (Check(typeof(FirstAllowsRef<,>), [tr, typeof(int)], refusesTypedReference, "FirstAllowsRef`2") is int r1) return 10 + r1;

        // `TypedReference` before a type variable.
        if (Check(typeof(FirstAllowsRef<,>), [tr, u], refusesTypedReference, "FirstAllowsRef`2") is int r2) return 20 + r2;

        // After one, the walk has stopped.
        if (Check(typeof(SecondAllowsRef<,>), [u, tr], false, "SecondAllowsRef`2") is int r3) return 30 + r3;

        // An open construction is not a type variable, so the walk goes past it.
        Type boxU = typeof(Box<>).MakeGenericType(u);
        if (Check(typeof(SecondAllowsRef<,>), [boxU, tr], refusesTypedReference, "SecondAllowsRef`2") is int r4) return 40 + r4;

        // Nor is a generic type definition.
        if (Check(typeof(SecondAllowsRef<,>), [typeof(Box<>), tr], refusesTypedReference, "SecondAllowsRef`2") is int r5) return 50 + r5;

        // The type's name carries its namespace.
        if (Check(typeof(Sample.FirstAllowsRef<,>), [tr, u], refusesTypedReference, "Sample.FirstAllowsRef`2") is int r6) return 60 + r6;

        // A nested type's is its own name alone: `Assembly::ThrowTypeLoadException` reads the TypeDef's
        // name and namespace, and a nested TypeDef has no namespace.
        if (Check(typeof(Sample.Outer.Inner<,>), [tr, u], refusesTypedReference, "Inner`2") is int r7) return 70 + r7;

        // The loader refuses before it checks constraints, so where `TypedReference` violates one,
        // the `ArgumentException` naming it wraps the loader's refusal.
        try
        {
            typeof(Box<>).MakeGenericType(tr);
            return 80;
        }
        catch (ArgumentException e)
        {
            if (e.InnerException is not TypeLoadException inner) return 81;
            if (refusesTypedReference && inner.Message != Message("Box`1", typeof(Box<>))) return 82;
        }

        return 0;
    }

    static string Message(string typeName, Type definition) =>
        $"The generic type '{typeName}' was used with an invalid instantiation in assembly '{definition.Assembly.FullName}'.";

    static int? Check(Type definition, Type[] arguments, bool refused, string typeName)
    {
        Type made;
        try
        {
            made = definition.MakeGenericType(arguments);
        }
        catch (TypeLoadException e)
        {
            if (!refused) return 1;
            if (e.TypeName != typeName) return 2;
            if (e.Message != Message(typeName, definition)) return 3;
            return null;
        }

        if (refused) return 4;
        if (!ReferenceEquals(made.GetGenericTypeDefinition(), definition)) return 5;
        Type[] madeArguments = made.GetGenericArguments();
        for (int i = 0; i < arguments.Length; i++)
        {
            if (!ReferenceEquals(madeArguments[i], arguments[i])) return 6 + i;
        }

        return null;
    }
}
