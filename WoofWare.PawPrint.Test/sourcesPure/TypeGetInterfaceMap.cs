using System;
using System.Collections.Generic;
using System.Reflection;

// `Type.GetInterfaceMap` over interfaces which declare methods: a class implementing one directly, and
// `List<string>` against `IEnumerable<object>`, which it implements only by variance. Each map has one
// method on each side.
//
// Measured on real .NET 10 and 11. Returns 0 on success, or the number of the first check that
// failed.

public interface IOne { void M(); }
public class COne : IOne { public void M() { } }

public static class Program
{
    public static int Main()
    {
        InterfaceMapping one = typeof(COne).GetInterfaceMap(typeof(IOne));
        if (one.InterfaceMethods.Length != 1) return 1;
        if (one.TargetMethods.Length != 1) return 2;
        if (one.InterfaceMethods[0].Name != "M") return 3;
        if (one.TargetMethods[0].DeclaringType != typeof(COne)) return 4;

        InterfaceMapping variant = typeof(List<string>).GetInterfaceMap(typeof(IEnumerable<object>));
        if (variant.InterfaceMethods.Length != 1) return 5;
        if (variant.TargetMethods.Length != 1) return 6;

        return 0;
    }
}
