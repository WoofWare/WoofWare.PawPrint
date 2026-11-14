namespace WoofWare.PawPrint.Test

open System
open System.Collections.Immutable
open System.Reflection
open System.Reflection.Emit
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint
open WoofWare.PawPrint.Test.EmitScopeGen

/// A dynamic module's parameters: `EmitScope.addParam`'s refusals; `EmitScopeDefinitions.defineParam` against
/// a model of `RegMeta::DefineParam` with `_SetParamProps`, and against `ParamTableReplay` for the calls that
/// would strand MethodDef rows; and both, through the image, against real .NET's `TypeBuilder` in this process.
/// The last covers the Domain, the writer and the model of `RuntimeParameterInfo.GetParameters`, not the QCall
/// handler's reading of its arguments, the lazy MethodDef tokens or the truncation to sixteen bits, which only
/// the guests cover.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestEmitScopeParams =

    let private methodFlags : MethodAttributes =
        MethodAttributes.Public
        ||| MethodAttributes.Abstract
        ||| MethodAttributes.Virtual
        ||| MethodAttributes.NewSlot
        ||| MethodAttributes.HideBySig

    /// An instance method's signature returning `int32`, with `arity` parameters of `int32`.
    let private signatureOf (arity : int) : ImmutableArray<byte> =
        ImmutableArray.CreateRange [ yield 0x20uy ; yield byte arity ; yield 0x08uy ; for _ in 1..arity -> 0x08uy ]

    /// A scope with `<Module>` and `types` abstract types, `T0` at TypeDef 2 onwards, and no methods.
    let private withTypes (types : int) : EmitScope =
        (EmitScope.create "Params" Guid.Empty, [ 0 .. types - 1 ])
        ||> List.fold (fun scope t ->
            match
                EmitScopeDefinitions.defineTopLevelTypeDef
                    $"T%d{t}"
                    (TypeAttributes.Public ||| TypeAttributes.Abstract)
                    None
                    []
                    scope
            with
            | TypeDefDefinition.Defined (_, scope) -> scope
            | TypeDefDefinition.Duplicate _ -> failwith $"T%d{t} is defined twice"
        )

    /// A scope with `<Module>`, one type and `methods` methods of it, each with two parameters.
    let private withMethods (methods : int) : EmitScope * MethodDefRid list =
        List.mapFold
            (fun scope i ->
                EmitScopeDefinitions.defineMethod (TypeDefRid 2) $"M%d{i}" (signatureOf 2) methodFlags scope
            )
            (withTypes 1)
            [ 1..methods ]
        |> fun (rids, scope) -> scope, rids

    [<TestCase 0x10000>]
    [<TestCase -1>]
    let ``addParam refuses flags wider than the column's two bytes`` (flags : int) : unit =
        let scope, methods = withMethods 1

        Assert.Throws<ArgumentException> (fun () ->
            EmitScope.addParam
                {
                    Owner = List.head methods
                    Flags = enum<ParameterAttributes> flags
                    Sequence = 1us
                    Name = StringOffset 0
                }
                scope
            |> ignore<ParamRid * EmitScope>
        )
        |> ignore<ArgumentException>

    /// One call of a generated history.
    type private Call =
        /// `defineMethod` on type `owner`, modulo the type count.
        | Method of owner : int
        /// `defineParam` on method `method`, modulo the method count, if the scope has one.
        | Param of method : int * sequence : int * name : string * flags : int

    /// From 0 upwards, with 0, 255, 256, 65535, 65536 and 65537 common, and small sequences more so, so that
    /// a method's rows slide past each other and tie.
    let private genSequence : Gen<int> =
        Gen.frequency
            [
                6, Gen.choose (0, 3)
                2, Gen.elements [ 0 ; 255 ; 256 ; 65535 ; 65536 ; 65537 ]
                1, Gen.choose (0, Int32.MaxValue)
            ]

    /// Every `int` without a bit of `ParameterAttributes.ReservedMask`, which every managed caller clears, with
    /// the high sixteen bits common.
    let private genFlags : Gen<int> =
        Gen.frequency
            [
                2, Gen.elements [ 0 ; 0x0001 ; 0x0002 ; 0x0010 ; 0x0FFF ]
                2, Gen.choose (Int32.MinValue, Int32.MaxValue)
                2, Gen.map2 (fun high low -> (high <<< 16) ||| low) (Gen.choose (1, 0xFFFF)) (Gen.choose (0, 0x0FFF))
            ]
        |> Gen.map (fun flags -> flags &&& ~~~0xF000)

    let private genParamName : Gen<string> =
        Gen.elements [ "" ; "a" ; "b" ; "é中" ; "\U0001F600" ; "x y" ]

    let private genCalls : Gen<int * Call list> =
        gen {
            let! types = Gen.choose (1, 3)

            let genCall =
                Gen.frequency
                    [
                        1, Gen.map Call.Method (Gen.choose (0, 2))
                        3,
                        gen {
                            let! method = Gen.choose (0, 1000)
                            let! sequence = genSequence
                            let! name = genParamName
                            let! flags = genFlags
                            return Call.Param (method, sequence, name, flags)
                        }
                    ]

            let! length = Gen.choose (0, 40)
            let! calls = Gen.listOfLength length genCall
            return types, List.ofSeq calls
        }

    /// What a history reached.
    [<RequireQualifiedAccess>]
    type private Reach =
        | Refused
        /// A refusal in which every stranded row belongs to a type other than the slid method's.
        | RefusedAcrossTypes
        | SlideWithTable
        | SlideOnLastMethod
        | DirectStartThenSlide
        | WideSequence
        | HighFlags
        | DuplicateSequence

    [<Test>]
    let ``defineParam adds CoreCLR's row, or refuses exactly the calls that strand rows`` () : unit =
        let property (cover : Reach -> unit) (types : int, calls : Call list) : unit =
            let mutable scope = withTypes types
            let mutable replay = ParamTableReplay.empty
            // Each method's type, by RID - 1.
            let owners = ResizeArray<int> ()

            for call in calls do
                match call with
                | Call.Method owner ->
                    let owner = owner % types

                    scope <-
                        EmitScopeDefinitions.defineMethod
                            (TypeDefRid (owner + 2))
                            $"M%d{owners.Count}"
                            (signatureOf 1)
                            methodFlags
                            scope
                        |> snd

                    replay <- ParamTableReplay.addMethod replay
                    owners.Add owner
                | Call.Param (method, sequence, name, flags) when owners.Count > 0 ->
                    let md = method % owners.Count + 1
                    let stored = uint16 (sequence &&& 0xFFFF)
                    let before = EmitScope.parameters scope

                    match
                        EmitScopeDefinitions.defineParam (MethodDefRid md) sequence name flags scope,
                        ParamTableReplay.addParam md stored replay
                    with
                    | ParamDefinition.StrandsLaterMethods (slid, last), ParamTableReplay.Step.Stranded _ ->
                        slid |> shouldEqual (MethodDefRid md)
                        last |> shouldEqual (MethodDefRid owners.Count)
                        cover Reach.Refused

                        if owners |> Seq.skip md |> Seq.forall (fun t -> t <> owners.[md - 1]) then
                            cover Reach.RefusedAcrossTypes
                    | ParamDefinition.Defined (rid, after),
                      ParamTableReplay.Step.Added (replayRid, replayAfter, started, slots) ->
                        rid.Rid |> shouldEqual (before.Count + 1)
                        rid.Rid |> shouldEqual replayRid

                        let parameters = EmitScope.parameters after
                        parameters.Count |> shouldEqual (before.Count + 1)

                        List.ofSeq parameters
                        |> List.take before.Count
                        |> shouldEqual (List.ofSeq before)

                        let row = parameters.[before.Count]
                        row.Owner |> shouldEqual (MethodDefRid md)
                        row.Sequence |> shouldEqual stored
                        int row.Flags |> shouldEqual (flags &&& 0xFFFF)
                        EmitScope.stringAt row.Name after |> shouldEqual name

                        if before |> Seq.exists (fun p -> p.Owner.Rid = md && p.Sequence = stored) then
                            cover Reach.DuplicateSequence

                        if slots > 0 && (ParamTableReplay.pointers replay).IsSome then
                            cover Reach.SlideWithTable

                        if slots > 0 && md = owners.Count then
                            cover Reach.SlideOnLastMethod

                        if slots > 0 && started = Some ParamTableReplay.TableStart.Direct then
                            cover Reach.DirectStartThenSlide

                        if sequence > 0xFFFF then
                            cover Reach.WideSequence

                        if flags >>> 16 <> 0 then
                            cover Reach.HighFlags

                        scope <- after
                        replay <- replayAfter
                    | defined, step -> failwith $"defineParam gave %A{defined}, but the replay %A{step}"
                | Call.Param _ -> ()

        let coverage =
            CoverageSample.check (Config.QuickThrowOnFailure.WithMaxTest 300) (Arb.fromGen genCalls) property

        coverage.Count Reach.Refused |> shouldBeGreaterThan 50
        coverage.Count Reach.RefusedAcrossTypes |> shouldBeGreaterThan 10
        coverage.Count Reach.SlideWithTable |> shouldBeGreaterThan 50
        coverage.Count Reach.SlideOnLastMethod |> shouldBeGreaterThan 50
        coverage.Count Reach.DirectStartThenSlide |> shouldBeGreaterThan 10
        coverage.Count Reach.WideSequence |> shouldBeGreaterThan 50
        coverage.Count Reach.HighFlags |> shouldBeGreaterThan 50
        coverage.Count Reach.DuplicateSequence |> shouldBeGreaterThan 50

    /// `MethodBuilder.DefineParameter`'s flag sweep, measured on .NET 10.0.14 and 11.0.0-rc.3: each value given,
    /// and the flags reflection reads from the row. The managed caller clears 0xF000 first.
    let private measuredFlags : (int * int) list =
        [
            for bit in 0..11 -> 1 <<< bit, 1 <<< bit
            for bit in 12..31 -> 1 <<< bit, 0
            yield 0, 0
            yield 0xFFFF, 0x0FFF
            yield -1, 0x0FFF
            yield 0x1000, 0
            yield 0x2000, 0
            yield 0xF000, 0
            yield 0x7FFF0013, 0x0013
            yield 0x0FFF, 0x0FFF
        ]

    [<Test>]
    let ``the measured flags give the measured rows`` () : unit =
        measuredFlags.Length |> shouldEqual 40
        let scope, methods = withMethods 1

        for given, row in measuredFlags do
            match EmitScopeDefinitions.defineParam (List.head methods) 1 "p" (given &&& ~~~0xF000) scope with
            | ParamDefinition.Defined (rid, after) ->
                int (EmitScope.parameters after).[rid.Rid - 1].Flags |> shouldEqual row
            | ParamDefinition.StrandsLaterMethods _ -> failwith "a first parameter cannot strand rows"

    /// An argument `defineParam` refuses.
    type private Bad =
        | ReservedFlags of flags : int
        | NegativeSequence of sequence : int
        | MissingMethod of beyond : int
        | NulInName of name : string

    [<Test>]
    let ``defineParam refuses an argument that no managed caller passes`` () : unit =
        let genBad =
            Gen.oneof
                [
                    Gen.map2
                        (fun flags bit -> Bad.ReservedFlags (flags ||| (1 <<< bit)))
                        (Gen.choose (Int32.MinValue, Int32.MaxValue))
                        (Gen.choose (12, 15))
                    Gen.constant (Bad.ReservedFlags -1)
                    Gen.map Bad.NegativeSequence (Gen.choose (Int32.MinValue, -1))
                    Gen.map Bad.MissingMethod (Gen.choose (1, 5))
                    Gen.elements [ Bad.NulInName "\000" ; Bad.NulInName "a\000b" ; Bad.NulInName "x\000" ]
                ]

        let property (bad : Bad) : unit =
            let scope, methods = withMethods 2

            let method, sequence, name, flags =
                match bad with
                | Bad.ReservedFlags flags -> List.head methods, 1, "p", flags
                | Bad.NegativeSequence sequence -> List.head methods, sequence, "p", 0
                | Bad.MissingMethod beyond -> MethodDefRid (2 + beyond), 1, "p", 0
                | Bad.NulInName name -> List.head methods, 1, name, 0

            Assert.Throws<ArgumentException> (fun () ->
                EmitScopeDefinitions.defineParam method sequence name flags scope
                |> ignore<ParamDefinition>
            )
            |> ignore<ArgumentException>

        Check.One (Config.QuickThrowOnFailure.WithMaxTest 200, Prop.forAll (Arb.fromGen genBad) property)

    /// One step of a schedule run on real .NET's `TypeBuilder` and on a scope alike: `DefineParameter` on the
    /// tokenised method `method`, modulo their count, at `position`, modulo the method's arity plus one.
    type private Define =
        {
            Method : int
            Position : int
            Flags : int
            Name : string
        }

    type private Schedule =
        {
            /// Each method's type and arity, in the order the schedule gives them tokens, which is each type's
            /// methods in definition order.
            Methods : (int * int) list
            Types : int
            /// How many methods have tokens before the first `DefineParameter`.
            Prefix : int
            /// The step before which the rest are given tokens.
            RestAt : int
            Defines : Define list
        }

    /// What reflection reports of one parameter: its name, flags and token.
    type private Reported =
        {
            Name : string
            Flags : int
            Token : int
        }

    /// What the differential cases reached.
    [<RequireQualifiedAccess>]
    type private DifferentialReach =
        | Refused
        | SlideWithTable
        | SlideOnLastMethod
        | TokenisedAfterSlideOnLast
        | DuplicatePosition
        | TwoReturnRows
        | TooManyRows
        | PointerTable
        | FlagsAboveTwelveBits
        | NullName

    /// `RuntimeParameterInfo.GetParameters` over a method's Param rows in the order the method lists them, each
    /// its Sequence, flags, name and token: the parameters by position, a null element where a position has no
    /// row and there are as many rows as the signature has parameters plus one, or the exception's type.
    let private modelParameters
        (arity : int)
        (run : (int * int * string * int) list)
        : Result<Reported option list, string>
        =
        if run.Length > arity + 1 then
            Error "BadImageFormatException"
        else
            let slots : Reported option array = Array.create arity None

            let placed =
                run
                |> List.fold
                    (fun ok (sequence, flags, name, token) ->
                        match ok with
                        | Error e -> Error e
                        | Ok () when sequence = 0 -> Ok ()
                        | Ok () when sequence - 1 >= arity -> Error "BadImageFormatException"
                        | Ok () ->
                            slots.[sequence - 1] <-
                                Some
                                    {
                                        Name = name
                                        Flags = flags
                                        Token = token
                                    }

                            Ok ()
                    )
                    (Ok ())

            placed
            |> Result.map (fun () ->
                if run.Length < arity + 1 then
                    slots
                    |> Array.map (
                        Option.orElse (
                            Some
                                {
                                    Name = null
                                    Flags = 0
                                    Token = 0x08000000
                                }
                        )
                    )
                    |> List.ofArray
                else
                    List.ofArray slots
            )

    /// `RuntimeParameterInfo.GetReturnParameter` over the same rows.
    let private modelReturn (arity : int) (run : (int * int * string * int) list) : Result<Reported, string> =
        if run.Length > arity + 1 then
            Error "BadImageFormatException"
        else
            match run |> List.filter (fun (sequence, _, _, _) -> sequence = 0) with
            | [] ->
                Ok
                    {
                        Name = null
                        Flags = 0
                        Token = 0x08000000
                    }
            | [ (_, flags, name, token) ] ->
                Ok
                    {
                        Name = name
                        Flags = flags
                        Token = token
                    }
            | _ -> Error "BadImageFormatException"

    let private reported (p : ParameterInfo) : Reported option =
        p
        |> Option.ofObj
        |> Option.map (fun p ->
            {
                Name = p.Name
                Flags = int p.Attributes
                Token = p.MetadataToken
            }
        )

    let private attempt (f : unit -> 'a) : Result<'a, string> =
        try
            Ok (f ())
        with e ->
            Error (e.GetType().Name)

    [<Test>]
    let ``parameters read back as real .NET's TypeBuilder reports them`` () : unit =
        let property (cover : DifferentialReach -> unit) (schedule : Schedule) : unit =
            let assembly =
                AssemblyBuilder.DefineDynamicAssembly (AssemblyName "ParamsOracle", AssemblyBuilderAccess.RunAndCollect)

            let host = assembly.DefineDynamicModule "ParamsOracle"

            let hostTypes =
                [|
                    for t in 0 .. schedule.Types - 1 do
                        yield host.DefineType ($"T%d{t}", TypeAttributes.Public ||| TypeAttributes.Abstract)
                |]

            let hostMethods =
                schedule.Methods
                |> List.mapi (fun i (t, arity) ->
                    hostTypes.[t].DefineMethod ($"M%d{i}", methodFlags, typeof<int>, Array.replicate arity typeof<int>)
                )
                |> Array.ofList

            let arities = schedule.Methods |> List.map snd |> Array.ofList
            let mutable scope = withTypes schedule.Types
            let mutable replay = ParamTableReplay.empty
            let mutable tokenised = 0
            let mutable slidOnLastWith : int option = None

            let tokenise (count : int) : unit =
                for i in tokenised .. count - 1 do
                    hostMethods.[i].MetadataToken |> shouldEqual (0x06000000 ||| (i + 1))

                    scope <-
                        EmitScopeDefinitions.defineMethod
                            (TypeDefRid (fst schedule.Methods.[i] + 2))
                            $"M%d{i}"
                            (signatureOf arities.[i])
                            methodFlags
                            scope
                        |> snd

                    replay <- ParamTableReplay.addMethod replay

                tokenised <- max tokenised count

            let token (builder : ParameterBuilder) : int =
                builder.GetType().GetField("_token", BindingFlags.NonPublic ||| BindingFlags.Instance).GetValue builder
                |> unbox<int>

            tokenise schedule.Prefix
            let mutable stranded = false

            for step, define in List.indexed schedule.Defines do
                if not stranded then
                    if step = schedule.RestAt then
                        tokenise hostMethods.Length

                    if tokenised > 0 then
                        let i = define.Method % tokenised
                        let md = i + 1
                        let position = define.Position % (arities.[i] + 1)
                        let flags = define.Flags &&& ~~~0xF000
                        // `DefineParameterCore` clears the reserved bits, and the QCall reads a name to its first NUL.
                        let name =
                            match define.Name with
                            | null -> ""
                            | name -> name.Split('\000').[0]

                        let paramsBefore = (EmitScope.parameters scope).Count

                        if define.Flags &&& 0xFFFFF000 <> 0 then
                            cover DifferentialReach.FlagsAboveTwelveBits

                        if isNull define.Name then
                            cover DifferentialReach.NullName

                        let builder =
                            hostMethods.[i]
                                .DefineParameter (position, enum<ParameterAttributes> define.Flags, define.Name)

                        match EmitScopeDefinitions.defineParam (MethodDefRid md) position name flags scope with
                        | ParamDefinition.Defined (rid, after) ->
                            token builder |> shouldEqual (0x08000000 ||| rid.Rid)

                            match ParamTableReplay.addParam md (uint16 position) replay with
                            | ParamTableReplay.Step.Added (_, replayAfter, _, slots) ->
                                if slots > 0 && (ParamTableReplay.pointers replay).IsSome then
                                    cover DifferentialReach.SlideWithTable

                                if slots > 0 && md = tokenised then
                                    cover DifferentialReach.SlideOnLastMethod
                                    slidOnLastWith <- Some tokenised

                                match slidOnLastWith with
                                | Some count when md > count -> cover DifferentialReach.TokenisedAfterSlideOnLast
                                | _ -> ()

                                replay <- replayAfter
                            | ParamTableReplay.Step.Stranded _ -> failwith "defineParam defined a row that strands rows"

                            scope <- after
                        | ParamDefinition.StrandsLaterMethods _ ->
                            token builder |> shouldEqual (0x08000000 ||| (paramsBefore + 1))

                            let e =
                                Assert.Throws<BadImageFormatException> (fun () ->
                                    hostMethods.[i].DefineParameter (0, ParameterAttributes.None, null)
                                    |> ignore<ParameterBuilder>
                                )

                            e.HResult |> shouldEqual 0x80131124
                            cover DifferentialReach.Refused
                            stranded <- true

            if not stranded then
                tokenise hostMethods.Length
                let created = hostTypes |> Array.map (fun t -> t.CreateType ())
                let pe, reader = readImage (EmitScopeImage.write scope)
                use _ = pe

                if (paramPtrRows pe reader).IsSome then
                    cover DifferentialReach.PointerTable

                for i in 0 .. hostMethods.Length - 1 do
                    let arity = arities.[i]

                    let run =
                        reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle (i + 1)).GetParameters ()
                        |> Seq.map (fun handle ->
                            let p = reader.GetParameter handle

                            p.SequenceNumber,
                            int p.Attributes,
                            reader.GetString p.Name,
                            0x08000000
                            ||| MetadataTokens.GetRowNumber (ParameterHandle.op_Implicit handle : EntityHandle)
                        )
                        |> List.ofSeq

                    let sequences = run |> List.map (fun (s, _, _, _) -> s)

                    if sequences.Length <> (List.distinct sequences).Length then
                        cover DifferentialReach.DuplicatePosition

                    if sequences |> List.filter ((=) 0) |> List.length > 1 then
                        cover DifferentialReach.TwoReturnRows

                    if run.Length > arity + 1 then
                        cover DifferentialReach.TooManyRows

                    let method =
                        created.[fst schedule.Methods.[i]]
                            .GetMethod (
                                $"M%d{i}",
                                BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.DeclaredOnly
                            )

                    attempt (fun () -> method.GetParameters () |> Seq.map reported |> List.ofSeq)
                    |> shouldEqual (modelParameters arity run)

                    attempt (fun () -> (reported method.ReturnParameter).Value)
                    |> shouldEqual (modelReturn arity run)

        let gen =
            gen {
                let! types = Gen.choose (1, 3)
                let! perType = Gen.listOfLength types (Gen.choose (1, 3))
                // Each type's methods in definition order, interleaved across types.
                let! order =
                    perType
                    |> List.ofSeq
                    |> List.indexed
                    |> List.collect (fun (t, n) -> List.replicate n t)
                    |> Gen.shuffle

                let! arities = Gen.listOfLength order.Length (Gen.choose (0, 4))
                let! prefix = Gen.choose (0, order.Length)
                let! length = Gen.choose (0, 25)

                let genDefine =
                    gen {
                        let! method = Gen.choose (0, 1000)
                        let! position = Gen.choose (0, 1000)

                        let! flags =
                            Gen.frequency
                                [
                                    3, Gen.elements [ 0 ; 0x0001 ; 0x0002 ; 0x0010 ]
                                    2, Gen.choose (Int32.MinValue, Int32.MaxValue)
                                ]

                        let! name = Gen.elements [ null ; "" ; "a" ; "b" ; "c\000d" ; "\000x" ; "é" ]

                        return
                            {
                                Method = method
                                Position = position
                                Flags = flags
                                Name = name
                            }
                    }

                let! defines = Gen.listOfLength length genDefine

                // A third of the schedules open with the first tokenised method's position 1 and then its
                // return value, a slide that strands rows when a later method has a token.
                let! opening =
                    Gen.frequency
                        [
                            2, Gen.constant []
                            1,
                            Gen.constant
                                [
                                    for position in [ 1 ; 0 ] ->
                                        {
                                            Method = 0
                                            Position = position
                                            Flags = 0
                                            Name = "o"
                                        }
                                ]
                        ]

                let defines = opening @ List.ofSeq defines
                let! restAt = Gen.choose (0, defines.Length)

                return
                    {
                        Methods = List.zip (List.ofArray order) (List.ofSeq arities)
                        Types = types
                        Prefix = prefix
                        RestAt = restAt
                        Defines = defines
                    }
            }

        let coverage =
            CoverageSample.check (Config.QuickThrowOnFailure.WithMaxTest 200) (Arb.fromGen gen) property

        coverage.Count DifferentialReach.Refused |> shouldBeGreaterThan 20
        coverage.Count DifferentialReach.SlideWithTable |> shouldBeGreaterThan 20
        coverage.Count DifferentialReach.SlideOnLastMethod |> shouldBeGreaterThan 20

        coverage.Count DifferentialReach.TokenisedAfterSlideOnLast
        |> shouldBeGreaterThan 10

        coverage.Count DifferentialReach.DuplicatePosition |> shouldBeGreaterThan 20
        coverage.Count DifferentialReach.TwoReturnRows |> shouldBeGreaterThan 10
        coverage.Count DifferentialReach.TooManyRows |> shouldBeGreaterThan 10
        coverage.Count DifferentialReach.PointerTable |> shouldBeGreaterThan 20
        coverage.Count DifferentialReach.FlagsAboveTwelveBits |> shouldBeGreaterThan 20
        coverage.Count DifferentialReach.NullName |> shouldBeGreaterThan 20
