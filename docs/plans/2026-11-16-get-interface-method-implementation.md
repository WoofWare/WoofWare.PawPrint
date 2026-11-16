# `RuntimeTypeHandle_GetInterfaceMethodImplementation` (issue #2290)

Status: decided; pull request 1 (the TypeSystem) built, see "As built (PR 1)" at the end; pull request 2 (the
handler and the guests) to do. Written 2026-11-16 on `pr-2289-num-virtuals` at `52f675317` (#2301, which implements
#2289 and was on the merge queue, not yet merged), on top of `main` at `0652e513a` (#2298, #2270).

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
8167 / 8066). The functions this QCall reaches have the same logic on both runtimes except for the handling of an
async variant method, which `GetInterfaceMap` never passes (read, by diffing each function).

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

## Decisions

Decided 2026-11-16 by the delegated decider, after checking the plan's claims against the code at `31247de24` and
against both pinned runtime sources, and after extending the plan's two-version probe on real .NET 10 and 11.
"Measured here" below means that extended probe; the two runtimes printed the same for every case.

### What the checks found

Confirmed by reading:

- The QCall's text, its one managed caller and the managed loop are as the plan quotes them, on both runtimes.
  `FindDispatchImpl`, `TryGetCandidateImplementation`, `FindDefaultInterfaceImplementation` and
  `ResolveVirtualStaticMethod` have the same logic on .NET 10 and 11; the text differs only in contract macros and in
  one async-variant argument, so "the same text" in the plan should read "the same logic".
- The plan's reading of each step of the instance path and the static path is right, including the three places where
  the QCall's answer differs from a call's.
- `DefaultInterfaceImplementation.search`, `candidateOn` and `implementationOnType` read only identity from the method
  they are given: its declaring interface, its MethodDef row, whether it is static and whether it is abstract. So
  decision 3 (a) costs nothing in the lower pieces.
- `TestStaticVirtualOverrideOrder.fs` already uses the host's `GetInterfaceMap` as its oracle and records, as
  measured on .NET 10 and 11, that `GetInterfaceMap` resolves afresh at every call. So the plan's "read, not measured"
  claim about an order-dependent static body is in fact measured: reflection answers the body a read now finds.

Wrong or incomplete in the plan:

1. **"Four call sites" and "existing dispatch tests green unedited" cannot both hold under the plan's B.**
   `ConcreteVirtualDispatch.tryResolveVirtualImplementation` and `StaticVirtualDispatch.resolve` are also called
   directly by `TestSzArrayInterfaceDispatch`, `TestDispatchReadsNoBody`, `TestAmbiguousDefaultInterfaceDispatch`,
   `TestAsyncVariantDispatch`, `TestStaticVirtualOverrideOrder` and `TestDispatchBindingFailure`. A new parameter on
   either function edits every one of those tests in the commit that is meant to change no behaviour.
2. **`tryResolveVirtualImplementation` is not an interface-only function.** It also resolves a class's virtual
   methods, by name and concretised signature. "The interface instantiation and the method definition in place of a
   concretised method" cannot be its signature; it can only be the signature of the interface half.
3. **`VirtualImplementation.Reabstracted` does not say which interface instantiation the reabstraction was found
   on.** It carries the definition alone. The throwaway minted a static reabstraction's handle with no type
   arguments, which is right only because the probed interface was not generic. Measured here:
   `CReabsSG : IRrG<string>`, where `IRrG<T> : IArG<T>` reabstracts `static virtual int IArG<T>.S()`, maps
   `IArG<string>.S` to `IRrG<string>::IArG<T>.S`, reflected as `IRrG<string>`, abstract. The same holds for a
   generic class `CReabsSGen<int> : IRrG<int>`.
4. **A receiver that implements `IDynamicInterfaceCastable` does reach the "nothing found" branch.** The plan says
   dispatch's `Unmodelled` for such a receiver arises only for an interface the receiver does not implement.
   Measured here: a class implementing `IDynamicInterfaceCastable` and an interface whose instance method ends in a
   reabstraction, or in a diamond with no most specific body, maps that method to null. The call path answers
   `Unmodelled` there today, so a projection of the call path would refuse where real .NET answers null.
5. **An open receiver reaches this QCall through public API today.** PawPrint's `GetInterfaces` refuses an open
   definition only where an interface row names a type parameter. `class CGBase<T> : IOne` has a closed row, so
   `typeof(CGBase<>).GetInterfaceMap(typeof(IOne))` is expected to arrive here with an open receiver and a closed
   interface (read, not run under PawPrint). Measured here on real .NET: with a default body the row is
   `IAi.M -> IAi::M [IAi]`; with a class method it is `IAi.M -> CGBaseM<T>::M [CGBaseM<T>]`.
