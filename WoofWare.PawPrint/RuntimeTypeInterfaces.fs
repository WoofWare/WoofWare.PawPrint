namespace WoofWare.PawPrint

open System.Collections.Immutable
open Microsoft.Extensions.Logging

/// The interfaces CoreCLR's `RuntimeTypeHandle_GetInterfaces` QCall (runtimehandles.cpp) answers for
/// a receiver, which is what `Type.GetInterfaces()` returns before managed code appends an SZ array's
/// generic interfaces. Callable outside a guest; the QCall's handler allocates the array.
[<RequireQualifiedAccess>]
module RuntimeTypeInterfaces =

    /// The interfaces of `receiver`, in the order the QCall returns them. A receiver the managed
    /// wrapper never passes to the QCall (a TypeDesc) fails loudly.
    let ofReceiver
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (operation : string)
        (state : IlMachineState)
        (receiver : RuntimeTypeHandleTarget)
        : IlMachineState * RuntimeTypeHandleTarget list
        =
        match receiver with
        | RuntimeTypeHandleTarget.DynamicMethodsClass scopeAssembly ->
            RuntimeTypeHandleTarget.refuseMetadataQuery operation scopeAssembly
        | RuntimeTypeHandleTarget.OpenConstructed _ as openConstructed ->
            failwith
                $"TODO: open constructed types are not handled at RuntimeTypeInterfaces.fs:%s{__LINE__}; got %O{openConstructed}"
        | RuntimeTypeHandleTarget.Closed (ConcreteTypeHandle.Byref _ as handle)
        | RuntimeTypeHandleTarget.Closed (ConcreteTypeHandle.Pointer _ as handle)
        | RuntimeTypeHandleTarget.Closed (ConcreteTypeHandle.FunctionPointer _ as handle) ->
            failwith
                $"%s{operation}: byref/pointer/function-pointer handle %O{handle} reached the QCall; the managed wrapper should have short-circuited IsTypeDesc to `[]`"
        | RuntimeTypeHandleTarget.Closed (ConcreteTypeHandle.Continuation layout) ->
            ContinuationType.unasked operation layout
        | RuntimeTypeHandleTarget.GenericParameter (declaringType, position) ->
            failwith
                $"%s{operation}: generic parameter #%i{position} of %O{declaringType.TypeDefinition.Get} reached the QCall; the managed wrapper short-circuits IsTypeDesc to `[]` before this point"
        | RuntimeTypeHandleTarget.MethodGenericParameter (declaringType, declaringMethod, position) ->
            failwith
                $"%s{operation}: method generic parameter #%i{position} of method %O{declaringMethod.Get} on %O{declaringType.TypeDefinition.Get} reached the QCall; the managed wrapper short-circuits IsTypeDesc to `[]` before this point"
        | RuntimeTypeHandleTarget.Composite ((CompositeShape.Byref | CompositeShape.Pointer), _)
        | RuntimeTypeHandleTarget.FunctionPointer _ ->
            failwith
                $"%s{operation}: TypeDesc target %O{receiver} reached the QCall; the managed wrapper short-circuits IsTypeDesc to `[]` before this point"
        | RuntimeTypeHandleTarget.Composite ((CompositeShape.OneDimArrayZero | CompositeShape.Array _), _) ->
            RuntimeTypeHandleTarget.refuseComposite operation receiver
        // An array's MethodTable interface map is *inherited verbatim* from
        // `System.Array` — CoreCLR copies it row for row in `CreateArrayMethodTable`
        // ("Because of array method table persisting, we need to copy the map",
        // `src/coreclr/vm/array.cpp:410-424`), so `GetNumInterfaces()` for `int[]` is
        // `System.Array`'s six and this QCall returns exactly those.
        //
        // The five implicit generic interfaces of an SZ array (`IList<T>` and friends)
        // are deliberately *not* added here. They are appended in managed code by
        // `RuntimeTypeCache.PopulateInterfaces` (`RuntimeType.CoreCLR.cs:1043-1055`),
        // which PawPrint runs from the guest's own corelib. Synthesising them here would
        // double-count them — `PopulateInterfaces` appends unconditionally, with no
        // dedup against what we return — and would make our `MethodTable` projection
        // disagree with the map we hand back.
        //
        // So array handles simply fall through to the ordinary walk below: they
        // contribute no interfaces of their own, and `resolveBaseConcreteType` already
        // knows that an array's base type is `System.Array`, whose closure the walk then
        // collects.
        | RuntimeTypeHandleTarget.Closed (ConcreteTypeHandle.Concrete _)
        | RuntimeTypeHandleTarget.Closed (ConcreteTypeHandle.OneDimArrayZero _)
        | RuntimeTypeHandleTarget.Closed (ConcreteTypeHandle.Array _)
        | RuntimeTypeHandleTarget.OpenGenericTypeDefinition _ ->

        // CoreCLR's MethodTable interface map enumerates ALL implemented interfaces:
        // the type's direct ImplementedInterfaces rows, every interface those interfaces
        // transitively extend, and the same closure for every base class up the chain.
        // Mirror that here so e.g. `class D : B` where `B : IDisposable` reports
        // `IDisposable`, and a class implementing `IList<T>` also reports `ICollection<T>`,
        // `IEnumerable<T>`, `IEnumerable`. Concretization always uses the *owning* type's
        // generics: when walking B<int>'s interfaces we resolve under `[int]`, regardless
        // of the derived class's own generic instantiation.
        //
        // For an `OpenGenericTypeDefinition` (CoreCLR's canonical MethodTable
        // `G<__Canon>`), the interface map is also canonical: each row whose
        // metadata references `G`'s generic parameters becomes a `bound shared`
        // MT (e.g. `class G<T> : IList<T>` → slot `IList<__Canon>` →
        // `OpenGenericTypeDefinition IList`). We do not yet have a representation
        // for that shape, so the open-generic walker only handles rows that are
        // fully closed in metadata (no generic parameter references at all);
        // anything else fails loudly.
        let rec collectInterfaces
            (state : IlMachineState, seen : Set<RuntimeTypeHandleTarget>, ordered : RuntimeTypeHandleTarget list)
            (current : RuntimeTypeHandleTarget)
            : IlMachineState * Set<RuntimeTypeHandleTarget> * RuntimeTypeHandleTarget list
            =
            let state, seen, ordered =
                match current with
                // Structural handles carry no interface rows of their own. For arrays
                // that is CoreCLR's own rule (the map is inherited from `System.Array`,
                // reached by the base-type walk below); byrefs, pointers and function
                // pointers are TypeDescs, which have no MethodTable and so no map at
                // all. Either way the walk continues to the parent rather than stopping.
                | RuntimeTypeHandleTarget.DynamicMethodsClass scopeAssembly ->
                    RuntimeTypeHandleTarget.refuseMetadataQuery operation scopeAssembly
                | RuntimeTypeHandleTarget.OpenConstructed _ as openConstructed ->
                    failwith
                        $"TODO: open constructed types are not handled at RuntimeTypeInterfaces.fs:%s{__LINE__}; got %O{openConstructed}"
                | RuntimeTypeHandleTarget.Closed (ConcreteTypeHandle.Continuation layout) ->
                    ContinuationType.unasked operation layout
                | RuntimeTypeHandleTarget.Closed (ConcreteTypeHandle.OneDimArrayZero _)
                | RuntimeTypeHandleTarget.Closed (ConcreteTypeHandle.Array _)
                | RuntimeTypeHandleTarget.Closed (ConcreteTypeHandle.Byref _)
                | RuntimeTypeHandleTarget.Closed (ConcreteTypeHandle.Pointer _)
                | RuntimeTypeHandleTarget.Closed (ConcreteTypeHandle.FunctionPointer _)
                | RuntimeTypeHandleTarget.Composite _
                | RuntimeTypeHandleTarget.FunctionPointer _ -> state, seen, ordered
                | RuntimeTypeHandleTarget.Closed (ConcreteTypeHandle.Concrete _ as currentHandle) ->
                    match IlMachineState.tryGetConcreteTypeInfo state currentHandle with
                    // A `Concrete` handle names a type with a TypeDef row, so its type
                    // info must be registered; a miss is a bug in whoever produced the
                    // handle, not a type that legitimately has no interfaces.
                    | None -> failwith $"%s{operation}: concrete type handle was not registered: %O{currentHandle}"
                    | Some (currentCt, currentTypeInfo) ->
                        let currentAssy =
                            state.LoadedAssembly currentCt.Identity.AssemblyFullName
                            |> Option.defaultWith (fun () ->
                                failwith
                                    $"%s{operation}: owning assembly %s{currentCt.Identity.AssemblyFullName} not loaded"
                            )

                        ((state, seen, ordered), currentTypeInfo.ImplementedInterfaces)
                        ||> Seq.fold (fun (state, seen, ordered) impl ->
                            let implAssy =
                                state.LoadedAssembly impl.RelativeToAssembly.FullName
                                |> Option.defaultValue currentAssy

                            let state, implTypeDefnResult =
                                IlMachineState.resolveTypeMetadataToken
                                    loggerFactory
                                    baseClassTypes
                                    state
                                    implAssy
                                    impl.InterfaceHandle

                            let implTypeDefn, implResolvedAssy =
                                TokenBindingStop.typeToken implAssy impl.InterfaceHandle implTypeDefnResult

                            let state, implHandle =
                                IlMachineState.concretizeType
                                    loggerFactory
                                    baseClassTypes
                                    state
                                    implResolvedAssy.DefinitionFullName
                                    currentCt.Generics
                                    ImmutableArray.Empty
                                    implTypeDefn

                            let implTarget = RuntimeTypeHandleTarget.Closed implHandle

                            if Set.contains implTarget seen then
                                state, seen, ordered
                            else
                                let seen = Set.add implTarget seen
                                let ordered = implTarget :: ordered
                                // Recurse into the interface's own transitive interface set.
                                collectInterfaces (state, seen, ordered) implTarget
                        )
                | RuntimeTypeHandleTarget.OpenGenericTypeDefinition identity ->
                    let currentAssy =
                        state.LoadedAssembly identity.AssemblyFullName
                        |> Option.defaultWith (fun () ->
                            failwith
                                $"%s{operation}: assembly %s{identity.AssemblyFullName} not loaded for open generic typedef %O{identity.TypeDefinition.Get}"
                        )

                    let currentTypeInfo = currentAssy.TypeDefs.[identity.TypeDefinition.Get]

                    ((state, seen, ordered), currentTypeInfo.ImplementedInterfaces)
                    ||> Seq.fold (fun (state, seen, ordered) impl ->
                        let implAssy =
                            state.LoadedAssembly impl.RelativeToAssembly.FullName
                            |> Option.defaultValue currentAssy

                        let state, implTypeDefnResult =
                            IlMachineState.resolveTypeMetadataToken
                                loggerFactory
                                baseClassTypes
                                state
                                implAssy
                                impl.InterfaceHandle

                        let implTypeDefn, implResolvedAssy =
                            TokenBindingStop.typeToken implAssy impl.InterfaceHandle implTypeDefnResult

                        if IlMachineState.containsAnyGenericParameter implTypeDefn then
                            failwith
                                $"TODO: %s{operation} for open generic typedef %O{identity.TypeDefinition.Get} in %s{identity.AssemblyFullName}: interface row resolves to %O{implTypeDefn}, which references generic parameters (bound-shared interface MT); only fully-closed interface rows are supported today"
                        else
                            let state, implHandle =
                                IlMachineState.concretizeType
                                    loggerFactory
                                    baseClassTypes
                                    state
                                    implResolvedAssy.DefinitionFullName
                                    ImmutableArray.Empty
                                    ImmutableArray.Empty
                                    implTypeDefn

                            let implTarget = RuntimeTypeHandleTarget.Closed implHandle

                            if Set.contains implTarget seen then
                                state, seen, ordered
                            else
                                let seen = Set.add implTarget seen
                                let ordered = implTarget :: ordered
                                collectInterfaces (state, seen, ordered) implTarget
                    )
                | RuntimeTypeHandleTarget.GenericParameter _
                | RuntimeTypeHandleTarget.MethodGenericParameter _ ->
                    // The top-level dispatch above rejects these; the only way one
                    // could reach the recursive walker is if a base-walk produced
                    // a generic-parameter target, but `resolveBaseRuntimeTypeHandleTarget`
                    // refuses for those shapes. Fail loudly if we ever get here.
                    failwith
                        $"%s{operation}: generic-parameter target %O{current} reached collectInterfaces; TypeDescs have no interface map"

            // Walk up the base type chain.
            let state, baseTarget =
                IlMachineState.resolveBaseRuntimeTypeHandleTarget loggerFactory baseClassTypes state current

            match baseTarget with
            | None -> state, seen, ordered
            | Some baseTarget -> collectInterfaces (state, seen, ordered) baseTarget

        let state, _, interfaceTargetsReversed =
            collectInterfaces (state, Set.empty, []) receiver

        state, List.rev interfaceTargetsReversed
