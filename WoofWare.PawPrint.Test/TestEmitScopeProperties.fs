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

/// A dynamic module's properties and their accessors: `EmitScopeDefinitions.defineProperty` and
/// `defineMethodSemantics` against models of `RegMeta::DefineProperty` and `_DefineMethodSemantics`;
/// `ReadWriteMetadataLookup.rowsOfKey`, the order in which CoreCLR's read-write metadata finds a key's
/// rows; and both, through the image, against real .NET's `TypeBuilder` in this process. The last covers
/// the Domain and the read rule, not the QCall handlers' reading of their arguments, which only the guests
/// cover.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestEmitScopeProperties =

    let private propertySignature : ImmutableArray<byte> =
        ImmutableArray.Create<byte> [| 0x08uy ; 0x00uy ; 0x08uy |]

    let private methodSignature : ImmutableArray<byte> =
        ImmutableArray.Create<byte> [| 0x20uy ; 0x00uy ; 0x08uy |]

    /// `_SetPropertyProps`: `-1` leaves the flags unset, so 0; otherwise without `prReservedMask`, in the
    /// column's two bytes.
    let private storedFlags (flags : int) : int =
        if flags = -1 then 0 else flags &&& ~~~0xF400 &&& 0xFFFF

    /// A scope with `<Module>` and `types` types, each with `methodsPerType` methods, defined type by
    /// type, so that type `t`'s method `i` is MethodDef `t * methodsPerType + i + 1`.
    let private withTypes (types : int) (methodsPerType : int) : EmitScope =
        let scope = EmitScope.create "Props" Guid.Empty

        (scope, [ 0 .. types - 1 ])
        ||> List.fold (fun scope t ->
            let typeDef, scope =
                match
                    EmitScopeDefinitions.defineTopLevelTypeDef
                        $"T%d{t}"
                        (TypeAttributes.Public ||| TypeAttributes.Abstract)
                        None
                        []
                        scope
                with
                | TypeDefDefinition.Defined (rid, scope) -> rid, scope
                | TypeDefDefinition.Duplicate _ -> failwith $"T%d{t} is defined twice"

            (scope, [ 0 .. methodsPerType - 1 ])
            ||> List.fold (fun scope i ->
                EmitScopeDefinitions.defineMethod
                    typeDef
                    $"M%d{t}_%d{i}"
                    methodSignature
                    (MethodAttributes.Public
                     ||| MethodAttributes.Abstract
                     ||| MethodAttributes.Virtual
                     ||| MethodAttributes.NewSlot
                     ||| MethodAttributes.HideBySig)
                    scope
                |> snd
            )
        )

    let private genFlags : Gen<int> =
        Gen.frequency
            [
                1, Gen.constant -1
                1, Gen.elements [ 0 ; 0x0200 ; 0x0400 ; 0x1000 ; 0xF400 ; 0x0BFF ; 0xFFFF ; 0x10000 ]
                3, Gen.choose (Int32.MinValue, Int32.MaxValue)
            ]

    [<Test>]
    let ``each property is a new row, in definition order across the module, its flags as the emitter stores them``
        ()
        : unit
        =
        let property (types : int) (definitions : (int * string * int) list) : unit =
            let start = withTypes types 0

            let scope =
                (start, definitions)
                ||> List.fold (fun scope (owner, name, flags) ->
                    let rid, after =
                        EmitScopeDefinitions.defineProperty
                            (TypeDefRid (owner % types + 2))
                            name
                            propertySignature
                            flags
                            scope

                    rid.Rid |> shouldEqual ((EmitScope.properties scope).Count + 1)
                    after
                )

            EmitScope.properties scope
            |> Seq.map (fun row ->
                row.Owner.Rid,
                EmitScope.stringAt row.Name scope,
                int row.Flags,
                List.ofSeq (EmitScope.blobAt row.Signature scope)
            )
            |> List.ofSeq
            |> shouldEqual (
                definitions
                |> List.map (fun (owner, name, flags) ->
                    owner % types + 2, name, storedFlags flags, List.ofSeq propertySignature
                )
            )

        let gen =
            gen {
                let! types = Gen.choose (1, 4)

                // Few names, so that a type often has two properties with one name and signature.
                let! definitions = Gen.listOf (Gen.zip3 (Gen.choose (0, 10)) (Gen.elements [ "A" ; "B" ; "" ]) genFlags)

                return types, List.ofSeq definitions
            }

        Check.One (
            Config.QuickThrowOnFailure.WithMaxTest 300,
            Prop.forAll (Arb.fromGen gen) (fun (types, definitions) -> property types definitions)
        )

    [<Test>]
    let ``each accessor is a new row, in the order added`` () : unit =
        let property (calls : (int * int * int) list) : unit =
            let start = withTypes 2 3

            let start =
                (start, [ 0..2 ])
                ||> List.fold (fun scope i ->
                    EmitScopeDefinitions.defineProperty (TypeDefRid (i % 2 + 2)) $"P%d{i}" propertySignature 0 scope
                    |> snd
                )

            let semantics (c : int) =
                [
                    MethodSemanticsAttributes.Getter
                    MethodSemanticsAttributes.Setter
                    MethodSemanticsAttributes.Other
                ]
                |> List.item (c % 3)

            let scope =
                (start, calls)
                ||> List.fold (fun scope (p, s, m) ->
                    EmitScopeDefinitions.defineMethodSemantics
                        (HasSemantics.Property (EmitScope.tryPropertyRid (p % 3 + 1) scope).Value)
                        (semantics s)
                        (MethodDefRid (m % 6 + 1))
                        scope
                )

            EmitScope.methodSemantics scope
            |> Seq.map (fun row -> row.Association.CodedIndex, row.Semantics, row.Method.Rid)
            |> List.ofSeq
            |> shouldEqual (
                calls
                |> List.map (fun (p, s, m) -> ((p % 3 + 1) <<< 1) ||| 1, semantics s, m % 6 + 1)
            )

        Check.One (
            Config.QuickThrowOnFailure.WithMaxTest 300,
            Prop.forAll (Arb.fromGen (Gen.listOf (Gen.zip3 choice choice choice) |> Gen.map List.ofSeq)) property
        )

    [<Test>]
    let ``an accessor naming a property or method the scope lacks is refused`` () : unit =
        let small = withTypes 1 1
        let large = withTypes 1 2

        let large =
            EmitScopeDefinitions.defineProperty (TypeDefRid 2) "P" propertySignature 0 large
            |> snd

        let property = (EmitScope.tryPropertyRid 1 large).Value
        let method = (EmitScope.tryMethodDefRid 2 large).Value

        // `small` has no property, and one method.
        Assert.Throws<ArgumentException> (fun () ->
            EmitScopeDefinitions.defineMethodSemantics
                (HasSemantics.Property property)
                MethodSemanticsAttributes.Getter
                (EmitScope.tryMethodDefRid 1 small).Value
                small
            |> ignore<EmitScope>
        )
        |> ignore<ArgumentException>

        let small =
            EmitScopeDefinitions.defineProperty (TypeDefRid 2) "P" propertySignature 0 small
            |> snd

        Assert.Throws<ArgumentException> (fun () ->
            EmitScopeDefinitions.defineMethodSemantics
                (HasSemantics.Property property)
                MethodSemanticsAttributes.Getter
                method
                small
            |> ignore<EmitScope>
        )
        |> ignore<ArgumentException>

        EmitScope.tryPropertyRid 2 small |> shouldEqual None
        EmitScope.tryPropertyRid 0 small |> shouldEqual None

    [<Test>]
    let ``a key's rows are found in RID order below 25 rows, and in reverse from 25`` () : unit =
        ReadWriteMetadataLookup.rowsOfKey 24 [ 1 ; 2 ; 3 ] |> shouldEqual [ 1 ; 2 ; 3 ]
        ReadWriteMetadataLookup.rowsOfKey 25 [ 1 ; 2 ; 3 ] |> shouldEqual [ 3 ; 2 ; 1 ]
        ReadWriteMetadataLookup.rowsOfKey 0 ([] : int list) |> shouldEqual []

        let property (tableRows : int) (matching : int list) : unit =
            let matching = matching |> List.truncate tableRows

            ReadWriteMetadataLookup.rowsOfKey tableRows matching
            |> shouldEqual (if tableRows >= 25 then List.rev matching else matching)

        Check.One (
            Config.QuickThrowOnFailure.WithMaxTest 300,
            Prop.forAll
                (Arb.fromGen (Gen.zip (Gen.choose (0, 60)) (Gen.listOf choice |> Gen.map List.ofSeq)))
                (fun (rows, matching) -> property rows matching)
        )

        // More matching rows than the table holds is a caller's fault.
        Assert.Throws<ArgumentException> (fun () -> ReadWriteMetadataLookup.rowsOfKey 1 [ 1 ; 2 ] |> ignore<int list>)
        |> ignore<ArgumentException>

    [<Test>]
    let ``a key's rows since creation have two orders only if the table crossed 25 rows after its owner was created``
        ()
        : unit
        =
        ReadWriteMetadataLookup.rowsOfKeySinceCreation 2 24 [ 1 ; 2 ]
        |> shouldEqual (RowsSinceCreation.OneOrder [ 1 ; 2 ])

        ReadWriteMetadataLookup.rowsOfKeySinceCreation 25 30 [ 1 ; 2 ]
        |> shouldEqual (RowsSinceCreation.OneOrder [ 2 ; 1 ])

        ReadWriteMetadataLookup.rowsOfKeySinceCreation 24 25 [ 1 ; 2 ]
        |> shouldEqual (RowsSinceCreation.TwoOrders ([ 1 ; 2 ], [ 2 ; 1 ]))

        let property (atCreation : int) (added : int) (matching : int list) : unit =
            let matching = matching |> List.truncate atCreation
            let now = atCreation + added

            match ReadWriteMetadataLookup.rowsOfKeySinceCreation atCreation now matching with
            | RowsSinceCreation.OneOrder order ->
                (now < 25 || atCreation >= 25) |> shouldEqual true
                order |> shouldEqual (ReadWriteMetadataLookup.rowsOfKey now matching)
            | RowsSinceCreation.TwoOrders (inOrderAdded, reversed) ->
                (atCreation < 25 && now >= 25) |> shouldEqual true
                inOrderAdded |> shouldEqual matching
                reversed |> shouldEqual (List.rev matching)

        Check.One (
            Config.QuickThrowOnFailure.WithMaxTest 500,
            Prop.forAll
                (Arb.fromGen (
                    Gen.zip3 (Gen.choose (0, 60)) (Gen.choose (0, 30)) (Gen.listOf choice |> Gen.map List.ofSeq)
                ))
                (fun (atCreation, added, matching) -> property atCreation added matching)
        )

        // Rows are only added, and a key's rows all precede its owner's creation.
        Assert.Throws<ArgumentException> (fun () ->
            ReadWriteMetadataLookup.rowsOfKeySinceCreation 5 4 [ 1 ]
            |> ignore<RowsSinceCreation<int>>
        )
        |> ignore<ArgumentException>

        Assert.Throws<ArgumentException> (fun () ->
            ReadWriteMetadataLookup.rowsOfKeySinceCreation -1 4 ([] : int list)
            |> ignore<RowsSinceCreation<int>>
        )
        |> ignore<ArgumentException>

        Assert.Throws<ArgumentException> (fun () ->
            ReadWriteMetadataLookup.rowsOfKeySinceCreation 1 30 [ 1 ; 2 ]
            |> ignore<RowsSinceCreation<int>>
        )
        |> ignore<ArgumentException>

    /// One call a schedule makes, on real .NET's `TypeBuilder` and on a scope alike.
    type private Call =
        /// `DefineProperty` on type `owner`, with these flags.
        | Define of owner : int * flags : int
        /// `SetGetMethod` (0), `SetSetMethod` (1) or `AddOtherMethod` (2) on property `property`, with one
        /// of its type's methods, if there is a property.
        | Accessor of property : int * semantics : int * method : int

    /// What reflection reports of one property: its name, token and flags, its getter's and setter's
    /// names, and its accessors' names in `GetAccessors(true)`'s order.
    type private Reported =
        {
            Name : string
            Token : int
            Flags : int
            Getter : string option
            Setter : string option
            Accessors : string list
        }

    /// What the cases reached.
    [<RequireQualifiedAccess>]
    type private Reach =
        /// The module had 25 or more MethodSemantics rows, and a property two of one semantics.
        | Reversed
        /// The module had fewer than 25 rows, and a property two of one semantics.
        | InOrder
        /// The image needed a `PropertyPtr`.
        | PointerTable

    [<Test>]
    let ``properties read back as real .NET's TypeBuilder reports them`` () : unit =
        let all =
            BindingFlags.DeclaredOnly
            ||| BindingFlags.Public
            ||| BindingFlags.NonPublic
            ||| BindingFlags.Instance
            ||| BindingFlags.Static

        let property (cover : Reach -> unit) (types : int) (methodsPerType : int) (calls : Call list) : unit =
            let assembly =
                AssemblyBuilder.DefineDynamicAssembly (AssemblyName "PropsOracle", AssemblyBuilderAccess.RunAndCollect)

            let host = assembly.DefineDynamicModule "PropsOracle"

            let hostTypes =
                [|
                    for t in 0 .. types - 1 do
                        yield host.DefineType ($"T%d{t}", TypeAttributes.Public ||| TypeAttributes.Abstract)
                |]

            let hostMethods =
                [|
                    for t in 0 .. types - 1 do
                        yield
                            [|
                                for i in 0 .. methodsPerType - 1 do
                                    yield
                                        hostTypes.[t]
                                            .DefineMethod (
                                                $"M%d{t}_%d{i}",
                                                MethodAttributes.Public
                                                ||| MethodAttributes.Abstract
                                                ||| MethodAttributes.Virtual
                                                ||| MethodAttributes.NewSlot
                                                ||| MethodAttributes.HideBySig,
                                                typeof<int>,
                                                Type.EmptyTypes
                                            )
                            |]
                |]

            // Every method's token now, type by type, so that the scope's MethodDef RIDs agree without
            // modelling when a `MethodBuilder` is given its token.
            for t in 0 .. types - 1 do
                for i in 0 .. methodsPerType - 1 do
                    hostMethods.[t].[i].MetadataToken
                    |> shouldEqual (0x06000000 ||| (t * methodsPerType + i + 1))

            let mutable scope = withTypes types methodsPerType
            let hostProperties = ResizeArray<PropertyBuilder * int> ()

            for call in calls do
                match call with
                | Call.Define (owner, flags) ->
                    let owner = owner % types
                    let name = $"P%d{hostProperties.Count}"

                    let builder =
                        hostTypes.[owner]
                            .DefineProperty (name, enum<PropertyAttributes> flags, typeof<int>, Type.EmptyTypes)

                    hostProperties.Add (builder, owner)

                    scope <-
                        EmitScopeDefinitions.defineProperty (TypeDefRid (owner + 2)) name propertySignature flags scope
                        |> snd
                | Call.Accessor (p, semantics, m) when hostProperties.Count > 0 ->
                    let p = p % hostProperties.Count
                    let builder, owner = hostProperties.[p]
                    let m = m % methodsPerType
                    let method = hostMethods.[owner].[m]

                    let attribute =
                        match semantics % 3 with
                        | 0 ->
                            builder.SetGetMethod method
                            MethodSemanticsAttributes.Getter
                        | 1 ->
                            builder.SetSetMethod method
                            MethodSemanticsAttributes.Setter
                        | _ ->
                            builder.AddOtherMethod method
                            MethodSemanticsAttributes.Other

                    scope <-
                        EmitScopeDefinitions.defineMethodSemantics
                            (HasSemantics.Property (EmitScope.tryPropertyRid (p + 1) scope).Value)
                            attribute
                            (MethodDefRid (owner * methodsPerType + m + 1))
                            scope
                | Call.Accessor _ -> ()

            let created = hostTypes |> Array.map (fun t -> t.CreateType ())

            let reportedByHost =
                [
                    for t in created do
                        yield
                            [
                                for p in t.GetProperties all do
                                    let name (m : MethodInfo) = m.Name

                                    yield
                                        {
                                            Name = p.Name
                                            Token = p.MetadataToken
                                            Flags = int p.Attributes
                                            Getter = p.GetGetMethod true |> Option.ofObj |> Option.map name
                                            Setter = p.GetSetMethod true |> Option.ofObj |> Option.map name
                                            Accessors = p.GetAccessors true |> Seq.map name |> List.ofSeq
                                        }
                            ]
                ]

            let pe, reader = readImage (EmitScopeImage.write scope)
            use _ = pe
            let semanticsRows = methodSemanticsRows pe reader

            let methodName (rid : int) =
                reader.GetString (reader.GetMethodDefinition (MetadataTokens.MethodDefinitionHandle rid)).Name

            // What `Associates.AssignAssociates` makes of each property's rows, found as CoreCLR's read-write
            // metadata finds them; and `RuntimeType`'s `PopulateProperties`, which lists a class's property
            // only if no earlier one's public getter (or else setter) has its vtable slot, and each of these
            // abstract methods has a slot of its own.
            let reportedByImage =
                [
                    for t in 0 .. types - 1 do
                        let definition =
                            reader.GetTypeDefinition (MetadataTokens.TypeDefinitionHandle (t + 2))

                        let mutable usedSlots = Set.empty

                        yield
                            [
                                for handle in definition.GetProperties () do
                                    let rid =
                                        MetadataTokens.GetRowNumber (
                                            PropertyDefinitionHandle.op_Implicit handle : EntityHandle
                                        )

                                    let row = reader.GetPropertyDefinition handle
                                    let association = (rid <<< 1) ||| 1

                                    let rows =
                                        semanticsRows
                                        |> List.filter (fun (_, _, a) -> a = association)
                                        |> List.map (fun (s, m, _) -> s, m)
                                        |> ReadWriteMetadataLookup.rowsOfKey semanticsRows.Length

                                    let pick (semantics : int) =
                                        rows
                                        |> List.filter (fun (s, _) -> s = semantics)
                                        |> List.tryLast
                                        |> Option.map snd

                                    let getter = pick 2
                                    let setter = pick 1
                                    let others = rows |> List.filter (fun (s, _) -> s = 4) |> List.map snd

                                    let listed =
                                        match Option.orElse setter getter with
                                        | Some slot when usedSlots.Contains slot -> false
                                        | Some slot ->
                                            usedSlots <- usedSlots.Add slot
                                            true
                                        | None -> true

                                    if listed then
                                        yield
                                            {
                                                Name = reader.GetString row.Name
                                                Token = 0x17000000 ||| rid
                                                Flags = int row.Attributes
                                                Getter = getter |> Option.map methodName
                                                Setter = setter |> Option.map methodName
                                                Accessors =
                                                    [
                                                        yield! Option.toList getter
                                                        yield! Option.toList setter
                                                        yield! others
                                                    ]
                                                    |> List.map methodName
                                            }
                            ]
                ]

            reportedByImage |> shouldEqual reportedByHost

            let repeated =
                EmitScope.methodSemantics scope
                |> Seq.countBy (fun row -> row.Association, row.Semantics)
                |> Seq.exists (fun (_, n) -> n > 1)

            if repeated then
                if semanticsRows.Length >= 25 then
                    cover Reach.Reversed
                else
                    cover Reach.InOrder

            if (propertyPtrRows pe reader).IsSome then
                cover Reach.PointerTable

        let genCall =
            Gen.frequency
                [
                    1, Gen.map2 (fun o f -> Call.Define (o, f)) choice genFlags
                    4, Gen.map3 (fun p s m -> Call.Accessor (p, s, m)) choice choice choice
                ]

        let gen =
            gen {
                let! types = Gen.choose (1, 3)
                let! methodsPerType = Gen.choose (1, 4)
                // Enough calls that many modules reach 25 MethodSemantics rows, and many do not.
                let! length = Gen.choose (0, 60)
                let! calls = Gen.listOfLength length genCall
                return types, methodsPerType, List.ofSeq calls
            }

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 150)
                (Arb.fromGen gen)
                (fun cover (types, methodsPerType, calls) -> property cover types methodsPerType calls)

        coverage.Count Reach.Reversed |> shouldBeGreaterThan 20
        coverage.Count Reach.InOrder |> shouldBeGreaterThan 20
        coverage.Count Reach.PointerTable |> shouldBeGreaterThan 20
