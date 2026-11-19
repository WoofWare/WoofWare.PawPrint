# `Type.GetInterfaces` on a generic definition or an open construction (issue #2309)

Status: decided (see "Decisions"); the first three of five pull requests are built: the closed order (#1318, "As built
(PR 1)", merged as #2322), the definition-level interface map ("As built (PR 2)", merged as #2337, on top of #2331,
which shares the substitution comparison's work) and open receivers (#2309, "As built (PR 3)"); the other two are not
started. The study below was measured 2026-11-16 on `main` at `c50428b33` (#2307),
with a throwaway handler described under "PawPrint with a throwaway", since reverted.

Scope is the one QCall `RuntimeTypeHandle_GetInterfaces`, for every receiver it can be given. The issue asks only about
open receivers, but the study found that the closed receivers' answer is wrong today too (in order, and in dropping
duplicates), and that the two are one rule, so the plan covers both. The neighbours #2303 (`GetInterfaceMap` on an open
receiver) and #2295 (the cast rule's open value types) are not in scope; what this changes for each is under
"Neighbours".

Real .NET means 10.0.14 and 11.0.0-rc.3. Each probe was a throwaway C# console program with a mode per measurement,
published self-contained for osx-arm64 against .NET 10 and, from the same source, against .NET 11; each was run on its
own runtime and under PawPrint (which then runs that runtime's CoreLib). PawPrint means a Release build of the App.
Nothing was kept (see "Probe files" at the end). Each claim is marked *measured* or *read*.

## What it is

### Who reaches it

`RuntimeType.GetInterfaces()` (`RuntimeType.CoreCLR.cs:2696-2700` on .NET 10, `:2694-2698` on .NET 11) asks the
type's cache for its interface list, which `RuntimeTypeCache.PopulateInterfaces` (`:1019-1060` on both) fills:

```csharp
if (!RuntimeTypeHandle.IsGenericVariable(declaringType))
{
    Type[] ifaces = RuntimeTypeHandle.GetInterfaces(declaringType);
    foreach (Type iface in ifaces)
    {
        RuntimeType interfaceType = (RuntimeType)iface;
        if (filter.RequiresStringComparison())
        {
            if (!filter.Match(RuntimeTypeHandle.GetUtf8Name(interfaceType)))
                continue;
        }
        Debug.Assert(interfaceType.IsActualInterface);
        list.Add(interfaceType);
    }

    if (ReflectedType.IsSZArray)
    {
        ...  // AddSpecialInterface for IList<T>, IReadOnlyList<T> and their generic parents
    }
}
```

The wrapper `RuntimeTypeHandle.GetInterfaces(RuntimeType)` (`RuntimeHandles.cs:562-575` on both) returns `[]` for a
TypeDesc without calling the QCall, and otherwise passes the MethodTable (read). Four things follow (read):

- The list is the QCall's array in the QCall's order, unfiltered for `GetInterfaces()`. There is no deduplication on
  the managed side; the cache merge (`MergeWithGlobalListInOrder`, `:469-497`) swaps in cached equal entries without
  removing any.
- `GetInterface(name)` goes through the same QCall and then calls `RuntimeTypeHandle.GetUtf8Name` on *every*
  interface in the list, so an open receiver's open interfaces reach `GetUtf8NameInternal`.
- An SZ array's five generic interfaces are appended in managed code, after `IsAssignableFrom` and `GetUtf8Name` of
  each (`AddSpecialInterface`, `:991-1013`). The QCall itself answers `System.Array`'s map for any array.
- A type variable never reaches the QCall (`IsGenericVariable`), nor does a byref or pointer (`IsTypeDesc`).

### What the QCall returns

`RuntimeTypeHandle_GetInterfaces` is `vm/runtimehandles.cpp:518-559`, textually identical on both runtimes (read, by
diffing the two):

```cpp
INT32 ifaceCount = pMT->GetNumInterfaces();
if (ifaceCount > 0)
{
    ...
    MethodTable::InterfaceMapIterator it = pMT->IterateInterfaceMap();
    while (it.Next())
    {
        OBJECTREF refInterface = it.GetInterface(pMT)->GetManagedClassObject();
        gc.Types->SetAt(i, refInterface);
        i++;
    }
    result.Set(gc.Types);
}
```

So the answer is the receiver's interface map, entry for entry, in map order, duplicates included.
`InterfaceMapIterator::GetInterface(pMTOwner)` (`methodtable.cpp:8922-8944` on .NET 10, `:8821-8846` on .NET 11; the
same apart from a contract macro, read) resolves a *special marker* entry: if the entry is a generic type definition and
the owner's `MayHaveOpenInterfacesInInterfaceMap` flag is clear, it loads the interface instantiated over
`pMTOwner->GetSpecialInstantiationType()` instead. So whatever compressed form the map holds, the QCall hands back the
exact instantiation; #2295's flag decides only what the map stores, not what this QCall answers (read; the measurements
below agree).

### How the map is ordered

`MethodTableBuilder` builds the map (read, .NET 10 line numbers; on .NET 11 the text of every function named here is
the same, 4 lines earlier for those before `:620` and 278 lines later for `LoadExactInterfaceMap`):

- **The parent's map first, verbatim.** `LoadApproxInterfaceMap` (`methodtablebuilder.cpp:531-619`) calls
  `ExpandApproxInheritedInterfaces` (`:472-524`) before anything else: "First inherit all the parent's interfaces. This
  is important, because our interface map must list the interfaces in identical order to our parent" (`:582-583`). The
  parent's map is computed with the parent read as an open type (`#InterfaceMap_SupersetOfParent`, `:482-498`), so it
  is the same at every level of every instantiation.
- **Then each declared interface, in InterfaceImpl order, followed by its own parents, depth-first**
  (`ExpandApproxDeclaredInterfaces`, `:439-468`, and `ExpandApproxInterface`, `:314-432`: "adding the current node BEFORE
  expanding the parents of the current node"). An interface already in the map is not added again (`:339-373`, by
  `MetaSig::CompareTypeDefsUnderSubstitutions`), and the comparison is made under substitutions, in the vocabulary of
  the generic definition being built, not on closed types.
- **CoreLib's value types and interfaces expand nothing** (`:416-431`, "#SpecialCorelibInterfaceExpansionAlgorithm": the
  C# compiler has already listed every interface on the type).
- **The exact map keeps the definition's shape.** `LoadExactInterfaceMap` (`:9553` on) loads exact instantiations and,
  when they collide, falls back to technique 2 (`:9840` on), whose comment states the rule: "an exact instantiation's
  interface map must have entries that match the ordering of the interface map in the generic case", so that "there
  will be two entries for I<string> in the final interface map for D<string>" (`:9847`).

PawPrint already models this order, for closed classes and structs: `ConcreteInterfaceDispatch.interfaceMapOf`
(`WoofWare.PawPrint.TypeSystem/ConcreteInterfaceDispatch.fs:139-305`) builds the parent's entries first, then each
declared row and its parents depth-first (`expand`, `:222-302`), deduplicating by "the same instantiation *of the
definition*" (`findExisting`, `:243-264`, which compares the entries' definition-rooted `Substitution`s), and skipping
expansion for a CoreLib value type (`expandParents`, `:206-214`). `TestInterfaceDispatchMap.fs` compares its order with
the host's `GetInterfaces()` over CoreLib and a generated corpus. It answers only for a closed `ConcreteTypeHandle`.

### Measurements on real .NET

The probe's fixtures (C#; the ones C# would refuse, such as two directly declared rows that unify, are reached through
inheritance instead):

```csharp
public interface IPair<A, B> { }
public class Own<T> : IEnumerable<T>, IPair<T, int> { ... }
public class Derived<U> : Own<U[]>, IComparable<Derived<U>> { ... }
public class Plain<T> : IDisposable { ... }
public interface IA<T> { }
public interface IB { }
public interface IC<T> : IA<T>, IB { }
public class BaseA<T> : IA<T> { }
public class Mixed<T> : IA<int>, IPair<T, int>, IB, IA<T[]> { }
public class Self<T> : IEquatable<Self<T>> { ... }
public struct SSelf<T> : IEquatable<SSelf<T>>, IComparable<SSelf<T>> { ... }
public struct SOwn<T> : IA<T>, IC<T> { }
public interface IX<T> : IC<T>, IEquatable<IX<T>> { }
public class Dup<T, U> : BaseA<T>, IA<U> { }
public class Redecl<T> : BaseA<T>, IA<T>, IB { }
public class Mix2<T> : BaseA<int>, IA<T> { }
public class Outer<T> { public class Inner<U> : IPair<T, U> { } }
public class Rev<T> : IC<T> { }
public class DeepBase<T> : Rev<T[]>, IA<List<T>> { }
public class ClosedBase : IB { }
public class ClosedDerived : ClosedBase, IA<string> { }
public class TwoBase<T> : IA<T>, IB { }
public class TwoDerived<T> : TwoBase<T>, IC<T> { }
public interface ISelfIface<TSelf> : IEquatable<TSelf>, IA<ISelfIface<TSelf>> where TSelf : ISelfIface<TSelf> { }
public struct SCrtp<T> : ISelfIface<SCrtp<T>> { ... }
```

In the table, `T` is always the receiver's (or the named definition's) own variable, `K` is `Dictionary<,>`'s first
variable, and `#def` marks an argument that *is* the generic definition (`IsGenericTypeDefinition` true), which is how
CoreCLR represents a definition applied to its own variables. All measured, the same on both runtimes:

| receiver | `GetInterfaces()` |
|---|---|
| `Plain<>` | `IDisposable` |
| `Own<>` | `IEnumerable<T>`, `IEnumerable`, `IPair<T,int>` |
| `Derived<>` | `IEnumerable<T[]>`, `IEnumerable`, `IPair<T[],int>`, `IComparable<Derived#def>` |
| `Derived<>.BaseType`, i.e. `Own<T[]>` over `Derived`'s `T` | `IEnumerable<T[]>`, `IEnumerable`, `IPair<T[],int>` |
| `List<>` | `IList<T>`, `ICollection<T>`, `IEnumerable<T>`, `IEnumerable`, `IList`, `ICollection`, `IReadOnlyList<T>`, `IReadOnlyCollection<T>` |
| `IList<>` | `ICollection<T>`, `IEnumerable<T>`, `IEnumerable` |
| `Mixed<>` | `IA<int>`, `IPair<T,int>`, `IB`, `IA<T[]>` (closed and open rows, in row order) |
| `Self<>`, `SSelf<>` | `IEquatable<Self#def>`; `IEquatable<SSelf#def>`, `IComparable<SSelf#def>` |
| `SOwn<>` (a value type outside CoreLib) | `IA<T>`, `IC<T>`, `IB` |
| `IX<>` | `IC<T>`, `IA<T>`, `IB`, `IEquatable<IX#def>` |
| `Dup<,>` | `IA<T>`, `IA<U>` |
| `Dup<K,K>` (an open construction) | `IA<K>`, `IA<K>` |
| `Dup<int,int>`; `Dup<int,string>` | `IA<int>`, `IA<int>`; `IA<int>`, `IA<string>` |
| `Redecl<>`; its base `BaseA<T>` | `IA<T>`, `IB`; `IA<T>` |
| `Mix2<>` | `IA<int>`, `IA<T>` |
| `Outer<>.Inner<>` | `IPair<T,U>`, both `Inner`'s own variables |
| `Rev<>`; `DeepBase<>`; `DeepBase<>.BaseType` | `IC<T>`, `IA<T>`, `IB`; `IC<T[]>`, `IA<T[]>`, `IB`, `IA<List<T>>`; `IC<T[]>`, `IA<T[]>`, `IB` |
| `ClosedDerived`; `TwoDerived<int>` | `IB`, `IA<string>`; `IA<int>`, `IB`, `IC<int>` |
| `ISelfIface<>`; `SCrtp<>` | `IEquatable<T>`, `IA<ISelfIface#def>`; `ISelfIface<SCrtp#def>`, `IEquatable<SCrtp#def>`, `IA<ISelfIface<SCrtp#def>>` |
| `List<K>`; `typeof(List<>).MakeGenericType(List<>'s T)` | `List<>`'s list over `K`; the same object as `typeof(List<>)`, with `List<>`'s list |
| `Nullable<>`, `KeyValuePair<,>`, `Lazy<>` | (none) |

Identity (measured, both runtimes): `typeof(List<>).GetInterfaces()[0].GetGenericArguments()[0]` is reference-equal to
`typeof(List<>).GetGenericArguments()[0]`; the same for `IList<>` with its own `T` (so a CoreLib interface's special
marker entry comes back over the *receiver's* variable, not the interface's own); the first interface is
reference-equal to `typeof(IList<>).MakeGenericType(List<>'s T)`, reports `ContainsGenericParameters` and not
`IsGenericTypeDefinition`; `IX<>`'s `IEquatable<>` argument is reference-equal to `typeof(IX<>)`.

**Measured, the order rule, by interfaces missing from the metadata.** C# lists every interface's parents on every
type, so the fixtures above cannot tell the depth-first expansion from appending each declared row's parents at the end,
or from expanding nothing. A second probe compiled its classes against a library whose `IC<T>` and `IE<T>` extend
nothing, and ran against a second version of it where `IC<T> : IA<T>, IB` and `IE<T> : IA<T[]>`, so the classes'
InterfaceImpl rows omit those parents:

| receiver (rows as compiled) | `GetInterfaces()` against the second version |
|---|---|
| `class C<T> : IC<T>, ID` | `IC<T>`, `IA<T>`, `IB`, `ID` |
| `C<int>` | `IC<int>`, `IA<int>`, `IB`, `ID` |
| `class CE<T> : IE<T>, IC<T>` | `IE<T>`, `IA<T[]>`, `IC<T>`, `IA<T>`, `IB` |
| `struct S<T> : IC<T>, ID` | `IC<T>`, `IA<T>`, `IB`, `ID` (a value type outside CoreLib expands) |
| `interface IF<T> : IC<T>, ID` | `IC<T>`, `IA<T>`, `IB`, `ID` |
| `class Der<T> : LBase<T>, ID`, where the library's `LBase<T> : IC<T>` | `IC<T>`, `IA<T>`, `IB`, `ID` |
| `class CClosed : IC<int>, ID` | `IC<int>`, `IA<int>`, `IB`, `ID` |

The same on both runtimes. This is the only fixture shape that distinguishes the expansion rule.

**Measured, the substitution law.** For every generic definition `G` and every instantiation `G<A>`, closed or open:
`G<A>.GetInterfaces()` is `G.GetInterfaces()` with each of `G`'s variables replaced by the matching argument, entry for
entry, in order, with nothing removed even where two entries become equal. Checked over every generic type definition in
CoreLib (421 on .NET 10, of which 231 exported; 480 on .NET 11, 266 exported) and the 27 fixture definitions, each
instantiated over all-`object`, all-`int`, alternating `string` and `long`, all-`K`, alternating `Dictionary<,>`'s
`V` and `K`, and its own variables in reverse order (the open ones only for definitions without constraints):
1,750 instantiations on .NET 10 and 1,822 on .NET 11 (970 and 1,000 with at least one interface), 151 of the fixtures,
no exception. Of .NET 10 CoreLib's 421 definitions, 210 have an interface and 124 have one that mentions a variable.

**Measured, arrays over a variable.** `List<>`'s `T[]` answers `System.Array`'s six interfaces then `IList<T>`,
`ICollection<T>`, `IEnumerable<T>`, `IReadOnlyList<T>`, `IReadOnlyCollection<T>` over that `T` (the last five from
managed code); `List<K>[]` likewise over `List<K>`; `T[,]` the six alone.

**Measured, a `TypeBuilder` generic definition.** A created `DynG<T>` with `AddInterfaceImplementation` of `IC<T>` and
`IPair<T,int>` answers `IC<T>`, `IA<T>`, `IB`, `IPair<T,int>`, and `DynG<string>` the same over `string`.

### PawPrint today

The handler is `WoofWare.PawPrint/Native/NativeRuntimeTypeQCall.fs:2671-2956` (read):

