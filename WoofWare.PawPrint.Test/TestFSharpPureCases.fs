namespace WoofWare.Pawprint.Test

open System
open System.Collections.Immutable
open System.IO
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint
open WoofWare.PawPrint.Test

[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
// Runs guests under the interpreter, which is where essentially all of the suite's
// time goes; `Explicit` keeps it out of a bare `dotnet test` so local iteration is
// quick. CI selects it by category and so runs it. See AGENTS.md.
[<Category("Guest")>]
[<Explicit>]
module TestFSharpPureCases =

    /// The cases' publish. Checked against the shared framework only, not the whole of
    /// `FrameworkUnderTest.runtimeDirs ()`: that list also holds the test assembly's own directory,
    /// whose FSharp.Core the publish's is meant to shadow.
    let private published : Lazy<PublishedGuest> =
        lazy
            GuestPublish.publish
                "WoofWare.PawPrint.Test.FSharpPureCases"
                "WoofWare.PawPrint.Test.FSharpPureCases.fsproj"
                "WoofWare.PawPrint.Test.FSharpPureCases"
                [ FrameworkUnderTest.sharedFrameworkDirectory () ]

    /// The cases' assembly, read from the publish.
    let private loadImage () : PublishedGuest * byte array =
        let guest = published.Force ()
        guest, File.ReadAllBytes guest.Dll

    let testCases : string list =
        [
            "Placeholder"
            "CeqBranch"
            "TailCall"
            "AbstractDispatch"
            "ByrefDispatch"
            "SprintfBasic"
            "UnionVirtualSlots"
            "UnionReflection"
            "PrintfCacheHit"
            "SprintfPercentA"
            "SprintfPercentO"
        ]

    /// F# cases parked on every runtime, each with why it is parked, what would un-park it, and how
    /// it was verified to behave on real .NET. `onRuntime` folds these into every runtime's parks.
    ///
    /// A parked case is skipped as Inconclusive by `F# pure tests`, and only its *real-runtime*
    /// behaviour is checked. Nothing therefore detects a parked case that has started passing, so
    /// before recording that a case is blocked on a named primitive, un-park it and observe the
    /// failure: parking it is what stops the claim being checked.
    let parkedEverywhere : Map<string, string> = Map.empty

    /// How each F# case ends, on every runtime, when that is not a normal exit with code 0.
    /// `onRuntime` folds these into every runtime's endings.
    ///
    /// An unhandled exception is declared only when the throw is the intended observable: without
    /// the declaration, a case that crashes both runtimes would silently pass. `AbstractDispatch`
    /// returns the sum computed by its `Combine` call (see issue #693: 40 + 2 = 42) rather than a
    /// boolean success/failure code, so a wrong dispatch is directly observable as a wrong number
    /// rather than being laundered through an if/else into 0-or-1.
    let endingsEverywhere : Map<string, GuestEnding> =
        [
            "AbstractDispatch", GuestEnding.ExitCode 42
            "ByrefDispatch", GuestEnding.ExitCode 42
        ]
        |> Map.ofList

    /// F# cases parked on .NET 11 alone, each with why it is parked there and what would un-park it.
    let parkedOnNet11 : Map<string, string> = Map.empty

    /// Which F# cases are parked on `runtime`, and how each case is expected to end there.
    let onRuntime (runtime : EmulatedRuntime) : GuestCasesOnRuntime<string> =
        match runtime with
        | EmulatedRuntime.Net10 ->
            {
                Parked = parkedEverywhere
                Endings = endingsEverywhere
            }
        | EmulatedRuntime.Net11 ->
            {
                Parked = GuestTable.fold "Net11 parks" parkedEverywhere parkedOnNet11
                Endings = endingsEverywhere
            }

    /// The F# cases parked on the runtime under test.
    let unimplemented () : string list =
        (onRuntime (FrameworkUnderTest.runtime ())).Parked |> Map.keys |> List.ofSeq

    let private runTest (testCaseName : string) : unit =
        let guest, image = loadImage ()
        let messages, loggerFactory = LoggerFactory.makeTest ()

        let ending = (onRuntime (FrameworkUnderTest.runtime ())).Ending testCaseName

        let dotnetRuntimes =
            seq {
                yield guest.Directory
                yield! FrameworkUnderTest.runtimeDirs ()
            }
            |> ImmutableArray.CreateRange

        use peImage = new MemoryStream (image)

        try
            // The two runs are independent, so the oracle's child process runs while the
            // interpreter works rather than before it. See
            // `DifferentialOracle.alongsideInterpreted`.
            let realResult, pawPrintResult =
                DifferentialOracle.alongsideInterpreted
                    (fun () -> RealRuntime.executeAssemblyInPlace [| testCaseName |] guest.Dll)
                    (fun () ->
                        Program.run
                            loggerFactory
                            (Some guest.Dll)
                            peImage
                            { HostConfig.Default dotnetRuntimes with
                                Guest =
                                    { GuestConfig.Default dotnetRuntimes with
                                        Argv = [ testCaseName ]
                                    }
                            }
                    )

            // The publish holds no framework, so the guest runs on the framework under test's CoreLib.
            FrameworkUnderTest.assertOutcomeServes pawPrintResult

            match realResult, ExpectRun.ended pawPrintResult with
            | RealRuntimeResult.NormalExit exitCode, RunOutcome.NormalExit (terminalState, _, _) ->
                GuestEnding.ExitCode exitCode |> shouldEqual ending
                terminalState.LatchedExitCode |> shouldEqual exitCode
            | RealRuntimeResult.UnhandledException realExn, RunOutcome.GuestUnhandledException (finalState, _, exn, _) ->
                if ending <> GuestEnding.UnhandledException then
                    failwith
                        $"Both runtimes threw unhandled exceptions for %s{testCaseName}, but this test was not expected to throw. Declare its ending as an unhandled exception if that is intentional.\nReal runtime:\n%s{realExn}\nPawPrint:\n%s{UnhandledExceptionReport.describe finalState exn}"
            | RealRuntimeResult.NormalExit exitCode, RunOutcome.GuestUnhandledException (finalState, _, exn, _) ->
                failwith
                    $"Real runtime exited normally with code %d{exitCode}, but PawPrint threw unhandled exception:\n%s{UnhandledExceptionReport.describe finalState exn}"
            | RealRuntimeResult.Aborted (_code, report), _ ->
                failwith
                    $"Real runtime called Environment.FailFast for %s{testCaseName}; this fixture does not exercise FailFast:\n%s{report}"
            | RealRuntimeResult.UnhandledException realExn, RunOutcome.NormalExit (terminalState, _, _) ->
                failwith
                    $"Real runtime terminated with an unhandled exception, but PawPrint exited normally (code: %d{terminalState.LatchedExitCode}):\n%s{realExn}"
            | _, RunOutcome.Aborted (_, _, fatal, _) ->
                failwith
                    $"PawPrint aborted (%O{fatal.Code}); the real runtime can't have done so or the test harness would be gone"
            | _, RunOutcome.ProcessExit _ ->
                failwith
                    "PawPrint called Environment.Exit; the real runtime can't have done so or the test harness would be gone"
            | _, RunOutcome.SignalTerminated _ ->
                failwith
                    "PawPrint terminated due to a signal; the real runtime can't have done so or the test harness would be gone"
        with _ ->
            for message in messages () do
                System.Console.Error.WriteLine $"{message}"

            reraise ()

    [<TestCaseSource(nameof testCases)>]
    let ``F# pure tests`` (testCaseName : string) =
        match (onRuntime (FrameworkUnderTest.runtime ())).Parked.TryFind testCaseName with
        | Some reason -> Assert.Inconclusive $"Test case '%s{testCaseName}' is parked: %s{reason}"
        | None -> ()

        runTest testCaseName

    /// The `TailCall` case only covers the `tail.` prefix if FSC actually emits it, which
    /// depends on the optimiser (Release + `--tailcalls+`) and on the exact shapes in
    /// TailCall.fs. Without this guard, a compiler change that stopped emitting `tail.`
    /// would leave `F# pure tests(TailCall)` silently passing while covering nothing.
    /// If this fails, re-inspect the IL (`dotnet run --project WoofWare.PawPrint.IlDump --
    /// <published dll> TailCall`) and reshape TailCall.fs until the prefix comes back.
    [<Test>]
    let ``TailCall case really does contain tail. prefixes`` () : unit =
        let guest, image = loadImage ()
        let _, loggerFactory = LoggerFactory.makeTest ()
        use _loggerFactoryResource = loggerFactory
        use peImage = new MemoryStream (image)

        let assy = Assembly.read loggerFactory (Some guest.Dll) peImage

        let tailPrefixed =
            assy.TypeDefs.Values
            |> Seq.collect (fun typeInfo -> typeInfo.Methods)
            |> Seq.filter (fun methodInfo ->
                methodInfo.RequiredDeclaringType.Name = "TailCall"
                && match MethodInfo.tryIlBody methodInfo with
                   | None -> false
                   | Some instructions ->
                       instructions.Instructions
                       |> List.exists (fun (op, _offset) ->
                           match op with
                           | IlOp.Nullary NullaryIlOp.Tail -> true
                           | _ -> false
                       )
            )
            |> Seq.map (fun methodInfo -> methodInfo.Name)
            |> Set.ofSeq

        // `isEven`/`isOdd` are `tail. call`; `applyTail` is `tail. callvirt`. All three are
        // reached from `TailCall.main`, so the end-to-end case really executes the prefix.
        tailPrefixed |> shouldEqual (Set.ofList [ "isEven" ; "isOdd" ; "applyTail" ])

    /// The `AbstractDispatch` case only exercises issue #693 if FSC actually emits the
    /// abstract `Combine` declaration with zero Param-table rows despite its signature
    /// declaring one parameter. Without this guard, a compiler change that started emitting
    /// a Param row for the abstract declaration would leave `F# pure tests(AbstractDispatch)`
    /// silently passing while covering nothing — `Parameters.Length` would then agree with
    /// the true arity, and the bug this test exists to catch could regress unnoticed.
    /// If this fails, re-inspect the metadata (`dotnet run --project WoofWare.PawPrint.IlDump
    /// -- <published dll> Base Combine`) and reshape AbstractDispatch.fs until the shape
    /// comes back.
    [<Test>]
    let ``AbstractDispatch's abstract Combine really does have zero Param rows`` () : unit =
        let guest, image = loadImage ()
        let _, loggerFactory = LoggerFactory.makeTest ()
        use _loggerFactoryResource = loggerFactory
        use peImage = new MemoryStream (image)

        let assy = Assembly.read loggerFactory (Some guest.Dll) peImage

        let combine =
            assy.TypeDefs.Values
            |> Seq.collect (fun typeInfo -> typeInfo.Methods)
            |> Seq.filter (fun methodInfo ->
                methodInfo.RequiredDeclaringType.Name = "Base" && methodInfo.Name = "Combine"
            )
            |> Seq.toList
            |> function
                | [ m ] -> m
                | [] -> failwith "AbstractDispatch.Base::Combine not found in the published assembly"
                | ms -> failwith $"expected exactly one AbstractDispatch.Base::Combine, found %d{List.length ms}"

        (match combine.Body with
         | MethodBody.Abstract -> ()
         | other -> failwith $"expected AbstractDispatch.Base::Combine to be MethodBody.Abstract, got %O{other}")

        // Deliberately the Param *table*, not the arity: the whole point of this test is that the
        // two disagree for an F#-emitted abstract member.
        (MethodInfo.requireMetadata "test" combine).Parameters.IsEmpty
        |> shouldEqual true

        MethodInfo.arity combine |> shouldEqual 1

    /// Regression guard for issue #692, mirroring the `AbstractDispatch` guard above. #692's
    /// real-world trigger (FSharp.Core's `MapEnumerator`1::DoMoveNext(byref<T>)`) is an
    /// abstract method with a *byref* parameter; this asserts `ByrefDispatch.fs` reproduces
    /// that same zero-Param-rows-but-nonzero-arity shape, and specifically that the one
    /// parameter is a byref. Without this guard, a compiler change that started emitting a
    /// Param row for the abstract declaration (or stopped modelling the parameter as a byref)
    /// would leave `F# pure tests(ByrefDispatch)` silently passing while covering nothing.
    [<Test>]
    let ``ByrefDispatch's abstract Bump really does have zero Param rows and a byref parameter`` () : unit =
        let guest, image = loadImage ()
        let _, loggerFactory = LoggerFactory.makeTest ()
        use _loggerFactoryResource = loggerFactory
        use peImage = new MemoryStream (image)

        let assy = Assembly.read loggerFactory (Some guest.Dll) peImage

        let bump =
            assy.TypeDefs.Values
            |> Seq.collect (fun typeInfo -> typeInfo.Methods)
            |> Seq.filter (fun methodInfo -> methodInfo.RequiredDeclaringType.Name = "Base" && methodInfo.Name = "Bump")
            |> Seq.toList
            |> function
                | [ m ] -> m
                | [] -> failwith "ByrefDispatch.Base::Bump not found in the published assembly"
                | ms -> failwith $"expected exactly one ByrefDispatch.Base::Bump, found %d{List.length ms}"

        (match bump.Body with
         | MethodBody.Abstract -> ()
         | other -> failwith $"expected ByrefDispatch.Base::Bump to be MethodBody.Abstract, got %O{other}")

        // As above: the Param table, not the arity.
        (MethodInfo.requireMetadata "test" bump).Parameters.IsEmpty |> shouldEqual true
        MethodInfo.arity bump |> shouldEqual 1

        (match bump.Signature.ParameterTypes.[0] with
         | TypeDefn.Byref _ -> ()
         | other -> failwith $"expected ByrefDispatch.Base::Bump's sole parameter to be a byref, got %O{other}")

    [<TestCaseSource(nameof unimplemented)>]
    let ``Unimplemented F# tests have correct real-runtime behaviour`` (testCaseName : string) =
        // This case never runs PawPrint, so it needs the publish rather than the image.
        let guest = published.Force ()

        let ending = (onRuntime (FrameworkUnderTest.runtime ())).Ending testCaseName

        RealRuntime.executeAssemblyInPlace [| testCaseName |] guest.Dll
        |> GuestEnding.assertReal testCaseName ending
