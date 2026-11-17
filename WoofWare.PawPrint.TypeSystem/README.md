# WoofWare.PawPrint.TypeSystem

The CLI type system as CoreCLR lays it out.

Consider this to be unstable for now; it's pre-1.0 for a reason.

`WoofWare.PawPrint.TypeSystem` is there to define what the runtime builds when it gets a definition from `WoofWare.PawPrint.Loader`.
For example, it handles the concrete instantiation of generics, and the layout of method tables.

It answers four kinds of question:

* the precise concrete type or method denoted by a generic type or method once it's been instantiated
* what a type's method table holds: which slot each method owns, what each slot holds once
  MethodImpls are applied, and which slot implements each interface method
* which method gets run by a given call, including the dispatch of `constrained.` calls and virtual/interface methods,
  default interface bodies, and variance
* what the runtime and the JIT decide about concrete types: whether one can be cast to another,
  whether an instantiation satisfies its type parameters' constraints, whether a method runs as
  shared generic code, whether storage holds GC references.

The motivating consumer, besides PawPrint's interpreter, is an analyser that answers questions about
a method without running it.
