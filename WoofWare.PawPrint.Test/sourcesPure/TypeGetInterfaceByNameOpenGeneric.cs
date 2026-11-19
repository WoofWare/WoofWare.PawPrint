// Two ordinary calls ask `RuntimeTypeHandle.GetUtf8Name` for the name of an open construction, such
// as `IList<T>` over `List<>`'s `T`:
//
// - `Type.GetInterface(name)` on an open receiver: `RuntimeTypeCache.PopulateInterfaces` names every
//   interface in the receiver's list, and an open receiver's interfaces are open constructions.
// - `GetInterfaces()` on an SZ array over a variable: after the QCall's `System.Array` interfaces,
//   `AddSpecialInterface` names each generic interface it appends (`IList<T>` and so on).
//
// CoreCLR's `RuntimeTypeHandle::GetUtf8Name` reads the TypeDef row that `pMT->GetCl()` names, which
// for any instantiation, open or closed, is the generic definition's: so the answer is the
// definition's short name with its arity suffix (``IList`1``).
//
// Each check compares by reference with an expectation built from `typeof` and `MakeGenericType`, so
// a wrong entry, a missed one or an ambiguity fails that check alone, with its own exit code. Every
// expectation was measured on real .NET 10 and 11.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

public interface IA<T> { }
public interface IB { }
public interface IPair<A, B> { }

public class Own<T> : IEnumerable<T>, IPair<T, int>
{
    public IEnumerator<T> GetEnumerator() => null;
    IEnumerator IEnumerable.GetEnumerator() => null;
}

// Two entries named ``IA`1``, one closed and one open.
public class Mixed<T> : IA<int>, IPair<T, int>, IB, IA<T[]> { }

public class Outer<T>
{
    public class Inner<U> : IPair<T, U> { }
}

public class Holder
{
    public interface INested<T> { }
}

public class UsesNested<T> : Holder.INested<T> { }

public static class Program
{
    static string Render(Type[] types)
    {
        string[] names = new string[types.Length];
        for (int i = 0; i < types.Length; i++) names[i] = types[i].ToString();
        return "[" + string.Join(", ", names) + "]";
    }

    // 0 if `actual` is `expected`, by reference; otherwise `code`, having printed both.
    static int Is(int code, string what, Type actual, Type expected)
    {
        if (ReferenceEquals(actual, expected)) return 0;
        Console.WriteLine("check " + code + ", " + what + ": expected " + (expected?.ToString() ?? "null") + ", got " + (actual?.ToString() ?? "null"));
        return code;
    }

    // 0 if `actual` is `expected`, element for element and by reference; otherwise `code`, having
    // printed both.
    static int Same(int code, string what, Type[] actual, params Type[] expected)
    {
        bool same = actual.Length == expected.Length;
        for (int i = 0; same && i < actual.Length; i++)
        {
            same = ReferenceEquals(actual[i], expected[i]);
        }

        if (same) return 0;
        Console.WriteLine("check " + code + ", " + what + ": expected " + Render(expected) + ", got " + Render(actual));
        return code;
    }

    static Type Over(Type definition, params Type[] arguments) => definition.MakeGenericType(arguments);

    // An SZ array's interfaces: `System.Array`'s six from the QCall, then the five generic ones
    // managed code appends over the element.
    static Type[] SzArrayInterfaces(Type element) => new[]
    {
        typeof(ICloneable), typeof(IList), typeof(ICollection), typeof(IEnumerable),
        typeof(IStructuralComparable), typeof(IStructuralEquatable),
        Over(typeof(IList<>), element), Over(typeof(ICollection<>), element),
        Over(typeof(IEnumerable<>), element), Over(typeof(IReadOnlyList<>), element),
        Over(typeof(IReadOnlyCollection<>), element),
    };

    public static List<TM> M<TM>() => null;

