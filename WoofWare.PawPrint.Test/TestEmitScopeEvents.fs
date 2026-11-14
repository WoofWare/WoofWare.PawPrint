namespace WoofWare.PawPrint.Test

open System
open System.Collections.Immutable
open System.Reflection
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint
open WoofWare.PawPrint.Test.EmitScopeGen

/// A dynamic module's events and their accessors: `EmitScopeDefinitions.defineEvent` and
/// `defineMethodSemantics` against models of `RegMeta::_DefineEvent` and `_DefineMethodSemantics`, and
/// `EmitScope.addEvent`'s refusals. `TestEmitScopeProperties` checks events through the image against real
/// .NET's `TypeBuilder`.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestEmitScopeEvents =

    let private methodSignature : ImmutableArray<byte> =
        ImmutableArray.Create<byte> [| 0x20uy ; 0x00uy ; 0x01uy |]

    /// `_SetEventProps1`: `-1` leaves the flags unset, so 0; otherwise without `evReservedMask`
    /// (`RTSpecialName`), in the column's two bytes.
    let private storedFlags (flags : int) : int =
        if flags = -1 then 0 else flags &&& ~~~0x0400 &&& 0xFFFF

    /// A scope with `<Module>` and `types` types, each with `methodsPerType` methods, defined type by
    /// type, so that type `t` is TypeDef `t + 2` and its method `i` is MethodDef `t * methodsPerType + i + 1`;
    /// then TypeRef 1 and TypeSpec 1, so that an event type can be of each kind.
    let private withTypes (types : int) (methodsPerType : int) : EmitScope =
        let scope = EmitScope.create "Events" Guid.Empty

        let scope =
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

        let name, scope = EmitScope.internString "Action" scope
        let ns, scope = EmitScope.internString "System" scope

        let _, scope =
            EmitScope.addTypeRef
                {
                    ResolutionScope = ResolutionScope.Module
                    Name = name
                    Namespace = ns
                }
                scope

        let signature, scope =
            EmitScope.internBlob (ImmutableArray.Create<byte> [| 0x1Duy ; 0x08uy |]) scope

        EmitScope.addTypeSpec
            ({
                Signature = signature
            }
            : TypeSpecRow)
            scope
        |> snd

    /// The event types `withTypes` provides: each type's TypeDef, TypeRef 1 and TypeSpec 1.
    let private eventTypes (scope : EmitScope) : TypeDefOrRef list =
        typeDefOrRefs scope
        |> List.filter (fun t -> t <> TypeDefOrRef.TypeDef (TypeDefRid 1))

    let private genFlags : Gen<int> =
        Gen.frequency
            [
                1, Gen.constant -1
                2,
                Gen.elements
                    [
                        0
                        0x0200
                        0x0400
                        0x0600
                        0xF400
                        0xFBFF
                        0xFFFF
                        0x1FFFF
                        0x10000
                        0x7FFF0600
                        -2
                    ]
                3, Gen.choose (Int32.MinValue, Int32.MaxValue)
            ]

    [<Test>]
    let ``each event is a new row, in definition order across the module, its flags as the emitter stores them``
        ()
        : unit
        =
        let property (types : int) (definitions : (int * string * int * int) list) : unit =
            let start = withTypes types 0
            let targets = eventTypes start

            let scope =
                (start, definitions)
                ||> List.fold (fun scope (owner, name, flags, eventType) ->
                    let rid, after =
                        EmitScopeDefinitions.defineEvent
                            (TypeDefRid (owner % types + 2))
                            name
                            (pick eventType targets).Value
                            flags
                            scope

                    rid.Rid |> shouldEqual ((EmitScope.events scope).Count + 1)
                    after
                )

            EmitScope.events scope
            |> Seq.map (fun row -> row.Owner.Rid, EmitScope.stringAt row.Name scope, int row.Flags, row.EventType)
            |> List.ofSeq
            |> shouldEqual (
                definitions
                |> List.map (fun (owner, name, flags, eventType) ->
                    owner % types + 2, name, storedFlags flags, (pick eventType targets).Value
                )
            )

        let gen =
            gen {
                let! types = Gen.choose (1, 4)

                // Few names, so that a type often has two events with one name.
                let! definitions =
                    Gen.listOf (
                        gen {
                            let! owner = Gen.choose (0, 10)
                            let! name = Gen.elements [ "A" ; "B" ; "" ]
                            let! flags = genFlags
                            let! eventType = choice
                            return owner, name, flags, eventType
                        }
                    )

                return types, List.ofSeq definitions
            }

        Check.One (
            Config.QuickThrowOnFailure.WithMaxTest 300,
            Prop.forAll (Arb.fromGen gen) (fun (types, definitions) -> property types definitions)
        )

    [<Test>]
    let ``the flags each measured value stores`` () : unit =
        // Measured on .NET 10 and 11: each single bit, then nine other values, through `DefineEvent`.
        let values =
            [ for i in 0..31 -> 1 <<< i ]
            @ [ 0 ; 0x0600 ; 0xFFFF ; 0xFFFE ; 0xFBFF ; 0x7FFF0600 ; -1 ; -2 ; 0x1FFFF ]

        let expected =
            [ for i in 0..15 -> if i = 10 then 0 else 1 <<< i ]
            @ List.replicate 16 0
            @ [ 0 ; 0x0200 ; 0xFBFF ; 0xFBFE ; 0xFBFF ; 0x0200 ; 0 ; 0xFBFE ; 0xFBFF ]

        let scope =
            (withTypes 1 0, values)
            ||> List.fold (fun scope flags ->
                EmitScopeDefinitions.defineEvent (TypeDefRid 2) "F" (TypeDefOrRef.TypeDef (TypeDefRid 2)) flags scope
                |> snd
            )

        EmitScope.events scope
        |> Seq.map (fun row -> int row.Flags)
        |> List.ofSeq
        |> shouldEqual expected

    [<Test>]
    let ``each event accessor is a new row, in the order added`` () : unit =
        let semanticsChoices =
            [
                MethodSemanticsAttributes.Adder
                MethodSemanticsAttributes.Remover
                MethodSemanticsAttributes.Raiser
                MethodSemanticsAttributes.Other
            ]

        // An event's and a property's rows, interleaved; `isEvent` picks the kind.
        let property (calls : (bool * int * int * int) list) : unit =
            let start = withTypes 2 3

            let start =
                (start, [ 0..2 ])
                ||> List.fold (fun scope i ->
                    let scope =
                        EmitScopeDefinitions.defineEvent
                            (TypeDefRid (i % 2 + 2))
                            $"E%d{i}"
                            (TypeDefOrRef.TypeDef (TypeDefRid 2))
                            0
                            scope
                        |> snd

                    EmitScopeDefinitions.defineProperty
                        (TypeDefRid (i % 2 + 2))
                        $"P%d{i}"
                        (ImmutableArray.Create<byte> [| 0x08uy ; 0x00uy ; 0x08uy |])
                        0
                        scope
                    |> snd
                )

            let association (isEvent : bool) (a : int) (scope : EmitScope) =
                if isEvent then
                    HasSemantics.Event (EmitScope.tryEventRid (a % 3 + 1) scope).Value
                else
                    HasSemantics.Property (EmitScope.tryPropertyRid (a % 3 + 1) scope).Value

            let scope =
                (start, calls)
                ||> List.fold (fun scope (isEvent, a, s, m) ->
                    EmitScopeDefinitions.defineMethodSemantics
                        (association isEvent a scope)
                        (pick s semanticsChoices).Value
                        (MethodDefRid (m % 6 + 1))
                        scope
                )

            EmitScope.methodSemantics scope
            |> Seq.map (fun row -> row.Association.CodedIndex, row.Semantics, row.Method.Rid)
            |> List.ofSeq
            |> shouldEqual (
                calls
                |> List.map (fun (isEvent, a, s, m) ->
                    ((a % 3 + 1) <<< 1) ||| (if isEvent then 0 else 1), (pick s semanticsChoices).Value, m % 6 + 1
                )
            )

        let gen =
            Gen.listOf (
                gen {
                    let! isEvent = Gen.elements [ true ; true ; false ]
                    let! a = choice
                    let! s = choice
                    let! m = choice
                    return isEvent, a, s, m
                }
            )
            |> Gen.map List.ofSeq

        Check.One (Config.QuickThrowOnFailure.WithMaxTest 300, Prop.forAll (Arb.fromGen gen) property)

    [<Test>]
    let ``an accessor naming an event the scope lacks is refused`` () : unit =
        let small = withTypes 1 1

        let large =
            EmitScopeDefinitions.defineEvent (TypeDefRid 2) "E" (TypeDefOrRef.TypeDef (TypeDefRid 2)) 0 (withTypes 1 1)
            |> snd

        let ev = (EmitScope.tryEventRid 1 large).Value

        // `small` has no event.
        Assert.Throws<ArgumentException> (fun () ->
            EmitScopeDefinitions.defineMethodSemantics
                (HasSemantics.Event ev)
                MethodSemanticsAttributes.Adder
                (EmitScope.tryMethodDefRid 1 small).Value
                small
            |> ignore<EmitScope>
        )
        |> ignore<ArgumentException>

        EmitScope.tryEventRid 1 small |> shouldEqual None
        EmitScope.tryEventRid 2 large |> shouldEqual None
        EmitScope.tryEventRid 0 large |> shouldEqual None

    [<Test>]
    let ``an event naming an owner, a name or an event type the scope lacks is refused`` () : unit =
        let small = withTypes 1 0
        let large = withTypes 2 0
        let name, large = EmitScope.internString "a name only the larger scope holds" large

        let signature, large =
            EmitScope.internBlob (ImmutableArray.Create<byte> [| 0x1Duy ; 0x0Euy |]) large

        let typeSpec, large =
            EmitScope.addTypeSpec
                ({
                    Signature = signature
                }
                : TypeSpecRow)
                large

        let smallName, small = EmitScope.internString "E" small

        let row : EventRow =
            {
                Owner = TypeDefRid 2
                Flags = EventAttributes.None
                Name = smallName
                EventType = TypeDefOrRef.TypeDef (TypeDefRid 2)
            }

        EmitScope.addEvent row small |> fst |> (fun rid -> rid.Rid) |> shouldEqual 1

        let refused (row : EventRow) =
            Assert.Throws<ArgumentException> (fun () -> EmitScope.addEvent row small |> ignore<EventRid * EmitScope>)
            |> ignore<ArgumentException>

        // TypeDef 3 is the larger scope's second type.
        refused
            { row with
                Owner = TypeDefRid 3
            }

        refused
            { row with
                Name = name
            }

        refused
            { row with
                EventType = TypeDefOrRef.TypeDef (TypeDefRid 3)
            }

        refused
            { row with
                EventType = TypeDefOrRef.TypeRef (TypeRefRid 2)
            }

        refused
            { row with
                EventType = TypeDefOrRef.TypeSpec typeSpec
            }

        refused
            { row with
                Flags = enum<EventAttributes> 0x10000
            }
