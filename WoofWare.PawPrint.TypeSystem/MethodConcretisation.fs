namespace WoofWare.PawPrint

open System.Collections.Immutable
open System.Reflection.Metadata
open Microsoft.Extensions.Logging

/// A method instantiated for execution: its declaring type and its own generic parameters bound to
/// concrete types, and its signature and locals concretised against them. Whatever loads an
/// assembly or registers a concrete type on the way returns the state it leaves behind;
/// `dotnetRuntimeDirs` is where the loader looks for an assembly not yet loaded.
[<RequireQualifiedAccess>]
module MethodConcretisation =

    /// Whether CoreCLR creates the instantiation `methodGenerics` of `method`, declared on
    /// `declaringType`, as far as its type parameters' constraints decide it
    /// (`InstantiatedMethodDesc::NewInstantiatedMethodDesc`, genmeth.cpp, which checks them once the
    /// instantiation is well formed and before the method's signature is read). A violation is the
    /// refusal `TypeLoadRefusal.MethodConstraintsViolated`; an argument whose verdict
    /// `ConstraintSatisfaction` leaves undecided is a failure PawPrint does not model.
    let private methodConstraints
        (loggerFactory : ILoggerFactory)
        (dotnetRuntimeDirs : string seq)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (method : WoofWare.PawPrint.MethodInfo<'ty, GenericParamFromMetadata, TypeDefn>)
        (declaringType : ConcreteTypeHandle)
        (methodGenerics : ImmutableArray<ConcreteTypeHandle>)
        (state : TypeSystemState)
        : TypeSystemState * Result<unit, TypeBindingFailure>
        =
        if method.Generics.IsEmpty then
            state, Ok ()
        else

        // CoreCLR exempts an instantiation with an argument in its canonical form, which only code
        // shared between instantiations names. PawPrint runs every instantiation exactly, and none
        // of its arguments is canonical; CoreCLR checks each exact instantiation too, as it creates
        // it, which in shared code is when the instruction naming it first runs.
        let declaring =
            match AllConcreteTypes.lookup declaringType state.ConcreteTypes with
            | Some declaring -> declaring
            | None ->
                failwith
                    $"BUG: the declaring type %O{declaringType} of %s{method.Name}, which concretisation returned, is not registered"

        match
            ConstraintSatisfaction.satisfiesClosed
                loggerFactory
                dotnetRuntimeDirs
                baseClassTypes
                state
                (MethodOwner.requireDeclaringType "checking a method instantiation's constraints" method.Owner)
                    .AssemblyFullName
                declaring.Generics
                methodGenerics
                method.Generics
                (List.ofSeq methodGenerics)
        with
        | state, Error failure -> state, Error failure
        | state, Ok ConstraintVerdict.Satisfied -> state, Ok ()
        | state, Ok (ConstraintVerdict.Violated violation) ->
            state,
            Error (
                TypeBindingFailure.Refused (
                    TypeLoadRefusal.MethodConstraintsViolated (declaring.Identity, method.Name, violation.Parameter)
                )
            )
        | state, Ok (ConstraintVerdict.Undecided (parameter, required)) ->
            state,
            Error (
                TypeBindingFailure.Unmodelled
                    {
                        Diagnostic =
                            $"TODO: the argument %d{parameter} of the instantiation of %s{method.Name} on %O{declaring.Identity} is abstract and can be cast to %O{required}, an interface declaring static abstract methods; CoreCLR then requires each of them to resolve on the argument, which PawPrint does not decide"
                    }
            )

    let concretizeMethodWithAllGenerics
        (loggerFactory : ILoggerFactory)
        (dotnetRuntimeDirs : string seq)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (typeGenerics : ImmutableArray<ConcreteTypeHandle>)
        (methodToCall : WoofWare.PawPrint.MethodInfo<'ty, GenericParamFromMetadata, TypeDefn>)
        (methodGenerics : ImmutableArray<ConcreteTypeHandle>)
        (state : TypeSystemState)
        : TypeSystemState *
          Result<
              WoofWare.PawPrint.MethodInfo<ConcreteTypeHandle, ConcreteTypeHandle, ConcreteTypeHandle> *
              ConcreteTypeHandle,
              TypeBindingFailure
           >
        =
        // `Concretization.concretizeMethod` reads the definition's metadata, the two handle
        // lists, and of the declaring type only its identity and its generic *arity*; so that
        // is the key. A dynamic method has no declaring type and is not memoised.
        let key : ConcreteMethodKey option =
            ConcreteMethodKey.ofMethod methodToCall typeGenerics methodGenerics

        match key |> Option.bind (fun key -> Map.tryFind key state._ConcretisedMethods) with
        | Some hit -> state, Ok (hit.Method, hit.DeclaringTypeHandle)
        | None ->

        let loader = TypeResolution.directoryLoader loggerFactory dotnetRuntimeDirs

        let declaringTypeHandle, newConcreteTypes, newAssemblies =
            Concretization.concretizeMethodDeclaringType
                state.ConcreteTypes
                loader
                state._LoadedAssemblies
                baseClassTypes
                methodToCall
                typeGenerics
                methodGenerics

        let state =
            { state with
                ConcreteTypes = newConcreteTypes
                _LoadedAssemblies = newAssemblies
            }

        // A failure is not memoised: binding the method again fails the same way.
        match declaringTypeHandle with
        | Error failure -> state, Error failure
        | Ok declaringTypeHandle ->

        match
            methodConstraints
                loggerFactory
                dotnetRuntimeDirs
                baseClassTypes
                methodToCall
                declaringTypeHandle
                methodGenerics
                state
        with
        | state, Error failure -> state, Error failure
        | state, Ok () ->

        let concretizedMethod, newConcreteTypes, newAssemblies =
            Concretization.concretizeMethodSignatureOn
                state.ConcreteTypes
                loader
                state._LoadedAssemblies
                baseClassTypes
                methodToCall
                typeGenerics
                methodGenerics
                declaringTypeHandle

        let state =
            { state with
                ConcreteTypes = newConcreteTypes
                _LoadedAssemblies = newAssemblies
            }

        match concretizedMethod with
        | Error failure -> state, Error failure
        | Ok concretizedMethod ->

        let state =
            match key with
            | None -> state
            | Some key ->
                state.WithConcretisedMethod
                    key
                    {
                        Method = concretizedMethod
                        DeclaringTypeHandle = declaringTypeHandle
                    }

        state, Ok (concretizedMethod, declaringTypeHandle)

    /// As `concretizeMethodWithAllGenerics`, with the method's instantiation `methodGenerics` first
    /// concretized in the context `typeGenerics` and `currentExecutingMethodGenerics`, as spelled in
    /// `callingAssemblyFullName`. `None` names the method with no instantiation, as a bare MethodDef
    /// or MemberRef token does, and fails on a generic method.
    let concretizeMethodWithTypeGenerics
        (loggerFactory : ILoggerFactory)
        (dotnetRuntimeDirs : string seq)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (typeGenerics : ImmutableArray<ConcreteTypeHandle>)
        (methodToCall : WoofWare.PawPrint.MethodInfo<'ty, GenericParamFromMetadata, TypeDefn>)
        (methodGenerics : TypeDefn ImmutableArray option)
        (callingAssemblyFullName : string)
        (currentExecutingMethodGenerics : ImmutableArray<ConcreteTypeHandle>)
        (state : TypeSystemState)
        : TypeSystemState *
          Result<
              WoofWare.PawPrint.MethodInfo<ConcreteTypeHandle, ConcreteTypeHandle, ConcreteTypeHandle> *
              ConcreteTypeHandle,
              TypeBindingFailure
           >
        =

        // CoreCLR instantiates nothing for a generic method named bare, so this is not its refusal
        // of a wrong-arity instantiation. Measured on .NET 10, a `call` through such a token throws
        // InvalidOperationException ("not fully instantiated") when it runs, unless the JIT inlines
        // the callee, and `ldftn` and `ldtoken` succeed.
        match methodGenerics with
        | None when not methodToCall.Generics.IsEmpty ->
            failwith
                $"TODO: the generic method %s{methodToCall.Name}, which declares %d{methodToCall.Generics.Length} generic parameter(s), is named with no instantiation; CoreCLR's answer depends on the instruction and on whether the JIT inlines it"
        | _ ->

        // Concretize method generics if any, stopping at the first that does not bind
        let rec concretizeGenerics
            (state : TypeSystemState)
            (handles : ConcreteTypeHandle list)
            (generics : TypeDefn list)
            : TypeSystemState * Result<ImmutableArray<ConcreteTypeHandle>, TypeBindingFailure>
            =
            match generics with
            | [] -> state, Ok (handles |> List.rev |> ImmutableArray.CreateRange)
            | generic :: rest ->
                match
                    TypeSystemState.concretizeType
                        loggerFactory
                        dotnetRuntimeDirs
                        baseClassTypes
                        state
                        callingAssemblyFullName
                        typeGenerics
                        currentExecutingMethodGenerics
                        generic
                with
                | state, Ok handle -> concretizeGenerics state (handle :: handles) rest
                | state, Error failure -> state, Error failure

        match concretizeGenerics state [] (methodGenerics |> Option.map List.ofSeq |> Option.defaultValue []) with
        | state, Error failure -> state, Error failure
        | state, Ok concretizedMethodGenerics ->

        // Now concretize the entire method
        concretizeMethodWithAllGenerics
            loggerFactory
            dotnetRuntimeDirs
            baseClassTypes
            typeGenerics
            methodToCall
            concretizedMethodGenerics
            state

    /// The concrete types of the locals of `method`, a method `concretizeMethodWithAllGenerics`
    /// instantiated, against that instantiation, in slot order (`Concretization.concretizeLocals`):
    /// what CoreCLR loads when it compiles the method. A dynamic method's are not memoised.
    let concretizeLocals
        (loggerFactory : ILoggerFactory)
        (dotnetRuntimeDirs : string seq)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (method : WoofWare.PawPrint.MethodInfo<ConcreteTypeHandle, ConcreteTypeHandle, ConcreteTypeHandle>)
        (state : TypeSystemState)
        : TypeSystemState * Result<ImmutableArray<ConcreteTypeHandle>, TypeBindingFailure>
        =
        let key =
            ConcreteMethodKey.ofMethod method method.DeclaringTypeGenerics method.Generics

        match key |> Option.bind (fun key -> Map.tryFind key state._ConcretisedLocals) with
        | Some hit -> state, Ok hit
        | None ->

        let locals, concreteTypes, assemblies =
            Concretization.concretizeLocals
                state.ConcreteTypes
                (TypeResolution.directoryLoader loggerFactory dotnetRuntimeDirs)
                state._LoadedAssemblies
                baseClassTypes
                method

        let state =
            { state with
                ConcreteTypes = concreteTypes
                _LoadedAssemblies = assemblies
            }

        // A failure is not memoised: concretising the locals again fails the same way.
        match locals, key with
        | Ok locals, Some key -> state.WithConcretisedLocals key locals, Ok locals
        | Ok locals, None -> state, Ok locals
        | Error failure, _ -> state, Error failure
