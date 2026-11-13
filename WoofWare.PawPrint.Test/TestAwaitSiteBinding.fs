namespace WoofWare.PawPrint.Test

open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// `AwaitSiteBinding.classify`, decision 13's classification of a matched await site, against the
/// rule of stage 4c of `docs/plans/2026-11-11-runtime-async.md`: a site binds the callee's async
/// variant exactly when it is a tail shape or an await with `ConfigureAwait(false)`, has no
/// `constrained.` prefix, does not dispatch, and names a method whose variant runs IL, an `Async`
/// method's or an async version's. An await continues on the thread pool, and a tail shape with no
/// preference.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestAwaitSiteBinding =

    let private genRefusal : Gen<AsyncVariantRefusal> =
        Gen.elements
            [
                AsyncVariantRefusal.Synthesised
                AsyncVariantRefusal.AlreadyAsyncVariant
                AsyncVariantRefusal.ReturnsNoPromise
                AsyncVariantRefusal.DelegateMethod
                AsyncVariantRefusal.ComImportType
                AsyncVariantRefusal.UnmodelledBody UnmodelledVariantBody.PInvoke
                AsyncVariantRefusal.UnmodelledBody UnmodelledVariantBody.InternalCall
                AsyncVariantRefusal.UnmodelledBody UnmodelledVariantBody.MissingIl
                AsyncVariantRefusal.UnmodelledBody (
                    UnmodelledVariantBody.RuntimeProvided RuntimeBehaviour.DelegateInvoke
                )
            ]

    /// Whether `facts` names an abstract method through a call that neither dispatches nor has a
    /// `constrained.` prefix to resolve it, which CoreCLR's JIT refuses to compile.
    let private callsAbstractDirectly (facts : AwaitSiteFacts) : bool =
        facts.NamedVariant = Ok AsyncVariantBody.Abstract
        && not facts.Constrained
        && not (facts.Opcode = AwaitedCall.Callvirt && facts.NamedDispatchesVirtually)

    let private genFacts : Gen<AwaitSiteFacts> =
        gen {
            let! pattern =
                Gen.elements
                    [
                        MatchedPattern.Await true
                        MatchedPattern.Await false
                        MatchedPattern.TailShape
                    ]

            let! opcode = Gen.elements [ AwaitedCall.Call ; AwaitedCall.Callvirt ]
            let! constrained = Gen.elements [ true ; false ]
            let! dispatches = Gen.elements [ true ; false ]

            let! variant =
                Gen.frequency
                    [
                        3,
                        Gen.elements
                            [
                                AsyncVariantBody.RuntimeAsyncIl
                                AsyncVariantBody.AsyncVersion
                                AsyncVariantBody.Abstract
                            ]
                        |> Gen.map Ok
                        1, genRefusal |> Gen.map Error
                    ]

            return
                {
                    Pattern = pattern
                    Opcode = opcode
                    Constrained = constrained
                    NamedDispatchesVirtually = dispatches
                    NamedVariant = variant
                }
        }
        |> Gen.filter (callsAbstractDirectly >> not)

    /// Whether the site dispatches, as stage 4c reads it: a `callvirt` of a method that is virtual
    /// and not final.
    let private dispatches (facts : AwaitSiteFacts) : bool =
        facts.Opcode = AwaitedCall.Callvirt && facts.NamedDispatchesVirtually

    /// Whether `reason` holds of `facts`.
    let private holds (facts : AwaitSiteFacts) (reason : AwaitNotYetBound) : bool =
        match reason with
        | AwaitNotYetBound.ContinuesOnCapturedContext -> facts.Pattern = MatchedPattern.Await true
        | AwaitNotYetBound.Constrained -> facts.Constrained
        | AwaitNotYetBound.CalleeDispatches -> dispatches facts

    [<Test>]
    let ``A site binds exactly when stage 4c's four conditions hold, and continues as its pattern says`` () : unit =
        let property (facts : AwaitSiteFacts) : unit =
            let runsIl =
                facts.NamedVariant = Ok AsyncVariantBody.RuntimeAsyncIl
                || facts.NamedVariant = Ok AsyncVariantBody.AsyncVersion

            let binds =
                runsIl
                && facts.Pattern <> MatchedPattern.Await true
                && not facts.Constrained
                && not (dispatches facts)

            match AwaitSiteBinding.classify facts with
            | AwaitSiteBinding.BindsVariant handling ->
                binds |> shouldEqual true

                match facts.Pattern with
                | MatchedPattern.Await _ -> handling |> shouldEqual ContinuationContextHandling.ContinueOnThreadPool
                | MatchedPattern.TailShape -> handling |> shouldEqual ContinuationContextHandling.NoPreference
            | AwaitSiteBinding.NoVariant _
            | AwaitSiteBinding.NotYetBound _
            | AwaitSiteBinding.Unmodelled _ -> binds |> shouldEqual false

        Check.One (Config.QuickThrowOnFailure.WithMaxTest 2000, Prop.forAll (Arb.fromGen genFacts) property)

    [<Test>]
    let ``A site has no variant exactly when CoreCLR's VM supplies none, and says why`` () : unit =
        let property (facts : AwaitSiteFacts) : unit =
            let expected =
                match facts.NamedVariant with
                | Error (AsyncVariantRefusal.UnmodelledBody _)
                | Ok _ -> None
                | Error refusal -> Some refusal

            match AwaitSiteBinding.classify facts with
            | AwaitSiteBinding.NoVariant refusal -> Some refusal |> shouldEqual expected
            | AwaitSiteBinding.BindsVariant _
            | AwaitSiteBinding.NotYetBound _
            | AwaitSiteBinding.Unmodelled _ -> expected |> shouldEqual None

        Check.One (Config.QuickThrowOnFailure.WithMaxTest 2000, Prop.forAll (Arb.fromGen genFacts) property)

    [<Test>]
    let ``A site not yet bound names a reason that holds of it`` () : unit =
        let property (facts : AwaitSiteFacts) : unit =
            match AwaitSiteBinding.classify facts with
            | AwaitSiteBinding.NotYetBound reason -> holds facts reason |> shouldEqual true
            | AwaitSiteBinding.BindsVariant _
            | AwaitSiteBinding.NoVariant _
            | AwaitSiteBinding.Unmodelled _ -> ()

        Check.One (Config.QuickThrowOnFailure.WithMaxTest 2000, Prop.forAll (Arb.fromGen genFacts) property)

    /// CoreCLR binds the variant of a method whose body PawPrint does not model, so such a site is
    /// refused rather than awaiting the promise of the ordinary variant.
    [<Test>]
    let ``A site is unmodelled exactly when the named method's variant has a body PawPrint does not model`` () : unit =
        let property (facts : AwaitSiteFacts) : unit =
            let expected =
                match facts.NamedVariant with
                | Error (AsyncVariantRefusal.UnmodelledBody body) -> Some body
                | Error _
                | Ok _ -> None

            match AwaitSiteBinding.classify facts with
            | AwaitSiteBinding.Unmodelled body -> Some body |> shouldEqual expected
            | AwaitSiteBinding.BindsVariant _
            | AwaitSiteBinding.NoVariant _
            | AwaitSiteBinding.NotYetBound _ -> expected |> shouldEqual None

        Check.One (Config.QuickThrowOnFailure.WithMaxTest 2000, Prop.forAll (Arb.fromGen genFacts) property)

    /// Every reason is reachable: a classifier that lost one would still pass the property above.
    [<Test>]
    let ``Each reason a site is not yet bound comes up`` () : unit =
        let reasons =
            Gen.sample 2000 genFacts
            |> Seq.choose (fun facts ->
                match AwaitSiteBinding.classify facts with
                | AwaitSiteBinding.NotYetBound reason -> Some (string<AwaitNotYetBound> reason)
                | AwaitSiteBinding.BindsVariant _
                | AwaitSiteBinding.NoVariant _
                | AwaitSiteBinding.Unmodelled _ -> None
            )
            |> Set.ofSeq

        reasons
        |> shouldEqual (Set.ofList [ "ContinuesOnCapturedContext" ; "Constrained" ; "CalleeDispatches" ])

    [<Test>]
    let ``A call naming an abstract method directly is refused as a precondition`` () : unit =
        let facts : AwaitSiteFacts =
            {
                Pattern = MatchedPattern.Await false
                Opcode = AwaitedCall.Call
                Constrained = false
                NamedDispatchesVirtually = true
                NamedVariant = Ok AsyncVariantBody.Abstract
            }

        Assert.Throws<System.ArgumentException> (fun () -> AwaitSiteBinding.classify facts |> ignore)
        |> ignore
