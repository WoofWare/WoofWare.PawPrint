namespace WoofWare.PawPrint.Test

open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework

/// `Coverage`, which `CoverageSample` counts labels into.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestCoverageSample =

    let private counts (labels : 'label list) : ('label * int) list =
        labels |> List.countBy id |> List.sortBy (fun (label, _) -> sprintf "%A" label)

    let private countsAsCoverageHas (labels : 'label list) : ('label * int) list =
        let coverage = Coverage<'label> ()
        List.iter coverage.Hit labels

        for label in labels do
            coverage.Count label
            |> shouldEqual (labels |> List.filter ((=) label) |> List.length)

        coverage.Reached |> List.sortBy (fun (label, _) -> sprintf "%A" label)

    // `None` and `()` are null at run time.
    [<Test>]
    let ``Coverage counts each label as often as it is hit, a label represented as null too`` () : unit =
        Check.One (
            Config.QuickThrowOnFailure.WithMaxTest 300,
            fun (labels : int option list) -> countsAsCoverageHas labels |> shouldEqual (counts labels)
        )

        Check.One (
            Config.QuickThrowOnFailure.WithMaxTest 100,
            fun (labels : unit list) -> countsAsCoverageHas labels |> shouldEqual (counts labels)
        )

    [<Test>]
    let ``Coverage counts zero for a label it never reached`` () : unit =
        Check.One (
            Config.QuickThrowOnFailure.WithMaxTest 300,
            fun (labels : int option list) (other : int option) ->
                let coverage = Coverage<int option> ()
                labels |> List.filter ((<>) other) |> List.iter coverage.Hit
                coverage.Count other |> shouldEqual 0
        )
