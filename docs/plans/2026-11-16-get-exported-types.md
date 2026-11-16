# `AssemblyNative_GetExportedTypes` (issue #2269)

Status: decided and implemented as option B (see "### As built" at the end). The study below was measured 2026-11-16 on `origin/main` at `3381ac09c`.

Every probe, fixture, throwaway and log named below is in this session's scratchpad,
`/private/tmp/claude-501/-Users-patrick-Documents-GitHub-WoofWare-PawPrint--claude-worktrees-test-pipeline-performance-724897/ba89fbb1-81d8-460f-a9be-c4df3a5bd899/scratchpad/2269/`
(written `2269/` below). Real .NET means 10.0.14, and 11.0.0-rc.3.26522.148 reached by rolling the same build forward
(`DOTNET_ROLL_FORWARD=LatestMajor`, `DOTNET_ROLL_FORWARD_TO_PRERELEASE=1`). Every real-.NET result below is the same on
both runtimes unless it says otherwise. PawPrint runs used the Release App built from `3381ac09c`, on .NET 10's CoreLib,
with the guest published self-contained for osx-arm64. Each claim is marked *measured* or *read*.

## What it is

### Who reaches it

`Assembly.GetExportedTypes()` and `Assembly.ExportedTypes` (whose getter calls it, `Assembly.cs:72-77`) reach
`RuntimeAssembly.GetExportedTypes`, which is the same on both runtimes (`RuntimeAssembly.cs:237-247`, read):

```csharp
Type[]? types = null;
RuntimeAssembly runtimeAssembly = this;
GetExportedTypes(new QCallAssembly(ref runtimeAssembly), ObjectHandleOnStack.Create(ref types));
return types!;
```

So the QCall must always write the array, including an empty one: a skipped write is a `NullReferenceException` in
the caller, as for `RuntimeModule_GetTypes`.

`AssemblyBuilder.GetExportedTypes()` is managed and throws `NotSupportedException` without reaching the QCall
(`AssemblyBuilder.cs:63-64`, read). PawPrint already does the same (measured, `t1-dynbuilder.log`). But a type that
`TypeBuilder.CreateType` returns reports the builder's internal `RuntimeAssembly` as its `Assembly`, not the
`AssemblyBuilder`. So `created.Assembly.GetExportedTypes()` reaches the QCall on a dynamic assembly (measured,
`exported/real10.log`, cases `dyncreated*`).

### What the QCall does

`AssemblyNative_GetExportedTypes` is `vm/assemblynative.cpp:806-921` on .NET 10 and `:819-934` on .NET 11. The two
bodies are textually identical (read, by diffing them). It runs in three steps.

1. **TypeDefs** (`:816-846` on .NET 10). It walks the TypeDef table with `EnumTypeDefInit`, which starts at row 2: row 1, the global type, is
   skipped by its position, not its name (`md/runtime/mdinternalro.cpp:194-217`, read). For each row it takes the
   flags. While the current type has a NestedClass row and its flags are `NestedPublic`, it replaces the flags with
   its encloser's. Then it keeps the row if the flags are `Public`. A kept row is loaded with
   `ClassLoader::LoadTypeDefThrowing(..., PermitUninstDefOrRef)`, so a generic type comes back as its open definition.
2. **ExportedTypes** (`:850-894`). It walks the ExportedType table. For each row it follows the `Implementation` column through
   parent ExportedType rows while the flags are `NestedPublic`, as step 1 does through enclosers. It keeps the row if
   that walk ends at a `File` token and the flags there are `Public`. A forwarder ends at an `AssemblyRef`, so it is
   never kept. A kept row is loaded by name through the assembly's class loader, which has to load the File's module.
3. **The array** (`:905`). `AllocateObjectArray(count, CoreLibBinder::GetClass(CLASS__TYPE))`, and `CLASS__TYPE` is
   `System.Type` (`vm/corelib.h:992`, read). So the array is a `Type[]`. `RuntimeModule_GetTypes` instead allocates
   `CLASS__CLASS`, a `RuntimeType[]`. Each element is the loaded type's `GetManagedClassObject()`, the same object as
   `typeof`.

Nothing catches a load in steps 1 or 2. The first load that throws ends the QCall, and the exception reaches the
caller unwrapped. `RuntimeModule_GetTypes` instead collects its rows' failures into a `ReflectionTypeLoadException`.

Step 2 can never return a type on CoreCLR. `ModuleBase::LoadModule` reads the File row's name and then unconditionally
throws `EEFileLoadException(name, COR_E_MULTIMODULEASSEMBLIESDIALLOWED)` (`vm/ceeload.cpp:2573` on .NET 10, `:2584`
on .NET 11, read). `GetModuleIfLoaded` returns null for every File token except nil, so the module is never already
loaded (read). PawPrint's `AssemblyNative_GetModules` handler records the same fact and refuses File rows for it
(`Native/NativeRuntimeAssembly.fs:2450-2481`).

### What is native and what is managed

All of the selection, the loading and the array are native, so the handler models all of it. The managed wrapper and
`AssemblyBuilder`'s override already run under PawPrint.

### The order

The array holds the kept TypeDefs in ascending row order, then the kept ExportedTypes in ascending row order. Measured:
on a Roslyn assembly whose TypeDef table puts every top-level type before the nested ones, the array is in token order
(`exported/real10.log`, case `self`). The ExportedType half of the order is not observable, because its first element
always throws. What is observable is that the TypeDefs come first: an assembly with both a failing public TypeDef and a
public File export throws the TypeDef's `TypeLoadException`, not the `FileLoadException` (measured, `gen/load10.log`,
`multibad`).

## Measurements

### Real .NET over the visibility matrix (`exported/Exported.cs`, `exported/real10.log`, `real11.log`)

The probe's own assembly declares every visibility C# can write, generics, a `file` type, and three forwarders:
`[assembly: TypeForwardedTo]` of `StringBuilder`, `Environment` and `Dictionary<,>`. Roslyn writes the forwarders'
nested public types as ExportedType rows too, 14 rows in all (`gen/dump.log`). The result, on both runtimes, is these
21 types in this order, as a `System.Type[]`:

