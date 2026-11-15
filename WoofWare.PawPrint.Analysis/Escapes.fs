namespace WoofWare.PawPrint.Analysis

open System.Reflection.Metadata
open WoofWare.PawPrint

/// A method, identified across every assembly an analysis has loaded: the definition identity of
/// the assembly that declares it, its MethodDef row there, and which of the row's `MethodDesc`s it
/// is. A row returning a promise type has two on .NET 11: the ordinary variant, which its token
/// binds, and the async variant, which an await binds (`RuntimeAsyncMethod.asyncVariantOf`).
type MethodKey =
    {
        AssemblyFullName : string
        Method : ComparableMethodDefinitionHandle
        Variant : MethodVariant
    }

    override this.ToString () : string =
        match this.Variant with
        | MethodVariant.Ordinary -> $"%s{this.AssemblyFullName}/%O{this.Method.Get}"
        | MethodVariant.AsyncVariant -> $"%s{this.AssemblyFullName}/%O{this.Method.Get} (async variant)"

[<RequireQualifiedAccess>]
module MethodKey =
    /// The ordinary variant of `method`, a MethodDef row of `assembly`: the method its token binds.
    let make (assembly : DumpedAssembly) (method : MethodDefinitionHandle) : MethodKey =
        {
            AssemblyFullName = assembly.DefinitionFullName
            Method = ComparableMethodDefinitionHandle.Make method
            Variant = MethodVariant.ordinary
        }

    /// The method `method` is, which `assembly` declares on a MethodDef row: its ordinary variant, or
    /// the async variant `RuntimeAsyncMethod.asyncVariantOf` made of it.
    let ofMethod
        (assembly : DumpedAssembly)
        (method : MethodInfo<'typeGenerics, 'methodGenerics, 'methodVars>)
        : MethodKey
        =
        match method with
        | MethodInfo.Metadata (_, facts, variant) ->
            if method.DeclaringAssemblyFullName <> assembly.DefinitionFullName then
                invalidArg
                    (nameof assembly)
                    $"%O{method} is declared by %s{method.DeclaringAssemblyFullName}, not %s{assembly.DefinitionFullName}"

            {
                AssemblyFullName = assembly.DefinitionFullName
                Method = ComparableMethodDefinitionHandle.Make facts.Handle
                Variant = variant
            }
        | MethodInfo.Synthesised _ -> invalidArg (nameof method) $"%O{method} is synthesised, and has no MethodDef row"

/// The type of an exception that may escape.
///
/// IL may throw an object that is not an exception. Such an object is named as itself here; a
/// <c>catch</c> or <c>filter</c> clause sees it as <c>RuntimeWrappedException</c> instead if its
/// method's assembly wraps non-exception throws (<c>RuntimeCompatibility.wrapsNonExceptionThrows</c>).
[<RequireQualifiedAccess>]
type ThrownType =
    /// An exception of exactly this type: one constructed with `newobj` and thrown, or raised by the
    /// runtime.
    | Exactly of ResolvedTypeIdentity
    /// An exception of this type or of any type derived from it: what is known of a value thrown
    /// when only its static type is, such as the result of a helper that returns the exception.
    | SubtypeOf of ResolvedTypeIdentity

/// What may escape a method.
///
/// <c>Types</c> are the exception types the analysis can name. <c>Unknown</c> says that somewhere
/// on a path the analysis could not see through (a virtual call, a native method, a <c>rethrow</c>),
/// so anything at all may escape besides; with it false, <c>Types</c> is a complete answer, provided
/// every assumption in <c>Assumes</c> holds. <c>Assumes</c> lists every assumption whose contract
/// stands in for a body that something escaping could come from. It may also list one that a
/// handler in a method this one calls would have made unnecessary: a callee's answer records that
/// it relied on an assumption, not which types would escape it if the assumption failed.
type Escapes =
    {
        Types : Set<ThrownType>
        Unknown : bool
        Assumes : Set<Assumption>
    }

[<RequireQualifiedAccess>]
module Escapes =
    /// Nothing escapes.
    let none : Escapes =
        {
            Types = Set.empty
            Unknown = false
            Assumes = Set.empty
        }

    /// Everything either may let escape.
    let union (a : Escapes) (b : Escapes) : Escapes =
        {
            Types = Set.union a.Types b.Types
            Unknown = a.Unknown || b.Unknown
            Assumes = Set.union a.Assumes b.Assumes
        }
