# `RuntimeTypeHandle_GetInterfaceMethodImplementation` (issue #2290)

Status: proposed, awaiting decisions. Written 2026-11-16 on `pr-2289-num-virtuals` at `52f675317` (#2301, which
implements #2289 and was on the merge queue, not yet merged), on top of `main` at `0652e513a` (#2298, #2270).

Scope is this one QCall, the third and last that `Type.GetInterfaceMap` makes, after
`RuntimeTypeHandle_VerifyInterfaceIsImplemented` (#2270, merged as #2298) and
`RuntimeTypeHandle_GetNumVirtualsAndStaticVirtuals` (#2289, #2301). It is also the next stop of FakeItEasy 8.0.0 on
`A.CallTo` (#2092).

Real .NET means 10.0.14, and 11.0.0-rc.3 reached by rolling the same build forward
(`DOTNET_ROLL_FORWARD=LatestMajor`, `DOTNET_ROLL_FORWARD_TO_PRERELEASE=1`). PawPrint runs used the Release App built
from `52f675317` plus the throwaway handler described under "PawPrint with a throwaway", each probe published
self-contained for osx-arm64 and AnyCPU, with `IsDynamicCodeSupported` set true in its runtimeconfig. Each claim is
marked *measured* or *read*. The probes were throwaway programs and are not kept; "Probe files" at the end says what
each did.

## What it is

### Who reaches it

`RuntimeType.GetInterfaceMap` is the only caller, on both runtimes (read: a search of both pinned sources finds the
`LibraryImport`, the wrapper and this one call). The managed text is the same on .NET 10
(`RuntimeType.CoreCLR.cs:2739-2817`) and .NET 11 (`:2737-2815`) (read, by diffing them). For each slot below
`GetNumVirtualsAndStaticVirtuals(interface)` at which `GetMethodAt` answers a method:

```csharp
MethodBase ifaceMethodBase = GetMethodBase(ifaceRtType, ifaceRtMethodHandle)!;
im.InterfaceMethods[actualCount] = (MethodInfo)ifaceMethodBase;

// If the impl is null, then virtual stub dispatch is active.
RuntimeMethodHandleInternal classRtMethodHandle = TypeHandle.GetInterfaceMethodImplementation(ifaceRtTypeHandle, ifaceRtMethodHandle);

if (classRtMethodHandle.IsNullHandle())
{
    actualCount++;
    continue;
}

// If we resolved to an interface method, use the interface type as reflected type. Otherwise use `this`.
RuntimeType reflectedType = RuntimeMethodHandle.GetDeclaringType(classRtMethodHandle);
if (!reflectedType.IsActualInterface)
    reflectedType = this;

MethodBase? rtTypeMethodBase = GetMethodBase(reflectedType, classRtMethodHandle);
RuntimeMethodInfo? targetMethod = (RuntimeMethodInfo?)rtTypeMethodBase;
im.TargetMethods[actualCount++] = (targetMethod is { IsGenericMethod: true, IsGenericMethodDefinition: false })
    ? targetMethod.GetGenericMethodDefinition() : targetMethod!;
```

The wrapper (`RuntimeHandles.cs:634-639`, both runtimes) is an instance method of the *receiver's* handle; it passes
the receiver, the interface and the interface method's handle, and checks nothing. So:

- The method is always one `GetMethodAt` minted for the interface: a method *definition* declared on the interface
  as `GetInterfaceMap` was given it (a closed interface such as `IG<string>`, a generic definition, or an open
  construction such as `IG<T>` over `CGen<>`'s `T`), never an async variant (it skips those), never a method
  instantiation.
- The receiver has passed #2270's checks: it is not a type variable, a TypeDesc or an interface, and it implements the
  interface or casts to it by variance. An SZ array never reaches this QCall with a generic interface (the managed code
  throws `Argument_ArrayGetInterfaceMap` first). The receiver can be a generic definition (`typeof(List<>)`) with one
  of its own open interfaces.
- A null answer is kept as a row whose target is null. A non-null answer is passed to `GetMethodBase` with the
  interface as the reflected type when its declaring type is an interface, and the receiver otherwise; so the handle's
  declaring type must be on the receiver's class chain (or the receiver itself), or be an interface.
- Whatever the QCall throws comes out of `GetInterfaceMap`.

### What CoreCLR answers

`RuntimeTypeHandle_GetInterfaceMethodImplementation` is `vm/runtimehandles.cpp:849-884` on .NET 10 and `:850-885` on
.NET 11, the same text (read, by diffing):

```cpp
TypeHandle typeHandle = pTypeHandle.AsTypeHandle();
TypeHandle thOwnerOfMD = pOwner.AsTypeHandle();

if (pMD->IsStatic())
{
    pResult = typeHandle.GetMethodTable()->ResolveVirtualStaticMethod(
        thOwnerOfMD.GetMethodTable(),
        pMD,
        ResolveVirtualStaticMethodFlags::AllowNullResult |
        ResolveVirtualStaticMethodFlags::AllowVariantMatches);
}
else
{
    // Ok to have INVALID_SLOT in the case where abstract class does not implement an interface method.
    DispatchSlot slot(typeHandle.GetMethodTable()->FindDispatchSlotForInterfaceMD(thOwnerOfMD, pMD, FALSE /* throwOnConflict */));
    if (!slot.IsNull())
        pResult = slot.GetMethodDesc();
}
```

Line numbers below are .NET 10's `methodtable.cpp`; .NET 11's are 75 to 101 lower for the same functions
(`FindDispatchImpl` 5444 / 5369, `FindDefaultInterfaceImplementation` 5805 / 5729, `ResolveVirtualStaticMethod`
8167 / 8066). The functions this QCall reaches are the same on both runtimes except for the handling of an async
variant method, which `GetInterfaceMap` never passes (read, by diffing each function).

**An instance method** (all read).

1. `FindDispatchSlotForInterfaceMD` (`:6026`) is `FindDispatchSlot(owner's type ID, pMD->GetSlot(), throwOnConflict)`:
   the *exact* interface the caller named and the method's slot number in it.
2. `FindDispatchImpl` (`:5444`) first walks the receiver and then each parent, and at each level looks in that level's
   own dispatch map for an entry at that interface and slot: first an exact match, then, if the interface is variant,
   the first entry whose interface casts to it by variance (`FindDispatchEntry` `:5369`, `FindEncodedMapDispatchEntry`
   `:5234`). The two passes are per level, so a variance-compatible entry on a more derived type wins over an exact
   one on its base. A found entry names a slot of the receiver's own vtable, and the answer is what that slot holds
   (`GetRestoredSlot`), so an override further down the chain is the answer.
3. No level maps it. For an array, the generic-interface case goes to `SZArrayHelper` (`:5484-5519`); otherwise
   `FindDefaultInterfaceImplementation` is asked at the exact interface, and only if that finds nothing and the
   interface has variance, again with variance allowed (`:5539-5547`).
4. `FindDefaultInterfaceImplementation` (`:5805`) collects the most specific candidates among the receiver (if it is
   an interface) and the interfaces of each level, using `TryGetCandidateImplementation` (`:5634`): on the method's
   own interface, the method itself unless it is abstract; on a more specific interface, a MethodImpl naming it,
   abstract or not. More than one survivor is a conflict. With variance allowed, an instance method's search takes the
   first survivor and never reports a conflict (`:5966-5969`). A conflict with `throwOnConflict` false returns FALSE,
   so step 3 then tries the variant pass if the interface is variant, and otherwise finds nothing.
5. A found body that is abstract is a reabstraction, and with `throwOnConflict` false it is treated as nothing found
   (`:5553-5563`). Otherwise the answer is the body, instantiated over the interface instantiation it was found on
   (`InstantiateFoundMethodDesc`).
6. Nothing found: the slot is null, and the QCall answers null.

**A static method** (all read). `ResolveVirtualStaticMethod` (`:8167`) with `AllowNullResult | AllowVariantMatches`
and no `uniqueResolution` pointer:

1. Each level from the receiver up its parents is asked for a MethodImpl naming the method at exactly the interface
   instantiation (`TryResolveVirtualStaticMethodOnThisType`, `:8335`), then, if the interface is variant, at each
   variance-compatible instantiation in that level's interface map. The first found is the answer.
2. Then `FindDefaultInterfaceImplementation`, exactly and then allowing variance, with `ThrowOnConflict` set because
   `uniqueResolution` is null. For a static method the variant pass *does* look for conflicts (the `break` at `:5968`
   is for instance methods only), so a conflict in either pass throws `AmbiguousImplementationException`.
3. The unique default found is returned whatever it is, **an abstract reabstraction included**: unlike the instance
   path, nothing here checks `IsAbstract`.
4. Otherwise the method's own body, if it has one; otherwise null (`AllowNullResult`).

### What it answers, case by case

Measured on real .NET 10 and 11 by a console program printing `GetInterfaceMap` for each pair (rows `interface method
-> target`, the target as `declaring type::name [reflected type]`). The two runtimes print the same apart from
CoreLib's metadata tokens.

| receiver / interface | rows (interface method -> target) |
|---|---|
| `COne : IOne { public void M() }` | `IOne.M -> COne::M [COne]` |
| `CExpl : IOne { void IOne.M() }` | `IOne.M -> CExpl::IOne.M [CExpl]` |
| `CDerNoOverride : CBase`, `CBase : IOne { public virtual void M() }` | `IOne.M -> CBase::M [CDerNoOverride]` |
| `CDerOverride : CBase { public override void M() }` | `IOne.M -> CDerOverride::M [CDerOverride]` |
| `CDerNV : CBaseNV`, `CBaseNV : IOne { public void M() }` (non-virtual in C#) | `IOne.M -> CBaseNV::M [CDerNV]` |
| `CReimpl : CBaseNV, IOne { public new void M() }` | `IOne.M -> CReimpl::M [CReimpl]` |
| `CRelist : CBase, IOne { }` | `IOne.M -> CBase::M [CRelist]` |
| `CExplDer : CBase, IOne { void IOne.M() }` | `IOne.M -> CExplDer::IOne.M [CExplDer]` |
| `CTwo : IOne, IOneB { public void M() }`, both interfaces | `COne`-like, the same `CTwo::M` for each |
| `abstract CAbs : IOne { public abstract void M(); }` | `IOne.M -> CAbs::M [CAbs]` |
| `struct SOne : IOne { public void M() }` | `IOne.M -> SOne::M [SOne]`, and it `==` `typeof(SOne).GetMethod("M")` |
| `struct SExpl : IOne { void IOne.M() }` | `IOne.M -> SExpl::IOne.M [SExpl]` |
| `CDim : IDim { }`, `IDim { void D() { } }` (and the struct `SDim`) | `IDim.D -> IDim::D [IDim]` |
| `CDimOverridden : IDim { public void D() }` | `IDim.D -> CDimOverridden::D [CDimOverridden]` |
| `CReabsD : IReabsD { void IBaseD.M() }`, `IReabsD : IBaseD { abstract void IBaseD.M(); }`, against `IBaseD` | `IBaseD.M -> CReabsD::IBaseD.M [CReabsD]` |
| the same, against `IReabsD` | `IReabsD.IBaseD.M -> null` (its own reabstraction) |
| `CDimOverride : IDimOverride`, `IDimOverride : IBase2 { void IBase2.M() { } }`, against `IBase2` | `IBase2.M -> IDimOverride::IBase2.M [IDimOverride]` |
| the same, against `IDimOverride` | `IDimOverride.IBase2.M -> IDimOverride::IBase2.M [IDimOverride]` |
| `CDiamondResolved : ID`, `IB, IC : IA` each overriding `IA.M`, `ID : IB, IC` overriding it again, against `IA` | `IA.M -> ID::IA.M [ID]` |
| the same, against `IB` | `IB.IA.M -> IB::IA.M [IB]` |
| `CDiamondClass : IB, IC { public void M() }`, against `IA` | `IA.M -> CDiamondClass::M [CDiamondClass]` |
| `struct SStat : IStat`, `IStat { static abstract int S(); void I(); }` | `IStat.I -> SStat::I [SStat]`, `IStat.S -> SStat::S [SStat]` |
| `CStatExpl : IStat` (explicit) | `-> CStatExpl::IStat.I`, `-> CStatExpl::IStat.S` |
| `CStatVirt : IStatVirt`, `IStatVirt { static virtual int SV() => 1; static abstract int SA(); }` | `IStatVirt.SV -> IStatVirt::SV [IStatVirt]`, `IStatVirt.SA -> CStatVirt::SA [CStatVirt]` |
| `CStatVirtOverride` (explicit `IStatVirt.SV`), `CStatVirtImplicit` (`public static int SV()`) | `SV` to the class's own method in each |
| `CStaticReabs : IStaticReabs`, `IStaticReabs : IBaseS { static abstract int IBaseS.S(); }`, against `IBaseS` | `IBaseS.S -> CStaticReabs::IBaseS.S [CStaticReabs]` |
| the same, against `IStaticReabs` | `IStaticReabs.IBaseS.S -> null` |
| `CDerSV : IDerSV`, `IDerSV : IBaseSV { static int IBaseSV.S() => 2; }`, against `IBaseSV` | `IBaseSV.S -> IDerSV::IBaseSV.S [IDerSV]` |
| the same, against `IDerSV` | no rows (its explicit static body is not virtual, so not counted) |
| `CStatDer : CStatBase`, `CStatBase : IStat` | both rows to `CStatBase`'s methods, reflected `CStatDer` |
| `CStatDerRelist : CStatBase, IStat { public static new int S() }` | `I -> CStatBase::I [CStatDerRelist]`, `S -> CStatDerRelist::S` |
| `CCo : ICo<string>`, `ICo<out T> { T Get(); }`, against `ICo<object>` | `ICo<object>.Get -> CCo::Get [CCo]` |
| `CCoD : ICoD<string>`, `ICoD<out T> { string D() => "x"; }`, against `ICoD<object>` | `ICoD<object>.D -> ICoD<string>::D [ICoD<string>]` |
| `VDer : VBase, ICo<string> { public new string Get() }`, `VBase : ICo<object>`, against `ICo<object>` | `-> VDer::Get [VDer]`: the variance-compatible entry on `VDer` wins over the exact one on `VBase` |
| `CCoS : ICoS<string>`, `ICoS<out T> { static abstract string S(); }`, against `ICoS<object>` | `-> CCoS::S [CCoS]` |
| `CContra : IContra<object>`, against `IContra<string>` | `-> CContra::Put [CContra]` |
| `List<string>`, against `IEnumerable<object>` | `-> List<string>::IEnumerable<T>.GetEnumerator [List<string>]` |
| `CGM : IGM`, `IGM { void G<T>(); static abstract void SG<T>(); }` | `-> CGM::G`, `-> CGM::SG`, both generic method definitions |
| `CGDM : IGDM`, `IGDM { void G<T>() { } }` | `IGDM.G -> IGDM::G [IGDM]`, a generic method definition |
| `CG : IG<int>`; `CGen<string> : IG<string>`; `CGen<int>` | each row to the receiver's own method |
| `CGen<>` against its own `IG<T>` (an open receiver) | `IG<T>.Get -> CGen<T>::Get [CGen<T>]`, and `Make` likewise |
| `List<>` against its first interface | rows to `List<T>`'s methods |
| `CGDer : CGBase<string>`, `CGBase<T> : IOne { public virtual void M() }` | `IOne.M -> CGBase<string>::M [CGDer]` |
| `CGD : IGD<string>`, `IGD<T> { T D() => default; }` | `-> IGD<string>::D [IGD<string>]`, which `==` `typeof(IGD<string>).GetMethod("D")` |
| `CProps : IProps` (property and event) | four rows to the accessors |
| `CTask : ITask`, `ITask { Task<int> A(); int B(); ValueTask C(); static abstract Task S(); static abstract int T(); }` | five rows, `A, B, C, S, T`, each to `CTask`'s method, on both runtimes |
| `int[]` against `IList` (and `ICollection`, `IEnumerable`, `IStructuralEquatable`); `int[,]` against `IList`; `string[]` against `IEnumerable` | each row to `System.Array`'s method, reflected as the array type: `IList.get_Item -> Array::IList.get_Item [Int32[]]` |
| `int` against `IComparable<int>`; `string` against `IEnumerable<char>` | the receiver's own methods |

**Cases C# refuses to compile**, measured with a library built in two versions: the classes were compiled against
the first, in which the shape below is absent, and run against the second. Same answers on both runtimes.

| shape | `GetInterfaceMap` |
|---|---|
| instance diamond with no most specific body: `IB, IC : IA`, each overriding `IA.M`, class `: IB, IC` (also measured with `System.Reflection.Emit`) | `IA.M -> null` |
| the same through a variant interface, both overriding `IAv<string>.M`, asked at `IAv<string>` | `-> IBv::IAv<string>.M [IBv]`: the exact pass conflicts, the variant pass takes the first |
| the same, asked at `IAv<object>` | `-> IBv::IAv<string>.M [IBv]` |
| static diamond: `IBs, ICs : IAs` each overriding `static virtual int IAs.S()` | **throws** `AmbiguousImplementationException`, 0x8013106a, "Could not call method 'IAs.S()' on interface 'IAs' with type 'CDiamondS' from assembly '...' because there are multiple incompatible interface methods overriding this method."; against `IBs`, no rows |
| static diamond through a variant interface, asked at `IAsv<string>` or at `IAsv<object>` | **throws** `AmbiguousImplementationException` at both, where a constrained call through `IAsv<object>` returns the first body's 2 |
| static reabstraction most specific: `IRr : IAr { static abstract int IAr.S(); }` over `IAr`'s body, class `: IRr` implementing nothing | `IAr.S -> IRr::IAr.S [IRr]`, an **abstract** method (`IsAbstract` true); a constrained call throws `EntryPointNotFoundException`. Against `IRr`: `IRr.IAr.S -> null` |
| instance reabstraction most specific, class implementing nothing (also with Reflection.Emit) | `IAi.M -> null` |
| an abstract class listing `IOne` without `M` (Reflection.Emit) | `IOne.M -> null` |
| an abstract class listing `IBaseS` without implementing its static abstract `S` (Reflection.Emit) | `IBaseS.S -> null` |
| a Reflection.Emit class implementing `IOne.M` by a MethodImpl naming a private method `IOne_M_Intercepted`, as a proxy does | `IOne.M -> Proxy::IOne_M_Intercepted [Proxy]` |
| a Reflection.Emit class implementing it implicitly by name | `IOne.M -> ProxyImplicit::M` |

Reflection.Emit refuses to define a static virtual method ("Method cannot be both static and virtual."), so the
static shapes can only be built from two library versions (or by hand-written metadata).

### Corrections to the issue

1. The issue's table says `int[]` against `IList` gives targets "not recorded". Measured: every row has a target,
   `System.Array`'s method, reflected as `Int32[]`, on both runtimes.
2. "Reabstraction (null?)" holds for an instance method and for an interface's *own* reabstraction asked about
   directly, static or not. It does **not** hold for a static method whose most specific default is a reabstraction
   on a more derived interface: the answer is that abstract reabstraction.
3. A diamond without a most specific body does not give one answer: null for an instance method, the first body for
   an instance method of a variant interface conflicting at the exact instantiation, and a thrown
   `AmbiguousImplementationException` for a static method, variant or not.

## PawPrint today

### What exists (read)

- **Instance dispatch**: `ConcreteVirtualDispatch.tryResolveVirtualImplementation`
  (`WoofWare.PawPrint.TypeSystem/ConcreteVirtualDispatch.fs`), which `callvirt`, `ldvirtftn`, delegates and the
  escape analysis use. For an interface method it is `ConcreteInterfaceDispatch.tryFindImplementationSlot` (the
  per-level, exact-then-variant dispatch map walk, the same rule as `FindDispatchEntry`), then reads that slot of the
  receiver's dispatch table; failing that, `DefaultInterfaceImplementation.search` exactly and then, for a variant
  interface, allowing variance. It answers a `VirtualImplementation`: `Found` (a definition and its type and method
  arguments), `NotImplemented`, `Reabstracted`, `Ambiguous`, `Unmodelled` or (never for an interface method with
  `walkBaseTypes` true) `NotOverridden`. It refuses a static method.
- **Static dispatch**: `StaticVirtualDispatch.resolve`, which the `constrained.` call and `ldftn` use, and the escape
  analysis: the chain's MethodImpls exactly and then by variance, `DefaultInterfaceImplementation.search` exactly
  and then by variance, then the method's own body. It refuses a non-static method.
- Both take the interface method *concretised*, `MethodInfo<ConcreteTypeHandle, ConcreteTypeHandle,
  ConcreteTypeHandle>`, and the receiver as a `ConcreteTypeHandle`. Neither accepts an open receiver.
- **The existing oracle**: `TestInterfaceDispatchMap.fs` compares `tryFindImplementationSlot` and the slot's
  occupant against host `GetInterfaceMap` over CoreLib and a generated corpus of class hierarchies. It compares only
  the class half: it skips static methods, and where PawPrint finds no class implementation it accepts whatever the
  host answers, so default bodies, reabstractions and conflicts are not compared.
- **Handles**: `GetMethodAt` mints `MethodHandleRegistry.getOrAllocateInternalId (assembly, declaring target, method
  definition)` with the declaring type the receiver's chain names (`VirtualSlotLayout.declaringTypeAt`). This QCall's
  argument is such a handle, decoded by `NativeRuntimeMethodHandle.resolveMetadataIdentityFromArg` and
  `MethodHandleResolution.methodInfoOfMetadataIdentity`; its answer is a `RuntimeMethodHandleInternal` value, minted
  the same way, or `MethodHandleRegistry.zeroInternalHandle` for null. PawPrint has no canonical forms and no
  unboxing stubs, so where CoreCLR's handle is `CGBase<__Canon>.M` or `SOne.M`'s unboxing stub, PawPrint's is
  `CGBase<string>.M` or `SOne.M`; `GetMethodBase` resolves both to the same `MethodInfo` (measured below).

### Does one function already answer the question?

Not exactly. The interpreter's two dispatch functions answer "which method does a *call* run", and this QCall asks
"which method does `FindDispatchSlot` or `ResolveVirtualStaticMethod` name *for reflection*". They are the same walk
with different handling of the failures. Mapping each `VirtualImplementation` to what the QCall must answer:

| dispatch answer | instance method: QCall | static method: QCall |
|---|---|---|
| `Found m` | `m` | `m` |
| `NotImplemented` | null | null |
| `Reabstracted r` | null | **`r`**, the abstract reabstraction |
| `Ambiguous _` (exact pass, non-variant interface) | null | raise `AmbiguousImplementationException` |
| `Unmodelled` for a conflict at a variant interface's exact instantiation | **the variant pass's first survivor** (determinate: `throwOnConflict` is false) | not produced |
| static, conflict in the *variant* pass: dispatch answers `Found first` (the JIT passes `uniqueResolution`) | not applicable | **raise** `AmbiguousImplementationException` |
| `Unmodelled` for `IDynamicInterfaceCastable`, or an array with a generic interface | both arise only for an interface the receiver does not implement, which #2270's check refuses before this QCall (expected for `IDynamicInterfaceCastable`, whose dynamic implementations `CanCastTo` does not consult; not measured), and the managed caller refuses an SZ array with a generic interface | not applicable |

So a projection of `VirtualImplementation` answers every row but two: the instance conflict through a variant
interface (dispatch refuses, the QCall has an answer) and the static conflict in the variant pass (dispatch gives the
call's answer, the QCall throws). Both need CoreCLR's own parameter, `throwOnConflict` for the instance walk and the
presence of `uniqueResolution` for the static one, which PawPrint's dispatch fixes to the call's value.

Two more differences are not in the result type:

- **Open receivers.** `typeof(CGen<>).GetInterfaceMap(typeof(CGen<>).GetInterfaces()[0])` succeeds on real .NET.
  PawPrint's dispatch works on `ConcreteTypeHandle`s and cannot take `CGen<>` or `IG<T>`.
- **Generic methods.** The QCall is given a method definition (`G<T>`) and answers one. The dispatch functions take a
  concretised method, and `concretizeMethodWithAllGenerics` has no way to leave the method's own parameters open. Only
  identity matters to the walk for an interface method (the dispatch map is keyed on the slot identity and the
  default-body search on the MethodDef), but the signatures make the caller invent arguments.

### PawPrint with a throwaway (reverted)

The throwaway handler in `Native/NativeRuntimeTypeQCall.fs` was option A of decision 1: it decoded the two type
handles with `NativeCall.qCallTypeHandleToRuntimeTypeHandleTarget` and the method with
`NativeRuntimeMethodHandle.resolveMetadataIdentityFromArg`, refused anything but a closed receiver and interface,
concretised the interface method over the interface's arguments (and `System.Object` for each method parameter),
called `ConcreteVirtualDispatch.tryResolveVirtualImplementation` (`walkBaseTypes` true) or
`StaticVirtualDispatch.resolve`, and projected the answer by the table above, minting a `Found` method with
`getOrAllocateInternalHandle` on the closed type that declares it, and raising `AmbiguousImplementationException`
with no message for a static `Ambiguous`. All measured:

- **The C#-compilable rows.** 66 receiver and interface pairs, each run as its own PawPrint process on .NET 10's
  CoreLib and compared line by line with real .NET 10 (row counts, method names, declaring and reflected types, and
  each method's metadata token, CoreLib's included). 64 are identical: every row of the table above that has a closed receiver. The other two are
  the open receivers (`CGen<>`, `List<>`), which stop *before* this QCall, in `GetInterfaces`: "TODO:
  RuntimeTypeHandle.GetInterfaces for open generic typedef ... interface row resolves to ...". So no open receiver
  reaches this QCall through `GetInterfaces()` today; one can only by building the open interface by hand.
- **.NET 11's CoreLib.** 11 of the pairs, chosen to cover each kind (class, struct, default body, interface's own
  reabstraction, static, static by a derived interface's body, variant default body, `List<string>` by variance,
  generic methods, `ITask`, `int[]` with `IList`), published against .NET 11 and compared with real .NET 11, tokens
  included: identical, `CTask` / `ITask` included (five rows, `A, B, C, S, T`).
- **`TypeBuilder` types.** The Reflection.Emit mode, with dynamic code allowed: identical to real .NET 10, for the
  proxy-shaped MethodImpl, the implicit implementation, the abstract classes without an implementation (null) and the
  instance diamond and reabstraction (null).
- **Shapes C# refuses** (the two-version library), each case its own process, against real .NET 10:
  - the static diamond (`IAs`) and the static variant diamond asked at its exact instantiation (`IAsv<string>`):
    `AmbiguousImplementationException` with HResult 0x8013106a, with the message "Ambiguous implementation found."
    where real .NET names the method;
  - the static reabstraction (`IRr::IAr.S`, abstract) and its own row (null), the instance reabstraction (null), the
    variant diamond asked by variance (`IBv`'s body): identical;
  - **the variant diamond asked at its exact instantiation (`IAv<string>`): refused**, by dispatch's `Unmodelled`
    ("more than one most-specific default body of M at a variant interface's exact instantiation, where CoreCLR
    throws or runs the first depending on how the JIT compiled the call"), where real .NET answers `IBv`'s body;
  - **the static variant diamond asked by variance (`IAsv<object>`): a wrong answer**, a map with one row to
    `IBsv::IAsv<string>.S`, where real .NET throws `AmbiguousImplementationException`. The constrained call through
    `IAsv<object>` returns 2 on both, so dispatch is right for the call and wrong for reflection;
  - a class compiled against an interface to which version 2 adds a `static abstract` member: real .NET throws
    `TypeLoadException` ("Virtual static method 'S' is not implemented on type 'CAddS' ...") when the type loads;
    PawPrint loads it and maps the row to null. That is CoreCLR's `VerifyThatAllVirtualStaticMethodsAreImplemented`
    (`methodtable.cpp:8535`), a load-time check PawPrint does not make; it is outside this QCall, and worth an issue.
- **The parked guest.** `sourcesPure/TypeGetInterfaceMap.cs`, compiled as its own program: exit 0 under PawPrint on
  .NET 10's CoreLib, and on real .NET 10 and 11.
- **CoreLib.** One guest printing a line for every row of every map of every exported non-generic CoreLib type that
  is not an interface (6,835 rows over 490 types on real .NET 10), run under PawPrint on .NET 10's CoreLib with the
  throwaway changed to answer null, and log the reason, where it would otherwise stop. Compared row by row, keyed on
  type, interface and interface method token, over the first 4,701 rows (302 of the 490 types; the run was stopped
  after 80 minutes on a loaded machine): every row is identical, tokens included, except 161, and those 161 are exactly the rows
  the throwaway skipped: a static virtual generic method of `INumberBase<T>` (`CreateChecked`,
  `TryConvertFromChecked` and their siblings) or `IFloatingPoint<T>` (`ConvertToInteger`, `ConvertToIntegerNative`),
  each refused by decision 2 (b)'s invented `System.Object` argument. 2,102 of the compared rows are on
  `System.Numerics` interfaces, so static virtuals answered by a MethodImpl are well covered, and 315 are answered by
  the interface's own default body. No row of real .NET's run is null or throws, so CoreLib alone does not exercise
  the null and conflict rows; the generated and two-version corpora must.

Read, not measured: `StaticVirtualDispatch` refuses a static body whose MethodImpl rows a dynamic module reordered
after the type was created (`DefaultInterfaceImplementation.OrderDependentBody`), because a call's answer depends on
whether CoreCLR resolved and cached it before the change. This QCall caches nothing: `TryResolveVirtualStaticMethodOnThisType`
enumerates the rows afresh at every call (`methodtable.cpp:8335`), so its answer is the one a read *now* finds. That is
a third place where the reflection answer is determinate and the call's is not, and decision 1 B's parameter should
select it.

## Options

### Decision 1: how the QCall's answer relates to the interpreter's dispatch

Every option answers the rows of the case table with a closed receiver the same way (the throwaway, option A, matched
all 64). They differ in the conflict rows, in what keeps a reflection answer and a call's answer in agreement, and in what a test of one
says about the other.

#### A. Project the interpreter's dispatch as it is (the throwaway)

The handler concretises the interface method, calls `ConcreteVirtualDispatch.tryResolveVirtualImplementation` or
`StaticVirtualDispatch.resolve`, and maps the `VirtualImplementation` by the table under "Does one function already
answer the question?".

- One walk for calls and reflection, and no change to the TypeSystem.
- It cannot answer the two rows where CoreCLR's parameter differs: the instance conflict through a variant interface
  (dispatch says `Unmodelled`, so the handler refuses where real .NET answers), and the static conflict in the variant
  pass, where dispatch says `Found first` and the handler would return it where real .NET throws: a **wrong answer**,
  not a refusal, and nothing in `VirtualImplementation` lets the handler tell. Both need IL C# refuses.
- Generic interface methods need invented method arguments (the throwaway used `System.Object`), which constraints
  refuse: measured in the CoreLib sweep (decision 2).
- Blast radius: the handler. Reversibility: total. Information kept: less than the walk has.

#### B. Give the shared walk CoreCLR's own parameter

CoreCLR shares one walk between calls and this QCall and differs only in a parameter: `throwOnConflict` for an
instance method (`FindDispatchImpl`), and whether a `uniqueResolution` pointer is passed for a static one
(`ResolveVirtualStaticMethod`). Do the same: each TypeSystem function takes an explicit argument saying which of
CoreCLR's callers it is answering for, as a DU rather than a `bool` (for instance `ConflictPolicy.CallSite` and
`ConflictPolicy.Reflection`, the names to be settled in review), with these meanings:

| | `CallSite` (today's behaviour) | `Reflection` |
|---|---|---|
| instance, exact-pass conflict, non-variant interface | `Ambiguous` | `NotImplemented` |
| instance, exact-pass conflict, variant interface | `Unmodelled` (JIT-dependent) | the variant pass's first survivor |
| instance, reabstraction most specific | `Reabstracted` | `Reabstracted` (the projection makes it null) |
| static, exact-pass conflict | `Ambiguous` | `Ambiguous` |
| static, variant-pass conflict | the first survivor | `Ambiguous` |
| static, a body whose MethodImpl rows a dynamic module reordered (`OrderDependent`) | refused | the body a read now finds |

The handler then projects the result as in A, with nothing left unanswerable. The interpreter's callers and the
escape analysis pass `CallSite` and change no behaviour.

- One walk, written once; the two callers differ in a value a reader can see at the call site, as in CoreCLR.
- A host oracle over `GetInterfaceMap` then tests the interpreter's dispatch walk itself, default bodies and static
  virtuals included, which `TestInterfaceDispatchMap.fs` does not reach today.
- Blast radius: a public signature change in two TypeSystem functions (the published package), four call sites (the
  interpreter's two wrappers, the escape analysis's two uses), and a behaviour-preserving refactor commit. Reversibility:
  high. Information kept: all of it.

#### C. A richer result, projected by each caller

The walk returns what it found at each stage: the chain's implementation, or the exact pass's survivors, and the
variant pass's survivors. The call path's choice (`Ambiguous`, `Unmodelled`, first) and the QCall's become two
projections of one value.

- Keeps the most information, and makes the call path's `Unmodelled` row a visible projection rather than a branch
  inside the walk.
- Replaces `VirtualImplementation` for every consumer, the escape analysis included, and asks the static walk to
  report a variant-pass conflict it currently resolves; the largest change for no answer B does not also give.
- Blast radius: every reader of `VirtualImplementation`. Reversibility: moderate.

#### D. A separate resolver for reflection

A new function transliterating the QCall's path (`FindDispatchImpl` with `throwOnConflict` false, and
`ResolveVirtualStaticMethod` without `uniqueResolution`), composed from the same lower pieces
(`tryFindImplementationSlot`, the dispatch table, `DefaultInterfaceImplementation.search`, `implementationOnType`).

- Closest to the QCall's source text, and leaves the call path untouched.
- Two compositions of one search order which must agree, where a disagreement means reflection and calls name
  different methods for the same pair; and the host oracle would test only the reflection copy.
- Blast radius: a new module. Reversibility: total. Information kept: as B, but in two places.

**Recommended: B.**

### Decision 2: what the shared walk is given for the interface method

- **(a) The interface instantiation and the method definition**, not a concretised method. For an interface method
  the walk needs only identity: the dispatch map is keyed on the method's `SlotIdentity`, the default-body search on
  the MethodDef, and a static MethodImpl on the MethodDef and the interface instantiation. The found method comes back
  as today's `DispatchedMethod` with the type arguments it was found at; a call site adds its method arguments when it
  instantiates it, which it does already (`IlMachineStateExecution.tryResolveVirtualImplementation`). The QCall then
  needs no method arguments at all.
- **(b) Keep the concretised method**, and let the handler concretise a generic interface method over invented
  arguments, discarding them from the answer. Smallest change; the throwaway did it with `System.Object` and passed
  `IGM`. But it puts a fiction into a type that says "as the call instantiates it", and a constrained parameter
  refuses it: measured, the CoreLib sweep stopped at `decimal`'s `IFloatingPoint<decimal>`, whose static virtual
  `ConvertToInteger<TInteger> where TInteger : IBinaryInteger<TInteger>` cannot be instantiated over `System.Object`
  ("CoreCLR refuses an instantiation of the generic method ConvertToInteger ... with VerificationException: its
  argument 0 violates that type parameter's constraints"). `INumberBase<T>.CreateChecked<TOther> where TOther :
  INumberBase<TOther>` has the same shape (read), so every numeric type's maps are expected to stop the same way.
- **(c) Refuse generic interface methods** in the handler, with a TODO. Honest, but `GetInterfaceMap` over any
  interface with a generic method stops, and a FakeItEasy fake of such an interface would stop at its first
`A.CallTo` (not measured).

**Recommended: (a)**, in B's refactor commit. If B is not chosen, (b).

### Decision 3: open receivers

`typeof(CGen<>)` with its own `IG<T>`, or `typeof(List<>)` with its interfaces, succeed on real .NET (measured), and
`VerifyInterfaceIsImplemented` already admits them in PawPrint.

- **(a) Refuse**, with a `TODO:` naming the measured answer, before any concretisation, and file an issue. PawPrint's
  dispatch maps are built over `ConcreteTypeHandle`s, which cannot name `CGen<>`'s `T`; nothing measured on the way
  to FakeItEasy reaches this.
- **(b) Model it**: walk the definition's dispatch map and slot table (`contentVtableOfDefinition` exists for the slot
  table, not for the interface map), answering with a handle declared on the open receiver or one of its open bases,
  as `VirtualSlotLayout.declaringTypeAt` already names them. A second, definition-level dispatch, which is the thing
  decision 1 is trying not to have.

**Recommended: (a).**

### Decision 4: where the projection to the QCall's answer lives

- **(a) In `WoofWare.PawPrint.TypeSystem`**, as a function returning its own DU, for instance
  `InterfaceMapTarget = Method of DispatchedMethod | NoTarget | Ambiguous of candidates`, so that `NoTarget` (a null
  row) and `Ambiguous` (a raise) are named rather than read off `VirtualImplementation` by each caller. The handler
  mints the handle and raises. A test can then ask it with no machine, as `TestInterfaceDispatchMap.fs` asks the slot
  walk.
- **(b) In the handler**, as a `match` over `VirtualImplementation`. Smaller; the projection table lives only in
  `Native/NativeRuntimeTypeQCall.fs`, where a property test must drive it through a full `IlMachineState`.

**Recommended: (a).** AGENTS.md says to put a fact in the lowest project that can hold it, and the projection is a fact
about CoreCLR's QCall, not about the machine.

### Decision 5: the handle the QCall answers

- **(a) The method declared by the type the walk found it on, at that type's instantiation**: the receiver's class
  chain for a class implementation (`CGBase<string>` for `CGDer`), the interface instantiation for a default body
  (`ICoD<string>` for `CCoD` asked at `ICoD<object>`), the reabstracting interface for a static reabstraction. Minted
  with `MethodHandleRegistry.getOrAllocateInternalId`, as `GetMethodAt` mints. This is what `GetMethodBase` and the
  managed caller's reflected-type rule need (measured: every row's `DeclaringType` and `ReflectedType` match).
- **(b) CoreCLR's exact `MethodDesc`**, canonical (`CGBase<__Canon>.M`) or an unboxing stub. PawPrint has neither, and
  `GetMethodBase` turns CoreCLR's into the same `MethodInfo` as (a) (measured: `SOne`'s target `==`
  `typeof(SOne).GetMethod("M")`; `CGDer`'s target's declaring type is `CGBase<string>`).

**Recommended: (a).** The difference is visible only by reflecting over CoreLib's internals to compare handles.

### Decision 6: the message of `AmbiguousImplementationException`

- **(a) The parameterless constructor's message**, "Ambiguous implementation found.", as the interpreter's
  `raiseDispatchFailure` raises for a call. The throwaway did this.
- **(b) CoreCLR's `IDS_CLASSLOAD_AMBIGUOUS_OVERRIDE`**, "Could not call method 'IAs.S()' on interface 'IAs' with type
  'CDiamondS' from assembly '...' because there are multiple incompatible interface methods overriding this method.",
  which needs CoreCLR's `TypeString` rendering of the method, interface, type and assembly.

**Recommended: (a)**, consistent with the call path, and an issue for both if the message is wanted; the static
conflict needs IL C# refuses.

## Questions for the decider

1. **How does the QCall relate to the interpreter's dispatch: A (project it as it is), B (give the shared walk
   CoreCLR's own conflict parameter), C (a richer result projected by each caller) or D (a separate resolver for
   reflection)?** Recommended: B. A answers the two conflict rows wrongly or not at all; both need IL C# refuses.
2. **If B, may the TypeSystem's `ConcreteVirtualDispatch.tryResolveVirtualImplementation` and
   `StaticVirtualDispatch.resolve` change their public signatures** (the new parameter, and decision 2's interface
   handle and definition in place of a concretised method), in a commit of their own that changes no behaviour? They
   are in the published `WoofWare.PawPrint.TypeSystem` package. Recommended: yes, and say so in the PR body.
3. **What is the walk given for the interface method: the interface instantiation and the method definition (a), a
   concretised method over invented method arguments (b), or are generic interface methods refused (c)?**
   Recommended: (a).
4. **Are open receivers (`typeof(List<>)` with its own interfaces) refused with a `TODO:` and an issue (a), or
   modelled by a definition-level walk (b)?** Recommended: (a). Measured: real .NET answers them; nothing on
   FakeItEasy's path reaches them.
5. **Does the projection to the QCall's answer live in the TypeSystem as a function with its own result type (a), or
   in the handler (b)?** Recommended: (a).
6. **Is the answer's handle declared by the type the walk found the method on, at that instantiation (a), with no
   attempt at CoreCLR's canonical or unboxing-stub `MethodDesc`s (b)?** Recommended: (a).
7. **Does the QCall's `AmbiguousImplementationException` carry the parameterless constructor's message, as the
   interpreter's call path does (a), or CoreCLR's `IDS_CLASSLOAD_AMBIGUOUS_OVERRIDE` text (b)?** Recommended: (a),
   and one issue for both paths if the text is wanted.
8. **May the property test's third corpus load two versions of a library into the host (a collectible
   `AssemblyLoadContext`) to build shapes C# refuses?** It is the only same-image oracle for the conflict rows.
   Recommended: yes; fall back to cross-assembly guests alone if the host side proves fragile.
9. **Should the issue's table be corrected?** Its `int[]` / `IList` row says the targets are "not recorded"; measured,
   each is `System.Array`'s method, reflected as `Int32[]`. Recommended: yes, in a comment on #2290 when the decisions
   are recorded.

## Order of work (with the recommendations)

1. The tests and guests below, failing, in one commit.
2. Decisions 1 B and 2 (a): the conflict parameter and the interface-handle-and-definition entry for the two
   TypeSystem dispatch functions, every existing caller passing `CallSite`; no behaviour change.
3. Decision 4 (a)'s projection in the TypeSystem, and the handler in `Native/NativeRuntimeTypeQCall.fs`, registered in
   `Native/NativeQCall.fs` beside `RuntimeTypeHandle_GetNumVirtualsAndStaticVirtuals`; decision 3 (a)'s refusal.
4. Un-park `TypeGetInterfaceMap.cs`.

Issues to file, none fixed here: open receivers (decision 3); the missing `VerifyThatAllVirtualStaticMethodsAreImplemented`
load check (measured above); and, if decision 6 (b) is wanted, CoreCLR's message for `AmbiguousImplementationException`
on both paths.

## Test design

Write the tests and guests first, in a commit of their own, with their failing output in the commit message (the
compile failure of the new F# tests; the unimplemented-QCall stop of each guest). If decision 1 is B, the refactor that
adds the parameter comes next, as a commit that changes no behaviour, with the existing dispatch tests
(`TestInterfaceDispatchMap`, `TestAmbiguousDefaultInterfaceDispatch`, `TestCrossAssemblyReabstraction`,
`TestCrossAssemblyAmbiguousDefault`, `TestStaticVirtualOverrideOrder`, `TestAsyncVariantDispatch`, the escape
analysis's oracle) green across it unedited. Run the default suite and the four Guest categories as separate
invocations.

### Property tests against the host

In `WoofWare.PawPrint.Test`, as `TestInterfaceMethodImplementation.fs`, using `HostTypeTarget` (lifted by #2289) to
build each type twice, as a PawPrint target and as a host `Type` over the same image.

The oracle for a receiver `t` and an interface `i` it implements (`t.GetInterfaces()`, skipping an SZ array's generic
interfaces, which the managed caller refuses): `t.GetInterfaceMap(i)`. For each row, PawPrint's answer for
(`t`, `i`, the row's interface method definition) must be:

- no target where the host's target is null;
- `Ambiguous` where the host's `GetInterfaceMap` throws `AmbiguousImplementationException` (the whole map, since the
  host gives no rows then; PawPrint must be `Ambiguous` for at least one of the interface's methods and agree on the
  others);
- otherwise a method with the host target's metadata token and module, declared by the type that is the host target's
  `DeclaringType` (an interface instantiation or a type on `t`'s chain, compared by `HostTypeTarget`), and abstract
  exactly when the host's is.

With decision 1 B, the same property also states that the call path agrees where CoreCLR's two callers do: for every
row where the host answers a non-abstract target, the interpreter's dispatch with `CallSite` finds the same method,
and for a null instance row it finds `NotImplemented`, `Reabstracted` or `Ambiguous`. That is the part
`TestInterfaceDispatchMap` cannot say today.

1. **CoreLib.** Every exported CoreLib type that is not an interface, with each interface it implements, and the
   spread of generic instantiations `TestInterfaceDispatchMap` uses. Generic definitions (open receivers) are counted
   and, under decision 3 (a), asserted to be refused; they are not compared. Assert floors on the comparisons, on the
   rows answered by a default body, and on static rows (CoreLib's `INumber<T>` family gives hundreds; measure the
   counts in the tests commit and set each floor at about half).
2. **Generated corpus (FsCheck).** Extend #2289's interface generator (`TestNumVirtualsAndStaticVirtuals.fs`: instance
   abstract and default methods, sealed and private methods, static abstract and static virtual methods, generic
   methods, explicit default bodies, instance and static reabstractions of a base interface's method) with classes and
   structs that implement them in every way C# allows: implicitly, explicitly, by an inherited virtual or non-virtual
   method, by an override further down the chain, by `new` re-implementation and by re-listing the interface, by
   leaving a default body in place, and over a variant interface (`out T` and `in T`) asked at a variance-compatible
   instantiation. Lift the generator into a shared module rather than copying it. Coverage labels from the host's side,
   with floors at about half of what a fixed sample reaches: a row answered by a class's own method, by an inherited
   one, by a default body on the interface itself, by a default body on a more specific interface, a null row, a static
   row answered by a MethodImpl, a static row answered by a default body, a row answered through variance, and a row
   whose target is a generic method definition.
3. **Shapes C# refuses**, by compiling the classes against a first version of the interface library and loading
   them on the host (in a collectible `AssemblyLoadContext`) and into PawPrint against a second version, as
   `TestCrossAssemblyReabstraction.fs` does for calls: the instance diamond with no most specific body, the same
   through a variant interface asked at its exact instantiation and at a variance-compatible one, the static diamond
   and the static variant diamond (both throw), the static reabstraction as most specific default (an abstract target),
   the instance reabstraction as most specific default (null). These are the rows that decide decision 1; generating
   them (a random second version of a generated library) is worth it if the fixed shapes find anything.

### Example tests

- Decision 3 (a): `CGen<>` with its own `IG<T>` and `List<>` with `IEnumerable<T>` are refused with the `TODO:`
  naming the measured answer.
- Decision 2 (a): `IGM`'s instance and static generic methods and `IGDM`'s generic default body answer the definitions
  (`G`, `SG`, `IGDM.G`) without any method arguments being supplied.
- The handle: `CGDer`'s answer is declared by `CGBase<string>`; `CCoD` asked at `ICoD<object>` is declared by
  `ICoD<string>`.

### Guests

Every expectation below was measured on real .NET 10.0.14 and 11.0.0-rc.3 by the probes (the case table, the
two-version library, the Reflection.Emit mode); the guests themselves are still to be written, and their controls are
what confirm each check on both runtimes. Under PawPrint with the throwaway, the C#-compilable and Reflection.Emit
expectations held on .NET 10's CoreLib, and the 11 sampled pairs, `ITask` among them, on .NET 11's.

Each with one exit code per check, each expectation the same on real .NET 10 and 11, and each run as a control on both
with every expectation perturbed, as #2289's controls were (the guest as written returns 0; each perturbed copy returns
its own check's number).

- **New `sourcesPure/TypeGetInterfaceMapTargets.cs`**: the C#-compilable rows of the table above, checking for each
  the row count, the interface method's name, the target's name, `DeclaringType`, `ReflectedType`, and
  `IsGenericMethodDefinition` where it applies, and null where real .NET answers null (`IReabsD`, `IStaticReabs`). At
  least: `COne`, `CExpl`, `CDerNoOverride`, `CDerOverride`, `CDerNV`, `CReimpl`, `CRelist`, `CExplDer`, `CAbs`, `SOne`,
  `SExpl`, `CDim`, `SDim`, `CDimOverridden`, `CReabsD` against both interfaces, `CDimOverride` against both,
  `CDiamondResolved` against `IA`, `SStat`, `CStatExpl`, `CStatVirt`, `CStaticReabs` against both, `CDerSV` against
  `IBaseSV`, `CStatDerRelist`, `CCo` and `CCoD` and `VDer` at `object`, `CCoS` at `object`, `CContra` at `string`, `CGM`,
  `CGDM`, `CGDer`, `CGD`, `CProps`, `int[]` against `IList`. Its fixtures name no `Task`-returning member, so the
  guest is the same on both runtimes.
- **Un-park `sourcesPure/TypeGetInterfaceMap.cs`** on every runtime. Measured to exit 0 under PawPrint with the
  throwaway on .NET 10's CoreLib, and on real .NET 10 and 11. Its assertions are few; leave them, since the new guest
  carries the rest.
- **New `sourcesPure/TypeGetInterfaceMapTask.cs`**, the `ITask` guest #2290's comment asks for: `CTask` against
  `ITask` gives five rows, `A, B, C, S, T`, in that order, each to `CTask`'s method. The same on real .NET 10 and 11
  (measured); it is the check, through public API alone, that the async variant slots PawPrint does not lay out cannot
  be seen in a map. It runs on every runtime; it is the .NET 11 run that matters.
- **New cross-assembly guests** (`CrossAssemblyHarness`, like `TestCrossAssemblyReabstraction.fs`) for the shapes C#
  refuses, with the same expectations as the property's third corpus: null, the first body (`IBv`'s), a caught
  `AmbiguousImplementationException` (twice), and an abstract target declared by the reabstracting interface. These
  are the guests that kill decision 1's mutants.
- **New `sourcesImpure/TypeBuilderGetInterfaceMap.cs`**: a created class implementing `IOne.M` through a MethodImpl
  naming a private method (the shape a proxy takes), one implementing it implicitly by name, and an abstract class
  listing `IOne` without `M` (null). Measured on real .NET 10 and 11.

## Mutants

Each must turn some test red; record which. "Guest" means `TypeGetInterfaceMapTargets.cs` unless named.

| mutant | expected to be killed by |
|---|---|
| a null answer encoded as `MethodRegistryHandle 0` instead of a verbatim zero | every null row (`IsNullHandle` is false, and `GetMethodBase` fails) |
| an instance `Reabstracted` answered as the reabstraction | the cross-assembly instance-reabstraction guest |
| a static `Reabstracted` answered as null | the cross-assembly static-reabstraction guest |
| an instance `Ambiguous` raised instead of null | the cross-assembly instance-diamond guest |
| a static `Ambiguous` answered as null or as its first candidate | the cross-assembly static-diamond guest |
| the instance conflict through a variant interface answered as null (`throwOnConflict` read as for a non-variant interface) | the cross-assembly variant-diamond guest at `IAv<string>` |
| the static conflict in the variant pass answered as the first survivor (the call's rule) | the cross-assembly static variant-diamond guest at `IAsv<object>` |
| the handle declared by the receiver instead of the type on its chain that declares the method | `CDerNoOverride`'s and `CGDer`'s `DeclaringType` checks |
| the slot's *owner* answered instead of what the receiver's slot holds | `CDerOverride` |
| a default body declared by the interface asked about instead of the instantiation it was found on | `CCoD` at `ICoD<object>` (`DeclaringType` is `ICoD<string>`) |
| per-level variance passes merged (an exact entry on a base wins over a variance-compatible one on the receiver) | `VDer` at `ICo<object>` |
| a static method resolved by default bodies before the chain's MethodImpls | `CStatVirtOverride`'s and `CStatDerRelist`'s rows |
| the chain's static MethodImpls searched from the base down | `CStatDerRelist` (`S` is `CStatDerRelist::S`) |
| an interface's own reabstraction (asked about directly) answered as the base method's implementation | `IReabsD` and `IStaticReabs` rows (null) |
| an array receiver answered from its element type | `int[]` against `IList` |
| a generic method answered as an instantiation over invented arguments | the example test of decision 2 (the managed caller hides it from every guest by taking `GetGenericMethodDefinition()`: equivalent through public API) |
| open receivers concretised (or answered) instead of refused | the open-receiver example test |
| decision 1 B's parameter ignored in the call path (`Reflection` passed by `callvirt`) | `TestAmbiguousDefaultInterfaceDispatch` and `TestCrossAssemblyAmbiguousDefault`, which expect the call to throw |

## What FakeItEasy needs next

Nothing, as far as these scenarios reach. With the throwaway in place (on `pr-2289-num-virtuals`, so with #2269,
#2270, #2289 and #2291 all implemented), five FakeItEasy 8.0.0 scenarios were published self-contained (AnyCPU, with
`IsDynamicCodeSupported` set true in the runtimeconfig, as #2092's measurements were) and run under PawPrint on .NET
10's CoreLib:

| scenario | real .NET 10 and 11 | PawPrint |
|---|---|---|
| #2092's snippet: `A.Fake<IThing>()`, `A.CallTo(() => fake.Value).Returns(42)` | exit 0, "fake.Value = 42" | exit 0, the same output |
| `MustHaveHappenedOnceExactly` passing | exit 0 | exit 0, the same output |
| `MustHaveHappened` failing, printing the `ExpectationException` message | exit 0 | exit 0, the same message |
| argument constraints with `ReturnsLazily` and `Throws` | exit 0 | exit 0, the same output |
| a fake of an abstract class with constructor arguments | exit 0 | exit 0, the same output |

Each took 16 to 33 s under PawPrint, with other PawPrint runs in parallel. The snippet stopped at this QCall before
the throwaway (#2092's table, stop 4), so the QCall is reached and its answer is used. So once this issue is
implemented, #2092's snippet should run, and the next thing to find is a FakeItEasy scenario these five do not cover.
The issue's snippet is a candidate for a guest of its own; it needs the FakeItEasy and Castle.Core packages in the test
project, which is a decision outside this plan.

Without `IsDynamicCodeSupported`, every scenario fails at `A.Fake` with `FakeCreationException` wrapping
`PlatformNotSupportedException` ("Dynamic code generation is not supported on this platform."), which is PawPrint's
default policy (#853), not a gap.

## Probe files

None were kept. They were:

- A console program with one mode per measurement: `map N` (the `N`th receiver and interface pair, printing its map),
  `maps` (all of them), `identity` (whether a struct's target `==` `GetMethod`, the declaring type of an inherited
  target, whether a target is a generic method definition), `emit` (the Reflection.Emit types: proxy-shaped MethodImpl,
  implicit implementation, abstract classes without implementations, instance diamond and reabstraction; the static
  shapes fail to build, because Reflection.Emit refuses a static virtual), and `corelib` (every exported non-generic
  CoreLib type with each interface it implements, one line per row). Built for .NET 10 and run as is and rolled forward
  to .NET 11; published self-contained against .NET 10 and, as a copy, against .NET 11 for PawPrint's runs.
- A two-version library: a library whose second version adds the bodies and reabstractions C# would refuse the classes
  over, a class library compiled against the first, and a driver with one mode per case. Run against the second
  version on real .NET 10 and 11 and under PawPrint.
- The FakeItEasy scenarios, one program with a mode per scenario.
- The parked guest, copied unchanged into a scratch project.

A script compared each PawPrint run with the real run line by line, after dropping PawPrint's log lines.
