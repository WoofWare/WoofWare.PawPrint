using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

// `Type.IsVisible` of the class that owns every `DynamicMethod` of a module: CoreCLR's
// `CreateMinimalMethodTable` gives it no metadata but sets its class attributes to
// `tdPublic | tdSealed`, so `IsTypeDefExternallyVisible` answers true before reading any metadata.
// Measured on .NET 10 and 11: the class and its array type are both visible.
//
// The class is reached through CoreLib's internals by name (`DynamicMethod.GetMethodDescriptor`,
// `RuntimeMethodHandle.GetMethodInfo`, `RuntimeMethodHandle.GetDeclaringType`), so that a rename
// there breaks this guest alone. It asks `IsVisible` only: PawPrint refuses the class's `Name` and
// `Attributes`.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    public static int Main()
    {
        DynamicMethod dm = new DynamicMethod("f", typeof(int), Type.EmptyTypes, typeof(Program).Module);
        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);

        if (((Func<int>) dm.CreateDelegate(typeof(Func<int>)))() != 1) return 1;

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

        Type dynamicClass = (Type) getDeclaringType.Invoke(null, new[] { info });

        if (!dynamicClass.IsVisible) return 2;
        if (!dynamicClass.MakeArrayType().IsVisible) return 3;

        return 0;
    }
}
