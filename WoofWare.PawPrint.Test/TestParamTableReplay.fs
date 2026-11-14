namespace WoofWare.PawPrint.Test

open FsUnitTyped
open NUnit.Framework

/// `ParamTableReplay`, pinned to the eighteen scenarios measured on .NET 10.0.14 and 11.0.0 (the probes of
/// stage 5x's amended decisions in `docs/plans/2026-11-04-type-builder.md`, `5x-impl/eotprobe` and
/// `5x-decide/probe2`). Each scenario's methods are rows of the module in the order the guest's calls gave them
/// tokens, and each parameter call names a method's RID and a sequence.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestParamTableReplay =

    type ReplayOp =
        /// A MethodDef row, as reading a `MethodBuilder`'s token, or defining a parameter on a method without
        /// one, adds it and every earlier method of its type.
        | Method
        | Param of md : int * sequence : int

    /// What a scenario measured: every parameter call's token up to the first that strands rows, and that call's
    /// position among the parameter calls, if one does; with no strand, each method's parameters in the order
    /// reflection lists them, as Param RIDs.
    type Measured =
        {
            Tokens : int list
            StrandsAt : int option
            Runs : int list list
        }

    type Scenario =
        {
            Name : string
            Ops : ReplayOp list
            Measured : Measured
        }

    let private stranded (name : string) (ops : ReplayOp list) (at : int) : Scenario =
        {
            Name = name
            Ops = ops
            Measured =
                {
                    Tokens = [ 1..at ]
                    StrandsAt = Some at
                    Runs = []
                }
        }

    let private fine (name : string) (ops : ReplayOp list) (runs : int list list) : Scenario =
        let calls =
            ops
            |> List.filter (
                function
                | ReplayOp.Param _ -> true
                | ReplayOp.Method -> false
            )
            |> List.length

        {
            Name = name
            Ops = ops
            Measured =
                {
                    Tokens = [ 1..calls ]
                    StrandsAt = None
                    Runs = runs
                }
        }

    let private m = ReplayOp.Method
    let private p (md : int) (sequence : int) = ReplayOp.Param (md, sequence)

    /// The measured scenarios. `X` is method 1, `Y` 2 and `Z` 3 wherever those names appear.
    let scenarios : Scenario list =
        [
            stranded "SlideThenMiddle" [ m ; m ; m ; p 1 2 ; p 1 1 ; p 2 1 ; p 2 1 ] 2
            stranded "SlideThenLast" [ m ; m ; m ; p 1 2 ; p 1 1 ; p 3 1 ; p 3 1 ] 2
            fine "NoSlideThenMiddle" [ m ; m ; m ; p 1 1 ; p 1 2 ; p 2 1 ; p 2 1 ] [ [ 1 ; 2 ] ; [ 3 ; 4 ] ; [] ]
            fine "SlideThenMiddleLazy" [ m ; p 1 2 ; p 1 1 ; m ; p 2 1 ; p 2 1 ] [ [ 2 ; 1 ] ; [ 3 ; 4 ] ]
            fine "SlideThenLastLazy" [ m ; p 1 2 ; p 1 1 ; m ; m ; p 3 1 ; p 3 1 ] [ [ 2 ; 1 ] ; [] ; [ 3 ; 4 ] ]
            stranded "SlideThenSliderAgain" [ m ; m ; m ; p 1 2 ; p 1 1 ; p 1 0 ] 2
            stranded "MiddleSlidesEarlierAdds" [ m ; m ; m ; p 2 2 ; p 2 1 ; p 1 1 ] 2
            stranded "ReturnAfterParam" [ m ; m ; p 1 1 ; p 1 0 ; p 2 1 ] 2
            stranded "SlideNoLaterUse" [ m ; m ; m ; p 1 2 ; p 1 1 ] 2
            fine
                "DirectTableThenSlide"
                [ m ; m ; m ; p 3 1 ; p 1 2 ; p 1 1 ; p 2 2 ; p 2 1 ; p 2 0 ; p 3 0 ]
                [ [ 3 ; 2 ] ; [ 6 ; 5 ; 4 ] ; [ 7 ; 1 ] ]
            fine
                "DirectAndSlideOneCall"
                [ m ; m ; m ; p 1 2 ; p 2 1 ; p 1 1 ; p 3 1 ; p 2 0 ]
                [ [ 3 ; 1 ] ; [ 5 ; 2 ] ; [ 4 ] ]
            fine
                "SlideOnLastThenLater"
                [ m ; m ; p 2 2 ; p 2 1 ; p 1 1 ; m ; p 3 1 ; p 3 0 ; p 2 0 ]
                [ [ 3 ] ; [ 6 ; 2 ; 1 ] ; [ 5 ; 4 ] ]
            stranded "StrandThenInvoke" [ m ; m ; m ; p 1 2 ; p 1 1 ] 2
            fine "TieIsNoSlide" [ m ; m ; m ; p 1 1 ; p 1 1 ; p 2 1 ; p 3 1 ] [ [ 1 ; 2 ] ; [ 3 ] ; [ 4 ] ]
            stranded "StrandThenSameMethod_Y" [ m ; m ; m ; p 1 1 ; p 2 2 ; p 2 1 ; p 2 2 ] 3
            stranded "StrandThenSameMethod_ret" [ m ; m ; p 1 1 ; p 1 0 ; p 1 2 ] 2
            stranded "StrandThenNewMethod" [ m ; m ; p 1 2 ; p 1 1 ; m ; p 3 1 ] 2
            // The constructor of `A` is method 1, and `B.M` method 2.
            stranded "CtorStrandAcrossTypes" [ m ; m ; p 1 2 ; p 1 1 ; p 1 1 ; p 2 1 ] 2
        ]

    let scenarioCases : TestCaseData list =
        scenarios
        |> List.map (fun s -> TestCaseData(s).SetName $"the replay agrees with real .NET on %s{s.Name}")

    /// The replay's account of a scenario, in the measured scenario's terms.
    let private replay (ops : ReplayOp list) : Measured =
        let rec go (state : ParamTableReplay.State) (calls : int) (tokens : int list) (ops : ReplayOp list) : Measured =
            match ops with
            | [] ->
                {
                    Tokens = List.rev tokens
                    StrandsAt = None
                    Runs =
                        [
                            for md in 1 .. ParamTableReplay.methodCount state do
                                yield ParamTableReplay.runOf md state
                        ]
                }
            | ReplayOp.Method :: rest -> go (ParamTableReplay.addMethod state) calls tokens rest
            | ReplayOp.Param (md, sequence) :: rest ->
                match ParamTableReplay.addParam md (uint16 sequence) state with
                | ParamTableReplay.Step.Added (rid, state, _, _) -> go state (calls + 1) (rid :: tokens) rest
                | ParamTableReplay.Step.Stranded rid ->
                    {
                        Tokens = List.rev (rid :: tokens)
                        StrandsAt = Some (calls + 1)
                        Runs = []
                    }

        go ParamTableReplay.empty 0 [] ops

    [<TestCaseSource(nameof scenarioCases)>]
    let ``the replay agrees with real .NET`` (scenario : Scenario) : unit =
        replay scenario.Ops |> shouldEqual scenario.Measured

    [<Test>]
    let ``there are eighteen measured scenarios, eleven of which strand rows`` () : unit =
        scenarios.Length |> shouldEqual 18

        scenarios
        |> List.filter (fun s -> s.Measured.StrandsAt.IsSome)
        |> List.length
        |> shouldEqual 11
