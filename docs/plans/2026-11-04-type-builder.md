# Emitting types with `TypeBuilder`

Status: option A chosen (2026-11-04); stage 0 implemented (#2093); stage 1 implemented (#2101); stage 2 implemented (#2109); stage 3a implemented (#2115); stage 3b implemented (#2124); stage 3c implemented (#2135); stage 3d implemented (#2141); stage 3e implemented (#2144); sorted-table rows of a created type admitted to move (3f, #2154); stage 4a implemented (#2159); stage 4b implemented (#2160); stage 4c implemented (#2162), and with it the issue's guest passes; stage 4d implemented (#2163); stage 5a implemented (#2164); stage 5b implemented (#2165); stage 5c implemented (#2166); stage 5d implemented (#2167); stage 5e implemented (#2176); stage 5f implemented (#2191); stage 5g implemented (#2192); stage 5h implemented (#2195); stage 5i implemented (#2197); stage 5j implemented (#2213); stage 5k in progress. Tracking issue: #2091. Follows
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
  MethodDef parent is valid only for a vararg call site (ECMA-335 II.22.25), and no CoreLib path passes
  a TypeDef; both would be untested.
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
  token taken from the parent; and an AssemblyRef added for the module itself. The name left
  unescaped is not a mutant any guest can catch yet; see below.

### 5j: left to later stages

* A nested type of the module, once `DefineNestedType` adds `NestedClass` rows, and a field of an
  instantiation of a created generic type, once the scope has `GenericParam` rows.
* `TypeString`, which renders `RuntimeType.FullName`, does not escape a name's reserved characters as
  `TypeNameBuilder::EscapeName` does: a created type named `Odd+One` is `Odd\+One` on .NET 10 and
  `Odd+One` under PawPrint. So no guest yet asks `GetTypeRef` for an escaped name of its own module; the
  host oracle covers the unescaping.

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
  method's, the TypeSpec parent read as a TypeRef, and the MemberRef added without the find.

### 5k: left to later stages

* A member of an instantiation of the module's own generic `TypeBuilder`, once `CreateType` can add its
  `GenericParam` rows.
