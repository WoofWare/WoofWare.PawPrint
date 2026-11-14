namespace WoofWare.PawPrint.Test

open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// `AwaitSiteBinding.classify`, decision 13's classification of a matched await site, against the
/// rule of stage 4f of `docs/plans/2026-11-11-runtime-async.md`: a site binds the callee's async
/// variant exactly when it is no `callvirt` with a `constrained.` prefix, and names a method with a
/// variant PawPrint models: one that runs IL, an `Async` method's or an async version's, or an
/// abstract one, which only a call that dispatches or has a `constrained.` prefix names. An await
/// with `ConfigureAwait(false)` continues on the thread pool, any other await on the captured
/// context, and a tail shape with no preference.
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

    /// Whether `reason` holds of `facts`.
    let private holds (facts : AwaitSiteFacts) (reason : AwaitNotYetBound) : bool =
        match reason with
        | AwaitNotYetBound.ConstrainedCallvirt -> facts.Constrained && facts.Opcode = AwaitedCall.Callvirt

    [<Test>]
    let ``A site binds exactly when stage 4f's two conditions hold, and continues as its pattern says`` () : unit =
        let property (facts : AwaitSiteFacts) : unit =
            let hasVariant =
                match facts.NamedVariant with
                | Ok _ -> true
                | Error _ -> false

            let binds =
                hasVariant && not (facts.Constrained && facts.Opcode = AwaitedCall.Callvirt)

            match AwaitSiteBinding.classify facts with
            | AwaitSiteBinding.BindsVariant handling ->
                binds |> shouldEqual true

                match facts.Pattern with
                | MatchedPattern.Await true ->
                    handling |> shouldEqual ContinuationContextHandling.ContinueOnCapturedContext
                | MatchedPattern.Await false -> handling |> shouldEqual ContinuationContextHandling.ContinueOnThreadPool
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

        reasons |> shouldEqual (Set.ofList [ "ConstrainedCallvirt" ])

    /// A `callvirt` that dispatches binds the named method's variant, abstract or not: dispatch
    /// finds the receiver's implementation through the ordinary sibling (decision 19).
    [<Test>]
    let ``A site that dispatches binds, whatever variant the named method has`` () : unit =
        for body in
            [
                AsyncVariantBody.RuntimeAsyncIl
                AsyncVariantBody.AsyncVersion
                AsyncVariantBody.Abstract
            ] do
            for pattern in
                [
                    MatchedPattern.Await true
                    MatchedPattern.Await false
                    MatchedPattern.TailShape
                ] do
                let facts : AwaitSiteFacts =
                    {
                        Pattern = pattern
                        Opcode = AwaitedCall.Callvirt
                        Constrained = false
                        NamedDispatchesVirtually = true
                        NamedVariant = Ok body
                    }

                match AwaitSiteBinding.classify facts with
                | AwaitSiteBinding.BindsVariant _ -> ()
                | other -> failwith $"%A{facts} classified as %A{other}"

    /// A `call` with a `constrained.` prefix, as of a static virtual method, binds the named method's
    /// variant, abstract or not: the prefix resolves the variant's implementation through the
    /// ordinary sibling's (decision 19).
    [<Test>]
    let ``A constrained call binds, whatever variant the named method has`` () : unit =
        for body in
            [
                AsyncVariantBody.RuntimeAsyncIl
                AsyncVariantBody.AsyncVersion
                AsyncVariantBody.Abstract
            ] do
            for pattern in
                [
                    MatchedPattern.Await true
                    MatchedPattern.Await false
                    MatchedPattern.TailShape
                ] do
                for dispatches in [ true ; false ] do
                    let facts : AwaitSiteFacts =
                        {
                            Pattern = pattern
                            Opcode = AwaitedCall.Call
                            Constrained = true
                            NamedDispatchesVirtually = dispatches
                            NamedVariant = Ok body
                        }

                    match AwaitSiteBinding.classify facts with
                    | AwaitSiteBinding.BindsVariant _ -> ()
                    | other -> failwith $"%A{facts} classified as %A{other}"

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
