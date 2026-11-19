// C# `dynamic`: each operation on a `dynamic` value is a call site that the runtime binder in
// `Microsoft.CSharp` binds on first use, and whose rules `System.Linq.Expressions` compiles. The
// binder builds its own symbol table from reflection, and asks each generic definition it loads
// (`List<>`, `IEnumerable<>` and their kin) for its interfaces with `Type.GetInterfaces()`.
//
// These four checks do not depend on what it is told: the binder decides a conversion from a
// run-time object with `Type.IsInstanceOfType`, not from its symbol table, and checks 1 and 2 read
// members the receiver's class declares itself. So this guest tests that the binder runs at all, not
// what `GetInterfaces` answers. `DynamicBaseInterfaceMember.cs` holds the checks that do depend on
// the answers.
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

        return 0;
    }
}
