namespace WoofWare.PawPrint.Test

open NUnit.Framework

/// `Type.GetInterfaceMap` run end to end, under PawPrint and on the real runtime, where its third QCall,
/// `RuntimeTypeHandle_GetInterfaceMethodImplementation`, decides the answer in a way the C#-compilable
/// guests (`sourcesPure/TypeGetInterfaceMapTargets.cs`) cannot show.
///
/// The shapes C# refuses to compile are `TestInterfaceMethodImplementation.swapLibrary`'s: the entry
/// assembly, holding the classes, is compiled against the library's first version and runs against its
/// second, which adds the bodies and reabstractions. There a map's row can be null, can name an abstract
/// method, or can make the whole map throw `AmbiguousImplementationException`; and in two places a call
/// and the map disagree, which the guest checks too.
///
/// A receiver that is a generic type definition is refused (#2303); its tests assert what the real
/// runtime answers and that PawPrint refuses.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestGetInterfaceMethodImplementation =

    let private libraryName = "SwapLib"

    let private entryName = "SwapEntry"

    let private libraryBefore : CrossAssemblySpec =
        CrossAssemblySpec.library libraryName [] [ TestInterfaceMethodImplementation.swapLibrary ]

    let private libraryAfter : CrossAssemblySpec =
        CrossAssemblySpec.library libraryName [] [ "#define V2\n" + TestInterfaceMethodImplementation.swapLibrary ]

    let private shapesProgram : string =
        """
using System;
using System.Reflection;
using System.Runtime;

// An instance diamond on an interface with no variance, for a receiver that is not
// IDynamicInterfaceCastable.
public class CDiamondD : IBd, ICd { }

public static class Program
{
    static string Target(MethodInfo m)
    {
        if (m == null) return "null";
        string rendered = m.DeclaringType + "::" + m.Name + " [" + m.ReflectedType + "]";
        if (m.IsAbstract) rendered += " abstract";
        return rendered;
    }

    // The target of the map's one row, or "ambiguous" if the map throws AmbiguousImplementationException
    // with the runtime's HResult.
    static string One(Type receiver, Type iface)
    {
        try
        {
            InterfaceMapping map = receiver.GetInterfaceMap(iface);
            if (map.InterfaceMethods.Length != 1 || map.TargetMethods.Length != 1)
                return map.InterfaceMethods.Length + " and " + map.TargetMethods.Length + " rows";
            return Target(map.TargetMethods[0]);
        }
        catch (AmbiguousImplementationException e)
        {
            return e.HResult == unchecked((int)0x8013106A) ? "ambiguous" : "ambiguous, HResult " + e.HResult;
        }
    }

    // 0 if `actual` is `expected`; otherwise `code`, having printed both.
    static int Check(int code, string actual, string expected)
    {
        if (actual == expected) return 0;
        Console.WriteLine("check " + code + ": expected " + expected + ", got " + actual);
        return code;
    }

    static int VariantStatic<T>() where T : IAsv<object> => T.S();

    static int ReabstractedStatic<T>() where T : IAr => T.S();

    static string Called(Func<int> call)
    {
        try
        {
            return call().ToString();
        }
        catch (EntryPointNotFoundException)
        {
            return "EntryPointNotFoundException";
        }
    }

    public static int Main()
    {
        int r;

        // Static diamonds: the map throws, at the exact instantiation and by variance.
        if ((r = Check(1, One(typeof(CDiamondS), typeof(IAs)), "ambiguous")) != 0) return r;
        if ((r = Check(2, One(typeof(CDiamondSV), typeof(IAsv<string>)), "ambiguous")) != 0) return r;
        if ((r = Check(3, One(typeof(CDiamondSV), typeof(IAsv<object>)), "ambiguous")) != 0) return r;
        // A call through the variant interface runs the first body, where the map throws.
        if ((r = Check(4, Called(VariantStatic<CDiamondSV>), "2")) != 0) return r;

        // A static reabstraction as the most specific default is the target, abstract; asked about at
        // the reabstracting interface itself, the row has none.
        if ((r = Check(5, One(typeof(CReabsS), typeof(IAr)), "IRr::IAr.S [IRr] abstract")) != 0) return r;
        if ((r = Check(6, One(typeof(CReabsS), typeof(IRr)), "null")) != 0) return r;
        // A call finds no implementation, where the map names the reabstraction.
        if ((r = Check(7, Called(ReabstractedStatic<CReabsS>), "EntryPointNotFoundException")) != 0) return r;
        if ((r = Check(8, One(typeof(CReabsSG), typeof(IArG<string>)), "IRrG`1[System.String]::IArG<T>.S [IRrG`1[System.String]] abstract")) != 0) return r;
        if ((r = Check(9, One(typeof(CReabsSG), typeof(IRrG<string>)), "null")) != 0) return r;
        if ((r = Check(10, One(typeof(CReabsSGen<int>), typeof(IArG<int>)), "IRrG`1[System.Int32]::IArG<T>.S [IRrG`1[System.Int32]] abstract")) != 0) return r;

        // Instance diamonds and reabstractions have no target, except through a variant interface,
        // where the first most specific body is the target unless it is a reabstraction.
        if ((r = Check(11, One(typeof(CDiamondD), typeof(IAd)), "null")) != 0) return r;
        if ((r = Check(12, One(typeof(CReabsI), typeof(IAi)), "null")) != 0) return r;
        if ((r = Check(13, One(typeof(CDiamondV), typeof(IAv<string>)), "IBv::IAv<System.String>.M [IBv]")) != 0) return r;
        if ((r = Check(14, One(typeof(CDiamondV), typeof(IAv<object>)), "IBv::IAv<System.String>.M [IBv]")) != 0) return r;
        if ((r = Check(15, One(typeof(CDiamondW), typeof(IAw<string>)), "null")) != 0) return r;
        if ((r = Check(16, One(typeof(CDiamondW), typeof(IAw<object>)), "null")) != 0) return r;
        if ((r = Check(17, One(typeof(CDiamondW2), typeof(IAw<string>)), "ICw::IAw<System.String>.M [ICw]")) != 0) return r;
        if ((r = Check(18, One(typeof(CDiamondW2), typeof(IAw<object>)), "ICw::IAw<System.String>.M [ICw]")) != 0) return r;

        // An IDynamicInterfaceCastable receiver is answered from its own interfaces alone.
        if ((r = Check(19, One(typeof(CDicReabs), typeof(IAi)), "null")) != 0) return r;
        if ((r = Check(20, One(typeof(CDicDiamond), typeof(IAd)), "null")) != 0) return r;

        return 0;
    }
}
"""

    [<Test>]
    let ``GetInterfaceMap over shapes C# refuses to compile`` () : unit =
        {
            Assemblies =
                [
                    libraryBefore
                    CrossAssemblySpec.entryPoint
                        entryName
                        [ libraryName ]
                        [ TestInterfaceMethodImplementation.swapClasses ; shapesProgram ]
                ]
            EntryAssemblyName = entryName
            ExpectedReturnCode = 0
        }
        |> CrossAssemblyHarness.runTestReplacing [ libraryAfter ]

    /// A guest whose one assembly is `program`, which returns 0 where the real runtime's map is the one
    /// it expects; PawPrint must refuse at this QCall's open-receiver `TODO`.
    let private refusesOpenReceiver (name : string) (program : string) : unit =
        {
            Assemblies = [ CrossAssemblySpec.entryPoint name [] [ program ] ]
            EntryAssemblyName = name
            ExpectedReturnCode = 0
        }
        |> CrossAssemblyHarness.runTestExpectingRefusal
            [ "TODO" ; "RuntimeTypeHandle_GetInterfaceMethodImplementation" ; "#2303" ]
            []
            []

    /// `GetInterfaces` gives a generic definition's closed interface rows, so a map of one reaches the
    /// QCall with an open receiver and a closed interface.
    [<Test>]
    let ``A generic definition's closed interface is refused`` () : unit =
        """
using System;
using System.Reflection;

public interface IOne { void M(); }
public class CGBase<T> : IOne { public virtual void M() { } }

public static class Program
{
    public static int Main()
    {
        if (Array.IndexOf(typeof(CGBase<>).GetInterfaces(), typeof(IOne)) < 0) return 1;
        InterfaceMapping map = typeof(CGBase<>).GetInterfaceMap(typeof(IOne));
        if (map.TargetMethods.Length != 1) return 2;
        MethodInfo target = map.TargetMethods[0];
        if (target.Name != "M") return 3;
        if (target.DeclaringType != typeof(CGBase<>)) return 4;
        if (target.ReflectedType != typeof(CGBase<>)) return 5;
        return 0;
    }
}
"""
        |> refusesOpenReceiver "OpenReceiverClosedInterface"

    /// A generic definition with its own open interface, which this guest builds by hand because
    /// PawPrint's `GetInterfaces` refuses an open interface row.
    [<Test>]
    let ``A generic definition's open interface is refused`` () : unit =
        """
using System;
using System.Reflection;

public interface IG<T> { T Get(); }
public class CGen<T> : IG<T> { public T Get() => default; }

public static class Program
{
    public static int Main()
    {
        Type openInterface = typeof(IG<>).MakeGenericType(typeof(CGen<>).GetGenericArguments());
        InterfaceMapping map = typeof(CGen<>).GetInterfaceMap(openInterface);
        if (map.TargetMethods.Length != 1) return 1;
        MethodInfo target = map.TargetMethods[0];
        if (target.Name != "Get") return 2;
        if (target.DeclaringType != typeof(CGen<>)) return 3;
        if (target.ReflectedType != typeof(CGen<>)) return 4;
        return 0;
    }
}
"""
        |> refusesOpenReceiver "OpenReceiverOpenInterface"
