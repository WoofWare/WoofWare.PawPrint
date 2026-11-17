# WoofWare.PawPrint.Analysis

Static analysis of a .NET method.

This is not part of the WoofWare.PawPrint interpreter, but is intended to become a static analysis framework for use in e.g. an IDE.

Currently undocumented, because this package is wildly in flux at the moment.

## Goals

(These goals are extremely not yet met.)

* State precisely which exceptions can escape a method, transitively, including exceptions raised within the BCL and the .NET runtime.
* State precisely what I/O or nondeterminism is present in a method (e.g. infer purity/referential transparency).

## Progress

`EscapeAnalysis.escapes` reads a method's IL and all the transitive calls it makes through all other assemblies, and describes what the method can throw and any reasons that the analysis is incomplete (e.g. a virtual call or a native method).
This includes exceptions which are a result of the semantics of IL (which we read from `WoofWare.PawPrint.Semantics`'s `OpcodeFaults`, which is shared by WoofWare.PawPrint - e.g. `DivideByZeroException` on a division operation), as well as explicit requests to `throw` from code.

Usually, we choose an over-approximation: a sufficiently smart static analysis may be able to determine, for example, that a dividend is never `0` and therefore that a method cannot throw `DivideByZeroException`, but we don't promise to be able to do that.
We *do* model resource exhaustion, so most methods can raise `StackOverflowException`; but the actual API and contract here is in flux because I haven't yet decided what is ergonomic.

It's not strictly an over-approximation, though: we implicitly assume that IL is well-typed, and shenanigans with `Unsafe.As` may be able to fool the runtime into throwing more exceptions than we were able to identify.
We also generally assume that the BCL's native code is the same as what PawPrint models, and that the BCL is all present (otherwise your program's `ResolvingUnmanagedDll` handlers would run, and that's arbitrary user code).

## User-provided assumptions

There is an [`Assumption`](./Assumption.fs) mechanism which lets us eliminate classes of exception.
If we don't make any assumptions, then almost all code can throw almost any exception.
For example, if a type is defined in a corrupted assembly, then any IL instruction can throw if it causes that type to load.
This is extremely noisy in practice, so we provide a whole load of "the runtime is operating normally" and "developers aren't committing horrible crimes" assumptions you can explicitly make to trim the analysis.

Some example assumptions you can make:

* Every type will be successfully located without running `AssemblyResolve` handlers
* No exception classes ever raise during `Source` and `StackTrace` access
* The BCL's own type initialisers run as we expect them to (e.g. you haven't swapped them out for your own System.Private.CoreLib DLL)
* The JIT is active with at least the tier-0 optimisations (this lets us optimise away a whole load of otherwise-throwing instructions)
* Every implementer of `IDynamicInterfaceCastable` has conformed to the documentation (e.g. `IsInterfaceImplemented` does not throw anything except `InvalidCastException`)
* Every member access obeys the visibility rules that a C# or F# compiler would enforce, so we don't have to track `MethodAccessException` (e.g. no IL tries to access a private member of a non-friend class)
* No thread ever aborts (otherwise almost every instruction could raise `ThreadAbortException`)

## Sources of the answer "unknown"

Almost every analysis has some blind spots - e.g. we can't magically see what native code does - so `EscapeAnalysis.unknownSources` tells you why an analysis contains holes.

