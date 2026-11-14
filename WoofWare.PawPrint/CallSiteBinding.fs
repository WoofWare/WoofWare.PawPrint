namespace WoofWare.PawPrint

open System.Reflection.Metadata
open Microsoft.Extensions.Logging

/// Why CoreCLR's JIT refuses to compile a method over one of its instructions' tokens. The
/// compilation raises in the method's caller, before the method has run anything.
[<RequireQualifiedAccess>]
type internal CompilationFailure =
    /// A `call` or `ldftn` names an abstract method itself, rather than through a `constrained.`
    /// prefix that resolves it, or a `newobj` names an abstract method: `BadImageFormatException`,
    /// "Bad IL format." (`BFA_BAD_IL`).
    | NamesAbstractMethod
    /// A `newobj` names a static method that is not abstract (`NewobjTarget.StaticMethod`):
    /// `InvalidProgramException`.
    | NewobjOfStaticMethod
    /// A `newobj` names an instance constructor of an abstract class (`NewobjTarget.AbstractClass`):
    /// `InvalidOperationException`, "Instances of abstract classes cannot be created."
    | InstantiatesAbstractClass
    /// The token names a type CoreCLR refuses as the instruction's operand (`TypeOperand.refusal`),
    /// or names its member through one: `InvalidProgramException`.
    | RefusesTypeOperand of TypeOperandRefusal
    /// The token names an instantiation whose constraints reject its arguments: `TypeLoadException`
    /// for the declaring type's, `VerificationException` for the method's.
    | ViolatesConstraints of ConstraintRefusal

/// Which accessor of a multi-dimensional array type a `call` names.
[<RequireQualifiedAccess>]
type internal ArrayAccessorKind =
    | Get
    | Set
    | Address

/// An accessor of a multi-dimensional array type, which the runtime synthesises (ECMA-335
/// II.14.2): a MemberRef row whose parent is a TypeSpec of `TypeDefn.Array`.
[<NoEquality ; NoComparison>]
type internal ArrayAccessor =
    {
        Kind : ArrayAccessorKind
        Element : TypeDefn
        Rank : int
        /// The MemberRef row's signature, as the call site spells it.
        Signature : MemberSignature
    }

/// Where the caller of an async variant that a matched await site binds stands, and what it does if
/// the variant suspends.
[<RequireQualifiedAccess>]
type internal BoundAwaitSite =
    /// The await pattern in a user's runtime-async body, whose whole pattern is the call's step
    /// (decision 15 of `docs/plans/2026-11-11-runtime-async.md`). The caller stands at the pattern's
    /// `call Await`, at `awaitOffset`, while the variant runs, for suspension, `DiagnosticIP`,
    /// exception dispatch and stack walks, as the JIT reports the call at that offset, and continues
    /// after that instruction. If the variant suspends, the caller does too, with a continuation that
    /// continues as `handling` says.
    | Await of awaitOffset : int * handling : ContinuationContextHandling
    /// A tail shape in an async version (decision 17), whose call is the last thing the async
    /// version does: it stands at the call while the variant runs, and returns the variant's result.
    /// If the variant suspends, the caller does what `onSuspension` says.
    | TailShape of onSuspension : OnSuspension

[<RequireQualifiedAccess>]
module internal BoundAwaitSite =

    /// The offset the caller moves its program counter to before it calls the variant, if any.
    let awaitOffset (site : BoundAwaitSite) : int option =
        match site with
        | BoundAwaitSite.Await (awaitOffset, _) -> Some awaitOffset
        | BoundAwaitSite.TailShape _ -> None

    /// What the caller does if the variant suspends.
    let onSuspension (site : BoundAwaitSite) : OnSuspension =
        match site with
        | BoundAwaitSite.Await (_, handling) -> OnSuspension.CallerSuspends (SuspendedCallFlags.Continues handling)
        | BoundAwaitSite.TailShape onSuspension -> onSuspension

/// A call at a matched await site that binds the named method's async variant.
[<NoEquality ; NoComparison>]
type internal BoundAwait =
    {
        /// The async variant the call calls.
        Variant : WoofWare.PawPrint.MethodInfo<ConcreteTypeHandle, ConcreteTypeHandle, ConcreteTypeHandle>
        Site : BoundAwaitSite
    }

