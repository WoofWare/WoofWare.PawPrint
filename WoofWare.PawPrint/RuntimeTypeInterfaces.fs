namespace WoofWare.PawPrint

open System.Collections.Generic
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

    /// The interfaces of the generic definition `definition` applied to `arguments`, one target per
    /// variable the definition declares (none for a type that is its own definition), in the order the
    /// QCall returns them: the definition's interface map (`interfaceMapOfDefinition`), each entry
    /// read over `arguments`, in the same order and with the same duplicates. This is CoreCLR's rule
    /// that an instantiation's map is its definition's, entry for entry
    /// (`MethodTableBuilder::LoadExactInterfaceMap`).
    ///
    /// An entry's argument that is one of the definition's variables is the matching argument; a
    /// closed one is that type; one the definition spells is read in the assembly that spells it,
    /// over its own context converted the same way. An entry whose arguments are all closed, as is an
    /// interface with none, is the closed instantiation, and the definition applied to its own
    /// variables is the definition (`ReflectedTypeTarget.instantiateTargets`).
    ///
    /// Once the definition's map is built, the receiver's base chain is loaded, as CoreCLR loads a
    /// type's parent before the type, whether or not the parent adds an entry. Then every
    /// InterfaceImpl row the map read is loaded as it is spelled, in the order read, as
    /// `interfaceMapOf` loads them for a closed type, whether or not the row adds an entry; and then
    /// each entry is converted. The map comes first because it checks each extends clause's shape and
    /// the variables it names, which loading the chain would otherwise stop on.
    ///
    /// The `Error` is the definition map's refusal. That map refuses some malformed InterfaceImpl rows
    /// and extends clauses, but not every extends clause CoreCLR refuses (#2336). A type that the
    /// base chain, a row or an entry names and that does not load stops the run, as
    /// `reflectedTypeTarget` and `resolveBaseRuntimeTypeHandleTarget` stop it, and so does an
    /// instantiation with the wrong number of arguments. But an open entry's other load checks are
    /// not made: a definition whose row has a pointer, byref or function-pointer argument, or nests
    /// an instantiation spelled with the wrong kind, is answered where CoreCLR refuses to load the
    /// definition, while a closed receiver of the same definition is refused (#2340).
    let internal ofInstantiation
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (operation : string)
        (state : IlMachineState)
        (definition : ResolvedTypeIdentity)
        (arguments : ImmutableArray<RuntimeTypeHandleTarget>)
        : IlMachineState * Result<RuntimeTypeHandleTarget list, TypeBindingFailure>
        =
        let typeInfo =
            match state.LoadedAssembly definition.AssemblyFullName with
            | Some assembly -> assembly.TypeDefs.[definition.TypeDefinition.Get]
            | None ->
                failwith
                    $"%s{operation}: assembly %s{definition.AssemblyFullName} of %O{definition.TypeDefinition.Get} is not loaded"

        if typeInfo.Generics.Length <> arguments.Length then
            failwith
                $"%s{operation}: %s{typeInfo.Namespace}.%s{typeInfo.Name} declares %d{typeInfo.Generics.Length} generic parameter(s), but %d{arguments.Length} argument(s) were supplied"

        let typeSystem, map =
            ConcreteInterfaceDispatch.interfaceMapOfDefinition
                loggerFactory
                state.DotnetRuntimeDirs
                baseClassTypes
                operation
                state.TypeSystem
                definition

        let state = state.WithTypeSystem typeSystem

        match map with
        | Error failure -> state, Error failure
        | Ok map ->

        let state, receiver =
            ReflectedTypeTarget.instantiateTargets
                loggerFactory
                baseClassTypes
                operation
                state
                definition
                (List.ofSeq arguments)

        let rec loadBaseChain (state : IlMachineState) (target : RuntimeTypeHandleTarget) : IlMachineState =
            match IlMachineState.resolveBaseRuntimeTypeHandleTarget loggerFactory baseClassTypes state target with
            | state, None -> state
            | state, Some parent -> loadBaseChain state parent

        let state = loadBaseChain state receiver


        let ownerDescription =
            $"an interface-map entry of %s{typeInfo.Namespace}.%s{typeInfo.Name}"

        // Each context the map shares is converted once: a chain of interfaces nests one context
        // inside the next, and converting each occurrence afresh would take time exponential in its
        // length. Keyed on the array itself, which `ImmutableArray` compares by reference.
        let converted =
            Dictionary<ImmutableArray<TypeConcretization.SubstitutionArgument>, ImmutableArray<RuntimeTypeHandleTarget>> ()

        let rec convertArgument
            (state : IlMachineState)
            (argument : TypeConcretization.SubstitutionArgument)
            : IlMachineState * RuntimeTypeHandleTarget
            =
            match argument with
            | TypeConcretization.SubstitutionArgument.Closed handle -> state, RuntimeTypeHandleTarget.Closed handle
            | TypeConcretization.SubstitutionArgument.Formal (owner, index) ->
                // A definition's map mentions only that definition's own variables.
                if owner <> definition || index < 0 || index >= arguments.Length then
                    failwith
                        $"%s{operation}: %s{ownerDescription} mentions variable !%d{index} of %s{owner.AssemblyFullName}/%O{owner.TypeDefinition.Get}, which is not one of the definition's"

                state, arguments.[index]
            | TypeConcretization.SubstitutionArgument.Spelled (assemblyName, spelling, inner) ->
                let state, innerTargets = convertContext state inner

                let assembly =
                    state.LoadedAssembly assemblyName
                    |> Option.defaultWith (fun () ->
                        failwith
                            $"%s{operation}: %s{ownerDescription} is spelled in %s{assemblyName}, which is not loaded"
                    )

                // `Open` even where every target is closed: a `Bound` axis concretises the spelling whole,
                // and concretisation refuses an instantiation whose argument carries a custom modifier,
                // which the walk builds from its parts. A closed piece still comes back closed.
                ReflectedTypeTarget.reflectedTypeTarget
                    loggerFactory
                    baseClassTypes
                    operation
                    ownerDescription
                    assembly
                    {
                        TypeVariables = ReflectedTypeTarget.ReflectionVariableBinding.Open innerTargets
                        MethodVariables = ReflectedTypeTarget.ReflectionVariableBinding.Open ImmutableArray.Empty
                    }
                    state
                    spelling

        and convertContext
            (state : IlMachineState)
            (context : ImmutableArray<TypeConcretization.SubstitutionArgument>)
            : IlMachineState * ImmutableArray<RuntimeTypeHandleTarget>
            =
            match converted.TryGetValue context with
            | true, targets -> state, targets
            | false, _ ->
                let state, targets =
                    ((state, []), context)
                    ||> Seq.fold (fun (state, acc) argument ->
                        let state, target = convertArgument state argument
                        state, target :: acc
                    )

                let targets = targets |> List.rev |> ImmutableArray.CreateRange
                converted.[context] <- targets
                state, targets

        let state =
            (state, map.Rows)
            ||> List.fold (fun state row -> convertArgument state row.Spelling |> fst)

        let state, targets =
            ((state, []), map.Entries)
            ||> List.fold (fun (state, acc) entry ->
                let state, entryArguments = convertContext state entry.Substitution.Arguments

                let state, target =
                    ReflectedTypeTarget.instantiateTargets
                        loggerFactory
                        baseClassTypes
                        operation
                        state
                        entry.Interface
                        (List.ofSeq entryArguments)

                state, target :: acc
            )

        state, Ok (List.rev targets)

    /// `System.Array`'s interface map, which is every array's: `CreateArrayMethodTable` (array.cpp)
    /// copies it row for row into each array MethodTable ("Because of array method table
    /// persisting, we need to copy the map"), whatever the element, so `int[]` and `T[]` alike answer
    /// `System.Array`'s six.
    ///
    /// An SZ array's five generic interfaces (`IList<T>` and its parents) are not in the map:
    /// `RuntimeTypeCache.PopulateInterfaces` (RuntimeType.CoreCLR.cs) appends them in managed code,
    /// which PawPrint runs from the guest's own CoreLib, unconditionally and without removing
    /// duplicates, so answering them here would list them twice.
    let private ofArray
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (operation : string)
        (state : IlMachineState)
        : IlMachineState * RuntimeTypeHandleTarget list
        =
        let state, arrayHandle =
            LoadedTypeInfo.typeInfoToTypeDefn' baseClassTypes state.TypeSystem._LoadedAssemblies baseClassTypes.Array
            |> IlMachineState.concretizeType
                loggerFactory
                baseClassTypes
                state
                baseClassTypes.Corelib.DefinitionFullName
                ImmutableArray.Empty
                ImmutableArray.Empty

        ofClosed loggerFactory baseClassTypes operation state arrayHandle

    /// The interfaces of `receiver`, in the order the QCall returns them. A receiver the managed
    /// wrapper never passes to the QCall (a TypeDesc) fails loudly, as does one whose definition's
    /// map is refused.
    let ofReceiver
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (operation : string)
        (state : IlMachineState)
        (receiver : RuntimeTypeHandleTarget)
        : IlMachineState * RuntimeTypeHandleTarget list
        =
        let ofOpen (definition : ResolvedTypeIdentity) (arguments : ImmutableArray<RuntimeTypeHandleTarget>) =
            let state, result =
                ofInstantiation loggerFactory baseClassTypes operation state definition arguments

            state, BindingStop.bound result

        match receiver with
        | RuntimeTypeHandleTarget.DynamicMethodsClass scopeAssembly ->
            RuntimeTypeHandleTarget.refuseMetadataQuery operation scopeAssembly
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
        | RuntimeTypeHandleTarget.Closed (ConcreteTypeHandle.Concrete _ as handle) ->
            ofClosed loggerFactory baseClassTypes operation state handle
        // An array over a variable (`T[]`, `List<K>[]`, `T[,]`) answers as a closed array does.
        | RuntimeTypeHandleTarget.Closed (ConcreteTypeHandle.OneDimArrayZero _)
        | RuntimeTypeHandleTarget.Closed (ConcreteTypeHandle.Array _)
        | RuntimeTypeHandleTarget.Composite ((CompositeShape.OneDimArrayZero | CompositeShape.Array _), _) ->
            ofArray loggerFactory baseClassTypes operation state
        | RuntimeTypeHandleTarget.OpenGenericTypeDefinition definition ->
            // CoreCLR's typical instantiation: the definition over its own variables.
            let arity =
                match state.LoadedAssembly definition.AssemblyFullName with
                | Some assembly -> assembly.TypeDefs.[definition.TypeDefinition.Get].Generics.Length
                | None ->
                    failwith
                        $"%s{operation}: assembly %s{definition.AssemblyFullName} of the generic definition %O{definition.TypeDefinition.Get} is not loaded"

            Seq.init arity (fun i -> RuntimeTypeHandleTarget.GenericParameter (definition, i))
            |> ImmutableArray.CreateRange
            |> ofOpen definition
        | RuntimeTypeHandleTarget.OpenConstructed (definition, arguments) ->
            ofOpen definition (ImmutableArray.CreateRange arguments)
