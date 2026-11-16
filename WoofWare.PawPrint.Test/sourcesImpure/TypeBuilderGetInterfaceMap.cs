using System;
using System.Reflection;
using System.Reflection.Emit;

// `Type.GetInterfaceMap` over types created with `TypeBuilder`: a class implementing `IOne.M` through a
// MethodImpl naming a private method of another name (the shape a proxy takes), a class implementing it
// implicitly by name, and an abstract class that lists `IOne` and implements nothing, whose row has no
// target. Measured on real .NET 10 and 11 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public interface IOne { void M(); }

public static class Program
{
    const MethodAttributes PrivateFinal =
        MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot;

    const MethodAttributes PublicFinal =
        MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot;

    static MethodBuilder Empty(TypeBuilder type, string name, MethodAttributes attributes)
    {
        MethodBuilder method = type.DefineMethod(name, attributes, typeof(void), Type.EmptyTypes);
        method.GetILGenerator().Emit(OpCodes.Ret);
        return method;
    }

    static string Target(MethodInfo m) =>
        m == null ? "null" : m.DeclaringType.Name + "::" + m.Name + " [" + m.ReflectedType.Name + "]";

    static string Map(Type receiver)
    {
        InterfaceMapping map = receiver.GetInterfaceMap(typeof(IOne));
        if (map.InterfaceMethods.Length != 1 || map.TargetMethods.Length != 1)
            return map.InterfaceMethods.Length + " and " + map.TargetMethods.Length + " rows";
        return map.InterfaceMethods[0].Name + " -> " + Target(map.TargetMethods[0]);
    }

    // 0 if `actual` is `expected`; otherwise `code`, having printed both.
    static int Check(int code, string actual, string expected)
    {
        if (actual == expected) return 0;
        Console.WriteLine("check " + code + ": expected " + expected + ", got " + actual);
        return code;
    }

    public static int Main()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Dyn"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule("Dyn");

        TypeBuilder proxy = module.DefineType("Proxy", TypeAttributes.Public | TypeAttributes.Class);
        proxy.AddInterfaceImplementation(typeof(IOne));
        MethodBuilder intercepted = Empty(proxy, "IOne_M_Intercepted", PrivateFinal);
        proxy.DefineMethodOverride(intercepted, typeof(IOne).GetMethod("M"));
        proxy.DefineDefaultConstructor(MethodAttributes.Public);
        Type proxyType = proxy.CreateType();

        TypeBuilder implicitly = module.DefineType("Implicit", TypeAttributes.Public | TypeAttributes.Class);
        implicitly.AddInterfaceImplementation(typeof(IOne));
        Empty(implicitly, "M", PublicFinal);
        implicitly.DefineDefaultConstructor(MethodAttributes.Public);
        Type implicitType = implicitly.CreateType();

        TypeBuilder none = module.DefineType("AbstractNone", TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract);
        none.AddInterfaceImplementation(typeof(IOne));
        Type noneType = none.CreateType();

        int r;
        if ((r = Check(1, Map(proxyType), "M -> Proxy::IOne_M_Intercepted [Proxy]")) != 0) return r;
        if ((r = Check(2, Map(implicitType), "M -> Implicit::M [Implicit]")) != 0) return r;
        if ((r = Check(3, Map(noneType), "M -> null")) != 0) return r;

        // The map's target is the method the created type declares.
        MethodInfo declared = proxyType.GetMethod("IOne_M_Intercepted", BindingFlags.NonPublic | BindingFlags.Instance);
        if ((r = Check(4, (proxyType.GetInterfaceMap(typeof(IOne)).TargetMethods[0] == declared).ToString(), "True")) != 0) return r;

        return 0;
    }
}
