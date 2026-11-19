// `Type.GetInterfaces()` on an open receiver -- a generic definition, an open construction, or the
// open base of a definition -- returns the receiver's interface map as CoreCLR builds it for the
// generic definition (`MethodTableBuilder::LoadApproxInterfaceMap`), each entry read over the
// receiver's own arguments: the base class's entries first, then each declared interface followed by
// its own parents, with an entry kept apart from another that the receiver's arguments make equal.
//
// An entry over the definition's own variables is an open construction over the *receiver's*
// variables, and one whose argument is the definition applied to its own variables is that
// definition (`typeof(Derived<>)`), as CoreCLR represents a typical instantiation. An interface
// without generic arguments, or one whose arguments are all closed, is the closed type.
//
// Each check compares the whole list, element by element and by reference, with an expectation built
// from `typeof` and `MakeGenericType` (or, where `MakeGenericType` would have to check a constraint
// against an open argument, from each entry's definition and arguments), so a wrong order, a merged
// duplicate, a missing entry or a wrong identity fails that check alone, with its own exit code.
// Every expectation was measured on real .NET 10 and 11.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.Serialization;

public interface IA<T> { }
public interface IB { }
public interface IC<T> : IA<T>, IB { }
public interface IPair<A, B> { }

public class BaseA<T> : IA<T> { }

public class Plain<T> : IDisposable
{
    public void Dispose() { }
}

public class Own<T> : IEnumerable<T>, IPair<T, int>
{
    public IEnumerator<T> GetEnumerator() => null;
    IEnumerator IEnumerable.GetEnumerator() => null;
}

public class Derived<U> : Own<U[]>, IComparable<Derived<U>>
{
    public int CompareTo(Derived<U> other) => 0;
}

// Closed and open rows, in row order.
public class Mixed<T> : IA<int>, IPair<T, int>, IB, IA<T[]> { }

public struct SSelf<T> : IEquatable<SSelf<T>>, IComparable<SSelf<T>>
{
    public bool Equals(SSelf<T> other) => true;
    public int CompareTo(SSelf<T> other) => 0;
}

public interface IX<T> : IC<T>, IEquatable<IX<T>> { }

// The base's `IA<T>` and the type's own `IA<U>` are different instantiations of the definition's
// interfaces, whatever `T` and `U` are.
public class Dup<T, U> : BaseA<T>, IA<U> { }
public class Redecl<T> : BaseA<T>, IA<T>, IB { }
public class Mix2<T> : BaseA<int>, IA<T> { }

public class Outer<T>
{
    public class Inner<U> : IPair<T, U> { }
}

public interface ISelfIface<TSelf> : IEquatable<TSelf>, IA<ISelfIface<TSelf>> where TSelf : ISelfIface<TSelf> { }

public struct SCrtp<T> : ISelfIface<SCrtp<T>>
{
    public bool Equals(SCrtp<T> other) => true;
}

public class ClosedBase : IB { }
public class OverClosed<T> : ClosedBase, IDisposable
{
    public void Dispose() { }
}

public class X<T> : IC<X<T>> { }

