# Emitting types with `TypeBuilder`

Status: option A chosen (2026-11-04); stage 0 implemented (#2093); stage 1 implemented (#2101); stage 2 implemented (#2109); stage 3a implemented (#2115); stage 3b implemented (#2124); stage 3c implemented (#2135); stage 3d implemented (#2141); stage 3e implemented (#2144); sorted-table rows of a created type admitted to move (3f, #2154); stage 4a implemented (#2159); stage 4b implemented (#2160); stage 4c implemented (#2162), and with it the issue's guest passes; stage 4d implemented (#2163); stage 5a implemented (#2164); stage 5b implemented (#2165); stage 5c implemented (#2166); stage 5d implemented (#2167); stage 5e implemented (#2176); stage 5f implemented (#2191); stage 5g implemented (#2192); stage 5h implemented (#2195); stage 5i implemented (#2197); stage 5j implemented (#2213); stage 5k implemented (#2220); stage 5l implemented (#2227); stage 5m implemented (#2228); stage 5n implemented (#2229); stage 5o implemented (#2231); stage 5p implemented (#2234); stage 5q implemented (#2235); stage 5r implemented (#2236); stage 5s implemented (#2239); stage 5t implemented (#2242); stage 5u implemented (#2244); stage 5v implemented (#2246); the `newobj` stage in progress. Tracking issue: #2091. Follows
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

### 5t: the order of a dynamic type's MethodImpl rows

5s found that CoreCLR's read-write metadata walks a lookup hash once a table holds 25 rows, and so finds one key's
rows in descending RID order, and recorded that 3e's MethodImpl rows go through the same hash. This stage asks what a
guest can observe of that, and makes PawPrint agree.

**What CoreCLR does.** Read from the pinned source (`$DOTNET_RUNTIME_SRC_NET10`):

* `RegMeta::DefineMethodImpl` calls `AddMethodImplToHash` after every row (md/compiler/emit.cpp:178), which is
  `GenericAddToHash(TBL_MethodImpl, COL_Class, rid)` (md/inc/metamodelrw.h:734). As for MethodSemantics, the hash is
  built when the table reaches 25 rows (`GenericBuildHashTable`, md/enc/metamodelrw.cpp:5514), and each bucket
  chains new entries at the front.
* `MDInternalRW::EnumMethodImplInit` (md/enc/mdinternalrw.cpp:809-854) takes a class's rows from
  `CMiniMdRW::FindMethodImplHelper` (md/enc/metamodelrw.cpp:5133-5187) and returns their (body, declaration) pairs in
  that order. The helper has three arms: a sorted table's range, the hash, and a linear scan in RID order. `AddRecord`
  clears the sorted flag (md/enc/metamodelrw.cpp:3883), so a dynamic module never takes the first arm: it scans below
  25 rows and walks the hash from 25. Correction to 3f, which says a class's rows are read "through a `VirtualSort`":
  `FindMethodImplHelper` uses no virtual sort. Below 25 rows the order 3f describes (the class's rows in the order
  added) is still the order observed.
* The enumeration has three consumers in the VM on the platforms PawPrint emulates:
  * `MethodTableBuilder::EnumerateMethodImpls` (vm/methodtablebuilder.cpp:2220-2305) copies every pair into an array,
    `qsort`s it by body token then declaration token (`MethodImplTokenPair::Compare`, :2117-2129; the sort at :2282)
    and removes exact duplicates. Everything the builder does afterwards (the vtable, the dispatch map, the
    `IDS_CLASSLOAD_MI_MULTIPLEOVERRIDES` check in `AddMethodImplDispatchMapping`, :6342) reads the sorted array, so
    it is independent of the enumeration order. A static virtual override is validated (`ValidateStaticMethodImpl`,
    :5986) and given no slot.
  * `MethodTable::TryResolveVirtualStaticMethodOnThisType` (vm/methodtable.cpp:8335-8531) enumerates the class's
    rows afresh at every call, and walks them in enumeration order. Outside verification it returns the body of the
    first row whose declaration is the method sought (:8518); under verification a second such row is a
    `TypeLoadException` (:8524), whether or not its body differs. It is reached from
    `ResolveVirtualStaticMethod` (:8216 and :8260), so from a `constrained.` call of a static virtual (the JIT, and
    a generic dictionary's slot, vm/genericdict.cpp:1157), and from `GetInterfaceMap` (vm/runtimehandles.cpp:862);
    and from `TryGetCandidateImplementation` (:5734-5750), the default-body search, on an interface's own rows, with
    variance allowed in its second pass.
  * `VerifyThatAllVirtualStaticMethodsAreImplemented` (vm/methodtable.cpp:8535-8567, called at :4868 for a
    non-abstract typical type, and from the constraint check, vm/typedesc.cpp:1705) resolves each *abstract* static
    virtual under verification. So two rows naming an abstract static virtual are a `TypeLoadException` at load,
    whatever the order; two rows naming one with a default body are not checked at all.
* `classcompat.cpp:2053` enumerates them too, but is built only with COM interop (vm/CMakeLists.txt:598-601).
  `RegMeta::EnumMethodImpls` (md/compiler/import.cpp:702) is `IMetaDataImport`, for debuggers and profilers; no
  managed API reads a MethodImpl row.

So the order is visible only through static virtual resolution, and there only when more than one row of one type
matches the method looked for: two rows naming a static virtual that has a default body, or, on an interface, two rows
whose declarations are different instantiations that are both variance-compatible with the call's.

**Measured on .NET 10.0.14** (the devshell's runtime since #2233; 5s measured 10.0.12) with a throwaway probe
(`5t-probe`), a fresh dynamic module for each case. `IDef` has `static virtual int M() => 0`, `IAbs` has `static
abstract int M()`, `IVar<in T>` has `static virtual int M() => 0`, and `Caller.CallDef<T>() where T : IDef => T.M()`
(and `CallAbs`, `CallVar<T> where T : IVar<string>`), invoked by `MakeGenericMethod` over the created type. `C` gets
static bodies `B1` (returns 1) and `B2` (returns 2); "rows" is the module's MethodImpl row count, the rest made up by
other types each overriding an instance interface method once; `C` is created after the last row unless stated.

| Emitted | .NET 10.0.14 | PawPrint today |
|---|---|---|
| `C : IDef`, rows `B1`, `B2` for `IDef.M`; 2, 24 rows (`C`'s first); 24 rows (`C`'s last) | 1 | 1 |
| the same, 25 and 26 rows (`C`'s first); 25 rows (`C`'s last) | 2 | 1 |
| rows `B2`, `B1`; 2 rows / 25 rows | 2 / 1 | 2 / 2 |
| interface `I2 : IDef` of the module with rows `D1`, `D2` for `IDef.M`, `C : I2`; 2 rows / 25 rows | 1 / 2 | 1 / 1 |
| `I2 : IVar<object>, IVar<IComparable>`, rows `D1` (for `IVar<object>.M`) then `D2` (for `IVar<IComparable>.M`), `C : I2`, `CallVar<C>`; 2 rows / 25 rows | 1 / 2 | 1 / 1 |
| the same, rows added `D2` then `D1`; 2 rows / 25 rows | 2 / 1 | 2 / 2 |
| `C` created at 2 rows, then 23 more rows, then `CallDef<C>` and `CallDefAgain<C>` (a second generic method of the same shape) | 2, 2 | 1, 1 |
| `C` created at 2 rows, `CallDef<C>` called, then 23 more rows, then `CallDef<C>` and `CallDefAgain<C>` | 1; then 1, 2 | 1; then 1, 1 |
| `C : IAbs` (non-abstract), rows `B1`, `B2` for `IAbs.M`; 2 rows / 25 rows | `TypeLoadException` at `CreateType` | `CreateType` succeeds; `CallAbs<C>` gives 1 |
| `C : IAbs`, `B1` twice; 2 rows / 25 rows | `TypeLoadException` at `CreateType` | `CreateType` succeeds; `CallAbs<C>` gives 1 |
| abstract `C : IAbs`, rows `B1`, `B2`, `CallAbs<C>`; 2 rows / 25 rows | `TypeLoadException` from the call | refuses by name ("is abstract and can be cast to ... which PawPrint does not decide") |
| `C : IInst` (instance), rows `B1`, `B2` for `IInst.N`, either order; 2 rows / 25 rows | `TypeLoadException` at `CreateType` naming `B2` (the larger body token) | refuses by name at the `callvirt` ("CoreCLR rejects this type at load time (IDS_CLASSLOAD_MI_MULTIPLEOVERRIDES)") |
| `C : IInst`, `B1` twice; 2 rows / 25 rows | `N()` gives 1 | 1 |

A second probe (`5t-probe2`):

| Emitted | .NET 10.0.14 | PawPrint today |
|---|---|---|
| on disk (C#): `IDisk : IVar<object>, IVar<IComparable>` with explicit static implementations in that order, `CDisk : IDisk`, and its swapped twin, in an assembly with 34 MethodImpl rows; `CallVar<CDisk>`, `CallVar<CDiskSwapped>` | 1, 2 (RID order: an image on disk is read by range, not by the hash) | 1, 2 |
| `C` with one row for `IDef.M` (`B1`) and one for `IDef2.M2` (`B2`), created at 2 rows, then 23 more rows, then both called | 1, 2 | 1, 2 |

Every row of both tables is the same on .NET 11.0.0-rc.3 (rolled forward from the net10.0 build).

So:

1. A guest can observe the order. From 25 MethodImpl rows in the module, a static virtual resolution that meets two
   matching rows on one type takes the one added last; below 25, the one added first. PawPrint always takes the one
   added first, so it diverges from 25 rows. The variant case shows this needs no duplicate declaration: two
   legitimate rows of a generated interface are enough.
2. The answer depends on when the resolution happens, not on when the type was created. A type created below 25 rows
   and resolved only after the module crosses 25 gets the reversed answer. But CoreCLR caches a resolution where it
   makes one (a generic dictionary slot, or the JIT's direct call in exact code), so after the crossing a call site
   that resolved before it keeps the old answer, while a site that resolves now gets the new one (`CallDef<C>` 1,
   `CallDefAgain<C>` 2). The JIT also resolves a `constrained.` call in exact code when it compiles the method, before
   the call executes. Which answer such a guest sees therefore depends on JIT tiering, inlining and code sharing.
   A type created at 25 rows or more has one answer always; a type resolved while the module is below 25 rows has one
   answer for that resolution only.
3. Duplicates of an abstract static virtual are not an order question: CoreCLR refuses the type at `CreateType` (even
   the same body twice, since verification counts rows, not bodies), and PawPrint creates it and runs the first row.
   That divergence is separate from this stage (see "left to later stages").
4. The instance-method cases and the class loader are independent of the order, on both runtimes. PawPrint refuses the
   instance duplicate by name at the first call, but CoreCLR has already refused the type at `CreateType`, so a guest
   that catches that exception diverges before the refusal. Not an order question either; it belongs with
   observation 3.

**GenericParam and GenericParamConstraint** do not go through the hash (5s "as built" is corrected to say so). No
`Add*ToHash` exists for either table (the complete list is md/inc/metamodelrw.h:631-734), and neither is passed to
`GenericFindWithHash`, so `m_pLookUpHashes` never holds a hash for them and the hash arms of `FindGenericParamHelper`
and `FindGenericParamConstraintHelper` (md/enc/metamodelrw.cpp:5196-5321) cannot run. The VM does not call those
helpers anyway: `MDInternalRW::EnumInit` (md/enc/mdinternalrw.cpp:1091-1133) finds an owner's parameters with
`getGenericParamsForTypeDef` / `ForMethodDef` (`SearchTableForMultipleRows`, md/inc/metamodel.h:1693, a binary search
with a linear fallback for an unsorted GenericParam, md/enc/metamodelrw.cpp:5824) and a parameter's constraints through
a `VirtualSort`, which orders by owner and then RID (md/enc/metamodelrw.cpp:7253-7295). Measured with `5t-probe`: after
30 GenericParam rows of other types, `G<A, B, C2>`'s arguments are `A#0`, `B#1`, `C2#2` (tokens `0x2A00001F` to
`0x2A000021`); after 30 GenericParamConstraint rows of other types, `H<T> where T : IA, IB, IC` reports `IA, IB, IC`;
PawPrint gives the same. No guest sees their order change at 25 rows; nothing for a later stage.

**Where PawPrint reads the order.** `TypeInfo.MethodImpls` (WoofWare.PawPrint.Domain/TypeInfo.fs:112) holds a type's
rows in image order, which for a dynamic module is the order added (`EmitScopeImage` sorts stably by class, :127).
Four consumers read it:

* `MethodTableLayout` (WoofWare.PawPrint.TypeSystem/MethodTableLayout.fs:1522), `ConcreteInterfaceDispatch`
  (ConcreteInterfaceDispatch.fs:812) and `ConcreteVirtualDispatch.findMatchingMethodImplBodies`
  (ConcreteVirtualDispatch.fs:583) model the class loader. Each fails on a conflict and otherwise gives the same result
  for any order. They need nothing.
* `DefaultInterfaceImplementation.implementationOnType` (DefaultInterfaceImplementation.fs:47-176). Its instance arm
  picks the body with the lowest slot, independent of order. Its static arm (`first`, :113-128) returns the first row
  that matches: this is `TryResolveVirtualStaticMethodOnThisType`, and it is the only place the order enters. It is
  called for each type of the chain (StaticVirtualDispatch.fs:169, and :229 for the variant entries) and for each
  interface the default-body search considers (DefaultInterfaceImplementation.fs:240). `StaticVirtualDispatch.resolve`
  is also called by `WoofWare.PawPrint.Analysis` (`EscapeAnalysis.fs:6814`), which so gets the same rule and refusal:
  it asks what a `constrained.` call runs.

PawPrint resolves a `constrained.` call afresh each time (`CallSiteBinding.fs:226`; nothing caches the result), and
`RuntimeTypeHandle_GetInterfaceMethodImplementation` has no handler yet.

### 5t: the options

Every option leaves the image, the emit scope and the three order-independent consumers as they are, and changes only
what the static arm of `implementationOnType` walks, for a type of a dynamic module. They differ in what is modelled
and where.

**A. Apply the read rule where CoreCLR has it, and refuse the case whose answer depends on CoreCLR's caches.** The
static arm walks `ReadWriteMetadataLookup.rowsOfKey` of the module's current MethodImpl row count over the level's rows
in image order, when the level's assembly is dynamic (`TryDynamicScope`), and stops at the first match as now. To find
the window of observation 2 it needs one more fact: how many MethodImpl rows the module had when the level's type was
created. `LoadedAssemblies` keeps it beside the created set (`Created` becomes a map from `TypeDefRid` to that count,
filled by `WithCreatedType`). A pure function in Domain, beside `rowsOfKey`, decides from the two counts: below 25 now,
or 25 or more at creation, gives the order; created below 25 and 25 or more now is the window. In the window the
static arm evaluates every row of the level, and if more than one matches with different bodies it fails with a TODO
naming the condition and CoreCLR's behaviour (the answer depends on whether and where CoreCLR resolved this method
before the module reached 25 rows). One match, or several with one body, resolves as now.

* Correctness: every deterministic row of the tables above; the window refused only where the answer can differ.
* Blast radius if wrong: static virtual resolution over types of dynamic modules with 25 or more MethodImpl rows and
  two matching rows on one type. A wrong threshold or a wrong count would show in the guest at 24, 25 and 26 rows.
  On-disk images are untouched by construction (`TryDynamicScope` is `None`).
* Reversibility: the rule is one call in one function; the creation count is one field. Either can be removed.
* Information: keeps the image as CoreCLR's rows in CoreCLR's order, and adds the creation-time count, which nothing
  else records today.
* Size: the Domain function and its property, the `Created` map and its accessor, the static arm's change, the
  refusal, the guest, the refusal test.

**B. Refuse every order-dependent resolution from 25 rows.** No new state: when the level's module is dynamic and has
25 or more MethodImpl rows, the static arm evaluates every row, and more than one match with different bodies is a TODO
failure. Below 25 it is as now, which is correct (and stays correct for a call site resolved then, since CoreCLR's
cached answer is the same first row).

* Correctness: never gives a wrong answer, but refuses the deterministic cases of observation 2 (a type created at 25
  rows or more), which A answers: every 25-row line of the first table becomes a refusal.
* Blast radius: the same programs as A, refused rather than answered.
* Reversibility: A is B plus the creation count and the reversal, so B can be extended to A later without undoing it.
* Information: preserves nothing new; the guest can pin only the refusal and the sub-25 behaviour.
* Size: smallest. No change to `LoadedAssemblies`, and `rowsOfKey` is not called at all.

**C. Put CoreCLR's enumeration order into `TypeInfo.MethodImpls` for a dynamic image.** The reader that builds a
dynamic module's `DumpedAssembly` is told the image is read-write metadata and its MethodImpl row count, and stores each
type's rows in `rowsOfKey` order; every consumer then reads CoreCLR's enumeration order. The window refusal is still
needed, so C is A with the reversal moved from the read site into the reader.

* Correctness: as A.
* Blast radius: every consumer of `TypeInfo.MethodImpls`, for dynamic modules. The three order-independent ones
  produce the same results; only which conflict a malformed type reports first could change, and they fail with a
  host failure there anyway.
* Information: `TypeInfo.MethodImpls` would mean different orders for an image on disk and a dynamic one, and the
  Domain reader would carry a rule about CoreCLR's read-write metadata. A future order-sensitive consumer would get
  CoreCLR's order without knowing it needed it; equally, a consumer modelling the class loader's sort would not
  notice that its input order had changed meaning.
* Reversibility: the reader's new parameter reaches every place a dynamic image is read.

Rejected without a full write-up:

* Writing the reversed order into the image (5s rejected this for MethodSemantics for the same reason): a created
  type's rows would change places when the module crossed 25 rows, and the image would no longer be CoreCLR's rows.
* Modelling CoreCLR's resolution caches, to answer the window: which call sites cache and when they resolve depends on
  shared versus exact code, inlining and tiering (the `late` measurement shows two sites disagreeing). That is
  JIT-tier behaviour, which this project refuses rather than models.

**Recommended: A.** It answers everything that has one answer, refuses only what does not, and follows 5s's decision
to model the rule on the read path where CoreCLR has it, with the image left as CoreCLR's rows. B is the fallback cut
if the creation count is judged not worth a field; it can grow into A. C spreads a read-write rule into the reader
for one consumer.

Open questions:

1. A, B or C.
2. The window: refuse (recommended), or model the answer of a first resolution after the crossing. The second is right
   for `late callBefore=False` and wrong for `CallDef<C>` in `late callBefore=True`, and PawPrint cannot tell which
   sites CoreCLR's JIT resolved early.
3. What the creation record holds: the MethodImpl row count (recommended, the one fact needed), or the whole
   `EmitScope` at creation, which would answer the same question for CustomAttribute or Event rows later at the cost of
   keeping old scopes reachable.
4. Whether to pin, in this stage, the separate divergence of observation 3 (abstract static virtual overridden twice:
   CoreCLR refuses the type at `CreateType`, PawPrint runs the first row) as a parked guest, so that it is not
   forgotten. Recommended: yes, parked with "PawPrint does not verify a dynamic type's static virtual overrides at
   `CreateType`" as its reason.
5. Whether the differential property below can drive PawPrint's own resolution. That needs a test harness that loads a
   scope's image into a `TypeSystemState` and calls `StaticVirtualDispatch.resolve`; if none exists, the property checks
   only the model against real .NET, and the guest is the only check of PawPrint's wiring. The plan should say which.
6. 5t needs `ReadWriteMetadataLookup` from 5s, so it stacks on 5s until 5s merges, then rebases onto `main`.

### 5t: the oracle

Written first, and seen to fail on this branch before the change:

* `TypeBuilderStaticVirtualOverrideOrder.cs` (in `sourcesImpure`, registered in `TestImpureCases.fs`,
  `IsDynamicCodeSupported` on), the deterministic rows of both probes, each in a module of its own and each type
  resolved once, after its module's last row: `C : IDef` at 2, 24, 25 and 26 rows with `C`'s rows first, and 24 and 25
  with them last; the swapped pair at 2 and 25; `I2 : IDef` at 2 and 25; the variant `I2` in both row orders at 2 and
  25; the single-match window (one row each for `IDef.M` and `IDef2.M2`, which must run, not refuse); and the on-disk
  `IDisk` and `IDiskSwapped` in the guest's own assembly, with enough explicit interface implementations to give it 25
  or more MethodImpl rows. Exits 0 on .NET 10, to be confirmed before committing. Today it fails at the 25-row cases.
* Its terminal-state check reads each module's MethodImpl rows in the order added (class, body, declaration), as 3e's
  does, and the scope's row count, so a filler that wrote no row cannot make a 25-row case a 24-row one unseen.
* A refusal test, in a fixture beside `TestTypeBuilderPropertyRefusals`: the `late` shape (`C` created at 2 rows with
  `B1` and `B2` for `IDef.M`, 23 more rows, then `CallDef<C>`) fails with the window's TODO, naming the type and the
  method.
* A property of the Domain function: for all counts at creation and now (now at least the creation count, both from 0
  to 60) and all row lists, it gives `rowsOfKey now` exactly when now is below 25 or the creation count is 25 or more,
  and the window otherwise; with `rowsOfKey`'s own property from 5s unchanged.
* A differential property against real .NET in the test host, as 5s's: generated schedules of up to four static
  override rows on one type (a class over `IDef`-like interfaces, or an interface over two instantiations of a variant
  one), bodies returning distinct constants, a filler count from 0 to 30 placed before, between or after the type's
  rows, and the type created after the last row; the host's answer through a fresh generic method instantiation must
  equal the model's (the first matching row of `rowsOfKey total rows`), and PawPrint's if question 5 allows. The fixed
  sample must reach 23 to 27 rows with two matching rows many times. `RunAndCollect` modules.
* A host-only oracle test pinning observation 2 on real .NET, so that a runtime bump which changes the caching shows up:
  `late callBefore=True` gives 1, then 1 and 2.
* Mutants, each run against the guest, the refusal test and the properties:
  * the rule never applied (the guest's 25-row cases);
  * the threshold at 24 (24-row cases) and at 26 (25-row cases);
  * the rule applied to an image on disk (the guest's `IDisk`);
  * the rule keyed on the type's own row count rather than the module's (the 25-row cases, where `C` has 2);
  * the window refusal dropped (the refusal test);
  * the window refusal also taken with one match (the single-match window case);
  * the window refusal taken when two matching rows share one body (needs a guest case of `B1` twice for `IDef.M` in
    the window, measured first; read, not measured: the builder deduplicates the pair, `IDef.M` is not abstract so
    nothing verifies it, and resolution meets `B1` whichever row comes first);
  * the count at creation taken now instead (the refusal test, since the window then never opens);
  * the rule applied to the instance arm as well (expected to survive: the instance arm picks the lowest slot, so the
    order cannot change its answer; record it as equivalent).

### 5t: left to later stages

* Verifying a dynamic type's static virtual overrides at `CreateType`: CoreCLR throws `TypeLoadException` for a
  non-abstract type with two rows naming one abstract static virtual, even with the same body, and for an abstract
  type as a generic argument against such a constraint; PawPrint creates the type and runs the first row (question 4),
  and refuses the abstract-argument case by name. Likewise two rows overriding one instance method with different
  bodies: CoreCLR throws at `CreateType` (`IDS_CLASSLOAD_MI_MULTIPLEOVERRIDES`), PawPrint at the first call. Not order
  questions.
* `RuntimeTypeHandle_GetInterfaceMethodImplementation` (`GetInterfaceMap`), which has no handler. For a static virtual
  it must go through `StaticVirtualDispatch.resolve` with no cache, as CoreCLR's does, and so inherits this stage's rule,
  but not its refusal: having no cache, it has one answer in the window, the order at the read. That stage moves the
  refusal out of the static arm to the consumers which CoreCLR caches.
* The first conflict a malformed type reports: CoreCLR's class loader reports the first in (body token, declaration
  token) order, PawPrint the first in row order. PawPrint fails with a host failure there today, so the message is all
  that differs.
* CustomAttribute (5s's item) still has the same hash and will want the same measurement when custom attributes come;
  the creation record of question 3 is where its window would be found. So do Constant, FieldMarshal, ClassLayout,
  FieldLayout, ImplMap, FieldRVA and NestedClass (md/inc/metamodelrw.h:668-726); each holds one row for a key in
  well-formed metadata, so the order matters only where an emitter adds a second.
* Nothing for GenericParam or GenericParamConstraint: they have no hash (above).

### 5t: the decisions

Taken 2026-11-14.

* **A: the static arm of `implementationOnType` walks a dynamic level's rows in CoreCLR's order, and refuses only where
  CoreCLR's answer depends on its own caches.** Checked against the pinned source:
  * `FindMethodImplHelper` (md/enc/metamodelrw.cpp:5133) has the three arms as described and uses no virtual sort.
    `AddRecord` clears the sorted flag (:3883). `DefineMethodImpl` calls `AddMethodImplToHash` (md/compiler/emit.cpp:178).
  * `TryResolveVirtualStaticMethodOnThisType` (vm/methodtable.cpp:8335) enumerates afresh at every call. It returns at
    its first match unless verifying, and under verification it throws on a second matching row.
  * Its callers are :5745, :8216 and :8260.
  * B refuses what has one answer, and the user asked for the order to be right.
  * C is rejected for a stronger reason than the options give. The order depends on the module's row count at the read,
    so a created type's `TypeInfo.MethodImpls` would change when another type gains a row. 3f's extension check exists
    to promise that a created type's rows read back the same and in the same order.
  * A second structure was weighed: the resolver reports that its answer depended on the order, and the consumer that
    caches (the `constrained.` call) refuses. It puts the refusal where CoreCLR has the cache, but it threads a marker
    through `StaticVirtualDispatch.resolve` and the default-body search for a distinction with one consumer today. It
    is the shape the `GetInterfaceMap` stage takes; see the correction below.
* **Conditions on A.**
  * The rule is keyed on the level whose rows are walked: that level's assembly, that module's MethodImpl row count now,
    and that TypeDef's count at creation. It is never keyed on the constrained type. A base class or an interface of
    another dynamic module is read by its own module's counts.
  * One member of `LoadedAssemblies` answers both counts together for a TypeDef of a dynamic assembly, so that the arm
    cannot pair one module's count with another's creation record. It answers `None` for an assembly that is not
    dynamic. A dynamic type that is not created is a PawPrint fault, since the type system refuses to intern one.
  * The decision is a pure function in `ReadWriteMetadataLookup`, beside `rowsOfKey`. It takes the count at creation,
    the count now and the key's rows in RID order, and returns a union. One case is one order: now below 25, or 25 or
    more at creation. The other case is both orders: created below 25 and 25 or more now. A count now below the count
    at creation is an `ArgumentException`. Both orders are kept because a reader without a cache wants the second.
  * In the window the arm walks the rows in the order added and again in reverse, each stopping at its first match. It
    resolves when the two bodies are the same and refuses when they differ. This is narrower than "more than one match
    with different bodies": rows `B1`, `B2`, `B1` resolve to `B1` in either regime. It is also exactly the condition
    under which CoreCLR's two regimes disagree. An error from either walk is the result.
  * Evaluating rows past the first match loads nothing that CoreCLR has not loaded. Read, not measured: the class loader
    resolves every row's declaration when it builds the type (`ProcessMethodImpls`, where `ValidateStaticMethodImpl` is
    called at vm/methodtablebuilder.cpp:5986).
  * The refusal is a TODO failure. It names the level, the method, the two bodies and both counts, and says that
    CoreCLR's answer depends on whether it resolved the call before the module reached 25 rows.
  * The instance arm is untouched.
* **In the window: refuse.** The refusal is needed, and it is not a guess avoided for form's sake.
  * PawPrint resolves a `constrained.` call at every execution (`CallSiteBinding.fs:226`; nothing caches the result).
    So "no cache" is the only thing it could model. That gives the reversed order, which is CoreCLR's answer for a site
    that first resolves after the crossing and wrong for a site that resolved before it (`late callBefore=True`).
  * Recording PawPrint's own resolutions would not close the gap. CoreCLR's resolutions are not the executions. For an
    exact instantiation the JIT resolves when it compiles the caller, which can be before the call runs and on a path
    never taken, and it resolves again when the method is recompiled at a higher tier, on a background thread. A
    generic dictionary resolves at the slot's first use. Which of these applies is JIT-tier behaviour, which this
    project refuses rather than models.
  * 5s has no such window, and the reason is worth keeping. `RuntimeType`'s property cache is managed code that
    PawPrint interprets, so its caching is modelled for free. The caches here are native.
  * The cost is small. The refusal needs two matching rows with different bodies on one type, and a module that
    crosses 25 MethodImpl rows after that type was created.
* **The creation record holds the count, in a record of one named field, not a snapshot of the scope.** `Created`
  becomes a map from `TypeDefRid` to that record. `WithCreatedType` reads the count from the scope it already holds,
  so its signature does not change. A snapshot has no reader, and it keeps every old scope reachable. The stage that
  needs another fact adds a field.
* **The duplicate overrides which CoreCLR refuses at `CreateType` are pinned outside 5t.**
  * The cases are two rows for one abstract static virtual, and two bodies for one instance method. A guest which
    catches the `TypeLoadException` takes a different path under PawPrint with no refusal. That is a wrong answer
    today, at 2 rows, on `main`, and it does not depend on this stage.
  * One guest for each goes into a test-only PR against `main`. Each is parked with "PawPrint does not verify a dynamic
    type's MethodImpl rows at `CreateType`" as its reason, so that 5t's diff does not claim them.
  * They assert the exception's type, not its message. The static case's message is "Undefined resource string
    ID:0x80004005".
* **The differential property drives PawPrint's own resolver.** The pieces exist: `WithDynamicAssembly` and
  `WithCreatedType` over a scope built from `EmitScopeDefinitions` (`TestEmitScopeDefinitions.fs:273`), and a test that
  calls `StaticVirtualDispatch.resolveConstrainedStaticInterfaceMethod` on a `TypeSystemState`
  (`TestDispatchBindingFailure.fs:1446`).
  * The rule's three inputs (the count now, the count at creation, dynamic or not) are wiring. A property over the
    model alone cannot kill the wiring mutants, and the guest kills them only at its fixed cases.
  * The scope is built with the same `EmitScopeDefinitions` calls the handlers make. The interfaces come from a
    compiled fixture assembly. The bodies are static methods whose IL the resolver never reads. The comparison is on
    which body's MethodDef is chosen.
  * If the scope cannot be built without reimplementing what the handlers in the interpreter project do, stop. The
    property then checks the model against real .NET only, "as built" says so, and the guest gains every schedule
    shape named below.
* **The host's reader in that property is `GetInterfaceMap`, once measured.** Read, not measured:
  * `RuntimeType.GetInterfaceMap` calls `RuntimeTypeHandle_GetInterfaceMethodImplementation` for each method at every
    call and caches nothing (RuntimeType.CoreCLR.cs:2785).
  * That QCall calls `ResolveVirtualStaticMethod` with variance allowed (vm/runtimehandles.cpp:862).
  * So it is a reader with no cache. In the window it should give the order added before the crossing and the reverse
    after it, on one type.
  * The first step of the stage is to measure that on .NET 10 and 11: `C` created at 2 rows, the map read, 23 rows
    added, the map read again.
  * If it agrees, the property's schedules may read on both sides of the crossing, and the union's second order is
    checked against real .NET. If it disagrees, keep the planner's fresh generic method instantiations and report.
* **5t is built on 5s's branch, and its PR is not opened until 5s has merged and 5t is rebased onto `main`.** It needs
  `ReadWriteMetadataLookup`, which only 5s has. A stacked PR runs no CI. The test-only PR above does not wait.
* **One feature, one PR.** The read rule, the creation record, the refusal and their tests are one feature: a static
  virtual resolved over a dynamic module's rows as CoreCLR resolves it. The commits go in the order tests, Domain,
  Loader, the static arm, with the failing run recorded.
* **Corrections to the other 5t sections.**
  * "Where PawPrint reads the order" omits a caller. `StaticVirtualDispatch.resolve` is also called by
    `WoofWare.PawPrint.Analysis` (`EscapeAnalysis.fs:6814`). It gets the same rule and the same refusal, which is right:
    it asks what a `constrained.` call runs.
  * "Left to later stages" says `GetInterfaceMap` inherits this stage's rule and refusal. It inherits the rule only.
    Having no cache, it has one answer in the window, the order at the read. That stage moves the refusal out of the
    static arm to the consumers which CoreCLR caches.
  * Observation 2's last sentence is too wide. A type resolved while the module is below 25 has one answer for that
    resolution only. A type created at 25 rows or more has one answer always.
  * The hash list is longer than "CustomAttribute still has the same hash". Constant, FieldMarshal, ClassLayout,
    FieldLayout, ImplMap, FieldRVA and NestedClass have one too (md/inc/metamodelrw.h:668-726). Each holds one row for
    a key in well-formed metadata, so the order matters only where an emitter adds a second.
  * 5s "as built" is wrong about GenericParam and GenericParamConstraint, as this stage's text says. Its last bullet is
    to be corrected in place when 5t is recorded.
* **The oracle is sufficient with these additions.**
  * Guest cases, each measured on .NET 10 first:
    * a struct `C` at 2 and 25 rows, so that the JIT's own resolution of exact code is covered beside the dictionary's;
    * `C` created at 25 rows, five more rows added, then resolved: reversed, and not refused;
    * a base `C` with rows `B1`, `B2` in a module of 25 rows and `D : C` in a module of none, with `CallDef<D>`, and the
      converse with `C`'s module at 2 rows and `D`'s at 25;
    * in the window, `B1` twice, and `B1`, `B2`, `B1`, which must run.
  * The Domain property also requires that the window's two orders are the given rows and their reverse.
  * The differential schedules draw bodies with repetition, so that `B1`, `B2`, `B1` occurs.
  * The host-only test of the cache (`late callBefore=True`) stays, as the evidence for the refusal. It uses a
    reference type, so the resolution sits in a dictionary slot that no recompilation revisits. Run it repeatedly
    before committing. If it is not stable, drop it and record that as further grounds for refusing.
  * Mutants added:
    * the counts taken from the constrained type's module, not the level's (the two-module cases);
    * the window taken whenever rows were added since creation (the type created at 25);
    * the window refusing on more than one match where the two walks agree (`B1`, `B2`, `B1`);
    * the window answered with the reversed order (the refusal test).
* **Additions to "left to later stages".**
  * The test-only PR's two parked guests, and the verification at `CreateType` that un-parks them.
  * `GetInterfaceMap` as corrected above.
  * The Constant stage checks whether a second `SetConstant` adds a row or overwrites one, since its table has the hash.

### 5t: as built

Built 2026-11-14 as decided above, after the measurements the decisions asked for, which agree with the reading on .NET
10.0.14 and 11.0.0-rc.3 alike (throwaway probe `5t-probe3`):

| Emitted | Observed |
|---|---|
| `C : IDef` with rows `B1`, `B2`, created at 2 rows; `GetInterfaceMap` read; 23 rows added; read twice more | `B1`, then `B2`, `B2`: no cache, the order at the read |
| a struct `C` with rows `B1`, `B2`, at 2 / 25 rows | 1 / 2 |
| `C` created at 25 rows, 5 more rows, then called | 2 |
| base `C` (rows `B1`, `B2`) in a module of 25 rows, `D : C` in a module of none; `CallDef<D>` | 2 |
| the converse: `C`'s module at 2 rows, `D`'s at 25 | 1 |
| in the window, rows `B1` twice; rows `B1`, `B2`, `B1` | 1; 1 |

The host-only cache test (`late callBefore=True`) gave 1, then 1 and 2, in 20 of 20 runs under default tiering, 10 of
10 under `DOTNET_TieredCompilation=0` and 10 of 10 under `DOTNET_TieredPGO=0`, so it stays.

* `ReadWriteMetadataLookup.rowsOfKeySinceCreation`, beside `rowsOfKey`, returns a `RowsSinceCreation`: `OneOrder`, or
  `TwoOrders (inOrderAdded, reversed)` exactly when the owner was created below 25 rows and the table has 25 or more now.
  It refuses a count now below the count at creation, and a key with more rows than the table had at creation.
* `LoadedAssemblies.Created` maps each created `TypeDefRid` to a `TypeCreation` record, whose one field is the scope's
  MethodImpl row count when `WithCreatedType` was called. `TryMethodImplRowCounts` takes the type's
  `ResolvedTypeIdentity` (`TypeDefRid`'s constructor is not visible outside Domain) and answers both counts from that
  one assembly's content, `None` for an assembly that is not dynamic, and crashes for a dynamic type not created.
* The static arm of `DefaultInterfaceImplementation.implementationOnType` asks for the level's counts. With none, it
  walks the rows in image order as before. With one order, it walks that. With two, it walks both, each to its first
  match; an error from either is the result, the same body (or no match) resolves, and two bodies are the TODO
  failure, which names the level, the method, both bodies and both counts. The instance arm is untouched.
* Tests: `TypeBuilderStaticVirtualOverrideOrder.cs` (25 checks, registered in `TestImpureCases.fs` with
  `assertStaticVirtualOverrideModules`, which checks every module's MethodImpl row count and `Def25a`'s two rows);
  `TestStaticVirtualOverrideOrderRefusals` (the window); `TestStaticVirtualOverrideOrder`, holding the host-only cache
  test and the differential property, which builds each schedule's scope with `EmitScopeReferences`,
  `EmitScopeDefinitions` and `EmitScope.addMethodImpl`, registers it with `WithDynamicAssembly`,
  `WithAssemblyRefTarget`, `WithCreatedType` and `WithEmitScope`, and resolves `IDef.M` with
  `StaticVirtualDispatch.resolve`, at creation and at the end, against real .NET's `GetInterfaceMap` at the same two
  points; the Domain property in `TestEmitScopeProperties`; and the counts in `TestLoadedAssemblies`. Bodies are drawn
  with repetition, so `B1`, `B2`, `B1` occurs; the fixed sample's coverage floors are asserted.
* The test-only commit's red run: the guest exited 3 ("check 3: got 1, expected 2"), the refusal test saw no failure,
  and the property was falsified at creation by `{ Before = 21; Rows = [3; 2; 3; 2]; Between = 17; After = 0 }`
  (PawPrint `B3`, real .NET `B2`).
* Each mutant was run against the current shape, after the refusal moved to the consumers (addendum below), with a
  fresh build each, against the guest, both refusal tests (the class's and the interface's), the differential property
  with its interface schedules, the Domain properties (`TestEmitScopeProperties`) and `TestLoadedAssemblies`:

  | Mutant | Killed by |
  |---|---|
  | The rule never applied | the guest (check 3); the property; both refusal tests |
  | The threshold at 24 | the guest (check 2); both Domain properties; 5s's differential property |
  | The threshold at 26 | the guest (refuses `After25`, created at 25 rows); both Domain properties; the property; both refusal tests; 5s's differential property |
  | The rule applied to an image on disk | the guest (check 15, `CDisk`) alone: neither property reads an image on disk |
  | The rule keyed on the type's own row count, not the module's | the guest (check 3); the property; both refusal tests |
  | The window answered with the reversed order (no order-dependent body) | the property; both refusal tests. The guest passes: its window cases are the ones whose walks agree, or that a more specific body or a conflict decides |
  | The window giving an order-dependent body on any match | the guest (refuses `WindowOne`, one row per method); the property |
  | The window giving an order-dependent body on more than one match where the two walks agree | the guest (refuses `Window2`, `B1` twice); the property |
  | The count at creation taken now | the property; both refusal tests (the window never opens); the guest passes |
  | The counts taken from the constrained type's module, not the level's | the guest: check 20 through `rowsOfKeySinceCreation`'s check that a key's rows fit the table at creation (`D`'s module had none), and, with that check and `rowsOfKey`'s removed too, check 20's answer (1, not 2) |
  | The window taken whenever rows were added since creation | the guest (refuses `After25`); the Domain property; the property |
  | `WithCreatedType` recording 0 | the guest (through the same check, then with it removed by refusing `Def25a`); `TestLoadedAssemblies`; the property; both refusal tests |
  | `TryMethodImplRowCounts` giving the count at creation as the count now | `TestLoadedAssemblies`; the property; both refusal tests; the guest passes |
  | `TwoOrders` holding its orders swapped | the Domain property; both refusal tests (their messages name the bodies in the two orders) |
  | The rule applied to the instance arm too | survives, as expected: the instance arm picks the body with the lowest slot, so the order of its rows cannot change its answer |

* Suites at the tip, built from the whole solution: the default suite (5569 passed, 37 skipped), `Guest` (234),
  `GuestSolo` (2), `GuestPure` (1179) and `GuestImpure` (276), each run on its own, all passing.
* Addendum, after Codex's review: the refusal moved from the static arm to the consumers that decide the call.
  * Codex's case: `I2 : IDef` with rows `D1`, `D2`, created below 25 MethodImpl rows, then its module grown to 25;
    `I3 : I2` supplying `D3`; `C : I3`. Measured on .NET 10.0.14 and 11.0.0-rc.3 (throwaway probe `5t-probe4`): `D3`
    before and after the crossing, at a fresh call site and through `GetInterfaceMap` alike. The arm refused at `I2`,
    although the default-body search discards `I2`'s candidate for `I3`'s. `C : I2` alone gives `D1` at a site that
    resolved before the crossing and `D2` at one that did not, as the class case does.
  * Measured too, the same way: two equally specific interfaces in the window, one or both of whose rows disagree,
    make the call throw `AmbiguousImplementationException` before and after the crossing. Which candidates conflict
    depends on their interfaces, not their bodies, so an order-dependent candidate in a conflict decides nothing.
  * So this is the decisions' "second structure", now with two consumers. The static arm reports
    `FoundBody.OrderDependent` (both bodies and both counts) where it refused, and `FoundBody.Certain` otherwise.
    `StaticVirtualDispatch`'s walk of the chain refuses an order-dependent body, since a MethodImpl of the chain is
    the call's answer; its default-body step refuses one only for the single surviving candidate (or the first
    survivor of the variant pass, which is what runs), and names a conflicting one by the body a read now finds.
    `DefaultInterfaceImplementation.search` carries the candidates unchanged in shape, `Body` now a `FoundBody`;
    the instance search never yields an order-dependent body, and its consumer crashes if it does.
    `EscapeAnalysis` asks `StaticVirtualDispatch.resolve`, so it gets the same answers.
  * Tests, committed first: the guest gains Codex's case (checks 26-27) and the two ambiguities (28-29); the
    refusal fixture gains `C : I2` with `I2` the most specific; the differential property's schedules give the rows
    to `C`, to an interface `C` implements, or to an interface beneath a more specific one, and half of them now
    cross 25 rows, so that every window label is reached many times. Red: the guest refused at `I2` ("the
    MethodImpl rows of I2 that implement M give D1 in the order added and D2 in reverse ... module Overridden"), and
    the property was falsified by `{ Shape = Overridden; Before = 8; Rows = [1; 3; 3]; Between = 7; After = 18 }`;
    both refusal tests passed.
  * Mutants added, each run as before:

    | Mutant | Killed by |
    |---|---|
    | The refusal back at the level | the guest (refuses `I2` in `Overridden`); the property |
    | An order-dependent body resolved to the one a read now finds, everywhere | both refusal tests; the property |
    | An order-dependent body resolved to the one found in the order added, everywhere | both refusal tests; the property |
    | The default-body step resolving an order-dependent survivor | the interface refusal test; the property |
    | The chain walk resolving an order-dependent MethodImpl | the class refusal test; the property |

  * Suites at the tip, built from the whole solution: the default suite (5570 passed, 37 skipped), `Guest` (234),
    `GuestSolo` (2), `GuestPure` (1179) and `GuestImpure` (276), each run on its own, all passing.

### 5u: `RuntimeTypeHandle_IsVisible`

**Which stage.** Three candidates, each checked to be still outstanding on `origin/main` (06fc2a1d7) and on 5t's branch
(cbe8a5fc4): neither branch has a handler for `TypeBuilder_SetParamInfo` or `RuntimeTypeHandle_IsVisible`, and on both
`MetadataImport.Enum` refuses token type `0x14000000` (`Native/NativeMetadataImport.fs:1175`).

* `RuntimeTypeHandle_IsVisible` is reached through `Type.IsVisible` of any `RuntimeType`. In the .NET 10.0.14 shared
  framework, `get_IsVisible` is called by `DispatchProxyGenerator.ProxyAssembly.EnsureTypeIsVisible` (of the proxy's
  base type and of every interface it implements), by `System.Linq.Expressions.Compiler.ILGen.ShouldLdtoken` (a `Type`
  constant in a compiled expression), by `XmlSerializer`'s `TypeScope.ImportTypeDesc`, by `DataContractSerializer`'s
  `ClassDataContract.IsNonAttributedTypeValidForSerialization`, by `CustomValidationAttribute` and by `CryptoConfig`
  (found by searching each framework assembly for the name, then disassembling the callers with
  `WoofWare.PawPrint.IlDump`). Measured under PawPrint at 5t's tip: both parked `DispatchProxy` guests, and a compiled
  `Expression.Constant(typeof(T), typeof(Type))`, stop at it first. It is small: one QCall, over a walk CoreCLR writes
  in about forty lines.
* Event enumeration (`MetadataImport.Enum` of a type's events) is the second stop of both `DispatchProxy` guests. Every
  program measured reaches `IsVisible` before it, so doing it first moves no guest.
* `TypeBuilder_SetParamInfo` is the next emit QCall by 5s's survey, and every generator that names its parameters stops
  at it (in the shared framework, `System.Private.Xml`, which holds `XmlSerializer`'s IL generator, references
  `DefineParameter`; outside it, Castle's `DynamicProxy` is believed to call it, not checked here). But no program
  known to call it would run to completion with it alone: Castle applies custom attributes too (likewise unchecked),
  and `XmlSerializer`
  stops earlier, refusing `AssemblyNative_InternalLoad` of its pregenerated `*.XmlSerializers` assembly (measured,
  `5u-probe xml`).

So `IsVisible` comes first. It is the first stop of `DispatchProxy`, the one realistic emit consumer that two small
stages then run end to end (with event enumeration as 5v, the method-only guest exits 0, and since 5s the property
guest too, both measured by 5s with throwaway edits), and four other framework paths reach it. It is reflection, not
emit, and 5s decided that it and event enumeration were reflection PRs against `main`, outside this plan. Planning it
here reverses that (see the decisions): the measurements of `DispatchProxy`'s stops are in this plan. "5v" in the 5u
sections means "the events stage", wherever that is planned. The runner-up, `SetParamInfo`, comes after it.

**What CoreCLR does.** Read from the pinned source (`$DOTNET_RUNTIME_SRC_NET10`):

* `RuntimeTypeHandle.IsVisible(RuntimeType)` (System.Private.CoreLib/src/System/RuntimeHandles.cs:645) is a
  `LibraryImport` of `RuntimeTypeHandle_IsVisible(QCallTypeHandle)` returning `bool`, marshalled from a 4-byte integer.
  The QCall (vm/runtimehandles.cpp:709) returns `TypeHandle::IsExternallyVisible()`. Only a `RuntimeType` reaches it:
  `Type.IsVisible` (libraries/System.Private.CoreLib/src/System/Type.Helpers.cs:76) answers any other `Type` (a
  `TypeBuilder`, a `TypeBuilderInstantiation`) by a managed walk.
* `TypeHandle::IsExternallyVisible` (vm/typehandle.cpp:991): a MethodTable asks `MethodTable::IsExternallyVisible`; a
  generic variable (`VAR`, `MVAR`) is visible; a function pointer asks `FnPtrTypeDesc::IsExternallyVisible`
  (vm/typedesc.cpp:1775), which requires its return type and every parameter type to be visible; a pointer or byref asks
  its element.
* `MethodTable::IsExternallyVisible` (vm/methodtable.cpp:1500): an array asks its element; any other type asks
  `IsTypeDefExternallyVisible` of its TypeDef, and, if that holds and the type has an instantiation and is not a generic
  type definition, every argument must be visible.
* `IsTypeDefExternallyVisible` (vm/siginfo.cpp:3190): `tdPublic` is visible; anything but `tdNestedPublic` is not;
  `tdNestedPublic` walks out through the enclosing types while each is `tdNestedPublic`, and the answer is whether the
  first that is not is `tdPublic`. So `tdNestedFamily`, `tdNestedFamORAssem` and the rest are invisible at every level,
  though reflection calls them accessible from a derived type. A failed lookup of the encloser gives false; PawPrint's
  reader refuses a `TypeInfo` whose nested visibility and NestedClass row disagree (TypeInfo.fs:211, `IsNested`), so
  that arm has no counterpart.
* Two MethodTables have no metadata. The dynamic-methods class (`CreateMinimalMethodTable`, vm/methodtable.cpp:663)
  sets its class attributes to `tdPublic | tdSealed` (:694), so it is visible with no metadata read. A runtime-async
  continuation's class (.NET 11 only: `AsyncContinuationsManager::CreateSingletonSubContinuationEEClass`,
  vm/asynccontinuations.cpp:54 of `$DOTNET_RUNTIME_SRC_NET11`) comes from `EEClass::CreateMinimalClass` and has no
  attribute set, so by the source it is `tdNotPublic` and invisible. Not measured: user code cannot reach a
  continuation's `Type`.
* Nothing caches the answer, and it reads only the type's identity and its TypeDef flags, which do not change once the
  type exists, so one type has one answer at every call.

**Measured on .NET 10.0.14** with a throwaway probe (`5u-probe`), every row the same on 11.0.0-rc.3 (rolled forward
from the net10.0 build). `Pub` is a public class and `Internal` an internal one, `PubG<T>` and `InternalG<T>` generic
classes of each visibility, `Pub.X` a type nested in `Pub`, `IntS` an internal struct, and `T` the parameter of `PubG<>`
unless stated:

| Type | `IsVisible` |
|---|---|
| `Pub`, a public struct, interface, enum and delegate, `int`, `object`, `void`, `TypedReference`, a public struct's `Nullable` | true |
| `Internal`, an internal interface, an internal delegate, CoreLib's `System.RuntimeType` | false |
| `Pub.NPub` (nested public), `Pub.NPub.NPubPub` | true |
| `Pub.NFam`, `Pub.NAsm`, `Pub.NFamOrAsm`, `Pub.NFamAndAsm`, `Pub.NPriv`; a nested public type inside each of `NFam`, `NAsm` and `NPriv`; a protected type inside `NPub`; `Internal.NPub` | false |
| `PubG<int>`, `Pub.G<int>.Inner`, `int?` | true |
| `PubG<Internal>`, `PubG<PubG<Internal>>`, `PubG<Internal[]>`, `Dictionary<int, Internal>`, `List<RuntimeType>`, `InternalG<int>`, `Pub.G<Internal>.Inner` | false |
| `PubG<>`, `Pub.G<>.Inner` (generic type definitions); `InternalG<>` | true; false |
| `T`, the parameter of `InternalG<>`, the parameter of a generic method of `Pub` | true |
| `PubG<U>` with `U` the parameter of `InternalG<>`, `PubG<Dictionary<Pub, T>>`, `Pub.G<T>.Inner`, `T[]` of `InternalG<>`'s parameter, its byref, `T*` | true |
| `InternalG<T>`, `PubG<Dictionary<Internal, T>>`, `Dictionary<Internal, T>[]` | false |
| `Pub[]`, `Pub[][]`, `int*`, `Pub&`, `delegate*<int, void>` | true |
| `Internal[]`, `Internal[,]`, `Internal[][]`, `Pub.NFamOrAsm[]`, `IntS*`, `Internal&`, `delegate*<Internal, void>`, `delegate*<Internal>` (the return type alone), `delegate* unmanaged<IntS, int>` | false |
| `delegate*<T, void>` over the parameter of a generic class `FnHolder<T>`, as a field's type and as a method parameter's type; `delegate*<int, void>` as `FnHolder<int>`'s field's type | true |
| `delegate*<T, Internal>` as `FnHolder<>`'s field's type | false |
| a dynamic module's `DPub` (`Public`) and `DNotPublic` (`NotPublic`), as `TypeBuilder`s (the managed walk) and as created `RuntimeType`s (the QCall) | true and false, both ways |
| a dynamic generic `DG<T>` created: `DG<>`, `DG<int>`, `DG<Internal>`; `PubG<DNotPublic>` | true, true, false; false |
| `ModuleBuilder.DefineType("DNested", TypeAttributes.NestedPublic)` | `ArgumentException` before any QCall, so a dynamic module cannot hold a top-level nested type |

That last row is not why a dynamic module has no nested type here: PawPrint refuses `DefineNestedType` itself
(`TypeBuilder_DefineType` with an encloser stops, because an emit scope has no NestedClass table), so every dynamic type
5u can see is top-level.

A second probe (`5u-probe2`) reaches the dynamic-methods class through CoreLib's internal `DynamicMethod.GetMethodDescriptor`
and `RuntimeMethodHandle.GetDeclaringType`: its `Attributes` is `0x101`, and it and its array type are visible, on both
runtimes. Its `Name` is `(dynamicClass)` (vm/typestring.cpp:841; measured on both runtimes by a run of `5u-probe2` with
no argument, saved as `net10-noargs.out` and `net11-noargs.out`, after the decisions noted that the earlier saved runs
skipped that line).

Under PawPrint at 5t's tip, every type of `5u-probe` is constructed, and named as on .NET 10 (`5u-probe shapes`), except
the field type of `FnHolder<>`'s `delegate*<T, void>` field: reading it stops in `Signature.Init` with "CoreCLR refuses
a signature with BadImageFormatException: it names the type parameter !0, and its context instantiates 0", where real
.NET answers; the same type as a method parameter's type is constructed. The first `IsVisible` of a `RuntimeType` stops
at "Unimplemented native method (PInvokeImpl
QCall!RuntimeTypeHandle_IsVisible)", while a `TypeBuilder`'s already answers as on .NET 10. With a throwaway handler
answering a constant (reverted):

| Program | Answer given | Next stop |
|---|---|---|
| `DispatchProxyMethodInterface.cs`, `DispatchProxyPropertyInterface.cs` (public interfaces) | true | `MetadataImport.Enum` of token type `0x14000000`, the interface's events |
| the method-only guest with its interface made `internal` (`5u-proxy-internal`; exits 0 on .NET 10 and 11) | false, as on real .NET | `TypeBuilder_DefineCustomAttribute`: `DispatchProxy` defines `IgnoresAccessChecksToAttribute` and applies it to its assembly, so that the proxy may implement the interface |
| the same | true, which is wrong | the events stop, as for a public interface |
| `Expression.Lambda<Func<Type>>(Expression.Constant(typeof(Internal), typeof(Type))).Compile()` | false, as on real .NET | `ReflectionInvocation_CompileMethod` |

So the next stop does not catch a wrong answer: answering true for an internal interface skips the attribute and goes on
as a public interface does. A parked guest of the internal interface would not catch it either, since nothing runs a
parked case or reads its stop; what pins the false answer in CI is the internal interface's row in the pure guest and
the differential property (see the decisions).

The dynamic-methods class shows a separate divergence: PawPrint refuses its `Name` (`RuntimeTypeHandle_ConstructName`)
and its `Attributes` (`RuntimeTypeHandle.GetAttributes`) through `RuntimeTypeHandleTarget.refuseMetadataQuery`, whose
message says CoreCLR has nothing to answer from; but CoreCLR's `GetAttributes` reads the attributes
`CreateMinimalMethodTable` sets, and answers `0x101` (measured). Not this stage's (see "left to later stages").

**Where PawPrint will answer.** A guest `RuntimeType`'s handle arrives as a `RuntimeTypeHandleTarget`
(`NativeCall.qCallTypeHandleToRuntimeTypeHandleTarget`), whose cases match CoreCLR's kinds of TypeHandle: `Closed` of a
`ConcreteTypeHandle` (`Concrete`, `Byref`, `Pointer`, `OneDimArrayZero`, `Array`, `FunctionPointer`, `Continuation`),
`OpenGenericTypeDefinition`, `GenericParameter`, `MethodGenericParameter`, `OpenConstructed`, `Composite`, an open
`FunctionPointer`, and `DynamicMethodsClass`. Under PawPrint, `5u-probe` reaches every case except `Continuation` and
`DynamicMethodsClass` (the open `FunctionPointer` through a method parameter's type). The saved run of `5u-probe2`
stops at `RuntimeTypeHandle.GetAttributes`, the line before `IsVisible`, so it shows only that the reflection route to
the dynamic-methods class's `Type` runs.
`NativeCall.qCallTypeHandleToConcreteTypeHandle` refuses every open case, so the handler cannot use it. The TypeDef's
facts are in its `TypeInfo`, `TypeAttributes` and `DeclaringType` (the NestedClass row), for an image on disk and a
dynamic module's alike. `AccessCheck.levelIsVisible` (WoofWare.PawPrint.Domain/AccessCheck.fs:114) decides one nesting
level of an access check, which is a different question: it admits a `NotPublic` level to a friend assembly, and fails
on the family and private levels. `NativeRuntimeMethodHandle.buildAccessLevelChain` already walks `DeclaringType` to
the outermost type, privately, for that access check; 5u does not touch it.

### 5u: the options

Every option adds a handler for `RuntimeTypeHandle_IsVisible` in `NativeRuntimeTypeQCall`, registered in `NativeQCall`,
which decodes the target, pushes 1 or 0, and changes nothing else. They differ in where the rule lives and when it is
evaluated.

**A. CoreCLR's walk, evaluated at each call, in three layers.** Domain gains `isTypeDefExternallyVisible` over one
assembly's `TypeInfo`s, which is `IsTypeDefExternallyVisible` walking `DeclaringType` (an encloser is always in its
nested type's assembly). TypeSystem gains a walk over a `ConcreteTypeHandle`, given the concrete types: an array, byref
or pointer asks its element; a function pointer asks its return and parameter types; `Concrete` asks its definition's
rule, then its generic arguments. The handler walks the `RuntimeTypeHandleTarget`: `Closed` goes to the TypeSystem walk;
a generic parameter of either kind is true; `OpenGenericTypeDefinition` is its definition's rule alone; `OpenConstructed`
is its definition's rule, then each argument; `Composite` is its element; an open `FunctionPointer` is its return and
parameter types; `DynamicMethodsClass` is true, citing `CreateMinimalMethodTable`'s `tdPublic | tdSealed`; and
`Continuation` is refused by name (question 4).

* Correctness: every row above, open shapes included, each a case of the source's.
* Blast radius if wrong: `Type.IsVisible` of a `RuntimeType`. A wrong true changes control flow in `DispatchProxy` (no
  `IgnoresAccessChecksTo`), in `Expression.Compile` (an `ldtoken` in place of a closure constant), and in which types
  `XmlSerializer` and `DataContractSerializer` accept; a wrong false the converse.
* Reversibility: one new function in each of two projects, and a handler; nothing existing changes shape. Domain is a
  published package, so its new function is public API, but an addition.
* Information: computed from the facts the image holds; nothing new is stored.
* Size: the two functions and their tests, the handler, the guests.

**B. The access-check model, asked for a stranger.** `IsVisible` of a type is `AccessCheck.canAccessClass` from an
accessor that no assembly names as a friend and that declares no `IgnoresAccessChecksTo`, extended to walk shapes and
generic arguments.

* Less new code for the TypeDef rule. But `levelIsVisible` fails on the family and private levels by design (porting them
  needs `CanAccessFamily`), and a stranger's answer for them must be false, so B either ports them or special-cases them;
  and it builds a synthetic `AccessParty`, which names an assembly, for a question that names none.
* Not CoreCLR's structure: `IsExternallyVisible` is not an access check, and sees neither `InternalsVisibleTo` nor
  `IgnoresAccessChecksTo`. A later change to access checks (porting family access, say) would change `IsVisible`
  silently.
* Blast radius: `IsVisible` and every access check share a path, so a fault in either shows in both.
* Reversibility: separating them later means writing A.

**C. Visibility computed once, when a type's definition or instantiation is made.** The reader records
`IsExternallyVisible` on each `TypeInfo` (it has the NestedClass rows to hand), and concretisation records it on each
`ConcreteType` (from the definition and the arguments); the handler reads the field for `Closed`, and walks only shapes
and open forms.

* Answers each call in constant time, which nothing needs: CoreCLR walks at every call, and no measured consumer calls
  it in a loop.
* A field on `TypeInfo` and on `ConcreteType`, both public types of a published package: every constructor of either
  (the reader, the re-read of an emit image, test fixtures) gains it, and removing it later is a breaking change.
* A derived fact stored beside the facts it is derived from, so every path that builds a `TypeInfo` must agree with the
  walk; and the open forms still need A's walk in the handler, so C is A plus a cache.

**Recommended: A.** It is CoreCLR's structure; it puts each fact in the lowest project that can hold it (the TypeDef rule
in Domain, the instantiation walk in TypeSystem, the walk over a handle where the handle is); and it is total over the
handle's cases rather than projecting an open handle onto a closed one. B couples two rules that CoreCLR keeps apart. C
adds a cached copy of A's answer to two published types, for no measured need.

Open questions:

1. A, B or C.
2. Whether this stage answers the open forms (generic parameters, open constructions, and shapes and function pointers
   over them), every one measured above and each a line of the walk, or refuses them by name until a consumer needs them.
   Answering is recommended: `Expression.Compile` asks `IsVisible` of whatever `Type` constant an expression holds, open
   generic definitions included, and a refusal would leave the walk partial.
3. The dynamic-methods class: answer true (measured; from attributes CoreCLR sets with no metadata), or refuse it as
   `GetAttributes` and `ConstructName` do. Answering is recommended, with `GetAttributes`' refusal recorded as a separate
   divergence, since its premise, that CoreCLR has nothing to read, is false for attributes.
4. The runtime-async continuation (.NET 11 only): refuse by name (recommended: read from the source, not measured, and
   unreachable from user code), or answer false from the source.
5. Whether the stage belongs in this plan, as planned here, or in a plan of its own for the reflection `DispatchProxy`
   needs, with event enumeration as that plan's second stage.
6. Whether to add, in this stage, a parked guest of `DispatchProxy` over an `internal` interface, whose stop at
   `TypeBuilder_DefineCustomAttribute` (measured with the false answer) records the program that custom attributes
   un-park. Recommended: yes, parked with that stop as its reason. It is not a check of the false answer while parked
   (see the decisions), so it also checks, for when it runs, the `IgnoresAccessChecksToAttribute` that only the false
   answer produces.

### 5u: the oracle

Written first, and seen to fail on this branch before the handler exists:

* `TypeIsVisible.cs` in `sourcesPure` (registered in `TestPureCases.fs`, so that the real-runtime oracle runs it as
  well), with a check number for every compiled row of `5u-probe`'s table: every nesting level's visibility, the
  instantiations, the generic definitions, the generic parameters, the open constructions, the arrays, pointers, byrefs
  and function pointers (the open ones as a method parameter's type), `List<RuntimeType>`, and a generic definition as
  an argument, both ways (`PubG<PubG<>>` true and `PubG<InternalG<>>` false, measured on both runtimes, `5u-probe3`).
  It returns the number of the first failing check, so a wrong answer names its type. Exits 0 on .NET 10.
* `TypeBuilderIsVisible.cs` in `sourcesImpure` (`IsDynamicCodeSupported` on): `DPub` and `DNotPublic` as builders and
  as created types; `DG<>`, `DG<int>`, `DG<Internal>` and `PubG<DNotPublic>`; `DefineType` with `NestedPublic` throwing
  `ArgumentException`. It has no nested dynamic type, for the reason above.
* `DynamicMethodsClassIsVisible.cs`, a guest of its own because it reaches CoreLib's internals by name: the class and its
  szarray (`MakeArrayType` over the class runs under PawPrint and reaches `IsVisible`, measured with `5u-probe3`). It
  asks `IsVisible` only, never `Name` or `Attributes`. No terminal-state check in any of the three guests: the QCall
  writes nothing, and every answer is in the exit code.
* `DispatchProxyMethodInterface.cs` and `DispatchProxyPropertyInterface.cs` stay parked, their reasons changed to their
  next stop, measured by naming each case once the handler exists. `DispatchProxyInternalInterface.cs` is parked with
  question 6's stop as its reason; besides the proxy's calls it checks that the proxy's assembly carries
  `IgnoresAccessChecksToAttribute` naming the guest's assembly, read with `GetCustomAttributesData`, which real .NET
  exposes on both runtimes (measured with `5u-probe3`: the internal interface's proxy assembly has it, with the
  caller's assembly name as its argument).
* A same-image differential property against real .NET, in the test host, over the pure function of the target (the
  handler is a stub named by the source generator, and holds no decision; the guests cover it), with PawPrint's answer
  equal to host `Type.IsVisible` of the same type:
  * the CoreLib sweep in two parts: the Domain rule against the host's `IsVisible` for every TypeDef of `HostImage`'s
    CoreLib, by metadata token, with the count asserted equal to the image's; and the walk for each TypeDef as an
    `OpenGenericTypeDefinition` or a `Closed` handle, where a type that concretisation refuses is listed by name with
    its reason, and the test asserts that list;
  * an enumerated nesting fixture, compiled once: every chain of a top-level class, public or not, with up to three
    nested levels of each of the six nested visibilities (518 types, 432 of them innermost at the fourth level), and a
    second, smaller forest with generic levels;
  * FsCheck constructions over that fixture's types and CoreLib's, as a small tree (definition, instantiation, szarray,
    array of rank 2 or 3, pointer, byref outermost, either kind of generic parameter, function pointer). The host's
    `Type` and PawPrint's target are both built from the tree; the target goes through `openConstructed`, `composite`
    and `functionPointer`, and through concretisation where every part is closed. The generator draws only what the
    host can build (no pointer, byref or `void` as a generic argument, no array or pointer over a byref, instantiation
    only of definitions with no constraints), so nothing is discarded after the draw. The host's function pointer types
    come from fields of the fixture: every signature of at most two parameters over a handful of types the holder can
    name (a public type, an internal one, a public generic over the internal one, the holder's own parameter, and
    `void` as a return).
  Coverage is asserted on the fixed sample for the constructions: a visible definition with an invisible argument at
  depth one and deeper, an invisible type under each shape, an open construction with an invisible closed argument, a
  function pointer invisible by its return type alone, and a bare definition as an argument.
* The Domain property, over every chain of visibilities up to length five, exhaustively, against the model: true exactly
  when every level but the last is `NestedPublic` and the last is `Public`. Unit tests of the pure function for the
  `DynamicMethodsClass` target and a `Composite` over it, built directly.
* Mutants, each run against the guests, the differential property and the unit tests:
  * `NestedFamily` and `NestedFamORAssem` treated as visible (`Pub.NFam`, `Pub.NFamOrAsm`);
  * the walk stopping at the immediate encloser, asking whether it is `Public` (`Pub.NPub.NPubPub`);
  * `NestedPublic` accepted without the walk (`Internal.NPub`, the public type inside `NPriv`);
  * generic arguments not checked (`PubG<Internal>`, `List<RuntimeType>`);
  * the argument check one level deep only (`PubG<PubG<Internal>>`);
  * a generic parameter answered by its owner's visibility (`InternalG<>`'s parameter);
  * an array answered by `System.Array`'s own TypeDef rather than its element (`Internal[]`, `Internal[,]`);
  * a function pointer's return type skipped (`delegate*<Internal>`);
  * an open function pointer answered true without its types (`delegate*<T, Internal>`, taken as a method parameter's
    type, since its field route stops first under PawPrint);
  * an open construction's arguments skipped (`PubG<Dictionary<Internal, T>>`);
  * a `Composite` answered true without its element (`Dictionary<Internal, T>[]`);
  * the dynamic-methods class answered false;
  * the rule applied to images on disk only, with a dynamic module's created type answered true (`DNotPublic`);
  * each of the five non-public nested levels treated as `NestedPublic`, one at a time;
  * a top-level `NotPublic` type answered true (`Internal`);
  * a closed byref and a closed pointer answered true without their element (`Internal&`, `IntS*`);
  * a closed function pointer's parameters skipped (`delegate*<Internal, void>`);
  * a closed instantiation answered by its arguments alone (`InternalG<int>`);
  * an open construction answered by its arguments alone (`InternalG<T>` over `PubG<>`'s parameter);
  * a `Void` return answered false (`delegate*<int, void>`);
  * the handler pushing the inverted answer (any guest).

### 5u: left to later stages

* Event enumeration, as 5v: `MetadataImport.Enum` of token type `0x14000000` under a TypeDef, and whatever
  `RuntimeType.GetEvents` then reaches for a type that has events (`GetEventProps`; the associates already handle an
  Event parent, `methodSemanticsForAssociation`). With it, both `DispatchProxy` guests are expected to exit 0 (measured
  by 5s with throwaway edits).
* `TypeBuilder_SetParamInfo` (`DefineParameter`), the next emit stage, after 5v.
* `RuntimeTypeHandle.GetAttributes` and `RuntimeTypeHandle_ConstructName` of the dynamic-methods class: PawPrint refuses
  both as having no metadata, but CoreCLR answers `0x101` and `(dynamicClass)` (measured).
* `TypeBuilder_DefineCustomAttribute`, which `DispatchProxy` reaches for any non-public interface or base type (question
  6); and `Expression.Compile`'s next stop, `ReflectionInvocation_CompileMethod`.
* `XmlSerializer`, which stops before `IsVisible`, at the `AssemblyNative_InternalLoad` refusal of a name with a
  processor architecture.
* `Signature.Init` of a field of a generic type definition whose type is a function pointer over the type's parameter:
  PawPrint refuses it as a signature naming `!0` in a context that instantiates nothing, where real .NET constructs the
  open function pointer type (measured above). Not specific to `IsVisible`; pinned by a parked guest in a test-only PR
  against `main`, and un-parked by the `Signature.Init` fix.
* Nested dynamic types: when an emit scope gains a NestedClass table and `DefineNestedType` runs,
  `TypeBuilderIsVisible.cs` gains a created nested builder of each visibility. The Domain rule needs no change if the
  re-read `TypeInfo` carries `DeclaringType`.
* The rest of the two docstrings of the dynamic-methods class ("no name outside a debug build"), with
  `GetAttributes` (vm/runtimehandles.cpp:642) and `ConstructName` (vm/typestring.cpp:841).
* `buildAccessLevelChain` could gather its levels through the Domain function 5u adds. That is not a reason to join the
  two rules.
* Whether the events stage is 5v here or a plan of its own.
* The internal-interface guest's un-parking by custom attributes, with its check of the attribute.

### 5u: the decisions

Taken 2026-11-14.

* **A: CoreCLR's walk, computed at each call, in three layers.** Checked against the pinned source:
  * `RuntimeTypeHandle_IsVisible` (vm/runtimehandles.cpp:709) returns `TypeHandle::IsExternallyVisible` and nothing
    else. `TypeHandle::IsExternallyVisible` (vm/typehandle.cpp:991), `MethodTable::IsExternallyVisible`
    (vm/methodtable.cpp:1500), `FnPtrTypeDesc::IsExternallyVisible` (vm/typedesc.cpp:1775) and
    `IsTypeDefExternallyVisible` (vm/siginfo.cpp:3189) are as the stage describes them. Nothing caches.
  * `5u-probe`'s output is the same on .NET 10.0.14 and 11.0.0-rc.3 (`net10.out` and `net11.out` are identical), and
    every row of the table agrees with it.
  * B is rejected. `AccessCheck.levelIsVisible` fails by design on the private and family levels, to which
    `IsVisible` must answer false, and `IsExternallyVisible` reads neither `InternalsVisibleTo` nor
    `IgnoresAccessChecksTo`. Sharing a path would make a later port of family access change `IsVisible` with no test
    of `IsVisible` in the diff.
  * C is rejected. It stores a derived fact beside the facts it derives from, on a public record of a published
    package, and the open forms need A's walk anyway. Its cost on `ConcreteType` is smaller than the options say (the
    record is private and built by `ConcreteType.make`), but nothing measured calls `IsVisible` in a loop.
  * A fourth structure was weighed: one walk in the interpreter, beside `RuntimeTypeHandleTarget.isByRefLike`, with the
    closed shapes handled in its `Closed` arm. It writes each shape's rule once, where A writes it for a closed handle
    and again for an open one. It is rejected because the closed walk reads only `LoadedAssemblies` and
    `AllConcreteTypes`, so TypeSystem is the lowest project that can hold it, and the two copies are short and each
    has its own mutants below.
* **Conditions on A.**
  * Domain holds the rule as a function of data: the visibility of each level of a nesting chain, innermost first,
    answering true exactly when every level but the last is `NestedPublic` and the last is `Public`. It is total over
    the eight values of `TypeAttributes.VisibilityMask` and fails on nothing. A second Domain function gathers that
    chain for a `TypeInfo` from its `DumpedAssembly` by `DeclaringType`.
  * The gathering function fails, naming the type, where an encloser's handle is absent from `TypeDefs`, and where the
    chain grows longer than the assembly has TypeDefs (a cycle among NestedClass rows). CoreCLR answers false to a
    failed lookup of the encloser, but for an image which it would have refused to load; a host failure is the honest
    counterpart, and this is wider than the stage's "that arm has no counterpart", which covers only a disagreement
    between the visibility bits and the row.
  * TypeSystem holds the walk over a `ConcreteTypeHandle`, in a file of its own after `ContinuationSlots.fs`. `Byref`,
    `Pointer`, `OneDimArrayZero` and `Array` ask their element. `FunctionPointer` asks every parameter type and its
    return type, and a `MethodReturnType.Void` return is visible, because `System.Void` is a public type (`void` and
    `delegate*<int, void>` are measured true). `Concrete` asks the rule of its definition and then each generic
    argument. A `Concrete` handle with no row in `AllConcreteTypes` is a PawPrint fault and fails by name.
  * The interpreter holds the walk over a `RuntimeTypeHandleTarget` as a pure function in the `RuntimeTypeHandleTarget`
    module, beside `isByRefLike` and with its parameters. The handler in `NativeRuntimeTypeQCall` decodes the target
    with `NativeCall.qCallTypeHandleToRuntimeTypeHandleTarget`, calls that function, pushes the `Int32` 1 or 0, and
    changes nothing else.
  * No arm of any of the three is a wildcard. A new case of either union must fail to compile here.
* **Open forms are answered now.** Each is one arm, each is measured, and a refusal would leave a walk that is partial
  over the cases of its own input. `GenericParameter` and `MethodGenericParameter` are true whatever their owner.
  `OpenGenericTypeDefinition` is the rule of its definition alone. `OpenConstructed` is the rule of its definition and
  then each argument, by this same walk, so an argument which canonicalisation has collapsed to a bare definition
  (`IWrap<INested<>>`) is answered as a definition, as CoreCLR answers the MethodTable of a generic type definition.
  `Composite` asks its element. An open `FunctionPointer` asks its parameter types and its return type, with `Void` as
  above.
* **The dynamic-methods class is answered true.** `CreateMinimalMethodTable` sets `tdPublic | tdSealed` on the class,
  `IsTypeDefExternallyVisible` returns at `IsTdPublic` before it reads any metadata, and both runtimes measure true for
  the class and for its array. Refusing it would repeat, in a new arm, the premise that is already wrong for
  `GetAttributes`.
  * The arm cites `CreateMinimalMethodTable` and does not call `refuseMetadataQuery`.
  * The docstrings of the `DynamicMethodsClass` case and of `refuseMetadataQuery` both say that the class has no
    attributes. 5u corrects those words, since its own arm reads one. It changes neither handler that refuses; those
    stay with "left to later stages".
* **A runtime-async continuation fails through `ContinuationType.unasked`, at whatever depth the walk meets it.** This
  is narrower than "refused by name": it is the BUG failure which every neighbouring query already gives, and it is
  right for the same reason. PawPrint refuses `GetType()` on a continuation, so no `RuntimeType` names one and the
  QCall cannot be asked. The comment on the arm records that CoreCLR's source gives false (a minimal class with no
  attributes set), so that whoever first makes the arm reachable starts from that reading and measures it.
* **The stage stays in this plan, and this reverses 5s.** 5s decided that `RuntimeTypeHandle_IsVisible` and event
  enumeration were "reflection PRs against `main`, outside this plan". The stage cites "5s's question 4" as if 5s had
  decided the opposite; that sentence is corrected below.
  * The reason to reverse is where the record is. The measurements of `DispatchProxy`'s stops are in this plan, the
    stream builds one stage at a time whichever document holds it, and a new plan for one QCall would hold forty lines
    of design and a pointer back here.
  * What 5s wanted is kept in substance. 5u's code depends on nothing of 5t's and touches none of 5t's files, so that
    it rebases onto `main` cleanly when 5t merges. The PR is titled for what it is, `Type.IsVisible` of a
    `RuntimeType`.
  * Whether event enumeration is 5v here or a reflection plan of its own is decided when it is planned. Reflection over
    events is not implemented for any assembly, which is a larger thing than one QCall. Until then "5v" in the 5u
    sections means "the events stage".
* **The internal-interface guest is added, parked, and it is not a check.** A parked case runs only when someone names
  it, and at either answer it stays parked: a wrong true moves its stop from `TypeBuilder_DefineCustomAttribute` to the
  events, and no test reads the stop. So the stage's claim that it is "the only end-to-end check" of the false answer
  is wrong.
  * What pins the false answer in CI is the internal interface's row in `TypeIsVisible.cs` and the differential
    property.
  * The guest is still worth having. It records a measured stop, it is the program which the custom-attributes stage
    un-parks, and it costs one file and one registration.
  * It must be able to tell the answers apart once it runs. With a wrong true and everything else implemented,
    `DispatchProxy` skips `IgnoresAccessChecksToAttribute`, and the proxy may still load under PawPrint and exit 0. So
    the guest also checks something that only the false answer produces, if real .NET exposes it: the attribute on the
    proxy's assembly, read through `GetCustomAttributesData`. Measure that on .NET 10 and 11 first. If real .NET does
    not expose it, the reason string says that the guest passes under either answer.
* **One feature, one PR.** The Domain rule, the two walks, the handler and their tests are one feature: `IsVisible` of
  a `RuntimeType`. The commits go in the stream's order, tests first with the failing run recorded, then Domain,
  TypeSystem and the interpreter.
* **Corrections to the other 5u sections.**
  * "Which stage" cites 5s's question 4 in support. 5s decided the opposite, as above.
  * "Under PawPrint, `5u-probe` and `5u-probe2` reach every case except `Continuation`" is not shown for the
    dynamic-methods class. The saved run of `5u-probe2` stops at `RuntimeTypeHandle.GetAttributes` (the line before
    `IsVisible`), so PawPrint has never been seen to ask `IsVisible` of that class or to build its array. The reflection
    route to the `Type` does run.
  * The class's `Name`, `(dynamicClass)`, is in no saved output: both real-runtime runs were given an argument, which
    skips that line. It is read from the source instead (vm/typestring.cpp:841). `0x101` is measured.
  * The table's row for `DefineType` with `NestedPublic` is right, but it is not why a dynamic module has no nested
    type here. PawPrint refuses `DefineNestedType` itself: `TypeBuilder_DefineType` with an encloser stops because an
    emit scope has no NestedClass table. Every dynamic type which 5u can see is top-level.
  * "Where PawPrint will answer" omits a neighbour. `NativeRuntimeMethodHandle.buildAccessLevelChain` already walks
    `DeclaringType` to the outermost type, privately, for the access check. 5u does not touch it.
  * The differential property does not drive the handler. The native method is a stub which the source generator
    names (`<_IsVisible>g____PInvoke|82_0`), with counters that differ between SDKs, and the handler holds no decision.
    The property drives the pure function over the target; the guests cover the handler.
  * Question 6's recommendation stands, with the claim about what the guest checks corrected as above.
* **The oracle is sufficient with these changes.**
  * The nesting fixture is enumerated, not drawn, and compiled once. It holds every chain of a top-level class, public
    or not, with up to three nested levels of each of the six nested visibilities: 518 types, of which 432 are
    innermost at the fourth level. Exhaustive beats sampled for a rule this small, a failure names a type and not a
    seed, and one compilation replaces one for every FsCheck case. A second, smaller forest has generic levels.
  * FsCheck then draws constructions over that fixture's types and CoreLib's, as a small tree (definition,
    instantiation, szarray, array of rank 2 or 3, pointer, byref outermost, either kind of generic parameter, function
    pointer). The host's `Type` and PawPrint's target are both built from the tree. The target goes through
    `openConstructed`, `composite` and `functionPointer`, and through concretisation where every part is closed.
  * The generator draws only what the host can build: no pointer, byref or `void` as a generic argument, no array or
    pointer over a byref, and instantiation only of definitions with no constraints. Nothing is discarded after the
    draw, so there is no skip rate to hide a thin sample. This replaces "skipped where the host's `MakeGenericType`
    rejects the constraints".
  * The host's function pointer types come from fields of the fixture, so their signatures are a fixed set: every
    signature of at most two parameters over a handful of types which the holder can name (a public type, an internal
    one, a public generic over the internal one, the holder's own parameter, and `void` as a return).
  * The sweep of CoreLib has two parts. The Domain rule is compared with the host's `IsVisible` for every TypeDef of
    `HostImage`'s CoreLib, by metadata token, with the count asserted equal to the image's. That needs no
    concretisation, and it is exact for a generic definition too, since a definition's arguments are not consulted.
    The walk is compared for each TypeDef as an `OpenGenericTypeDefinition` or a `Closed` handle; a type which
    concretisation refuses is listed by name with its reason, and the test asserts that list.
  * The Domain property runs over every chain of visibilities up to length five, exhaustively, against the model
    written as the sentence above.
  * Coverage is asserted on the fixed sample for the constructions only: a visible definition with an invisible
    argument at depth one and deeper, an invisible type under each shape, an open construction with an invisible closed
    argument, a function pointer invisible by its return type alone, and a bare definition as an argument.
  * `TypeIsVisible.cs` gains rows for a generic definition as an argument, both answers:
    `typeof(PubG<>).MakeGenericType(typeof(PubG<>))` and the same over `InternalG<>`. Measure both on .NET 10 first.
  * The dynamic-methods class gets a guest of its own, not rows in `TypeBuilderIsVisible.cs`. It reaches CoreLib's
    internals by name, and a rename there should break one small guest. It asks `IsVisible` only, never `Name` or
    `Attributes`. If the array route stops elsewhere under PawPrint, the array row leaves the guest and "as built"
    records the stop. Either way a unit test asks the pure function about the `DynamicMethodsClass` target and a
    `Composite` over it, built directly.
  * `TypeBuilderIsVisible.cs` keeps its other rows. It has no nested dynamic type, for the reason above.
  * Mutants added:
    * each of the five non-public nested levels treated as `NestedPublic`, one at a time (the stage has two of them);
    * a top-level `NotPublic` type answered true (`Internal`);
    * a closed byref and a closed pointer answered true without their element (`Internal&`, `IntS*`);
    * a closed function pointer's parameters skipped (`delegate*<Internal, void>`);
    * a closed instantiation answered by its arguments alone (`InternalG<int>`);
    * an open construction answered by its arguments alone (`InternalG<T>` over `PubG<>`'s parameter);
    * a `Void` return answered false (`delegate*<int, void>`);
    * the handler pushing the inverted answer (any guest).
  * The mutant "the argument check applied to a generic type definition" is dropped, not recorded as equivalent.
    `OpenGenericTypeDefinition` carries no arguments, so the mutant cannot be written without inventing the parameters
    it would then find visible.
* **The two findings outside the stage are correctly outside it.**
  * The dynamic-methods class's `GetAttributes` and `ConstructName`: the source confirms both. `GetAttributes` returns
    `GetAttrClass()` for any MethodTable (vm/runtimehandles.cpp:642), which is the `tdPublic | tdSealed` that
    `CreateMinimalMethodTable` set, and `TypeString` appends `(dynamicClass)` for a type with no metadata. They are two
    other queries with a helper of their own, and only private reflection over CoreLib reaches them.
  * The field of `FnHolder<>` whose type is `delegate*<T, void>`: `Signature.Init` of a field of a generic type
    definition, nothing to do with visibility. It is the worse of the two, because ordinary reflection reaches it and
    PawPrint's message asserts that CoreCLR throws `BadImageFormatException` where CoreCLR answers. It is pinned by a
    parked pure guest in a test-only PR against `main`, as 5t pinned its duplicate overrides, so that 5u's diff does
    not claim it.
* **Additions to "left to later stages".**
  * Nested dynamic types. When an emit scope gains a NestedClass table and `DefineNestedType` runs,
    `TypeBuilderIsVisible.cs` gains a nested builder of each visibility, created. The Domain rule needs no change if
    the re-read `TypeInfo` carries `DeclaringType`.
  * The test-only PR's parked guest for the field of function pointer type, and the `Signature.Init` fix which un-parks
    it.
  * The dynamic-methods class's `GetAttributes` and `ConstructName`, with their sources as cited above, and the rest of
    the two docstrings ("no name outside a debug build").
  * `buildAccessLevelChain` could gather its levels through the Domain function which 5u adds. That is not a reason to
    join the two rules.
  * Whether the events stage is 5v here or a plan of its own.
  * The internal-interface guest's un-parking by custom attributes, with the check that separates the two answers.

### 5u: as built

Built 2026-11-14 as decided above, after the measurements the decisions asked for (throwaway probe `5u-probe3`, and
`5u-probe2` rerun with no argument), each the same on .NET 10.0.14 and 11.0.0-rc.3:

| Emitted or asked | Observed |
|---|---|
| `typeof(PubG<>).MakeGenericType(typeof(PubG<>))`; the same over `InternalG<>` | true; false |
| `DispatchProxy.Create` over an internal interface: `GetCustomAttributesData` of the proxy's assembly | `IgnoresAccessChecksToAttribute` with the caller's assembly name; over a public interface, none (the guest's control exits 4) |
| under PawPrint before the handler: `MakeArrayType` over the dynamic-methods class | runs, and its element is the class; `IsVisible` of either then reaches the unimplemented QCall |
| the dynamic-methods class's `Name` | `(dynamicClass)` |

* `TypeVisibility` (WoofWare.PawPrint.Domain/TypeVisibility.fs): `isChainExternallyVisible`, the rule over a chain of
  visibilities, innermost first; `nestingChain`, which gathers it through `DeclaringType` and fails, naming the type, on
  an absent encloser or a chain longer than the assembly has TypeDefs; and `isTypeDefExternallyVisible`, the two
  composed.
* `ConcreteTypeVisibility.isExternallyVisible` (WoofWare.PawPrint.TypeSystem/ConcreteTypeVisibility.fs, after
  `ContinuationSlots.fs`), over a `ConcreteTypeHandle`, with a continuation failing through `ContinuationType.unasked`.
* `RuntimeTypeHandleTarget.isExternallyVisible` (ManagedPointerSource.fs, beside `isByRefLike`), taking the loaded
  assemblies and concrete types; `isByRefLike`'s `BaseClassTypes` parameter is not taken, since nothing here reads it.
  The two docstrings that called the dynamic-methods class attribute-less now say its attributes are set.
* The handler in `NativeRuntimeTypeQCall` matches the entry point, declaring type and signature, with the method name
  unmatched (the generated stub's), and pushes the `Int32`; it is registered in `NativeQCall`.
* Tests, committed first with the three functions as stubs that fail, so that the commit builds. Red: 11 of the 13 unit
  tests failed (at the stubs, and the property at its first case), and `TypeIsVisible.cs`, `TypeBuilderIsVisible.cs` and
  `DynamicMethodsClassIsVisible.cs` stopped at "Unimplemented native method (PInvokeImpl
  QCall!RuntimeTypeHandle_IsVisible)". The two that passed are the fixture's census and the check that every
  instantiated definition is unconstrained.
  * `TypeIsVisible.cs` (68 checks) and the two impure guests exit 0 on .NET 10 and 11, and a control of `TypeIsVisible.cs`
    with check 40 inverted exits 40 on both.
  * `TestTypeVisibility`: the chain rule against its model over all 37,448 chains of one to five levels (five are
    visible), with and without non-visibility bits set; a gathered chain; the two refusals.
  * `TestIsVisibleSameImage`: the CoreLib sweep in its two parts, over all 2,647 TypeDefs of the host's .NET 10.0.14
    CoreLib except `<Module>`, which the host's `Module.ResolveType` refuses to name (the count is asserted against the
    image's TypeDef table). Concretisation refused none of the 2,646, so the refusal list asserted is empty and the
    closed-handle sweep stays exhaustive; its peak memory is in the suite results below. The fixture's 518 chain types,
    its generic forest and its construction pool, each through the rule and the walk. The FsCheck property over drawn
    constructions, 300 fixed cases and 300 fresh, with every label reached at least 15 times in the fixed sample (the
    least, 25). The dynamic-methods class and its array, built directly.
  * After the first green run, the generator gained a fixed opening for the rarest label (4 hits in 300), and, after the
    first mutant run, visible nominals drawn as often as the whole pool and closed void-returning function pointers,
    with two more labels: `closed-args-alone` and `void-false` below had survived the property and died only to the
    pure guest.
* The parked cases, measured by naming them once the handler existed: `DispatchProxyMethodInterface.cs` and
  `DispatchProxyPropertyInterface.cs` stop at "TODO: MetadataImport.Enum does not yet support token type 0x14000000 with
  parent 0x02000002", and `DispatchProxyInternalInterface.cs` at "Unimplemented native method (PInvokeImpl
  QCall!TypeBuilder_DefineCustomAttribute)". Their reasons say so.
* The `FnHolder<>` finding is broader than function pointers: PawPrint refuses any field of an open generic type
  definition whose type mentions the definition's parameters, a plain `public T Plain;` included, with the same
  `Signature.Init` message, because `runtimeTypeForField` (`NativeSignature.fs`) reads such a field with no type
  arguments in scope. #2243 parks it as `sourcesPure/FieldTypeOfGenericDefinition.cs` (a plain `T` field, a `List<T>`
  field and the function pointer). The item in "left to later stages" means the general fix: reading a field of a
  generic definition in the definition's own context.
* Mutants, each built fresh and run against the three guests and every unit test above, after the generator's last
  change. The pure guest's exit code is the number of its first failing check. "P" is the property, "CD" and "CW" the
  CoreLib sweeps of the rule and the walk, "F" the fixture's, "M" the chain model, "K" the masked chains, "C" the gathered
  chain, "D" the dynamic-methods class's unit test:

  | Mutant | Killed by |
  |---|---|
  | `NestedPrivate` treated as `NestedPublic` | pure guest (24); P, CD, CW, F, M, K |
  | `NestedFamily` treated as `NestedPublic` | pure guest (17); F, M, K, C. No CoreLib type's chain is visible but for a family level |
  | `NestedAssembly` treated as `NestedPublic` | pure guest (20); P, CD, CW, F, M, K |
  | `NestedFamANDAssem` treated as `NestedPublic` | pure guest (23); P, CD, CW, F, M, K |
  | `NestedFamORAssem` treated as `NestedPublic` | pure guest (22); P, CD, CW, F, M, K |
  | the walk stopping at the immediate encloser, asking whether it is `Public` | pure guest (16); P, CD, CW, F, M, K |
  | `NestedPublic` accepted without the walk | pure guest (19); P, CD, CW, F, M, K |
  | a top-level `NotPublic` type answered true | pure guest (2); `TypeBuilderIsVisible.cs` (4); P, CD, CW, F, M, K |
  | a closed instantiation's arguments not checked | pure guest (28); `TypeBuilderIsVisible.cs` (8); P |
  | the argument check one level deep only | pure guest (29); P |
  | a closed instantiation answered by its arguments alone | pure guest (31); P |
  | an array answered by `System.Array`'s TypeDef rather than its element | pure guest (30); P |
  | a closed byref answered true without its element | pure guest (62); P |
  | a closed pointer answered true without its element | pure guest (60); P |
  | a closed function pointer's return type skipped | pure guest (65); P |
  | a closed function pointer's parameters skipped | pure guest (64); P |
  | a `void` return answered false | pure guest (63); P |
  | a dynamic module's created type answered true | `TypeBuilderIsVisible.cs` (4) alone: nothing else reads a dynamic module |
  | a generic parameter answered by its owner's visibility | pure guest (42); P |
  | an open function pointer answered true without its types | pure guest (68); P |
  | an open construction's arguments skipped | pure guest (40); P |
  | an open construction answered by its arguments alone | pure guest (45); P |
  | a `Composite` answered true without its element | pure guest (52); P |
  | the dynamic-methods class answered false | `DynamicMethodsClassIsVisible.cs` (2); D |
  | the handler pushing the inverted answer | pure guest (1); `TypeBuilderIsVisible.cs` (3); `DynamicMethodsClassIsVisible.cs` (2) |

  In the first run, against the earlier generator, `NestedFamily` was killed by the property too, and the property did
  not kill `closed-args-alone` or `void-false`; every other row was as above.
* Suites at the tip, built from the whole solution, each run on its own: `Guest` (234), `GuestSolo` (2), `GuestPure`
  (1180) and `GuestImpure` (278), all passing. The default suite first failed one test, "every fixture declares whether
  it runs in parallel", which named `TestIsVisibleSameImage`; with that declared, it passed (5583 passed, 37 skipped).
  `TestIsVisibleSameImage` alone peaks at 1.3 GB resident (`/usr/bin/time -l`), well under the 8 GiB cap, so the
  closed-handle sweep of CoreLib stays exhaustive.

### 5v: event enumeration

**Which stage.** 5u's list puts event enumeration next, and leaves open whether it belongs here. Checked on this branch
(5u's, 47c41e4b8): `MetadataImport.Enum` refuses token type `0x14000000` (`Native/NativeMetadataImport.fs:1175`),
`MetadataImport.GetEventProps` has no handler, `MetadataImport.GetName` refuses an Event token (its comment at `:1409`
says the arm comes with the enumeration), and `TypeBuilder_DefineEvent` has no handler. Reflection over events is
therefore refused for every assembly, compiled ones included, and `DefineEvent` could not be read back if it were
written.

The question the stage turns on is whether `DispatchProxy` needs the emit side. It does not, for the two parked
guests. Read from `ProxyBuilder.AddInterfaceImpl`'s IL (`WoofWare.PawPrint.IlDump` over the .NET 10.0.14
`System.Reflection.DispatchProxy.dll`): it calls `GetRuntimeEvents` on the interface twice, and calls
`TypeBuilder.DefineEvent`, `SetAddOnMethod`, `SetRemoveOnMethod` and `SetRaiseMethod` once for each event it finds, so
an interface without events reaches only the enumeration, and gets none back. Measured under PawPrint with a throwaway
arm for the Enum alone (reverted): `DispatchProxyMethodInterface.cs` exits 0. The emit side is reached only by an
interface that has an event (below).

So 5v is the read side: the three primitives `RuntimeType.GetEvents` reaches. The emit side, `TypeBuilder_DefineEvent`
with its tables and the Event case of the association, is the next stage, 5w, sketched under "left to later stages"
with what has been measured for it. Doing both at once would be a 5s-sized emit feature (new tables, a derived map
and pointer table, a writer, an extension rule, a differential property) stacked on a reflection feature that every
compiled assembly reaches without it.

**What CoreCLR does.** Read from the pinned source (`$DOTNET_RUNTIME_SRC_NET10`):

* `RuntimeType.RuntimeTypeCache.PopulateEvents` (RuntimeType.CoreCLR.cs:1142) walks the class hierarchy, or only the
  interface itself for an interface, after stepping a generic parameter up to its base type. For each type with a
  token it calls `MetadataImport.EnumEvents`, which is `MetadataImport_Enum` with `mdtEvent` and the TypeDef
  (MdImport.cs:374). Under a name filter it reads each event's name through `MetadataImport.GetName`. It constructs a
  `RuntimeEventInfo` per token, drops an inherited private one, and drops a later event whose name it has already
  seen. All of that is managed code, which PawPrint interprets.
* `MetadataImport_Enum` (vm/managedmdimport.cpp:539) takes its generic arm for `mdtEvent`: `EnumInit` then `EnumNext`.
  Read-only metadata (md/runtime/mdinternalro.cpp:410) and read-write metadata (md/enc/mdinternalrw.cpp:1195) both find
  the type's EventMap row and give its run, through `EventPtr` when the module has one, in that order. No lookup hash
  is involved: `EventMap` has no `Add*ToHash` (md/inc/metamodelrw.h:631-734). A type with no EventMap row gives none.
* `RuntimeEventInfo`'s constructor (RuntimeEventInfo.cs:28) calls `MetadataImport.GetEventProps`, an FCall
  (vm/ecalllist.h:110, vm/managedmdimport.cpp:279) which returns the row's `#Strings` name and its raw `EventFlags`,
  and then `Associates.AssignAssociates` with the Event token, which `methodSemanticsForAssociation` already answers
  (5s), including the read-write rule from 25 rows. The Event row's `EventType` column is read by nothing:
  `EventInfo.EventHandlerType` (EventInfo.cs:43) is the first parameter of the add method that derives from
  `Delegate`, and is a `NullReferenceException` when there is no add method.
* `MetadataImport.GetName` of an Event token is `GetEventProps` for the name alone (vm/managedmdimport.cpp:242).

So three primitives: the enumeration, `GetEventProps`, and the Event arm of `GetName`. Everything else `GetEvents`,
`GetEvent`, `AddEventHandler` and the accessors do is managed code over those and over the existing associates.

**Measured on .NET 10.0.14** with a throwaway probe (`5v-probe`), every line identical on 11.0.0-rc.3 (rolled forward
from the net10.0 build). `Pub` has a public, a private, a public static, a protected, an attributed and a
custom-accessor event; `Derived : Base` has its own event and a `new` event hiding one of `Base`'s, and `Base` a public,
a private and a static one; `G<T>` and a struct have one each; `IEv` is an interface with one, and `ExplicitImpl`
implements it explicitly:

| Asked | Observed |
|---|---|
| `GetEvents(all)` of an interface with methods only, `object`, `int[]` | none |
| `Pub.GetEvents()`; `GetEvents(all)` | the four public ones in token order, the static one included; all six (`0x14000002` to `0x14000007`) in token order. Every `Attributes` is 0, `IsSpecialName` false, `IsMulticast` true, `RaiseMethod` null and no other methods |
| `GetEvent("E1")`, `GetEvent("e1", IgnoreCase \| Public \| Instance)`, the private one by name without and with `NonPublic`, a missing name | found; found; null and found; null |
| `AddEventHandler`, the event raised, `RemoveEventHandler`, raised again; then the custom-accessor event's `AddEventHandler` | the handler ran once; the custom `add` ran |
| `Derived.GetEvents()`, and with `all` | `DerivedE`, `Derived.Shadowed` (`Action<int>`), `BaseE`: `Base.Shadowed` is dropped by name, `BasePriv` as an inherited private, `BaseStatic` without `FlattenHierarchy` |
| the same with `FlattenHierarchy`; with `DeclaredOnly` | `BaseStatic` added, with `ReflectedType` `Derived`; only the two declared |
| `G<int>`'s event; `G<>`'s | `Action<Int32>`; `Action<T>`, both token `0x1400000E` |
| `IEv`'s; `ExplicitImpl`'s with `all` | `Changed`; `IEv.Changed`, with accessors `IEv.add_Changed` and `IEv.remove_Changed` |
| the attributed event's `GetCustomAttributes` and `GetCustomAttributesData` | the attribute's argument; one entry |
| `GetEvents(all)` of `T` in `Holder<T> where T : Derived`; of an unconstrained parameter; of `Derived[]` | `Derived`'s three, with `ReflectedType` `T`; none; none |
| a dynamic type deriving from a compiled class with a public, a private and a protected event (`5v-emit nodyn`) | none declared; inherited `Pub` and `Prot`, with `ReflectedType` the dynamic type |

No compiler writes a raise method, an other method, event flags or two events of one name, so a second probe writes an
image with `PersistedAssemblyBuilder` (`5v-fabwrite`) and a third, compiled against it, reflects over it
(`5v-fabread`). The type has 24 events (more than `MetadataImport.Enum`'s 16-entry inline buffer) and 32
MethodSemantics rows:

| Written | Observed on .NET 10.0.14 and 11.0.0-rc.3 |
|---|---|
| `Ev` with flags `SpecialName \| RTSpecialName`, an add, a remove, a raise, and other methods `otherB` then `otherA` | `Attributes` `0x0600` (an image on disk keeps the flag that `DefineEvent` clears, below), `IsSpecialName` true; the raise method; others `otherB, otherA`; `AddEventHandler` then the raise method through `Invoke` runs the handler once |
| two events named `Dup`, each with its own add | only the first is listed, and `GetEvent("Dup")` is it |
| `Many` with adds `a1`, `a2` and others `o1`, `o2`, `o3`, among 32 rows | add `a2`, others `o1, o2, o3`: RID order, as 5s measured for a property on disk |

Under PawPrint at this branch's tip, every scenario reaches the Enum TODO, since each asks `GetEvents` of a type with a
token. With throwaway arms for the three primitives, each reading the image's metadata (`TypeDefinition.GetEvents`,
`GetEventDefinition`), every scenario's output, the probe's `all` run and `5v-fabread`'s included, is byte-identical to
.NET 10's. With the Enum arm alone, a proxy over an interface with an event stopped at "Unimplemented native method
(InternalCall) ... MetadataImport::GetEventProps"; the `GetEventProps` and `GetName` arms were then added together (the
second because `PopulateEvents`' name filter calls it and `GetName` refuses an Event token; that stop was not measured
on its own). With all three, nothing further stopped. So the stage is those three arms and nothing else on the read
side. A control, with the arms reverted and the App rebuilt: `DispatchProxyMethodInterface.cs` stops at the Enum TODO
again.

**What `DispatchProxy` reaches next.** Measured with the three throwaway arms (`5v-proxy`, and the parked guests),
each program exiting 0 on .NET 10.0.14 and 11.0.0-rc.3:

| Program | Under PawPrint |
|---|---|
| `DispatchProxyMethodInterface.cs` | exits 0 (with the Enum arm alone) |
| one proxy over an interface with a read-write property (`5v-proxy once-prop`) | exits 0 |
| `DispatchProxyPropertyInterface.cs` | its first four checks pass, then the second `DispatchProxy.Create` (line 100) stops at "TODO: a dynamic method's Newobj names DynamicScope entry 4 (token 0x06000004), but newobj naming a DynamicMethod is an InvalidProgramException on real .NET, while newobj naming a reflected constructor constructs the object (both measured); PawPrint does neither yet" |
| two proxies over a method-only interface (`5v-proxy twice-method`) | the same stop |
| `typeof(StringBuilder).GetConstructor(int).Invoke` twice, with `IsDynamicCodeSupported` on (`5v-proxy ctor-twice`) | the same stop, at entry 6 |
| the same without the switch in the runtimeconfig (`5v-ctor ctor-twice`) | exits 0 |
| a proxy over `INotify` (an event and a method), and over `IDerivedEv : IBaseEv` with the event on the base | "Unimplemented native method (PInvokeImpl QCall!TypeBuilder_DefineEvent)". On .NET the first proxy's type has `Changed`, token `0x14000001`, with `add_Changed` and `remove_Changed` |

The second-`Create` stop is not about events or properties. `MethodBaseInvoker` interprets a constructor's first
reflection call and emits an invoke stub for the second when dynamic code is supported, and the stub is `newobj` of the
reflected constructor (`InvokerEmitUtil.cs:225`), which `IlDecoding.fs:107` refuses. A guest with
`IsDynamicCodeSupported` on, as every `TypeBuilder` guest has, reaches it as soon as it reflection-invokes one
constructor twice; no test pins it. `DispatchProxyPropertyInterface.cs`'s parked reason says it "exited 0 once
`TypeBuilder_DefineProperty` was implemented (measured with stage 5s)", but what 5s measured was its survey's single
proxy: the guest's second `Create` was added with the guest (#2238), and has never been run past the events. 5v
therefore un-parks only `DispatchProxyMethodInterface.cs`.

### 5v: the options

Every option implements the same three primitives in `NativeMetadataImport`, refuses a parent that is not a TypeDef
of the assembly, and changes nothing about the associates, which 5s's `methodSemanticsForAssociation` already reads
for an Event parent. They differ in where the event facts are read from.

**A. Read the image at each call, as the property arms do.** `eventDefinitionsForTypeDefinition`, beside
`propertyDefinitionsForTypeDefinition` (`:339`), validates the TypeDef and returns `TypeDefinition.GetEvents` as tokens,
which follows the EventMap run and an `EventPtr` if there is one. A `GetEventProps` arm, beside `GetPropertyProps`,
bounds-checks the row as `propertyDefinition` does and writes the raw flags and the `#Strings` name. `GetName` gains an
Event arm from the same bounds-checked row, and its comment at `:1409` goes. The three functions, and
`methodSemanticsForAssociation`, are `internal` rather than `private`, so that the tests (which `InternalsVisibleTo`
already admits) can call them.

* Correctness: every row above, measured with exactly this structure.
* Blast radius if wrong: reflection over events, of every assembly. Nothing reflects over events today, so nothing
  that runs now can change.
* Reversibility: three arms and one widened visibility; nothing existing changes shape.
* Information: the image is the only source, so a dynamic module's events are read from the image current at the call,
  as its properties and associates are.
* Size: the arms, the guests, the fabricated test, the property.

**B. Answer from Domain's parsed events.** `TypeInfo.Events` (`TypeInfo.fs:157`) already holds each type's
`EventDefn`s (handle, name, flags), parsed at load, and today only IlDump reads them. The Enum maps a type's
`Events`; `GetEventProps` and `GetName` need an Event token's row, so `DumpedAssembly` gains an `Events` index beside
`Fields`, and `typeOwningEvent`'s scan of every TypeDef (`:869`, used by `GetParentToken`) becomes a lookup.

* Correctness: the same answers, if the parsed index is current.
* Information: one parse of the table. But properties are read from the image directly, so two sibling tables would
  be read two ways; making them agree means moving properties too, a migration this stage does not need.
* Blast radius: a public record of a published package grows a field, and every builder of a `DumpedAssembly` (the
  reader, the re-read of a dynamic module's image, fixtures) must fill it. For a dynamic module, once 5w adds events,
  the index must be rebuilt with every image, or a type created after the index was built has no events.
* Reversibility: removing the field later is a breaking change to Domain.

**C. The Enum arm alone.** The smallest change that un-parks `DispatchProxyMethodInterface.cs`, whose interface has no
events. Any type with an event then stops at `GetEventProps` rather than at the Enum. It leaves the feature partial: the
three primitives are one feature, `GetEvents` of a type that has events, and the other two are a dozen lines each.

**Recommended: A.** It is the structure the property arms already have, it reads the image that the associates read,
and it needs no Domain change. B's index has no measured need and makes sibling tables disagree in how they are read. C
un-parks one guest and leaves every type with an event refused one primitive later.

Open questions:

1. A, B or C.
2. Read side alone as 5v, emit side as 5w (recommended), or both in one stage. The read side un-parks one guest and
   reflects over every compiled assembly's events; the emit side is 5s's shape again, and its first guest needs the
   read side to read anything back.
3. This plan, or a plan of its own for reflection. Recommended: this plan, as 5u decided for `IsVisible`, since the
   `DispatchProxy` measurements are here and 5w is emit. The PR is titled for what it is: reflection over events.
4. What comes after 5v: 5w (`DefineEvent`, which unblocks a proxy over an interface with an event), or the reflected
   constructor named by an invoke stub's `newobj` (which un-parks `DispatchProxyPropertyInterface.cs`, and every
   `TypeBuilder` guest that constructs one type twice by reflection). The second is not emit, and not specific to
   `DispatchProxy`; whether it belongs in this plan is the same question as 3.
5. Whether to pin that `newobj` stop now with a parked impure guest (`ConstructorInfo.Invoke` twice with
   `IsDynamicCodeSupported` on, measured above) in a test-only PR against `main`, as 5t and 5u pinned their findings
   outside the stage. Recommended: yes.
6. Whether the guest of a proxy over an interface with an event is added in 5v, parked at `TypeBuilder_DefineEvent`
   (measured), so that 5w has its program waiting. Recommended: yes; it costs a file and a registration.

### 5v: the oracle

Written first, and seen to fail on this branch before the arms exist:

* `TypeEvents.cs` in `sourcesPure` (registered by `TestPureCases.fs`, so the real-runtime oracle runs it too), with a
  check number for every compiled row of `5v-probe`'s table: names, tokens, `Attributes`, `EventHandlerType`, the
  accessors' names, `DeclaringType` and `ReflectedType` of each event listed; the binding-flag cases; the name filter
  both ways and case-insensitively; `AddEventHandler` and `RemoveEventHandler` with the raise counted, and the custom
  accessor; the hierarchy cases (the hidden `Shadowed`, the dropped private, `FlattenHierarchy`, `DeclaredOnly`); the
  generic instantiation and definition; the struct; the interface and the explicit implementation; the attribute; the
  generic parameters and the array. It returns the number of the first failing check. The probe's output is the same
  on .NET 10 and 11; the guest's exit code is to be confirmed on both before committing.
* `TypeBuilderInheritedEvents.cs` in `sourcesImpure` (`IsDynamicCodeSupported` on): `5v-emit nodyn`, a dynamic type
  deriving from a compiled class with events, so that the Enum is asked of a dynamic module's TypeDef (none) and then
  of the base's.
* `TestFabricatedEventAccessors`, beside `TestFabricatedPropertyAccessorOrder` and written the same way
  (`PersistedAssemblyBuilder`, then the guest under PawPrint and on real .NET): `5v-fabwrite`'s type, with every row of
  its table checked. It is the only check of a raise method, of other methods, of flags, of two events of one name, of
  a type with more than 16 events (the Enum's long-result path), and of an event's accessors in RID order among 25 or
  more rows of an image on disk.
* `DispatchProxyMethodInterface.cs` is registered as passing. `DispatchProxyPropertyInterface.cs` stays parked with
  the second-`Create` stop as its reason, corrected as above. If question 6 is taken, `DispatchProxyEventInterface.cs`
  (`5v-proxy event`, with checks of the proxy's events) is parked at `TypeBuilder_DefineEvent`.
* A same-image property in the test host (`TestEventsSameImage`), over the internal functions of option A, against host
  reflection of the same image:
  * every TypeDef of `HostImage`'s CoreLib, compared by token, the count asserted equal to the image's TypeDef table:
    the enumeration is the tokens of host `GetEvents(all | DeclaredOnly)` in order, and each event's name, flags,
    add, remove, raise and other methods (through `methodSemanticsForAssociation`) are the host's. CoreLib,
    System.ObjectModel and System.Console together hold only 20 types with events, 45 events, and no raise method, other
    method or flag (counted with `5v-probe corelib`), so the sweep mostly checks that every other type gives none;
  * an FsCheck property over images written by `PersistedAssemblyBuilder` in the test host and read back with
    `Assembly.read`: a few types, each with up to five events of distinct names (the host hides a repeated name, so
    repeats stay in the fabricated test), flags drawn from all sixteen bits, and accessors drawn with repetition
    (several adds, raises and other methods on one event) so that the table reaches 25 rows; the host loads the same
    bytes into a collectible `AssemblyLoadContext`. The fixed sample asserts its coverage: an event with a raise method,
    one with other methods, one with two adds, a type with more than 16 events, and an image with 25 or more rows.
  The property drives the functions, not the handlers' reading of their arguments, which only the guests cover.
* Mutants, each run against the guests, the fabricated test and the property:
  * the Enum answering none for every type (`TypeEvents.cs`; not `DispatchProxyMethodInterface.cs`, whose interface
    has no events);
  * the Enum giving the next TypeDef's events (`Pub`'s first check; the sweep);
  * the Enum answering for an image on disk only, refusing a dynamic module (`TypeBuilderInheritedEvents.cs`);
  * the long-result path dropped, so that a type with more than 16 events loses the rest (the fabricated test; the
    property's large type);
  * `GetEventProps` reading the neighbouring row (`TypeEvents.cs`);
  * `GetEventProps` clearing `RTSpecialName`, as `DefineEvent` does on the emit side (the fabricated `0x0600`; the
    property's flags);
  * `GetEventProps` writing the flags into the name's slot and the name into nothing, or the two out-pointers swapped
    (`TypeEvents.cs`);
  * the `GetName` arm giving the neighbouring row's name (`TypeEvents.cs`'s name filter);
  * `methodSemanticsForAssociation` encoding an Event parent with the Property tag (every add is lost: `TypeEvents.cs`);
  * the read-write reversal applied to an Event parent on disk (the fabricated `Many`).

### 5v: left to later stages

* 5w, `TypeBuilder_DefineEvent`: an Event table, a derived EventMap and EventPtr, and `HasSemantics.Event` (tag 0) in
  `TypeBuilder_DefineMethodSemantics`, whose refusal of an Event association (`NativeRuntimeTypeBuilder.fs:456`) goes.
  Read from the source, it is 5s's shape with these differences:
  * `RegMeta::_DefineEvent` (md/compiler/regmeta_emit.cpp:1256) searches only under `MDDupEvent`, which neither
    `MDDupDefault` nor vm/assembly.cpp:88 sets; it adds the map row before the Event row, and
    `AddEventToEventMap` (md/enc/metamodelrw.cpp:4927) is `AddPropertyToPropertyMap` with the event tables, so the map
    order and the pointer rule are 5s's.
  * `_SetEventProps1` (:1346) clears `evReservedMask`, which is `RTSpecialName` alone (0x0400, inc/corhdr.h:544), not
    5s's `0xF400`, and keeps the low sixteen bits, except that `0xFFFFFFFF` leaves the flags unset.
  * The row has an `EventType` column, a TypeDefOrRef from `GetTypeTokenInternal`, which may add a TypeRef or TypeSpec
    row before the QCall. No reflection API reads the column.
  * `DefineEventCore` checks for a leading NUL before the QCall (RuntimeTypeBuilder.cs:1433), unlike
    `DefinePropertyCore`, so such a name consumes no token.

  Measured on .NET 10.0.14 and 11.0.0-rc.3 alike (`5v-emit`; the two outputs are identical):

  | Emitted | Observed |
  |---|---|
  | `A.DefineEvent("E1", Action)`, `B.DefineEvent("F1", SpecialName \| RTSpecialName, EventHandler)`, `A.DefineEvent("E2", Action<int>)` | `0x14000001` to `0x14000003`; `E2`'s `DefineEvent` made the TypeSpec `0x1B000001`, which `GetTypeMetadataToken(typeof(Action<int>))` then returns, and `Func<int>` is `0x1B000002` |
  | `F1`'s flags | `0x0200` on the created event |
  | `Dup` twice | `0x14000004` and `0x14000005`; reflection lists the first |
  | `"\0x"`; a null event type; then `AfterNul` | `ArgumentException`; `ArgumentNullException`; `AfterNul` is `0x14000006`, so neither consumed a token |
  | flags `(EventAttributes)(-1)`; `0x7FFF0600` | `0x0000`; `0x0200` |
  | `Weird` with event type `int` and an add method taking `Action` | `EventHandlerType` is `Action`: the column is not read |
  | an event with no add method | `EventHandlerType` throws `NullReferenceException`; the event is not listed by `GetEvents()` |
  | a property `P` (RID 1) given its getter, then `E1` (RID 1) given remove, add, raise, `other2`, `other1` | `P`'s getter gives 11; `E1`'s accessors as set, others `other2, other1`; `AddEventHandler` then the raise method through `Invoke` runs the handler once |
  | an event given adds `a1`, `a2` and others `o1`, `o2`, `o3`, in modules of 5, 24 (either side of the other rows), 25 (either side) and 26 MethodSemantics rows | below 25: add `a2`, others `o1, o2, o3`; from 25: add `a1`, others `o3, o2, o1` |
  | 20 property rows, then that event's five rows | reversed: property and event rows count together |
  | `DefineEvent`, and `SetAddOnMethod`, after `CreateType` | `InvalidOperationException` each |
  | an interface's event with abstract accessors | listed, with its accessors |

  The property-then-event case is the guest-level killer of 5s's surviving mutant, the MethodSemantics sort keyed on
  the RID with the tag dropped: `P`'s row (coded 3) is added before `E1`'s (coded 2), so the mutant leaves them out of
  order, and SRM refuses the image. The 5w guests are this table, `DispatchProxyEventInterface.cs` (question 6), and
  5s's differential property extended to events.
* The reflected constructor named by an invoke stub's `newobj` (`IlDecoding.fs:107`), which un-parks
  `DispatchProxyPropertyInterface.cs` (question 4), with its pinning guest (question 5).
* `typeOwningEvent` (`:869`) scans every TypeDef for `GetParentToken`. Nothing measured asks that of an event; it stays.

### 5v: the decisions

Taken 2026-11-14.

* **A: three arms in `NativeMetadataImport`, each reading the image at the call.** Checked against the pinned source:
  * `PopulateEvents` (RuntimeType.CoreCLR.cs:1141-1235), `RuntimeEventInfo`'s constructor (RuntimeEventInfo.cs:28),
    `MetaDataImport::GetName` and `GetEventProps` (vm/managedmdimport.cpp:216, :279), `MetadataImport_Enum`'s generic arm
    (:539), and both `EnumInit` arms (md/runtime/mdinternalro.cpp:410, md/enc/mdinternalrw.cpp:1195) are as the stage
    describes them. `FindEventMapFor` (md/runtime/metamodel.cpp:1003) is a linear scan that takes the first matching
    row, as SRM's is.
  * `5v-probe`, `5v-emit` and `5v-fabread` each give the same output on .NET 10.0.14 and 11.0.0-rc.3, and
    `5v-probe`'s and `5v-fabread`'s output under the throwaway arms is byte-identical to .NET 10's.
  * B is rejected. `TypeInfo.Events` already holds each type's events, so B's Enum arm needs no new index; the index
    is needed only to answer `GetEventProps` and `GetName` by token. That is a new field on a public record of a
    published package, to save a bounds check and one row read, and it would leave Property and Event read two ways.
  * C is rejected. The stop it leaves, `GetEventProps`, is one call later for every type that has an event.
  * A fourth structure was weighed: the Enum from `TypeInfo.Events`, and the two row reads from the image. It is
    rejected because the three arms would then read two sources, and a dynamic module's answer would depend on which
    of the two was rebuilt last. The two sources are instead held to agree by a test (below).
* **Conditions on A.**
  * The Enum arm refuses a parent that is not a TypeDef, and a TypeDef absent from `TypeDefs` (row 0 included), by
    name, as the property arm does. `PopulateEvents` screens a null token before it calls.
  * One function reads an Event row from a raw token: it refuses a token that is not an Event, and a row below 1 or
    past the table's length, and returns the `#Strings` name and the raw sixteen-bit flags. `GetEventProps` and the
    Event arm of `GetName` both call it, so that the two cannot disagree about a name.
  * The flags are written as the column holds them. `RTSpecialName` is not cleared and nothing is masked: an image
    on disk keeps what was written (`0x0600` and `0xFFFF` are both measured below).
  * Nothing reads `isDynamic` for the Enum. No lookup hash covers EventMap, and a dynamic module has no Event rows
    until 5w.
  * No function changes visibility. The stage proposed making four functions `internal` for the tests; the tests
    drive the handlers instead (below), so the functions stay `private`.
  * `GetName`'s refusal message names the four token kinds it now answers, and its comment about the missing Event
    arm goes.
* **Read side alone.** The emit side is 5s's shape again (tables, a derived map and pointer table, an extension rule,
  a differential property), and its first guest reads back through these three arms. Stacking the two would put an
  emit feature in the diff of a feature that every compiled assembly reaches without it.
* **The stage stays in this plan.** 5u left this open because reflection over events is larger than one QCall. As
  planned it is three arms in one file, with nothing new in Domain or TypeSystem, and the `DispatchProxy`
  measurements which motivate it are here. The PR is titled for what it is, reflection over events.
  * The code depends on nothing of 5u's. The un-parking of `DispatchProxyMethodInterface.cs` does: without
    `RuntimeTypeHandle_IsVisible` that guest stops one call earlier. So 5v branches from `main` once #2244 merges. If
    #2244 is still open when 5v is ready, 5v is stacked, the four suites are run locally at its tip, and the PR body
    says so.
* **After 5v: the reflected constructor named by an invoke stub's `newobj`, then 5w.** The `newobj` stop is reached
  by any guest with dynamic code supported which constructs through one `ConstructorInfo` twice, and so by any
  program which makes two proxies of one type. `DefineEvent` is reached only by a proxy over an interface that has an
  event. The `newobj` stage un-parks `DispatchProxyPropertyInterface.cs`, whose reason string has promised since 5s
  that the events would un-park it.
  * It is planned in this plan, under a heading named for what it is and with no 5-number, since it is not an emit
    stage. "5w" keeps meaning `DefineEvent` wherever the 5v sections use it.
  * Its planning starts by measuring, with a throwaway arm, what `DispatchProxyPropertyInterface.cs` and
    `5v-proxy twice-method` reach past the `newobj`. Nothing has run past it. If more than one feature stands
    behind it, 5w goes first and the `newobj` stage is planned again after it.
  * 5w is `DefineEvent`, ahead of `TypeBuilder_SetParamInfo`, which 5u's list named as the next emit stage. The
    reasons are that `DefineEvent` has a measured consumer and a guest waiting, and that it is where 5s's surviving
    mutant dies. `SetParamInfo` follows it.
* **The `newobj` stop is pinned now, in a test-only PR against `main`.** One impure guest, parked:
  `ConstructorInfo.Invoke` twice on a class of the guest's own, with a different argument each time, checking both
  objects. It is measured to exit 0 on .NET 10 and 11 before it is committed, and measured under PawPrint to stop at
  the `newobj` refusal. Its reason string quotes the refusal without the DynamicScope entry number, which varies
  with the guest (entries 4 and 6 are both measured). `5v-proxy ctor-twice` used `StringBuilder`; the guest uses its
  own class so that a change to CoreLib cannot move the stop.
* **The event-interface guest is added in 5v, parked, and it is not a check.** As 5u decided for the
  internal-interface guest: a parked case runs only when someone names it, and it records a measured stop.
  * It holds one `DispatchProxy.Create`, so that the `newobj` stop cannot stand between it and `DefineEvent`
    whichever stage comes first. The interface has an event of its own and inherits a second from a base interface,
    which covers both of `5v-proxy`'s event scenarios with one proxy type.
  * It checks the add and remove calls arriving at `Invoke`, and the proxy type's events by name, handler type and
    accessor names. Measure it on .NET 10 and 11 first, and measure the stop under PawPrint by naming the case.
* **Corrections to the other 5v sections.**
  * The fabricated image has 31 MethodSemantics rows, not 32, and `Many` has one add row, not two (read back with
    SRM, `5v-decider-census/fab.fsx`). `PersistedAssemblyBuilder`'s `EventBuilder` keeps one add, one remove and one
    raise method, and a second `SetAddOnMethod` or `SetRaiseMethod` replaces the first (measured again in
    `5v-decider-census/flags.fsx`: five events given an add and two raises make ten rows). So "add `a2`: RID order"
    shows nothing about two adds on disk. What shows RID order in an image of 25 or more rows is `Many`'s other
    methods, `o1, o2, o3`, and that still kills the reversal mutant.
  * For the same reason the property cannot draw "several adds, raises" on one event, and its coverage requirement
    of "one with two adds" cannot be met. Both are struck (below). Two adds on one event are a dynamic module's case,
    measured in `5v-emit`, and belong to 5w.
  * "No compiler writes ... a type with more than 16 events" is not so: C# compiles a class of seventeen events. The
    fabricated test is therefore not the only possible check of the Enum's long result, and the pure guest and the
    unit fixture each gain one (below). What is true is that no image of the 10.0.14 shared framework has such a
    type: the most is twelve (`System.Net.WebClient`).
  * "The property drives the functions, not the handlers' reading of their arguments, which only the guests cover"
    overlooks `TestNativeMetadataImport`. That fixture already drives these handlers through
    `NativeDispatch.tryExecute`, with host reflection of the same image as its oracle: `invokeEnum`, `invokeGetName`,
    `invokeGetPropertyProps` and `invokeEnumAssociates`, with tests of each refusal and of the long result.
  * Two existing tests there change, and the PR says so. "`MetadataImport GetName` rejects a token of an unsupported
    kind" expects the old message's text. The comment on "`MetadataImport Enum` reports an event's accessors as
    method and semantics pairs" says that no guest can reach an Event token; that becomes false, and only the comment
    changes.
  * The census line's `dupnames` is zero by construction: the host's `GetEvents` has already dropped a repeated name
    before the probe counts. Counted again with SRM over every image of the devshell's 10.0.14 shared framework
    (`5v-decider-census`): no type repeats an event name, no event has a raise method, an other method or a flag, and
    no image has an EventPtr. CoreLib alone has 12 types with events and 32 events; the stage's 20 and 45 are CoreLib,
    System.ObjectModel and System.Console together.
  * "Nothing reflects over events today, so nothing that runs now can change" is right for guests, and was checked
    for the parked list: only the two `DispatchProxy` reasons quote token type `0x14000000`.
  * "5v: left to later stages" does not carry forward 5u's list. The items of that list which 5v does not close
    still stand: `TypeBuilder_SetParamInfo`, `TypeBuilder_DefineCustomAttribute`, `XmlSerializer`, the
    dynamic-methods class's two queries, nested dynamic types, and the `Signature.Init` fix.
  * Option B's "needs an `Events` index" applies to the two row reads only, as above.
  * The `GetName` stop "was not measured on its own". It is measured in 5v by the mutants that remove one arm at a
    time (below).
* **The oracle is sufficient with these changes.**
  * The handler tests go in `TestNativeMetadataImport`, in that fixture's style, and its source gains the event
    shapes: a class with a public, a private, a static, a protected and a custom-accessor event; a base and a
    derived class; a class of seventeen events; an interface with one. Each answer is compared with host reflection
    of `fixture.Image`. The tests: the Enum's tokens in declaration order; only a type's own events; none for a type
    with fields only, and none for an empty type; the long result at seventeen; `GetEventProps` and `GetName` for
    each event; the Enum's refusal of a parent that is not a TypeDef and of an absent TypeDef; and the row read's
    refusal of a token that is not an Event and of three absent rows (`0x14FFFFFF`, the first row past the end, and
    row 0), through both `GetEventProps` and `GetName`.
  * The sweep and the FsCheck property also drive the handlers, through the same `invoke*` helpers and a new
    `invokeGetEventProps`. The fixture's state already holds CoreLib; a drawn image is added to a state as the
    fixture adds its own. This needs no stub for the tests-first commit, since the handlers refuse until the arms
    exist. If a drawn case proves too slow to run a few hundred of, the property alone falls back to `internal`
    functions as the stage proposed, and "as built" records the measured cost.
  * The sweep of CoreLib compares, for every type the host's `GetTypes` returns, the Enum with the tokens of the
    host's `GetEvents(all | DeclaredOnly)`, then each event's name, flags and associates. The count asserted is the
    TypeDef table's less `<Module>`, which the host does not return and which the Enum alone is asserted to give none
    for. It also asserts that each `TypeInfo.Events` holds the same handles in the same order, so that IlDump's
    reading and reflection's cannot drift apart. It is a check that nearly three thousand types give none and twelve
    give the right run; it checks no flag, no raise method and no long result, as the stage says.
  * The property's images are written by `PersistedAssemblyBuilder` as interfaces with abstract accessors, which the
    host loads into a collectible context with no IL (measured, `flags.fsx`). Each event draws its flags from all
    sixteen bits, each of add, remove and raise present or absent, and zero to four other methods. Types with no
    events are drawn between types that have them. The writer keeps every flag bit and the host reports it
    (`0xFFFF` written, `0xFFFF` read; an `EventAttributes` of `-1` is also written as `0xFFFF`, unlike a dynamic
    module's `DefineEvent`).
  * Coverage asserted on the fixed sample: an event with a raise method; one with other methods; one with no
    accessor at all; a flag bit outside `0x0600`; bit 15; a type with more than 16 events; a type with none between
    two that have some; the last type of the image having events; an image of 25 or more MethodSemantics rows.
  * The fabricated image gains an event with flags `0xFFFF`, so that a guest sees a flag outside `0x0600`. A unit
    test asks the Enum for the fabricated type's tokens and expects both `Dup` rows. That expectation is read from
    `EnumInit`, not measured: `PopulateEvents` drops the second by name whatever the Enum returns, so no guest can
    tell an Enum that returned one `Dup` from an Enum that returned two.
  * `TypeEvents.cs` gains a class of seventeen events (count and order), a class deriving from `Progress<int>`
    (`ProgressChanged`, declared by a CoreLib type and reflected through a guest type, with handler type
    `EventHandler<int>`), and an interface deriving from an interface with an event (none, since an interface lists
    only its own). Measure each on .NET 10 and 11 first. If the `Progress<int>` row stops elsewhere under PawPrint,
    it leaves the guest and "as built" records the stop.
  * The guest checks tokens as relations, not literals: a type's events are consecutive and ascending in
    declaration order, and an instantiation's token equals its definition's. A literal would renumber with every
    type added to the guest. The unit fixture compares literal tokens with the host's.
  * The guest's exit code is confirmed on both runtimes before the commit, and a control with one check inverted is
    seen to exit with that check's number on both, as 5u's was.
  * Mutants added:
    * each arm removed on its own, with the guest's stop recorded: without `GetEventProps`, the stop the stage
      measured; without the `GetName` arm, the first `GetEvent` by name;
    * the Enum's order reversed (`Pub`'s order);
    * the Enum including the base type's events (`Derived` with `DeclaredOnly`; the fixture's "only its own");
    * the Enum dropping a repeated name (the unit test of `Dup` alone, as above);
    * the flags written as 0 (the fabricated test, the property; `TypeEvents.cs` cannot, since every compiled event
      has none);
    * the flags masked to `0x0600` (the fabricated `0xFFFF`; the property);
    * the flags sign-extended from sixteen bits (the same two);
    * `GetName` of an Event token answered from the Property row of the same RID (the name filter);
    * each of the three refusals removed (the unit tests; the absent-row ones must name the token, not surface as
      `BadImageFormatException`).
  * The stage's mutant "the long-result path dropped" is now killed three ways: the unit fixture, the pure guest and
    the fabricated test.
  * No test in 5v reads an EventPtr. No compiler and no `PersistedAssemblyBuilder` writes one, and a dynamic module
    has no events yet. The arm follows one only through SRM's `GetEvents`. 5w's guest, which defines events on two
    types out of order, is the first check.
* **One feature, one PR.** The three arms and their tests are one feature. The commits go tests first, with the
  failing run recorded (the guests at the Enum TODO, the unit tests and the property at the same refusal), then the
  arms. After the arms exist, the two `DispatchProxy` cases are run by name: `DispatchProxyMethodInterface.cs` is
  registered as passing once it exits 0 under PawPrint and on both real runtimes, and
  `DispatchProxyPropertyInterface.cs` keeps a reason that quotes the `newobj` stop and names the pinning guest.
* **Additions to "left to later stages".**
  * The reflected-constructor `newobj` stage, planned next as above, with its pinning guest from the test-only PR.
  * 5w also asks whether read-write metadata's `HasDelete` arm (md/enc/mdinternalrw.cpp:1208), which skips an
    `RTSpecialName` event with a deleted name, can be reached in a dynamic module. `DefineEvent` clears
    `RTSpecialName`, so the expected answer is no; 5w reads the source to confirm it.
  * 5w's guest is the first check of an EventPtr and of two adds on one event.
  * The items of 5u's list named above.
  * `TypeInfo.Events` is read by IlDump and, from 5v, by one test. Whether reflection should read it, or it should
    go, is not decided here.

### 5v: as built

Built 2026-11-14 as decided above. Each guest was measured first on real .NET, built against net10.0 and run on
10.0.14 and rolled forward to 11.0.0-rc.3 (scratchpad `5v-guest`), with a control inverting one check:

| Guest | .NET 10.0.14 | .NET 11.0.0-rc.3 | Control |
|---|---|---|---|
| `sourcesPure/TypeEvents.cs` (53 checks) | 0 | 0 | check 27 inverted: 27 on both |
| `sourcesImpure/TypeBuilderInheritedEvents.cs` (8) | 0 | 0 | check 2 inverted: 2 on both |
| `sourcesImpure/DispatchProxyEventInterface.cs` (8, parked) | 0 | 0 | check 3 inverted: 3 on both |
| `sourcesImpure/DispatchProxyMethodInterface.cs` | 0 | 0 | |
| `sourcesImpure/DispatchProxyPropertyInterface.cs` | 0 | 0 | |

`TypeEvents.cs`'s `Progress<int>` rows (checks 50 to 53) pass under PawPrint, so they stayed in the guest.
`TestFabricatedEventAccessors`' driver runs on the framework under test, so it was measured on both by running the
fixture with `PAWPRINT_TEST_RUNTIME` unset and `Net11`; both pass, PawPrint and the real runtime alike. The three
passing guests also pass under PawPrint with `PAWPRINT_TEST_RUNTIME=Net11`.

* `NativeMetadataImport` (WoofWare.PawPrint/Native/NativeMetadataImport.fs): `metadataTokenTypeEvent`;
  `eventDefinitionsForTypeDefinition`, beside the property enumeration, which the Enum arm calls; `EventRow` and
  `eventRow`, the one read of an Event row from a raw token, which the new `GetEventProps` FCall arm and the Event arm
  of `GetName` both call. `GetName`'s comment about the missing Event arm is gone and its refusal names four kinds.
  No function changed visibility, and nothing reads `isDynamic` for the Enum.
* Tests, committed first. `TestNativeMetadataImport` gains the event shapes in its source (appended after
  `MarshalShapes`, so no existing token moves), `invokeEnumIn`, `invokeGetNameIn`, `invokeEnumAssociatesIn` and
  `invokeGetEventPropsIn` (the existing helpers now call them with the fixture's assembly), the handler tests the
  decisions list, the CoreLib sweep and the property. `TestFabricatedEventAccessors` is compiled before it, so that its
  `fabricate` serves the unit test of both `Dup` rows. The two existing tests changed as the decisions said.
  * The fabricated image is `5v-fabwrite`'s type with `Flagged` (flags `0xFFFF`, one add) between `Many` and `F0`: 25
    events and 32 MethodSemantics rows (5 for `Ev`, 2 for the `Dup`s, 4 for `Many`, 1 for `Flagged`, 20 fillers). Its
    driver has 16 checks.
  * The CoreLib sweep compares every type the host's `GetTypes` returns, the count asserted as the TypeDef table's
    less `<Module>`, and holds `TypeInfo.Events` of every TypeDef to the enumeration. It takes about half a second.
  * The property draws one to six interfaces, each with no events or up to a bound itself drawn from 3, 8 and 24, so
    that some types have more than sixteen. 500 fixed cases and 500 fresh take three seconds, so the handlers are
    driven throughout and nothing fell back to `internal` functions. Every coverage label is asserted above 15 in the
    fixed sample; the least reached is `MoreThanSixteenEvents`, 88 times (counted per image; the event labels are
    counted per event).
* Red, at the tests-first commit: `TypeEvents.cs`, `TypeBuilderInheritedEvents.cs` and the three `DispatchProxy`
  cases named stopped at "TODO: MetadataImport.Enum does not yet support token type 0x14000000 with parent
  0x02000002", and the fabricated test at the same TODO. Of the 114 tests in `TestNativeMetadataImport` and
  `TestFabricatedEventAccessors`, 16 failed: the Enum tests, the sweep and the property (after its host-side
  assertions passed on the first case) at that TODO; the two `GetEventProps` refusal tests and the every-event test at
  "MetadataImport native method did not match"; the two Enum refusal tests with the TODO in place of the refusal; and
  "GetName rejects a token of an unsupported kind" on the old message.
* The parked cases, measured by naming them once the arms existed: `DispatchProxyMethodInterface.cs` exits 0 and is
  registered as passing. `DispatchProxyPropertyInterface.cs` passes its first four checks and its second `Create`
  (line 100, read from the Release App's report) stops at the invoke stub's `newobj`; its reason quotes that without
  the entry number and names `ReflectionConstructorInvokeTwice.cs`. `DispatchProxyEventInterface.cs`, parked in the
  tests-first commit at the Enum TODO, now stops at "Unimplemented native method (PInvokeImpl
  QCall!TypeBuilder_DefineEvent)", and its reason says so.
* Mutants, each built fresh and run against every test of `TestNativeMetadataImport` and `TestFabricatedEventAccessors`
  and against the three passing guests (scratchpad `5v-mut`). A guest's number is its exit code, the number of its
  first failing check. "U" is the handler tests, "S" the CoreLib sweep, "E" the every-event test, "P" the property, "D"
  the unit test of the fabricated image, "F" the fabricated guest (with its driver's exit code), "A" the existing event
  associates test:

  | Mutant | Killed by |
  |---|---|
  | the Enum answering none | `TypeEvents.cs` (4); `TypeBuilderInheritedEvents.cs` (2); U, S, P, D, F (1) |
  | the Enum giving the next TypeDef's events | `TypeEvents.cs` (PawPrint throws); `TypeBuilderInheritedEvents.cs` (2); U, S, P, D, F (1) |
  | the Enum refusing a dynamic module | `TypeBuilderInheritedEvents.cs` alone: nothing else asks a dynamic module |
  | the Enum truncated at sixteen | `TypeEvents.cs` (46); U (seventeen), P, D, F (1) |
  | the Enum's order reversed | `TypeEvents.cs` (4); `TypeBuilderInheritedEvents.cs` (2); U, S, P, D, F (1) |
  | the Enum including the base TypeDef's events | `TypeEvents.cs` (19); U ("only a type's own"), S |
  | the Enum dropping a repeated name | D alone, as the decisions expected |
  | `GetEventProps` reading the next row | `TypeEvents.cs` (4); `TypeBuilderInheritedEvents.cs` (the row past the end refused); U, S, E, P, D, F |
  | `GetEventProps` clearing `RTSpecialName` | P, D, F (5) |
  | `GetEventProps`' two out-pointers swapped | both guests (a store refused); S, E, P, D, F |
  | the flags written as 0 | P, D, F (5) |
  | the flags masked to `0x0600` | P, D, F (12) |
  | the flags sign-extended from sixteen bits | P, D, F (12) |
  | the `GetEventProps` arm removed | both guests, at "Unimplemented native method (InternalCall) ... MetadataImport::GetEventProps", the stop the stage measured; U, S, E, P, D, F |
  | the `GetName` Event arm removed | both guests, at `GetName`'s refusal of an Event token; S, E, U (absent rows), P, F |
  | `GetName` giving the next row's name | `TypeEvents.cs` (11, the first `GetEvent` by name); `TypeBuilderInheritedEvents.cs` (the row past the end refused); S, E, U, P, F (9) |
  | `GetName` read from the Property row of the same RID | both guests ("Read out of bounds"); S, E, U, P, F |
  | an Event parent encoded with the Property tag | `TypeEvents.cs` (4); `TypeBuilderInheritedEvents.cs` (2); A, S, E, P, F (6) |
  | the read-write reversal applied to an Event parent on disk | A, P, F (8: `Ev`'s two other methods, before `Many`'s three) |
  | the Enum's refusal of a non-TypeDef parent removed | U alone |
  | the Enum's refusal of an absent TypeDef removed | U alone |
  | the row read's refusal of a non-Event token removed | U alone |
  | the row read's bounds check removed | U alone |
  | the row read's lower bound removed | U alone (row 0) |

  The `GetName` arm's own stop, which the stage had not measured alone, is `GetName`'s refusal; `GetName` giving the
  wrong name passes checks 1 to 10 of `TypeEvents.cs` and fails 11, so the first `GetName` call is the first `GetEvent`
  by name. No mutant survived, so no test was strengthened after the first run.
* Suites, built from the whole solution at `78d6112bb` (the later commits change only a test helper's docstring and
  this plan), each run on its own: `Guest` (234), `GuestSolo` (2), `GuestPure` (1182) and `GuestImpure` (280), all
  passing, and the default suite (5598 passed, 37 skipped). They ran in a detached worktree while the mutants ran in
  this one.
* Deviations from the decisions: none in the design. Where the decisions left a choice: the sweep and the property sit
  in `TestNativeMetadataImport`, beside the private `invoke*` helpers they drive, rather than in a fixture of their own;
  the property's case count is 500, since it is cheap; and the sweep asserts that some types have events and some more
  than one, rather than CoreLib's exact counts, which a CoreLib bump would move.

## A reflected constructor named by a dynamic method's `newobj`

### The `newobj` stage: what it is

**Which stage.** 5v's decisions put this stage after 5v and before 5w, and require its planning to start by measuring,
with a throwaway arm, what lies past the `newobj`, so that 5w goes first if more than one feature stands behind it. One
feature stands behind it. A throwaway edit (scratchpad `newobj/throwaway.diff`, reverted) made three changes: the
`Newobj` case of `IlDecoding.scopeOperandKind` (`WoofWare.PawPrint.Domain/IlDecoding.fs:102`) became `Method`; a `Newobj`
arm in `StackShapeOfMethod.dynamicTokenShape` gave a reflected entry the metadata path's shape; and `executeNewobj`
(`UnaryMetadataObjectOps.fs:382`) gave a reflected entry to `constructObject` after
`MethodHandleResolution.concretizeClosedMetadataIdentity`, printing a line each time it did. With nothing else changed:

| Program | Under PawPrint with the throwaway arm |
|---|---|
| `ReflectionConstructorInvokeTwice.cs` (Release App) | exits 0; the arm ran once, at the second `Invoke` |
| `DispatchProxyPropertyInterface.cs` (Release App) | exits 0; the arm ran once, at the second `Create` |
| `5v-proxy twice-method` | prints `twice-method 3 7` and exits 0; the arm ran once |
| the first two, run by name as registered (`--filter "FullyQualifiedName~..."`), unset and `PAWPRINT_TEST_RUNTIME=Net11` | both `Passed` on both runtimes (Debug build, so the stack-shape depth assertion ran) |
| `ReflectionGetMethodFromCanonicalUnshared.cs`, rebuilt with `IsDynamicCodeSupported` on (scratchpad `newobj/canon`) | exits 0, as on .NET 10.0.14 and 11.0.0-rc.3; the arm ran once (`Activator.CreateInstance` of CoreLib's `RuntimeMethodHandleInternal`) |

With the edit reverted and the App rebuilt, the probe below stops at the refusal again for both a constructor's invoke
stub and a directly emitted `newobj`. So 5w does not go first, and this stage is the `newobj` alone. The last row is
the guest that `docs/divergences.md`'s table of first refusals (:555) records as stopping here when the dynamic-code
baseline is emptied.

**What CoreCLR does.** Read from the pinned source (`$DOTNET_RUNTIME_SRC_NET10`; every file named is the same in
`_NET11` in the lines that matter, and the JIT and VM lines are at the offsets given in brackets):

* The stub. `MethodInvokerCommon.DetermineStrategy_*` (MethodInvokerCommon.cs:104-181) runs a method's first reflection
  call through the runtime's invoker and, from the second, through a delegate `InvokerEmitUtil` emits when
  `RuntimeFeature.IsDynamicCodeSupported`. For a constructor `emitNew` is `method is RuntimeConstructorInfo`
  (InvokerEmitUtil.cs:23, :90, :147); the stub pushes the arguments, emits `newobj` of the constructor (:225), boxes the
  result if the declaring type is a value type (:237-243), and returns. There are three shapes: `Obj4Args` for up to
  four arguments, `ObjSpanArgs` for more (each read through `Span<object>.get_Item`), and `RefArgs` when a parameter is
  a byref (each read through `ldfld ByReference.Value` and `ldobj`). `ConstructorInvoker`, `Activator.CreateInstance`
  with arguments, and `DispatchProxy.Create` (which constructs the proxy type through its constructor) all reach it.
* The entry. `DynamicILGenerator.Emit(OpCode, ConstructorInfo)` (DynamicILGenerator.cs:109-127) requires a
  `RuntimeConstructorInfo` and stores `rtConstructor.MethodHandle`, a `RuntimeMethodHandle` whose `m_value` is the
  `RuntimeConstructorInfo` itself (RuntimeConstructorInfo.CoreCLR.cs:195), or, when the declaring type is generic or an
  array, a `GenericMethodInfo` pairing that handle with the declaring type (`GetTokenFor`, :511-519), tagged MethodDef.
  These are two of the four kinds `ResolveToken` reads in method position, and no new kind: no entry ever holds a
  `RuntimeConstructorInfo` or `RuntimeMethodInfo` other than as a handle's `m_value`. `Emit(OpCode, MethodInfo)`
  (:55-107) rules out a `DynamicMethod` only for `ldtoken`, `ldftn` and `ldvirtftn`, so `newobj` can name one, and it
  can name any reflected method, a static or abstract one included.
* Resolution. `ResolveToken` (:772-831) reads the entry without knowing the opcode: a handle gives its method, a
  `GenericMethodInfo` its method and context, a `DynamicMethod` `GetMethodDescriptor()`, which mints it first. In
  `CEEInfo::resolveToken`'s dynamic branch (vm/jitinterface.cpp:878-960) a method needs a method tag and the Method bit
  of the token kind; `CORINFO_TOKENKIND_NewObj` is `0x200 | CORINFO_TOKENKIND_Method` (inc/corinfo.h:1399), so `newobj`
  resolves exactly what `call` resolves, and fails where `call` fails.
* The refusals, in order. The importer's `CEE_NEWOBJ` (jit/importer.cpp:8851 [:8996]) resolves the token, then
  calls `getCallInfo`, which raises `BadImageFormatException` (`BFA_BAD_IL`, "Bad IL format.") for a direct call of an
  abstract method (vm/jitinterface.cpp:5182-5190 [:5283]), as it does for `call`. Then `BADCODE("newobj on static or
  abstract method")` (importer.cpp:8870-8873 [:9017]) makes a static method an `InvalidProgramException`; a
  `DynamicMethod` is always static, which is why `newobj` of one is that exception. Then an array goes to
  `impImportNewObjArray`, `String` (variable size) is called with no `this`, and anything else is allocated through
  `getNewHelper`, which throws `InvalidOperationException` "Instances of abstract classes cannot be created."
  (`InvalidOperation_CantInstantiateAbstractClass`, jitinterface.cpp:5630-5633 [:5720]) for an abstract class. All three
  are raised as the method is compiled, before it runs anything.
* Nothing in that sequence reads the token's universe. A metadata `newobj` meets the same three refusals; only the
  resolution of the token differs.

**Measured** with a throwaway probe (scratchpad `newobj/probe`), built against net10.0 with `IsDynamicCodeSupported`
on, run on 10.0.14 and rolled forward to 11.0.0-rc.3: every line is the same on both runtimes. The first group invokes a
constructor three times through `ConstructorInfo.Invoke`, so that the stub runs twice; the second emits `newobj`
directly in a `DynamicMethod` (`skipVisibility` on) and calls it through a delegate; the third emits it in a
`TypeBuilder` method's body, so that the token is metadata. A refused row calls a marker method before the `newobj`,
and on real .NET the marker never ran (`marks=0`), so each refusal is raised at compilation. "Throwaway" is PawPrint
with the throwaway arm; without it, every row of the first two groups that reaches the stub's or the method's `newobj`
stops at the refusal.

| Scenario | .NET 10.0.14 and 11.0.0-rc.3 | PawPrint, throwaway arm |
|---|---|---|
| a class's constructor, one `int` | three objects, values 1, 2, 3 | the same |
| a struct's constructor (the stub boxes) | `S1:1/10`, `2/20`, `3/30` | the same |
| `G<int>(int)`, `G<string>(string)` (a `GenericMethodInfo` entry) | the values | the same |
| five arguments (`ObjSpanArgs`) | `54321`, `54322`, `54323` | the same |
| a constructor that throws | `TargetInvocationException` wrapping it, each time | the same |
| a class with a `.cctor` | log `cctor;ctor1;ctor2;ctor3;` | the same |
| a private nested class | constructed | the same |
| `ConstructorInvoker.Create(ctor).Invoke`; `Activator.CreateInstance(type, i)` | constructed | the same |
| a `ref int` parameter (`RefArgs`) | values 1, 2, 3, the argument incremented | first call right; the stub stops at `ldfld ByReference.Value` (IL offset 1, before its `newobj`): "CliValueType.FindFieldById: field '...::Value' not found" |
| a static method with a `ref int` parameter, through `MethodInfo.Invoke` | 101, 102, 103 | the same stop at the same instruction, with no `newobj` involved |
| `string(char, int)`; `Nullable<int>(int)`; `int[,](int, int)` | constructed | stops before any stub: `InvokeMethod` with `isConstructor` on `String`, and on `Nullable<T>`; `GetFirstIntroducedMethod` for the array, at `GetConstructor` |
| `newobj` of a class's, a struct's, `G<int>`'s and `String`'s constructor | constructed | the same |
| `newobj` of `int[,]`'s constructor | `2x3` | stops at `GetConstructor`, as above |
| `newobj` of a constructor that throws, inside the method's own `try`/`catch` | the handler gets it | the same |
| `newobj` of `G<>`'s constructor (open) | `InvalidProgramException` | the same (`tryMethod`'s existing refusal), raised at the instruction (`marks=1`) |
| `newobj` of a `DynamicMethod` | `InvalidProgramException`; afterwards the target's `_methodHandle` is set | the throwaway arm's refusal |
| `newobj` of a `DynamicMethod` with no body | `InvalidOperationException` "Method 'emptytarget' does not have a method body."; `_methodHandle` still null | not run |
| `newobj` of a reflected static method | `InvalidProgramException` | calls it as a constructor, then "Invalid CIL: method ... returned with more than one evaluation stack value" |
| `newobj` of `Stream.Flush` (abstract, on an abstract class) | `BadImageFormatException` "Bad IL format." | "reached executeOneStep for abstract method System.IO.Stream::Flush" |
| `newobj` of an abstract class's constructor | `InvalidOperationException` "Instances of abstract classes cannot be created." | constructs an instance of the abstract class |
| `newobj` of `Inst.NotCtor`, a `void` instance method that is not a constructor | allocates and calls it: `V` is 77 | the same |
| the last five in a `TypeBuilder` body (metadata token) | the same five answers | static: the same "Invalid CIL"; abstract method: the same stop; abstract class: constructs it; `NotCtor`: 77. This is PawPrint at this branch's tip, since the throwaway arm does not touch the metadata path |

The two `DynamicMethod` rows show that `newobj` mints its target before refusing it: an empty body is the mint's own
exception, and a good one is minted though never called.

A second probe (scratchpad `newobj/sweep`) measures the refusal rule over all of CoreLib on real CoreCLR. For each method
of a closed type that is not itself generic, it compiles `ldarg.0; brfalse skip; <a default per parameter>; newobj m;
pop; skip: ret` in a `DynamicMethod` and calls it with `false`, so that compilation happens and the `newobj` never runs.
It takes about ten seconds.

| Shape (count on 10.0.14; 11.0.0-rc.3 has more methods and the same outcome per shape) | Outcome |
|---|---|
| abstract, instance or static, on a class or an interface (686; of them 17 static abstract interface members) | `BadImageFormatException` |
| static, not abstract, on any kind of type (21,252) | `InvalidProgramException` |
| an instance constructor of an abstract class, `Delegate`'s included (94) | `InvalidOperationException`, the abstract-class message |
| an instance constructor of anything else: class, struct, byref-like struct, `String`, delegate (1,974) | compiles |
| an instance method that is not a constructor | not swept: CoreCLR crashed the process (SIGSEGV, exit 139) compiling `newobj` of a delegate's `Invoke`, of a struct's `Interop+Sys+DirectoryEntry.GetName`, and of a class's `SafeFileHandle+ThreadPoolValueTaskSource.QueueRead` |

So the rule has four outcomes and a fixed order, abstract before static before abstract class, and a fifth case real .NET
does not handle consistently: `NotCtor` runs, and other non-constructors crash the runtime.

**How PawPrint represents a scope entry today.** A dynamic method's body is decoded at mint against
`IlTokenUniverse.DynamicScope`, and a token operand becomes `MetadataOperand.FromDynamicScope index` without reading the
entry. `IlDecoding.scopeOperandKind` (IlDecoding.fs:78) is the one classification of what each opcode reads an entry as.
The decoder refuses at mint, by opcode alone, every opcode it classes `NotYetSupported`, which is where `newobj` stops
today (:102-107). When an instruction runs, `UnaryMetadataIlOp.execute` (UnaryMetadataIlOp.fs:44, :86-121) asks the same
classification and resolves the entry before dispatching: a `Method` operand goes through `DynamicScopeOperand.method`
(DynamicScopeOperand.fs:1079, over `tryMethod` at :892 and `reflectedMethod` at :778), which reads all four entry kinds,
raises what real .NET raises for a malformed entry, and answers `NeedsMinting` for an unminted `DynamicMethod`, which
`execute` mints by pushing the guest's `GetMethodDescriptor` and re-executing the instruction. The op receives
`ResolvedMetadataOperand.ScopeMethod` of a `MethodHandle`: `FromMetadata` of a closed identity, or `FromDynamic`.

Which opcodes already resolve a method entry:

* `call` and `callvirt` are classed `Method`. `executeCall` (UnaryMetadataCallOps.fs:754) splits on `ctx.Operand`
  before it touches `ctx.ActiveAssembly`, which is partial and fails for a scope operand
  (UnaryMetadataIlOpContext.fs:139). A reflected entry is concretised by `concretizeClosedMetadataIdentity`
  (MethodHandleResolution.fs:166) and entered through `enterNamedCallee` (:693), which refuses an abstract method with
  `raiseNamedAbstractMethod` (:671), the same refusal the metadata path gives through
  `CompilationFailure.NamesAbstractMethod` (CallSiteBinding.fs:9). A `DynamicMethod` is concretised by
  `DynamicMethodExecution.concretize`. `executeScopeCallvirt` (:1015) raises `MissingMethodException` for a static
  callee, on the scope path only, with origin `ResolutionFault.ScopeEntry`.
* `ldftn` and `ldvirtftn` are `NotYetSupported`, with the reason that they are not wired yet; `ldtoken` of a method- or
  field-tagged token is refused by the decoder by its tag.
* `StackShapeOfMethod.dynamicTokenShape` (StackShapeOfMethod.fs:294) computes a shape for `call` and `callvirt` from the
  same `tryMethod`, and for `newobj` has a `failwith "BUG: ..."` arm (:362), since the decoder refuses such a body. The
  analysis runs on a body's first execution, so the stub reaches it before its `newobj` runs.

So `newobj` follows `call`: its classification becomes `Method`, and resolution, minting, the malformed-entry refusals
and `NeedsMinting` all come from what exists. What it needs of its own is in `executeNewobj` and the stack shape.

`executeNewobj` today binds `ctx.ActiveAssembly` and `ctx.MetadataToken` on its first lines, then resolves a MethodDef or
MemberRef (with the array-constructor shortcut first), binds the method with `bindMethodForInstruction`, and calls
`constructObject` (UnaryMetadataObjectOps.fs:230), which redirects a `String` constructor to its `Ctor`, allocates a
value type on the heap and copies it out, and otherwise allocates and calls the constructor. Neither checks the method
or the class: the metadata path constructs an abstract class and calls a static method as a constructor, as the
`TypeBuilder` rows show. `OpcodeFaults`' entry for `newobj` (WoofWare.PawPrint.Semantics/OpcodeFaults.fs:519) carries
`TokenBinding`, whose documentation and exception list (:220) name `call`'s abstract refusal but not `newobj`'s three,
and do not include `InvalidOperationException`.

### The `newobj` stage: the options

Every option that resolves the entry makes the same three changes: `scopeOperandKind` classes `Newobj` as `Method`, so
the decoder admits it and `execute` resolves, mints and refuses entries as for `call`; `executeNewobj` splits on
`ctx.Operand` before it reads `ActiveAssembly`, as `executeCall` does; and `dynamicTokenShape` gains a `Newobj` arm that
gives a reflected entry `TokenShape.Callee (parameter count, None)`, as `StackShapeTokens.ofMetadataToken` does for a
metadata `newobj`, and gives no shape for a `DynamicMethod` or a refused method, whose instruction raises. The
`DynamicMethod` arm raises `InvalidProgramException` after the mint `execute` already performs, which is CoreCLR's
order (measured above). They differ in where the refusals live and which universe gets them.

**A. A scope arm with its own refusals; the metadata path unchanged.** `executeNewobj`'s scope arm concretises the
identity, then refuses in CoreCLR's order (an abstract method through `raiseNamedAbstractMethod`; a static method,
`InvalidProgramException`; an abstract declaring type, `InvalidOperationException` with the CLR message), refuses a
method that is not an instance constructor by name, and calls `constructObject`. The metadata arm is untouched. This is
how `executeScopeCallvirt` treats a static callee.

* Correctness: every scope row above. The metadata path keeps constructing abstract classes, a wrong answer reachable
  from any `TypeBuilder` body, and keeps calling static methods as constructors.
* Blast radius if wrong: `newobj` in a dynamic method, all of which is refused today.
* Reversibility: one arm; nothing existing changes shape.
* Information: the same instruction gets two answers by universe, and the refusals for the metadata path would later
  be written again or moved, leaving the scope arm's copy to migrate.

**B. One rule for both universes.** A function over the concretised method and its declaring type gives the refusal,
if any, in CoreCLR's order: `CompilationFailure` gains `NewobjOfStaticMethod` (`InvalidProgramException`) and
`InstantiatesAbstractClass` (`InvalidOperationException`, the CLR message), and reuses `NamesAbstractMethod`. A method
that is not an instance constructor is refused by name (see question 3). Both arms of `executeNewobj` call the function
between binding and `constructObject`, and raise through `raiseCompilationFailure`. `OpcodeFaults`' `TokenBinding`
documentation names the three refusals, and its list gains `System.InvalidOperationException`.

* Correctness: every row above, in both universes; the `TypeBuilder` rows change from a wrong answer and two crashes to
  real .NET's three exceptions.
* Blast radius if wrong: every `newobj` of every guest calls the function: three flag reads and a type lookup the
  binding has already made. A wrong rule refuses ordinary constructors at once, which every guest suite would show. The
  sweep says what it changes for compiled code: no instance constructor of a non-abstract type is refused, and no
  compiler emits the other cases.
* Reversibility: two `CompilationFailure` cases and a call in each arm.
* Information: one rule, stated once and checked against the sweep, which an analyser could later read if it moved to
  Semantics (question 5).

**C. Stop CoreLib emitting the stub.** Seed `System.Reflection.ForceInterpretedInvoke` (read by
`LocalAppContextSwitches`; MethodInvokerCommon.cs:19) so that every reflection call takes the runtime's invoker.

* Correctness: un-parks the three programs, but a guest's own `Emit(OpCodes.Newobj, ctor)` stays refused, and a guest
  that reads the switch sees a value real .NET does not set.
* Blast radius: every reflection invocation of every guest with dynamic code on, and the method stubs that
  `ReflectionInvokeThroughEmittedStub.cs` pins would no longer run. It hides an interpreter gap by changing what
  managed code runs, which AGENTS.md's rule about primitive boundaries forbids in spirit.
* Rejected.

**D. Turn the entry into a metadata token and reuse the metadata arm.** For a non-generic declaring type the identity
carries a MethodDef handle in its assembly, and `executeNewobj`'s MethodDef branch could run on it. But that branch takes
the token's assembly to be the executing method's (`ctx.ActiveAssembly`, which a dynamic method does not have) and binds
against the executing method's generic context, and for `G<int>` (a `GenericMethodInfo` entry) no token names the
instantiation. It coerces an identity into a token to reuse metadata code, which AGENTS.md's architecture guidelines
rule out. Rejected.

**Recommended: B.** The three refusals are rules of `newobj`, not of the scope: the sweep shows them over every shape of
method, and the `TypeBuilder` rows show the metadata path giving a wrong answer for one of them today. `call`'s abstract
refusal is already shared between the universes through `enterNamedCallee`; the callvirt-of-static rule is scope-only,
so both structures exist, and B is chosen on its merits: one statement of the rule, checked against CoreCLR across
CoreLib, and no copy to migrate later. A would add a correct arm beside a wrong one. C and D are rejected above.

Open questions:

1. A, B, C or D.
2. Under B, one PR, or the shared refusals first as a PR of their own over the metadata path (pinned by the `TypeBuilder`
   guest below), then the scope arm on top. Recommended: two PRs, the refusals first. AGENTS.md prefers a dependency
   merged first, and the refusals are what keep the scope arm from constructing abstract classes; each PR is then one
   feature.
3. A method that is not an instance constructor. Real .NET runs `Inst.NotCtor` and crashes the process on others
   (measured), and PawPrint's metadata path runs them all. Refuse it by name in both universes (recommended: correctness
   over availability, and no compiler emits it), or let it run in both, or refuse it on the scope path only.
4. The raise origin of the `DynamicMethod` arm's `InvalidProgramException`. `ResolutionFault.TokenBinding`
   (recommended: it is the static rule, which a metadata token meets too), or `ScopeEntry`, as `executeScopeCallvirt`
   classes its static refusal.
5. Where the rule lives. In the interpreter, as `CompilationFailure` cases beside `NamesAbstractMethod` (recommended:
   only the interpreter asks), or in Semantics as a pure classification over Domain's method and type facts, which
   `EscapeAnalysis` could read for `newobj`'s escapes. Moving it later is mechanical.

### The `newobj` stage: the oracle

Written first, and seen to fail before the implementation:

* `ReflectionConstructorInvokeTwice.cs` and `DispatchProxyPropertyInterface.cs` are registered as passing. Both passed
  by name on both runtimes with the throwaway arm.
* `sourcesImpure/ReflectionConstructorInvokeShapes.cs` (`IsDynamicCodeSupported` on): the probe's first group, each
  constructor invoked three times with a different argument and every result checked, so that the stub runs twice:
  the class, the struct, `G<int>` and `G<string>`, five arguments, the constructor that throws (a
  `TargetInvocationException` with the inner exception's type and message, each time), the `.cctor` log, the private
  nested class, `ConstructorInvoker` and `Activator.CreateInstance`. It leaves out the `ref` parameter, `String`,
  `Nullable<int>` and the array, which stop elsewhere (below). It returns the number of the first failing check.
* `sourcesImpure/DynamicMethodNewobj.cs`: the probe's second group, with each refusal's exception type and, where the
  CLR gives one, its message: the class, struct (boxed), `G<int>` and `String` constructors; the throwing constructor
  caught in the method; `G<>`'s constructor; a `DynamicMethod`, then reading its `_methodHandle` by private reflection
  (set); an empty `DynamicMethod` (the mint's exception, and `_methodHandle` still null); a static method; `Stream.Flush`;
  a static abstract interface member (the sweep's row; measure it in the guest on both runtimes first); an abstract
  class's constructor; and, if question 3 keeps it running, `Inst.NotCtor`. No refused case does anything observable
  before its `newobj`, because PawPrint raises at the instruction where CoreCLR raises at compilation, a divergence
  already recorded for every compile-time refusal.
* `sourcesImpure/TypeBuilderNewobjRefusals.cs` (option B): the third group, a `TypeBuilder` method per case through a
  delegate: a constructor (the control), a static method, `Stream.Flush`, an abstract class's constructor. Today it
  fails at the abstract class (constructed) or crashes, depending on order; under question 2's first PR it is that PR's
  guest.
* Each guest is measured on .NET 10.0.14 and 11.0.0-rc.3 before it is committed, with a control inverting one check,
  as 5v's were.
* `TestNativeGetDynamicMethod`'s "a newobj naming a dynamic-method entry is refused as unsupported rather than as
  wrong-kind" (:1977) is replaced by a test that `newobj` naming each of the four method entry kinds is minted, beside
  the one for `call` and `callvirt`. The PR says that an existing test changed and why.
* The sweep as a test in the host (option B): for every method the sweep compiles (instance constructors, static
  methods, abstract methods, of every closed type of `HostImage`'s CoreLib that is not generic), the rule function's
  answer for the method read from the image equals the exception, or none, that real CoreCLR's compilation raised. The
  count is asserted equal to the methods enumerated, and coverage is asserted for each of the four outcomes, for a
  static abstract member, for a value type's and a `String` constructor, and for `Delegate`'s constructor (abstract
  class). The host's runtime is the test host's, so this checks .NET 10; the probe has checked 11 by hand. The rule
  function is `internal` so that the test can call it; if question 5 moves it to Semantics, it is public there.
* Mutants, each run against the guests, the replaced unit test and the sweep:
  * `Newobj` classed `NotYetSupported` again (all three stub programs stop at mint);
  * the scope arm falling through to the metadata arm (`ActiveAssembly`'s "gone out of step" TODO);
  * the stack shape counting `this` among the arguments (the Debug depth assertion in every guest run; to be seen to
    fire, since the suites build Debug and the throwaway arm passed there);
  * the stack shape giving no shape for a reflected entry (whether anything kills it is to be measured and recorded);
  * the `DynamicMethod` arm constructing, or raising before the mint (the two `DynamicMethod` rows: `_methodHandle` set,
    and the empty body's exception);
  * the static refusal checked before the abstract one (the static abstract interface member; the sweep);
  * the abstract-class refusal checked before the abstract-method one (`Stream.Flush`; the sweep);
  * the abstract-class refusal dropped (the abstract class's row in both guests; the sweep);
  * a `GenericMethodInfo` entry concretised at its definition rather than its identity (`G<int>`, `G<string>`);
  * `constructObject` given the definition's declaring type rather than the identity's (`G<string>`);
  * under B, the metadata arm skipping the rule (`TypeBuilderNewobjRefusals.cs`).

### The `newobj` stage: left to later stages

* 5w, `TypeBuilder_DefineEvent`, next, as 5v's decisions say; then `TypeBuilder_SetParamInfo`.
* The by-ref invoke stub. `InvokerEmitUtil.CreateInvokeDelegate_RefArgs`' `ldfld ByReference.Value` over CoreLib's
  `StackAllocatedByRefs` stops any reflection call with a byref parameter from its second invocation on, a constructor's
  or a method's alike (measured above), and `docs/divergences.md`'s table (:555) counts 30 guests that stop there when
  the dynamic-code baseline is emptied. It is a byref-layout question, not emit, and not this plan's.
* Reflection invocation of a `String` constructor, of a `Nullable<T>` constructor, and of an array type's constructor,
  each refused before any stub by `RuntimeMethodHandle.InvokeMethod` or `GetFirstIntroducedMethod` (measured above).
* `ldftn` and `ldvirtftn` of a scope entry, and method- or field-tagged `ldtoken`: unchanged, with no measured consumer.
* The timing of a compile-time refusal: PawPrint raises it at the instruction, after the method has run what precedes
  it; recorded in `docs/divergences.md` already.
* `newobj` of an array type's constructor named by a scope entry: unreachable until reflection can name one
  (`GetConstructor` stops first). When it can, the scope arm needs the metadata arm's array shortcut.
* Comments that describe the refusal as current: the header of `sourcesImpure/DynamicMethodExceptionRegions.cs`
  (:10-14), `sourcesImpure/DynamicMethodStackTrace.cs` (:25-28), `IlDecoding.scopeOperandKind`'s note on the
  method-shaped opcodes (:88-91), and `docs/divergences.md`'s paragraph listing what is still refused by name (:583).
  These change with the stage. The dated table at :555 records a measurement and stays as it is.

### The `newobj` stage: the decisions

Taken 2026-11-14.

* **B: one rule for both universes, in two PRs, the refusals first.** Checked against the pinned source:
  * The importer's `CEE_NEWOBJ` (jit/importer.cpp:8851) resolves the token, calls `getCallInfo`, then raises
    `BADCODE("newobj on static or abstract method")` (:8870-8873), then splits on the class flags, and asks
    `getNewHelper` only on the fixed-size class path (:9013). `getCallInfo`'s abstract refusal
    (vm/jitinterface.cpp:5182-5190) and `getNewHelper`'s abstract-class refusal (:5630-5633) are as the stage
    describes them. `Emit(OpCode, MethodInfo)` and `Emit(OpCode, ConstructorInfo)` (DynamicILGenerator.cs:55-127)
    are as described.
  * The probe's rows were run again on .NET 10.0.14 and 11.0.0-rc.3 for this decision: the four `TypeBuilder` rows
    and their control, the two `DynamicMethod` mint rows, and the by-ref method row. Each agrees with the table.
    The sweep's two outputs have the same 31 shapes with the same outcome each (24,006 methods on 10, 26,115 on 11),
    and the stage's four counts add up.
  * A is rejected. It adds a correct arm beside a wrong one, and the stack-shape arm would need the same rule a
    third time.
  * C and D are rejected for the stage's reasons.
  * A fifth structure was weighed: the scope arm first, with the three refused shapes stopped by name, and the
    shared rule afterwards. It un-parks the guests one PR sooner. It is rejected because it writes three host
    refusals in order to delete them in the next PR, and AGENTS.md prefers the dependency merged first.
* **The stage is two features, and neither stands behind the stop.** 5v's condition was about what a guest reaches
  past the `newobj`, and the measurement answers it: nothing. So this stage stays ahead of 5w.
  * The refusals are not behind the stop. They are a wrong answer on the metadata path that exists today, found
    while planning. They are their own PR because the scope arm would otherwise make the abstract-class answer
    reachable from a second universe.
  * PR 1, from `main` now: "Refuse a `newobj` of an abstract method, a static method or an abstract class, as
    CoreCLR's JIT does". It depends on nothing of 5v's.
  * PR 2, from `main` once PR 1 and #2246 have merged: "Run a `newobj` that names a reflected constructor in a
    dynamic method". Its code needs only PR 1; un-parking `DispatchProxyPropertyInterface.cs` needs #2246. If
    #2246 is still open, PR 2 is stacked on it, the four suites are run locally at its tip, and the PR body says so.
  * Then 5w.
* **The rule lives in Semantics, as a pure function of Domain facts.** The stage's "only the interpreter asks" is
  wrong: there are four readers.
  * Both arms of `executeNewobj` ask it.
  * `StackShapeOfMethod.dynamicTokenShape` asks it, since it must give no shape exactly where the instruction
    refuses, as its `call` arm does today with a copy of `call`'s rule.
  * The escape analysis should ask it (below).
  * The function reads only the method's attributes and name, and the declaring type's attributes. It needs no
    machine state, so Semantics is the lowest project that can hold it. The sweep then calls a public function,
    and nothing is made `internal` for a test.
  * If refusals later move to method entry, a rule held as data moves with a change of caller.
* **Conditions on the rule.**
  * It has five outcomes, in this order: an abstract method (`BadImageFormatException`, "Bad IL format."); a static
    method (`InvalidProgramException`); a method that is not an instance constructor (stopped by name, below); an
    abstract declaring type (`InvalidOperationException`, the CLR message); otherwise it constructs.
  * The third sits where it does because of what is measured. The first two outcomes are measured on
    non-constructors: all 686 abstract methods and all 21,252 static methods in the sweep are non-constructors. The
    abstract-class outcome is measured on constructors only.
  * An instance constructor is CoreCLR's `IsMdInstanceInitializer` (inc/corhdr.h:433): `RTSpecialName` set and the
    name `.ctor`. A method named `.ctor` without the flag is not one. A `.cctor` is static and takes the second
    outcome.
  * The abstract-class outcome is CoreCLR's for a class. A value type takes the importer's value-class path, which
    never calls `getNewHelper`, and no loader rule forbids the `Abstract` bit on a value type. So the source says
    an abstract value type constructs, and nothing measures it. PR 1 measures one fabricated abstract struct on
    .NET 10 and 11. If it constructs, the rule takes "is a value type" as an input and the fabricated test gains
    the row. Until measured, the rule stops it by name.
  * The rule's result type has no wildcard arm at any reader. In the interpreter, the three exceptions are
    `CompilationFailure` cases raised through `raiseCompilationFailure`.
  * The rule is called in `executeNewobj`, never in `constructObject`. `Activator`, the runtime's reflection
    invoker and `[UnsafeAccessor]` also reach `constructObject`, and CoreCLR gives each its own check and its own
    exception.
  * On the metadata arm the rule is asked after the array shortcut and after binding, so a type refusal or a
    constraint refusal still comes first, as `resolveToken` precedes `getCallInfo`.
  * `executeNewobj` splits on every case of `ResolvedMetadataOperand` with no wildcard: `FromMetadata` is the
    metadata arm, `ScopeMethod` the scope arm, and `StubMethod`, `ScopeType`, `ScopeField` and `ScopeTypeTarget`
    fail by name. The throwaway's `| _ ->` is not kept.
* **A method that is not an instance constructor is stopped by name, in both universes.**
  * Real .NET has no single behaviour to copy: `Inst.NotCtor` runs, and three other shapes crash the process.
  * Two of the three crashing methods return a value (`ReadOnlySpan<char>`, `ValueTask<int>`) and the third is a
    delegate's `Invoke`. That suggests the importer assumes a `void` non-delegate target. It is a reading of three
    cases and is not relied on.
  * Letting it run would also need a distinction PawPrint does not keep. CoreCLR allocates the class the token
    names (`getNewHelper(resolvedToken.hClass)`, and `gtNewAllocObjNode(&resolvedToken, ...)`), which for an
    inherited method is not the declaring class. For a constructor the two are the same, because a MemberRef never
    finds an inherited constructor (vm/memberload.cpp:1160).
  * This takes away one answer that matches real .NET today: `tb-instmethod`'s 77. No compiler emits the shape, and
    the stop names it.
  * Refusing it on the scope path only is rejected: it would leave the same instruction with two answers by
    universe.
* **The fault category is `TokenBinding` for all three refusals, the `DynamicMethod` case included.**
  * A `DynamicMethod` is refused because it is static, which is the rule a metadata token meets too. Its arm
    raises the same `CompilationFailure` through `raiseCompilationFailure`, after the mint `execute` already
    performs. The category then cannot differ between the universes, and no test needs to pin it. No guest can see
    it.
  * `ResolutionFault.typeNames TokenBinding` gains `System.InvalidOperationException`, and the case's documentation
    names `newobj`'s three refusals. This loosens the raise-time check slightly for every other `TokenBinding`
    raise.
  * A new `ResolutionFault` case for `newobj` was weighed and rejected. It would keep that check tight, but it is
    one more case for one exception, and it would force an edit to the escape analysis's exhaustive match inside
    PR 1.
  * `executeScopeCallvirt` keeps `ScopeEntry` for its static callee. Whether the metadata path's `callvirt` of a
    static method gives the same `MissingMethodException` is not measured here (below).
* **The escape analysis needs the rule, in a PR of its own after PR 1.**
  * It models a `newobj` as a call and an allocation. Its `abstractCallees` covers `call` and `ldftn` only. So for
    a body in metadata that holds one of the three refused shapes, it reports nothing escaping where CoreCLR
    raises before the body runs. That is a false negative today, whatever PawPrint's interpreter does.
  * Its comment at EscapeAnalysis.fs:5927 says a compile-time refusal for what a token binds to is
    `abstractCallees`' and `refusals`'. Once `TokenBinding`'s documentation names `newobj`'s three, that comment is
    known to be incomplete. PR 1 does not touch Analysis; the follow-up does.
  * Title: "Report what a `newobj` that CoreCLR's JIT refuses raises". It does not gate PR 2 or 5w.
  * `TestEscapeAnalysis`'s Roslyn fixture cannot spell the IL, so its fixture is emitted, as
    `TestEscapeAnalysisConstraintViolation`'s are.
* **Corrections to the stage's other sections.**
  * `probe/net10.out` and `probe/net11.out` predate the `tb-*`, `dyn-dm-empty`, `dyn-dm-mints` and `byref-method`
    rows: neither file holds them. The rows are right (run again, above). PR 1 saves the full output of both
    runtimes.
  * "The last five in a `TypeBuilder` body" is four rows and a control. There is no `DynamicMethod` row in that
    group.
  * "An abstract declaring type" is more exactly the class the token names, on the fixed-size class path only, as
    above.
  * "A divergence already recorded for every compile-time refusal" is not so for `newobj`. `docs/divergences.md`
    records the timing for `call` and `ldftn` of an abstract method, for refused type operands and for constraint
    violations. PR 1 adds `newobj`'s three to it.
  * The stage's oracle makes the `TypeBuilder` guest PR 1's check of the metadata path. The precedent is
    `TestFabricatedAbstractMethodCall`: an image on disk, run under PawPrint and on the real runtime by
    `FabricatedGuest`, in the default suite, with no dependence on emit. PR 1's main check is of that kind
    (below).
  * "The stack shape counting `this` among the arguments (the Debug depth assertion in every guest run)" and "giving
    no shape ... to be measured" both lean on the guests. A token with no shape is recorded as untyped
    (StackShapeOfMethod.fs:481-492) and the run goes on, so a guest need not notice either. Both get a unit test
    (below).
  * The sweep skips every generic type, so it holds no abstract generic class. The guests add one.
  * PawPrint raises no `MethodAccessException` anywhere, for a metadata token or a scope entry. The probe's
    abstract class has a protected constructor, named from another assembly. Real .NET still gives the
    abstract-class exception, because it is raised at compilation, before the access helper could run. The guests
    give their abstract class a public constructor, so that no row leans on that order.
  * Option B's "three flag reads and a type lookup the binding has already made" stands, and `constructObject`
    already reads the same `TypeInfo`.
* **The oracle is sufficient with these changes.** PR 1, tests first:
  * The rule is tested exhaustively over its inputs (abstract method, static, the constructor predicate, abstract
    type, and value type if it becomes an input) against the order written as the sentence above.
  * The sweep runs in the host as the stage describes, and also:
    * for every method it skips, it asserts that the rule answers "not an instance constructor" exactly where
      reflection says the method is neither a constructor, nor static, nor abstract;
    * it asserts that every exception CoreCLR raised is one `ResolutionFault.typeNames TokenBinding` names;
    * its coverage also names a `.cctor`, a static method of an interface, and a byref-like struct's constructor.
  * The sweep's docstring says it compiles in the test host, that CoreCLR crashes the process on the shapes it
    skips, and that a crash of the host after a CoreLib bump should be looked for here first.
  * `TestFabricatedNewobjRefusals`, through `FabricatedGuest`, with types of the fabricated image's own:
    * a constructor (the control);
    * a static method;
    * an abstract method of an abstract class;
    * an interface's instance method;
    * a static abstract interface member;
    * an abstract class's public constructor;
    * an abstract generic class's constructor at an instantiation;
    * the abstract struct, once measured.

    Each method puts the `newobj` first and has no handler, so the timing divergence cannot show.
  * A separate test expects PawPrint to stop by name at a `newobj` of an instance method that is not a
    constructor, and at one named `.ctor` without `RTSpecialName`.
  * `sourcesImpure/TypeBuilderNewobjRefusals.cs` stays as the stage has it, as the check that an emit scope's
    tokens reach the same rule.
  * Mutants:
    * each pair of adjacent outcomes swapped;
    * the abstract-class refusal dropped;
    * the metadata arm skipping the rule;
    * the non-constructor stop dropped;
    * the constructor predicate reading the name alone;
    * the rule moved into `constructObject` (record which existing `Activator` or reflection guest kills it; if
      none does, add a row to one).
  * The four Guest suites and the default suite run at the tip, since every `newobj` of every guest now asks the
    rule.
* **The oracle, PR 2**, tests first:
  * The stage's guests, mutants and replaced unit test stand.
  * `DynamicMethodNewobj.cs` gains the abstract generic class. Every dynamic method in it is made with
    `skipVisibility`.
  * A unit test, beside the replaced one in `TestNativeGetDynamicMethod`, mints `ldarg.0; newobj <reflected
    constructor of one parameter>; ret`. It asserts the shape the analysis records at the `newobj`: one argument
    popped, one value pushed, and `this` not counted. It kills both shape mutants.
  * A second unit test asserts no shape for an entry the rule refuses, and for a `DynamicMethod`.
  * A refused `newobj` with its arguments not pushed is measured on .NET 10 and 11 first (a static method of one
    parameter, with nothing pushed). If real .NET gives the rule's exception, as it does for `call`, the guest
    gains the row. That row kills the mutant "a shape given to a refused entry".
  * Mutants added: the scope arm skipping the rule (each refusal row), and the `DynamicMethod` arm raising its own
    exception instead of going through the rule (the row's exception type; the category is fixed by construction,
    as above).
* **Additions to "left to later stages".**
  * The escape analysis's reading of the rule, as above.
  * The metadata path's stack shape for a refused `newobj`. `StackShapeTokens.ofMetadataToken` gives every `newobj`
    a callee's shape. A body that refuses with its arguments not pushed would be stopped for underflow, where
    CoreCLR raises the refusal. No compiler emits one.
  * A delegate's constructor named by a scope `newobj`. It is unmeasured, and a guest's own body cannot reach it
    usefully until `ldftn` of a scope entry is wired. `ConstructorInfo.Invoke` twice on a delegate's constructor
    would reach it through the stub, and is also unmeasured.
  * Access checks. PawPrint makes none, so a dynamic method without `skipVisibility` that names a private
    constructor constructs, where real .NET raises `MethodAccessException`. This is the same gap `call` has.
  * `callvirt` of a static method named by a metadata token, and whether `executeScopeCallvirt`'s rule should be
    shared as this stage shares `newobj`'s.
  * Raising compile-time refusals at method entry. The rule is data in Semantics so that this needs a change of
    caller only.
  * A non-constructor target, if a guest ever stops at it. The starting point is the measurement that `NotCtor`
    runs, and the reading that CoreCLR allocates the token's class.

### The `newobj` stage, PR 1: as built

Built 2026-11-14 as decided above, from `main` at `101ebd665`.

**Measured first**, each built against net10.0 and run on 10.0.14 and rolled forward to 11.0.0-rc.3:

* The abstract struct (scratchpad `newobj/absstruct`). An image from `PersistedAssemblyBuilder` holds three value
  types, each with a public constructor from `int` that stores it: `AbsSealed` (`Abstract | Sealed`), `AbsOpen`
  (`Abstract`) and `Plain` (`Sealed`). A static method per type puts `newobj` of its constructor first, and the image is
  loaded from disk, so the token is metadata. On both runtimes all three types load, reflection reports `IsAbstract`
  for the first two, and all three `newobj`s construct (the field reads back 42), boxed or not. So the rule takes "is a
  value type" as an input and `TestFabricatedNewobjRefusals` has the row. `Activator.CreateInstance` of `AbsSealed`
  is refused by its own check (`MemberAccessException`, "Cannot create an instance of AbsSealed because it is an
  abstract class."), on both.
* `[UnsafeAccessor(UnsafeAccessorKind.Constructor)]` of `AbsOpen` (scratchpad `newobj/absua`, compiled against
  CoreLib's implementation assembly so that it can name the fabricated type): it constructs on both runtimes. See the
  deviations.
* The probe's full output on both runtimes (scratchpad `newobj/probe/net10-full.out` and `net11-full.out`): identical
  but for the version line, and holding the `tb-*`, `dyn-dm-empty`, `dyn-dm-mints` and `byref-method` rows, each as the
  stage's table and the decisions give it.
* `sourcesImpure/TypeBuilderNewobjRefusals.cs` (scratchpad `newobj/tbguest`): 0 on both; with the abstract method's
  message altered in the check, 6 on both.
* The sweep without its skip of a delegate type's non-constructors (scratchpad `newobj/sweep2`): the five static
  methods that skip also left out (`MulticastDelegate::TrySetSlot` and others) compile to `InvalidProgramException` on
  both runtimes, and nothing crashes; 24,011 methods on 10, 26,117 on 11.
* `[SuppressGCTransition]` in the test host's CoreLib (scratchpad `newobj/sgt`): 36 methods, P/Invokes into `QCall`
  (23) and `libSystem.Native` (13), all static, so the sweep compiles a `newobj` of each. The CoreCLR 10.0.12 JIT-lock
  leak needs a `DllNotFoundException` during compilation; both libraries are present, and the sweep asserts that every
  compilation raises the rule's exception, so none raised that.

**Files.**

* `WoofWare.PawPrint.Semantics/NewobjTarget.fs` (new, after `StringConstructor.fs`): `NewobjTarget`, five cases in
  the decisions' order; `NewobjTarget.isInstanceConstructor`, CoreCLR's `IsMdInstanceInitializer`; and
  `NewobjTarget.classify` over the method's attributes and name and the declaring type's attributes and
  value-typeness. Public, so the sweep calls it directly.
* `OpcodeFaults.fs`: `ResolutionFault.TokenBinding`'s documentation names `newobj`'s three refusals, and
  `typeNames TokenBinding` gains `System.InvalidOperationException`.
* `CallSiteBinding.fs`: `CompilationFailure.NewobjOfStaticMethod` and `InstantiatesAbstractClass`;
  `NamesAbstractMethod`'s documentation names `newobj`. `UnaryMetadataCallOps.raiseCompilationFailure` raises both as
  `TokenBinding` (`InvalidProgramException` with its default message; `InvalidOperationException` with the CLR's), and
  `raiseNamedAbstractMethod`'s docstring names `newobj`.
* `Exceptions.fs`: `InvalidOperationMessages.cantInstantiateAbstractClass`, which `UnsafeAccessorDispatch`'s existing
  refusal now reads instead of its own copy of the text.
* `UnaryMetadataObjectOps.fs`: `executeNewobj` splits on every `ResolvedMetadataOperand` case with no wildcard.
  `FromMetadata` is `executeMetadataNewobj`, the old body; `ScopeMethod` fails by name (the decoder refuses such a body
  at mint, so it is unreachable until PR 2); the other four fail as bugs. After the array shortcut and the binding,
  `constructOrRefuse` asks `NewobjTarget.classify`, matches all five outcomes, raises the three refusals through
  `raiseCompilationFailure`, stops by name at `NotInstanceConstructor`, and otherwise calls `constructObject`, which is
  unchanged.
* `docs/divergences.md`: "A `newobj` that the JIT refuses fails when it is reached, and one of a non-constructor
  stops", after the section on an abstract method's `call`.
* Tests, committed first (`bcbb0f2bd`):
  * `TestNewobjTarget`: all 192 combinations of abstract, static, `RTSpecialName`, six names, abstract type and value
    type, against the order written as a list of conditions, with each outcome's count asserted (96, 48, 44, 1, 3); the
    constructor predicate's cases; and an FsCheck property over uniformly drawn 16-bit method and 32-bit type attribute
    words (10,000 cases), whose outcome counts are each asserted at least seven standard deviations below expectation.
  * `TestNewobjTargetOnHost`: the sweep, in the test host. It enumerates 35,478 methods, compiles 24,011 (686
    `AbstractMethod`, 21,257 `StaticMethod`, 94 `AbstractClass`, 1,974 `Constructs`) and skips 11,467, for each of
    which the rule says `NotInstanceConstructor`. It reads each method and type from PawPrint's image of the host's
    CoreLib by metadata token, takes value-typeness from `LoadedTypeInfo.isValueType` (checked against reflection for
    every type), asserts the exception type and message, that each exception is one `TokenBinding` names, the counts,
    and the coverage the decisions list. It takes 3 to 4 seconds.
  * `TestFabricatedNewobjRefusals`: the decisions' eight rows through `FabricatedGuest.run`, and the stop by name at a
    `newobj` of `Inst.NotCtor` and of a `.ctor` without `RTSpecialName` (a second image, run on PawPrint alone).
  * `sourcesImpure/TypeBuilderNewobjRefusals.cs`, registered in `TestImpureCases` beside its `TypeBuilder` siblings.

**Red**, at `bcbb0f2bd`: the four rule tests at the stubs' "TODO: NewobjTarget... is not implemented yet"; the
fabricated refusals test with the real runtime exiting 0 and PawPrint stopped at "stack shape: NewStatic ... at
offset 5 has 2 value(s) on its evaluation stack, but the analysis expects 1" (the static method called as a
constructor); both stop-by-name cases at "PawPrint ran the guest, which exited 0"; and the `TypeBuilder` guest at the
same stack-shape failure in `Dyn.T.Static`.

**Mutants**, each built fresh and run against the seven default-suite tests and the `TypeBuilder` guest (scratchpad
`newobj-mut`). "C" is the combinations test, "K" the constructor predicate test, "P" the property, "S" the sweep, "F"
the fabricated refusals (with the driver's exit code, where it exited), "N" the two stop-by-name cases, "G" the guest:

| Mutant | Killed by |
|---|---|
| static checked before abstract | C, P, S, F (9: the static abstract interface member) |
| not-a-constructor checked before static | C, P, S, F and G (stopped by name at the static method) |
| abstract class checked before not-a-constructor | C, P, S (the instance methods of abstract classes) |
| the abstract-class refusal dropped (abstract class and "otherwise" swapped) | C, P, S, F (10), G (7) |
| the value-type exemption dropped | C, P, F (the abstract struct's `InvalidOperationException` unhandled) |
| the metadata arm skipping the rule | F and G (the stack-shape failure), N |
| the non-constructor stop dropped | N alone |
| the constructor predicate reading the name alone | C, K, P, N (`NewFake`) |
| the rule moved into `constructObject` | survived; see the deviations |

No test was strengthened after the first run.

**The in-process sweep**, run ten times alone and ten times beside the other fixtures that compile in the test host's
JIT (`TestBoxPattern`, `TestArrayConstructor`, `TestOpcodeFaultsOnHost*`, `TestResolutionFaultsOnHost`,
`TestFabricatedAbstractMethodCall`), each a `dotnet test` process of its own: 20 of 20 passed, the sweep taking 3 to 4
seconds and each process 9 to 11 seconds of wall time.

**Suites**, built from the whole solution at `b9ddf3457`, each run on its own: `Guest` (234), `GuestSolo` (2), `GuestPure` (1185) and
`GuestImpure` (281), all passing, and the default suite (5613 passed, 37 skipped).

**Deviations.**

* The `StackShapeOfMethod` `newobj` arm is not in PR 1. The decoder still refuses a dynamic method's `newobj` at
  mint, so the arm is unreachable, and its tests (the decisions' PR 2 unit tests) need the scope arm.
* The rule moved into `constructObject` survives every test, the guests that reach the other routes included (47
  `UnsafeAccessor` and `Activator` default-suite tests, and 118 `UnsafeAccessor`, `Activator` and `Reflection` guests in
  `GuestPure` and `GuestImpure`, all passing as at the tip). It is equivalent, so no row was added. `constructObject`
  has one caller besides `executeNewobj`: `UnsafeAccessorDispatch`. The runtime's reflection invoker and `Activator` do
  not reach it, contrary to the decisions' list. `UnsafeAccessorDispatch` finds only constructors for its `Constructor`
  kind, and refuses an abstract type before it calls `constructObject`, so the rule there never answers anything but
  `Constructs`.
* A finding that bears on the decisions' reason for keeping the rule out of `constructObject`: CoreCLR's
  `[UnsafeAccessor]` constructor stub is IL whose body is a `newobj` (`vm/unsafeaccessors.cpp:908`), so its
  abstract-class refusal is the JIT's rule, value-type exemption included: real .NET constructs an abstract struct
  through one (measured above). PawPrint's `UnsafeAccessorDispatch` refuses every type flagged abstract, so it diverges
  for an abstract struct. Not changed here; asking `NewobjTarget` there is a candidate follow-up.
* The sweep compiles every static method, so 24,011 rather than the planner's 24,006: the planner's skip of a delegate
  type's non-constructors also skipped five static methods, which compile (measured above).
* The fabricated abstract struct's method returns the field its constructor stored, boxed, rather than the struct: C#
  reads an abstract sealed type as a static class, so the driver cannot name it.

### 5w: what it is

**Which stage.** 5v's decisions put 5w after the `newobj` stage. 5w is `TypeBuilder_DefineEvent`, with the Event case
of `TypeBuilder_DefineMethodSemantics`, so that a guest's `TypeBuilder.DefineEvent` and its `EventBuilder`'s
`SetAddOnMethod`, `SetRemoveOnMethod`, `SetRaiseMethod` and `AddOtherMethod` write what CoreCLR writes, and reflection
over the created type then reads it back through 5v's three arms. It un-parks `DispatchProxyEventInterface.cs`, which
stops at "Unimplemented native method (PInvokeImpl QCall!TypeBuilder_DefineEvent)" (measured again on this branch,
`main` at 9ea367d4b: scratchpad `5w/guest/DispatchProxyEventInterface-pawprint-reverted.log`). Its code depends on
nothing in the `newobj` stage's PR 2, and the guest holds one `DispatchProxy.Create`, so 5w can branch from `main`
whichever lands first.

**One feature stands behind the stop.** A throwaway arm (scratchpad `5w/throwaway.diff`, 146 lines, reverted) gave the
scope an Event table (owner, flags, name, event type), `HasSemantics.Event` with tag 0, a writer that derived an
EventMap and EventPtr with 5s's `propertyMaps` and `pointerTable`, the extension rule's Event cases, a `TypeBuilder_DefineEvent`
handler and an Event arm in `TypeBuilder_DefineMethodSemantics`. Nothing else changed. Under the Release App:

| Program | Under PawPrint with the throwaway arm |
|---|---|
| `DispatchProxyEventInterface.cs` | exits 0 (`5w/guest/DispatchProxyEventInterface-pawprint-throwaway.log`); a control with check 3 inverted exits 3 under PawPrint and on .NET 10.0.14 and 11.0.0-rc.3 |
| `5v-proxy event`, `5v-proxy event-base` | the proxy's `Changed` read back as on .NET; `event-base` lists one event (`5w/5v-proxy-*-pawprint-throwaway.log`) |
| `5v-emit all` (5v's emit probe: tokens, two adds, the 5/24/25/26-row thresholds, 20 property rows then an event's five, an interface) | output byte-identical to .NET 10.0.14's (`5w/5v-emit-pawprint-throwaway.stdout`) |
| `5w/probe` scenarios `refusals`, `flags`, `interleave`, `semantics`, `reflect` (below) | each byte-identical to .NET 10.0.14's (`5w/probe/diff-*.txt` empty) |
| `5w/probe foreign` | identical for its first eight lines; then the existing TODO for an accessor whose RID is past the module's MethodDefs (`NativeRuntimeTypeBuilder.fs:483`), which 5s decided to refuse |

With the arm reverted and the App rebuilt, the guest stops at the `DefineEvent` refusal again. So PawPrint's reader,
5v's arms, `methodSemanticsForAssociation` with `ReadWriteMetadataLookup.rowsOfKey`, and the existing TypeRef and
TypeSpec QCalls that `GetTypeTokenInternal` reaches all serve events unchanged. The stage is the write side alone.

**What CoreCLR does.** Read from the pinned source (`$DOTNET_RUNTIME_SRC_NET10`; the lines that matter are the same in
`_NET11`):

* `TypeBuilder.DefineEvent` (libraries TypeBuilder.cs:62) refuses a null or empty name
  (`ArgumentException.ThrowIfNullOrEmpty`). `RuntimeTypeBuilder.DefineEventCore` (RuntimeTypeBuilder.cs:1431) refuses a
  name whose first char is NUL (`Argument_IllegalName`), then takes the lock, then `ThrowIfCreated`, then
  `GetTypeTokenInternal(eventtype)`, which throws `ArgumentNullException` (`type`) for a null type
  (RuntimeModuleBuilder.cs:896). Nothing in that sequence refuses a byref, pointer, array, open generic or generic
  parameter type: each is a TypeSpec (RuntimeModuleBuilder.cs:908-915), a TypeBuilder of the module is its TypeDef, and
  anything else is a TypeRef. Then the QCall, with `StringMarshalling.Utf16`, so the native side sees a `ushort*` and
  reads to the first NUL.
* `TypeBuilder_DefineEvent` (vm/comdynamic.cpp:580) calls `RegMeta::DefineEventHelper` (md/compiler/helper.cpp:214),
  which calls `_DefineEvent` (md/compiler/regmeta_emit.cpp:1256). That searches only under `MDDupEvent`, which neither
  `MDDupDefault` nor vm/assembly.cpp:88 sets. It finds the type's EventMap row, adding one at the type's first event and
  before the Event row; adds the Event row; and appends it to the map's run with `AddEventToEventMap`
  (md/enc/metamodelrw.cpp:4927), which is `AddPropertyToPropertyMap` (:4892) with the event tables: direct when the map
  is the last, else through an EventPtr. As for properties, no API shows a dynamic module's EventMap or EventPtr, so
  their order is read from source and its oracle is a model of it.
* `_SetEventProps1` (regmeta_emit.cpp:1346): when the flags are not `UINT32_MAX` it clears `evReservedMask`, which is
  `evRTSpecialName` alone (0x0400, inc/corhdr.h:544), ORs in the reserved bits the fresh row already has (none), and
  stores the low sixteen bits; `-1` leaves the column 0. It writes `EventType` unless the token is nil, which the
  managed caller never passes.
* `RuntimeEventBuilder.SetMethodSemantics` (RuntimeEventBuilder.cs:48) checks the builder for null, then
  `m_type.ThrowIfCreated()`, then passes `mdBuilder.MetadataToken` to `TypeBuilder_DefineMethodSemantics`, as 5s read
  for properties. Nothing checks the method's type or module.
* Read-write metadata's `EnumInit` for `mdtEvent` (md/enc/mdinternalrw.cpp:1195-1235) has a `HasDelete` arm which
  skips an `RTSpecialName` event with a deleted name. 5v asked whether a dynamic module reaches it. It does not, for two
  reasons: `HAS_DELETE` is set only under `MDUpdateIncremental` (md/enc/metamodelrw.cpp:1136-1137), a dynamic module's
  scope is created with the dispenser's default `MDUpdateFull` (md/compiler/disp.cpp:33; vm/assembly.cpp sets no update
  mode), and `_SetEventProps1` clears `RTSpecialName` anyway.
* ECMA-335 II.22.13: `EventType` "can be null or non-null"; a name "shall index a non-empty string".

**Measured on .NET 10.0.14 and 11.0.0-rc.3**, with a throwaway probe (scratchpad `5w/probe`, `Program.cs`), built
against net10.0 with `IsDynamicCodeSupported` on, run on 10.0.14 and rolled forward to 11.0.0-rc.3.26522.148
(`5w/probe/net10-version.out`, `net11-version.out`). The two outputs are identical (`net10-all.out`, `net11-all.out`).
Tokens are read from each `EventBuilder`'s private `m_evToken`, as 5v did. A "TypeSpec probe" is
`GetTypeMetadataToken` of a fresh `Func<...>`, which shows how many TypeSpec rows the call before it added.

| Emitted (module `Ref` unless named) | Observed |
|---|---|
| `DefineEvent(null, ...)`; `""`; `"\0x"`; `"\0"` | `ArgumentNullException` (`name`); `ArgumentException` (`name`, "cannot be an empty string"); `ArgumentException` (`name`, "Illegal name.") for both |
| a null event type; null name and null type; empty name and null type | `ArgumentNullException` (`type`); the name's exception in both, so the name is checked first |
| the event after those seven refusals | `0x14000002`, after `E0`'s `0x14000001`: none consumed a token |
| `"a\0b"` | `0x14000003`, and the created type lists an event named `a` |
| event type `int&`; `int*`; `int[]`; `Action<>` (open) | each accepted, each adding one TypeSpec (probes `0x1B000003`, `0x1B000005`, `0x1B000007`, `0x1B000009`); `GetTypeMetadataToken(typeof(Action<>))` is then the TypeRef `0x01000004` |
| `Action<int>` twice | one TypeSpec (`0x1B00000A`), shared |
| `G`'s own parameter `T`; the TypeBuilder `A`; the generic TypeBuilder `G` | a TypeSpec; none; a TypeSpec |
| `EventHandler` after `GetTypeMetadataToken(typeof(EventHandler))` | the existing TypeRef `0x01000005`, no new row |
| `E1` again | `0x1400000E`, a second row |
| `SetAddOnMethod(null)`, `AddOtherMethod(null)` | `ArgumentNullException` (`mdBuilder`) |
| `AddOtherMethod(o)` twice | others `o, o` |
| after `CreateType`: `SetAddOnMethod(null)`; `SetAddOnMethod(o)` | `ArgumentNullException`; `InvalidOperationException` |
| after `CreateType`: null name; `""`; `"\0x"`; a null type; a good event | the three name exceptions; `InvalidOperationException` twice: `ThrowIfCreated` precedes the type's null check |
| the next event, on a new type `B` | `0x14000010`: the refusals after creation consumed nothing |
| flags: each of the 32 single bits, and `0`, `0x0600`, `0xFFFF`, `0xFFFE`, `0xFBFF`, `0x7FFF0600`, `-1`, `-2`, `0x1FFFF` | bits 0 to 15 kept except `0x0400`; bits 16 to 31 dropped; `0x0600` → `0x0200`; `0xFFFF` and `0x1FFFF` → `0xFBFF`; `-2` → `0xFBFE`; `-1` → `0x0000`. `IsSpecialName` follows `0x0200`. The rule is "0 for `-1`, otherwise the low sixteen bits without `0x0400`", which is not 5s's (`0xF400`) |
| `B.Zed`, `A.E1`, `C.G1`, `A.Alpha`, `B.F2`, `A.E3`; `B` created; `C.G2`, `A.E4`; `E` defined; `E.H1`, `C.G3`, `E.H2` (module `Inter`) | tokens `0x14000001` to `0x1400000B` in that order; each created type lists its own in token order (`A`: `E1`, `Alpha`, `E3`, `E4`), `D` none. EventMap rows in first-event order are `B`, `A`, `C`, `E`, and an EventPtr is needed (read, not measured) |
| a property `P` and an event `E` of one type, given in turn `g1`, `a1`, `po1`, `r1`, `eo1`, `s1`, `a2`, `g2`, `eo2`, `f1`, `f2` (11 rows), after 0, 13 and 14 filler getters (modules `S11`, `S24`, `S25`) | below 25 rows: getter `g2`, accessors `g2, s1, po1`; add `a2`, remove `r1`, raise `f2`, others `eo1, eo2`. At 25: getter `g1`; add `a1`, raise `f1`, others `eo2, eo1`. Property and event rows count together, and each association's rows reverse, raise included |
| `G<T>`'s `Changed` of type `Action<T>` | `EventHandlerType` `Action<T>` on `G<>` and `Action<Int32>` on `G<int>`, one token |
| on `T`: `Inst` (`SpecialName`, field-backed add and remove), static `Stat` (`EventHandler<int>`), `Priv` (private add), `NotDelegate` (add takes `int`), `RaiseOnly` (only a raise method), `Z` (its add defined before every event) | `GetEvents(all)` in token order; `Inst` `0x0200`, `IsSpecialName`; `Stat` `EventHandler<Int32>`; `NotDelegate`'s `EventHandlerType` null and `IsMulticast` false; `RaiseOnly`'s `EventHandlerType` throws `NullReferenceException`; `GetEvents()` is `Inst, Stat, NotDelegate, RaiseOnly, Z` (a public raise method alone makes the event public); `NonPublic \| Instance` is `Priv`; `AddEventHandler` twice, the field invoked, `RemoveEventHandler`, invoked again: 3 calls |
| a struct's event; an interface's with abstract accessors | each listed with its accessors |
| `DynDerived : DynBase`, both dynamic, each with an event `BaseE`, and `DynDerived.Own` | `GetEvents(Public \| Instance)` is `DynDerived`'s `BaseE` (`Action<Int32>`) and `Own`: the base's is hidden by name |
| `FA.X` with `FB.add_X` as add (`FB` created first) (module `Foreign`) | accepted; `FA.GetEvents` and `GetEvent("X")` throw `ArgumentException` ("Type handle 'FA' and method handle with declaring type 'FB' are incompatible") |
| `Derived.Y` with created `Base.add_FromBase` as add | listed with that add; `AddEventHandler` reaches `Base`'s field |
| module `X`'s `Wild` with module `Y`'s `y0` (`0x06000001`), `X`'s `add_Own` being `0x06000001`; `Wild2` with `y2` (`0x06000003`) | both accepted; `TX.GetEvents` throws `ArgumentOutOfRangeException` (`methodToken`, "Token 6000003 is not valid"), so `Wild2` hides `Wild` from reflection. Under PawPrint the first is admitted and the second refused at `SetAddOnMethod`, as 5s decided for properties |

5v's `5v-emit` table (two adds on one event, the 5/24/25/26-row thresholds, 20 property rows then an event's five,
`DefineEvent` and `SetAddOnMethod` after `CreateType`) stands; its two outputs are identical on both runtimes.

**What 5w must change in PawPrint.** Cited at `main` 9ea367d4b.

* `WoofWare.PawPrint/Native/NativeRuntimeTypeBuilder.fs`:
  * `TypeBuilder_DefineMethodSemantics` (`:418`) refuses any association that is not a Property token (`:451-456`), with
    a message saying no `EventBuilder` can exist. That arm becomes an Event arm over the scope's Event rows.
  * Its three accessor refusals (`:466-483`) say "reflection over the property"; they now serve events too.
  * There is no `TypeBuilder_DefineEvent` handler. `TypeBuilder_DefineProperty` (`:348-416`) is the template: the module
    from the `QCallModule`, a TypeDef parent and nothing else, the name read with `readNullTerminatedUtf16`. The event
    type is a fifth argument, an `int` token, which `typeDefOrRef` (`:48-70`) already reads as `RegMeta` does, `None` for
    a nil token. `propertyTable` (`:19`) gains an `eventTable` sibling.
  * `NativeQCall.fs:231-233` registers the two property QCalls; `TypeBuilder_DefineEvent` joins them.
* `WoofWare.PawPrint.Domain/EmitScope.fs`: `PropertyRid` (`:159-167`), `HasSemantics` with its one case and its coded
  index (`:214-224`), `PropertyRow` and `MethodSemanticsRow` (`:376-394`), the record's two lists (`:445-446`),
  `tryPropertyRid` (`:595-600`), `addProperty` (`:1008-1027`), `addMethodSemantics`, whose match on the association
  (`:1039-1040`) gains the Event case, and the readers `properties` and `methodSemantics` (`:1103-1108`).
* `EmitScopeDefinitions.fs`: `reservedPropertyFlags` and `defineProperty` (`:459-499`), where the flag rule lives;
  `defineMethodSemantics` (`:503`) is unchanged.
* `EmitScopeImage.fs`: the docstring (`:49-71`) names PropertyMap and PropertyPtr only; `propertyMaps` (`:151-169`)
  derives a map from owners and is not specific to properties; the derivation (`:299-309`) and the row counts
  (`:324-327`); `hasSemanticsSize` (`:372-373`) already counts Event rows; the table writes (`:500-522`) need the three
  event tables before PropertyMap, which ECMA-335's table order (0x12 to 0x14) puts there. `semanticsOrder`
  (`:141-149`) sorts on the coded index and needs nothing.
* `WoofWare.PawPrint.Loader/EmitScopeExtension.fs`: `associationOwner` (`:163-167`) matches the one case; the Property
  checks (`:222-242`) want Event siblings; the docstrings (`:13`, and `AddedToCreatedType`'s) name properties.
* `WoofWare.PawPrint/Native/NativeMetadataImport.fs` needs nothing, as the throwaway showed. 5v's
  `eventDefinitionsForTypeDefinition` (`:417-445`) follows the EventMap run through an EventPtr by SRM's `GetEvents`;
  `eventRow` (`:447-486`) reads the raw flags; `methodSemanticsForAssociation` (`:612`) already takes an Event parent and
  applies `ReadWriteMetadataLookup.rowsOfKey` (Domain `ReadWriteMetadataLookup.fs:45`) for a dynamic module
  (`:735-736`, `:1255-1258`), over the table's whole row count, property and event rows together. The Enum's arm reads
  no `isDynamic` (`:1246-1247`), and still needs none: EventMap has no lookup hash.
* Tests: `EmitScopeGen.fs`'s property generator (`:731-751`) and `propertyMapReplay` (`:906`), with
  `propertyMapRows`/`propertyPtrRows` (`:965-970`); `TestEmitScopeProperties.fs`, whose differential property
  (`:325`) is the one 5w extends; `TestEmitScopeImage.fs`'s round trip (`:424-450`); `TestEmitScopeExtension.fs`;
  `TestTypeBuilderPropertyRefusals.fs`; `TestImpureCases.fs`, where `DispatchProxyEventInterface.cs` is parked
  (`:559-573`) and `assertPropertyRows` (`:1784`) is the precedent for a terminal-state check.

### 5w: the options

Every option adds a `TypeBuilder_DefineEvent` handler and an Event arm in `TypeBuilder_DefineMethodSemantics`, each
checking the module and the tokens it is given, keeps the accessor refusals 5s decided (a RID past the module's
MethodDefs, a nil token, another table) for an Event association too, with messages that say "association", and leaves
the read side alone. They differ in what the emit scope holds.

**A. An Event table beside the Property table, each row naming its owner; `HasSemantics` gains `Event`; the writer
derives EventMap and EventPtr with the map rule it already has.** `EventRid` and `EventRow` (owner, flags, name, event
type) mirror `PropertyRid` and `PropertyRow`; `HasSemantics.Event` codes as `rid <<< 1 ||| 0`. `EmitScopeDefinitions.defineEvent`
holds the flag rule (0 for `-1`, otherwise the low sixteen bits without `0x0400`) and searches for nothing.
`EmitScopeImage` uses one map derivation for both kinds, `propertyMaps` generalised and renamed, and `pointerTable` over
each event's map position, as 5s's conditions require for properties. `EmitScopeExtension` refuses an Event row of a
created type and a MethodSemantics row whose event is of one, keyed on the association's owner.

* Correctness: every row above, measured with exactly this structure by the throwaway arm.
* Blast radius if wrong: events of dynamic types, which are refused today. An image whose MethodSemantics sort or
  EventMap is wrong is refused by SRM or misreads at once, in every guest that defines two events.
* Reversibility: new types and cases beside 5s's; nothing existing changes shape, except that `HasSemantics` gains a
  case, which is a change to a published package's union (Domain).
* Information: CoreCLR's rows in CoreCLR's order. 5s's surviving mutant (the sort keyed on the RID with the tag
  dropped) becomes killable.
* Size: about the throwaway's 146 lines, plus docstrings, tests and the generalised map helper.

**B. The accessors as a column of the event.** `EventRow` carries a list of (semantics, method), and the writer merges
those columns with 5s's MethodSemantics table into one sorted table.

* Correctness: the same answers, if the merge reproduces the module's row count for `rowsOfKey`.
* But it is two structures for one CoreCLR table, which 5s's decisions rejected for properties for this very reason
  ("the events stage would have to merge two columns into one sorted table"). The 25-row count would be a sum over
  the table and every event's column, and the extension rule gains an append-only column.
* Rejected unless the decider reopens 5s's choice.

**C. A union of the two association kinds in one table.** One list of map members, `Property of ... | Event of ...`,
with each kind's RID its rank among rows of its kind, and one writer path deriving both maps.

* It states the shared map rule once in the data as well as the writer.
* But each kind's RID becomes a computed rank rather than a list position, every reader of `properties` changes, and
  5s's `PropertyRow` and `addProperty`, which are public in Domain, change shape. What the two kinds share is the map
  derivation, which A shares in the writer without changing the rows.

**Recommended: A.** It is 5s's structure, decided and built; the throwaway measured it end to end; and the one rule the
kinds share (the map and pointer table) is shared in the writer, where it is a function of owners alone. B is 5s's
rejected option again. C changes 5s's public rows to share a function that A already shares.

Open questions:

1. A, B or C.
2. One PR (recommended: the handler, the tables and the Event arm are one feature, a readable event, and the throwaway
   shows the whole feature is about 150 lines), or split by layer, the Domain tables and their properties first and the
   handlers and guests second (5s's fallback cut, if review finds it too large). Either way, tests first.
3. The `EventType` column. (a) Held as a `TypeDefOrRef`, not optional, with a nil token refused by the handler as a
   PawPrint fault, since `GetTypeTokenInternal` never returns one (recommended: it keeps what CoreCLR writes, the
   image shows it to IlDump and SRM, and an unreachable state is not representable). (b) Held as `TypeDefOrRef option`,
   modelling `_SetEventProps1`'s nil case, which no caller reaches. (c) Not held, written as nil: smallest, but the image
   would no longer be CoreCLR's, and the mutant "column written nil" could not be told from the design.
4. An empty name. The managed side refuses an empty or NUL-led name before the QCall, so, unlike `DefineProperty`'s,
   the handler never sees one (measured: `"\0x"` consumes no token, `"a\0b"` stores `a`). (a) The handler refuses an
   empty name as a PawPrint fault and Domain's `defineEvent` stores what it is given, as `RegMeta` does (recommended:
   the fault states the managed caller's guarantee, and Domain stays a model of the emitter). (b) Domain refuses it
   too, as ECMA-335 II.22.13 requires a non-empty name. (c) Neither refuses.
5. Sharing with 5s's code. (a) Generalise `EmitScopeImage.propertyMaps` and `EmitScopeGen.propertyMapReplay` into one
   function each, over owners, used by both kinds (recommended: `AddEventToEventMap` is `AddPropertyToPropertyMap`
   with other tables, and one replay checked against two writers kills more mutants than two copies checked against
   themselves). (b) Copy them for events, leaving 5s's code untouched.
6. Whether the guests pin the foreign-type accessor (`FA.X` with `FB.add_X`, `ArgumentException`) and the in-range
   accessor of another module (`Wild` with `y0`), both measured identical under the throwaway arm. Recommended: yes,
   in the main guest, since 5s pinned both for properties and the arm already answers them as .NET does.
7. Whether 5w also changes the docstrings and messages that name properties only (`EmitScopeImage`'s module
   docstring, `EmitScopeExtensionViolation`'s, `HasSemantics`', the accessor refusals' "reflection over the
   property"). Recommended: yes, in the same PR, since each becomes false the moment the Event case exists.

### 5w: the oracle

Written first, and seen to fail on this branch at the `DefineEvent` refusal before the handler exists:

* `sourcesImpure/TypeBuilderDefineEvent.cs` (`IsDynamicCodeSupported` on, registered in `TestImpureCases.fs`): the
  probe's scenarios without the private-field reads except `m_evToken`, returning the number of the first failing check:
  * the refusals and their order (each name refusal, the null type, the three pairs, all on a live and a created type;
    `SetAddOnMethod(null)` before and after creation), with a token read after them showing none was consumed;
  * the embedded NUL (`a`, one token);
  * the event types: byref, pointer, array, open generic, `Action<int>` twice, a generic parameter, the module's own
    TypeBuilder and generic TypeBuilder, `EventHandler` with its TypeRef taken first, each followed by its TypeSpec
    probe, so that the TypeSpec and TypeRef rows each adds are checked;
  * the duplicate name (two rows; reflection lists the first);
  * the flag sweep (all 32 single bits and the nine specials), on a type of its own so that no other token moves;
  * the interleaving across `A`, `B`, `C`, `D`, `E`, including events added after `B` is created and on a type defined
    after other types' events;
  * `S11`, `S24` and `S25`, with a property's and an event's rows interleaved, two raises included. `P`'s first row
    precedes `E`'s, and both have RID 1, which kills 5s's surviving mutant (the sort keyed on the RID, the tag dropped)
    because SRM then refuses the image;
  * the reflection rows: `G<T>`, the six events of `T` with every binding-flag query and the add/remove/invoke count,
    the struct, the interface, `DynDerived` hiding `DynBase.BaseE`;
  * question 6's two foreign accessors, and `Derived.Y` with its base's add.

  Tokens are checked as relations where a literal would move as the guest grows (5v's rule), and as literals within
  each module of their own, as the probe does. Its terminal-state check, beside `assertPropertyRows`, reads the scope's
  Event rows (owner, name, flags, event type) and MethodSemantics rows in the order added, and, from the image SRM reads
  back, the EventMap in first-event order and the EventPtr's presence and contents for `Inter`. That check is the only
  oracle of the map's order and of the EventType column, which no guest can see.
* `DispatchProxyEventInterface.cs` is registered as passing.
* Both guests are built against net10.0 and run on .NET 10.0.14 and rolled forward to 11.0.0-rc.3 before they are
  committed, each with a control inverting one check that must exit with that check's number on both, and each run
  under PawPrint with `PAWPRINT_TEST_RUNTIME` unset and `Net11`.
* Domain models (in `TestEmitScopeProperties` or a sibling `TestEmitScopeEvents`): `defineEvent` against a model over
  generated interleavings across owners and flags drawn from the whole `int` range (`-1` and values above sixteen bits
  over-represented): each event the next RID, flags as the rule says, no search. `defineMethodSemantics` with an Event
  association: one row per call, in call order; an Event RID the scope lacks refused.
* The image round trip (`TestEmitScopeImage`): `EmitScopeGen` draws events across owners and accessor rows over both
  kinds of association in any order. For every type, SRM's `GetEvents` is the model's events of that type in
  definition order; every Event row reads back (flags, name, `EventType`'s coded index); the EventMap and EventPtr equal
  a replay of `AddChildRowDirectForParent` one event at a time (question 5's shared replay, not `pointerTable` again);
  `methodSemanticsForAssociation`'s scan gives each association's rows in the order added; SRM opening the image checks
  the MethodSemantics sort across both tags. `TypeInfo.Events` of every TypeDef equals SRM's `GetEvents`, as 5v's sweep
  asserts for CoreLib. The fixed sample asserts its coverage: an EventPtr, an EventMap order unlike TypeDef order, an
  event and a property of one RID with the property's row added first, 25 rows with a repeated add and a repeated
  raise, and a type with events created before a later type gets its first.
* The differential property against real .NET (`TestEmitScopeProperties`' existing one, extended): schedules of
  `DefineProperty`, `DefineEvent` and every accessor call of both kinds, over a few abstract types with abstract
  methods (tokens forced first, `RunAndCollect`, as now), reaching both sides of 25 rows with repeated adds, removes and
  raises, flags from the whole `int` range. For every created type, the host's `GetEvents(all | DeclaredOnly)` names,
  tokens and `Attributes`, and each event's add, remove, raise and other methods in order, equal the image's read
  through `rowsOfKey`, beside the property comparison it makes now. Each event's type is one of the module's own types,
  so that no TypeRef or TypeSpec needs modelling; the host does not read the column. The property's model of
  reflection needs `PopulateEvents`' rules for the flags it uses (5v measured them: a repeated name hidden, an event
  with only a raise method public if that method is). It covers Domain and the read rule, not the handlers' reading of
  their arguments, which the guests cover.
* `TestEmitScopeExtension`: an Event row added to a created type, and a MethodSemantics row for an event of a created
  type, are each a violation; one naming a method of a created type, for an event of a type not yet created, is not;
  a MethodSemantics row that moves because a later event's row sorts before it is not.
* `TestTypeBuilderPropertyRefusals` (or a sibling): an event accessor whose RID is past the module's MethodDefs
  (`Wild2` with `y2`) is refused by name, the message naming the association.
* Mutants, each run against both guests, the Domain models, the round trip, the extension tests and the differential
  property:
  * the event's RID off by one in the handler; the token's table byte `0x17`;
  * the property's reserved mask (`0xF400`) applied to events (the sweep's bits 12 to 15; `0xFFFF` gives `0x0BFF`);
  * `RTSpecialName` kept (the sweep's `0x0400`, `F1`-style `0x0600`);
  * `-1` masked rather than zeroed (`0xFBFF`); the high sixteen bits kept (Domain's column check, or `0x10000`);
  * a search for an existing event (the duplicate `E1`);
  * the name read past the first NUL (`a\0b`);
  * the EventPtr omitted (`Inter`'s `A`);
  * the EventMap in TypeDef order (expected to survive both guests; killed by the terminal-state check and the
    round trip's replay);
  * the EventPtr keyed on the owner's TypeDef RID rather than the map position (`Inter`, where `B`'s map row precedes
    `A`'s);
  * `HasSemantics.Event` coded with tag 1 (property and event of one RID collide; `S11`);
  * the MethodSemantics sort on the RID with the tag dropped (5s's survivor; `S11`, SRM's refusal);
  * the 25-row count taken over event rows or property rows alone (`S25`);
  * the add and remove semantics swapped, and the raise written as Other (`S11`, `T`'s `Inst` count);
  * the `EventType` column written nil, or with the wrong coded tag (the terminal-state check and the round trip only:
    no reflection reads it);
  * the Event arm of `DefineMethodSemantics` reading the RID through `tryPropertyRid` (a property and an event of one
    RID: `S11`);
  * the extension rule's `associationOwner` reading an Event RID from the Property table (`TestEmitScopeExtension`);
  * the extension check not refusing an Event row of a created type (`TestEmitScopeExtension` alone, since the managed
    caller refuses first);
  * `DefineEvent`'s extended scope not kept (both guests).

### 5w: left to later stages

* `EventBuilder.SetCustomAttribute`, through `TypeBuilder_DefineCustomAttribute`, which is refused for every parent
  today; custom attributes are their own stage.
* Modelling an accessor whose RID is past the module's MethodDefs (`ArgumentOutOfRangeException` from reflection,
  measured for `Wild2`), and the nil token's silent no-op: still refused, now for both kinds of association.
* `TypeBuilder_SetParamInfo`, the next emit stage by 5v's decisions.
* 5u's list as 5v carried it: `XmlSerializer`, the dynamic-methods class's two queries, nested dynamic types, and the
  `Signature.Init` fix.
* Whether reflection should read `TypeInfo.Events`, or it should go (5v's item), unchanged by 5w.
* The `newobj` stage's PR 2, independent of 5w.

### 5w: the decisions

Taken 2026-11-14.

* **A: an Event table beside the Property table, `HasSemantics.Event`, and an EventMap and EventPtr which the writer
  derives.** Checked against the pinned source:
  * `DefineEventCore` (RuntimeTypeBuilder.cs:1431-1460), `GetTypeTokenWorkerNoLock` (RuntimeModuleBuilder.cs:894-939),
    `TypeBuilder_DefineEvent` (vm/comdynamic.cpp:580), `_DefineEvent` and `_SetEventProps1`
    (md/compiler/regmeta_emit.cpp:1256, :1346), `AddEventToEventMap` and `AddChildRowDirectForParent`
    (md/enc/metamodelrw.cpp:4927, :4500), `RuntimeEventBuilder.SetMethodSemantics` (RuntimeEventBuilder.cs:48), the
    `HasDelete` reading (md/enc/mdinternalrw.cpp:1208, metamodelrw.cpp:1136, md/compiler/disp.cpp:33), `evReservedMask`
    (inc/corhdr.h:544) and ECMA-335 II.22.13 are each as "5w: what it is" describes them.
  * `AddEventToEventMap` differs from `AddPropertyToPropertyMap` only in the tables it names, so the map order and
    the pointer rule are 5s's. A map row is added only with its first event, so every map row has a child, and "an
    event added to a map that is not the last" is the whole condition for an EventPtr.
  * `5w/probe/net10-all.out` and `net11-all.out` are identical and hold every row of the stage's table.
  * B is rejected for 5s's reason: two structures for one CoreCLR table, and a 25-row count that becomes a sum.
  * C is rejected. It changes `PropertyRow`, `addProperty` and every reader of `properties`, all public in a
    published package, to share a derivation that is already a function of owners alone.
  * A fourth structure was weighed: the scope holding EventMap and EventPtr rows, added one event at a time as
    CoreCLR adds them. It is rejected because a map that disagrees with its events could then be represented, and
    because 5s derives the PropertyMap; the two kinds would be held two ways.
  * Conditions on A.
    * The scope holds no EventMap or EventPtr rows. The EventPtr is `pointerTable` over each event's map position.
    * `HasSemantics.Event` codes as `rid <<< 1`. The header's `Sorted` bit for MethodSemantics stays clear.
    * `EventRid` has an `internal` constructor and a `tryEventRid`, as `PropertyRid` has.
    * The three event tables are written between StandAloneSig and PropertyMap, which is ECMA-335's table order.
    * The Event arm of `TypeBuilder_DefineMethodSemantics` stores the semantics it is given, as the Property arm
      does. An Event token out of range is a PawPrint fault: an `EventBuilder` passes the token its own module gave it.
    * `HasSemantics` is a public union of a published package and gains a case. The PR body says so.
* **One PR.** The handler, the tables and the Event arm are one feature: an event that can be read back.
  * The split by layer stays the cut to make if review finds the PR too large. It is not the default, for 5s's
    reason: the new adders would ship with nothing calling them.
  * 5w branches from `main`. It needs nothing from the `newobj` stage's PR 2.
* **The `EventType` column is a `TypeDefOrRef`, not optional. The handler refuses a nil token as a PawPrint fault.**
  * `GetTypeTokenWorkerNoLock` returns a TypeSpec, a TypeBuilder's TypeDef or a TypeRef, and throws for a null type.
    It has no path that returns nil.
  * `InterfaceImplRow.Interface` and `GenericParamConstraintRow.Constraint` are already held this way, and the writer
    takes `Some` of each.
  * The handler's `typeDefOrRef` gives `None` for a nil token. The Event handler fails by name on `None`, and the
    message says that `GetTypeTokenInternal` hands out no nil token.
  * `EmitScope.addEvent` requires that the target row exists, as it requires the owner and the name.
  * An optional column is rejected: it models a branch of `_SetEventProps1` that no caller reaches, and every reader
    would then carry a case that cannot occur.
  * Writing the column as nil is rejected: the image would no longer be CoreCLR's.
  * Nothing in PawPrint reads the column. `EventDefn.make` reads the name and the flags only, and reflection takes
    `EventHandlerType` from the add method's parameter (measured: `Weird`, below).
* **An empty name: the handler refuses it as a PawPrint fault, and Domain's `defineEvent` stores what it is given.**
  * `TypeBuilder.DefineEvent` refuses a null or empty name and `DefineEventCore` refuses a leading NUL, both before
    the QCall. So an empty name at the handler means that PawPrint misread the string. The handler also refuses a
    null name pointer, as the property handler does.
  * Domain refusing it too is rejected. `defineEvent` models `RegMeta::_DefineEvent`, which stores any name, and
    `defineProperty` beside it must store an empty one.
  * Neither refusing is rejected: a misread string would then become a silent row.
  * The rule that reconciles this with the `EventType` decision: Domain holds what CoreCLR's emitter can be made to
    hold by its managed callers. Where a type can exclude an unreachable state at no cost, it does (the column).
    Where it cannot without a new wrapper type (the name), the handler states the caller's guarantee as a fault.
* **One map derivation and one replay serve both kinds.**
  * `EmitScopeImage.propertyMaps` already takes owners alone, and `EmitScopeGen.propertyMapReplay` already takes
    TypeDef RIDs alone. Each is renamed for the rule it holds, a map table derived from its members' owners, and its
    docstring names both `RegMeta::DefineProperty` and `_DefineEvent`. `propertyMapRows` and `propertyPtrRows` take
    the table to read.
  * Copies for events are rejected: a second copy of the replay would be checked only against a second copy of the
    writer's rule.
  * Nothing else is shared. The flag rules differ (`0xF400` and `0x0400`), and the rows and RIDs stay two types. No
    abstraction over "a member of a map" is introduced.
* **Both foreign accessors are pinned in the main guest.**
  * `FA.X` with `FB.add_X`, `FB` created first: the throwaway arm gave real .NET's `ArgumentException` from both
    `GetEvents` and `GetEvent`. 5s gave its counterpart a guest of its own only because the answer was then unknown.
  * `Wild` with `y0` goes in a module of its own, without `Wild2`. Measured for this decision on .NET 10.0.14 and
    11.0.0-rc.3 (scratchpad `5w-decider/probe`): `TX.GetEvents` lists `Wild` with add method `TX.add_Own`, which is
    `X`'s MethodDef 1.
  * `Wild2` with `y2` goes in the refusal fixture.
* **Wording that names only properties is corrected in this PR.** Each of these becomes false when the Event case
  exists: `EmitScopeImage`'s module docstring; `HasSemantics`' docstring; `EmitScopeExtensionViolation`'s remarks and
  `AddedToCreatedType`'s; the comment on `associationOwner`; the three accessor refusals' "reflection over the
  property"; the comment and the last arm's message in `TypeBuilder_DefineMethodSemantics`; and the docstrings of
  `TestTypeBuilderPropertyRefusals` and of the differential property.
  * No test file is renamed. A rename would hide the changes to tests that review must read. Each fixture's
    docstring says what it now holds.
* **Corrections to the other 5w sections.**
  * Question 6 says both foreign accessors were "measured identical under the throwaway arm". For `Wild` only the
    acceptance of `SetAddOnMethod(y0)` was measured. The probe defines `Wild2` on the same type, so real .NET's
    `TX.GetEvents` throws and PawPrint stops before it. The answer for `Wild` alone is the measurement above. Under
    PawPrint it is not yet measured; the guest measures it.
  * The oracle says the differential property "needs `PopulateEvents`' rules for the flags it uses". With every
    binding flag set and `DeclaredOnly`, the only rule is the one for a repeated name. An event with no accessor is
    listed (measured, `5w-decider/probe`: `N1`). No rule about a vtable slot exists for events.
  * The mutant "the Event arm of `DefineMethodSemantics` reading the RID through `tryPropertyRid`" is two mutants
    (below). `S11` kills only the one that builds a Property association.
  * The logs of the PawPrint runs under `5w/` hold no exit code: `pp.sh` prints it to the terminal. The exit codes in
    the stage's table are the planner's reading. The PR measures and records them.
  * "The stage is the write side alone" stands for the code. For the tests, 5w's guest is the first check of 5v's
    Enum arm through an EventPtr, as 5v's decisions said.
  * Option A's "nothing existing changes shape" leaves out one thing: `assertPropertyRows` matches on
    `HasSemantics` with one case. It gains an Event case that fails by name, since `Props` has no event.
* **The oracle is sufficient with these changes.** In this order:
  * Before any commit, both guests are built against net10.0 and run on .NET 10.0.14 and 11.0.0-rc.3, each with a
    control that inverts one check, as the stage says.
  * Commit 1 holds what compiles today, and its failing run is recorded:
    * `TypeBuilderDefineEvent.cs`, registered;
    * `DispatchProxyEventInterface.cs`, registered as passing;
    * the `Wild2` refusal test.

    Each stops at "Unimplemented native method (PInvokeImpl QCall!TypeBuilder_DefineEvent)".
  * Commit 2 holds the smallest Domain surface that lets the other tests compile: `EventRid`, `EventRow`,
    `HasSemantics.Event`, and `addEvent` and `defineEvent` with bodies that fail by name. With it come the Domain
    models, the round trip, the differential property and the extension tests, and their failing run is recorded.
  * Then Domain, then the handlers.
  * The guest is the stage's list, with these changes.
    * It gains `Weird`: event type `int`, add method taking `Action`, `EventHandlerType` `Action`. It is the one row
      which shows that reflection does not read the column.
    * `Wild` is in a module of its own, as above.
    * Its terminal-state check reads four scopes. `Ref`: every Event row's owner, name, flags and event type, with
      the kind and RID of each type (the two `Action<int>` rows hold one TypeSpec RID; `OfA` holds `A`'s TypeDef;
      `EH` holds TypeRef 5). `Flags`: the flags column of all 41 rows. `Inter`: the EventMap in first-event order
      (`B`, `A`, `C`, `E`) and the EventPtr's contents, from the image that SRM reads back. `S11`: the
      MethodSemantics rows in the order added, with each association's kind.
  * The Domain models go in a new `TestEmitScopeEvents`. Beside the stage's two, `addEvent` refuses an owner, a name
    offset or an event type that the scope lacks.
  * The round trip is as the stage has it, with these changes.
    * The generator draws all six semantics for either kind of association. `_DefineMethodSemantics` takes any, and
      neither the sort nor the read rule depends on the semantics.
    * The fixed sample also asserts an event type of each of the three kinds.
  * The differential property is as the stage has it, with these changes. The shape was measured for this decision
    on both runtimes (`5w-decider/probe`): abstract methods returning `int` serve as every kind of accessor.
    * Event names are drawn from a small pool, so that names repeat within a type. The model lists the first event
      of each name, in token order, whatever its accessors. Property names stay distinct.
    * It reads each event's add, remove and raise methods and its other methods with non-public methods included.
      It does not read `EventHandlerType`, which throws when there is no add method.
    * Its fixed sample asserts, each above a stated count: an event with two rows of one semantics in a module
      below 25 rows, and one in a module of 25 or more; an EventPtr; a property and an event of one RID, each with
      a row; a repeated event name; flags of `-1`; an event with no accessor.
  * `TestEmitScopeExtension` is as the stage has it, with these changes.
    * A history that changes an Event row's flags or event type is `RowChanged`.
    * The generated property compares each created type's events and their accessors, as it does properties.
  * The refusal test goes in `TestTypeBuilderPropertyRefusals`, beside the property's.
  * Mutants: the stage's list, with these changes. Each is built fresh, after the work is committed.
    * `-1` recognised on the low sixteen bits alone, so that `0xFFFF` and `0x1FFFF` store 0 (the sweep; the Domain
      model; the differential property).
    * The Event arm checking the RID against the Property table but building an Event association (any module with
      more events than properties: `Ref`, `Flags`, `Inter`).
    * The Event arm building a Property association (`S11`).
    * The writer deriving the EventMap from the properties' owners (the replay; `Inter`).
    * The extension rule no longer comparing the earlier scope's Event rows (`TestEmitScopeExtension` alone).
    * The extension rule keyed on the accessor's owner for an event (`FA.X`, since `FB` is created first;
      `TestEmitScopeExtension`).
    * The handler reading a TypeSpec token as a TypeRef (`Ref`'s terminal-state check, or the handler's refusal of a
      row that does not exist).
    * Four guards have no killer by construction, since no managed caller reaches them: the refusals of an empty
      name, of a null name pointer, of a nil event type, and of a parent that is not a TypeDef. Each is recorded as
      surviving, with that reason.
    * "As built" records, for each mutant, which test killed it. A mutant that survives where a killer is named
      above means a test is strengthened and the run repeated.
  * At the tip, the default suite and the four Guest suites run, each as its own invocation, built from the whole
    solution. Both guests also run under PawPrint with `PAWPRINT_TEST_RUNTIME=Net11`.
* **PR title:** "A TypeBuilder's events (type-builder stage 5w)".
* **Additions to "left to later stages".**
  * An event type that is a TypeBuilder of another dynamic module, or an `EnumBuilder`. Each goes through
    `GetTypeTokenInternal` and the existing TypeRef QCalls, and neither is measured for events.
  * A name that `RegMeta`'s conversion to UTF-8 changes, such as a lone surrogate. Every QCall that takes a name
    shares the question, and no stage has measured it.
  * A MethodSemantics row whose semantics does not belong to its association's kind, such as a getter of an event.
    The scope admits it, as CoreCLR's table does; no managed caller writes one.
  * The four guards above are assertions about the managed callers. A test that drives a handler with arguments
    which no caller passes would be their check; no fixture does that for the emit QCalls today.

### 5w: as built

Built 2026-11-14 as decided above, in the decisions' order: the guests measured, then the tests that compile today
(`b7816ee89`), then the smallest Domain surface with the Domain tests (`840f161ba`), then Domain (`600a2baeb`), then the
handlers (`dd131b3dd`). Probes, logs and the mutation harness are in scratchpad `5w-impl`.

Each guest was built against net10.0, run on 10.0.14 and rolled forward to 11.0.0-rc.3.26522.148, and run under the
Debug App at the handlers' commit (which runs on the host's .NET 10), each with a control inverting check 3:

| Guest | .NET 10.0.14 | .NET 11.0.0-rc.3 | PawPrint | Control (check 3 inverted): .NET 10, .NET 11, PawPrint |
|---|---|---|---|---|
| `sourcesImpure/TypeBuilderDefineEvent.cs` (47 checks) | 0 | 0 | 0 | 3, 3, 3 |
| `sourcesImpure/DispatchProxyEventInterface.cs` (8 checks) | 0 | 0 | 0 | 3, 3, 3 |

Both pass as registered tests with `PAWPRINT_TEST_RUNTIME` unset and `Net11`, the terminal-state check included.

* Domain (`WoofWare.PawPrint.Domain/EmitScope.fs`, `EmitScopeDefinitions.fs`, `EmitScopeImage.fs`): `EventRid` with an
  `internal` constructor and `tryEventRid`; `EventRow` (owner, flags, name, and `EventType`, a `TypeDefOrRef`);
  `HasSemantics.Event`, coded `rid <<< 1`; `addEvent`, which requires the owner, the name and the event type to exist and
  the flags to fit two bytes; `events`; and `defineEvent`, which stores 0 for `-1` and otherwise the low sixteen bits
  without `evReservedMask` (0x0400), with no search. The writer's map derivation is renamed `ownerMaps` and serves both
  kinds; EventMap, EventPtr and Event are written between StandAloneSig and PropertyMap. The `Sorted` bit for
  MethodSemantics stays clear.
* Loader (`EmitScopeExtension.fs`): the earlier scope's Event rows are compared, an Event row of a created type is
  refused, and an accessor row keys on its event's owner.
* Interpreter (`Native/NativeRuntimeTypeBuilder.fs`, `Native/NativeQCall.fs`): `TypeBuilder_DefineEvent`, with the four
  guards the decisions name as PawPrint faults; the Event arm of `TypeBuilder_DefineMethodSemantics`; the accessor
  refusal's message says "association".
* Tests: `TypeBuilderDefineEvent.cs` and its terminal-state check `assertEventRows` (`Ref`'s 16 Event rows, `Flags`' 41
  flags, `Inter`'s EventMap `(3,1) (2,3) (4,7) (6,10)` and EventPtr `1 5 2 4 6 8 3 7 10 9 11`, `S11`'s eleven
  MethodSemantics rows); `DispatchProxyEventInterface.cs` registered as passing; the `Wild2` refusal in
  `TestTypeBuilderPropertyRefusals`; `TestEmitScopeEvents` (the Domain models, the probe's 41 measured flag values, and
  `addEvent`'s refusals); events in `EmitScopeGen`, the image round trip, `Assembly.read`'s `TypeInfo.Events`, the
  extension tests and the differential property.
* Red at `b7816ee89`: both guests failed with "GuestFailureException : Unimplemented native method (PInvokeImpl
  QCall!TypeBuilder_DefineEvent)", and the event refusal test with `Expected: String containing "TODO:
  TypeBuilder_DefineMethodSemantics"` but that message. Red at `840f161ba`: of the 65 tests in `TestEmitScopeEvents`,
  `TestEmitScopeProperties`, `TestEmitScopeImage`, `TestEmitScopeExtension` and `TestTypeBuilderPropertyRefusals`, 35
  failed: 22 at "TODO: EmitScope.events: the scope holds no Event table yet", 8 at "TODO:
  EmitScopeDefinitions.defineEvent", 4 at "TODO: EmitScope.addEvent of TypeDef ...", and the event refusal test at the
  unimplemented QCall. The guests still stopped there; `TypeBuilderDefineProperty.cs` passed.
* The differential property's fixed sample (150 cases) reached: a property with two rows of one semantics from 25 rows
  70 times and below 25 rows 42 times; the same for an event 70 and 44 times; a PropertyPtr 42 times; an EventPtr 47
  times; a property and an event of one RID each with a row 114 times; a repeated event name 79 times; flags of `-1` 75
  times; an event with no accessor 81 times. Each is asserted above 20.
* Mutants, each built fresh after the work was committed and run against the three `TypeBuilderDefine*`/`DispatchProxy`
  guests (`TypeBuilderDefineEvent.cs`, `DispatchProxyEventInterface.cs`, `TypeBuilderDefineProperty.cs`) and the 65 tests
  of the five fixtures above. A guest's number is the exit code of `TypeBuilderDefineEvent.cs`, its first failing check;
  "round trip" is "every row reads back at the RID the scope gave it", "differential" the differential property, "Domain
  model" `TestEmitScopeEvents`:

  | Mutant | Killed by |
  |---|---|
  | The event's RID off by one in the handler | both guests (the accessor call names no Event row); the refusal test |
  | The token's table byte `0x17` | both guests (the accessor call names no Property row); the refusal test |
  | The property's mask `0xF400` applied to events | the guest (28, `Flags`); the Domain model and the 41 measured values; the differential |
  | `RTSpecialName` kept | the same three |
  | `-1` masked rather than zeroed | the same three |
  | The high sixteen bits kept | the guest (`addEvent` refuses flags 0x10000); the same unit tests |
  | A search for an existing event | the guest (18, the duplicate `E1`); the Domain model; the differential |
  | The EventPtr omitted | the guest (20); the round trip; `Assembly.read`; the differential |
  | The EventMap in TypeDef order | the terminal-state check (`Inter`'s map rows); the round trip's replay. The guest's checks, and the differential, survive it, as the stage expected |
  | The EventPtr keyed on the owner's TypeDef RID | the guest (30, `Inter`); the round trip; `Assembly.read`; the differential; the generated extension property |
  | `HasSemantics.Event` coded with tag 1 | both guests (21); the extension tests (the moved rows; the generated property); the differential; the Domain accessor model |
  | The MethodSemantics sort on the RID, the tag dropped (5s's survivor) | the guest (SRM: "Metadata table MethodSemantics not sorted"); the round trip; the differential; the extension tests |
  | The 25-row count taken over one kind's rows alone | the guest (33, `S25`) alone: the differential applies `rowsOfKey` to the whole table itself |
  | An event's add and remove swapped | both guests (21, `Acc`); no unit test, since no unit test runs the handler |
  | An event's raise written as Other | the guest (31, `S11`) alone |
  | The EventType column written nil | the round trip and the index-width round trips alone: no reflection reads the column |
  | The EventType column with a TypeSpec's tag written as a TypeRef's | the round trip alone |
  | The Event arm checking the RID against the Property table but building an Event association | both guests, at `Ref`'s first accessor call, since `Ref` has no property; the refusal test |
  | The Event arm building a Property association | the same, for the same reason: `Ref` fails before `S11` is reached |
  | `associationOwner` reading an Event RID from the Property table | both guests (index out of range); the generated extension property; the history property |
  | The extension check not refusing an Event row of a created type | `TestEmitScopeExtension` alone ("an event added to a created type is refused"; the generated property), since the managed caller refuses first |
  | `DefineEvent`'s extended scope not kept | both guests (7); the refusal test |
  | `-1` recognised on the low sixteen bits alone | the guest (28); the Domain model and the 41 measured values; the differential |
  | The EventMap derived from the properties' owners | all three guests; the round trip; `Assembly.read`; the differential |
  | The extension rule no longer comparing the earlier scope's Event rows | `TestEmitScopeExtension` alone ("a changed event is reported at its RID"; the history property) |
  | The extension rule keyed on the accessor's owner for an event | the guest (`Foreign`: "AddedToCreatedType (MethodSemantics, 1, TypeDefRid 3)"); `TestEmitScopeExtension` |
  | The handler reading a TypeSpec token as a TypeRef | the guest (the handler's refusal: "the event type 0x01000004 names no row") |
  | The refusal of an empty name removed | survives: no managed caller passes one |
  | The refusal of a null name pointer removed | survives, for the same reason |
  | The refusal of a nil event type removed | survives: `GetTypeTokenInternal` hands out no nil token |
  | The refusal of a parent that is not a TypeDef removed | survives: the managed caller passes only its TypeBuilder's |
  | `addEvent` not checking the event type (beyond the decisions) | the Domain model's refusal test alone |
  | `addEvent` not checking the flags' width (beyond the decisions) | the same |

  No mutant with a named killer survived, so no test was strengthened after the battery.
* Suites, built from the whole solution at `dd131b3dd` in a detached worktree while the mutants ran here (the later
  commit changes only this plan), each run on its own: the default suite (5623 passed, 37 skipped), `Guest` (234),
  `GuestSolo` (2), `GuestPure` (1185) and `GuestImpure` (283), all passing; both 5w guests under
  `PAWPRINT_TEST_RUNTIME=Net11` (2), passing.
* Deviations from the decisions, and findings:
  * `assertEventRows` is in the second commit, not the first: it reads `EmitScope.events` and `HasSemantics.Event`,
    which do not compile before the Domain surface exists. The guest itself, registered, is in the first.
  * The Domain surface of the second commit also has `EmitScope.events` and `tryEventRid`, failing by name, beyond the
    decisions' list, because the tests read them; and the Event arms of `addMethodSemantics` and of the extension rule's
    `associationOwner`, failing by name, because a match without them does not compile.
  * `EmitScopeGen` gained `MethodSemanticsRun`, several accessors of one association at once, in the Domain commit: with
    single rows none of the 200 fixed-sample scopes reached 25 MethodSemantics rows with an event given two adds, which
    the round trip's coverage asserts. The coverage assertion itself is as first committed.
  * "A type with events created before a later type gets its first" is asserted in the round trip's fixed sample as a
    type defined after the module's first event that then gets events of its own, since a scope has no notion of a
    created type; the guest's `Inter` (events on `E`, defined after `B` is created) is the case with creation.
  * The mutant "the name read past the first NUL" was not built. The handler gets no length, and the shared
    `readNullTerminatedUtf16` stops at the first NUL by construction; a mutant reading past it would read past every
    name's terminator, so it could not single out `a\0b`.
  * The two `tryPropertyRid` mutants are both killed at `Ref`'s first accessor, not at `S11` as the decisions expected
    for the second: `Ref` has no property, so the Property check fails before an association of either kind is built.
  * `EmitScopeExtensionViolation`'s remarks needed no change: they already speak of an association, not a property. Only
    one of the three accessor refusals said "reflection over the property"; it now says "association".
  * `describeRows` (`EmitScopeGen`) reads Event rows, so the history property checks them; Property rows it does not
    read, as before.
