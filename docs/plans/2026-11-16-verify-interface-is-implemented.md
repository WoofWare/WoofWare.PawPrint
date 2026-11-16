# `RuntimeTypeHandle_VerifyInterfaceIsImplemented` (issue #2270)

Status: planned, not decided. The study below was measured 2026-11-16 on `origin/main` at `a66b75274`.

Scope is this one QCall, as the comment on #2270 decided while planning #2269. `Type.GetInterfaceMap` makes two more
unimplemented QCalls after it: `RuntimeTypeHandle_GetNumVirtualsAndStaticVirtuals` (#2289), then
`RuntimeTypeHandle_GetInterfaceMethodImplementation` (#2290). A guest of the success path is parked on #2289.

Every probe, throwaway and log named below is in this session's scratchpad,
`/private/tmp/claude-501/-Users-patrick-Documents-GitHub-WoofWare-PawPrint--claude-worktrees-test-pipeline-performance-724897/ba89fbb1-81d8-460f-a9be-c4df3a5bd899/scratchpad/2270/`
(written `2270/` below). Real .NET means 10.0.14, and 11.0.0-rc.3 reached by rolling the same build forward
(`DOTNET_ROLL_FORWARD=LatestMajor`, `DOTNET_ROLL_FORWARD_TO_PRERELEASE=1`). Every real-.NET result below is the same on
both runtimes; each log pair was diffed and differs only in its `runtime` line. PawPrint runs used the Release App
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
  under PawPrint, which reaches the QCall that way: `2270/main-one-direct.log`, "Unimplemented native method
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
with no disagreement and no message other than "Interface not found." from arm 5 (`2270/probe/sweep-10.log`,
`sweep-11.log`). `RuntimeType.IsAssignableFrom` of two `RuntimeType`s is `RuntimeTypeHandle.CanCastTo`
(`RuntimeType.cs:279-291`, read), which is `TypeHandle.CanCastToForReflection` (`RuntimeHandles.cs:683-685`,
`RuntimeHelpers.CoreCLR.cs:1191-1202`, read): `TypeHandle::CanCastTo` plus the `T` to `Nullable<T>` rule, which an
interface target cannot meet. So the relation arm 5 asks is reflection's `IsAssignableFrom`, and
nothing narrower.

### Measurements on real .NET (`2270/probe/Verify.cs`, `rows-10.log`, `rows-11.log`; `2270/probe2/`, `extra-*.log`, `dynclass-*.log`)

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
QCall!RuntimeTypeHandle_VerifyInterfaceIsImplemented)" (measured, `2270/main-one-map.log`, `main-one-direct.log`).
Nothing before it stops for an ordinary class.

### PawPrint with a throwaway of the rule (`2270/throwaway.diff`, reverted)

The throwaway added a handler in `Native/NativeRuntimeTypeQCall.fs` which decodes both arguments with
`NativeCall.qCallTypeHandleToRuntimeTypeHandleTarget`, takes the arms in CoreCLR's order, answers arm 5 with
`IlMachineState.isRuntimeTypeHandleTargetAssignableTo`, and raises with `NativeHandlerResult.raiseExceptionWithMessage`.

- Every row of `rows-nosuccess` (the rows above without the three successes, 106 lines) prints the same as on real
  .NET 10 (`2270/t3-rows.log`, compared in `t3-rows.cmp`, whose only differences are the App's own `info:` log lines).
- Every row of `extra` likewise (`2270/t5-extra.log`, `t5-extra.cmp`).
- The direct rows over the dynamic-methods class likewise (`2270/t5-dynclass.log`). Its `GetInterfaceMap` row does not
  reach the QCall under PawPrint: `RuntimeType.GetInterfaceMap`'s first line asks `IsGenericParameter`, and
  `RuntimeTypeHandle.IsGenericVariable` (`Native/NativeRuntimeTypeFCall.fs:415`) refuses the dynamic-methods class as a
  metadata query. CoreCLR answers `false` there without metadata (`IsGenericVariable` is `IsTypeDesc() && ...`, and the
  class is a MethodTable). That refusal is not this issue's; see question 8.
- The success path stops at the next QCall, as the issue says: "Unimplemented native method (PInvokeImpl
  QCall!RuntimeTypeHandle_GetNumVirtualsAndStaticVirtuals)" (`2270/t5-success.log`, at row `list-variant`).
- A sampled sweep (`sample 40`: every 40th visible CoreLib non-interface type, then the probe's fixtures, against 159
  interfaces; one line per pair) was stopped after 802 s, at 2,691 of its 8,037 pairs: the first 20 CoreLib receivers
  (`System.Byte`, `Action<,>`, `Func<...>` definitions, `ValueTuple<,,>`, enums, static classes, exception types and so
  on), 30 of them passing. Every one of the 2,691 lines is the same as real .NET 10's (`2270/t4-sample.log` against
  `2270/probe/sample-10.log`, compared by `cmp-sample.py`). The fixtures at its end were already covered by the rows
  above. It also gives a cost: about 0.3 s per pair inside a guest, almost all of it the reflection call and the raise,
  which is why the property tests below call the verdict function directly instead.
- **The one divergence: a refusal.** `SGenInC<>`, a generic struct *definition* implementing the closed `IInC<object>`,
  against the variant definition `IInC<>`: real .NET raises "Interface not found."; PawPrint stops in
  `isRuntimeTypeHandleTargetAssignableTo` with "TODO: isRuntimeTypeHandleTargetAssignableTo: the open value type or
  interface ... has an interface that casts to the variant interface definition ..., but whether CoreCLR scans for it
  depends on its MayHaveOpenInterfacesInInterfaceMap flag, which PawPrint does not model for open types"
  (`2270/t6-openstruct.log`; real, `2270/probe2/openstruct-10.log`, `-11.log`, where `IsAssignableFrom` agrees). The
  closed `SGenInC<int>` and both `CGenInC` rows agree (`t6-openstruct-rest.log`). CoreCLR sets the flag on every class,
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
  interface rows are supported today" (measured, `2270/t1-rows.log`). `isRuntimeTypeHandleTargetAssignableTo` builds
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
exception the same way, or expects none. The fixture types are the probe's (`2270/probe/Verify.cs`).

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

- `2270/probe/Verify.cs`, `Verify.csproj`, `real.sh`, `publish.sh`: the probe, with modes `rows`, `rows-nosuccess`,
  `sweep`, `sample <k>`, `one-direct`, `one-map`. Logs: `rows-10.log`, `rows-11.log`, `rows-nosuccess-*.log`,
  `sweep-10.log`, `sweep-11.log`, `sample-10.log`, `sample-11.log`.
- `2270/probe2/`: the same probe with modes `extra`, `dynclass`, `openstruct` and `openstruct-rest`. Logs:
  `extra-*.log`, `dynclass-*.log`, `openstruct-*.log`, `openstruct-rest-*.log`.
- `2270/pp.sh`, `pp2.sh`, `build-app.sh`, `main-runs.sh`, `t1.sh` to `t5.sh`, `cmp-sample.py`: runners and the sweep
  comparison. Logs: `main-one-direct.log`, `main-one-map.log`, `t1-rows.log` (the `GetInterfaces` stop),
  `t3-rows.log`, `t3-rows.cmp`, `t4-sample.log`, `t5-extra.log`, `t5-extra.cmp`, `t5-dynclass.log`, `t5-success.log`,
  `t6-openstruct.log`, `t6-openstruct-rest.log`.
- `2270/throwaway.diff`: the throwaway handler.
