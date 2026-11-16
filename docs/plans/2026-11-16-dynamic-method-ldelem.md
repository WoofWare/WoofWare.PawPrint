# `ldelem <type>` and `stelem <type>` in a dynamic method (issue #2308)

Status: proposed. The study settles every choice; there is no structural decision to make (see "Options"). Three small
questions remain, none of which changes the fix.

Part of #849. Measured 2026-11-16 on `main` at `b799325a8`, with and without the throwaway fix described under "The
change". Real .NET means 10.0.14, and 11.0.0-rc.3 reached by rolling the same build forward
(`DOTNET_ROLL_FORWARD=LatestMajor`, `DOTNET_ROLL_FORWARD_TO_PRERELEASE=1`). PawPrint runs used the App built from that
commit (Release, and once Debug), with each probe published self-contained for osx-arm64 against .NET 10, and the main
probe also against .NET 11 (PawPrint then runs .NET 11's CoreLib). Every guest had `IsDynamicCodeSupported` true in
its runtimeconfig. Each claim is marked *measured* or *read*.

## What it is

`IlDecoding.scopeOperandKind` answers `NotYetSupported` for `ldelem <type>` and `stelem <type>`, so the decoder refuses
a dynamic method whose body holds either when the method is minted (`readMetadataToken`, `IlDecoding.fs:177-180`).
The refusal is a host `failwith`, so the whole run stops; no guest handler sees it. The message gives a reason:

> ldelem/stelem resolve their element type through a separate path that yields a TypeInfo rather than a
> ConcreteTypeHandle

That reason is out of date (see "The TODO's premise is gone" below).

### Who reaches it

- **Microsoft.Extensions.DependencyInjection 9.0.0**, once a service has been resolved twice: the provider compiles
  the service's resolver with `ILEmitResolverBuilder` on a thread-pool thread. Its constants and factories are read
  with `ldelem <T>`, and an `IEnumerable<T>` service is built with `newarr` and `stelem <T>`. Measured on `main`: a
  transient service stops with the TODO naming entry 3, and an `IEnumerable<IService>` with the TODO naming entry 4.
- **`System.Linq.Expressions`' compiler**, for an array whose element is a value type with no primitive type code
  (`DateTime`, `decimal`, any struct). Read, with PawPrint's own IL dump of `ILGen.EmitLoadElement`: a reference type
  takes `ldelem.ref`, the twelve primitive type codes take `ldelem.i1` to `ldelem.r8` through a `switch`, and the
  default branch is `Emit(OpCodes.Ldelem, type)`. Measured on `main`: `Expression.ArrayIndex` over a `DateTime[]`,
  compiled, stops with the TODO.
- **A guest's own `DynamicMethod`**, such as the issue's snippet. Measured on `main`: stops with the TODO.

## Study

### How the operand resolves for an ordinary method

`ldelem`, `stelem`, `ldelema` and `newarr` each take a TypeDef, TypeRef or TypeSpec token (ECMA-335 Partition III,
chapter 4). For a method read from an image, `UnaryMetadataIlOp.execute` hands the op
`ResolvedMetadataOperand.FromMetadata (assembly, token)` (`UnaryMetadataIlOp.fs:72-85`) and each op resolves the token
itself:

- `executeNewarr` (`UnaryMetadataArrayOps.fs:18-39`) and `executeLdelema` (`:215-235`) match on `ctx.TypeOperand`,
  and in the `FromMetadata` arm call `IlMachineState.resolveTypeMetadataToken`, then `TokenBindingStop.typeToken`, then
  `IlMachineState.concretizeType`, giving a `ConcreteTypeHandle`.
- `executeLdelem` (`:349-371`) and `executeStelem` (`:303-347`) call the private `elementTypeOperand` (`:150-177`),
  which makes the same three calls, but reads `ctx.ActiveAssembly` and `ctx.MetadataToken` directly instead of
  matching on `ctx.TypeOperand`. It too returns a `ConcreteTypeHandle`.

All four then ask `UnaryMetadataIlOpContext.refuseTypeOperand` (`UnaryMetadataIlOpContext.fs:298-306`), which raises
`InvalidProgramException` for a byref or `System.Void` before anything is popped.

What each op does with the handle afterwards:

- `ldelem` uses it for the refusal only. It pushes whatever the array holds at the index
  (`ManagedHeap.getArrayValue`, `:367`), with no check against the array's element type.
- `stelem` uses it to coerce the value it stores (`cliTypeZeroOfHandle` and `EvalStackValue.toCliTypeCoerced`,
  `:325-343`), after the bounds check and the array-store covariance check
  (`IlMachineStateExecution.checkArrayStoreVariance`, `:330-341`), which looks at the array and the value, not the
  token.
- `ldelema` compares it with the array's element type exactly unless `readonly.` came first (`:270-301`).

### How it resolves for a dynamic method

For a method minted by `Reflection.Emit`, the decoder produces `MetadataOperand.FromDynamicScope index`, masking the
token with `0x00FFFFFF` (`IlDecoding.scopeIndexOf`, `IlDecoding.fs:37`), but only for an opcode whose
`scopeOperandKind` is not `NotYetSupported` (`:177-183`). `newarr`, `ldelema` and nine other type-consuming opcodes are
`ScopeOperandKind.Type` (`:80-90`); `ldelem` and `stelem` are `NotYetSupported` (`:136-140`).

For a `Type` opcode, `UnaryMetadataIlOp.execute` reads the entry before dispatching (`UnaryMetadataIlOp.fs:95-98`):
`DynamicScopeOperand.closedType` (`DynamicScopeOperand.fs:456-471`) narrows the boxed `RuntimeTypeHandle` to a closed
type, and anything else raises into the guest:

- an entry that is not a type handle: `BadImageFormatException`, through `typeHandleTarget`;
- an open generic definition, a generic parameter, an open construction and the other targets
  `containsGenericVariables` lists (`:527-536`): `InvalidProgramException`.

The op then receives `ResolvedMetadataOperand.ScopeType handle`, which `ctx.TypeOperand` turns into
`ResolvedTypeOperand.FromScope handle` (`UnaryMetadataIlOpContext.fs:160-175`). `newarr` and `ldelema` take that
handle in their `FromScope` arm (`UnaryMetadataArrayOps.fs:20`, `:217`) and go on exactly as for a metadata token,
`refuseTypeOperand` included.

`elementTypeOperand` has no `FromScope` arm. Were the decoder to admit the body, `ctx.ActiveAssembly` would fail with
its own "the two have gone out of step" TODO (`UnaryMetadataIlOpContext.fs:139-150`).

### The TODO's premise is gone

The reason in the refusal was written by #1007 (`a4403663b`), when `ldelem` and `stelem` shared
`resolveElementTypeToken`, which returned a `TypeInfo<TypeDefn, TypeDefn>`. #2009 (`4bf73dfe9`) replaced it with
`elementTypeOperand`, which returns a `ConcreteTypeHandle` like every other type op, but left the refusal's text as it
was. Read, from `git show 4bf73dfe9 -- WoofWare.PawPrint/UnaryMetadataArrayOps.fs`. So nothing separates `ldelem` and
`stelem` from `ldelema` now except the missing arm and the classifier.

### The other readers of the operand

None needs a change (read):

- `StackShapeOfMethod.dynamicTokenShape` (`StackShapeOfMethod.fs:439-447`) already reads a dynamic `ldelem`'s entry
  through `closedType` and `TypeOperand.refusal`, and gives no shape to one the instruction will refuse. `stelem`
  needs no shape: its stack effect is three pops whatever the token (`StackEffect.fs:474`).
- `StackShapeTokens.ofMetadataToken` (`StackShapeTokens.fs:418-422`) is the metadata counterpart, and is unaffected.
- `OpcodeFaults` (`OpcodeFaults.fs:605-612`) declares `ldelem` with `uncheckedElementAccess` and `stelem` with
  `covariantElementAccess`; `resolves` adds `ResolutionFault.ScopeEntry` for every token opcode (`:481-484`), so the
  scope refusals are already declared.
- `ContextSwitchPrior` treats every scope operand as guest-visible (`ContextSwitchPrior.fs:460-461`), and
  `EscapeAnalysis` treats it as opaque.

### What CoreCLR does

Read, in both pinned sources.

- **Emit.** `DynamicILGenerator.Emit(OpCode, Type)` (`DynamicILGenerator.cs:129-138` on .NET 10, `:130` on .NET 11)
  stores `rtType.TypeHandle` in the scope through `GetTokenFor(RuntimeType)` (`:494-497`) whatever the opcode, and
  writes a TypeDef-tagged token.
- **Resolution.** `DynamicResolver.ResolveToken` (`:772-783` on .NET 10, `:789` on .NET 11) throws
  `InvalidProgramException` for a null entry and returns the `RuntimeTypeHandle`'s value for a type entry.
  `CEEInfo::resolveToken`'s dynamic branch (`jitinterface.cpp:878-962` on .NET 10, 31 lines earlier on .NET 11) throws
  "bad token" when the entry is a method or field or a null type handle, or when the token kind does not include
  `CORINFO_TOKENKIND_Class`, and `InvalidProgramException` for a type that contains generic variables (`:959-962`).
  Then, for every token kind but `ldtoken`'s and `newarr`'s own, it refuses `ELEMENT_TYPE_BYREF` and
  `ELEMENT_TYPE_VOID` (`:1123-1127`; `newarr`'s check is `:1105-1111`).
- **The importer.** `CEE_LDELEM` (`importer.cpp:7252-7261` on .NET 10, `:7405` on .NET 11) resolves the token as
  `CORINFO_TOKENKIND_Class`, takes the element's JIT type from the class handle, and goes to `ARR_LD`, which loads
  without looking at the array's element type. `CEE_STELEM` (`:7329-7344`, `:7482`) does the same, except that a
  reference type falls through to `CEE_STELEM_REF`, the covariant store helper. `CEE_LDELEMA` (`:7205-7248`), for
  comparison, calls the exact-type `LDELEMA_REF` helper for a reference type without `readonly.`.

So `ldelem` and `stelem` resolve their token by the same path, and with the same refusals, as `sizeof`, `box` and the
others, and the token decides only the width and kind of the access. That is what PawPrint's metadata path already
models.

### Measurements on real .NET

One probe program, each check its own `DynamicMethod` built with `typeof(Program).Module` as its scope, invoked through
`CreateDelegate`. Every row below is the same on .NET 10 and .NET 11 (measured; the two outputs differ only in their
`Environment.Version` line). PawPrint with the throwaway fix gives the same answer on every row except where the last
column says otherwise, on .NET 10's CoreLib (Release and Debug App) and on .NET 11's (Release).

Element types, each read from an array the C# caller filled, or written to one it then reads:

| check | real .NET | PawPrint with the fix |
|---|---|---|
| the issue's snippet | 2020 | same |
| `ldelem string` of `{"a","b"}` at 1 / `stelem string` "z" at 1 | `b` / `null,z` | same |
| `ldelem int32` / `stelem int32` | 6 / `0,99` | same |
| `ldelem int16` of -2; `ldelem uint8` of 200; `ldelem char` of U+FFFE | -2; 200; 65534 | same |
| `stelem int16` of 0x12345 into a `short[]` | 9029 (0x2345) | same |
| `ldelem int64`; `ldelem float32`; `stelem float64` | 0x123456789A; 1.5; 2.25 | same |
| `ldelem` / `stelem` of a 24-byte struct at index 1 | `2,3,4` / `0,5,6,7` | same |
| `ldelem` / `stelem` of a struct holding a `string` and an `int` | `q,8` / `r,9` | same |
| `ldelem List<int>` | the same object | same |
| `ldelem` / `stelem` of `KeyValuePair<int,string>` | `3,t` / `4,u` | same |
| `ldelem DayOfWeek`; `ldelem int?` | Friday; 12 | same |
| `ldelem int*` of an `int*[]`, then `ldind.i4` | 77 | same |

The array's element type is not the operand's (each from verifiable or merely unusual IL; the JIT does not check):

| check | real .NET | PawPrint with the fix |
|---|---|---|
| `ldelem object` of a `string[]` | the string | same |
| `stelem object` of a non-string into a `string[]` | `ArrayTypeMismatchException` | same |
| `stelem object` of a string into a `string[]`; `stelem string` into a `string[]` passed as `object[]` | stored | same |
| `ldelem int32` of a `uint[]` holding 0xFFFFFFFF | -1 | same |
| `ldelem int32` of a `DayOfWeek[]` holding Saturday | 6 | same |
| `stelem int32` of 2 into a `DayOfWeek[]` | Tuesday | same |
| `ldelem int32` of a null array; of index 5 of an `int[1]` | `NullReferenceException`; `IndexOutOfRangeException` | same |

Refusals. Each operand under three bodies: `ldelem` of a null array, `ldelem` of an `int[1]`, and `stelem` of 0 (or
null) into a null array, each popping and returning 1:

| operand | real .NET, all three bodies | PawPrint with the fix |
|---|---|---|
| `int`, `string` (controls) | `NullReferenceException`, 1, `NullReferenceException` | same |
| `System.Void` | `InvalidProgramException`, default message | same |
| `int&` | `InvalidProgramException` | same |
| `List<>` (open definition) | `InvalidProgramException` | same |
| `List<>`'s `T` | `InvalidProgramException` | same |
| `List<TKey>` over `Dictionary<,>`'s `TKey` | `InvalidProgramException` | same |
| `Span<int>`, `TypedReference` (null-array bodies only) | `NullReferenceException`: not refused | same |
| `ldelem void` or `ldelem List<>` with nothing on the stack | `InvalidProgramException` | same |
| `ldelem void` or `ldelem List<>` behind a branch on an argument that is never taken | `InvalidProgramException` | **returns 1** |

The last row is the known divergence: CoreCLR refuses when it compiles the body, PawPrint when the instruction is
reached (`UnaryMetadataIlOp.fs:143-153`, and "A byref or `System.Void` type operand fails when it is reached" in
`docs/divergences.md`). It is the same for every scope-resolved type opcode today, so this plan does not test it.

Scope entries rewritten through private reflection after `CreateDelegate`, as `DynamicScopeRewrittenEntries.cs` does,
in a body `ldnull; ldc.i4.0; ldelem int32; ret` and its `stelem int32` counterpart. The slot is found by
`RuntimeTypeHandle` equality with `typeof(int)` and must be unique; the "not a type" rows change the answer from the
control's, so the rewrite reaches the entry the instruction reads:

| entry | real .NET, `ldelem` and `stelem` alike | PawPrint with the fix |
|---|---|---|
| unchanged (control) | `NullReferenceException` | same |
| `"not a type"` | `BadImageFormatException`, "Bad class token." | same |
| `null` | `InvalidProgramException`, default message | same |
| `default(RuntimeTypeHandle)` | `BadImageFormatException`, "Bad class token." | same |
| a `RuntimeMethodHandle`; a `RuntimeFieldHandle` | `BadImageFormatException`, "Bad class token." | same |
| `typeof(void).TypeHandle` | `InvalidProgramException` | same |

The `System.Linq.Expressions` compiler, with dynamic code on:

| check | real .NET | PawPrint on `main` | PawPrint with the fix |
|---|---|---|---|
| `Expression.ArrayIndex` over a `DateTime[]`, compiled, at index 1 | 2002, from a method whose `DeclaringType` is null | stops with the TODO | same as real |
| `Expression.NewArrayInit` of two 24-byte structs, compiled | length 2, `[1].C` 3 | not run (the first row stops the run) | same as real |

### Found on the way, outside this issue

**A refused type operand with too few values on the stack stops the run** (measured). Real .NET raises
`InvalidProgramException` for `stelem void`, `box void` or `stobj void` with nothing on the stack: the importer resolves
the token before it looks at the stack. PawPrint stops with "stack shape: ... is executing offset 0, which is invalid
IL: StackUnderflow". `StackShapeOfMethod.dynamicTokenShape` gives no shape to an operand the instruction will refuse,
but the stack effect of `stelem`, `box` and `stobj` does not need one, so the analysis still counts the pops and
refuses the underflow first. `ldelem`, `ldobj` and `unbox.any` escape this because their effect does push a token
shape, and a missing one leaves the instruction untyped. `box` and `stobj` already resolve against the scope on `main`,
so this is not new with the fix. The metadata path has the same analysis, so it probably has the same gap there (read,
not measured: no compiler emits the shape, and `TestFabricatedTypeOperandRefusal` always supplies the stack). See
question 1.

**An `ldelem` or `stelem` whose width differs from the array's element** (unverifiable IL) reads by the token's
width on real .NET and by the stored element in PawPrint. Measured on real .NET 10 and 11: `ldelem int64` of an `int[]`
holding `{1, 2}` answers 8589934593 (both elements), and `ldelem int16` of index 1 of an `int[]` holding
`{0x00030004, 5}` answers 3 (the high half of element 0), as does `ldelem.i2`. PawPrint with the fix stopped on the
first with "TODO: Int32(1)". The metadata path behaves the same (read): `executeLdelem` ignores the token after the
refusal. See question 2.

### What Microsoft.Extensions.DependencyInjection reaches next

Nothing, in the shapes measured: with the throwaway fix it runs to the end, and runs the code it emits.

- Fable's scenario (a transient service with a singleton dependency, resolved five times): exit 0 with real .NET's
  output, "total 210", in 21 s. Fable measured it stopping with the TODO at the tip of #2307.
- A probe of seven modes, each resolving until the service's constructor sees, in `Environment.StackTrace`, a frame of
  the emitted `ResolveService` method, up to 200 times with a 5 ms sleep between. Each then printed the name of the
  method behind the provider's accessor for the service, read by private reflection. The modes: a transient service
  with a singleton dependency; the same with the dependency registered as an instance (a constant); a factory
  registration; `IEnumerable<IService>` over two implementations; a scoped service (resolved twice per scope, which
  emits the scope cache's lock and `try`/`finally`); an open generic registration resolved as `IRepo<string>`; and a
  struct registered as an instance and taken by a constructor (a boxed constant read with `ldelem object` and
  `unbox.any`). On real .NET and under PawPrint with the fix, every mode printed "ran compiled resolver True; accessor
  ResolveService" and exited 0. PawPrint took 32 to 48 s per mode, four at a time. On `main`, the transient and
  `IEnumerable` modes stop with the TODO (entries 3 and 4).
- `ILEmitResolverBuilder` emits only `ldloc`, `ldarg.0`, `ldarg.1`, `callvirt`, `call`, `ldc.i4`, `stloc`, `ret`,
  `unbox.any`, `newobj`, `ldloca`, `ldfld`, `ldelem`, `stelem`, `pop`, `newarr`, `ldtoken`, `dup`, `brtrue`, `brfalse`,
  `brfalse.s` and `box`, plus one exception block with a `finally` (read, from PawPrint's IL dump of the type: the
  `OpCodes` fields it loads). After the fix, every one of them resolves against a scope.

The provider catches an exception thrown by the background compilation and keeps the runtime resolver (read, from
memory of the DI source, which is not in the pinned checkout), so a guest exception there would not show in the
output. That is why the probe checks that the emitted method ran, not only the exit code.

## The change

1. `IlDecoding.scopeOperandKind` (`IlDecoding.fs:78-153`): move `Ldelem` and `Stelem` into the `ScopeOperandKind.Type`
   arm, delete their `NotYetSupported` arm, and fix the heading comment above the remaining refusals (`:136`), which
   then covers only `constrained.`.
2. `elementTypeOperand` (`UnaryMetadataArrayOps.fs:150-177`): match on `ctx.TypeOperand`, with a `FromScope handle`
   arm returning the handle, as `executeNewarr` and `executeLdelema` do. `executeLdelem` and `executeStelem` keep calling
   `refuseTypeOperand` on whatever it returns.
3. Comments that count the type-consuming opcodes say "eleven": `UnaryMetadataIlOpContext.fs:71`, `:74` and `:107`,
   `DynamicScopeOperand.fs:359` and `:438`, `UnaryMetadataTokenOps.fs:518`. Make them thirteen, or better, stop
   counting.

The throwaway was items 1 and 2, nine lines. Nothing else in the interpreter, Semantics or Analysis changes.

## Options

There is no structural choice here. The issue's quoted TODO suggested one: whether `ldelem` and `stelem` should keep a
path that yields a `TypeInfo`, or be given a `ConcreteTypeHandle` the scope can supply. #2009 already made that choice
for the metadata path, and the scope gives a `ConcreteTypeHandle` directly, so the fix is the same arm `newarr` and
`ldelema` have.

The nearest thing to an alternative is to fold the three copies of "type operand to `ConcreteTypeHandle`" (in
`executeNewarr`, `executeLdelema` and `elementTypeOperand`) into one function. That is an adjacent variant, not a
different design. It is also a behaviour change in its own right: `executeNewarr` concretises against the frame's
`MethodState.Generics`, the other two against `ctx.CurrentMethod.Generics`, and `ResolvedMetadataOperand`'s remarks keep
the ops' metadata paths apart for that reason. It does not belong in this change.

## Questions for the decider

1. **Should the stack-shape analysis's refusal of `stelem void` (and `box void`, `stobj void`) on an empty stack become
   an issue of its own?** Measured above: real .NET raises `InvalidProgramException`, PawPrint stops the run.
   Recommended: yes, a separate issue covering every opcode whose stack effect ignores the token, in both token
   universes. The new guest leaves out the bare `stelem`, and keeps the bare `ldelem`, which passes.
2. **Should the width-mismatched `ldelem` become an issue, or a `docs/divergences.md` entry?** It is unverifiable IL
   that no emitter measured here writes, and it is the same in both universes. Recommended: an issue, low priority,
   with the two measurements, and nothing in this change.
3. **Should Microsoft.Extensions.DependencyInjection get a guest now?** It needs a `PackageReference`, which belongs in
   the consumer-cases guest project #2092 is to get, not in the test project. Recommended: no; this change uses the
   `System.Linq.Expressions` compiler as its consumer check, the PR body records the DI measurement, and the DI modes go
   into that project when it exists.

## Test design

Write the tests first, in a commit of their own, with the failing output in the commit message: the TODO stop for the
guest, and the assertion failures for the two unit tests. Then the fix. Run the default suite, and the four Guest
categories as a separate invocation, each holding a lock slot.

### Unit tests, in `TestIlDecoding.fs`

That file already maps each `UnaryMetadataTokenIlOp` case to its `System.Reflection.Emit.OpCodes` entry and encodes
operands (`opCodes`, `caseNameOverrides`, `encode`), with `OpCodes` as an oracle independent of PawPrint.

1. **The classifier agrees with each opcode's operand type.** Over every `UnaryMetadataTokenIlOp` case: an
   `InlineType` opcode is `ScopeOperandKind.Type` unless it is in a pinned refusal set; an `InlineMethod` opcode is
   `Method` unless pinned; an `InlineField` opcode is `Field`; `InlineTok` is `AnyType`; `calli`, the one `InlineSig`, is refused. The refused set is exactly
   `{Jmp, Calli, Ldftn, Ldvirtftn, Constrained, Cpobj, Mkrefany, Refanyval}`. Exhaustive rather than random, since
   there are thirty-one cases. Fails before the fix on `Ldelem` and `Stelem`.
2. **The decoder obeys the classifier, for every token** (FsCheck through `CoverageSample.check`, as the file's other
   properties). Generate a case and a 32-bit token, encode the instruction, decode it in `IlTokenUniverse.DynamicScope`.
   The property: for `Type`, `Method` and `Field`, the result is `FromDynamicScope (token &&& 0x00FFFFFF)`; for
   `NotYetSupported`, decoding throws; for `ldtoken`, it is `FromDynamicScope` exactly when the tag is TypeDef or
   TypeRef. Coverage labels: each kind, and a token whose high byte is not a type tag. Nothing in the suite decodes in
   the `DynamicScope` universe today. Fails before the fix on `Ldelem` and `Stelem`.

### Guest: `sourcesImpure/DynamicMethodArrayElementToken.cs`

New, registered in `TestImpureCases.fs` beside `DynamicMethodTypeToken.cs`: `IsDynamicCodeSupported` true,
`ExpectedReturnCode = 0`, `OraclePolicy.Never` like its siblings (the oracle cannot seed that switch). One exit code per
check, returning the number of the first that fails. Exceptions are compared by exact type, and by message where the
table gives one (`new InvalidProgramException().Message` for the default). Signatures may name the guest's own structs:
`ELEMENT_TYPE_INTERNAL` is decoded now (`InternalSignatureTypeResolution.fs`).

| # | check | expected |
|---|---|---|
| 1 | the issue's snippet | 2020 |
| 2, 3 | `ldelem string` at 1 of `{"a","b"}`; `stelem string` "z" at 1 | `"b"`; `{null, "z"}` |
| 4, 5 | `ldelem int32` at 1 of `{5,6}`; `stelem int32` 99 at 1 | 6; `{0, 99}` |
| 6, 7, 8 | `ldelem int16` of -2; `ldelem uint8` of 200; `ldelem char` of U+FFFE | -2; 200; 65534 |
| 9 | `stelem int16` of 0x12345 | 0x2345 |
| 10, 11, 12 | `ldelem int64` of 0x123456789A; `ldelem float32` of 1.5; `stelem float64` 2.25 | as given |
| 13, 14 | `ldelem Big` at 1; `stelem Big` at 1, where `Big` is three `long`s | `(2,3,4)`; index 0 untouched, `(5,6,7)` |
| 15, 16 | `ldelem` / `stelem` of a struct holding a `string` and an `int` | `("q",8)`; `("r",9)` |
| 17 | `ldelem List<int>` | the same object |
| 18, 19 | `ldelem` / `stelem` of `KeyValuePair<int,string>` | `(3,"t")`; `(4,"u")` |
| 20, 21 | `ldelem DayOfWeek`; `ldelem int?` at 1 of `{null, 12}` | Friday; 12 |
| 22 | `ldelem int*` of an `int*[]`, then `ldind.i4` | 77 |
| 23 | `ldelem object` of a `string[]` | the string |
| 24 | `stelem object` of a non-string into a `string[]` | `ArrayTypeMismatchException` |
| 25 | `stelem string` into a `string[]` passed as `object[]` | stored |
| 26, 27 | `ldelem int32` of a `uint[]` holding 0xFFFFFFFF; of a `DayOfWeek[]` holding Saturday | -1; 6 |
| 28 | `stelem int32` of 2 into a `DayOfWeek[]` | Tuesday |
| 29, 30 | `ldelem int32` of a null array; of index 5 of an `int[1]` | `NullReferenceException`; `IndexOutOfRangeException` |
| 31 to 45 | for each of `void`, `int&`, `List<>`, `List<>`'s `T`, `List<TKey>` over `Dictionary<,>`'s `TKey`: `ldelem` of a null array, `ldelem` of an `int[1]`, `stelem` into a null array | `InvalidProgramException`, default message |
| 46, 47, 48 | the same three bodies over `int` | `NullReferenceException`, 1, `NullReferenceException` |
| 49 to 52 | `ldelem` and `stelem` of a null array over `Span<int>` and over `TypedReference` | `NullReferenceException` |
| 53, 54 | `ldelem void`, `ldelem List<>`, nothing on the stack | `InvalidProgramException` |
| 55 to 68 | for `ldelem` and `stelem`: the rewritten-entry rows above, in order (control, `"not a type"`, `null`, `default(RuntimeTypeHandle)`, method handle, field handle, `void` handle) | as in that table |
| 69 | `Expression.ArrayIndex` over a `DateTime[]` at 1, compiled; also checks the delegate's method has no declaring type | 2002, and it does not |
| 70 | `Expression.NewArrayInit` of two `Big`s, compiled | length 2, `[1].C` 3 |

Checks 46 to 52 are what keep the refusal honest: a refusal widened to every value type, or to byref-like types, fails
there. Checks 31 to 45 and 53 to 68 are the only ones that see which entry `ldelem` resolved, because `ldelem` reads
the stored element whatever the token says. Checks 14, 16 and 19 see `stelem`'s coercion to the resolved type.

**Controls on real .NET.** Before committing: the guest, published as the probe was, exits 0 on real .NET 10 and on
11 by roll-forward; and each expectation, perturbed one at a time, exits with that check's number on both. Under
PawPrint on `main`, the guest stops with the TODO at check 1 (the decoder refuses it when it is minted).

The guests run on the linux-x64 CoreLib in CI, where the probes ran on osx-arm64's. Nothing here depends on the
flavour: `DynamicILGenerator`, the Expression compiler and the opcodes are the same code in both.

## Mutants

Each must turn a test red. Record which.

| mutant | killed by |
|---|---|
| `Ldelem` and `Stelem` left `NotYetSupported` | guest (stops at check 1); unit tests 1 and 2 |
| only one of them moved to `Type` | guest (stops at check 1, whose body holds both); unit tests |
| either classified `AnyType` | guest (`TypeOperand`'s BUG `failwith`); unit test 1 |
| either classified `Field` or `Method` | guest (2 onwards answer a refusal); unit test 1 |
| `elementTypeOperand`'s `FromScope` arm returns a fixed type (`int32`, or `object`) instead of the handle | 31 to 36, 53 (refusal lost); 14, 16, 19 (wrong coercion) |
| `refuseTypeOperand` skipped for a scope operand in `ldelem` or `stelem` | 31 to 36 |
| `closedType`'s narrowing bypassed for these opcodes (an unnarrowed read) | 37 to 45, 54 |
| `executeLdelem` pops before the refusal | 53 |
| the decoder keeps the tag in the index for these opcodes | guest (entry past the end); unit test 2 |
| the scope handle taken in `executeLdelem` alone, leaving `elementTypeOperand` as it is | guest (check 1's `stelem` stops on `ctx.ActiveAssembly`) |

`stelem` coercing to the array's element type instead of the token's survives, and is equivalent on verifiable IL: the
two differ only when they differ in width, which is question 2's unverifiable case.

## Probe files

The probes were not kept. One console program, built for .NET 10 and run as is and rolled forward to .NET 11, and
published self-contained for PawPrint (also built for .NET 11 and published against it, for PawPrint on .NET 11's
CoreLib). Its default mode printed one line per check, `name: value ...` or `name: throws <type> [<message>]`, so the
three outputs could be compared with `diff`; further modes ran the bare opcodes one per process, the width mismatches,
and the Expression compiler. A second program referenced Microsoft.Extensions.DependencyInjection 9.0.0 and had the
seven DI modes above; Fable's own scenario was rerun from its earlier publish. PawPrint's IL dump was run over
`ILEmitResolverBuilder` and `ILGen.EmitLoadElement`. The rewrite helper, as `DynamicScopeRewrittenEntries.cs` has it:

```csharp
static List<object> Tokens(ILGenerator il)
{
    object scope = il.GetType().GetField("m_scope", Any).GetValue(il);
    return (List<object>)scope.GetType().GetField("m_tokens", Any).GetValue(scope);
}
// The slot holding typeof(int)'s handle, which must be the only one.
int slot = SlotOf(Tokens(il), typeof(int));
```
