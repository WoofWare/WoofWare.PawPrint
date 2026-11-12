namespace WoofWare.PawPrint.Test

open System.Collections.Immutable
open System.IO
open System.Reflection.Metadata
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// `AwaitPattern`, the JIT's await pattern and an async version's tail shapes, against a reference
/// that reads backwards from each `call` of `AsyncHelpers.Await` and each `ret`: over generated
/// instruction sequences, whose block starts the generator knows, and over every body of the
/// framework under test.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestAwaitPattern =

    /// What a method an operand names is, as far as the patterns care.
    [<RequireQualifiedAccess>]
    type private Named =
        /// `ConfigureAwait` of one of the four promise types.
        | ConfigureAwait
        /// `AsyncHelpers.Await`.
        | Await
        /// A constructor of `ValueTask` or `ValueTask<T>` taking one argument, not `ValueTask<T>(T)`.
        | ValueTaskConstructor
        /// `ValueTask.AsTask` or `ValueTask<T>.AsTask`.
        | AsTask
        | Other

    /// What the caller of `AwaitPattern.awaitAfter` binds: the follower whose operands name what
    /// the JIT tests for.
    let private confirmAwait
        (named : 'operand -> Named)
        (follower : AwaitFollower<'operand> option)
        : AwaitFollower<'operand> option
        =
        follower
        |> Option.filter (fun follower ->
            named follower.Await = Named.Await
            && (follower.ConfigureAwait
                |> Option.forall (fun call -> named call.Method = Named.ConfigureAwait))
        )

    let private confirmTail
        (named : 'operand -> Named)
        (shape : TailShape<'operand> option)
        : TailShape<'operand> option
        =
        shape
        |> Option.filter (fun shape ->
            match shape with
            | TailShape.Return -> true
            | TailShape.NewValueTask constructor -> named constructor = Named.ValueTaskConstructor
            | TailShape.AsTask asTask -> named asTask = Named.AsTask
        )

    let private isCall (op : IlOp) : MetadataOperand option =
        match op with
        | IlOp.UnaryMetadataToken (UnaryMetadataTokenIlOp.Call, operand)
        | IlOp.UnaryMetadataToken (UnaryMetadataTokenIlOp.Callvirt, operand) -> Some operand
        | _ -> None

    let private stlocIndex (op : IlOp) : int option =
        match op with
        | IlOp.Nullary NullaryIlOp.Stloc_0 -> Some 0
        | IlOp.Nullary NullaryIlOp.Stloc_1 -> Some 1
        | IlOp.Nullary NullaryIlOp.Stloc_2 -> Some 2
        | IlOp.Nullary NullaryIlOp.Stloc_3 -> Some 3
        | IlOp.UnaryConst (UnaryConstIlOp.Stloc_s index) -> Some (int index)
        | IlOp.UnaryConst (UnaryConstIlOp.Stloc index) -> Some (int index)
        | _ -> None

    let private ldlocaIndex (op : IlOp) : int option =
        match op with
        | IlOp.UnaryConst (UnaryConstIlOp.Ldloca_s index) -> Some (int index)
        | IlOp.UnaryConst (UnaryConstIlOp.Ldloca index) -> Some (int index)
        | _ -> None

    /// What the reference expects of each call in a body, keyed by the call's offset: the await
    /// pattern and the tail shape it begins, each read backwards from where it ends.
    type private Expected =
        {
            Awaits : Map<int, AwaitFollower<MetadataOperand>>
            Tails : Map<int, TailShape<MetadataOperand>>
        }

    let private reference
        (named : MetadataOperand -> Named)
        (starts : Set<int>)
        (body : MethodInstructions)
        : Expected
        =
        let instructions = List.toArray body.Instructions

        // The instruction before the one at `index`, where both are in one block.
        let before (index : int) : (int * IlOp * int) option =
            if index = 0 || starts.Contains (snd instructions.[index]) then
                None
            else
                let op, offset = instructions.[index - 1]
                Some (index - 1, op, offset)

        let add (key : int) (value : 'v) (map : Map<int, 'v>) : Map<int, 'v> =
            if map.ContainsKey key then
                failwith $"BUG: the reference reads two patterns beginning at %d{key}"

            Map.add key value map

        let mutable awaits = Map.empty
        let mutable tails = Map.empty

        instructions
        |> Array.iteri (fun index (op, offset) ->
            match op with
            | IlOp.UnaryMetadataToken (UnaryMetadataTokenIlOp.Call, awaitOperand) when named awaitOperand = Named.Await ->
                let follower (configureAwait : ConfigureAwaitCall<MetadataOperand> option) =
                    {
                        ConfigureAwait = configureAwait
                        Await = awaitOperand
                        AwaitOffset = offset
                        AfterAwait = offset + IlOp.NumberOfBytes op
                    }

                match before index with
                | None -> ()
                | Some (previous, previousOp, previousOffset) ->
                    match isCall previousOp with
                    | None -> ()
                    | Some previousOperand ->
                        // The call straight before the await is a call the await follows, whatever
                        // it names.
                        awaits <- add previousOffset (follower None) awaits

                        if named previousOperand = Named.ConfigureAwait then
                            let constant =
                                match before previous with
                                | Some (constant, IlOp.Nullary NullaryIlOp.LdcI4_0, _) -> Some (constant, false)
                                | Some (constant, IlOp.Nullary NullaryIlOp.LdcI4_1, _) -> Some (constant, true)
                                | _ -> None

                            match constant with
                            | None -> ()
                            | Some (constant, argument) ->
                                let configureAwait =
                                    Some
                                        {
                                            ContinueOnCapturedContext = argument
                                            Method = previousOperand
                                        }

                                match before constant with
                                | Some (_, callOp, callOffset) when (isCall callOp).IsSome ->
                                    awaits <- add callOffset (follower configureAwait) awaits
                                | Some (ldloca, ldlocaOp, _) ->
                                    match ldlocaIndex ldlocaOp, before ldloca with
                                    | Some local, Some (stloc, stlocOp, _) when stlocIndex stlocOp = Some local ->
                                        match before stloc with
                                        | Some (_, callOp, callOffset) when (isCall callOp).IsSome ->
                                            awaits <- add callOffset (follower configureAwait) awaits
                                        | _ -> ()
                                    | _ -> ()
                                | None -> ()
            | IlOp.Nullary NullaryIlOp.Ret ->
                match before index with
                | None -> ()
                | Some (previous, previousOp, previousOffset) ->
                    match previousOp with
                    | IlOp.UnaryMetadataToken (UnaryMetadataTokenIlOp.Newobj, constructor) when
                        named constructor = Named.ValueTaskConstructor
                        ->
                        match before previous with
                        | Some (_, callOp, callOffset) when (isCall callOp).IsSome ->
                            tails <- add callOffset (TailShape.NewValueTask constructor) tails
                        | _ -> ()
                    | _ ->
                        // A call straight before a `ret` is a tail shape, whatever it names.
                        if (isCall previousOp).IsSome then
                            tails <- add previousOffset TailShape.Return tails

                        match previousOp with
                        | IlOp.UnaryMetadataToken (UnaryMetadataTokenIlOp.Call, asTask) when
                            named asTask = Named.AsTask
                            ->
                            match before previous with
                            | Some (ldloca, ldlocaOp, _) ->
                                match ldlocaIndex ldlocaOp, before ldloca with
                                | Some local, Some (stloc, stlocOp, _) when stlocIndex stlocOp = Some local ->
                                    match before stloc with
                                    | Some (_, callOp, callOffset) when (isCall callOp).IsSome ->
                                        tails <- add callOffset (TailShape.AsTask asTask) tails
                                    | _ -> ()
                                | _ -> ()
                            | None -> ()
                        | _ -> ()
            | _ -> ()
        )

        {
            Awaits = awaits
            Tails = tails
        }

    /// What a comparison of the matcher with the reference saw, for asserting it is not vacuous.
    type private Tally =
        {
            mutable AwaitsWithoutConfigure : int
            mutable AwaitsWithConfigure : int
            mutable AwaitsThroughLocal : int
            mutable AwaitsContinuingOnContext : int
            mutable Returns : int
            mutable NewValueTasks : int
            mutable AsTasks : int
            /// Calls the matcher answered for but whose operands name something else.
            mutable Unconfirmed : int
            /// Await patterns that would match but for the start of a block within them.
            mutable SplitAwaits : int
            /// Tail shapes that would match but for the start of a block within them.
            mutable SplitTails : int
        }

    let private emptyTally () : Tally =
        {
            AwaitsWithoutConfigure = 0
            AwaitsWithConfigure = 0
            AwaitsThroughLocal = 0
            AwaitsContinuingOnContext = 0
            Returns = 0
            NewValueTasks = 0
            AsTasks = 0
            Unconfirmed = 0
            SplitAwaits = 0
            SplitTails = 0
        }

    /// The matcher answers as the reference does at every call of `body`, and no match it confirms
    /// spans the start of a block.
    let private check<'key when 'key : equality>
        (named : MetadataOperand -> Named)
        (key : MetadataOperand -> 'key)
        (starts : Set<int>)
        (tally : Tally)
        (description : string)
        (body : MethodInstructions)
        : unit
        =
        let expected = reference named starts body
        let instructions = List.toArray body.Instructions

        // What the reference would read if no block started within the body.
        let unsplit = reference named Set.empty body

        for KeyValue (offset, _) in unsplit.Awaits do
            if not (expected.Awaits.ContainsKey offset) then
                tally.SplitAwaits <- tally.SplitAwaits + 1

        for KeyValue (offset, _) in unsplit.Tails do
            if not (expected.Tails.ContainsKey offset) then
                tally.SplitTails <- tally.SplitTails + 1

        instructions
        |> Array.iteri (fun index (op, offset) ->
            match isCall op with
            | None -> ()
            | Some _ ->
                let awaitFollower = AwaitPattern.awaitAfter body offset
                let tailShape = AwaitPattern.tailShapeAfter body offset
                let confirmedAwait = confirmAwait named awaitFollower
                let confirmedTail = confirmTail named tailShape

                if awaitFollower.IsSome && confirmedAwait.IsNone then
                    tally.Unconfirmed <- tally.Unconfirmed + 1

                if tailShape.IsSome && confirmedTail.IsNone then
                    tally.Unconfirmed <- tally.Unconfirmed + 1

                let keyedAwait (follower : AwaitFollower<MetadataOperand> option) =
                    follower |> Option.map (AwaitFollower.map key)

                let keyedTail (shape : TailShape<MetadataOperand> option) = shape |> Option.map (TailShape.map key)

                if keyedAwait confirmedAwait <> keyedAwait (expected.Awaits.TryFind offset) then
                    failwith
                        $"%s{description}: at the call at %d{offset}, the matcher read the await %A{awaitFollower} (confirmed: %A{confirmedAwait}); the reference, %A{expected.Awaits.TryFind offset}"

                if keyedTail confirmedTail <> keyedTail (expected.Tails.TryFind offset) then
                    failwith
                        $"%s{description}: at the call at %d{offset}, the matcher read the tail shape %A{tailShape} (confirmed: %A{confirmedTail}); the reference, %A{expected.Tails.TryFind offset}"

                match confirmedAwait with
                | None -> ()
                | Some follower ->
                    for _, inner in body.Instructions do
                        if inner > offset && inner <= follower.AwaitOffset && starts.Contains inner then
                            failwith
                                $"%s{description}: the await read from the call at %d{offset} spans the block starting at %d{inner}"

                    match follower.ConfigureAwait with
                    | None -> tally.AwaitsWithoutConfigure <- tally.AwaitsWithoutConfigure + 1
                    | Some call ->
                        tally.AwaitsWithConfigure <- tally.AwaitsWithConfigure + 1

                        if call.ContinueOnCapturedContext then
                            tally.AwaitsContinuingOnContext <- tally.AwaitsContinuingOnContext + 1

                        match Array.tryItem (index + 1) instructions with
                        | Some (next, _) when (stlocIndex next).IsSome ->
                            tally.AwaitsThroughLocal <- tally.AwaitsThroughLocal + 1
                        | _ -> ()

                match confirmedTail with
                | None -> ()
                | Some TailShape.Return -> tally.Returns <- tally.Returns + 1
                | Some (TailShape.NewValueTask _) -> tally.NewValueTasks <- tally.NewValueTasks + 1
                | Some (TailShape.AsTask _) -> tally.AsTasks <- tally.AsTasks + 1
        )

    // ---- Generated instruction sequences ----

    /// The method a generated operand names, by its index.
    let private generatedNames : Named list =
        [
            Named.Other
            Named.ConfigureAwait
            Named.Await
            Named.ValueTaskConstructor
            Named.AsTask
            // A constructor of `ValueTask<T>` that is `ValueTask<T>(T)`, or of something else.
            Named.Other
        ]

    let private operand (index : int) : MetadataOperand = MetadataOperand.FromStubScope index

    let private generatedKey (operand : MetadataOperand) : int =
        match operand with
        | MetadataOperand.FromStubScope index -> index
        | other -> failwith $"BUG: a generated body names %O{other}"

    let private generatedNamed (operand : MetadataOperand) : Named =
        match operand with
        | MetadataOperand.FromStubScope index -> generatedNames.[index]
        | other -> failwith $"BUG: a generated body names %O{other}"

    /// An element of a generated body: an instruction, or a mark that a block starts there.
    [<RequireQualifiedAccess>]
    type private Item =
        | Op of IlOp
        /// A `brtrue` placed before the body lands here.
        | Landed
        /// The bound of an exception region falls here.
        | RegionBound

    let private call (name : int) : IlOp =
        IlOp.UnaryMetadataToken (UnaryMetadataTokenIlOp.Call, operand name)

    let private callvirt (name : int) : IlOp =
        IlOp.UnaryMetadataToken (UnaryMetadataTokenIlOp.Callvirt, operand name)

    let private genName : Gen<int> =
        Gen.frequency [ 3, Gen.constant 0 ; 1, Gen.choose (0, generatedNames.Length - 1) ]

    let private genCall (name : Gen<int>) : Gen<IlOp> =
        gen {
            let! name = name
            let! virt = Gen.elements [ false ; true ]
            return if virt then callvirt name else call name
        }

    let private genStloc (local : int) : Gen<IlOp> =
        Gen.elements
            [
                if local = 0 then
                    IlOp.Nullary NullaryIlOp.Stloc_0
                if local = 1 then
                    IlOp.Nullary NullaryIlOp.Stloc_1
                if local = 2 then
                    IlOp.Nullary NullaryIlOp.Stloc_2
                if local = 3 then
                    IlOp.Nullary NullaryIlOp.Stloc_3
                IlOp.UnaryConst (UnaryConstIlOp.Stloc_s (uint8 local))
                IlOp.UnaryConst (UnaryConstIlOp.Stloc (uint16 local))
            ]

    let private genLdloca (local : int) : Gen<IlOp> =
        Gen.elements
            [
                IlOp.UnaryConst (UnaryConstIlOp.Ldloca_s (uint8 local))
                IlOp.UnaryConst (UnaryConstIlOp.Ldloca (uint16 local))
            ]

    /// A `stloc` and an `ldloca`, usually of one local.
    let private genLocalRoundTrip : Gen<IlOp list> =
        gen {
            let! local = Gen.choose (0, 5)
            let! other = Gen.frequency [ 5, Gen.constant local ; 1, Gen.choose (0, 5) ]
            let! stloc = genStloc local
            let! ldloca = genLdloca other
            return [ stloc ; ldloca ]
        }

    /// A constant before a `ConfigureAwait`, usually one the pattern names.
    let private genConstant : Gen<IlOp> =
        Gen.frequency
            [
                4, Gen.elements [ IlOp.Nullary NullaryIlOp.LdcI4_0 ; IlOp.Nullary NullaryIlOp.LdcI4_1 ]
                1,
                Gen.elements
                    [
                        IlOp.Nullary NullaryIlOp.LdcI4_2
                        IlOp.UnaryConst (UnaryConstIlOp.Ldc_I4_s 0y)
                        IlOp.UnaryConst (UnaryConstIlOp.Ldc_I4 1)
                    ]
            ]

    /// Usually `name`, sometimes another.
    let private mostly (name : int) : Gen<int> =
        Gen.frequency [ 6, Gen.constant name ; 1, genName ]

    /// An await pattern, perhaps with a part wrong.
    let private genAwaitPattern : Gen<IlOp list> =
        gen {
            let! callee = genCall genName
            let! withLocal = Gen.elements [ false ; true ]
            let! withConfigure = Gen.elements [ false ; true ]
            let! local = if withLocal then genLocalRoundTrip else Gen.constant []
            let! constant = genConstant
            let! configure = genCall (mostly 1)
            let! awaitName = mostly 2
            let! awaitVirt = Gen.frequency [ 6, Gen.constant false ; 1, Gen.constant true ]

            let await = if awaitVirt then callvirt awaitName else call awaitName

            return
                [
                    callee
                    yield! local
                    if withConfigure then
                        constant
                        configure
                    await
                ]
        }

    /// A tail shape, perhaps with a part wrong.
    let private genTailShape : Gen<IlOp list> =
        gen {
            let! callee = genCall genName
            let! constructor = mostly 3
            let! asTask = mostly 4
            let! asTaskVirt = Gen.frequency [ 6, Gen.constant false ; 1, Gen.constant true ]
            let! local = genLocalRoundTrip

            let! shape =
                Gen.elements
                    [
                        []
                        [ IlOp.UnaryMetadataToken (UnaryMetadataTokenIlOp.Newobj, operand constructor) ]
                        local @ [ (if asTaskVirt then callvirt asTask else call asTask) ]
                    ]

            return callee :: shape @ [ IlOp.Nullary NullaryIlOp.Ret ]
        }

    let private genSingle : Gen<IlOp list> =
        Gen.oneof
            [
                genCall genName |> Gen.map List.singleton
                genConstant |> Gen.map List.singleton
                Gen.choose (0, 5) |> Gen.bind genStloc |> Gen.map List.singleton
                Gen.choose (0, 5) |> Gen.bind genLdloca |> Gen.map List.singleton
                genName
                |> Gen.map (fun name -> [ IlOp.UnaryMetadataToken (UnaryMetadataTokenIlOp.Newobj, operand name) ])
                Gen.elements
                    [
                        [ IlOp.Nullary NullaryIlOp.Ret ]
                        [ IlOp.Nullary NullaryIlOp.Nop ]
                        [ IlOp.Nullary NullaryIlOp.Pop ]
                        [ IlOp.Nullary NullaryIlOp.Ldloc_0 ]
                    ]
            ]

    /// A body's items: pieces of patterns, with block starts among them at a density the body
    /// itself chooses.
    let private genItems : Gen<Item list> =
        gen {
            let! markDensity = Gen.choose (0, 30)
            let! pieceCount = Gen.choose (1, 12)

            let genMark : Gen<Item list> =
                gen {
                    let! roll = Gen.choose (0, 99)

                    if roll < markDensity then
                        let! mark = Gen.elements [ Item.Landed ; Item.RegionBound ]
                        return [ mark ]
                    else
                        return []
                }

            let genPiece : Gen<Item list> =
                gen {
                    let! ops = Gen.frequency [ 3, genAwaitPattern ; 3, genTailShape ; 2, genSingle ]

                    let! marked =
                        ops
                        |> List.map (fun op ->
                            gen {
                                let! mark = genMark
                                return mark @ [ Item.Op op ]
                            }
                        )
                        |> Gen.sequenceToList

                    return List.concat marked
                }

            let! pieces = Gen.listOfLength pieceCount genPiece
            return List.concat pieces
        }

    /// The body `items` describe, behind a `ldc.i4.0; brtrue` for each place a branch lands, with an
    /// exception region for each four region bounds, and the offsets at which its blocks start.
    let private assemble (items : Item list) : MethodInstructions * Set<int> =
        let landings =
            items
            |> List.filter (fun item ->
                match item with
                | Item.Landed -> true
                | Item.Op _
                | Item.RegionBound -> false
            )
            |> List.length

        let branchBytes =
            IlOp.NumberOfBytes (IlOp.Nullary NullaryIlOp.LdcI4_0)
            + IlOp.NumberOfBytes (IlOp.UnaryConst (UnaryConstIlOp.Brtrue 0))

        // The offset each item is at, behind the branches.
        let placed, _ =
            ((branchBytes * landings), items)
            ||> List.mapFold (fun offset item ->
                match item with
                | Item.Op op -> (item, offset), offset + IlOp.NumberOfBytes op
                | Item.Landed
                | Item.RegionBound -> (item, offset), offset
            )

        let landed =
            placed
            |> List.choose (fun (item, offset) ->
                match item with
                | Item.Landed -> Some offset
                | _ -> None
            )

        let bounds =
            placed
            |> List.choose (fun (item, offset) ->
                match item with
                | Item.RegionBound -> Some offset
                | _ -> None
            )

        let branches =
            landed
            |> List.mapi (fun index target ->
                let offset = index * branchBytes
                let brtrueOffset = offset + IlOp.NumberOfBytes (IlOp.Nullary NullaryIlOp.LdcI4_0)

                let delta =
                    target
                    - (brtrueOffset + IlOp.NumberOfBytes (IlOp.UnaryConst (UnaryConstIlOp.Brtrue 0)))

                [
                    IlOp.Nullary NullaryIlOp.LdcI4_0, offset
                    IlOp.UnaryConst (UnaryConstIlOp.Brtrue delta), brtrueOffset
                ]
            )
            |> List.concat

        let regions =
            bounds
            |> List.chunkBySize 4
            |> List.filter (fun chunk -> chunk.Length = 4)
            |> List.map (fun chunk ->
                match chunk with
                | [ a ; b ; c ; d ] ->
                    ExceptionRegion.Fault
                        {
                            TryOffset = a
                            TryLength = b - a
                            HandlerOffset = c
                            HandlerLength = d - c
                        }
                | _ -> failwith "unreachable"
            )

        let regionBounds =
            bounds
            |> List.chunkBySize 4
            |> List.filter (fun chunk -> chunk.Length = 4)
            |> List.concat

        let instructions =
            branches
            @ (placed
               |> List.choose (fun (item, offset) ->
                   match item with
                   | Item.Op op -> Some (op, offset)
                   | _ -> None
               ))

        let body =
            {
                Instructions = instructions
                Locations = instructions |> List.map (fun (op, offset) -> offset, op) |> Map.ofList
                LocalsInit = false
                LocalVars = None
                ExceptionRegions = ImmutableArray.CreateRange regions
            }

        body, Set.ofList (landed @ regionBounds)

    [<Test>]
    let ``the matcher reads what the reference reads backwards, over generated instruction sequences`` () : unit =
        let tally = emptyTally ()
        let mutable bodies = 0

        let property (items : Item list) : unit =
            let body, starts = assemble items
            bodies <- bodies + 1

            // The generator's own record of where blocks start is `BoxPattern`'s.
            BoxPattern.blockStarts body |> shouldEqual starts

            check generatedNamed generatedKey starts tally $"%A{body.Instructions}" body

        Check.One (Config.QuickThrowOnFailure.WithMaxTest 3000, Prop.forAll (Arb.fromGen genItems) property)

        bodies |> shouldEqual 3000

        // Each kind of match, and each near miss, came up often.
        tally.AwaitsWithoutConfigure |> shouldBeGreaterThan 500
        tally.AwaitsWithConfigure |> shouldBeGreaterThan 500
        tally.AwaitsThroughLocal |> shouldBeGreaterThan 200
        tally.AwaitsContinuingOnContext |> shouldBeGreaterThan 200
        tally.Returns |> shouldBeGreaterThan 500
        tally.NewValueTasks |> shouldBeGreaterThan 200
        tally.AsTasks |> shouldBeGreaterThan 200
        tally.Unconfirmed |> shouldBeGreaterThan 500
        tally.SplitAwaits |> shouldBeGreaterThan 50
        tally.SplitTails |> shouldBeGreaterThan 50

    [<Test>]
    let ``the matcher refuses an offset that is not a call`` () : unit =
        let body, _ =
            assemble
                [
                    Item.Op (IlOp.Nullary NullaryIlOp.Nop)
                    Item.Op (IlOp.Nullary NullaryIlOp.Ret)
                ]

        Assert.Throws<System.ArgumentException> (fun () -> AwaitPattern.awaitAfter body 0 |> ignore)
        |> ignore

        Assert.Throws<System.ArgumentException> (fun () -> AwaitPattern.tailShapeAfter body 1 |> ignore)
        |> ignore

    // ---- The framework under test ----

    /// The namespace, type name and name of the method `token` names in `assembly`, as its rows
    /// spell them, with its parameter types where the token is a reference.
    let rec private methodNamed
        (assembly : DumpedAssembly)
        (token : MetadataToken)
        : string * string * string * TypeDefn list option
        =
        let typeName (parent : MetadataToken) : string * string =
            match parent with
            | MetadataToken.TypeReference handle ->
                let reference = assembly.TypeRefs.[handle]
                reference.Namespace, reference.Name
            | MetadataToken.TypeDefinition handle ->
                let definition = assembly.TypeDefs.[handle]
                definition.Namespace, definition.Name
            | MetadataToken.TypeSpecification handle ->
                match assembly.TypeSpecs.[handle].Signature with
                | TypeDefn.GenericInstantiation (TypeDefn.FromReference (reference, _), _) ->
                    reference.Namespace, reference.Name
                | TypeDefn.GenericInstantiation (TypeDefn.FromDefinition (identity, _), _) ->
                    let definition = assembly.TypeDefs.[identity.TypeDefinition.Get]
                    definition.Namespace, definition.Name
                | _ -> "", ""
            | _ -> "", ""

        match token with
        | MetadataToken.MethodDef handle ->
            let method = assembly.Methods.[handle]
            let declaring = method.RequiredDeclaringType
            declaring.Namespace, declaring.Name, method.Name, None
        | MetadataToken.MemberReference handle ->
            let reference = assembly.Members.[handle]
            let ns, name = typeName reference.Parent

            let parameters =
                match reference.Signature with
                | MemberSignature.Method signature -> Some signature.ParameterTypes
                | MemberSignature.Field _ -> None

            ns, name, reference.PrettyName, parameters
        | MetadataToken.MethodSpecification handle -> methodNamed assembly assembly.MethodSpecs.[handle].Method
        | _ -> "", "", "", None

    let private frameworkNamed (assembly : DumpedAssembly) (operand : MetadataOperand) : Named =
        match operand with
        | MetadataOperand.FromMetadata sourced ->
            match methodNamed assembly sourced.Token with
            | "System.Runtime.CompilerServices", "AsyncHelpers", "Await", _ -> Named.Await
            | "System.Threading.Tasks", ("Task" | "Task`1" | "ValueTask" | "ValueTask`1"), "ConfigureAwait", _ ->
                Named.ConfigureAwait
            | "System.Threading.Tasks", ("ValueTask" | "ValueTask`1"), "AsTask", _ -> Named.AsTask
            | "System.Threading.Tasks", "ValueTask", ".ctor", Some [ _ ] -> Named.ValueTaskConstructor
            | "System.Threading.Tasks", "ValueTask`1", ".ctor", Some [ parameter ] when
                parameter <> TypeDefn.GenericTypeParameter 0
                ->
                Named.ValueTaskConstructor
            | _ -> Named.Other
        | MetadataOperand.FromDynamicScope _
        | MetadataOperand.FromStubScope _ -> failwith "BUG: a body read from an image has no scope operand"

    let private frameworkKey (operand : MetadataOperand) : MetadataToken =
        match operand with
        | MetadataOperand.FromMetadata sourced -> sourced.Token
        | MetadataOperand.FromDynamicScope _
        | MetadataOperand.FromStubScope _ -> failwith "BUG: a body read from an image has no scope operand"

    [<Test>]
    let ``the matcher reads what the reference reads backwards, over every body of the framework under test``
        ()
        : unit
        =
        let frameworkDirectory = FrameworkUnderTest.sharedFrameworkDirectory ()
        let _, loggerFactory = LoggerFactory.makeTest ()
        let tally = emptyTally ()
        let mutable runtimeAsyncBodies = 0

        for path in Directory.GetFiles (frameworkDirectory, "*.dll") |> Array.sort do
            let assembly = Assembly.readFile loggerFactory path
            let named = frameworkNamed assembly

            for method in assembly.Methods.Values do
                let body =
                    match method.Body with
                    | MethodBody.Il body -> Some body
                    | MethodBody.AsyncIl body ->
                        runtimeAsyncBodies <- runtimeAsyncBodies + 1
                        Some body
                    | MethodBody.InternalCall
                    | MethodBody.PInvoke
                    | MethodBody.RuntimeProvided _
                    | MethodBody.Abstract
                    | MethodBody.MissingIl -> None

                match body with
                | None -> ()
                | Some body ->
                    check
                        named
                        frameworkKey
                        (BoxPattern.blockStarts body)
                        tally
                        $"%s{Path.GetFileName path}: %O{method}"
                        body

        // Every tail shape comes up in the IL of either runtime, and so do tail shapes that a
        // block's start splits.
        tally.Returns |> shouldBeGreaterThan 10000
        tally.NewValueTasks |> shouldBeGreaterThan 10
        tally.AsTasks |> shouldBeGreaterThan 10
        tally.SplitTails |> shouldBeGreaterThan 100

        match FrameworkUnderTest.runtime () with
        | EmulatedRuntime.Net10 -> ()
        | EmulatedRuntime.Net11 ->
            // RC1's runtime-async bodies await nearly two thousand calls, most through
            // `ConfigureAwait`, which a `ValueTask` takes through a local; four textual awaits are
            // split by a block's start.
            runtimeAsyncBodies |> shouldBeGreaterThan 800
            tally.AwaitsWithConfigure |> shouldBeGreaterThan 1000
            tally.AwaitsWithoutConfigure |> shouldBeGreaterThan 1000
            tally.AwaitsThroughLocal |> shouldBeGreaterThan 300
            tally.SplitAwaits |> shouldBeGreaterThan 0

    /// `StreamReader.ReadToEndAsyncInternal`, a runtime-async body of .NET 11's CoreLib, awaits
    /// `ReadBufferAsync` through `ConfigureAwait(false)`, its `ValueTask<int>` held in a local.
    [<Test>]
    let ``ReadToEndAsyncInternal awaits ReadBufferAsync at IL_0053`` () : unit =
        let coreLibPath = FrameworkUnderTest.requireCoreLibPathOf EmulatedRuntime.Net11
        let _, loggerFactory = LoggerFactory.makeTest ()
        let coreLib = Assembly.readFile loggerFactory coreLibPath

        let method =
            coreLib.Methods.Values
            |> Seq.find (fun m ->
                m.Name = "ReadToEndAsyncInternal"
                && m.RequiredDeclaringType.Namespace = "System.IO"
                && m.RequiredDeclaringType.Name = "StreamReader"
            )

        let body =
            match method.Body with
            | MethodBody.AsyncIl body -> body
            | other -> failwith $"expected runtime-async IL, got %O{other}"

        let describe (operand : MetadataOperand) : string =
            match operand with
            | MetadataOperand.FromMetadata sourced ->
                let ns, ty, name, _ = methodNamed coreLib sourced.Token
                $"%s{ns}.%s{ty}::%s{name}"
            | other -> failwith $"unexpected operand %O{other}"

        match body.Locations.[0x45] with
        | IlOp.UnaryMetadataToken (UnaryMetadataTokenIlOp.Call, callee) ->
            describe callee |> shouldEqual "System.IO.StreamReader::ReadBufferAsync"
        | other -> failwith $"expected a call at IL_0045, got %O{other}"

        AwaitPattern.awaitAfter body 0x45
        |> Option.map (AwaitFollower.map describe)
        |> shouldEqual (
            Some
                {
                    ConfigureAwait =
                        Some
                            {
                                ContinueOnCapturedContext = false
                                Method = "System.Threading.Tasks.ValueTask`1::ConfigureAwait"
                            }
                    Await = "System.Runtime.CompilerServices.AsyncHelpers::Await"
                    AwaitOffset = 0x53
                    AfterAwait = 0x58
                }
        )

    /// Decision 17's measurements on real .NET 11: which awaits of an async version leave no frame
    /// of it in a trace.
    [<Test>]
    let ``isTailAwait gives what real .NET 11 measured`` () : unit =
        let optimised : TailCallFacts =
            {
                AssemblyAllowsOptimisation = true
                CallerNoOptimization = false
                CallerNoInlining = false
                CallerIsEntryPoint = false
                ReturnTypesAgree = true
                ExactCalleeRequiresSecObject = false
            }

        let unoptimised =
            { optimised with
                AssemblyAllowsOptimisation = false
            }

        let cases : (string * AsyncVersionAwait<unit> * bool) list =
            [
                "a wrapped ret", AsyncVersionAwait.WrappedReturn, true
                "Task<int> M() => Leaf()", AsyncVersionAwait.TailCall (TailShape.Return, optimised), true
                "new ValueTask<int>(Leaf())", AsyncVersionAwait.TailCall (TailShape.NewValueTask (), optimised), true
                "Task M() => Leaf(), a Task<int> as a Task",
                AsyncVersionAwait.TailCall (
                    TailShape.Return,
                    { optimised with
                        ReturnTypesAgree = false
                    }
                ),
                false
                "a NoInlining M",
                AsyncVersionAwait.TailCall (
                    TailShape.Return,
                    { optimised with
                        CallerNoInlining = true
                    }
                ),
                false
                "LeafVT().AsTask()", AsyncVersionAwait.TailCall (TailShape.AsTask (), optimised), false
                "Task<int> M() => Leaf(), unoptimised",
                AsyncVersionAwait.TailCall (TailShape.Return, unoptimised),
                false
                "new ValueTask<int>(Leaf()), unoptimised",
                AsyncVersionAwait.TailCall (TailShape.NewValueTask (), unoptimised),
                false
            ]

        cases
        |> List.map (fun (name, await, _) -> name, AwaitPattern.isTailAwait await)
        |> shouldEqual (cases |> List.map (fun (name, _, expected) -> name, expected))

    /// The rest of the rule, read from the JIT and the VM rather than measured: a body the JIT does
    /// not optimise (`Tier0OptimizationEnabled`) makes no tail await of a call, nor does an entry
    /// point, nor a call of a method that may look for its caller's frame (`CEEInfo::canTailCall`).
    [<Test>]
    let ``isTailAwait refuses a tail await wherever canTailCall or Tier0OptimizationEnabled does`` () : unit =
        let optimised : TailCallFacts =
            {
                AssemblyAllowsOptimisation = true
                CallerNoOptimization = false
                CallerNoInlining = false
                CallerIsEntryPoint = false
                ReturnTypesAgree = true
                ExactCalleeRequiresSecObject = false
            }

        let refusing : (string * TailCallFacts) list =
            [
                "NoOptimization",
                { optimised with
                    CallerNoOptimization = true
                }
                "the entry point",
                { optimised with
                    CallerIsEntryPoint = true
                }
                "a callee requiring a security object",
                { optimised with
                    ExactCalleeRequiresSecObject = true
                }
            ]

        for shape in [ TailShape.Return ; TailShape.NewValueTask () ] do
            AwaitPattern.isTailAwait (AsyncVersionAwait.TailCall (shape, optimised))
            |> shouldEqual true

            refusing
            |> List.map (fun (name, facts) ->
                name, AwaitPattern.isTailAwait (AsyncVersionAwait.TailCall (shape, facts))
            )
            |> shouldEqual (refusing |> List.map (fun (name, _) -> name, false))