```
PublicTop, GenericTop`2, StaticTop, IPublic, PublicStruct, PublicEnum, PublicDelegate, AbstractTop, SealedTop, Program,
Ns.Inner.InNamespace, PublicTop+NestedPublic, PublicTop+NestedGeneric`1, PublicTop+NestedDelegate, PublicTop+NestedEnum,
PublicTop+INested, PublicTop+NestedStruct, GenericTop`2+Nested`1, Ns.Inner.InNamespace+NestedInNamespace,
PublicTop+NestedPublic+NestedPublicDeep, PublicTop+NestedGeneric`1+InnerOfGeneric
```

- Excluded: the internal top-level types; `file class FileLocal`; every nested `protected`, `internal`,
  `protected internal`, `private protected` and `private` type; and every nested public type under one of those or
  under an internal type (`InternalTop+NestedPublicInInternal`, `+Deeper`, `NestedProtected+InsideProtected`, ...).
- Every generic one, including `InnerOfGeneric`, which has no parameters of its own, is a generic type definition.
- No forwarded type appears. `GetForwardedTypes()` returns 14.
- Two calls return different arrays with the same elements. Element 0 is the object `typeof(PublicTop)` returns.
  `ExportedTypes` is the same `Type[]` sequence.
- `GetExportedTypes()` equals `GetTypes().Where(t => t.IsVisible)`, element by element, for this assembly and for
  CoreLib, on both runtimes (case `visible`). That holds for any single-module assembly whose types all load. It gives
  a guest an oracle that does not depend on the runtime version.
- CoreLib: 1378 types on .NET 10 and 1447 on .NET 11, starting at
  `Microsoft.Win32.SafeHandles.CriticalHandleMinusOneIsInvalid` and ending at `Internal.Console+Error`.
- `System.Runtime`: an empty array. It is a facade whose types are all forwarders.

### Real .NET over fixtures Roslyn cannot put in one guest (`gen/Gen.cs`, `gen/load10.log`, `load11.log`, `gen/dump.log`)

`Gen.cs` compiles each fixture with Roslyn 4.14, loads it with `Assembly.LoadFrom` in a process of its own, and calls
`GetExportedTypes`, then `GetTypes`.

| fixture | `GetExportedTypes` | `GetTypes` |
|---|---|---|
| `badlayout`: public `GoodBefore`, public explicit-layout `BadPublic` with an `object` field overlapped by an `int`, an internal one like it, public `GoodAfter` | `TypeLoadException` 0x80131522 "Could not load type 'BadPublic' ... incorrectly aligned or overlapped by a non-object field." | `ReflectionTypeLoadException`, 2 loaded, 2 loader exceptions |
| `badinternalonly`: public `Good`, the internal bad struct | `[Good]`: the internal type is never loaded | `ReflectionTypeLoadException` |
| `usesmissing`: public `Derived : MissingBase`, where `MissingBase`'s assembly is not on disk | `FileNotFoundException` 0x80070002 for `MissingDep` | `ReflectionTypeLoadException` |
| `multi`: manifest with public `ManifestPublic`, and File-implemented ExportedType rows for a netmodule's `ModPublic`, `ModPublic+ModNested`, `+ModDeep` and `ModGeneric`1`, with the netmodule beside it | `FileLoadException` 0x8013101E "Could not load file or assembly 'Mod.netmodule'. The module cannot be loaded because only single file assemblies are supported. (0x8013101E)", `FileName = "Mod.netmodule"` | the same `FileLoadException` (from `GetModules`) |
| `multinomod`: the same manifest without the netmodule | the same `FileLoadException` | the same |
| `multibad`: `multi`'s exports plus a public bad struct in the manifest | the struct's `TypeLoadException` | the `FileLoadException` |
| `vis-nestedpublic1`: a nested type patched to visibility `Public` (1) under an internal encloser | `BadImageFormatException` 0x8007000B: CoreCLR's walk keeps the row, then the loader refuses it | `ReflectionTypeLoadException` |
| `vis-toplevelnested`: a top-level type patched to `NestedPublic` | the other three public types; the patched one is never loaded | `ReflectionTypeLoadException` (`TypeLoadException` for the patched type) |
| `vis-nestednotpublic`: a nested type patched to `NotPublic` under a public encloser | the three public top-level types | `ReflectionTypeLoadException` (`BadImageFormatException`) |
| `vis-control`: the unpatched image | `Outer, Top2, Top3, Outer+InnerOfPublic` | 6 types |

Roslyn writes File-implemented ExportedType rows only for a netmodule's externally visible types, with flags `Public`
or `NestedPublic`. It writes a forwarder's nested rows with flags 0 (`NotPublic`), so CoreCLR's walk stops at once on
them and finds a parent ExportedType rather than a File (`gen/dump.log`).

### Real .NET over a dynamic assembly (`exported/real10.log`, cases `dyn*`)

One module defines public `DynPublic`, internal `DynInternal` and public `DynLater`, and creates all three:
`[DynPublic, DynLater]` (`dyncreatedflat`). With a public nested type as well: `[DynPublic, DynPublic+DynNested, DynLater]`
(`dyncreated`). Leaving `DynLater` uncreated: `TypeLoadException` "Could not load type 'DynLater' from assembly 'DynC,
...'." (`dynuncreated*`).

### PawPrint on `main`

Every case that calls the QCall stops at `Unimplemented native method (PInvokeImpl QCall!AssemblyNative_GetExportedTypes)`
(measured, `main-self.log`). Nothing before it stops.

### PawPrint with a throwaway of the real rule (`throwaway.diff`, reverted)

The throwaway added a handler in `Native/NativeRuntimeModule.fs` that selects as CoreCLR does (TypeDefs in row order
from row 2, kept when every encloser up to a `Public` top-level type is `NestedPublic`; a `failwith` for a kept File
export), loads each row as `RuntimeModule_GetTypes` does, and writes a `Type[]`.

- `self`: identical to real .NET, all 21 types in order, every check (`t1-self.log`). One divergence that is not this
  issue's: `GetForwardedTypes()` returns 0 where real .NET returns 14, which is #1322 (`MetadataImport.Enum(ExportedType, 0)`
  answers an empty list).
- `corelib`: 1378 types with the same first and last elements as real .NET 10, in a 4 s run (`t1-corelib.log`).
- `sysruntime`: an empty `Type[]` (`t1-sysruntime.log`).
- `visible`: `GetExportedTypes()` equals `GetTypes().Where(IsVisible)` for the guest's assembly and for CoreLib, in a
  10 s run (`t6-visible.log`).
- `dyncreatedflat`: `[DynPublic, DynLater]`, identical (`t1-dyncreatedflat.log`). `dyncreated` stops earlier, at
  `TypeBuilder_DefineType`'s TODO for a nested type ("an emit scope has no NestedClass table yet"), which is not this
  issue. `dynuncreatedflat` stops at the existing TODO in `TypeConcretisation.intern`: "loading DynLater ... which
  CreateType has not created; CoreCLR raises the TypeResolve event for it instead of loading it". That is the right
  place for it, and it is not this issue either.

### What PawPrint already has

- **The TypeDef half of the rule.** `TypeVisibility.isTypeDefExternallyVisible` (`Domain/TypeVisibility.fs`) is
  CoreCLR's `IsTypeDefExternallyVisible` (`vm/siginfo.cpp:3190`), which `Type.IsVisible` uses. CoreCLR's two walks
  agree on every chain, malformed ones included: each stops at the first level, innermost first, that is not
  `NestedPublic`, and keeps the type if that level is `Public`, whether or not it has an encloser (read). PawPrint's
  function differs from both only on a chain with a `Public` level that is not the outermost, which it rejects. That chain cannot reach it: `TypeInfo.fs:612-632` refuses at read any image whose
  visibility and NestedClass row disagree, as the `vis-*` fixtures' images do. `TestTypeVisibility.fs` and
  `TestIsVisibleSameImage.fs` already test the function against a model and against the host.
- **The ExportedType rows.** `DumpedAssembly.ExportedTypes` holds each row as an `ExportedType` (`Domain/ExportedType.fs`)
  with its flags and its implementation as `ForwardsTo`, `ParentExportedType` or `AssemblyFile`. `LoadedTypeResolution`
  answers `Unmodelled` for an `AssemblyFile` export.
