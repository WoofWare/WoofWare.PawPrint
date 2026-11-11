namespace WoofWare.PawPrint

open System.Collections.Immutable
open Microsoft.Extensions.Logging

/// <summary>
/// The pieces of suspending a runtime-async frame at an await and resuming it that act on the
/// heap, on native memory or on one frame, as CoreCLR's JIT emits them
/// (<c>AsyncTransformation::CreateSuspension</c> and <c>CreateResumption</c>, jit/async.cpp):
/// making the frame's continuation, giving it its <c>ResumeInfo</c>, reducing the frame to what a
/// suspension keeps, and delivering a result. Which frames suspend, and in what order, is
/// <c>IlMachineStateExecution.leaveBySuspension</c>'s.
/// </summary>
/// <remarks>
/// See decisions 2 to 5 of <c>docs/plans/2026-11-11-runtime-async.md</c>.
/// </remarks>
[<RequireQualifiedAccess>]
module AsyncSuspension =

    /// The continuation a user-written runtime-async body makes when it suspends at an await of a
    /// call returning `awaited`: an `ExecutionContext` slot, which the JIT always gives one, and a
    /// slot for the call's result. PawPrint binds no async variant at an await (decision 6), so
    /// every await is a call of an `AsyncHelpers.Await` overload, which the JIT gives no
    /// continuation context of the frame's own; and a suspension at an await in a `try` region,
    /// which alone gives one an exception slot, is refused (decision 11).
    let layoutAwaiting (awaited : MethodReturnType<ConcreteTypeHandle>) : ContinuationLayout =
        {
            ExecutionContext = true
            ContinuationContext = false
            Exception = false
            Result =
                match awaited with
                | MethodReturnType.Void -> None
                | MethodReturnType.Returns result -> Some result
        }

    let private corelibType
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (typeNamespace : string)
        (typeName : string)
        (state : IlMachineState)
        : IlMachineState * ConcreteTypeHandle
        =
        CorelibEntryPoint.findCorelibType
            baseClassTypes.Corelib
            typeNamespace
            typeName
            "lay out a suspended runtime-async frame's continuation"
        |> LoadedTypeInfo.typeInfoToTypeDefn' baseClassTypes state.TypeSystem._LoadedAssemblies
        |> IlMachineState.concretizeType
            loggerFactory
            baseClassTypes
            state
            baseClassTypes.Corelib.DefinitionFullName
            ImmutableArray.Empty
            ImmutableArray.Empty

    /// The type each slot holds, as CoreLib reads it.
    let private slotType
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (layout : ContinuationLayout)
        (slot : ContinuationSlot)
        (state : IlMachineState)
        : IlMachineState * ConcreteTypeHandle
        =
        match slot with
        | ContinuationSlot.ExecutionContext ->
            corelibType loggerFactory baseClassTypes "System.Threading" "ExecutionContext" state
        | ContinuationSlot.ContinuationContext -> corelibType loggerFactory baseClassTypes "System" "Object" state
        | ContinuationSlot.Exception -> corelibType loggerFactory baseClassTypes "System" "Exception" state
        | ContinuationSlot.Result ->
            match layout.Result with
            | Some result -> state, result
            | None -> failwith $"BUG: asked for the result slot's type of %O{layout}, which has no result"

    /// Allocate a zeroed continuation of type `layout`: CoreLib's `Continuation`'s own fields, then
    /// a field for each slot, at the offset its `Flags` will encode.
    let allocateContinuation
        (loggerFactory : ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (layout : ContinuationLayout)
        (state : IlMachineState)
        : ManagedHeapAddress * IlMachineState
        =
        let handle = ConcreteTypeHandle.Continuation layout

        let state, parent =
            match IlMachineState.resolveBaseConcreteType loggerFactory baseClassTypes state handle with
            | state, Some parent -> state, parent
            | _, None ->
                failwith $"BUG: %O{layout} has no base type, though every continuation type derives from Continuation"

        let state, inherited =
            IlMachineState.collectInstanceFieldChain loggerFactory baseClassTypes state parent

        let state, ownFields =
            ((state, []), ContinuationSlots.present layout)
            ||> List.fold (fun (state, fields) slot ->
                let state, ty = slotType loggerFactory baseClassTypes layout slot state
                let zero, state = IlMachineState.cliTypeZeroOfHandle state baseClassTypes ty

                let field : CliField =
                    {
                        Id = FieldId.ContinuationSlot slot
                        Name = (FieldId.ContinuationSlot slot).Name
                        Contents = zero
                        Offset = None
                        Type = ty
                        MarshallingDescriptor = None
                    }

                state, field :: fields
            )

        // The JIT lays the slots out in this order, pointer-aligned, after `Continuation`'s own
        // fields (`ContinuationLayoutBuilder::Create`); auto layout over these fields places them so,
        // which the check below holds it to.
        let level : TypeLayoutLevel =
            {
                Declared = handle
                Facts =
                    {
                        IsValueType = false
                        IsEnum = false
                        NominalAlignment = None
                        LayoutKind = TypeLayoutKind.Auto
                        Layout = Layout.Default
                        CharSet = System.Runtime.InteropServices.CharSet.Ansi
                    }
                OwnFields = List.rev ownFields
                IsTrivialParent = false
            }

        let contents =
            CliValueType.OfFieldChain baseClassTypes state.TypeSystem.ConcreteTypes handle (inherited @ [ level ])

        for slot in ContinuationSlots.present layout do
            let offset, _ =
                CliValueType.GetFieldLayoutById (FieldId.ContinuationSlot slot) contents

            match ContinuationSlots.offsetOf slot layout with
            | Some expected when expected = offset -> ()
            | expected ->
                failwith
                    $"BUG: %O{layout} has its %O{slot} slot at byte %d{offset}, but its Flags will tell CoreLib it is at %O{expected}"

        IlMachineState.allocateManagedObject handle contents state

    /// Overwrite the field `name` of the object at `address`, coercing `value` to the field's type
    /// as a `stfld` would.
    let private setField
        (address : ManagedHeapAddress)
        (name : string)
        (value : EvalStackValue)
        (state : IlMachineState)
        : IlMachineState
        =
        let field = FieldId.named name

        let existing =
            ManagedHeap.get address state.ManagedHeap
            |> AllocatedNonArrayObject.DereferenceFieldById field

        IlMachineState.setInstanceFieldById address field (EvalStackValue.toCliTypeCoerced existing value) state

    /// Allocate the `ResumeInfo` of a continuation for a frame of `method` suspending at its await
    /// at `callSite`: `Resume` is `FunctionPointerTarget.AsyncResume`, and `DiagnosticIP` names the
    /// await. It lives until the frame resumes (`freeResumeInfo`).
    let allocateResumeInfo
        (method : MethodInfo<ConcreteTypeHandle, ConcreteTypeHandle, ConcreteTypeHandle>)
        (callSite : int)
        (state : IlMachineState)
        : NativeMemoryBlockId * IlMachineState
        =
        let pointerSize = ContinuationSlots.PointerSize
        let pool = IlMachineState.getNativeMemoryPool state

        let block, pool =
            NativeMemoryPool.allocate
                MemoryBlockInitialization.ZeroInitialized
                (NativeBlockAlignment.ofBytes pointerSize)
                (2 * pointerSize)
                pool

        let codeAddress (target : FunctionPointerTarget) =
            CliType.Numeric (CliNumericType.NativeInt (NativeIntSource.FunctionPointer target))

        let pool =
            pool
            |> NativeMemoryPool.writeCell block 0 (codeAddress FunctionPointerTarget.AsyncResume)
            |> NativeMemoryPool.writeCell
                block
                pointerSize
                (codeAddress (FunctionPointerTarget.SuspensionPoint (method, callSite)))

        block, IlMachineState.setNativeMemoryPool pool state

    /// Release the `ResumeInfo` of a continuation whose frame has resumed.
    let freeResumeInfo (block : NativeMemoryBlockId) (state : IlMachineState) : IlMachineState =
        IlMachineState.freeNativeMemory block state

    /// Fill in a fresh continuation's own fields: `ResumeInfo`, `Flags` saying where its slots lie,
    /// and `State`, which only the method's own resumption reads, and which PawPrint sets to the
    /// IL offset of the await, as no managed code reads it.
    let initialiseContinuation
        (continuation : ManagedHeapAddress)
        (layout : ContinuationLayout)
        (resumeInfo : NativeMemoryBlockId)
        (callSite : int)
        (state : IlMachineState)
        : IlMachineState
        =
        let resumeInfoPointer =
            ManagedPointerSource.Byref
                {
                    Root = ByrefRoot.NativeMemoryByte (resumeInfo, 0)
                    Projections = []
                }

        let int32 (value : int) =
            EvalStackValue.ofCliType (CliType.Numeric (CliNumericType.Int32 value))

        state
        |> setField continuation "ResumeInfo" (EvalStackValue.ManagedPointer resumeInfoPointer)
        |> setField continuation "Flags" (int32 (ContinuationSlots.slotIndexFlags layout))
        |> setField continuation "State" (int32 callSite)

    /// Make `next` the continuation after `continuation` in its chain: the callee's continuation's
    /// `Next` is its awaiting caller's (`AllocContinuation`).
    let link (continuation : ManagedHeapAddress) (next : ManagedHeapAddress) (state : IlMachineState) : IlMachineState =
        setField continuation "Next" (EvalStackValue.ObjectRef next) state

    /// A pointer to `slot` of `continuation`, for CoreLib's suspension helpers, which write it.
    let slotPointer (continuation : ManagedHeapAddress) (slot : ContinuationSlot) : ManagedPointerSource =
        ManagedPointerSource.Byref
            {
                Root = ByrefRoot.HeapObjectField (continuation, FieldId.ContinuationSlot slot)
                Projections = []
            }

    /// The value in the result slot of `continuation`, which the continuation before it wrote when
    /// the call its frame awaits completed; `None` if the call returns nothing.
    let readResult
        (continuation : ManagedHeapAddress)
        (layout : ContinuationLayout)
        (state : IlMachineState)
        : CliType option
        =
        match layout.Result with
        | None -> None
        | Some _ ->
            ManagedHeap.get continuation state.ManagedHeap
            |> AllocatedNonArrayObject.DereferenceFieldById (FieldId.ContinuationSlot ContinuationSlot.Result)
            |> Some

    /// The IL offset range of every `try`, filter and handler of `method`'s body that `callSite`
    /// lies in, as `(start, end)` with `end` exclusive.
    let protectedRegionsAround (method : MethodInfo<_, _, _>) (callSite : int) : (int * int) list =
        match MethodInfo.tryIlBody method with
        | None -> []
        | Some body ->
            body.ExceptionRegions
            |> Seq.collect (fun region ->
                let offsets, filter =
                    match region with
                    | ExceptionRegion.Filter (filterOffset, offsets) ->
                        offsets, [ filterOffset, offsets.HandlerOffset ]
                    | ExceptionRegion.Catch (_, offsets)
                    | ExceptionRegion.Finally offsets
                    | ExceptionRegion.Fault offsets -> offsets, []

                [
                    offsets.TryOffset, offsets.TryOffset + offsets.TryLength
                    offsets.HandlerOffset, offsets.HandlerOffset + offsets.HandlerLength
                ]
                @ filter
            )
            |> Seq.filter (fun (start, finish) -> start <= callSite && callSite < finish)
            |> Seq.toList

    /// `values`, typed by `types`, with each value whose type `zeroed` names replaced by its zero.
    let zeroValues
        (zeroed : ConcreteTypeHandle -> CliType option)
        (types : ConcreteTypeHandle list)
        (values : ImmutableArray<CliType>)
        : ImmutableArray<CliType>
        =
        if types.Length <> values.Length then
            failwith $"BUG: %d{values.Length} value(s) typed by %d{types.Length} type(s)"

        (values, List.indexed types)
        ||> List.fold (fun values (index, ty) ->
            match zeroed ty with
            | Some zero -> values.SetItem (index, zero)
            | None -> values
        )

    /// `stack`, with each pointer into a stack frame made null and each value of a type `zeroed`
    /// names replaced by its zero.
    ///
    /// The evaluation stack records no IL types, and a provenance-tracked unmanaged pointer is a
    /// `ManagedPointer` on it as a byref is. The JIT zeroes a byref and keeps an unmanaged pointer
    /// as the number it is, so a pointer into storage that outlives the frame (the heap, native
    /// memory, a static) is kept: kept, a byref is one the method cannot use after the await, and an
    /// unmanaged pointer is right. A pointer into a stack frame, which a suspension ends, is made
    /// null either way.
    let zeroStack (zeroed : ConcreteTypeHandle -> CliType option) (stack : EvalStack) : EvalStack =
        {
            Values =
                stack.Values
                |> List.map (fun value ->
                    match value with
                    | EvalStackValue.ManagedPointer (ManagedPointerSource.Byref addressed) ->
                        match addressed.Root with
                        | ByrefRoot.LocalVariable _
                        | ByrefRoot.Argument _
                        | ByrefRoot.StackMemoryByte _ -> EvalStackValue.ManagedPointer ManagedPointerSource.Null
                        | ByrefRoot.NativeMemoryByte _
                        | ByrefRoot.HeapValue _
                        | ByrefRoot.HeapObjectField _
                        | ByrefRoot.ArrayElement _
                        | ByrefRoot.PeByteRange _
                        | ByrefRoot.StaticField _
                        | ByrefRoot.StringCharAt _
                        | ByrefRoot.ExposedClassObject _ -> value
                    | EvalStackValue.ManagedPointer ManagedPointerSource.Null
                    | EvalStackValue.ManagedPointer (ManagedPointerSource.NativeIntPlaceholder _) -> value
                    | EvalStackValue.UserDefinedValueType contents ->
                        match zeroed contents.Declared with
                        | Some zero -> EvalStackValue.ofCliType zero
                        | None -> value
                    | EvalStackValue.Int32 _
                    | EvalStackValue.Int64 _
                    | EvalStackValue.NativeInt _
                    | EvalStackValue.Float _
                    | EvalStackValue.NullObjectRef
                    | EvalStackValue.ObjectRef _ -> value
                )
        }

    /// `frame` as a suspension keeps it, as CoreCLR's JIT and interpreter keep a suspended frame:
    /// every byref, and every value of a byref-like type, in its locals and arguments zeroed, every
    /// pointer into a stack frame on its evaluation stack too (`zeroStack`), and its `stackalloc`
    /// memory dropped, so a pointer into either that the frame
    /// kept fails when used rather than reaching storage that no longer exists.
    let suspendedFrame
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (frame : MethodState)
        (state : IlMachineState)
        : MethodState * IlMachineState
        =
        let method = frame.ExecutingMethod

        let receiver =
            if method.IsStatic then
                []
            else
                let declaring =
                    AllConcreteTypes.findExistingConcreteType
                        state.TypeSystem.ConcreteTypes
                        method.RequiredDeclaringType.Identity
                        method.DeclaringTypeGenerics
                    |> Option.defaultWith (fun () ->
                        failwith $"BUG: the declaring type of %O{method}, which is suspending, is not concretised"
                    )

                let isValueType =
                    AllConcreteTypes.tryIsValueType
                        baseClassTypes
                        state.TypeSystem._LoadedAssemblies
                        state.TypeSystem.ConcreteTypes
                        declaring
                    |> Option.defaultValue false

                // A value type's instance method takes `this` by reference.
                [
                    if isValueType then
                        ConcreteTypeHandle.Byref declaring
                    else
                        declaring
                ]

        let argumentTypes = receiver @ method.Signature.ParameterTypes
        let localTypes = List.ofSeq frame.LocalVariableTypes

        let stackTypes =
            frame.EvaluationStack.Values
            |> List.choose (fun value ->
                match value with
                | EvalStackValue.UserDefinedValueType contents -> Some contents.Declared
                | _ -> None
            )

        let isZeroed (ty : ConcreteTypeHandle) : bool =
            match ty with
            | ConcreteTypeHandle.Byref _ -> true
            | ConcreteTypeHandle.Concrete _ ->
                AllConcreteTypes.tryTypeInfo state.TypeSystem._LoadedAssemblies state.TypeSystem.ConcreteTypes ty
                |> Option.exists (fun (_, typeInfo) ->
                    LoadedTypeInfo.isByRefLike baseClassTypes state.TypeSystem._LoadedAssemblies typeInfo
                )
            | ConcreteTypeHandle.Pointer _
            | ConcreteTypeHandle.FunctionPointer _
            | ConcreteTypeHandle.OneDimArrayZero _
            | ConcreteTypeHandle.Array _
            | ConcreteTypeHandle.Continuation _ -> false

        let state, zeros =
            ((state, Map.empty), argumentTypes @ localTypes @ stackTypes |> List.distinct |> List.filter isZeroed)
            ||> List.fold (fun (state, zeros) ty ->
                let zero, state = IlMachineState.cliTypeZeroOfHandle state baseClassTypes ty
                state, zeros |> Map.add ty zero
            )

        let zeroed (ty : ConcreteTypeHandle) : CliType option = zeros |> Map.tryFind ty

        { frame with
            LocalVariables = zeroValues zeroed localTypes frame.LocalVariables
            Arguments = zeroValues zeroed argumentTypes frame.Arguments
            EvaluationStack = zeroStack zeroed frame.EvaluationStack
            StackMemoryPool = StackMemoryPool.empty
        },
        state

    /// Write `returned`, the value a resumed frame returned, through `resultLocation`, the result
    /// slot of the continuation after the frame's (or the task's own result), as the VM's resumption
    /// stub does (`stobj`); a frame returning nothing writes nothing.
    let deliverResult
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (resultLocation : ManagedPointerSource)
        (returned : (EvalStackValue * ConcreteTypeHandle) option)
        (state : IlMachineState)
        : IlMachineState
        =
        match returned with
        | None -> state
        | Some (value, ty) ->
            let zero, state = IlMachineState.cliTypeZeroOfHandle state baseClassTypes ty
            let coerced = EvalStackValue.toCliTypeCoerced zero value

            match ManagedPointerSource.reinterpretAs ty resultLocation with
            | ManagedPointerSource.Byref addressed ->
                match addressed.Root with
                | ByrefRoot.StackMemoryByte _
                | ByrefRoot.NativeMemoryByte _ ->
                    IlMachineState.writeManagedByrefBytesOrTypedCell baseClassTypes state addressed coerced
                | _ -> IlMachineState.writeManagedByrefWithBase baseClassTypes state addressed coerced
            | ManagedPointerSource.Null
            | ManagedPointerSource.NativeIntPlaceholder _ ->
                failwith
                    $"BUG: a resumed runtime-async frame returned %O{value}, but CoreLib's dispatcher gave it the result location %O{resultLocation}, which addresses no storage"
