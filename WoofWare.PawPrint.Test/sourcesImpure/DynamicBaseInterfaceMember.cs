// C# `dynamic` calling, with a dynamic argument, a method that the receiver's static interface type
// has only through a base interface: `ICollection<T>.Contains` on an `IList<int>`, and a user
// interface's base on an `IDerived<int>`. The runtime binder in `Microsoft.CSharp` builds its own
// symbol table from reflection, one aggregate per generic *definition*: it asks each definition it
// loads for its interfaces with `Type.GetInterfaces()`, and looks a member of an interface up in the
// interfaces its symbol table holds for the interface's definition. So a wrong answer for `IList<>`
// or `IDerived<>` fails to bind.
//
// Neither receiver's class has the member as a public method of its own (an array's
// `ICollection<T>.Contains` comes from the runtime, and `Impl` implements `Get` explicitly), so a
// binder that looked the member up on the run-time class instead would fail to bind too.
//
// It is impure because it needs `IsDynamicCodeSupported` true, which is what real .NET runs with.
//
// Each check prints what it bound to and has its own exit code. Every expectation was measured on
// real .NET 10 and 11.

using System;
using System.Collections.Generic;

public interface IBase<T>
{
    int Get(T x);
}

public interface IDerived<T> : IBase<T> { }

public class Impl : IDerived<int>
{
    int IBase<int>.Get(int x) => x * 10;
}

public static class Program
{
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

        // A CoreLib interface's base-interface member: `IList<T>` declares no `Contains`.
        IList<int> asList = new int[] { 1, 2, 3 };
        dynamic three = 3;
        bool contains = asList.Contains(three);
        r = Check(1, "asList.Contains(three)", contains ? 1 : 0, 1);
        if (r != 0) return r;

        // A user interface's base-interface member: `IDerived<T>` declares no `Get`.
        IDerived<int> derived = new Impl();
        dynamic two = 2;
        int got = derived.Get(two);
        r = Check(2, "derived.Get(two)", got, 20);
        if (r != 0) return r;

        return 0;
    }
}
