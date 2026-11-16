using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

// The `RuntimeTypeHandle_VerifyInterfaceIsImplemented` QCall over types made at run time: a type a
// `TypeBuilder` created, which is an ordinary receiver, and the class that owns every `DynamicMethod`
// of a module, which CoreCLR's `CreateMinimalMethodTable` builds with no interfaces and no metadata.
// An uncreated `TypeBuilder` never reaches the QCall: its own `GetInterfaceMap` raises
// `NotSupportedException` in managed code.
//
// The dynamic-methods class is reached through CoreLib's internals by name, as
// `DynamicMethodsClassIsVisible.cs` reaches it. As a receiver it goes to the QCall's managed wrapper
// directly, because `GetInterfaceMap` first asks `IsGenericParameter` of it, which PawPrint refuses
// as a metadata query (#2293).
//
// Each dynamic assembly has a name of its own. Measured on real .NET 10 and 11. Returns 0 on success,
// or the number of the first check that failed.

public interface IMarker { }
public interface IOne { void M(); }
public class COne : IOne { public void M() { } }

public static class Program
{
    const string NotFound = "Interface not found.";
    const string MustBeInterface = "Type passed must be an interface.";
    const string Unsupported = "The invoked member is not supported before the type is created.";

    static readonly MethodInfo Wrapper =
        typeof(RuntimeTypeHandle).GetMethod(
            "VerifyInterfaceIsImplemented",
            BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            new[] { typeof(RuntimeTypeHandle) },
            null);

    static Exception Verify(Type receiver, Type interfaceType)
    {
        try
        {
            Wrapper.Invoke(receiver.TypeHandle, new object[] { interfaceType.TypeHandle });
            return null;
        }
        catch (TargetInvocationException e)
        {
            return e.InnerException;
        }
    }

    // 0 if `e` is exactly an `ArgumentException` with `message`, `HResult` 0x80070057 and the given
    // `paramName`; otherwise `code`.
    static int IsArgument(int code, Exception e, string message, string paramName = null)
    {
        if (e == null) return code;
        if (e.GetType() != typeof(ArgumentException)) return code;
        if (e.Message != message) return code;
        if (e.HResult != unchecked((int)0x80070057)) return code;
        if (((ArgumentException)e).ParamName != paramName) return code;
        return 0;
    }

    static Exception Map(Type receiver, Type interfaceType)
    {
        try
        {
            receiver.GetInterfaceMap(interfaceType);
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    static TypeBuilder DefineType(string assemblyName, bool implement)
    {
        AssemblyBuilder ab = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
        ModuleBuilder mb = ab.DefineDynamicModule(assemblyName);
        TypeBuilder tb = mb.DefineType("Made", TypeAttributes.Public | TypeAttributes.Class);
        if (implement)
        {
            tb.AddInterfaceImplementation(typeof(IMarker));
        }
        return tb;
    }

    static Type DynamicMethodsClass()
    {
        DynamicMethod dm = new DynamicMethod("f", typeof(int), Type.EmptyTypes, typeof(Program).Module);
        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
        ((Func<int>) dm.CreateDelegate(typeof(Func<int>)))();

        MethodInfo getDescriptor =
            typeof(DynamicMethod).GetMethod("GetMethodDescriptor", BindingFlags.NonPublic | BindingFlags.Instance);
        RuntimeMethodHandle handle = (RuntimeMethodHandle) getDescriptor.Invoke(dm, null);

        MethodInfo getInfo =
            typeof(RuntimeMethodHandle).GetMethod("GetMethodInfo", BindingFlags.NonPublic | BindingFlags.Instance);
        object info = getInfo.Invoke(handle, null);

        MethodInfo getDeclaringType = typeof(RuntimeMethodHandle)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m =>
                m.Name == "GetDeclaringType"
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType.Name == "IRuntimeMethodInfo");

        return (Type) getDeclaringType.Invoke(null, new[] { info });
    }

    public static int Main()
    {
        if (Wrapper == null) return 1;
        int r;

        // A created type which does not implement `IMarker`, through `GetInterfaceMap`.
        Type without = DefineType("VerifyWithout", false).CreateType();
        if ((r = IsArgument(2, Map(without, typeof(IMarker)), NotFound)) != 0) return r;

        // A created type which implements it passes the QCall.
        Type with = DefineType("VerifyWith", true).CreateType();
        if (Verify(with, typeof(IMarker)) != null) return 3;

        // An uncreated `TypeBuilder` raises in managed code before the QCall.
        TypeBuilder uncreated = DefineType("VerifyUncreated", true);
        Exception e = Map(uncreated, typeof(IMarker));
        if (e == null || e.GetType() != typeof(NotSupportedException) || e.Message != Unsupported) return 4;

        // The dynamic-methods class declares no interface, and is not an interface.
        Type dynamicClass = DynamicMethodsClass();
        if ((r = IsArgument(5, Verify(dynamicClass, typeof(IMarker)), NotFound)) != 0) return r;
        if ((r = IsArgument(6, Verify(typeof(COne), dynamicClass), MustBeInterface)) != 0) return r;
        if ((r = IsArgument(7, Map(typeof(COne), dynamicClass), MustBeInterface)) != 0) return r;

        return 0;
    }
}
