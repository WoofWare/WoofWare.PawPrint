namespace WoofWare.PawPrint.Test

open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// `ExplicitFieldOffsets.place` against a transcription of CoreCLR's
/// `ReadOffsetsForExplicitLayout` and `SetFieldOffsets` (classlayoutinfo.cpp), done in 64-bit
/// arithmetic where CoreCLR uses `ClrSafeInt<UINT32>`. The boundary points are the ones measured on
/// real .NET 10 and 11 in `TestFabricatedFieldLayoutOffset`.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestExplicitFieldOffsets =

    /// CoreCLR's algorithm, step for step: a pass over the rows, adding the parent's size and
    /// checking the field's end, throwing on overflow; a pass over the fields in order, for a missing
    /// row and then for an offset above `INT32_MAX`; and `FieldDesc::SetOffset` over the fields in
    /// order.
    let private reference
        (parentSize : uint32)
        (fields : (string * uint32 * uint32 option) list)
        : Result<(string * int) list, ExplicitFieldOffsetRefusal<string>>
        =
        let limit = 0xFFFFFFFFUL

        let overflow =
            fields
            |> List.tryPick (fun (name, size, row) ->
                match row with
                | None -> None
                | Some offset ->
                    let placed = uint64 offset + uint64 parentSize

                    if placed > limit || placed + uint64 size > limit then
                        Some (ExplicitFieldOffsetRefusal.OffsetOverflows (name, offset))
                    else
                        None
            )

        match overflow with
        | Some refusal -> Error refusal
        | None ->

        let placed =
            fields
            |> List.map (fun (name, _, row) -> name, row |> Option.map (fun o -> uint64 o + uint64 parentSize))

        let perField =
            placed
            |> List.tryPick (fun (name, placed) ->
                match placed with
                | None -> Some (ExplicitFieldOffsetRefusal.NoOffset name)
                | Some p when p > uint64 System.Int32.MaxValue ->
                    let raw = fields |> List.pick (fun (n, _, row) -> if n = name then row else None)
                    Some (ExplicitFieldOffsetRefusal.NegativeOffset (name, raw))
                | Some _ -> None
            )

        match perField with
        | Some refusal -> Error refusal
        | None ->

        let lastReal = uint64 ((1 <<< 27) - 1 - 7)

        match
            placed
            |> List.tryPick (fun (name, placed) ->
                if placed.Value > lastReal then
                    let raw = fields |> List.pick (fun (n, _, row) -> if n = name then row else None)
                    Some (ExplicitFieldOffsetRefusal.BeyondFieldDescOffsetLimit (name, raw))
                else
                    None
            )
        with
        | Some refusal -> Error refusal
        | None -> placed |> List.map (fun (name, p) -> name, int p.Value) |> Ok

    [<RequireQualifiedAccess>]
    type private Outcome =
        | Placed
        | Overflows
        | NoOffset
        | Negative
        | Beyond

    [<Test>]
    let ``place agrees with CoreCLR's algorithm over drawn fields`` () : unit =
        let interesting =
            [
                0u
                1u
                0x07FFFFF4u
                0x07FFFFF8u
                0x07FFFFF9u
                0x7FFFFFFBu
                0x7FFFFFFFu
                0x80000000u
                0xFFFFFFF8u
                0xFFFFFFFBu
                0xFFFFFFFCu
                0xFFFFFFFFu
            ]

        let near : Gen<uint32> =
            gen {
                let! anchor = Gen.elements interesting
                let! delta = Gen.choose (-8, 8)
                return uint32 (int64 anchor + int64 delta)
            }

        let offset : Gen<uint32> =
            Gen.frequency
                [
                    3, near
                    2, Gen.choose (0, 64) |> Gen.map uint32
                    1, ArbMap.defaults |> ArbMap.generate<uint32>
                ]

        let field : Gen<uint32 * uint32 option> =
            gen {
                let! size = Gen.elements [ 1u ; 2u ; 4u ; 8u ; 16u ; 24u ]
                let! row = Gen.frequency [ 1, Gen.constant None ; 8, Gen.map Some offset ]
                return size, row
            }

        let case : Gen<uint32 * (string * uint32 * uint32 option) list> =
            gen {
                let! parent = Gen.frequency [ 4, Gen.constant 0u ; 1, Gen.elements [ 4u ; 8u ; 24u ] ; 1, near ]
                let! n = Gen.choose (1, 4)
                let! fields = Gen.listOfLength n field
                return parent, fields |> List.mapi (fun i (size, row) -> $"F%d{i}", size, row)
            }

        let property (cover : Outcome -> unit) (parent : uint32, fields : (string * uint32 * uint32 option) list) =
            let expected = reference parent fields

            match expected with
            | Ok _ -> cover Outcome.Placed
            | Error (ExplicitFieldOffsetRefusal.OffsetOverflows _) -> cover Outcome.Overflows
            | Error (ExplicitFieldOffsetRefusal.NoOffset _) -> cover Outcome.NoOffset
            | Error (ExplicitFieldOffsetRefusal.NegativeOffset _) -> cover Outcome.Negative
            | Error (ExplicitFieldOffsetRefusal.BeyondFieldDescOffsetLimit _) -> cover Outcome.Beyond

            ExplicitFieldOffsets.place parent fields |> shouldEqual expected

        let coverage =
            CoverageSample.check (Config.QuickThrowOnFailure.WithMaxTest 2000) (Arb.fromGen case) property

        for label in
            [
                Outcome.Placed
                Outcome.Overflows
                Outcome.NoOffset
                Outcome.Negative
                Outcome.Beyond
            ] do
            coverage.Count label |> shouldBeGreaterThan 100

    /// `B : int` at each offset measured on real .NET, after `A : int` at 0, with no parent.
    [<TestCase(0x10u, "placed")>]
    [<TestCase(0x07FFFFF8u, "placed")>]
    [<TestCase(0x07FFFFF9u, "beyond")>]
    [<TestCase(0x7FFFFFFFu, "beyond")>]
    [<TestCase(0x80000000u, "negative")>]
    [<TestCase(0xFFFFFFFBu, "negative")>]
    [<TestCase(0xFFFFFFFCu, "overflows")>]
    [<TestCase(0xFFFFFFFFu, "overflows")>]
    let ``place reproduces the measured boundaries`` (offset : uint32, outcome : string) : unit =
        let actual =
            match ExplicitFieldOffsets.place 0u [ "A", 4u, Some 0u ; "B", 4u, Some offset ] with
            | Ok placed ->
                placed |> shouldEqual [ "A", 0 ; "B", int offset ]
                "placed"
            | Error (ExplicitFieldOffsetRefusal.BeyondFieldDescOffsetLimit ("B", o))
            | Error (ExplicitFieldOffsetRefusal.NegativeOffset ("B", o))
            | Error (ExplicitFieldOffsetRefusal.OffsetOverflows ("B", o)) when o <> offset ->
                failwith $"refusal names offset %x{o}, not %x{offset}"
            | Error (ExplicitFieldOffsetRefusal.BeyondFieldDescOffsetLimit ("B", _)) -> "beyond"
            | Error (ExplicitFieldOffsetRefusal.NegativeOffset ("B", _)) -> "negative"
            | Error (ExplicitFieldOffsetRefusal.OffsetOverflows ("B", _)) -> "overflows"
            | Error other -> failwith $"unexpected refusal %A{other}"

        actual |> shouldEqual outcome

    [<Test>]
    let ``an overflow is reported before a missing row, which is reported before a negative offset`` () : unit =
        ExplicitFieldOffsets.place 0u [ "A", 4u, Some 0x80000000u ; "B", 4u, None ; "C", 4u, Some 0xFFFFFFFFu ]
        |> shouldEqual (Error (ExplicitFieldOffsetRefusal.OffsetOverflows ("C", 0xFFFFFFFFu)))

        ExplicitFieldOffsets.place 0u [ "A", 4u, Some 0x80000000u ; "B", 4u, None ]
        |> shouldEqual (Error (ExplicitFieldOffsetRefusal.NegativeOffset ("A", 0x80000000u)))

        ExplicitFieldOffsets.place 0u [ "A", 4u, Some 0x08000000u ; "B", 4u, None ]
        |> shouldEqual (Error (ExplicitFieldOffsetRefusal.NoOffset "B"))