6. **The variant pass's first survivor can be a reabstraction.** Measured here: `CDiamondW : IBw, ICw`, where `IBw`
   reabstracts `IAw<string>.M` and `ICw` overrides it with a body, maps `IAw<string>.M` and `IAw<object>.M` to
   **null**; with the interfaces listed in the other order (`ICw, IBw`) both map to `ICw`'s body. So the reflection
   rule is "the first survivor, and null if it is abstract", not "the first survivor with a body".
7. **The property's `Ambiguous` clause asks for something the host cannot supply.** When the host's
   `GetInterfaceMap` throws there are no rows, so "agree on the others" has no oracle.

### 1. How the QCall relates to the interpreter's dispatch

**B, but with the caller's value kept inside the TypeSystem and out of every public signature.** One walk, as in
CoreCLR; two public entry points, each with a result type that means one thing.

The options were weighed like this:

- **A** returns a wrong answer, silently, for a static conflict in the variant pass, and by finding 4 above refuses
  three more shapes that real .NET answers. A wrong answer with no refusal is not acceptable here.
- **C** replaces `VirtualImplementation` for every reader, the escape analysis included, to give answers that a
  smaller change also gives.
- **D** keeps two compositions of one search order. Nothing but a test would keep them in step.
- **The plan's B** shares the walk, which is right. But it puts the caller's value on two public functions, so every
  interpreter caller must pass it and can pass the wrong one; it edits six test files in the commit meant to change
  nothing; and it makes the meaning of `VirtualImplementation` depend on an argument. Under `Reflection`,
  `Reabstracted` would mean "this abstract method is the answer" for a static method and "null" for an instance one,
  while its docstring says "the call throws `EntryPointNotFoundException`". AGENTS.md asks that a classifier's
  contract stay truthful for its callers.

What to build instead:

1. **The two existing public functions keep their signatures, their docstrings and their behaviour.**
   `ConcreteVirtualDispatch.tryResolveVirtualImplementation` and `StaticVirtualDispatch.resolve` go on answering
   "which method does a call run". No caller in the interpreter, the escape analysis or the tests changes.
2. **The interface half of each becomes one internal function** that takes the receiver, the interface's
   `ConcreteTypeHandle` and the interface method's definition (decision 3), and returns an internal result that
   states what the search found and nothing about what a caller does with it. A recommended shape, which the
   implementer may adjust so long as each case means one thing:
   - a method of the class chain (the occupant of the receiver's slot, or a static MethodImpl's body), with the
     handle of the chain type that declares it. It may be abstract (`CAbs::M`);
   - the most specific default body, with the handle of the interface instantiation it was found on. It may be
     abstract, which is a reabstraction, and for a static method it may be order-dependent;
   - a conflict, with its candidates and their interface handles;
   - for a static method, the method's own body, reached when no interface in the receiver's map supplies it;
   - nothing.
3. **One internal value says which of CoreCLR's callers the search answers for**, a two-case DU private to the
   TypeSystem (for instance `InterfaceMethodCaller.Call` and `InterfaceMethodCaller.InterfaceMap`). It decides
   exactly what CoreCLR's own parameter decides, and nothing else:
   - instance search (`throwOnConflict`): at a conflict in the exact pass, `Call` stops and reports the conflict;
     `InterfaceMap` goes on to the variant pass if the interface is variant, and otherwise reports nothing found;
   - static search (whether `uniqueResolution` is passed): at a conflict in the variant pass, `Call` reports the
     first survivor; `InterfaceMap` reports the conflict.
   Only three places name a case: the two existing public functions (`Call`) and the new function of decision 5
   (`InterfaceMap`).
4. **Everything else that differs is a projection of the internal result**, written once per caller:
   - the call's projection is today's behaviour: `Found`, `Reabstracted`, `Ambiguous`, `Unmodelled` for an instance
     conflict at a variant interface's exact instantiation, the `IDynamicInterfaceCastable` and generic-array
     `Unmodelled` answers in the "nothing found" branch, and the refusal of an order-dependent static body;
   - the reflection projection is decision 5's.
   The `IDynamicInterfaceCastable` check, the SZ-array diversion to `SZArrayHelper`, and the refusal of an
   order-dependent body belong to the call's side only. CoreCLR's `FindDispatchImpl` never consults
   `IDynamicInterfaceCastable` (its stub resolver does), and this QCall caches nothing.
5. An async variant is the call path's business: `AsyncVariantDispatch` stays in the two existing public functions.
   The new function fails loudly if it is handed one, since `GetInterfaceMap` never passes one.