- **Loading rows into `RuntimeType` objects.** `NativeRuntimeModule.getTypes` (the `RuntimeModule_GetTypes` handler)
  does exactly the load step: `lookupTypeDefn`, then `runtimeTypeHandleTargetForTypeToken` with
  `allowOpenGenericDefinition = true`, then `getOrAllocateType`, then `allocateArray` and `writeManagedByrefWithBase`.
  A row it cannot load stops the run; its comments say why it does not raise a `ReflectionTypeLoadException`.
  `runtimeTypeHandleTargetForTypeToken` has no error channel, so a load failure cannot be raised in the guest from
  there today.
- **Raising `FileLoadException`.** `NativeHandlerResult.RaiseException` can set `Message`, `HResult` and
  `FileNotFoundException`'s file name (`RuntimeExceptionField`), but not `FileLoadException.FileName`. CoreCLR builds
  the exception with the private `FileLoadException(string fileName, int hResult)` constructor, which calls two QCalls
  that PawPrint does not implement (`GetFileLoadExceptionMessage`, `FileLoadException_GetMessageForHR`).

## Surprises against the issue's description

1. "Then each ExportedType row whose implementation is a File": on CoreCLR such a row, if public, never yields a type.
   It always throws `FileLoadException` 0x8013101E naming the File row, whether the module is on disk or not.
2. A load failure is not wrapped. `TypeLoadException`, `FileNotFoundException` and `BadImageFormatException` each
   escape as themselves. A type that is not exported is never loaded, so its failure is never seen.
3. The rule as CoreCLR codes it keeps a nested type whose own visibility is `Public`, whatever its encloser, and then
   fails to load it. PawPrint refuses such an image at read, so the difference cannot be observed.
4. The array is a `Type[]`, not the `RuntimeType[]` that `GetTypes` returns.
5. FakeItEasy cannot tell the real rule from the planning shortcut: the only assembly it asks about is the program's
   own, which has no nested types (below).

## Options

Every option writes a fresh `Type[]` holding the selected TypeDefs' `RuntimeType` objects in row order, reuses the load
step of `RuntimeModule_GetTypes`, and leaves an uncreated dynamic type to the existing TypeResolve TODO. They differ in
where the selection lives and what it is.

### A. The selection inside the handler

The `AssemblyNative_GetExportedTypes` handler in `Native/NativeRuntimeAssembly.fs` filters `assembly.TypeDefs` by
`TypeVisibility.isTypeDefExternallyVisible`, sorts by row, and walks `assembly.ExportedTypes` for a public File export
inline, as the throwaway did.

- Tested only through guests. A guest cannot contain a File export, so that branch is tested by nothing.
- Blast radius: one handler and its registration, plus the shared load helper (question 4).
- Reversibility: total.
- Information kept: none outside the handler; nothing else can ask "what does this assembly export?" without running
  the interpreter.

### B. The selection as data in Domain, which the handler interprets

A function beside `TypeVisibility` in `WoofWare.PawPrint.Domain` returns what CoreCLR's two walks select, without
loading anything:

```fsharp
type ExportedTypeSelection =
    {
        /// The TypeDefs CoreCLR loads, in ascending row order.
        Definitions : TypeDefinitionHandle list
        /// The ExportedType rows CoreCLR loads through a File, in ascending row order, each with the File it names.
        FileExports : (ExportedTypeHandle * AssemblyFileHandle) list
    }

ExportedTypeSelection.ofAssembly : DumpedAssembly -> ExportedTypeSelection
```

The TypeDef half calls `isTypeDefExternallyVisible`. The ExportedType half is a new walk over `ExportedTypes`, written
over the rows so that it can be tested on generated rows. The handler loads each of `Definitions`, then acts on the
first of `FileExports`, if any, as question 2 decides.

- Domain is the lowest project that can hold it: the selection reads one image and binds nothing. `TypeVisibility`,
  CoreCLR's other walk over the same chain, is already there. `WoofWare.PawPrint.Analysis` and any later consumer can
  ask without the interpreter.
- Testable as a pure function against the host over Roslyn images, File exports included (netmodule fixtures), and
  against a transcription of CoreCLR's loop over generated rows (test design, below).
- Blast radius: a new Domain file and type. Domain is a published package, so the type is public API of that package.
  The handler and the shared load helper are as in A.
- Reversibility: high. The type has one consumer, and folding it back into the handler is mechanical.
- Information kept: all of it. `FileExports` keeps every row although the interpreter can only ever act on the first,
  so a consumer that wants to report them all can.

### C. Load every TypeDef, then keep the visible ones

Reuse `RuntimeModule_GetTypes` whole: load every row, then keep those whose `RuntimeType` `IsVisible` (or whose concrete
handle `ConcreteTypeVisibility.isExternallyVisible` accepts), as `GetTypes().Where(IsVisible)` does in managed code.

- Smallest new code, and the measured equivalence says it gives the right array whenever every type loads.
- Wrong where a non-exported type fails to load: CoreCLR never loads it (`badinternalonly` returns `[Good]`), but this
  loads it. Under PawPrint that is a stop where CoreCLR succeeds, and once PawPrint refuses more bad types (#2276, for
  the overlapping layout), every such program stops. It also concretises every internal type of the assembly, which for
  CoreLib is about 1300 extra types.
- Blast radius: small now; it grows with every type-load refusal PawPrint adds.
- Reversibility: total.
- Information kept: none.

### Rejected: the planning shortcut

Top-level public TypeDefs only. It is not the rule. It drops `PublicTop+NestedPublic` and every other nested public
type, and no consumer measured needs it to be faster than the rule.

## Questions for Fable

1. **Where does the selection live: A, B or C?** Recommended: B. The rule is a fact about one image, and it belongs
   next to `TypeVisibility`, where its other half already is. B is the only option under which the File branch is
   tested at all, and it can be tested against the host without running a guest. C is rejected on a measurement: it
   loads types CoreCLR never loads.

