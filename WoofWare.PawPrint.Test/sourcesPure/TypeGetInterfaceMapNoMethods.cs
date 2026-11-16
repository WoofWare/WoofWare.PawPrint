using System;
using System.Reflection;

// `Type.GetInterfaceMap` over interfaces which declare no methods: a non-generic one, and the variant
// definition `IInC<>` on a class, which CoreCLR's `RuntimeTypeHandle_VerifyInterfaceIsImplemented`
// accepts as the special marker. Each map holds two empty arrays, and names the two types it was asked
// about.
//
// Measured on real .NET 10 and 11. Returns 0 on success, or the number of the first check that
// failed.

public interface IMarker { }
public class CMarker : IMarker { }
public interface IInC<in T> where T : class { }
public class ClassInC : IInC<object> { }

public static class Program
{
    static int Empty(int code, Type receiver, Type interfaceType)
    {
        InterfaceMapping map = receiver.GetInterfaceMap(interfaceType);
        if (map.TargetType != receiver) return code;
        if (map.InterfaceType != interfaceType) return code + 1;
        if (map.InterfaceMethods.Length != 0) return code + 2;
        if (map.TargetMethods.Length != 0) return code + 3;
        return 0;
    }

    public static int Main()
    {
        int r;
        if ((r = Empty(1, typeof(CMarker), typeof(IMarker))) != 0) return r;
        if ((r = Empty(5, typeof(ClassInC), typeof(IInC<>))) != 0) return r;
        return 0;
    }
}
