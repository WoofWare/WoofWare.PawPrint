# Emitting types with `TypeBuilder`

Status: option A chosen (2026-11-04); stage 0 implemented (#2093); stage 1 implemented (#2101); stage 2 implemented (#2109); stage 3a implemented (#2115); stage 3b implemented (#2124); stage 3c implemented (#2135); stage 3d implemented (#2141); stage 3e implemented (#2144); sorted-table rows of a created type admitted to move (3f, #2154); stage 4a implemented (#2159); stage 4b implemented (#2160); stage 4c implemented (#2162), and with it the issue's guest passes; stage 4d implemented (#2163); stage 5a implemented (#2164); stage 5b implemented (#2165); stage 5c implemented (#2166); stage 5d implemented (#2167); stage 5e implemented (#2176); stage 5f implemented (#2191); stage 5g implemented (#2192); stage 5h implemented (#2195); stage 5i implemented (#2197); stage 5j implemented (#2213); stage 5k implemented (#2220); stage 5l implemented (#2227); stage 5m implemented (#2228); stage 5n implemented (#2229); stage 5o implemented (#2231); stage 5p implemented (#2234); stage 5q implemented (#2235); stage 5r implemented (#2236); stage 5s in progress. Tracking issue: #2091. Follows
`docs/plans/2026-09-26-expression-thunk-dynamic-method.md`, whose stage 3 gave a dynamic assembly real
metadata (#1636) and deferred "`TypeBuilder` would later have to *extend* that metadata".

## The gap

The issue's guest defines a dynamic assembly, a type `Thing : object, IThing`, a `get_Value` method
returning 42 with an explicit `MethodImpl` onto `IThing.get_Value`, creates the type, instantiates it with
`Activator.CreateInstance` and calls through the interface. It needs
`RuntimeFeature.IsDynamicCodeSupported` overridden to `true`, as every `sourcesImpure` emit guest does,
since `AssemblyBuilder.DefineDynamicAssembly` refuses otherwise.

Measured on `main` at 96b17b82e: real .NET 10 exits 0. PawPrint stops at
`AssemblyNative_GetLoadContextForAssembly`, which `AssemblyBuilder.DefineDynamicAssembly` calls to find
the caller's load context. (The anonymous host never reaches it, because it passes
`AssemblyLoadContext.Default`.)

Past that, the path runs `RuntimeTypeBuilder`'s and `RuntimeModuleBuilder`'s QCalls. CoreLib declares 31
of them in `System.Reflection.Emit`; the issue's guest reaches, judging from the managed source, about a
dozen: `ModuleBuilder_GetTypeRef` (for `object` and `IThing`), `TypeBuilder_DefineType` (with the
interface list), `TypeBuilder_DefineMethod` (twice, counting the default constructor `CreateType` adds),
`ModuleBuilder_GetMemberRefOfMethodInfo` (for `IThing.get_Value` and `object..ctor`),
`TypeBuilder_DefineMethodImpl`, `TypeBuilder_GetTokenFromSig`, `TypeBuilder_SetMethodIL`, perhaps
`TypeBuilder_SetParamInfo`, and `TypeBuilder_TermCreateClass`. Then reflection, allocation and
interface dispatch over the new type, all reading the dynamic module's metadata.

## The constraint: tokens

Measured on .NET 10, defining types `A` and `B`, then methods `B1`, `A1`, `B2`, `A2` in that order:

```
A=02000002 B=02000003 b1=06000001 a1=06000002 b2=06000003 a2=06000004
```

and after `CreateType`, reflection over the created types reports the same tokens
(`A.A1 06000002`, `B.B1 06000001`, ...). A type `C` defined afterwards got `C1=06000007`, because each
`CreateType` added a default constructor (`06000005`, `06000006`).

So a dynamic module's `MethodDef` (and `Field`, `Param`, `Property`, `Event`) RIDs are in definition
order, not grouped by type, and they stay so for the module's lifetime. CoreCLR's read-write metadata
does this with indirection tables: `CMiniMdRW::AddChildRowIndirectForParent` (md/enc/metamodelrw.cpp)
appends the row and, when it is not at the end of its parent's range, starts a `MethodPtr`/`FieldPtr`
table so that `TypeDef.MethodList` ranges over the pointer table instead. A guest observes the tokens
through `MetadataToken`, and the IL that `ILGenerator` emits holds them.

`System.Reflection.Metadata.Ecma335.MetadataBuilder` cannot write pointer tables: a type's methods are
the contiguous run from its `MethodList`, so an image it writes cannot carry these RIDs.

## The decision: how a dynamic module's metadata grows

**A. An append-only row log, written with pointer tables.** The dynamic assembly's state is a log of
metadata rows in definition order, each QCall appending a row (or filling a column of a row whose type is
not yet created). After each QCall that changes it, PawPrint serialises the log with its own ECMA-335
writer, emitting `MethodPtr`/`FieldPtr`/`ParamPtr`/`PropertyPtr`/`EventPtr` in an uncompressed (`#-`)
table stream exactly when CoreCLR would, and re-reads the image through `Assembly.read`, replacing the
dynamic assembly's `DumpedAssembly` under the same identity. Every RID is then the token CoreCLR handed
out, there is one token space, and everything that reads an image (the loader, the type system,
`MetadataImport`, IL bodies) works unchanged. Because the log only appends, a handle in the old image
means the same in the new one, so nothing already loaded is invalidated; the replacement asserts that
every changed row belongs to a type not yet created. This is CoreCLR's own model. Cost: a table, heap and
PE writer (the heaps are simple; the tables are those Reflection.Emit writes, about twenty), and the
replace operation in `LoadedAssemblies`. Its oracle is a round-trip property over generated logs, read
back by SRM's `MetadataReader` (which reads pointer tables, by its source; stage 1 confirms it), plus the guest-level differential cases.

**B. `MetadataBuilder` with a token map.** Keep writing grouped images with `MetadataBuilder`, and keep a
bijection between CoreCLR's definition-order tokens and the image's handles, translated at every
boundary: QCall arguments, the tokens inside IL bodies and signatures (rewritten at serialisation), and
every token reflection or `MetadataImport` reports back. The writer is free. But there are two token
spaces for one module, every reader of a dynamic module's token has to know which it holds, and a type
created earlier can have its handles shift when a still-open type that precedes it gains a member, so
the type system's handle-keyed caches would need remapping too.

**C. No image for a dynamic module.** Make an assembly's content a union,
`Image of PEReader | Emitted of EmittedModule`, build the `TypeDefs`/`Methods`/... dictionaries directly
from the log keyed by CoreCLR's tokens, and hold IL bodies and blobs as byte arrays. The most explicit
about the difference, and no serialiser. But it is the same decision #1636 rejected: about 34 lines in the
interpreter, Loader and TypeSystem reach for `PeReader` or a `MetadataReader`, and `Assembly.read`'s parsing would gain a second source that has to agree
with it, which is a difference CoreCLR does not draw (a dynamic module there has metadata).

**Chosen: A** (2026-11-04). It keeps one token space, matching what the guest measures; it changes nothing that
reads an image; and its new code is a pure function from a log to bytes, with a mechanical oracle. B's
cheap writer buys a translation layer at every boundary. C's blast radius is the whole interpreter.

## Stages

Each is its own PR, and each re-runs the issue's guest to find the next stop rather than predicting it.

0. **`AssemblyNative_GetLoadContextForAssembly`.** CoreCLR returns 0 for an assembly in the default
   binder, and managed code maps 0 to `AssemblyLoadContext.Default`. Every assembly PawPrint loads is in
   the default binder (3a refuses any other context), so the handler answers 0. No design decision.

   Measured after stage 0: `DefineDynamicModule` reaches no QCall, and the issue's guest stops in
   `DefineType` at `ModuleBuilder_GetTypeRef`, resolving the parent type `object` (stage 3).
1. **The writer**, under option A: the row log as immutable data, its serialiser, and the round-trip
   property, with no interpreter change. `DynamicAssemblyImage.build` becomes the log's first three rows.
   Its own decisions are under "Stage 1 in detail" below.
2. **The log in the machine state**: the dynamic assembly holds its log, and `LoadedAssemblies` gains the
   superset-replacement operation, with its invariant asserted.
3. **The definition QCalls the issue reaches**, each appending what CoreCLR's emitter does, and each
   other `TypeBuilder` QCall refused by name. Probably split in two: references (`GetTypeRef`,
   `GetMemberRef*`, `GetTokenFromSig`, with the `AssemblyRef` rows `Assembly::AddAssemblyRef` writes)
   and definitions (`DefineType`, `DefineMethod`, `DefineMethodImpl`, `SetMethodIL`, ...).
4. **`TypeBuilder_TermCreateClass`**: load the type from the replaced image and hand back its
   `RuntimeType`; then whatever `Activator.CreateInstance` and the interface call reach. The issue's guest
   goes into `sourcesImpure` as the acceptance case.

## Stage 1 in detail

### What SRM's reader accepts

Read from `System.Reflection.Metadata` at v10.0.0 (`MetadataReader.cs`, `Internal/Tables.cs`):

* It reads a pointer table whenever that table has rows, in either kind of table stream, and inverts one
  by linear search (`GetRowIdForMethodDefRow`). So an image whose `MethodDef` RIDs are in definition order
  reads back with every method under its own type.
* It refuses with `BadImageFormatException` (`TableNotSorted`) an `InterfaceImpl`, `MethodImpl`,
  `Constant`, `FieldMarshal`, `DeclSecurity`, `ClassLayout`, `FieldLayout`, `MethodSemantics`, `ImplMap`,
  `FieldRva`, `NestedClass`, `GenericParam` or `GenericParamConstraint` table that is out of key order.
  It sorts `CustomAttribute` virtually instead.

CoreCLR's read-write metadata leaves each of those tables in append order, and keeps a `VirtualSort` over
it for lookups (`CMiniMdRW`, md/enc/metamodelrw.cpp). An image SRM can read must sort them, so their
image RIDs can differ from the tokens CoreCLR assigns. Of those tables, only `GenericParam` hands a token
to the guest (`GenericTypeParameterBuilder.MetadataToken`), and the issue's guest defines no generic
parameter.

**Chosen (2026-11-05): sort stably.** A stable sort by key keeps each owner's rows in append order, which
is the order CoreCLR enumerates them in, so only the RIDs move, and only when the guest's appends were
out of key order. Nothing hands out the RID of an `InterfaceImpl` or `MethodImpl` row, and PawPrint keeps
neither handle outside the `DumpedAssembly` that a re-read rebuilds. `GenericParam` is decided when a guest
first reaches it: either refuse an append that would put that table out of order, or carry the
permutation. Stage 2's replacement compares the sorted-table rows of every created type and crashes if
one moved.

### The decision: what the log holds

**1a. Tables as data.** An emit scope is an immutable record of append-only tables and heaps. Each
child row holds its parent's RID as a column (a method holds its type, a parameter its method), never as
a list range, and every reference is typed (`TypeDefRid`, a `TypeDefOrRef` union, a `StringOffset` into
the scope's own string heap). Adding a row returns its RID at once, as CoreCLR's emitter returns a token,
and interning a string or blob returns its offset at once. The heaps only grow, so offsets stay stable
too. The writer is a pure function of the scope. It derives `FieldList`, `MethodList` and `ParamList`;
it emits a pointer table for a child table exactly when that table's rows are not grouped by parent;
and it sorts each sorted table stably by key.

**1b. A log of operations.** The scope is a list of emit operations (`DefineType`, `DefineMethod`,
`SetMethodBody`, ...). The writer replays it into tables. A QCall needs its token immediately, so it has
to replay the log to learn it, or keep a second copy of 1a's counters beside the log. The meaning of each
operation then lives in the replay, which is 1a's add functions under another name.

**Chosen: 1a** (2026-11-05). It is CoreCLR's own model, where `CMiniMdRW` holds tables and pools and each define
call appends to them. Every token is known when it is handed out. The round-trip property is stated over
the scope's rows directly.

### Scope of stage 1

* Tables: `Module`, `TypeRef`, `TypeDef`, `Field`, `MethodDef`, `Param`, `InterfaceImpl`, `MemberRef`,
  `StandAloneSig`, `MethodImpl`, `Assembly`, `AssemblyRef`, and the pointer tables `FieldPtr`,
  `MethodPtr` and `ParamPtr`. Fields are included because `TypeDef` has a `FieldList` column whatever
  the guest defines, and their ranges are derived by the same function as methods'. Properties, events,
  generic parameters, custom attributes and type specs come in when a guest reaches them.
* Heaps: `#Strings`, `#Blob` and `#GUID`, each interned as CoreCLR's pools intern. `#US` is written with
  only its empty entry until `ldstr` needs more.
* A method body is held as the bytes CoreCLR would store, header included. Encoding those bytes from
  `SetMethodIL`'s arguments is stage 3's job. The writer places each body 4-byte aligned in `.text`, and
  writes its RVA into the row.
* The PE: a subclass of SRM's `PEBuilder` with one `.text` section, holding the CLI header, the bodies
  and the metadata. `ManagedPEBuilder` cannot be used, because it takes only `MetadataBuilder`'s tables.
* The table stream is always `#-`, so pointer tables are allowed in every image.
* It lives in Domain, because stage 2 has `LoadedAssemblies`, in the Loader, hold each dynamic assembly's
  scope and write its image.

### The oracle

* Round trip, over generated scopes: read the image with SRM's `MetadataReader`, and every row reads
  back with the same columns at the RID the scope assigned. Every child lies in its own parent's
  range, through the pointer table when there is one. Sorted tables hold the same multiset of rows,
  in key order.
* Through `Assembly.read`: the `DumpedAssembly` lists each type's methods and fields as the scope does.
* Prefix stability: for a scope `s` and any extension `s'` of it, every row and heap entry of `s` reads
  back the same in `s'`'s image (sorted tables excepted, by content). Stage 2's replacement relies on
  this.
* Determinism: `write` is a function of the scope.
* `DynamicAssemblyImage.build`'s existing tests pass unchanged over the new writer.
* A list column cannot name the row one past the end of a table of exactly 65535 rows, because the column
  is then two bytes and SRM reads 0 there as an empty range, which breaks the range before it. The
  writer refuses such a scope rather than write an image that reads back wrong.

## Stage 2 in detail

### The decisions

**Where the scope lives. Chosen (2026-11-05): in `LoadedAssemblies`.** Each dynamic assembly's entry holds
its scope and the set of its types the runtime has created, beside the `DumpedAssembly` read from the
scope's image. `WithDynamicAssembly` takes the scope and reads the image itself, and `WithEmitScope`
checks, writes, reads and swaps in one call, so the scope and the image cannot disagree. The cost is that
the model and writer moved to Domain, a published package. The alternative was a map from assembly to
scope in `IlMachineState`, beside a `LoadedAssemblies` operation that replaces an image it cannot check.

**What the replacement checks. Chosen (2026-11-05): the scopes.** `EmitScopeExtension.violations` compares
the old scope with the new:

* Every row and heap entry of the old one is unchanged in the new one.
* No row the new one adds is a member of a created type: a field, a method, a parameter of one of its
  methods, an interface implementation or a method impl.
* No created type's `InterfaceImpl` or `MethodImpl` row has a different image RID, which happens when a
  row is added for a type that sorts before it. Both scopes' RIDs come from the writer
  (`EmitScopeImage.interfaceImplRids`, `methodImplRids`), so the check and the image cannot disagree
  about the sort.

The link to the image is stage 1's prefix-stability property. The alternative was comparing the
read-back `TypeInfo`s of created types, which needs a structural comparison that `TypeInfo` does not
have: its `MethodImpls` is an `ImmutableDictionary`, compared by reference.

### The oracle

The check is judged by the images of the two scopes, read back through SRM:

* For a scope extended by appending, the created types it reports are exactly those whose methods,
  parameters, fields, interface implementations or method impls read back differently.
* For a rewritten history, every row, heap entry and sorted-table run that reads back differently is
  reported as its own violation.

All thirteen mutants of the check, the load-context members and the writer's RID function are caught.

### Left to later stages

* **Stage 3** fills columns of rows it has already added (`SetMethodIL` sets a method's body, for
  instance). The check then has to admit a changed row whose type is not yet created. Filling an
  earlier method's body moves the RVAs of the bodies after it in `.text`. That is harmless: `Assembly.read`
  parses each body when it reads the image, and later reads through `PeReader` look the assembly up by
  name, so they go to the current image.
* **Stage 4** calls `WithCreatedType` from `TypeBuilder_TermCreateClass`. A guest may add an
  `InterfaceImpl` or `MethodImpl` to an earlier-defined type after creating a later one (create `B`,
  then `DefineMethodOverride` on `A`), which moves `B`'s rows in the image; 3f admits it.

## Stage 3 in detail

### The split

**Chosen (2026-11-05): one QCall group per PR.** The issue's guest interleaves references and
definitions (`GetTypeRef` for `object` and `IThing`, then `DefineType`, `DefineMethod`, a `MemberRef` for
`IThing.get_Value`, ...), so a PR of all the references would move it no further than one of
`GetTypeRef` alone. Each PR implements one group and re-runs the guest to find the next:

* **3a, `ModuleBuilder_GetTypeRef`.** The guest then stops at `TypeBuilder_DefineType`.
* **3b, `TypeBuilder_DefineType`.** The guest then stops at `TypeBuilder_DefineMethod`.
* **3c, `TypeBuilder_DefineMethod` and `TypeBuilder_SetMethodImpl`**, which a `MethodBuilder`'s first
  `MetadataToken` calls one after the other. The guest then stops in `DefineMethodOverride` at
  `ModuleBuilder_GetMemberRefOfMethodInfo`, for `IThing.get_Value`.
* **3d, `ModuleBuilder_GetMemberRefOfMethodInfo`**, for a method whose signature names no type by
  token. The guest then stops in `DefineMethodOverride` at `TypeBuilder_DefineMethodImpl`.
* **3e, `TypeBuilder_DefineMethodImpl`.** The guest then stops in `CreateType` at
  `TypeBuilder_SetParentType`.
* **3f, a created type's rows of a sorted table may move in the image**, which stage 4 would otherwise
  refuse for a later override or interface implementation of a type with a lower RID. The guest is
  unchanged.
* Then the rest in the order the guest reaches them.

### 3a: what CoreCLR's emitter writes

Read from `ModuleBuilder_GetTypeRef` (vm/commodule.cpp), `Assembly::AddAssemblyRef`, `AssemblySpec`
and `RegMeta` (md/compiler), and measured on .NET 10 through the public
`ModuleBuilder.GetTypeMetadataToken`:

* A dynamic module's emitter checks for duplicates of `TypeRef`, `AssemblyRef`, `MemberRef`,
  signatures and the rest (`MDDupDefault | MDDupTypeDef | MDDupAssemblyRef | ...`, set in
  `Assembly::CreateDynamic`'s scope). A type asked for twice gets one row.
* A top-level type's `TypeRef` is scoped by an `AssemblyRef` made from the defining assembly's own
  `Assembly` row: its name, version and culture, the key reduced to its token, and the row's flags
  without `PublicKey`, then with the processor-architecture bits cleared unless marked specified.
  `FindAssemblyRef` matches on name, culture, version and token, and ignores build and revision for
  `mscorlib`.
* A nested type's `TypeRef` is scoped by its enclosing type's, which the managed caller asks for first,
  so the enclosing type's row comes first.
* The namespace is everything before the last `.`, except that `A..ctor` is the name `.ctor` in `A`.
* A reference from the module to its own type is looked up among its TypeDefs, not added. The managed
  caller asks for one only for a type of this module that is not a `TypeBuilder`, which only
  `CreateType` makes, so 3a refuses it with a TODO.

### 3a: which assembly an AssemblyRef names

CoreCLR's module also records, in its rid map, the loaded assembly each `AssemblyRef` was emitted for
(`ForceStoreAssemblyRef`, first association kept), and a reference resolves through that before the
binder sees it. That is how one dynamic module refers to a type of another, which the binder never
finds.

**Chosen (2026-11-05): a per-module map now.** Each dynamic assembly's entry in `LoadedAssemblies`
holds `AssemblyRefRid -> definition identity`, which `WithAssemblyRefTarget` fills, asserting that the
row is the one the scope finds for that assembly, and `TryResolveReference` consults it first. The
alternative was to bind by name and refuse a reference to a dynamic assembly until a guest made one.
An `AssemblyReference` now carries its referencing assembly's stored definition identity, so the lookup
costs one dictionary probe rather than an `AssemblyName.FullName`, which hashes the key on every call.

### 3a: the oracle

* Find-or-define, over generated sequences: each `TypeRef` and `AssemblyRef` is found exactly when a
  model of `FindTypeRefByName` and `FindAssemblyRef` says so, a found row adds nothing to any heap, and
  each new row holds what `_SetAssemblyRefProps` stores.
* Over every assembly in the test host's framework and an unsigned one, the request made from an
  assembly's row names that assembly, with the token `AssemblyName` derives.
* `ModuleBuilderTypeReferences.cs` checks the tokens, `GetReferencedAssemblies` and `ResolveType` over
  twelve types of four assemblies, against constants measured on .NET 10.
* Of 25 mutants of the Domain functions, the load-context map and the handler, 24 are caught. The one
  that survives drops the handler's `WithAssemblyRefTarget` call; see below.

### 3a: left to later stages

* The handler's call to `WithAssemblyRefTarget` is seen by no guest yet, because every assembly a 3a
  guest can reference is one the binder also finds. A guest that derives a type in one dynamic module
  from a `TypeBuilder` in another is the case that shows it, once `CreateType` loads the derived type
  and so resolves the reference.

### 3b: what CoreCLR's emitter writes

Read from `COMDynamicWrite::DefineType` (vm/comdynamic.cpp), `RegMeta::_DefineTypeDef`,
`_SetTypeDefProps` and `_SetImplements` (md/compiler), and measured on .NET 10:

* A new type's token is the next `TypeDef` RID, after `<Module>`'s. The managed caller asks for its
  interfaces' tokens before its parent's, so their `TypeRef`s come first.
* The emitter checks for a duplicate `TypeDef` (`MDDupTypeDef`) with `FindTypeDefByName`: a row with the
  same namespace and name, ignoring rows of nested visibility for a top-level type. It then returns
  `META_S_DUPLICATE`, for which the QCall raises `ArgumentException`. The managed check
  (`CheckTypeNameConflict`) knows only the module's `TypeBuilder`s, so `DefineType("<Module>")` reaches
  it.
* A new row interns its name before its namespace (the reverse of a `TypeRef`), and keeps its flags
  without `tdReservedMask`. It has no parent if the caller passes none (an interface).
* Each interface gets an `InterfaceImpl` row, in order. The emitter does not check these for
  duplicates (`MDDupInterfaceImpl` is not set), so an interface listed twice gets two rows.
* `AddAvailableClassDontHaveLock` makes the type findable by name. The image read back from the scope
  holds it.

3b refuses, with a TODO, a nested type (the scope has no `NestedClass` table yet) and a `TypeSpec`
parent or interface. `ModuleBuilder_GetTokenFromTypeSpec` is what hands a `TypeSpec` out, and it is
not implemented.

### 3b: a type that is defined but not created

Real .NET never loads a type of a dynamic module before `CreateType`. Its loader raises the
AppDomain's `TypeResolve` event instead (`ClassLoader::LoadTypeDefThrowing`). Measured:
`Assembly.GetType` and `Type.GetType` give null, and `ModuleBuilder.ResolveType` throws
`TypeLoadException`. Once `DefineType` adds the row, PawPrint's image holds the type, so something has
to stop PawPrint loading it.

**Chosen (2026-11-05): refuse in the type system.** `AllConcreteTypes.add` and `addNonGeneric`, the
only ways to mint a concrete type, take the `LoadedAssemblies`, and crash with a TODO on a type that
`IsUncreatedDynamicType` reports. The two alternatives were:

* leaving uncreated types out of the dynamic image's name index, which would make the index disagree
  with the image, and which would still diverge for a `TypeResolve` handler that answers;
* deferring the check to stage 4, which would leave a window in which a guest sees a type that .NET
  does not load.

**Chosen (2026-11-06): the module's global type is loaded lazily, for every module.** PawPrint filled
`RuntimeModule.m_runtimeType` with the `<Module>` type when it allocated the module, so every dynamic
module crashed on the refusal at once. CoreCLR fills it on first read, through
`ModuleHandle_GetModuleType`, which 3b implements. The alternative was to keep the eager fill for
modules read from an image and leave the field null only for dynamic ones; that takes two paths where
CoreCLR has one. The QCall loads `<Module>` only if it has a method or a field
(`Module::NeedsGlobalMethodTable`), and otherwise answers null, so a module C# compiles without a
module initializer has no global type. A dynamic module has one only once `DefineGlobalMethod` or a
global field adds a member, and reading it before `CreateGlobalFunctions` crashes on the refusal. In
CoreCLR that raises `TypeResolve`, and the QCall then swallows the failure.

### 3b: the oracle

* Find-or-define over generated sequences, against a model of `FindTypeDefByName` and
  `_DefineTypeDef`. Names in the model differ only in case, split alike or apart, collide with
  `<Module>`, or differ only in a lone surrogate (stored as the same UTF-8). Flags are random 32-bit
  values. Parents and interface lists repeat entries.
* `TypeBuilderDefineType.cs` checks the tokens, the `TypeRef` order, and both duplicate refusals,
  against constants measured on .NET 10. Its terminal-state check reads the rows no guest can see
  before `CreateType`: flags, parents and interfaces.
* `ModuleGlobalMembers.cs` and `ModuleInitializerGlobalType.cs` read a static module's global type,
  under the real-runtime oracle: null without a module initializer, `<Module>` with one.
* The refusal is checked through both interning functions, before and after `WithCreatedType`.
* All 17 mutants are caught: of the Domain functions, `IsUncreatedDynamicType`, the refusal, the
  handler and the global-type QCall.

### 3b: left to later stages

* Stage 4's `TypeBuilder_TermCreateClass` must call `WithCreatedType` before it loads the type.
* A dynamic module's `<Module>` is loadable only once `CreateGlobalFunctions` creates it. Before that,
  modelling CoreCLR needs the `TypeResolve` event.

### 3c: what CoreCLR's emitter writes

Read from `TypeBuilder_DefineMethod` and `TypeBuilder_SetMethodImpl` (vm/comdynamic.cpp),
`RegMeta::DefineMethod`, `_SetMethodProps` and `SetMethodImplFlags` (md/compiler/emit.cpp), and measured
on .NET 10 through `MethodBuilder.MetadataToken` and, after `CreateType`, reflection over each token:

* A method's token is the next `MethodDef` RID, across types, in the order tokens are first read. The
  emitter does not check for a duplicate (`Assembly::CreateDynamic` does not set `MDDupMethodDef`),
  so a type's second method with the same name and signature is a second row. Measured, the same
  holds for two global methods.
* The row keeps the flags' low 16 bits without `mdReservedMask`. A method named `.ctor` or `.cctor`,
  or whose name starts with `_VtblGap`, gains `RTSpecialName` and `SpecialName`; the name is
  compared as UTF-16, case-sensitively. Its implementation flags are `miIL | miManaged`, and it has
  no body.
* A nil parent token (`mdTokenNil` or `mdTypeDefNil`, what `ModuleBuilder`'s global type passes) is
  the module's `<Module>` (`IsGlobalMethodParent`).
* `SetMethodImpl` overwrites the row's implementation flags with their low 16 bits. The managed
  caller calls it straight after `DefineMethod`, and again from `SetImplementationFlags`, which
  refuses a created type.

### 3c: a row's column filled in later

`SetMethodImpl` changes a row it has already added, which stage 2's extension check refused as a
rewritten row. **Chosen (2026-11-06): the check admits a fill.** A method row of `before` that differs
in `after` only in its implementation flags is a fill. That is no violation while the method's type
is not created, and `FilledInCreatedType` once it is. Any other difference in the row is still
`RowChanged`. That is CoreCLR's own model: its record is overwritten in place. The alternative was to
keep the scope append-only by logging each fill as a row of its own and letting the writer apply the
last. But the image would still read the earlier row back changed, so nothing that reads the image
would gain, and the scope would have two places for one column. Prefix stability and the history
properties now leave out that column, and the appending property counts a fill of a created type
among the changes it must report. `SetMethodIL` will add the body to the fillable columns.

### 3c: a method that has no body yet

A defined method has no body until `SetMethodIL`, and `MethodInfo.read` refused an IL method with RVA
0, so the dynamic image failed at the first `MetadataToken`. #2132 classifies such a method as
`MethodBody.MissingIl`, as CoreCLR treats it: the type loads, and only invoking the method fails.
Nothing loads a type of a dynamic module before `CreateType`, which needs every body, so 3c's rows
are read but never run.

### 3c: the oracle

* Over generated sequences of definitions and fills, against a model of `DefineMethod` and
  `SetMethodImplFlags`: names that are and are not special (including `.CTOR`, `_VtblGa` and a lone
  surrogate after `.ctor`), random 32-bit flags, and repeated names and signatures.
* `EmitScope.addMethodDef` and `setMethodImplFlags` refuse flags wider than the column.
* The extension check's appending property, over generated scopes that now fill implementation flags.
* `TypeBuilderDefineMethod.cs` checks nine tokens against constants measured on .NET 10, which exits
  0 on it: duplicates, a signature naming a `TypeRef` and a `TypeDef`, the special names, the
  reserved and high bits, a global method, and a constructor. Its terminal-state check reads each
  row's owner, name, flags, implementation flags and signature, which .NET 10 reports for each token
  after `CreateType`.
* All 19 mutants are caught: of the Domain functions, `EmitScope`'s column checks, the extension
  check's fill rule, and the two handlers.

### 3c: left to later stages

* `SetMethodIL` fills the body, which the extension check must then admit too.
* Two rows that real .NET holds until `CreateType` are refused, because `MethodInfo.read` cannot read
  them back from the dynamic image. Each has a parked guest. One is a P/Invoke row before
  `TypeBuilder_SetPInvokeData` writes its import, as `DefinePInvokeMethod` defines one
  (`TypeBuilderDefinePInvokeMethod.cs`). The other is a bodiless method given implementation flags
  that are not managed IL (`TypeBuilderNonIlImplementationFlags.cs`).
* A method of `<Module>` makes the module's global type loadable by `ModuleHandle_GetModuleType`
  (`NeedsGlobalMethodTable`), which then refuses it as uncreated. In CoreCLR that raises
  `TypeResolve`, and the QCall swallows the failure. No guest reads a dynamic module's global type
  before `CreateGlobalFunctions` yet.

### 3d: what CoreCLR's emitter writes

Read from `ModuleBuilder_GetMemberRefOfMethodInfo` (vm/commodule.cpp), `RegMeta::DefineMemberRef`
(md/compiler/emit.cpp), `ImportHelper::FindMemberRef` and `CMiniMdRW::CompareMemberRefs`, and
`ImportHelper::MergeUpdateTokenInSig`, and measured on .NET 10 through
`ModuleBuilder.GetMethodMetadataToken` and `Module.ResolveSignature` on the dynamic module:

* The managed caller passes the token of the method's declaring type, which it makes first: a
  `TypeRef` for a non-generic type of another module, a `TypeSpec` for a generic one. A method
  reached through a derived type is a reference to its declaring type's.
* For a method of the dynamic module itself, the QCall hands back its `MethodDef` token. Only a created
  type has such a method.
* Otherwise it reads the method's name and signature from its `MethodDef`, so an instantiated generic
  method's reference is to its definition. `TranslateSigWithScope` rewrites each type token in the
  signature to name a `TypeRef` of the dynamic module, adding `TypeRef` and `AssemblyRef` rows.
  `MergeUpdateTokenInSig` writes the rest of the signature as follows. A type or method parameter's
  index, and an instantiation's argument count, come out in their shortest form. Every other byte is
  copied, including an overlong parameter count, generic arity or array size. Bytes after the last type
  are dropped. Measured: an overlong `MVAR` or `VAR` index comes out shortest, so two methods `M<T>(T)`
  of one type, which differ only in that encoding, share one `MemberRef`.
* `DefineMemberRef` checks for a duplicate in a dynamic module (`MDDupDefault` holds `MDDupMemberRef`):
  the first row with the same parent token, the same UTF-8 name and the same signature bytes is
  handed back, and otherwise a row is added, interning the name before the signature.

### 3d: a signature that names a type by token

**Chosen (2026-11-06): copy only a signature that names no type by token, and refuse the rest by
name.** The issue's guest reaches only such signatures (`IThing.get_Value`'s `20 00 08` and
`object..ctor`'s `20 00 01`). The translation is about 1300 lines of C++, and it adds rows of its
own that do not agree with `GetTypeRef`'s. Measured, after a reference to `Console.SetOut(TextWriter)`
names a new `TypeRef` for `TextWriter`, `GetTypeMetadataToken(typeof(TextWriter))` adds a second one,
while for `Type` and `Guid`, which `GetTypeRef` had named first, the translation reuses its row. That
is a stage of its own. The alternative was to translate now, in a PR doing two things.
`EmitScopeReferences.importMethodSignature` walks a signature as `MergeUpdateTokenInSig` does. It gives
the bytes the emitter writes for a signature that names no type by token, and `NamesTypeByToken` for
any other, which the handler refuses with a TODO. A parent `TypeSpec` is refused too; it cannot be reached yet,
because `ModuleBuilder_GetTokenFromTypeSpec` is not implemented.

### 3d: the oracle

* Over generated scopes and requests, against a model: a request with the parent, name and signature
  of an earlier row (one the scope started with included) is that row, the first of two alike, and
  leaves the scope as it was; any other is a new row holding the request.
* Over method signatures generated from the ECMA-335 grammar (nested arrays, pointers, by-refs,
  instantiations, function pointers, custom modifiers, type and method parameters), each integer in
  its shortest or an overlong form, with trailing bytes: `importMethodSignature` against the
  grammar's own answer.
* Real .NET's emitter, three ways:
  * For every method and constructor of a non-generic CoreLib type whose signature names no type by
    token, each asked for twice, the dynamic module's signature is the method's own bytes, and its
    token is the one `defineMemberRef` gives.
  * For eleven signatures in a fabricated image, overlong or with a trailing byte, the dynamic
    module's signature is the one `importMethodSignature` gives.
  * `TestFabricatedMemberRefOfOverlongSignature`, run on both runtimes, gives two methods differing
    only in an overlong `MVAR` index one token.
* `ModuleBuilderMemberRefOfMethodInfo.cs` checks eleven tokens against constants measured on .NET 10,
  which exits 0 on it: a duplicate, a constructor, the same parent and signature under two names,
  the same name with two signatures, a method reached through a derived type, the same name and
  signature on two parents, and a generic method and its instantiation. Its terminal-state check
  reads each row's parent, name and signature.
* `ModuleBuilderMemberRefTranslatedSignature.cs`, parked, measured on .NET 10 to exit 0, reaches the
  refusal for a class and a value type in a signature.
* All 24 mutants are caught. They cover the lookup, the find-or-add, the signature walker's
  re-encoding, copying, token detection and refusals, and the handler's refusal, imported signature,
  token, parent and scope update.

### 3d: left to later stages

* `TranslateSigWithScope`, and with it the parked guest; it must reproduce the measured `TypeRef`
  duplicate.
* A reference to a method of the dynamic module itself, which needs `CreateType` (stage 4).
* A parent `TypeSpec`, with `ModuleBuilder_GetTokenFromTypeSpec`.

### 3e: what CoreCLR's emitter writes

Read from `TypeBuilder_DefineMethodImpl` (vm/comdynamic.cpp), `RegMeta::DefineMethodImpl`
(md/compiler/emit.cpp) and `DefineMethodOverrideCore` (RuntimeTypeBuilder.cs):

* The managed caller refuses a created type, and a body whose declaring type is not its own
  `TypeBuilder`, before it asks for any token. It then passes its own `TypeDef` token, and the body's
  and declaration's tokens from `GetMethodMetadataToken`: a `MethodDef` for a method of the module, a
  `MemberRef` for any other (stage 3d).
* `DefineMethodImpl` looks for a duplicate only under `MDDupMethodDef`, which a dynamic module's
  emitter does not set (`MDDupDefault` lacks it), so each call adds a row, even for an override already
  defined. It checks nothing else about the tokens beyond debug assertions.
* The row holds the class, the body and the declaration. The emitter's table is in the order rows are
  added, and the image sorts it stably by class, as the writer already does.

Nothing about a `MethodImpl` row is visible to a guest before `CreateType`, so 3e needs no design
decision: the handler decodes the three tokens and appends the row with `EmitScope.addMethodImpl`.

### 3e: the oracle

* `TypeBuilderDefineMethodImpl.cs`, which exits 0 on .NET 10, defines five overrides: a declaration of
  the module (a `MethodDef`) and of another assembly (a `MemberRef`), an interface's method and a
  class's virtual method, the same override twice, and a type defined later whose row comes first. It
  also reaches the managed caller's two refusals. Its terminal-state check reads each row's class,
  body and declaration in the order they were added, and the `MemberRef` rows they name.
* Each mutant of the handler is caught: body and declaration swapped, the wrong class, a `MemberRef`
  or `MethodDef` RID off by one, a duplicate check, and the scope not replaced.

### 3e: left to later stages

* The image sorts `MethodImpl` and `InterfaceImpl` by class, so once stage 4 creates a type, a row added
  afterwards for a type with a lower `TypeDef` RID moves the created type's rows. 3f admits it.

### 3f: a created type's rows of a sorted table may move

CoreCLR's read-write metadata keeps `MethodImpl` and `InterfaceImpl` in the order rows were added, and
reads one class's rows through a `VirtualSort` (md/enc/metamodelrw.cpp) whose `Compare` orders by class,
then by RID. That is the image's stable sort by class: a class's rows come back in the order they were
added, wherever rows of other classes land. A row's RID in either table is not visible to a guest:
Reflection.Emit hands out no token for either, and `IMetaDataImport::EnumMethodImpls` returns the body
and declaration, not the row.

So the extension check's `SortedRowMoved` refused a replacement that changes nothing a guest or the
runtime can read. The options weighed:

* **Keep no handle into a sorted table (chosen).** `TypeInfo.MethodImpls`, the only place the runtime
  kept one (a dictionary keyed by `MethodImplementationHandle`, re-sorted by handle wherever it was
  read), becomes an array in table order. With no handle kept, a moved RID is harmless by construction,
  and the check drops `SortedRowMoved`; it still requires every row unchanged and none added for a
  created type, so a created type's rows read back the same and in the same order. A breaking change to
  `WoofWare.PawPrint.Domain`'s public `TypeInfo`.
* **Drop the check alone.** Sound only because nothing resolves an old handle against a new image,
  which nothing would enforce.
* **Re-read created types when a row moves.** Keeps the image and the check, and replaces every cached
  `TypeInfo` of a created type; more machinery, reaching into the type system's caches.
* **Write the tables unsorted.** System.Reflection.Metadata's reader refuses an unsorted `MethodImpl`
  or `InterfaceImpl` table.

Not every table CoreCLR virtual-sorts is like these two: a `GenericParam` row's token is visible to a
guest, through `GenericTypeParameterBuilder.MetadataToken`, so that table will need its own answer when
a stage emits it.

### 3f: the oracle

* `TestEmitScopeExtension`'s extension property compares a created type's interface implementations
  and method impls by what each names, in table order. A second property appends rows for `<Module>`,
  which sort before every other type's, so that most samples move a created type's rows, and requires
  the check to admit each.
* `TestLoadedAssemblies` replaces a scope whose new rows, for a type with a lower RID, move a created
  type's rows of both tables, and checks the created type's `TypeInfo` reads back the same.

## Stage 4 in detail

### The order

The issue's guest stops in `CreateType` at `TypeBuilder_SetParentType`. `CreateTypeNoLock`
(RuntimeTypeBuilder.cs) calls, in order: `SetParentType`, when the type and its parent both have
non-nil tokens; `DefineDefaultConstructor`, for a class with no constructor that is not an interface,
a value type or static; `TypeBuilder_SetMethodIL` for every method, an abstract one included, with an
empty body; then `TypeBuilder_TermCreateClass`.

**Chosen (2026-11-07): `TermCreateClass` first, then `SetParentType`, then `SetMethodIL`.** What
`SetParentType` and `SetMethodIL` write is visible to a guest only through a created type, so neither
could have a guest that exits 0 on real .NET before `TermCreateClass` works. An interface without
methods reaches `TermCreateClass` alone: it has no parent and no methods to give a body. So stage 4a's
guest creates only such interfaces, and the stages after it each re-run the issue's guest, as before.
A Fable consultation agreed, adding that `SetParentType` also needs the extension check to admit a
filled-in `Extends` column of a type not yet created, which is better reviewed against a working
`CreateType`.

### 4a: what CoreCLR does

`TypeBuilder_TermCreateClass` (vm/comdynamic.cpp) calls `ReflectionModule::AddClass(tk)`, which marks
the type created, then, unless `tk` is nil, `ClassLoader::LoadTypeHandleForTypeKey`, and writes the
loaded type's `RuntimeType` to `retType`. The managed caller returns its cached `RuntimeType` for a type
already created, without the QCall.

### 4a: the decisions

* **The type is recorded as created, interned and its `RuntimeType` handed back; its layout is not
  forced.** CoreCLR loads the type fully here and raises `TypeLoadException` for a type it rejects.
  PawPrint builds a type's layout when it is first needed, for a disk type as for this one, and its
  load-time checks are host refusals, not guest exceptions. Forcing the layout here would move such a
  refusal earlier without making it catchable, and would be a path only dynamic types take. A guest
  that catches `TypeLoadException` from `CreateType` is not modelled.
* **The nil token is refused by name.** It is the global type, which `ModuleBuilder.CreateGlobalFunctions`
  creates; no guest reaches it yet. When one does, creating it is `WithCreatedType` for `TypeDef` 1, as
  for any type.

### 4a: the oracle

* `TypeBuilderCreateInterface.cs`, which exits 0 on .NET 10, creates an interface, one extending it, and
  a third defined after both are created. It checks each `CreateType` result's name, namespace, token
  and that it is an interface, and that a second `CreateType` returns the first result. Its
  terminal-state check reads which of the module's types are recorded as created.
* Each mutant of the handler is caught: the type not recorded as created, a null result, and the next
  `TypeDef` loaded in place of the one created.

### 4a: left to later stages

* `SetParentType` (4b), with the extension check admitting a filled-in `Extends` column of a type not yet
  created.
* `SetMethodIL` (4c), for every method, including the empty body an abstract method gets.

### 4b: what CoreCLR's emitter writes

`TypeBuilder_SetParentType` calls `RegMeta::SetTypeParent` (md/compiler/helper.cpp), which writes the
parent's token over the type's `Extends` column and checks nothing else. `DefineType` already wrote a
parent, so for most types this writes the same token again; it differs when the guest called
`SetParent` in between. The managed caller passes a `TypeDef`, `TypeRef` or `TypeSpec` from
`GetTypeTokenInternal`, and never a nil token for either argument.

### 4b: the decisions

* **A type's `Extends` is a column the emitter fills in, like a method's implementation flags.**
  `EmitScope.setTypeDefExtends` overwrites it, and the extension check admits a change to that column
  alone for a type not yet created, reporting it as `FilledInCreatedType` for a created one. Nothing
  reads an uncreated type's parent: the loader refuses the type itself.
* **A `TypeSpec` parent is refused by name**, as `typeDefOrRef` already refuses one for `DefineType`,
  since a scope has no `TypeSpec` table yet.

### 4b: the oracle

* `EmitScopeGen` gains an op that overwrites a type's parent, so the extension properties generate the
  fill, and a created type's view includes its parent. A unit test checks the fill is admitted for a
  type not yet created, even while another is created, and refused for a created one, and that any
  other change to the row is still `RowChanged`.
* `TypeBuilderSetParent.cs`, which exits 0 on .NET 10, creates two static classes, one reparented to
  `Exception` after `DefineType`, and a value type: none gets a default constructor, so with no methods
  none reaches `SetMethodIL`. It checks each created type's `BaseType`.
* Each mutant of the handler is caught: the column left as `DefineType` wrote it, and the previous
  type's parent written instead.

### 4b: left to later stages

* `SetMethodIL` (4c), which the issue's guest now stops at, giving `Thing.get_Value` its body.

### 4c: what CoreCLR's emitter writes

Read from `TypeBuilder_SetMethodIL` (vm/comdynamic.cpp), `IlmethodSize` and `IlmethodEmit`
(inc/corhlpr.cpp), `RegMeta::GetTokenFromSig` and `ImportHelper::FindStandAloneSig`, and
`CreateTypeNoLock` and `RuntimeMethodBuilder.GetLocalSignature` and `GetMaxStack`:

* `CreateTypeNoLock` calls `SetMethodIL` for every method, an abstract one included, whose code is
  empty. The QCall sets the method's RVA only for code that is not empty, so an abstract method keeps
  no body. It always passes a local signature: for a method with no locals, `07 00`
  (`IMAGE_CEE_CS_CALLCONV_LOCAL_SIG` and a count of zero). The QCall's test for an empty signature is
  the bytes `00 00`, so even that gets a `StandAloneSig` row, through `GetTokenFromSig`, which in a
  dynamic module finds the first row with the same bytes before adding one.
* The header is the one `IlmethodSize` picks: tiny when the method zeroes no locals, has no local
  signature, a max stack of at most 8 and fewer than 64 bytes of code; fat otherwise. Since every body
  has a local signature, every body `CreateType` writes is fat.
* A `CreateType` that throws partway, as baking a method with an unmarked label does, has already
  called `SetMethodIL` for the methods before it, and a retry calls it again. In a `Run` assembly a
  method with code has released its body by then, and the retry refuses to bake it again; an abstract
  method's empty code is given again and sets nothing. In a `RunAndCollect` assembly the body is kept,
  and the second call overwrites the first RVA.
* The token fixups are relocations for an image saved to disk; they change no byte in memory.

Measured on .NET 10 (`GetMethodBody` of each method of `TypeBuilderSetMethodIL.cs`'s `T`): every body
reports the max stack, init-locals flag and IL it was built with, and a local signature token of
`StandAloneSig` 1 (`07 00`) or 2 (`07 01 08`). The abstract method reports no body.

### 4c: the decisions

* **A method's body is a column the emitter fills in, as it does the implementation flags.**
  `EmitScope.setMethodBody` overwrites any body the method had, and the extension check admits any
  change to a body for a type not yet created; it cannot tell a body set again from a rewritten one, as
  with the flags. Empty code sets no body. (A Codex review found the first version, which refused a
  second body, crashing on a retried `CreateType` that real .NET completes.)
* **A local signature of no locals reads back as an empty one.** ECMA-335 II.23.2.6 asks for at least
  one local, and SRM's decoder refuses a count of zero, but CoreCLR's emitter writes `07 00` and CoreCLR
  runs it, so `MethodInfo.readMethodBody` reads it as no locals rather than refusing it.
* **Exception handlers are refused by name.** Their section of the body (small or fat clauses,
  `SectEH_Emit`) is 4d.

### 4c: the oracle

* `TypeBuilderSetMethodIL.cs`, which exits 0 on .NET 10, creates an interface with an abstract method
  and a static class whose methods have no locals, one local, the same local signature with locals
  not zeroed, and calls to another of its methods, then invokes each. Its terminal-state check reads
  each method's body bytes, the abstract method's absent, and the module's two `StandAloneSig` rows.
* `TypeBuilderCreateTypeRetry.cs`, which exits 0 on .NET 10, retries a `CreateType` that failed at an
  unmarked label: once after an abstract method's empty code, which succeeds, and once after a method
  with code, whose released body the retry refuses to bake. PawPrint refuses a `RunAndCollect`
  assembly, so a body set a second time is tested only by the unit tests and the extension properties.
* `TestEmitScopeReferences` checks `defineStandAloneSig` against real .NET's
  `ModuleBuilder.GetSignatureMetadataToken`, which reaches the same `GetTokenFromSig`.
* `TestEmitScopeMethods` reads each `methodBody` back from the module's image with SRM, its header tiny
  exactly when CoreCLR's would be; the generator favours each bound of the tiny header.
* The extension properties generate a body set, over any the method had, for a method that is not
  abstract, and a created type's view includes its methods' bodies.
* Each mutant of the handler is caught: a body set for empty code, the init-locals flag inverted, the
  local signature dropped, the max stack off by one, and the body given to another method.
* Issue #2091's guest, `TypeBuilderInterfaceOverride.cs`, now passes and is un-parked.

### 4c: left to later stages

* Exception handlers in a body (4d).

### 4d: what CoreCLR's emitter writes

Read from `TypeBuilder_SetMethodIL` and `ExceptionHandlingSize` (vm/comdynamic.cpp) and `SectEH_Emit`
(inc/corhlpr.cpp):

* The managed `ExceptionHandler` mirrors the native `ExceptionInstance`: seven `int`s, the class token,
  try start and end, filter offset, handler start and end, and kind
  (`ExceptionHandlingClauseOptions`, the same values as `CorExceptionFlag`).
* A clause's class token is the filter offset for a filter, `mdTypeRefNil` for a finally, and the
  token the managed caller passes otherwise, which is 0 for a fault.
* With any clause, the header is fat and marked `CorILMethod_MoreSects`, and the section follows the
  code at the next 4-byte boundary. It is small when it would hold at most 20 clauses (`4 + 12n` bytes
  within a one-byte data size) and every clause's offsets fit 16 bits and lengths 8; otherwise fat. The
  sizing and the emitter compare the data size with 255 by `>` and `<` respectively, but `4 + 12n` is
  never 255, so they always agree.

Measured on .NET 10 (`GetMethodBody` of each method of `TypeBuilderSetMethodILExceptions.cs`): each
clause's kind, ranges and catch type or filter offset are as the `ILGenerator` built them.

### 4d: the decisions

* **A clause is data, `ExceptionClause`, whose handler is a union of the four kinds**, and
  `methodBody` lays the section out from it; the handler decodes the guest's array into it and refuses a
  kind the managed caller never passes.

### 4d: the oracle

* `TypeBuilderSetMethodILExceptions.cs`, which exits 0 on .NET 10, invokes a typed catch, a finally, a
  filter, a fault inside a catch, and a catch whose try block is too long for a small clause. Its
  terminal-state check reads each body back from the module's image and compares it with what .NET 10
  reports, checks each section's format, and reads the finally's small section whole.
* `TestEmitScopeMethods` reads each `methodBody` back with SRM, clauses included, its section small
  exactly when CoreCLR's would be; the generator favours either side of each bound, and draws 20 or 21
  clauses that each fit a small section.
* Each mutant is caught: a filter read as a catch, a fault read as a finally, a handler length off by
  one, a finally's class token 0, and each bound of the small section moved by one.

## Stage 5: what the issue's guest does not reach

With 4c the issue's guest passes. Stage 5 takes the `TypeBuilder` and `ModuleBuilder` QCalls it never
reached, one group a PR, most common first, each with a guest measured on real .NET.

### 5a: `TypeBuilder_DefineField`

Read from `TypeBuilder_DefineField` (vm/comdynamic.cpp), `RegMeta::DefineField` and `_SetFieldProps`
(md/compiler), and `RuntimeFieldBuilder`'s constructor:

* `RuntimeFieldBuilder` clears `FieldAttributes.ReservedMask` (`RTSpecialName`, `HasFieldMarshal`,
  `HasDefault` and `HasFieldRVA`) and passes a field signature from `SignatureHelper`.
* `RegMeta::DefineField` reads a nil parent as the global type, checks for no duplicate
  (`MDDupFieldDef` is not set), clears `RTSpecialName`, `HasFieldMarshal` and `HasDefault` itself, adds
  `RTSpecialName` and `SpecialName` to a field named exactly `value__`, and stores the low 16 bits. With
  `HasFieldRVA` it would add a `FieldRVA` row, which no `TypeBuilder` caller asks for.

`EmitScopeDefinitions.defineField` does the same, refusing `HasFieldRVA`; the handler refuses any
reserved flag, which the managed caller never passes.

### 5a: the oracle

* `TypeBuilderDefineField.cs`, which exits 0 on .NET 10, reads and writes a static and an instance
  field from emitted IL through their `FieldDef` tokens, and creates a value type with a field named
  `value__`. Its terminal-state check reads each field's row as .NET 10 reports the created field.
  Each method is invoked once: with dynamic code supported, reflection's second invocation of a method
  emits an invoke stub, which reaches `StubHelpers.NextCallReturnAddress`, a JIT intrinsic PawPrint
  refuses.
* `TestEmitScopeDefinitions` checks `defineField`'s rows and flags over generated names and flags, and
  its refusal of `HasFieldRVA`.
* Each mutant is caught: the token's RID off by one, `value__` not marked special, and `HasDefault`
  kept; the last only by the unit test, since the managed caller clears it before the emitter sees it.

### 5b: `ModuleBuilder_GetStringConstant`

`ldstr` in emitted IL, and `ModuleBuilder.GetStringMetadataToken`, ask for a user string's token. Read
from `ModuleBuilder_GetStringConstant` (vm/commodule.cpp), `RegMeta::DefineUserString` and
`RegMeta::GetSaveSize`, and measured on .NET 10:

* `DefineUserString` stores the string's UTF-16 code units and a final byte of 1, always, rather than
  ECMA-335's flag for special characters, and the heap finds an entry with the same bytes before it adds
  one, so a string asked for twice is one token.
* `GetSaveSize` gives an empty heap a space whose final byte is 0, and a dynamic module has it from its
  creation: offset 1 holds `" "` before the guest asks for anything, and its first string is at offset
  5. A guest's own `" "` has a final byte of 1, so it is a separate entry.

### 5b: the decision

**The heap holds each entry's bytes (code units and final byte), interned by those bytes**, as the
`#Blob` heap is. Holding strings and computing the bytes when writing would lose the space's final byte
of 0, unless special-cased. `EmitScope.create` starts every scope with the space.

### 5b: the oracle

* `TestEmitScopeImage` asks real .NET's `ModuleBuilder.GetStringMetadataToken` for generated sequences of
  strings, recurring ones, the space, a lone surrogate and each width of length prefix among them, and
  requires `internUserString` to give each the same offset; and reads each entry back from the image.
* `TestEmitScopeExtension` refuses a rewritten entry.
* `ModuleBuilderStringConstant.cs`, which exits 0 on .NET 10, checks tokens and loads the strings from
  emitted methods, the same literal in two methods as one interned object. Its terminal-state check
  reads the heap's entries.
* Each mutant is caught: the final byte 0, the scope starting without the space, the token's table
  byte wrong, and each code unit's high byte dropped.

### 5c: `TypeBuilder_AddInterfaceImpl`

`TypeBuilder.AddInterfaceImplementation` adds an interface after `DefineType`. `RegMeta::AddInterfaceImpl`
looks for a row with the same class and interface (`ImportHelper::FindInterfaceImpl`, over the whole
table, which the dynamic emitter keeps unsorted) and adds one only if there is none. It searches whether
or not the emitter checks for duplicates; `DefineType`'s interfaces, through `_SetImplements`, are
searched only under `MDDupInterfaceImpl`, which a dynamic module does not set, so naming an interface
twice there is two rows. `EmitScopeDefinitions.addInterfaceImpl` does the same search.

### 5c: the oracle

* `TypeBuilderAddInterfaceImpl.cs`, which exits 0 on .NET 10, adds an interface of another assembly and
  one of the module, the first twice, names an interface twice in `DefineType` and adds it again, then
  creates the types and checks they implement the interfaces. Its terminal-state check reads the
  `InterfaceImpl` rows; the TypeRef numbers in it were measured on .NET 10 (`DefineType` asks for the
  interfaces' tokens before the parent's).
* `TestEmitScopeDefinitions` checks `addInterfaceImpl` against a model of distinct pairs in the order
  first asked for.
* Each mutant is caught: the search removed, and the row given to the previous type.

### 5d: `ModuleBuilder_GetTokenFromTypeSpec`

`GetTypeTokenInternal` asks for a TypeSpec for any type that is neither a TypeDef nor a TypeRef: a
generic instantiation, an array, a pointer. `castclass`, `isinst`, `ldtoken` and the like in emitted IL
reach it, and so would a generic parent or interface. `RegMeta::GetTokenFromTypeSpec`, under
`MDDupTypeSpec`, which a dynamic module sets, finds the first row with the same signature
(`ImportHelper::FindTypeSpec`) before adding one.

### 5d: the decision

**`TypeDefOrRef` gains a `TypeSpec` case.** ECMA-335's `TypeDefOrRef` coded index includes TypeSpec, and
every column the scope types `TypeDefOrRef` (a parent, an interface) may name one; a separate union for
the columns that admit a TypeSpec would have none to tell apart. `MemberRefParent` does not gain one
yet: no QCall adds a member of a TypeSpec. A breaking change to `WoofWare.PawPrint.Domain`'s
`TypeDefOrRef`.

### 5d: the oracle

* `TestEmitScopeReferences` checks `defineTypeSpec` against real .NET's
  `ModuleBuilder.GetTypeMetadataToken` over generic instantiations and arrays, using the signatures
  real .NET reports.
* The generator adds TypeSpecs, which parents and interfaces may then name, so the image's round trip
  and the extension properties cover the table.
* `ModuleBuilderTypeSpec.cs`, which exits 0 on .NET 10, emits `castclass` and `isinst` against
  `List<int>` and `Dictionary<string, int>`, checks their tokens, and invokes each method. Its
  terminal-state check reads the TypeSpec signatures as .NET 10 reports them.
* Each mutant is caught: the search removed, the coded index's TypeSpec tag wrong, and the token's
  table byte wrong.

### 5d: left to later stages

* A generic parent or interface: `typeDefOrRef` no longer refuses one, but no guest creates such a type.
* A member of a TypeSpec, as a call to a method of a generic instantiation needs. The managed caller
  builds such a method's signature and asks `ModuleBuilder_GetMemberRefFromSignature` for it, but first
  asks for the return parameter's and each parameter's custom modifiers, which reaches
  `Signature.GetParameterOffsetInternal` for a method signature, which PawPrint refuses (it handles only
  a field signature). A field of a generic instantiation goes through `ModuleBuilder_GetMemberRefOfFieldInfo`
  instead. So `GetMemberRefFromSignature` waited on that refusal, and then on a second one: the
  modifiers are asked of the generic definition's method, and `Signature_GetCustomModifiersAtOffset`
  refused every member of an open generic definition. #2169 and #2175 lifted them, and 5e adds the
  member.

### 5e: a member of a generic instantiation

A call to a method of a generic instantiation, such as `List<int>.Add`, names a MemberRef whose parent
is the instantiation's TypeSpec. Read from `RuntimeModuleBuilder.GetMethodTokenNoLock` and
`GetMemberRefToken`, and from `ModuleBuilder_GetMemberRefFromSignature` (vm/commodule.cpp):

* A method's reference goes through `GetMemberRefToken`. It builds the signature from the generic
  definition's method (`List<T>.Add` takes `!0`), after asking each parameter for its custom
  modifiers, takes the TypeSpec of the declaring instantiation as the parent, and calls
  `ModuleBuilder_GetMemberRefFromSignature`, which is `RegMeta::DefineMemberRef`: under
  `MDDupMemberRef` it finds a row with the same parent, name and signature before adding one.
* A constructor's reference goes through `ModuleBuilder_GetMemberRefOfMethodInfo` with the TypeSpec as
  the parent, and the signature of the constructor's MethodDef.
* `GetMemberRefToken` also passes a TypeRef or a MethodDef as the parent, for a vararg call site, and a
  user-derived `MemberInfo` passes its type's token; neither is a member of a TypeSpec.

### 5e: the decisions

* **`MemberRefParent` gains a `TypeSpec` case**, as ECMA-335's coded index has it (tag 4). A breaking
  change to `WoofWare.PawPrint.Domain`'s `MemberRefParent`.
* **`GetMemberRefFromSignature` refuses any parent but a TypeSpec.** A vararg MemberRef carries a
  sentinel and its own duplicate rules, which want measuring on their own.

### 5e: the oracle

* `ModuleBuilderMemberRefFromSignature.cs`, which exits 0 on .NET 10, emits a method that constructs a
  `List<int>`, adds to it twice and reads its count, checks that naming `Add` twice gives one token, and
  invokes the method. Its terminal-state check reads the TypeSpec and the three MemberRef rows, each a
  member of that TypeSpec, as .NET 10 reports them.
* The generator draws TypeSpec parents, so the image's round trip and the extension properties cover
  the new case.
* Each mutant is caught: the coded index's TypeSpec tag wrong, the token's table byte wrong, the parent's
  row number wrong, and the constructor's parent read as a TypeRef.

### 5e: left to later stages

* A vararg call site's MemberRef, whose parent is a TypeRef or a MethodDef.
* A generic method's instantiation, which also wants a MethodSpec row.

### 5f: a generic method's instantiation

A call to an instantiation of a generic method, such as `Array.Empty<int>`, names a MethodSpec. Read from
`RuntimeModuleBuilder.GetMethodTokenInternal` and `GetMemberRefToken`, `TypeBuilder_DefineMethodSpec`
(vm/comdynamic.cpp) and `RegMeta::DefineMethodSpec` (md/compiler/emit.cpp):

* `GetMethodTokenInternal` takes the generic method definition. If it is of another module, or of a
  generic type, it asks `GetMemberRefToken` for a MemberRef to it; otherwise it asks for its MethodDef,
  which is a generic `MethodBuilder` of this module. It then builds the instantiation's signature
  (`0x0A`, the count, each type argument) and calls `TypeBuilder_DefineMethodSpec` with that token as the
  parent. `ldtoken`, `ldftn` and `ldvirtftn` of a generic method definition stop at the parent.
* `GetMemberRefToken`'s signature is the definition's: the generic flag and a count of generic
  parameters, and `ELEMENT_TYPE_MVAR` for each of the method's own type parameters, which is why
  `SignatureHelper` asks every generic parameter for its `DeclaringMethod` (#2183). Its parent is the
  declaring instantiation's TypeSpec for a generic type, as in 5e, and otherwise, for a method of
  another module, the declaring type's TypeRef. That TypeRef parent is new: 5e saw only TypeSpecs.
  `RegMeta::DefineMemberRef` treats the two alike, finding a row with the same parent, name and
  signature before adding one.
* `RegMeta::DefineMethodSpec` takes a MethodDef or a MemberRef parent and refuses any other. Under
  `MDDupMethodSpec`, which is part of the `MDDupDefault` a dynamic module sets, it finds a row with the
  same parent and the same instantiation bytes (`FindMethodSpecByMethodAndInstantiation`) before adding
  one. ECMA-335 does not sort the MethodSpec table.

### 5f: the decisions

* **The scope gains a MethodSpec table**, rows of a `MethodDefOrRef` (the coded index MethodImpl already
  uses) and an instantiation blob, with `EmitScopeReferences.defineMethodSpec` searching as
  `DefineMethodSpec` does, and the image and the extension carrying the table as they carry TypeSpec.
* **`GetMemberRefFromSignature` accepts a TypeRef or a TypeSpec parent for a signature that is not
  vararg**, and refuses a vararg signature whatever its parent, and a TypeDef or MethodDef parent. A
  MethodDef parent is valid only for a vararg call site (ECMA-335 II.22.25). This decision held that no
  CoreLib path passes a TypeDef; 5o measured one, a call through a created type's `MethodInfo` to an
  instantiation of its generic method, which stays refused by name for a later stage.
* **`TypeBuilder_DefineMethodSpec` accepts a MethodDef parent as well as a MemberRef**, as
  `DefineMethodSpec` does, under the same rule. No guest reaches the MethodDef case yet: a generic
  `MethodBuilder` needs `TypeBuilder_DefineGenericParam` and a GenericParam table, which PawPrint does not
  have. The unit tests cover it.

### 5f: the oracle

* `ModuleBuilderDefineMethodSpec.cs`, which exits 0 on .NET 10, emits calls to `Array.IndexOf<int>`,
  `Array.Empty<int>` twice and `Array.Empty<string>` (members of a TypeRef), and to
  `List<int>.ConvertAll<long>` (a member of a TypeSpec), and invokes the first method. Its
  terminal-state check reads the MemberRef and MethodSpec rows as .NET 10 reports them; the source
  predicts one MemberRef to `Empty`, asked for three times, and one MethodSpec for both of
  `Empty<int>`'s calls.
* `TestEmitScopeReferences` checks `defineMethodSpec` against a model, the first row with the same
  parent and bytes or else a new one, over generated sequences that repeat parents and instantiations.
* The generator draws MethodSpecs, so the image's round trip and the extension properties cover the
  table.
* Each mutant is caught: the search removed, keyed on the parent alone, or keyed on the bytes alone;
  the coded index's MemberRef tag wrong; the table's row count or its extension check dropped; the
  token's table byte wrong; the extended scope never stored; and a TypeRef parent's row number wrong.

### 5f: left to later stages

* A generic `MethodBuilder` (`TypeBuilder_DefineGenericParam`, the GenericParam table), and calls to
  its instantiations through a MethodDef parent.
* A vararg call site's MemberRef.

### 5g: a method reference whose signature names a type by token

3d copies only a signature that names no type by token, and refuses the rest, so a reference to a method
of another module that takes or returns a class or a non-primitive value type (`Type.GetType(string)`,
`Guid.Parse(string)`, `Console.SetOut(TextWriter)`) stops there. CoreCLR translates such a signature
into the dynamic module (`TranslateSigWithScope`). Read from `ModuleBuilder_GetMemberRefOfMethodInfo`
(vm/commodule.cpp), `MDInternalRW::TranslateSigWithScope` (md/enc), and `ImportHelper`'s
`MergeUpdateTokenInFieldSig`, `ImportTypeDef`, `ImportTypeRef`, `CreateNesterHierarchy`,
`CreateAssemblyRefFromAssembly`, `CreateAssemblyRefFromAssemblyRef`, `CompareAssemblyRefToAssembly` and
`FindAssemblyRef` (md/compiler/importhelper.cpp):

* The walk is 3d's. Each `CLASS`, `VALUETYPE`, `CMOD_REQD` or `CMOD_OPT` token is rewritten where the
  walk meets it, so a row the translation adds for an earlier token is found for a later one.
* A **TypeDef** of the source module (`ImportTypeDef`) names a chain of TypeRefs, outermost first, each
  scoped by the one enclosing it, with the source TypeDefs' own names and namespaces. The outermost is
  scoped by the AssemblyRef `CreateAssemblyRefFromAssembly` makes from the source assembly's `Assembly`
  row: its name, version (with no 65535 rule), culture, flags without `PublicKey` (the
  processor-architecture bits untouched, unlike `GetTypeRef`'s), the key reduced to its token, and an
  empty hash. `FindAssemblyRef` looks for it first, as 3a describes. `CreateNesterHierarchy` then finds
  the chain by name from the outermost, and from the first miss adds the rest without looking again,
  each interning a non-empty namespace before the name.
* A **TypeRef** of the source module (`ImportTypeRef`) first compares its outermost AssemblyRef with
  the emitting assembly's `Assembly` row (`CompareAssemblyRefToAssembly`), then copies that AssemblyRef
  whole (`CreateAssemblyRefFromAssemblyRef`: flags, full key or token, and hash as they are), found
  first by `FindAssemblyRef`, which compares a full key with a token by reducing the key. A nil,
  `Module` or `ModuleRef` outermost scope, and a `TypeSpec` token, each take a path of their own.
* A dynamic assembly has one module, so the assembly's emit scope and the module's are one `RegMeta`:
  `CreateAssemblyRef*` adds to the first and finds in the second what it added.
* The managed caller asks for the declaring type's token first, through `GetTypeRef`, whose AssemblyRef
  for the source assembly the translation then finds. So its write of a token for an unsigned assembly
  it has no AssemblyRef for, eight bytes of an uninitialised buffer, is not reached.
* Measured on .NET 10, for `Type.GetType(string)`, `Console.SetOut`, then `typeof(TextWriter)`,
  `Environment.GetFolderPath(SpecialFolder)`, `string.Join(string, IEnumerable<string>)`, a method of
  an unsigned assembly asked for twice and one taking a type nested in it, and `Enumerable.ToList`:
  thirteen TypeRefs and six AssemblyRefs, as the reading predicts. `Console.SetOut` names `TextWriter`
  through `System.Console`'s AssemblyRef to `System.Runtime`, and `GetTypeRef` through
  `System.Private.CoreLib`, so they are two TypeRefs; that is the duplicate 3d measured. `IEnumerable<T>`
  is two for the same reason.

### 5g: the decisions

* **Two stages, split by the token's kind.** 5g translates a TypeDef of the source module and refuses a
  TypeRef by name; 5h translates a TypeRef. CoreLib names its own types by TypeDef, so 5g covers every
  CoreLib method and the guest 3d parked. 5h brings the copied AssemblyRef, which can hold a full key,
  and its comparison with the emitting assembly. The alternative was one stage, whose AssemblyRef
  changes and two translation paths would review as one.
* **The translation is a Domain function over the source module's `MetadataReader`**, threading the
  scope, which gives the translated bytes and the extended scope, or a refusal naming the token it met.
  Domain answers what is in a DLL, the handler already holds the reader, and a dynamic source module is
  read through its own image. The translation finds rows it added itself, so it must thread the scope
  rather than return rows to add. The alternatives were the interpreter translating over its loaded
  assemblies, which only a guest could test, and a callback for each token, which hides what is read.
* **The AssemblyRef is made by a function of its own**, not `defineAssemblyRef`, since it stores the
  version and flags as they are, where `defineAssemblyRef` applies `_SetAssemblyRefProps`' rules.
* **Refused by name, each with a TODO:** a TypeRef token (5h), a TypeSpec token, a token tagged as no
  table (tag 3), and, as an assertion, a TypeDef of an unsigned source assembly whose AssemblyRef the
  scope lacks, which the managed caller never reaches. No row is added for a refused signature.
* **No `WithAssemblyRefTarget` for the translation's rows.** CoreCLR records only `GetTypeRef`'s
  AssemblyRefs in the module's rid map, so a translated reference resolves through the binder, as the
  source module's own does.

### 5g: the oracle

* Real .NET's emitter, in the test host: for every method of a non-generic CoreLib type whose signature
  names a type by token, in order, and some asked again, `GetMethodMetadataToken` on a real dynamic
  module and the Domain functions on a scope (the declaring type's TypeRef as `GetTypeRef` makes it,
  then the translation and `defineMemberRef`) give the same token and the same signature bytes. After
  each, `GetTypeMetadataToken` of each type the method names gives the same TypeRef in both, which
  shows that the translated TypeRef is scoped as `GetTypeRef` scopes it. At the end, both have the same
  TypeRefs by name and defining assembly, and the same AssemblyRefs.
* Over signatures generated from the ECMA-335 grammar, against fabricated source images with nested
  TypeDefs, signed and unsigned, and scopes that may already hold some of the rows: against a model, a
  signature with a refused token is refused at the first; any other adds exactly the AssemblyRef and
  TypeRefs the model adds, in its order, and has 3d's bytes with each token rewritten to the model's
  row.
* `ModuleBuilderMemberRefTranslatedSignature.cs`, parked since 3d, is unparked and extended with a
  nested type and an instantiation, with a terminal-state check of its TypeRef, AssemblyRef and
  MemberRef rows.
* Each mutant is caught: a TypeDef refused; the AssemblyRef made by `defineAssemblyRef`, never found,
  or added for an unsigned assembly; the chain reversed, never found, or without its namespaces; the
  translated token tagged as a TypeDef; the scope not threaded from one token to the next; a custom
  modifier's type skipped; and the handler adding the reference to the scope it had before translating.
  Looking on after the chain's first miss is not a mutant: a TypeRef just added scopes nothing yet.

### 5g: left to later stages

* A TypeRef of the source module (5h), and the paths of a nil, `Module` or `ModuleRef` scope.
* A TypeSpec token in a method signature.
* Field references, through `ModuleBuilder_GetMemberRefOfFieldInfo`, which translate a field signature
  the same way.

### 5h: a TypeRef in a translated signature

A method of any assembly but CoreLib names the types of other assemblies by TypeRef (`Console.SetOut`'s
`TextWriter`, through `System.Console`'s AssemblyRef to `System.Runtime`), which 5g refuses. Read from
`ImportTypeRef`, `GetTRNesterHierarchy`, `CompareAssemblyRefToAssembly`,
`CreateAssemblyRefFromAssemblyRef` and `FindAssemblyRef` (md/compiler/importhelper.cpp):

* The chain is the source TypeRef and the TypeRefs scoping it, outermost last, each with its own
  namespace and name. The outermost's resolution scope decides the rest.
* For an **AssemblyRef**, the emitter first compares it with the emitting assembly's `Assembly` row
  (`CompareAssemblyRefToAssembly`), which a dynamic assembly always has: equal name, culture and all four
  version parts, and, if the reference holds a key or token, the definition's key reduced to a token
  equal to it. A reference holding a full key never matches, since after comparing the keys the
  function compares the reference's bytes with the definition's eight-byte token too.
* An AssemblyRef that does not name the emitting assembly is copied whole
  (`CreateAssemblyRefFromAssemblyRef`): name, version, culture, flags, key or token, and hash value, as
  they are. `FindAssemblyRef` looks for it first. It reads a row's `PublicKey` flag, and the request's,
  only when there are key bytes, and compares a full key with a token by reducing the key. The chain of
  TypeRefs is then found or added in its scope, as for a TypeDef.
* An AssemblyRef that names the emitting assembly makes the chain name a type of the emitting module
  (an ExportedType, or a TypeDef). A nil scope is looked up among the source assembly's ExportedTypes;
  a `Module` or `ModuleRef` scope is the source assembly itself (`CreateAssemblyRefFromAssembly`).

### 5h: the decisions

* **A copied AssemblyRef is a type of its own, `AssemblyRefCopy`, whose key is a union
  `PublicKeyOrToken = Absent | Token | Key`.** A row's `PublicKey` flag and its bytes can disagree; the
  union cannot, so the reader of the source row refuses a flag without bytes, and the copy writes the
  flag from the case. `AssemblyRefRequest`, which only ever holds a token, keeps its shape. The
  alternative was one request type with a flag and bytes that could disagree.
* **`tryFindAssemblyRef` gains `FindAssemblyRef`'s three comparisons**, replacing its refusal of a row
  holding a full key, and one search serves both kinds of request.
* **The comparison with the emitting assembly is modelled exactly**, the full-key case included, since
  it decides between copying the reference and refusing it.
* **Refused by name, each with a TODO**: a nil, `Module` or `ModuleRef` outermost scope, and an
  AssemblyRef that names the emitting assembly. Compilers write the first three for their own module's
  types, which a framework method does not name, and the fourth needs a static assembly to name a
  dynamic one.
* **A reference holding a full key that `StrongNameIsValidPublicKey` rejects is refused, not copied.**
  CoreCLR copies one, since `FindAssemblyRef` reduces no key when no row of that name holds key bytes,
  and then throws `SecurityException` ("Invalid assembly public key", measured on .NET 10) from any later
  search that reduces it to a token. A loaded assembly's AssemblyRefs can hold such a key, because
  loading does not check them. Refusing the copy refuses every such later search, at the
  cost of also refusing copies CoreCLR would never reduce. `StrongName.isValidPublicKey` moves to
  Domain from `NativeRuntimeAssembly` for this, and `AssemblyRefRequest.publicKeyToken` refuses a blob
  it rejects.

### 5h: the oracle

* Real .NET's emitter, in the test host, over methods of non-generic types in several framework
  assemblies other than CoreLib, as 5g's oracle does for CoreLib: the same tokens and signature bytes;
  `GetTypeMetadataToken` of each type named gives the same TypeRef in both, which now is often a second
  row for a type the translation named through a facade; the same TypeRefs and AssemblyRefs at the end.
* Real .NET's emitter over fabricated source images, loaded in the test host: an AssemblyRef holding a
  full key is copied with its key, and a later reference to the same assembly by token finds it.
* Over generated signatures against fabricated sources whose TypeRefs are nested and scoped by
  AssemblyRefs holding nothing, a token or a full key (one naming the emitting assembly), by nil, by the
  module and by a ModuleRef: against a model, as in 5g.
* A guest asking for `Console.SetOut`, then `typeof(TextWriter)`, and `Enumerable.ToList`, with a
  terminal-state check of the TypeRef and AssemblyRef rows, the hash values included.
* Each mutant is caught: a TypeRef refused; the copy's hash dropped, its key left unflagged, or never
  found; a row's key, or a request's, compared with a token unreduced; a full key naming the emitting
  assembly, or the emitting assembly never compared; the chain innermost first; an invalid key copied;
  and a `Module` scope refused as a `ModuleRef`.

### 5h: left to later stages

* A nil, `Module` or `ModuleRef` outermost scope, an AssemblyRef naming the emitting assembly, and a
  copied full key that `StrongNameIsValidPublicKey` rejects.
* A TypeSpec token in a method signature.
* Field references, through `ModuleBuilder_GetMemberRefOfFieldInfo`.

### 5i: a reference to a field of another module

`ILGenerator.Emit(OpCodes.Ldsfld, field)` and its kin ask `ModuleBuilder.GetFieldMetadataToken`, which
for a `RuntimeFieldInfo` takes the declaring type's token (a TypeRef, or for a generic instantiation the
TypeSpec from `GetTokenFromTypeSpec`) and calls `ModuleBuilder_GetMemberRefOfFieldInfo`. PawPrint does
not implement that QCall. Read from it (vm/commodule.cpp), `GetFieldTokenNoLock` (RuntimeModuleBuilder.cs)
and `MergeUpdateTokenInSig`:

* For a TypeDef parent, a type of the dynamic module itself, the QCall hands back the field's own
  FieldDef token. Only a created type has a `RuntimeFieldInfo`.
* Otherwise it reads the field's name and signature from the FieldDef of the declaring type's module,
  translates the signature (`TranslateSigWithScope`) and finds or adds the MemberRef, as 3d and 5g do
  for a method. `MergeUpdateTokenInSig` writes a field signature's calling convention in its shortest
  form and walks its one type as it walks a method's parameter.
* A literal field is an `MdFieldInfo`, not a `RuntimeFieldInfo`, and the managed caller builds its
  signature itself and goes through `GetMemberRefFromSignature`, as 5e describes. A `FieldBuilder` or a
  field of a `TypeBuilder` instantiation goes through `ModuleBuilder_GetMemberRef`.
* Measured on .NET 10, for `String.Empty`, `Type.EmptyTypes`, `Regex.InfiniteMatchTimeout` and
  `ValueTuple<int,int>.Item1`, and a created type's own field: the tokens and signatures the reading
  predicts, the last being its FieldDef. `InfiniteMatchTimeout`'s `TimeSpan` is named through a copy of the
  `System.Text.RegularExpressions` reference to `System.Runtime`, so `typeof(TimeSpan)` is another
  TypeRef.

### 5i: the decisions

* **`importFieldSignature` beside `importMethodSignature`, over one walk**, each refusing the other's
  calling convention. The result type becomes `SignatureImport`, since it is no longer only a method's.
* **The TypeDef parent is refused by name.** The managed caller passes one only for a created type's
  field, after `ModuleBuilder_GetTypeRef` has found the type's TypeDef by name, which 3a refuses; so no
  guest can reach the parent until that is implemented.

### 5i: the oracle

* A property: a field signature of a type translates as a method signature with that type as its one
  parameter does, to the same rows, with the field's calling convention in place of the method's
  header; and each function refuses the other's calling convention.
* Real .NET's emitter over every non-literal field of the non-generic types of CoreLib and the seven
  framework assemblies 5h's oracle reads, alongside their methods: the same tokens, signatures and
  rows.
* `ModuleBuilderMemberRefOfFieldInfo.cs` emits a method reading `String.Empty` and `Type.EmptyTypes`,
  checks their tokens, `Regex.InfiniteMatchTimeout`'s and `ValueTuple<int,int>.Item1`'s, and invokes
  the method, with a terminal-state check of the rows.
* Each mutant is caught: a field walked as a method, its calling convention copied as it is encoded or
  not checked, and the handler translating it as a method's, adding the reference to the scope it had
  before translating, or naming the wrong TypeSpec.

### 5i: left to later stages

* `ModuleBuilder_GetMemberRef`, for a `FieldBuilder` and a field of a `TypeBuilder` instantiation.
* A created type's own field (the TypeDef parent), with `ModuleBuilder_GetTypeRef` for a type of the
  module itself.

### 5j: a reference to the module's own created type

A created type of a dynamic module is a `RuntimeType`, not a `TypeBuilder`, so a reference to it, or to a
`RuntimeMethodInfo` or `RuntimeFieldInfo` of it, takes the paths that 3a, 3d and 5i refuse for the
module's own types. Read from `ModuleBuilder_GetTypeRef`, `ModuleBuilder_GetMemberRefOfMethodInfo` and
`ModuleBuilder_GetMemberRefOfFieldInfo` (vm/commodule.cpp), `RegMeta::FindTypeDefByName`,
`ImportHelper::FindTypeDefByName` and `ImportTypeDef` (md/compiler), and `GetTypeTokenWorkerNoLock`,
`GetMethodTokenNoLock` and `GetFieldTokenNoLock` (RuntimeModuleBuilder.cs):

* `GetTypeTokenWorkerNoLock` asks `GetTypeRef` for a type of this module that is not a `TypeBuilder`,
  `EnumBuilder` or `GenericTypeParameterBuilder`, passing the type's escaped `FullName` and, for a nested
  type, its enclosing type's token. The QCall unescapes the name, and for its own module finds the TypeDef
  (`FindTypeDefByName`): the name divided as `ns::SplitInline` divides it, which is
  `EmitScopeReferences.splitTypeName`, then the first row with that namespace and name that is not
  nested, or, given an enclosing TypeDef, that its `NestedClass` row nests in it. Nothing is added, and no
  AssemblyRef is recorded. A name it does not find fails with `CLDB_E_RECORD_NOTFOUND`, which only a
  user-derived `Type` claiming the module can reach.
* For a method whose `MethodTable`'s module is the emitting module, `GetMemberRefOfMethodInfo` hands back
  the method's MethodDef token, whatever parent it was given.
* For a TypeDef parent, `GetMemberRefOfFieldInfo` hands back the field's FieldDef token. A field of an
  instantiation of a created generic type has a TypeSpec parent, and its signature is translated from the
  emitting module into itself, which `ImportTypeDef` recognises by the scopes' MVIDs and answers with the
  TypeDef.
* Measured on .NET 10 by `ModuleBuilderOwnTypeReferences.cs`: a created type's and a created interface's
  tokens are their TypeDefs; its field, method and constructor have their builders' tokens; a second type
  calls, stores, constructs and loads through them; and no TypeRef or MemberRef is added.

### 5j: the decisions

* **The lookup is 3b's `EmitScopeDefinitions.tryFindTopLevelTypeDef`**, which `defineTopLevelTypeDef`
  already uses to find a duplicate, over the name `splitTypeName` divides. It is `FindTypeDefByName` with
  no enclosing type.
* **Refused by name, each with a TODO:** a nested type of the module, which no scope holds yet (3b refuses
  `DefineNestedType`, since an emit scope has no `NestedClass` table); a name no TypeDef has, for which
  CoreCLR raises a `COMException`; and a field of the module declared under a TypeSpec parent, which needs
  a created generic type, and the scope has no `GenericParam` table either.
* **The FieldDef token is checked against the scope's fields and the parent**, though CoreCLR hands it
  back unread: the managed caller passes a `RuntimeFieldInfo`'s own token and its declaring type's
  TypeDef, so a field the scope lacks, or one the parent does not own, is a PawPrint fault. Likewise the
  method's MethodDef must be a row of the scope.

### 5j: the oracle

* Real .NET's emitter, in the test host, over generated lists of type names (with namespaces, leading,
  trailing and doubled dots, and the characters `FullName` escapes): each name real .NET accepts is
  defined in a real dynamic module and in a scope, each type created, and `GetTypeMetadataToken` of each
  created type equals the TypeDef that `tryFindTopLevelTypeDef` finds for its unescaped, divided
  `FullName`.
* `ModuleBuilderOwnTypeReferences.cs`, with a terminal-state check that the module's TypeRef and
  MemberRef rows are only `object` and `object..ctor`.
* Each mutant of the handlers is caught: a TypeRef added for the module's own type (by the AssemblyRef's
  record, which finds no static assembly of that name); the name not divided; the method's or field's
  token taken from the parent; an AssemblyRef added for the module itself; and the name left
  unescaped, which the guest's `Odd+One` and `Back\+Slash` catch, since `FullName` escapes their
  reserved characters as `TypeNameBuilder::EscapeName` does.

### 5j: left to later stages

* A nested type of the module, once `DefineNestedType` adds `NestedClass` rows, and a field of an
  instantiation of a created generic type, once the scope has `GenericParam` rows (5n).

### 5k: a reference to a member of another module's builder

`ModuleBuilder_GetMemberRef` takes the referring module, the module that defines the member, the parent
token the managed caller made, and the member's MethodDef or FieldDef token in its own module. Read from
it (vm/commodule.cpp) and its callers in `RuntimeModuleBuilder.cs`:

* The QCall reads the member's name and signature from the defining module's metadata, a MethodDef's for
  a MethodDef token and a FieldDef's otherwise, translates the signature into the referring module
  (`TranslateSigWithScope`), and finds or adds the MemberRef (`DefineMemberRef`, under `MDDupMemberRef`)
  under the parent. For a TypeDef parent it first makes a TypeRef for it (`DefineTypeRefHelper`).
* `GetFieldTokenNoLock`, `GetMethodTokenNoLock` and `GetMethodMetadataToken(ConstructorInfo)` call it for
  a `FieldBuilder`, `MethodBuilder` or `ConstructorBuilder` of another module, with the declaring type's
  TypeRef as the parent; and for a field or constructor of a `TypeBuilder` instantiation
  (`FieldOnTypeBuilderInstantiation`, `ConstructorOnTypeBuilderInstantiation`), and a `FieldBuilder` of a
  generic `TypeBuilder`, with the instantiation's TypeSpec. A method of a `TypeBuilder` instantiation goes
  through `GetMemberRefToken` and `GetMemberRefFromSignature` instead, as 5e describes.
* The defining module is the referring module itself for a member of an instantiation of one of its own
  generic `TypeBuilder`s. That needs no created type: a generic parameter's `GenericParam` row is added
  only by `CreateType`, and a TypeSpec naming `!0` can be made before. The translation is then from the
  module into itself, where `ImportTypeDef` keeps each TypeDef token. (For a `FieldBuilder` of a generic
  type the caller passes its own module as the defining one, even when the field is of another.)
* Measured on .NET 10, with `DynLib` defining `Lib.Thing` and a generic `Lib.Box<T>` that it does not
  create, and `DynUser` referring to their members: a field of `int`, a field of type `Thing`, a method and
  a constructor of `Thing`, each a new MemberRef under `Thing`'s TypeRef, the second's signature naming
  `Thing` by that TypeRef; `Box<int>`'s field and constructor, each under `Box<int>`'s TypeSpec, the
  field's signature `!0` as `DynLib` wrote it; the created type's `RuntimeFieldInfo` and
  `RuntimeMethodInfo` finding the rows the builders added; and a method of `DynUser` constructing, calling,
  storing and loading through them.

### 5k: the decisions

* **A member of another module, method or field, under a TypeRef or TypeSpec parent.** The defining
  module's metadata is read as 5i and 5j read a declaring module's: a dynamic module's image is re-read
  as its scope grows, so the builder's row is there whether or not its type is created.
* **The referring module's own member is refused by name, with a TODO.** Two options were weighed. One
  translates from a scope into itself now, keeping each TypeDef and TypeRef token: a second import mode
  beside 5h's, which maps a source TypeDef to a TypeRef chain, and whose only guest would ask for tokens
  it can never run through. The other waits for a generic `TypeBuilder` that can be created, which wants
  `GenericParam` rows (`TypeBuilder_DefineGenericParam`), and then tests the translation by running
  through it. This stage takes the second.
* **A TypeDef parent is refused by name, with a TODO.** No caller passes one for a builder of another
  module, since its declaring type's token is a TypeRef; only a user-derived `FieldBuilder`,
  `MethodBuilder` or `ConstructorBuilder` claiming a type of this module could.
* **The three handlers that translate a member of another module share their tail**: the parent token's
  row, and the MemberRef found or added with the scope re-read. Each handler still decides which parents
  it accepts.

### 5k: the oracle

* `ModuleBuilderMemberRefOfBuilder.cs`, the measurement above, exits 0 on .NET 10. Its terminal-state
  check reads `DynUser`'s AssemblyRef, TypeRef, TypeSpec and MemberRef rows.
* Each mutant is caught: the name and signature read from the referring module, a field's read as a
  method's, the TypeSpec parent read as a TypeRef, the MemberRef added without the find, and the scope
  the handler extended not kept.

### 5k: left to later stages

* A member of an instantiation of the module's own generic `TypeBuilder`, once `CreateType` can add its
  `GenericParam` rows (5n).

### 5l: a generic type's parameters

5f's MethodDef parent, 5j's field of an instantiation of a created generic type and 5k's member of an
instantiation of the module's own generic `TypeBuilder` each need a generic type or method that can be
created, and an emit scope has no `GenericParam` table. Read from `RuntimeTypeBuilder.CreateTypeNoLock` and
`DefineGenericParametersCore`, `RuntimeMethodBuilder.GetToken` and `GetTokenNoLock`,
`RuntimeGenericTypeParameterBuilder`, `TypeBuilder_DefineGenericParam` (vm/comdynamic.cpp),
`RegMeta::DefineGenericParam` and `_SetGenericParamProps` (md/compiler/emit.cpp), and `CMiniMdRW`'s
`FindGenericParamHelper` and `PreSave` (md/enc/metamodelrw.cpp):

* `DefineGenericParameters` makes the parameters' builders and calls nothing native, and a parameter's
  `MetadataToken` is 0 until it has a row. A type's parameters get their rows in `CreateType`, after
  `SetParentType` and before the default constructor and the methods' bodies: one
  `TypeBuilder_DefineGenericParam` each, in position order. A method's parameters get theirs when the
  method is given its token (`GetTokenNoLock`), straight after its `DefineMethod` and before
  `SetMethodImpl`. That can be long before `CreateType`: emitting a call to the method gives it its token,
  and so does asking for it, and giving a method its token first gives one to each earlier method of its
  type.
* Each call passes the name, the owner's TypeDef or MethodDef token, the attributes, the position, and the
  constraint tokens, the interfaces' and then the base type's, ended by a nil token. A parameter without
  constraints passes the nil token alone, never a null array. The managed caller has asked for each
  constraint's token first, so the TypeRefs and TypeSpecs they name exist.
* `RegMeta::DefineGenericParam` looks for a row with the same owner and number only under
  `MDDupGenericParam`, which a dynamic module does not set (vm/assembly.cpp sets `MDDupDefault` and five
  others), so it always adds a row. `_SetGenericParamProps` sets the name only if it is not empty, which
  stores what interning the empty string would; sets the flags unless they are `-1`, truncated to the
  column's 16 bits; and adds one `GenericParamConstraint` row for each constraint token, in the order
  given.
* The read-write metadata keeps the table in the order its rows were added, and finds an owner's rows by
  scanning or through a virtual sort; only `PreSave`, which a module being saved goes through, sorts it by
  owner. So a parameter's token is its place in the order of creation, whoever owns it, and reflection over
  the created type reports the same token.
* A parameter's builder makes its row once: it sets `m_hasBeenCreated` before returning, and a retried
  `CreateType` creates only parameters not yet created.

Measured on .NET 10 with a throwaway probe:

* `Box<T, U>` (TypeDef 3), with `T : class, new(), IComparable<T>` and
  `U : Exception, IDisposable, IFormattable, IMark` (`IMark` a created interface of the module): `T` is
  `0x2A000001` and `U` is `0x2A000002`, each 0 before `CreateType`, and the created type's parameters report
  the same tokens and attributes. `U`'s constraints come back as the three interfaces, then `Exception`, and
  the constraints' TypeRefs were added as ``IComparable`1``, `IDisposable`, `IFormattable`, `Exception`, after
  `DefineType`'s `object`.
* `A<T>` (TypeDef 2) created after `B<U, V>` (TypeDef 3): `U`, `V` and `T` are `0x2A000001` to `0x2A000003`,
  and reflection reports the same. Sorted by owner, `T` would be row 1.
* `C<T>` whose one method is `M<U>` (MethodDef 1), created without asking for `M`'s token: `T` is
  `0x2A000001` and `U` is `0x2A000002`, where ECMA-335's order puts `U` (owner coded index 3) before `T` (4).
  Asking for `M`'s token before `CreateType` makes `U` `0x2A000001` there and then. With a non-generic
  method before `M`, which makes `M` MethodDef 2 (coded index 5), creation order and owner order agree.
* System.Reflection.Metadata refuses an image whose GenericParam table is not in owner order and not
  declared sorted ("Metadata table GenericParam not sorted"). Declared sorted, it reads the image, and gives
  the wrong parameters for a type.
* Attributes of `-1` store 0, and `0x10004` stores 4. An empty name is accepted. A covariant parameter of a
  class makes `CreateType` throw `TypeLoadException` ("declares a covariant or contravariant type parameter
  and is not an interface or delegate"); one of an interface loads. A `CreateType` that throws (for an
  abstract method of a class that is not abstract) and is retried leaves the parameter's token as it was,
  and the next parameter created is the next row.

So the image carries CoreCLR's tokens exactly when the parameters are created in owner order. The work
splits in four, each its own PR:

1. **5l: a type's parameters, without constraints.** The GenericParam table; `TypeBuilder_DefineGenericParam`
   for a TypeDef owner and no constraint token; and `TypeBuilder_TermCreateClass` handing back a generic type
   definition. It lifts none of 5f's, 5j's or 5k's refusals, but makes the last two reachable by a guest
   that runs through them.
2. **Constraints**: the GenericParamConstraint table.
3. **A generic `MethodBuilder`**: MethodDef owners, with which 5f's MethodDef parent reaches a guest. Its
   commonest shape is out of owner order (the third measurement), so it reopens the first decision below.
4. **A reference from a module into itself**: 5k's member of an instantiation of the module's own generic
   `TypeBuilder` and 5j's field of an instantiation of a created generic type, translating a signature from
   the module into itself, where `ImportTypeDef` keeps each TypeDef token. It needs only 1.

### 5l: the decisions

Taken 2026-11-13. Each keeps 5l a strict prefix of what a later stage may need, so that lifting a
refusal is a deletion.

* **Parameters created out of owner order are refused by name.** CoreCLR's token is the creation order,
  and the image must be in owner order (SRM refuses it otherwise, measured). The scope's RID is the
  creation index, which is the token, and no row ever moves, a created type's included, so the reader,
  the loader and the extension check see an ordinary sorted table. The rule is one Domain function,
  `EmitScope.genericParamOrderConflict`: a row is admitted only if its owner's `TypeOrMethodDef` coded
  index is at least that of the scope's last row. The writer's assertion that the table is sorted is in
  one place too. Nothing new reads `GenericParameter.Handle` as a token, and nothing new calls SRM's
  `GetGenericParameters`; the terminal-state check reads the scope's rows through
  `EmitScope.genericParams`. The alternatives, translating the token and writing creation order, are
  under "left to later stages", for the stage that adds method owners.
* **The rows are a table appended in creation order.** `EmitScope.addGenericParam` checks what a row
  names, that its flags fit their two bytes, that its number is the count of rows its owner already has
  (CoreCLR's managed callers create an owner's parameters once each, in position order), and the order
  rule, and refuses loudly otherwise. The alternative, each owner holding its parameters and the writer
  deriving the table, would make the RID handed out depend on rows added later, and lose the creation
  order.
* **The owner is a `TypeOrMethodDef` union with both of ECMA-335's cases**, and Domain and Loader are
  total over it: the writer and the generators cover MethodDef owners, read back through SRM's
  `MethodDefinition.GetGenericParameters`, and `EmitScopeExtension.violations` counts a method's
  parameter as its declaring type's for `AddedToCreatedType`. Only the QCall handler refuses a MethodDef
  owner, by name.
* **The order is checked where the row is defined.** `EmitScopeDefinitions.defineGenericParam` answers
  `Defined` with the RID and the scope, or `OutOfOrder` with the requested owner and the last row's,
  which the handler refuses with a TODO naming both and their coded indexes. `addGenericParam` asserts
  the same, and the writer fails on an unsorted table. The extension check gets a positional
  `RowChanged` and `AddedToCreatedType` for GenericParam, and no moved-row case.
* **Constraints wait for their own stage.** The handler refuses by name a non-nil first constraint token,
  and a null constraint pointer, which `_SetGenericParamProps` reads as leaving the constraints as they
  are. The flags of the special constraints (`class`, `struct`, `new()`) are admitted.
* **The flags are stored as CoreCLR stores them**: 0 for `-1`, and otherwise the low 16 bits (measured).
* **`TypeBuilder_TermCreateClass` hands back the generic type definition**,
  `RuntimeTypeHandleTarget.OpenGenericTypeDefinition`, as CoreCLR returns the typical type, where it
  concretizes a type without parameters.
* **A variant parameter of a type that is not an interface is refused by name at `TermCreateClass`,
  delegates included.** CoreCLR throws `TypeLoadException` from `CreateType` for a class (measured) and
  loads a delegate, and PawPrint models neither. A parameter with both variance bits (`attrs &&& 3 = 3`)
  is refused on any type. `EmitScopeDefinitions.unmodelledVariance` finds either.

### 5l: the oracle

* The generator draws GenericParam rows for types and methods, in owner order and numbered after their
  owner's others, so the image's round trip (each row by RID, and each owner's
  `GetGenericParameters`) and the extension properties cover the table.
* Properties: `defineGenericParam` and `addGenericParam` admit exactly the rows a model of the order rule
  admits, the latter refusing loudly what the former answers `OutOfOrder`; `addGenericParam` admits
  exactly a number equal to the count of the owner's rows; the flags and name are stored as CoreCLR
  stores them; and `unmodelledVariance` finds exactly the parameter a model finds.
* Real .NET's emitter, in the test host, over generated generic interfaces with generated names and
  attributes: each parameter's token, and the name, position and attributes reflection reports for the
  created type's parameter, are the scope's row.
* `TestEmitScopeExtension`: a parameter added to a created type, or to one of its methods, is
  `AddedToCreatedType`, and a changed row is `RowChanged` at its RID.
* `TypeBuilderDefineGenericParam.cs`, which exits 0 on .NET 10, defines `Plain`, `Box<T, U>` with
  `U : class, new()` as flags, and `IOut<out V>`; checks the tokens are 0 before `CreateType` and
  `0x2A000001` to `0x2A000003` after, equal to those of the created types' parameters, with their names,
  positions and attributes; and makes `Box<int, object>`, creates one and calls `Id(42)`. Its
  terminal-state check reads the GenericParam rows. Nothing needed parking.
* `TestTypeBuilderGenericParamRefusals` runs a guest for each refusal by name: a type created after one
  with a higher TypeDef RID, a constraint, a generic method's parameter, a covariant parameter of a class,
  and both variance bits on an interface.

### 5l: left to later stages

* Constraints: one GenericParamConstraint row for each token, the interfaces' then the base type's,
  which `GetGenericParameterConstraints` reports in that order (measured). While parameters are created
  in owner order, their constraints are added in parameter order, so the table is sorted by
  construction.
* A generic `MethodBuilder`, whose parameters get their rows when the method gets its token, and with it
  5f's MethodDef parent. Its commonest shape is out of owner order (the third measurement), so that stage
  chooses again between the order rule and two candidates, both strict extensions of 5l:
  * *B, translate the token.* The scope keeps the creation order and the image sorts the rows stably by
    owner; every place a dynamic module's GenericParam token crosses into or out of the guest translates
    it (`RuntimeType.MetadataToken` of a parameter, `MetadataImport`'s `GetGenericParamProps` and
    `GetParentToken`, later a custom attribute's parent and a constraint's owner), and `Assembly.read`
    learns the permutation, keeping CoreCLR's token where `TypeInfo.Generics` now keeps the handle. A
    missed crossing is a silently wrong token.
  * *F, write creation order and index it ourselves.* The image holds the rows in creation order, the
    table declared sorted so that SRM opens it, and PawPrint builds its own owner-to-rows index in one
    pass per `Assembly.read`, replacing SRM's owner range lookup at its four call sites
    (`TypeInfo.fs:619`, `MethodInfo.fs:1620` and `:1639`, `FieldInfo.fs:245`, all feeding
    `GenericParameter.readAll`). The index is oracled against SRM over real, sorted assemblies. It makes a
    sorted bit untruthful, and SRM's by-RID accessors on a table declared sorted but not sorted must be
    measured before choosing it (only `GetGenericParameters` was measured, and it gives wrong rows).
* A reference from a module into itself: 5k's member of an instantiation of the module's own generic
  `TypeBuilder` and 5j's field of an instantiation of a created generic type, now able to run (5n).
* Types created out of TypeDef order, which the order rule refuses.
* A variant parameter of a class or a delegate, and both variance bits.
* A custom attribute on a parameter, which `CreateType` writes with the parameter's token straight after
  its row; an emit scope has no CustomAttribute table yet.

### 5m: a generic type's parameter constraints

5l refuses a generic parameter with a constraint. Read from `RuntimeTypeBuilder.CreateTypeNoLock`,
`SetParentCore` and `SetInterfaces`, `RuntimeGenericTypeParameterBuilder.SetBaseTypeConstraintCore` and
`SetInterfaceConstraintsCore`, `TypeBuilder_DefineGenericParam` (vm/comdynamic.cpp),
`RegMeta::_SetGenericParamProps` (md/compiler/emit.cpp), `CMiniMdRW::PutToken` (md/enc/metamodelrw.cpp)
and `CMiniMdBase::encodeToken` (md/runtime/metamodel.cpp):

* A parameter's builder is a `RuntimeTypeBuilder`, and its constraints are that builder's parent and
  interfaces. `SetBaseTypeConstraint` is `SetParent`, which refuses an interface (`ArgumentException`,
  measured) and stores `object` for `null`, since a parameter's builder has no `Interface` flag.
  `SetInterfaceConstraints` replaces the interface list.
* `CreateTypeNoLock`, for a parameter, asks `GetTypeTokenInternal` for each interface's token and the
  parent's, which finds or adds the TypeRef or TypeSpec each names, or gives a `TypeBuilder` of the
  module its TypeDef token. It passes the interfaces' tokens in the order they were set, then the
  parent's if there is one, then a nil token.
* `_SetGenericParamProps`, given a non-null array, first gives each existing constraint row of the
  parameter the owner 0, and a parameter just added has none. It then adds one `GenericParamConstraint`
  row for each token before the first whose RID is 0, owned by the parameter's RID, in the order given.
  Nothing looks for a duplicate: `MDDupGenericParamConstraint` is declared in corhdr.h and never read.
* The Constraint column is a `TypeDefOrRef` coded index. `PutToken` encodes a token of any other kind as
  0 in a release build (`encodeToken` returns `mdTokenNil`); no managed caller passes one.
* So, in a dynamic module, where `TypeBuilder_DefineGenericParam` is the only caller, a parameter's
  constraint rows are added straight after its own row, and the table is in owner order whatever the
  order of the parameters.

Measured on .NET 10 with a throwaway probe, reading each parameter's `GenericParamConstraint` RIDs
through CoreLib's internal `MetadataImport.Enum` and the token each constraint names through
`ModuleBuilder.GetTypeMetadataToken`:

* `Box<T, U, V, W>` (TypeDef 4), after `IMark` (TypeDef 2) and `Base : IMark` (TypeDef 3), with
  `T : IComparable<T>`, `U : Stream, IDisposable, IAsyncDisposable, new()` (the base type set first),
  `V : class, Base, IMark` and `W : T`: rows 1 to 7, `T`'s 1, `U`'s 2 to 4, `V`'s 5 and 6, `W`'s 7.
  `GetGenericParameterConstraints` reports each parameter's in RID order: `IComparable<T>` (TypeSpec 2),
  `IDisposable`, `IAsyncDisposable`, `Stream` (TypeRefs 3 to 5), `IMark`, `Base` (TypeDefs 2 and 3), and
  `T` (TypeSpec 1, `!0`), so the interfaces come first however the calls were ordered. The special
  constraints are only flags: a parameter with the `struct` flag and the constraint `IFormattable`
  reports `IFormattable` alone, with no `ValueType`, which Roslyn writes as a row beside the flag and
  `TypeBuilder` does not.
* `SetBaseTypeConstraint(null)` adds a row naming `object` (TypeRef 1). `SetInterfaceConstraints` with
  `IDisposable` twice adds two rows, and the type loads. An interface constraint naming `string` loads.
  Setting the interface constraints twice keeps the second list.
* `MakeGenericType` checks the constraints of a created dynamic type as of any other: `Cmp<T>` with
  `T : IComparable<T>` takes `int` and `string` and refuses `object`; `Two<A, B>` with `B : A` takes
  `(Exception, ArgumentException)` and refuses the reverse, each with `ArgumentException`. With a
  `TypeBuilder` among the arguments it checks nothing, building a `TypeBuilderInstantiation` instead.

### 5m: the decisions

Taken 2026-11-13.

* **The scope gains a `GenericParamConstraint` table, whose rows are added only with their parameter.**
  A `GenericParamConstraintRow` holds its owner's `GenericParamRid` and a `TypeDefOrRef` with
  ECMA-335's three cases. `EmitScope.addGenericParam` takes the parameter's constraints, checks that each
  names a row of the scope, and appends the parameter's row and then one constraint row for each, in the
  order given. A constraint row's owner is then always the scope's last parameter when it is added, so
  the table is in owner order by construction, needs no order rule of its own, and its RIDs are
  CoreCLR's. The writer asserts the order, as it does `GenericParam`'s. Two alternatives were rejected:
  * a separate primitive adding one constraint row, with a rule that its owner is at least the last
    row's: a second order rule, guarding a state no CoreCLR caller reaches;
  * the constraints held on `GenericParamRow` and the table derived by the writer: its RIDs would be the
    same, but the extension check could report a changed constraint only as a changed parameter, and a
    later stage that names a constraint row (a custom attribute on one, which `HasCustomAttribute` can
    name) would change the published row type again.
* **`TypeBuilder_DefineGenericParam` reads the tokens up to the first whose RID is 0**, each a TypeDef,
  TypeRef or TypeSpec of the module's scope, with the reader `TypeBuilder_DefineType` uses for its
  interface tokens, which follows the same rule. A token of another kind, or one naming no row, fails
  loudly: no managed caller passes one, and CoreCLR would store a nil constraint. A null array stays
  refused by name, as 5l refuses it.
* **No duplicate check, and no check of what a constraint names**: a token given twice is two rows, and
  `object` for a null base type is a row, as measured. The managed caller decides what the tokens are.
* **The extension check gets `RowChanged` and `AddedToCreatedType` for the table**, a constraint counting
  as a member of the type that owns its parameter, or of the method's declaring type for a method's
  parameter. Since a constraint is added only with its parameter, a constraint added to a created type
  comes with its parameter's own `AddedToCreatedType`; the constraint's arm says which rows.
* **PawPrint enforces the constraints of a created dynamic type as of any other**: `MakeGenericType`
  reads them from the image through `GenericParameter.readAll`, as for an assembly on disk.
  `TypeBuilderGenericParamConstraintViolated.cs` passes without a change for it.

### 5m: the oracle

* The generator draws, with each generic parameter, constraints naming TypeDefs, TypeRefs and TypeSpecs
  of the scope, repeats included, so the image's round trip (each row by RID, and each parameter's
  `GetConstraints` in order) and the extension properties cover the table.
* Properties: `addGenericParam` appends exactly the given constraints after the parameter, in order,
  and refuses loudly a constraint naming no row; `defineGenericParam` passes them on; and every
  generated scope's constraint rows are in owner order.
* Real .NET's emitter, in the test host, over generated generic interfaces whose parameters carry
  constraints drawn from non-generic interfaces and classes the module defines first, repeats included:
  each parameter's constraint RIDs, read through CoreLib's internal `MetadataImport` as the probe read
  them, and the types reflection reports, are the scope's rows. TypeRef and TypeSpec constraints are
  left to the guest: the scope would need every TypeRef and TypeSpec row real .NET's emitter adds first.
* `TestEmitScopeExtension`: a parameter with constraints added to a created type, or to one of its
  methods, makes each constraint row `AddedToCreatedType`, and a changed constraint row is `RowChanged`
  at its RID.
* `TypeBuilderGenericParamConstraints.cs`, the measurement's `Box<T, U, V, W>`, exits 0 on .NET 10: it
  checks the tokens, each parameter's constraints and flags, makes `Box<int, MemoryStream, Base, int>`,
  and calls a method comparing two `T`s through `constrained. !0 callvirt IComparable<!0>::CompareTo`.
  Its terminal-state check reads the GenericParamConstraint rows.
* `TypeBuilderGenericParamStructFlag.cs`, the `struct` flag without a `ValueType` row, exits 0 on .NET
  10 and is parked: PawPrint reports `ValueType` among the constraints, because
  `GenericParameter.readAll` drops the row Roslyn writes beside the flag and
  `genericParameterConstraintTargets` appends `ValueType` for the flag, whether or not the image had the
  row. That is so for any image without the row, and is not about this table.
* `TypeBuilderGenericParamConstraintViolated.cs` asks `MakeGenericType` for instantiations of created
  dynamic types that break a TypeRef, a TypeDef and a TypeSpec constraint, and expects
  `ArgumentException` for each.
* `TestTypeBuilderGenericParamRefusals` loses its constraint case.
* Each mutant is caught: the constraints dropped or reversed in the scope; the owner off by one in the
  scope or in the writer; a target not checked against the scope; the coded index's TypeDef and TypeRef
  tags swapped, or a TypeSpec tagged as a TypeRef; the writer's columns swapped, or its row count 0; the
  extension check's `RowChanged` or `AddedToCreatedType` arm removed, or the latter naming the first
  parameter's type; and the handler passing no constraints, stopping at the first token rather than
  the nil, or reversing them. The handler's mutants are caught only by the guests. The writer's
  assertion that the table is sorted guards a state the scope cannot hold, so no test reaches it.

### 5m: left to later stages

* A null constraint array, which `_SetGenericParamProps` reads as leaving the constraints as they are;
  no managed caller passes one.
* A generic method's parameters' constraints, which come with a generic `MethodBuilder`; the table
  already takes them.
* If a later stage writes `GenericParam` rows out of creation order (5l's options B and F), the
  constraint rows' owners move with them.
* The `struct` flag's constraints, reported as the rows the metadata holds, which un-parks
  `TypeBuilderGenericParamStructFlag.cs`.

### 5n: a dynamic module's references into itself

5l lets a generic `TypeBuilder` be created, so 5k's member of an instantiation of the module's own generic
`TypeBuilder` and 5j's field of an instantiation of its created generic type can now be run through. Each
is refused by name. Read from `ModuleBuilder_GetMemberRef`, `ModuleBuilder_GetMemberRefOfFieldInfo` and
`ModuleBuilder_GetMemberRefOfMethodInfo` (vm/commodule.cpp), `MDInternalRW::TranslateSigWithScope`
(md/enc), `ImportHelper`'s `MergeUpdateTokenInSig`, `MergeUpdateTokenInFieldSig`, `ImportTypeDef` and
`ImportTypeRef` (md/compiler/importhelper.cpp), and `GetFieldTokenNoLock`, `GetMethodTokenNoLock`,
`GetMethodMetadataToken(ConstructorInfo)`, `GetMethodTokenInternal` and `GetMemberRefToken`
(RuntimeModuleBuilder.cs):

* The walk is 3d's and 5g's. Before each TypeDef or TypeRef token, `ImportTypeDef` and `ImportTypeRef`
  compare the MVIDs of the source's assembly and module scopes with the emitting ones'. A dynamic assembly
  has one module, whose emit scope is the assembly's too, so translating from it into itself finds both
  pairs equal. `ImportTypeDef`, which the walk calls asking for the TypeDef back, then answers the source
  TypeDef token; `ImportTypeRef` reads the source TypeRef's chain and answers the source TypeRef token.
  Neither adds or looks for a row, so no TypeRef or AssemblyRef is added.
* A TypeSpec token's signature is translated in turn and looked for among the emitting scope's TypeSpecs
  (`FindTypeSpec`), and added if absent. Translated into its own scope, a TypeSpec whose signature is in
  its shortest form finds itself, or an earlier row with the same bytes. A token tagged as no table
  (`mdtBaseType`) is copied.
* Otherwise the translation is the walk's: a field's calling convention, a type or method parameter's
  index and an instantiation's argument count in their shortest forms, everything else copied, and bytes
  after the last type dropped. So a signature the module's own `SignatureHelper` wrote, which is in that
  form already and names no TypeSpec, translates into itself unchanged.
* `ModuleBuilder_GetMemberRef` reads the member from the module the managed caller names as the defining
  one, which is the referring module for a `FieldOnTypeBuilderInstantiation` or
  `ConstructorOnTypeBuilderInstantiation` of its own generic `TypeBuilder` and for any `FieldBuilder` of
  a generic type, its own or another module's. Each passes the instantiation's TypeSpec as the parent.
  `ILGenerator` never asks for a constructor of a generic type through it: `GetMethodTokenInternal` sends
  every method and constructor of a generic type, a `TypeBuilder` instantiation's or a created type's,
  through `GetMemberRefToken`, which makes the signature itself and calls
  `ModuleBuilder_GetMemberRefFromSignature`, as 5e describes. `ModuleBuilder.GetMethodMetadataToken` of a
  `ConstructorOnTypeBuilderInstantiation` does reach it.
* `ModuleBuilder_GetMemberRefOfFieldInfo` with a TypeSpec parent reads the field from the module of its
  declaring type, which for an instantiation of the module's own created type is the module itself.
* `ModuleBuilder_GetMemberRefOfMethodInfo` already hands back the MethodDef of any method whose
  `MethodTable`'s module is the emitting module, which is so for a method or constructor of an
  instantiation of the module's own created generic type, whatever parent it was given. 5j implements
  that, so nothing new is reachable there.

Measured on .NET 10 with a throwaway probe, in one module `Dyn` with `Thing` (TypeDef 2), `Box<T>`
(TypeDef 3) holding `T Content`, `Thing Partner` and `Version Version` (TypeRef 2, after `object`), a
default constructor and `T Get()`, and `User` (TypeDef 4); the tokens are read back with
`ResolveSignature` and `ResolveMember`:

* `Get`'s body reading `Content` adds TypeSpec 1, `Box<!0>` (`15 12 0C 01 13 00`), and MemberRef 2,
  `Content` under it with `06 13 00`.
* Before `Box` is created, `Box<int>` (TypeSpec 2) and `Box<Thing>` (TypeSpec 3, `15 12 0C 01 12 08`): each
  field and constructor is a new MemberRef under its TypeSpec. `Partner`'s signature is `06 12 08`, naming
  `Thing` by its TypeDef, and `Version`'s `06 12 09`, by its TypeRef. A method is a MemberRef made from
  `20 00 13 00`. Asked again, a field and a constructor find their rows.
* After `Box` is created, `Box<int>`'s and `Box<Thing>`'s `RuntimeFieldInfo`s find the builders' rows, and
  one not yet named (`Box<Thing>.Version`) is a new MemberRef under TypeSpec 3. The created type's
  constructor and `Get`, through `GetMethodMetadataToken`, are MethodDefs 2 and 3, as CoreCLR's reading
  says.
* No TypeRef or AssemblyRef is added: `Random`, asked for last, is TypeRef 3, and the assembly references
  only `System.Private.CoreLib`.
* `User.Before`, emitted before `Box` is created, constructs a `Box<int>`, stores to its `Content` and
  calls `Get` through the `TypeBuilder` instantiation's members; `User.After`, emitted after, does as much
  for `Box<Thing>` through the created type's. Neither adds a row, and both return 42.
* A `FieldBuilder` of `DynLib`'s generic `Pair<T>`, its FieldDef 2, asked for from `Dyn`: the MemberRef is
  under `Pair<!0>`'s TypeSpec, and its signature is `06 12 08`, which is `Dyn`'s own FieldDef 2,
  `Partner`. A FieldDef that `Dyn` does not have throws `BadImageFormatException` ("Index not found").

### 5n: the decisions

Taken 2026-11-13.

* **The self-translation is the walk's second source, a TypeDef or TypeRef token kept, behind entry points
  of its own that return bytes alone.** The walk's source becomes a private union: another module, read
  through its `MetadataReader`, or the emitting scope itself. Of the latter, a TypeDef or TypeRef token is
  kept after checking that the scope has that row, and a TypeSpec or a token tagged as no table is
  refused. `EmitScopeReferences.importOwnMethodSignature` and `importOwnFieldSignature` take only the
  scope and the bytes, and give the translated bytes or the refusal; no scope comes back, so that "adds
  no rows" is the type's to say. The handlers choose them exactly when the module the QCall reads the
  member from is the referring module. Three options were weighed:
  * *A, this one.* Correct for every token CoreCLR handles without adding a row, refusing the rest; the
    walk stays one walk, the token map a case of data; the Domain change is additive; and the return type
    preserves that nothing was added.
  * *B, the existing entry points compare MVIDs as CoreCLR does*, keeping each token when the source
    reader's module is the scope's. No new API and CoreCLR's own test, but which translation runs is
    decided by data the call site does not show, and the published `importMethodSignature` changes
    meaning for a reader it used to translate into TypeRefs.
  * *C, no translation*: the handler copies the member's signature from the scope as it is. Right for
    every signature `SignatureHelper` writes, but the claim that the walk would give the same bytes is
    then unchecked: an overlong encoding, trailing bytes, or a TypeSpec would diverge silently.
  B hides the choice and changes a published function; C trades a loud refusal for a silent divergence.
* **A TypeSpec token is refused by name, with a TODO.** CoreCLR translates its signature and finds or adds
  a TypeSpec, which can add a row when the signature is not in its shortest form. Only a custom modifier
  naming a generic instantiation puts a TypeSpec in a signature `SignatureHelper` writes.
* **The refusal is its own union, `OwnSignatureRefusal`, of a TypeSpec (as the scope's `TypeSpecRid`)
  and a token tagged as no table.** The cross-module refusals cannot arise from the scope itself. A token
  naming no row of the scope is an `ArgumentException`, as 5g's walk throws for one naming no row of the
  source.
* **The defining module is the one the QCall names**, so a `FieldBuilder` of another module's generic type
  is read as the referring module's FieldDef with its RID, as CoreCLR reads it (measured), and a RID the
  module lacks stays refused with 5k's TODO, where CoreCLR throws.
* **5k's TypeDef parent stays refused.** The managed callers of `ModuleBuilder_GetMemberRef` pass a
  TypeSpec for a member of the module itself, and only a user-derived builder passes a TypeDef.

### 5n: the oracle

* Properties of the self-translation, over signatures generated from the ECMA-335 grammar (5g's
  generator) whose tokens name the scope's TypeDefs and TypeRefs: each translates to its bytes with every
  integer the walk shortens in its shortest form, trailing bytes dropped and every token kept, which is
  the identity on a signature already in that form; translating the result again changes nothing; a field
  signature translates as the method signature taking its type does, with the field's calling convention
  shortest; and a TypeSpec or a token tagged as no table is refused at the first such token, while a
  TypeDef, TypeRef or TypeSpec naming no row of the scope throws.
* `ModuleBuilderSelfReferences.cs`, the measurement above, exits 0 on .NET 10, with a terminal-state check
  of `Dyn`'s AssemblyRef, TypeRef, TypeSpec and MemberRef rows.
* Each mutant is caught: a TypeDef translated to a TypeRef (the handler calling 5g's import with the
  module's own reader, and the walk tagging a kept TypeDef as a TypeRef); a token kept without checking
  its row; a TypeSpec kept; the signature copied unwalked; the MemberRef added without the find; the wrong
  parent; and the scope the handler extended not kept.

### 5n: left to later stages

* A TypeSpec token in a signature translated from the module into itself, which CoreCLR finds or adds by
  its translated bytes, and in one translated from another module (5g).
* A TypeDef parent for a member of the module itself, which only a user-derived builder passes.

### 5o: a generic MethodBuilder's parameters

5l and 5m refuse a generic parameter owned by a method, and with it 5f's MethodSpec with a MethodDef parent
never reaches a guest. Read from `RuntimeMethodBuilder.DefineGenericParametersCore`, `GetToken` and
`GetTokenNoLock`, `RuntimeTypeBuilder.CreateTypeNoLock`, `RuntimeModuleBuilder.GetMethodTokenInternal`,
`GetMemberRefToken` and `GetMethodTokenNoLock`, and `TypeBuilder_DefineGenericParam` and
`TypeBuilder_DefineMethodSpec` (vm/comdynamic.cpp):

* `DefineGenericParameters` on a method makes the builders and calls nothing native, and refuses once the
  method has its token. A method's parameters get their rows in `GetTokenNoLock`, straight after
  `TypeBuilder_DefineMethod` gives the method its MethodDef row and before `SetMethodImpl`, one
  `TypeBuilder_DefineGenericParam` each in position order, the owner being the method's new token. Their
  constraints come with them, as 5m reads.
* A method gets its token when anything asks for it, and `GetToken` first gives one to every earlier method of
  its type that has none. What asks: `MetadataToken`; emitting a call, `ldftn` or `ldtoken` of the method, or of
  an instantiation of it, when its declaring type is not generic (`GetMethodTokenInternal` takes the MethodDef
  token); `DefineParameter`, which writes a Param row naming the method; `SetImplementationFlags`;
  `DefineMethodOverride`; a custom attribute on the method; and, failing all those, `CreateType`, whose loop over the methods asks each generic
  method definition for its token before setting its body.
* A type's parameters get their rows in `CreateType`, after `SetParentType` and before the default constructor
  and that loop (5l). So a generic method of a generic type that has not been given its token by then gets its
  parameters' rows after its type's.
* MethodDef RIDs are given out when a method gets its token, so among themselves methods' parameters are
  created in MethodDef order, and so in owner order. Only the interleaving of methods' and types' parameters can
  break owner order. With the type `TypeDef k` and the method `MethodDef m` (coded indexes `2k` and `2m + 1`),
  `C<T>.M<U>` is in owner order exactly when `m >= k` if `M` gets its token in `CreateType`, and exactly when
  `m < k` if it got it earlier.
* A call to an instantiation of a generic method of a non-generic type of the module, `D.G<int>`, takes `G`'s
  MethodDef token, which gives `G` (and so its parameters) a row there and then, and
  `TypeBuilder_DefineMethodSpec` with that MethodDef as parent, found again under `MDDupMethodSpec` for the
  same instantiation. A call to one of a generic type, `C<T>.M<int>`, whether through `M.MakeGenericMethod` or
  `TypeBuilder.GetMethod(C<X>, M).MakeGenericMethod`, takes `GetMemberRefToken` instead: a MemberRef whose
  parent is the TypeSpec of the declaring type, and a MethodSpec whose parent is that MemberRef. It does not
  give `M` its token. That is 5k's member of an instantiation of the module's own generic `TypeBuilder`, not
  this stage's.

Measured on .NET 10 with a throwaway probe, reading each parameter's token from reflection over the created
types and each MethodSpec's and MemberRef's parent through CoreLib's internal `MetadataImport.GetParentToken`:

| Shape | GenericParam rows, by RID | Owner order |
|---|---|---|
| `C<T>` (TypeDef 2) whose only method is `M<U>` (MethodDef 1) | `T`, `U` | no |
| the same, `M.MetadataToken` asked before `CreateType` | `U`, `T` | yes |
| the same, with `Caller` defined before `M` and emitting `call C<!0>::M<int>` | `T`, `U` (`M` is MethodDef 2) | yes |
| the same, `Caller` defined after `M` | `T`, `U` (`M` is MethodDef 1) | no |
| `P` (TypeDef 2) with `M<U>` and `N<V, W>`, with or without asking `N`'s token first | `U`, `V`, `W` | yes |
| `A` and `B` (TypeDefs 2, 3), not generic, with `A.M<U>` and `B.N<V>`, created in either order, tokens asked in either order | creation order, which is MethodDef order | yes |
| `A<TA>` and `B<TB>` with the same methods, created `A` then `B` | `TA`, `U`, `TB`, `V` | no |
| the same, created `B` then `A` | `TB`, `V`, `TA`, `U` | no |
| `First` (two methods, created), then `C<T>` (TypeDef 3) with `M<U>` (MethodDef 4) | `T`, `U` | yes |
| the same, `M.MetadataToken` asked before `CreateType` | `U`, `T` | no |
| the same, `M.DefineParameter(1, ...)` before `CreateType` | `U`, `T` | no |
| `First`, then `A<TA>` with `M<U>` (MethodDef 4), then `B<TB>` (TypeDef 4) | `TA`, `U`, `TB` | no |
| `P` (three methods, then `G<X>`, MethodDef 4), then `Q<T>` (TypeDef 3) | `X`, `T` | no |
| interface `IFoo` with `M<U>`, then `K<TK> : IFoo` with `IFoo.M<V>` and `DefineMethodOverride` | `U`, `V`, `TK` | yes |

So the order rule admits every module whose generic parameters are all methods', and every module whose are all
types' created in TypeDef order, and refuses most modules that mix them: past the first few types a method's RID
exceeds the RIDs of the types defined after it, so a generic method of one type and a generic type created
later are out of order, and so is a generic method of a generic type that is given its token before
`CreateType`, which `DefineParameter` alone does.

For calls, `D` (TypeDef 2, not generic) with `Caller` (MethodDef 1) calling `G<int>` twice and `G<string>`,
then `E` calling `G<int>` through `G`'s `MethodBuilder` and `G<int>` and `G<long>` through the created `D`'s
`MethodInfo`:

* `X`, `G`'s parameter, is `0x2A000001` as soon as `Caller`'s IL is emitted, and `Caller` and `G` have MethodDef
  tokens 1 and 2.
* `Caller`'s calls give MethodSpecs 1 (`<int>`, used twice) and 2 (`<string>`), each with the parent
  `0x06000002`. There is no MemberRef for them.
* `E`'s call through the `MethodBuilder` finds MethodSpec 1 again. Its calls through the created type's
  `MethodInfo` add a MemberRef (2) named `G` whose parent is `D`'s TypeDef `0x02000002`, and MethodSpecs 3
  (`<int>`) and 4 (`<long>`) whose parent is that MemberRef: `GetMethodTokenInternal` finds that a
  `RuntimeMethodInfo`'s module is not the `ModuleBuilder` and asks `GetMemberRefToken`, which asks
  `GetMemberRefFromSignature` with the type's TypeDef. 5f's decision said no CoreLib path passes a TypeDef
  parent there, and refuses one; this is such a path, and it is a reference from the module into itself.
* `C<T>` with `Caller` calling `M<int>` through `TypeBuilder.GetMethod(C<int>, M)` and `C<T>`: MemberRefs 1
  and 2 with TypeSpec parents 1 and 2, MethodSpecs 1 and 2 with those MemberRefs as parents, and neither call
  gives `M` its token.

System.Reflection.Metadata, read from the .NET 10.0.12 `System.Reflection.Metadata.dll` with
`WoofWare.PawPrint.IlDump`, and measured with a second throwaway probe:

* Each sorted table's reader takes the table's bit of the header's `Sorted` mask. With the bit set it trusts it
  and checks nothing; with it clear it scans the owner column (`CheckSorted`, through
  `MemoryBlock.IsOrderedByReferenceAscending`) and throws "Metadata table GenericParam not sorted" if the column
  is out of order. That is the only check, in the reader's constructor; nothing validates later. PawPrint's
  writer declares no table sorted (`EmitScopeImage`), so today SRM checks every table that must be sorted.
* The only reads of `GenericParam` by owner are `TypeDefinition.GetGenericParameters` and
  `MethodDefinition.GetGenericParameters` (`FindGenericParametersForType` and `ForMethod`, a binary search for a
  run of rows). `GenericParameter.GetConstraints` binary-searches `GenericParamConstraint` by its owner column,
  which holds GenericParam RIDs. Nothing else in the assembly reads either table by key.
* `MetadataBuilder` refuses to serialise `GenericParam` rows added out of owner order
  (`InvalidOperationException`, "Metadata table GenericParam not sorted"), so the probe wrote a sorted image with
  it and permuted the table's rows in the bytes, with the `Sorted` bit left set, and rewrote
  `GenericParamConstraint` so that each parameter's rows follow it, owned by its new RID, as an emit scope's
  are. Over about 8,750 generated images (one to five types, up to three methods each, up to three parameters
  per owner, up to two constraints per parameter), two thirds with the owners' rows interleaved at random and
  a third with each owner's rows contiguous, as CoreCLR creates them:
  * every reader opened, and nothing threw;
  * `GetGenericParameter` by RID gave the right `Parent`, `Index`, `Name` and `Attributes` every time;
  * `GetConstraints` gave exactly the parameter's constraint rows, in order, every time, and
    `GetGenericParameterConstraint` by RID the right `Parameter` and `Type`;
  * `GetGenericParameters` was wrong for about 52% of the owners queried with interleaved rows and 36% with
    contiguous rows, every wrong answer in a permuted image: empty, or (with interleaved rows only) some of
    the owner's rows. It never gave another owner's rows.

### 5o: the decisions

Taken 2026-11-13.

* **R now, then F; B is ruled out.** 5o admits a method owner wherever the order rule admits it, and two later
  stages, 5p and 5q, write creation order with PawPrint's own owner index.
  * B brings back, for one table, both things the top-level decision rejected: two token spaces for one
    module, and a created type's handles shifting when a later row is added. Its failure, a missed crossing,
    is a plausible token that is wrong, which nothing detects.
  * F is CoreCLR's own model: `CMiniMdRW` keeps `GenericParam` in creation order and finds an owner's rows
    through a virtual sort. It keeps option A's property that a handle in an old image means the same in the
    new one.
  * A private metadata stream carrying the order, and an honest unsorted table with its bit clear, were
    considered and rejected: SRM's reader constructor is the only gate, and it refuses an unsorted table whose
    bit is clear, so the choice is between the bit and B.
  * F's false `Sorted` bit is acceptable only if nothing is left relying on it, under four conditions, all
    required: (i) one index path for every assembly, not one for dynamic modules alone; (ii) a mechanical ban
    on `TypeDefinition.GetGenericParameters` and `MethodDefinition.GetGenericParameters`, by a scan of the
    built non-test assemblies' MemberRefs allowing only the index's module, and a text-scan ratchet in the test
    project allowing only the oracle's file; (iii) a checked-in property of SRM's by-RID behaviour on
    creation-order images (`GetGenericParameter`'s `Parent`, `Index`, `Name` and `Attributes`,
    `GetConstraints`, and `GetGenericParameterConstraint`), so that an SRM bump that breaks it fails CI; (iv)
    the bit set unconditionally in an emit image, and documented on `EmitScopeImage` and on
    `DumpedAssembly.PeReader`, every other table's bit staying clear.
* **5p's index is explicit in the Domain API and serves every assembly.** An opaque `GenericParamOwnerIndex`
  with a private constructor, built once in `Assembly.read` by one pass over the rows by RID: a pure core over
  `(rid, owner coded index, number)` rows, tested against a model, and a thin `MetadataReader` adapter. It keeps
  the reader it was built from and asserts reference equality with the reader it is used with. The public
  breaks are `MethodInfo.read`, `FieldInfo.make` and `GenericParameter.readAll`, which takes the index and an
  owner in place of a `GenericParameterHandleCollection`. Building it asserts that each owner's Numbers are
  0 to n-1 in RID order, and refuses the image otherwise; if the oracle's sweep ever trips that on a real
  assembly, the stage stops for a decision. Rejected: a side table keyed on the reader (global mutable state);
  a scan per call (quadratic over CoreLib); generics read beforehand and passed in by the caller.
* **Three PRs.** 5o is R. 5p is the index, the API changes, the oracle (the index equals SRM's
  `GetGenericParameters` over the framework under test and the test assemblies, which can be checked only while
  every image is sorted) and the ratchet, with no change in behaviour; it also moves `TestEmitScopeImage.fs`
  and `TestEmitScopeExtension.fs` to the index. 5q sets `GenericParam`'s bit in the writer and drops
  `requireGenericParamsSorted` (keeping the constraints' check), deletes `genericParamOrderConflict` and
  `OutOfOrder`, moves the generator to creation order, adds the SRM by-RID property, and adds guests for the
  mixed shapes.
* **The TypeDef-parent MemberRef is a later stage**, with 5j's and 5k's references from the module into
  itself. 5o refuses it by name: `ModuleBuilder_GetMemberRefFromSignature`'s TODO names the case, and
  `TestTypeBuilderGenericParamRefusals` runs it. 5o's guest calls only through the `MethodBuilder`.

* **A covariant or contravariant parameter of a method is refused by name at `TermCreateClass`**, of an
  interface's method too, as 5l refuses a variant parameter of a class: CoreCLR's class loader throws
  `TypeLoadException` from `CreateType` for it (`VLDTR_E_GP_ILLEGAL_VARIANT_MVAR`, measured on .NET 10), and
  PawPrint models neither. `EmitScopeDefinitions.unmodelledVariance` finds it among the parameters of the type's
  methods.

The options weighed, each a strict extension of 5l's and 5m's tables: for a module whose parameters are created
in owner order, each writes the same image, and each leaves `GenericParam` RIDs in the scope as CoreCLR's
tokens. They differ in what the image holds and who must know.

**R. Keep the order rule, and admit a method owner wherever the rule admits it (chosen for 5o).** The handler stops refusing a
MethodDef owner; everything else stays.

* Correctness: exact where it runs, and loud otherwise, by the existing refusal naming both owners.
* What runs: a module whose generic parameters are all methods' (always in owner order, by the reading above),
  and so 5f's MethodDef-parent MethodSpec; a module whose are all types'; mixtures that happen to be ordered.
  It refuses most mixtures, including a generic method of a generic type given a parameter name with
  `DefineParameter` in any module past its first type.
* Blast radius if wrong: none beyond the handler; the rule is already oracled.
* Reversibility: total. B and F each delete the rule.
* Information: the image is CoreCLR's tokens and owner-sorted, with a truthful header.
* Size: deleting one refusal, a guest, and moving 5l's refusal test from "a generic method's parameter" to a
  mixed shape.
* Oracle: 5l's, plus the guest below.

**B. Translate the token (rejected).** The scope keeps creation order. The writer sorts `GenericParam` stably by owner,
remaps `GenericParamConstraint`'s owner column to the image RIDs and re-sorts it, and a dynamic module's
`DumpedAssembly` carries the permutation (from the scope, since the image cannot say what order its rows were
created in), and every place a GenericParam token crosses into or out of the guest translates it.

* Correctness: right at every crossing translated, and silently wrong at one missed. The crossings today:
  out to the guest, `NativeRuntimeTypeHelpers.fs` ~697 and ~715 (`RuntimeTypeHandle.GetToken` of a type's and
  a method's parameter); in from the guest, `NativeMetadataImport.fs` ~2138 (`GetParentToken`) and ~2258
  (`GetGenericParamProps`). Later: `GenericParamConstraint` tokens, which are permuted too (in `C<T :
  IDisposable>.M<U : IComparable, IFormattable>`, CoreCLR's constraint rows are `T`'s then `U`'s, and the
  image's `U`'s then `T`'s), once `MetadataImport.Enum` answers `mdtGenericParamConstraint`; and a custom
  attribute on a parameter, whose `CustomAttributesByParentToken` key is the image's token.
* Rows of a created type move. A parameter created later with a lower owner is inserted before them in the
  image, so a created type's `GenericParameter.Handle` from an earlier image names another row in the next.
  The extension check (no row of a created type changes) and every cached `TypeInfo` assume the opposite. So
  `GenericParameter.Handle` must stop being an image handle for a dynamic module (holding CoreCLR's token, as
  5l's note says), which makes `TypeLoad.fs` ~880 (`GetGenericParameter(parameter.Handle.Get).GetConstraints ()`)
  and `GenericParameter.readAll` itself translate too, and the extension check must admit a moved
  `GenericParam` and `GenericParamConstraint` row, as 3f admitted moved `MethodImpl` rows, but for a table
  whose RIDs, unlike 3f's, a guest sees.
* Blast radius if wrong: a token that is plausible and wrong, in reflection and in `MetadataImport`.
* Reversibility: the permutation lives in the writer and `DumpedAssembly`, and each crossing names it, so it
  can be removed site by site.
* Information: the image is ECMA-335-conformant with a truthful header; CoreCLR's numbering is held beside it.
* Size: the writer's sort and remap; the permutation in `DumpedAssembly` and how `Assembly.read` of a dynamic
  module receives it; seven sites translated now, two kinds later; the extension check's moved-row case.
* Oracle: a property that the writer's permutation and the scope's are inverse, over generated creation
  orders; real .NET's emitter in the test host for each crossing, over generated mixed shapes; the guests below.

**F. Write creation order, and find an owner's parameters ourselves (chosen for 5p and 5q).** The image holds `GenericParam` in the
scope's order, with the table's `Sorted` bit set so that SRM opens it, and PawPrint stops asking SRM for an
owner's parameters: `Assembly.read` builds, in one pass over the table by RID, an index from each owner to its
rows in RID order, and `GenericParameter.readAll` takes that index's rows where it now takes SRM's
`GenericParameterHandleCollection`.

* Correctness: by-RID reads and `GetConstraints` are right on such an image (measured above), and
  `GenericParamConstraint` stays genuinely sorted by its owner, which is now the creation RID, so it keeps a
  clear bit and SRM keeps checking it. Every RID in the image is CoreCLR's token, so nothing translates, and no
  row moves, so the extension check and cached `TypeInfo`s are untouched.
* The SRM calls replaced: `TypeInfo.fs` ~637, `MethodInfo.fs` ~1621 and ~1640, and `FieldInfo.fs` ~245, all
  feeding `readAll`. Reaching them threads the index through `TypeInfo.read` (from `Assembly.fs` ~1202),
  `MethodInfo.read` (from `TypeInfo.fs` ~641) and `FieldInfo.make` (from `TypeInfo.fs` ~599, `Assembly.fs`
  ~1314 and `TestFabricatedFieldMarshal.fs` ~379), a breaking change to `WoofWare.PawPrint.Domain`. Tests that
  read an emit image's parameters through SRM switch to the index: `TestEmitScopeImage.fs` ~269 and ~313, and
  `TestEmitScopeExtension.fs` ~86 and ~110. `TestNativeMetadataImport.fs` ~1386 and ~1400 read compiled
  assemblies and need not.
* The cost is a false header bit: the image says `GenericParam` is sorted when it may not be. Only PawPrint
  reads these images (a guest cannot get a dynamic module's bytes), and only SRM's two
  `GetGenericParameters` read the bit's promise; each answers wrongly and quietly (empty or short, never
  another owner's rows). A ratchet test, failing on any call of `GetGenericParameters` outside the index's
  module, keeps a later reader from trusting it. Setting the bit only when the table is out of order is
  possible and changes nothing SRM does; setting it always is one fewer branch.
* Blast radius if wrong: the index replaces SRM's lookup for every assembly PawPrint reads, CoreLib included,
  so a bug in it is everywhere. Restricting it to dynamic modules confines that, at the price of two paths that
  must agree; with the oracle below, the single path is the better trade.
* Reversibility: the index is a pure function of a `MetadataReader` and its four call sites are mechanical, so
  going back to SRM, or on to B, is a deletion plus whatever B needs.
* Information: every RID is CoreCLR's, in the scope, the image and reflection alike, and the image records the
  creation order. It loses ECMA-335 conformance of one table, which only the header bit admits.
* Size: one new Domain function and its signature threading; the writer setting one bit and dropping
  `requireGenericParamsSorted`; the scope dropping `genericParamOrderConflict` and `OutOfOrder`.
* Oracle: the index against SRM's `GetGenericParameters` for every TypeDef and MethodDef of every assembly in
  the framework under test and the test assemblies, which are sorted; against a model over generated
  creation-order images, built as the probe built them; and the guests below.

A fourth shape, the scope supplying `Assembly.read` with each owner's parameters directly rather than the index
reading them from the image, is F with a second input to `Assembly.read` for a dynamic module only; it buys
nothing the index does not.

### 5o: the oracle

* The generator already draws MethodDef owners; the properties of 5l and 5m stand.
* `TypeBuilderGenericMethod.cs`, which exits 0 on .NET 10: `D` with `G<X> where X : IComparable` and
  `Caller` calling `G<int>` twice and `G<string>`; checks `X`'s token and constraint, and that the two `<int>`
  calls share one MethodSpec token, read from `Caller`'s IL; invokes `Caller`, and `G<long>` through
  reflection. Its terminal-state check reads the GenericParam, GenericParamConstraint and MethodSpec rows.
* Properties: `unmodelledVariance` finds exactly the parameter a model finds, now counting the parameters of
  the type's methods.
* `TestTypeBuilderGenericParamRefusals` replaces "a generic method's parameter" with two shapes refused out of
  order: `C<T>` whose only method is `M<U>`, created without asking `M`'s token, and `C<T>` whose `M<U>` is asked
  for its token before `CreateType`, after another type. (`DefineParameter` would give `M` its token too, but
  reaches `TypeBuilder_SetParamInfo`, which PawPrint does not implement.) It adds a contravariant parameter of an interface's
  method, and the TypeDef-parent MemberRef.
* Mutants: a MethodDef owner refused again; the owner's coded index wrong for a method; a method's parameter
  counted in `AddedToCreatedType` as another type's; the handler's check that the MethodDef names a row of the
  scope removed.

### 5o: left to later stages

* **5p: PawPrint's own index of each owner's generic parameters**, with no change in behaviour, as decided
  above: in progress on this branch, recorded under "5p" below.
* **5q: parameters out of owner order**, as decided above: built on this branch, recorded under "5q"
  below. Its guests are the mixed shapes measured above that
  the order rule refuses (`C<T>.M<U>` both ways, `A<TA>.M<U>` then `B<TB>`, `P.G<X>` then `Q<T>`), each checking
  the tokens real .NET reports and running an instantiation.
* A MemberRef whose parent is the module's own TypeDef, for a generic method reached through the created
  type's `MethodInfo` (measured above), which 5f refuses; a reference from the module into itself, alongside
  5k's and 5j's.
* A call to an instantiation of a generic method of the module's own generic `TypeBuilder`, a MemberRef on a
  TypeSpec of it (5k).
* A custom attribute on a method's parameter, as on a type's (5l).
* `TypeBuilder_SetParamInfo`, which `MethodBuilder.DefineParameter` reaches, and with it a guest naming a
  generic method's parameter.

### 5p: PawPrint's own index of each owner's generic parameters

Built 2026-11-13 as decided under "5o: the decisions", with no change in behaviour: the writer, its `Sorted`
bit and the order rule are untouched, and every image PawPrint reads is still sorted.

* `GenericParamOwnerIndex` (`WoofWare.PawPrint.Domain/GenericParamOwnerIndex.fs`) holds the `MetadataReader`
  it was built from and each owner's rows in RID order. Its core, `group`, takes `GenericParamOwnerRow`s
  (a `GenericParameterHandle`, a `GenericParamOwner`, which is a TypeDef or MethodDef handle, and the
  `Number` column), which must be the whole table from RID 1 in order, and returns each owner's rows or the
  first `GenericParamNumberingError`. `ofReader` reads each row's `Parent` and `Index` by RID and hands them to
  `group`; `parametersOf` answers an owner's rows, empty if it has none, and fails unless asked through the
  very reader the index was built from.
* `GenericParamOwner` is new rather than `EmitScope`'s `TypeOrMethodDef`, which names RIDs of an emit scope,
  not handles of an image, and is compiled after `GenericParameter.fs`.
* `Assembly.read` builds the index before it reads any type, and refuses the image on a numbering error.
  `TypeInfo.read`, `MethodInfo.read`, `FieldInfo.make` and `GenericParameter.readAll` take it; the last takes
  it and an owner in place of a `GenericParameterHandleCollection`.
* The oracle, `TestGenericParamOwnerOracle`, found the index equal to SRM's `GetGenericParameters` for every
  TypeDef and MethodDef, and no image tripping the numbering assertion, over the .NET 10.0.12 shared
  framework (172 images; 2,057 generic types and 4,707 generic methods), its linux-x64 pack (172; 2,067;
  4,756), ASP.NET Core (141; 1,246; 1,285) and the test assembly's directory (41; 4,432; 6,448). It refuses
  to compare an image whose GenericParam table is not sorted by owner, so 5q's creation-order images are
  outside it, as expected.
* The ratchet, `TestGenericParamOwnerRatchet`: an IL scan of every PawPrint assembly the test assembly
  references that is not a test, finding each instruction naming a MemberRef `GetGenericParameters` on
  `System.Reflection.Metadata.TypeDefinition` or `MethodDefinition` and allowing it only inside the index's
  module (`WoofWare.PawPrint.GenericParamOwnerIndexModule`, nested closures included), which itself calls
  neither; and a text scan of the test project allowing the name only in the oracle's file, an exact count.
  `TestNativeMetadataImport` now takes a parameter's handle from the parsed `TypeInfo` and `MethodInfo`, so it
  needs no allowance.
* `MetadataBuilder` refuses to serialise an owner's parameters unless their numbers increase, as well as out
  of owner order, so the adapter's test writes each owner's rows numbered 0, 1, ... and then overwrites the
  `Number` column in the bytes.

### 5q: generic parameters in creation order

Built 2026-11-13 as decided under "5o: the decisions": option F's conditions (iii) and (iv), on 5p's index
and ratchet, which met (i) and (ii).

* `EmitScopeImage` writes `GenericParam` in the scope's order, which is creation order, and sets the
  table's bit of the header's `Sorted` mask unconditionally, every other bit staying clear.
  `requireGenericParamsSorted` is gone; `requireGenericParamConstraintsSorted` stays, since
  `GenericParamConstraint` is genuinely sorted by its owner's RID. The false declaration is documented on
  `EmitScopeImage` and on `DumpedAssembly.PeReader`.
* `EmitScope.genericParamOrderConflict` is deleted, and `EmitScope.addGenericParam` admits a row of any
  owner of the scope. With nothing left to refuse, `GenericParamDefinition` would have had one case, so it
  is deleted too: `EmitScopeDefinitions.defineGenericParam` returns the new `GenericParamRid` and scope,
  and the handler's TODO for an out-of-order parameter went with the case. These three are the public
  breaks to `WoofWare.PawPrint.Domain`.
* The generator (`EmitScopeGen.genericParamOwners`) draws a parameter's owner from every type and method
  of the scope, so generated scopes hold rows out of owner order, and every property over them (the
  round trip, the extension check, the image's extension property) now covers creation order.
* Condition (iii) is `TestSrmCreationOrderGenericParams`: over generated scopes whose parameters are
  interleaved over their owners or in one run per owner in a random owner order (as CoreCLR makes them),
  the header declares `GenericParam` sorted and nothing else (read from where SRM puts the `Module`
  table's rows, and checked against the `Valid` mask there); `GetGenericParameter` by RID gives each row's
  `Parent`, `Index`, `Name` and `Attributes`; `GetConstraints` gives exactly the parameter's constraint
  rows; `GetGenericParameterConstraint` by RID gives each row's `Parameter` and `Type`; and
  `GenericParamOwnerIndex` gives each TypeDef's and MethodDef's rows as the scope has them. Its fixed
  sample reaches 441 interleaved and 420 contiguous images out of owner order, and 1,414 parameters out of
  place with constraints.
* Four guests, each run on .NET 10.0.12 first, where each exits 0, and then under PawPrint, where each
  passed with no change beyond removing the order rule:

  | Guest | Owners | GenericParam rows, by RID |
  |---|---|---|
  | `TypeBuilderMixedParamsMethodAfterType.cs` | `C<T>` TypeDef 2, `M<U>` MethodDef 1 | `T`, `U` |
  | `TypeBuilderMixedParamsMethodBeforeType.cs` | `First` TypeDef 2, `C<T>` TypeDef 3, `M<U>` MethodDef 4, its token asked before `CreateType` | `U`, `T` |
  | `TypeBuilderMixedParamsLaterGenericType.cs` | `A<TA>` TypeDef 3, `M<U>` MethodDef 4, `B<TB>` TypeDef 4 | `TA`, `U`, `TB` |
  | `TypeBuilderMixedParamsGenericMethodThenType.cs` | `P` TypeDef 2, `G<X>` MethodDef 4, `Q<T>` TypeDef 3 | `X`, `T` |

  Each checks the builders' and the created types' tokens, each parameter's owner and position, and runs
  an instantiation: `M<U>` boxes a type's and a method's parameter through TypeSpecs, and `Q<T>.Call` calls
  `G<T>` through a MethodSpec whose parent is `G`'s MethodDef and whose instantiation is `VAR 0`
  (`0A 01 13 00`), which the last guest's terminal check reads with the rows.
* Nothing else assumed a sorted table. The ratchet passes, so only the index's module calls SRM's lookup;
  `TestGenericParamOwnerOracle` reads only images on disk, which a dynamic module never is, and refuses an
  unsorted one; `TypeLoad`'s read of a parameter's constraints is by RID; and `EmitScopeExtension` compares
  `GenericParam` rows by position, which is their RID, in any order.
* Mutants, each run against the emit-scope, SRM, index and refusal tests and the TypeBuilder guests:

  | Mutant | Killed by |
  |---|---|
  | The `GenericParam` bit not set | SRM refuses every out-of-order image: the SRM property, the round trip, the extension properties, `Assembly.read`, the four guests (27 tests) |
  | The `InterfaceImpl` bit set too | the SRM property's header check alone: the writer sorts that table, so SRM reads the same |
  | The `GenericParamConstraint` bit set too | the SRM property's header check alone, likewise |
  | The constraints' sortedness check dropped | survives: `EmitScope`'s record is private and only `addGenericParam` appends constraint rows, each owned by the row it adds, so no scope reaches the check |
  | The writer sorting `GenericParam` by owner | the SRM property, the round trip, the extension properties, the four guests (23 tests) |
  | The generator in owner order again | the SRM property's coverage, and the generator's coverage test |
  | `EmitScopeExtension` comparing `GenericParam` rows in owner order | the out-of-order extension test, the extension properties, the four guests |

### 5r: a MemberRef whose parent is the module's own TypeDef

5f and 5o refuse a TypeDef parent in `ModuleBuilder_GetMemberRefFromSignature`, and 5o measured a guest that
reaches one: a call to an instantiation of a generic method of the module's own created type through the created
type's `MethodInfo`. Read from `RuntimeModuleBuilder.GetMethodTokenInternal`, `GetMemberRefToken`,
`GetMemberRefSignature`, `GetMethodTokenNoLock`, `GetFieldTokenNoLock` and `GetMethodMetadataToken(ConstructorInfo)`,
`ModuleBuilder_GetMemberRefFromSignature` and `ModuleBuilder_GetMemberRef` (vm/commodule.cpp), `RegMeta::DefineMemberRef`
(md/compiler/emit.cpp), `ImportHelper::FindMemberRef` (md/compiler/importhelper.cpp) and
`MemberLoader::GetDescFromMemberRef` (vm/memberload.cpp):

* `GetMethodTokenInternal` takes a generic method's definition, and asks `GetMemberRefToken` for it when its module
  is not the `ModuleBuilder` or its declaring type is generic. A created type's `RuntimeMethodInfo` is of the
  `RuntimeModule`, which is not equal to the `ModuleBuilder`, so its generic methods always go there; a builder's
  (`MethodBuilder`) do not. `GetMemberRefToken` makes the signature from the method's `ParameterInfo`s
  (`GetMemberRefSignature`), with the generic flag and count and `MVAR` for the method's own parameters, and names
  each type through `GetTypeTokenInternal`, so the module's own types by TypeDef (5j). Its parent is the declaring
  instantiation's TypeSpec for a generic type; otherwise, because `method.Module.Equals(this)` is false, it is
  `GetTypeMetadataToken(method.DeclaringType)`, which for a created type of the module is its TypeDef (5j). The
  third branch, a MethodDef parent, is reached only by a vararg method of a `MethodBuilder`.
* A non-generic method of a created type, through its `RuntimeMethodInfo`, goes to `GetMethodTokenNoLock`, whose
  `GetMemberRefOfMethodInfo` hands back its MethodDef (5j). So does `ModuleBuilder.GetMethodMetadataToken` of the
  generic method or of an instantiation of it: the asymmetry is between `ILGenerator`'s path and the public one.
* The same parent comes from a user-derived `MethodInfo` whose `DeclaringType`, or a user-derived `FieldInfo` or
  `ConstructorInfo` whose `ReflectedType`, is a type of the module (`GetMethodTokenNoLock`'s, `GetFieldTokenNoLock`'s
  and `GetMethodMetadataToken`'s last arms, each asking `GetTypeTokenInternal` of that type), and from a vararg
  method of a created type (`GetMemberRefToken` with a sentinel), each through the same QCall. The QCall cannot
  tell them apart.
* `ModuleBuilder_GetMemberRefFromSignature` calls `DefineMemberRef` with the token and the bytes as they are: no
  translation, and no reading of the parent. `RegMeta::DefineMemberRef` admits a TypeDef parent with the others
  and, under `MDDupMemberRef`, finds a row with the same parent token, name and signature bytes
  (`FindMemberRef` compares the parent token exactly, so a TypeDef and a TypeRef to the same type are two rows)
  before adding one.
* The MethodSpec is 5f's: `TypeBuilder_DefineMethodSpec` with the MemberRef as parent, found again for the same
  parent and instantiation. It is a different row from the instantiation through the `MethodBuilder`, whose parent
  is the MethodDef.
* Binding: `GetDescFromMemberRef` loads a TypeDef parent as it loads a TypeRef (`LoadTypeDefOrRefThrowing`) and
  then looks for the member by name and signature, so a MemberRef naming a member of its own module's TypeDef runs.
* `ModuleBuilder_GetMemberRef` is not this path: given a TypeDef parent it first makes a TypeRef to the type, scoped
  by the module itself (`DefineTypeRefHelper`). Only a user-derived builder reaches it with a TypeDef (5k, 5n).

ECMA-335 II.22.25: the `Class` column is a `MemberRefParent` coded index, which includes TypeDef, but rule 1's
informative list (TypeRef for another module, ModuleRef for a global, MethodDef for a vararg call site, TypeSpec for a
generic type) does not name it; rule 1a's note says a TypeRef to a type of the same module is unusual but valid "in
which case, its TypeDef token can be used instead". CoreCLR emits and binds a TypeDef parent. SRM reads one, and
PawPrint's Loader already binds one: `MemberReferenceParent.resolve` has a `TypeDefinition` arm (`Nominal`), and
fabricated images in the tests already hold TypeDef-parent MemberRefs (a MethodImpl body in
`TestFabricatedClassSlotMethodImpl`, a field in `TestEscapeAnalysis`, a method in `UnmodelledImage`). The emit scope already
holds one: `MemberRefParent.TypeDef`, written by `EmitScopeImage` (tag 0), checked by `EmitScope.addMemberRef`, drawn
by `EmitScopeGen`, so the round trip and extension properties cover it.

Measured on .NET 10.0.12 with a throwaway probe (`5r-probe`), in a module `Own` with `D` (TypeDef 2: `G<X>(X) => x`,
MethodDef 1; `N(int) => n + 1`, MethodDef 2; `H<Y>(D, Y) => 7`, MethodDef 3; a static `int F`, FieldDef 1) and `K`
(TypeDef 3, a default constructor, which adds MemberRef 1, `object..ctor` under TypeRef 1), both created, and then `E`
emitting through the created types' members. Rows read with `ResolveMember`, `ResolveSignature` and CoreLib's
internal `MetadataImport.GetParentToken`, and IL operands from `GetILAsByteArray`:

| Emitted | Operand | Rows added |
|---|---|---|
| `call` `G<int>` through `created.GetMethod("G")` | MethodSpec 1 | MemberRef 2 `G`, parent `0x02000002`, `10 01 01 1E 00 1E 00`; MethodSpec 1 under it |
| the same again, in the same method and in another | MethodSpec 1 | none |
| `call` `N` through its `RuntimeMethodInfo` | MethodDef `0x06000002` | none |
| `call` `H<string>` | MethodSpec 2 | MemberRef 3 `H`, parent TypeDef 2, `10 01 02 08 12 08 1E 00` (`D` as `12 08`) |
| `call` `G<long>` | MethodSpec 3 | MethodSpec 3 under MemberRef 2 |
| `call` `G<int>` through the `MethodBuilder` `g` | MethodSpec 4 | MethodSpec 4 under MethodDef 1 |
| `ldftn` `G<int>` | MethodSpec 1 | none |
| `ldtoken` `created.GetMethod("G")`, the definition | MemberRef 2 | none: `useMethodDef` stops at the MemberRef |
| `ldtoken` `G<string>` | MethodSpec 5 | MethodSpec 5 under MemberRef 2 |
| `call` through a user-derived `MethodInfo` delegating to `N`, twice | MemberRef 4 | MemberRef 4 `N`, parent TypeDef 2, `00 01 08 08`, found the second time |
| `stsfld` and `ldsfld` through a user-derived `FieldInfo` delegating to `F` | MemberRef 5 | MemberRef 5 `F`, parent TypeDef 2, `06 08`, found the second time |
| `newobj` through a user-derived `ConstructorInfo` delegating to `K..ctor` | MemberRef 6 | MemberRef 6 `.ctor`, parent TypeDef 3, `20 00 01` |
| `module.GetMethodMetadataToken` of `G`, of `G<int>`, of `N` | `0x06000001`, `0x06000001`, `0x06000002` | none |

No TypeRef or AssemblyRef is added for `D` or `K` (the module's only other TypeRef is `RuntimeMethodHandle`, for a
return type). Every emitted method runs: the calls return what the bodies say, the `ldftn` pointer is non-null, the
`ldtoken`s give back `G` and `G<string>` through `MethodBase.GetMethodFromHandle`, and `ResolveMethod` of `G`'s
MemberRef gives the created type's `G`. So the JIT binds a MemberRef to its own module's TypeDef, for a method, a
generic method, a field and a constructor.

The neighbouring path, a generic method of the module's own generic type, in a module `Gen` with `C<T>` (TypeDef 2:
`M<U>(T, U) => u`, `Id(T) => t`) and `User` calling `M<long>` and `Id` on `C<int>` through `TypeBuilder.GetMethod`
before `C` is created, then `After` calling them through the created `C<int>`'s `RuntimeMethodInfo`s: `User` adds
TypeSpec 1 (`15 12 08 01 08`), MemberRefs 1 (`M`, `10 01 02 1E 00 13 00 1E 00`) and 2 (`Id`, `00 01 13 00 13 00`)
under it, and MethodSpec 1 under MemberRef 1; `After` adds nothing and emits the same operands, since
`GetGenericMethodBaseDefinition` and `ResolveMethod` give the definition whose `ParameterInfo`s write the builder's
signature. Both return what they should.

Under PawPrint, the same probe run with `WoofWare.PawPrint.App` on this branch:

* `Gen` exits 0 today: the TypeSpec parent is 5e's and 5n's, and nothing is refused.
* `Own` stops at the TypeDef-parent TODO. With that one arm changed to find or add a `MemberRefParent.TypeDef` (a
  throwaway edit, reverted), it exits 0, the user-derived shapes included, except for two things outside this stage:
  `ldtoken` of the definition's MemberRef stops at `MethodTokenBinding`'s TODO for a bare MemberRef to a generic
  method, which names the typical instantiation and is refused whatever the parent; and `GetILAsByteArray`, which the
  probe used only to read operands, reaches the unimplemented `RuntimeMethodHandle_GetMethodBody`.

### 5r: the options

Not yet decided. Each is a change to the one arm of `ModuleBuilder_GetMemberRefFromSignature`; nothing else the stage
touches is new code.

**A. Find or add the MemberRef under the TypeDef, as `DefineMemberRef` does.** The arm checks that the token names a
TypeDef row of the scope (a PawPrint fault otherwise, since 5j's `GetTypeRef` or `DefineType` handed it out) and
hands `MemberRefParent.TypeDef` to `EmitScopeReferences.defineMemberRef`, as the TypeRef and TypeSpec arms do. The
signature is stored as the managed caller wrote it, and the vararg refusal still applies after the parent.

* Correctness: every row and token measured above, for every caller of the QCall, since CoreCLR reads nothing of the
  parent. A user-derived member naming nothing gets its row, as on CoreCLR, and binding it fails later through the
  Loader's existing missing-member path.
* Blast radius if wrong: the module's MemberRef and MethodSpec rows; the Loader's binding of a TypeDef parent is
  already used.
* Reversibility: one arm.
* Information: the image is CoreCLR's, row for row.
* Size: a few lines, the guest, the refusal test moved.

**B. Answer the definition's token, adding no MemberRef**, as `GetMemberRefOfMethodInfo` does for the module's own
method: for a TypeDef parent, find the method (or field) of that name and signature among the TypeDef's and hand back
its MethodDef (FieldDef), so the MethodSpec's parent is the MethodDef.

* Correctness: wrong tokens. Real .NET keeps `G<int>` through the `RuntimeMethodInfo` (MethodSpec 1, under MemberRef
  2) apart from `G<int>` through the builder (MethodSpec 4, under MethodDef 1); B makes them one row and renumbers
  every later MethodSpec, `ldtoken` of the definition gives a MethodDef, and `ResolveMethod` of the MemberRef token
  names nothing. A user-derived member naming nothing has no answer, so it would have to be refused.
* Blast radius if wrong: guest-visible tokens that are plausible and wrong, which nothing detects.
* Reversibility: as A. Information: loses the row CoreCLR writes.

**C. A, admitting only the measured `RuntimeMethodInfo` shape**: a generic method signature (`0x10`) whose name and
bytes are a MethodDef's of that TypeDef, with every other TypeDef-parent reference (a non-generic method, a field, a
constructor, a member the type lacks, each from a user-derived member) refused by name.

* Correctness: exact where admitted; refuses shapes measured to run on .NET 10 (and, with A's arm, under PawPrint).
* Cost: a search of the scope's MethodDefs by owner, name and signature, a classifier whose truthfulness the refusal
  then depends on, for a distinction the QCall itself never draws.
* Blast radius and reversibility: small; dropping the gate is a deletion.
* Information: as A where admitted.

**Recommended: A.** It is what CoreCLR does, the measurement shows it runs for every shape that reaches the arm, and
everything downstream of the arm (the scope's row, the writer, the extension, the Loader's binding, the MethodSpec
under a MemberRef) already exists and is tested. B gives wrong tokens. C spends a classifier on refusing behaviour
already measured to be harmless, and the refusals would be of user-derived members, which nothing in CoreLib passes.

**Scope.** The neighbouring refusals do not share this mechanism, so each stays its own stage:

* 5j's nested type of the module needs `DefineNestedType` and a `NestedClass` table.
* `ModuleBuilder_GetMemberRef` with a TypeDef parent adds a TypeRef scoped by the module itself
  (`DefineTypeRefHelper`) before the MemberRef, and only a user-derived builder reaches it.
* 5n's TypeSpec in a signature translated from the module into itself needs the walk to translate a TypeSpec and
  find or add it.
* `GetMemberRefOfFieldInfo` with a TypeDef parent is done (5j: the FieldDef is handed back), and 5j's field under a
  TypeSpec parent is done (5n).
* A generic method of the module's own generic type, through `TypeBuilder.GetMethod` or through the created type's
  `MethodInfo`, already runs under PawPrint (measured above); it lacks only a guest.

The user-derived `MethodInfo`, `FieldInfo` and `ConstructorInfo` reach the same arm with the same arguments, so A
admits them whether or not the stage tests them; the oracle below does, since they were measured.

Open questions:

1. A, B or C.
2. Whether the guest for the generic method of the module's own generic type (`Gen` above) goes into 5r, as a second
   guest beside the one below, or into a test-only PR of its own. It needs no implementation change.
3. Whether the user-derived member shapes belong in 5r's guest (they reach the same arm, and were measured), or are
   left untested as "only a user-derived MemberInfo".
4. Where the stage's remaining refusal goes. `TestTypeBuilderGenericParamRefusals` loses its TypeDef-parent case,
   which then passes. The next stop on the same path, `ldtoken` of a created type's generic method definition, is a
   refusal of `MethodTokenBinding` that a TypeRef parent reaches too; it could replace that case, in a refusal
   fixture about tokens rather than generic parameters, or stay unpinned.

### 5r: the oracle

* `ModuleBuilderMemberRefOfOwnTypeDef.cs` (in `sourcesImpure`, registered in `TestImpureCases.fs`), the measurement
  above without the `ldtoken` of the definition, and without `GetILAsByteArray`, which exits 0 on .NET 10: `D`, `K` and
  `E` as above; checks `G`'s, `N`'s and `H`'s MethodDef tokens; `GetMethodMetadataToken` of `G`, `G<int>` and `N`
  (5j's MethodDefs); `ResolveMethod` of each MemberRef and MethodSpec token by number, against the measured
  numbering, to name `G`, `H`, `N`, `F`, `K..ctor`, and `G<int>`, `H<string>`, `G<long>`, `G<int>`, `G<string>`; and
  runs each of `E`'s methods, checking the results, and `GetMethodFromHandle` of the `ldtoken` of `G<string>`. Its
  terminal-state check reads `Own`'s TypeRef rows (`object`, `RuntimeMethodHandle`: none for `D` or `K`), MemberRef
  rows (each parent, name and signature as measured) and MethodSpec rows (each parent, and the instantiation bytes
  `SignatureHelper.GetMethodSpecSigHelper` writes).
* The terminal-state check also reads the operand of each `call`, `ldftn`, `ldtoken`, `stsfld`, `ldsfld` and
  `newobj` in `E`'s bodies from the scope, as the decisions below add.
* A parked guest, `ModuleBuilderLdtokenOwnGenericMethodDefinition.cs`, for the `ldtoken` of the definition, as the
  decisions below say.
* `TestTypeBuilderGenericParamRefusals` drops the TypeDef-parent case and its mention in the fixture's docstring.
* No new property: `EmitScopeGen` already draws TypeDef parents, so the round trip, `TestEmitScopeMemberRefs` and the
  extension properties cover the row.
* Mutants, each run against the guest and the emit-scope tests: the arm refusing again; the parent recorded as a
  TypeRef of the same RID; the find skipped (a new MemberRef, and so a new MethodSpec, for the second `G<int>`); B's
  shortcut to the MethodDef (MethodSpecs 1 and 4 merge); the scope the handler extended not kept; and the three the
  decisions below add. The handler's own check that the TypeDef names a row is expected to survive at the guest
  level, since no managed caller passes another and `EmitScope.addMemberRef` checks it again; the plan records that
  rather than inventing a guest for it.
* The "parent recorded as a TypeRef of the same RID" mutant is killed by a crash, not by an assertion: when `G<int>`
  is emitted the module has one TypeRef, so no TypeRef has `D`'s RID 2, and the mutant cannot make one (a RID's
  case is internal to the Domain, so the handler gets a `TypeRefRid` only from `EmitScope.tryTypeRefRid`). The
  terminal-state check of each MemberRef's parent token is the oracle that would remain if such a RID named a
  TypeRef row, since `ResolveMethod` binds a TypeDef and a TypeRef parent to the same member.

### 5r: left to later stages

* `ldtoken` of a generic method definition named by a MemberRef (and by a MethodDef), which PawPrint refuses
  whatever the parent, since it names the typical instantiation.
* A vararg method of a created type, called through its `MethodInfo`: a MemberRef under the TypeDef with a sentinel
  signature, which 5f's vararg refusal still refuses.
* `ModuleBuilder_GetMemberRef` with a TypeDef parent (a user-derived builder), a nested type of the module, and a
  TypeSpec in a self-translated signature, as listed above.
* `RuntimeMethodHandle_GetMethodBody`, which `MethodBody.GetILAsByteArray` reaches.
* The `Gen` guest, a generic method of the module's own generic type through `TypeBuilder.GetMethod` and through the
  created type's `MethodInfo`: a test-only PR of its own against `main`, as decided below.
* Binding a MemberRef whose TypeDef parent names a type not yet created, which a user-derived member of a
  `TypeBuilder` passes. CoreCLR adds the row, and A adds it too (the guest emits one, measured on .NET 10); binding it
  is expected to stop at 3b's refusal of a type that is defined but not created. Not measured under PawPrint.
* A TypeDef parent naming a generic type definition, from a user-derived `MethodInfo` of it: A adds the row, and
  binding it stops loudly at `MethodTokenBinding`'s refusal of a MemberRef whose parent names a generic type
  definition without an instantiation.

### 5r: the decisions

Taken 2026-11-13.

* **A: find or add the MemberRef under the TypeDef, as `DefineMemberRef` does.** Checked against the pinned
  source: `ModuleBuilder_GetMemberRefFromSignature` hands the token and bytes to `DefineMemberRef` unread;
  `RegMeta::DefineMemberRef` admits `mdtTypeDef`; `MDDupDefault` contains `MDDupMemberRef` and the dynamic
  module's dispenser sets it (vm/assembly.cpp); `FindMemberRef` compares the parent token exactly.
  * B writes tokens that are plausible and wrong (MethodSpecs 1 and 4 merge, and every later MethodSpec is
    renumbered), which is the failure 5o ruled out.
  * C needs a classifier for a distinction the QCall never draws, and its refusals would be of shapes measured
    to run. A classifier which CoreCLR does not have is one more thing that can be untruthful.
  * Conditions on A. The arm reads nothing but the token: not the signature, not whether the type is created
    (a user-derived member whose type is a `TypeBuilder` passes an uncreated type's TypeDef, CoreCLR adds
    the row, and 3b's refusal is what stops PawPrint binding it), and not whether the type is generic. It
    checks the RID against the scope as the TypeRef and TypeSpec arms do, and a miss is a PawPrint fault, not
    a TODO. The vararg refusal stays after the parent. The fall-through arm's TODO text and the comment above
    the match are rewritten, since "PawPrint adds only a member of another module or of a generic
    instantiation" becomes false.
* **The `Gen` guest (a generic method of the module's own generic type) is a test-only PR of its own**, not
  part of 5r. It needs no implementation change and does not depend on 5r's arm, so it can go against `main`
  at once; putting it in 5r would make 5r's diff claim coverage of a path which 5r does not touch. Its guest must
  not resolve the MemberRefs before `C` is created: the probe measured `TypeLoadException` there.
* **The user-derived method, field and constructor shapes go into 5r's guest.** A admits them whether
  tested or not, and admitted-but-untested is the worse state. They are also the only cases which put a
  non-generic method signature (`00`), a field signature (`06`) and a second TypeDef (`K`, RID 3) through the
  arm, so they are what kills "the arm admits only `0x10`" and "the parent is always `D`". They stay in the
  measured order in the one guest, so that the MemberRef numbers 4 to 6 are the probe's.
* **`ldtoken` of the created type's generic method definition is pinned by a parked guest, not by a
  replacement refusal test.** The refusal itself (`MethodTokenBinding`, decided on the method's generic
  parameters before the parent is consulted) is already asserted by `TestLdtokenMemberTokens`' sweep, so a
  second refusal test in a fixture about generic parameters would pin the same line under a misleading name.
  What is not recorded anywhere executable is that Reflection.Emit reaches it and that real .NET answers `G`.
  So: a small guest (`ldtoken created.GetMethod("G")`, then `GetMethodFromHandle` equal to `G`, exit 0 on
  .NET 10), parked in `TestImpureCases.fs` with the `MethodTokenBinding` TODO as its reason. It
  asserts the right behaviour for the day the typical instantiation lands, and it cannot be forgotten.
  `TestTypeBuilderGenericParamRefusals` drops its TypeDef-parent case and the docstring's mention, with no
  replacement there.
* **Additions to the oracle.**
  * The terminal-state check also reads the operand of each `call`, `ldftn`, `ldtoken`, `stsfld`, `ldsfld`
    and `newobj` in `E`'s bodies from the scope. Without `GetILAsByteArray` the guest cannot see them, and
    "the second `G<int>` emits MethodSpec 1 again" and "through the `MethodBuilder` emits MethodSpec 4" are
    claims about operands; `ResolveMethod` by number gives `G<int>` for both 1 and 4 and cannot tell
    them apart.
  * `ResolveMethod` cannot tell a TypeDef parent from a TypeRef parent either; the row check of each
    MemberRef's parent token is the only oracle for that, and the plan should say so.
  * Mutants added: the arm admitting only a generic-method signature; the arm checking that the type is
    created; the RID taken from the wrong bits (parent always TypeDef 2, which `K..ctor` kills).

### 5r: as built

Built 2026-11-13 as decided above.

* `ModuleBuilder_GetMemberRefFromSignature`'s TypeDef arm checks the token's RID against the scope
  (`EmitScope.tryTypeDefRid`), failing as a PawPrint fault on a miss, and hands `MemberRefParent.TypeDef` to
  `EmitScopeReferences.defineMemberRef`. It reads nothing else. The comment above the match and the fall-through's
  TODO, which a vararg call site of a `MethodBuilder` (a MethodDef parent) is now the only thing to reach, are
  rewritten. No public signature changed.
* `ModuleBuilderMemberRefOfOwnTypeDef.cs` is the measurement, in the measured order. Its check of MemberRef 5 by
  `ResolveField` is left out: `ModuleHandle_ResolveField` is not implemented, which is a QCall of its own. The
  terminal-state check reads that row, and `E`'s `stsfld` and `ldsfld` run through it.
* The guest also emits, last, a call through a user-derived method `M` of `U` (TypeDef 5), which is never created,
  into a body of `U` that never runs: MemberRef 7 under `U`'s TypeDef, `00 00 08`, as measured on .NET 10. The
  decisions did not list it; it is there so that an arm consulting whether the type is created is caught.
* `ModuleBuilderLdtokenOwnGenericMethodDefinition.cs` exits 0 on .NET 10 and is parked everywhere; run on purpose
  under PawPrint, it stops at the `MethodTokenBinding` TODO its reason quotes.
* Probed under PawPrint with the arm in place, each of which real .NET answers with an exception the guest catches
  from `Invoke`:
  * A user-derived member naming nothing (a method `Nope` of `D`, a field `Nope` of `D`): the MemberRefs are added,
    and binding them is a host failure, not a guest exception: "binds to no method, so CoreCLR throws
    MissingMethodException; that is not modelled", and for the field "PawPrint cannot vouch for every type
    CoreCLR's load of its parent reaches (D is not declared by CoreLib), so cannot tell which exception binding
    throws; TODO". Real .NET throws `MissingMethodException` and `MissingFieldException` at JIT time.
  * `Emit(OpCodes.Call, created.GetMethod("G"))`, the definition with `useMethodDef` false, from a non-generic
    method: the emitter writes a MethodSpec under `G`'s MemberRef whose instantiation is `G`'s own `MVAR 0`, which
    real .NET refuses at JIT time with `BadImageFormatException`. PawPrint emits it and then stops with a host
    failure ("CoreCLR refuses a signature with BadImageFormatException: it names the method parameter !!0, and its
    context instantiates 0"). Left out of the guest, since PawPrint does not raise it.
* Each mutant was run against the emit-scope tests, `TestTypeBuilderGenericParamRefusals`, `TestGuestParking` and
  every `ModuleBuilder` and `TypeBuilder` guest:

  | Mutant | Killed by |
  |---|---|
  | The arm refusing again | the guest |
  | The parent a TypeRef of the same RID | the guest, by the mutant's own failure: no TypeRef has RID 2 when `G<int>` is emitted |
  | The parent TypeRef 1 (`object`) | the guest, binding `G` under `object` ("binds to no method"), before the terminal-state check |
  | The find skipped for a TypeDef parent (in `defineMemberRef`) | the guest, and `TestEmitScopeMemberRefs`' model property and its refusal test |
  | B's shortcut to a MethodDef of the same name | the guest |
  | The extended scope not kept for a TypeDef parent | the guest |
  | The arm admitting only a generic method signature | the guest (`N`, `F`, `K..ctor`) |
  | The arm admitting only a created type | the guest (`U`), once the uncreated shape was added; before it, survived |
  | The RID always 2 | the guest (`K..ctor`, `U.M`) |
  | The arm's RID check dropped | not expressible: the handler can make a `TypeDefRid` only through `EmitScope.tryTypeDefRid` |

* Left to later stages, besides those above: `ModuleHandle_ResolveField`; a guest exception
  (`MissingMethodException`, `MissingFieldException`) for a MemberRef that binds to nothing; and the
  `BadImageFormatException` for a call to a generic method definition.

### 5s: a TypeBuilder's properties

**Which stage.** Surveyed on this branch (5r's, over `main` at cd3265704) by reading every "left to later stages" list
above against the code, and by running small guests of the shapes realistic Reflection.Emit programs use, on .NET 10.0.12
and under `WoofWare.PawPrint.App` (throwaway probe `5s-survey`). Every survey guest exits 0 on .NET 10. Nothing a list
above names has been done since it was written, except what a later stage records doing. Still outstanding:

| Item | Where PawPrint refuses it | Size |
|---|---|---|
| Properties: `TypeBuilder_DefineProperty`, `TypeBuilder_DefineMethodSemantics` | no handler: `NativeCall.failUnimplemented` (`Native/NativeCall.fs:1027`) | medium: a Property table, a derived PropertyMap and PropertyPtr, a sorted MethodSemantics table, two handlers |
| Parameters: `TypeBuilder_SetParamInfo` (`DefineParameter`) | no handler | small: the scope's Param table and the writer's ParamPtr exist already (stage 1) |
| Custom attributes: `TypeBuilder_DefineCustomAttribute` (and so a generic parameter's, 5l) | no handler | large: a sorted table over every HasCustomAttribute parent, and the pseudo-attributes the managed side turns into flags |
| Constants: `TypeBuilder_SetConstantValue` (`EnumBuilder.DefineLiteral`, `SetConstant` on a field, parameter or property) | no handler | medium: a Constant table sorted by parent, a value blob per element type |
| Events: `TypeBuilder_DefineEvent` | no handler; and reflection over any type's events stops at `MetadataImport.Enum` of an Event (`Native/NativeMetadataImport.fs:1159`), compiled assemblies included | medium, after event enumeration |
| Nested types (`DefineNestedType`) | `Native/NativeRuntimeTypeBuilder.fs:181`; `Native/NativeRuntimeModuleBuilder.fs:419` | medium: a sorted NestedClass table, and `GetTypeRef`'s lookup through it |
| A P/Invoke method: `TypeBuilder_SetPInvokeData` | `Native/NativeRuntimeTypeBuilder.fs:266` (parked `TypeBuilderDefinePInvokeMethod.cs`) | medium: ImplMap and ModuleRef rows, and `MethodInfo.read` of a P/Invoke row without its import |
| A bodiless method with non-IL implementation flags | `Native/NativeRuntimeTypeBuilder.fs:393` (parked `TypeBuilderNonIlImplementationFlags.cs`) | small to medium |
| Explicit layout: `TypeBuilder_SetClassLayout`, `TypeBuilder_SetFieldLayoutOffset` | no handler | medium: two sorted tables, and the type loader reading them for a dynamic type |
| `TypeBuilder_GetTokenFromSig` (`EmitCalli`, `GetSignatureMetadataToken`) | no handler | small: the StandAloneSig table exists |
| `ModuleBuilder_GetArrayMethodToken` (a multi-dimensional array's `Get`, `Set`, `Address`) | no handler | small to medium |
| `ModuleBuilder_SetFieldRVAContent` (`DefineInitializedData`) | no handler | medium: FieldRVA and a data section |
| `<Module>` and `CreateGlobalFunctions` (3b, 3c) | `Native/NativeRuntimeTypeBuilder.fs:874` | medium |
| A vararg call site's MemberRef (5e, 5f, 5r) | `Native/NativeRuntimeModuleBuilder.fs:350`, and `:325` for a MethodDef parent | small to medium |
| `ModuleBuilder_GetMemberRef` with a TypeDef parent (a user-derived builder, 5n, 5r) | `Native/NativeRuntimeModuleBuilder.fs:577` | small |
| A TypeSpec in a signature translated from the module into itself (5n) | `Native/NativeRuntimeModuleBuilder.fs:88` | small to medium |
| A translated TypeRef with a nil, `Module` or `ModuleRef` scope, an AssemblyRef naming the emitting assembly, or a rejected key (5h) | `Native/NativeRuntimeModuleBuilder.fs:51` to `:63` | small each, rare |
| Variance of a generic parameter (5l, 5o) | `Native/NativeRuntimeTypeBuilder.fs:902` to `:908` | medium: variance in casting and dispatch |
| A `struct` flag's constraints (5m) | parked `TypeBuilderGenericParamStructFlag.cs` | small |
| `ldtoken` of a generic method definition (5r) | `MethodTokenBinding.fs:144` (also `:163`, `:283`, `:374`) | medium; not specific to emit |
| A MemberRef that binds to nothing raising `MissingMethodException` or `MissingFieldException` (5r) | `WoofWare.PawPrint.TypeSystem/MemberReferenceInstantiation.fs:106` | small to medium; not specific to emit |
| `RuntimeMethodHandle_GetMethodBody`, `ModuleHandle_ResolveField` (5r) | no handler | small each; reflection |

Measured under PawPrint: a runtime DTO (a field, a property, a getter and a setter, read back by `GetProperty` and
`GetValue`) stops at `TypeBuilder_DefineProperty`; `DefineParameter` at `TypeBuilder_SetParamInfo`;
`SetCustomAttribute` at `TypeBuilder_DefineCustomAttribute`; `EnumBuilder` at `TypeBuilder_SetConstantValue`;
`DefineEvent` at `TypeBuilder_DefineEvent`; `DefineNestedType` at the NestedClass TODO; and a method with a local runs.
`DispatchProxy.Create` (CoreLib's own consumer of `TypeBuilder`, in `System.Reflection.DispatchProxy`) over an interface
with one method stops at `RuntimeTypeHandle_IsVisible`, which `DispatchProxyGenerator` asks of the interface, and then at
`MetadataImport.Enum` of the interface's events. With both answered by throwaway edits (reverted) it exits 0: the proxy
runs. Over an interface with a property, it then stops at `TypeBuilder_DefineProperty`. Its proxy builder calls
`DefineProperty`, `SetGetMethod`, `SetSetMethod`, `DefineEvent` and the event accessors, and no `DefineParameter`,
`SetCustomAttribute` or `SetConstant` (read from its IL with `WoofWare.PawPrint.IlDump`).

So properties are the next stage. A property is the first thing that stops both of the commonest shapes, a generated
data type and an interface proxy; `DispatchProxy` reaches them for any interface with a property; and nothing else is
needed first. The runners-up:

* `SetParamInfo` is smaller, since the Param table and its pointer table already exist, and generators that copy a
  method's parameter names call it routinely. But a program rarely reads the names back, and the generators that call
  it (Castle's, for instance) need properties and custom attributes too. It is a good next stage after this one.
* Custom attributes are as common, but are several features: a sorted table, every kind of parent, the pseudo-attributes,
  and the attribute's own constructor reference. They want their own options.
* Constants, which `EnumBuilder` needs: a narrower set of programs.
* Events: reflection over events is not implemented for any assembly, so they wait on that.
* `RuntimeTypeHandle_IsVisible` and event enumeration: small and worth doing for `DispatchProxy`, but they are reflection
  QCalls rather than emit, and each is its own PR outside this plan.

**What CoreCLR does.** Read from `RuntimeTypeBuilder.DefinePropertyCore` (RuntimeTypeBuilder.cs:1390),
`RuntimePropertyBuilder` (`SetMethodSemantics`, line 69), `SignatureHelper.GetPropertySigHelper`,
`TypeBuilder_DefineProperty` and `TypeBuilder_DefineMethodSemantics` (vm/comdynamic.cpp:543 and :610),
`RegMeta::DefineProperty` (md/compiler/emit.cpp:2662), `RegMeta::_SetPropertyProps` and `_DefineMethodSemantics`
(md/compiler/regmeta_emit.cpp:1633 and :975), `RegMeta::DefineMethodSemanticsHelper` (md/compiler/helper.cpp:158),
`CMiniMdRW::AddPropertyToPropertyMap` and `FindMethodSemanticsHelper` (md/enc/metamodelrw.cpp:4892 and :5000), and
`Associates.AssignAssociates`:

* `DefinePropertyCore` refuses once the type is created (`ThrowIfCreated`, before the QCall), builds the signature with
  `GetPropertySigHelper` and passes the attributes as the caller gave them. The signature's first byte is `0x08`, with
  `HASTHIS` only when the caller's calling convention has `HasThis`; `TypeBuilder.DefineProperty(name, attributes,
  returnType, parameterTypes)` passes `(CallingConventions)0`, so an instance property's signature usually says static.
  Types are named by the module's tokens (5j for its own types, `VAR` for a type parameter).
* `TypeBuilder_DefineProperty` calls `RegMeta::DefineProperty` with no default value, no getter, no setter and no other
  methods. It does not look for an existing row: `MDDupProperty` (0x100) is in neither `MDDupDefault` nor what
  vm/assembly.cpp:88 adds for a dynamic module. It finds the type's PropertyMap row, adding one at the type's first
  property, adds the Property row (so a property's token is in definition order across the module), and appends it to
  the map's run, starting a `PropertyPtr` when the map is not the last one (`AddChildRowDirectForParent`, else
  `AddChildRowIndirectForParent`), as FieldPtr and MethodPtr are started. No API shows a dynamic module's PropertyMap
  or PropertyPtr, so the map's order and when a PropertyPtr starts are read from this source and cannot be measured;
  their oracle is a model of it. `_SetPropertyProps` clears `prReservedMask` (0xF400: `RTSpecialName`, `HasDefault` and
  the unused bits) and keeps the low 16 bits, except that it leaves the flags unset when the attributes are
  `0xFFFFFFFF`: `PropertyAttributes` is an `int` enum, so `(PropertyAttributes)(-1)` writes flags 0. With nil accessors
  and `bClear` false (the call is the emitter's own, after `SetCallerDefine`) it adds no MethodSemantics row.
* `RuntimePropertyBuilder`'s constructor checks the name only after the QCall has returned, and the QCall reads the name
  to its first NUL, so `DefineProperty("\0x", ...)` writes a row with an empty name, consumes a token, and then throws
  `ArgumentException`.
* `SetGetMethod`, `SetSetMethod` and `AddOtherMethod` each check only that the property's own type is not created, ask
  the `MethodBuilder` for its `MetadataToken` (which gives it, and every earlier method of its type, a MethodDef row, as
  5o reads), and call `TypeBuilder_DefineMethodSemantics` with the property's token, the semantics and that token.
  `_DefineMethodSemantics` (with `bClear` false) appends a row every time: no search, and nothing checks that the method
  belongs to the property's type, or to the module.
* A read-write scope finds a property's rows by the table's sort if it is sorted, else by a hash, else by a scan in RID
  order (`FindMethodSemanticsHelper`). `_DefineMethodSemantics` calls `AddMethodSemanticsToHash` after every row, and
  `GenericAddToHash` builds the hash once the table holds 25 rows (`INDEX_ROW_COUNT_THRESHOLD`, md/enc/metamodelrw.cpp:33
  and :5514); its buckets chain each entry at the front, in RID order. So while the module has fewer than 25
  MethodSemantics rows an association's rows come back in the order added, and from 25 rows in reverse.
  `AssignAssociates` walks them and keeps the last getter and the last setter it meets, and the other methods in the
  order met: the last added below 25 rows, the first added from 25.
* ECMA-335: Property (II.22.34), PropertyMap (II.22.35, not required to be sorted) and MethodSemantics (II.22.28,
  required sorted by its `Association` column, a HasSemantics coded index: Event tag 0, Property tag 1).
* SRM, read from the .NET 10.0.12 `System.Reflection.Metadata.dll` with `WoofWare.PawPrint.IlDump`:
  `PropertyMapTableReader.FindPropertyMapRowIdFor` searches the PropertyMap linearly, so a map in first-property order
  is read; `FindTypeContainingProperty` binary-searches the `PropertyList` column, which ascends in that order; a
  `PropertyPtrTableReader` exists; and `MethodSemanticsTableReader`'s constructor calls `CheckSorted` and throws
  `TableNotSorted` unless the header declares the table sorted. PawPrint's associate enumeration scans the table's rows
  in order (`NativeMetadataImport.fs`, `methodSemanticsForAssociation`), so it reports an association's rows in their
  image order.

**Measured on .NET 10.0.12** with a throwaway probe (`5s-probe`), in a module `Props` with `A`, `B`, `D`, `E` (TypeDefs
2 to 5) and later `C<T>`, reading each Property row (name, flags, signature) through CoreLib's internal
`MetadataImport.GetPropertyProps`, and each `PropertyBuilder`'s token from its `m_tkProperty` (`PropertyBuilder` does not
override `MetadataToken`, which throws):

| Emitted | Observed |
|---|---|
| `A.DefineProperty("P1", None, int)`, `B.DefineProperty("Q1", SpecialName \| RTSpecialName \| HasDefault, string)`, `A.DefineProperty("P2", None, HasThis, long)` | `0x17000001`, `0x17000002`, `0x17000003`; created `A` lists `P1`, `P2` (and later ones), `B` lists `Q1` |
| the flags of `Q1` | the builder reports `0x1600`, the row and the created `PropertyInfo` `0x0200` |
| `P1`'s signature (default convention, instance getter) | `08 00 08` |
| `P2`'s (`HasThis`) | `28 00 0A` |
| `S` (`Standard`, static getter) | `08 00 08`; `GetValue(null)` runs |
| `Item` with parameters `int, string` | `08 02 08 08 0E`; `GetIndexParameters` has 2 |
| `Self` of type `A`, with a parameter of type `B` | `08 01 12 08 12 0C` |
| `C<T>.V` of type `T` | `08 00 13 00`; `C<int>`'s `V` is `Int32`, and reads the field |
| `Dup` twice, same signature | `0x17000004` and `0x17000005`, two rows; reflection lists only the first (`GetProperty("Dup", all)` is `0x17000004`) |
| a property with no accessors (`Dup`, `Self`) | not public to reflection: `GetProperty("Dup")` is null |
| `Q1.SetGetMethod(get_Q1)` of `B`, then `P1.SetSetMethod(set_P1)`, `P1.SetGetMethod(get_P1)` of `A` | `get_Q1` `0x06000001`, `get_P1` `0x06000002`, `set_P1` `0x06000003`; `P1`'s `SetValue(30)` then `GetValue` gives 30 |
| `P2`: `SetGetMethod(g1)`, `AddOtherMethod(o2)`, `SetGetMethod(g2)`, `AddOtherMethod(o1)`, `AddOtherMethod(o3)` | the getter is `g2`, and `GetAccessors(true)` is `g2, o2, o1, o3`: the rows in the order added, the last getter kept |
| `D.d0` and `D.get_X` defined, then `E.e0.MetadataToken`, then `D.X.SetGetMethod(get_X)` | `e0` `0x0600000B`, `d0` `0x0600000C`, `get_X` `0x0600000D`, `X` `0x17000009` |
| `DefineProperty` after `CreateType` | `InvalidOperationException`, before the QCall |
| a property of `FA` whose getter is a method of `FB`, both of another module `Foreign` | the row is written and both types are created; `FA.GetProperty` throws `ArgumentException` ("Type handle 'FA' and method handle with declaring type 'FB' are incompatible"). `RuntimeType.GetMethodBase` accepts a method of a type the reflected type derives from, so the exception is for unrelated types |

A second probe (`5s-probe2`):

| Emitted | Observed |
|---|---|
| an interface `IHas` with `Value` and an abstract getter, then `Impl : IHas` with its own `Value` (`DispatchProxy`'s shape) | `0x17000001` and `0x17000002`; the interface's `PropertyInfo.GetValue` on an `Impl` gives 42 |
| a property of module `X` whose getter is a `MethodBuilder` of module `Y` (`0x06000003`; `X` has one method) | accepted, and `X`'s type is created; reflecting over the property throws `ArgumentOutOfRangeException` ("Token 6000003 is not valid in the scope of module") |

Under PawPrint, the same probes stop at `TypeBuilder_DefineProperty`'s missing handler. The reading side exists: a
compiled class with an instance property, a static one, an indexer and a property of a generic class reflects, reads
and writes the same under PawPrint as on .NET 10 (`5s-survey static-props`). What a compiled assembly never shows
PawPrint is a PropertyPtr table, a property signature without `HASTHIS` on an instance property, and a MethodSemantics
table in an emitted image.

Measured on .NET 10.0.12 after the decisions below, which named these as read from the source only (`5s-reversal`). Each
row-count case is a fresh module, because `RuntimeType` and `RuntimePropertyInfo` cache, and each property is read once,
after its module's last row. `P` is given `g1`, `o1`, `g2`, `o2`, `o3`, and other properties one getter each, to make the
module's MethodSemantics row count:

| Emitted | Observed |
|---|---|
| 5 rows; 24 rows with `P`'s first; 24 rows with `P`'s last | getter `g2`, `GetAccessors(true)` `g2, o1, o2, o3` |
| 25 rows with `P`'s first; 25 rows with `P`'s last; 26 rows with `P`'s first | getter `g1`, `GetAccessors(true)` `g1, o3, o2, o1` |
| `DefineProperty("M1", (PropertyAttributes)(-1), ...)`; `(PropertyAttributes)0x7FFF0200` | `0x0000`; `0x0200` |
| `A`, then `"\0x"`, then `B` | the second throws `ArgumentException`; `B` is `0x17000003`, and the created type lists `A`, a property named `""` (`0x17000002`) and `B` |
| `Derived : Base` (`Base` created) with a property whose getter is `Base.get_FromBase` | the getter is `Base.get_FromBase`, and `GetValue` gives its value |
| module `X`'s `Wild` with module `Y`'s `y0` (`0x06000001`) as getter, `X`'s `get_Own` being `0x06000001` | `Wild`'s getter is `get_Own`, and gives 7 |
| `SetGetMethod` after the property's type is created | `InvalidOperationException` |

So the reversal holds as read, from exactly 25 rows, wherever the property's rows are.

### 5s: the options

Not yet decided. Every option adds the handlers for `TypeBuilder_DefineProperty` and `TypeBuilder_DefineMethodSemantics`
in `NativeRuntimeTypeBuilder`, each checking the module and the token it is given, storing the signature bytes as the
managed caller wrote them and the flags with `prReservedMask` cleared, and refusing an Event association by name (there
is no Event table, and `DefineEvent` is refused before one could exist). They differ in what the emit scope holds.

**A. A Property table and a MethodSemantics table, each row naming what owns it; the writer derives the rest.** As a
Field row names its type and a Param row its method, a `PropertyRow` holds its owner (`TypeDefRid`), flags, name and
signature, and `PropertyRid`s are given out in definition order. A `MethodSemanticsRow` holds the semantics, a
`MethodDefRid` and its association (a HasSemantics union whose only case for now is a `PropertyRid`), added once per
call in call order, with no search. The writer derives the PropertyMap (one row per owner, in the order of each owner's
first property, as CoreCLR adds them) and the PropertyPtr (exactly when a property's owner is not the owner most recently
given a map row, CoreCLR's rule), and sorts MethodSemantics stably by association, as it sorts InterfaceImpl and
MethodImpl (3e, 3f). `EmitScopeExtension` refuses a Property row of a created type and a MethodSemantics row whose
property is of a created type, and admits MethodSemantics rows moving, as InterfaceImpl's and MethodImpl's do.

* Correctness: every measured token, flag, signature and accessor order above. A MethodSemantics row has no token, so its
  position in the image is not visible; an association's rows keep their order.
* Blast radius if wrong: properties of dynamic types, and an image SRM refuses to open if the sort is wrong (which every
  guest with two properties set out of order would show).
* Reversibility: new tables and two handlers; nothing existing changes shape.
* Information: CoreCLR's rows, in its order. The PropertyMap's order, and whether there is a PropertyPtr, are not visible
  to a guest; they are kept as CoreCLR's because the top-level decision says pointer tables are emitted exactly when
  CoreCLR would.
* Size: the two row types and their adders in `EmitScopeDefinitions`, three tables in `EmitScopeImage`, the extension
  rules, the generator, the two handlers, the guest.

**B. The accessors as a column of the property.** `PropertyRow` carries its accessors, a list of (semantics,
`MethodDefRid`) that `DefineMethodSemantics` appends to, as `SetMethodIL` fills in a method's body; the scope holds no
MethodSemantics table, and the writer emits each property's accessors as its run of MethodSemantics rows, already in
association order.

* Correctness: as A for everything a guest can see.
* An association cannot name a missing property, by construction rather than by `addMethodSemantics`' RID check.
* But it is not CoreCLR's structure: `DefineMethodSemanticsHelper` takes any association, and the handler has to dispatch
  on its kind to a different row type. The extension check gains an append-only column, a third kind beside "added" and
  "filled in". When events come, `EventRow` needs the same column, and the writer merges the two into one sorted table.
* Information: loses the order of rows across associations, which no guest can see.
* Reversibility: as A, but turning it into A later rewrites the row type and the extension rule.

**C. Split in two: `DefineProperty` alone now (Property, PropertyMap, PropertyPtr), `DefineMethodSemantics` as the next
stage.**

* Each PR is smaller, and the PropertyPtr rule is reviewed on its own.
* But the first guest can check only tokens, names, flags and signatures, and that a property with no accessors is not
  public; no realistic program stops after `DefineProperty`. `DispatchProxy` and the DTO both call `SetGetMethod` straight
  after it, so C unblocks nothing until its second half lands.

**Recommended: A.** It is CoreCLR's model and the scope's existing pattern (rows naming their owner; tables the image
sorts sorted by the writer), every piece of the image it adds has an existing precedent to copy (FieldPtr for
PropertyPtr, MethodImpl for MethodSemantics), and the events stage then adds a second case to the association union
rather than a second accessor column. B's guarantee is the same one A gets from the RID check every adder already makes.
C splits a feature whose first half no program uses alone.

Open questions:

1. A, B or C.
2. A getter that is a `MethodBuilder` of another module, whose token names a different method of this module, or none.
   CoreCLR writes the row and reflection throws `ArgumentOutOfRangeException` later. Under A or B the RID is checked
   against the scope, so the handler must either refuse a token beyond the module's MethodDefs by name (a TODO naming
   CoreCLR's behaviour), or hold an unchecked MethodDef token in the row and model the later exception. The first is
   recommended: no generator does this, and modelling it would make the row's method column untyped.
3. An accessor that is a method of another type of the same module. The RID is in range, so A admits it as CoreCLR does,
   and real .NET's reflection then throws `ArgumentException` from `RuntimeType.GetMethodBase`. Whether PawPrint raises
   the same is not measured. Should the guest pin it (and so the stage make it pass), should it be a parked guest, or
   should it be left unpinned?
4. Whether `DispatchProxy` guests are wanted, and when: one over a method-only interface needs `RuntimeTypeHandle_IsVisible`
   and event enumeration first (two small PRs outside this plan, both measured above to be all it needs); one over an
   interface with a property needs those and this stage.
5. Whether the duplicate property (`Dup` twice) goes in the guest. CoreCLR writes two rows, so A does too; reflection hides
   the second. It is the only case that kills a mutant which searches for an existing row, other than the unit test.

### 5s: the oracle

Written first, and seen to fail on this branch before the handlers exist:

* `TypeBuilderDefineProperty.cs` (in `sourcesImpure`, registered in `TestImpureCases.fs`, `IsDynamicCodeSupported` on),
  the first probe without the internal `MetadataImport` reads, in the measured order, so that every token is the
  probe's: the interleaved `P1`, `Q1`, `P2`; `Q1`'s flags; the default, `HasThis` and `Standard` conventions; the indexer;
  `Self`; `Dup` twice (question 5); `P2`'s two getters and three other methods; `D`'s and `E`'s token order; `C<T>.V`;
  `DefineProperty` after `CreateType`. It checks each created property's `MetadataToken`, `Attributes`, `PropertyType` and
  `GetIndexParameters`, the getters and `GetAccessors(true)` order, the MethodDef tokens, and reads and writes each
  property with an accessor. It also emits a method that calls `P1`'s getter and setter directly, and the interface property
  of the second probe, read through the interface's `PropertyInfo`. Exits 0 on .NET 10, to be confirmed before
  committing.
* Its terminal-state check reads `Props`' scope: each Property row (owner, name, flags, signature bytes, as measured),
  each MethodSemantics row in the order added (semantics, method, property), and from the image that SRM reads back, the
  PropertyMap rows' owners in first-property order and the PropertyPtr's presence and contents. That is the only oracle for
  the map's order and the pointer table, which no guest can see.
* `TestEmitScopeDefinitions` (or a new `TestEmitScopeProperties`): `defineProperty` against a model over generated
  interleavings of properties across types: each gets the next RID, flags are stored with `prReservedMask` cleared, no
  search. `defineMethodSemantics` against a model: one row per call, in call order; a refused RID for a property or
  method the scope lacks.
* The generator draws properties across owners in any interleaving and MethodSemantics rows in any order, so the
  image's round trip covers the three tables, with properties beyond the new ones: for every type, SRM's
  `TypeDefinition.GetProperties` is the model's properties of that type in definition order; every property's row reads
  back; `methodSemanticsForAssociation`'s scan gives each property's rows in the order added; and the image has a
  PropertyPtr exactly when the model's CoreCLR rule says so. SRM opening the image at all checks the MethodSemantics sort.
* A differential property against real .NET, in-process: a generated schedule of `DefineProperty`, `SetGetMethod`,
  `SetSetMethod` and `AddOtherMethod` calls over a few types (distinct names, accessors of the property's own type), run
  on the host's `TypeBuilder` and created, and through the scope; for every created type, the host's
  `GetProperties(DeclaredOnly | all)` names and `MetadataToken`s, and each property's getter, setter and accessor order,
  equal what the image gives. This is what checks "definition order across the module" and "rows in the order added"
  beyond the guest's few cases.
* `TestEmitScopeExtension`: a Property row added to a created type, and a MethodSemantics row for a property of a
  created type, are each a violation; a MethodSemantics row that moves because a later row sorts before it is not.
* Mutants, each run against the guest, the emit-scope tests and the differential property:
  * the property's RID off by one, and the token's table byte wrong;
  * the reserved flags kept (`Q1`'s `0x1600`);
  * a search for an existing property added (`Dup`'s second token; question 5, otherwise only the unit test);
  * the PropertyPtr omitted, so that `A`'s run covers `Q1`;
  * the PropertyMap in TypeDef order rather than first-property order (expected to survive the guest, which cannot see
    it; killed by the terminal-state check and the round trip's CoreCLR-rule check);
  * MethodSemantics not sorted (SRM refuses the image when `Q1`'s row precedes `P1`'s);
  * the sort keyed on (association, method), not stable on association alone (`P2`'s accessors become `g2, o1, o2, o3`);
  * the getter and setter semantics swapped;
  * the association's HasSemantics tag written as an Event's (every getter is lost);
  * a `HASTHIS` bit added to, or dropped from, the stored signature;
  * the extended scope not kept by one handler;
  * the extension check not refusing a Property row of a created type (killed only by `TestEmitScopeExtension`, since
    the managed caller refuses first).

### 5s: left to later stages

* Events: `TypeBuilder_DefineEvent`, the Event, EventMap and EventPtr tables, and the Event case of the association,
  after reflection can enumerate events (`MetadataImport.Enum`, `NativeMetadataImport.fs:1159`).
* `PropertyBuilder.SetConstant`, through `TypeBuilder_SetConstantValue`, with the Constant table, which enums and literal
  fields want too.
* `PropertyBuilder.SetCustomAttribute`, through `TypeBuilder_DefineCustomAttribute`.
* `RuntimeTypeHandle_IsVisible` and event enumeration, which `DispatchProxy` reaches before any `TypeBuilder` QCall; then
  `DispatchProxy` guests (question 4).
* `TypeBuilder_SetParamInfo`, the next stage by the survey above.

### 5s: the decisions

Taken 2026-11-13.

* **A: Property and MethodSemantics rows naming their owners; the writer derives PropertyMap and PropertyPtr and
  sorts MethodSemantics.** Checked against the pinned source: `RegMeta::DefineProperty` searches only under
  `MDDupProperty`, which neither `MDDupDefault` nor vm/assembly.cpp:88 sets; it adds the map row at the type's first
  property and before the Property row; `_DefineMethodSemantics` with `bClear` false appends and reads nothing of the
  method but its RID.
  * B keeps everything a guest can see, but it is a second structure for one CoreCLR table. The read rule corrected
    below depends on the module's count of MethodSemantics rows, which under B is a sum over every property's (and
    later every event's) column. The events stage would have to merge two columns into one sorted table. Its
    guarantee, that an association names a property that exists, is the one which A's RID check already gives.
  * C's first half is used by no program, and its guest could check only tokens and signatures.
  * A split by layer (the Domain rows, the writer and their properties first; the handlers and the guest second) was
    also considered, and is the cut to make if the PR outgrows review. It is not the default: Domain is a published
    package, and the new adders would ship with nothing calling them and with no guest showing that PawPrint's own
    loader reads the image.
  * Conditions on A. The scope holds no PropertyMap or PropertyPtr rows; both are derived from the Property rows'
    owners, so a map that disagrees with its properties cannot be represented. The PropertyPtr is `pointerTable` over
    each property's map position (the rank of its owner's first property), not over its owner's TypeDef RID. The
    MethodSemantics sort is stable on the HasSemantics coded value (`rid <<< 1 ||| 1` for a property), not on the
    property's RID, so that the Event case sorts correctly when it arrives. The header's `Sorted` bit for
    MethodSemantics stays clear, so that SRM checks the order. `EmitScopeImage`'s docstring and
    `EmitScopeExtensionViolation`'s remarks are rewritten to name MethodSemantics among the tables whose rows move.
* **Correction: an association's rows are read in the order added only while the module has fewer than 25
  MethodSemantics rows.** Read, not measured. `_DefineMethodSemantics` calls `AddMethodSemanticsToHash` after every
  row; `GenericAddToHash` builds the lookup hash once the table holds 25 rows (`INDEX_ROW_COUNT_THRESHOLD`,
  md/enc/metamodelrw.cpp:33 and :5514). `AddRecord` clears the table's sorted flag, so `FindMethodSemanticsHelper`
  takes the hash arm from then on. `CMetaDataHashTemplate::Add` and `ReHash` (md/inc/metadatahash.h) both chain each
  entry at the front of its bucket, in heap order, which is RID order. So from the 25th row the walk gives an
  association's rows in descending RID order. `Associates.AssignAssociates` then keeps the first getter added, not
  the last, and lists the other methods in reverse. The planner's probes add about a dozen rows and never crossed
  the threshold. A generated data type with thirteen read-write properties has 26 rows, so realistic modules are in
  this regime, though only a property with two rows of one semantics can show it.
  * The first step of the stage is to measure it on .NET 10: three modules, each with a property given `g1`, `o1`,
    `g2`, `o2`, `o3`, and enough other accessors to make 5, 24 and 25 rows. Each property is read once, after its
    module's last row, because `RuntimePropertyInfo` caches its accessors.
  * If the measurement agrees, PawPrint models it where CoreCLR has it, on the read path:
    `methodSemanticsForAssociation` reverses the matching rows when the assembly is a dynamic module
    (`TryDynamicScope`) whose table has 25 or more rows. The rule is one pure function, in the lowest project that
    can hold it, called by the handler and by the differential property below. The image stays CoreCLR's rows in
    CoreCLR's order. Writing the read order into the image instead was rejected: a created type's rows would then
    change places within their association when a later row crossed the threshold, and the image would no longer be
    what CoreCLR holds.
  * This is the one place where reflection over a created type depends on rows added to the module afterwards. It is
    CoreCLR's behaviour, and PawPrint gets it by applying the rule to the image current at the read.
  * If the measurement disagrees with the reading, or shows more than "reversed from 25", stop and report before
    writing code. The fallback is a refusal by name in the enumeration (a dynamic module, 25 or more rows, and an
    association with two rows of one semantics), not an order that is guessed.
* **An accessor whose token names no MethodDef row of the module is refused by name.** The handler sees only a token
  and cannot know the `MethodBuilder`'s module, as CoreCLR cannot.
  * A token whose RID is in range gets its row, whatever module the builder came from; that is CoreCLR's row. The
    guest pins one: probe 2's shape with `y0` in place of `y2`, so that `X`'s `Wild` gets `X`'s MethodDef 1,
    `get_Own`, as its getter. Measure on .NET 10 first.
  * A RID beyond the module's MethodDefs at the time of the call, a nil token, and a token of another table are each
    a TODO failure which names the condition and CoreCLR's behaviour. CoreCLR stores the RID unchecked, writes no row
    for a nil token, and throws `ArgumentOutOfRangeException` from reflection, unless a method defined later brings
    the RID into range. A user-derived `MethodBuilder`, or another module's, is the only thing which reaches these.
  * Storing an unchecked RID was rejected: the row's method column would stop being a `MethodDefRid`, the writer
    would emit an index naming no row, and what reflection then does depends on methods defined afterwards.
  * The refusal is pinned by a refusal test in a fixture about properties, with probe 2's `y2` shape.
* **An accessor of another type of the same module is admitted, and what PawPrint then does is measured, in a guest
  of its own.** A admits it whether tested or not, and admitted but unmeasured is the worse state.
  * `TypeBuilderPropertyForeignAccessor.cs`: `FA`'s property with `FB`'s method as getter, `FB` created before
    `SetGetMethod` is called (the property's type is the only one the managed side checks), then `FA.GetProperty`
    expecting `ArgumentException`.
  * Run under PawPrint once the handlers exist. If it exits 0, it is registered as passing. If it stops at an
    unimplemented primitive, it is parked with that stop as its reason. If PawPrint gives the guest any other answer
    (no exception, or a different one), stop and report: that is a divergence which this stage would be introducing.
  * Correction to the plan: the exception is for unrelated types. `RuntimeType.GetMethodBase` accepts a method whose
    declaring type the reflected type derives from, so a base type's method as accessor should resolve. Read, not
    measured; the variant joins this guest once measured.
* **No `DispatchProxy` guest in 5s, and 5s does not wait for one.** `RuntimeTypeHandle_IsVisible` and event
  enumeration are reflection PRs against `main`, outside this plan and in either order.
  * The two guests (a method-only interface; an interface with a property) go into a test-only PR against `main`,
    parked with their present first stop as the reason, so that they cannot be forgotten and 5s's diff does not claim
    them.
  * After 5s is built, the property-interface proxy is run once with the two throwaway edits, and its next stop is
    recorded in "as built". The plan measures each next stop and does not predict it; "needs those and this stage"
    is so far a prediction.
* **The duplicate property stays in the guest.** It is measured, it is part of the measured token sequence
  (`0x17000004` and `0x17000005`), and it is the only guest-level killer of a search for an existing row.
* **Corrections to "what CoreCLR does".**
  * `_SetPropertyProps` leaves the flags unset when the attributes are `0xFFFFFFFF`. `PropertyAttributes` is an
    `int` enum, so `DefineProperty(name, (PropertyAttributes)(-1), ...)` writes flags 0, not `0x0BFF`. Read, not
    measured; the handler models it, and the unit model and the differential property draw it.
  * `RuntimePropertyBuilder`'s constructor checks the name after the QCall has returned. `DefineProperty("\0x", ...)`
    therefore writes a row whose name is empty (the QCall reads to the first NUL), consumes a token, and then throws
    `ArgumentException`. The handler must not refuse an empty name. Measure, then pin at the end of the guest, on a
    type of its own so that no measured token moves.
  * "PropertyMap in first-property order" and "a PropertyPtr exactly when" are read from `RegMeta::DefineProperty`
    and `AddChildRowDirectForParent`, and cannot be measured: no API shows a dynamic module's PropertyMap. The oracle
    for them is a model of the source, and the plan should say so.
* **Conditions on the handlers.** `TypeBuilder_DefineProperty` takes a TypeDef token of the scope and nothing else: a
  nil parent is a PawPrint fault, not `<Module>`, so `methodParent` is not reused. It stores the signature bytes
  unread. The flag rule (the `-1` case, `prReservedMask` cleared, the low sixteen bits kept) lives in
  `EmitScopeDefinitions.defineProperty`, so that the Domain tests and the differential property cover it.
  `TypeBuilder_DefineMethodSemantics` reads the association's table and RID, the method's RID, and the semantics as
  two bytes. A Property token out of range is a PawPrint fault. An Event token is a fault too, since `DefineEvent` is
  refused before an `EventBuilder` can exist. Neither handler consults whether a type is created; the extension check
  does that.
* **The extension rule keys on the property's owner alone.** A Property row added to a created type, and a
  MethodSemantics row whose property is of a created type, are violations. A MethodSemantics row naming a method of a
  created type, for a property of a type not yet created, is not: CoreCLR checks only the property's type, and
  nothing reads MethodSemantics by method.
* **The oracle is sufficient with these additions.**
  * The differential property is the right second oracle, and it is the one which would have found the 25-row rule.
    It must be built so that it can. Its schedules reach both sides of 25 rows, with repeated getters and setters and
    several other methods on one property. It reads the image through the shared enumeration rule and not through a
    model of its own. It draws flags from the whole `int` range. It forces every `MethodBuilder`'s `MetadataToken`,
    in order, before the first property call, and adds the scope's methods in that order, so that MethodDef RIDs
    agree without modelling 5o's lazy tokens. It uses abstract methods of abstract types, so that no body is needed,
    and `RunAndCollect` assemblies. It covers the Domain and the read rule, not the handlers' reading of their
    arguments, which only the guests cover; the plan says so.
  * The round trip's "CoreCLR rule" for the PropertyPtr is a replay of `AddChildRowDirectForParent` one property at a
    time, not `pointerTable` again. Otherwise the mutant "map in TypeDef order" is checked against its own
    arithmetic.
  * That mutant surviving the guest is accepted: the map's order is invisible to a guest. It must be killed twice,
    by the terminal-state check and by the replay.
  * Guest additions, each measured first: a second module which crosses 25 rows, with a property given two getters
    and three other methods; `SetGetMethod` after `CreateType` (`InvalidOperationException`); the in-range accessor
    from another module; the empty name.
  * `TestEmitScopeExtension` gains the case of the created method's owner, which is not a violation.
  * Mutants added: the reversal's threshold at 24 or at 26; the reversal never applied; the reversal applied to an
    assembly which is not dynamic; the `-1` flags masked rather than zeroed; the extension rule also keyed on the
    method's owner; the PropertyPtr keyed on the owner's TypeDef RID; the sort keyed on the property's RID with the
    tag dropped; an empty name refused; `DefineProperty` treating a nil parent as `<Module>`.
  * "The reversal applied to an assembly which is not dynamic" needs an on-disk image with two rows of one semantics
    on one property and 25 rows in all, which no compiler writes. Fabricate one if the existing fixtures for
    fabricated images can reach `MetadataImport.Enum`; otherwise record it as surviving, with the reason.
  * "The sort keyed on the property's RID with the tag dropped" is expected to survive until the Event case exists.
    Record it for the events stage.
* **One feature, one PR.** The two QCalls, the three tables and the read rule are one feature: a property that can be
  read back. The commits go in the order tests, Domain, handlers, with the failing run recorded.
* **Additions to "left to later stages".**
  * 3e's MethodImpl rows go through the same hash (`AddMethodImplToHash`, `FindMethodImplHelper`), so in a dynamic
    module with 25 or more MethodImpl rows CoreCLR enumerates a class's rows in reverse. PawPrint keeps them in the
    order added. Whether any behaviour depends on that order is not known; it wants its own measurement.
  * Event rows will count towards the 25, and CustomAttribute has the same hash.
  * Modelling an accessor token that names no row: `ArgumentOutOfRangeException` from reflection, and the nil token's
    silent no-op.
  * A base type's method as an accessor, until measured.
  * The plan's item "whichever of questions 2 and 3 is not taken up" is replaced by the two items above and by
    whatever `TypeBuilderPropertyForeignAccessor.cs` is parked on.

### 5s: as built

Built 2026-11-13 as decided above, after the measurements the decisions asked for (recorded under "5s: a
TypeBuilder's properties"), which agree with the reading: the reversal is from exactly 25 rows, wherever the property's
rows are.

* `EmitScope` holds `PropertyRow`s (owner, flags, name, signature; `PropertyRid` in definition order) and
  `MethodSemanticsRow`s (semantics, `MethodDefRid`, and a `HasSemantics` whose one case is `Property`).
  `EmitScopeDefinitions.defineProperty` stores the flags as `_SetPropertyProps` does and searches for nothing;
  `defineMethodSemantics` adds a row per call. `EmitScopeImage` derives the PropertyMap (first-property order) and the
  PropertyPtr (`pointerTable` over each property's map position), and sorts MethodSemantics stably on the coded
  index, its `Sorted` bit clear. `EmitScopeExtension` refuses a Property row of a created type and a MethodSemantics
  row whose property is of one, keyed on the property's owner alone.
* The read rule is `ReadWriteMetadataLookup.rowsOfKey`, in Domain: it takes the table's row count and one key's rows in
  RID order, and is named for what it models rather than for properties, since CoreCLR's read-write metadata applies
  the same hash to MethodImpl and CustomAttribute (the user asked for that shape, for the MethodImpl stage that
  follows). Only `methodSemanticsForAssociation` calls it in 5s, for an assembly that `TryDynamicScope` knows; its
  docstring now says which reader CoreCLR has for each kind of image.
* `TypeBuilder_DefineProperty` and `TypeBuilder_DefineMethodSemantics` are as decided. The refusals of an accessor
  token (a RID beyond the module's MethodDefs, a nil token, another table) are TODO failures naming CoreCLR's
  behaviour; the refusal test (`TestTypeBuilderPropertyRefusals`) pins the RID beyond, with probe 2's `y2`.
* `TypeBuilderDefineProperty.cs` is the first probe in the measured order, with the decisions' additions: `SetGetMethod`
  after `CreateType`; `X`'s `Wild` with `y0`; the empty name, last in `Props`; and modules either side of the
  threshold. Beyond the decisions, each added after the first mutant run let a mutant through: it reads each
  `PropertyBuilder`'s token from its private `m_tkProperty` (the probe's way, since `MetadataToken` throws); it gives
  `Other` (TypeDef 9) a property, with flags of `-1`, before `Named` (TypeDef 8) gets its own, so that the PropertyMap's
  first-property order and the PropertyPtr differ from TypeDef order; and it reads a 24-row module, `Fewer`, beside the
  25-row `Many`. It also has a method of `Caller` calling `P1`'s accessors. The guest happens not to invoke any method through
  reflection twice, so whether the plan's caution about a second invocation is stale (#2180) was not checked here.
* `TypeBuilderPropertyForeignAccessor.cs` exits 0 under PawPrint as on .NET 10: `FA.GetProperty` throws
  `ArgumentException` from CoreLib's own `RuntimeType.GetMethodBase`, and `Derived`'s property with `Base`'s getter
  (measured: it resolves, and gives 9) reads back. It is registered as passing.
* `TestEmitScopeProperties` holds the Domain models, the read rule's property, and the differential property against
  real .NET's `TypeBuilder` in the test host (`RunAndCollect`, abstract types, every method's token forced first).
  Its model of reflection includes `PopulateProperties`' vtable-slot rule, which lists a property only if no earlier
  property's public getter (or else setter) shares its slot: generated properties often share an accessor. Its fixed
  sample reaches 25 rows with a repeated semantics 56 times, below 25 rows 67 times, and a PropertyPtr 66 times.
* `TestFabricatedPropertyAccessorOrder` is the decisions' on-disk image: `PersistedAssemblyBuilder` writes a property
  with three other methods among 25 MethodSemantics rows, and both runtimes list them in RID order.
* `TestEmitScopeImage`'s round trip reads each Property row, each type's properties, each property's accessors, and
  the PropertyMap and PropertyPtr against `EmitScopeGen.propertyMapReplay`, a replay of `AddChildRowDirectForParent`
  one property at a time. `TestEmitScopeExtension`'s generated property compares each created type's properties and
  their accessors too.
* `DispatchProxy`, run once with the two throwaway edits (`RuntimeTypeHandle_IsVisible` answering true, `MetadataImport.Enum`
  of events answering none), reverted: over an interface with a property it exits 0 (`Value=42`), as it does over one
  with a method; the DTO of the survey exits 0 without them.
* Each mutant was run against the emit-scope tests, `TestTypeBuilderPropertyRefusals`,
  `TestFabricatedPropertyAccessorOrder` and both guests:

  | Mutant | Killed by |
  |---|---|
  | The property's token one RID too high (handler) | the guest; the refusal test, whose accessor call then names no property |
  | The token's table byte `0x18`, emitted and accepted back alike | the guest's read of `m_tkProperty`, once added; before it, survived |
  | The reserved flags kept | the guest (`Q1`); the Domain model; the differential property |
  | A search for an existing property | the guest (`Dup`); the Domain model |
  | No PropertyPtr | the guest; the round trip; the differential property; the generated extension property |
  | The PropertyMap in TypeDef order | the terminal-state check, once `Other` and `Named` were added (before, survived, the order being the same); the round trip's replay |
  | MethodSemantics not sorted | the guest (SRM: "Metadata table MethodSemantics not sorted"); the round trip and every image-reading property |
  | The sort keyed on (association, method) | the guest; the round trip; the differential property |
  | Getter and setter swapped | the guest |
  | The association written with an Event's tag | the guest; the round trip; the differential property |
  | `HASTHIS` added to the stored signature | the terminal-state check |
  | `DefineMethodSemantics`' extended scope not kept | the guest |
  | The extension check not refusing a property of a created type | `TestEmitScopeExtension` alone, since the managed caller refuses first |
  | The threshold at 24 | the guest's `Fewer`, once added; the read rule's property; the differential property |
  | The threshold at 26 | the guest's `Many`; the read rule's property; the differential property |
  | The reversal never applied | the guest's `Many` |
  | The reversal applied to an image on disk too | `TestFabricatedPropertyAccessorOrder` |
  | `-1` masked rather than zeroed | the guest's `Other.O`, once added; the Domain model; the differential property |
  | The extension rule also keyed on the accessor's owner | `TypeBuilderPropertyForeignAccessor.cs` (`FB` is created before its method becomes `FA`'s accessor); `TestEmitScopeExtension` |
  | The PropertyPtr keyed on the owner's TypeDef RID | the guest, once `Other` and `Named` were added; the round trip; the differential property |
  | The sort on the property's RID, the tag dropped | survives, as the decisions expected: nothing tells it from the coded index until the Event case exists. The events stage must kill it |
  | An empty name refused | the guest |
  | `DefineProperty` reading a nil parent as `<Module>` | survives: no managed caller passes a nil parent, and the handler's refusal of one is a PawPrint fault, not a guest's behaviour |

* Suites at the tip, built from the whole solution: the default suite (5564 passed, 38 skipped), `Guest` (235),
  `GuestSolo` (2), `GuestPure` (1179) and `GuestImpure` (274), each run on its own, all passing.
* `GenericParam` and `GenericParamConstraint` are not in the 25-row hash, although `FindGenericParamHelper` and
  `FindGenericParamConstraintHelper` (md/enc/metamodelrw.cpp:5196-5321) have hash arms: no `Add*ToHash` exists for
  either table (md/inc/metamodelrw.h), so those arms never run, and the VM's enumeration (md/enc/mdinternalrw.cpp:1091-1133)
  uses neither helper but `SearchTableForMultipleRows` for parameters and a stable VirtualSort for constraints. Measured
  after 30 rows of other types, on .NET 10 and under PawPrint alike: parameters and constraints come back in RID order.
  Nothing is left to do for them.