2. **What does a public File-implemented export do?** CoreCLR always throws `FileLoadException`, `HResult` 0x8013101E,
   `FileName` the File row's name, message "Could not load file or assembly '<name>'. The module cannot be loaded
   because only single file assemblies are supported. (0x8013101E)", after loading every exported TypeDef.
   - (a) Refuse: a `failwith` naming the assembly, the ExportedType row and the File, as `AssemblyNative_GetModules`
     does for File rows.
   - (b) Raise it in the guest. That needs a `RuntimeExceptionField` case for `FileLoadException.FileName` (an
     auto-property's backing field), and the message written out from `mscorrc`'s resource for the HRESULT, since the
     constructor CoreCLR calls needs two unimplemented QCalls.
   Recommended: (a), to match `GetModules`. No compiler PawPrint's users run writes a multi-module assembly that a
   CoreCLR program uses, and when one is wanted, (b) is the same change for both QCalls and should be made for both at
   once. `GetModules`' comment says the raise machinery "can set neither" `FileName` nor `HResult`; `HResult` can now
   be set, so whoever does (b) should update that comment.

3. **What does a row that fails to load do?** CoreCLR lets the exception escape unwrapped. PawPrint's load step has no
   error channel, so every failure is a host stop today.
   - (a) Stop, as `RuntimeModule_GetTypes` does.
   - (b) Make the load step return the failure, and raise CoreCLR's exception for each failure PawPrint models
     (`TypeAbsent` and the type-load refusals as `TypeLoadException`, an unavailable assembly as
     `FileNotFoundException`), stopping only for `Unmodelled`.
   Recommended: (a). (b) changes `runtimeTypeHandleTargetForTypeToken`, which many callers share, and it is the same
   change that would let `GetTypes` raise its `ReflectionTypeLoadException`, so it should be its own issue for both.
   Because GetExportedTypes never wraps, (a) cannot hand the guest a short array: a stop is the only wrong outcome.

4. **Where does the shared load step go?** `getTypes` in `Native/NativeRuntimeModule.fs` holds it privately.
   `NativeRuntimeAssembly.fs` compiles before `NativeRuntimeModule.fs`, so the handler cannot call it there.
   Recommended: move the load step (rows in, `RuntimeType` addresses out, then an array of a named CoreLib element type
   written through the `ObjectHandleOnStack`) into `NativeRuntimeTypeHelpers`, which both files see, taking the element
   type as `RuntimeType` or `Type`, and have both handlers call it. The other way, putting the new handler into
   `NativeRuntimeModule.fs`, puts an `AssemblyNative_*` QCall among the `RuntimeModule_*` ones.

5. **Does this PR test the dynamic-assembly path?** `created.Assembly.GetExportedTypes()` over created flat types already
   works with the throwaway. Recommended: yes, one `sourcesImpure` guest over created flat types (public, internal,
   public). Leave the uncreated case to the TypeResolve TODO, which is a stop, not a wrong answer, and leave nested
   types to the NestedClass table, which the emit scope does not have yet.

6. **Does the guest also check `GetExportedTypes() == GetTypes().Where(IsVisible)` over CoreLib?** It costs about 6 s
   more per run than the guest's own checks (measured: 4 s for CoreLib's `GetExportedTypes` alone, 10 s with
   `GetTypes` and `IsVisible` too). Recommended: yes. It is the one check that covers 1300 rows of a real image on
   whichever runtime the suite runs, and it needs no expected list that a runtime bump would invalidate. Its weakness
   is that with B, both sides end in `isTypeDefExternallyVisible`; the order, the array type and the load step are
   still checked independently, and the predicate has its own host oracle.

## Test design

### Pure tests of the selection (with B)

In `WoofWare.PawPrint.Test`, beside `TestIsVisibleSameImage.fs`, whose fixture and host-loading machinery they can
reuse.

1. **Same-image oracle over generated assemblies (FsCheck).** Generate a forest: top-level types `public` or
   `internal`, optionally in a namespace, optionally `file`; nested types of the six nested visibilities to depth 3;
   generic arity 0 to 2 at each level; and a subset of CoreLib types to forward with `[assembly: TypeForwardedTo]`,
   some of them with nested public types (`Environment`, `Dictionary<,>`, `StringBuilder`). Compile it with Roslyn.
   Load the bytes into a collectible `AssemblyLoadContext` on the host and take the metadata tokens of
   `GetExportedTypes()`. Read the same bytes into a `DumpedAssembly`. Property: `Definitions` is that token sequence,
   in that order, and `FileExports` is empty. This checks order, both halves of the TypeDef rule and the exclusion of
   forwarders and their nested rows.
2. **Same-image oracle over netmodule fixtures.** Generate a netmodule's forest the same way, compile it with
   `OutputKind.NetModule`, and compile a manifest that references it, with only internal types of its own (as
   `gen/Gen.cs` does). On the host, `GetExportedTypes()` throws `FileLoadException`, whose `FileName` is the module's
   name, exactly when the module has an externally visible type. Property: `FileExports` is non-empty exactly then,
   and its first element names a File row with that name. The host cannot check the manifest's own `Definitions` in
   such an assembly, because the File export throws first; test 1 covers them.
3. **Model test over generated ExportedType rows.** Build `ExportedType` records directly: chains of
   `ParentExportedType` rows ending at an `AssemblyFile` or a `ForwardsTo`, each with any of the eight visibilities,
   including shapes no compiler writes (a `NestedPublic` row under a `NotPublic` File export, a `Public` row under a
   parent). The model is CoreCLR's loop transcribed literally from `assemblynative.cpp:850-894` (.NET 10). Property:
   the walk keeps exactly the rows the model keeps, in row order. This is the only test of the walk on malformed
   chains, because PawPrint reads ExportedType flags without checking them against the chain.

`TestTypeVisibility.fs` already covers `isTypeDefExternallyVisible` against a model of every chain.

### Guests

**`sourcesPure/AssemblyGetExportedTypes.cs`**, shaped like `ModuleGetTypes.cs`, each check returning its own exit code:

- The visibility matrix of `exported/Exported.cs` (without the dynamic cases): the array is exactly the 21 names
  above, in that order, compared with `FullName`.
- `GetExportedTypes().GetType() == typeof(Type[])`, not `RuntimeType[]`.
- Two calls give different arrays with the same elements; element 0 is `typeof(PublicTop)`.
- `ExportedTypes` gives the same sequence.
- `GenericTop`2`, `GenericTop`2+Nested`1` and `PublicTop+NestedGeneric`1+InnerOfGeneric` are generic type definitions.
- No element's assembly is another assembly: the three forwarders and their nested types are absent.
- `Assembly.Load("System.Runtime").GetExportedTypes()` is an empty array, not null. This is the check that the QCall
  writes an empty array.
- `GetExportedTypes()` equals `GetTypes().Where(t => t.IsVisible)` for the guest's assembly and, if question 6 says
  so, for CoreLib.

Controls on real .NET: before trusting the guest, the implementer runs it on real .NET 10 and 11 once as written
(exit 0) and once per perturbed expectation, each of which must exit non-zero with its own code: the expected list
with `InternalTop+NestedPublicInInternal` added, with `PublicTop+NestedProtectedInternal` added, with two names
swapped, and with `PublicTop+NestedPublic` removed; `typeof(RuntimeType[])` expected (via `GetType().Name ==
"RuntimeType[]"`); the same array expected from two calls; and a forwarded `StringBuilder` expected.

**`sourcesImpure/TypeBuilderGetExportedTypes.cs`** (if question 5 says yes): `DynPublic`, `DynInternal`, `DynLater`
defined and created; `created.Assembly.GetExportedTypes()` is `[DynPublic, DynLater]`. Registered in
`TestImpureCases.fs` with dynamic code enabled, as the other emit guests are.

### Mutants

Each must turn some test red. The implementer runs each against the pure tests and the two guests.

