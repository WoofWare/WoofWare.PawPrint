namespace WoofWare.PawPrint

open System.Collections.Immutable
open Microsoft.Extensions.Logging

/// <summary>
/// What CoreCLR's JIT adds to a user-written runtime-async body (<c>AsyncFrame.SavesContexts</c>)
/// to save the thread's <c>ExecutionContext</c> and <c>SynchronizationContext</c> on entry and put
/// them back as the body leaves (<c>SaveAsyncContexts</c>, jit/async.cpp): a call of
/// <c>AsyncHelpers.CaptureContexts</c> before the body, and a call of
/// <c>AsyncHelpers.RestoreContexts</c> before it returns and in a fault handler round the whole
/// body.
/// </summary>
/// <remarks>
/// Each call is a managed call the frame makes in place of an instruction of its own, whose return
/// does the frame's next step (<c>AsyncContextStep</c>). The body's IL is not rewritten, which would
/// move the offsets its stack traces report.
/// </remarks>
[<RequireQualifiedAccess>]
module AsyncContextSaving =

    /// Call the static `AsyncHelpers.methodName` with `arguments` from the active frame of
    /// `thread`, whose program counter stays where it is; `step` is what the frame does once it
    /// returns.
    let private callHelper
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (methodName : string)
        (arguments : CliType list)
        (step : AsyncContextStep)
        (thread : ThreadId)
        (state : IlMachineState)
        : ExecutionResult
        =
        let helper =
            CorelibEntryPoint.findCorelibStaticMethod
                baseClassTypes
                "System.Runtime.CompilerServices"
                "AsyncHelpers"
                methodName
                (List.length arguments)
                "save and restore a runtime-async body's contexts as CoreCLR's JIT does"

        let state, concretized =
            ExecutionConcretization.concretizeMethodWithAllGenerics
                loggerFactory
                baseClassTypes
                ImmutableArray.Empty
                helper
                ImmutableArray.Empty
                state

        let concretized, _ = BindingStop.bound concretized

        let state =
            (state, arguments)
            ||> List.fold (fun state argument -> IlMachineState.pushToEvalStack argument thread state)

        let state, commitment =
            IlMachineStateExecution.callMethodWithCommitment
                loggerFactory
                baseClassTypes
                None
                ConstructionState.NotConstructing
                IlMachineStateExecution.CallDispatch.Direct
                false
                false
                IlMachineStateExecution.CallSiteTransition.StaysCooperative
                concretized.Generics
                concretized
                thread
                state.ThreadState.[thread]
                None
                (ReturnValueDisposition.ResumeAsyncContexts step)
                ExceptionEscape.AsyncContextHelper
                state

        match commitment with
        | IlMachineStateExecution.CallCommitment.Committed -> ExecutionResult.stepped (state, WhatWeDid.Executed)
        | IlMachineStateExecution.CallCommitment.Raised ->
            failwith
                $"TODO: calling AsyncHelpers.%s{methodName} for a runtime-async body's contexts raised before it ran; what CoreCLR raises then is not modelled"
        | IlMachineStateExecution.CallCommitment.Aborted fatal -> ExecutionResult.Aborted (state, thread, fatal)

    let private restore
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (contexts : AsyncContexts)
        (step : AsyncContextStep)
        (thread : ThreadId)
        (state : IlMachineState)
        : ExecutionResult
        =
        callHelper
            loggerFactory
            baseClassTypes
            "RestoreContexts"
            [
                CliType.ofBool contexts.Resumed
                CliType.ObjectRef contexts.Thread
                CliType.ObjectRef contexts.ExecutionContext
                CliType.ObjectRef contexts.SynchronizationContext
            ]
            step
            thread
            state

    /// What the active frame of `thread` does before its next instruction, if it is a user-written
    /// runtime-async body that has a call of a context helper to make first: the capture of its
    /// prologue, or the restore of an exception leaving it. `None` if the frame's next step is its
    /// instruction.
    let beforeInstruction
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (thread : ThreadId)
        (state : IlMachineState)
        : ExecutionResult option
        =
        match state.ThreadState.[thread].MethodState.AsyncFrame with
        | Some (AsyncFrame.SavesContexts AsyncContextHandling.AwaitingCapture) ->
            // `CaptureContexts(out thread, out execCtx, out syncCtx)` stores through pointers to the
            // JIT's hidden locals; these cells stand in for them.
            let threadCell, pool =
                NativeReferenceCell.allocate None (IlMachineState.getNativeMemoryPool state)

            let executionContext, pool = NativeReferenceCell.allocate None pool
            let synchronizationContext, pool = NativeReferenceCell.allocate None pool
            let state = IlMachineState.setNativeMemoryPool pool state

            let pointer (cell : NativeReferenceCell) =
                CliType.RuntimePointer (CliRuntimePointer.Managed cell.Pointer)

            callHelper
                loggerFactory
                baseClassTypes
                "CaptureContexts"
                [
                    pointer threadCell
                    pointer executionContext
                    pointer synchronizationContext
                ]
                (AsyncContextStep.AfterCapture (threadCell, executionContext, synchronizationContext))
                thread
                state
            |> Some
        | Some (AsyncFrame.SavesContexts (AsyncContextHandling.RestoringOnUnwind (contexts, _))) ->
            restore loggerFactory baseClassTypes contexts AsyncContextStep.AfterRestoreOnUnwind thread state
            |> Some
        | Some (AsyncFrame.SavesContexts (AsyncContextHandling.Captured _))
        | Some (AsyncFrame.SavesContexts AsyncContextHandling.Restored)
        | Some AsyncFrame.Transparent
        | None -> None

    /// What the active frame of `thread` does at a `ret`, before it returns, if it is a user-written
    /// runtime-async body still to restore its contexts: the call of `RestoreContexts`, after which
    /// the `ret` runs again. `None` if the frame returns now.
    let beforeReturn
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (thread : ThreadId)
        (state : IlMachineState)
        : ExecutionResult option
        =
        let frame = state.ThreadState.[thread].MethodState

        match frame.AsyncFrame with
        | Some (AsyncFrame.SavesContexts (AsyncContextHandling.Captured contexts)) ->
            restore loggerFactory baseClassTypes contexts AsyncContextStep.AfterRestoreOnReturn thread state
            |> Some
        | Some (AsyncFrame.SavesContexts AsyncContextHandling.Restored)
        | Some AsyncFrame.Transparent
        | None -> None
        | Some (AsyncFrame.SavesContexts handling) ->
            failwith $"BUG: %O{frame.ExecutingMethod} reached a ret while its contexts were %A{handling}"

    /// Do what the frame of the user-written runtime-async body that a context helper has just
    /// returned to, which is active, does next.
    let resume
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (step : AsyncContextStep)
        (thread : ThreadId)
        (state : IlMachineState)
        : ExecutionResult
        =
        let frameId = state.ThreadState.[thread].ActiveMethodState
        let frame = state.ThreadState.[thread].MethodState

        let setHandling (handling : AsyncContextHandling) (state : IlMachineState) : IlMachineState =
            IlMachineState.mapFrame thread frameId (MethodState.withAsyncContextHandling handling) state

        match step, frame.AsyncFrame with
        | AsyncContextStep.AfterCapture (threadCell, executionContext, synchronizationContext),
          Some (AsyncFrame.SavesContexts AsyncContextHandling.AwaitingCapture) ->
            let pool = IlMachineState.getNativeMemoryPool state

            let contexts =
                {
                    Thread = NativeReferenceCell.read threadCell pool
                    ExecutionContext = NativeReferenceCell.read executionContext pool
                    SynchronizationContext = NativeReferenceCell.read synchronizationContext pool
                    Resumed = false
                }

            let pool =
                (pool, [ threadCell ; executionContext ; synchronizationContext ])
                ||> List.fold (fun pool cell -> NativeReferenceCell.free cell pool)

            state
            |> IlMachineState.setNativeMemoryPool pool
            |> setHandling (AsyncContextHandling.Captured contexts)
            |> fun state -> ExecutionResult.stepped (state, WhatWeDid.Executed)
        | AsyncContextStep.AfterRestoreOnReturn, Some (AsyncFrame.SavesContexts (AsyncContextHandling.Captured _)) ->
            // The frame's `ret`, whose program counter did not move, runs again and returns.
            state
            |> setHandling AsyncContextHandling.Restored
            |> fun state -> ExecutionResult.stepped (state, WhatWeDid.Executed)
        | AsyncContextStep.AfterRestoreOnUnwind,
          Some (AsyncFrame.SavesContexts (AsyncContextHandling.RestoringOnUnwind (_, unwind))) ->
            let state = setHandling AsyncContextHandling.Restored state

            match
                ExceptionDispatching.resumeUnwindAfterContextRestore loggerFactory baseClassTypes state thread unwind
            with
            | ExceptionDispatchResult.Dispatched state -> ExecutionResult.stepped (state, WhatWeDid.Executed)
            | ExceptionDispatchResult.ExceptionUnhandled (state, exn) ->
                ExecutionResult.UnhandledException (state, thread, exn)
        | step, asyncFrame ->
            failwith
                $"BUG: AsyncHelpers returned to %O{frame.ExecutingMethod} for the step %A{step}, but the frame is %A{asyncFrame}"