public static class Program
{
    static string Render(Type[] types)
    {
        string[] names = new string[types.Length];
        for (int i = 0; i < types.Length; i++) names[i] = types[i].ToString();
        return "[" + string.Join(", ", names) + "]";
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

    static int Check(int code, Type receiver, params Type[] expected) =>
        Same(code, receiver.ToString(), receiver.GetInterfaces(), expected);

    // 0 if `actual` is an instantiation of `definition` whose arguments are `arguments`, by reference.
    static bool IsOver(Type actual, Type definition, params Type[] arguments)
    {
        if (!actual.IsConstructedGenericType || !ReferenceEquals(actual.GetGenericTypeDefinition(), definition))
            return false;
        Type[] actualArguments = actual.GetGenericArguments();
        if (actualArguments.Length != arguments.Length) return false;
        for (int i = 0; i < arguments.Length; i++)
        {
            if (!ReferenceEquals(actualArguments[i], arguments[i])) return false;
        }

        return true;
    }

    static Type Over(Type definition, params Type[] arguments) => definition.MakeGenericType(arguments);

    static Type[] ListInterfaces(Type t) => new[]
    {
        Over(typeof(IList<>), t),
        Over(typeof(ICollection<>), t),
        Over(typeof(IEnumerable<>), t),
        typeof(IEnumerable),
        typeof(IList),
        typeof(ICollection),
        Over(typeof(IReadOnlyList<>), t),
        Over(typeof(IReadOnlyCollection<>), t),
    };

    public static List<TM> M<TM>() => null;

    public static int Main()
    {
        int r;

        Type[] dictionaryVariables = typeof(Dictionary<,>).GetGenericArguments();
        Type k = dictionaryVariables[0];
        Type v = dictionaryVariables[1];

        // A closed row of a definition.
        if ((r = Check(1, typeof(Plain<>), typeof(IDisposable))) != 0) return r;

        // Rows over the definition's own variable; a non-generic interface is the closed type.
        Type ownT = typeof(Own<>).GetGenericArguments()[0];
        if ((r = Check(2, typeof(Own<>),
                Over(typeof(IEnumerable<>), ownT), typeof(IEnumerable), Over(typeof(IPair<,>), ownT, typeof(int)))) != 0) return r;

        // A base over the type's own variable, then the type's own row over itself, whose argument is
        // the definition.
        Type derivedU = typeof(Derived<>).GetGenericArguments()[0];
        Type derivedUArray = derivedU.MakeArrayType();
        if ((r = Check(3, typeof(Derived<>),
                Over(typeof(IEnumerable<>), derivedUArray), typeof(IEnumerable), Over(typeof(IPair<,>), derivedUArray, typeof(int)),
                Over(typeof(IComparable<>), typeof(Derived<>)))) != 0) return r;

        // The definition's open base, `Own<U[]>` over `Derived`'s `U`.
        if ((r = Check(4, typeof(Derived<>).BaseType,
                Over(typeof(IEnumerable<>), derivedUArray), typeof(IEnumerable), Over(typeof(IPair<,>), derivedUArray, typeof(int)))) != 0) return r;

        // CoreLib definitions.
        Type listT = typeof(List<>).GetGenericArguments()[0];
        if ((r = Check(5, typeof(List<>), ListInterfaces(listT))) != 0) return r;

        Type ilistT = typeof(IList<>).GetGenericArguments()[0];
        if ((r = Check(6, typeof(IList<>),
                Over(typeof(ICollection<>), ilistT), Over(typeof(IEnumerable<>), ilistT), typeof(IEnumerable))) != 0) return r;

        Type pair = Over(typeof(KeyValuePair<,>), k, v);
        if ((r = Check(7, typeof(Dictionary<,>),
                Over(typeof(IDictionary<,>), k, v),
                Over(typeof(ICollection<>), pair),
                Over(typeof(IEnumerable<>), pair),
                typeof(IEnumerable),
                typeof(IDictionary),
                typeof(ICollection),
                Over(typeof(IReadOnlyDictionary<,>), k, v),
                Over(typeof(IReadOnlyCollection<>), pair),
                typeof(ISerializable),
                typeof(IDeserializationCallback))) != 0) return r;

        Type mixedT = typeof(Mixed<>).GetGenericArguments()[0];
        if ((r = Check(8, typeof(Mixed<>),
                typeof(IA<int>), Over(typeof(IPair<,>), mixedT, typeof(int)), typeof(IB), Over(typeof(IA<>), mixedT.MakeArrayType()))) != 0) return r;

        if ((r = Check(9, typeof(SSelf<>),
                Over(typeof(IEquatable<>), typeof(SSelf<>)), Over(typeof(IComparable<>), typeof(SSelf<>)))) != 0) return r;

        Type ixT = typeof(IX<>).GetGenericArguments()[0];
        if ((r = Check(10, typeof(IX<>),
                Over(typeof(IC<>), ixT), Over(typeof(IA<>), ixT), typeof(IB), Over(typeof(IEquatable<>), typeof(IX<>)))) != 0) return r;

        // Duplicates: two instantiations of the definition's interfaces, kept apart even where the
        // receiver's arguments make them one type.
        Type[] dupVariables = typeof(Dup<,>).GetGenericArguments();
        if ((r = Check(11, typeof(Dup<,>), Over(typeof(IA<>), dupVariables[0]), Over(typeof(IA<>), dupVariables[1]))) != 0) return r;
        if ((r = Check(12, Over(typeof(Dup<,>), k, k), Over(typeof(IA<>), k), Over(typeof(IA<>), k))) != 0) return r;

        Type redeclT = typeof(Redecl<>).GetGenericArguments()[0];
        if ((r = Check(13, typeof(Redecl<>), Over(typeof(IA<>), redeclT), typeof(IB))) != 0) return r;

        Type mix2T = typeof(Mix2<>).GetGenericArguments()[0];
        if ((r = Check(14, typeof(Mix2<>), typeof(IA<int>), Over(typeof(IA<>), mix2T))) != 0) return r;

        // A nested definition: both variables are `Inner`'s own.
        Type[] innerVariables = typeof(Outer<>.Inner<>).GetGenericArguments();
        if ((r = Check(15, typeof(Outer<>.Inner<>), Over(typeof(IPair<,>), innerVariables[0], innerVariables[1]))) != 0) return r;

        // `ISelfIface<>` constrains its argument, so the expectation is read from each entry.
        Type[] crtp = typeof(SCrtp<>).GetInterfaces();
        if (crtp.Length != 3
            || !IsOver(crtp[0], typeof(ISelfIface<>), typeof(SCrtp<>))
            || !ReferenceEquals(crtp[1], Over(typeof(IEquatable<>), typeof(SCrtp<>)))
            || !IsOver(crtp[2], typeof(IA<>), crtp[0]))
        {
            Console.WriteLine("check 16: got " + Render(crtp));
            return 16;
        }

        // An open construction of a CoreLib definition.
        if ((r = Check(17, Over(typeof(List<>), k), ListInterfaces(k))) != 0) return r;

        // A definition applied to its own variables is the definition.
        Type typical = Over(typeof(List<>), listT);
        if (!ReferenceEquals(typical, typeof(List<>))) return 18;
        if ((r = Check(118, typical, ListInterfaces(listT))) != 0) return r;

        Type first = typeof(List<>).GetInterfaces()[0];
        if (!first.ContainsGenericParameters || first.IsGenericTypeDefinition) return 19;
        if (!ReferenceEquals(first.GetGenericArguments()[0], listT)) return 119;

        // A generic delegate's base, `MulticastDelegate`, has interfaces, which come first.
        if ((r = Check(20, typeof(Action<>), typeof(ICloneable), typeof(ISerializable))) != 0) return r;
        if ((r = Check(21, typeof(OverClosed<>), typeof(IB), typeof(IDisposable))) != 0) return r;

        // An open construction over a generic method's variable.
        Type listOfTm = typeof(Program).GetMethod("M").ReturnType;
        Type tm = listOfTm.GetGenericArguments()[0];
        if (!tm.IsGenericMethodParameter) return 22;
        if ((r = Check(122, listOfTm, ListInterfaces(tm))) != 0) return r;

        // An open construction of an interface.
        if ((r = Check(23, Over(typeof(IC<>), k), Over(typeof(IA<>), k), typeof(IB))) != 0) return r;

        // An interface whose argument is a definition: `X<>`'s `IC<X<>>`, whose own map is over `X<>`.
        Type xIc = typeof(X<>).GetInterfaces()[0];
        if (!ReferenceEquals(xIc, Over(typeof(IC<>), typeof(X<>)))) return 24;
        if ((r = Check(124, xIc, Over(typeof(IA<>), typeof(X<>)), typeof(IB))) != 0) return r;

        return 0;
    }
}