- Keep the top-level public TypeDefs only (the planning shortcut): guest name list; pure test 1.
- Keep every `NestedPublic` type whatever its enclosers: `NestedPublicInInternal`; pure test 1.
- Keep `NestedFamORAssem` (`protected internal`) as well: guest; pure test 1.
- Take the rows in dictionary order or sorted by name: the guest's order check; pure test 1.
- Keep forwarders: the guest's forwarder check; pure tests 1 and 3.
- Keep a File export whose chain is not public all the way: pure test 3.
- Ignore File exports: pure test 2.
- Allocate `RuntimeType[]`: the guest's array-type check.
- Cache the array between calls: the guest's fresh-array check.
- Skip the write when there are no types: the guest's `System.Runtime` check (`NullReferenceException`).
- Load every TypeDef and filter afterwards (option C): no listed test fails, because every guest type loads. This is
  the mutant that shows C's failure is not covered by a guest; a `badinternalonly`-shaped guest would need PawPrint to
  refuse that layout first (#2276). Record it as surviving, with that reason.
- Drop the skip of TypeDef row 1: equivalent. Roslyn's row 1 is always `<Module>`, which is `NotPublic`, so no test
  can see it. Record it as equivalent.

## FakeItEasy 8.0.0

`fie/Program.cs` references FakeItEasy 8.0.0 (and through it Castle.Core 5.1.1) from the local NuGet cache, so it is
not a guest. It runs one scenario per argument:

- `fake`: `A.Fake<ICalc>()`, then an unconfigured call and property.
- `callto`: `A.CallTo(...).Returns(42)`, `MustHaveHappenedOnceExactly`, and a `MustHaveHappened` that fails, printing
  its message.
- `strict`: a strict fake's unconfigured call throws.
- `args`: `A<int>._`, `That.Matches`, `Throws`, `Invokes`, `ReturnsLazily`, `MustNotHaveHappened`,
  `MustHaveHappened(3, Times.Exactly)`, `Fake.GetCalls`.
- `class`: a fake abstract class with constructor arguments and `CallsBaseMethod`.
- `dummy`: `A.Dummy<string>()` and `A.CollectionOfFake<ICalc>(2)`.

On real .NET 10 and 11 every scenario exits 0 with identical output (`fie/real10.out`, `fie/real11.out`).

**On `main`**, every scenario stops first at `AssemblyNative_GetExportedTypes`, called from
`FakeItEasy.BootstrapperLocator.FindBootstrapper` (`BootstrapperLocator.cs:32`), with or without
`Switch.System.Reflection.ForceInterpretedInvoke` (`fie/main-*.log`). FakeItEasy asks only about the program's own
assembly, which has two or three public top-level types and no nested ones.

**The stops after it**, each passed with a throwaway in `throwaway.diff` (since reverted), one after another:

| # | stop | reached by | from | throwaway |
|---|---|---|---|---|
| 1 | `AssemblyNative_GetExportedTypes` (#2269) | every scenario | `BootstrapperLocator.FindBootstrapper` | the real rule, above |
| 2 | `RuntimeTypeHandle_VerifyInterfaceIsImplemented` (#2270) | `callto`, `args` | `Type.GetInterfaceMap`, from `FakeItEasy.MethodInfoManager.GetMethodOnTypeThatImplementsInterfaceMethod` (`MethodInfoManager.cs:96`) | does nothing; the check passes on real .NET, since the proxy implements `ICalc` |
| 3 | `RuntimeTypeHandle_GetNumVirtualsAndStaticVirtuals` | `callto`, `args` | the same `GetInterfaceMap` | `VirtualSlotLayout.numVirtuals`, not counting static virtuals (`ICalc` has none); answered 2 (`fie/t2-callto.log`) |
| 4 | `RuntimeTypeHandle_GetInterfaceMethodImplementation` | `callto`, `args` | the same `GetInterfaceMap` | `IlMachineStateExecution.tryResolveVirtualImplementation` of the interface method on the proxy type, minted with `MethodHandleRegistry.getOrAllocateConcreteInternalHandle`; instance methods only (`fie/t3-callto.log`) |
| 5 | `Math.Log10` (an `InternalCall` intrinsic) | `callto` | `FakeItEasy.CallInfo.NumberOfDigitsInCallNumber` (`CallWriter.cs:118`), formatting the failed assertion's message | `DeterministicMath.log x / DeterministicMath.log 10.0`, which is deterministic but not correctly rounded (`fie/t4-callto.log`) |

With all five, every scenario exits 0, and the six outputs together are identical to real .NET's
(`fie/compare.sh t5 ...`, `fie/t5-*.log`), without `ForceInterpretedInvoke`. Each run took 13 to 19 s.

So FakeItEasy needs, in total: this issue; the whole of `Type.GetInterfaceMap`, which is #2270 plus two QCalls that
have no issue yet (`GetNumVirtualsAndStaticVirtuals`, `GetInterfaceMethodImplementation`); and `Math.Log10`, which has
no issue yet. None of them is a large feature. `GetMethodAt` and the dispatch resolution that stop 4 needs already
exist; `VirtualSlotLayout`'s remarks already describe how `GetNumVirtualsAndStaticVirtuals` counts; `Math.Log10` needs
a correctly rounded `DeterministicMath.log10` beside `DeterministicMath.log`, and `Semantics/NativeMethod.fs` already
names it (`MathFunction.Log10`).

## Probe files

- `exported/Exported.cs`, `Exported.csproj`, `real.sh`, `publish.sh`, `dynflat.sh`, `pp-visible.sh`: the guest-shaped
  probe. Logs: `exported/real10.log`, `real11.log` (every case), `main-self.log`, `t1-*.log`, `t6-visible.log`.
- `gen/Gen.cs`, `Gen.csproj`, `run.sh`: the Roslyn fixtures. Logs: `gen/dump.log`, `load10.log`, `load11.log`.
- `fie/Program.cs`, `Fie.csproj`, `build.sh`, `pp-all.sh`, `step.sh`, `stops.sh`, `compare.sh`: the FakeItEasy
  consumer. Logs: `fie/real10.out`, `real11.out`, `main-*.log`, `t1-*.log` to `t5-*.log`.
- `throwaway.diff`: every throwaway handler used above.
- `pp.sh`, `dn.sh`, `show.sh`, `cmp.sh`: helpers.

### Decisions

Decided 2026-11-16, against `3381ac09c`. I checked the plan's claims against the source, and measured five more things on real .NET 10.0.14 and 11.0.0-rc.3 (the same result on both, every time). The probe and its logs are in the scratchpad at `2269-decide/probe/` (`Decide.cs`, `modpublic*.log`, `loadbytes.log`, `dynhidden.log`, `arrtype.log`, `ifacemap*.log`, `log10-*.log`). Nothing was run under PawPrint; where a decision predicts PawPrint's behaviour, it says so.

What was measured:

| probe | real .NET |
|---|---|
| `Vis.dll` with TypeDef row 1 (`<Module>`) patched to `Public` | the image loads; `GetExportedTypes` is the same four types as the unpatched control, without `<Module>` |
| the `multi`, `multibad`, `badinternalonly` and patched images, loaded from bytes into a collectible `AssemblyLoadContext` | the same results as `Assembly.LoadFrom` gave the planner, `FileName = "Mod.netmodule"` included |
| a dynamic module with `DynPublic` created, `DynInternal` defined and never created, `DynLater` created | `GetExportedTypes` is `[DynPublic, DynLater]`; `GetTypes` throws `ReflectionTypeLoadException` for `DynInternal` |
| the array's own type | `Module.GetTypes()` and `Assembly.GetTypes()` are `System.RuntimeType[]`; `GetExportedTypes()` is `System.Type[]` |
| `Math.Log10(1000)` against `Math.Log(1000) / Math.Log(10)` | 3 against 2.9999999999999996; also wrong at 1e15, 1e23 and 0.001 |

**1. Option B: the selection is data in Domain, and the handler interprets it.**

- B is chosen. The rule reads one image and binds nothing, so Domain is the lowest project which can hold it.
- A is rejected: under A no test reaches the File branch, because a guest cannot contain a File export.
- C is rejected on two measurements. The planner's: `badinternalonly` returns `[Good]`. Mine: an internal dynamic type that was never created does not stop `GetExportedTypes`, and it does stop `GetTypes`. So C is wrong on a case which a guest can hold today.
- The new file is `Domain/ExportedTypeSelection.fs`, after `TypeVisibility.fs`. It is new public API of a published package; flag that in the PR body.
- The record is as the plan has it, with one change: `FileExports` is a list of a named record (`Export : ExportedTypeHandle`, `File : AssemblyFileHandle`), not of a tuple. The two handles are easy to swap in a tuple pattern only by position; names cost nothing.
- `FileExports` keeps every kept row, not only the first. The interpreter acts on the first. A consumer which reports them all can.
- The module has three functions.
  - The TypeDef rows as CoreCLR's `EnumTypeDefInit` yields them: ascending from row 2, with row 1 skipped by its position. `RuntimeModule_GetTypes` computes the same list inline today (`NativeRuntimeModule.fs:66-72`). Both callers use the one function after this PR. It is a fact about the image, so it may sit beside `DumpedAssembly` rather than in the new module; the implementer chooses.
  - The File-export walk, over the ExportedType rows alone (not over a `DumpedAssembly`), so that the model test can hand it generated rows.
  - `ofAssembly`, which filters the first by `TypeVisibility.isTypeDefExternallyVisible` and calls the second.
- The walk over ExportedType rows is total on the `ExportedTypeData` cases.
  - `ForwardsTo`: not kept.
  - `AssemblyFile`: kept exactly when the flags there are `Public`.
  - `ParentExportedType`: if the flags are `NestedPublic`, continue at the parent with the parent's flags. Otherwise not kept.
  - A nil parent handle ends the walk and the row is not kept, as CoreCLR's `mdImpl != mdExportedTypeNil` test does (read, `assemblynative.cpp:870-872`).
- Two malformed shapes fail with a message naming the row: a parent handle that is not a row of the table, and a cycle of `NestedPublic` rows (bounded by the row count). CoreCLR throws on the first and loops for ever on the second (read). This is `TypeVisibility.nestingChain`'s policy for the TypeDef half.
- The function throws on those shapes and does not return a `Result`. The TypeDef half already throws through `nestingChain`, so a `Result` here would promise a totality which the function does not have. Changing both halves to results is a change to `TypeVisibility`'s public API and is not this PR.
- Docstrings say what a caller needs: the order, that row 1 is skipped by position, that nothing is loaded, and when the function throws.

**2. A public File export is refused, option (a).**

- The handler loads every one of `Definitions` first. Then, if `FileExports` is not empty, it stops with a `failwith`. This order is CoreCLR's (measured by the planner, `multibad`).
- The message names the assembly, the first kept ExportedType row (namespace and name), and the File row which that export names. It does not name File row 1, as the `GetModules` refusal does: the first kept export may be implemented by a later File. CoreCLR's `FileName` is the export's File (read, `ceeload.cpp:2573`).
- (b) is not done here. It needs a `RuntimeExceptionField` case for `FileLoadException.FileName` and CoreCLR's message written out from the resource, and it is the same change for `GetModules`. No consumer measured reaches either. A stop is a refusal, not a wrong answer.
- `RuntimeExceptionField.HResult` exists now (`IlMachineRuntimeMetadata.fs:975`, read). So the `GetModules` comment at `NativeRuntimeAssembly.fs:2468-2473`, which says that the raise machinery can set neither `FileName` nor `HResult`, is wrong about `HResult`. This PR corrects that sentence, because it writes the sibling refusal beside it and the two must give the same true reason: `FileName` cannot be set, and the message needs two QCalls which PawPrint does not implement.
- That is #2287. Both refusals name it.

**3. A row that fails to load stops the run, option (a).**

- `GetExportedTypes` never wraps a failure, so a stop cannot hand the guest a short array.
- (b) changes `runtimeTypeHandleTargetForTypeToken`, which is named in ten places in the interpreter (counted with grep, its definition included). It is the same change which would let `GetTypes` raise its `ReflectionTypeLoadException`. It is filed once for both, as #2288.
- A type which PawPrint loads and CoreCLR refuses (#2276, the overlapped object field) is in the array where CoreCLR throws. That wrong answer is #2276's, and this PR does not widen it: the type is equally wrong at `typeof`.
- The uncreated public dynamic type stays with the TypeResolve TODO in `TypeConcretisation.intern`, as the plan says.

**4. The shared load step moves to `NativeRuntimeTypeHelpers`, as two functions.**

- Both handlers see that module, and `NativeRuntimeAssembly.fs` compiles before `NativeRuntimeModule.fs` (read, `WoofWare.PawPrint.fsproj:236,248`). The handler goes in `NativeRuntimeAssembly.fs`, beside `AssemblyNative_GetModules`.
- The step is two functions, not one, because the File refusal of decision 2 sits between them.
  - Load: an assembly and a list of TypeDef rows in, the `RuntimeType` addresses out in the same order. It takes rows, and never enumerates the table itself.
  - Write: allocate a fresh array of the named element type, store the addresses, and write it through the `ObjectHandleOnStack`. It writes for an empty list too.
- The element type is a two-case union (`RuntimeType`, `Type`), not a namespace and a name. Nothing else is legal here.
- `getTypes` keeps its comments about `ReflectionTypeLoadException` and about `PermitUninstDefOrRef`; the second moves with the load function, since it is true of both callers.
- I considered a file of its own for the two functions. It is cleaner than adding to a 3400-line module, and it is one more named thing for 60 lines. Either is trivially reversible; the helpers module is chosen.

**5. Yes: one `sourcesImpure` guest over a dynamic module, with one type left uncreated.**

- The guest defines `DynPublic`, `DynInternal` and `DynLater`. It creates `DynPublic` and `DynLater`, and never creates `DynInternal`.
- `created.Assembly.GetExportedTypes()` is `[DynPublic, DynLater]` (measured on real .NET). Under B PawPrint never loads `DynInternal`, so it should answer the same; this is predicted, not measured under PawPrint. If PawPrint stops on something else, the implementer reports it and falls back to the plan's version, which creates all three.
- This is the departure from the plan. It makes the guest the killer of the option-C mutant, which the plan records as surviving.
- The guest does not call `GetTypes` on that assembly: real .NET throws there, and PawPrint stops.
- The uncreated public type and the nested type stay out, as the plan says.

**6. Yes: the guest checks `GetExportedTypes()` against `GetTypes().Where(IsVisible)` over CoreLib.**

- It is the only check over a large real image which needs no expected list, so it survives a runtime bump and runs on both CoreLib flavours.
- Its weakness is as the plan says: both sides end in `isTypeDefExternallyVisible`. The pure same-image test is what checks the predicate independently.
- The cost is 6 s per run (the planner's measurement, not repeated).

**7. Missed by the plan: row 1 is not an equivalent mutant.**

- The plan records "drop the skip of TypeDef row 1" as equivalent, because Roslyn's `<Module>` is `NotPublic`.
- A `<Module>` patched to `Public` loads on real .NET, and `GetExportedTypes` leaves it out (measured). PawPrint's reader admits that image: its visibility and its NestedClass row agree (read, `TypeInfo.fs:612-632`).
- So the pure tests get a fixture with row 1 patched to `Public`, compared with the host as the others are. The mutant must then go red.

**8. Missed by the plan: the `GetTypes` array type has no test, and the shared write function puts it at risk.**

- `ModuleGetTypes.cs` does not check the array's own type. Once one function writes both arrays, passing the wrong case for `GetTypes` is a one-word mistake.
- Add to `ModuleGetTypes.cs`: `GetTypes().GetType().GetElementType().FullName == "System.RuntimeType"` (measured on real .NET).

**9. FakeItEasy's later stops: separate PRs, in this order, each with its own issue.**

- #2270 is not widened. Each of the three `GetInterfaceMap` QCalls can be tested alone, so each is its own PR.
  - #2270, `VerifyInterfaceIsImplemented`. Its five raises are all visible without the other two QCalls, and so is the managed raise for a generic interface on an array (measured, `ifacemap10.log`). The success path then stops at the next QCall; that guest is parked.
  - `GetNumVirtualsAndStaticVirtuals` (#2289) next. With it, the map of an interface with no methods completes, with two empty arrays (measured). The count for other interfaces has a host oracle: the private static method, called by reflection.
  - `GetInterfaceMethodImplementation` (#2290) last. It is the only one of the three with design questions (static virtuals, default interface methods, variance, an abstract class, the null result), and it gets a plan of its own. The planner's throwaway refused static methods; real .NET maps them (measured: `SStat` maps `IStat.S`).
- `Math.Log10` gets its own issue, #2291, and has no dependency on the others. It can go at any time. The planner's throwaway quotient is not a candidate: it answers 2.9999999999999996 for 1000, so FakeItEasy would count three digits in call number 1000 where real .NET counts four (measured).
- Filed: #2287 and #2288 (decisions 2 and 3), #2289, #2290 and #2291. A comment on #2270 says what it covers alone and what follows it, and a comment on the FakeItEasy tracking issue #2092 lists the whole chain.
- I searched the issues, open and closed, for `GetInterfaceMap`, the two QCall names, `Log10`, `ReflectionTypeLoadException`, `FileLoadException` and `netmodule`. Only #2270 and #1322 came back, and neither covers any of the five.

### Where the decisions contradict the plan

- The plan records the option-C mutant as surviving, for want of a guest which holds a type that fails to load. An internal dynamic type which was never created is such a type, and the impure guest now holds one (decision 5).
- The plan records the row-1 mutant as equivalent. It is not (decision 7).
- The plan's `FileExports` is a list of tuples. It is a list of a named record.
- The plan has one shared load step. It is two functions, so that the File refusal can sit between the loads and the array.
- The plan leaves the `GetModules` comment to whoever does (b). This PR corrects its false half, since it writes the same refusal beside it.
- The plan says the refusal should match `GetModules`. It matches in kind only: `GetModules` names File row 1, and this refusal names the File of the first kept export.
- The plan's B section has "a new walk over `ExportedTypes`" and does not say what a cycle or an absent parent does. Decision 1 says: fail, naming the row; a nil parent is not kept.
- The plan says `GetInterfaceMap` is "#2270 plus two QCalls". It stays three PRs, and the third needs a plan.

### Additions to the test design

- Write the tests and the guests first, in a commit of their own, and record the failing output (`Unimplemented native method … AssemblyNative_GetExportedTypes`, and the compile failure of the pure tests) before the selection or the handler exists.
- Pure test 1 loads from bytes into a collectible `AssemblyLoadContext`. That works for every fixture kind used here (measured, `loadbytes.log`).
- Pure test 1 gains the fixture of decision 7: one generated image per run, or a fixed one, with row 1's flags patched to `Public`.
- Pure test 2 uses two netmodules, not one. The first has only internal types on some draws. The property then says that the first of `FileExports` names the File of the first module which has an externally visible type, and the host's `FileName` is that module's name. With one module, "always name File row 1" passes.
- Pure test 3 also generates a nil parent handle. Two example tests, not properties, pin the two failures: a cycle of `NestedPublic` rows, and a parent which is not in the table.
- `AssemblyGetExportedTypes.cs` gains the check that `ModuleGetTypes.cs` has for `GetTypes`: a public type with a static constructor which sets a witness on another type, and the witness is still false after `GetExportedTypes()`. The load function is shared, so the property is too.
- `AssemblyGetExportedTypes.cs` does not call `GetForwardedTypes()`. It answers 0 under PawPrint and 14 on real .NET (#1322).
- `ModuleGetTypes.cs` gains the element-type check of decision 8.
- `TypeBuilderGetExportedTypes.cs` is as decision 5 has it. Control on real .NET: with `DynInternal`'s attributes changed to `Public` and still uncreated, the guest must fail there (real .NET throws `TypeLoadException`).
- Run the default suite and the four Guest categories as separate invocations.

### Additions to the mutants

- Load every TypeDef and filter afterwards (option C): `TypeBuilderGetExportedTypes.cs` stops at the TypeResolve TODO for `DynInternal`. No longer recorded as surviving.
- Drop the skip of row 1: the patched fixture of decision 7. No longer recorded as equivalent.
- Name File row 1 in `FileExports` (or take the File from the wrong row): pure test 2 with two netmodules.
- Refuse the File export before loading the TypeDefs: no test can see it, because both orders end in a stop. Record it as unobservable until (b) is done, and say so in the issue.
- Write the `GetTypes` array as `Type[]`: the new check in `ModuleGetTypes.cs`.
- Keep a `NestedPublic` row whose parent handle is nil: pure test 3.
- Remove the cycle bound from the ExportedType walk: the cycle example test must fail by not terminating, so give it a timeout.
- Have `getTypes` keep its own row enumeration and skip row 1 by name: not observable on Roslyn images. The defence is structural: `getTypes` has no enumeration of its own after this PR.

### As built

Built 2026-11-16 on `origin/main` at `a66b75274`, in two commits after the plan's: the tests and guests first, with
their failing output in the commit message, then the implementation.

**What changed.**

- `WoofWare.PawPrint.Domain/ExportedTypeSelection.fs`, after `TypeVisibility.fs`. Its public API, new in the Domain
  package:
  - `FileExport`, a record with `Export : ExportedTypeHandle` and `File : AssemblyFileHandle`;
  - `ExportedTypeSelection`, a record with `Definitions : TypeDefinitionHandle list` and
    `FileExports : FileExport list`;
  - `ExportedTypeSelection.enumeratedTypeDefs : DumpedAssembly -> TypeDefinitionHandle list`, the TypeDef rows from
    row 2, ascending;
  - `ExportedTypeSelection.fileExports : IReadOnlyDictionary<ExportedTypeHandle, ExportedType> -> FileExport list`, the
    walk over an ExportedType table alone;
  - `ExportedTypeSelection.ofAssembly : DumpedAssembly -> ExportedTypeSelection`.
- `NativeRuntimeTypeHelpers` gains `loadTypeDefRuntimeTypes` (rows to `RuntimeType` addresses) and `writeTypeArray`
  (allocate, fill, write through the `ObjectHandleOnStack`, empty included), and the union `TypeArrayElement`
  (`RuntimeType`, `Type`) beside the module. `RuntimeModule_GetTypes` calls both, over `enumeratedTypeDefs`, and has no
  enumeration of its own.
- The `AssemblyNative_GetExportedTypes` handler, in `NativeRuntimeAssembly.fs` beside `AssemblyNative_GetModules`, and
  its registration in `NativeQCall.fs`. It loads every one of `Definitions`, then refuses the first of `FileExports`
  with a message naming the assembly, the export and the export's File and #2287, then writes a `Type[]`.
- The `GetModules` comment now gives the true reason (`FileName` cannot be set; the message needs two QCalls), and its
  refusal names #2287. The same false claim about `HResult` was in the comment of `TestAssemblyNativeQCalls`' `GetModules`
  File-row test, and is corrected there too.

**Tests.**

- `sourcesPure/AssemblyGetExportedTypes.cs`: 17 checks, each its own exit code. The expected list has 23 names, the
  plan's 21 and `Witness` and `HasCctor`, which the witness check needs public.
- `sourcesImpure/TypeBuilderGetExportedTypes.cs`, as decision 5 has it, registered in `TestImpureCases.fs`.
- `ModuleGetTypes.cs` check 18.
- `TestExportedTypeSelection.fs`: pure tests 1 to 3 and the two example tests, and a third example test (an absent
  parent under a visibility other than `NestedPublic` is never looked up, as in CoreCLR). Pure test 1 patches
  `<Module>` to `Public` on a quarter of its draws rather than in one fixed fixture. Each property counts its labels
  over `CoverageSample`'s fixed sample and asserts a floor for each; the floors are about half of what the sample
  reaches.
- Not in the plan: two handler tests in `TestAssemblyNativeQCalls.fs`. One compiles a manifest over two netmodules, the
  first with no public type, and checks that the handler refuses, naming the second File, the export and #2287, and
  not the first File. The other has a File row and no public export, and checks that the handler answers with a
  two-element `Type[]`. No guest can hold a File row, so these are the only tests of the handler's refusal.

**Measurements.**

- Real .NET 10.0.14 and 11.0.0-rc.3: all three guests exit 0. Twenty controls each exit with the number of the check
  they perturb, on both runtimes: the four list perturbations of the test design (1, 1, 2, 1); `RuntimeType[]`
  expected (3); one array expected from two calls (4); the second call's elements shifted (5); `typeof(SealedTop)` for
  element 0 (6); `ExportedTypes` reversed (7); a non-generic definition expected (8); a forwarded `StringBuilder`
  expected (9); a non-empty `System.Runtime` expected (11); `IsVisible` inverted (12); CoreLib's list compared with the
  guest's (13); `HasCctor` touched first (14); in the impure guest, `DynInternal` public and uncreated (134, an
  unhandled `TypeLoadException` "Could not load type 'DynInternal'"), three types expected (2), the two swapped (3) and
  `RuntimeType[]` expected (5); and in `ModuleGetTypes.cs`, `System.Type` expected (18).
- PawPrint: every guest passes on the host's CoreLib and with `PAWPRINT_TEST_RUNTIME=Net11`. Each guest takes about
  2 s in the suite.
- Decision 5's prediction holds: PawPrint answers `[DynPublic, DynLater]` and never loads the uncreated `DynInternal`.
  The fallback was not needed.

**Mutants.** Each was run against the selection and handler unit tests, the two pure guests and the impure guest.

| mutant | killed by |
|---|---|
| top-level public TypeDefs only | pure test 1; guest exit 1 |
| every `NestedPublic` TypeDef kept | pure tests 1 and 2; guest exit 1 |
| `NestedFamORAssem` kept too | pure test 1; guest exit 1 |
| TypeDefs sorted by name | pure test 1; guest exit 2; `ModuleGetTypes.cs` exit 2; impure guest |
| TypeDefs in dictionary order (sort removed) | **survives**: see below |
| forwarders kept as File exports | pure tests 1 and 3; the guest stops in the File refusal |
| a chain followed through rows that are not `NestedPublic` | pure test 3; the example test of an absent parent under `NestedAssembly` |
| File exports ignored by the selection | pure tests 2 and 3; the handler refusal test; the example test of an absent parent under `NestedAssembly` |
| File exports ignored by the handler | the handler refusal test |
| `RuntimeType[]` for `GetExportedTypes` | handler `Type[]` test; guest exit 3; impure guest |
| the array cached between calls | guest exit 4 |
| no write for an empty array | guest exit 10 |
| load every TypeDef, then keep the visible ones (option C) | impure guest, at the TypeResolve TODO for `DynInternal` |
| row 1 kept | pure test 1 (token `0x02000001` from the patched fixture); `ModuleGetTypes.cs` exit 1 |
| File row 1 named in `FileExports` | pure tests 2 and 3; the handler refusal test |
| the File refusal before the TypeDef loads | **survives**, as decision "Additions to the mutants" predicts: both orders stop |
| `Type[]` for `GetTypes` | `ModuleGetTypes.cs` exit 18 |
| a `NestedPublic` row with a nil parent kept | pure test 3 |
| the cycle bound removed | the cycle example test, at its 30 s bound |
| a public row at a nil File kept | pure test 3 |
| File exports in descending row order | pure tests 2 and 3; the cycle example test (which then names row 3); the handler refusal test |
| CoreLib's last exported type dropped (a check that check 13 runs under PawPrint) | guest exit 13 |

"Have `getTypes` keep its own row enumeration and skip row 1 by name" was not run: `getTypes` has no enumeration of its
own.

**Where the decisions were wrong or silent.**

- The dictionary-order mutant survives, and cannot be killed by a test. `TypeDefs` is an `ImmutableDictionary`, which
  enumerates in the order of its keys' hash codes, and a `TypeDefinitionHandle`'s hash code is its row number. So
  removing the sort changes nothing today; the sort guards an order the collection does not promise. The name-order
  mutant stands in for it.
- A nil File: decision 1 says a nil parent is not kept, and is silent on a nil File. CoreCLR's `mdImpl != mdFileNil`
  test does not keep it, and neither does the walk. Pure test 3 generates both.
- An absent parent is an error only where CoreCLR would read it, under a `NestedPublic` row; the walk checks presence
  before the cycle bound, so an absent parent at the end of a chain as long as the table is reported as absent, not
  as a cycle.
- `fileExports` fails if a row is keyed by a handle other than its own, which a table built by `Assembly.read` never is.
- Decision 1 left the place of `enumeratedTypeDefs` to the implementer; it is in `ExportedTypeSelection`.
- Under option B a forwarded type can only reach the array through `FileExports`, so the guest's check 9 cannot see the
  "keep forwarders" mutant: that mutant ends in the File refusal (on the guest, at a host `BadImageFormatException`
  reading File row 1, which the guest does not have) rather than at exit 9.
- The refuse-before-load mutant is unobservable until (b) is done. This PR does not comment on #2287 to say so.

**Suites**, after a whole-solution build at the implementation commit rebased on `a66b75274`, each a separate
invocation, all `Test Run Successful`: the default suite 5830 (5791 passed, 39 skipped); `TestCategory=Guest` 234;
`GuestSolo` 2; `GuestPure` 1197; `GuestImpure` 300.
