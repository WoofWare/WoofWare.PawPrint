# `Type.GetInterfaces` on a generic definition or an open construction (issue #2309)

Status: planned; awaiting decisions. The study below was measured 2026-11-16 on `main` at `c50428b33` (#2307), with a
throwaway handler described under "PawPrint with a throwaway", since reverted.

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
