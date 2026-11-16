# `RuntimeTypeHandle_VerifyInterfaceIsImplemented` (issue #2270)

Status: decided and built ("As built", at the end). The study below was measured 2026-11-16 on `origin/main` at
`a66b75274`.

Scope is this one QCall, as the comment on #2270 decided while planning #2269. `Type.GetInterfaceMap` makes two more
unimplemented QCalls after it: `RuntimeTypeHandle_GetNumVirtualsAndStaticVirtuals` (#2289), then
`RuntimeTypeHandle_GetInterfaceMethodImplementation` (#2290). A guest of the success path is parked on #2289.

The probes were throwaway C# console programs, not kept (see "Probe files" at the end). Each printed one line per row
named below: what `Type.GetInterfaceMap`, or the QCall's managed wrapper called by reflection (a `direct` row), returned
or raised. Real .NET means 10.0.14, and 11.0.0-rc.3 reached by rolling the same build forward
(`DOTNET_ROLL_FORWARD=LatestMajor`, `DOTNET_ROLL_FORWARD_TO_PRERELEASE=1`). Every real-.NET result below is the same on
both runtimes; each pair of outputs was diffed and differs only in its `runtime` line. PawPrint runs used the Release App
built from `a66b75274`, on .NET 10's CoreLib, with the probe published self-contained for osx-arm64. Each claim is
marked *measured* or *read*.

## What it is

### Who reaches it

`RuntimeType.GetInterfaceMap` (`RuntimeType.CoreCLR.cs:2739-2758` on .NET 10, `:2737-2756` on .NET 11, the same text;
read) is the only caller in CoreLib:

```csharp
if (IsGenericParameter)
    throw new InvalidOperationException(SR.Arg_GenericParameter);
ArgumentNullException.ThrowIfNull(interfaceType);
RuntimeType ifaceRtType = interfaceType as RuntimeType ??
    throw new ArgumentException(SR.Argument_MustBeRuntimeType, nameof(interfaceType));
RuntimeTypeHandle ifaceRtTypeHandle = ifaceRtType.TypeHandle;
TypeHandle.VerifyInterfaceIsImplemented(ifaceRtTypeHandle);
...
if (IsSZArray && interfaceType.IsGenericType)
    throw new ArgumentException(SR.Argument_ArrayGetInterfaceMap);
int ifaceVirtualMethodCount = RuntimeTypeHandle.GetNumVirtualsAndStaticVirtuals(ifaceRtType);
```

The wrapper is `RuntimeTypeHandle.VerifyInterfaceIsImplemented(RuntimeTypeHandle)` (`RuntimeHandles.cs:621-629` on both,
read), an `internal` instance method which takes both native handles and calls the `LibraryImport`
`VerifyInterfaceIsImplemented(QCallTypeHandle, QCallTypeHandle)`, returning `void`.

Three things follow for testing.

- A type variable as `this` never reaches the QCall through `GetInterfaceMap`: the managed check throws
  `InvalidOperationException` first (measured, rows `genericparam`, `methodgenericparam`).
- On success the QCall returns nothing, and for any receiver but an SZ array `GetInterfaceMap` goes straight on to
  #2289's QCall. So through `GetInterfaceMap` a guest can see the QCall pass only for an SZ array and a generic
  interface: the managed `Argument_ArrayGetInterfaceMap` raise after it proves it returned.
- The wrapper can be called directly by reflection, without the rest of `GetInterfaceMap`. That reaches every arm,
  the type-variable arm included, and shows a pass as "no exception" (measured on real .NET, the `direct` rows; and
  under PawPrint, which reaches the QCall that way: "Unimplemented native method
  (PInvokeImpl QCall!RuntimeTypeHandle_VerifyInterfaceIsImplemented)", 9 frames out from the probe's `Verify`).

`RuntimeTypeBuilder.GetInterfaceMap` throws `NotSupportedException` before the type is created, and afterwards calls
the baked `RuntimeType`'s (`RuntimeTypeBuilder.cs:811-817`, read; measured, row `uncreated`). `ModifiedType` forwards to
its unmodified type (`ModifiedType.cs:131`, read; measured, row `modified-type`). Neither has logic of its own here.

### What the QCall does

`RuntimeTypeHandle_VerifyInterfaceIsImplemented` is `vm/runtimehandles.cpp:813-845` on .NET 10 and `:814-846` on .NET 11.
The two bodies are textually identical (read, by diffing them). In order, each raises an `ArgumentException`:

1. `typeHandle.IsGenericVariable()` (a `TypeVarTypeDesc`): `Arg_InvalidHandle`.
2. `typeHandle.IsTypeDesc()` (a byref, pointer, function pointer; a type variable was caught by 1): `Arg_NotFoundIFace`.
3. `typeHandle.IsInterface()`: `Argument_InterfaceMap`.
4. `!ifaceHandle.IsInterface()`: `Arg_MustBeInterface`. `TypeHandle::IsInterface` is `!IsTypeDesc() &&
   AsMethodTable()->IsInterface()` (`typehandle.cpp:377-382` on .NET 10, `:409` on .NET 11; read), so a TypeDesc second
   argument lands here.
5. `typeHandle.GetMethodTable()->ImplementsInterface(ifaceHandle.AsMethodTable())`, and if that fails
   `typeHandle.CanCastTo(ifaceHandle)`; if both fail, `Arg_NotFoundIFace`.

The arms are checked in that order, and the order is observable: `typeof(IOne).GetInterfaceMap(typeof(string))` raises
arm 3's message, and `typeof(int*).GetInterfaceMap(typeof(string))` arm 2's (measured, rows `order-*`).

`COMPlusThrow(kArgumentException, W("..."))` names a CoreLib resource. The messages are `Strings.resx`'s, the same on
both runtimes (read): `Arg_InvalidHandle` "Invalid handle.", `Arg_NotFoundIFace` "Interface not found.",
`Argument_InterfaceMap` "'this' type cannot be an interface itself.", `Arg_MustBeInterface` "Type passed must be an
interface.". Every raise is an `ArgumentException` with `HResult` 0x80070057 and a null `ParamName` (measured, every
row).

### What arm 5 asks

Arm 5 is `ImplementsInterface || CanCastTo`. For the receivers that reach it (a MethodTable which is not an interface)
and a target that is an interface MethodTable, it is the same relation as `CanCastTo` alone (read):

- `MethodTable::ImplementsInterface` (`methodtable.cpp:1746-1754`, `methodtable.inl:1303-1380` on .NET 10;
  `:1706-1714`, `:1314-1391` on .NET 11, the same text) scans the interface map for the exact MethodTable, refusing a
  generic interface definition (the "special marker") unless `MayHaveOpenInterfacesInInterfaceMap`.
- `TypeHandle::CanCastTo` (`typehandle.cpp:592-633`, `:631-672`) consults the cast cache, excludes `T` to
  `Nullable<T>` (which an interface target never is), and calls `MethodTable::CanCastTo` (`methodtable.cpp:1385-1430`,
  `:1346-1391`). For an array receiver and a generic interface that is `ArraySupportsBizarreInterface` (`:1433-1456`):
  SZ arrays only, the five implicit interfaces only, element compared by `CanCastParam`. Otherwise it is
  `CanCastToInterface` (`:1203-1239`): for a non-variant target, `ImplementsEquivalentInterface`, which without
  `FEATURE_TYPEEQUIVALENCE` (Windows only) is the same scan; for a variant target,
  `CanCastByVarianceToInterfaceOrDelegate` over every interface in the map, which accepts an exact match first.
- An array's interface map is `System.Array`'s, which holds no generic interface (`array.cpp:410-424`, as
  `NativeRuntimeTypeQCall.fs`' `GetInterfaces` handler records), so `ImplementsInterface` adds nothing for an array
  either.
- Neither function consults `IDynamicInterfaceCastable` or COM: `CanCastTo` is "type-based conversion rules" only
  (`methodtable.cpp:1422`).

Between .NET 10 and 11 these functions differ by one added `PRECONDITION` in `CanCastToClass`, which an interface target
never reaches (read, by diffing them).

**Measured**: over every pair of an exported CoreLib type (and the probe's fixtures) that is neither a TypeDesc nor an
interface, against every exported CoreLib interface (and the probe's), the QCall passes exactly when
`interfaceType.IsAssignableFrom(type)`. 184,752 pairs on .NET 10 (1,476 pass) and 197,100 on .NET 11 (1,520 pass),
with no disagreement and no message other than "Interface not found." from arm 5. The sweep's comparison, for each
receiver `s` and interface `i` (`Verify` is the direct call, null when the QCall passed):

```csharp
var e = Verify(s, i);
if (e != null && !(e is ArgumentException && e.Message == "Interface not found."))
    disagreements.Add($"{s} / {i}: unexpected {Describe(e)}");
else if (i.IsAssignableFrom(s) != (e == null))
    disagreements.Add($"{s} / {i}: verify={(e == null ? "passes" : "raises")}");
```

`RuntimeType.IsAssignableFrom` of two `RuntimeType`s is `RuntimeTypeHandle.CanCastTo`
(`RuntimeType.cs:279-291`, read), which is `TypeHandle.CanCastToForReflection` (`RuntimeHandles.cs:683-685`,
`RuntimeHelpers.CoreCLR.cs:1191-1202`, read): `TypeHandle::CanCastTo` plus the `T` to `Nullable<T>` rule, which an
interface target cannot meet. So the relation arm 5 asks is reflection's `IsAssignableFrom`, and
nothing narrower.

### Measurements on real .NET

The issue's five rows, as the comment on #2270 gives them, measured again: identical.

Through `GetInterfaceMap` (`"... "` is the message of an `ArgumentException`; "array" is
"Interface maps for generic interfaces on arrays cannot be retrieved.", which means the QCall passed):

| row | call | result |
|---|---|---|
| `order-interface-before-target` | `typeof(IOne).GetInterfaceMap(typeof(string))` | "'this' type cannot be an interface itself." |
| `order-typedesc-before-target` | `typeof(int*).GetInterfaceMap(typeof(string))` | "Interface not found." |
| `byref`, `fnptr` | `typeof(int).MakeByRefType()`, `typeof(delegate*<void>)` against `IComparable` | "Interface not found." |
| `pointer-over-var`, `byref-over-var` | `T*`, `T&` over `List<>`'s `T` | "Interface not found." |
| `target-genericparam`, `-pointer`, `-array`, `-struct` | `typeof(COne)` against `T`, `int*`, `IOne[]`, `int` | "Type passed must be an interface." |
| `opendef-class-target`, `openconstructed-class-target` | `typeof(COne)` against `List<>`, `List<T>` | "Type passed must be an interface." |
| `opendef-interface-source`, `openconstructed-interface-source` | `IEnumerable<>`, `IEnumerable<T>` against `IEnumerable` | "'this' type cannot be an interface itself." |
| `sz-variant` | `string[]` / `IEnumerable<object>` | array |
| `sz-width` | `int[]` / `IList<uint>` | array |
| `sz-enum` | `DayOfWeek[]` / `IList<int>` | array |
| `sz-readonly`, `sz-derived` | `int[]` / `IReadOnlyList<int>`; `Derived[]` / `IReadOnlyCollection<Base>` | array |
| `sz-over-var` | `T[]` / `IEnumerable<T>` | array |
| `sz-wrongwidth` | `int[]` / `IList<long>` | "Interface not found." |
| `sz-valuevariance` | `int[]` / `IEnumerable<object>` | "Interface not found." |
| `sz-notfive` | `int[]` / `IEquatable<int>` | "Interface not found." |
| `md-generic`, `md-over-var-generic` | `int[,]` / `IList<int>`; `T[,]` / `IList<T>` | "Interface not found." |
| `sz-open-definition` | `int[]` / `IEnumerable<>` | "Interface not found." |
| `nullable`, `nullable-notimpl` | `int?` / `IComparable`, `IDisposable` | "Interface not found." |
| `list-valuevariance` | `List<int>` / `IEnumerable<object>` | "Interface not found." |
| `contravariant-notimpl` | `CmpObj : IComparer<object>` / `IComparer<int>` | "Interface not found." |
| `invariant-notimpl` | `CInv : IInv<int>` / `IInv<uint>` | "Interface not found." |
| `listdef-marker`, `closed-marker`, `listdef-nonvariant-marker` | `List<>`, `List<string>` / `IEnumerable<>`; `List<>` / `IList<>` | "Interface not found." |
| `structinc-marker-map` | `StructInC : IInC<object>` / `IInC<>`, where `interface IInC<in T> where T : class` | "Interface not found." |
| `canon` | `System.__Canon` / `IDisposable` | "Interface not found." |
| `dyncastable` | a class implementing `IDynamicInterfaceCastable` / `IMarker` | "Interface not found." |
| `dynamic-notimpl` | a created `TypeBuilder` type / `IMarker` | "Interface not found." |
| `dynclass-imarker-map` | the dynamic-methods class (as `sourcesImpure/DynamicMethodsClassIsVisible.cs` reaches it) / `IMarker` | "Interface not found." |
| `list-variant` | `List<string>` / `IEnumerable<object>` | succeeds: one method each side |
| `classinc-marker-map` | `ClassInC : IInC<object>` / `IInC<>` | succeeds: two empty arrays |
| `dynamic-impl-marker` | a created `TypeBuilder` type implementing `IMarker` / `IMarker` | succeeds: two empty arrays |

Through the wrapper directly (`direct` rows), where "passes" means no exception:

| passes | raises |
|---|---|
| `COne` / `IOne`; `DOne : COne` / `IOne` (through a base); `CDerived : IDerived` / `IBase` (through an interface's interface); `SOne` (struct) / `IOne`; `int` / `IComparable`; `DayOfWeek` / `IComparable`; `Action` / `ICloneable`; `string` / `IEnumerable<char>` | generic parameter of a type or of a method, against `IComparable` or `string`: "Invalid handle." |
| `List<string>` / `IEnumerable<object>`; `COutString : IOut<string>` / `IOut<object>`; `CmpObj` / `IComparer<string>`; `CInObject : IIn<object>` / `IIn<string>`; the struct `SInObject` likewise | `int?` / `IComparable`; `List<int>` / `IEnumerable<object>`; `string` / `IEnumerable<object>`; `CmpObj` / `IComparer<int>`; `CInv` / `IInv<uint>`: "Interface not found." |
| `Crtp : IEquatable<Crtp>` / `IEquatable<Crtp>` | `CrtpIn : IIn<CrtpIn>`, `SCrtpIn`, `GCrtp<string>`, `GCrtp<>` against `IIn<>` (unconstrained `T`): "Interface not found." |
| `ClassInC` / `IInC<>` (the variant definition, `T : class`, on a class) | `StructInC` / `IInC<>`; `CInObject`, `SInObject` / `IIn<>`; `CInv` / `IInv<>`: "Interface not found." |
| `string[]` / `IEnumerable<object>`; `int[]` / `IList<uint>`; `int[]`, `int[,]` / `IList` | `int[]` / `IList<long>`; `int[,]` / `IList<int>`: "Interface not found." |
| `List<>` / `IEnumerable<T>` over `List<>`'s `T`; `List<>` / `IEnumerable`; `BaseG<T>` (open constructed, from `typeof(G<>).BaseType`) / `IBaseOf<T>`; `G<>` / `IBaseOf<T>` over `G<>`'s `T`; `T[]` / `IEnumerable<T>` | `List<>` / `IEnumerable<>`, `IEnumerable<string>`; `List<string>` / `IEnumerable<>`; `BaseG<T>` / `IBaseOf<>`; `T[]` / `IEnumerable<object>`: "Interface not found." |
| a created `TypeBuilder` type implementing `IMarker` / `IMarker` | the same not implementing it; `__Canon` / `IDisposable`; the `IDynamicInterfaceCastable` class / `IMarker`; the dynamic-methods class / `IMarker`: "Interface not found." |
| | `IOne` / `IOne`, `string`; `IDerived` / `IBase`; `IIInObject : IIn<object>` / `IIn<string>`: "'this' type cannot be an interface itself." |
| | `COne` / `string`; `COne` / the dynamic-methods class: "Type passed must be an interface." |
| | `int*` / `string`; `int&`, `delegate*<void>` / `IComparable`: "Interface not found." |
| `CGenInC<>` and `CGenInC<int>` (a generic class implementing `IInC<object>`) / `IInC<>` | `SGenInC<>` and `SGenInC<int>` (the same as a struct) / `IInC<>`: "Interface not found." |

Two of these are worth naming. `ClassInC` / `IInC<>` passes and `StructInC` / `IInC<>` raises: the special-marker rule,
which `isRuntimeTypeHandleTargetAssignableTo` already models and `sourcesPure/IsAssignableFromVariantInterfaceDefinition.cs`
already pins for `IsAssignableFrom`. And `ClassInC.GetInterfaceMap(typeof(IInC<>))` succeeds with two empty arrays, so a
variant interface definition can be the second argument of a map that real .NET returns.

### PawPrint on `main`

Both `GetInterfaceMap` and the direct call stop at "Unimplemented native method (PInvokeImpl
QCall!RuntimeTypeHandle_VerifyInterfaceIsImplemented)" (measured).
Nothing before it stops for an ordinary class.

### PawPrint with a throwaway of the rule (reverted)

The throwaway added a handler in `Native/NativeRuntimeTypeQCall.fs` which decodes both arguments with
`NativeCall.qCallTypeHandleToRuntimeTypeHandleTarget`, takes the arms in CoreCLR's order, answers arm 5 with
`IlMachineState.isRuntimeTypeHandleTargetAssignableTo`, and raises with `NativeHandlerResult.raiseExceptionWithMessage`.

- Every row of `rows-nosuccess` (the rows above without the three successes, 106 lines) prints the same as on real
  .NET 10 (compared line by line; the only differences are the App's own `info:` log lines).
- Every row of `extra` likewise.
- The direct rows over the dynamic-methods class likewise. Its `GetInterfaceMap` row does not
  reach the QCall under PawPrint: `RuntimeType.GetInterfaceMap`'s first line asks `IsGenericParameter`, and
  `RuntimeTypeHandle.IsGenericVariable` (`Native/NativeRuntimeTypeFCall.fs:415`) refuses the dynamic-methods class as a
  metadata query. CoreCLR answers `false` there without metadata (`IsGenericVariable` is `IsTypeDesc() && ...`, and the
  class is a MethodTable). That refusal is not this issue's; see question 8.
- The success path stops at the next QCall, as the issue says: "Unimplemented native method (PInvokeImpl
  QCall!RuntimeTypeHandle_GetNumVirtualsAndStaticVirtuals)" (at row `list-variant`).
- A sampled sweep (`sample 40`: every 40th visible CoreLib non-interface type, then the probe's fixtures, against 159
  interfaces; one line per pair) was stopped after 802 s, at 2,691 of its 8,037 pairs: the first 20 CoreLib receivers
  (`System.Byte`, `Action<,>`, `Func<...>` definitions, `ValueTuple<,,>`, enums, static classes, exception types and so
  on), 30 of them passing. Every one of the 2,691 lines is the same as real .NET 10's (compared line by line by a
  script). The fixtures at its end were already covered by the rows
  above. It also gives a cost: about 0.3 s per pair inside a guest, almost all of it the reflection call and the raise,
  which is why the property tests below call the verdict function directly instead.
- **The one divergence: a refusal.** `SGenInC<>`, a generic struct *definition* implementing the closed `IInC<object>`,
  against the variant definition `IInC<>`: real .NET raises "Interface not found."; PawPrint stops in
  `isRuntimeTypeHandleTargetAssignableTo` with "TODO: isRuntimeTypeHandleTargetAssignableTo: the open value type or
  interface ... has an interface that casts to the variant interface definition ..., but whether CoreCLR scans for it
  depends on its MayHaveOpenInterfacesInInterfaceMap flag, which PawPrint does not model for open types"
  (measured; on real .NET 10 and 11 `IsAssignableFrom` agrees with the QCall). The closed `SGenInC<int>` and both
  `CGenInC` rows agree (measured). CoreCLR sets the flag on every class,
  on a type shared by generic instantiations, and on a value type or interface only when exact interface loading had to
  retry for an ambiguity (`methodtablebuilder.cpp:9611-9617`, `:9809-9813`, read). So for an open value type the
  flag is unset unless that retry happened, and with it unset the answer is "not found" (measured for `SGenInC<>`). The refusal is shared with `IsAssignableFrom`
  (`TypeHandle_CanCastTo_NoCacheLookup`), and is question 9.

Apart from that refusal, no divergence was found between CoreCLR's arm 5 and `isRuntimeTypeHandleTargetAssignableTo`
on any measured pair.

### What PawPrint already has

- **Arm 5.** `IlMachineRuntimeMetadata.isRuntimeTypeHandleTargetAssignableTo` (`IlMachineRuntimeMetadata.fs:2186-2686`)
  is documented as, and tested as, CoreCLR's `TypeHandle::CanCastTo` over the whole `RuntimeTypeHandleTarget` union.
  For two closed types it calls `TypeAssignability.isConcreteTypeAssignableToVisiting`
  (`WoofWare.PawPrint.TypeSystem/TypeAssignability.fs`); otherwise it walks the parent chain and the interface map
  (closed under each interface's own interfaces), with `CanCastByVarianceToInterfaceOrDelegate`, the array rules
  (`ArraySupportsBizarreInterface`, `ArrayIsInstanceOf`, `CanCastParam`), the special-marker rule, and a TODO refusal
  where a match on an open value type or open interface would depend on `MayHaveOpenInterfacesInInterfaceMap`. Arm 3
  keeps interface receivers away from that refusal, but an open value type still reaches it (below, the one divergence
  measured). `TypeHandle_CanCastTo_NoCacheLookup`
  (`NativeRuntimeTypeQCall.fs:165-212`) already answers with it. `TestCompositeTypeVariableCasts.fs` checks it against
  the host's `IsAssignableFrom` for every pair of a pool with at least one open side.
  How it treats each case where a difference from CoreCLR might hide:
  - *Generic variance*: modelled, with `TypeHandlePairList`'s cycle rule; value-type arguments refused as
    `IsBoxedAndCanCastTo` does (measured agreement: `list-valuevariance`, `sz-valuevariance`).
  - *Arrays*: modelled, including the primitive-width and enum rules (`sz-width`, `sz-enum`, `sz-wrongwidth`) and the
    MD-array refusal (`md-generic`).
  - *TypeDesc kinds*: a byref, pointer or function pointer casts to no interface, so arm 5 would also say "not found";
    but arm 2 must still come before arm 4 (`order-typedesc-before-target`), so the handler classifies them itself.
  - *Interfaces implemented only through a base type*, or only through another interface: modelled (`DOne`,
    `CDerived`).
  - *Generic type definitions and open constructed types*: modelled as receivers (`List<>`, `BaseG<T>`, `T[]` agree);
    as targets, a variant definition is the special marker (`ClassInC` passes, `StructInC` raises).
  - *`__Canon`*: an ordinary CoreLib class with no interfaces to both (`canon`).
  - *Nullable*: the `T` to `Nullable<T>` rule cannot apply to an interface target; `Nullable<int>` declares no
    interface, so it raises (`nullable`).
  - *Dynamic `TypeBuilder` types*: a created type is an ordinary target, and its `InterfaceImpl` rows are in the emit
    scope (`TypeBuilder_AddInterfaceImpl`, `EmitScopeDefinitions.addInterfaceImpl`); `dynamic-impl` passes and
    `dynamic-notimpl` raises.
  - *`IDynamicInterfaceCastable`*: not consulted, as in CoreCLR (`dyncastable`).
- **Not arm 5.** The interpreter's own cast path (`UnaryMetadataObjectOps.isObjectInstanceOf`, under `castclass` and
  `isinst`; `NativeCastHelpers.objIsInstanceOfCore`, under `CastHelpers.IsInstanceOf_NoCacheLookup`) is *object*
  castability: closed types only, with the `T` to `Nullable<T>` rule, and the second refuses for a type implementing
  `IDynamicInterfaceCastable` rather than answering. The QCall asks *type* castability, of a receiver that may be open,
  and CoreCLR answers `IDynamicInterfaceCastable` receivers with a plain "not found".
- **The interface map.** The `RuntimeTypeHandle_GetInterfaces` handler (`NativeRuntimeTypeQCall.fs:2466-2751`) builds
  the map, but refuses a generic type definition whose interface rows mention its parameters: `typeof(List<>)
  .GetInterfaces()` stops with "TODO: RuntimeTypeHandle.GetInterfaces for open generic typedef ... only fully-closed
  interface rows are supported today" (measured). `isRuntimeTypeHandleTargetAssignableTo` builds
  its own map through `declaredInterfaceTargets` and answers `List<>` (measured).
- **IsTypeDesc.** Three spellings of one rule: `TypeHandleTag.forTarget` (`NativeIntSource.fs:602`, the tag bit, which
  others call "the classifier"), `NativeCastHelpers.isTypeDescTarget` (private, "must stay consistent with it"), and
  inline matches in several handlers.
- **IsInterface of a target.** Two private copies, `IlMachineRuntimeMetadata.isInterfaceTarget` (`:2034`, through
  `nominalInstantiation`) and `NativeCastHelpers.isInterfaceTarget` (`:48`). They agree on every case: only a closed
  `Concrete` handle, a generic definition or an open construction can be an interface.
- **Decoding.** `NativeCall.qCallTypeHandleToRuntimeTypeHandleTarget` reads a `QCallTypeHandle`'s `_handle`, and
  `RuntimeTypeHandle_IsVisible` uses it.
- **Raising.** `NativeHandlerResult.raiseExceptionWithMessage` with `BaseClassTypes.ArgumentException`: the dispatcher
  runs the parameterless constructor (which sets `HResult` 0x80070057 and leaves `ParamName` null) and writes
  `_message`. Every native handler which raises with a CoreLib or `mscorrc` message spells the English text in place
  (for example `AssemblyNative_InternalLoad`'s "String cannot have zero length.").

## Surprises against the issue's description

1. Arm 5 is `IsAssignableFrom`, measured over 185,000 pairs per runtime. `ImplementsInterface` is a fast path that
   adds no pair `CanCastTo` would refuse.
2. The variant row cannot be seen through `GetInterfaceMap` without #2289: on success the next QCall stops. A guest sees
   it by calling the wrapper directly, as the probe does. The only passes `GetInterfaceMap` shows today are SZ arrays
   against generic interfaces.
3. Arm 1 is reachable, by the direct call only (`Invalid handle.`).
4. A variant interface *definition* can pass, on a class (`ClassInC` / `IInC<>`), and real .NET then returns a map.
5. `typeof(List<>).GetInterfaces()` stops under PawPrint, so neither the guest nor the property test may build an open
   type's interface by asking for its interfaces. `MakeGenericType` over the definition's own parameter gives the same
   type (measured: the direct row over it passes on both).
6. The dynamic-methods class cannot reach this QCall through `GetInterfaceMap` under PawPrint, for a reason outside it
   (question 8).
7. An open generic struct against a variant interface definition is refused by the existing cast rule, where real .NET
   answers "not found" (question 9).

## Options

### Decision 1: where the rule lives

Every option decodes both arguments, takes the arms in CoreCLR's order and raises the same five messages. They differ
in where the classification is and what can test it.

#### A. Inline in the handler

The handler in `Native/NativeRuntimeTypeQCall.fs` classifies the two targets in a `match`, calls
`isRuntimeTypeHandleTargetAssignableTo` for arm 5, and raises. The throwaway is this option.

- Tested only through guests, and through a handler unit test that would need a `NativeCallContext`.
- Blast radius: one handler arm and its registration in `NativeQCall.fs`. It needs `isTypeDesc` and `isInterface`,
  either as a third and fourth private copy or by making two existing ones visible.
- Reversibility: total.
- Information kept: none; the arm taken exists only as the message raised.

#### B. A verdict as data, which the handler interprets

A function beside `isRuntimeTypeHandleTargetAssignableTo` in `IlMachineRuntimeMetadata.fs` returns which arm CoreCLR
takes, without raising:

```fsharp
/// Which arm of CoreCLR's `RuntimeTypeHandle_VerifyInterfaceIsImplemented` a pair of handles takes.
[<RequireQualifiedAccess>]
type InterfaceVerification =
    | Implemented
    /// Arm 1: the receiver is a type variable.
    | ReceiverIsTypeVariable
    /// Arm 2: the receiver is a byref, pointer or function pointer.
    | ReceiverIsTypeDesc
    /// Arm 3.
    | ReceiverIsInterface
    /// Arm 4.
    | TargetIsNotInterface
    /// Arm 5 failed: neither `ImplementsInterface` nor `CanCastTo`.
    | NotImplemented

IlMachineRuntimeMetadata.verifyInterfaceIsImplemented :
    ILoggerFactory -> BaseClassTypes<DumpedAssembly> -> IlMachineState
    -> RuntimeTypeHandleTarget -> RuntimeTypeHandleTarget -> IlMachineState * InterfaceVerification
```

The handler decodes, calls it, and maps each case to a message (`ReceiverIsTypeDesc` and `NotImplemented` to the same
one). `isInterfaceTarget` and `nominalInstantiation` are already in that module; `isTypeDesc` comes from question 3.

- Testable as a function against the host over a pool of targets, without running a guest: the host's verdict for the
  same pair is the direct call by reflection (test design, below). That is the only option under which the rule has a
  property test.
- Blast radius: one new public type and function in the `WoofWare.PawPrint` package (flag it in the PR body), plus the
  handler arm. `isRuntimeTypeHandleTargetAssignableTo` beside it is already public.
- Reversibility: high. Folding it into the handler is mechanical.
- Information kept: which arm, including the two arms that share a message. #2290's plan may want "is this receiver
  one `GetInterfaceMap` accepts" without raising.

#### C. The closed-type half in `WoofWare.PawPrint.TypeSystem`

`TypeAssignability` gains an "implements interface" function over `ConcreteTypeHandle` (an interface-map scan, then the
variance and array rules), and the handler uses it for a closed receiver and `isRuntimeTypeHandleTargetAssignableTo`
for an open one.

- The lowest project that can hold the closed half. The analyser could ask it.
- But `RuntimeTypeHandleTarget` is the interpreter's (`ManagedPointerSource.fs`), so the open half cannot move, and the
  rule is split across two projects by whether the receiver is closed. The closed half would also be a second spelling
  of what `isConcreteTypeAssignableTo` already answers (measured: arm 5 is `IsAssignableFrom`).
- Blast radius: a new public function in the TypeSystem package and its tests.
- Reversibility: moderate; new API in the TypeSystem package, which the interpreter and the analyser both consume.
- Information kept: as A.

**Recommended: B.** The classification is the whole of the QCall's own logic, and B is the only option under which it
is tested against the host over generated types. C splits one rule by a property the rule does not care about.

### Decision 2: what answers arm 5

- **(i) `isRuntimeTypeHandleTargetAssignableTo` alone**, that is `CanCastTo`. Justified by the reading above and by the
  sweep: `ImplementsInterface` adds nothing. Already tested against the host for open pairs, and through
  `isConcreteTypeAssignableTo` for closed ones. Measured to agree on every probed row under PawPrint.
- **(ii) `ImplementsInterface`, modelled as membership of the interface map, then `CanCastTo`**: a literal
  transliteration. The map would come from the `GetInterfaces` handler's walk, which refuses `List<>` (surprise 5), so
  (ii) stops where (i) answers correctly; and since `ImplementsInterface` implies `CanCastTo`, a mutant which drops the
  first half survives every test.
- **(iii) The interpreter's object-cast path** (`isObjectInstanceOf`, `objIsInstanceOfCore`): closed types only, adds
  the `Nullable<T>` boxing rule, and refuses an `IDynamicInterfaceCastable` receiver which CoreCLR answers.

**Recommended: (i)**, with the reason (the sweep) in a comment at the call.

### Decision 3: the TypeDesc and interface classifiers

- **(a) One shared `IsTypeDesc`.** Add `RuntimeTypeHandleTarget.isTypeDesc` defined as `TypeHandleTag.forTarget target
  <> 0L`, or move `NativeCastHelpers.isTypeDescTarget` to `NativeIntSource.fs` beside `forTarget` and make
  `NativeCastHelpers` use it. The verdict function uses that, and `IlMachineRuntimeMetadata.isInterfaceTarget` (already
  in scope for B).
- **(b) Local copies** in the verdict function or the handler, as `NativeCastHelpers` did.

(a) removes a third copy of a rule whose copies are documented as having to agree, at the cost of touching
`NativeCastHelpers`. (b) is smaller and adds a copy. **Recommended: (a)**, the definition through `forTarget`, since
other code already names `forTarget` as the classifier. `isInterfaceTarget` stays private to `IlMachineRuntimeMetadata`
under B; under A it would have to be made public.

### Decision 4: how the arguments are decoded

- **(a) `NativeCall.qCallTypeHandleToRuntimeTypeHandleTarget` for both**, which fails loudly on a handle that is not a
  `TypeHandlePtr`. A null handle cannot arrive: the managed wrapper takes `GetNativeHandle()` of two non-null
  `RuntimeType`s.
- **(b) The `...Option` variant**, treating an absent handle as a case to answer. CoreCLR dereferences it and crashes,
  so there is no answer to model.

**Recommended: (a).**

### Decision 5: the messages

- **(a) The English text of `Strings.resx` written in the handler**, through `raiseExceptionWithMessage`, with the
  resource name beside each in a comment. Every native raise does this today.
- **(b) Ask the guest's CoreLib for the resource** (`SR.GetResourceString` by name), as CoreCLR's
  `EEResourceException` does, so a guest with another `CurrentUICulture` and satellite assemblies would see their
  text. Nothing in PawPrint calls back into managed code to build a message, and every other raise would want the same.
- **(c) No message**, as `RuntimeTypeHandle_GetMethodAt`'s out-of-range raise does today, which gives "Value does not
  fall within the expected range.". Wrong against the measured text.

**Recommended: (a).** (b) is a change for every native raise at once.

## Questions for the decider

1. **Where does the rule live: A, B or C?** Recommended: B, a verdict union beside
   `isRuntimeTypeHandleTargetAssignableTo`, interpreted by the handler.
2. **What answers arm 5?** Recommended: (i), `isRuntimeTypeHandleTargetAssignableTo` alone, on the evidence that the
   QCall's verdict is `IsAssignableFrom` over 185,000 pairs on each runtime.
3. **Does this PR make `IsTypeDesc` one shared function?** Recommended: yes, `RuntimeTypeHandleTarget.isTypeDesc` through
   `TypeHandleTag.forTarget`, with `NativeCastHelpers` switched to it in the same PR.
4. **Do `ReceiverIsTypeDesc` and `NotImplemented` stay separate cases although they raise the same message?**
   Recommended: yes. The property test checks both against the host's single message, and the separate cases cost one
   line in the handler.
5. **May the guest call the internal wrapper `RuntimeTypeHandle.VerifyInterfaceIsImplemented` by reflection?** It is
   the only way a guest sees a pass for a non-array receiver before #2289, and the only way to reach arm 1. Precedent:
   `ReflectionStripMethodInstantiationOpenDefinition.cs` and `DynamicMethodsClassIsVisible.cs` reach CoreLib internals by
   name. Its signature is the same on .NET 10 and 11 (read). Recommended: yes, with a first check that the method was
   found, so a rename fails with its own exit code.
6. **One parked success-path guest or two?** With #2289 alone, a map over an interface without methods completes (two
   empty arrays, measured on real .NET); a map over one with methods then stops at #2290. Recommended: two parked guests,
   one of interfaces without methods (`IMarker`, `IInC<>` on `ClassInC`) parked on #2289, and one with methods (`IOne`,
   `List<string>` / `IEnumerable<object>`) parked on #2289 with its reason naming #2290 as the next stop. Then #2289's
   PR un-parks the first and re-parks the second.
7. **Does the property test also run over CoreLib's types?** The host side is cheap (the sweep runs in seconds); the
   PawPrint side is not yet measured outside a guest. Recommended: yes, over every exported non-interface CoreLib type
   against every exported CoreLib interface, falling back to a fixed stride if it costs more than a few seconds.
8. **Should the two gaps outside this QCall become issues?** Neither is fixed by this plan.
   - `RuntimeTypeHandle.IsGenericVariable` refuses the dynamic-methods class (`NativeRuntimeTypeFCall.fs:415`), so
     `GetInterfaceMap`, and any other API that asks `IsGenericParameter` first, stops on it; CoreCLR answers `false`.
   - `RuntimeTypeHandle_GetMethodAt`'s out-of-range raise (`NativeRuntimeTypeQCall.fs:2316-2318`) has no message;
     CoreCLR's is `Arg_ArgumentOutOfRangeException`, "Specified argument was out of the range of valid values.".
   Recommended: file both, each naming its measurement.
9. **Does this PR teach `isRuntimeTypeHandleTargetAssignableTo` the open-value-type case of
   `MayHaveOpenInterfacesInInterfaceMap`?** Options: (a) leave the refusal, which is a stop and not a wrong answer, and
   which `IsAssignableFrom` shares; (b) answer "no scan" for an open value type unless the retry would have set the
   flag, which needs a model of `MethodTableBuilder`'s exact-interface-loading ambiguity; (c) answer "no scan" for
   every open value type, wrong only on that ambiguity. Recommended: (a) in this PR, with the property test's
   generator steering clear of the shape and one example test pinning that it is refused (so that whoever answers it
   sees the test change). (c) is a small follow-up if a consumer needs it; it would need its own probe of an ambiguous
   struct to know what it gets wrong.

## Test design

Write the tests and guests first, in a commit of their own, and record the failing output ("Unimplemented native method
(PInvokeImpl QCall!RuntimeTypeHandle_VerifyInterfaceIsImplemented)" for the guests, and the compile failure of the
property test) before the verdict function or the handler exists. Run the default suite and the four Guest categories as
separate invocations.

### Property tests (with B)

In `WoofWare.PawPrint.Test`, as `TestInterfaceVerification.fs`, reusing `TestCompositeTypeVariableCasts.fs`' way of
building a pool twice, as PawPrint targets and as host `Type`s over the same image.

The host's verdict for a pair is the direct call: `RuntimeTypeHandle.VerifyInterfaceIsImplemented(RuntimeTypeHandle)`
by reflection on `source.TypeHandle`, with no exception meaning `Implemented` and the inner `ArgumentException`'s message
naming the arm (`"Interface not found."` meaning `ReceiverIsTypeDesc` or `NotImplemented`). The property: PawPrint's
verdict maps to the host's message, and every exception the host raises is an `ArgumentException` with `HResult`
0x80070057 and a null `ParamName`.

1. **Generated corpora (FsCheck).** Generate a small C# assembly: up to four interfaces, of arity 0 to 2, each parameter
   `in`, `out` or invariant, optionally `where T : class`, each extending up to two of the earlier ones over its own
   parameters or closed arguments; up to four classes and structs, of arity 0 or 1, each class with an optional base
   among the earlier classes, each implementing up to three interface instantiations over the argument alphabet
   `object`, `string`, `int`, its own `T`, and itself (the CRTP shape). Compile with Roslyn; load the bytes into a
   collectible `AssemblyLoadContext` on the host and read them into a `DumpedAssembly`. The pool is each type's
   definition, its instantiations over `object`, `string` and `int` (where the constraint admits them), its generic
   parameters, `T[]`, `T[,]`, `T*` and `T&` over one parameter, `int[]`, `string[]`, `int?` and a few CoreLib interfaces
   (`IEnumerable<object>`, `IList<int>`, `IComparable`). Check every ordered pair. Count labels per verdict over
   `CoverageSample`'s fixed sample and assert a floor for each of the six cases, about half of what the sample reaches,
   as `TestExportedTypeSelection.fs` does (on #2269's branch).
   Under question 9 (a), the property skips a pair whose receiver is an open value type and whose target is a variant
   interface definition, and counts the skips; an example test pins that PawPrint refuses `SGenInC<>` / `IInC<>` with
   the `MayHaveOpenInterfacesInInterfaceMap` TODO, and that the host raises "Interface not found." for it.
2. **CoreLib** (if question 7 says so). Every exported CoreLib type that is not an interface (TypeDescs excluded, as no
   exported type is one) against every exported CoreLib interface, checked the same way. 184,752 pairs on .NET 10 with
   1,476 passes, on the host.

`TestCompositeTypeVariableCasts.fs` already covers `isRuntimeTypeHandleTargetAssignableTo` for open pairs against
`IsAssignableFrom`; these tests cover the classification in front of it, the order of the arms, and closed pairs.

### Guests

**`sourcesPure/TypeVerifyInterfaceIsImplemented.cs`**: each check returns its own exit code, and each runs without the
success path (no check can stop at #2289). A check through `GetInterfaceMap` catches the exception and compares its
exact type, `HResult`, `Message` and `ParamName`; a direct check calls the wrapper by reflection and compares the inner
exception the same way, or expects none. The fixture types are the probe's.

| # | check | expected |
|---|---|---|
| 1 | the wrapper is found by reflection | not null |
| 2 | `COne.GetInterfaceMap(IMarker)` | "Interface not found." |
| 3 | `COne.GetInterfaceMap(string)` | "Type passed must be an interface." |
| 4 | `IOne.GetInterfaceMap(IOne)` | "'this' type cannot be an interface itself." |
| 5 | `int*.GetInterfaceMap(IDisposable)` | "Interface not found." |
| 6 | `int[].GetInterfaceMap(IEnumerable<int>)` | the array message (the QCall passed) |
| 7 | `IOne.GetInterfaceMap(string)` | "'this' type ..." (arm 3 before arm 4) |
| 8 | `int*.GetInterfaceMap(string)` | "Interface not found." (arm 2 before arm 4) |
| 9 | `int&` and `delegate*<void>` against `IComparable` | "Interface not found." |
| 10 | `T*` over `List<>`'s `T` against `IComparable` | "Interface not found." |
| 11 | `COne.GetInterfaceMap(List<>)` | "Type passed must be an interface." |
| 12 | `IEnumerable<>.GetInterfaceMap(IEnumerable)` | "'this' type ..." |
| 13 | `string[]` / `IEnumerable<object>` | the array message (variance through `CanCastParam`) |
| 14 | `int[]` / `IList<uint>` and `DayOfWeek[]` / `IList<int>` | the array message |
| 15 | `T[]` / `IEnumerable<T>` | the array message |
| 16 | `int[]` / `IList<long>`, `int[]` / `IEnumerable<object>`, `int[]` / `IEquatable<int>`, `int[,]` / `IList<int>` | "Interface not found." |
| 17 | `int?` / `IComparable` | "Interface not found." |
| 18 | `List<int>` / `IEnumerable<object>`; `CInv` / `IInv<uint>` | "Interface not found." |
| 19 | `List<>` / `IEnumerable<>`; `StructInC` / `IInC<>` | "Interface not found." |
| 20 | the `IDynamicInterfaceCastable` class / `IMarker`; `__Canon` / `IDisposable` | "Interface not found." |
| 21 | `T.GetInterfaceMap(IComparable)` | `InvalidOperationException` (managed, before the QCall) |
| 22 | direct: `COne` / `IOne`; `DOne` / `IOne`; `CDerived` / `IBase`; `SOne` / `IOne` | passes |
| 23 | direct: `List<string>` / `IEnumerable<object>` (the variant row) | passes |
| 24 | direct: `CmpObj` / `IComparer<string>`; `Crtp` / `IEquatable<Crtp>` | passes |
| 25 | direct: `ClassInC` / `IInC<>`; `CGenInC<>` / `IInC<>` | passes |
| 26 | direct: `List<>` / `IEnumerable<T>` over its `T` (built with `MakeGenericType`); `BaseG<T>` / `IBaseOf<T>` | passes |
| 27 | direct: `List<>`'s `T` against `IComparable`, and a method's type parameter | "Invalid handle." |

Rows that group several calls return the same code for each; split them if a failure would be ambiguous to read.

**`sourcesImpure/TypeBuilderVerifyInterfaceIsImplemented.cs`**, registered in `TestImpureCases.fs` with dynamic code
enabled: a created type without `IMarker` raises "Interface not found." through `GetInterfaceMap`; a created type with
it passes directly; an uncreated `TypeBuilder` raises `NotSupportedException` (managed); the dynamic-methods class,
reached as `DynamicMethodsClassIsVisible.cs` reaches it, raises "Interface not found." directly against `IMarker` and
"Type passed must be an interface." as the second argument. Each assembly gets its own name: PawPrint refuses two
dynamic assemblies with one name ("TODO: AppDomain_CreateDynamicAssembly: ... would share its identity", measured).

**Parked: `sourcesPure/TypeGetInterfaceMap.cs`** (and its sibling, if question 6 says two). `COne.GetInterfaceMap(IOne)`:
one method each side, `InterfaceMethods[0].Name == "M"`, `TargetMethods[0].DeclaringType == typeof(COne)`;
`CMarker.GetInterfaceMap(IMarker)` and `ClassInC.GetInterfaceMap(IInC<>)`: two empty arrays;
`List<string>.GetInterfaceMap(IEnumerable<object>)`: one method each side. Parked in `TestPureCases.fs` on #2289 with the
measured message "Unimplemented native method (PInvokeImpl QCall!RuntimeTypeHandle_GetNumVirtualsAndStaticVirtuals)"
and "verified to exit 0 on real .NET 10 and 11".

Controls on real .NET: before trusting a guest, run it on real .NET 10 and 11 once as written (exit 0) and once per
perturbed expectation, each of which must exit with its own check's number: for each message check, the next arm's
message; for each pass, an expected raise; for each raise, an expected pass; the managed `InvalidOperationException`
expected as an `ArgumentException`; a non-null `ParamName` expected.

## Mutants

Each must turn some test red. Run each against the property tests and the guests.

| mutant | killed by |
|---|---|
| do nothing (the planning throwaway on #2270) | guest 2; property 1 |
| arm 1 folded into arm 2 (a type variable is "not found") | guest 27; property 1 |
| arms 3 and 4 swapped | guest 7; property 1 |
| arm 4 before arm 2 | guest 8; property 1 |
| arrays treated as TypeDescs | guests 6, 13 to 15 |
| an open definition or open construction treated as not an interface | guest 12 |
| an open definition receiver refused or treated as a TypeDesc | guest 26; property 1 |
| arm 5 as an exact match on the declared interfaces only (no base, no inherited interfaces) | guest 22 |
| arm 5 without variance | guests 23, 24, 25, 13; property 1 |
| arm 5 with variance over value-type arguments | guests 16, 18; property 1 |
| the special marker accepted on a struct | guest 19; property 1 |
| the special marker never accepted | guest 25; property 1 |
| arm 5 through the object-cast path (`Nullable<T>` rule, `IDynamicInterfaceCastable` refusal) | guest 20 stops at the refusal |
| arm 5 through `TypeAssignability.isConcreteTypeAssignableTo` only (open receivers refused) | guests 15, 26 |
| arm 5 through the `GetInterfaces` walk (decision 2 (ii)) | guest 26 stops at "only fully-closed interface rows are supported" |
| no message (`raiseException`) | every message check |
| `InvalidOperationException` or `TypeLoadException` instead of `ArgumentException` | every raise check |
| `ParamName` set (for example to `interfaceType`) | every raise check |
| `ReceiverIsTypeDesc` mapped to "Type passed must be an interface." | guests 5, 8, 9 |
| `ImplementsInterface` modelled in front of `CanCastTo` and then the `CanCastTo` half removed | guests 13 to 15, 23 to 25 |
| `ImplementsInterface` in front of `CanCastTo`, and the `ImplementsInterface` half removed | equivalent: `CanCastTo` implies it (measured over the sweep). Record as equivalent. |
| arm 2 removed, so a TypeDesc receiver falls through to arms 3 to 5 | guest 8 (arm 4's message); guests 5, 9, 10 still pass, since arm 5 refuses a TypeDesc too |

## Probe files

The probes were not kept. One console program, built for .NET 10 and run as is and rolled forward to .NET 11, and
published self-contained for PawPrint's runs, had a mode per group of rows: `rows` (and `rows-nosuccess`, without the
three rows that pass), `extra`, `dynclass`, `openstruct`, `sweep` (the comparison quoted under "What arm 5 asks"),
`sample <k>`, `one-direct` and `one-map`. Its fixture types are the ones the guests built from this plan declare, and its
direct call is their `Verify` helper:

```csharp
static readonly MethodInfo VerifyMethod = typeof(RuntimeTypeHandle).GetMethod(
    "VerifyInterfaceIsImplemented", BindingFlags.Instance | BindingFlags.NonPublic,
    null, new[] { typeof(RuntimeTypeHandle) }, null);

static Exception Verify(Type t, Type i)
{
    try { VerifyMethod.Invoke(t.TypeHandle, new object[] { i.TypeHandle }); return null; }
    catch (TargetInvocationException e) { return e.InnerException; }
}
```

The throwaway handler was option A of decision 1, below.

## Decisions

Decided 2026-11-16, by the decider the owner delegated to. Before deciding I read the QCall and everything it calls
in both pinned runtime sources, read the PawPrint code each option touches, and spot-checked the planner's logs
against the plan's tables. The plan's account of CoreCLR is right. Three of its claims about PawPrint are wrong or
incomplete; they are under "Corrections to the study" below, and decisions 3 and 8 depend on them.

Four issues come out of this plan: #2293,
#2294, #2295 and
#2296. None of them is fixed by this change.

### 1. Where the rule lives: B

A verdict as data, beside `isRuntimeTypeHandleTargetAssignableTo`, which the handler interprets.

- The type is `InterfaceVerification`, with the plan's six cases, declared inside `IlMachineRuntimeMetadata` just
  above the function (the module already declares types of its own there).
- The function is `IlMachineRuntimeMetadata.verifyInterfaceIsImplemented`, with the plan's signature. Name its two
  target parameters `receiver` and `interfaceType`, in the QCall's order. Add the alias
  `IlMachineState.verifyInterfaceIsImplemented`, as the cast function has, and call it through the alias from the
  handler.
- The function takes the arms in CoreCLR's order and calls the cast function only in arm 5. Arms 1 to 4 must not
  call it: it refuses some shapes that arms 1 to 4 answer.
- The handler decodes both arguments, calls the function, and is then one `match` over the six cases:
  `Implemented` completes, and each of the other five raises `ArgumentException` with its message. Write the
  resource name beside each message in a comment.

Why not A: the classification is the whole of this QCall's own logic, and under A nothing can test it against the
host without running a guest at about 0.3 s a pair. Why not C: the measured relation is `IsAssignableFrom`, which
`isConcreteTypeAssignableTo` already answers for closed types, so C would add a second spelling of that in the
TypeSystem package and still leave the open half in the interpreter.

The new public names (`InterfaceVerification`, `verifyInterfaceIsImplemented`, and `TypeHandleTag.isTypeDesc` from
decision 3) are additions to the published `WoofWare.PawPrint` package. Say so in the pull request body.

### 2. What answers arm 5: (i), the cast function alone

I checked the reading in both runtimes. For an interface target, `ImplementsInterface` accepts nothing that
`CanCastTo` refuses: for a target without variance `CanCastToInterface` is `ImplementsEquivalentInterface`, which
makes the same marker check and then the same `ImplementsInterfaceInline` scan; for a variant target
`CanCastByVarianceToInterfaceOrDelegate` accepts an identical type first and has its own shortcut for the
special-marker entry that the second scan of `ImplementsInterfaceInline` accepts; and an array's map is
`System.Array`'s. The sweep (184,752 pairs on .NET 10 and 197,100 on .NET 11) agrees.

Put the reason in a comment at the call: CoreCLR's first check is a fast path which accepts nothing the second
refuses, and say where that was measured. Do not model `ImplementsInterface` separately.

### 3. One shared `IsTypeDesc`: yes, as `TypeHandleTag.isTypeDesc`

The plan's name, `RuntimeTypeHandleTarget.isTypeDesc` defined through `TypeHandleTag.forTarget`, cannot be built:
the `RuntimeTypeHandleTarget` module is in `ManagedPointerSource.fs`, which is compiled before `NativeIntSource.fs`,
where `forTarget` lives. So:

- Add `TypeHandleTag.isTypeDesc (target : RuntimeTypeHandleTarget) : bool`, defined as `forTarget target <> 0L`,
  directly after `forTarget`. Its docstring says it is CoreCLR's `TypeHandle::IsTypeDesc`, and that a type variable
  is a TypeDesc too.
- Delete `NativeCastHelpers.isTypeDescTarget` and use the shared function there.
- The verdict function uses it for arm 2, after arm 1 has matched the two type-variable cases by name.

The two existing spellings do not agree on every case, as the plan says they do. For
`Closed (ConcreteTypeHandle.Continuation _)`, `isTypeDescTarget` answers `false` and `forTarget` refuses with
`ContinuationType.unasked`. The switch is still safe: in `objIsInstanceOfCore` the next step after a `false` is
`isRuntimeTypeHandleTargetAssignableTo`, which refuses a continuation on either side with the same `unasked`
refusal. So the switch changes which refusal a continuation cast target meets, and no answer. Check that by reading
`objIsInstanceOfCore` again before deleting the copy, and say it in the commit message.

Make this its own commit, before the commit that adds the verdict function, so that a reviewer can read it as a
change that preserves behaviour. Do not sweep the other handlers which match the TypeDesc shapes inline, and leave
the two private `isInterfaceTarget` copies as they are: `IlMachineRuntimeMetadata`'s own is in scope for the verdict
function, and neither needs to become public for this change.

### 4. `ReceiverIsTypeDesc` and `NotImplemented` stay separate: yes

They are two arms of CoreCLR's function, and their order relative to arm 4 is observable. Since the host raises one
message for both, no comparison with the host can tell them apart, so the plan's property test does not pin the
distinction. Add one example test that does (under "Tests", below): without it, a verdict function that never
returns `ReceiverIsTypeDesc` passes everything else.

### 5. The guest may call the internal wrapper by reflection: yes

It is the only way a guest sees arm 1 at all, and the only way it sees a pass for anything but an SZ array before
#2289. I confirmed the wrapper's text is the same on .NET 10 and 11.

- `RuntimeTypeHandle` has two methods of that name: the private static `LibraryImport` and the internal instance
  wrapper. Look the wrapper up with `BindingFlags.NonPublic | BindingFlags.Instance`, a null binder, and the
  parameter types `{ typeof(RuntimeTypeHandle) }`, so that the lookup is not ambiguous and a change of signature
  returns null.
- Split the active guest in two, so that a rename inside CoreLib breaks one guest and leaves the public contract
  tested:
  - `sourcesPure/TypeGetInterfaceMapRefusals.cs`: the checks which go through `Type.GetInterfaceMap` (the plan's
    checks 2 to 21, and the additions below). It uses public API only.
  - `sourcesPure/RuntimeTypeHandleVerifyInterfaceIsImplemented.cs`: the checks which call the wrapper (the plan's
    checks 1 and 22 to 27, and the additions below). Its first check is that the wrapper was found.
- Every call gets its own exit code. Do not group several calls under one code.

### 6. Two parked guests

- `sourcesPure/TypeGetInterfaceMapNoMethods.cs`: `CMarker.GetInterfaceMap(IMarker)` and
  `ClassInC.GetInterfaceMap(IInC<>)`, each two empty arrays, with `TargetType` and `InterfaceType` checked. Parked on
  #2289.
- `sourcesPure/TypeGetInterfaceMap.cs`: `COne.GetInterfaceMap(IOne)` and
  `List<string>.GetInterfaceMap(IEnumerable<object>)`, as the plan describes. Parked on #2289, with a reason that
  says its next stop after #2289 is #2290. Keep it to the plan's few assertions; the plan for #2290 will design the
  real tests of the map's contents.

The first can be un-parked by #2289 alone, which is what makes that issue's change testable end to end. Each reason
quotes the measured stop, "Unimplemented native method (PInvokeImpl
QCall!RuntimeTypeHandle_GetNumVirtualsAndStaticVirtuals)". The suite's own test of parked cases runs both on real
.NET, which is the check that they exit 0 there.

### 7. The property test also runs over CoreLib: yes, with a measured budget

The host sweep shows that CoreCLR's QCall is `IsAssignableFrom`. It does not show that PawPrint's function agrees
with it over CoreLib: the planner's PawPrint run compared 2,691 pairs from 20 receivers. So a sweep of PawPrint's
verdict against the host over CoreLib is a new measurement, and worth having.

- Pair the two sides by metadata token over `HostImage.coreLibPath`, which is the image the host itself runs. A
  type without type parameters is its closed target, and a generic type is its definition. Build each target the
  way the reflection handlers do, so that it is the target a guest's `typeof` would give.
- Receivers: every exported type which is not an interface. Targets: every exported interface. Also add a small
  fixed set of second arguments which are not interfaces (`string`, `int`, `List<>`), so that arm 4 is reached.
- Measure the whole sweep first, and write the time and the pass count in the pull request body. If it takes under
  about 30 seconds on the development machine, commit the whole sweep. If it takes longer, commit every pair the
  host passes, and every pair of a fixed stride through the rest, with the stride chosen to fit the 30 seconds. A
  plain stride through all pairs is the wrong reduction: under 1% of the pairs pass, and those are the pairs that
  exercise variance and inheritance.
- Thread the state through the pairs of one receiver and start each receiver from the initial state, so that the
  test host's memory stays bounded.
- A pair of the shape in decision 9 is checked by decision 9's weaker property. If the sweep meets any other
  refusal, stop and report it to the owner. Do not widen the test's exemption to cover it.
- Assert that the host passed some pairs and raised on others, as `TestCompositeTypeVariableCasts.fs` does.

### 8. File both issues: yes

- #2293. I confirmed the code and the source: `RuntimeTypeHandle::
  IsGenericVariable` is `IsTypeDesc() && ...`, which is `false` for any MethodTable without reading metadata, and
  PawPrint's handler refuses the dynamic-methods class as a metadata query.
- #2294. I confirmed the code and the source (`COMPlusThrow(kArgumentException,
  W("Arg_ArgumentOutOfRangeException"))`). The planner read the message and did not measure it, and the issue says
  so. It is close to the question #2289's body already asks about `GetMethodAt`, so the issue names #2289.

Two more come from decisions 9 and "Messages" below.

### 9. The open-value-type case: (a), leave the refusal, with a weaker property instead of a skip

Option (b) is not small. The flag is set on a value type when `LoadExactInterfaceMap`'s first technique retries, and
the retry has three triggers inside a six-case analysis of each inherited interface (`methodtablebuilder.cpp`, the
loop after `#LoadExactInterfaceMap_Algorithm1`). Option (c) answers wrongly exactly when one of those triggers
fires, and nothing has measured which types fire them. A refusal is a stop and not a wrong answer, so it stays, and
#2295 records what a fix needs.

The plan would have the property skip every pair whose receiver is an open value type and whose target is a variant
interface definition. Do not skip them. For a pair of that shape, the property is weaker: PawPrint's function either
returns the verdict that matches the host, or stops with the refusal whose message names
`MayHaveOpenInterfacesInInterfaceMap`. Any other exception, and any verdict that disagrees with the host, fails.
Count both outcomes. This keeps the common case under test (the scan finds nothing, and the answer is "not found"),
and it means a later change which answers these pairs wrongly fails here.

Keep the plan's example test: `SGenInC<>` against `IInC<>` is refused by PawPrint with that message, and the host
raises "Interface not found." for it. Whoever resolves #2295 will see it go
red, which is its purpose. Add the closed `SGenInC<string>` against `IInC<>` to the wrapper guest, expecting
"Interface not found.": the planner measured `SGenInC<int>` only, and an instantiation over a reference type is the
one whose code CoreCLR shares. Measure it on real .NET 10 and 11 before writing the expectation.

### Decoding and messages (the plan's decisions 4 and 5, which had no question)

- Decoding: `NativeCall.qCallTypeHandleToRuntimeTypeHandleTarget` for both arguments. I confirmed that a null handle
  cannot arrive: `GetNativeHandle()` throws `ArgumentNullException` in managed code for a default handle, before
  the QCall.
- Messages: the English text of `Strings.resx`, written in the handler. I confirmed the four texts against the
  pinned `Strings.resx`. CoreCLR does not hold the text itself: `EEResourceException::GetThrowableMessage` asks
  managed code (`Environment.GetResourceStringLocal`, which is `SR.GetResourceString`), so the guest's culture and
  resource settings can change it. That gap is shared by every native raise which carries a message, and is
  #2296. Name that issue in a comment beside the handler's messages.

### Corrections to the study

1. "Three spellings of one rule" for `IsTypeDesc`: two of them differ on a continuation type (decision 3).
2. `RuntimeTypeHandleTarget.isTypeDesc` cannot be defined through `forTarget` because of compile order
   (decision 3).
3. The `GetMethodAt` message in question 8 was read, not measured.
4. `TestExportedTypeSelection.fs` is on the open pull request #2292, not on `main`. Follow its use of
   `CoverageSample` if it has merged by the time this is built; otherwise write the same shape here.

### Tests: additions and changes to the plan's design

Write every test and guest first, in a commit of its own, and record the failing output, as the plan says.

**Example test of the six verdicts** (decision 4). In `TestInterfaceVerification.fs`, over a fixed corpus, assert
the exact case for one pair of each: `COne` / `IOne` is `Implemented`; `List<>`'s `T` / `IComparable` is
`ReceiverIsTypeVariable`; `int*` / `IDisposable` is `ReceiverIsTypeDesc`; `IOne` / `IOne` is `ReceiverIsInterface`;
`COne` / `string` is `TargetIsNotInterface`; `COne` / `IMarker` is `NotImplemented`. Add `int*` / `string` as
`ReceiverIsTypeDesc`, which is arm 2 before arm 4 at the level of the function.

**The generated property.**

- The generator builds valid C# by construction: no variance error (an `out` parameter never used as the argument
  of an `in` position, and so on), no two implemented interfaces that could unify, no struct with a base, and every
  argument within its constraint. A Roslyn error fails the test as a bug in the generator. Do not discard a case
  that fails to compile, because that hides lost coverage.
- Take the coverage labels from the host's side, so that they do not read the structure under test: the host's
  message, with "Interface not found." divided by whether the host's receiver is a pointer, byref or function
  pointer. That gives the six labels. Add three more, each with a floor: a pass where the target is not among
  `receiver.GetInterfaces()` (a pass by variance alone); a pass where the target is a generic interface definition
  (the special marker accepted); and a pair of decision 9's shape.
- Pool: add the fixed non-interface targets `string` and `int`, and the type variables, pointers and arrays as
  second arguments too (the plan's "every ordered pair" already implies it; the floors for arm 4 should be reached
  by more than one shape of target).
- The host's verdict comes from the wrapper invoked by reflection on the boxed `source.TypeHandle`. Unwrap the
  `TargetInvocationException`. Assert that every inner exception is exactly `ArgumentException`, with `HResult`
  0x80070057 and a null `ParamName`.
- Budget: about 30 seconds on the development machine for the fixed sample and the fresh cases together. Build the
  state that holds CoreLib once and add each generated assembly to it. If the test is over budget, lower the number
  of cases before shrinking the pool, and write the measured time in the pull request body.

**Guest checks to add.**

- To the `GetInterfaceMap` guest: `COne` against `List<>`'s `T`, against `int*`, against `IOne[]` and against `int`,
  each "Type passed must be an interface." (the planner measured these as the `target-*` rows, and the plan's guest
  table left them out). They show that a second argument which is a type variable or a TypeDesc reaches arm 4.
- To the wrapper guest: a type variable against `string`, "Invalid handle." (arm 1 before arm 4; measured as
  `direct genericparam-string`); `IDerived` against `IBase`, "'this' type cannot be an interface itself." (arm 3
  before arm 5, where arm 5 would pass); and `SGenInC<string>` from decision 9.
- To the impure guest: if `typeof(COne).GetInterfaceMap(dynamicClass)` raises "Type passed must be an interface." on
  real .NET 10 and 11, add it. The planner measured that pair through the wrapper only. Keep every other use of the
  dynamic-methods class as a receiver on the wrapper, because of
  #2293.

**Controls on real .NET**, as the plan says, for all four guests.

### Mutants: additions to the plan's table

| mutant | killed by |
|---|---|
| the handler passes its two arguments in the wrong order | `COne` / `IMarker` through `GetInterfaceMap` (it then raises arm 3's message) |
| arm 4 before arm 1 | the wrapper guest's type variable against `string`; the generated property |
| arm 5 before arm 3 | the wrapper guest's `IDerived` / `IBase`; the generated property |
| the verdict function returns `NotImplemented` where it should return `ReceiverIsTypeDesc` | the example test of the six verdicts only. No guest and no comparison with the host can see it. |
| a second argument which is a type variable, a pointer or an array is treated as an interface, or refused | the four added `GetInterfaceMap` checks; the generated property |
| `TypeHandleTag.isTypeDesc` answers `false` for a type variable, or `true` for an array | arm 1 hides the first from the verdict function, so add a small example test of `isTypeDesc` with one assertion for each shape of `RuntimeTypeHandleTarget` (a closed class, `int[]`, `int*`, `int&`, a function pointer, a definition, an open construction, a type variable, a method's type variable, `T[]`, `T*`); the second is also killed by the SZ-array guest checks |
| decision 9's pairs answered "not found" without the refusal (the plan's option (c)) | the pinned example test of `SGenInC<>` / `IInC<>`, by design |
| arm 3 before arm 1, or arm 3 before arm 2 | equivalent: a type variable or a TypeDesc is never an interface. Record as equivalent. |

Run each mutant against the example tests, both properties and the guests, and record which test went red.

### Order of work

1. The tests and guests, failing, in one commit.
2. `TypeHandleTag.isTypeDesc`, with `NativeCastHelpers` switched to it, in one commit.
3. The verdict type and function, then the handler and its registration in `NativeQCall.fs`.
4. The mutants.
5. The default suite, and then each of the four Guest categories as its own invocation, since step 2 touches the
   interpreter's cast path.
6. Change this document's status line to "decided and built" in the same pull request.

## As built

Built 2026-11-16 in the order above: the tests and guests first (their failing output is in that commit's message),
then `TypeHandleTag.isTypeDesc` with `NativeCastHelpers` switched to it, then the verdict type and function and the
handler.

### What was built

- `IlMachineRuntimeMetadata.InterfaceVerification` and `IlMachineRuntimeMetadata.verifyInterfaceIsImplemented`, with
  the alias `IlMachineState.verifyInterfaceIsImplemented`, as decision 1 says; arm 5 is
  `isRuntimeTypeHandleTargetAssignableTo` alone, with the reason at the call (decision 2).
- `TypeHandleTag.isTypeDesc` in `NativeIntSource.fs`; `NativeCastHelpers.isTypeDescTarget` is deleted (decision 3).
- The handler in `Native/NativeRuntimeTypeQCall.fs`, registered in `Native/NativeQCall.fs`: both arguments through
  `NativeCall.qCallTypeHandleToRuntimeTypeHandleTarget`, one `match` over the six cases, each message with its resource
  name, and #2296 named beside them.
- New public API in the published `WoofWare.PawPrint` package: `IlMachineRuntimeMetadata.InterfaceVerification`,
  `IlMachineRuntimeMetadata.verifyInterfaceIsImplemented`, `IlMachineState.verifyInterfaceIsImplemented` and
  `TypeHandleTag.isTypeDesc`.
- Guests: `sourcesPure/TypeGetInterfaceMapRefusals.cs` (32 checks, public API only),
  `sourcesPure/RuntimeTypeHandleVerifyInterfaceIsImplemented.cs` (17 checks through the wrapper, the first that it was
  found), `sourcesImpure/TypeBuilderVerifyInterfaceIsImplemented.cs` (7 checks), and the parked
  `sourcesPure/TypeGetInterfaceMapNoMethods.cs` and `sourcesPure/TypeGetInterfaceMap.cs`, both on #2289, the second
  naming #2290 as its next stop. Both parked guests stop at "Unimplemented native method (PInvokeImpl
  QCall!RuntimeTypeHandle_GetNumVirtualsAndStaticVirtuals)" (measured).
- `TestInterfaceVerification.fs`: the example test of the six verdicts (with `int*` / `string`), the example test of
  `isTypeDesc` over sixteen target shapes, the pinned refusal of `SGenInC<>` / `IInC<>`, the generated property and the
  CoreLib sweep.

### Measurements

- **Controls on real .NET.** Every guest exits 0 on .NET 10.0.14 and on 11.0.0-rc.3 rolled forward. Each perturbed
  expectation (each message changed to the next arm's, each pass expected as a raise, each raise expected as a pass, a
  non-null `ParamName` expected of each `ArgumentException`, the managed `InvalidOperationException` and
  `NotSupportedException` each expected as an `ArgumentException`, the wrapper lookup inverted, each comparison of the
  parked guests and of the active guests' helpers inverted): 160 perturbations, each exiting with its own check's
  number on both runtimes.
- **`SGenInC<string>` / `IInC<>`** (decision 9's addition) raises "Interface not found." on both runtimes, and PawPrint
  answers it without refusing.
- **`typeof(COne).GetInterfaceMap(dynamicClass)`** raises "Type passed must be an interface." on both runtimes, so the
  impure guest has it (check 7).
- **The CoreLib sweep** (decision 7) fits the budget whole, so the whole sweep is committed: 1,252 exported
  non-interface receivers against 126 exported interfaces and `string`, `int` and `List<>`, 161,508 pairs, 1,396 of
  them passing, no disagreement and no refusal. 1,050 of the pairs are of decision 9's shape, and PawPrint answers
  every one of them. It takes 21 s alone on the development machine. (The planner's host sweep counted 184,752 pairs
  because it added its fixtures and some closed instantiations; this one is the exported types only, as decision 7
  says.)
- **The generated property** runs 150 cases from `CoverageSample`'s fixed sample and 150 fresh ones, in 27.9 s alone.
  The fixed sample reaches: a pass 2,280 times, of which 442 by variance alone and 22 on a generic interface
  definition; "Invalid handle." 8,356; "Interface not found." on a TypeDesc 8,560 and otherwise 20,135; "'this' type
  ..." 47,817; "Type passed ..." 40,138; decision 9's shape 110, of which PawPrint refused 3. Each floor is about half
  of its count; decision 9's floor (55) is on the shape, whichever way PawPrint takes it.

### Where the decisions were silent, and deviations

1. **Decision 3's reading of `objIsInstanceOfCore`** is right with one exception, recorded in that commit's message:
   `isRuntimeTypeHandleTargetAssignableTo` answers `true` for two equal targets before it refuses a continuation, so
   for an object whose own type is a continuation, cast to that same type, the switch turns a `true` into a refusal.
   It cannot be reached: no token, signature or reflection object names a continuation type, and PawPrint refuses
   `GetType()` on an object of one.
2. **The guests' numbering** differs from the plan's table, because decision 5 gives every call its own exit code and
   adds checks. The map guest's checks 1 to 28 are the plan's checks 2 to 21, one call per code; 29 to 32 are decision
   5's four second arguments. The wrapper guest's 1 is the lookup, 2 to 12 the plan's 22 to 26, 13 to 15 arm 1 (the
   third against `string`), 16 `IDerived` / `IBase`, and 17 `SGenInC<string>`.
3. **The generator is valid by repair, not by discard.** It draws freely and then, deterministically, replaces an
   argument which breaks a `class` constraint, or which makes a base interface not output-safe, with `object`, and
   drops a declared interface which repeats one already declared or could unify with another (CS0695). It judges
   "could unify" conservatively, and counts the interfaces a class inherits from its base classes too, which C# does
   not require. A Roslyn error fails the test; every corpus generated so far has compiled.
4. **Coverage labels** are the host's, as decision 7 says, plus one of PawPrint's side, the refusals of decision 9's
   shape, which is counted and not floored: the fixed sample reaches it three times, and the pinned example test
   covers it.
5. **#2292 merged** before the rebase (the decisions' correction 4): its `TestExportedTypeSelection.fs` uses
   `CoverageSample.check` with floors asserted over the fixed sample, the same shape as this plan's generated property.
6. **The test builds no function pointer from a host type.** The pools hold none; the `isTypeDesc` example builds both
   kinds directly, and the map guest's check 9 covers `delegate*<void>` as a receiver.

### Mutants

Each was run against the five tests of `TestInterfaceVerification` and the three active guests, then restored from a
copy and rebuilt. "map" is `TypeGetInterfaceMapRefusals.cs`, "wrapper" is
`RuntimeTypeHandleVerifyInterfaceIsImplemented.cs`, "impure" is `TypeBuilderVerifyInterfaceIsImplemented.cs`, and a
number after a guest is its exit code.

| mutant | killed by |
|---|---|
| do nothing (the registration removed) | all three guests stop at the unimplemented QCall |
| arm 1 folded into arm 2 | six verdicts; generated; wrapper 13 |
| arms 3 and 4 swapped | generated; map 6 |
| arm 4 before arm 2 | six verdicts; generated; map 6 |
| arm 4 before arm 1 | six verdicts; generated; map 6; wrapper 15 |
| arm 5 before arm 3 | six verdicts; generated; wrapper 16; map stops at #2289's QCall (check 3 passes the QCall) |
| arm 3 before arm 1; arm 3 before arm 2 | survive: equivalent, as decided (a type variable or a TypeDesc is never an interface) |
| arm 2 removed | six verdicts; generated; map 7 |
| `ReceiverIsTypeDesc` returned as `NotImplemented` | six verdicts only, as decision 4 predicted |
| arrays treated as TypeDescs | generated; map 5 |
| an open definition or construction treated as not an interface, on either side | pinned refusal; generated; CoreLib; wrapper 9; map stops at #2289 |
| an open definition receiver treated as a TypeDesc | pinned refusal; generated; CoreLib; map 12; wrapper 10 |
| an open definition receiver refused (what decision 2 (ii)'s `GetInterfaces` walk does for `List<>`) | pinned refusal; generated; CoreLib; map and wrapper stop at the refusal |
| a second argument which is a type variable, pointer or array treated as an interface | generated; map 29 |
| arm 5 as an exact match on the declared interfaces | pinned refusal; generated; CoreLib; wrapper 3; map and impure stop where that walk refuses an array and the dynamic-methods class |
| arm 5 through `isConcreteTypeAssignableTo` only (open pairs refused) | pinned refusal; generated; CoreLib; all three guests stop at the refusal |
| arm 5 through the object-cast path (closed only, `IDynamicInterfaceCastable` refused) | the same |
| arm 5 without variance (the closed and the open rule) | pinned refusal; generated; wrapper 6 |
| arm 5 as `ImplementsInterface` alone (no variance, no implicit array interfaces) | pinned refusal; generated; map 5; wrapper 6; impure (the interpreter's own call verification) |
| variance over value-type arguments (the closed and the open rule) | generated; CoreLib; map stops at #2289 (a check that must raise passes) |
| the special marker accepted on a struct | pinned refusal; generated; wrapper 17; map stops at #2289 |
| the special marker never accepted | pinned refusal; generated; wrapper 9 |
| decision 9's pairs answered "not found" without the refusal | pinned refusal only, by design |
| `isTypeDesc` false for a type variable | the `isTypeDesc` example only, as the decisions' table predicted |
| `isTypeDesc` true for an array | `isTypeDesc` example; generated; map 5 |
| the handler passes its arguments in the wrong order | map 1; wrapper 2; impure |
| no message | map 1; wrapper 13; impure |
| `InvalidOperationException` instead of `ArgumentException` | map 1; wrapper 13; impure |
| `TypeLoadException` instead of `ArgumentException` | map 1; wrapper 13; impure |
| `ReceiverIsTypeDesc` raising "Type passed must be an interface." | map 4 |
| `ParamName` set | not constructed: `raiseExceptionWithMessage` cannot set `ParamName`. The real-.NET controls show that every raise check fails when a non-null `ParamName` is expected. |
| `ImplementsInterface` in front of `CanCastTo`, with its own half removed | equivalent by construction: `ImplementsInterface` is not modelled |

### Suites

After a whole-solution build with the reference assemblies deleted, each its own invocation, on the branch rebased
onto `origin/main` at `4ef328514` (which brought in #2292 and #2297):

| suite | total | passed | skipped |
|---|---|---|---|
| default | 5,846 | 5,807 | 39 |
| `TestCategory=Guest` | 234 | 234 | 0 |
| `TestCategory=GuestSolo` | 2 | 2 | 0 |
| `TestCategory=GuestPure` | 1,202 | 1,202 | 0 |
| `TestCategory=GuestImpure` | 301 | 301 | 0 |

Before the rebase, on `a66b75274`, the same five were 5,827 (5,788 passed, 39 skipped), 234, 2, 1,200 and 300, all
passing.
