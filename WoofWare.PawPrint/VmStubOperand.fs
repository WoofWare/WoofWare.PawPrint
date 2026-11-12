namespace WoofWare.PawPrint

open System.Collections.Immutable
open Microsoft.Extensions.Logging

/// Resolving an operand of the stub the runtime emits for the executing frame's method
/// (`MetadataOperand.FromStubScope`), relative to that method, as CoreCLR's stub linker resolved
/// it when it emitted the stub.
[<RequireQualifiedAccess>]
module internal VmStubOperand =

    /// The `T` of the `Task<T>` or `ValueTask<T>` `method` returns.
    let private promiseResult
        (state : IlMachineState)
        (method : WoofWare.PawPrint.MethodInfo<ConcreteTypeHandle, ConcreteTypeHandle, ConcreteTypeHandle>)
        : ConcreteTypeHandle
        =
        match method.Signature.ReturnType with
        | MethodReturnType.Returns promise ->
            match AllConcreteTypes.lookup promise state.TypeSystem.ConcreteTypes with
            | Some promise when promise.Generics.Length = 1 -> promise.Generics.[0]
            | other ->
                failwith
                    $"BUG: the stub %O{method} runs names its promise's result type, but the method returns %O{other}, which is not a Task<T> or ValueTask<T>"
        | MethodReturnType.Void ->
            failwith $"BUG: the stub %O{method} runs names its promise's result type, but the method returns void"

    /// Operand `index` of the stub the active frame of `thread` runs.
    let resolve
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (op : UnaryMetadataTokenIlOp)
        (index : int)
        (thread : ThreadId)
        (state : IlMachineState)
        : IlMachineState * ResolvedMetadataOperand
        =
        let frame = state.ThreadState.[thread].MethodState
        let corelib = baseClassTypes.Corelib

        let stub =
            match frame.VmStub with
            | Some stub -> stub
            | None ->
                failwith
                    $"BUG: %O{op} in %O{frame.ExecutingMethod} names operand %d{index} of a stub the runtime emits, but the frame runs no stub"

        // The CoreLib members are named by their tokens in CoreLib, which is exactly the member the
        // stub linker resolved.
        match stub.Operands.[index] with
        | StubOperand.CoreLibField handle ->
            state, ResolvedMetadataOperand.FromMetadata (corelib, MetadataToken.FieldDefinition handle)
        | StubOperand.CoreLibMethod handle ->
            state, ResolvedMetadataOperand.FromMetadata (corelib, MetadataToken.MethodDef handle)
        | StubOperand.CoreLibType handle ->
            state, ResolvedMetadataOperand.FromMetadata (corelib, MetadataToken.TypeDefinition handle)
        | StubOperand.CoreLibMethodAtResultType handle ->
            let state, method =
                ExecutionConcretization.concretizeMethodWithAllGenerics
                    loggerFactory
                    baseClassTypes
                    ImmutableArray.Empty
                    corelib.Methods.[handle]
                    (ImmutableArray.Create (promiseResult state frame.ExecutingMethod))
                    state

            let method, _ = BindingStop.bound method
            state, ResolvedMetadataOperand.StubMethod method
        | StubOperand.AsyncVariantOfStubMethod ->
            let stubMethod = frame.ExecutingMethod

            let facts =
                MethodInfo.requireMetadata "the target of a task-returning thunk" stubMethod

            let declaringAssy =
                state.LoadedAssembly stubMethod.DeclaringAssemblyFullName
                |> Option.defaultWith (fun () ->
                    failwith
                        $"BUG: %O{stubMethod} is executing, but its declaring assembly %s{stubMethod.DeclaringAssemblyFullName} is not loaded"
                )

            match
                RuntimeAsyncMethod.asyncVariantOf
                    loggerFactory
                    state.DotnetRuntimeDirs
                    corelib
                    declaringAssy
                    declaringAssy.Methods.[facts.Handle]
                    state.TypeSystem._LoadedAssemblies
            with
            | assemblies, Ok variant ->
                let state =
                    { state with
                        TypeSystem =
                            { state.TypeSystem with
                                _LoadedAssemblies = assemblies
                            }
                    }

                let state, method =
                    ExecutionConcretization.concretizeMethodWithAllGenerics
                        loggerFactory
                        baseClassTypes
                        stubMethod.DeclaringTypeGenerics
                        variant
                        stubMethod.Generics
                        state

                let method, _ = BindingStop.bound method
                state, ResolvedMetadataOperand.StubMethod method
            | _, Error refusal ->
                failwith
                    $"BUG: %O{stubMethod} runs a task-returning thunk, which calls the method's async variant, but it has none: %A{refusal}"