/// What CoreCLR's JIT makes of a `call` or `callvirt` as the start of an await site: an await in a
/// user's runtime-async body, or a tail shape in an async version.
[<RequireQualifiedAccess>]
[<NoEquality ; NoComparison>]
type internal AwaitSiteOutcome =
    /// The JIT reads no await site here: the frame's method does not read the pattern, or nothing
    /// that matches it follows the call.
    | NotAnAwaitSite
    /// A matched await site that binds the named method's async variant.
    | Binds of BoundAwait
    /// A matched await site that binds no variant, as decision 13's classification says (never
    /// `AwaitSiteBinding.BindsVariant`). The call calls the named method, and the instructions of
    /// the pattern run after it.
    | DoesNotBind of AwaitSiteBinding

/// What a `call` or `ldftn` does, decided by binding its token, with the type its `constrained.`
/// prefix names, as CoreCLR's JIT binds them when it compiles the method holding the instruction.
[<RequireQualifiedAccess>]
[<NoEquality ; NoComparison>]
type internal CallSiteBinding =
    /// The instruction calls this method, or loads a pointer to it.
    | Method of WoofWare.PawPrint.MethodInfo<ConcreteTypeHandle, ConcreteTypeHandle, ConcreteTypeHandle>
    /// The `call` begins a matched await site that binds the named method's async variant.
    | BoundAwait of BoundAwait
    /// The `call` names an accessor of a multi-dimensional array type.
    | ArrayAccessor of ArrayAccessor
    /// The JIT compiles a throw of this failure in place of the instruction: a `constrained.` call
    /// or load of a static virtual method that resolves to no implementation, in code CoreCLR
    /// compiles for the exact instantiation (`getCallInfo`, jitinterface.cpp).
    | RaisesWhenExecuted of DispatchFailure
    /// CoreCLR refuses to compile the method holding the instruction.
    | CompilationFails of CompilationFailure
    /// PawPrint cannot bind the instruction's token.
    | Unbound of UnboundToken

/// What the type token of a `constrained.` prefix names, bound as CoreCLR's JIT binds it when it
/// compiles the method holding the prefix.
[<RequireQualifiedAccess>]
[<NoEquality ; NoComparison>]
type internal ConstrainedPrefixBinding =
    /// The type the prefix names.
    | Type of ConcreteTypeHandle
    /// CoreCLR refuses to compile the method holding the prefix.
    | CompilationFails of CompilationFailure
    /// PawPrint cannot bind the prefix's token.
    | Unbound of UnboundToken

