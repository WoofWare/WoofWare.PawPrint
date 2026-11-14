namespace WoofWare.PawPrint

open System.Reflection

/// Whether a type definition is visible outside its assembly: CoreCLR's `IsTypeDefExternallyVisible`
/// (vm/siginfo.cpp:3189), the TypeDef half of `Type.IsVisible` of a `RuntimeType`. This is not an
/// access check: it reads neither `InternalsVisibleTo` nor `IgnoresAccessChecksTo`, and a family-style
/// nested type is invisible here though reflection calls it accessible from a derived type.
[<RequireQualifiedAccess>]
module TypeVisibility =

    /// <summary>
    /// The rule over a nesting chain's visibilities (each masked by <c>TypeAttributes.VisibilityMask</c>
    /// here), innermost first: true exactly when every level but the last is <c>NestedPublic</c> and the
    /// last is <c>Public</c>. Total over all eight visibility values and every chain; an empty chain
    /// names no type, and is false.
    /// </summary>
    let isChainExternallyVisible (chain : TypeAttributes list) : bool =
        // CoreCLR returns true at a `tdPublic` innermost level and walks out through `tdNestedPublic`
        // enclosers to the first that is not, asking whether it is `tdPublic`. On a chain a reader
        // admits (nested levels inside, one top-level level last) that is this sentence.
        let rec walk (levels : TypeAttributes list) : bool =
            match levels with
            | [] -> false
            | [ last ] -> last &&& TypeAttributes.VisibilityMask = TypeAttributes.Public
            | level :: outer ->
                level &&& TypeAttributes.VisibilityMask = TypeAttributes.NestedPublic
                && walk outer

        walk chain

    /// <summary>
    /// The <c>TypeAttributes</c> of <paramref name="typeInfo"/> and of each type enclosing it, innermost
    /// first, gathered through each level's <c>DeclaringType</c> (the NestedClass row) within
    /// <paramref name="assembly"/>, which an encloser always shares with its nested type.
    /// </summary>
    /// <remarks>
    /// Fails, naming the type, where an encloser is absent from the assembly's <c>TypeDefs</c>, or where the
    /// chain grows longer than the assembly has TypeDefs (a cycle among NestedClass rows): an image CoreCLR
    /// would have refused to load.
    /// </remarks>
    let nestingChain
        (assembly : DumpedAssembly)
        (typeInfo : TypeInfo<GenericParamFromMetadata, TypeDefn>)
        : TypeAttributes list
        =
        let rec gather
            (current : TypeInfo<GenericParamFromMetadata, TypeDefn>)
            (accumulated : TypeAttributes list)
            (length : int)
            : TypeAttributes list
            =
            if length > assembly.TypeDefs.Count then
                failwith
                    $"TypeVisibility.nestingChain: the enclosing types of %s{typeInfo.Namespace}.%s{typeInfo.Name} in %s{assembly.DefinitionFullName} form a cycle among its NestedClass rows"

            let accumulated = current.TypeAttributes :: accumulated

            if not current.IsNested then
                List.rev accumulated
            else
                match assembly.TypeDefs.TryGetValue current.DeclaringType with
                | true, encloser -> gather encloser accumulated (length + 1)
                | false, _ ->
                    failwith
                        $"TypeVisibility.nestingChain: %s{current.Name}, enclosing %s{typeInfo.Namespace}.%s{typeInfo.Name} or itself, names an encloser that is not a TypeDef of %s{assembly.DefinitionFullName}"

        gather typeInfo [] 1

    /// <summary>
    /// <c>isChainExternallyVisible</c> of <paramref name="typeInfo"/>'s nesting chain in
    /// <paramref name="assembly"/>.
    /// </summary>
    let isTypeDefExternallyVisible
        (assembly : DumpedAssembly)
        (typeInfo : TypeInfo<GenericParamFromMetadata, TypeDefn>)
        : bool
        =
        isChainExternallyVisible (nestingChain assembly typeInfo)
