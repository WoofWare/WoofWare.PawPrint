namespace WoofWare.PawPrint

open System.Collections.Immutable
open Microsoft.Extensions.Logging

/// <summary>
/// How an async version leaves (decision 17 of <c>docs/plans/2026-11-11-runtime-async.md</c>). Its
/// IL is the ordinary variant's, which returns a promise where the async variant returns the
/// promise's result, so CoreCLR's JIT makes each <c>ret</c> that ends no tail shape await the
/// promise through <c>AsyncHelpers.TransparentAwait</c>, as a tail await
/// (<c>impWrapTopOfStackInAwait</c>, importer.cpp). That call, like the call of a tail shape, is the
/// last thing the frame does: once it has completed, by returning or by the frame's resuming, the
/// frame returns its result (<c>AsyncVersionStep.Returning</c>).
/// </summary>
/// <remarks>
/// The IL is not rewritten, which would move the offsets its stack traces report.
/// </remarks>
[<RequireQualifiedAccess>]
module AsyncVersionReturn =

    /// Whether a promise type is a `ValueTask`, and whether it has a result.
    let private promiseShape (promise : ThunkPromise) : bool * bool =
        match promise with
        | ThunkPromise.Task -> false, false
        | ThunkPromise.ValueTask -> true, false
        | ThunkPromise.TaskOf _ -> false, true
        | ThunkPromise.ValueTaskOf _ -> true, true

    /// The overload of `AsyncHelpers.TransparentAwait` that awaits `promise`, as the VM chooses it
    /// (`CEEInfo::getAwaitReturnCall`, jitinterface.cpp).
    let private transparentAwait
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (promise : ThunkPromise)
        : WoofWare.PawPrint.MethodInfo<GenericParamFromMetadata, GenericParamFromMetadata, TypeDefn>
        =
        let purpose = "await the promise an async version returns"

        let helpers =
            CorelibEntryPoint.findCorelibType
                baseClassTypes.Corelib
                "System.Runtime.CompilerServices"
                "AsyncHelpers"
                purpose

        let candidates =
            helpers.Methods
            |> List.filter (fun method ->
                method.Name = "TransparentAwait"
                && method.IsStatic
                && (
                    match method.Signature.ParameterTypes with
                    | [ awaited ] ->
                        promiseShape (fst (TaskReturningThunk.promiseOf (MethodReturnType.Returns awaited))) = promiseShape
                            promise
                    | _ -> false
                )
            )

        match candidates with
        | [ single ] -> single
        | other ->
            failwith
                $"Expected one AsyncHelpers.TransparentAwait in CoreLib awaiting %A{promise}, found %d{List.length other}; PawPrint calls it to %s{purpose}"

    /// What the active frame of `thread` does at a `ret`, before it returns, if it is an async
    /// version: it calls `AsyncHelpers.TransparentAwait` with the promise on its evaluation stack,
    /// standing at the `ret`, and returns that call's result once it has completed. `None` if the
    /// frame is no async version, and returns now.
    let beforeReturn
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (thread : ThreadId)
        (state : IlMachineState)
        : ExecutionResult option
        =
        let frame = state.ThreadState.[thread].MethodState

        match frame.AsyncFrame with
        | None
        | Some (AsyncFrame.Transparent _)
        | Some (AsyncFrame.SavesContexts _) -> None
        | Some (AsyncFrame.AsyncVersion (AsyncVersionStep.Returning _)) ->
            failwith
                $"BUG: %O{frame.ExecutingMethod}, an async version whose last call has completed, reached a ret, where it returns before running another instruction"
        | Some (AsyncFrame.AsyncVersion AsyncVersionStep.Running) ->

        let method = frame.ExecutingMethod
        let facts = MethodInfo.requireMetadata "an async version" method

        let definition =
            match state.LoadedAssembly method.DeclaringAssemblyFullName with
            | Some assembly -> assembly.Methods.[facts.Handle]
            | None ->
                failwith
                    $"BUG: %O{method} is running, but its declaring assembly %s{method.DeclaringAssemblyFullName} is not loaded"

        let promise, _ = TaskReturningThunk.promiseOf definition.Signature.ReturnType

        // `TransparentAwait<T>` is instantiated at the async version's own result type.
        let generics =
            match promise, method.Signature.ReturnType with
            | ThunkPromise.Task, MethodReturnType.Void
            | ThunkPromise.ValueTask, MethodReturnType.Void -> ImmutableArray.Empty
            | ThunkPromise.TaskOf _, MethodReturnType.Returns result
            | ThunkPromise.ValueTaskOf _, MethodReturnType.Returns result -> ImmutableArray.Create result
            | promise, returned ->
                failwith $"BUG: %O{method}, an async version of a method returning %A{promise}, returns %O{returned}"

        let state, helper =
            ExecutionConcretization.concretizeMethodWithAllGenerics
                loggerFactory
                baseClassTypes
                ImmutableArray.Empty
                (transparentAwait baseClassTypes promise)
                generics
                state

        let helper, _ = BindingStop.bound helper

        // The promise on the evaluation stack is the call's argument. The frame stands at its `ret`,
        // where the JIT reports the call.
        match
            IlMachineStateExecution.callMethodWithCommitment
                loggerFactory
                baseClassTypes
                None
                ConstructionState.NotConstructing
                IlMachineStateExecution.CallDispatch.Direct
                false
                false
                IlMachineStateExecution.CallSiteTransition.StaysCooperative
                helper.Generics
                helper
                thread
                state.ThreadState.[thread]
                None
                ReturnValueDisposition.PushToCaller
                ExceptionEscape.Propagate
                (Some OnSuspension.TailAwait)
                state
        with
        | state, IlMachineStateExecution.CallCommitment.Committed
        | state, IlMachineStateExecution.CallCommitment.Raised -> ExecutionResult.stepped (state, WhatWeDid.Executed)
        | state, IlMachineStateExecution.CallCommitment.Aborted fatal -> ExecutionResult.Aborted (state, thread, fatal)
        |> Some

    /// The state from which the active frame of `thread` returns before running another
    /// instruction, if it is an async version whose last call has completed: its evaluation stack
    /// then holds what it returns, the call's result dropped where the frame returns nothing. `None`
    /// if the frame runs its next instruction.
    let returnsNow (thread : ThreadId) (state : IlMachineState) : IlMachineState option =
        let frame = state.ThreadState.[thread].MethodState

        match frame.AsyncFrame with
        | None
        | Some (AsyncFrame.Transparent _)
        | Some (AsyncFrame.SavesContexts _)
        | Some (AsyncFrame.AsyncVersion AsyncVersionStep.Running) -> None
        | Some (AsyncFrame.AsyncVersion (AsyncVersionStep.Returning dropsResult)) ->

        if dropsResult then
            match frame.EvaluationStack.Values with
            | [ _ ] -> Some (IlMachineState.popEvalStack thread state |> snd)
            | other ->
                failwith
                    $"BUG: %O{frame.ExecutingMethod}, an async version returning nothing, holds %O{other} on its evaluation stack, where the call it returns after left one value"
        else
            Some state