- An `OpenConstructed` receiver stops at once (`:2707-2709`, the issue's second message), as does an array over a
  variable (`refuseComposite`, `:2727-2728`).
- Everything else goes to a local walk, `collectInterfaces` (`:2769-2911`): for the current type, each InterfaceImpl
  row, concretised under the type's own closed arguments, followed recursively by that interface's walk; then the base
  type's walk; deduplicating with a `Set` of closed targets. A generic definition's row that mentions a variable stops
  (`:2873-2875`, the issue's first message); its closed rows are answered.

That walk is not the map order. It is the order `ConcreteVirtualDispatch.collectInterfaceMap`
(`WoofWare.PawPrint.TypeSystem/ConcreteVirtualDispatch.fs:1547`) *deliberately* uses for default-interface and variance
search (the type's own declared interfaces first, then its base's, as `FindDefaultInterfaceImplementation` scans them;
its docstring says so), and the closed-handle `Set` merges entries CoreCLR keeps apart. So the closed answer is wrong
today, silently (measured, the baseline App at #2307's tip, whose handler is the one on `main`):

| receiver | real .NET | PawPrint today |
|---|---|---|
| `ClosedDerived` | `IB`, `IA<string>` | `IA<string>`, `IB` |
| `TwoDerived<int>` | `IA<int>`, `IB`, `IC<int>` | `IC<int>`, `IA<int>`, `IB` |
| `Dup<int,int>` | `IA<int>`, `IA<int>` | `IA<int>` |
| `Dup<int,string>` | `IA<int>`, `IA<string>` | `IA<string>`, `IA<int>` |
| `Mix2<int>` | `IA<int>`, `IA<int>` | `IA<int>` |
| `Derived<int>` | `IEnumerable<int[]>`, ..., `IComparable<Derived<int>>` | `IComparable<Derived<int>>` first |

Over every exported closed CoreLib type (1,147 on .NET 10), 31 differ in order: all 23 delegate types (`ICloneable`,
`ISerializable` reversed), `Type`, `TypeInfo`, `TypeDelegator`, `TypeBuilder`, `EnumBuilder`,
`GenericTypeParameterBuilder` (`MemberInfo`'s interface after `Type`'s), `CultureAwareComparer` and `OrdinalComparer`
(`StringComparer`'s interfaces after their own).

So PawPrint has four walks over a type's interfaces:

1. `ConcreteInterfaceDispatch.interfaceMapOf` (TypeSystem): CoreCLR's map order, closed classes and structs only,
   host-compared. Dispatch, default interface methods and static virtual dispatch read it.
2. `ConcreteVirtualDispatch.collectInterfaceMap` / `interfaceMapHandles` (TypeSystem): search order, closed, deliberate,
   pinned by `sourcesPure/VariantInterfaceMapOrder.cs`.
3. The QCall's `collectInterfaces`: search order where map order is wanted, closed only (the bug above).
4. The cast rule's `closeOver` (`WoofWare.PawPrint/IlMachineRuntimeMetadata.fs:2617-2632`): an unordered `Set` over
   `RuntimeTypeHandleTarget`s, open receivers included, built on `declaredInterfaceTargets` (`:2055-2119`), which already
   reads a type's InterfaceImpl rows under an open construction's arguments.

### How PawPrint can name the answers

Every interface in the tables has a `RuntimeTypeHandleTarget` (`WoofWare.PawPrint/ManagedPointerSource.fs:115`),
read and then confirmed by the throwaway:

- a closed row (`IA<int>`, `IDisposable`) is `Closed` of its `ConcreteTypeHandle`;
- a row over the receiver's variables is `OpenConstructed (definition, arguments)` with `GenericParameter (owner, i)`
  arguments, built through `RuntimeTypeHandleTarget.openConstructed` (`:395-417`), which collapses a definition applied
  to its own variables to `OpenGenericTypeDefinition` (`IEquatable<Self#def>`'s argument; `isTypicalInstantiation`,
  `:370-382`) and refuses an all-closed one (which must be concretised);
- `ReflectedTypeTarget.reflectedTypeTarget` (`WoofWare.PawPrint/ReflectedTypeTarget.fs:209-414`) already does exactly
  that for a signature element read in an `Open` environment, and `resolveBaseRuntimeTypeHandleTarget`
  (`IlMachineRuntimeMetadata.fs:321`) and `declaredInterfaceTargets` already use it for an extends clause and for
  InterfaceImpl rows;
- in the TypeSystem the same entries are already spelled, rooted at the definition: an `InterfaceMapEntry`'s
  `Substitution` (`ConcreteInterfaceDispatch.fs:80-99`) is a `TypeConcretization.SubstitutionContext` whose arguments
  are `SubstitutionArgument.Formal (definition, i)`, `Closed` or `Spelled`
  (`WoofWare.PawPrint.TypeSystem/TypeConcretisation.fs:1481-1499`), and `SubstitutionContext.rebase` (`:1557-1590`)
  re-reads one onto any arguments. What the TypeSystem lacks is a way to start from a definition: `interfaceMapOf` needs a
  closed handle for its `Interface` field and for `resolveImplementedInterface`'s concretisation.

`VirtualSlotLayout` and `MethodTableLayout` walk a definition's *slots* (`contentVtableOfDefinition`,
`slotTableOfDefinition`), but nothing walks a definition's interface map; #2303 says the same.

### PawPrint with a throwaway (reverted)

The throwaway (in `IlMachineRuntimeMetadata.fs`, beside `declaredInterfaceTargets`) computed an ordered list for any
MethodTable target: the base target's list (by `resolveBaseRuntimeTypeHandleTarget`) verbatim, then each row from
`declaredInterfaceTargets` followed by that interface's own list (none for CoreLib value types and interfaces), keeping
the first occurrence. For a target other than a definition read as itself, it deduplicated by computing the same
candidate lists for the bare definition, position by position, and keeping a value where the definition's candidate was
new: the substitution law above, applied. The handler sent every closed, open and array receiver to it.

All measured, .NET 10's CoreLib unless marked:

- **Rows.** Every row of the fixture probe, all 48 receivers, prints the same as real .NET 10, and on .NET 11's CoreLib
  the same as real .NET 11. The identity checks pass, the swapped-library probe prints the same, and so does the
  `TypeBuilder` probe (these three on .NET 10's CoreLib).
- **The whole of CoreLib.** Every generic definition (421) prints the same as real .NET 10, in 492 s inside a guest; on
  .NET 11's CoreLib every one of its 480 prints the same as real .NET 11 (319 s). Every exported closed type (1,147)
  prints the same, which removes the 31 differences.
- **The substitution law** holds under PawPrint over the same 1,750 CoreLib instantiations and 151 fixture ones, with
  the same output as real .NET (1,490 s for CoreLib).
- **Forcing expansion for CoreLib value types and interfaces** changed nothing over the generic definitions or the
  closed types: the special rule is unobservable over CoreLib as Roslyn compiles it.
- **Arrays over a variable.** The QCall answers, and the run then stops in managed code's `AddSpecialInterface`, at
  `GetUtf8NameInternal` of `IList<T>`: "TODO: open constructed types are not handled at
  Native/NativeRuntimeTypeFCall.fs:251". The same stop ends `typeof(List<>).GetInterface("IList`1")`, which real .NET
  answers with `IList<T>`.
- **The consumers, rerun.** On `main` plus the throwaway, each runs to the end with real .NET's stdout:

  | scenario | before | with the throwaway |
  |---|---|---|
  | Autofac 8.0.0, `ContainerBuilder.Build()` and three resolves | stops in `GetInterfaces` | exit 0, same stdout, 18-35 s |
  | `dynamic d = poco; (int)d.Id + 1`, then `(int)s.Length` | stops in `GetInterfaces` | exit 0, same stdout, 163-277 s |
  | Moq 4.20.72, the `Mock.Of<IRepo<string>>(r => r.Get(1) == "one")` scenario | stops in `GetInterfaces` | exit 0, same stdout, 57-67 s |

  A trace of open receivers showed each asks only about generic *definitions*, never an open construction: Autofac
  made two such calls and Moq two (one of each on the library's own definition), the `dynamic` scenario 35, over
  CoreLib's and `System.Linq.Expressions`'s definitions. None reached the #2303 or #2295 refusals (a refusal would have stopped the run). Before
  #2307 merged, Moq's scenario stopped next at the then-unimplemented `GetInterfaceMethodImplementation`; on `main` it
  does not.

## Surprises against the issue's description

1. The closed path is wrong today, not just the open one: base-class interfaces come after the type's own, and
   duplicates are merged. 31 exported CoreLib types, including every delegate and `System.Type`, show it.
2. Duplicates are real and survive substitution: `Dup<int,int>` has `IA<int>` twice, and so does the open `Dup<K,K>`.
3. Every receiver's answer is its definition's answer substituted (the law above), so one definition-level list
   answers closed, open and typical receivers alike.
4. The "special marker" form the issue worried about is invisible here: the QCall resolves it before answering.
5. The three consumers need nothing beyond this: each runs to the end, and each asks only about definitions.
6. `GetInterface(name)` on an open receiver, and `GetInterfaces()` on an array over a variable, reach a second gap,
   `GetUtf8NameInternal` of an open construction.

## Options

### Decision 1: where an open receiver's list comes from

#### A. A definition-level interface map in the TypeSystem, which every receiver reads

Add `ConcreteInterfaceDispatch.interfaceMapOfDefinition`: given a definition's identity, its map as entries in the
definition's own vocabulary (the interface's identity, its `TypeInfo`, the definition-rooted `Substitution`, and the
two flags), built by `interfaceMapOf`'s algorithm without concretising anything: the parent definition's entries rebased
onto the extends clause's arguments (`ConcreteMethodTable.baseOfDefinition` already supplies them), then each row and its
parents, deduplicated by `substitutionsEquivalent` as `findExisting` already does. Then:

- the QCall, for a receiver with definition `G` and arguments (targets), maps each entry to a target (decision 3); a
  closed receiver maps the same way, or through `interfaceMapOf` (decision 2);
- the closed `interfaceMapOf` becomes "the definition's map, each entry rebased onto the closed arguments and
  concretised" (duplicates preserved, as now), so one function decides the order of dispatch and of reflection.

Blast radius: the TypeSystem package (a new public or internal function), and, for the last bullet, every caller of
`interfaceMapOf` (dispatch, default interface methods, static virtual dispatch). `TestInterfaceDispatchMap` already
compares that function with the host over CoreLib and a generated corpus, so a refactor that changed its answers would be
seen. Reversibility: high for the first two bullets; the third is a refactor that preserves behaviour, in a commit of its
own. Information kept: everything, in the TypeSystem's vocabulary, including `DeclaredOnType` and `ImplementedByParent`,
which #2303 (dispatch on an open receiver) and #2295 (which open types' maps hold markers) both need from a definition.

#### B. An ordered walk over `RuntimeTypeHandleTarget`s in the interpreter

The throwaway's shape, made permanent beside `declaredInterfaceTargets`: the base target's list, then each row and its
list, deduplicated by comparing with the bare definition's candidates position by position. Small, local, and measured to
agree with the host everywhere above.

Blast radius: the interpreter only. Reversibility: total. Information kept: the targets, nothing about entries (no
flags, no substitution); the TypeSystem still cannot answer for a definition. Cost: a second implementation of map order
beside `interfaceMapOf`, kept in agreement only by tests (the CoreLib rule, the dedup rule and the expansion rule each
written twice), and a third and fourth walk (`collectInterfaceMap`, the cast rule's `closeOver`) still beside them.

#### C. Instantiate the definition at stand-in closed types and map them back

Concretise `G<Placeholder0, ...>`, run `interfaceMapOf`, and replace each placeholder by the variable it stood for.
Cheapest to write. But it is the coercion of an open identity into a closed one that AGENTS.md's "keep lookups honest"
guideline rules out: it registers types that do not exist in `AllConcreteTypes`, a placeholder that a constraint rejects
stops the run, and a placeholder that coincides with a real argument (`G<P0>` against a row `IA<P0>`) gives a wrong
answer silently. Listed to rule it out.

**Recommended: A**, staged (see "Stages"). The substitution law says the map is a property of the definition, CoreCLR
computes it once per definition, and `interfaceMapOf` already holds the definition-rooted substitution; the only thing
missing is a way to start from the definition. B is proven but writes the rule twice, and the next two issues in this
area need A's function anyway.

### Decision 2: what the QCall answers for a closed receiver

- **(i) Read the TypeSystem's map order**, `interfaceMapOf` (or, under A, the definition's map rebased onto the closed
  arguments, which is the same function once A's third bullet lands). Fixes the 31 CoreLib differences and the
  duplicates. An array asks `System.Array`'s map, as the walk already does.
- **(ii) The same path as an open receiver** (the throwaway): a closed receiver is just a receiver whose arguments are
  closed. Under A this is (i) again; under B it is B for everything, which leaves the closed order decided in two places.
- **(iii) Keep `collectInterfaces`, fixed in place** (base first, a list instead of a set). A third copy of the order.

**Recommended: (i).** `interfaceMapOf` is host-compared already; the QCall should report the map dispatch uses. Delete
`collectInterfaces`.

### Decision 3 (with A): how a definition-rooted entry becomes the guest's `Type`

- **(a) Convert the substitution directly**: a new interpreter function from a `SubstitutionArgument` to a
  `RuntimeTypeHandleTarget`, given the receiver's arguments as targets. `Closed h` is `Closed h`; `Formal (G, i)` is the
  receiver's `i`th argument; `Spelled (assembly, spelling, inner)` is `reflectedTypeTarget` of the spelling in that
  assembly, in an `Open` environment of `inner` converted recursively. The entry's target is then
  `RuntimeTypeHandleTarget.openConstructed` of the interface's identity over the converted arguments, or the
  concretised closed type when every argument is closed (as `reflectedTypeTarget`'s `GenericInstantiation` arm does).
  The canonical forms (`#def`, closed collapse) come from the existing constructors.
- **(b) Flatten each entry to one `TypeDefn`** in the receiver definition's vocabulary, in the TypeSystem, and read it
  with `reflectedTypeTarget` in the receiver's environment. Simpler at the call site, but a `TypeDefn` is spelled in one
  assembly's token space and an entry's chain crosses assemblies (a CoreLib interface's parents, read for a user type),
  so the flattening would need to re-spell tokens or refuse.
- **(c) Substitute targets** (the throwaway's position-by-position alignment, or a substitution over
  `RuntimeTypeHandleTarget`s with a special case for `OpenGenericTypeDefinition G` meaning `G` over its own variables).
  Works, but keeps no tie to the TypeSystem's entries and needs that special case to be exactly right.

**Recommended: (a).** It is the only one that reads the TypeSystem's entries as they are, and every piece of it
exists.

### Decision 4: an array over a variable (`T[]`, `List<K>[]`)

The QCall answers `System.Array`'s map whatever the element (CoreCLR copies it into every array MethodTable). Today the
handler refuses an array over a variable before looking.

- **(a) Answer it**, as a closed array is answered. The guest then stops in managed code, at `GetUtf8NameInternal` of
  `IList<T>` (an open construction), which needs its own issue.
- **(b) Keep the refusal**, and file one issue for the array case and `GetUtf8NameInternal` together.

**Recommended: (a).** The QCall's answer is right and cheap; the next stop is a different QCall's gap, which
`typeof(List<>).GetInterface("IList`1")` reaches anyway once this lands. File it (question 6).

### Decision 5: CoreLib's no-expansion rule

CoreCLR skips expansion for CoreLib's value types *and interfaces*; `interfaceMapOf` skips it for value types only.
Forcing expansion for both changed nothing over CoreLib (measured), so no test can tell the three options apart.

- **(a) Mirror CoreCLR**: value types and interfaces, with a comment that the rule is unobservable for CoreLib as
  compiled and why it is kept.
- **(b) Keep `interfaceMapOf`'s rule** (value types only).
- **(c) Drop the rule**: one fewer branch, and no observable change.

**Recommended: (a)**, since the definition map moves the rule anyway and CoreCLR's text is the specification; but this
is the decider's taste. Its mutant is equivalent and is listed as such.

### Decision 6: the cast rule's walk

`closeOver` is an unordered set over targets and answers #2295's question. It could read the new map (as a set).

- **(a) Leave it** for #2295, which needs a model of the marker flag first.
- **(b) Move it onto the new map** now, so there are three walks instead of four.

**Recommended: (a).** One feature per change; say in #2295 that a definition-level map now exists.

## Stages (with A)

1. `interfaceMapOfDefinition` in the TypeSystem, and a property that rebasing it onto each closed type's arguments and
   concretising gives `interfaceMapOf`, entry for entry, over `TestInterfaceDispatchMap`'s corpora. No behaviour change.
2. The QCall reads it, for closed, open and array receivers (decisions 2 to 4), and `collectInterfaces` is deleted. This
   is the change the issue asks for, and the one that fixes the closed order.
3. `interfaceMapOf` is re-expressed through the definition map, and its own walk deleted. A refactor that preserves
   behaviour; `TestInterfaceDispatchMap` and the dispatch guests must stay green across it without edits.

Stages 1 and 2 may be one pull request; 3 is its own.

## Questions for the decider

1. **Where does an open receiver's list come from: A (a definition-level map in the TypeSystem), B (an interpreter walk
   over targets) or C (placeholders)?** Recommended: A, in the three stages above.
2. **Does the closed path read the TypeSystem's map order (i), share the open path (ii), or keep its own walk fixed in
   place (iii)?** Recommended: (i), and delete `collectInterfaces`. This changes 31 exported CoreLib types' answers, all
   towards real .NET.
3. **Is stage 3 (re-expressing `interfaceMapOf` through the definition map) part of this issue, or a follow-up?**
   Recommended: part of it, as its own pull request, so that the order is decided in one place when the issue closes.
4. **With A, how does an entry become a target: (a) direct conversion, (b) flattening to a `TypeDefn`, (c) target
   substitution?** Recommended: (a).
5. **Does the QCall answer an array over a variable (a), or keep refusing it (b)?** Recommended: (a).
6. **Should `GetUtf8NameInternal` of an open construction become an issue?** Measured: real .NET answers
   `typeof(List<>).GetInterface("IList`1")` with `IList<T>`; PawPrint (with this change) stops at
   "TODO: open constructed types are not handled at Native/NativeRuntimeTypeFCall.fs:251"; `List<>`'s `T[]`
   `.GetInterfaces()` stops there too. Recommended: yes, one issue naming both.
7. **CoreLib's no-expansion rule: mirror CoreCLR (a), keep value types only (b), or drop it (c)?** Recommended: (a).
8. **Is the cast rule's `closeOver` left for #2295 (a)?** Recommended: yes.
9. **Is the `dynamic` scenario added as a guest?** It needs no package (`Microsoft.CSharp` is in the shared framework),
   ran in 163-277 s in a Release App, and is the most common of the three consumers. Recommended: yes, in `GuestSolo` if
   it is that slow in the harness; Autofac and Moq wait for #2092's consumer fixture.
10. **How are the swapped-library shapes tested** (classes compiled against one version of a library and run against
    another, as `TestInterfaceMethodImplementation.swapLibrary` does): as fixed example shapes (a), or by teaching the
    generator to drop rows (b)? They are the only shapes that distinguish the expansion rules. Recommended: (a).

## Test design

Write the tests and guests first, in a commit of their own, and record the failing output (the two TODO messages for
open receivers; the wrong order for the closed ones) before the function or the handler exists. Run the default suite and
the four Guest categories as separate invocations.

### Property tests

In `WoofWare.PawPrint.Test`, as `TestGetInterfacesOrder.fs`. The function under test is whatever the handler calls with a
`RuntimeTypeHandleTarget` (under A, the interpreter function of decision 3 over `interfaceMapOfDefinition`), called
outside a guest. The oracle is the host's `GetInterfaces()` on the same image, each host interface turned into a target
with `HostTypeTarget.target`. The property, for each receiver: PawPrint's list equals the host's mapped list, entry for
entry, in order (so a duplicate must appear twice, and a `#def` argument must be the definition's target).

1. **CoreLib, generic definitions.** Every generic type definition in the host's CoreLib (421 on .NET 10). Assert that at
   least 100 have an interface mentioning a variable, so the sweep is not vacuous.
2. **CoreLib, closed.** Every closed type in CoreLib with at least one interface, interfaces included (where
   `TestInterfaceDispatchMap` leaves them out), plus that test's spread of generic instantiations. Assert that some
   receiver has a base class with interfaces and declared interfaces of its own (any delegate), so the base-first rule
   is exercised.
3. **The substitution law, at PawPrint's level.** For each CoreLib definition, the instantiations the probe used (all
   `object`, all `int`, alternating `string`/`long`, all `K`, alternating `V`/`K`, reversed own variables; the open ones
   only for unconstrained definitions): PawPrint's list for the instantiation equals the host's. This is where open
   constructions are compared.
4. **Generated hierarchies (FsCheck).** Generate a small C# library of interfaces and classes and structs with arity 0 to
   2, whose base types and interface rows are drawn from: a closed interface; an interface over one of the type's own
   variables; over an array of one; over a closed instantiation of another generated generic type; over the type itself
   (`IEquatable<Self<T>>`); a base class that is an open construction over the type's variables (`Own<U[]>`), a closed
   one, or none; a row the base already implements (re-declared); a row that duplicates a base's entry only after
   substitution (`Dup<T,U> : BaseA<T>, IA<U>`); nested generic types. The pool: each definition, its closed
   instantiations over `int` and `string`, an open construction over another definition's variable, and `Derived<>.
   BaseType`-style bases. Valid by construction (a Roslyn error fails the test, as in #2270's generator). Coverage labels
   from the host's side, each with a floor at about half of what a fixed sample reaches: a receiver with a duplicate
   entry; one whose base contributes interfaces before its own; a `#def` argument; an open-construction receiver; a value
   type receiver; an interface receiver.

### Example tests

- The swapped-library shapes (question 10), through `CrossAssemblyHarness.runTestReplacing` as a guest, or at the
  function level with both images loaded: `C<>`, `C<int>`, `CE<>`, `S<>`, `IF<>`, `Der<>`, `CClosed` against the second
  version, expecting the table above. These are the only tests that kill the expansion mutants.
- The canonical forms: `List<>`'s first interface is `OpenConstructed (IList, [GenericParameter (List, 0)])`;
  `IX<>`'s `IEquatable<>` entry has `OpenGenericTypeDefinition IX` as its argument; `Dup<int,int>` gives the same closed
  handle twice.
- Under A, stage 1's property (definition map rebased equals `interfaceMapOf`) over `TestInterfaceDispatchMap`'s
  corpora.

### Guests

**New: `sourcesPure/TypeGetInterfacesOpenGeneric.cs`**, one exit code per check, each check comparing the list (by
reference, element by element) with an expectation built from `typeof` and `MakeGenericType`, so a wrong order, a
missing duplicate or a wrong identity each fail their own check:

| # | receiver | expected |
|---|---|---|
| 1 | `Plain<>` | `IDisposable` |
| 2 | `Own<>` | `IEnumerable<T>`, `IEnumerable`, `IPair<T,int>` |
| 3 | `Derived<>` | `IEnumerable<T[]>`, `IEnumerable`, `IPair<T[],int>`, `IComparable<Derived<>>` (the argument is `typeof(Derived<>)`) |
| 4 | `typeof(Derived<>).BaseType` | `IEnumerable<T[]>`, `IEnumerable`, `IPair<T[],int>` over `Derived`'s `T` |
| 5 | `List<>` | the eight in order |
| 6 | `IList<>` | `ICollection<T>`, `IEnumerable<T>`, `IEnumerable` over `IList`'s `T` |
| 7 | `Dictionary<,>` | the ten in order |
| 8 | `Mixed<>` | `IA<int>`, `IPair<T,int>`, `IB`, `IA<T[]>` |
| 9 | `SSelf<>` | `IEquatable<SSelf<>>`, `IComparable<SSelf<>>` |
| 10 | `IX<>` | `IC<T>`, `IA<T>`, `IB`, `IEquatable<IX<>>` |
| 11 | `Dup<,>` | `IA<T>`, `IA<U>` |
| 12 | `Dup<K,K>` | `IA<K>` twice |
| 13 | `Redecl<>` | `IA<T>`, `IB` |
| 14 | `Mix2<>` | `IA<int>`, `IA<T>` |
| 15 | `Outer<>.Inner<>` | `IPair<T,U>` over `Inner`'s variables |
| 16 | `SCrtp<>` | `ISelfIface<SCrtp<>>`, `IEquatable<SCrtp<>>`, `IA<ISelfIface<SCrtp<>>>` |
| 17 | `List<K>` | `List<>`'s eight over `K` |
| 18 | `typeof(List<>).MakeGenericType(List<>'s T)` | is `typeof(List<>)`, and its list is check 5's |
| 19 | `List<>`'s first interface | `ContainsGenericParameters` true, `IsGenericTypeDefinition` false, argument reference-equal to `List<>`'s `T` |

**New: `sourcesPure/TypeGetInterfacesOrder.cs`**, for the closed path, one exit code per check: `ClosedDerived`,
`TwoDerived<int>`, `Dup<int,int>` (two entries), `Dup<int,string>`, `Mix2<int>` (two entries), `Derived<int>`, `Action`
(`ICloneable` then `ISerializable`), `typeof(Type)` (its two entries, `ICustomAttributeProvider` then `IReflect`, as measured
on both runtimes).

**`sourcesImpure`: a `TypeBuilder` generic definition**, beside the existing `TypeBuilder` guests: `DynG<T>` with `IC<T>`
and `IPair<T,int>` answers `IC<T>`, `IA<T>`, `IB`, `IPair<T,int>`, and `DynG<string>` the same over `string`.

**`dynamic`, if question 9 is answered yes:** `sourcesPure/DynamicMemberAccess.cs`, the scenario above, returning 0 when
both values are right.

**The existing guests** that call `GetInterfaces()` (`RuntimeTypeGetInterfacesInherited.cs`, the `ArrayGetInterfaces*`
family, `RuntimeTypeGetInterfaceByName.cs`) must stay green without edits.

**Controls on real .NET**, as for #2270: each guest exits 0 on real .NET 10 and 11 as written, and each perturbed
expectation (two entries swapped, a duplicate removed, a `#def` argument replaced by the explicit instantiation over the
definition's own variables) exits with its own check's number on both.

### Refusal tests that move

- `TestGetInterfaceMethodImplementation.fs`: `typeof(CGen<>).GetInterfaceMap(typeof(CGen<>).GetInterfaces()[0])` now
  reaches #2303's refusal through public API (before, it stopped in `GetInterfaces`). Add it as a refusal test beside "A
  generic definition's closed interface is refused", asserting real .NET's map in the guest, so #2303 sees it change.
- With decision 4 (a): a refusal test for `List<>`'s `T[]` `.GetInterfaces()` stopping at the `GetUtf8NameInternal`
  TODO, naming question 6's issue.

## Mutants

Each must turn some test red. Run each against the example tests, the properties and the guests, and record which went
red.

| mutant | killed by |
|---|---|
| base-class entries after the type's own (today's closed order) | guest order checks; CoreLib closed property (31 types); generated label |
| entries deduplicated by target after substitution | guests 12, order `Dup<int,int>`; substitution law; generated label |
| own rows deduplicated against the parent in the *instantiation's* vocabulary (closed handles) rather than the definition's | order `Mix2<int>`; guest 14 under a closed instantiation; substitution law |
| a declared row's parents not expanded (outside CoreLib) | swapped-library examples |
| a declared row's parents appended after all declared rows (breadth-first) | swapped-library `C<>`, `CE<>` |
| parents expanded before the row itself | guests 2, 10; swapped-library examples |
| `#def` collapse skipped (`IEquatable<Self<T>>` spelled as an explicit instantiation) | guests 3, 9, 16; canonical-form example |
| an interface over the receiver's variables answered over the interface's own variables (`IList<>`'s `T` for `List<>`'s) | guest 19; guest 6 against 5 |
| an open-construction receiver answered as its definition (arguments ignored) | guests 4, 12, 17; substitution law |
| a closed row of an open receiver left unconcretised (`OpenConstructed` with closed arguments) | guest 8 (`IA<int>` by reference); `openConstructed`'s own refusal |
| an empty list for an open receiver whose rows all mention variables | guests 2 to 7 |
| arrays over a variable refused (decision 4 (a)) | the moved refusal test |
| the CoreLib no-expansion rule removed or widened | none: equivalent over CoreLib as compiled (measured); listed so nobody spends time on it |
| `interfaceMapOf` re-expressed with a different order (stage 3) | `TestInterfaceDispatchMap`; stage 1's property |

## Neighbours

- **#2303** (`GetInterfaceMap` on an open receiver). Not settled. After this change more of its shapes reach its refusal
  through public API (the open receiver with one of its own open interfaces), which the moved refusal test pins. A
  definition-level interface map (decision 1 A) is one of the two things its "Why it is not a small change" says is
  missing; the other, a definition-level dispatch map, remains.
- **#2295** (the cast rule's open value types). Not settled: what it needs is `MayHaveOpenInterfacesInInterfaceMap`,
  which decides what the map *stores*; this QCall resolves the stored form away. The definition map's entries are where
  a model of the flag's three retry triggers would be computed.
- **`GetUtf8NameInternal` of an open construction** (question 6): the next stop for `GetInterface(name)` on an open
  receiver and for `GetInterfaces()` on an array over a variable.
- **Constraint validation on an open argument**: the probe found that `MakeGenericType` of a constrained definition over
  an open construction (`ISelfIface<>` over `SCrtp<>`'s `SCrtp<T>`) stops with "TODO: validateConstraintsOn: ... is
  neither closed nor a type variable". Not reached by `GetInterfaces` or by the consumers; noted only.

## Probe files

The probes were not kept. One console program, built for .NET 10 and for .NET 11 from the same source, with modes `rows`
(the fixture table), `shapes` (arrays over a variable), `identity` (the reference-identity checks, one exit code each),
`dump` (every CoreLib generic definition's list, compared line by line between runtimes and with PawPrint), `closed`
(every exported closed CoreLib type), `closedrows`, `subst` (the substitution law, over CoreLib or the fixtures),
`byname` and `emit`. Each interface was rendered with the owner and position of every variable in it, so a variable of
the wrong owner shows. A second program was compiled against a library and run against a second version of it (the
swapped-library table). The consumer scenarios were Fable's, rerun unchanged. The throwaway also logged each open
receiver it was asked about, to stderr.

## Decisions

Decided 2026-11-16 by the delegated decider, after checking the plan against `main` at `c50428b33` and against the
pinned .NET 10 runtime source. The numbers match "Questions for the decider". "Checked" below means read in the code or
in the planner's probe output during the decision; nothing new was run against real .NET.

### What the check found, before the answers

These change the staging, so they come first.

- **The closed order bug already has an issue: #1318** (open since September, "returns the derived type's interfaces
  before the base class's"). It does not mention the merged duplicates, and its proposed fix (recurse into the base
  first inside `collectInterfaces`) is not the fix chosen here. The first pull request below closes it. No new issue is
  filed for it.
- **A generic definition is silently wrong today too, not only a closed type** (read, not measured). The handler
  answers a definition whose rows are all closed, and walks its base afterwards, so `typeof(Action<>)` answers
  `ISerializable`, `ICloneable` where real .NET answers `ICloneable`, `ISerializable` (real .NET's line is in the
  planner's dump of CoreLib's definitions). Every generic delegate definition has this shape, and so does any
  `class G<T> : ClosedBase, IClosed` whose base has an interface.
- **The closed `interfaceMapOf` already computes the definition's map on the way.** Its owner context is
  `SubstitutionContext.forDefinition` (`MethodTableLayout.ownerOfDefinition`), each entry's `Substitution` is rooted at
  the definition, and the duplicate check compares those. The closed handle is used only as a cheap first filter and as
  the entry's `Interface`. So the definition-level map is an extraction from that function, not a new algorithm, and
  `MethodTableLayout.nominalIdentityOfSpelling` already resolves the identity a row names without closing anything.
- **CoreCLR's "no expansion" rule is keyed on the type whose map is being built**, not on the interface being
  expanded: `if (!(GetModule()->IsSystem() && (IsValueClass() || IsInterface())))` in `ExpandApproxInterface` reads the
  builder's own module and kind. The plan's description of the throwaway ("followed by that interface's own list (none
  for CoreLib value types and interfaces)") can be read the other way. `interfaceMapOf`'s `expandParents` is already
  keyed on the type being built; keep it so. A user's class that implements a CoreLib interface expands that
  interface's parents by walking its rows.
- **The plan's line references into `methodtablebuilder.cpp` and its description of the handler, of
  `collectInterfaceMap`, of `reflectedTypeTarget` and of `openConstructed` are accurate.** The probe outputs agree with
  the tables: 31 of 1,147 closed lines differ on the baseline, none with the throwaway, and the swapped-library output is
  identical under PawPrint and real .NET.

### Staging: five pull requests, each landing on its own

This replaces the plan's "Stages". The order of the plan's stages 2 and 3 is reversed, and the closed fix goes first.

1. **The closed order (closes #1318).** Interpreter and tests only. The handler's closed arm (a `Concrete` receiver,
   and an array, which asks for `System.Array`'s map) answers `InterfaceDispatch.interfaceMapOf`'s entries, in order,
   duplicates kept. The definition arm keeps answering a definition whose rows are all closed, but **stops** (a `TODO`
   naming #2309) when the definition's base class has a non-empty interface map, because that is the one case where its
   order is wrong. `collectInterfaces` therefore loses its walk up the base chain; what is left of it serves only the
   definition arm and is deleted in the third pull request. A stop was chosen over merging the base's map into that arm
   in place: the merge would be a third statement of the order rule in the interpreter, written to be deleted a few days
   later, and a stop is honest in the meantime.
2. **The definition-level map in the TypeSystem, with `interfaceMapOf` re-expressed through it.** No change in
   behaviour, no new consumer. `interfaceMapOfDefinition` (internal) is extracted from `interfaceMapOf`, and
   `interfaceMapOf` becomes "the definition's map, each entry rebased onto the closed arguments and closed". The old
   walk is deleted in the same pull request.
3. **Open receivers (closes #2309).** The interpreter converts the definition's entries to targets (decision 4), the
   handler answers a definition, an open construction and an array over a variable, and `collectInterfaces` and the
   first pull request's temporary stop are deleted.
4. **`GetUtf8NameInternal` of an open construction** (#2315). A few lines; it cannot
   be reached through public API before the third pull request, so it follows it.
5. **The `dynamic` guest** (decision 9). Tests only.

Why the definition map replaces the old walk *before* the QCall reads it, rather than after as the plan had it: with
the plan's order, two implementations of the order rule live side by side in the TypeSystem, held together by a
property test, until a later refactor that nothing forces to happen. With this order there is never a second copy, the
new function has a real consumer (all of dispatch) from the day it lands, and its oracle is the strongest one available:
`TestInterfaceDispatchMap` against the host, `TestDispatchBindingFailure`, and every dispatch guest. The cost is that the
fix the issue asks for waits for a refactor of code dispatch depends on. That is accepted; AGENTS.md asks for a
dependency to land first. If the second pull request cannot reproduce `interfaceMapOf` exactly, stop and report; do not
fall back to landing the definition map beside the old walk.

Each pull request starts with a commit of tests alone, with the failing output recorded, as the plan's "Test design"
says. For the second, which changes no behaviour, the first commit instead adds the new function *beside* the old walk
with a property that the two agree entry for entry (handle, substitution by `substitutionsEquivalent`, and both flags)
over `TestInterfaceDispatchMap`'s corpora; record that run in the pull request body, then delete the old walk and that
property in a later commit of the same pull request.

### 1. Where an open receiver's list comes from: A

A definition-level interface map in the TypeSystem. B writes the order rule a second time and leaves the TypeSystem
unable to answer for a definition, which #2303 and #2295 both need. C coerces an open identity into a closed one, which
AGENTS.md's guidance on honest lookups rules out, and it can give a wrong answer silently when a stand-in coincides with
a real argument.

For the implementer:

- The definition entry holds the interface's identity, its `TypeInfo`, the definition-rooted `Substitution`,
  `DeclaredOnType` and `ImplementedByParent`. Consider making the closed `InterfaceMapEntry` hold a definition entry
  plus the closed `Interface` and `Type`, so the two cannot disagree; that is the implementer's call.
- The parent's entries come from the parent *definition's* map, rebased onto the arguments
  `ConcreteMethodTable.baseOfDefinition` gives, as the closed function does today. Nothing is concretised while building
  the definition map.
- The duplicate check is "same interface identity, and `substitutionsEquivalent`". The closed handle comparison that
  precedes it today is only a filter and goes away.
- Do not memoise the definition map unless a measurement asks for it. Compare the time of the four Guest categories
  before and after the second pull request and put the numbers in its body.
- Two things may shift in the second pull request and must be looked at, not assumed. First, the order in which
  interface types are resolved and concretised: the definition map resolves every row's identity before any argument is
  closed, where the old walk interleaved them. That is CoreCLR's own order (`LoadApproxInterfaceMap`, then
  `LoadExactInterfaceMap`), but it can change which assembly loads first and which concrete handle is registered first.
  Second, which failure a type with two unbindable rows reports. `TestDispatchBindingFailure` and the guests that
  observe assembly loads must stay green without edits; if one does not, stop and measure real .NET before changing
  the test.

### 2. The closed path: (i), and it lands first

A closed receiver answers `interfaceMapOf`. That is the first pull request, before any open-receiver work, because a
silent wrong answer is worse than a stop and the fix is a few lines over a function that is already compared with the
host. After the third pull request the closed arm stays on `interfaceMapOf`; it does not move to the target conversion.
The two closings (TypeSystem handles for a closed receiver, targets for an open one) are tied by a property instead:
for every closed receiver in the corpora, converting the definition's entries with the receiver's closed arguments as
targets gives exactly `interfaceMapOf`'s handles, in order.

`interfaceMapOf`'s docstring says "a class or value type", and `TestInterfaceDispatchMap` leaves interfaces out. The
first pull request makes an interface a supported receiver: correct the docstring, and extend the comparison with the
host to every closed CoreLib interface and to closed instantiations of generic ones (the map only; an interface has no
dispatch table to compare).

### 3. Re-expressing `interfaceMapOf` through the definition map: part of this work, and before the QCall change

It is the second pull request, not a follow-up and not a third stage. See "Staging" for the reason. #2309 closes with
the third pull request.

### 4. How an entry becomes a target: (a), direct conversion

A function in the interpreter from a definition entry and the receiver's arguments (as targets) to a target:
`Closed h` stays; `Formal (G, i)` is the receiver's `i`th argument, and a `Formal` of any other owner is a logic error
that fails loudly, as `SubstitutionContext.rebase` does; `Spelled` is `reflectedTypeTarget` of the spelling in its own
assembly, in an `Open` environment of the inner context converted the same way. (b) would have to re-spell tokens
across assemblies, and (c) keeps no tie to the TypeSystem's entries.

Points the plan did not state:

- **An interface with no generic arguments must come back `Closed`.** `RuntimeTypeHandleTarget.openConstructed` given an
  empty argument list returns `OpenGenericTypeDefinition`, which is wrong for `IEnumerable` or `IDisposable`. Test "every
  argument is closed" (true of an empty list) before reaching for `openConstructed`.
- "A definition applied to argument targets, in canonical form" is already written inline in `reflectedTypeTarget`'s
  `GenericInstantiation` arm. Extract that one piece and call it from both places; do not write it again.
- The whole answer is one function from a receiver target to an ordered list of targets, callable outside a guest. The
  handler classifies the receiver, calls it, and allocates the array. The property tests call the same function.

### 5. An array over a variable: (a), answer it

`T[]`, `List<K>[]` and `T[,]` answer `System.Array`'s closed map, as a closed array does. The guest then stops in
`GetUtf8NameInternal`, which is the fourth pull request. Until that lands, the stop is pinned by a refusal test that
names #2315.

### 6. `GetUtf8NameInternal` of an open construction: yes, an issue

Filed as #2315, naming both ways to reach it. It is small (CoreCLR reads the
definition's TypeDef name through `GetCl()`, and the arm for a bare definition already does that), and it is the fourth
pull request. When it lands, the refusal test of decision 5 becomes two positive guest checks:
`typeof(List<>).GetInterface("IList`1")` and `GetInterfaces()` on `List<>`'s `T[]`, eleven entries in the measured
order.

### 7. CoreLib's no-expansion rule: (a), mirror CoreCLR

Value types and interfaces declared in CoreLib, keyed on the type whose map is being built. It changes in the second
pull request, where the function is rewritten anyway, with a comment that says the rule cannot be observed over CoreLib
as Roslyn compiles it. The mutant stays listed as equivalent.

To keep "cannot be observed" a checked statement and not a measurement made once, add one cheap test over the host's
CoreLib metadata: for every value type and interface CoreLib declares, the number of InterfaceImpl rows plus the host's
count of the base class's interfaces equals the host's `GetInterfaces().Length`. While that holds, expansion can add
nothing for those types. If a future CoreLib breaks it, the rule has become observable there, the comparison with the
host tests it for real, and the "equivalent" label must be removed.

### 8. The cast rule's `closeOver`: (a), left for #2295

One feature per change. When the third pull request merges, say in its body what #2295 and #2303 can now use (a
definition's interface map, with its flags), so those issues are updated from the merge and not from a promise.

Two walks remain afterwards: `closeOver`, and `ConcreteVirtualDispatch.collectInterfaceMap`'s search order. The second
is deliberate, but by CoreCLR's own text it is a view of the map (each class level's newly added entries, most derived
level first), and it is written as an independent walk that removes duplicates by closed handle. Whether the two can
disagree is not known; #2316 asks. It is not part of this work.

### 9. The `dynamic` guest: yes, as its own pull request, last

`sourcesPure/DynamicMemberAccess.cs`, both operations, after the third pull request. It is a separate pull request
because its risks are the harness's (a reference to `Microsoft.CSharp`, the running time), not this feature's. Measure
it in the harness first: it goes in `GuestSolo` if a single run takes about as long as that category's other tests. If
it takes much longer than the Release App's three to five minutes, stop and ask before adding it. Autofac and Moq wait
for #2092's consumer fixture.

### 10. The swapped-library shapes: (a), fixed shapes, but with the host as the oracle

Run them at the function level, with both images loaded on the host and in PawPrint as `TestInterfaceMethodImplementation`
does with `swapLibrary`, and compare PawPrint's list with the host's `GetInterfaces()`; do not write the expected lists
by hand. Because the oracle is the host, adding a shape costs one line, so add the interactions the plan's seven shapes
miss (all against the second version of the library, where `IC<T> : IA<T>, IB`):

- `class C2<T> : IC<T>, IA<T>`: a parent found by expansion and then declared by a later row (it keeps its earlier
  position);
- `class Der2<T> : BaseA<T>, IC<T>`: a parent the base class already implements (no second entry);
- `class D2<T, U> : IC<T>, IA<U>`, and its instantiation over `int, int`: a parent found by expansion that a later row
  duplicates only after substitution (two `IA<int>` entries);
- `struct S2<T> : IC<T[]>`: expansion under a composite argument.

These first go red or green in the first pull request for the closed receivers among them, since the expansion code is
`interfaceMapOf`'s; the open receivers join in the third. Teaching the generator to drop rows is not done now.

### What the planner missed

**Tests and guest rows to add** (each expectation confirmed by a control on real .NET 10 and 11 first; where no
measurement exists yet, that is said):

- `typeof(Action<>)` gives `ICloneable`, `ISerializable` (measured in the planner's dump). In the first pull request this
  is a refusal test for the temporary stop; in the third it becomes a guest check.
- A user's `class OverClosed<T> : ClosedBase, IDisposable` gives `IB`, `IDisposable`: the same two steps. Not yet
  measured.
- A generic *method's* type parameter as an argument: the return type of `List<TM> M<TM>()` gives `List<>`'s eight over
  `TM`. The issue lists this as unmeasured and the plan did not measure it either. Measure it, then add it.
- An open-constructed *interface* receiver: `typeof(IC<>).MakeGenericType(K)` gives `IA<K>`, `IB`. The probe ran this;
  the guest table has no such row.
- An interface receiver whose argument is a bare definition: with `class X<T> : IC<X<T>>`,
  `typeof(X<>).GetInterfaces()[0].GetInterfaces()` gives `IA<X#def>`, `IB`, the argument reference-equal to
  `typeof(X<>)`. Not yet measured.
- In `TypeGetInterfacesOrder.cs`: a closed interface receiver (`IC<int>`, `IList<int>`), an enum, and a struct.
- The cross-check property of decision 2, and the CoreLib metadata test of decision 7.
- For the generated hierarchies, extend `InterfaceCorpusGenerator` if its shapes are close; do not start a second
  generator beside it without saying why.
- Keep each property test to about a minute at the function level. If the substitution sweep over 1,750 instantiations
  does not fit, sample it with a fixed seed and assert the labels' floors on the sample.

**Mutants to add to the plan's table:**

| mutant | killed by |
|---|---|
| first pull request: the closed arm back on search order, or duplicates removed by handle | `TypeGetInterfacesOrder.cs`; the host comparison |
| first pull request: the temporary stop removed (a definition's base interfaces after its own) | the `Action<>` and `OverClosed<>` refusal tests |
| an array receiver given no map, or its element's | the existing `ArrayGetInterfaces*` guests |
| an interface with no generic arguments answered as `OpenGenericTypeDefinition` | guests 1 and 2, by reference; a canonical-form example |
| `Formal (G, i)` read at the wrong index (arguments reversed) | `Dictionary<,>`, `Dup<,>`, `Outer<>.Inner<>` |
| the no-expansion rule keyed on the interface being expanded, not on the type being built | none over CoreLib as compiled; equivalent, listed with the plan's other equivalent mutant |
| second pull request: the duplicate check on interface identity alone | `Mixed<>` (`IA<int>` and `IA<T[]>`), `Dup<,>` |
| second pull request: the parent's entries not rebased onto the extends clause | `Derived<int>`, `DeepBase<>` |

**A diagnostic defect.** Every stop message in the probe output renders a type as
`System.Reflection.Metadata.TypeDefinitionHandle`, because `RuntimeTypeHandleTarget.ToString` formats the metadata
handle itself. Filed as #2318; not part of this work, but it will make the refusal
tests' expected messages easier to read once fixed.

**The constraint stop the plan only noted** is filed as #2317, since it is a stop
on a call real .NET answers and no issue recorded it.

### Issues and comments

- New: #2315, #2316,
  #2317, #2318.
- A comment on #1318 with the duplicates, the measured extent, the definition case, and the fix chosen.
- No issue for the closed order: #1318 is it.

### As built (PR 1)

The closed order, closing #1318. The interpreter and tests, and two lines of the TypeSystem (below); nothing public is
added to a published package.

**The function.** The QCall's walk moved, unchanged and in a commit of its own, into
`RuntimeTypeInterfaces.ofReceiver` (`WoofWare.PawPrint/RuntimeTypeInterfaces.fs`, internal to the interpreter), a
function from a receiver target to its ordered interfaces, callable outside a guest. The handler now reads its
arguments, calls it, and allocates the array. This is the one function decision 4 asks the third pull request to extend;
moving it first let the property tests call what the handler calls, and fail before the fix.

**The closed arm.** A `Closed (Concrete _)` receiver answers `InterfaceDispatch.interfaceMapOf`'s entries, in order,
duplicates kept. An array answers `System.Array`'s map, read directly rather than through the array's base type. The old
walk (own rows first, then the base's, deduplicated by closed handle) is deleted, along with its recursion into
structural handles. `interfaceMapOf`'s docstring now names an interface as a receiver and says the map is what
`Type.GetInterfaces()` reports.

**The definition arm.** A generic definition still answers when every row is closed, but stops with
"TODO: RuntimeTypeHandle.GetInterfaces for the generic definition ...: a base class has interfaces, whose entries come
first in the map; a definition's interface map is #2309" when any class on its base chain declares an InterfaceImpl row
(which is exactly when the base's map is non-empty). The chain is walked by `resolveBaseRuntimeTypeHandleTarget`, so an
open-constructed base with no interfaces (`G<T> : EmptyOpen<T>, IDisposable`) is now answered where it stopped before.
A row naming the definition's variables stops as before, its message now naming #2309. Each closed row is followed by
`interfaceMapOf` of that row, each closed handle once; with every row closed, the closed handle is the definition-level
identity, so this is the map's order. This replaces what was left of `collectInterfaces` rather than keeping its
recursive closed arm; the third pull request deletes it either way.

**A TypeSystem fix the move needed.** Codex's review found that `interfaceMapOf` refused an InterfaceImpl row, or an
extends clause, spelled as a TypeSpec with leading custom modifiers (`modopt(M) I<int>`), which the host loads as
`I<int>` and the deleted walk answered. `spelledInterfaceSubstitution` and `MethodTableLayout.baseOfDefinition` now
strip leading modifiers before decomposing the spelling, as CoreCLR passes over them when it loads the type. Dispatch
over such a type was refused before too, and is answered now. Tested with two hand-built images, each red before the
fix.

**Tests** (`TestGetInterfacesOrder.fs`, default suite), each against the host's `GetInterfaces()` over the same images
(for an array, `System.Array`'s), order and duplicates included:

- CoreLib: every exported closed type with an interface, interfaces included (535 receivers on a .NET 10 host, 19 of
  them interfaces), a spread of instantiations (generic classes, delegates, interfaces) and five arrays. Before the fix:
  34 disagreements (every delegate through `MulticastDelegate`, `Type`, `TypeInfo` and its subclasses,
  `CultureAwareComparer`, `OrdinalComparer`).
- Generated hierarchies (FsCheck, 100 fixed and 100 fresh corpora, about 1.5 s): interfaces, classes, structs and
  classes nested in a generic holder, arity 0 to 2, with base classes over the type's own variables, rows over
  variables, arrays, other generated types and the type itself, repaired to compile by dropping rows C# would reject as
  possibly unifying (CS0695). A fixed opening reaches `Dup<T, U> : BaseA<T>, IA<U>` and `Mix2<T> : BaseA<int>, IA<T>`.
  Closed receivers at all-`int`, all-`string` and the two mixed. Floors at half the fixed sample: duplicates 68, rows over
  a base with interfaces 190, value types 65, interfaces 210, nested 100. `InterfaceCorpusGenerator` was not extended: it
  has no base classes and deliberately drops instantiations that could unify, which are what this property is about.
- Shapes C# refuses (decision 10): `ExpansionLib` in two versions, the classes compiled against the first. Closed
  receivers `C<int>`, `C<string>`, `CE<int>`, `S<int>`, `IF<int>`, `Der<int>`, `CClosed`, `C2<int>`, `Der2<int>`,
  `D2<int, string>`, `S2<int>`, and the definition `CClosedG<T> : IC<int>, ID`. Before the fix `Der<int>` and
  `Der2<int>` disagreed.
- Decision 7's check: every CoreLib value type and interface has InterfaceImpl rows plus base interfaces equal to its
  `GetInterfaces().Length`.
- The definition arm: `typeof(Action<>)` and `OverClosed<T> : ClosedBase, IDisposable` are refused, with real .NET's
  answers asserted (`ICloneable`, `ISerializable`; `IB`, `IDisposable`); definitions over `object`, a closed base and an
  open base with no interfaces are answered, including a row with a parent (`IC`, `IB`, `IDisposable`).
- `TestInterfaceDispatchMap`'s CoreLib comparison now includes interfaces (the map only) and five closed generic
  interfaces.

**Guest** `sourcesPure/TypeGetInterfacesOrder.cs`, 14 checks: `ClosedDerived`, `TwoDerived<int>`, `Dup<int, int>`,
`Dup<int, string>`, `Mix2<int>`, `Redecl<int>`, `Derived<int>`, `Action`, `Type`, `IC<int>`, `IList<int>`,
`SOwn<int>`, an enum and `Enum`. It exits 0 on real .NET 10.0.14 and 11.0.0-rc.3, and each of 14 perturbed expectations
(entries swapped, a duplicate removed or added) exits with its own check's number on both. Under PawPrint before the fix
it exited 1.

**A measurement that corrects decision 10.** CoreCLR refuses to load `D2<int, int>` (`D2<T, U> : IC<T>, IA<U>` against
the second version) with `TypeLoadException`: "has a contracting interface set for some instantiations". Its expanded
`IA<T>` and declared `IA<U>` would become one entry, which `LoadExactInterfaceMap` does not allow, whereas a base's entry
duplicated by a declared row (`Dup<int, int>`) loads with both. So `D2<int, int>` is not a receiver, and the test asserts
the host's refusal instead. Nothing in PawPrint checks for it (read, not measured); the shape cannot be compiled by C#
against the library version it runs with, only reached by swapping a dependency.

**Mutants**, each run against `TestGetInterfacesOrder`, `TestInterfaceDispatchMap`, `TestArrayInterfaceMap` and
`TestGetInterfaceMethodImplementation`, and separately the pure guests `TypeGetInterfacesOrder`, `ArrayGetInterfaces*`,
`RuntimeTypeGetInterface*` and `TypeGetInterfaceMap*`:

| mutant | killed by |
|---|---|
| base entries after the type's own (`interfaceMapOf`, today's old order) | CoreLib and generated GetInterfaces properties, shapes, both `TestInterfaceDispatchMap` tests, the guest |
| duplicates merged (closed handle alone in `findExisting`) | generated GetInterfaces property, generated dispatch property, the guest |
| duplicates removed by handle in the closed arm | generated GetInterfaces property, the guest |
| no expansion of a declared row's parents | shapes |
| breadth-first expansion (parents after all declared rows) | shapes |
| parents expanded before the row | CoreLib and generated GetInterfaces properties, shapes, both `TestInterfaceDispatchMap` tests, the guest |
| an array given no map | CoreLib GetInterfaces property, `ArrayGetInterfacesByName`, `Count`, `PointerElement`, `Shape` |
| the definition arm's stop removed | the `Action<>` and `OverClosed<>` refusal tests |
| the definition arm: parents before the row | the answered-definition test |
| the definition arm: no expansion | shapes (`CClosedG<>`), after that receiver was added; it survived before |
| CoreLib interfaces not expanded (decision 7's rule) | none: equivalent over CoreLib as compiled, as decision 7's check states |

**Suites**, after a whole-solution build with the reference assemblies deleted, each its own invocation on a .NET 10
host, at the branch's tip rebased onto `77507f161`: default 5,882 (5,843 passed, 39 skipped); `Guest` 234 passed;
`GuestSolo` 2 passed; `GuestPure` 1,208 passed; `GuestImpure` 303 passed.

### As built (PR 2)

The definition-level interface map, with `interfaceMapOf` re-expressed through it and its own walk deleted. TypeSystem
and tests only; every name added or changed is `internal`, so nothing public is added to or removed from a published
package.

**Commits.**

1. *The definition-level interface map, beside interfaceMapOf's walk*: `interfaceMapOfDefinition` and a temporary
   closing function, the old walk untouched, and a property that the two agree entry for entry (below).
2. *interfaceMapOf is the definition's interface map, closed*: the closing function becomes `interfaceMapOf`, the old
   walk and the property are deleted, and a closed `InterfaceMapEntry` becomes `{ Definition; Interface; Type }`, so
   that the substitution and both flags live only on the definition entry. `ownDispatchMapOf` and
   `DefaultInterfaceImplementation` read them through `Definition`.
3. *Test: a re-declared interface's own row is loaded*, added when mutation testing found no test for it (below).
4. *Plan for #2309: record the second pull request as built*: this section, before Codex's review.
5. *Tests: interface rows C# never writes, and a deep interface chain*, from Codex's review; four failed before the
   next commit, each for the reason the review gave.
6. *Interface map: rows load in the order read; shared contexts close once*: the fix.
7. *Test: a row that adds no entry is still loaded*, added when mutation testing of commit 6 found that the
   re-declared-row test no longer reaches closing (below).
8. *Plan for #2309: the second pull request after Codex's review*: this section, before Codex's second review.
9. *Tests: rows the definition walk cannot read, and a head's base chain*, from Codex's second review; all five cases
   failed before the next commit, each for the reason the review gave.
10. *Interface map: arguments are an exact-load fact*: the fix.
11. *Plan for #2309: the second pull request after Codex's second review*: this section, before the third review.
12. *Tests: rows CoreCLR's approximate load refuses*, from Codex's third review; all four failed before the next commit.
13. *Interface map: what the approximate load refuses*: the fix.
14. *Plan for #2309: the second pull request after Codex's third review*: this section, before the fourth review.
15. *Tests: a TypeSpec row is a generic instantiation; heads load first; cycles*, from Codex's fourth review; six
    cases failed before the next commit, and the cycle test would have recursed without end.
16. *Interface map: TypeSpec shape, heads loaded first, no cycle re-walked*: the fix.
17. and 18. *Plan for #2309*: this section after the fourth review, and what the fifth left open.
19. *Plan for #2309: decisions after Codex's fifth review*: the subsection at the end of this section.
20. *Tests: shared-context chains, cycles, a non-token head, a base's arity*: the decisions' step 2, tests alone,
    with their red run in the message (W1 at a ratio of 178; a plain interface cycle answered; `I<T> : I<T*>`
    overflowing the test host's stack; the `int32`-headed row and `BadHead` throwing).
21. *Tests: a row naming a class over a cyclic base chain is not a cycle case*: the `K : K2`, `K2 : K` row case is
    removed. It hangs in the Loader's base-chain loading, which reading the row's TypeDef token runs before the walk
    sees the row, and which `main` reaches the same way and every concretisation that loads a base chain reaches
    too; that is #2330.
22. *Interface map: recursive loads, a row's shape, a base's arity*: the decisions' step 4.
23. *Test: a definition's map refuses a base chain that reaches itself*, added when the mutant removing the walk's
    entry check survived.
24. *Test: W2, a diamond of two chains of shared contexts*, written with `MetadataBuilder` as the decisions' second
    subsection requires, red before the rebase (ratio 168).

The branch was then rebased onto `002ac9d86` (#2331, the comparison pull request), and W1 and W2 went green with no
change here.

25. *Plan for #2309: the second pull request after the comparison pull request*: this section, before the sixth review.
26. *Tests: an interface walked once per map; an extends clause naming no class*, from Codex's sixth review; the sketch
    oracle now compares PawPrint's entries, as host types, with the host's answer in order. Two cases were red.
27. *Interface map: walk a head once per question; an extends clause naming no class*: the fix.
28. and 29. *Plan for #2309*: this section after the sixth review, and what the seventh left open.
30. *Tests: the sketch oracle builds each host type once; many instantiations*: the decisions after the seventh review,
    tests only.
31. *Plan for #2309*: those decisions, and the list of what this pull request leaves.

**Codex's sixth review, under the decisions' reopening rule.** It found:

- [P1] Two unreadable rows naming the same interface walked that interface's map twice, so a ladder of interfaces
  with two such rows a level doubled the work at every level (Codex: 2.7 MB at depth 8, 45.4 MB at depth 12; the
  test: 679,280 bytes at depth 6 against 45,896,672 at depth 12). Unbounded work on metadata, so fixed here: within
  one question (one call of `interfaceMapOf` or `interfaceMapOfDefinition`) the walk keeps the definitions whose walk
  has succeeded, and a row whose head is one of them does not walk it again. A succeeded walk cannot fail on another
  path: its other failures do not depend on the path, and a recursive load reaching it from a path here would have met
  itself in its own walk. After: 76,064 bytes at depth 6 against 122,616 at depth 12.
- [P2] An extends TypeSpec naming no class (`int32[]`) threw from `nominalIdentityOfSpelling` through
  `interfaceMapOfDefinition`. A host exception, so fixed here: `MethodTableLayout.baseOfDefinition` refuses such a
  clause as `Unmodelled`. A row naming such a class is refused already on the closed path, by the Loader's base-chain
  loading, and a test pins that. The same finding's `C extends valuetype V<int>`, answered by
  `interfaceMapOfDefinition` where .NET refuses, is neither a hang, a host exception nor valid metadata, and is not
  fixed here; `main`'s closed path answers it too.
- [P2] A cyclic base chain with a TypeRef edge does not end in `interfaceMapOfDefinition`, because
  `baseOfDefinition` resolves a TypeRef through `TypeResolution.resolveTypeFromRef`, which primes the whole base chain
  with no guard against a cycle. That is #2330's mechanism, in the Loader, and is left to it.
- [P2] The sketch oracle compared only how many interfaces each side answered. It now compares them, in order, as host
  types over the same image, outside the allocation measured; the chain, the diamond and the four non-cycle shapes
  pass it unchanged.

**The function.** `ConcreteInterfaceDispatch.interfaceMapOfDefinition` takes a definition's identity and returns a
`DefinitionInterfaceMap`: its entries (`DefinitionInterfaceMapEntry`, the decision's five fields, in the definition's
own vocabulary) and every InterfaceImpl row it read (`DefinitionInterfaceMapRow`: the row's spelling, in the same
vocabulary, and the entry it names), closing nothing. The parent definition's map comes first (through
`ConcreteMethodTable.baseOfDefinition`), re-read onto the extends clause's arguments, then each row and its parents,
depth-first, an entry being "the same interface identity, and `substitutionsEquivalent`". The CoreLib rule covers value
types and interfaces, keyed on the type being built, with the comment decision 7 asks for. `interfaceMapOf` loads the
closed type's base chain, takes its definition's map, concretises every row's spelling in the order read, and gives
each entry the handle its first row closed to, asserting that every later row naming the entry closes to the same
handle and that each handle is an instantiation of the interface the entry names. Not memoised.

**Where the decisions were silent.**

- **What is closed.** A closed entry is the concretisation of the row that added it, as the row spells it (a
  `SubstitutionArgument.Spelled` made by `SubstitutionContext.forBase` with one argument), not of the entry's identity
  and `Substitution`. So each row is loaded exactly as the old walk loaded it, with every check concretisation makes of
  a spelling, such as a type argument with a custom modifier, which closing from the substitution would have silently
  looked through.
- **Rows that add no entry.** CoreCLR's exact load reads every row's interface as written, and the old walk
  concretised every row before asking whether it was a duplicate. So the definition map keeps every row, in the order
  read, and closing loads them all in that order. Measured on a .NET 10 host: `C : B, I<int>` over `B : I<int>`, with
  `C`'s row spelled `GENERICINST VALUETYPE I 1 int32`, does not load ("Could not load TypeSpec"); PawPrint refuses with
  `KindMismatch`. An earlier version kept such rows on their entry and closed them beside it, which Codex showed reports
  the wrong one of two failing rows; the rows list replaced it.
- **What the definition walk checks, and what it leaves to closing.** CoreCLR's approximate load of a row
  (`LoadApproxInterfaceMap`) resolves the interface, loads its base chain and checks the spelling's kind; only the
  exact load (`LoadExactInterfaceMap`) reads the arguments, row by row. The definition walk does the same. Before
  reading any later row it refuses a TypeSpec row that is not a generic instantiation (`CLASS I`, `!!0`, `CLASS K`
  for a non-generic `K`: `Unmodelled`, as `ClassLoader::LoadApproxTypeThrowing` refuses them before resolving what
  they name), loads a TypeSpec row's interface's base chain (`BaseTypeAbsent` and the like), and refuses a spelling
  with the wrong kind (`KindMismatch`), a generic interface named by a TypeDef or TypeRef token, which carries no
  instantiation (`UninstantiatedDefinition`), and a type that is not an interface (`Unmodelled`, for
  `IDS_CLASSLOAD_NOTINTERFACE`); before the last two it walks the named type's own map, as the approximate load loads
  that type, so a failure there comes first. The walk carries the definitions whose load is in progress on the path
  to a row, and entering one of them again, at a walk's entry or as soon as a row's head resolves, is refused as
  CoreCLR refuses a recursive load (`IDS_CLASSLOAD_GENERAL`); a diamond is not a cycle. It also refuses an extends clause naming a variable the type does not have, before
  re-reading the parent's map through it. A row whose arguments it cannot read in the definition's vocabulary (a
  variable its context does not supply, or the wrong number of arguments) cannot be compared with the entries; the
  walk still walks that interface's own map, as the approximate load expands its parents, then records the row as
  unreadable, adds no entry, and reads on.
  `interfaceMapOfDefinition` refuses a map holding such a row with the first one's failure; `interfaceMapOf` loads
  every row in order, unreadable ones included, so an earlier row's absent argument is reported first and an unreadable
  row's own arguments are loaded before its arity is refused, as the old walk and the host report them. The first
  version of this pull request refused all of these in the walk, or none, which threw on five malformed images,
  recursed without end on one, answered two that .NET refuses, and reported the wrong failure on eight; Codex's four
  reviews found them, and each is now a test against the host, except the extends clause, which the closed path
  refuses first (in its base chain) and which only `interfaceMapOfDefinition`, not yet called, can reach.
- **Shared contexts.** A chain of generic interfaces nests each interface's arguments in the context of the one above,
  so the same context appears many times in a deep entry. Closing keeps one closed context per shared context, and
  re-reading a parent's map onto the extends clause keeps one re-read context per shared one, so the work is linear in
  the chain's depth. Codex measured the version without this at 165.75 MB for one map of a ten-deep chain of
  three-parameter interfaces, against 0.60 MB on the base. The walk's rows are accumulated last first and reversed
  once; appending them made a deep chain's rows (quadratic in number, since Roslyn lists every interface's parents)
  quartic to build. A row naming an existing entry copies the entries only when it re-declares one; Codex measured a
  128-deep chain at 64.2 MiB allocated with the copy against 23.2 MiB on the base.
- **The base chain.** The closed function walks the closed base chain with `resolveBaseConcreteType` before anything
  else. The definition map needs no closed parent, but the old walk concretised every ancestor first (its recursion
  did), so a type whose ancestor's arguments do not bind failed, and the ancestors were registered before any
  interface. The walk keeps both.
- **The concretisation context** is applied to the state through `ConcreteMethodTable.inContext`, made internal for it.
- **`interfaceMapOfDefinition` has no caller yet.** `interfaceMapOf` reads the walk directly, since it must load the
  rows `interfaceMapOfDefinition` refuses. The function is the third pull request's starting point, as the decisions
  intend; until then its refusal of an unreadable row, and of an extends clause naming a variable the type lacks, is
  exercised by no test, and that pull request's tests should cover both. One gap remains there, found by Codex's
  fourth review and left for that pull request: an extends clause giving its base the wrong number of arguments
  reaches `MethodTableLayout.baseOfDefinition`'s arity `failwith` rather than a refusal. The closed path never gets
  there, since its base-chain load refuses such a clause first.

**Found by Codex's fifth review (on commit 17), and settled by the decisions at the end of this section.** The
comparison is shared within one call by #2331; the cycle, the TypeSpec head and the base's arity are fixed here
(commit 22); a class head's approximate instantiation is #2329, with its diagnostic corrected and two shapes pinned as
refusals. After the rebase onto #2331, W1 allocated 350,056 bytes at depth 8 and 676,912 at depth 16 (ratio 1.9), and
W2 378,416 and 1,126,640 (ratio 3.0); both images are written with `MetadataBuilder`, one row per interface, so what
they hold is linear in the depth. The review found:

- **[P1] Exponential comparison on valid metadata.** The definition walk filters duplicate candidates by interface
  identity, where the old walk filtered by closed handle, so distinct instantiations of one interface now reach
  `substitutionsEquivalent`, which re-walks shared argument contexts without memoising them. Codex measured a 16-level
  chain of interfaces passing `Pair<A, A>` and `Pair<A, B>` on, with `I<A>` and `I<B>` at the bottom: 173 MB allocated
  for one map, against 0.36 MB on the base. The fix is a structural choice the decisions do not make: memoise pairs of
  shared contexts inside `TypeConcretization.substitutionsEquivalent` (published code that every signature comparison
  uses), or compare a canonical, shared form of definition-level types in the walk.
- **[P1] A malformed `I<T> : I<T*>`** expands without end: each level adds a pointer, so no entry is ever a duplicate.
  The expansion path needs to carry the interface definitions on it and refuse re-entry (CoreCLR refuses cyclic
  interface inheritance); the old walk refused the pointer argument when closing.
- **[P2]** A TypeSpec row whose head is not `CLASS <token>` (an `int32` head reaches a `failwith`; `VALUETYPE Missing`
  reports `TypeAbsent` where the host refuses the shape). A TypeSpec naming a class (`G<int, int>` for a one-parameter
  `G`) should load that instantiation approximately, and report its failure, before refusing it as not an interface.
  `MethodTableLayout.baseOfDefinition`'s arity `failwith` is reachable through a row head's base, not only through
  `interfaceMapOfDefinition`, and should become `Refused (WrongArity ...)`.

**Codex's seventh review (on commit 28), and what was decided.** The seventh round was the last. Its finding on valid
metadata, a class implementing many instantiations of one interface, stopped the work as the rule said, and the
decisions at the end of this section accepted it as a cost: CoreCLR's `ExpandApproxInterface` makes the same
`n(n-1)/2` comparisons, so only the price of one differs (#2335). A growth test pins the count: 128 instantiations
allocate 17,084,720 bytes against 4,683,656 for 64, a ratio of 3.6 where quadratic work gives 4. The bare-TypeSpec
extends clause was filed with `extends valuetype V<int>` (#2336). The sketch oracle now builds each host type once and
compares `Type` values, and its own allocation is bounded beside the code's (commit 30). There was no eighth round.

**Left by this pull request, each with its issue.**

- **#2329, the failure named for a malformed row.** A row naming a class at an instantiation CoreCLR's approximate
  load refuses (`G<int, int>` for one-parameter `G`, `G<valuetype Missing>`) is refused as not an interface, where
  the host names the earlier failure; and a TypeSpec head spelled `VALUETYPE Missing` reports `TypeAbsent` where the
  host refuses the shape. Both sides refuse; which refusal is named differs. Two tests pin the refusals.
- **#2336, the extends-clause shapes.** An extends clause spelled as a bare TypeSpec (`CLASS B`) or naming a value
  type (`valuetype V<int>`) is answered by `MethodTableLayout.baseOfDefinition` where CoreCLR refuses; that arm is on
  `main`. Only `interfaceMapOfDefinition`, which has no caller before PR 3, reaches it from this pull request.
- **#2335, the comparison's constant.** About 1.9 kB allocated per duplicate comparison of two instantiations of one
  interface: 253 MB for one map of a class implementing `I<A0>` to `I<A511>`, against 10.7 MB on `main`, which did not
  compare distinct closed instantiations.
- **#2330, the cyclic base chain.** The Loader's base-chain loading follows a cyclic chain without end, both through a
  row naming such a class and through `resolveTypeFromRef`'s priming of a TypeRef base; `main` reaches it the same way.

**The equivalence property** (commit 1, `DefinitionInterfaceMapEquivalence.fs`): for each closed receiver, the old
walk's map and the closed definition map agree entry for entry in closed handle, `ConcreteType`, `TypeInfo`,
substitution (by `substitutionsEquivalent`) and both flags. It ran over every receiver of `TestInterfaceDispatchMap`'s
two corpora (969 CoreLib receivers with 3,513 entries; 7,621 generated receivers with 27,121 entries) and over
`TestGetInterfacesOrder`'s closed receivers (CoreLib, the generated hierarchies, the shapes C# refuses): 11 tests, all
passed, on a .NET 10 host. It was checked not vacuous on commit 1 by mutating the new walk alone: a duplicate check on
identity alone failed four of the tests, and a re-declared entry left undeclared failed five.

**The before-and-after dump.** A throwaway patch to the two test files, not committed, wrote one record per receiver of
`TestInterfaceDispatchMap`'s two corpora (every entry's handle, both flags and substitution) and of
`TestGetInterfacesOrder`'s three (every answered target, with the expected targets built after the call rather than
before, so that what the call registers shows), then each receiver's newly registered handles in order, with their
definitions and arguments, and the assemblies loaded: 179,251 lines. On the base (#2322's tip) it was identical over
two runs; on commit 1, identical to the base, byte for byte. On commit 2 four files are identical and one differs: the
substitutions, and nothing else, of five CoreLib interface receivers (`IList<int>`, `IDictionary<string, object>`,
`IReadOnlyList<Exception>`, `ISet<int>`, `INumber<int>`). That is decision 7: a CoreLib interface no longer expands its
rows' parents, so an entry such as `IList<T>`'s `IEnumerable<T>` now comes from the interface's own row (spelled
`!0`) rather than from expanding `ICollection<T>` (spelled `!0` in `ICollection`'s context, over `IList`'s `!0`); the
two denote the same types, and the handles, order, flags, registrations and loads are unchanged. With only that rule
put back to value types, commit 2's dump is identical to the base in all five files. After the fixes of commits 6, 10, 13, 16, 22 and 27, and the rebase onto #2331,
the dump (with the deep chain's two receivers added in a sixth file) is identical to commit 2's in all five files it
shares with it, and so is the probe below; the sixth is identical across all of them.

**What moved, measured.** In those corpora every assembly is loaded before the walk, so the dump cannot see load
order. A throwaway probe (not committed) compiled classes against one version of a library and ran them against a
second that drops some of its types, and asked the host's `GetInterfaces()` and PawPrint's `interfaceMapOf`:

| type (rows) | host | old walk | new walk |
|---|---|---|---|
| `TwoArgFirst : IEquatable<Gone>, IGone` | `IGone` absent | `Gone` absent | `IGone` absent |
| `TwoHeadFirst : IGone, IEquatable<Gone>` | `IGone` | `IGone` | `IGone` |
| `TwoArgs : IEquatable<Gone>, IGen<Gone2>` | `Gone` | `Gone` | `Gone` |
| `TwoHeads : IGone, IGone2` | `IGone` | `IGone` | `IGone` |
| `DerHead : BaseArg, IGone`, `BaseArg : IEquatable<Gone>` | `IGone` | `Gone` | `IGone` |
| `ArgThenGenHead : IGen<Gone>, IGoneGen<int>` | ``IGoneGen`1`` | `Gone` | ``IGoneGen`1`` |

So a doubly broken type now reports the failure the host reports in all six shapes, where the old walk did in three:
the definition walk resolves every row's interface before any argument, as `LoadApproxInterfaceMap` does before
`LoadExactInterfaceMap`. For `LoadOrder : IEquatable<BitVector32>, IChangeTracking, IKeep` the old walk loaded
`System.Collections.Specialized`, `System.Runtime`, `System.ComponentModel`, and the new one loads
`System.ComponentModel`, `System.Collections.Specialized`, `System.Runtime`. A console program on real .NET 10, which
subscribes to `AssemblyLoad` and then asks `GetType("LoadOrder").GetInterfaces()`, loads `System.ComponentModel` then
`System.Collections.Specialized` (`System.Runtime` is already loaded at startup there), the new order. With two rows
of one interface (`IGen<BitVector32>, IGen<IChangeTracking>`) nothing moves. `TestDispatchBindingFailure` and every
guest stayed green without edits.

Codex's reviews found more malformed images on which an earlier version answered differently from the old walk and the
host. The tests named under commits 5, 7, 9, 12 and 15 now pin each against the host.

**Mutants**, each applied to commit 2 (those marked later to that commit, against the tests that name the shapes) by
an exact replacement, the test project built, and two selections run: the
tests (`TestInterfaceDispatchMap`, `TestGetInterfacesOrder`, `TestDispatchBindingFailure`, `TestArrayInterfaceMap`,
`TestGetInterfaceMethodImplementation`, `TestInterfaceMethodImplementation`) and the pure guests whose names contain
`Interface` or `TypeGetInterfacesOrder` (64). The file was restored and checked against `HEAD` after each.

| mutant | killed by |
|---|---|
| base-class entries after the type's own | both CoreLib and both generated host comparisons, shapes, the `GetInterfaceMap` corpora; guests `TypeGetInterfacesOrder`, `VariantInterfaceMapOrder` |
| entries deduplicated by closed handle after closing | generated `GetInterfaces` and dispatch comparisons; guests `InterfaceDispatchMapShapes`, `TypeGetInterfacesOrder` |
| own rows deduplicated against the parent's entries by closed handle | the same four |
| a declared row's parents not expanded | shapes |
| parents appended after all declared rows (breadth-first) | shapes |
| parents expanded before the row | both CoreLib and both generated comparisons, shapes; guest `TypeGetInterfacesOrder` |
| duplicate check on interface identity alone | CoreLib dispatch, both generated comparisons, shapes; seven guests (`InterfaceDispatchMapShapes`, `InterfaceSlotHiddenByDerivedMethod`, `TypeGetInterfaceMapTargets`, `TypeGetInterfacesOrder`, `VariantInterfaceDefaultBodyPrecedence`, `VariantInterfaceMapOrder`, `VariantInterfaceSlotOwnership`) |
| the parent's substitution not rebased onto the extends clause | CoreLib dispatch, both generated comparisons, shapes; guest `TypeGetInterfacesOrder` |
| the parent's spelling not rebased onto the extends clause | eleven tests, among them the modified-TypeSpec base case; four guests |
| a re-declared row not closed | the re-declared row test (commit 3); it survived the first battery, before that test existed |
| (after commit 6) the head's kind not checked by the definition walk | the wrong-kind-before-absent test |
| (after commit 6) a bare generic spelling not refused | the bare generic test |
| (after commit 6) a variable out of scope not refused | the out-of-scope variable test |
| (after commit 6) rows closed in entry order rather than the order read | the row-order test |
| (after commit 6) closed contexts not shared | the deep chain (ten minutes, then failed) |
| (after commit 6) re-read contexts not shared | the deep chain (73 s, then failed) |
| (after commit 6) rows that add no entry not closed | the no-entry row test (commit 7); it survived before that test existed, since commit 6's head check refuses the re-declared-row test's row before closing |
| (after commit 10) a variable out of scope refused by the walk | the earlier-absent-argument test (`!0` case) |
| (after commit 10) the wrong arity refused by the walk | the earlier-absent-argument test (arity case), the absent-argument-before-arity test |
| (after commit 10) the head's base chain not loaded before its kind | the absent-head-base test |
| (after commit 10) an unreadable row reported without loading it | the absent-argument-before-arity test |
| (after commit 13) a non-nominal head left to closing | the `!!0`-before-absent test |
| (after commit 13) only a TypeSpec checked for a missing instantiation | the TypeDef-row test |
| (after commit 13) a head that is not an interface accepted | the `System.Object` row test |
| (after commit 13) an unreadable row's interface not walked | the absent-parent test |
| (after commit 16) a TypeSpec row's shape not checked | five tests: the four moved to the shape refusal, and `CLASS K` |
| (after commit 16) a refused head's own map not walked first | the class-with-absent-interface test |
| (after commit 16) an unreadable row's interface walked again within a cycle | the cycle test (the test host overflowed its stack) |
| (after commit 22) the in-progress check removed from the expansion path | the cycle tests (the test host overflowed its stack) |
| (after commit 22) the in-progress check removed from the walk's entry | the base-chain cycle test (commit 23; stack overflow); it survived before that test existed, since every other path into the walk meets the head check first |
| (after commit 22) the in-progress set global rather than local to the path | the diamond, and both cases of the earlier-absent-argument test |
| (after commit 22) the row classification accepting a non-nominal head | both `int32`-head tests |
| (after commit 27) a head walked again for every row naming it | the wrong-arity ladder |
| (after commit 27) an extends clause's shape not checked | the definition-level `D : int32[]` test |
| the CoreLib no-expansion rule removed | none: equivalent over CoreLib as compiled, as decision 7's check states |
| the rule widened to every CoreLib type | none: equivalent, the same reason |
| the rule keyed on the interface being expanded, not the type being built | none: equivalent, as the decisions record |

**Suites**, after a whole-solution build with the reference assemblies deleted, each its own invocation on a .NET 10
host. At commit 3, on `24c6fdb3c`: default 5,886 (5,847 passed, 39 skipped); `Guest` 234 passed; `GuestSolo` 2 passed;
`GuestPure` 1,208 passed; `GuestImpure` 304 passed. At commit 6, on `3a6d1c53c` (whose two commits beyond `24c6fdb3c`
change only package descriptions, READMEs, CI and a page under `docs/developer`): default 5,891 (5,852 passed, 39
skipped); `Guest` 234 passed; `GuestSolo` 2 passed; `GuestPure` 1,208 passed; `GuestImpure` 304 passed. At commit 10,
on `3a6d1c53c`: default 5,897 (5,858 passed, 39 skipped); `Guest` 234 passed; `GuestSolo` 2 passed; `GuestPure` 1,208
passed; `GuestImpure` 304 passed. At commit 13, on `3a6d1c53c`: default 5,901 (5,862 passed, 39 skipped); `Guest` 234
passed; `GuestSolo` 2 passed; `GuestPure` 1,208 passed; `GuestImpure` 304 passed. At commit 16, on `3a6d1c53c`:
default 5,905 (5,866 passed, 39 skipped); `Guest` 234 passed; `GuestSolo` 2 passed; `GuestPure` 1,208 passed;
`GuestImpure` 304 passed. At commit 24, rebased onto `002ac9d86` (#2331): default 5,931 (5,892 passed, 39 skipped);
`Guest` 234 passed; `GuestSolo` 2 passed; `GuestPure` 1,208 passed; `GuestImpure` 304 passed. At commit 27, on
`002ac9d86`: default 5,934 (5,895 passed, 39 skipped); `Guest` 234 passed; `GuestSolo` 2 passed; `GuestPure` 1,208
passed; `GuestImpure` 304 passed. Commits 28 to 31 change only tests and this plan; at commit 31 the default suite
ran once more: 5,935 (5,896 passed, 39 skipped).

**Time of the guest categories**, on the same machine, one category at a time, in seconds: on the base (#2322's tip),
and at commits 3, 6, 10, 13, 16, 24 and 27.

| category | base | commit 3 | commit 6 | commit 10 | commit 13 | commit 16 | commit 24 | commit 27 |
|---|---|---|---|---|---|---|---|---|
| `Guest` | 1,092 | 1,179 | 1,698 | 1,395 | 1,175 | 1,515 | 1,131 | 956 |
| `GuestSolo` | 545 | 747 | 1,193 | 637 | 648 | 665 | 586 | 499 |
| `GuestPure` | 910 | 717 | 796 | 842 | 715 | 776 | 864 | 532 |
| `GuestImpure` | 883 | 289 | 408 | 396 | 687 | 693 | 687 | 416 |

The machine was shared with other runs, and the differences go both ways by more than any change in this branch could
explain (`GuestImpure` took 505 to 560 s in PR 1's runs; `GuestSolo`, two tests, ranged from 545 to 1,193 s here), so
these runs show no slowdown that stands out from that noise, and nothing asks for memoising the definition map. A
quieter machine would be needed to see a difference of a few per cent.

#### Decisions after Codex's fifth review

Decided 2026-11-18 by the delegated decider, after reading the branch at `2c9dc9c13`, `origin/main` at `3a6d1c53c`,
and the pinned .NET 10 source. "Read" below means read in the code; nothing was built or run for these decisions, so
each claim marked *read, not measured* is for the implementer to confirm with the test named beside it.

**In brief.**

| finding | decision |
|---|---|
| 1. exponential comparison | Share work inside the comparison itself (option A below). It lands **first, as its own pull request against `main`**; this pull request is rebased onto it. |
| 2. `I<T> : I<T*>` never ends | Fix here: one "load in progress" set, checked wherever a definition's walk is entered. |
| 3. a TypeSpec head that is not nominal | Fix the `failwith` here. The `VALUETYPE Missing` half is filed. |
| 4. a class head's approximate instantiation | Filed, not fixed here. The diagnostic is corrected and the shapes are pinned as refusals. |
| 5. `baseOfDefinition`'s arity `failwith` | Fix here: it becomes `Refused (WrongArity ...)`. |
| sixth Codex round | Yes, one, with a stated rule for what it may reopen; a seventh at most. |

##### 1. The comparison

**The claim, checked.** It is right, and the comparison is the whole of it.

- `compareElements` (TypeConcretisation.fs) resolves a spelled `!i` through `readTypeVariable` to the context's
  `i`th argument, itself a `Spelled` carrying its own context, and recurses. Nothing remembers a pair it has already
  compared. A spelling that mentions a variable twice (`Pair<!0, !0>`) therefore walks the context below it twice, and
  a chain of `n` such levels is walked as a tree of `2^n` leaves although it is held as `n` shared arrays.
- Codex's chain compares `I<X16>` with `I<Y16>`, where `X(n) = Pair<X(n-1), X(n-1)>` and
  `Y(n) = Pair<X(n-1), Y(n-1)>`. The first arguments are equal, which costs the full `2^(n-1)`, and the second differ
  only at the bottom, so the total is about `2^n` element comparisons. 173 MB over `2^16` is roughly 2.6 kB a step,
  which is what `allEqual`, `List.zip` and the token comparison allocate.
- The walk itself is not at fault: `rebaseMap` and the closing in `interfaceMapOf` each keep one result per shared
  context already.
- **Why the old walk avoided this input.** Its filter was the closed handle. Handles are interned, so `I<X16>` and
  `I<Y16>` have different handles and were never compared. It called `substitutionsEquivalent` only for two rows that
  close to one handle.
- **The old walk is exponential on a different valid input** (*read, not measured*): two rows that do close to one
  handle through two deep paths, such as `C : L16<int>, R16<int>` where both chains pass `Pair<A, A>` down to `I<A>`.
  There the handles agree, `substitutionsEquivalent` runs on two equal substitutions, and equality is the expensive
  answer. So the defect is in the comparison and predates this branch; the branch widened the set of inputs that
  reach it. Test W2 below measures this on `main`.
- **CoreCLR compares the same way, and is exponential too** (*read, not measured*). `ExpandApproxInterface`
  (methodtablebuilder.cpp:347) calls `MetaSig::CompareTypeDefsUnderSubstitutions` in the *approximate* pass: same
  TypeDef, then `CompareElementType` on each argument blob under the substitution chain (siginfo.cpp:4391).
  `CompareElementType` follows the chain afresh at every `ELEMENT_TYPE_VAR` (siginfo.cpp:3815-3862) and keeps no
  record of pairs. It does not compare approximate types by identity and leave exactness to a later pass:
  `ExpandExactInterface` (:10252) makes the same comparison again. The exact pass does have a handle filter
  (`InsertMethodTable`, "technique 1", :9618-9666), which is what the old walk's filter resembled, but it decides only
  whether the substitution comparison is needed for a closed type, never the definition's map. So the duplicate rule
  is settled, PawPrint's statement of it is already CoreCLR's, and only its cost is open. A chain deep enough hangs
  real .NET as well; PawPrint's step is about a thousand times dearer, which is why sixteen levels already hurts.

**Options.**

- **A. Share work inside the comparison.** `compareElements` keeps, for the length of one public comparison, the set
  of pairs it has already proved equal, and answers `true` for a pair in it. Every recursive step of the comparison is
  a conjunction (`allEqual`), so a `false` or an `Error` ends the whole comparison, and the only result that can ever
  be reused is `true`: the "memo" is a set, not a table.
  - *Correctness:* unchanged by construction. A pair enters the set only after the unshared comparison answered
    `true` for it without throwing, and the values are immutable, so a hit returns what recomputing would. Assembly
    loads made while proving a pair are made the first time and are idempotent after. No `failwith` or refusal is
    skipped, since a pair that throws or refuses never enters the set.
  - *Blast radius:* a private function of a published package changes; no signature, no public type, no answer.
    The risk is a wrong key. It is bounded by keeping the set out of closed comparisons altogether (below) and by the
    oracle property T2.
  - *Reversibility:* one private function and a local set; reverting it restores today's code exactly.
  - *For PR 3:* nothing changes. A definition entry's `Substitution` stays what it is, CoreCLR's own
    `Substitution` chain, which is what decision 4's conversion to targets reads.
- **B. A canonical, interned form of definition-level types in the walk,** compared by identity. To agree with
  CoreCLR it would have to restate everything `compareElements` decides: that `object` and `class System.Object` are
  different spellings, modifier order, the refusal of two GENERIC function pointers, the mixed-world `failwith`s, and
  the order in which TypeRefs are resolved (canonicalising resolves every TypeRef up front, where the comparison
  resolves them as it meets them and stops at the first difference, so assembly load order and the failure reported
  can both move). That is a second statement of the duplicate rule, held to the first by a property test, which is
  the arrangement "Staging" rejected for the walk itself. It adds a representation PR 3 would have to convert from or
  ignore, and it leaves `substitutionsEquivalent` exponential for `ownDispatchMapOf` and for every signature
  comparison through a deep base chain. Rejected.
- **C. A cheaper necessary condition before the comparison** (a fingerprint of the substituted type computed once per
  shared context, or the closed handle put back as a filter where a closed receiver exists). It removes Codex's
  example and nothing else: two equal instantiations pass any sound filter and then cost `2^n`, which is the input the
  old walk already mishandles. It bounds nothing. Rejected. A closed-handle filter would also give the closed path a
  rule the definition path lacks, against decision 3.
- **D. Leave it, since CoreCLR is exponential too.** Rejected: this branch takes a valid input from 0.36 MB to
  173 MB, and "no slower than the host asymptotically" is not a guarantee anyone can use at 2.6 kB a step.

**Chosen: A, as its own pull request against `main`, landing before this one.** It is a change to published code
that every signature comparison runs through, it is independent of the interface map, it fixes an input `main`
already mishandles, and its tests need no metadata image. Reviewed alone it is a few dozen lines; inside this pull
request it would be the one hunk a reviewer most needs to see and is least likely to find. Branch it from
`origin/main`, do not stack it. This pull request is rebased onto `main` once it merges, and changes nothing in
`TypeConcretisation.fs`.

Constraints on the implementation:

- The set lives for one call of a public comparison (`substitutionsEquivalent`, `signaturesEquivalent` and their
  siblings), as a local in that call. Nothing outlives the call and no signature changes. One mode for every entry
  point: a second, unshared mode would be a second comparison to keep in step.
- A pair is recorded only where sharing can arise: when at least one side was reached by reading a variable whose
  argument is `Spelled`. A closed comparison (every argument `Closed`) and an unsubstituted one must never allocate
  or consult the set, so that what the interpreter does for live objects runs exactly the code it runs today. Create
  the set lazily.
- The key is identity, never structure: on each side the assembly name, the context array (an `ImmutableArray`
  compares and hashes by the array it wraps, as `rebaseMap` already relies on) and the spelling node by reference
  (`HashIdentity.Reference`). The assembly is part of the key because one `TypeDefn` object is not guaranteed to
  belong to one image. Do not key on a `SubstitutionArgument` or `TypeDefn` by `=`.
- Do not add "the same object on both sides is equal" as a shortcut. `compareElements x x` is not always `true`: it
  can refuse (two GENERIC function pointers) or fail to resolve a TypeRef, and a shortcut would turn those into an
  answer.
- Record a pair after it is proved, not before.

**Tests for that pull request, written first and run on `main`.**

- **T1, red on `main`.** No image: build contexts with `SubstitutionContext.forDefinition` and repeated `forBase`
  over a two-parameter CoreLib generic (`KeyValuePair<!0, !0>` and `KeyValuePair<!0, !1>` at each level), in the two
  shapes above: Codex's (differing at the bottom of the last argument) and two equal substitutions built separately,
  so that no array is shared between the sides. Assert the answer, and that the bytes allocated by one comparison
  (`GC.GetAllocatedBytesForCurrentThread`, after one untimed warm-up call) at depth 16 are at most 8 times those at
  depth 8. Exponential work gives a ratio near 256 and linear work near 2, so the assertion has a factor of thirty on
  either side and no clock in it. Record the two ratios measured on `main` in the commit message.
- **T2, the oracle; green before and after.** For generated pairs of small substitutions (depth at most 6, contexts
  of one to three arguments, spellings drawn from `!i`, `KeyValuePair<!i, !j>`, `!i[]`, `!i*` and one level of nesting,
  all in one assembly with no TypeRefs and no modifiers), `substitutionsEquivalent` agrees with a reference written in
  the test: expand each side to a tree by substituting every variable, and compare the trees structurally. Within that
  fragment structural equality of the expansions *is* the rule. Generate both sides from a small alphabet so that
  equal pairs are common, and assert with `Prop.cover` (or a counted classification) that each answer occurs in at
  least a fifth of cases, so the property cannot pass on `false` alone.
- **T3, the bound.** Over generated chains of depth 1 to 20 (same spelling alphabet, the two sides either built
  separately from one recipe or differing in one leaf of the last argument at the bottom), the bytes allocated by one
  comparison are at most `K * depth * (1 + s)^2`, where `s` is the largest number of nodes in one level's spellings
  and `K` is twenty times the largest constant measured after the fix. That is quadratic in what the metadata holds
  and independent of how often it is shared. At depth 20 unshared work allocates gigabytes, so a regression fails by
  three orders of magnitude and still finishes in seconds; the depth is capped at 20 so that a red run ends. A step
  counter would be exact, but it would put a parameter into published code for the tests' sake alone, so allocation
  is the measure.
- **Mutants T2 must kill:** the key without the right-hand side; the key without the context (spelling node alone);
  a `false` or unproved pair recorded. T1 or T3 must kill "the set never consulted".

**Tests added to this pull request after the rebase.**

- **W1.** Codex's sixteen-level chain as an image, through `interfaceMapOf`, against the host's `GetInterfaces()`
  (19 entries), with the allocation of one map at sixteen levels at most 32 times that at eight. Run it on this
  branch *before* the rebase and record the failure: that is the red run.
- **W2.** The diamond `C : L16<int>, R16<int>` described above, same two assertions. Run it on `origin/main` before
  the comparison pull request and record what it does. If it is not exponential there, say so in the body of the
  comparison pull request and strike the "predates this branch" sentence above; the decision does not change.
- The map's *size* can be exponential in depth legitimately (each level passing two distinct instantiations on), and
  CoreCLR builds that map too. So any bound over generated hierarchies is stated in rows read and entries produced,
  not in depth alone.

##### 2. An interface that reaches itself: fix here

`origin/main` ends on `I<T> : I<T*>`, by accident: closing refuses a pointer argument. This branch never ends, so it
is this pull request's to fix. The accident does not cover `I<T> : I<List<T>>`, on which the old walk also expands
without end (*read, not measured*), and both walks *answer* the plain cycle `I<T> : J<T>`, `J<T> : I<T>` (the second
visit is a duplicate, so the expansion stops and a map comes back).

CoreCLR has one rule for all of them (*read, not measured*). The approximate load of a row loads the interface's
generic *definition* to `CLASS_LOAD_APPROXPARENTS` (`LoadApproxTypeThrowing`, clsload.cpp:2398), which loads that
definition's own rows. A definition whose load is already in progress on the thread is a recursive load, refused with
`IDS_CLASSLOAD_GENERAL` (clsload.cpp:3222-3226). Arguments are not loaded in that pass, which is why
`struct V : I<V>` and `class C<T> : I<C<T>>` are legal and `I<T> : I<anything>` is not.

So: **one set of definitions whose load is in progress, and entering the walk of a member of it is the refusal.**
It replaces today's `walking` set and its two special cases (`walkHead` returning the other refusal on re-entry,
`unreadableRow` silently not walking). It is checked in one place that both `walkDefinition` and the recursion of
`expand` into an interface's rows pass through, immediately after the row's head resolves and before its kind, arity
or arguments are looked at. It is local to the path, so a diamond is unaffected. The refusal is
`TypeBindingFailure.Unmodelled` with a diagnostic naming the definition and `IDS_CLASSLOAD_GENERAL`, like this
branch's other approximate-load refusals; a new `TypeLoadRefusal` case would be a public addition to a published
package, and making the refusal catchable by a guest is a feature of its own.

Checking at entry to `walkDefinition` also closes a hole nobody has reported: a base-class cycle reached through a
TypeDef or TypeRef row head (`K : K2`, `K2 : K`, no base chain loaded first) recurses through `baseOfDefinition`
with nothing to stop it (*read, not measured*).

Red first, each against the host, each run under an external time limit (NUnit's `Timeout` cannot stop a loop, and
the last cycle test overflowed the host's stack):

- `I<T> : I<T*>`: does not end on this branch.
- `I<T> : I<List<T>>`, and the indirect `I<T> : J<T[]>`, `J<T> : I<T>`: expected not to end on `main` either.
- `I<T> : J<T>`, `J<T> : I<T>` under `C : I<int>`: expected to be answered today where the host refuses.
- A row naming class `K` by TypeDef, with `K : K2`, `K2 : K`.
- Green throughout: a diamond (`C : L, R`, both over `I`), `struct V : I<V>`, `class C<T> : I<C<T>>`,
  `I<T> : J<I<T>>`.

`A cycle through an unreadable row is refused` expects `WrongArity` today. Under this rule the cycle is found first,
as CoreCLR finds it before any arity, so its expectation becomes the cycle refusal. That edit to an existing test is
intended; say so in the commit.

If the host answers any of the shapes above, stop and report: the rule as read is then wrong.

##### 3. A TypeSpec head that is not nominal: fix the `failwith` here

`main` returns `Error (Unmodelled ...)` for a row such as `GENERICINST int32 1 int32`; this branch throws. Fix it by
parsing rather than checking: classify the row once into "a TypeDef or TypeRef token" or "`GENERICINST` over a nominal
head, with arguments", or refuse its shape (`Unmodelled`, `IDS_CLASSLOAD_GENERAL`) before anything it names is
resolved, and have the rest of `expand` take the classified value. The `BUG:` `failwith` then has no arm to live in.
Red first: the `int32`-headed row, alone and before a later absent row.

The other half is filed, not fixed: CoreCLR also refuses a head spelled `VALUETYPE` before binding its token, so
`GENERICINST VALUETYPE Missing ...` is a shape refusal on the host and `TypeAbsent` here. `main` says `TypeAbsent`
too, both refuse, and the head that exists keeps its `KindMismatch`. It goes in #2329.

##### 4. A class head's approximate instantiation: filed

Not fixed here, and the caller's rule ("what this pull request introduces is fixed in it") deserves a direct answer.
On a row naming a class this branch differs from `main` in three ways:

| row | host | `main` | this branch |
|---|---|---|---|
| `G<int>`, well formed, `G` a class | refuses | **answers, with a class in the map** | refuses (`Unmodelled`) |
| `G<class Missing>` | not an interface | `TypeAbsent` | refuses as not an interface |
| `G<int, int>` for one-parameter `G` | wrong arity | `WrongArity` | refuses as not an interface |
| `G<valuetype Missing>` | missing type | `TypeAbsent` | refuses as not an interface |

Every row is refused on this branch, loudly, and the first is a silent wrong answer on `main`. What moved is which
refusal is named, toward the host in one row and away from it in two. Naming it correctly needs a primitive PawPrint
does not have: CoreCLR's approximate load of an instantiation (`SigPointer::GetTypeHandleThrowing` with
`dropGenericArgumentLevel`, siginfo.cpp:1457-1600), which replaces each argument that is a GC reference or a
`GENERICINST CLASS` by `__Canon` without loading it, loads every other argument recursively under the same rule,
and only then checks arity. A partial version (arity alone, or top-level value types alone) would name the right
refusal for Codex's two examples and the wrong one for their combinations, and would be a third thing to keep in step
with the host. It is one feature, with its own measurements, and nothing in PRs 3 to 5 needs it.

In this pull request:

- The diagnostic of the not-an-interface refusal must not claim more than is modelled: it says the row names a type
  that is not an interface, and that CoreCLR refuses the type either for that (`IDS_CLASSLOAD_NOTINTERFACE`) or for
  an earlier failure of the class's approximate instantiation, which is not modelled, naming #2329.
- Two tests pin the shapes as refusals without pinning the identity: for `G<int, int>` and `G<valuetype Missing>`
  the host fails and PawPrint returns `Error`, with #2329 named in the docstring. They are green now; they exist
  so that the fix for #2329 has a test to tighten.

Filed as #2329, which the diagnostic and the two docstrings name.

##### 5. `baseOfDefinition`'s arity `failwith`: fix here

`walkHead` is new in this pull request and is what lets a closed receiver reach it: `C` with a row naming `BadHead`,
where `BadHead : G<int, int>` over a one-parameter `G`. `main` never loads `BadHead`'s base and answers silently, so
a throw is not worse, but the fix is three lines in a function that already returns a `Result`, and it also closes
the gap "As built" leaves for PR 3. `MethodTableLayout.baseOfDefinition` returns
`Error (TypeBindingFailure.Refused (TypeLoadRefusal.WrongArity (baseIdentity, declared, supplied)))`, and its comment
says the clause can be malformed rather than that the image would not load. Its other callers get an `Error` where
they would have thrown, and each already propagates one.

Red first: Codex's `C`/`BadHead` image through `interfaceMapOf` (throws today; the host reports a type-load
failure), and the same extends clause through `interfaceMapOfDefinition` on `BadHead` itself, which gives that
function its first test.

##### Order of work, and the sixth review

1. The comparison pull request, from `origin/main`: T1 to T3 first, then the change, then its own Codex review. W2's
   run on `main` goes in its body.
2. On this branch, before rebasing: add W1, the cycle tests, the `int32`-head tests and the `BadHead` tests in one
   commit of tests alone, with the failing output recorded.
3. Rebase onto `main` with the comparison merged; W1 and W2 go green with no change here.
4. Findings 2, 3 and 5, and finding 4's diagnostic and two tests. Mutants: the in-progress check removed from the
   expansion path; removed from `walkDefinition`'s entry; made global rather than local to the path (must fail the
   diamond); the head classification accepting a non-nominal head.
5. Suites, the dump, and "As built" brought up to date, as for the earlier rounds.

**Run a sixth Codex round on this pull request**, since steps 2 to 4 change the walk's control flow. What it may
reopen is fixed in advance:

- A finding on valid metadata, or a hang or a host exception reachable from any metadata, is fixed here, tests
  first, and reviewed again.
- A finding only about *which* refusal is named for an image that both PawPrint and the host refuse is added to the
  #2329 and not fixed here. It does not call for another round.
- A finding that needs a structural choice stops the work, as before.

A seventh round is the last. If it still has a finding of the first kind, stop and report rather than fix: six rounds
of approximate-load corrections would then be saying that interleaving the approximate load's checks with the map's
construction is the wrong shape, and the choice to make is whether the approximate load of a row becomes a function
of its own which the walk calls.

**No further split of this pull request.** The re-expression cannot land without the row checks, because closing is
no longer what refuses a malformed row, and the checks are what five rounds have been about.

##### After the comparison pull request's first review

Decided 2026-11-18, after reading `fix-substitution-comparison-sharing` at `d3000bac5`, Codex's first review of it,
and the implementer's W2 probe and its logs. Nothing was built or run for these decisions.

**The three P2s: none changes the code.**

1. **Stack depth on a purely unary chain: accepted.** The walk that lost its tail call was constant in stack only
   for chains in which no level supplies an instantiation, and only in Release: F# emits no tail calls in Debug,
   which is how the suite runs, so the property was never one a test held. Everything else on the same path already
   uses stack linear in the chain's depth: `allEqual` for any `G<...>` argument, `concretizeType`,
   `SubstitutionContext.rebase`, and the interface walk's own recursion. One frame a level, on a chain as deep as an
   inheritance hierarchy, joins those. The worklist would put a second piece of mutable machinery into published
   code to protect a depth the callers cannot reach, and leaving unary pairs out of the record makes the rule for
   what is recorded depend on a spelling's shape and gives back a quadratic. If a stack limit is ever met here it
   will be met in the callers first, and the remedy belongs to all of them together.
2. **A chain of bare variables is quadratic: accepted, with no second record.** Each read of `!0` follows the alias
   chain to its end, as it does on `main`; this pull request neither adds nor removes that. It is polynomial, it is
   the cost of the commonest real shape (`class A<T> : B<T>`) at depths where it is nothing (587 kB at 64 levels),
   and a cache of resolutions per context and index is a second memo with a second key to get wrong. What must
   change is the claim: the docstring and the pull request body say the work is polynomial in what the chains hold,
   at most quadratic in depth, not linear. Add Codex's shape (`[!0; KeyValuePair<!0, !1>]` at every level, two equal
   chains built separately) as a fixed case beside T1: allocation at depth 256 at most 32 times that at depth 64.
   Quadratic work gives 16 and Codex measured 12.7, so the test pins "no worse than quadratic" with a factor of two
   to spare, and a regression to exponential cannot hide behind T3's constant.
3. **`provedEqualFor`'s scan: accepted, as the implementer argued.** It reads at most the declaring type's arity in
   array elements and allocates nothing; the alternative allocates on every comparison, closed ones included, which
   is the cost the constraint "a closed comparison runs the code it runs today" exists to avoid. Codex's 5 µs is at
   arity 1,024.

Put all three in the pull request body under "accepted costs", with the numbers. The one change is a test, so no
second Codex round is needed for it; open the pull request and enqueue it.

**W2: the probe measured the image, not the walk** (*read, not measured; the check is below*).

The probe compiles C# with Roslyn. Roslyn writes into every type's InterfaceImpl rows *all* of its interfaces, each
as a fully expanded TypeSpec, so `L16<A>` has a row for `I<Pair<Pair<...>>>` whose blob has `2^16` leaves, `L15`
one of `2^15`, and so on. The image doubles with each level. Reading it is the cost the probe found: every row of
every entry is resolved and concretised (about half a million spelled nodes at depth 16), the single chain with no
duplicate comparison pays it in full, a warmed second call pays it again because the rows are read again, and
`resolveImplementedInterface` is a third of it. That is work linear in the metadata, at roughly 8 kB a spelled node.
The host reads the same blobs.

Three things in hand agree with this:

- Codex's sixteen-level chain, built with `MetadataBuilder` with one row per level, closes the same doubly shared
  types (`Pair<X15, X15>` at the bottom) and costs 0.36 MB on `main`. So concretising a deeply shared *type* is cheap;
  it is the *spelling* that is large in the probe.
- The probe's diamond fell from 20.65 GB to 7.97 GB with the fix, and 7.97 GB is about twice the single chain's
  4.23 GB. So the comparison was 12.7 GB of the diamond on `main`, which is the "predates this branch" claim of
  decision 1 confirmed, and what remains is two chains' worth of reading.
- The remaining cost doubles per level exactly as the blobs do.

So there is no row-concretisation defect to file, and no pull request to land before PR 2. No issue is written.

**The check, before relying on that.** In the probe, print `image.Length` and the summed node count of every
InterfaceImpl row's `TypeDefn` at each depth. Both should double per level, and bytes allocated per spelled node
should be flat across depths. If they are, put the three numbers in the comparison pull request's body in place of
"the exact source was not found". **If the image does not grow so, or the cost per node climbs with depth, stop and
report**: then something does repeat work, and this subsection is wrong.

**W2 stays in PR 2, rebuilt.** My specification of it was at fault: it named a shape and not how to build it. W2 is
an image written with `MetadataBuilder`, as Codex's chain was: `L(k)<A> : L(k-1)<Pair<A, A>>` and the same for `R`,
**each interface's rows naming its one direct parent and nothing else**, `L0<A> : I<A>`, `R0<A> : I<A>`,
`C : L16<int>, R16<int>`. Its assertions are unchanged: the host's `GetInterfaces()` entry for entry, and one map at
sixteen levels allocating at most 32 times one at eight. On `main` before the comparison pull request it should be
exponential (two rows close to one handle and their equal substitutions are compared in full); record that run in
the comparison pull request's body. After the comparison merges it should be green on `main` already, and step 3's
"W1 and W2 go green after the rebase" stands for this form. If the rebuilt W2 is still exponential with the fix,
stop and report.

**W1 is not exposed, and is still expected to go green.** It is Codex's image: one row per level, spellings of
constant size, 19 entries. The base read and closed it for 0.36 MB, and this branch's closing path concretises each
row's spelling once, in a context closed once. Its only exponential term was the comparison.

**What this changes in the rules above.**

- Every image that a cost assertion is made over is written with `MetadataBuilder`, with rows chosen by the test.
  Roslyn's rows are fine for comparing answers with the host, and wrong for measuring, wherever an argument is
  wrapped at each level. (`A deep chain of generic interfaces agrees with the host` passes its arguments straight
  on, so its Roslyn image stays linear and it is unaffected.)
- A bound is stated in what the image holds, **spelled nodes included**, never in depth alone. For W1 and W2 the
  spelled nodes are linear in depth, which is why a ratio between two depths is a fair assertion there.

##### After Codex's seventh review

Decided 2026-11-19, after reading the branch at `441c45466`, Codex's seventh review and the plan's account of it.
Nothing was built or run for these decisions.

**In brief.** The pull request opens now. Two test-only changes go in first; no code under `WoofWare.PawPrint.*`
other than tests changes; there is no eighth Codex round. Findings 1 and 2 are filed.

**The stop was right, and the choice it anticipated does not arise.** The rule said a seventh-round finding on valid
metadata would mean the approximate load's checks are in the wrong place. Finding 1 is on valid metadata but is not
of that kind: it is about what one duplicate comparison costs, and no rearrangement of the walk changes it. The
approximate load stays where it is.

**1. Many instantiations of one interface: accepted in this pull request, the constant filed.**

- *What CoreCLR does* (read). `ExpandApproxInterface` (methodtablebuilder.cpp:341-375) scans **every** entry of the
  map for each interface it is about to add and calls `CompareTypeDefsUnderSubstitutions` on each. For an entry of
  another interface that returns at the TypeDef check; for an entry of the same interface it compares the
  arguments. So `n` instantiations of one interface cost `n(n-1)/2` argument comparisons there too: 130,816 for
  `I<A0>` to `I<A511>`. **The quadratic count is faithful.** PawPrint makes exactly the comparisons CoreCLR makes,
  and skips the others on identity as CoreCLR skips them on TypeDef.
- *What differs* is the price of one: about 1.9 kB allocated here to tell two TypeDefs of one image apart, a few
  instructions there. The old walk hid that price behind the closed-handle filter, which a definition cannot have.
- *Options.* (a) A check in the walk for "provably distinct" arguments ahead of the comparison, as Codex suggests.
  It is option C of decision 1 again, narrower: a second statement of part of the comparison rule (which spellings
  cannot be the same type: two TypeDefs of one image yes, two TypeRefs no, a variable never), to be kept in step
  with the first, and it leaves the same price under `ownDispatchMapOf` and method-table layout. Rejected.
  (b) Make the comparison itself cheap: a change to published code that every signature comparison runs through,
  to be led by a profile, and so a pull request of its own. Chosen, as an issue. (c) Index the entries by interface
  so that other interfaces are not scanned: irrelevant to this shape, where every entry is the same interface.
- *Why it does not hold this pull request.* The work is CoreCLR's own in shape; the regression is a constant on a
  type with hundreds of instantiations of one interface (about 1 MB at 32, where `main` is already 10.7 MB at 512
  for other reasons); the guest suites' times did not move; and the remedy is not in this pull request's files.
- *In this pull request:* one test, with the sketch fixture, that pins the growth: one map for a class implementing
  128 distinct instantiations of `I<T>` allocates at most 8 times one for 64. Quadratic work gives 4. Record the
  253 MB and 10.7 MB in the body under "accepted costs", with the issue's number.
- Filed as #2335.

**2. An extends clause spelled `CLASS B` as a bare TypeSpec: filed, with `extends valuetype V<int>`.**

Under round six's rule this is malformed metadata answered rather than refused, which is a finding of the first
kind only if this pull request made it so. It did not: the `| nominal -> nominal, ImmutableArray.Empty` arm of
`MethodTableLayout.baseOfDefinition` is on `main`, and it serves method-table layout, method reference resolution
and constrained calls as well. The fix is to the shape of an extends clause wherever one is read, and is one change
of its own.

*Does it matter before PR 3?* No. `interfaceMapOfDefinition` has no caller until then, and the closed path reaches a
base by `resolveBaseConcreteType`, unchanged from `main`. *Does it matter in PR 3?* Only as far as PawPrint already
hands a guest the `Type` of such a definition, where real .NET fails at the `ldtoken`; the missing refusal is the
same one seen from another side. So it does not block PR 3 either, but PR 3 must not claim that
`interfaceMapOfDefinition` refuses every extends clause CoreCLR refuses, and its docstring should name the issue.
Fixing it before PR 3 is welcome and not required.

Filed as #2336. The parse it asks for is the one #2329 asks for on a row, and each names the other.

**3. The sketch oracle: fix here.** It is this pull request's own test code, the change is small (host types kept
per handle, `Type` values compared, names formatted only to report a mismatch), and an oracle that does exponential
work beside a test asserting that the code under test does not is a trap for whoever next deepens the chain. Check
it by the same means as the code: `sketchOutcome`'s allocation outside the measured call at depth 16 is within a
small multiple of depth 8's.

**No eighth round.** The seventh was the last and stays the last. What follows it is two test-only changes, which
the rule after the comparison pull request's review already said need no round. If, against this decision, any
non-test file is edited before the pull request opens, that edit does get a review, and its findings are sorted by
round six's rule with no further round after it.

**Order.**

1. Finding 3's oracle change and finding 1's growth test, one commit, tests only.
2. File the three issues (the two above and the row issue from the fifth review, if it is not yet filed); put their
   numbers into the plan, the not-an-interface diagnostic's text if it still lacks one, and the tests' docstrings.
   A diagnostic's wording is not a reason for a review.
3. Bring "As built (PR 2)" up to date: replace "Open after Codex's seventh review" with what was decided, and list
   under one heading everything this pull request knowingly leaves, each with its issue: the failure named for a
   malformed row, the extends-clause shapes, the comparison's constant, and the cyclic base chain (#2330).
4. Default suite once more (the changes are tests), then open the pull request and enqueue it.

### As built (PR 3)

Open receivers, closing #2309. The interpreter and tests, and one docstring in the TypeSystem. Nothing is added to
or removed from a published package's public surface except `ReflectedTypeTarget.instantiateTargets` (below), in
the interpreter's package.

**Commits.**

1. *RuntimeTypeInterfaces.ofInstantiation: the signature the tests call, not yet implemented*: a stub that fails
   loudly, so that the tests of the next commit compile and go red for the reason intended.
2. *Tests: GetInterfaces on open receivers*: tests alone, with the red run in the message (below).
3. *GetInterfaces answers open receivers from the definition's interface map*: the change.
4. *Test: converting a definition's map to targets is linear in a shared chain*, added for a mutant; red on commit 3.
5. *instantiateTargets closes an instantiation under its arguments' handles*: the fix for what commit 4 found.
6. *Tests: an open construction over an interface's own variable*, added when a mutant survived.
7. *Plan for #2309: record the third pull request as built*: this section, before Codex's review.
8. *Tests: an open receiver loads its base chain and every row*, from Codex's first review; both new tests failed
   before the next commit.
9. *ofInstantiation loads the receiver's base chain and every row*: the fix.
10. This section, after Codex's first review.

**The function.** `RuntimeTypeInterfaces.ofInstantiation` (internal) takes a generic definition and one target per
variable it declares (none for a type that is its own definition), and answers `interfaceMapOfDefinition`'s entries
converted to targets, in order, duplicates kept (decision 4 (a)). A `Closed` argument stays. `Formal (definition, i)`
is the `i`th argument, and a variable of any other owner fails loudly. A `Spelled` argument is `reflectedTypeTarget`
of the spelling in its own assembly, in an environment of its inner context converted the same way: `Bound` when
every converted argument is closed, `Open` otherwise. Each context the map shares is converted once, keyed on its
array, as `interfaceMapOf` closes each once. A refused map is the function's `Error`.

`ofReceiver` answers:

- a generic definition at its own variables, and an open construction at its arguments, through `ofInstantiation`,
  stopping the run on a refused map as the closed path stops on `interfaceMapOf`'s;
- an array over a variable as a closed array is answered, with `System.Array`'s map (decision 5 (a));
- a closed type through `interfaceMapOf`, as before (decision 2).

The first pull request's temporary stop and what was left of the old definition arm are deleted.

**Canonical form.** An entry's target is `ReflectedTypeTarget.instantiateTargets` of its interface and converted
arguments. Decision 4 asked for this piece to be extracted from `reflectedTypeTarget`'s `GenericInstantiation` arm,
which now calls it. If every argument is closed, which is true of an interface with none, the entry is the closed
type. Otherwise it is `openConstructed`, which collapses a typical instantiation to the definition.

**Where the decisions were silent.**

- **An instantiation is closed under its arguments' handles.** The arm used to close an instantiation whose
  arguments are all closed through `instantiateOpenGenericTypeDefinition`. That function re-spells each argument's
  handle as the `TypeDefn` it names, and concretises the result. For a deeply shared argument (W1's
  `Pair<X15, X15>`) the spelling is exponentially larger than the handles: 2.5 GB to convert W1's sixteen-deep map
  for `C`. Commit 4 found it. `instantiateTargets` now concretises the definition over its own variables, with the
  handles as the type variables, which gives the same handle: 445 kB at depth 16. `instantiateOpenGenericTypeDefinition`
  itself is unchanged and still re-spells. `MakeGenericType` reaches it, among others.
- **What is loaded before the entries are converted.** Decision 4 converts entries. Codex's first review showed two
  malformed images the host refuses which entries alone answer. In one, `C<T> : B<IAbsent>`, the base `B<U>` adds no
  entry. In the other, `C<T>` re-declares its base's `I<valuetype Int32>` as `I<class Int32>`, a row that adds no
  entry. So once the definition's map is built (commit 9), `ofInstantiation` loads the receiver's base chain through
  `resolveBaseRuntimeTypeHandleTarget`, as `interfaceMapOf` loads a closed type's chain. It then converts every row
  the map read, in the order read, as `interfaceMapOf` concretises them, and only then converts the entries. The map
  comes first because it refuses an extends clause naming a variable the type lacks, which loading the chain would
  instead stop on with a `failwith`.
- **A refused definition map stops the run.** It does not raise a guest exception, just as `interfaceMapOf` does not
  on the closed path. `interfaceMapOfDefinition` refuses some malformed InterfaceImpl rows and extends clauses, but not
  every extends clause CoreCLR refuses (#2336). Its docstring now says so. A type that the base chain, a row or an
  entry names and that does not load stops the run inside the conversion, as `reflectedTypeTarget` stops it. It is not
  `ofInstantiation`'s `Error`, which is the map's refusal alone; the docstring says which is which.

**Tests** (default suite), each against the host's `GetInterfaces()` over the same image, order, duplicates and
canonical form included:

- CoreLib: every generic definition (421 on a .NET 10 host, 124 with an interface over a variable, floor 100), three
  arrays over a variable, and `List<>` over `IList<>`'s `T` and `Dictionary<,>` over `IDictionary<,>`'s variables.
- CoreLib: the substitution law, as in "Test design". Each definition is instantiated at all `object`, all `int` and
  alternating `string`/`long`, and, if it has no constraints, at all `Dictionary<,>`'s `K`, at its `V` alternating with
  `K`, and at its own variables reversed. Instantiations the host refuses are left out. That gives 1,569 receivers,
  456 of them open and 169 of those with an interface over a variable; the floors are 800 and 85. About 0.5 s.
- The generated hierarchies of PR 1 now also ask for each generic declaration as its definition, as an open
  construction over `K` alone and alternating with `int`, and its open base class. Three new labels are floored at
  half the fixed sample: definition receivers 210, open constructions 340, an interface whose argument is a definition
  29.
- The shapes C# refuses: each definition, `C<K>`, and `Der<>`'s open base `LBase<T>`.
- Decision 2's tie: every closed receiver of every corpus also checks that `ofInstantiation`, given its closed
  arguments as targets, gives `interfaceMapOf`'s handles in order.
- The canonical forms named outright: `List<>`'s first entry, its non-generic `IEnumerable`, `IX<>`'s
  `IEquatable<IX<>>`, and `Dup<int, int>`'s one handle twice.
- `interfaceMapOfDefinition`'s two refusals that PR 2 left without a test, now reached through an open receiver:
  an unreadable row (a variable out of scope, or the wrong arity), and an extends clause naming a variable the type
  lacks. The host refuses each.
- W1's chain converted to targets, for `C` and for the definition `N<depth>`: sixteen levels may cost at most 32
  times eight. That image is written with `MetadataBuilder`, one row per interface, each spelling of constant size,
  so what it holds is linear in the depth. Measured: `C` 235,472 and 445,456 bytes; `N` 135,208 and 265,080.
- Refusals that move: `GetInterfaceMap` of `CGen<>`'s open interface, asked through `GetInterfaces`, now reaches
  #2303's refusal through public API. The two refusal tests of PR 1's temporary stop are replaced by tests that pin
  the next stop, `GetUtf8NameInternal` of an open construction (#2315). One reaches it through `GetInterfaces()` on
  `List<>`'s `T[]` (eleven entries on the host); the other through `typeof(List<>).GetInterface("IList`1")`.

**Guests.** `sourcesPure/TypeGetInterfacesOpenGeneric.cs` has the plan's nineteen checks and the decisions'
additions: `Action<>`, `OverClosed<>`, a generic method's `List<TM>`, `IC<K>`, and `X<>`'s `IC<X<>>` with its own map.
Check 25 adds `List<>` over `IList<>`'s `T`. Check 16 reads `SCrtp<>`'s entries by definition and argument rather than
through `MakeGenericType`, which would check `ISelfIface<>`'s constraint against an open argument (#2317). The guest
exits 0 on real .NET 10.0.14 and 11.0.0-rc.3. Each of 30 perturbed expectations exits with its own check's number on
both runtimes; a perturbation swaps entries, adds or removes a duplicate, replaces a definition argument by a
variable, or reads a list over another owner's variable. The plan also asked for a `#def` argument to be replaced by
the explicit instantiation over the definition's own variables. That control cannot fail: real .NET's
`MakeGenericType` returns the definition itself for it, which check 18 asserts.
`sourcesImpure/TypeBuilderGenericGetInterfaces.cs` (`DynG<T>`) exits 0 on both runtimes. Before commit 3, both
guests stopped at the definition arm's "names the definition's variables" `TODO`.

**Red** (commit 2, on commit 1). In the default suite, 61 tests were selected and 15 failed. CoreLib definitions had
189 disagreements, ending in the old arm's `TODO`. The instantiations had 1,480 and CoreLib's closed types 530, both
in the cross-check, which reached the stub. The generated hierarchies failed after one test, and the shapes had 22
disagreements. The deep chain and the modified-TypeSpec tests failed in the cross-check. Each new refusal test failed
on the stub, or failed to find the refusal it names. Both guests stopped at the `TODO`.

**Mutants.** Each mutant was applied by an exact replacement and the test project built. Three selections were run:
the default tests (`TestGetInterfacesOrder`, `TestGetInterfaceMethodImplementation`, `TestInterfaceDispatchMap` and
`TestArrayInterfaceMap`, 69 tests); the pure guests `TypeGetInterfaces*`, `ArrayGetInterfaces*`,
`RuntimeTypeGetInterface*` and `TypeGetInterfaceMap*` (15); and the impure `TypeBuilderGenericGetInterfaces`. The file
was restored after each. In the table, "conversion" names `ofInstantiation` and "walk" names
`interfaceMapOfDefinition`.

| mutant | killed by |
|---|---|
| conversion: entries deduplicated by target | canonical forms, `N` cost, generated; guest `TypeGetInterfacesOpenGeneric` (check 12) |
| conversion: an entry's open construction spelled directly (no collapse of a typical instantiation) | CoreLib definitions (`List<>` over `IList<>`'s `T`); guest check 25, by the registry's canonical-form assertion. It survived before commit 6 |
| `instantiateTargets`: `OpenConstructed` instead of `openConstructed` (the plan's `#def` mutant) | canonical forms, CoreLib definitions and instantiations, generated; the open guest |
| conversion: an entry over variables answered over the interface's own variables | seven default tests (CoreLib definitions and instantiations, generated, shapes, canonical forms, both moved refusals); both guests |
| an open construction answered as its definition | CoreLib instantiations, generated, shapes; the open guest |
| conversion: a closed entry of an open receiver left unconcretised | nine default tests; the open guest |
| conversion: entries over variables dropped | eight default tests; both guests |
| an array over a variable refused | the array refusal test, CoreLib definitions |
| an array given no map | CoreLib closed and definitions; `ArrayGetInterfacesByName`, `Count`, `PointerElement`, `Shape` |
| conversion: an interface with no arguments answered as a bare definition | eight default tests; both guests |
| conversion: a variable read at the wrong index | deep chain, CoreLib closed, definitions and instantiations, generated, shapes; the open guest |
| conversion: converted contexts never shared | both cases of commit 4's cost test (`C` 3.6 GB at depth 16); the deep chain's cross-check did not finish in 24 minutes. Commit 4 was added for it |
| walk: base-class entries after the type's own | fifteen default tests; all fifteen pure guests; the impure guest |
| walk: a declared row's parents not expanded | thirteen default tests (shapes, cycles, both cost tests); the impure guest |
| walk: parents after all declared rows (breadth-first) | shapes, the two diamonds; the impure guest |
| walk: parents expanded before the row | sixteen default tests; twelve pure guests; the impure guest |
| walk: duplicate check on interface identity alone | nine default tests; `TypeGetInterfaceMapTargets` and both order guests |
| walk: the parent's map not rebased onto the extends clause | ten default tests; `TypeGetInterfaceMapTargets` and both order guests |
| conversion: an environment never `Bound`, always `Open` | none: equivalent on metadata that loads. With every target closed, the walk gives each variable its closed target, each instantiation the same closed handle through `instantiateTargets`, and each array or pointer the closed shape through `composite`. The two differ only where a function pointer is a generic argument, which CoreCLR refuses to load. `Bound` is kept because it concretises a spelling in one step |
| walk: the CoreLib no-expansion rule removed | none: equivalent over CoreLib as compiled, as decision 7's check states |
| (after commit 9) the receiver's base chain not loaded | the absent-base-argument test |
| (after commit 9) rows that add no entry not converted | the re-declared-row test |

**Codex's first review (on commit 7).** It ran the focused tests and both guests, and found:

- [P2] Two malformed images that the host refuses and an open receiver answered: an absent argument of a base that
  adds no entry, and a re-declared row of the wrong kind. Both are fixed in commits 8 and 9 (above).
- [P3] The two refusal tests for #2315 matched fragments that other open-construction stops share. They now also
  name `GetUtf8NameInternal` (commit 8).
- [P3] A type that does not load during the conversion stops the run rather than returning `Error`. The docstring
  now says so (commit 9). Returning it would mean a `Result` from `reflectedTypeTarget`, whose other callers all stop.
- **Left for a decision, not fixed here.** Each needs a structural choice the decisions do not make:
  - [P2] *Exponential registration of a deeply shared open target.* The QCall turns each target into a guest `Type`
    through `TypeHandleRegistry.getOrAllocate`. That runs `assertWellFormed`, which revisits every occurrence of a
    shared argument, and looks the target up in a `Map` keyed on it, which compares two equal trees structurally.
    Measured over the definition `N<depth>` of W1's chain: assertion alone took 292 kB at depth 8, 4.7 MB at 12 and
    75 MB at 16; registering again once the targets are present took 0.54 MB, 8.4 MB and 134 MB. Each two levels
    multiply the cost by four. A closed receiver is unaffected, its entries being interned handles. Bounding this
    means interning open targets, or memoising both the assertion and the comparison by reference, in the registry
    every open reflected type goes through. That is the representation of `RuntimeTypeHandleTarget`, beyond this
    feature.
  - [P2] *Open constructions are not checked as CoreCLR's loader checks them.* `reflectedTypeTarget`'s structural
    walk, and `instantiateTargets`'s open branch, build an `OpenConstructed` without checking the arity, the
    arguments `Generics::CheckInstantiation` refuses (a pointer, a byref, a function pointer), or the spelled
    `CLASS`/`VALUETYPE` kind of a nested instantiation. A closed instantiation is checked by concretisation. Codex
    showed three malformed definitions that the host refuses and an open receiver answers: `C<T> : I<T*>`,
    `C<T> : I<Pair<T>>` for a two-parameter `Pair`, and `I<valuetype G<T>>` for a class `G`. The walk serves every
    open reflected type: constraints, signatures and extends clauses. Checking there changes what all of them answer.
    `NativeRuntimeTypeHelpers.malformedInstantiationArgument`, the target-level form of the argument check, is
    compiled after `ReflectedTypeTarget`.

**Suites**, run at commit 10 on `a32418d9a` (#2337) after a whole-solution build, the reference assemblies deleted and
the solution built again, each suite its own invocation on a .NET 10 host: default 5,968 (5,929 passed, 39 skipped);
`Guest` 234 passed (717 s); `GuestSolo` 2 passed (324 s); `GuestPure` 1,209 passed (580 s); `GuestImpure` 305 passed
(274 s).

**A flaky assertion found on the way, not this pull request's.** One run of the selection failed `A diamond of two
chains passing Pair<A, A> on agrees with the host, in work linear in its depth` (PR 2's W2). It failed on the sketch
oracle's own naming cost: 17,488 bytes at depth 8 and 230,904 at depth 16, against a bound of eight times. Run alone
straight afterwards, it measured 17,488 and 34,200. Something allocated about 200 kB once on the test's thread during
the oracle's measurement, and a bound of 140 kB cannot absorb that.

**Neighbours, now that this has landed.** #2303 and #2295 can read a definition's interface map from
`ConcreteInterfaceDispatch.interfaceMapOfDefinition`: each entry's interface, its definition-rooted substitution,
`DeclaredOnType` and `ImplementedByParent`. They can read it as targets over any receiver's arguments from
`RuntimeTypeInterfaces.ofInstantiation`. #2315 is the next stop for `GetInterface(name)` on an open receiver and for
an array over a variable, and two refusal tests pin it.
