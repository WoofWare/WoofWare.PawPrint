using System;
using System.Reflection;
using System.Reflection.Emit;

// The `RuntimeTypeHandle_GetNumVirtualsAndStaticVirtuals` QCall over types a `TypeBuilder` created: an
// interface with one instance and one static abstract method, counted through the QCall's internal
// managed wrapper `RuntimeTypeHandle.GetNumVirtualsAndStaticVirtuals(RuntimeType)` by reflection; and
// a class implementing `IMarker`, through `Type.GetInterfaceMap`, which asks the QCall about `IMarker`
// and, the count being 0, returns two empty arrays without asking anything more.
//
// Each dynamic assembly has a name of its own. Measured on real .NET 10 and 11. Returns 0 on success,
// or the number of the first check that failed.

public interface IMarker { }

public static class Program
{
    static readonly MethodInfo Wrapper =
        typeof(RuntimeTypeHandle).GetMethod(
            "GetNumVirtualsAndStaticVirtuals",
            BindingFlags.NonPublic | BindingFlags.Static,
            null,
            new[] { typeof(object).GetType() },
            null);

    static int Count(Type t) => (int)Wrapper.Invoke(null, new object[] { t });

    static ModuleBuilder DefineModule(string assemblyName)
    {
        AssemblyBuilder ab = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
        return ab.DefineDynamicModule(assemblyName);
    }

    public static int Main()
    {
        if (Wrapper == null) return 1;

        // An interface with an instance abstract method and a static abstract one: 1 + 1.
        TypeBuilder ib = DefineModule("CountInterface").DefineType(
            "IMade", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        ib.DefineMethod(
            "I",
            MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
            typeof(void),
            Type.EmptyTypes);
        ib.DefineMethod(
            "S",
            MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.Static | MethodAttributes.HideBySig,
            typeof(int),
            Type.EmptyTypes);
        Type made = ib.CreateType();
        if (Count(made) != 2) return 2;

        // A class implementing `IMarker`: `System.Object`'s four virtuals, and no static virtual.
        TypeBuilder cb = DefineModule("CountClass").DefineType("CMade", TypeAttributes.Public | TypeAttributes.Class);
        cb.AddInterfaceImplementation(typeof(IMarker));
        cb.DefineDefaultConstructor(MethodAttributes.Public);
        Type cls = cb.CreateType();
        if (Count(cls) != 4) return 3;

        InterfaceMapping map = cls.GetInterfaceMap(typeof(IMarker));
        if (map.TargetType != cls) return 4;
        if (map.InterfaceType != typeof(IMarker)) return 5;
        if (map.InterfaceMethods.Length != 0) return 6;
        if (map.TargetMethods.Length != 0) return 7;

        return 0;
    }
}
