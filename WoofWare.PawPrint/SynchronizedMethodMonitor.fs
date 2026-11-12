namespace WoofWare.PawPrint

open Microsoft.Extensions.Logging

/// <summary>
/// What CoreCLR's JIT adds to a synchronized method (<c>MethodImplAttributes.Synchronized</c>) to
/// hold a monitor for the whole of its body (<c>fgAddSyncMethodEnterExit</c>, jit/flowgraph.cpp):
/// a call of the runtime's enter helper before the body, and a call of its exit helper before each
/// <c>ret</c> and in a fault handler round the whole body. The monitor is that of the method's
/// <c>this</c>, or for a static method of the <c>RuntimeType</c> of its exact declaring type
/// (<c>fgGetCritSectOfStaticMethod</c>). The helpers are <c>SynchronizedMethodHelpers.onRuntime</c>.
/// </summary>
/// <remarks>
/// Each call is a managed call the frame makes in place of an instruction of its own, whose return
/// does the frame's next step (<c>SynchronizedMethodStep</c>), as <c>AsyncContextSaving</c> does for
/// a runtime-async body's contexts. The body's IL is not rewritten, which would move the offsets its
/// stack traces report. Each helper takes the JIT's hidden <c>bool</c> saying whether the monitor
/// is held (<c>SynchronizedMonitor.LockTaken</c>), so a contended enter blocks, and an exit after
/// the body released the monitor itself fails, inside CoreLib exactly as on CoreCLR.
/// </remarks>
[<RequireQualifiedAccess>]
module SynchronizedMethodMonitor =

    let private callHelper
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (methodName : string)
        (monitor : SynchronizedMonitor)
        (step : SynchronizedMethodStep)
        (thread : ThreadId)
        (state : IlMachineState)
        : ExecutionResult
        =
        match
            IlMachineStateExecution.callMonitorHelper loggerFactory baseClassTypes methodName monitor step thread state
        with
        | state, IlMachineStateExecution.CallCommitment.Committed
        | state, IlMachineStateExecution.CallCommitment.Raised -> ExecutionResult.stepped (state, WhatWeDid.Executed)
        | state, IlMachineStateExecution.CallCommitment.Aborted fatal -> ExecutionResult.Aborted (state, thread, fatal)

    let private helpers (baseClassTypes : BaseClassTypes<DumpedAssembly>) : SynchronizedMethodHelpers =
        SynchronizedMethodHelpers.onRuntime (EmulatedRuntime.ofCoreLib baseClassTypes.Corelib)

    let private exit
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (monitor : SynchronizedMonitor)
        (step : SynchronizedMethodStep)
        (thread : ThreadId)
        (state : IlMachineState)
        : ExecutionResult
        =
        callHelper loggerFactory baseClassTypes (helpers baseClassTypes).Exit monitor step thread state

    /// The object whose monitor the active frame of `thread`, which runs a synchronized method,
    /// holds.
    let private lockObject
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (thread : ThreadId)
        (state : IlMachineState)
        : ManagedHeapAddress * IlMachineState
        =
        let frame = state.ThreadState.[thread].MethodState
        let method = frame.ExecutingMethod

        if method.IsStatic then
            let declaringType =
                match
                    AllConcreteTypes.findExistingConcreteType
                        state.TypeSystem.ConcreteTypes
                        method.RequiredDeclaringType.Identity
                        method.DeclaringTypeGenerics
                with
                | Some handle -> handle
                | None ->
                    failwith
                        $"BUG: the synchronized method %O{method} is running, but its declaring type is not registered in AllConcreteTypes"

            IlMachineState.getOrAllocateType
                loggerFactory
                baseClassTypes
                (RuntimeTypeHandleTarget.Closed declaringType)
                state
        else
            match frame.Arguments.[0] with
            | CliType.ObjectRef (Some this) -> this, state
            | CliType.ObjectRef None ->
                failwith
                    $"TODO: the synchronized instance method %O{method} was called on a null `this`; CoreCLR's monitor enter helper raises ArgumentNullException inside the region whose fault handler then calls the exit helper, which PawPrint does not model"
            | other ->
                failwith
                    $"BUG: the synchronized instance method %O{method} has %O{other} for its `this`, which is not an object reference; a synchronized method of a value type is refused when its frame is made"

    /// What the active frame of `thread` does before its next instruction, if it is a synchronized
    /// method's with a call of a monitor helper to make first: the enter of its prologue, or the
    /// exit of an exception leaving it. `None` if the frame's next step is its instruction.
    let beforeInstruction
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (thread : ThreadId)
        (state : IlMachineState)
        : ExecutionResult option
        =
        match state.ThreadState.[thread].MethodState.Synchronized with
        | Some SynchronizedFrame.AwaitingEnter ->
            let lockAddress, state = lockObject loggerFactory baseClassTypes thread state

            let lockTaken, pool =
                NativeFlagCell.allocate (IlMachineState.getNativeMemoryPool state)

            let monitor =
                {
                    Lock = lockAddress
                    LockTaken = lockTaken
                }

            let frameId = state.ThreadState.[thread].ActiveMethodState

            // The enter helper is inside the region the fault handler guards, so from here on an
            // exception leaving the frame calls the exit helper, which does nothing unless the
            // enter took the monitor.
            state
            |> IlMachineState.setNativeMemoryPool pool
            |> IlMachineState.mapFrame
                thread
                frameId
                (MethodState.withSynchronizedFrame (SynchronizedFrame.Holding monitor))
            |> callHelper
                loggerFactory
                baseClassTypes
                (helpers baseClassTypes).Enter
                monitor
                SynchronizedMethodStep.AfterEnter
                thread
            |> Some
        | Some (SynchronizedFrame.ExitingOnUnwind (monitor, _)) ->
            exit loggerFactory baseClassTypes monitor SynchronizedMethodStep.AfterExitOnUnwind thread state
            |> Some
        | Some (SynchronizedFrame.Holding _)
        | Some SynchronizedFrame.Exited
        | None -> None

    /// What the active frame of `thread` does at a `ret`, before it returns, if it is a synchronized
    /// method's still holding its monitor: the call of the exit helper, after which the `ret` runs
    /// again. `None` if the frame returns now.
    let beforeReturn
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (thread : ThreadId)
        (state : IlMachineState)
        : ExecutionResult option
        =
        let frame = state.ThreadState.[thread].MethodState

        match frame.Synchronized with
        | Some (SynchronizedFrame.Holding monitor) ->
            exit loggerFactory baseClassTypes monitor SynchronizedMethodStep.AfterExitOnReturn thread state
            |> Some
        | Some SynchronizedFrame.Exited
        | None -> None
        | Some (SynchronizedFrame.AwaitingEnter as progress)
        | Some (SynchronizedFrame.ExitingOnUnwind _ as progress) ->
            failwith $"BUG: %O{frame.ExecutingMethod} reached a ret while its monitor was %A{progress}"

    /// Do what the synchronized method's frame that a monitor helper has just returned to, which is
    /// active, does next.
    let resume
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (step : SynchronizedMethodStep)
        (thread : ThreadId)
        (state : IlMachineState)
        : ExecutionResult
        =
        let frameId = state.ThreadState.[thread].ActiveMethodState
        let frame = state.ThreadState.[thread].MethodState

        let leave (monitor : SynchronizedMonitor) (state : IlMachineState) : IlMachineState =
            state
            |> IlMachineState.setNativeMemoryPool (
                NativeFlagCell.free monitor.LockTaken (IlMachineState.getNativeMemoryPool state)
            )
            |> IlMachineState.mapFrame thread frameId (MethodState.withSynchronizedFrame SynchronizedFrame.Exited)

        match step, frame.Synchronized with
        | SynchronizedMethodStep.AfterEnter, Some (SynchronizedFrame.Holding _) ->
            ExecutionResult.stepped (state, WhatWeDid.Executed)
        | SynchronizedMethodStep.AfterExitOnReturn, Some (SynchronizedFrame.Holding monitor) ->
            // The frame's `ret`, whose program counter did not move, runs again and returns.
            ExecutionResult.stepped (leave monitor state, WhatWeDid.Executed)
        | SynchronizedMethodStep.AfterExitOnUnwind, Some (SynchronizedFrame.ExitingOnUnwind (monitor, unwind)) ->
            match
                ExceptionDispatching.resumeParkedUnwind loggerFactory baseClassTypes (leave monitor state) thread unwind
            with
            | ExceptionDispatchResult.Dispatched state -> ExecutionResult.stepped (state, WhatWeDid.Executed)
            | ExceptionDispatchResult.ExceptionUnhandled (state, exn) ->
                ExecutionResult.UnhandledException (state, thread, exn)
        | step, progress ->
            failwith
                $"BUG: a monitor helper returned to %O{frame.ExecutingMethod} for the step %A{step}, but its monitor is %A{progress}"
