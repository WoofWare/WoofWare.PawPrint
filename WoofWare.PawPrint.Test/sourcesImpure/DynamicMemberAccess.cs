// C# `dynamic`: each operation on a `dynamic` value is a call site that the runtime binder in
// `Microsoft.CSharp` binds on first use, and whose rules `System.Linq.Expressions` compiles. The
// binder builds its own symbol table from reflection, and asks each generic definition it loads
// (`List<>`, `IEnumerable<>` and their kin) for its interfaces with `Type.GetInterfaces()`.
//
// Checks 1 to 4 do not depend on what it is told: the binder decides a conversion from a run-time
// object with `Type.IsInstanceOfType`, not from its symbol table, and checks 1 and 2 read members
// the receiver's class declares itself. They test that the binder runs at all. Check 5 does depend
// on it: to infer `T` in `CountOf<T>(IEnumerable<T>)` from a `List<int>`, the binder reads
// `List<int>`'s interfaces from those its symbol table holds for `List<>`, and without
// `IEnumerable<T>` among them it chooses `CountOf(object)`. `DynamicBaseInterfaceMember.cs` holds
// checks that look a member up through a base interface.
//
// It is impure because it needs `IsDynamicCodeSupported` true, which is what real .NET runs with.
// With PawPrint's default, false, the first call site stops before the binder runs (#2349).
//
// Each check prints what it bound to and has its own exit code. Every expectation was measured on
// real .NET 10 and 11.

using System;
using System.Collections.Generic;

public class Poco
{
    public int Id { get; set; }
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

    static int CountOf<T>(IEnumerable<T> xs)
    {
        int count = 0;
        foreach (T x in xs) count++;
        return count;
    }

    // Chosen only when `T` cannot be inferred for the generic overload.
    static int CountOf(object o) => -1;

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

        // Type inference at run time: `T` is inferred from the interfaces the binder's symbol table
        // gives `List<int>`, which it substitutes into those `GetInterfaces` gave for `List<>`.
        int viaInference = CountOf(list);
        r = Check(5, "CountOf(list)", viaInference, 3);
        if (r != 0) return r;

        return 0;
    }
}
