# `Type.GetInterfaces` on a generic definition or an open construction (issue #2309)

Status: decided (see "Decisions"); the first two of five pull requests are built: the closed order (#1318, "As built
(PR 1)") and the definition-level interface map ("As built (PR 2)"); the other three are not started. The study below was measured 2026-11-16 on `main` at `c50428b33` (#2307),
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
  reading any later row it refuses a row that names no nominal type (`!!0` as the whole spelling, reported as
  concretisation reports it), loads a TypeSpec row's interface's base chain (`BaseTypeAbsent` and the like), and
  refuses a generic interface named with no instantiation by any token (`UninstantiatedDefinition`), a spelling with
  the wrong kind (`KindMismatch`), and a type that is not an interface (`Unmodelled`, for
  `IDS_CLASSLOAD_NOTINTERFACE`). It also refuses an extends clause naming a variable the type does not have, before
  re-reading the parent's map through it. A row whose arguments it cannot read in the definition's vocabulary (a
  variable its context does not supply, or the wrong number of arguments) cannot be compared with the entries; the
  walk still walks that interface's own map, as the approximate load expands its parents, then records the row as
  unreadable, adds no entry, and reads on.
  `interfaceMapOfDefinition` refuses a map holding such a row with the first one's failure; `interfaceMapOf` loads
  every row in order, unreadable ones included, so an earlier row's absent argument is reported first and an unreadable
  row's own arguments are loaded before its arity is refused, as the old walk and the host report them. The first
  version of this pull request refused all of these in the walk, or none, which threw on five malformed images,
  answered one, and reported the wrong failure on seven; Codex's three reviews found them, and each is now a test
  against the host, except the extends clause, which the closed path refuses first (in its base chain) and which only
  `interfaceMapOfDefinition`, not yet called, can reach.
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
  intend; until then its refusal of an unreadable row is exercised by no test, and that pull request's tests should
  cover it.

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
put back to value types, commit 2's dump is identical to the base in all five files. After the fixes of commits 6, 10 and 13,
the dump (with the deep chain's two receivers added in a sixth file) is identical to commit 2's in all five files it
shares with it, and so is the probe below; the sixth is identical across commits 6, 10 and 13.

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
host. The tests named under commits 5, 7, 9 and 12 now pin each against the host.

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
passed; `GuestSolo` 2 passed; `GuestPure` 1,208 passed; `GuestImpure` 304 passed.

**Time of the guest categories**, on the same machine, one category at a time, in seconds: on the base (#2322's tip),
and at commits 3, 6, 10 and 13.

| category | base | commit 3 | commit 6 | commit 10 | commit 13 |
|---|---|---|---|---|---|
| `Guest` | 1,092 | 1,179 | 1,698 | 1,395 | 1,175 |
| `GuestSolo` | 545 | 747 | 1,193 | 637 | 648 |
| `GuestPure` | 910 | 717 | 796 | 842 | 715 |
| `GuestImpure` | 883 | 289 | 408 | 396 | 687 |

The machine was shared with other runs, and the differences go both ways by more than any change in this branch could
explain (`GuestImpure` took 505 to 560 s in PR 1's runs; `GuestSolo`, two tests, ranged from 545 to 1,193 s here), so
these runs show no slowdown that stands out from that noise, and nothing asks for memoising the definition map. A
quieter machine would be needed to see a difference of a few per cent.