Blast radius: the internals of `callvirt`, `ldvirtftn`, delegate binding, the constrained call and the escape
analysis all run through the restructured code, so the refactor must be proved (decision 2). No public API is removed
or changed; one type and one function are added. Reversibility: high, since the additions can be deleted without
touching a caller. Information kept: all of it, in the internal result.

### 2. May the public signatures change? How is the refactor proved?

**They do not change**, by decision 1. The published `WoofWare.PawPrint.TypeSystem` package gains one public type
and one public function, and loses nothing. Say that in the pull request's body.

The refactor is proved behaviour-preserving in four ways, all required:

1. **A characterisation property of the call path, written and green before the refactor, and never edited across
   it.** In its own first commit, add to `TestInterfaceMethodImplementation.fs` a property that drives only the two
   existing public functions and compares them with the host's `GetInterfaceMap`, over the three corpora of the
   plan's test design. For each row:
   - the host's target is not abstract: the call path answers `Found` with that method (same module, same token,
     declared by the same type), or, for the fixed shapes listed below, the documented call-only answer;
   - the host's target is an abstract method of a class: `Found` with that method;
   - the host's target is an abstract method of an interface (a static reabstraction): `Reabstracted` with it;
   - the host's target is null: anything but `Found`;
   - the host throws `AmbiguousImplementationException`: `Ambiguous`, or `Found` with the first survivor for a
     conflict in the variant pass.
   The call-only answers are `Unmodelled` for the instance variant diamond at its exact instantiation, `Unmodelled`
   for an `IDynamicInterfaceCastable` receiver, and the refusal of an order-dependent body. Assert that the CoreLib
   corpus and the generated corpus produce **none** of them, and that each fixed two-version shape produces exactly
   the one expected. A generic interface method needs method arguments on the call path: instantiate an
   unconstrained one over `System.Object`, skip a constrained one, and count both with a floor.
   If this property is red on the unchanged code, stop and report: that is a dispatch bug that exists today, and it
   is fixed in a pull request of its own before this work goes on.
2. **The existing suites, unedited.** The default suite and the four Guest categories, each as its own invocation,
   green before and after. No file under `WoofWare.PawPrint.Test` that exists today is edited by the refactor
   commit, except to move the generator of decision 8 in a commit that only moves it.
3. **A before-and-after dump, byte-identical.** With a throwaway test that is not committed, write one line for
   every (receiver, interface, method) of the three corpora, at a fixed FsCheck seed: the rendered
   `VirtualImplementation` or the first line of the failure, followed by the number of concrete types registered and
   the number of assemblies loaded once the query returns. Run it on the commit before the refactor and on each
   commit after. The files must be identical. Record the line count and "identical" in the refactor commit's
   message. The two counts are there because the search loads types as it goes, and a reordered or widened search
   would otherwise pass unseen.
4. **Mutants**, listed at the end, each observed red.

The refactor is its own commit and adds no behaviour: it extracts the interface half, changes its input to the
interface handle and the definition, and introduces the internal result with the call's projection. The caller's
value and the reflection function arrive in the following commit, where the `Call` arm at each of the two branch
points must be the code that was there before.

### 3. What the shared walk is given for the interface method

**(a): the interface's `ConcreteTypeHandle` and the method's definition**, for the internal interface functions
only. The call path's method arguments stay a separate argument, as `methodGenerics` already is on
`tryResolveVirtualImplementation`, and are attached by the call's projection. The reflection function passes none
and returns none.

(b) is refused: it puts invented arguments into a type that says "as the call instantiates it", and the plan measured
that constraints reject them for 161 CoreLib rows. (c) is refused: it would stop `GetInterfaceMap` on every numeric
type.

### 4. Open receivers

**(a): refuse, and file #2303.** The handler refuses, before it concretises
anything, whenever the receiver or the interface is not `RuntimeTypeHandleTarget.Closed`. The `TODO:` message names
the receiver and the interface, says that real .NET answers with methods of the open receiver or of the interface,
and names the issue.

By finding 5 this refusal is reachable through public API today, so its example tests are: `CGBase<>` with the
closed `IOne` it lists (a guest or a machine-level test, which also settles whether the call arrives here as read);
and, at the handler, `CGen<>` with its own `IG<T>`. (b) would be a second, definition-level dispatch, and nothing on
FakeItEasy's path needs it.

### 5. Where the projection lives

**(a): in `WoofWare.PawPrint.TypeSystem`**, as the one new public function and its result type, in a new file
compiled after `StaticVirtualDispatch.fs`. For instance:

