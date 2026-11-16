# `RuntimeTypeHandle_GetNumVirtualsAndStaticVirtuals` (issue #2289)

Status: decided and built; see "As built" at the end. The study below was measured 2026-11-16 on
`pr-2270-verify-interface` at `2c334b95e` (#2298, which implements #2270 and was on the merge queue, not yet merged).

Scope is this one QCall, the second of the three that `Type.GetInterfaceMap` makes, as the comment on #2270 decided.
The first, `RuntimeTypeHandle_VerifyInterfaceIsImplemented` (#2270), is #2298. The third,
`RuntimeTypeHandle_GetInterfaceMethodImplementation` (#2290), is out of scope and needs its own plan. #2298 parks two
guests on this QCall; what happens to each is under "Guests".

The probes were throwaway C# console programs, not kept (see "Probe files" at the end). Real .NET means 10.0.14, and
11.0.0-rc.3 reached by rolling the same build forward (`DOTNET_ROLL_FORWARD=LatestMajor`,
`DOTNET_ROLL_FORWARD_TO_PRERELEASE=1`). PawPrint runs used the Release App built from `2c334b95e` plus the throwaway
handler described below, with each probe published self-contained for osx-arm64: against .NET 10 (PawPrint then runs
.NET 10's CoreLib), and for two of the probes also against .NET 11 (PawPrint then runs .NET 11's CoreLib). Each claim
is marked *measured* or *read*.

## What it is

### Who reaches it

`RuntimeType.GetInterfaceMap` is the only caller in CoreLib, on both runtimes (read: a search of both pinned sources
for the name finds the declaration, the wrapper and this one call). `RuntimeType.CoreCLR.cs:2739-2817` on .NET 10 and
`:2737-2815` on .NET 11, the same text (read, by diffing them):

```csharp
TypeHandle.VerifyInterfaceIsImplemented(ifaceRtTypeHandle);
...
if (IsSZArray && interfaceType.IsGenericType)
    throw new ArgumentException(SR.Argument_ArrayGetInterfaceMap);

int ifaceVirtualMethodCount = RuntimeTypeHandle.GetNumVirtualsAndStaticVirtuals(ifaceRtType);
im.InterfaceMethods = new MethodInfo[ifaceVirtualMethodCount];
im.TargetMethods = new MethodInfo[ifaceVirtualMethodCount];

int actualCount = 0;
for (int i = 0; i < ifaceVirtualMethodCount; i++)
{
    RuntimeMethodHandleInternal ifaceRtMethodHandle = RuntimeTypeHandle.GetMethodAt(ifaceRtType, i);
    // GetMethodAt may return null handle for methods that do not exist or are not supposed
    // to be seen in reflection. One example is async variant methods.
    if (ifaceRtMethodHandle.IsNullHandle())
        continue;
    ...  // GetMethodBase, then GetInterfaceMethodImplementation (#2290)
}
if (actualCount != ifaceVirtualMethodCount)
{
    Array.Resize(ref im.InterfaceMethods, actualCount);
    Array.Resize(ref im.TargetMethods, actualCount);
}
```

The wrapper is `internal static int GetNumVirtualsAndStaticVirtuals(RuntimeType type)` (`RuntimeHandles.cs:615-619` on
both, read), which `Debug.Assert`s a non-null type and calls the `LibraryImport`
`GetNumVirtualsAndStaticVirtuals(QCallTypeHandle)` with `new QCallTypeHandle(ref type)`. It checks nothing else.

Four things follow.

- Through `GetInterfaceMap` the argument is always the *interface*, never the receiver, and #2270's arm 4 has already
  refused anything that is not an interface MethodTable. So `GetInterfaceMap` asks this QCall only about closed
  interfaces, generic interface instantiations, a variant interface definition (the special marker, `ClassInC` /
  `IInC<>`), open constructed interfaces (`IEnumerable<T>` over `List<>`'s `T`, with the open `List<>` as receiver),
  and interfaces built by `TypeBuilder`.
- The count is only the length of the loop. Every slot below it is passed to `GetMethodAt`, so the count and
  `GetMethodAt` must agree: a count past the last slot `GetMethodAt` answers makes `GetInterfaceMap` throw
  `ArgumentException` where real .NET returns a map; a count short of it drops methods from the map. A null slot is
  skipped and the arrays shrunk, so null slots are invisible in the result.
- For an interface without methods the count is 0, the loop runs zero times, and `GetInterfaceMap` returns two empty
  arrays without reaching #2290. That is what lets `TypeGetInterfaceMapNoMethods.cs` pass with this QCall alone.
- The wrapper takes any `RuntimeType`, so reflection over CoreLib's internals reaches the QCall with a class, a struct,
  an array, a pointer, a byref, a function pointer or a type variable. Nothing public does.

### What the QCall counts

`RuntimeTypeHandle_GetNumVirtualsAndStaticVirtuals` is `vm/runtimehandles.cpp:365-397` on both runtimes, textually
identical (read, by diffing `:330-470` of both; the only difference in that range is in `RuntimeTypeHandle_GetFields`):

```cpp
TypeHandle typeHandle = pTypeHandle.AsTypeHandle();
_ASSERTE(!typeHandle.IsGenericVariable());
MethodTable *pMT = typeHandle.GetMethodTable();
if (pMT != NULL)
{
    numVirtuals = (INT32)pMT->GetNumVirtuals();
    if (pMT->HasVirtualStaticMethods())
    {
        for (MethodTable::MethodIterator it(pMT); it.IsValid(); it.Next())
        {
            MethodDesc *pMD = it.GetMethodDesc();
            if (pMD->IsVirtual() && pMD->IsStatic())
                numVirtuals++;
        }
    }
}
```

So the count is `GetNumVirtuals()`, plus every MethodDesc on the type's slots that is both virtual and static, when the
`HasVirtualStaticMethods` flag is set. Three readings decide what that means (all read, .NET 10 line numbers):

- **Which types have the flag.** `MethodTableBuilder` sets `fHasVirtualStaticMethods` on a type that declares a
  `static virtual` method (`methodtablebuilder.cpp:2936-2941`), *and* on any type that implements an interface which
  has the flag (`:323-326`, in the expansion of the interface set). So a class or struct implementing `IStat` has the
  flag.
- **Which types can declare a static virtual.** Only an interface: the builder throws `IDS_CLASSLOAD_STATICVIRTUAL`
  for a `static virtual` method on any other type (`:5124-5130`), and a static MethodImpl body must be either plain
  `static` or `static abstract virtual` (`:4878-4881`). C#'s implementation of a static abstract member is a plain
  `static` method with a MethodImpl. So for a class or struct with the flag, the loop finds nothing, and the count is
  `GetNumVirtuals()`.
- **What the loop walks.** `MethodIterator(pMT)` walks `MethodData`'s logical slots in slot order
  (`methodtable.cpp:7459-7464` and its `Init`): the vtable first, then the slots past it. An interface has no parent,
  so its vtable is its own instance virtuals; its static virtuals sit past the vtable, in the order
  `PlaceNonVirtualMethods` gives them, which for an interface is MethodDef order (a class constructor, if any, first,
  but it is not virtual). This is the order `RuntimeTypeHandle_GetMethodAt` (`runtimehandles.cpp:399-456`) also walks
  for slots at and past `GetNumVirtuals()`, but only when `pMT->IsInterface()`. For a non-interface the two agree only
  because the loop finds nothing.

`TypeHandle::GetMethodTable` (`typehandle.inl:20-28`, `typedesc.inl:17-33`, the same on both runtimes, read) answers a
MethodTable for every type that is one, and for a TypeDesc: `CoreLibBinder::GetElementType(ELEMENT_TYPE_U)`, that is
`UIntPtr`'s, for a pointer and a function pointer; `NULL` for a byref and a type variable. A `NULL` gives 0. The
`_ASSERTE(!IsGenericVariable())` is a debug check, so a release runtime answers 0 for a type variable.

### What it counts for each kind of type

| kind | what the count is | evidence |
|---|---|---|
| interface | its instance virtuals (abstract, default bodies, explicit overrides and reabstractions of a base interface's methods), then its static virtuals (`static abstract`, `static virtual` with a body, and static reabstractions), in slot order; nothing inherited from its base interfaces; sealed and private instance methods and non-virtual statics not counted | measured, rows below |
| generic interface: definition, instantiation, open construction | the definition's count, for each | measured: `IG<>`, `IG<int>`, `IG<string>`, `IG<T>` over `List<>`'s `T` all 2 |
| class or struct, including one implementing an interface with static virtuals | `GetNumVirtuals()` | measured: `SStat : IStat` is 5, `CStatOnly : IStatOnly` is 4 |
| delegate, enum, `Nullable<T>`, `__Canon` | `GetNumVirtuals()` | measured |
| array, SZ or MD, over any element including a type variable | `System.Array`'s `GetNumVirtuals()`, 24 on both runtimes | measured: `int[]`, `int[,]`, `string[]`, `T[]` |
| pointer, function pointer | `UIntPtr`'s count, 15 | measured: `int*`, `delegate*<void>` |
| byref, type variable | 0 | measured: `int&`, `List<>`'s `T` |
| created `TypeBuilder` type | as for its kind | measured: an interface with one instance abstract and one static abstract is 2; a class implementing `IMarker` is 4 |
| on .NET 11, a type with `Task`- or `ValueTask`-returning virtuals | one more for each, instance or static | measured, next section |

### .NET 11's async variants

On .NET 11, `MethodTableBuilder` adds an async variant MethodDesc after every method whose return type is CoreLib's
`Task`, `Task<T>`, `ValueTask` or `ValueTask<T>` (`methodtablebuilder.cpp:3340-3580` on .NET 11, with
`ClassifyMethodReturnKind`, `method.cpp:2498-2564`; read). The variant copies the method's attributes, so a virtual
method's variant takes its own vtable slot, straight after the method's, and a static virtual's variant is a second
`IsVirtual() && IsStatic()` MethodDesc, which this QCall counts. On .NET 10 the same code runs only under
`DOTNET_RuntimeAsync=1` (`methodtablebuilder.cpp:2686-2690`, `clrconfigvalues.h:714`, default 0; read).
`RuntimeTypeHandle_GetMethodAt` answers null for a variant (`runtimehandles.cpp:444-451` on both), and
`GetInterfaceMap` skips a null handle.

Measured, for `interface ITask { Task<int> A(); int B(); ValueTask C(); static abstract Task S(); static abstract int T(); }`:

| runtime | count | `GetMethodAt` for slots 0 up to the count |
|---|---|---|
| .NET 10 | 5 | `A`, `B`, `C`, `S`, `T` |
| .NET 11 | 8 | `A`, null, `B`, `C`, null, `S`, null, `T` |

`typeof(CTask).GetInterfaceMap(typeof(ITask))` is the same five pairs on both runtimes (measured). This is the
divergence `docs/divergences.md` already records under "A runtime-async method's async variant has no virtual slot of
its own" (decision 19 of `docs/plans/2026-11-11-runtime-async.md`): PawPrint lays out no variant slots, so its tables
on .NET 11 are .NET 10's. This QCall inherits it (below).

### Measurements on real .NET

The probe's fixtures (C#):

```csharp
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
public interface IStatVirtOnly { static virtual int S() => 4; }
```

with classes and a struct implementing them. Each row is the count from the private wrapper called by reflection
(`nvs`), `RuntimeTypeHandle.GetNumVirtuals` likewise (`nv`), and for an interface what `GetMethodAt` names at each slot
below the count. The same on both runtimes except where marked.

| type | nvs | nv | slots |
|---|---|---|---|
| `IMarker` | 0 | 0 | |
| `IOne` | 1 | 1 | `M` |
| `IStat` | 2 | 1 | `I`, static `S` |
| `IStatVirt` | 3 | 1 | `get_P`, static `SV`, static `SA` (`Plain` not counted) |
| `IDim` | 1 | 1 | `D` |
| `ISealedDim` | 1 | 1 | `A` (the sealed and private methods not counted) |
| `IBase` | 2 | 1 | `M`, static `S` |
| `IReabs` | 1 | 1 | `IBase.M` (its own reabstraction; `IBase`'s two not counted) |
| `IDimOverride` | 1 | 1 | `IBase.M` (its own explicit body) |
| `IStaticReabs` | 1 | 0 | static `IBase.S` |
| `IGenMethod` | 2 | 1 | `G`, static `SG` |
| `IG<>`, `IG<int>`, `IG<string>`, `IG<T>` over `List<>`'s `T` | 2 | 1 | `Get`, static `Make` |
| `IInC<>`, `IInC<object>` | 0 | 0 | |
| `IProps` | 4 | 4 | `get_P`, `set_P`, `add_E`, `remove_E` |
| `IStatOrder` | 4 | 2 | `I1`, `I2`, static `S1`, static `S2` (instance first, then static, each in declaration order) |
| `IStatOnly`, `IStatVirtOnly` | 1 | 0 | static `S` |
| `IEnumerable<int>`, `IEnumerable<>`, `IEnumerable<T>` over `List<>`'s `T` | 1 | 1 | `GetEnumerator` |
| `IList` | 11 | 11 | `get_Item` first |
| `IAsyncDisposable` | 1; 2 on .NET 11 | 1; 2 | `DisposeAsync`; on .NET 11 then null |
| `IAsyncEnumerator<int>` | 2; 3 on .NET 11 | 2; 3 | `MoveNextAsync`, `get_Current`; on .NET 11 a null between them |
| `INumberBase<int>`, `INumberBase<>` | 42; 45 on .NET 11 | 1 | one instance method, then the static virtuals (.NET 11's CoreLib adds three `TryParsePartial`) |
| `INumber<int>` | 10 | 0 | ten static virtuals |
| `COne : IOne`, `SStat : IStat`, `CAbs` (abstract), `CImplBase : IBase`, `CReabs`, `CG : IG<int>` | 5 | 5 | |
| `CStatOnly : IStatOnly`, `CDimOverride` | 4 | 4 | |
| `object`, `__Canon`, `int?` | 4 | 4 | |
| `string`, `int`, `List<int>`, `List<>` | 27, 31, 30, 30 | the same | |
| `int[]`, `int[,]`, `string[]`, `T[]` | 24 | 24 | |
| `int*`, `delegate*<void>` | 15 | 15 | (`UIntPtr`'s count) |
| `int&`, `List<>`'s `T` | 0 | 0 | |

**Measured, the rule.** Over every exported CoreLib type and every probe fixture, the count equals `GetNumVirtuals`
plus, for an interface only, the number of its declared methods that are both static and virtual (reflection's
`GetMethods(DeclaredOnly | Static | Public | NonPublic)`, filtered on `IsVirtual`), counting a `Task`-like one twice on
.NET 11. 1,414 types on .NET 10 (144 interfaces, 43 with static virtuals) and 1,483 on .NET 11 (146, 44), with no
disagreement on either. Without the doubling, .NET 11 disagrees on `ITask` alone; with it, .NET 10 disagrees on `ITask`
alone. So no CoreLib interface declares a `Task`-returning static virtual.

**Measured, `GetMethodAt` below the count.** For every one of those interfaces, every slot below the count either
names a method or is null, and the slot at the count raises `ArgumentException`. 622 slots on .NET 10, none null; 638 on
.NET 11, 5 null: one each in `IAsyncDisposable` and `IAsyncEnumerator<T>`, and three in `ITask`.

**Measured, maps.** `GetInterfaceMap` lists the instance methods first and then the static ones, in the count's order,
on both runtimes: `SStat` / `IStat` is `[I, S]`; `CStatOrder` / `IStatOrder` is `[I1, I2, S1, S2]`; `int` /
`INumberBase<int>` follows the slot list above. `CReabs` / `IReabs` maps `IReabs`'s own reabstraction of `IBase.M` to
null; `CDimOverride` / `IDimOverride` maps its explicit body to itself, with `IDimOverride` as the reflected type. (Those
two are #2290's questions, recorded here because the count decides which interface methods appear.)

**Measured, by-product for #2294.** `GetMethodAt` at the count (`IOne` slot 1, `IStat` slot 2, `COne` slot 5) raises
`System.ArgumentException`, `HResult` 0x80070057, `ParamName` null, message "Specified argument was out of the range of
valid values.", on both runtimes. PawPrint raises the same with the message "Value does not fall within the expected
range.". This is the measurement #2294 asks for first. `GetInterfaceMap` never asks for the slot at the count, so this
issue does not depend on it.

### PawPrint on `pr-2270-verify-interface`

Both parked guests stop at "Unimplemented native method (PInvokeImpl
QCall!RuntimeTypeHandle_GetNumVirtualsAndStaticVirtuals)" (measured by #2298).

What exists (read):

- **`VirtualSlotLayout.numVirtuals`** (`WoofWare.PawPrint/VirtualSlotLayout.fs:276-324`) answers
  `RuntimeTypeHandle.GetNumVirtuals` (the FCall, `Native/NativeRuntimeTypeFCall.fs`, the `"GetNumVirtuals"` arm) over
  the `RuntimeTypeHandleTarget` union: a closed type through `ConcreteMethodTable.numVirtualsOfClosed`, a generic
  definition or open construction through `numVirtualsOfDefinition`, an array through `System.Array`'s. It refuses the
  dynamic-methods class (`refuseMetadataQuery`) and a byref, pointer or function pointer *over a type variable*
  (`refuseComposite`), and fails on a type variable.
- **`VirtualSlotLayout.methodAt`** (`:349-447`) answers `RuntimeTypeHandle_GetMethodAt`. Past the vtable it searches
  only an interface, and takes the static virtuals from `MethodSlotTable.BeyondVtable` filtered to
  `IsVirtual && IsStatic`, in the order `MethodTableLayout.slotsBeyondVtableOfDefinition` places them, which is
  CoreCLR's `PlaceNonVirtualMethods` order (verified against the host's `GetSlot` over CoreLib, per that function's
  docstring). Its docstring already says `GetNumVirtualsAndStaticVirtuals` counts the same way. The filter is a local
  function inside `methodAt`, `staticVirtualsOf`, so nothing else can ask for the tail.
- **`MethodTableLayout.MethodSlotTable`** (`WoofWare.PawPrint.TypeSystem/MethodTableLayout.fs`) is the vtable and the
  region past it, as two lists; `ConcreteMethodTable.slotTableOfClosed` and `slotTableOfDefinition` build it.

So PawPrint already holds the number: `numVirtuals` plus, for an interface, the length of the tail which `methodAt`
filters. What it lacks is a function that answers it, and the tail is computed inside `methodAt` where the count cannot
reach it.

### PawPrint with a throwaway of the rule (reverted)

The throwaway handler in `Native/NativeRuntimeTypeQCall.fs` decoded the argument with
`NativeCall.qCallTypeHandleToRuntimeTypeHandleTarget`, took `VirtualSlotLayout.numVirtuals`, then called
`VirtualSlotLayout.methodAt` from that slot upwards until it answered `OutOfRange`, and pushed the slot it stopped at.

- **The rows.** Every row above, up to the TypeDescs, prints the same under PawPrint on .NET 10's CoreLib as on real
  .NET 10, slot lists included (compared line by line by a script, with #2294's message mapped). On .NET 11's CoreLib
  every row prints the same as on real .NET 11 except `ITask`, `CTask`, a class with two `Task`-returning virtuals,
  `IAsyncDisposable` and `IAsyncEnumerator<int>`, where PawPrint's count is the real count less the null slots, and its
  slot list is the real one with the nulls removed.
- **The whole of CoreLib.** A dump of every exported CoreLib type and fixture, with its count and, for an interface,
  its slot list, was compared between PawPrint and real .NET by a script: on .NET 10, 1,414 types and 622 interface
  slots, no difference; on .NET 11, 1,483 types, 23 differences, every one a type with a `Task`-like virtual (three
  interfaces, the probe's two classes, and 18 CoreLib classes and structs such as `Stream`, `TextWriter`, `Timer` and
  `CancellationTokenRegistration`). For each of the 23, PawPrint's count is the real count less the number of slots
  below it where real `GetMethodAt` answers null (for a class, counted by calling `GetMethodAt` on each of its slots),
  and no other type has a null slot. That is the property the test below asserts. The .NET 10 dump took under a minute
  inside a guest under PawPrint.
- **TypeDescs.** `int*` stops in `methodAt`: "RuntimeTypeHandle_GetNumVirtualsAndStaticVirtuals: *1 is a TypeDesc with
  no method table; the managed wrapper throws ArgumentException before this point". Real .NET answers 15. (`methodAt`'s
  own wrapper does throw for a TypeDesc; this QCall's does not.)
- **`TypeBuilder` types.** An impure probe with dynamic code on: the interface with one instance and one static
  abstract is 2, the class implementing `IMarker` is 4, and `GetInterfaceMap` over it returns two empty arrays naming
  the class. The same on real .NET 10 and 11.
- **The parked guests.** `TypeGetInterfaceMapNoMethods.cs` exits 0. `TypeGetInterfaceMap.cs` stops at its first map,
  `typeof(COne).GetInterfaceMap(typeof(IOne))` (line 19), with "Unimplemented native method (PInvokeImpl
  QCall!RuntimeTypeHandle_GetInterfaceMethodImplementation): System.Private.CoreLib
  System.RuntimeTypeHandle::GetInterfaceMethodImplementation(System.Runtime.CompilerServices.QCallTypeHandle,
  System.Runtime.CompilerServices.QCallTypeHandle, System.RuntimeMethodHandleInternal) ->
  System.RuntimeMethodHandleInternal", 3 frames out from `Main`. So `GetMethodAt` and `GetMethodBase` for `IOne.M`
  run, and the next stop is #2290, as #2298's parking reason predicts.

### A gap outside this QCall: `GetNumVirtuals` answers 0 for a pointer

The probe of the TypeDesc rows found a wrong answer, not a refusal, in the existing FCall. Under PawPrint,
`RuntimeTypeHandle.GetNumVirtuals(typeof(int*))` and `(typeof(delegate*<void>))` are 0; real .NET answers 15 on both
runtimes (measured), because `TypeHandle::GetMethodTable` gives a pointer `UIntPtr`'s MethodTable. The cause is
`ConcreteMethodTable.vtableOfClosed` (`WoofWare.PawPrint.TypeSystem/ConcreteMethodTable.fs`, the
`Byref | Pointer | FunctionPointer` arm), which answers an empty vtable for all three with the comment "TypeDescs in
CoreCLR with no MethodTable"; `slotTableOfClosed` repeats it. That is true of a byref and false of a pointer and a
function pointer. The FCall also fails on a type variable, where a release CoreCLR answers 0.

No public API reaches it: the BCL calls `GetNumVirtuals` only on the declaring type of a method, which is never a
TypeDesc. It is reachable by reflection over CoreLib's internals, and it matters to this plan because whatever this
QCall answers for a pointer should agree with the FCall (question 4).

## Surprises against the issue's description

1. The issue's rule, "plus one for each method of the type that is both virtual and static, when the type has any", is
   right, and in effect it applies to interfaces only: a class implementing an interface with static virtuals has the
   flag, but can declare none.
2. Static *reabstractions* (`static abstract int IBase.S();` in a derived interface) and static virtuals *with bodies*
   count, as do instance reabstractions and explicit default bodies of a base interface's method. Base interfaces'
   own methods never count.
3. On .NET 11 the count includes async variant slots, which `GetMethodAt` answers null for; PawPrint has none. The
   resulting difference is invisible through `GetInterfaceMap` and visible only by reflection over CoreLib's internals.
4. PawPrint's `GetMethodAt` already answers every slot this count implies, in the right order, measured over all of
   CoreLib. The issue's question about slots at or above `numVirtuals` is answered: it searches an interface's static
   virtuals, in the order real .NET does. Its only gap is #2294's message, which `GetInterfaceMap` never reaches.
5. A pointer's count is `UIntPtr`'s, not 0, and the existing `GetNumVirtuals` FCall gets that wrong.

## Options

### Decision 1: how the count relates to `GetMethodAt`

Every option answers the same numbers on every measured type. They differ in what keeps the count and `GetMethodAt`
in agreement, which `GetInterfaceMap` needs (a count past `GetMethodAt`'s last slot makes it throw).

#### A. Derive the count from `methodAt`

`numVirtuals`, then `methodAt` from there until `OutOfRange` (the throwaway).

- Agreement by construction, through the one function `GetInterfaceMap` also calls.
- The count is a side effect of probing a lookup, and every call rebuilds the slot table once per static virtual
  (`methodAt` computes `slotTableOfClosed` on each call; 42 times for `INumberBase<int>`). It also hides the rule: a
  reader cannot see from the code what is counted.
- Blast radius: the handler only. Reversibility: total. Information kept: none beyond the number.

#### B. One function for the static-virtual tail, used by both

Lift `methodAt`'s private `staticVirtualsOf` into a named function that returns an interface's static virtuals in slot
order, and an empty list for every other type. `methodAt` indexes it past the vtable; a new
`numVirtualsAndStaticVirtuals` is `numVirtuals` plus its length. The handler decodes, calls it and pushes the number.

- Agreement because both read the same list, and the rule is written once, where it can be read.
- Testable as a function against the host (below), and `methodAt`'s existing tests keep covering the list.
- Blast radius: `methodAt` changes shape inside (a refactor that preserves behaviour), plus a new public function and
  the handler. Reversibility: high.
- Information kept: the tail itself, which #2290's plan may want (`GetInterfaceMethodImplementation` treats a static
  interface method differently).

#### C. A transliteration of CoreCLR's loop, independent of `methodAt`

`numVirtuals` plus the count of declared methods that are virtual and static, for any type, without asking whether it
is an interface.

- Closest to the source text, and an independent count: if it and `methodAt` disagreed, a test comparing them would
  say so.
- But two spellings of one rule which must agree, which is what decision 19 of the runtime-async plan and
  `numVirtualsOfClosed`'s comment both argue against; and the disagreement it could reveal is exactly the one that
  makes `GetInterfaceMap` throw. Its extra generality (non-interfaces) answers nothing, since no loadable type reaches
  it.
- Blast radius as B. Information kept: the number only.

**Recommended: B.**

### Decision 2: where the tail function lives

- **(a) `WoofWare.PawPrint.TypeSystem`**, as `MethodTableLayout.staticVirtuals (isInterface : bool) (table :
  MethodSlotTable) : VtableSlot list` (or taking the `TypeInfo`), beside `slotsBeyondVtableOfDefinition`, which places
  the slots it filters. `VirtualSlotLayout` keeps the dispatch over `RuntimeTypeHandleTarget`, as `numVirtuals` does.
  It is a fact about CoreCLR's method table, and AGENTS.md says to put a fact in the lowest project that can hold it;
  the analyser could then ask it. A new public function in the TypeSystem package.
- **(b) `WoofWare.PawPrint/VirtualSlotLayout.fs`**, beside `methodAt`. Smaller; no change to the TypeSystem package's
  API. The rule stays out of reach of anything that cannot see the interpreter.

**Recommended: (a).** The function is three lines; its home decides who can use it.

### Decision 3: .NET 11's async variants

- **(i) Keep decision 19 of the runtime-async plan**: the count excludes variant slots, so it agrees with PawPrint's
  own `GetMethodAt`, and every `GetInterfaceMap` result is real .NET's (null slots are skipped there). Extend the
  existing `docs/divergences.md` entry to name this QCall alongside `GetSlot` and `GetNumVirtuals`, with `ITask`'s
  measurement.
- **(ii) Lay out variant slots** (decision 19's option (a)): every slot number on .NET 11 moves, `methodAt` must answer
  null at the variants, and the layout becomes a function of the runtime version. Its own change, decided against once
  already.
- **(iii) Count the variants here only**, without (ii): `GetInterfaceMap` would then call `GetMethodAt` past PawPrint's
  last slot and throw `ArgumentException` where real .NET returns a map. Wrong; listed only to rule it out.

**Recommended: (i).**

### Decision 4: receivers without a MethodTable of their own

Only reflection over CoreLib's internals reaches these.

- **(a) Model `TypeHandle::GetMethodTable`**: a pointer or function pointer counts as `UIntPtr` (15), a byref and a type
  variable as 0, as measured. The dynamic-methods class stays refused, as `numVirtuals` refuses it. Cheap, and right on
  every measured row. But the FCall `GetNumVirtuals` would still answer 0 for a pointer, so the two would disagree until
  that is fixed too; and fixing the FCall is a separate change with its own tests (`vtableOfClosed` answers for every
  consumer of the vtable, not only the FCall).
- **(b) Refuse** a byref, pointer, function pointer and type variable, with a TODO naming the measured answer, and file
  an issue to model `TypeHandle::GetMethodTable` for the FCall and this QCall together. A stop, not a wrong answer, and
  one feature per change.
- **(c) Follow `numVirtuals` as it is**: 0 for a closed pointer, a refusal for one over a type variable, a failure for a
  type variable. Copies a wrong answer into a new place.

**Recommended: (b).** `GetInterfaceMap` never reaches these, and the right fix is in `ConcreteMethodTable`, shared with
the FCall.

## Questions for the decider

1. **How does the count relate to `GetMethodAt`: A (derived from it), B (one shared tail) or C (an independent
   transliteration)?** Recommended: B.
2. **Where does the tail function live: in `WoofWare.PawPrint.TypeSystem` (`MethodTableLayout`) or in
   `VirtualSlotLayout`?** Recommended: TypeSystem. It is a new public function in that package; say so in the PR body.
3. **On .NET 11, does the count keep excluding async variant slots, as decision 19 of the runtime-async plan does for
   the vtable?** Recommended: yes, with the divergence entry extended to name this QCall.
4. **What does the QCall answer for a byref, pointer, function pointer or type variable: model
   `TypeHandle::GetMethodTable` (a), refuse (b), or copy `numVirtuals`' current answers (c)?** Recommended: (b), and an
   issue (question 5).
5. **Should the existing `GetNumVirtuals` FCall's 0 for a pointer and a function pointer become an issue?** Measured:
   real .NET 15 (`UIntPtr`'s count) on both runtimes, PawPrint 0. The cause is `ConcreteMethodTable.vtableOfClosed`'s
   TypeDesc arm. Recommended: yes, one issue covering the FCall and this QCall's TypeDesc arm, naming the measurement.
6. **May the new guest call the private wrapper `RuntimeTypeHandle.GetNumVirtualsAndStaticVirtuals(RuntimeType)` by
   reflection?** It is the only way a guest sees the count for an interface with methods before #2290, and for a
   class at all. #2270's decision 5 allowed the same for its wrapper. `RuntimeTypeHandle` has two static methods of that
   name, the `LibraryImport` over `QCallTypeHandle` and the wrapper over `RuntimeType`; the lookup names the parameter
   type `System.RuntimeType`. Recommended: yes, with a first check that the method was found.
7. **Does the property test also compare `GetMethodAt`'s non-null slots, in order, with PawPrint's `methodAt`?** That is
   the pairing `GetInterfaceMap` relies on, and the measurement shows it holds over CoreLib. `TestMethodAtSlotShapes.fs`
   covers some shapes of it for classes, not interfaces' static tails. Recommended: yes; it costs one call per slot and
   makes the property independent of the runtime (below).
8. **Does #2294's message fix ride along?** `GetInterfaceMap` never asks for the slot at the count, so this issue does
   not need it; the measurement #2294 asked for is now in hand ("Specified argument was out of the range of valid
   values.", `ArgumentException`, 0x80070057, null `ParamName`, on both runtimes). Recommended: no; leave it to #2294,
   and add the measurement to that issue.

## Test design

Write the tests and guests first, in a commit of their own, and record the failing output (the unimplemented-QCall
message for the guests, and the compile failure of the property tests) before the function or the handler exists. Run
the default suite and the four Guest categories as separate invocations.

### Property tests (with B)

In `WoofWare.PawPrint.Test`, as `TestNumVirtualsAndStaticVirtuals.fs`. Build each type twice, as a PawPrint target and
as a host `Type` over the same image. `TestInterfaceVerification.fs` has a private `build` that turns a host `Type` into
a target over CoreLib and a Roslyn image; lift it into a shared test helper rather than copying it.

The host oracle for a type `t` of count `n`: the private wrapper by reflection, then the private static
`RuntimeTypeHandle.GetMethodAt(RuntimeType, int)` and `RuntimeType.GetMethodBase(RuntimeType,
RuntimeMethodHandleInternal)` (as `TestMethodAtSlotShapes.fs` reaches them) for each slot below `n`. The property:

- PawPrint's count is `n` less the number of slots where the host's handle is null; and
- for an interface, `methodAt` over PawPrint's slots names, in order, the host's non-null methods (compared by
  metadata token and declaring type); and `methodAt` at PawPrint's count is `OutOfRange`.

On .NET 10 there are no null slots, so this is plain equality. On .NET 11 it is decision 3's divergence, stated as a
law rather than as a list of exceptions. Assert that some type in each corpus has a non-empty static tail, so the second
half is not vacuous; and count the types with null slots, which is 0 on the .NET 10 host.

1. **CoreLib.** Every exported CoreLib type (no TypeDesc is exported). 1,414 types on .NET 10, of which 144 interfaces
   with 622 slots; the PawPrint side of the same comparison took under a minute inside a guest, so outside one it should
   be well under that. Thread the state through the types of one assembly, as #2270's sweep does.
2. **Generated corpora (FsCheck).** Generate a small C# assembly of interfaces whose members are drawn from: an instance
   abstract method, a default body, a sealed default body and a private instance method (neither virtual), a
   `static abstract` method, a `static virtual` method with a body, a plain static method, an instance or static
   property and event, an instance or static generic method, and, over an earlier interface's members, an explicit
   default body, an instance reabstraction and a static reabstraction; with arity 0 or 1, and members mentioning the
   parameter. Then classes and structs implementing some of them, each member implemented. The pool: each interface's
   definition, its instantiation over `int` and `string`, an open construction over a class's own parameter, each class
   and struct, and their arrays. Valid by construction (a Roslyn error fails the test, as in #2270's generator). Coverage
   labels from the host's side, each with a floor at about half of what `CoverageSample`'s fixed sample reaches: an
   interface with a non-empty static tail; one with a reabstraction counted; one with a non-virtual member not counted;
   a class or struct implementing an interface with static virtuals; an open construction. Members returning `Task` are
   left out of the generator: on the .NET 10 host they are ordinary methods, and the CoreLib sweep reaches them on .NET
   11.

If the host is ever .NET 11 (by roll-forward; `PAWPRINT_TEST_RUNTIME` does not move the host), the property still
holds as stated; the example tests below pin the .NET 10 numbers.

### Example tests

- The fixtures above: `IStat` 2, `IStatVirt` 3, `IStaticReabs` 1, `IReabs` 1, `ISealedDim` 1, `IStatOrder` 4,
  `IG<T>` over another definition's `T` 2, `SStat` 5. The generated property can miss a shape; these cannot.
- Decision 4 (b): a pointer, a byref, a function pointer and a type variable are refused with the TODO, so whoever
  resolves question 5's issue sees the test change.

### Guests

**New: `sourcesPure/RuntimeTypeHandleGetNumVirtualsAndStaticVirtuals.cs`**, calling the wrapper by reflection, one exit
code per call, each expectation the same on real .NET 10 and 11 (so no `Task`-like member, and no CoreLib interface
whose shape differs between them, such as `INumberBase<T>`):

| # | type | expected |
|---|---|---|
| 1 | the wrapper is found | not null |
| 2 to 4 | `IMarker`, `IOne`, `IStat` | 0, 1, 2 |
| 5 | `IStatVirt` (static virtual with a body, a static non-virtual) | 3 |
| 6 | `ISealedDim` (sealed and private instance methods) | 1 |
| 7, 8 | `IReabs`, `IDimOverride` | 1, 1 |
| 9 | `IStaticReabs` | 1 |
| 10 | `IGenMethod` | 2 |
| 11 to 13 | `IG<>`, `IG<int>`, and `IG<T>` over another definition's `T` (by `MakeGenericType`) | 2 each |
| 14 | `IInC<>` | 0 |
| 15 | `IProps` | 4 |
| 16 | `IStatOrder` | 4 |
| 17 | `IStatOnly` | 1 |
| 18 | `IEnumerable<T>` over `List<>`'s `T` | 1 |
| 19 to 21 | `COne`, `SStat`, `CStatOnly` | 5, 5, 4 |
| 22, 23 | `int[]`, `T[]` over `List<>`'s `T` | 24, 24 |

(If question 4 is answered (a), add `int*` 15, `int&` 0 and `List<>`'s `T` 0.)

**Un-park `sourcesPure/TypeGetInterfaceMapNoMethods.cs`.** Measured to exit 0 under PawPrint with the throwaway, and on
real .NET 10 and 11.

**Re-park `sourcesPure/TypeGetInterfaceMap.cs`** on #2290, with the reason quoting the measured stop at its first map,
"Unimplemented native method (PInvokeImpl QCall!RuntimeTypeHandle_GetInterfaceMethodImplementation)". Keep its few
assertions; #2290's plan designs the map's tests.

**Add to `sourcesImpure/TypeBuilderVerifyInterfaceIsImplemented.cs`** (or a sibling, if the decider prefers a guest
per QCall): a created type implementing `IMarker`, through `GetInterfaceMap`, gives two empty arrays naming it; and a
created interface with one instance and one static abstract method has a count of 2 through the wrapper. Both measured
on real .NET 10 and 11 and under PawPrint with the throwaway.

**Controls on real .NET**, as for #2270: each guest exits 0 on real .NET 10 and 11 as written, and each perturbed
expectation (each count plus one, each count minus one, the lookup inverted) exits with its own check's number on both.

## Mutants

Each must turn some test red. Run each against the example tests, the properties and the guests, and record which went
red.

| mutant | killed by |
|---|---|
| the count is `numVirtuals` alone (the planning throwaway on FakeItEasy) | guest 4; CoreLib property (43 interfaces) |
| static virtuals counted for every type, not interfaces only, through a list that includes a class's static implementations | guests 20, 21 |
| the tail filter drops `static virtual` with a body (only abstract counted) | guest 5 |
| the tail filter counts every static (non-virtual included) | guest 5; CoreLib property |
| a static reabstraction not counted | guest 9 |
| instance non-virtual members counted | guest 6 |
| base interfaces' methods counted | guests 7, 9 |
| generic methods dropped | guest 10 |
| an open construction refused or answered as a TypeDesc | guest 13; generated property |
| a generic definition answered from its first instantiation's table, or refused | guest 11 |
| an array treated as having no MethodTable (0) | guests 22, 23 |
| `Task`-like virtuals counted twice (decision 3 (iii)) | CoreLib property on the .NET 10 host (`IAsyncDisposable`) |
| `methodAt` changed to read a different tail from the count (the two copies decision 1 B removes) | the second half of the property |
| the count one past the tail | every guest with a static tail |
| the handler pushes an `Int64` or a native int | every guest (the stub reads an `Int32`) |
| decision 4 (b)'s refusal replaced by 0 | the refusal example test |

## Probe files

The probes were not kept. One console program, built for .NET 10 and run as is and rolled forward to .NET 11, and
published self-contained for PawPrint's runs (also built for .NET 11 and published against it, for PawPrint on .NET 11's
CoreLib), with a mode per measurement: `rows` (the table above), `sweep` (the rule's check over CoreLib), `slotsweep`
(`GetMethodAt` below and at the count, for every interface), `maps`, `typedesc`, `oor` (#2294's measurement) and `dump`
(every type's count and slot list, which a script compared between runtimes; `dump nulls` added each class's count of
null slots). A second program built two types with `TypeBuilder`. The parked guests were copied unchanged into scratch
projects and run the same way. The direct call:

```csharp
static readonly Type RuntimeTypeT = typeof(object).Assembly.GetType("System.RuntimeType");
static readonly MethodInfo NvsM = typeof(RuntimeTypeHandle).GetMethod(
    "GetNumVirtualsAndStaticVirtuals", BindingFlags.NonPublic | BindingFlags.Static,
    null, new[] { RuntimeTypeT }, null);

static int Nvs(Type t) => (int)NvsM.Invoke(null, new object[] { t });
```

The throwaway handler was decision 1's option A.

## Decisions

Decided 2026-11-16, by the decider the owner delegated to. Before deciding I read the QCall, `RuntimeTypeHandle_GetMethodAt`,
the `GetNumVirtuals` FCall, `TypeHandle::GetMethodTable`, `TypeDesc::GetMethodTable`, the managed wrappers and
`RuntimeType.GetInterfaceMap` in both pinned runtime sources; read the PawPrint code each option touches
(`VirtualSlotLayout.numVirtuals` and `methodAt`, `ConcreteMethodTable`, `MethodTableLayout.slotsBeyondVtableOfDefinition`,
`TypeHandleTag.forTarget`); read decision 19 of the runtime-async plan and its entry in `docs/divergences.md`; and
spot-checked the planner's logs against the plan's tables (the row dumps on both runtimes, the rule's sweep, the slot
sweep, the TypeDesc rows, the out-of-range message, and PawPrint's rows on .NET 11's CoreLib).

The plan's account of CoreCLR is right, and so are its tables. The QCall and `GetMethodAt` are the same text on .NET 10
and .NET 11. The corrections and additions are under "Corrections to the study" and "Additions" below; decisions 1, 2, 4
and 5 depend on them.

One issue comes out of this plan: #2299. It is not fixed by this change. Two comments go
on existing issues: the measurement on #2294, and what this study learned about the map's contents on #2290.

### 1. How the count relates to `GetMethodAt`: B, one shared tail

The count is `numVirtuals` plus the length of the list of static virtuals, and `methodAt` indexes that same list past
the vtable. This is how CoreCLR writes both functions (`GetNumVirtuals()`, then the same loop), and the rule is then
written once.

- Add `VirtualSlotLayout.numVirtualsAndStaticVirtuals`, with `numVirtuals`' parameters and return type
  (`IlMachineState * int`).
- `methodAt` and the new function must obtain the tail from one function in `VirtualSlotLayout`, which answers over a
  `RuntimeTypeHandleTarget`. Delete the local `staticVirtualsOf` inside `methodAt`.
- The handler in `Native/NativeRuntimeTypeQCall.fs` decodes its one argument with
  `NativeCall.qCallTypeHandleToRuntimeTypeHandleTarget` (the wrapper asserts a non-null type, so the variant that
  tolerates a null handle is not wanted), calls the function, and pushes an `Int32`. Register it in
  `Native/NativeQCall.fs` beside `RuntimeTypeHandle_GetMethodAt`.
- Make the change to `methodAt` a commit of its own which preserves behaviour, after the commit that adds the tests and
  before the commit that adds the count. `TestMethodAtSlotShapes.fs`, `TestVirtualMethodSlots.fs` and
  `sourcesPure/ReflectionMethodAtSlot.cs` must stay green across it without edits.

Why not A: the count would be the side effect of probing a lookup until it fails, it rebuilds the slot table once for
each static virtual, and a reader cannot see what is counted. Why not C: it is a second spelling of the tail which has to
agree with `methodAt`'s, and the only thing a disagreement can do is make `GetInterfaceMap` throw.

One limit of B, which the tests must cover. B shares the tail, but the two functions still find the *boundary*
separately: the count's first half is the length of the placement vtable (`numVirtuals`), and `methodAt` takes its
boundary from the length of the content vtable (`dispatchTableOfClosed`'s occupants, or `contentVtableOfDefinition`).
Those two lengths are equal today and nothing in this change states it. So the property test checks the boundary for
every type, not only for interfaces (under "Tests").

### 2. Where the tail's rule lives: `WoofWare.PawPrint.TypeSystem`, and without a `bool` parameter

Agreed that it goes in `MethodTableLayout`, beside `slotsBeyondVtableOfDefinition`, which is `internal` to that project
and places the slots the rule filters. I overrule the proposed signature. `staticVirtuals (isInterface : bool) (table :
MethodSlotTable)` lets a caller pass a flag which does not describe the table, and it asks for a whole `MethodSlotTable`
when only the part past the vtable is read. Instead:

```fsharp
/// The static virtual methods a type declares, in slot order: the slots CoreCLR numbers on from
/// `GetNumVirtuals()` for an interface. Empty for every type that is not an interface.
let staticVirtualsOfDefinition
    (operation : string)
    (owner : SlotOwner)
    (typeInfo : TypeInfo<GenericParamFromMetadata, TypeDefn>)
    : VtableSlot list
```

- It answers `[]` when `typeInfo.IsInterface` is false, and otherwise `slotsBeyondVtableOfDefinition operation owner
  typeInfo` filtered to `IsVirtual && IsStatic`.
- It is a function of the definition's metadata alone. It needs no concretisation and no vtable, so it returns a plain
  list and threads no state. Today `methodAt` calls `slotTableOfClosed` or `slotTableOfDefinition` only to read
  `BeyondVtable`, which also builds the vtable a second time; the new function removes that. A closed interface's tail
  is its definition's (`ConcreteMethodTable.slotTableOfClosed` already takes it from the definition's identity).
- Put two facts in a comment at the `IsInterface` test, because the two CoreCLR functions differ there and the
  difference is easy to "fix" wrongly. `RuntimeTypeHandle_GetMethodAt` tests `IsInterface()` before it searches;
  `RuntimeTypeHandle_GetNumVirtualsAndStaticVirtuals` does not, and counts for any type whose
  `HasVirtualStaticMethods` flag is set, which includes a class that implements such an interface. The two agree
  because no type but an interface can declare a static virtual: the method table builder refuses the type
  (`IDS_CLASSLOAD_STATICVIRTUAL`), and `MethodTableLayout.declaredMethodsOf` already refuses it in the same place.
- In `VirtualSlotLayout`, the function over a target answers: a closed nominal type and a generic definition or open
  construction from the definition's metadata; an array `[]`, because its method table's slots are `System.Array`'s
  and that is a class. Do **not** route an array through `slotTableOfClosed`: that function refuses an array on
  purpose (the array's own methods past the vtable are not modelled). The static-virtual tail of an array is empty
  whatever those methods are, since none of them is virtual; say so in a comment.

`staticVirtualsOfDefinition` is a new public function in the published `WoofWare.PawPrint.TypeSystem` package, and
`numVirtualsAndStaticVirtuals` is a new public function in `WoofWare.PawPrint`. Say both in the pull request body.

### 3. .NET 11's async variants: (i), the count excludes them

I checked this against both sources and the logs. On .NET 11 the real count for `ITask` is 8 and real `GetMethodAt`
answers null at slots 1, 4 and 6; PawPrint on .NET 11's CoreLib answers 5 and five methods. `GetInterfaceMap` skips a
null handle and shrinks its arrays, so the map is the same five pairs either way. What must hold is that the count and
PawPrint's own `GetMethodAt` agree, and under decision 19 of the runtime-async plan PawPrint's tables have no variant
slots. Option (iii) would make `GetInterfaceMap` throw `ArgumentException` on .NET 11 for `IAsyncDisposable`,
`IAsyncEnumerator<T>` and every user interface with a `Task`-returning method, where real .NET returns a map. Option
(ii) is decision 19's option (a), which was decided against five days ago for reasons this QCall does not change: the
count is one more number that only indexes the same table.

What the implementer does:

- Extend the entry "A runtime-async method's async variant has no virtual slot of its own" in `docs/divergences.md`.
  Add to its "Observable example" that `RuntimeTypeHandle.GetNumVirtualsAndStaticVirtuals` and
  `RuntimeTypeHandle.GetMethodAt`, reached by reflecting over CoreLib's internals, show it too, with the measurement:
  on .NET 11.0.0-rc.3, `interface ITask { Task<int> A(); int B(); ValueTask C(); static abstract Task S(); static
  abstract int T(); }` has a count of 8 and `GetMethodAt` answers `A`, null, `B`, `C`, null, `S`, null, `T`; PawPrint
  answers 5 and `A`, `B`, `C`, `S`, `T`, and never a null handle. Add one sentence that `Type.GetInterfaceMap`, the
  only caller in CoreLib, returns the same map, because it skips null handles.
- Add a sentence to `numVirtualsAndStaticVirtuals`' docstring: on .NET 11 the number is CoreCLR's less its async
  variant slots, and it names the divergence entry. A caller needs to know that.
- Pin it with a test that does not need a .NET 11 host (under "Tests"). The plan's property is conditional on the
  host's null slots, and the test host is .NET 10, so as planned nothing in CI would read .NET 11's CoreLib at all.

### 4. A byref, pointer, function pointer or type variable: (b), refuse

`Type.GetInterfaceMap` cannot pass one: #2270's verdict has already raised for anything that is not an interface. Only
reflection over CoreLib's internals reaches this arm, and the right answer for a pointer needs something PawPrint does
not have (decision 5).

- `numVirtualsAndStaticVirtuals` tests `TypeHandleTag.isTypeDesc target` **first**, and refuses with a `failwith` whose
  message begins `TODO:`, names the operation and the target, says that CoreCLR answers the count of
  `TypeHandle::GetMethodTable()` (`UIntPtr`'s for a pointer and a function pointer, 0 for a byref and a type variable),
  and names #2299.
- The order matters, and the plan does not say so. If the new function simply added the tail to `numVirtuals`, a
  *closed* pointer, function pointer or byref would not be refused: `numVirtuals` sends
  `Closed (ConcreteTypeHandle.Pointer _)` to `ConcreteMethodTable.vtableOfClosed`, which answers an empty vtable, and
  the QCall would push 0. That is option (c), the wrong answer for a pointer, reached by accident. `numVirtuals`
  refuses only the composites over a type variable.
- Do not call `methodAt` for these either. Its failure message says the managed wrapper throws before the QCall, which
  is true of `GetMethodAt`'s wrapper and false of this one.
- `isTypeDesc` answers false for the dynamic-methods class, which `numVirtuals` then refuses with
  `refuseMetadataQuery`; keep that. It refuses a continuation type with `ContinuationType.unasked`; keep that too.
- A byref's 0 is right today, and I still refuse it: one test (`isTypeDesc`) with one refusal is easier to check than a
  rule with an exception, and #2299 answers all four kinds together.

### 5. File the issue: yes, #2299

I confirmed the measurement in the logs (`int*` and `delegate*<void>` are 15 from both the FCall and this QCall on
both runtimes; PawPrint's FCall prints 0 for both) and the reading (`TypeDesc::GetMethodTable` answers
`CoreLibBinder::GetElementType(ELEMENT_TYPE_U)` for `ELEMENT_TYPE_PTR` and `ELEMENT_TYPE_FNPTR`, and `NULL` otherwise,
on both runtimes).

I disagree with the plan about where the fix goes, and the issue says so. `vtableOfClosed`'s comment, "TypeDescs in
CoreCLR with no MethodTable", is true: a pointer has no method table of its own. What is missing is the step CoreCLR's
two functions take before they count, `TypeHandle::GetMethodTable()`, which *substitutes* `UIntPtr`'s method table for
a pointer or function pointer. Giving a pointer `UIntPtr`'s vtable inside `ConcreteMethodTable` would tell every reader
of the vtable, dispatch included, that a pointer has slots. The issue proposes a function from a target to the target
whose method table CoreCLR reads, used by the FCall and by this QCall, and leaves the choice to that issue's plan. It
also covers the FCall's failure on a type variable (a release CoreCLR answers 0), and the comment on the FCall's arm
which says a pointer has no method table.

### 6. The guest may call the wrapper by reflection: yes

It is the only way a guest sees a non-zero count before #2290. I confirmed the declarations, the same on both runtimes:
`private static partial int GetNumVirtualsAndStaticVirtuals(QCallTypeHandle)` is the `LibraryImport`, and
`internal static int GetNumVirtualsAndStaticVirtuals(RuntimeType)` is the wrapper (the plan calls the wrapper private;
it is internal, which changes nothing about the lookup).

- Look it up with `BindingFlags.NonPublic | BindingFlags.Static`, a null binder and the parameter types
  `{ typeof(object).GetType() }` (that is `System.RuntimeType`), so the lookup is not ambiguous and a change of
  signature returns null. Check 1 is that the method was found.
- Every call gets its own exit code.
- The public contract stays tested without the wrapper by `TypeGetInterfaceMapNoMethods.cs`, so a rename inside CoreLib
  breaks one guest and not the other. Keep the two apart; do not add wrapper calls to the public guest.
- No `Task`-returning member and no CoreLib type whose shape differs between runtimes in this guest. The logs show
  delegates differ too (`System.Action` is 15 on .NET 10 and 11 on .NET 11), so no delegate either.

### 7. The property also compares `GetMethodAt`'s slots: yes, and the boundary for every type

For an interface: PawPrint's `methodAt` over slots 0 up to its count names, in order, the methods the host's
`GetMethodAt` names at its non-null slots, compared by metadata token and declaring type.

For **every** type in the corpus, interface or not: `methodAt` at PawPrint's count is `OutOfRange`, and when the count
is above 0, `methodAt` at the count less one is a method. This is the check of the boundary that decision 1 asks for.
It costs two calls a type.

### 8. #2294's message does not ride along: agreed

`GetInterfaceMap` never asks for the slot at the count, and the owner asks for one feature in a change. The
measurement goes on #2294 as a comment, which removes that issue's first step. I confirmed it in the logs on both
runtimes: `System.ArgumentException`, `HResult` 0x80070057, `ParamName` null, "Specified argument was out of the range
of valid values."; PawPrint raises the same with "Value does not fall within the expected range.".

### Corrections to the study

- **"The right fix is in `ConcreteMethodTable`."** See decision 5: the vtable of a pointer is empty in CoreCLR too, and
  the missing piece is `TypeHandle::GetMethodTable`'s substitution.
- **Option 4 (b) as written does not refuse a closed pointer** unless the TypeDesc test comes first. See decision 4.
- **"The function is three lines"** with a `bool` and a `MethodSlotTable`. See decision 2 for the signature.
- **The property's .NET 11 half never runs in CI.** The host is .NET 10 unless someone rolls it forward by hand, so
  "the count is the host's less its null slots" is plain equality there, and no test as planned reads .NET 11's
  CoreLib. See the per-runtime test below.
- **The wrapper is `internal`, not private.**
- **The mutant "the tail filter loses `IsStatic`" is equivalent**, and is absent from the plan's table for that reason
  or by luck. Everything past an interface's vtable is static or not virtual, because `PlaceVirtualMethods` has already
  taken every instance virtual. So `IsVirtual` alone selects the same slots. Keep both conjuncts, since that is
  CoreCLR's text, and do not spend time trying to kill this mutant. Say it in a comment at the filter.

### Additions

#### Tests

In the order to write them. The first commit holds the tests and guests only, with their failing output recorded in
the commit message (the compile failure of the new F# tests, and the unimplemented-QCall stop of the guests).

1. **Lift the helper first.** Move `TestInterfaceVerification.fs`' private `build`, `target` and what they need into a
   shared test module, in a commit of its own that changes no test.
2. **The plan's property over the host's CoreLib and over generated corpora**, as designed, with decision 7's two
   halves. Keep the plan's coverage floors and its check that some type in each corpus has a non-empty tail.
3. **A per-runtime law which needs no host** (new). With `FrameworkUnderTest.everyRuntime` and
   `FrameworkUnderTest.requireCoreLibPathOf`, read each supported runtime's CoreLib and, for every interface
   definition in it, assert that PawPrint's count equals the number of the interface's MethodDef rows which have the
   `Virtual` attribute, read with `System.Reflection.Metadata` directly and not through PawPrint's reader. An
   interface has no parent, so its vtable is its own instance virtuals and its tail is its own static virtuals; the
   plan's sweep measured this rule on the host over 144 interfaces. On .NET 11's CoreLib this is the test that
   PawPrint's count has no variant slots. Assert that the corpus is not trivial: at least 40 interfaces with a static
   virtual on each runtime (43 and 44 measured, among exported types). If an interface breaks the law, stop and report
   it; do not weaken the law to pass.
4. **Named examples on each runtime's CoreLib** (new): `System.IAsyncDisposable` is 1 and
   `System.Collections.Generic.IAsyncEnumerator<>` is 2 on both, and `methodAt` at that count is `OutOfRange`. Real
   .NET 11 answers 2 and 3. A comment names the divergence entry.
5. **The plan's example tests** over the fixtures, and the refusals of decision 4: `int*`, `delegate*<void>`, `int&`,
   `List<>`'s `T`, and also `T*` and `T&` over `List<>`'s `T`. Each asserts the `TODO:` message names
   #2299. The closed `int*` case is the one that fails if the TypeDesc test is missing
   or late.
6. **Guests**, as the plan lists them, with these changes:
   - `sourcesPure/RuntimeTypeHandleGetNumVirtualsAndStaticVirtuals.cs`: the plan's 23 checks. Add `IDim` (1), an
     abstract class implementing `IOne` with an abstract method (5), `int[,]` (24), and an interface with a static
     field initialiser and one `static abstract` method (1: the class constructor is static, past the vtable, and not
     virtual). No TypeDesc checks, since decision 4 refuses them. `IDim`, the abstract class and `int[,]` were
     measured by the planner; the static-initialiser interface, and `IStat[]` in the mutant table below, are expected
     from the rule and were not measured, so measure them on real .NET 10 and 11 before relying on them (the guest's
     controls do this for the first).
   - Un-park `sourcesPure/TypeGetInterfaceMapNoMethods.cs` on every runtime.
   - Re-park `sourcesPure/TypeGetInterfaceMap.cs` on #2290 on every runtime, with the measured stop quoted.
   - The `TypeBuilder` checks go in a new guest of their own, `sourcesImpure/TypeBuilderGetNumVirtualsAndStaticVirtuals.cs`,
     not in `TypeBuilderVerifyInterfaceIsImplemented.cs`: a guest for each QCall keeps one QCall's regression from
     hiding the other's.
   - Run the controls on real .NET 10 and 11 that the plan describes, and record them in the pull request body.

#### Mutants

Run the plan's table, and add:

| mutant | killed by |
|---|---|
| `numVirtualsAndStaticVirtuals` loses its TypeDesc test, or makes it after calling `numVirtuals` | the closed `int*` refusal test |
| the tail is taken for an array from its element (`IStat[]` counted as `IStat`) | add `IStat[]` (24) to the example tests; guests 22 and 23 do not have an interface element |
| a closed instantiation's tail is empty (only definitions answered) | guest 12; the generated property |
| `methodAt`'s boundary moved by one against the count's | decision 7's boundary check, on a class |
| on .NET 11's CoreLib, one more counted for each `Task`-returning virtual | tests 3 and 4 above, on the .NET 11 case (the plan's CoreLib property cannot see this on a .NET 10 host) |
| the class constructor counted | the new guest check with a static field initialiser |

The mutant "the tail filter loses `IsStatic`" is equivalent (see the corrections); record it as such.

#### Before starting

This plan was written on top of #2298, which was still open when this was decided. Before writing code, check whether
it has merged, and rebase onto `origin/main` if it has; `TypeHandleTag.isTypeDesc` and both parked guests come from it.

#### Out of scope, on purpose

`RuntimeTypeHandle_GetInterfaceMethodImplementation` (#2290), the message of #2294, and the TypeDesc answers of
#2299. If un-parking `TypeGetInterfaceMapNoMethods.cs` meets a stop the plan did not
measure, park it on that stop and report it; do not implement the dependency in this change.

### As built

Built 2026-11-16 on top of #2298 (still open in the merge queue when this was written), in the decisions' commit
order: the lift of the test helper, the tests with their red output, the `methodAt` refactor, then the count and the
handler, and one later commit strengthening an example test (below).

**What exists.**

- `MethodTableLayout.staticVirtualsOfDefinition operation owner typeInfo` (TypeSystem, public): `[]` unless
  `typeInfo.IsInterface`, otherwise `slotsBeyondVtableOfDefinition` filtered to `IsVirtual && IsStatic`. The two
  comments decision 2 and the corrections ask for are at the `IsInterface` test and at the filter.
- `VirtualSlotLayout`'s private `staticVirtualsOf operation state target`: the dispatch over a
  `RuntimeTypeHandleTarget` decision 2 describes. `methodAt` indexes it past the vtable; its local `staticVirtualsOf`
  and its calls to `slotTableOfClosed` and `slotTableOfDefinition` are gone.
- `VirtualSlotLayout.numVirtualsAndStaticVirtuals` (public): `TypeHandleTag.isTypeDesc` first, refusing with a
  `TODO:` naming #2299; then `numVirtuals` plus the length of `staticVirtualsOf`. Its docstring carries decision 3's
  sentence.
- The handler in `Native/NativeRuntimeTypeQCall.fs`, registered in `Native/NativeQCall.fs` beside
  `RuntimeTypeHandle_GetMethodAt`, decodes with `NativeCall.qCallTypeHandleToRuntimeTypeHandleTarget` and pushes an
  `Int32`.
- `docs/divergences.md`'s entry on async variant slots names this QCall and `GetMethodAt`, with `ITask`'s
  measurement.
- Tests: `HostTypeTarget.fs` (the lifted builder) and `TestNumVirtualsAndStaticVirtuals.fs` (eight tests: the
  CoreLib property, the generated property, the per-runtime law and the named async examples on each runtime, the
  fixed shapes, the TypeDesc refusals). Guests: `sourcesPure/RuntimeTypeHandleGetNumVirtualsAndStaticVirtuals.cs`
  (28 checks), `sourcesImpure/TypeBuilderGetNumVirtualsAndStaticVirtuals.cs` (7 checks);
  `TypeGetInterfaceMapNoMethods.cs` un-parked; `TypeGetInterfaceMap.cs` re-parked on #2290 with its measured stop.

New public API: `MethodTableLayout.staticVirtualsOfDefinition` in the published `WoofWare.PawPrint.TypeSystem`
package; `VirtualSlotLayout.numVirtualsAndStaticVirtuals` in `WoofWare.PawPrint`. (`HostTypeTarget` is test code.)

**Measurements the decisions asked for.** Both derived expectations hold on real .NET 10.0.14 and 11.0.0-rc.3: the
interface with a static field initialiser and one static abstract method counts 1, and `IStat[]` counts 24. The
guests' controls compiled every perturbed copy of each guest into one program, each copy in a namespace of its own,
and ran it on both runtimes: the guest as written returns 0, and each copy with one expectation moved by one (or one
comparison inverted) returns its own check's number; 56 copies of the pure guest and 16 of the impure one, no mismatch
on either runtime. The per-runtime law holds for every interface in each CoreLib, exported or not: 167 interfaces
(50 with a static virtual) on .NET 10, 177 (57) on .NET 11. No interface breaks it.

**Deviations and places the decisions were silent.**

- *The CoreLib floor.* The decisions' "43 and 44 measured, among exported types" counted the planner's probe
  fixtures too (ten of them declare a static virtual). CoreLib's own exported interfaces with a static tail are 33 on
  the .NET 10 host, so the host property's floor is 30. The per-runtime law, over every interface, keeps the floor of
  40. The tests commit had 40 for both; the implementation commit corrected the first, and its message says so.
- *`IStat[]` and the static-initialiser interface are in the pure guest too* (checks 28 and 27), not only in the
  example test, so that the controls measured them on both runtimes. The mutant that counts the class constructor
  survived the default suite (no generated or CoreLib interface has a static field) and died only by guest check 27,
  so `IStatInit` was added to the fixed shapes in a later commit; the mutant then also dies there.
- *The re-park of `TypeGetInterfaceMap.cs`* went in the implementation commit, where its quoted stop first becomes
  true, not in the tests commit.
- *Commit 3 and the test project.* `TestNumVirtualsAndStaticVirtuals.fs` names the count, so the test project does
  not compile at the refactor commit. The refactor was checked green with that file left out of a local build:
  `TestMethodAtSlotShapes` and `TestVirtualMethodSlots` (113 of 113) and `ReflectionMethodAtSlot.cs`, unedited.
- *`staticVirtualsOf` is private* to `VirtualSlotLayout`; its only callers are there. A continuation type answers
  `[]` (its parent is a class and it has no methods of its own), as `methodAt` answered before; a closed TypeDesc
  fails, since both callers refuse one first.
- *The generator.* Interfaces have arity 0 or 1, no variance or constraints; members as the plan lists; overrides are
  of methods only (an explicit body, an instance reabstraction of an instance abstract or default method, a static
  reabstraction of a static abstract or virtual one), of any interface in the declarer's closure, and each base
  method is overridden by at most one interface in a corpus, which rules out diamonds. No closure names one
  interface with two arguments. Classes and structs implement every virtual member of every interface they inherit,
  explicitly. The pool: each interface's definition, its instantiations over `int` and `string` and an open
  construction over a class's own parameter; each class and struct, and an instantiation over `int` of a generic one;
  and an array of every closed type among them, interfaces included. Floors are half of the fixed sample:
  StaticTail 164 of 328, NonVirtualNotCounted 96 of 193, ImplementsStaticVirtualInterface 75 of 150,
  OpenConstruction 64 of 128, ReabstractionCounted 12 of 25. A `NullSlot` label is counted, not floored (0 on a
  .NET 10 host).
- *Decision 7's boundary check* is in the shared comparison, so it runs for every type of both the CoreLib and the
  generated corpora.

**Mutants.** Each run against `TestNumVirtualsAndStaticVirtuals`, `TestMethodAtSlotShapes`, `TestVirtualMethodSlots`,
the pure guests `RuntimeTypeHandleGetNumVirtualsAndStaticVirtuals.cs`, `TypeGetInterfaceMapNoMethods.cs` and
`ReflectionMethodAtSlot.cs`, and the impure guest, from a committed tree, restored after each.

| mutant | killed by |
|---|---|
| the count is `numVirtuals` alone | pure guest exit 4 (`IStat`); impure guest check 2; fixed shapes; CoreLib property; per-runtime law on both runtimes; generated property |
| static virtuals counted for every type, through a list that includes a class's static implementations | pure guest exit 20 (`SStat`); fixed shapes (`SStat` 6); CoreLib property; generated property; `TestVirtualMethodSlots` |
| the tail filter drops `static virtual` with a body | pure guest exit 5; fixed shapes; CoreLib property; per-runtime law on both; generated property; `TestVirtualMethodSlots` |
| the tail filter counts every static | pure guest exit 5; fixed shapes; CoreLib property; per-runtime law on both; generated property; `TestVirtualMethodSlots` |
| a static reabstraction not counted | pure guest exit 9; fixed shapes (`IStaticReabs` 0); generated property |
| instance non-virtual members counted | pure guest exit 6; fixed shapes (`ISealedDim` 3); generated property |
| base interfaces' methods counted (as one more for each InterfaceImpl row of an interface) | pure guest exit 7; fixed shapes; CoreLib property; per-runtime law and async examples on both; generated property |
| generic methods dropped from the tail | pure guest exit 10; CoreLib property; per-runtime law on both; generated property; `TestVirtualMethodSlots` |
| an open construction refused | pure guest (stops on the refusal, at check 13); fixed shapes (`IG<T>`); generated property |
| a generic definition refused | pure guest (at check 11); `TypeGetInterfaceMapNoMethods.cs` (`IInC<>`); CoreLib property; per-runtime law and async examples on both; generated property |
| an array counted as 0 | pure guest exit 22; fixed shapes (`IStat[]`); generated property |
| `Task`-like virtuals counted twice, on every runtime | CoreLib property on the .NET 10 host; per-runtime law and async examples on both. The guests survive: none has a `Task` member |
| `Task`-like virtuals counted twice on .NET 11's CoreLib only | per-runtime law and async examples, .NET 11 cases only |
| `methodAt` reads the tail reversed | CoreLib property; generated property; `TestVirtualMethodSlots`. The guests survive: they only count |
| `methodAt` reads a differently filtered tail (generic methods dropped) | CoreLib property; generated property; `TestVirtualMethodSlots` |
| the count one past the tail | pure guest exit 4; impure guest check 2; fixed shapes; CoreLib property; per-runtime law on both; generated property |
| the handler pushes an `Int64` | every guest that reaches the QCall: the `LibraryImport` stub reads an `Int32`, and the interpreter refuses "TODO: Int64(0)" |
| the handler pushes a native int | every guest that reaches the QCall, likewise: "TODO: NativeInt(0)" |
| decision 4 (b)'s refusal replaced by 0 | the TypeDesc refusal test |
| the TypeDesc test lost | the TypeDesc refusal test, at the closed `int*` |
| the TypeDesc test after `numVirtuals` | the TypeDesc refusal test, at `List<>`'s `T` |
| an array's tail taken from its element | fixed shapes (`IStat[]` 25); generated property; the pure guest stops at `T[]` |
| a closed instantiation's tail empty | pure guest exit 12; generated property; `TestVirtualMethodSlots` |
| `methodAt`'s boundary moved up by one | async examples on both runtimes; CoreLib property; generated property; `TestVirtualMethodSlots` (`System.Object` slot 4) |
| the class constructor counted | pure guest exit 27; fixed shapes (`IStatInit` 2), once `IStatInit` joined them |
| the tail filter loses `IsStatic` | survives: equivalent, as the corrections say |

**Suites**, after a whole-solution build with `*/obj/*/*/ref` and `refint` deleted, each its own invocation:

| suite | total | passed | skipped | failed |
|---|---|---|---|---|
| default | 5,854 | 5,815 | 39 | 0 |
| `TestCategory=Guest` | 234 | 234 | 0 | 0 |
| `TestCategory=GuestSolo` | 2 | 2 | 0 | 0 |
| `TestCategory=GuestPure` | 1,203 | 1,203 | 0 | 0 |
| `TestCategory=GuestImpure` | 302 | 302 | 0 | 0 |

The new guests also pass on .NET 11's CoreLib (`PAWPRINT_TEST_RUNTIME=Net11`), run by name.