    public static int Main()
    {
        int r;

        Type listT = typeof(List<>).GetGenericArguments()[0];
        Type listIList = Over(typeof(IList<>), listT);

        // By name on a CoreLib definition: the map's first entry, which is `IList<>` over `List<>`'s
        // `T`.
        if ((r = Is(1, "List<>.GetInterface(IList`1)", typeof(List<>).GetInterface("IList`1"), typeof(List<>).GetInterfaces()[0])) != 0) return r;
        if ((r = Is(2, "List<>.GetInterface(IList`1)", typeof(List<>).GetInterface("IList`1"), listIList)) != 0) return r;

        // The namespace is compared after the name; a wrong one misses.
        if ((r = Is(3, "List<>.GetInterface(System.Collections.Generic.IList`1)",
                typeof(List<>).GetInterface("System.Collections.Generic.IList`1"), listIList)) != 0) return r;
        if ((r = Is(4, "List<>.GetInterface(System.IList`1)", typeof(List<>).GetInterface("System.IList`1"), null)) != 0) return r;

        // The non-generic `IList` is a different name; ``IList`2`` and the unmangled `IEnumerable<T>`
        // name nothing.
        if ((r = Is(5, "List<>.GetInterface(IList)", typeof(List<>).GetInterface("IList"), typeof(IList))) != 0) return r;
        if ((r = Is(6, "List<>.GetInterface(IList`2)", typeof(List<>).GetInterface("IList`2"), null)) != 0) return r;
        if ((r = Is(7, "List<>.GetInterface(IEnumerable<T>)", typeof(List<>).GetInterface("IEnumerable<T>"), null)) != 0) return r;

        // By name on a user definition with an open row.
        Type ownT = typeof(Own<>).GetGenericArguments()[0];
        if ((r = Is(8, "Own<>.GetInterface(IPair`2)", typeof(Own<>).GetInterface("IPair`2"), Over(typeof(IPair<,>), ownT, typeof(int)))) != 0) return r;
        if ((r = Is(9, "Own<>.GetInterface(IEnumerable`1)", typeof(Own<>).GetInterface("IEnumerable`1"), Over(typeof(IEnumerable<>), ownT))) != 0) return r;

        // Two entries of one name, the closed `IA<int>` and the open `IA<T[]>`, are ambiguous.
        try
        {
            Type found = typeof(Mixed<>).GetInterface("IA`1");
            Console.WriteLine("check 10: expected AmbiguousMatchException, got " + (found?.ToString() ?? "null"));
            return 10;
        }
        catch (AmbiguousMatchException)
        {
        }

        Type mixedT = typeof(Mixed<>).GetGenericArguments()[0];
        if ((r = Is(11, "Mixed<>.GetInterface(IPair`2)", typeof(Mixed<>).GetInterface("IPair`2"), Over(typeof(IPair<,>), mixedT, typeof(int)))) != 0) return r;

        // A nested definition's name is its own, without its declaring type's.
        Type[] innerArguments = typeof(Outer<>.Inner<>).GetGenericArguments();
        if ((r = Is(12, "Outer<>.Inner<>.GetInterface(IPair`2)", typeof(Outer<>.Inner<>).GetInterface("IPair`2"), Over(typeof(IPair<,>), innerArguments))) != 0) return r;

        // An open construction as the receiver: `List<>` over `Dictionary<,>`'s `K`.
        Type k = typeof(Dictionary<,>).GetGenericArguments()[0];
        if ((r = Is(13, "List<K>.GetInterface(IEnumerable`1)", Over(typeof(List<>), k).GetInterface("IEnumerable`1"), Over(typeof(IEnumerable<>), k))) != 0) return r;

        // `List<>` over `IList<>`'s own `T`: its `IList<>` entry is `IList<>` applied to its own
        // variable, which is the definition `typeof(IList<>)`.
        Type ilistT = typeof(IList<>).GetGenericArguments()[0];
        if ((r = Is(14, "List<IList's T>.GetInterface(IList`1)", Over(typeof(List<>), ilistT).GetInterface("IList`1"), typeof(IList<>))) != 0) return r;

        // Arrays over a variable: `System.Array`'s six, then the five generic interfaces over the
        // element, in order.
        Type listTArray = listT.MakeArrayType();
        if ((r = Same(15, "List<>'s T[]", listTArray.GetInterfaces(), SzArrayInterfaces(listT))) != 0) return r;

        Type listK = Over(typeof(List<>), k);
        if ((r = Same(16, "List<K>[]", listK.MakeArrayType().GetInterfaces(), SzArrayInterfaces(listK))) != 0) return r;

        Type tm = typeof(Program).GetMethod("M").GetGenericArguments()[0];
        if ((r = Same(17, "M<TM>'s TM[]", tm.MakeArrayType().GetInterfaces(), SzArrayInterfaces(tm))) != 0) return r;

        // By name on an array over a variable.
        if ((r = Is(18, "List<>'s T[].GetInterface(IReadOnlyList`1)", listTArray.GetInterface("IReadOnlyList`1"), Over(typeof(IReadOnlyList<>), listT))) != 0) return r;

        // Over `IList<>`'s own `T`, the first generic interface appended is `IList<>` applied to its
        // own variable, which is the definition `typeof(IList<>)`.
        Type ilistTArray = ilistT.MakeArrayType();
        if ((r = Is(19, "IList<>'s T[], seventh interface", ilistTArray.GetInterfaces()[6], typeof(IList<>))) != 0) return r;
        if ((r = Same(20, "IList<>'s T[]", ilistTArray.GetInterfaces(), SzArrayInterfaces(ilistT))) != 0) return r;

        // A nested interface's name is its own, without its declaring type's.
        Type usesNestedT = typeof(UsesNested<>).GetGenericArguments()[0];
        if ((r = Is(21, "UsesNested<>.GetInterface(INested`1)", typeof(UsesNested<>).GetInterface("INested`1"), Over(typeof(Holder.INested<>), usesNestedT))) != 0) return r;

        return 0;
    }
}