```fsharp
[<RequireQualifiedAccess>]
type InterfaceMapTarget =
    /// The method `GetInterfaceMap` reports, and the type that declares it at the instantiation it was found on.
    /// It may be abstract.
    | Method of definition : MethodInfo<GenericParamFromMetadata, GenericParamFromMetadata, TypeDefn> * declaringType : ConcreteTypeHandle
    /// The row's target is null.
    | NoTarget
    /// `GetInterfaceMap` throws `AmbiguousImplementationException`. Only a static method gives this.
    | Ambiguous of candidates : MethodInfo<GenericParamFromMetadata, GenericParamFromMetadata, TypeDefn> list
```

The function takes the receiver's handle, the interface's handle and the interface method's definition, and
dispatches on whether the definition is static. Its projection of the internal result:

| the search found | instance method | static method |
|---|---|---|
| a method of the class chain | `Method`, abstract or not | `Method`; an order-dependent body is the one a read now finds |
| a default body that is not abstract | `Method`, declared by the interface instantiation it was found on | the same; an order-dependent body is the one a read now finds |
| a default body that is abstract | `NoTarget` | `Method`, declared by the reabstracting interface's instantiation |
| a conflict | `NoTarget` (it can only be the exact pass of an interface with no variance) | `Ambiguous` |
| the method's own body (static only) | not applicable | `Method`, declared by the interface |
| nothing | `NoTarget` | `NoTarget` |

It fails loudly, naming the condition, when the method is not declared by the interface's definition, when it has no
MethodDef row, when it is an async variant, and when the receiver is an SZ array and the interface is generic (the
managed caller throws `Argument_ArrayGetInterfaceMap` before the QCall for that last one).

`ResolveVirtualStaticMethod` skips its whole search when the receiver's interface map holds the canonical form of the
interface. Read: that needs a receiver that is itself a canonical instantiation, which no reflection handle names, so
it is not modelled, and a comment at the static projection says so.

### 6. The handle the QCall answers

**(a).** The handler mints the handle from `InterfaceMapTarget.Method`'s definition and declaring type with
`MethodHandleRegistry.getOrAllocateInternalHandle`, as `GetMethodAt` mints. `NoTarget` is
`MethodHandleRegistry.zeroInternalHandle`. `Ambiguous` raises. The declaring type must come from the result and never
be rebuilt from a definition with no type arguments (finding 3).

