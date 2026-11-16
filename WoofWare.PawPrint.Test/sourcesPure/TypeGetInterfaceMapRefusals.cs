using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;

// `Type.GetInterfaceMap` up to and including the `RuntimeTypeHandle_VerifyInterfaceIsImplemented`
// QCall, through public API only. The QCall takes CoreCLR's five arms in order, each raising an
// `ArgumentException` with a null `ParamName` and `HResult` 0x80070057:
//
// 1. the receiver is a type variable: "Invalid handle." (`GetInterfaceMap` raises
//    `InvalidOperationException` in managed code first, so only check 28 sees this side of it);
// 2. the receiver is a byref, pointer or function pointer: "Interface not found.";
// 3. the receiver is an interface: "'this' type cannot be an interface itself.";
// 4. the second argument is not an interface: "Type passed must be an interface.";
// 5. the receiver does not cast to the interface: "Interface not found.".
//
// On success the QCall returns nothing and `GetInterfaceMap` goes on to the next QCall, so no check
// here passes it except for an SZ array against a generic interface, which `GetInterfaceMap` then
// refuses in managed code ("Interface maps for generic interfaces on arrays cannot be retrieved.").
// `RuntimeTypeHandleVerifyInterfaceIsImplemented.cs` calls the QCall's wrapper directly for the
// other passes and for arm 1.
//
// Measured on real .NET 10 and 11. Returns 0 on success, or the number of the first check that
// failed.

public interface IMarker { }
public interface IOne { void M(); }
public class COne : IOne { public void M() { } }
public interface IInv<T> { }
public class CInv : IInv<int> { }
public interface IInC<in T> where T : class { }
public struct StructInC : IInC<object> { }

public class Dyn : IDynamicInterfaceCastable
{
    public RuntimeTypeHandle GetInterfaceImplementation(RuntimeTypeHandle interfaceType) => default;
    public bool IsInterfaceImplemented(RuntimeTypeHandle interfaceType, bool throwIfNotImplemented) => true;
}

public static unsafe class Program
{
    const string InvalidHandle = "Invalid handle.";
    const string NotFound = "Interface not found.";
    const string ThisIsInterface = "'this' type cannot be an interface itself.";
    const string MustBeInterface = "Type passed must be an interface.";
    const string ArrayMap = "Interface maps for generic interfaces on arrays cannot be retrieved.";
    const string GenericParameter = "Method must be called on a Type for which Type.IsGenericParameter is false.";

    // 0 if `map` raised exactly `expected` with `message`; otherwise `code`. An `ArgumentException`
    // must also carry `HResult` 0x80070057 and the given `paramName`.
    static int Raises(int code, Func<object> map, Type expected, string message, string paramName = null)
    {
        try
        {
            map();
            return code;
        }
        catch (Exception e)
        {
            if (e.GetType() != expected) return code;
            if (e.Message != message) return code;
            if (expected == typeof(ArgumentException))
            {
                if (e.HResult != unchecked((int)0x80070057)) return code;
                if (((ArgumentException)e).ParamName != paramName) return code;
            }
            return 0;
        }
    }

    // 0 if `map` returned without raising; otherwise `code`.
    static int Passes(int code, Func<object> map)
    {
        try
        {
            map();
            return 0;
        }
        catch (Exception)
        {
            return code;
        }
    }

