// `Type.GetInterfaces()` on a closed type returns the type's interface map as CoreCLR builds it
// (`MethodTableBuilder::LoadApproxInterfaceMap`): the base class's entries first, in the base's
// order, then each interface the type declares followed by that interface's own parents. An
// entry is kept apart from another that names the same closed type when the two are different
// instantiations of the generic definition's interfaces, so a closed type can list one interface
// twice.
//
// Each check compares the whole list, element by element and by reference, with an expectation
// built from `typeof`, so a wrong order, a merged duplicate or a missing entry fails that check
// alone, with its own exit code. Every expectation was measured on real .NET 10 and 11.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;

public interface IA<T> { }
public interface IB { }
public interface IC<T> : IA<T>, IB { }
public interface IPair<A, B> { }

public class BaseA<T> : IA<T> { }

public class ClosedBase : IB { }
public class ClosedDerived : ClosedBase, IA<string> { }

public class TwoBase<T> : IA<T>, IB { }
public class TwoDerived<T> : TwoBase<T>, IC<T> { }

// The base's `IA<T>` and the type's own `IA<U>` are different instantiations of the definition's
// interfaces, whatever `T` and `U` are.
public class Dup<T, U> : BaseA<T>, IA<U> { }
public class Mix2<T> : BaseA<int>, IA<T> { }

// Re-declaring an interface the base already implements adds no second entry.
public class Redecl<T> : BaseA<T>, IA<T>, IB { }

public class Own<T> : IEnumerable<T>, IPair<T, int>
{
    public IEnumerator<T> GetEnumerator() => null;
    IEnumerator IEnumerable.GetEnumerator() => null;
}

public class Derived<U> : Own<U[]>, IComparable<Derived<U>>
{
    public int CompareTo(Derived<U> other) => 0;
}

public struct SOwn<T> : IA<T>, IC<T> { }

public enum Colour { Red, Green }

public static class Program
{
    static string Render(Type[] types)
    {
        string[] names = new string[types.Length];
        for (int i = 0; i < types.Length; i++) names[i] = types[i].ToString();
        return "[" + string.Join(", ", names) + "]";
    }

    // 0 if `receiver.GetInterfaces()` is `expected`, element for element; otherwise `code`, having
    // printed both.
    static int Check(int code, Type receiver, params Type[] expected)
    {
        Type[] actual = receiver.GetInterfaces();
        bool same = actual.Length == expected.Length;
        for (int i = 0; same && i < actual.Length; i++)
        {
            same = ReferenceEquals(actual[i], expected[i]);
        }

        if (same) return 0;
        Console.WriteLine("check " + code + ", " + receiver + ": expected " + Render(expected) + ", got " + Render(actual));
        return code;
    }

    public static int Main()
    {
        int r;

        // The base class's entries come before the type's own.
        if ((r = Check(1, typeof(ClosedDerived), typeof(IB), typeof(IA<string>))) != 0) return r;
        if ((r = Check(2, typeof(TwoDerived<int>), typeof(IA<int>), typeof(IB), typeof(IC<int>))) != 0) return r;

        // Duplicates are kept.
        if ((r = Check(3, typeof(Dup<int, int>), typeof(IA<int>), typeof(IA<int>))) != 0) return r;
        if ((r = Check(4, typeof(Dup<int, string>), typeof(IA<int>), typeof(IA<string>))) != 0) return r;
        if ((r = Check(5, typeof(Mix2<int>), typeof(IA<int>), typeof(IA<int>))) != 0) return r;
        if ((r = Check(6, typeof(Redecl<int>), typeof(IA<int>), typeof(IB))) != 0) return r;

        // A base over the type's own variable, then the type's own row over itself.
        if ((r = Check(7, typeof(Derived<int>),
                typeof(IEnumerable<int[]>), typeof(IEnumerable), typeof(IPair<int[], int>), typeof(IComparable<Derived<int>>))) != 0) return r;

        // CoreLib: a delegate's base re-declares `ISerializable` after `Delegate`'s `ICloneable`, and
        // `Type`'s `IReflect` comes after `MemberInfo`'s `ICustomAttributeProvider`.
        if ((r = Check(8, typeof(Action), typeof(ICloneable), typeof(ISerializable))) != 0) return r;
        if ((r = Check(9, typeof(Type), typeof(ICustomAttributeProvider), typeof(IReflect))) != 0) return r;

        // Interfaces: each row, then its own parents.
        if ((r = Check(10, typeof(IC<int>), typeof(IA<int>), typeof(IB))) != 0) return r;
        if ((r = Check(11, typeof(IList<int>), typeof(ICollection<int>), typeof(IEnumerable<int>), typeof(IEnumerable))) != 0) return r;

        // A struct outside CoreLib expands its rows; an enum answers `Enum`'s map.
        if ((r = Check(12, typeof(SOwn<int>), typeof(IA<int>), typeof(IC<int>), typeof(IB))) != 0) return r;
        if ((r = Check(13, typeof(Colour), typeof(Enum).GetInterfaces())) != 0) return r;
        if ((r = Check(14, typeof(Enum),
                typeof(IComparable), typeof(ISpanFormattable), typeof(IFormattable), typeof(IConvertible))) != 0) return r;

        return 0;
    }
}
