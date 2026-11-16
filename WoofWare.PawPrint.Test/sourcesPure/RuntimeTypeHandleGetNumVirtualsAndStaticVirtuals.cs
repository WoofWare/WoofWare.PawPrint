using System;
using System.Collections.Generic;
using System.Reflection;

// The `RuntimeTypeHandle_GetNumVirtualsAndStaticVirtuals` QCall, through its internal managed wrapper
// `RuntimeTypeHandle.GetNumVirtualsAndStaticVirtuals(RuntimeType)`, reached by reflection.
// `Type.GetInterfaceMap` is its only caller in CoreLib, and asks it only about an interface; before
// `GetInterfaceMap` can return a map with methods in it, this wrapper is the only way to see the count.
//
// The count is CoreCLR's `GetNumVirtuals()`, plus, for an interface, each static virtual it declares
// itself (abstract, with a body, or a reabstraction of a base interface's), in slot order. Methods a
// base interface declares are not counted, nor sealed, private or plain static ones. An array has
// `System.Array`'s count.
//
// Every type here has the same count on .NET 10 and 11: no member returns a `Task`, whose async
// variant .NET 11 counts, and no CoreLib type whose shape differs between the two is asked about.
//
// Measured on real .NET 10 and 11. Returns 0 on success, or the number of the first check that
// failed.

public interface IMarker { }
public interface IOne { void M(); }
public interface IStat { static abstract int S(); void I(); }
public interface IStatVirt { static virtual int SV() => 1; static abstract int SA(); int P { get; } static int Plain() => 2; }
public interface IDim { void D() { } }
public interface ISealedDim { sealed void S() { } private void P() { } void A(); }
public interface IBase { void M(); static abstract int S(); }
public interface IReabs : IBase { abstract void IBase.M(); }
public interface IDimOverride : IBase { void IBase.M() { } }
public interface IStaticReabs : IBase { static abstract int IBase.S(); }
public interface IGenMethod { void G<T>(); static abstract void SG<T>(); }
public interface IG<T> { T Get(); static abstract T Make(); }
public interface IInC<in T> where T : class { }
public interface IProps { int P { get; set; } event Action E; }
public interface IStatOrder { void I1(); static abstract int S1(); void I2(); static abstract int S2(); }
public interface IStatOnly { static abstract int S(); }
// The field initialiser gives the interface a class constructor, which is static and not virtual.
public interface IStatInit { static int F = 3; static abstract int S(); }

public class COne : IOne { public void M() { } }
public struct SStat : IStat { public static int S() => 1; public void I() { } }
public class CStatOnly : IStatOnly { public static int S() => 2; }
public abstract class CAbs : IOne { public abstract void M(); }
public class HasT<T> { }

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

    // 0 if the count of `t` is `expected`; otherwise `code`.
    static int Check(int code, Type t, int expected) => Count(t) == expected ? 0 : code;

    public static int Main()
    {
        if (Wrapper == null) return 1;

        Type listT = typeof(List<>).GetGenericArguments()[0];
        Type hasT = typeof(HasT<>).GetGenericArguments()[0];

        int r;
        if ((r = Check(2, typeof(IMarker), 0)) != 0) return r;
        if ((r = Check(3, typeof(IOne), 1)) != 0) return r;
        if ((r = Check(4, typeof(IStat), 2)) != 0) return r;
        if ((r = Check(5, typeof(IStatVirt), 3)) != 0) return r;
        if ((r = Check(6, typeof(ISealedDim), 1)) != 0) return r;
        if ((r = Check(7, typeof(IReabs), 1)) != 0) return r;
        if ((r = Check(8, typeof(IDimOverride), 1)) != 0) return r;
        if ((r = Check(9, typeof(IStaticReabs), 1)) != 0) return r;
        if ((r = Check(10, typeof(IGenMethod), 2)) != 0) return r;
        if ((r = Check(11, typeof(IG<>), 2)) != 0) return r;
        if ((r = Check(12, typeof(IG<int>), 2)) != 0) return r;
        if ((r = Check(13, typeof(IG<>).MakeGenericType(hasT), 2)) != 0) return r;
        if ((r = Check(14, typeof(IInC<>), 0)) != 0) return r;
        if ((r = Check(15, typeof(IProps), 4)) != 0) return r;
        if ((r = Check(16, typeof(IStatOrder), 4)) != 0) return r;
        if ((r = Check(17, typeof(IStatOnly), 1)) != 0) return r;
        if ((r = Check(18, typeof(IEnumerable<>).MakeGenericType(listT), 1)) != 0) return r;
        if ((r = Check(19, typeof(COne), 5)) != 0) return r;
        if ((r = Check(20, typeof(SStat), 5)) != 0) return r;
        if ((r = Check(21, typeof(CStatOnly), 4)) != 0) return r;
        if ((r = Check(22, typeof(int[]), 24)) != 0) return r;
        if ((r = Check(23, listT.MakeArrayType(), 24)) != 0) return r;
        if ((r = Check(24, typeof(IDim), 1)) != 0) return r;
        if ((r = Check(25, typeof(CAbs), 5)) != 0) return r;
        if ((r = Check(26, typeof(int[,]), 24)) != 0) return r;
        if ((r = Check(27, typeof(IStatInit), 1)) != 0) return r;
        // An array's count is `System.Array`'s whatever its element: not the element interface's.
        if ((r = Check(28, typeof(IStat[]), 24)) != 0) return r;

        return 0;
    }
}