    public static int Main()
    {
        Type t = typeof(List<>).GetGenericArguments()[0];
        Type canon = typeof(object).Assembly.GetType("System.__Canon");
        int r;

        if ((r = Raises(1, () => typeof(COne).GetInterfaceMap(typeof(IMarker)), typeof(ArgumentException), NotFound)) != 0) return r;
        if ((r = Raises(2, () => typeof(COne).GetInterfaceMap(typeof(string)), typeof(ArgumentException), MustBeInterface)) != 0) return r;
        if ((r = Raises(3, () => typeof(IOne).GetInterfaceMap(typeof(IOne)), typeof(ArgumentException), ThisIsInterface)) != 0) return r;
        if ((r = Raises(4, () => typeof(int*).GetInterfaceMap(typeof(IDisposable)), typeof(ArgumentException), NotFound)) != 0) return r;
        if ((r = Raises(5, () => typeof(int[]).GetInterfaceMap(typeof(IEnumerable<int>)), typeof(ArgumentException), ArrayMap)) != 0) return r;

        // Arm 3 before arm 4, and arm 2 before arm 4.
        if ((r = Raises(6, () => typeof(IOne).GetInterfaceMap(typeof(string)), typeof(ArgumentException), ThisIsInterface)) != 0) return r;
        if ((r = Raises(7, () => typeof(int*).GetInterfaceMap(typeof(string)), typeof(ArgumentException), NotFound)) != 0) return r;

        // The other TypeDescs as receivers.
        if ((r = Raises(8, () => typeof(int).MakeByRefType().GetInterfaceMap(typeof(IComparable)), typeof(ArgumentException), NotFound)) != 0) return r;
        if ((r = Raises(9, () => typeof(delegate*<void>).GetInterfaceMap(typeof(IComparable)), typeof(ArgumentException), NotFound)) != 0) return r;
        if ((r = Raises(10, () => t.MakePointerType().GetInterfaceMap(typeof(IComparable)), typeof(ArgumentException), NotFound)) != 0) return r;

        // Open types on either side.
        if ((r = Raises(11, () => typeof(COne).GetInterfaceMap(typeof(List<>)), typeof(ArgumentException), MustBeInterface)) != 0) return r;
        if ((r = Raises(12, () => typeof(IEnumerable<>).GetInterfaceMap(typeof(IEnumerable)), typeof(ArgumentException), ThisIsInterface)) != 0) return r;

        // SZ arrays: the QCall passes by `ArraySupportsBizarreInterface`, and `GetInterfaceMap` then
        // refuses a generic interface in managed code.
        if ((r = Raises(13, () => typeof(string[]).GetInterfaceMap(typeof(IEnumerable<object>)), typeof(ArgumentException), ArrayMap)) != 0) return r;
        if ((r = Raises(14, () => typeof(int[]).GetInterfaceMap(typeof(IList<uint>)), typeof(ArgumentException), ArrayMap)) != 0) return r;
        if ((r = Raises(15, () => typeof(DayOfWeek[]).GetInterfaceMap(typeof(IList<int>)), typeof(ArgumentException), ArrayMap)) != 0) return r;
        if ((r = Raises(16, () => t.MakeArrayType().GetInterfaceMap(typeof(IEnumerable<>).MakeGenericType(t)), typeof(ArgumentException), ArrayMap)) != 0) return r;

        // Arrays which the QCall refuses: a different width, variance over a value type, an
        // interface outside the five, a multi-dimensional array.
        if ((r = Raises(17, () => typeof(int[]).GetInterfaceMap(typeof(IList<long>)), typeof(ArgumentException), NotFound)) != 0) return r;
        if ((r = Raises(18, () => typeof(int[]).GetInterfaceMap(typeof(IEnumerable<object>)), typeof(ArgumentException), NotFound)) != 0) return r;
        if ((r = Raises(19, () => typeof(int[]).GetInterfaceMap(typeof(IEquatable<int>)), typeof(ArgumentException), NotFound)) != 0) return r;
        if ((r = Raises(20, () => typeof(int[,]).GetInterfaceMap(typeof(IList<int>)), typeof(ArgumentException), NotFound)) != 0) return r;

        // `Nullable<int>` declares no interface, and the `T` to `Nullable<T>` rule cannot apply to an
        // interface target.
        if ((r = Raises(21, () => typeof(int?).GetInterfaceMap(typeof(IComparable)), typeof(ArgumentException), NotFound)) != 0) return r;

        // No variance over a value-type argument, nor for an invariant parameter.
        if ((r = Raises(22, () => typeof(List<int>).GetInterfaceMap(typeof(IEnumerable<object>)), typeof(ArgumentException), NotFound)) != 0) return r;
        if ((r = Raises(23, () => typeof(CInv).GetInterfaceMap(typeof(IInv<uint>)), typeof(ArgumentException), NotFound)) != 0) return r;

        // A generic interface definition as the second argument: `List<>` maps the typical
        // instantiation `IEnumerable<T>`, not the definition, and the variant definition is the
        // special marker only on a class.
        if ((r = Raises(24, () => typeof(List<>).GetInterfaceMap(typeof(IEnumerable<>)), typeof(ArgumentException), NotFound)) != 0) return r;
        if ((r = Raises(25, () => typeof(StructInC).GetInterfaceMap(typeof(IInC<>)), typeof(ArgumentException), NotFound)) != 0) return r;

        // Neither `IDynamicInterfaceCastable` nor `__Canon` casts to an interface it does not declare.
        if ((r = Raises(26, () => typeof(Dyn).GetInterfaceMap(typeof(IMarker)), typeof(ArgumentException), NotFound)) != 0) return r;
        if ((r = Raises(27, () => canon.GetInterfaceMap(typeof(IDisposable)), typeof(ArgumentException), NotFound)) != 0) return r;

        // A type variable as the receiver never reaches the QCall through `GetInterfaceMap`.
        if ((r = Raises(28, () => t.GetInterfaceMap(typeof(IComparable)), typeof(InvalidOperationException), GenericParameter)) != 0) return r;

        // A second argument which is a type variable, a pointer, an array or a value type reaches arm 4.
        if ((r = Raises(29, () => typeof(COne).GetInterfaceMap(t), typeof(ArgumentException), MustBeInterface)) != 0) return r;
        if ((r = Raises(30, () => typeof(COne).GetInterfaceMap(typeof(int*)), typeof(ArgumentException), MustBeInterface)) != 0) return r;
        if ((r = Raises(31, () => typeof(COne).GetInterfaceMap(typeof(IOne[])), typeof(ArgumentException), MustBeInterface)) != 0) return r;
        if ((r = Raises(32, () => typeof(COne).GetInterfaceMap(typeof(int)), typeof(ArgumentException), MustBeInterface)) != 0) return r;

        return 0;
    }
}