/// Binds the tokens of `call`, `ldftn` and the `constrained.` prefix as CoreCLR's JIT does when it
/// compiles the method holding them, against the executing frame's generic context. Each binding
/// returns the state it leaves behind, having loaded assemblies and registered concrete types on
/// the way, and decides what the instruction does without running any of it.
[<RequireQualifiedAccess>]
module internal CallSiteBinding =

    /// The accessor of a multi-dimensional array type `token` of `assembly` names, if it names one:
    /// a MemberRef row whose parent TypeSpec is `TypeDefn.Array`. `None` for any other token.
    let private arrayAccessor
        (assembly : DumpedAssembly)
        (token : MetadataToken)
        : Result<ArrayAccessor, UnboundToken> option
        =
        match token with
        | MetadataToken.MemberReference reference ->
            match assembly.Members.TryGetValue reference with
            | false, _ -> None
            | true, memberRef ->

            match memberRef.Parent with
            | MetadataToken.TypeSpecification spec ->
                match assembly.TypeSpecs.TryGetValue spec with
                | true, typeSpec ->
                    match typeSpec.Signature with
                    | TypeDefn.Array (element, rank) ->
                        let accessor (kind : ArrayAccessorKind) : Result<ArrayAccessor, UnboundToken> =
                            Ok
                                {
                                    Kind = kind
                                    Element = element
                                    Rank = rank
                                    Signature = memberRef.Signature
                                }

                        match assembly.Strings memberRef.Name with
                        | "Get" -> accessor ArrayAccessorKind.Get
                        | "Set" -> accessor ArrayAccessorKind.Set
                        | "Address" -> accessor ArrayAccessorKind.Address
                        | other ->
                            UnboundToken.unmodelled
                                $"unexpected synthesized member %s{other} on multi-dim array (rank %d{rank}); expected Get/Set/Address/.ctor"
                            |> Error
                        |> Some
                    | _ -> None
                | false, _ -> None
            | _ -> None
        | _ -> None

    /// Which static virtual method a `constrained.` call or load of `fromMetadata`, bound as
    /// `concretized`, runs, where the prefix names `constrained`; and what the instruction does
    /// where it runs none.
    ///
    /// `opName` names the instruction, for diagnostics. `isLdftn` says it is an `ldftn`, where a
    /// failure in code CoreCLR shares between instantiations is not the JIT's to raise.
    let private constrainedStaticImplementation
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (thread : ThreadId)
        (opName : string)
        (isLdftn : bool)
        (constrained : ConcreteTypeHandle)
        (fromMetadata : WoofWare.PawPrint.MethodInfo<TypeDefn, GenericParamFromMetadata, TypeDefn>)
        (concretized : WoofWare.PawPrint.MethodInfo<ConcreteTypeHandle, ConcreteTypeHandle, ConcreteTypeHandle>)
        (state : IlMachineState)
        : IlMachineState * CallSiteBinding
        =
        let typeSystem, resolvedAgainst =
            ConstrainedType.resolve
                loggerFactory
                state.DotnetRuntimeDirs
                baseClassTypes
                state.TypeSystem
                constrained
                fromMetadata

        let state = state.WithTypeSystem typeSystem

        let resolvedAgainst =
            match resolvedAgainst with
            | Error failure -> Error (UnboundToken.Type failure)
            | Ok (ConstrainedType.AsNamed named) -> Ok named
            | Ok (ConstrainedType.EnumUnderlying (_, underlying, RetargetedCall.Named)) -> Ok underlying
            | Ok (ConstrainedType.EnumUnderlying (_, _, RetargetedCall.ObjectGetHashCode)) ->
                UnboundToken.unmodelled
                    $"%s{opName}: expected a static interface method, got %s{MethodOwner.describe fromMetadata.Owner}::%s{fromMetadata.Name}, a class's method"
                |> Error
            | Ok (ConstrainedType.Undecided enum) ->
                let described =
                    AllConcreteTypes.describe state.TypeSystem._LoadedAssemblies state.TypeSystem.ConcreteTypes enum

                UnboundToken.unmodelled
                    $"TODO: %s{opName} of %s{MethodOwner.describe fromMetadata.Owner}::%s{fromMetadata.Name} on the enum %s{described}: CoreCLR resolves it against the enum's underlying primitive if the method holds Object.GetHashCode's slot number, but PawPrint places no slot for this method, or reads no single instance field `value__` off the enum"
                |> Error

        match resolvedAgainst with
        | Error unbound -> state, CallSiteBinding.Unbound unbound
        | Ok resolvedAgainst ->

        let typeSystem, implementation =
            StaticVirtualDispatch.resolveConstrainedStaticInterfaceMethod
                loggerFactory
                state.DotnetRuntimeDirs
                baseClassTypes
                opName
                resolvedAgainst
                fromMetadata
                concretized
                state.TypeSystem

        let state = state.WithTypeSystem typeSystem

        match implementation with
        | Error failure -> state, CallSiteBinding.Unbound (UnboundToken.Type failure)
        | Ok (ConstrainedStaticImplementation.Runs (implementation, _declaringType)) ->
            state, CallSiteBinding.Method implementation
        | Ok (ConstrainedStaticImplementation.Fails failure) ->
            let frame = state.ThreadState.[thread].MethodState.ExecutingMethod

            // Where CoreCLR raises an `ldftn`'s failure depends on whether it compiled this frame as
            // shared code. In exact code the JIT resolves the slot as it compiles, and `getCallInfo`
            // (jitinterface.cpp) plants a throw helper at the `ldftn` itself. In shared code the
            // pointer comes from a generic dictionary slot holding a stub that resolves and throws
            // when called (`CreateStubForStaticVirtualDispatch`, genericdict.cpp) -- unless the JIT
            // inlined this frame into exact code, where the `ldftn` throws. Measured: under default
            // tiering the delegate is made, and under `DOTNET_TieredCompilation=0` an inlined one
            // throws at the `ldftn`.
            if isLdftn && CodeSharing.isShared baseClassTypes state.TypeSystem frame then
                state,
                CallSiteBinding.Unbound (
                    UnboundToken.unmodelled
                        $"TODO: constrained. Ldftn of %s{fromMetadata.Name} resolved to %s{DispatchFailure.describe failure}, in %s{frame.Name}, which CoreCLR runs as shared generic code. Measured: real .NET then makes a delegate over a stub, and invoking it raises what that failure does, unless the JIT inlined the frame into exact code, where the ldftn throws; which one happens depends on the JIT"
                )
            else
                state, CallSiteBinding.RaisesWhenExecuted failure

    /// Whether the call whose operand is `operand`, read in `assembly`, names a JIT intrinsic of
    /// CoreLib that `accept` takes, by its declaring type's namespace and name, its own name and its
    /// row (`eeIsIntrinsic` and `lookupNamedIntrinsic`, importercalls.cpp).
    let private namesIntrinsic
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (thread : ThreadId)
        (assembly : DumpedAssembly)
        (accept : string -> string -> string -> MetadataMethodFacts -> bool)
        (operand : MetadataOperand)
        (state : IlMachineState)
        : IlMachineState * bool
        =
        match operand with
        | MetadataOperand.FromDynamicScope _
        | MetadataOperand.FromStubScope _ ->
            failwith
                $"BUG: a runtime-async body names %O{operand}, but every such body is read from an image, whose operands are its tokens"
        | MetadataOperand.FromMetadata sourced ->

        match
            MethodTokenBinding.bind
                loggerFactory
                baseClassTypes
                thread
                (MemberReferenceUse.Other "call")
                "call"
                GenericTypeMethodRow.Typical
                assembly
                sourced.Token
                state
        with
        | state, MethodTokenBinding.Method (MethodInfo.Metadata (_, facts, _) as method, _) when
            method.DeclaringAssemblyFullName = baseClassTypes.Corelib.DefinitionFullName
            ->
            let declaring = method.RequiredDeclaringType

            state,
            accept declaring.Namespace declaring.Name method.Name facts
            && IntrinsicBody.isIntrinsic baseClassTypes.Corelib facts.Handle
        // Not one of CoreLib's methods, or a token the JIT cannot resolve; the instruction meets the
        // latter when it runs.
        | state, MethodTokenBinding.Method _
        | state, MethodTokenBinding.RefusedParent _
        | state, MethodTokenBinding.ViolatesConstraints _
        | state, MethodTokenBinding.Unbound _ -> state, false

    /// The async variant CoreCLR's VM supplies for a call naming `named` at a matched await site
    /// (`CORINFO_TOKENKIND_Await`, jitinterface.cpp), or why it supplies none.
    let private variantOfNamed
        (loggerFactory : ILoggerFactory)
        (named : WoofWare.PawPrint.MethodInfo<ConcreteTypeHandle, ConcreteTypeHandle, ConcreteTypeHandle>)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (state : IlMachineState)
        : IlMachineState *
          Result<
              WoofWare.PawPrint.MethodInfo<GenericParamFromMetadata, GenericParamFromMetadata, TypeDefn>,
              AsyncVariantRefusal
           >
        =
        match named with
        | MethodInfo.Synthesised _ -> state, Error AsyncVariantRefusal.Synthesised
        | MethodInfo.Metadata (_, facts, _) ->
            let declaringAssembly =
                state.LoadedAssembly named.DeclaringAssemblyFullName
                |> Option.defaultWith (fun () ->
                    failwith
                        $"BUG: a call binds %O{named}, but its declaring assembly %s{named.DeclaringAssemblyFullName} is not loaded"
                )

            match
                RuntimeAsyncMethod.asyncVariantOf
                    loggerFactory
                    state.DotnetRuntimeDirs
                    baseClassTypes.Corelib
                    declaringAssembly
                    declaringAssembly.Methods.[facts.Handle]
                    state.TypeSystem._LoadedAssemblies
            with
            | assemblies, variant ->
                let state =
                    { state with
                        TypeSystem =
                            { state.TypeSystem with
                                _LoadedAssemblies = assemblies
                            }
                    }

                state, variant

    /// Decision 13's classification of a matched await site whose pattern is `pattern`, and, where
    /// it binds, the variant concretised as `named` is, with the continuation handling the
    /// classification gives.
    let private classifySite
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (pattern : MatchedPattern)
        (opcode : AwaitedCall)
        (constrained : bool)
        (named : WoofWare.PawPrint.MethodInfo<ConcreteTypeHandle, ConcreteTypeHandle, ConcreteTypeHandle>)
        (site : string)
        (state : IlMachineState)
        : IlMachineState *
          Result<
              WoofWare.PawPrint.MethodInfo<ConcreteTypeHandle, ConcreteTypeHandle, ConcreteTypeHandle> *
              ContinuationContextHandling,
              AwaitSiteBinding
           >
        =
        let state, variant = variantOfNamed loggerFactory named baseClassTypes state

        let classified =
            AwaitSiteBinding.classify
                {
                    Pattern = pattern
                    Opcode = opcode
                    Constrained = constrained
                    NamedDispatchesVirtually = named.DispatchesVirtually
                    NamedVariant =
                        variant
                        |> Result.map (fun variant ->
                            match variant.Body with
                            | MethodBody.AsyncIl _ -> AsyncVariantBody.RuntimeAsyncIl
                            | MethodBody.Il _ -> AsyncVariantBody.AsyncVersion
                            | MethodBody.Abstract -> AsyncVariantBody.Abstract
                            | other ->
                                failwith
                                    $"BUG: RuntimeAsyncMethod.asyncVariantOf gave %O{variant} the body %O{other}, which it refuses"
                        )
                }

        match classified, variant with
        | AwaitSiteBinding.BindsVariant handling, Ok variant ->
            let state, variant =
                ExecutionConcretization.concretizeMethodWithAllGenerics
                    loggerFactory
                    baseClassTypes
                    named.DeclaringTypeGenerics
                    variant
                    named.Generics
                    state

            let variant, _ = BindingStop.bound variant
            state, Ok (variant, handling)
        | AwaitSiteBinding.BindsVariant _, Error refusal ->
            failwith $"BUG: %s{site} of %O{named} binds its async variant, which it has none of: %A{refusal}"
        // PawPrint cannot tell whether CoreCLR supplies a variant, though CoreCLR loaded the method.
        | AwaitSiteBinding.NoVariant (AsyncVariantRefusal.ReturnTypeUnbound failure), _
        | AwaitSiteBinding.NoVariant (AsyncVariantRefusal.BaseTypeUnbound failure), _ ->
            failwith
                $"%s{site} of %O{named}: deciding whether it has an async variant needs a type PawPrint cannot bind: %s{TypeBindingFailure.describe failure}"
        | AwaitSiteBinding.Unmodelled body, _ ->
            failwith
                $"TODO: %s{site} of %O{named} binds its async variant, whose body is %A{body}; CoreCLR runs that variant, which PawPrint does not model (decision 16 of docs/plans/2026-11-11-runtime-async.md)"
        | AwaitSiteBinding.NoVariant _, _
        | AwaitSiteBinding.NotYetBound _, _ -> state, Error classified

    /// The tail shape the `call` or `callvirt` at the active frame of `thread`, an async version,
    /// begins (`impMatchAsyncVersionTailCall`, importer.cpp), and what decision 13 binds there; where
    /// it binds, whether its await is a tail await (decision 17).
    let private tailShapeSite
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (thread : ThreadId)
        (assembly : DumpedAssembly)
        (opcode : AwaitedCall)
        (constrained : bool)
        (named : WoofWare.PawPrint.MethodInfo<ConcreteTypeHandle, ConcreteTypeHandle, ConcreteTypeHandle>)
        (body : MethodInstructions)
        (state : IlMachineState)
        : IlMachineState * AwaitSiteOutcome
        =
        let frame = state.ThreadState.[thread].MethodState
        let caller = MethodInfo.requireMetadata "an async version" frame.ExecutingMethod

        // The JIT reads no tail shape in the async version of a synchronized method
        // (`CORINFO_FLG_SYNCH`), whose every `ret` wraps.
        if caller.ImplAttributes.HasFlag System.Reflection.MethodImplAttributes.Synchronized then
            state, AwaitSiteOutcome.NotAnAwaitSite
        else

        match AwaitPattern.tailShapeAfter body frame.IlOpIndex with
        | None -> state, AwaitSiteOutcome.NotAnAwaitSite
        | Some shape ->

        let state, confirmed =
            match shape with
            | TailShape.Return -> state, true
            | TailShape.NewValueTask constructor ->
                namesIntrinsic
                    loggerFactory
                    baseClassTypes
                    thread
                    assembly
                    (fun ns typeName name facts ->
                        ns = "System.Threading.Tasks"
                        && (typeName = "ValueTask" || typeName = "ValueTask`1")
                        && name = ".ctor"
                        // One argument, and not the `T` of `ValueTask<T>(T)`, which
                        // `impFoldAwaitedTopOfStack` folds instead.
                        && (
                            match facts.RawSignature.ParameterTypes with
                            | [ TypeDefn.GenericTypeParameter 0 ] -> false
                            | [ _ ] -> true
                            | _ -> false
                        )
                    )
                    constructor
                    state
            | TailShape.AsTask asTask ->
                namesIntrinsic
                    loggerFactory
                    baseClassTypes
                    thread
                    assembly
                    (fun ns typeName name _ ->
                        ns = "System.Threading.Tasks"
                        && (typeName = "ValueTask" || typeName = "ValueTask`1")
                        && name = "AsTask"
                    )
                    asTask
                    state

        // Where an operand names something else, nothing shorter the JIT would read can match.
        if not confirmed then
            state, AwaitSiteOutcome.NotAnAwaitSite
        else

        let site =
            $"the tail shape at IL_%04X{frame.IlOpIndex} of %O{frame.ExecutingMethod}"

        match
            classifySite loggerFactory baseClassTypes MatchedPattern.TailShape opcode constrained named site state
        with
        | state, Error classified -> state, AwaitSiteOutcome.DoesNotBind classified
        | state, Ok (variant, handling) ->

        let returnTypesAgree =
            match frame.ExecutingMethod.Signature.ReturnType, variant.Signature.ReturnType with
            | MethodReturnType.Void, MethodReturnType.Void -> true
            | MethodReturnType.Returns returned, MethodReturnType.Returns result when returned = result -> true
            // A `Task<T>` returned as a `Task`.
            | MethodReturnType.Void, MethodReturnType.Returns _ -> false
            | returned, result ->
                failwith
                    $"TODO: %s{site} returns what %O{variant} returns, %O{result}, as %O{returned}; only unverifiable IL returns one promise type as another that is not `Task`, and the JIT compares the two as its own types (`gtReturnType`), which PawPrint does not model"

        let await =
            AsyncVersionAwait.TailCall (
                shape,
                TailCallFacts.ofCall
                    assembly
                    caller
                    // The JIT knows its callee exactly only where the call does not dispatch.
                    (match opcode with
                     | AwaitedCall.Callvirt when named.DispatchesVirtually -> None
                     | AwaitedCall.Callvirt
                     | AwaitedCall.Call -> Some (MethodInfo.requireMetadata "the callee of a tail shape" named))
                    returnTypesAgree
            )

        let flags =
            match shape, handling with
            | TailShape.AsTask _, ContinuationContextHandling.NoPreference -> SuspendedCallFlags.ValueTaskAdaptedToTask
            | TailShape.Return, handling
            | TailShape.NewValueTask _, handling
            | TailShape.AsTask _, handling -> SuspendedCallFlags.Continues handling

        let onSuspension =
            if AwaitPattern.isTailAwait await then
                OnSuspension.TailAwait
            else
                OnSuspension.CallerSuspends flags

        state,
        AwaitSiteOutcome.Binds
            {
                Variant = variant
                Site = BoundAwaitSite.TailShape onSuspension
            }

    /// The await site the `call` or `callvirt` (`opcode`) at the active frame's program counter of
    /// `thread` begins, as CoreCLR's JIT reads it when it compiles the frame's method, and what
    /// decision 13 of `docs/plans/2026-11-11-runtime-async.md` binds there: an await
    /// (`impMatchTaskAwaitPattern`, importer.cpp) in a frame with runtime async's calling convention
    /// that is not an async version, or a tail shape (`impMatchAsyncVersionTailCall`) in an async
    /// version. `assembly` holds the frame's IL; the call's token binds `named`; `constrained` says
    /// whether a `constrained.` prefix precedes it.
    ///
    /// Asked only of a call that runs: the JIT never imports a call inside a longer match that binds.
    /// A matched await in a frame of runtime async's own machinery is refused.
    let awaitSite
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (thread : ThreadId)
        (assembly : DumpedAssembly)
        (opcode : AwaitedCall)
        (constrained : bool)
        (named : WoofWare.PawPrint.MethodInfo<ConcreteTypeHandle, ConcreteTypeHandle, ConcreteTypeHandle>)
        (state : IlMachineState)
        : IlMachineState * AwaitSiteOutcome
        =
        let frame = state.ThreadState.[thread].MethodState

        // The JIT reads the patterns only in a method with runtime async's calling convention
        // (`compIsAsync`).
        match frame.AsyncFrame with
        | None -> state, AwaitSiteOutcome.NotAnAwaitSite
        | Some asyncFrame ->

        let body =
            match frame.ExecutingMethod.Body with
            | MethodBody.Il body -> body
            | other ->
                failwith
                    $"BUG: %O{frame.ExecutingMethod} has runtime async's calling convention and is calling, but runs %O{other} rather than IL"

        match asyncFrame with
        | AsyncFrame.AsyncVersion _ ->
            tailShapeSite loggerFactory baseClassTypes thread assembly opcode constrained named body state
        | AsyncFrame.Transparent _
        | AsyncFrame.SavesContexts _ ->

        match AwaitPattern.awaitAfter body frame.IlOpIndex with
        | None -> state, AwaitSiteOutcome.NotAnAwaitSite
        | Some follower ->

        let state, awaits =
            namesIntrinsic
                loggerFactory
                baseClassTypes
                thread
                assembly
                (fun ns typeName name _ ->
                    ns = "System.Runtime.CompilerServices"
                    && typeName = "AsyncHelpers"
                    && name = "Await"
                )
                follower.Await
                state

        let state, configures =
            match follower.ConfigureAwait with
            | None -> state, true
            | Some configureAwait ->
                namesIntrinsic
                    loggerFactory
                    baseClassTypes
                    thread
                    assembly
                    (fun ns typeName name _ ->
                        ns = "System.Threading.Tasks"
                        && (typeName = "Task"
                            || typeName = "Task`1"
                            || typeName = "ValueTask"
                            || typeName = "ValueTask`1")
                        && name = "ConfigureAwait"
                    )
                    configureAwait.Method
                    state

        // Where an operand names something else, nothing shorter the JIT would read can match.
        if not (awaits && configures) then
            state, AwaitSiteOutcome.NotAnAwaitSite
        else

        match asyncFrame with
        | AsyncFrame.Transparent _ ->
            failwith
                $"TODO: %O{frame.ExecutingMethod}, a method of runtime async's own machinery, awaits its call at IL_%04X{frame.IlOpIndex} through the JIT's await pattern (to IL_%04X{follower.AwaitOffset}), which binds an async variant there; PawPrint binds none in such a frame (decision 15 of docs/plans/2026-11-11-runtime-async.md)"
        | AsyncFrame.AsyncVersion _ -> failwith "logic error: an async version reads tail shapes, as matched above"
        | AsyncFrame.SavesContexts _ ->

        let pattern =
            follower.ConfigureAwait
            |> Option.forall (fun configureAwait -> configureAwait.ContinueOnCapturedContext)
            |> MatchedPattern.Await

        let site = $"awaiting at IL_%04X{follower.AwaitOffset} of %O{frame.ExecutingMethod}"

        match classifySite loggerFactory baseClassTypes pattern opcode constrained named site state with
        | state, Error classified -> state, AwaitSiteOutcome.DoesNotBind classified
        | state, Ok (variant, handling) ->
            state,
            AwaitSiteOutcome.Binds
                {
                    Variant = variant
                    Site = BoundAwaitSite.Await (follower.AwaitOffset, handling)
                }

    /// What a `call` or `ldftn` (`opName`, used as `use'`) of method token `token` does, where its
    /// `constrained.` prefix names `constrained`; `constrainedOpName` names the instruction under
    /// that prefix.
    let private bindMethodSite
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (thread : ThreadId)
        (use' : MemberReferenceUse)
        (opName : string)
        (constrainedOpName : string)
        (isLdftn : bool)
        (assembly : DumpedAssembly)
        (constrained : ConcreteTypeHandle option)
        (token : MetadataToken)
        (state : IlMachineState)
        : IlMachineState * CallSiteBinding
        =
        match
            MethodTokenBinding.bind
                loggerFactory
                baseClassTypes
                thread
                use'
                opName
                GenericTypeMethodRow.Typical
                assembly
                token
                state
        with
        | state, MethodTokenBinding.Unbound unbound -> state, CallSiteBinding.Unbound unbound
        | state, MethodTokenBinding.RefusedParent refusal ->
            state, CallSiteBinding.CompilationFails (CompilationFailure.RefusesTypeOperand refusal)
        | state, MethodTokenBinding.ViolatesConstraints refusal ->
            state, CallSiteBinding.CompilationFails (CompilationFailure.ViolatesConstraints refusal)
        | state, MethodTokenBinding.Method (concretized, fromMetadata) ->

        match constrained, concretized.Body with
        | None, MethodBody.Abstract -> state, CallSiteBinding.CompilationFails CompilationFailure.NamesAbstractMethod
        | _ ->

        let state, awaitSite =
            if isLdftn then
                state, AwaitSiteOutcome.NotAnAwaitSite
            else
                awaitSite
                    loggerFactory
                    baseClassTypes
                    thread
                    assembly
                    AwaitedCall.Call
                    constrained.IsSome
                    concretized
                    state

        match awaitSite with
        | AwaitSiteOutcome.Binds bound -> state, CallSiteBinding.BoundAwait bound
        | AwaitSiteOutcome.NotAnAwaitSite
        | AwaitSiteOutcome.DoesNotBind _ ->

        match constrained with
        | Some constrained ->
            constrainedStaticImplementation
                loggerFactory
                baseClassTypes
                thread
                constrainedOpName
                isLdftn
                constrained
                fromMetadata
                concretized
                state
        | None -> state, CallSiteBinding.Method concretized

    /// What a `call` of metadata token `token` of `assembly` does, where its `constrained.` prefix
    /// names `constrained`.
    let bindCall
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (thread : ThreadId)
        (assembly : DumpedAssembly)
        (constrained : ConcreteTypeHandle option)
        (token : MetadataToken)
        (state : IlMachineState)
        : IlMachineState * CallSiteBinding
        =
        // An array accessor has no managed body for member resolution to find: it would look the
        // name up on `System.Array` and fail.
        match arrayAccessor assembly token with
        | Some (Ok accessor) -> state, CallSiteBinding.ArrayAccessor accessor
        | Some (Error unbound) -> state, CallSiteBinding.Unbound unbound
        | None ->
            bindMethodSite
                loggerFactory
                baseClassTypes
                thread
                (MemberReferenceUse.Other "call")
                "call"
                "constrained.call"
                false
                assembly
                constrained
                token
                state

    /// What an `ldftn` of metadata token `token` of `assembly` does, where its `constrained.` prefix
    /// names `constrained`.
    let bindLdftn
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (thread : ThreadId)
        (assembly : DumpedAssembly)
        (constrained : ConcreteTypeHandle option)
        (token : MetadataToken)
        (state : IlMachineState)
        : IlMachineState * CallSiteBinding
        =
        bindMethodSite
            loggerFactory
            baseClassTypes
            thread
            (MemberReferenceUse.Other (string<UnaryMetadataTokenIlOp> UnaryMetadataTokenIlOp.Ldftn))
            "Ldftn"
            "Ldftn"
            true
            assembly
            constrained
            token
            state

    /// What the `constrained.` prefix of metadata token `token` of `assembly` names.
    let bindConstrainedPrefix
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (thread : ThreadId)
        (assembly : DumpedAssembly)
        (token : MetadataToken)
        (state : IlMachineState)
        : IlMachineState * ConstrainedPrefixBinding
        =
        let state, named =
            match token with
            | MetadataToken.TypeDefinition handle ->
                match IlMachineState.lookupTypeDefn loggerFactory baseClassTypes state assembly handle with
                | state, Ok ty -> state, Ok (ty, assembly)
                | state, Error failure -> state, Error (UnboundToken.Type failure)
            | MetadataToken.TypeReference handle ->
                match IlMachineState.lookupTypeRef loggerFactory baseClassTypes state assembly handle with
                | state, Ok named -> state, Ok named
                | state, Error failure ->
                    state, Error (UnboundToken.TypeReference (assembly, assembly.TypeRefs.[handle], failure))
            | MetadataToken.TypeSpecification spec -> state, Ok (assembly.TypeSpecs.[spec].Signature, assembly)
            | other -> state, Error (UnboundToken.unmodelled $"unexpected token %O{other} in Constrained")

        match named with
        | Error unbound -> state, ConstrainedPrefixBinding.Unbound unbound
        | Ok (ty, declaredIn) ->

        match
            MethodTokenBinding.concretizeInFrame
                loggerFactory
                baseClassTypes
                thread
                declaredIn.DefinitionFullName
                ty
                state
        with
        | state, Error failure -> state, ConstrainedPrefixBinding.Unbound (UnboundToken.Type failure)
        | state, Ok handle ->

        match TypeOperand.refusal baseClassTypes state.TypeSystem.ConcreteTypes handle with
        | Some refusal ->
            state, ConstrainedPrefixBinding.CompilationFails (CompilationFailure.RefusesTypeOperand refusal)
        | None -> state, ConstrainedPrefixBinding.Type handle
