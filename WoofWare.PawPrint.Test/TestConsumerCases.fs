namespace WoofWare.PawPrint.Test

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint
open WoofWare.PosixKernel

/// Scenarios from the libraries that consumers run under PawPrint (the mocking libraries of issue
/// #2092), from the guest project `WoofWare.PawPrint.Test.ConsumerCases`, each run under PawPrint
/// and under real .NET, with the two runs compared.
///
/// The guest is published framework-dependent once per test run (`GuestPublish`), and each
/// scenario runs under `BoundedRun` on the pack of the default kernel's flavour, with the publish
/// at the head of the runtime-dir list, and alongside it on real .NET through
/// `RealRuntime.captureAssemblyInPlace`. A scenario passes when both runs end as the scenario's
/// expected ending says, both print the same lines to standard output, and, for a scenario that
/// exits 0, neither writes to standard error. The real runtime's output reaches the oracle as
/// lines, so the comparison claims the same lines, not the same bytes: it cannot see a missing
/// final newline.
///
/// Both sides run with the AppContext properties `appContext` and the environment entries
/// `environment`. Of the two statements of invariant globalisation, the environment variable
/// decides: CoreLib reads `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT` in preference to the
/// `System.Globalization.Invariant` property. Every variable `environment` does not name reaches
/// the real runtime from the test host (issue #2334).
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
// Runs guests under the interpreter; `Explicit` keeps it out of a bare `dotnet test`, and CI
// selects it by category. See AGENTS.md.
[<Category("Guest")>]
[<Explicit>]
module TestConsumerCases =

    /// Every scenario the guest knows, in the order its `list` argument prints them.
    let scenarios : string list =
        [
            "castle-generic"
            "castle-class"
            "castle-target"
            "moq-basic"
            "moq-verifyfail"
            "moq-strict"
            "nsub-basic"
            "nsub-fail"
            "nsub-class"
            "fie-callto"
            "fie-mhhfail"
            "fie-generic"
            "fie-wrap"
        ]

    /// The libraries the scenarios exercise, each named as the prefix of its scenarios' names.
    let libraries : string list = [ "castle" ; "moq" ; "nsub" ; "fie" ]

    /// The library whose prefix `scenario`'s name begins with.
    let libraryOf (scenario : string) : string =
        match scenario.IndexOf '-' with
        | -1 -> failwith $"consumer scenario %s{scenario} names no library prefix"
        | i -> scenario.Substring (0, i)

    /// For at least one scenario of each library, a line that the scenario prints only when the
    /// library handed it an object of a type the library generated at run time. A scenario's run
    /// under PawPrint must print its line, so that agreement with the real runtime cannot come
    /// from a path that never emits code.
    let generatedTypeWitnesses : Map<string, string> =
        [
            "castle-class", "generated type Castle.Proxies.ConcreteProxy in a dynamic assembly: True"
            "moq-basic", "generated type Castle.Proxies.IThingProxy in a dynamic assembly: True"
            "nsub-basic", "generated type Castle.Proxies.ObjectProxy in a dynamic assembly: True"
            "fie-generic", "generated type Castle.Proxies.ObjectProxy in a dynamic assembly: True"
        ]
        |> Map.ofList

    /// The AppContext properties of both sides. The guest project declares the same values, and
    /// the publish's `runtimeconfig.json` is checked against these.
    let appContext : AppContextProperties =
        [
            "System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported", "true"
            "System.Globalization.Invariant", "true"
        ]
        |> Map.ofList
        |> AppContextProperties.ofMap

    /// The environment entries of both sides: `KernelConfig.Environment` under PawPrint, and the
    /// entries laid over the test host's environment for the real runtime.
    ///
    /// PawPrint's kernel seeds `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` by default; it is stated
    /// here so that a reader sees what both sides ran with, and a change to that default cannot
    /// separate them. `DOTNET_SYSTEM_GLOBALIZATION_PREDEFINED_CULTURES_ONLY=1` is what invariant
    /// mode already defaults to, so it changes nothing under PawPrint; it is stated so that a test
    /// host which sets it to `0` cannot let the real runtime construct cultures that PawPrint's
    /// guest cannot.
    let environment : string list =
        [
            EnvironmentPal.nameValueEntry "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT" "1"
            EnvironmentPal.nameValueEntry "DOTNET_SYSTEM_GLOBALIZATION_PREDEFINED_CULTURES_ONLY" "1"
        ]

    /// The kernel PawPrint runs every scenario on, whose flavour decides which CoreLib pack it runs.
    let private kernel : KernelConfig =
        { KernelConfig.Default with
            Environment = environment
        }

    /// Scenarios parked on every runtime, each with why it is parked, what would un-park it, and
    /// how it was verified to behave on real .NET. `onRuntime` folds these into every runtime's
    /// parks.
    let parkedEverywhere : Map<string, string> = Map.empty

    /// How each scenario ends, on every runtime, when that is not a normal exit with code 0.
    /// `onRuntime` folds these into every runtime's endings.
    let endingsEverywhere : Map<string, GuestEnding> = Map.empty

    /// Scenarios parked on .NET 11 alone, each with why it is parked there and what would un-park
    /// it.
    let parkedOnNet11 : Map<string, string> = Map.empty

    /// Which scenarios are parked on `runtime`, and how each is expected to end there.
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

    /// The scenarios parked on the runtime under test.
    let unimplemented () : string list =
        (onRuntime (FrameworkUnderTest.runtime ())).Parked |> Map.keys |> List.ofSeq

    let private projectName = "WoofWare.PawPrint.Test.ConsumerCases"

    /// Every entry of the publish directory, files and directories alike.
    let private expectedPublishEntries : Set<string> =
        Set.ofList
            [
                "Castle.Core.dll"
                "FakeItEasy.dll"
                "Moq.dll"
                "NSubstitute.dll"
                "System.Diagnostics.EventLog.dll"
                $"%s{projectName}.deps.json"
                $"%s{projectName}.dll"
                $"%s{projectName}.runtimeconfig.json"
                // System.Diagnostics.EventLog's Windows assemblies, which no Unix host loads.
                "runtimes"
            ]

    /// The packages the restore resolved, as the publish's `deps.json` names them.
    let private expectedPackages : Set<string> =
        Set.ofList
            [
                "Castle.Core/5.1.1"
                "FakeItEasy/8.0.0"
                "Moq/4.20.72"
                "NSubstitute/5.1.0"
                "System.Diagnostics.EventLog/6.0.0"
            ]

    /// Each package assembly's file version, which, unlike its assembly version, names the
    /// package's release.
    let private expectedFileVersions : Map<string, Version> =
        [
            "Castle.Core.dll", Version (5, 1, 1, 0)
            "FakeItEasy.dll", Version (8, 0, 0, 0)
            "Moq.dll", Version (4, 20, 72, 0)
            "NSubstitute.dll", Version (5, 1, 0, 0)
            "System.Diagnostics.EventLog.dll", Version (6, 0, 21, 52210)
        ]
        |> Map.ofList

    /// Fails unless the publish holds exactly the pinned packages, and its `runtimeconfig.json`
    /// gives each property of `appContext` the same value.
    let private checkPublish (guest : PublishedGuest) : unit =
        Directory.EnumerateFileSystemEntries guest.Directory
        |> Seq.map Path.GetFileName
        |> Set.ofSeq
        |> shouldEqual expectedPublishEntries

        use depsJson =
            Path.Combine (guest.Directory, $"%s{projectName}.deps.json")
            |> File.ReadAllText
            |> JsonDocument.Parse

        depsJson.RootElement.GetProperty("libraries").EnumerateObject ()
        |> Seq.filter (fun library -> library.Value.GetProperty("type").GetString () = "package")
        |> Seq.map (fun library -> library.Name)
        |> Set.ofSeq
        |> shouldEqual expectedPackages

        for KeyValue (file, version) in expectedFileVersions do
            // By its parts: the version string is the attribute's text, which need not have four.
            let info = FileVersionInfo.GetVersionInfo (Path.Combine (guest.Directory, file))

            let actual =
                Version (info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart)

            if actual <> version then
                failwith $"%s{file} in the publish has file version %O{actual}, but %O{version} is pinned"

        let runtimeConfigPath =
            Path.Combine (guest.Directory, $"%s{projectName}.runtimeconfig.json")

        let declared =
            match RuntimeConfig.parse (File.ReadAllBytes runtimeConfigPath) with
            | Ok properties -> AppContextProperties.toMap properties
            | Error error -> failwith $"%s{runtimeConfigPath} does not parse: %A{error}"

        for KeyValue (name, value) in AppContextProperties.toMap appContext do
            match Map.tryFind name declared with
            | Some declaredValue when declaredValue = value -> ()
            | declaredValue ->
                failwith
                    $"The fixture gives the AppContext property %s{name} the value %s{value}, but the guest project declares %A{declaredValue} in %s{runtimeConfigPath}. Make the guest project's MSBuild agree with the fixture."

    /// The guest's publish, checked by `checkPublish`, and against every framework directory PawPrint
    /// or the oracle runs it on.
    let private published : Lazy<PublishedGuest> =
        lazy
            let shared = FrameworkUnderTest.sharedFrameworkDirectory ()

            let packs =
                FrameworkUnderTest.runtimeDirsFor kernel.UnixPlatform
                |> Seq.except (FrameworkUnderTest.runtimeDirs ())
                |> List.ofSeq

            let guest =
                GuestPublish.publish projectName $"%s{projectName}.csproj" projectName (shared :: packs)

            checkPublish guest
            guest

    /// `text` as the oracle captures standard output: lines, each ended by `Environment.NewLine`,
    /// a last unterminated line included, with any of `\r`, `\n` and `\r\n` ending a line.
    let private asLines (text : string) : string =
        let builder = StringBuilder ()
        use reader = new StringReader (text)

        let rec go () : unit =
            match reader.ReadLine () with
            | null -> ()
            | line ->
                builder.AppendLine line |> ignore
                go ()

        go ()
        builder.ToString ()

    /// What the guest wrote to `role` under PawPrint, decoded as UTF-8 and put in the form of
    /// `CapturedRun`'s text. Fails on bytes that are not UTF-8, which the comparison could not see.
    let private pawPrintText (state : IlMachineState) (role : FileDescriptorRole) : string =
        let strict = UTF8Encoding (false, true)

        state.Kernel.OutputLog
        |> OutputLogEntry.bytesFor role
        |> Seq.toArray
        |> strict.GetString
        |> asLines

    let private runScenario (scenario : string) : unit =
        let guest = published.Force ()
        let image = File.ReadAllBytes guest.Dll
        let messages, loggerFactory = LoggerFactory.makeTest ()
        use _loggerFactoryResource = loggerFactory

        let expected = (onRuntime (FrameworkUnderTest.runtime ())).Ending scenario

        let dotnetRuntimes =
            (FrameworkUnderTest.runtimeDirsFor kernel.UnixPlatform).Insert (0, guest.Directory)

        let hostConfig =
            { HostConfig.Default dotnetRuntimes with
                Guest =
                    { GuestConfig.Default dotnetRuntimes with
                        Kernel = kernel
                        Argv = [ scenario ]
                        AppContext = appContext
                    }
            }

        use peImage = new MemoryStream (image)

        try
            let real, pawPrint =
                DifferentialOracle.alongsideInterpreted
                    (fun () -> RealRuntime.captureAssemblyInPlace appContext environment [| scenario |] guest.Dll)
                    (fun () ->
                        BoundedRun.run
                            loggerFactory
                            $"consumer scenario %s{scenario}"
                            (Some guest.Dll)
                            peImage
                            hostConfig
                    )

            let state = RunEnd.state pawPrint
            let pawPrintStdout = pawPrintText state FileDescriptorRole.StandardOutput
            let pawPrintStderr = pawPrintText state FileDescriptorRole.StandardError

            let report =
                $"PawPrint's stdout:\n%s{pawPrintStdout}\nPawPrint's stderr:\n%s{pawPrintStderr}\nThe real runtime's stdout:\n%s{real.Stdout}\nThe real runtime's stderr:\n%s{real.Stderr}"

            match GuestEnding.checkReal scenario expected real.Result with
            | Ok () -> ()
            | Error message -> failwith $"%s{message}\n%s{report}"

            // The real runtime has ended as expected, so this is the comparison every differential
            // fixture makes: PawPrint ended the same way, an `Environment.Exit` counting as a normal
            // exit, with its kernel's termination status agreeing.
            let expectedExitCode, expectsUnhandledException =
                match expected with
                | GuestEnding.ExitCode code -> code, false
                | GuestEnding.UnhandledException -> 0, true

            try
                DifferentialOracle.compareOutcomes
                    scenario
                    expectedExitCode
                    expectsUnhandledException
                    real.Result
                    pawPrint
            with e ->
                failwith $"%s{e.Message}\n%s{report}"

            if pawPrintStdout <> real.Stdout then
                failwith
                    $"%s{scenario} wrote different standard output under PawPrint and under the real runtime.\n%s{report}"

            if expected = GuestEnding.ExitCode 0 && (pawPrintStderr <> "" || real.Stderr <> "") then
                failwith $"%s{scenario} exited 0, but wrote to standard error.\n%s{report}"

            match Map.tryFind scenario generatedTypeWitnesses with
            | None -> ()
            | Some witness ->
                if not (pawPrintStdout.Split Environment.NewLine |> Array.contains witness) then
                    failwith
                        $"%s{scenario} did not print the line by which it shows that its library generated a type at run time, %s{witness}.\n%s{report}"
        with _ ->
            for message in messages () do
                Console.Error.WriteLine $"{message}"

            reraise ()

    /// The scenarios not parked on the runtime under test.
    let running () : string list =
        let parked = (onRuntime (FrameworkUnderTest.runtime ())).Parked
        scenarios |> List.filter (fun scenario -> not (parked.ContainsKey scenario))

    [<TestCaseSource(nameof running)>]
    let ``Consumer scenario`` (scenario : string) : unit = runScenario scenario

    /// Each parked scenario, run as an unparked one is. Method-level `Explicit`, so a category
    /// filter skips it and a filter naming the scenario runs it, to see whether it now passes.
    [<TestCaseSource(nameof unimplemented)>]
    [<Explicit>]
    let ``Consumer scenario, parked`` (scenario : string) : unit = runScenario scenario

    /// The checks of the publish, which every other test here makes first, by themselves.
    [<Test>]
    let ``The publish holds exactly the pinned packages, configured as the fixture states`` () : unit =
        published.Force () |> ignore<PublishedGuest>

    /// A scenario added to the guest and not to `scenarios` would never run.
    [<Test>]
    let ``The guest lists exactly the fixture's scenarios`` () : unit =
        let guest = published.Force ()

        let run =
            RealRuntime.captureAssemblyInPlace appContext environment [| "list" |] guest.Dll

        GuestEnding.assertReal "list" (GuestEnding.ExitCode 0) run.Result

        run.Stdout
        |> shouldEqual (scenarios |> List.map (fun s -> s + Environment.NewLine) |> String.concat "")

    /// Both sides would refuse a misspelt scenario alike, so agreement alone would pass one; the
    /// guest's distinct exit code for it is what makes each scenario's expected ending catch it.
    [<Test>]
    let ``The guest refuses a scenario it does not know`` () : unit =
        let guest = published.Force ()

        let run =
            RealRuntime.captureAssemblyInPlace appContext environment [| "no-such-scenario" |] guest.Dll

        GuestEnding.assertReal "no-such-scenario" (GuestEnding.ExitCode 2) run.Result

    [<TestCaseSource(nameof unimplemented)>]
    let ``Parked consumer scenarios have correct real-runtime behaviour`` (scenario : string) : unit =
        let guest = published.Force ()
        let expected = (onRuntime (FrameworkUnderTest.runtime ())).Ending scenario

        (RealRuntime.captureAssemblyInPlace appContext environment [| scenario |] guest.Dll).Result
        |> GuestEnding.assertReal scenario expected