PawPrint has no canonical forms and no unboxing stubs; that is already a recorded divergence ("A method handle is
per-instantiation, where CoreCLR shares one per canonical form" in `docs/divergences.md`). Add this QCall to that
entry's list of places if the entry keeps one.

### 7. The message of `AmbiguousImplementationException`

**(a): the parameterless constructor's message**, raised with `NativeHandlerResult.raiseException`, as the call path
raises it. No new issue. `docs/divergences.md` already records, under "`Activator.CreateInstance` rejection
messages", that every exception PawPrint raises itself carries its type's default message. CoreCLR's text here would
also need `System.__Canon` rendered: measured, the message for the variant static diamond names
``'IAsv`1[System.__Canon].S()'``. Guests compare the exception's type and `HResult` (0x8013106a), never its message.

### 8. The third corpus and a collectible `AssemblyLoadContext`

**Yes.** `TestAmbiguousDefaultInterfaceDispatch.fs` already loads generated images into a collectible context on the
host, so this is an established pattern. Load the second version of the library into the context first, then the
classes compiled against the first version, so that the classes' reference binds to the image already loaded there.
Use one context per case and unload it. Build Roslyn's metadata references once and share them; references rebuilt
per case have exhausted native memory in this suite before. If the host side proves fragile, the cross-assembly
guests alone carry these rows, and the pull request says that the TypeSystem-level oracle for them is missing.

Corrections to the plan's property:

- Where the host throws `AmbiguousImplementationException`, assert that PawPrint answers `Ambiguous` for at least
  one static method of that interface, and where the host does not throw, that PawPrint answers `Ambiguous` for
  none. Give each fixed conflict shape an interface with one method, so that the row in question is unambiguous.
- Add these fixed shapes to the third corpus, all measured here on real .NET 10 and 11:
  - generic static reabstraction: `IArG<string>.S -> IRrG<string>::IArG<T>.S`, abstract, reflected as
    `IRrG<string>`; asked at `IRrG<string>`: null;
  - variant instance diamond with a reabstraction listed first: null at `IAw<string>` and at `IAw<object>`; with
    the reabstraction listed second: `ICw`'s body at both;
  - an `IDynamicInterfaceCastable` receiver with an instance reabstraction as most specific: null; with an instance
    diamond: null.
- Add to the C#-compilable rows: `CStatG : IStatG<string>`, `IStatG<T> { static virtual int SV() => 1; }`, which
  maps to `IStatG<string>::SV`, reflected as `IStatG<string>`.
- Extend `TestStaticVirtualOverrideOrder`'s property with the new function: in the window where the call path
  refuses, the reflection function answers the host's `GetInterfaceMap` body. This is an added assertion; the
  existing ones stay as they are.
- Lifting #2289's generator into a shared module is a move between files. Make it a commit that only moves, so that
  `scripts/check-move-is-rename-only.sh` and the docstring attachment check pass on it.

### 9. Correcting the issue

**Yes.** Post the comment prepared for #2290, which corrects the `int[]` row, answers the body's open
question about reabstractions and diamonds, and adds the cases measured here. Post the FakeItEasy result on #2092.

### Staging

Two pull requests, each landing on its own.

**Pull request 1: the TypeSystem.** No handler, no guest.

1. The characterisation property of decision 2, green on the unchanged code. Include the generator's move as its own
   commit before it.
2. The failing tests of the reflection function: the host property over the three corpora, the example tests of
   decisions 3 and 6, and the added assertion in `TestStaticVirtualOverrideOrder`. The failing output (a compile
   failure) goes in the commit message.
3. The refactor of decision 1, items 1, 2 and 4: no caller value yet, no new behaviour. The dump of decision 2 is
   run across this commit.
4. The caller value and the reflection function. The dump is run again across this commit; the tests of commit 2 go
   green.

Because the dispatch internals change, the four Guest categories are part of this pull request's testing. If review
finds it too large, commits 1 and 3 stand alone as a first pull request, since they leave nothing unused.

**Pull request 2: the QCall.**

1. The guests and handler-level tests, failing, with each guest's stop in the commit message: the plan's
   `TypeGetInterfaceMapTargets.cs`, `TypeGetInterfaceMapTask.cs`, `TypeBuilderGetInterfaceMap.cs` and cross-assembly
   guests, plus the open-receiver refusal tests of decision 4. Each guest is run as a control on real .NET 10 and 11
   with every expectation perturbed, as the plan says.
2. The handler in `Native/NativeRuntimeTypeQCall.fs`, registered in `Native/NativeQCall.fs`. It checks the argument
   count, decodes the two type handles and the method handle, refuses a receiver or interface that is not closed,
   fails loudly if the method handle's declaring type is not the interface it was passed, calls the reflection
   function, and mints, pushes zero or raises per decision 6.
3. Un-park `sourcesPure/TypeGetInterfaceMap.cs`.

The cross-assembly guests also make the call where the plan measured that a call and the map differ, so that one
guest pins both: the static variant diamond returns 2 from a constrained call through `IAsv<object>` while its map
throws, and the static reabstraction throws `EntryPointNotFoundException` from the call while its map names the
abstract method. They do not make the instance call through the variant diamond at its exact instantiation, which
PawPrint refuses.

File #2303 and #2304 before pull request 2 opens, so the
`TODO:` text can name the first. The cross-assembly guest for a class that leaves a static abstract member
unimplemented is not written: real .NET cannot load that type, and PawPrint's answer for it is
#2304's subject.

### Mutants to add to the plan's table

Each must be observed red; record the test that kills it.

| mutant | expected to be killed by |
|---|---|
| a static reabstraction's declaring type built from the definition with no type arguments | the generic static reabstraction shape (`IRrG<string>`) |
| the reflection search consults `IDynamicInterfaceCastable` in its "nothing found" branch | the two `IDynamicInterfaceCastable` shapes (null, not a refusal) |
| the variant pass's survivor skipped when abstract, in favour of the next one with a body | the variant diamond with the reabstraction listed first (null) |
| the variant pass's first survivor answered although abstract | the same shape (null, not `IBw`'s reabstraction) |
| an instance conflict on an interface with no variance answered as its first candidate | the instance diamond (null) |
| an abstract method of a class answered as `NoTarget` | `CAbs` |
| an order-dependent static body refused by the reflection function | the added assertion in `TestStaticVirtualOverrideOrder` |
| an order-dependent static body answered by the call path | `TestStaticVirtualOverrideOrder`'s existing refusal assertion |
| `Call` and `InterfaceMap` swapped in the instance public function | `TestAmbiguousDefaultInterfaceDispatch`, `TestCrossAssemblyAmbiguousDefault`, and the characterisation property's fixed shapes |
| `Call` and `InterfaceMap` swapped in the static public function | the characterisation property's static variant diamond (`Found` first), and the cross-assembly guest's call returning 2 |
| a generic static default body declared by the interface's definition instead of its instantiation | `CStatG` (`IStatG<string>`) |
| the handler accepts an open definition with a closed interface | the `CGBase<>` refusal test |

