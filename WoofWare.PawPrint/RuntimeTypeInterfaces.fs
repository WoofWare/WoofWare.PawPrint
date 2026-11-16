namespace WoofWare.PawPrint

open System.Collections.Immutable
open Microsoft.Extensions.Logging

/// The interfaces CoreCLR's `RuntimeTypeHandle_GetInterfaces` QCall (runtimehandles.cpp) answers for
/// a receiver, which is what `Type.GetInterfaces()` returns before managed code appends an SZ array's
/// generic interfaces. Callable outside a guest; the QCall's handler allocates the array.
[<RequireQualifiedAccess>]
module RuntimeTypeInterfaces =

    /// The interface map of a closed type with a TypeDef row, entry for entry: `interfaceMapOf`'s
    /// order is CoreCLR's, and its duplicates are CoreCLR's.
    let private ofClosed
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (operation : string)
        (state : IlMachineState)
        (handle : ConcreteTypeHandle)
        : IlMachineState * RuntimeTypeHandleTarget list
        =
        let state, map =
            InterfaceDispatch.interfaceMapOf loggerFactory baseClassTypes operation state handle

        state, map |> List.map (fun entry -> RuntimeTypeHandleTarget.Closed entry.Interface)

    /// Whether any class on the base chain of `target` declares an InterfaceImpl row, which is when
    /// its interface map is non-empty.
    let rec private baseChainDeclaresInterfaces
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (operation : string)
        (state : IlMachineState)
        (target : RuntimeTypeHandleTarget)
        : IlMachineState * bool
        =
        match IlMachineState.resolveBaseRuntimeTypeHandleTarget loggerFactory baseClassTypes state target with
        | state, None -> state, false
        | state, Some parent ->
            let rows =
                match parent with
                | RuntimeTypeHandleTarget.Closed (ConcreteTypeHandle.Concrete _ as handle) ->
                    match IlMachineState.tryGetConcreteTypeInfo state handle with
                    | Some (_, typeInfo) -> typeInfo.ImplementedInterfaces
                    | None -> failwith $"%s{operation}: base class handle %O{handle} of %O{target} was not registered"
                | RuntimeTypeHandleTarget.OpenConstructed (identity, _)
                | RuntimeTypeHandleTarget.OpenGenericTypeDefinition identity ->
                    match state.LoadedAssembly identity.AssemblyFullName with
                    | Some assembly -> assembly.TypeDefs.[identity.TypeDefinition.Get].ImplementedInterfaces
                    | None ->
                        failwith
                            $"%s{operation}: assembly %s{identity.AssemblyFullName} of the base class of %O{target} is not loaded"
                | other ->
                    failwith $"%s{operation}: the base class of %O{target} resolved to %O{other}, which is not a class"

            if rows.IsEmpty then
                baseChainDeclaresInterfaces loggerFactory baseClassTypes operation state parent
            else
                state, true

    /// A generic definition's interface map, where every InterfaceImpl row is closed and no base class
    /// has interfaces: each row followed by its own interfaces, each closed instantiation once.
    let private ofDefinition
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (operation : string)
        (state : IlMachineState)
        (identity : ResolvedTypeIdentity)
        : IlMachineState * RuntimeTypeHandleTarget list
        =
        let receiver = RuntimeTypeHandleTarget.OpenGenericTypeDefinition identity

        // The base's entries come first in the map, which this arm cannot yet build for a definition.
        match baseChainDeclaresInterfaces loggerFactory baseClassTypes operation state receiver with
        | _, true ->
            failwith
                $"TODO: %s{operation} for the generic definition %O{identity.TypeDefinition.Get} in %s{identity.AssemblyFullName}: a base class has interfaces, whose entries come first in the map; a definition's interface map is #2309"
        | state, false ->

        let assembly =
            state.LoadedAssembly identity.AssemblyFullName
            |> Option.defaultWith (fun () ->
                failwith
                    $"%s{operation}: assembly %s{identity.AssemblyFullName} not loaded for the generic definition %O{identity.TypeDefinition.Get}"
            )

        let typeInfo = assembly.TypeDefs.[identity.TypeDefinition.Get]

        let state, entries =
            ((state, []), typeInfo.ImplementedInterfaces)
            ||> Seq.fold (fun (state, entries) row ->
                let rowAssembly =
                    state.LoadedAssembly row.RelativeToAssembly.FullName
                    |> Option.defaultValue assembly

                let state, rowTypeDefnResult =
                    IlMachineState.resolveTypeMetadataToken
                        loggerFactory
                        baseClassTypes
                        state
                        rowAssembly
                        row.InterfaceHandle

                let rowTypeDefn, rowResolvedAssembly =
                    TokenBindingStop.typeToken rowAssembly row.InterfaceHandle rowTypeDefnResult

                if IlMachineState.containsAnyGenericParameter rowTypeDefn then
                    failwith
                        $"TODO: %s{operation} for the generic definition %O{identity.TypeDefinition.Get} in %s{identity.AssemblyFullName}: the interface row %O{rowTypeDefn} names the definition's variables; a definition's interface map is #2309"

                let state, rowHandle =
                    IlMachineState.concretizeType
                        loggerFactory
                        baseClassTypes
                        state
                        rowResolvedAssembly.DefinitionFullName
                        ImmutableArray.Empty
                        ImmutableArray.Empty
                        rowTypeDefn

                let rowTarget = RuntimeTypeHandleTarget.Closed rowHandle

                // A row already in the map adds nothing, nor do its parents, which came with it.
                if List.contains rowTarget entries then
                    state, entries
                else

                let state, parents = ofClosed loggerFactory baseClassTypes operation state rowHandle

                let entries =
                    (entries @ [ rowTarget ], parents)
                    ||> List.fold (fun entries parent ->
                        if List.contains parent entries then
                            entries
                        else
                            entries @ [ parent ]
                    )

                state, entries
            )

        state, entries

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
        | RuntimeTypeHandleTarget.Closed (ConcreteTypeHandle.Concrete _ as handle) ->
            ofClosed loggerFactory baseClassTypes operation state handle
        // An array's interface map is `System.Array`'s, copied row for row into every array
        // MethodTable (`CreateArrayMethodTable`, array.cpp: "Because of array method table persisting,
        // we need to copy the map"), so `int[]` answers `System.Array`'s six.
        //
        // An SZ array's five generic interfaces (`IList<T>` and its parents) are not in the map:
        // `RuntimeTypeCache.PopulateInterfaces` (RuntimeType.CoreCLR.cs) appends them in managed code,
        // which PawPrint runs from the guest's own CoreLib, unconditionally and without removing
        // duplicates, so answering them here would list them twice.
        | RuntimeTypeHandleTarget.Closed (ConcreteTypeHandle.OneDimArrayZero _)
        | RuntimeTypeHandleTarget.Closed (ConcreteTypeHandle.Array _) ->
            let state, arrayHandle =
                LoadedTypeInfo.typeInfoToTypeDefn'
                    baseClassTypes
                    state.TypeSystem._LoadedAssemblies
                    baseClassTypes.Array
                |> IlMachineState.concretizeType
                    loggerFactory
                    baseClassTypes
                    state
                    baseClassTypes.Corelib.DefinitionFullName
                    ImmutableArray.Empty
                    ImmutableArray.Empty

            ofClosed loggerFactory baseClassTypes operation state arrayHandle
        | RuntimeTypeHandleTarget.OpenGenericTypeDefinition identity ->
            ofDefinition loggerFactory baseClassTypes operation state identity
