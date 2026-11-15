namespace WoofWare.PawPrint

open Microsoft.Extensions.Logging

[<RequireQualifiedAccess>]
module NativeRuntimeHelpers =
    let tryExecuteQCall (entryPoint : string) (ctx : NativeCallContext) : NativeHandlerResult option =
        let state = ctx.State
        let instruction = ctx.Instruction

        match
            entryPoint,
            ctx.TargetAssembly.Name.Name,
            ctx.TargetType.Namespace,
            ctx.TargetType.Name,
            instruction.ExecutingMethod.Name,
            instruction.ExecutingMethod.Signature.ParameterTypes,
            instruction.ExecutingMethod.Signature.ReturnType
        with
        | "ReflectionInvocation_RunClassConstructor",
          "System.Private.CoreLib",
          "System.Runtime.CompilerServices",
          "RuntimeHelpers",
          "RunClassConstructor",
          [ CorelibType state.TypeSystem.ConcreteTypes ("System.Runtime.CompilerServices", "QCallTypeHandle", generics) ],
          MethodReturnType.Void when generics.IsEmpty ->
            let operation = "RuntimeHelpers.RunClassConstructor"
            let qCallHandle = instruction.Arguments.[0] |> EvalStackValue.ofCliType

            let typeHandleTarget =
                NativeCall.qCallTypeHandleToRuntimeTypeHandleTarget operation state qCallHandle

            match typeHandleTarget with
            | RuntimeTypeHandleTarget.DynamicMethodsClass scopeAssembly ->
                RuntimeTypeHandleTarget.refuseMetadataQuery operation scopeAssembly
            | RuntimeTypeHandleTarget.OpenConstructed _ as openConstructed ->
                failwith
                    $"TODO: open constructed types are not handled at Native/NativeRuntimeHelpers.fs:%s{__LINE__}; got %O{openConstructed}"
            | RuntimeTypeHandleTarget.OpenGenericTypeDefinition _ ->
                failwith
                    $"TODO: RuntimeHelpers.RunClassConstructor for open generic type definition %O{typeHandleTarget}"
            | RuntimeTypeHandleTarget.GenericParameter _
            | RuntimeTypeHandleTarget.MethodGenericParameter _ ->
                failwith $"TODO: RuntimeHelpers.RunClassConstructor for generic parameter %O{typeHandleTarget}"
            | RuntimeTypeHandleTarget.Composite _
            | RuntimeTypeHandleTarget.FunctionPointer _ ->
                // No .cctor, for the same reason as the closed shapes below.
                NativeHandlerResult.completed state |> Some
            | RuntimeTypeHandleTarget.Closed typeHandle ->
                match typeHandle with
                | ConcreteTypeHandle.Byref _
                | ConcreteTypeHandle.Pointer _
                | ConcreteTypeHandle.FunctionPointer _
                | ConcreteTypeHandle.OneDimArrayZero _
                | ConcreteTypeHandle.Array _ ->
                    // Pointer, byref, fnptr, and array type descriptors have no .cctor;
                    // CoreCLR treats this as a no-op. Return immediately.
                    NativeHandlerResult.completed state |> Some
                | ConcreteTypeHandle.Continuation layout ->
                    ContinuationType.unasked "RuntimeHelpers.RunClassConstructor" layout
                | ConcreteTypeHandle.Concrete _ ->
                    let state, typeInit =
                        IlMachineStateExecution.ensureTypeInitialised
                            ctx.LoggerFactory
                            ctx.BaseClassTypes
                            ctx.Thread
                            typeHandle
                            state

                    match typeInit with
                    | WhatWeDid.Executed -> NativeHandlerResult.completed state |> Some
                    | WhatWeDid.Aborted fatal -> NativeHandlerResult.aborted ctx.Thread fatal state |> Some
                    | WhatWeDid.UnhandledException exn ->
                        NativeHandlerResult.unhandledException ctx.Thread exn state |> Some
                    | WhatWeDid.SuspendedForClassInit ->
                        // The cctor was pushed as a new frame. We must NOT go through the normal
                        // returnStackFrame path (which would pop the cctor frame we just pushed).
                        // Instead, return Stepped directly so the dispatch loop runs the cctor.
                        // When the cctor finishes, returnStackFrame pops it, bringing us back to
                        // this native method frame. executeOneStep re-enters here and
                        // ensureTypeInitialised will return Executed.
                        NativeHandlerResult.suspendedForClassInit state |> Some
                    | WhatWeDid.SuspendedForManagedCall ->
                        failwith "logic error: ensureTypeInitialised cannot suspend for an arbitrary managed call"
                    | WhatWeDid.ThrowingTypeInitializationException ->
                        NativeHandlerResult.throwingTypeInitializationException state |> Some
                    | WhatWeDid.BlockedOnClassInit blockedBy ->
                        // Another thread owns this type's .cctor lock. Yield so the scheduler
                        // can run that thread to completion before re-entering.
                        NativeHandlerResult.blockedOnClassInit blockedBy state |> Some
                    | WhatWeDid.VoluntaryYield _ ->
                        failwith "logic error: ensureTypeInitialised cannot produce a VoluntaryYield"
        | "ReflectionInvocation_CompileMethod",
          "System.Private.CoreLib",
          "System.Runtime.CompilerServices",
          "RuntimeHelpers",
          "CompileMethod",
          [ CorelibType state.TypeSystem.ConcreteTypes ("System", "RuntimeMethodHandleInternal", generics) ],
          MethodReturnType.Void when generics.IsEmpty ->
            // CoreCLR JIT-compiles the method here unless it is already compiled
            // (`reflectioninvocation.cpp`, `IsPointingToPrestub` on .NET 10, `ShouldCallPrestub` on
            // .NET 11), and the failures of that compilation come out of the QCall.
            // `DynamicMethod.CreateDelegate` is its only caller in CoreLib, for a
            // restricted-skip-visibility method, so the method is always a dynamic one, and
            // compiling it is what `DynamicMethodExecution.prepare` models: latching its
            // `initLocals` and its `catch` clause types, or refusing a clause type with the
            // exception the JIT raises. `prepare` does nothing for a method already prepared, and
            // latches nothing when it refuses, so a second `CreateDelegate` prepares it again, as a
            // failed compilation is not remembered either. The instruction operands are still bound
            // when each instruction executes (docs/divergences.md, "What a
            // restricted-skip-visibility method's `CreateDelegate` compiles").
            let operation = "RuntimeHelpers.CompileMethod"

            let handle =
                match MethodHandleResolution.resolveMethodHandleFromArg operation state instruction.Arguments.[0] with
                | MethodHandle.FromDynamic handle -> handle
                | MethodHandle.FromMetadata identity ->
                    failwith
                        $"%s{operation}: the method handle names the metadata method %O{identity}; DynamicMethod.CreateDelegate, the QCall's only caller in CoreLib, passes a dynamic method's handle"

            match DynamicMethodExecution.prepare ctx.BaseClassTypes operation handle state with
            | state, Ok _ -> NativeHandlerResult.completed state |> Some
            | state, Error (exceptionType, why) ->
                let logger = ctx.LoggerFactory.CreateLogger "NativeRuntimeHelpers"
                logger.LogWarning ("RuntimeHelpers.CompileMethod refused a DynamicMethod: {Reason}", why)

                NativeHandlerResult.raiseExceptionWithFields
                    exceptionType
                    (DynamicMethodExecution.preparationFailureFields ctx.BaseClassTypes exceptionType)
                    state
                |> Some
        | _ -> None

    /// Identity hash for a managed object reference. Heap addresses are positive and
    /// monotonically increasing, so the address is a deterministic, unique, non-zero
    /// hash for any allocated object; null hashes to 0. The contract (non-zero for
    /// non-null, deterministic, stable across calls) matches real .NET. The bit
    /// pattern does not — CoreCLR returns a 26-bit randomised hash stored in the
    /// object header — so do not assume hashes fit any narrower field than int32.
    let private identityHash (operation : string) (arg : EvalStackValue) : int =
        match arg with
        | EvalStackValue.NullObjectRef -> 0
        | EvalStackValue.ObjectRef (ManagedHeapAddress addr) -> addr
        | other -> failwith $"%s{operation}: expected ObjectRef or NullObjectRef, got %O{other}"

    let tryExecute (ctx : NativeCallContext) : NativeHandlerResult option =
        let state = ctx.State
        let instruction = ctx.Instruction

        match
            ctx.TargetAssembly.Name.Name,
            ctx.TargetType.Namespace,
            ctx.TargetType.Name,
            instruction.ExecutingMethod.Name,
            instruction.ExecutingMethod.Signature.ParameterTypes,
            instruction.ExecutingMethod.Signature.ReturnType
        with
        | "System.Private.CoreLib",
          "System.Runtime.CompilerServices",
          "RuntimeHelpers",
          "GetHashCode",
          [ ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.Object ],
          MethodReturnType.Returns (ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.Int32) ->
            let hash =
                identityHash "RuntimeHelpers.GetHashCode" (EvalStackValue.ofCliType instruction.Arguments.[0])

            let state =
                IlMachineState.pushToEvalStack' (EvalStackValue.Int32 (Int32Source.Verbatim hash)) ctx.Thread state

            NativeHandlerResult.completed state |> Some
        | "System.Private.CoreLib",
          "System.Runtime.CompilerServices",
          "RuntimeHelpers",
          "TryEnsureSufficientExecutionStack",
          [],
          MethodReturnType.Returns (ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.Boolean) ->
            // CoreCLR probes the native thread's stack guard pages; BCL callers (async
            // resume, recursive parsers, `ConditionalWeakTable` rebuild, expression
            // compilation, ...) use this as a "take the fast recursive path or fall back
            // to an iterative one" decision. PawPrint has no native frame stack to
            // exhaust — the abstract machine's frames live on the F# heap — so the
            // honest answer is always "yes, sufficient." Returning `true` is also
            // deterministic, which matters more here than modelling stack pressure.
            // See issue #625 for the tracking discussion of a virtual frame-budget
            // model that would also let us cover the BCL's iterative fallback paths.
            let state = IlMachineState.pushToEvalStack (CliType.ofBool true) ctx.Thread state

            NativeHandlerResult.completed state |> Some
        | "System.Private.CoreLib",
          "System.Runtime.CompilerServices",
          "RuntimeHelpers",
          "TryGetHashCode",
          [ ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.Object ],
          MethodReturnType.Returns (ConcretePrimitive state.TypeSystem.ConcreteTypes PrimitiveType.Int32) ->
            // In CoreCLR, TryGetHashCode returns the cached identity hash or 0 if no hash
            // has been assigned yet, and the public GetHashCode wraps it as
            //     int h = TryGetHashCode(o); if (h == 0) return GetHashCodeWorker(o); return h;
            // We don't model lazy hash assignment, so we always return the same identity
            // hash GetHashCode would, keeping the wrapper's short-circuit consistent. In
            // the pinned .NET 10 CoreLib only TryGetHashCode is InternalCall — the public
            // GetHashCode is that managed wrapper — but we also intercept the public
            // GetHashCode directly; intercepting TryGetHashCode here covers callers
            // (e.g. ConditionalWeakTable) that bypass the public wrapper.
            let hash =
                identityHash "RuntimeHelpers.TryGetHashCode" (EvalStackValue.ofCliType instruction.Arguments.[0])

            let state =
                IlMachineState.pushToEvalStack' (EvalStackValue.Int32 (Int32Source.Verbatim hash)) ctx.Thread state

            NativeHandlerResult.completed state |> Some
        | _ -> None
