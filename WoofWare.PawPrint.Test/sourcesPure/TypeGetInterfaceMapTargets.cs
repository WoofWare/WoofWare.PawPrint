using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

// The targets `Type.GetInterfaceMap` names, which come from the `RuntimeTypeHandle_GetInterfaceMethodImplementation`
// QCall: for each method of the interface, the method of the receiver (or of a type on its class
// chain, or of an interface, for a default body) that implements it, or null.
//
// Each check renders a whole map, row by row in the map's order, as
// `interface method -> declaring type::target [reflected type]`, with `generic` after a target that is a
// generic method definition and `abstract` after an abstract one. So each check covers the row count,
// the interface methods' names, and each target's name, declaring type and reflected type, and null
// where real .NET answers null.
//
// Every map here is the same on .NET 10 and 11: no member returns a `Task`, whose async variant .NET 11
// lays out, and the CoreLib types asked about have the same shape on both.
//
// Measured on real .NET 10 and 11. Returns 0 on success, or the number of the first check that
// failed.

public interface IOne { void M(); }
public interface IOneB { void M(); }
public class COne : IOne { public void M() { } }
public class CExpl : IOne { void IOne.M() { } }
public class CBase : IOne { public virtual void M() { } }
public class CDerNoOverride : CBase { }
public class CDerOverride : CBase { public override void M() { } }
public class CBaseNV : IOne { public void M() { } }
public class CDerNV : CBaseNV { }
public class CReimpl : CBaseNV, IOne { public new void M() { } }
public class CRelist : CBase, IOne { }
public class CExplDer : CBase, IOne { void IOne.M() { } }
public class CTwo : IOne, IOneB { public void M() { } }
public abstract class CAbs : IOne { public abstract void M(); }
public struct SOne : IOne { public void M() { } }
public struct SExpl : IOne { void IOne.M() { } }

public interface IDim { void D() { } }
public class CDim : IDim { }
public class CDimOverridden : IDim { public void D() { } }
public struct SDim : IDim { }

public interface IBaseD { void M() { } }
public interface IReabsD : IBaseD { abstract void IBaseD.M(); }
public class CReabsD : IReabsD { void IBaseD.M() { } }

public interface IBase2 { void M(); }
public interface IDimOverride : IBase2 { void IBase2.M() { } }
public class CDimOverride : IDimOverride { }

public interface IA { void M() { } }
public interface IB : IA { void IA.M() { } }
public interface IC : IA { void IA.M() { } }
public interface ID : IB, IC { void IA.M() { } }
public class CDiamondResolved : ID { }
public class CDiamondClass : IB, IC { public void M() { } }

public interface IStat { static abstract int S(); void I(); }
public struct SStat : IStat { public static int S() => 1; public void I() { } }
public class CStatExpl : IStat { static int IStat.S() => 2; void IStat.I() { } }
public interface IStatVirt { static virtual int SV() => 1; static abstract int SA(); }
public class CStatVirt : IStatVirt { public static int SA() => 3; }
public class CStatVirtOverride : IStatVirt { public static int SA() => 3; static int IStatVirt.SV() => 4; }
public class CStatVirtImplicit : IStatVirt { public static int SA() => 3; public static int SV() => 5; }
public interface IBaseS { static abstract int S(); }
public interface IStaticReabs : IBaseS { static abstract int IBaseS.S(); }
public class CStaticReabs : IStaticReabs { static int IBaseS.S() => 6; }
public interface IBaseSV { static virtual int S() => 1; }
public interface IDerSV : IBaseSV { static int IBaseSV.S() => 2; }
public class CDerSV : IDerSV { }
public class CStatBase : IStat { public static int S() => 7; public void I() { } }
public class CStatDer : CStatBase { }
public class CStatDerRelist : CStatBase, IStat { public static new int S() => 8; }
public interface IStatG<T> { static virtual int SV() => 1; }
public class CStatG : IStatG<string> { }

public interface ICo<out T> { T Get(); }
public class CCo : ICo<string> { public string Get() => ""; }
public interface ICoD<out T> { string D() => "x"; }
public class CCoD : ICoD<string> { }
public class VBase : ICo<object> { public object Get() => null; }
public class VDer : VBase, ICo<string> { public new string Get() => ""; }
public interface ICoS<out T> { static abstract string S(); }
public class CCoS : ICoS<string> { public static string S() => ""; }
public interface IContra<in T> { void Put(T t); }
public class CContra : IContra<object> { public void Put(object o) { } }

