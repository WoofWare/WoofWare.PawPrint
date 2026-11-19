// C# `dynamic`: each operation on a `dynamic` value is a call site that the runtime binder in
// `Microsoft.CSharp` binds on first use. The binder builds its own symbol table from reflection,
// one aggregate per generic *definition*: it asks each definition it loads (`IList<>`,
// `IEnumerable<>`, `List<>` and their kin) for its interfaces with `Type.GetInterfaces()`, and
// reads an instantiation's interfaces by substituting into the definition's.
//
// - Checks 1 and 2 read a property through a dynamic receiver, of a user class and of `string`.
// - Checks 3 and 4 convert a dynamic `List<int>` to `IEnumerable<int>`, implicitly and by
//   overload resolution. The binder decides a conversion from a runtime object with
//   `Type.IsInstanceOfType`, not from its symbol table, so these test the cast and the binding.
// - Checks 5 and 6 call, with a dynamic argument, a method that a receiver of static interface type
//   has only through a base interface: `ICollection<T>.Contains` on an `IList<int>`, and a user
//   interface's base. The binder looks a member of an interface up in the interfaces its symbol
//   table holds for the interface's definition, so a wrong answer for `IList<>` or `IDerived<>`
//   fails to bind.
//
// Each check prints what it bound to and has its own exit code. Every expectation was measured on
// real .NET 10 and 11.

using System;
using System.Collections.Generic;

public class Poco
{
    public int Id { get; set; }
}

public interface IBase<T>
{
    int Get(T x);
}

public interface IDerived<T> : IBase<T> { }

public class Impl : IDerived<int>
{
    public int Get(int x) => x * 10;
}

public static class Program
{
    static int Sum(IEnumerable<int> xs)
    {
        int total = 0;
        foreach (int x in xs) total += x;
        return total;
    }

    // Applicable to anything, so it is chosen only when the `IEnumerable<int>` overload is not.
    static int Sum(object o) => -1;

    static int Check(int code, string what, int actual, int expected)
    {
        Console.WriteLine(what + " = " + actual);
        if (actual == expected) return 0;
        Console.WriteLine("check " + code + ", " + what + ": expected " + expected + ", got " + actual);
        return code;
    }

    public static int Main(string[] args)
    {
        int r;

        // A property of a user class, read through a dynamic call site and converted to `int`.
        dynamic d = new Poco { Id = 41 };
        r = Check(1, "(int)d.Id + 1", (int)d.Id + 1, 42);
        if (r != 0) return r;

        // A property of a CoreLib class.
        dynamic s = "hello";
        r = Check(2, "(int)s.Length", (int)s.Length, 5);
        if (r != 0) return r;

        // An implicit conversion of a dynamic value to an interface its runtime type implements.
        dynamic list = new List<int> { 1, 2, 3 };
        IEnumerable<int> asEnumerable = list;
        r = Check(3, "IEnumerable<int> conversion, summed", Sum(asEnumerable), 6);
        if (r != 0) return r;

        // Overload resolution at run time: `Sum(IEnumerable<int>)` is better than `Sum(object)`
        // only if `List<int>` converts to `IEnumerable<int>`.
        int viaOverload = Sum(list);
        r = Check(4, "Sum(list)", viaOverload, 6);
        if (r != 0) return r;

        // A CoreLib interface's base-interface member: `IList<T>` declares no `Contains`.
        IList<int> asList = new List<int> { 1, 2, 3 };
        dynamic three = 3;
        bool contains = asList.Contains(three);
        r = Check(5, "asList.Contains(three)", contains ? 1 : 0, 1);
        if (r != 0) return r;

        // A user interface's base-interface member: `IDerived<T>` declares no `Get`.
        IDerived<int> derived = new Impl();
        dynamic two = 2;
        int got = derived.Get(two);
        r = Check(6, "derived.Get(two)", got, 20);
        if (r != 0) return r;

        return 0;
    }
}