One mutant of the plan's is equivalent and need not be killed: making the static reflection search continue from an
exact-pass conflict into the variant pass still ends in a conflict, because the variant pass considers every
candidate the exact pass did.

### Other notes for the implementer

- The plan's sentence "the same text" for the two runtimes' `methodtable.cpp` functions should be "the same logic"
  when the plan is revised as built.
- The static search's order (chain MethodImpls exactly, then by variance, per level; then default bodies exactly,
  then by variance; then the method's own body) is unchanged for both callers. Only the two branch points of
  decision 1, item 3 read the caller's value. A reviewer should be able to find both by searching for the DU's name.
- Run `scripts/check-docstring-attachment-branch.sh` after the extraction: it moves definitions that carry
  docstrings.
- If anything in the characterisation property or the dump shows the call path and the host disagreeing on a
  C#-compilable shape, stop and ask rather than adjusting the property.

### As built (PR 1)

Built on `fix-2290-dispatch-walk`, on top of the two plan commits, as the "Staging" section's pull request 1: the
TypeSystem half, with no handler and no guest.

#### Commits

1. *Make the generated interface corpus of TestNumVirtualsAndStaticVirtuals public*, and *Move the generated
   interface corpus into InterfaceCorpusGenerator*: decision 8's move, in two commits so that the second changes
   nothing but which file the text is in. `scripts/check-move-is-rename-only.sh HEAD~ --split ...` (the command is
   in the second commit's message) reports that the fragments reproduce the old file. The first commit only drops
   `private` from the moved definitions, which a move to another module needs and the move check cannot accept.
   The new file is `InterfaceCorpusGenerator.fs`, because `.gitignore` excludes `Generated*.fs`.
2. *Characterise the dispatch walk against GetInterfaceMap*: decision 2's property, green on the unchanged code.
3. *Failing tests for InterfaceMethodImplementation.resolve*: the reflection function's property over the three
   corpora, the examples of decisions 3 and 6, and the added assertion in `TestStaticVirtualOverrideOrder`. The
   commit message quotes the compile errors.
4. *Extract the interface half of both dispatch walks*: decision 1, items 1, 2 and 4, with no caller value.
5. *InterfaceMethodImplementation.resolve: GetInterfaceMap's target*: `InterfaceMethodCaller` and the public
   function.

The published `WoofWare.PawPrint.TypeSystem` package gains one public type, `InterfaceMapTarget`, and one public
function, `InterfaceMethodImplementation.resolve`, in `InterfaceMethodImplementation.fs` after
`StaticVirtualDispatch.fs`. Nothing public is removed or changed. The internal additions are
`InterfaceMethodSearch` and `InterfaceMethodCaller` (both in `ConcreteVirtualDispatch.fs`),
`ConcreteVirtualDispatch.searchInstanceInterfaceMethod` and `StaticVirtualDispatch.search`.
`DefaultInterfaceImplementation.search` takes the interface's handle in place of interning the method's declaring
type, and reads only the method's identity, so it accepts the definition or a concretised method.

#### The characterisation property

`TestInterfaceMethodImplementation.fs`. Measured on a .NET 10 host:

- CoreLib (every exported type that is not an interface, and a spread of instantiations and arrays): 6,965 rows
  compared, 332 answered by a default body, 2,170 static, 172 constrained generic methods skipped, 423 open
  receivers skipped. Floors at about half. No exported CoreLib type implements an interface with an unconstrained
  generic method, so the generated corpus carries that count.
- Generated corpora (`InterfaceCorpusGenerator.genImplementingCorpus`, 100 cases from `CoverageSample`'s fixed
  seed, then 100 fresh): the generator now gives variant interfaces (`out` and `in`), an `object` argument, and
  classes and structs that implement interface members explicitly, implicitly, by a virtual or abstract method, by
  an override further down the chain, by `new` re-implementation of a re-listed interface, by re-listing alone, and
  by leaving a default body. A third of the corpora begin with an interface pair (`I1 : I0` giving one of `I0`'s
  methods a default body and reabstracting the other), which makes a default body on a more specific interface and
  an interface's own reabstraction common. Pairs include each variance-compatible instantiation a receiver does not
  implement. The fixed sample reached: a class's own method 860 times, an inherited one 125, a default body on the
  interface asked about 127, on a more specific interface 23, a null row 88, a static row by a MethodImpl 430, by a
  default body 18, a row through variance 89, a generic method definition 179; floors at about half.
  `TestNumVirtualsAndStaticVirtuals` generates the same corpora as before: `genCorpus` draws exactly what it drew.
- Shapes C# refuses: the plan's two-version library, extended with the decider's shapes, loaded beside the classes
  in one collectible `AssemblyLoadContext` per shape, the second version first. Loading the second version first is
  enough for the classes' reference to bind to it; no `Load` override was needed. Each of 19 shapes asserts the host
  answer the plan measured and the one call answer the call path gives.

#### Where the decisions were silent, or did not match the code

- **The IDynamicInterfaceCastable shapes.** Finding 4 says the call path answers `Unmodelled` for an
  `IDynamicInterfaceCastable` receiver whose interface method ends in a reabstraction or a diamond. It does not: the
  call path's `IDynamicInterfaceCastable` refusal is in its "nothing found" branch, and those shapes find something.
  Measured, the call path answers `Reabstracted` (`IRi`'s reabstraction) and `Ambiguous` (`IBd`, `ICd`), both
  consistent with the host's null target. The characterisation records those answers. The reflection function never
  consults `IDynamicInterfaceCastable`, as decision 1 item 4 says, and the instance-diamond shape kills the mutant
  that makes it.
- **An instance conflict for the interface map.** Decision 1 item 3 says the instance search for `InterfaceMap`
  answers nothing found at a conflict on an interface with no variance; decision 5's table says the projection makes
  a conflict `NoTarget`. Item 3 was built, so the instance search never reports a conflict for `InterfaceMap`, and the
  projection's conflict arm fails loudly as a PawPrint bug rather than answering a value no search produces.
- **A class's virtual method.** `tryResolveVirtualImplementation` also resolves a class's virtual method, and when the
  class walk finds nothing it has always run the default-body search with the class's method, which never finds a
  body but does register the interfaces it looks at. The refactor keeps that call (through the same default-body
  function, with the class's concretised method), so that no state changes.
- **The commit order makes the refactor commit untestable on its own.** Commit 3's tests name the function commit 5
  adds, so the test project does not build at commit 4. The characterisation property, the default suite and the
  dump were run on commit 4's tree with commit 3's test edits taken back out (commit 2's test files over commit 4's
  TypeSystem). Commit 3 changes only tests and commit 4 only the TypeSystem, so that tree is the refactor exactly.
- **Open receivers.** The reflection function takes `ConcreteTypeHandle`s, which cannot name an open receiver, so the
  refusal of decision 4 has nothing to test in this pull request: the CoreLib corpus counts the open receivers it
  skips, and the refusal tests are the handler's, in pull request 2.
- **A synthesised static method.** `StaticVirtualDispatch.resolve` now reads the method's definition before its
  search rather than only at its last step. A synthesised static interface method fails there as it failed before, a
  little earlier; none exists.

#### The before-and-after dump

A throwaway test, not committed, wrote one line for every question of the three corpora (the CoreLib corpus, 100
generated corpora from a fixed seed, and the 19 shapes): the call path's answer, rendered with its type and method
arguments' handles, then the number of concrete types registered and of assemblies loaded once the query returned.
8,738 lines. Run twice on commit 2's tree: identical. Run on commit 4's tree (as above) and on commit 5: **identical**
to commit 2's, byte for byte.

#### Mutants

Each mutant was applied to the committed code by an exact text replacement, the test project built, and the tests
named run; every file was restored afterwards and checked against `HEAD`. "Shapes" means `TestInterfaceMethodImplementation`'s
shapes C# refuses, "generated" and "CoreLib" its other corpora; "call path" is the characterisation property, "map" the
reflection function's property.

The plan's table:

| mutant | killed by |
|---|---|
| a null answer encoded as a handle | the handler's; pull request 2 |
| an instance reabstraction answered as the reabstraction (also the decisions' "variant pass's first survivor answered although abstract": the same projection arm) | map, shapes (instance reabstraction, `IDynamicInterfaceCastable` reabstraction, `IAw` with the reabstraction first) |
| a static reabstraction answered as null | map, shapes (static and generic static reabstraction) |
| an instance conflict answered as `Ambiguous` (the interface map's search reports the conflict at an interface with no variance) | map, shapes (`IDynamicInterfaceCastable` diamond) |
| a static `Ambiguous` answered as null | map, shapes (static diamonds) |
| a static `Ambiguous` answered as its first candidate | map, shapes |
| the instance conflict through a variant interface answered as null | map, shapes (`IAv<string>`, `IAw<string>` with the reabstraction second) |
| the static conflict in the variant pass answered as the first survivor | map, shapes (`IAsv<object>`) |
| the target declared by the receiver instead of the type on its chain | the example "A target is declared by the instantiation the walk found it on" (`CGDer`), map over generated and CoreLib |
| the slot's owner instead of what the receiver's slot holds (realised as the occupant of that slot in the base-most class that has it) | call path and map, over generated and CoreLib |
| a default body declared by the interface asked about | the example (`CCoD` at `ICoD<object>`), map over generated and shapes |
| per-level variance passes merged (`tryFindImplementationSlot` searches every level exactly before any by variance) | only `TestInterfaceDispatchMap`'s generated hierarchies. The new generator drops a class whose base implements another instantiation of one of its interfaces, so it has no `VDer` shape |
| a static method resolved by default bodies before the chain's MethodImpls | call path and map over generated and CoreLib, `TestStaticVirtualOverrideOrder`, `TestStaticVirtualOverrideOrderRefusals`, `TestAsyncVariantDispatch` |
| the chain's static MethodImpls searched from the base down (each level's exact and variant searches kept) | call path and map over generated: a base's explicit implementation won over the derived class's |
| an interface's own reabstraction answered as a body (realised in `DefaultInterfaceImplementation.candidateOn`, by counting a method's own abstract declaration as a candidate) | call path and map over shapes (`IRr`, `IRrG<string>`), map over generated |
| an array receiver answered from its element type | map over CoreLib (`int[]`, `string[]`, `int[,]`) |
| a generic method answered as an instantiation over invented arguments | not representable: `InterfaceMapTarget.Method` carries a definition and no method arguments |
| open receivers concretised instead of refused | not representable here: the function takes `ConcreteTypeHandle`s; the handler's refusal is pull request 2's |
| the caller value ignored in the call path | as "`Call` and `InterfaceMap` swapped in the instance public function", below |

The decisions' additions:

| mutant | killed by |
|---|---|
| a static reabstraction's declaring type built from the definition with no type arguments | map, shapes (`IRrG<string>`) |
| the reflection search consults `IDynamicInterfaceCastable` when it finds nothing | map, shapes (`IDynamicInterfaceCastable` diamond) |
| the variant pass's survivor skipped when abstract, for the next one with a body | map, shapes (`IAw` with the reabstraction first) |
| the variant pass's first survivor answered although abstract | the plan's "instance reabstraction answered as the reabstraction", above |
| an instance conflict on an interface with no variance answered as its first candidate | map, shapes (`IDynamicInterfaceCastable` diamond) |
| an abstract method of a class answered as `NoTarget` | map over generated and CoreLib |
| an order-dependent static body refused by the reflection function | `TestStaticVirtualOverrideOrder`'s added assertion |
| an order-dependent static body answered by the call path | `TestStaticVirtualOverrideOrder`'s existing refusal assertion, and two tests of `TestStaticVirtualOverrideOrderRefusals` |
| `Call` and `InterfaceMap` swapped in the instance public function | `TestAmbiguousDefaultInterfaceDispatch` (both of its conflict tests), `TestCrossAssemblyAmbiguousDefault` (seven tests), `TestCrossAssemblyReabstraction` ("a reabstraction as specific as a body is ambiguous"), call path over shapes |
| `Call` and `InterfaceMap` swapped in the static public function | call path over shapes (`IAsv<object>`: `Found` the first survivor). The cross-assembly guest whose call returns 2 is pull request 2's |
| a generic static default body declared by the interface's definition instead of its instantiation | map over shapes (`CStatG`), generated and CoreLib |
| the handler accepts an open definition with a closed interface | the handler's; pull request 2 |
| the static interface map's search goes on from an exact-pass conflict into the variant pass | **survives**: equivalent, as the decisions say; the variant pass meets every candidate the exact pass did |

#### Suites

Each its own invocation after a whole-solution build with every `*/obj/*/*/ref` and `refint` deleted, on the tip
after rebasing onto `main` at `753baef7d` (#2302, #2305); each Guest category under a lock slot:

| suite | total | passed | skipped |
|---|---|---|---|
| default | 5,869 | 5,830 | 39 |
| `TestCategory=Guest` | 234 | 234 | 0 |
| `TestCategory=GuestSolo` | 2 | 2 | 0 |
| `TestCategory=GuestPure` | 1,204 | 1,204 | 0 |
| `TestCategory=GuestImpure` | 302 | 302 | 0 |

Before the rebase, on `main` at `a8d94b827`, the same five were green with 5,862, 234, 2, 1,203 and 302 tests.

The default suite on commit 4's tree (commit 2's test files over the refactored TypeSystem, as above): 5,857 total,
5,818 passed, 39 skipped; commit 3's new tests are the five more on the same base.
`scripts/check-docstring-attachment-branch.sh`, `dotnet fantomas --check .` and `scripts/footprint.py generate` are
clean.