public interface IGM { void G<T>(); static abstract void SG<T>(); }
public class CGM : IGM { public void G<T>() { } public static void SG<T>() { } }
public interface IG<T> { T Get(); static abstract T Make(); }
public class CG : IG<int> { public int Get() => 0; public static int Make() => 0; }
public class CGen<T> : IG<T> { public T Get() => default; public static T Make() => default; }
public class CGBase<T> : IOne { public virtual void M() { } }
public class CGDer : CGBase<string> { }
public class CGDerOverride : CGBase<int> { public override void M() { } }
public interface IGD<T> { T D() => default; }
public class CGD : IGD<string> { }
public interface IGDM { void G<T>() { } }
public class CGDM : IGDM { }

public interface IProps { int P { get; set; } event Action E; }
public class CProps : IProps { public int P { get; set; } public event Action E { add { } remove { } } }

public static class Program
{
    static string Target(MethodInfo m)
    {
        if (m == null) return "null";
        string rendered = m.DeclaringType + "::" + m.Name + " [" + m.ReflectedType + "]";
        if (m.IsGenericMethodDefinition) rendered += " generic";
        if (m.IsAbstract) rendered += " abstract";
        return rendered;
    }

    static string Map(Type receiver, Type iface)
    {
        InterfaceMapping map = receiver.GetInterfaceMap(iface);
        if (map.InterfaceMethods.Length != map.TargetMethods.Length) return "mismatched lengths";
        var rows = new List<string>();
        for (int i = 0; i < map.InterfaceMethods.Length; i++)
            rows.Add(map.InterfaceMethods[i].Name + " -> " + Target(map.TargetMethods[i]));
        return string.Join("; ", rows);
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
        int r;

        // Classes and structs implementing an instance method.
        if ((r = Check(1, Map(typeof(COne), typeof(IOne)), "M -> COne::M [COne]")) != 0) return r;
        if ((r = Check(2, Map(typeof(CExpl), typeof(IOne)), "M -> CExpl::IOne.M [CExpl]")) != 0) return r;
        if ((r = Check(3, Map(typeof(CDerNoOverride), typeof(IOne)), "M -> CBase::M [CDerNoOverride]")) != 0) return r;
        if ((r = Check(4, Map(typeof(CDerOverride), typeof(IOne)), "M -> CDerOverride::M [CDerOverride]")) != 0) return r;
        if ((r = Check(5, Map(typeof(CDerNV), typeof(IOne)), "M -> CBaseNV::M [CDerNV]")) != 0) return r;
        if ((r = Check(6, Map(typeof(CReimpl), typeof(IOne)), "M -> CReimpl::M [CReimpl]")) != 0) return r;
        if ((r = Check(7, Map(typeof(CRelist), typeof(IOne)), "M -> CBase::M [CRelist]")) != 0) return r;
        if ((r = Check(8, Map(typeof(CExplDer), typeof(IOne)), "M -> CExplDer::IOne.M [CExplDer]")) != 0) return r;
        if ((r = Check(9, Map(typeof(CTwo), typeof(IOne)), "M -> CTwo::M [CTwo]")) != 0) return r;
        if ((r = Check(10, Map(typeof(CTwo), typeof(IOneB)), "M -> CTwo::M [CTwo]")) != 0) return r;
        if ((r = Check(11, Map(typeof(CAbs), typeof(IOne)), "M -> CAbs::M [CAbs] abstract")) != 0) return r;
        if ((r = Check(12, Map(typeof(SOne), typeof(IOne)), "M -> SOne::M [SOne]")) != 0) return r;
        if ((r = Check(13, Map(typeof(SExpl), typeof(IOne)), "M -> SExpl::IOne.M [SExpl]")) != 0) return r;

        // Default bodies, reabstractions and diamonds.
        if ((r = Check(14, Map(typeof(CDim), typeof(IDim)), "D -> IDim::D [IDim]")) != 0) return r;
        if ((r = Check(15, Map(typeof(SDim), typeof(IDim)), "D -> IDim::D [IDim]")) != 0) return r;
        if ((r = Check(16, Map(typeof(CDimOverridden), typeof(IDim)), "D -> CDimOverridden::D [CDimOverridden]")) != 0) return r;
        if ((r = Check(17, Map(typeof(CReabsD), typeof(IBaseD)), "M -> CReabsD::IBaseD.M [CReabsD]")) != 0) return r;
        if ((r = Check(18, Map(typeof(CReabsD), typeof(IReabsD)), "IBaseD.M -> null")) != 0) return r;
        if ((r = Check(19, Map(typeof(CDimOverride), typeof(IBase2)), "M -> IDimOverride::IBase2.M [IDimOverride]")) != 0) return r;
        if ((r = Check(20, Map(typeof(CDimOverride), typeof(IDimOverride)), "IBase2.M -> IDimOverride::IBase2.M [IDimOverride]")) != 0) return r;
        if ((r = Check(21, Map(typeof(CDiamondResolved), typeof(IA)), "M -> ID::IA.M [ID]")) != 0) return r;
        if ((r = Check(22, Map(typeof(CDiamondResolved), typeof(IB)), "IA.M -> IB::IA.M [IB]")) != 0) return r;
        if ((r = Check(23, Map(typeof(CDiamondClass), typeof(IA)), "M -> CDiamondClass::M [CDiamondClass]")) != 0) return r;

        // Static virtuals.
        if ((r = Check(24, Map(typeof(SStat), typeof(IStat)), "I -> SStat::I [SStat]; S -> SStat::S [SStat]")) != 0) return r;
        if ((r = Check(25, Map(typeof(CStatExpl), typeof(IStat)), "I -> CStatExpl::IStat.I [CStatExpl]; S -> CStatExpl::IStat.S [CStatExpl]")) != 0) return r;
        if ((r = Check(26, Map(typeof(CStatVirt), typeof(IStatVirt)), "SV -> IStatVirt::SV [IStatVirt]; SA -> CStatVirt::SA [CStatVirt]")) != 0) return r;
        if ((r = Check(27, Map(typeof(CStatVirtOverride), typeof(IStatVirt)), "SV -> CStatVirtOverride::IStatVirt.SV [CStatVirtOverride]; SA -> CStatVirtOverride::SA [CStatVirtOverride]")) != 0) return r;
        if ((r = Check(28, Map(typeof(CStatVirtImplicit), typeof(IStatVirt)), "SV -> CStatVirtImplicit::SV [CStatVirtImplicit]; SA -> CStatVirtImplicit::SA [CStatVirtImplicit]")) != 0) return r;
        if ((r = Check(29, Map(typeof(CStaticReabs), typeof(IBaseS)), "S -> CStaticReabs::IBaseS.S [CStaticReabs]")) != 0) return r;
        if ((r = Check(30, Map(typeof(CStaticReabs), typeof(IStaticReabs)), "IBaseS.S -> null")) != 0) return r;
        if ((r = Check(31, Map(typeof(CDerSV), typeof(IBaseSV)), "S -> IDerSV::IBaseSV.S [IDerSV]")) != 0) return r;
        if ((r = Check(32, Map(typeof(CDerSV), typeof(IDerSV)), "")) != 0) return r;
        if ((r = Check(33, Map(typeof(CStatDer), typeof(IStat)), "I -> CStatBase::I [CStatDer]; S -> CStatBase::S [CStatDer]")) != 0) return r;
        if ((r = Check(34, Map(typeof(CStatDerRelist), typeof(IStat)), "I -> CStatBase::I [CStatDerRelist]; S -> CStatDerRelist::S [CStatDerRelist]")) != 0) return r;
        if ((r = Check(35, Map(typeof(CStatG), typeof(IStatG<string>)), "SV -> IStatG`1[System.String]::SV [IStatG`1[System.String]]")) != 0) return r;

        // Variance.
        if ((r = Check(36, Map(typeof(CCo), typeof(ICo<object>)), "Get -> CCo::Get [CCo]")) != 0) return r;
        if ((r = Check(37, Map(typeof(CCoD), typeof(ICoD<object>)), "D -> ICoD`1[System.String]::D [ICoD`1[System.String]]")) != 0) return r;
        if ((r = Check(38, Map(typeof(VDer), typeof(ICo<object>)), "Get -> VDer::Get [VDer]")) != 0) return r;
        if ((r = Check(39, Map(typeof(VDer), typeof(ICo<string>)), "Get -> VDer::Get [VDer]")) != 0) return r;
        if ((r = Check(40, Map(typeof(CCoS), typeof(ICoS<object>)), "S -> CCoS::S [CCoS]")) != 0) return r;
        if ((r = Check(41, Map(typeof(CContra), typeof(IContra<string>)), "Put -> CContra::Put [CContra]")) != 0) return r;

        // Generic methods and generic types.
        if ((r = Check(42, Map(typeof(CGM), typeof(IGM)), "G -> CGM::G [CGM] generic; SG -> CGM::SG [CGM] generic")) != 0) return r;
        if ((r = Check(43, Map(typeof(CGDM), typeof(IGDM)), "G -> IGDM::G [IGDM] generic")) != 0) return r;
        if ((r = Check(44, Map(typeof(CG), typeof(IG<int>)), "Get -> CG::Get [CG]; Make -> CG::Make [CG]")) != 0) return r;
        if ((r = Check(45, Map(typeof(CGen<string>), typeof(IG<string>)), "Get -> CGen`1[System.String]::Get [CGen`1[System.String]]; Make -> CGen`1[System.String]::Make [CGen`1[System.String]]")) != 0) return r;
        if ((r = Check(46, Map(typeof(CGen<int>), typeof(IG<int>)), "Get -> CGen`1[System.Int32]::Get [CGen`1[System.Int32]]; Make -> CGen`1[System.Int32]::Make [CGen`1[System.Int32]]")) != 0) return r;
        if ((r = Check(47, Map(typeof(CGDer), typeof(IOne)), "M -> CGBase`1[System.String]::M [CGDer]")) != 0) return r;
        if ((r = Check(48, Map(typeof(CGDerOverride), typeof(IOne)), "M -> CGDerOverride::M [CGDerOverride]")) != 0) return r;
        if ((r = Check(49, Map(typeof(CGD), typeof(IGD<string>)), "D -> IGD`1[System.String]::D [IGD`1[System.String]]")) != 0) return r;

        // Accessors.
        if ((r = Check(50, Map(typeof(CProps), typeof(IProps)), "get_P -> CProps::get_P [CProps]; set_P -> CProps::set_P [CProps]; add_E -> CProps::add_E [CProps]; remove_E -> CProps::remove_E [CProps]")) != 0) return r;

        // Arrays, and CoreLib's own types.
        if ((r = Check(51, Map(typeof(int[]), typeof(IList)), "get_Item -> System.Array::System.Collections.IList.get_Item [System.Int32[]]; set_Item -> System.Array::System.Collections.IList.set_Item [System.Int32[]]; Add -> System.Array::System.Collections.IList.Add [System.Int32[]]; Contains -> System.Array::System.Collections.IList.Contains [System.Int32[]]; Clear -> System.Array::System.Collections.IList.Clear [System.Int32[]]; get_IsReadOnly -> System.Array::get_IsReadOnly [System.Int32[]]; get_IsFixedSize -> System.Array::get_IsFixedSize [System.Int32[]]; IndexOf -> System.Array::System.Collections.IList.IndexOf [System.Int32[]]; Insert -> System.Array::System.Collections.IList.Insert [System.Int32[]]; Remove -> System.Array::System.Collections.IList.Remove [System.Int32[]]; RemoveAt -> System.Array::System.Collections.IList.RemoveAt [System.Int32[]]")) != 0) return r;
        if ((r = Check(52, Map(typeof(string[]), typeof(IEnumerable)), "GetEnumerator -> System.Array::GetEnumerator [System.String[]]")) != 0) return r;
        if ((r = Check(53, Map(typeof(int[,]), typeof(ICollection)), "CopyTo -> System.Array::CopyTo [System.Int32[,]]; get_Count -> System.Array::System.Collections.ICollection.get_Count [System.Int32[,]]; get_SyncRoot -> System.Array::get_SyncRoot [System.Int32[,]]; get_IsSynchronized -> System.Array::get_IsSynchronized [System.Int32[,]]")) != 0) return r;
        if ((r = Check(54, Map(typeof(int), typeof(IComparable<int>)), "CompareTo -> System.Int32::CompareTo [System.Int32]")) != 0) return r;

        // The target is the method reflection gives for the implementation, a struct's included.
        if ((r = Check(55, (typeof(SOne).GetInterfaceMap(typeof(IOne)).TargetMethods[0] == typeof(SOne).GetMethod("M")).ToString(), "True")) != 0) return r;
        if ((r = Check(56, (typeof(CGD).GetInterfaceMap(typeof(IGD<string>)).TargetMethods[0] == typeof(IGD<string>).GetMethod("D")).ToString(), "True")) != 0) return r;
        if ((r = Check(57, (typeof(CDerNoOverride).GetInterfaceMap(typeof(IOne)).TargetMethods[0] == typeof(CBase).GetMethod("M")).ToString(), "False")) != 0) return r;
        if ((r = Check(58, (typeof(CDerNoOverride).GetInterfaceMap(typeof(IOne)).TargetMethods[0] == typeof(CDerNoOverride).GetMethod("M")).ToString(), "True")) != 0) return r;

        return 0;
    }
}
