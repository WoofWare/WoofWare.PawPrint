using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

// The `RuntimeTypeHandle_VerifyInterfaceIsImplemented` QCall through its managed wrapper, the
// internal `RuntimeTypeHandle.VerifyInterfaceIsImplemented(RuntimeTypeHandle)`, called by reflection.
// `Type.GetInterfaceMap` calls it and then goes on to the next QCall, so only this way does a guest
// see the QCall pass for anything but an SZ array, or reach its first arm (a type variable as the
// receiver, "Invalid handle."), which `GetInterfaceMap` refuses in managed code first.
// `TypeGetInterfaceMapRefusals.cs` covers the rest through public API, so a rename inside CoreLib
// breaks this guest alone.
//
// A pass is the wrapper returning; a raise is an `ArgumentException` with a null `ParamName` and
// `HResult` 0x80070057, which reflection wraps in a `TargetInvocationException`.
//
// Measured on real .NET 10 and 11. Returns 0 on success, or the number of the first check that
// failed.

public interface IMarker { }
public interface IOne { void M(); }
public class COne : IOne { public void M() { } }
public class DOne : COne { }
public interface IBase { }
public interface IDerived : IBase { }
public class CDerived : IDerived { }
public struct SOne : IOne { public void M() { } }
public class CmpObj : IComparer<object> { public int Compare(object a, object b) => 0; }
public class Crtp : IEquatable<Crtp> { public bool Equals(Crtp other) => false; }
public interface IInC<in T> where T : class { }
public class ClassInC : IInC<object> { }
public class CGenInC<T> : IInC<object> { }
public struct SGenInC<T> : IInC<object> { }
public interface IBaseOf<T> { }
public class BaseG<T> : IBaseOf<T> { }
public class G<T> : BaseG<T> { }

public static class Program
{
    const string InvalidHandle = "Invalid handle.";
    const string NotFound = "Interface not found.";
    const string ThisIsInterface = "'this' type cannot be an interface itself.";

    static readonly MethodInfo Wrapper =
        typeof(RuntimeTypeHandle).GetMethod(
            "VerifyInterfaceIsImplemented",
            BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            new[] { typeof(RuntimeTypeHandle) },
            null);

    // What the wrapper raised for `receiver` and `interfaceType`, or null if it returned.
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

    static int Passes(int code, Type receiver, Type interfaceType)
    {
        return Verify(receiver, interfaceType) == null ? 0 : code;
    }

    // 0 if the wrapper raised an `ArgumentException` with `message`, `HResult` 0x80070057 and the
    // given `paramName`; otherwise `code`.
    static int Raises(int code, Type receiver, Type interfaceType, string message, string paramName = null)
    {
        Exception e = Verify(receiver, interfaceType);
        if (e == null) return code;
        if (e.GetType() != typeof(ArgumentException)) return code;
        if (e.Message != message) return code;
        if (e.HResult != unchecked((int)0x80070057)) return code;
        if (((ArgumentException)e).ParamName != paramName) return code;
        return 0;
    }

    static void GenericMethod<TM>() { }

    public static int Main()
    {
        if (Wrapper == null) return 1;

        Type listT = typeof(List<>).GetGenericArguments()[0];
        Type methodT = typeof(Program)
            .GetMethod(nameof(GenericMethod), BindingFlags.NonPublic | BindingFlags.Static)
            .GetGenericArguments()[0];
        Type gT = typeof(G<>).GetGenericArguments()[0];
        int r;

        // Arm 5 passes: directly, through a base class, through an interface's interface, on a struct.
        if ((r = Passes(2, typeof(COne), typeof(IOne))) != 0) return r;
        if ((r = Passes(3, typeof(DOne), typeof(IOne))) != 0) return r;
        if ((r = Passes(4, typeof(CDerived), typeof(IBase))) != 0) return r;
        if ((r = Passes(5, typeof(SOne), typeof(IOne))) != 0) return r;

        // By variance: covariant, contravariant, and the CRTP shape.
        if ((r = Passes(6, typeof(List<string>), typeof(IEnumerable<object>))) != 0) return r;
        if ((r = Passes(7, typeof(CmpObj), typeof(IComparer<string>))) != 0) return r;
        if ((r = Passes(8, typeof(Crtp), typeof(IEquatable<Crtp>))) != 0) return r;

        // The variant definition `IInC<>` is the special marker on a class, open or closed.
        if ((r = Passes(9, typeof(ClassInC), typeof(IInC<>))) != 0) return r;
        if ((r = Passes(10, typeof(CGenInC<>), typeof(IInC<>))) != 0) return r;

        // Open receivers: a definition against its own typical interface, and an open construction.
        if ((r = Passes(11, typeof(List<>), typeof(IEnumerable<>).MakeGenericType(listT))) != 0) return r;
        if ((r = Passes(12, typeof(G<>).BaseType, typeof(IBaseOf<>).MakeGenericType(gT))) != 0) return r;

        // Arm 1: a type variable of a type or of a method, and before arm 4.
        if ((r = Raises(13, listT, typeof(IComparable), InvalidHandle)) != 0) return r;
        if ((r = Raises(14, methodT, typeof(IComparable), InvalidHandle)) != 0) return r;
        if ((r = Raises(15, listT, typeof(string), InvalidHandle)) != 0) return r;

        // Arm 3 before arm 5, where arm 5 would pass.
        if ((r = Raises(16, typeof(IDerived), typeof(IBase), ThisIsInterface)) != 0) return r;

        // A closed generic struct over a reference type, whose code CoreCLR shares, against the
        // variant definition: on a value type the special marker is not accepted.
        if ((r = Raises(17, typeof(SGenInC<string>), typeof(IInC<>), NotFound)) != 0) return r;

        return 0;
    }
}
