namespace WoofWare.PawPrint.Test

open System.Collections.Immutable
open System.IO
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// The pieces of suspending a .NET 11 runtime-async frame that the guests in `sourcesPure`
/// (`RuntimeAsyncResumesWith*.cs`) reach only through their effects: where a continuation's slots
/// lie against what its `Flags` tell CoreLib, what a suspended frame keeps of its byrefs, and that
/// each suspended frame resumes exactly once (decisions 2 and 3 of
/// `docs/plans/2026-11-11-runtime-async.md`).
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestRuntimeAsyncSuspension =

    /// .NET 11's CoreLib, whose `Continuation` the layout must agree with, whichever runtime is under
    /// test: .NET 10's `Continuation` keeps its data in arrays instead.
    let private corelibPath () : string =
        FrameworkUnderTest.requireCoreLibPathOf EmulatedRuntime.Net11

    let private corelib : Lazy<DumpedAssembly> =
        lazy
            (let _, loggerFactory = LoggerFactory.makeTest ()
             Assembly.readFile loggerFactory (corelibPath ()))

    let private bct () : BaseClassTypes<DumpedAssembly> = BaseClassTypes.ofCorelib corelib.Value

    let private initialState () : IlMachineState =
        let corelib = corelib.Value
        let bct = bct ()
        let _, loggerFactory = LoggerFactory.makeTest ()
        let state = IlMachineState.initial loggerFactory ImmutableArray.Empty corelib

        { state with
            TypeSystem =
                { state.TypeSystem with
                    ConcreteTypes =
                        Corelib.concretizeAll (LoadedAssemblies.ofAssemblies [ corelib ]) bct AllConcreteTypes.Empty
                }
        }

    /// The value of the constant field `fieldName` of CoreLib's `typeName` in
    /// `System.Runtime.CompilerServices`, read from the image rather than transcribed.
    let private corelibConstant (typeName : string) (fieldName : string) : int =
        use stream = File.OpenRead (corelibPath ())
        use pe = new PEReader (stream)
        let reader = pe.GetMetadataReader ()

        let ty =
            reader.TypeDefinitions
            |> Seq.map reader.GetTypeDefinition
            |> Seq.filter (fun ty ->
                reader.GetString ty.Name = typeName
                && reader.GetString ty.Namespace = "System.Runtime.CompilerServices"
            )
            |> Seq.exactlyOne

        let field =
            ty.GetFields ()
            |> Seq.map reader.GetFieldDefinition
            |> Seq.filter (fun field -> reader.GetString field.Name = fieldName)
            |> Seq.exactlyOne

        let constant = reader.GetConstant (field.GetDefaultValue ())
        reader.GetBlobReader(constant.Value).ReadInt32 ()

    let private concretizeCorelibType
        (state : IlMachineState)
        (ns : string)
        (name : string)
        : IlMachineState * ConcreteTypeHandle
        =
        let _, loggerFactory = LoggerFactory.makeTest ()
        let bct = bct ()

        CorelibEntryPoint.findCorelibType corelib.Value ns name "test"
        |> LoadedTypeInfo.typeInfoToTypeDefn' bct state.TypeSystem._LoadedAssemblies
        |> IlMachineState.concretizeType
            loggerFactory
            bct
            state
            corelib.Value.DefinitionFullName
            ImmutableArray.Empty
            ImmutableArray.Empty

    /// A result type a continuation's slot might hold, by name in CoreLib.
    let private resultTypes : (string * string) list =
        [
            "System", "Int32"
            "System", "Int64"
            "System", "Byte"
            "System", "Boolean"
            "System", "Object"
            "System", "String"
            "System", "Guid"
            "System", "Decimal"
            // A struct holding a reference.
            "System.Threading", "CancellationToken"
            "System.Threading.Tasks", "VoidTaskResult"
        ]

    [<Test>]
    let ``Each slot of a continuation lies where its Flags tell CoreLib's accessors to look`` () : unit =
        let pointerSize = corelibConstant "Continuation" "PointerSize"
        let dataOffset = corelibConstant "Continuation" "DataOffset"

        // `(firstBit, numBits)` of each slot's index in `Flags`, as `ContinuationFlags` declares them.
        let field (prefix : string) =
            corelibConstant "ContinuationFlags" (prefix + "IndexFirstBit"),
            corelibConstant "ContinuationFlags" (prefix + "IndexNumBits")

        let fields =
            [
                ContinuationSlot.ExecutionContext, field "ExecutionContext"
                ContinuationSlot.ContinuationContext, field "ContinuationContext"
                ContinuationSlot.Exception, field "Exception"
                ContinuationSlot.Result, field "Result"
            ]

        ContinuationSlots.DataOffset |> shouldEqual dataOffset
        ContinuationSlots.PointerSize |> shouldEqual pointerSize

        let gen =
            gen {
                let! executionContext = Gen.elements [ true ; false ]
                let! continuationContext = Gen.elements [ true ; false ]
                let! exn = Gen.elements [ true ; false ]
                let! result = Gen.elements (None :: (resultTypes |> List.map Some))
                return executionContext, continuationContext, exn, result
            }

        let property (executionContext : bool, continuationContext : bool, exn : bool, result) : unit =
            let state = initialState ()

            let state, result =
                match result with
                | None -> state, None
                | Some (ns, name) ->
                    let state, handle = concretizeCorelibType state ns name
                    state, Some handle

            let layout : ContinuationLayout =
                {
                    ExecutionContext = executionContext
                    ContinuationContext = continuationContext
                    Exception = exn
                    Result = result
                }

            let _, loggerFactory = LoggerFactory.makeTest ()

            let address, state =
                AsyncSuspension.allocateContinuation loggerFactory (bct ()) layout state

            let contents = (ManagedHeap.get address state.ManagedHeap).Contents
            let flags = ContinuationSlots.slotIndexFlags layout

            for slot, (firstBit, numBits) in fields do
                // `Continuation.GetResultStorageOrNull` and its siblings.
                let index = (uint32 flags >>> firstBit) &&& ((1u <<< numBits) - 1u)

                let present =
                    match slot with
                    | ContinuationSlot.ExecutionContext -> executionContext
                    | ContinuationSlot.ContinuationContext -> continuationContext
                    | ContinuationSlot.Exception -> exn
                    | ContinuationSlot.Result -> result.IsSome

                if not present then
                    index |> shouldEqual 0u
                else
                    let coreLibOffset = (dataOffset - pointerSize) + int index * pointerSize

                    let offset, size =
                        CliValueType.GetFieldLayoutById (FieldId.ContinuationSlot slot) contents

                    offset |> shouldEqual coreLibOffset
                    // Every slot is pointer-aligned, and lies past `Continuation`'s own fields.
                    offset % pointerSize |> shouldEqual 0
                    (offset >= dataOffset) |> shouldEqual true
                    (size > 0) |> shouldEqual true

            // No two slots overlap.
            let extents =
                ContinuationSlots.present layout
                |> List.map (fun slot -> CliValueType.GetFieldLayoutById (FieldId.ContinuationSlot slot) contents)
                |> List.sortBy fst

            for (offset, size), (next, _) in List.pairwise extents do
                (offset + size <= next) |> shouldEqual true

        Check.One (Config.QuickThrowOnFailure.WithMaxTest 60, Prop.forAll (Arb.fromGen gen) property)

    /// A frame of `System.Object::.ctor`, given an IL body of one `ret`, on which a test sets whatever
    /// locals and evaluation stack it needs.
    let private frame (state : IlMachineState) : IlMachineState * MethodState =
        let _, loggerFactory = LoggerFactory.makeTest ()
        let bct = bct ()

        let objectConstructor =
            bct.Object.Methods
            |> List.find (fun method -> method.Name = ".ctor" && MethodInfo.arity method = 0)

        let state, signature =
            IlMachineState.concretizeMethodSignature
                loggerFactory
                bct
                state
                corelib.Value.DefinitionFullName
                ImmutableArray.Empty
                ImmutableArray.Empty
                objectConstructor.Signature

        let ops : (IlOp * int) list = [ IlOp.Nullary NullaryIlOp.Ret, 0 ]

        let instructions : MethodInstructions =
            {
                Instructions = ops
                Locations = ops |> List.map (fun (op, offset) -> offset, op) |> Map.ofList
                LocalsInit = false
                LocalVars = None
                ExceptionRegions = ImmutableArray.Empty
            }

        let method =
            objectConstructor
            |> MethodInfo.mapTypeGenerics (fun _ -> failwith "System.Object::.ctor is not type-generic")
            |> MethodInfo.mapMethodGenerics (fun _ _ -> failwith "System.Object::.ctor is not method-generic")
            |> MethodInfo.setMethodVars (MethodBody.Il instructions) signature

        match
            MethodState.Empty
                state.TypeSystem.ConcreteTypes
                bct
                state.TypeSystem._LoadedAssemblies
                corelib.Value
                method
                ImmutableArray.Empty
                ImmutableArray.Empty
                (ImmutableArray.Create (CliType.ObjectRef None))
                None
        with
        | Ok frame -> state, frame
        | Error missing -> failwith $"unexpected missing assemblies making a test frame: %O{missing}"

    /// What a local of a test frame holds.
    [<RequireQualifiedAccess>]
    type private LocalKind =
        | Int32
        | String
        /// A byref into a stack frame, which dangles once the frame suspends.
        | Byref
        /// An unmanaged pointer, which the JIT keeps as the number it is.
        | Pointer
        /// A `Span<int>` over a stack frame.
        | Span

    [<Test>]
    let ``A suspended frame keeps no byref, and nothing else changes`` () : unit =
        let thread = ThreadId.ThreadId 0
        let frameId = FrameId.FrameId 0

        let stackByref : ManagedPointerSource =
            ManagedPointerSource.Byref
                {
                    Root = ByrefRoot.LocalVariable (thread, frameId, 0us)
                    Projections = []
                }

        let gen =
            gen {
                let! locals =
                    Gen.listOf (
                        Gen.elements
                            [
                                LocalKind.Int32
                                LocalKind.String
                                LocalKind.Byref
                                LocalKind.Pointer
                                LocalKind.Span
                            ]
                    )

                let! stackByrefs = Gen.listOf (Gen.elements [ true ; false ])
                return locals, stackByrefs
            }

        let property (locals : LocalKind list, stackByrefs : bool list) : unit =
            let state = initialState ()
            let bct = bct ()

            let int32 =
                AllConcreteTypes.getRequiredNonGenericHandle state.TypeSystem.ConcreteTypes bct.Int32

            let string =
                AllConcreteTypes.getRequiredNonGenericHandle state.TypeSystem.ConcreteTypes bct.String

            let state, span =
                let _, loggerFactory = LoggerFactory.makeTest ()

                CorelibEntryPoint.findCorelibType corelib.Value "System" "Span`1" "test"
                |> LoadedTypeInfo.typeInfoToTypeDefn' bct state.TypeSystem._LoadedAssemblies
                |> fun typical ->
                    match typical with
                    | TypeDefn.GenericInstantiation (definition, _) ->
                        TypeDefn.GenericInstantiation (
                            definition,
                            ImmutableArray.Create (TypeDefn.PrimitiveType PrimitiveType.Int32)
                        )
                    | other -> failwith $"Span`1's typical instantiation is %O{other}"
                |> IlMachineState.concretizeType
                    loggerFactory
                    bct
                    state
                    corelib.Value.DefinitionFullName
                    ImmutableArray.Empty
                    ImmutableArray.Empty

            let spanZero, state = IlMachineState.cliTypeZeroOfHandle state bct span

            let spanOverStack =
                match spanZero with
                | CliType.ValueType contents ->
                    CliValueType.WithFieldSet
                        "_reference"
                        (CliType.RuntimePointer (CliRuntimePointer.Managed stackByref))
                        contents
                    |> CliType.ValueType
                | other -> failwith $"Span<int>'s zero is %O{other}"

            let nativeBlock, state =
                IlMachineState.allocateNativeMemory
                    MemoryBlockInitialization.ZeroInitialized
                    (NativeBlockAlignment.ofBytes 8)
                    8
                    state

            let typed (kind : LocalKind) : ConcreteTypeHandle * CliType * CliType =
                match kind with
                | LocalKind.Int32 ->
                    int32, CliType.Numeric (CliNumericType.Int32 7), CliType.Numeric (CliNumericType.Int32 7)
                | LocalKind.String -> string, CliType.ObjectRef None, CliType.ObjectRef None
                | LocalKind.Byref ->
                    ConcreteTypeHandle.Byref int32,
                    CliType.RuntimePointer (CliRuntimePointer.Managed stackByref),
                    CliType.RuntimePointer (CliRuntimePointer.Managed ManagedPointerSource.Null)
                | LocalKind.Pointer ->
                    let pointer = CliType.RuntimePointer (CliRuntimePointer.Managed nativeBlock)
                    ConcreteTypeHandle.Pointer int32, pointer, pointer
                | LocalKind.Span -> span, spanOverStack, spanZero

            let typedLocals = locals |> List.map typed

            let state, original = frame state

            let original =
                { original with
                    LocalVariableTypes = typedLocals |> List.map (fun (ty, _, _) -> ty) |> ImmutableArray.CreateRange
                    LocalVariables =
                        typedLocals
                        |> List.map (fun (_, value, _) -> value)
                        |> ImmutableArray.CreateRange
                    EvaluationStack =
                        {
                            Values =
                                stackByrefs
                                |> List.map (fun byref ->
                                    if byref then
                                        EvalStackValue.ManagedPointer stackByref
                                    else
                                        EvalStackValue.Int32 (Int32Source.Verbatim 3)
                                )
                        }
                }

            let suspended, _ = AsyncSuspension.suspendedFrame bct original state

            suspended.LocalVariables
            |> List.ofSeq
            |> shouldEqual (typedLocals |> List.map (fun (_, _, expected) -> expected))

            suspended.EvaluationStack.Values
            |> shouldEqual (
                stackByrefs
                |> List.map (fun byref ->
                    if byref then
                        EvalStackValue.ManagedPointer ManagedPointerSource.Null
                    else
                        EvalStackValue.Int32 (Int32Source.Verbatim 3)
                )
            )

            suspended.Arguments |> shouldEqual original.Arguments
            suspended.IlOpIndex |> shouldEqual original.IlOpIndex

        Check.One (Config.QuickThrowOnFailure.WithMaxTest 40, Prop.forAll (Arb.fromGen gen) property)

    /// One step of a model run over `SuspendedFrames`: suspend under, or resume, one of a few
    /// continuations.
    [<RequireQualifiedAccess>]
    type private MapOperation =
        | Suspend of continuation : int
        | Resume of continuation : int

    [<Test>]
    let ``Each suspended frame resumes exactly once`` () : unit =
        let gen =
            Gen.listOf (
                Gen.oneof
                    [
                        Gen.choose (1, 4) |> Gen.map MapOperation.Suspend
                        Gen.choose (1, 4) |> Gen.map MapOperation.Resume
                    ]
            )

        let state = initialState ()
        let state, template = frame state

        let entry (continuation : int) : SuspendedFrame =
            {
                Frame =
                    { template with
                        _IlOpIndex = continuation
                    }
                Layout =
                    {
                        ExecutionContext = true
                        ContinuationContext = false
                        Exception = false
                        Result = None
                    }
                ResumeInfo = NativeMemoryBlockId.NativeMemoryBlockId continuation
            }

        ignore<IlMachineState> state

        let property (operations : MapOperation list) : unit =
            ((SuspendedFrames.empty, Set.empty), operations)
            ||> List.fold (fun (frames, model) operation ->
                match operation with
                | MapOperation.Suspend continuation ->
                    let key = ManagedHeapAddress.ManagedHeapAddress continuation

                    if model.Contains continuation then
                        Assert.Throws<System.Exception> (fun () ->
                            SuspendedFrames.suspend key (entry continuation) frames |> ignore
                        )
                        |> ignore<System.Exception>

                        frames, model
                    else
                        SuspendedFrames.suspend key (entry continuation) frames, model.Add continuation
                | MapOperation.Resume continuation ->
                    let key = ManagedHeapAddress.ManagedHeapAddress continuation

                    if model.Contains continuation then
                        let resumed, frames = SuspendedFrames.resume key frames
                        resumed.Frame.IlOpIndex |> shouldEqual continuation
                        frames, model.Remove continuation
                    else
                        Assert.Throws<System.Exception> (fun () -> SuspendedFrames.resume key frames |> ignore)
                        |> ignore<System.Exception>

                        frames, model
            )
            |> fun (frames, model) ->
                SuspendedFrames.all frames
                |> Map.keys
                |> Seq.map (fun (ManagedHeapAddress.ManagedHeapAddress key) -> key)
                |> Set.ofSeq
                |> shouldEqual model

        Check.One (Config.QuickThrowOnFailure.WithMaxTest 300, Prop.forAll (Arb.fromGen gen) property)
