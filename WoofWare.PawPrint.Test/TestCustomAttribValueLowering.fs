namespace WoofWare.PawPrint.Test

open System.Collections.Generic
open System.Collections.Immutable
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestCustomAttribValueLowering =

    // The factory is intentionally undisposed: the returned DumpedAssembly.Logger closes
    // over its sinks, and disposing while the assembly is still live would silently drop
    // events.
    let private corelib : DumpedAssembly =
        let corelibPath = HostImage.coreLibPath
        let _, loggerFactory = LoggerFactory.makeTest ()
        Assembly.readFile loggerFactory corelibPath

    let private bct : BaseClassTypes<DumpedAssembly> = BaseClassTypes.ofCorelib corelib

    let private loaded : LoadedAssemblies = LoadedAssemblies.ofAssemblies [ corelib ]

    let private concreteTypes : AllConcreteTypes =
        Corelib.concretizeAll loaded bct AllConcreteTypes.Empty

    /// The plan every `SerString`-valued argument below is decoded and lowered with.
    let private stringPlan : CustomAttribArgPlan =
        CustomAttribArgPlan.Primitive PrimitiveType.String

    let private handleFor (ty : TypeInfo<GenericParamFromMetadata, TypeDefn>) : ConcreteTypeHandle =
        AllConcreteTypes.getRequiredNonGenericHandle concreteTypes ty

    let private byteHandle : ConcreteTypeHandle = handleFor bct.Byte
    let private int32Handle : ConcreteTypeHandle = handleFor bct.Int32
    let private stringHandle : ConcreteTypeHandle = handleFor bct.String

    let private bytePlan : CustomAttribArgPlan =
        CustomAttribArgPlan.SzArray (byteHandle, CustomAttribArgPlan.Primitive PrimitiveType.Byte)

    let private int32ArrayPlan : CustomAttribArgPlan =
        CustomAttribArgPlan.SzArray (int32Handle, CustomAttribArgPlan.Primitive PrimitiveType.Int32)

    let private stringArrayPlan : CustomAttribArgPlan =
        CustomAttribArgPlan.SzArray (stringHandle, CustomAttribArgPlan.Primitive PrimitiveType.String)

    /// `System.Security.SecurityRuleSet` is byte-underlying, so a lowering that used the enum's
    /// underlying *width* wrongly, or its underlying *type* as the array's element type, is
    /// visible: `byte` and `int32` differ in both stride and identity.
    let private securityRuleSetPlan
        (loggerFactory : Microsoft.Extensions.Logging.ILoggerFactory)
        (state : IlMachineState)
        : ConcreteTypeHandle * CustomAttribArgPlan * IlMachineState
        =
        let typeInfo =
            corelib.TryGetTopLevelTypeDef "System.Security" "SecurityRuleSet"
            |> Option.defaultWith (fun () -> failwith "System.Security.SecurityRuleSet not found in corelib")

        let state, handle =
            IlMachineTypeResolution.concretizeType
                loggerFactory
                bct
                state
                corelib.DefinitionFullName
                ImmutableArray.Empty
                ImmutableArray.Empty
                (TypeDefn.FromDefinition (typeInfo.Identity, System.Reflection.Metadata.SignatureTypeKind.ValueType))

        handle, CustomAttribArgPlan.SzArray (handle, CustomAttribArgPlan.Enum EnumUnderlyingType.Byte), state

    /// `toCliType` for a value that names no `System.Type`: nothing is supplied to resolve, and
    /// nothing may be left over.
    let private lower
        (loggerFactory : Microsoft.Extensions.Logging.ILoggerFactory)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (plan : CustomAttribArgPlan)
        (arg : CustomAttribFixedArg)
        (state : IlMachineState)
        : CliType * IlMachineState
        =
        let cli, unconsumed, state =
            CustomAttribValueLowering.toCliType loggerFactory baseClassTypes plan arg [] state

        unconsumed |> shouldEqual []
        cli, state

    let private arrayAddress (result : CliType) : ManagedHeapAddress =
        match result with
        | CliType.ObjectRef (Some addr) -> addr
        | other -> failwithf "expected ObjectRef (Some _), got %A" other

    let private cells (addr : ManagedHeapAddress) (state : IlMachineState) : CliType list =
        let shape = ManagedHeap.getArrayShape addr state.ManagedHeap

        [ 0 .. shape.Length - 1 ]
        |> List.map (fun i -> ManagedHeap.getArrayValue addr i state.ManagedHeap)

    let private freshState () : Microsoft.Extensions.Logging.ILoggerFactory * IlMachineState =
        let _, loggerFactory = LoggerFactory.makeTest ()

        let state =
            let initialState = IlMachineState.initial loggerFactory ImmutableArray.Empty corelib

            { initialState with
                TypeSystem =
                    { initialState.TypeSystem with
                        ConcreteTypes = concreteTypes
                    }
            }

        loggerFactory, state

    [<Test>]
    let ``primitive: Bool false`` () : unit =
        CustomAttribValueLowering.tryToPureCliType (CustomAttribFixedArg.Bool false)
        |> shouldEqual (Ok (CliType.Bool 0uy))

    [<Test>]
    let ``primitive: Bool true`` () : unit =
        CustomAttribValueLowering.tryToPureCliType (CustomAttribFixedArg.Bool true)
        |> shouldEqual (Ok (CliType.Bool 1uy))

    [<Test>]
    let ``primitive: Char ASCII`` () : unit =
        // ASCII 'A' is U+0041, so high byte = 0x00, low byte = 0x41.
        CustomAttribValueLowering.tryToPureCliType (CustomAttribFixedArg.Char 'A')
        |> shouldEqual (Ok (CliType.Char (0x00uy, 0x41uy)))

    [<Test>]
    let ``primitive: Char above BMP byte`` () : unit =
        // U+4E2D ('中') has high byte 0x4E, low byte 0x2D.
        CustomAttribValueLowering.tryToPureCliType (CustomAttribFixedArg.Char '中')
        |> shouldEqual (Ok (CliType.Char (0x4Euy, 0x2Duy)))

    [<Test>]
    let ``primitive: I1 wraps -1 correctly`` () : unit =
        CustomAttribValueLowering.tryToPureCliType (CustomAttribFixedArg.I1 -1y)
        |> shouldEqual (Ok (CliType.Numeric (CliNumericType.Int8 -1y)))

    [<Test>]
    let ``primitive: U1 max`` () : unit =
        CustomAttribValueLowering.tryToPureCliType (CustomAttribFixedArg.U1 255uy)
        |> shouldEqual (Ok (CliType.Numeric (CliNumericType.UInt8 (UInt8Source.Verbatim 255uy))))

    [<Test>]
    let ``primitive: I4 round-trips negative`` () : unit =
        CustomAttribValueLowering.tryToPureCliType (CustomAttribFixedArg.I4 -2147483648)
        |> shouldEqual (Ok (CliType.Numeric (CliNumericType.Int32 -2147483648)))

    [<Test>]
    let ``primitive: U4 max stored as Int32 -1 (two's complement wraparound)`` () : unit =
        CustomAttribValueLowering.tryToPureCliType (CustomAttribFixedArg.U4 4294967295u)
        |> shouldEqual (Ok (CliType.Numeric (CliNumericType.Int32 -1)))

    [<Test>]
    let ``primitive: I8 round-trips`` () : unit =
        CustomAttribValueLowering.tryToPureCliType (CustomAttribFixedArg.I8 -9223372036854775808L)
        |> shouldEqual (Ok (CliType.Numeric (CliNumericType.Int64 (Int64Source.Verbatim -9223372036854775808L))))

    [<Test>]
    let ``primitive: U8 max stored as Int64 -1`` () : unit =
        CustomAttribValueLowering.tryToPureCliType (CustomAttribFixedArg.U8 18446744073709551615UL)
        |> shouldEqual (Ok (CliType.Numeric (CliNumericType.Int64 (Int64Source.Verbatim -1L))))

    [<Test>]
    let ``primitive: R4 round-trips`` () : unit =
        CustomAttribValueLowering.tryToPureCliType (CustomAttribFixedArg.R4 3.14159f)
        |> shouldEqual (Ok (CliType.Numeric (CliNumericType.Float32 3.14159f)))

    [<Test>]
    let ``primitive: R8 round-trips`` () : unit =
        CustomAttribValueLowering.tryToPureCliType (CustomAttribFixedArg.R8 2.718281828459045)
        |> shouldEqual (Ok (CliType.Numeric (CliNumericType.Float64 2.718281828459045)))

    [<Test>]
    let ``String None lowers to null object ref`` () : unit =
        CustomAttribValueLowering.tryToPureCliType (CustomAttribFixedArg.String None)
        |> shouldEqual (Ok (CliType.ObjectRef None))

    [<Test>]
    let ``tryToPureCliType rejects String (Some _)`` () : unit =
        match CustomAttribValueLowering.tryToPureCliType (CustomAttribFixedArg.String (Some "hello")) with
        | Error msg -> msg |> shouldContainText "allocation"
        | Ok r -> failwithf "expected Error, got Ok %A" r

    [<Test>]
    let ``tryToPureCliType rejects Array (None)`` () : unit =
        match CustomAttribValueLowering.tryToPureCliType (CustomAttribFixedArg.Array None) with
        | Error msg -> msg |> shouldContainText "Array"
        | Ok r -> failwithf "expected Error, got Ok %A" r

    [<Test>]
    let ``tryToPureCliType rejects Array (Some _)`` () : unit =
        let arr =
            CustomAttribFixedArg.Array (Some [ CustomAttribFixedArg.I4 1 ; CustomAttribFixedArg.I4 2 ])

        match CustomAttribValueLowering.tryToPureCliType arr with
        | Error msg -> msg |> shouldContainText "Array"
        | Ok r -> failwithf "expected Error, got Ok %A" r

    [<Test>]
    let ``toCliType: String None routes through pure path`` () : unit =
        let loggerFactory, state = freshState ()

        let result, stateAfter =
            lower loggerFactory bct stringPlan (CustomAttribFixedArg.String None) state

        result |> shouldEqual (CliType.ObjectRef None)
        System.Object.ReferenceEquals (stateAfter, state) |> shouldEqual true

    [<Test>]
    let ``toCliType: String (Some _) allocates a managed string with the right contents`` () : unit =
        let loggerFactory, state = freshState ()

        let result, stateAfter =
            lower loggerFactory bct stringPlan (CustomAttribFixedArg.String (Some "hello")) state

        match result with
        | CliType.ObjectRef (Some addr) ->
            ManagedHeap.getStringContents addr stateAfter.ManagedHeap
            |> shouldEqual (Some "hello")
        | other -> failwithf "expected ObjectRef (Some _), got %A" other

    [<Test>]
    let ``toCliType: String (Some "") routes through canonical empty string`` () : unit =
        // CoreCLR's StringObject::NewString(0) returns GetEmptyString(); we mirror that
        // so attribute consumers comparing against `string.Empty` / `ldstr ""` get the
        // expected reference identity. See lower docs.
        let loggerFactory, state = freshState ()

        let result, stateAfter =
            lower loggerFactory bct stringPlan (CustomAttribFixedArg.String (Some "")) state

        let canonicalAddr, _ =
            IlMachineState.internCanonicalEmptyString loggerFactory bct stateAfter

        match result with
        | CliType.ObjectRef (Some addr) ->
            ManagedHeap.getStringContents addr stateAfter.ManagedHeap
            |> shouldEqual (Some "")

            addr |> shouldEqual canonicalAddr
        | other -> failwithf "expected ObjectRef (Some _), got %A" other

    [<Test>]
    let ``toCliType: String (Some "") is identity-stable across repeated calls`` () : unit =
        let loggerFactory, state = freshState ()

        let r1, state =
            lower loggerFactory bct stringPlan (CustomAttribFixedArg.String (Some "")) state

        let r2, _ =
            lower loggerFactory bct stringPlan (CustomAttribFixedArg.String (Some "")) state

        match r1, r2 with
        | CliType.ObjectRef (Some a1), CliType.ObjectRef (Some a2) -> a1 |> shouldEqual a2
        | _ -> failwithf "expected two ObjectRef (Some _), got %A and %A" r1 r2

    [<Test>]
    let ``toCliType: String (Some non-empty) allocates distinct objects on repeated calls`` () : unit =
        // Non-empty SerString values are not interned by CoreCLR's GetDataFromBlob;
        // each call to StringObject::NewString(psz, cBytes) produces a fresh instance.
        let loggerFactory, state = freshState ()

        let r1, state =
            lower loggerFactory bct stringPlan (CustomAttribFixedArg.String (Some "dup")) state

        let r2, _ =
            lower loggerFactory bct stringPlan (CustomAttribFixedArg.String (Some "dup")) state

        match r1, r2 with
        | CliType.ObjectRef (Some a1), CliType.ObjectRef (Some a2) -> a1 |> shouldNotEqual a2
        | _ -> failwithf "expected two ObjectRef (Some _), got %A and %A" r1 r2

    // --- property-based primitive round-trip --------------------------------

    let private propertyConfig : Config = Config.QuickThrowOnFailure.WithMaxTest 200

    let private genPrimitiveArg : Gen<CustomAttribFixedArg> =
        Gen.oneof
            [
                ArbMap.defaults |> ArbMap.generate<bool> |> Gen.map CustomAttribFixedArg.Bool
                ArbMap.defaults |> ArbMap.generate<char> |> Gen.map CustomAttribFixedArg.Char
                ArbMap.defaults |> ArbMap.generate<sbyte> |> Gen.map CustomAttribFixedArg.I1
                ArbMap.defaults |> ArbMap.generate<byte> |> Gen.map CustomAttribFixedArg.U1
                ArbMap.defaults |> ArbMap.generate<int16> |> Gen.map CustomAttribFixedArg.I2
                ArbMap.defaults |> ArbMap.generate<uint16> |> Gen.map CustomAttribFixedArg.U2
                ArbMap.defaults |> ArbMap.generate<int32> |> Gen.map CustomAttribFixedArg.I4
                ArbMap.defaults |> ArbMap.generate<uint32> |> Gen.map CustomAttribFixedArg.U4
                ArbMap.defaults |> ArbMap.generate<int64> |> Gen.map CustomAttribFixedArg.I8
                ArbMap.defaults |> ArbMap.generate<uint64> |> Gen.map CustomAttribFixedArg.U8
                ArbMap.defaults
                |> ArbMap.generate<NormalFloat>
                |> Gen.map (fun (NormalFloat f) -> CustomAttribFixedArg.R4 (float32 f))
                ArbMap.defaults
                |> ArbMap.generate<NormalFloat>
                |> Gen.map (fun (NormalFloat f) -> CustomAttribFixedArg.R8 f)
                Gen.constant (CustomAttribFixedArg.String None)
                // A null System.Type is a null reference, resolved against nothing.
                Gen.constant (CustomAttribFixedArg.Type None)
                // An enum lowers to its bare underlying integer — no enum wrapper is built here —
                // so it must land in exactly the slot its underlying type would.
                ArbMap.defaults
                |> ArbMap.generate<byte>
                |> Gen.map (CustomAttribFixedArg.U1 >> CustomAttribFixedArg.Enum)
                ArbMap.defaults
                |> ArbMap.generate<int64>
                |> Gen.map (CustomAttribFixedArg.I8 >> CustomAttribFixedArg.Enum)
            ]

    /// SizeOf agrees with the canonical mapping in `CliType.zeroOfPrimitive`,
    /// so the lowering preserves the expected slot width.
    let rec private expectedSize (arg : CustomAttribFixedArg) : int =
        match arg with
        // Deliberately the underlying type's width, not a wrapper's: the ctor's parameter slot is
        // built from the declared enum type by `callMethod`, and the value pushed here is the bare
        // integer that `toCliTypeCoerced` rewraps into it.
        | CustomAttribFixedArg.Enum underlying -> expectedSize underlying
        | CustomAttribFixedArg.Bool _ -> 1
        | CustomAttribFixedArg.Char _ -> 2
        | CustomAttribFixedArg.I1 _
        | CustomAttribFixedArg.U1 _ -> 1
        | CustomAttribFixedArg.I2 _
        | CustomAttribFixedArg.U2 _ -> 2
        | CustomAttribFixedArg.I4 _
        | CustomAttribFixedArg.U4 _ -> 4
        | CustomAttribFixedArg.I8 _
        | CustomAttribFixedArg.U8 _ -> 8
        | CustomAttribFixedArg.R4 _ -> 4
        | CustomAttribFixedArg.R8 _ -> 8
        | CustomAttribFixedArg.String _ -> 8
        | CustomAttribFixedArg.Type _ -> 8
        | CustomAttribFixedArg.Array _ ->
            failwith "expectedSize: Array is outside genPrimitiveArg's range and has no defined slot size here"
        | CustomAttribFixedArg.Tagged _ ->
            failwith "expectedSize: Tagged is outside genPrimitiveArg's range and has no defined slot size here"

    [<Test>]
    let ``tryToPureCliType: every non-allocating arg yields the expected slot size`` () : unit =
        let property (arg : CustomAttribFixedArg) : bool =
            match CustomAttribValueLowering.tryToPureCliType arg with
            | Ok cli -> (CliType.SizeOf cli).Size = expectedSize arg
            | Error _ -> false

        Check.One (propertyConfig, Prop.forAll (Arb.fromGen genPrimitiveArg) property)

    // --- TYPE ---------------------------------------------------------------

    /// A heap object to stand in for a resolved `RuntimeType` that is not an enum. The lowering
    /// passes the address through without inspecting the object, so any allocation will do; a
    /// string is the cheapest.
    let private standIn
        (loggerFactory : Microsoft.Extensions.Logging.ILoggerFactory)
        (label : string)
        (state : IlMachineState)
        : CustomAttribResolvedType * IlMachineState
        =
        let addr, state = IlMachineState.allocateManagedString loggerFactory bct label state

        {
            RuntimeType = addr
            Enum = None
        },
        state

    let private typeHandle : ConcreteTypeHandle = handleFor bct.RuntimeType

    let private typeArrayPlan : CustomAttribArgPlan =
        CustomAttribArgPlan.SzArray (typeHandle, CustomAttribArgPlan.Type)

    [<Test>]
    let ``toCliType: a null System.Type lowers to a null reference and consumes nothing`` () : unit =
        let loggerFactory, state = freshState ()
        use _loggerFactory = loggerFactory
        let spare, state = standIn loggerFactory "spare" state

        let result, unconsumed, _ =
            CustomAttribValueLowering.toCliType
                loggerFactory
                bct
                CustomAttribArgPlan.Type
                (CustomAttribFixedArg.Type None)
                [ spare ]
                state

        result |> shouldEqual (CliType.ObjectRef None)
        unconsumed |> shouldEqual [ spare ]

    [<Test>]
    let ``toCliType: a named System.Type is the head of the resolved list, and the tail is returned`` () : unit =
        let loggerFactory, state = freshState ()
        use _loggerFactory = loggerFactory
        let first, state = standIn loggerFactory "first" state
        let second, state = standIn loggerFactory "second" state

        let result, unconsumed, _ =
            CustomAttribValueLowering.toCliType
                loggerFactory
                bct
                CustomAttribArgPlan.Type
                (CustomAttribFixedArg.Type (Some "Any.Name"))
                [ first ; second ]
                state

        result |> shouldEqual (CliType.ObjectRef (Some first.RuntimeType))
        unconsumed |> shouldEqual [ second ]

    [<Test>]
    let ``toCliType: a named System.Type with nothing left to consume fails loudly`` () : unit =
        let loggerFactory, state = freshState ()
        use _loggerFactory = loggerFactory

        let exn =
            Assert.Throws<System.Exception> (fun () ->
                CustomAttribValueLowering.toCliType
                    loggerFactory
                    bct
                    CustomAttribArgPlan.Type
                    (CustomAttribFixedArg.Type (Some "Any.Name"))
                    []
                    state
                |> ignore
            )

        exn.Message |> shouldContainText "Any.Name"

    [<Test>]
    let ``toCliType: a System.Type[] consumes one resolved type per non-null element, in order`` () : unit =
        // Order-sensitive by construction: the three stand-ins are distinct, and the null element
        // in the middle must consume nothing, so a lowering that skipped or reversed would put the
        // wrong address in some cell.
        let loggerFactory, state = freshState ()
        use _loggerFactory = loggerFactory
        let a, state = standIn loggerFactory "a" state
        let b, state = standIn loggerFactory "b" state
        let spare, state = standIn loggerFactory "spare" state

        let arg =
            CustomAttribFixedArg.Array (
                Some
                    [
                        CustomAttribFixedArg.Type (Some "A")
                        CustomAttribFixedArg.Type None
                        CustomAttribFixedArg.Type (Some "B")
                    ]
            )

        let result, unconsumed, state =
            CustomAttribValueLowering.toCliType loggerFactory bct typeArrayPlan arg [ a ; b ; spare ] state

        unconsumed |> shouldEqual [ spare ]

        cells (arrayAddress result) state
        |> shouldEqual
            [
                CliType.ObjectRef (Some a.RuntimeType)
                CliType.ObjectRef None
                CliType.ObjectRef (Some b.RuntimeType)
            ]

        (ManagedHeap.getArrayShape (arrayAddress result) state.ManagedHeap).ConcreteType
        |> shouldEqual (ConcreteTypeHandle.OneDimArrayZero typeHandle)

    [<Test>]
    let ``toCliType: resolved types are consumed in the order typeNamesToResolve lists them`` () : unit =
        // The property the handler relies on: lowering a whole argument list against the objects
        // resolved for `typeNamesToResolve` of that list consumes exactly those objects, and puts
        // the i-th object where the i-th name was.
        let property (shapes : (bool * bool) list) : bool =
            let loggerFactory, state = freshState ()
            use _loggerFactory = loggerFactory

            let args =
                shapes
                |> List.mapi (fun i (isArray, isNull) ->
                    let element (j : int) =
                        if isNull then
                            CustomAttribFixedArg.Type None
                        else
                            CustomAttribFixedArg.Type (Some $"N%d{i}_%d{j}")

                    if isArray then
                        CustomAttribFixedArg.Array (Some [ element 0 ; element 1 ])
                    else
                        element 0
                )

            let plans =
                shapes
                |> List.map (fun (isArray, _) -> if isArray then typeArrayPlan else CustomAttribArgPlan.Type)

            let names = CustomAttribute.typeNamesToResolve args

            // One stand-in per name, labelled with the name, so a cell can be read back as the
            // name it was supposed to hold.
            let resolved, state =
                ((([] : CustomAttribResolvedType list), state), names)
                ||> List.fold (fun (acc, state) name ->
                    let addr, state = standIn loggerFactory name state
                    acc @ [ addr ], state
                )

            let lowered, unconsumed, state =
                ((([] : CliType list), resolved, state), List.zip plans args)
                ||> List.fold (fun (acc, resolved, state) (plan, arg) ->
                    let cli, resolved, state =
                        CustomAttribValueLowering.toCliType loggerFactory bct plan arg resolved state

                    acc @ [ cli ], resolved, state
                )

            let labelOf (cell : CliType) : string option =
                match cell with
                | CliType.ObjectRef (Some addr) -> ManagedHeap.getStringContents addr state.ManagedHeap
                | CliType.ObjectRef None -> None
                | other -> failwithf "unexpected cell %A" other

            let observed =
                List.zip args lowered
                |> List.collect (fun (arg, cell) ->
                    match arg with
                    | CustomAttribFixedArg.Array (Some _) -> cells (arrayAddress cell) state |> List.map labelOf
                    | _ -> [ labelOf cell ]
                )

            let expected =
                args
                |> List.collect (fun arg ->
                    match arg with
                    | CustomAttribFixedArg.Array (Some elements) -> elements
                    | other -> [ other ]
                )
                |> List.map (fun element ->
                    match element with
                    | CustomAttribFixedArg.Type name -> name
                    | other -> failwithf "unexpected element %A" other
                )

            unconsumed.IsEmpty && observed = expected

        Check.One (propertyConfig, Prop.forAll (ArbMap.defaults |> ArbMap.arbitrary<(bool * bool) list>) property)

    // --- SZARRAY ------------------------------------------------------------

    [<Test>]
    let ``toCliType: the null-array sentinel lowers to a null object ref`` () : unit =
        // ECMA-335 II.23.3's NumElem = 0xFFFFFFFF, which CoreCLR leaves as a null argument rather
        // than allocating an empty array.
        let loggerFactory, state = freshState ()

        let result, stateAfter =
            lower loggerFactory bct bytePlan (CustomAttribFixedArg.Array None) state

        result |> shouldEqual (CliType.ObjectRef None)
        System.Object.ReferenceEquals (stateAfter, state) |> shouldEqual true

    [<Test>]
    let ``toCliType: byte array allocates a byte[] with the decoded cells`` () : unit =
        let loggerFactory, state = freshState ()

        let arg =
            CustomAttribFixedArg.Array (Some [ CustomAttribFixedArg.U1 2uy ; CustomAttribFixedArg.U1 1uy ])

        let result, state = lower loggerFactory bct bytePlan arg state

        let addr = arrayAddress result

        cells addr state
        |> shouldEqual
            [
                CliType.Numeric (CliNumericType.UInt8 (UInt8Source.Verbatim 2uy))
                CliType.Numeric (CliNumericType.UInt8 (UInt8Source.Verbatim 1uy))
            ]

        // The array is a byte[], not an int[] that happens to hold small numbers.
        let shape = ManagedHeap.getArrayShape addr state.ManagedHeap

        shape.ConcreteType
        |> shouldEqual (ConcreteTypeHandle.OneDimArrayZero byteHandle)

        ManagedHeap.getArrayElementStride addr state.ManagedHeap |> shouldEqual 1

    [<Test>]
    let ``toCliType: empty array is distinct from the null sentinel and still knows its element type`` () : unit =
        // An empty array has no cell to sample, so its element type and stride are the only things
        // recording what it is an array *of*.
        let loggerFactory, state = freshState ()

        let result, state =
            lower loggerFactory bct bytePlan (CustomAttribFixedArg.Array (Some [])) state

        let addr = arrayAddress result
        let shape = ManagedHeap.getArrayShape addr state.ManagedHeap
        shape.Length |> shouldEqual 0

        shape.ConcreteType
        |> shouldEqual (ConcreteTypeHandle.OneDimArrayZero byteHandle)

        ManagedHeap.getArrayElementStride addr state.ManagedHeap |> shouldEqual 1

        ManagedHeap.getArrayElementZero addr state.ManagedHeap
        |> shouldEqual (CliType.Numeric (CliNumericType.UInt8 (UInt8Source.Verbatim 0uy)))

    [<Test>]
    let ``toCliType: string array lowers each element, including both null and empty`` () : unit =
        let loggerFactory, state = freshState ()

        let arg =
            CustomAttribFixedArg.Array (
                Some
                    [
                        CustomAttribFixedArg.String (Some "a")
                        CustomAttribFixedArg.String None
                        CustomAttribFixedArg.String (Some "")
                    ]
            )

        let result, state = lower loggerFactory bct stringArrayPlan arg state

        let addr = arrayAddress result

        let canonicalEmpty, _ =
            IlMachineState.internCanonicalEmptyString loggerFactory bct state

        match cells addr state with
        | [ CliType.ObjectRef (Some first) ; CliType.ObjectRef None ; CliType.ObjectRef (Some third) ] ->
            ManagedHeap.getStringContents first state.ManagedHeap |> shouldEqual (Some "a")
            // The empty SerString is the canonical interned instance, as it is for a scalar arg.
            third |> shouldEqual canonicalEmpty
        | other -> failwithf "expected [non-null; null; non-null] object refs, got %A" other

    [<Test>]
    let ``toCliType: enum array has the enum as its element type, not the underlying integer`` () : unit =
        let loggerFactory, state = freshState ()
        let enumHandle, plan, state = securityRuleSetPlan loggerFactory state

        let arg =
            CustomAttribFixedArg.Array (
                Some
                    [
                        CustomAttribFixedArg.Enum (CustomAttribFixedArg.U1 1uy)
                        CustomAttribFixedArg.Enum (CustomAttribFixedArg.U1 2uy)
                    ]
            )

        let result, state = lower loggerFactory bct plan arg state

        let addr = arrayAddress result

        let shape = ManagedHeap.getArrayShape addr state.ManagedHeap

        shape.ConcreteType
        |> shouldEqual (ConcreteTypeHandle.OneDimArrayZero enumHandle)

        // The cells hold the enum wrapper the array's stride was measured from, not the bare
        // integer the decoder produced: an enum lowers to its underlying value only where the
        // evaluation stack is going to rewrap it.
        cells addr state
        |> List.map (fun cell ->
            match cell with
            | CliType.ValueType vt ->
                vt.PrimitiveLikeKind |> shouldEqual (Some PrimitiveLikeKind.EnumLike)
                (CliValueType.PrimitiveLikeField vt).Contents
            | other -> failwithf "expected an enum value type, got %A" other
        )
        |> shouldEqual
            [
                CliType.Numeric (CliNumericType.UInt8 (UInt8Source.Verbatim 1uy))
                CliType.Numeric (CliNumericType.UInt8 (UInt8Source.Verbatim 2uy))
            ]

    [<Test>]
    let ``toCliType: nested array allocates an array of arrays`` () : unit =
        // ECMA-335's grammar admits `byte[][]` and CoreCLR's ReadArray handles it, though C#
        // forbids it in an attribute argument, so only hand-written IL gets here.
        let loggerFactory, state = freshState ()

        let plan =
            CustomAttribArgPlan.SzArray (ConcreteTypeHandle.OneDimArrayZero byteHandle, bytePlan)

        let arg =
            CustomAttribFixedArg.Array (
                Some
                    [
                        CustomAttribFixedArg.Array (Some [ CustomAttribFixedArg.U1 7uy ])
                        CustomAttribFixedArg.Array None
                    ]
            )

        let result, state = lower loggerFactory bct plan arg state

        let addr = arrayAddress result

        let shape = ManagedHeap.getArrayShape addr state.ManagedHeap

        shape.ConcreteType
        |> shouldEqual (ConcreteTypeHandle.OneDimArrayZero (ConcreteTypeHandle.OneDimArrayZero byteHandle))

        match cells addr state with
        | [ CliType.ObjectRef (Some inner) ; CliType.ObjectRef None ] ->
            (ManagedHeap.getArrayShape inner state.ManagedHeap).ConcreteType
            |> shouldEqual (ConcreteTypeHandle.OneDimArrayZero byteHandle)

            cells inner state
            |> shouldEqual [ CliType.Numeric (CliNumericType.UInt8 (UInt8Source.Verbatim 7uy)) ]
        | other -> failwithf "expected [array; null], got %A" other

    [<Test>]
    let ``toCliType: an array value under a scalar plan fails loudly`` () : unit =
        let loggerFactory, state = freshState ()

        let arg = CustomAttribFixedArg.Array (Some [ CustomAttribFixedArg.U1 1uy ])

        let exn =
            Assert.Throws (fun () -> lower loggerFactory bct stringPlan arg state |> ignore)

        exn.Message |> shouldContainText "scalar plan"

    [<Test>]
    let ``toCliType: a scalar value under an array plan fails loudly`` () : unit =
        let loggerFactory, state = freshState ()

        let exn =
            Assert.Throws (fun () -> lower loggerFactory bct bytePlan (CustomAttribFixedArg.U1 1uy) state |> ignore)

        exn.Message |> shouldContainText "SZARRAY plan"

    [<Test>]
    let ``toCliType: array cells agree element-by-element with lowering each element alone`` () : unit =
        // Order-sensitive by construction: a reversed or off-by-one fill disagrees with the
        // per-element lowering on any list whose entries are not all equal.
        let property (values : int list) : bool =
            let loggerFactory, state = freshState ()
            use _loggerFactory = loggerFactory

            let elements = values |> List.map CustomAttribFixedArg.I4

            let result, state =
                lower loggerFactory bct int32ArrayPlan (CustomAttribFixedArg.Array (Some elements)) state

            let expected =
                elements
                |> List.map (fun element ->
                    match CustomAttribValueLowering.tryToPureCliType element with
                    | Ok cli -> cli
                    | Error msg -> failwithf "expected a pure lowering, got Error %s" msg
                )

            cells (arrayAddress result) state = expected

        Check.One (propertyConfig, Prop.forAll (ArbMap.defaults |> ArbMap.arbitrary<int list>) property)

    // --- TAGGED_OBJECT ------------------------------------------------------

    /// The box `result` refers to: its type, and the value its sole field holds.
    let private boxed (fieldName : string) (result : CliType) (state : IlMachineState) : ConcreteTypeHandle * CliType =
        match result with
        | CliType.ObjectRef (Some addr) ->
            let obj = ManagedHeap.get addr state.ManagedHeap
            obj.ConcreteType, AllocatedNonArrayObject.DereferenceField fieldName obj
        | other -> failwithf "expected a reference to a box, got %A" other

    /// A resolved type that is the given enum.
    let private enumStandIn
        (loggerFactory : Microsoft.Extensions.Logging.ILoggerFactory)
        (label : string)
        (handle : ConcreteTypeHandle)
        (underlying : EnumUnderlyingType)
        (state : IlMachineState)
        : CustomAttribResolvedType * IlMachineState
        =
        let resolved, state = standIn loggerFactory label state

        { resolved with
            Enum = Some (handle, underlying)
        },
        state

    [<Test>]
    let ``toCliType: a tagged primitive boxes as the primitive its tag names`` () : unit =
        // The box's type comes from the tag, not from the value's eval-stack form: `U4` and `I4`
        // both lower to an Int32 slot, but box as UInt32 and Int32 respectively.
        let cases : (PrimitiveType * CustomAttribFixedArg * TypeInfo<GenericParamFromMetadata, TypeDefn>) list =
            [
                PrimitiveType.Boolean, CustomAttribFixedArg.Bool true, bct.Boolean
                PrimitiveType.Char, CustomAttribFixedArg.Char 'Z', bct.Char
                PrimitiveType.SByte, CustomAttribFixedArg.I1 -5y, bct.SByte
                PrimitiveType.Byte, CustomAttribFixedArg.U1 250uy, bct.Byte
                PrimitiveType.Int16, CustomAttribFixedArg.I2 -1234s, bct.Int16
                PrimitiveType.UInt16, CustomAttribFixedArg.U2 60000us, bct.UInt16
                PrimitiveType.Int32, CustomAttribFixedArg.I4 -123456, bct.Int32
                PrimitiveType.UInt32, CustomAttribFixedArg.U4 4000000000u, bct.UInt32
                PrimitiveType.Int64, CustomAttribFixedArg.I8 -1099511627776L, bct.Int64
                PrimitiveType.UInt64, CustomAttribFixedArg.U8 0xFEDCBA9876543210UL, bct.UInt64
                PrimitiveType.Single, CustomAttribFixedArg.R4 1.5f, bct.Single
                PrimitiveType.Double, CustomAttribFixedArg.R8 -2.25, bct.Double
            ]

        for pt, value, expected in cases do
            let loggerFactory, state = freshState ()
            use _loggerFactory = loggerFactory

            let result, state =
                lower
                    loggerFactory
                    bct
                    CustomAttribArgPlan.TaggedObject
                    (CustomAttribFixedArg.Tagged (CustomAttribTaggedType.Primitive pt, value))
                    state

            let boxType, contents = boxed "m_value" result state
            boxType |> shouldEqual (handleFor expected)

            // The payload is what the value lowers to as an argument, rewrapped into the box's
            // field exactly as `box` would rewrap it.
            let payload =
                match CustomAttribValueLowering.tryToPureCliType value with
                | Ok cli -> cli
                | Error msg -> failwithf "expected a pure lowering, got Error %s" msg

            EvalStackValue.toCliTypeCoerced contents (EvalStackValue.ofCliType payload)
            |> shouldEqual contents

            (EvalStackValue.ofCliType contents)
            |> shouldEqual (EvalStackValue.ofCliType payload)

    [<Test>]
    let ``toCliType: a tagged string is the string itself, unboxed`` () : unit =
        let loggerFactory, state = freshState ()
        use _loggerFactory = loggerFactory

        let tag = CustomAttribTaggedType.Primitive PrimitiveType.String

        let result, state =
            lower
                loggerFactory
                bct
                CustomAttribArgPlan.TaggedObject
                (CustomAttribFixedArg.Tagged (tag, CustomAttribFixedArg.String (Some "hello")))
                state

        match result with
        | CliType.ObjectRef (Some addr) ->
            ManagedHeap.getStringContents addr state.ManagedHeap
            |> shouldEqual (Some "hello")
        | other -> failwithf "expected a string reference, got %A" other

        lower
            loggerFactory
            bct
            CustomAttribArgPlan.TaggedObject
            (CustomAttribFixedArg.Tagged (tag, CustomAttribFixedArg.String None))
            state
        |> fst
        |> shouldEqual (CliType.ObjectRef None)

    [<Test>]
    let ``toCliType: a tagged enum consumes one resolution and boxes as that enum`` () : unit =
        let loggerFactory, state = freshState ()
        use _loggerFactory = loggerFactory
        let enumHandle, _, state = securityRuleSetPlan loggerFactory state

        let resolved, state =
            enumStandIn loggerFactory "E" enumHandle EnumUnderlyingType.Byte state

        let spare, state = standIn loggerFactory "spare" state

        let result, unconsumed, state =
            CustomAttribValueLowering.toCliType
                loggerFactory
                bct
                CustomAttribArgPlan.TaggedObject
                (CustomAttribFixedArg.Tagged (
                    CustomAttribTaggedType.Enum "System.Security.SecurityRuleSet",
                    CustomAttribFixedArg.Enum (CustomAttribFixedArg.U1 2uy)
                ))
                [ resolved ; spare ]
                state

        unconsumed |> shouldEqual [ spare ]
        let boxType, contents = boxed "value__" result state
        boxType |> shouldEqual enumHandle

        contents
        |> shouldEqual (CliType.Numeric (CliNumericType.UInt8 (UInt8Source.Verbatim 2uy)))

    [<Test>]
    let ``toCliType: a tagged enum whose resolution is not an enum fails loudly`` () : unit =
        let loggerFactory, state = freshState ()
        use _loggerFactory = loggerFactory
        let resolved, state = standIn loggerFactory "NotAnEnum" state

        let exn =
            Assert.Throws<System.Exception> (fun () ->
                CustomAttribValueLowering.toCliType
                    loggerFactory
                    bct
                    CustomAttribArgPlan.TaggedObject
                    (CustomAttribFixedArg.Tagged (
                        CustomAttribTaggedType.Enum "NotAnEnum",
                        CustomAttribFixedArg.Enum (CustomAttribFixedArg.U1 2uy)
                    ))
                    [ resolved ]
                    state
                |> ignore
            )

        exn.Message |> shouldContainText "NotAnEnum"

    [<Test>]
    let ``toCliType: a null tagged enum array still consumes its element type's resolution`` () : unit =
        let loggerFactory, state = freshState ()
        use _loggerFactory = loggerFactory
        let enumHandle, _, state = securityRuleSetPlan loggerFactory state

        let resolved, state =
            enumStandIn loggerFactory "E" enumHandle EnumUnderlyingType.Byte state

        let spare, state = standIn loggerFactory "spare" state

        let tag =
            CustomAttribTaggedType.SzArray (CustomAttribTaggedElementType.Enum "System.Security.SecurityRuleSet")

        let result, unconsumed, _ =
            CustomAttribValueLowering.toCliType
                loggerFactory
                bct
                CustomAttribArgPlan.TaggedObject
                (CustomAttribFixedArg.Tagged (tag, CustomAttribFixedArg.Array None))
                [ resolved ; spare ]
                state

        result |> shouldEqual (CliType.ObjectRef None)
        unconsumed |> shouldEqual [ spare ]

    [<Test>]
    let ``toCliType: a tagged array's element type is the one its tag names, even when empty`` () : unit =
        let loggerFactory, state = freshState ()
        use _loggerFactory = loggerFactory
        let enumHandle, _, state = securityRuleSetPlan loggerFactory state

        let resolved, state =
            enumStandIn loggerFactory "E" enumHandle EnumUnderlyingType.Byte state

        let state, systemType =
            let typeInfo =
                corelib.TryGetTopLevelTypeDef "System" "Type"
                |> Option.defaultWith (fun () -> failwith "System.Type not found in corelib")

            IlMachineTypeResolution.concretizeType
                loggerFactory
                bct
                state
                corelib.DefinitionFullName
                ImmutableArray.Empty
                ImmutableArray.Empty
                (TypeDefn.FromDefinition (typeInfo.Identity, System.Reflection.Metadata.SignatureTypeKind.Class))

        let cases : (CustomAttribTaggedElementType * CustomAttribResolvedType list * ConcreteTypeHandle) list =
            [
                CustomAttribTaggedElementType.Primitive PrimitiveType.Int32, [], int32Handle
                CustomAttribTaggedElementType.Primitive PrimitiveType.String, [], stringHandle
                CustomAttribTaggedElementType.Type, [], systemType
                CustomAttribTaggedElementType.Object, [], handleFor bct.Object
                CustomAttribTaggedElementType.Enum "E", [ resolved ], enumHandle
            ]

        for element, resolvedTypes, expected in cases do
            let result, unconsumed, state =
                CustomAttribValueLowering.toCliType
                    loggerFactory
                    bct
                    CustomAttribArgPlan.TaggedObject
                    (CustomAttribFixedArg.Tagged (
                        CustomAttribTaggedType.SzArray element,
                        CustomAttribFixedArg.Array (Some [])
                    ))
                    resolvedTypes
                    state

            unconsumed |> shouldEqual []

            (ManagedHeap.getArrayShape (arrayAddress result) state.ManagedHeap).ConcreteType
            |> shouldEqual (ConcreteTypeHandle.OneDimArrayZero expected)

    [<Test>]
    let ``toCliType: a tagged object[] boxes each element as its own tag says`` () : unit =
        let loggerFactory, state = freshState ()
        use _loggerFactory = loggerFactory

        let arg =
            CustomAttribFixedArg.Tagged (
                CustomAttribTaggedType.SzArray CustomAttribTaggedElementType.Object,
                CustomAttribFixedArg.Array (
                    Some
                        [
                            CustomAttribFixedArg.Tagged (
                                CustomAttribTaggedType.Primitive PrimitiveType.UInt16,
                                CustomAttribFixedArg.U2 7us
                            )
                            CustomAttribFixedArg.Tagged (
                                CustomAttribTaggedType.Primitive PrimitiveType.String,
                                CustomAttribFixedArg.String None
                            )
                        ]
                )
            )

        let result, state =
            lower loggerFactory bct CustomAttribArgPlan.TaggedObject arg state

        let addr = arrayAddress result

        (ManagedHeap.getArrayShape addr state.ManagedHeap).ConcreteType
        |> shouldEqual (ConcreteTypeHandle.OneDimArrayZero (handleFor bct.Object))

        match cells addr state with
        | [ first ; CliType.ObjectRef None ] -> boxed "m_value" first state |> fst |> shouldEqual (handleFor bct.UInt16)
        | other -> failwithf "expected [box; null], got %A" other

    [<Test>]
    let ``toCliType: tagged values consume resolutions in the order typeNamesToResolve lists them`` () : unit =
        // Names: E1 (a tagged enum), T1 (a Type argument), E2 (a null enum array's element type),
        // T2 (a Type inside an object[]), E3 (an enum inside the same object[]). Each resolution
        // is distinct, so a lowering that skipped or reordered one puts the wrong one somewhere.
        let loggerFactory, state = freshState ()
        use _loggerFactory = loggerFactory
        let enumHandle, _, state = securityRuleSetPlan loggerFactory state

        let e1, state =
            enumStandIn loggerFactory "E1" enumHandle EnumUnderlyingType.Byte state

        let t1, state = standIn loggerFactory "T1" state

        let e2, state =
            enumStandIn loggerFactory "E2" enumHandle EnumUnderlyingType.Byte state

        let t2, state = standIn loggerFactory "T2" state

        let e3, state =
            enumStandIn loggerFactory "E3" enumHandle EnumUnderlyingType.Byte state

        let byteEnum (v : byte) =
            CustomAttribFixedArg.Enum (CustomAttribFixedArg.U1 v)

        let args =
            [
                CustomAttribArgPlan.TaggedObject,
                CustomAttribFixedArg.Tagged (CustomAttribTaggedType.Enum "E1", byteEnum 1uy)
                CustomAttribArgPlan.Type, CustomAttribFixedArg.Type (Some "T1")
                CustomAttribArgPlan.TaggedObject,
                CustomAttribFixedArg.Tagged (
                    CustomAttribTaggedType.SzArray (CustomAttribTaggedElementType.Enum "E2"),
                    CustomAttribFixedArg.Array None
                )
                CustomAttribArgPlan.TaggedObject,
                CustomAttribFixedArg.Tagged (
                    CustomAttribTaggedType.SzArray CustomAttribTaggedElementType.Object,
                    CustomAttribFixedArg.Array (
                        Some
                            [
                                CustomAttribFixedArg.Tagged (
                                    CustomAttribTaggedType.Type,
                                    CustomAttribFixedArg.Type (Some "T2")
                                )
                                CustomAttribFixedArg.Tagged (CustomAttribTaggedType.Enum "E3", byteEnum 3uy)
                            ]
                    )
                )
            ]

        CustomAttribute.typeNamesToResolve (List.map snd args)
        |> shouldEqual [ "E1" ; "T1" ; "E2" ; "T2" ; "E3" ]

        let lowered, unconsumed, state =
            (([], [ e1 ; t1 ; e2 ; t2 ; e3 ], state), args)
            ||> List.fold (fun (acc, resolved, state) (plan, arg) ->
                let cli, resolved, state =
                    CustomAttribValueLowering.toCliType loggerFactory bct plan arg resolved state

                acc @ [ cli ], resolved, state
            )

        unconsumed |> shouldEqual []

        match lowered with
        | [ first ; second ; third ; fourth ] ->
            boxed "value__" first state
            |> shouldEqual (enumHandle, CliType.Numeric (CliNumericType.UInt8 (UInt8Source.Verbatim 1uy)))

            second |> shouldEqual (CliType.ObjectRef (Some t1.RuntimeType))
            third |> shouldEqual (CliType.ObjectRef None)

            match cells (arrayAddress fourth) state with
            | [ typeCell ; enumCell ] ->
                typeCell |> shouldEqual (CliType.ObjectRef (Some t2.RuntimeType))

                boxed "value__" enumCell state
                |> shouldEqual (enumHandle, CliType.Numeric (CliNumericType.UInt8 (UInt8Source.Verbatim 3uy)))
            | other -> failwithf "expected two cells, got %A" other
        | other -> failwithf "expected four lowered args, got %A" other

    [<Test>]
    let ``toCliType: a tagged value under another plan, and an untagged one under TaggedObject, fail loudly``
        ()
        : unit
        =
        let loggerFactory, state = freshState ()
        use _loggerFactory = loggerFactory

        let tagged =
            CustomAttribFixedArg.Tagged (
                CustomAttribTaggedType.Primitive PrimitiveType.Int32,
                CustomAttribFixedArg.I4 1
            )

        for plan, arg in
            [
                CustomAttribArgPlan.Primitive PrimitiveType.Int32, tagged
                CustomAttribArgPlan.Enum EnumUnderlyingType.Int32, tagged
                CustomAttribArgPlan.Type, tagged
                int32ArrayPlan, tagged
                CustomAttribArgPlan.TaggedObject, CustomAttribFixedArg.I4 1
                CustomAttribArgPlan.TaggedObject, CustomAttribFixedArg.String None
            ] do
            let exn =
                Assert.Throws<System.Exception> (fun () -> lower loggerFactory bct plan arg state |> ignore)

            exn.Message |> shouldContainText "bug"
