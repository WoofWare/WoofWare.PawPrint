namespace WoofWare.PawPrint

open Microsoft.Extensions.Logging

/// The method `Type.GetInterfaceMap` names as the target of one interface method, as CoreCLR's
/// `RuntimeTypeHandle_GetInterfaceMethodImplementation` (runtimehandles.cpp) answers it.
[<RequireQualifiedAccess>]
type InterfaceMapTarget =
    /// The method the map names, and the type that declares it at the instantiation the search found it
    /// on: a type on the receiver's class chain, or an interface instantiation. It may be abstract: an
    /// abstract class's method, or a static method's reabstraction.
    | Method of
        definition : WoofWare.PawPrint.MethodInfo<GenericParamFromMetadata, GenericParamFromMetadata, TypeDefn> *
        declaringType : ConcreteTypeHandle
    /// The row's target is null.
    | NoTarget
    /// `GetInterfaceMap` throws `AmbiguousImplementationException`: these default bodies are equally
    /// specific. Only a static method gives this.
    | Ambiguous of
        candidates : WoofWare.PawPrint.MethodInfo<GenericParamFromMetadata, GenericParamFromMetadata, TypeDefn> list

/// Which method `Type.GetInterfaceMap` names for an interface method on a receiver. CoreCLR answers
/// with the walk a call takes, `MethodTable::FindDispatchSlotForInterfaceMD` for an instance method and
/// `MethodTable::ResolveVirtualStaticMethod` for a static one, and differs from a call only in what the
/// walk does at a conflict, and in what the caller makes of the answer: an instance method's
/// reabstraction is no target, while a static method's is the target, and the map reads a dynamic
/// module's MethodImpl rows afresh, where a call's answer can depend on when it was resolved.
///
/// Whatever loads an assembly or registers a concrete type on the way returns the state it leaves
/// behind; `dotnetRuntimeDirs` is where the loader looks for an assembly not yet loaded.
[<RequireQualifiedAccess>]
module InterfaceMethodImplementation =

    let private operation = "interface map"

    /// The body a read of the rows now finds.
    let private now
        (body : DefaultInterfaceImplementation.FoundBody)
        : WoofWare.PawPrint.MethodInfo<GenericParamFromMetadata, GenericParamFromMetadata, TypeDefn>
        =
        match body with
        | DefaultInterfaceImplementation.FoundBody.Certain body -> body
        | DefaultInterfaceImplementation.FoundBody.OrderDependent body -> body.Now

    /// The target `GetInterfaceMap` names for `definition`, a method of the interface instantiation
    /// `iface`, on `receiver`, a type that implements `iface` or casts to it by variance.
    ///
    /// `definition` is the method as its MethodDef row declares it, generic or not: no method
    /// arguments are given, and a generic method's target is a definition too.
    ///
    /// Fails loudly, naming the condition, where `iface` is not a registered interface, where
    /// `definition` is not declared by `iface`'s definition, has no MethodDef row or is an async
    /// variant, and where `receiver` is an SZ array and `iface` is generic, which `GetInterfaceMap`
    /// refuses before it asks.
    ///
    /// A type the search reaches that does not bind is the failure, returned with what loaded before
    /// it.
    let resolve
        (loggerFactory : ILoggerFactory)
        (dotnetRuntimeDirs : string seq)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (receiver : ConcreteTypeHandle)
        (iface : ConcreteTypeHandle)
        (definition : WoofWare.PawPrint.MethodInfo<GenericParamFromMetadata, GenericParamFromMetadata, TypeDefn>)
        (state : TypeSystemState)
        : TypeSystemState * Result<InterfaceMapTarget, TypeBindingFailure>
        =
        let describe = $"%s{MethodOwner.describe definition.Owner}::%s{definition.Name}"

        let ifaceType =
            match TypeSystemState.tryGetConcreteTypeInfo state iface with
            | Some (ifaceType, ifaceInfo) when ifaceInfo.IsInterface -> ifaceType
            | _ -> failwith $"%s{operation} of %s{describe}: %O{iface} is not a registered interface"

        match definition.TryVariant with
        | None -> failwith $"%s{operation}: %s{describe} is synthesised, so it has no MethodDef row"
        | Some MethodVariant.AsyncVariant ->
            failwith $"%s{operation}: %s{describe} is an async variant, which RuntimeType.GetInterfaceMap never passes"
        | Some MethodVariant.Ordinary ->

        if definition.RequiredDeclaringType.Identity <> ifaceType.Identity then
            failwith $"%s{operation}: %s{describe} is not declared by the definition of %O{iface}"

        match receiver with
        | ConcreteTypeHandle.OneDimArrayZero _ when not ifaceType.Generics.IsEmpty ->
            failwith
                $"%s{operation} of %s{describe} on the SZ array %O{receiver}: RuntimeType.GetInterfaceMap throws Argument_ArrayGetInterfaceMap for a generic interface before it asks"
        | _ ->

        if definition.IsStatic then
            if (TypeSystemState.tryGetConcreteTypeInfo state receiver).IsNone then
                failwith
                    $"%s{operation} of the static %s{describe} on %O{receiver}, which has no TypeDef row; no interface such a type implements declares a static virtual"

            // `ResolveVirtualStaticMethod` skips its search where the receiver's interface map holds the
            // canonical form of the interface, which needs a receiver that is itself a canonical
            // instantiation. No reflection handle names one, and PawPrint has no canonical forms, so that
            // is not modelled.
            match
                StaticVirtualDispatch.search
                    loggerFactory
                    dotnetRuntimeDirs
                    baseClassTypes
                    InterfaceMethodCaller.InterfaceMap
                    receiver
                    iface
                    definition
                    state
            with
            | state, Error failure -> state, Error failure
            // The map reads a dynamic module's rows afresh, so an order-dependent body is the one a read
            // now finds.
            | state, Ok (InterfaceMethodSearch.OnChain (level, body)) ->
                state, Ok (InterfaceMapTarget.Method (now body, level))
            // A reabstraction included: nothing checks whether the default body found is abstract.
            | state, Ok (InterfaceMethodSearch.DefaultBody candidate) ->
                state, Ok (InterfaceMapTarget.Method (now candidate.Body, candidate.Interface))
            | state, Ok (InterfaceMethodSearch.Conflict candidates) ->
                state, Ok (InterfaceMapTarget.Ambiguous (candidates |> List.map (fun candidate -> now candidate.Body)))
            | state, Ok (InterfaceMethodSearch.OwnBody (declaringType, body)) ->
                state, Ok (InterfaceMapTarget.Method (body, declaringType))
            | state, Ok InterfaceMethodSearch.Nothing -> state, Ok InterfaceMapTarget.NoTarget
        else

        match
            ConcreteVirtualDispatch.searchInstanceInterfaceMethod
                loggerFactory
                dotnetRuntimeDirs
                baseClassTypes
                InterfaceMethodCaller.InterfaceMap
                receiver
                true
                iface
                definition
                state
        with
        | state, Error failure -> state, Error failure
        // What the receiver's slot holds, abstract or not.
        | state, Ok (InterfaceMethodSearch.OnChain (level, body)) ->
            state, Ok (InterfaceMapTarget.Method (now body, level))
        | state, Ok (InterfaceMethodSearch.DefaultBody candidate) ->
            let body = DefaultInterfaceImplementation.instanceCandidateBody candidate

            match body.Body with
            // A reabstraction: `FindDispatchImpl` with `throwOnConflict` false treats it as nothing found.
            | MethodBody.Abstract -> state, Ok InterfaceMapTarget.NoTarget
            | MethodBody.Il _
            | MethodBody.AsyncIl _
            | MethodBody.MissingIl
            | MethodBody.InternalCall
            | MethodBody.PInvoke
            | MethodBody.RuntimeProvided _ -> state, Ok (InterfaceMapTarget.Method (body, candidate.Interface))
        | _, Ok (InterfaceMethodSearch.Conflict _) ->
            failwith
                $"BUG: %s{operation} of %s{describe}: the instance search for the interface map reported a conflict, which it resolves or answers as nothing found"
        | _, Ok (InterfaceMethodSearch.OwnBody _) ->
            failwith $"BUG: %s{operation} of %s{describe}: an instance method's search found a static method's own body"
        | state, Ok InterfaceMethodSearch.Nothing -> state, Ok InterfaceMapTarget.NoTarget
