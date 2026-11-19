namespace WoofWare.PawPrint.Test

open System
open System.Collections.Immutable
open System.IO
open System.Text.Json
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open Microsoft.Extensions.Logging.Abstractions
open NUnit.Framework
open WoofWare.PawPrint
open WoofWare.PosixKernel

/// `FrameworkUnderTest` selects a framework only when it is exactly the one asked for, and never
/// falls back to the test host's.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestFrameworkUnderTest =

    /// Strings near a runtime's name, as well as arbitrary ones: a parser that trimmed, ignored
    /// case, or matched a prefix would accept some of these.
    let private candidateValues : Arbitrary<string> =
        let names = EmulatedRuntime.supported |> List.map FrameworkUnderTest.name

        let nearMiss =
            gen {
                let! name = Gen.elements names

                let! edit =
                    Gen.elements
                        [
                            id
                            (fun (s : string) -> s.ToLowerInvariant ())
                            (fun (s : string) -> s.ToUpperInvariant ())
                            (fun (s : string) -> " " + s)
                            (fun (s : string) -> s + "\n")
                            (fun (s : string) -> s.Substring (0, s.Length - 1))
                            (fun (s : string) -> s + "0")
                            (fun (s : string) -> "net" + s.Substring 3)
                        ]

                return edit name
            }

        let arbitrary =
            ArbMap.defaults
            |> ArbMap.generate<string>
            |> Gen.map (fun s -> s |> Option.ofObj |> Option.defaultValue "")

        Gen.oneof [ nearMiss ; arbitrary ; Gen.constant "" ] |> Arb.fromGen

    [<Test>]
    let ``A value selects a runtime exactly when it is that runtime's name`` () : unit =
        let property (value : string) : bool =
            let expected =
                EmulatedRuntime.supported
                |> List.filter (fun runtime -> FrameworkUnderTest.name runtime = value)

            match expected, FrameworkUnderTest.parse (Some value) with
            | [ runtime ], Ok parsed -> parsed = runtime
            | [], Error (FrameworkUnderTestError.UnknownRuntime unknown) -> unknown = value
            | _ -> false

        Check.One (Config.QuickThrowOnFailure.WithMaxTest 1000, Prop.forAll candidateValues property)

    [<Test>]
    let ``Every supported runtime has its own name`` () : unit =
        let names = EmulatedRuntime.supported |> List.map FrameworkUnderTest.name
        names |> List.distinct |> shouldEqual names

        for runtime in EmulatedRuntime.supported do
            FrameworkUnderTest.parse (Some (FrameworkUnderTest.name runtime))
            |> shouldEqual (Ok runtime)

    [<Test>]
    let ``Only an unset variable selects the default runtime`` () : unit =
        FrameworkUnderTest.parse None |> shouldEqual (Ok EmulatedRuntime.Net10)

        FrameworkUnderTest.parse (Some "")
        |> shouldEqual (Error (FrameworkUnderTestError.UnknownRuntime ""))

    [<Test>]
    let ``The selection this run was given is valid`` () : unit =
        let selected = FrameworkUnderTest.selected ()
        selected.RuntimeDirs |> shouldEqual (FrameworkUnderTest.runtimeDirs ())

        FrameworkUnderTest.check selected.Runtime selected.RuntimeDirs
        |> Result.map (fun checkedAgain -> checkedAgain.CoreLibPath)
        |> shouldEqual (Ok selected.CoreLibPath)

    [<Test>]
    let ``Net10 is the test host's own framework`` () : unit =
        if FrameworkUnderTest.runtime () <> EmulatedRuntime.Net10 then
            Assert.Ignore "this run selected a runtime other than the test host's"

        FrameworkUnderTest.resolveLinks (FrameworkUnderTest.sharedFrameworkDirectory ())
        |> shouldEqual (FrameworkUnderTest.resolveLinks HostImage.frameworkDirectory)

    let private withTempDir (body : string -> unit) : unit =
        let dir =
            Path.Combine (Path.GetTempPath (), "PawPrintFrameworkUnderTest", Guid.NewGuid().ToString ("N"))

        Directory.CreateDirectory dir |> ignore<DirectoryInfo>

        try
            body dir
        finally
            Directory.Delete (dir, true)

    [<Test>]
    let ``A missing directory fails the selection even when a later one would do`` () : unit =
        withTempDir (fun parent ->
            let missing = Path.Combine (parent, "absent")

            let dirs =
                seq {
                    yield missing
                    yield! FrameworkUnderTest.runtimeDirs ()
                }
                |> ImmutableArray.CreateRange

            match FrameworkUnderTest.check (FrameworkUnderTest.runtime ()) dirs with
            | Error (FrameworkUnderTestError.MissingDirectory (_, found)) -> found |> shouldEqual missing
            | other -> failwith $"expected MissingDirectory, got %O{other}"
        )

    [<Test>]
    let ``resolveLinks spells a directory reached through symbolic links as the directory itself`` () : unit =
        // The host's CoreLib has its links resolved already.
        FrameworkUnderTest.resolveLinks HostImage.frameworkDirectory
        |> shouldEqual HostImage.frameworkDirectory

        withTempDir (fun parent ->
            // `outer` names `inner` relatively, and `inner` the framework absolutely.
            let inner = Path.Combine (parent, "inner")
            let outer = Path.Combine (parent, "outer")

            Directory.CreateSymbolicLink (inner, HostImage.frameworkDirectory)
            |> ignore<FileSystemInfo>

            Directory.CreateSymbolicLink (outer, "inner") |> ignore<FileSystemInfo>

            FrameworkUnderTest.resolveLinks outer
            |> shouldEqual HostImage.frameworkDirectory

            FrameworkUnderTest.resolveLinks (Path.Combine (outer, "System.Private.CoreLib.dll"))
            |> shouldEqual HostImage.coreLibPath
        )

    [<Test>]
    let ``Directories with no CoreLib fail the selection`` () : unit =
        withTempDir (fun dir ->
            match FrameworkUnderTest.check (FrameworkUnderTest.runtime ()) (ImmutableArray.Create dir) with
            | Error (FrameworkUnderTestError.NoCoreLib _) -> ()
            | other -> failwith $"expected NoCoreLib, got %O{other}"
        )

    [<TestCase 9us>]
    [<TestCase 12us>]
    let ``A CoreLib of another major ahead of the framework fails the selection`` (major : uint16) : unit =
        withTempDir (fun dir ->
            let patched = PatchedCoreLib.write major dir

            let dirs =
                seq {
                    yield dir
                    yield! FrameworkUnderTest.runtimeDirs ()
                }
                |> ImmutableArray.CreateRange

            match FrameworkUnderTest.check (FrameworkUnderTest.runtime ()) dirs with
            | Error (FrameworkUnderTestError.CoreLibMismatch (_, _, path, Error unsupported)) ->
                path |> shouldEqual (Some patched)
                unsupported.FoundMajor |> shouldEqual (int major)
            | other -> failwith $"expected CoreLibMismatch, got %O{other}"
        )

    let otherRuntimes : TestCaseData list = FrameworkUnderTest.everyRuntime

    [<TestCaseSource(nameof otherRuntimes)>]
    let ``Another supported runtime's framework ahead of the framework under test fails the selection``
        (other : EmulatedRuntime)
        : unit
        =
        if other = FrameworkUnderTest.runtime () then
            Assert.Ignore "this case is the runtime under test"

        let otherCoreLib = FrameworkUnderTest.requireCoreLibPathOf other

        let otherDir =
            match Path.GetDirectoryName otherCoreLib with
            | null -> failwith $"logic error: %s{otherCoreLib} has no directory"
            | dir -> dir

        let dirs =
            seq {
                yield otherDir
                yield! FrameworkUnderTest.runtimeDirs ()
            }
            |> ImmutableArray.CreateRange

        match FrameworkUnderTest.check (FrameworkUnderTest.runtime ()) dirs with
        | Error (FrameworkUnderTestError.CoreLibMismatch (expected, _, path, Ok found) as error) ->
            expected |> shouldEqual (FrameworkUnderTest.runtime ())
            path |> shouldEqual (Some otherCoreLib)
            found |> shouldEqual other

            FrameworkUnderTest.describe error
            |> shouldContainText $"is %s{FrameworkUnderTest.name other}'s"
        | result -> failwith $"expected CoreLibMismatch naming %O{other}, got %O{result}"

    [<Test>]
    let ``A run on the framework under test serves it`` () : unit =
        let image =
            Roslyn.compile [ "public static class Program { public static int Main() { return 3; } }" ]

        let _, loggerFactory = LoggerFactory.makeTest ()
        use _loggerFactoryResource = loggerFactory
        use peImage = new MemoryStream (image)

        let outcome =
            BoundedRun.run
                loggerFactory
                "Trivial.cs"
                (Some "Trivial.cs")
                peImage
                (HostConfig.Default (FrameworkUnderTest.runtimeDirsFor KernelConfig.Default.UnixPlatform))

        FrameworkUnderTest.assertOutcomeServes outcome

        match ExpectRun.ended outcome with
        | RunOutcome.NormalExit (state, _, _) -> state.LatchedExitCode |> shouldEqual 3
        | other -> failwith $"expected the guest to exit normally, got %O{other}"

    /// A guest returning 3, run along `dirs` on a kernel of `platform`.
    let private runTrivialOn (platform : SimulatedUnixPlatform) (dirs : ImmutableArray<string>) : RunEnd =
        let image =
            Roslyn.compile [ "public static class Program { public static int Main() { return 3; } }" ]

        let _, loggerFactory = LoggerFactory.makeTest ()
        use _loggerFactoryResource = loggerFactory
        use peImage = new MemoryStream (image)

        BoundedRun.run
            loggerFactory
            "Trivial.cs"
            (Some "Trivial.cs")
            peImage
            { HostConfig.Default dirs with
                Guest =
                    { GuestConfig.Default dirs with
                        Kernel =
                            { KernelConfig.Default with
                                UnixPlatform = platform
                            }
                    }
            }

    let flavours : TestCaseData list =
        [ SimulatedUnixFlavour.Linux ; SimulatedUnixFlavour.Darwin ]
        |> List.map (fun flavour -> TestCaseData(flavour).SetArgDisplayNames $"%A{flavour}")

    [<TestCaseSource(nameof flavours)>]
    let ``A guest runs on the pinned CoreLib of its kernel's flavour`` (flavour : SimulatedUnixFlavour) : unit =
        let pack = FrameworkUnderTest.requireFrameworkPackDirectory flavour
        let platform = HostPlatform.platformOf flavour
        let dirs = FrameworkUnderTest.runtimeDirsFor platform

        List.ofSeq dirs
        |> shouldEqual (pack :: List.ofSeq (FrameworkUnderTest.runtimeDirs ()))

        match ExpectRun.ended (runTrivialOn platform dirs) with
        | RunOutcome.NormalExit (state, _, _) ->
            state.LatchedExitCode |> shouldEqual 3

            (FrameworkUnderTest.loadedCoreLib state).OriginalPath
            |> shouldEqual (Some (Path.Combine (pack, "System.Private.CoreLib.dll")))
        | other -> failwith $"expected the guest to exit normally, got %O{other}"

    /// The other flavour's pack, and the framework under test itself, which is the host's flavour
    /// but is not the pack: the refusal does not depend on the host.
    [<TestCaseSource(nameof flavours)>]
    let ``A guest run on a CoreLib other than its kernel flavour's pack is refused``
        (flavour : SimulatedUnixFlavour)
        : unit
        =
        let other =
            match flavour with
            | SimulatedUnixFlavour.Linux -> SimulatedUnixFlavour.Darwin
            | SimulatedUnixFlavour.Darwin -> SimulatedUnixFlavour.Linux

        let platform = HostPlatform.platformOf flavour
        // Outside the Nix devshell no pack is named, and nothing is refused.
        FrameworkUnderTest.requireFrameworkPackDirectory flavour |> ignore<string>
        FrameworkUnderTest.requireFrameworkPackDirectory other |> ignore<string>

        for dirs in
            [
                FrameworkUnderTest.runtimeDirs ()
                FrameworkUnderTest.runtimeDirsFor (HostPlatform.platformOf other)
            ] do
            let e =
                Assert.Throws<Exception> (fun () -> runTrivialOn platform dirs |> ignore<RunEnd>)

            e.Message |> shouldContainText "FrameworkUnderTest.runtimeDirsFor"

    [<TestCase 9us>]
    [<TestCase 12us>]
    let ``A CoreLib of another major does not serve the framework under test`` (major : uint16) : unit =
        withTempDir (fun dir ->
            let patched = PatchedCoreLib.write major dir
            let corelib = Assembly.readFile NullLoggerFactory.Instance patched

            match FrameworkUnderTest.checkServes (FrameworkUnderTest.runtime ()) corelib with
            | Error (FrameworkUnderTestError.CoreLibMismatch (_, _, path, Error unsupported)) ->
                path |> shouldEqual (Some patched)
                unsupported.FoundMajor |> shouldEqual (int major)
            | other -> failwith $"expected CoreLibMismatch, got %O{other}"
        )

    /// A machine state that has loaded `corelib`, if any, and nothing else.
    let private stateHaving (corelib : DumpedAssembly option) : IlMachineState =
        let image =
            Roslyn.compile [ "public static class Program { public static void Main() { } }" ]

        use peImage = new MemoryStream (image)
        let entry = Assembly.read NullLoggerFactory.Instance None peImage

        let state =
            IlMachineState.initial NullLoggerFactory.Instance (FrameworkUnderTest.runtimeDirs ()) entry

        match corelib with
        | Some corelib -> state.WithLoadedAssembly corelib
        | None -> state

    [<Test>]
    let ``A state that loaded no CoreLib does not serve the framework under test`` () : unit =
        let e =
            Assert.Throws<Exception> (fun () -> FrameworkUnderTest.assertServes (stateHaving None))

        e.Message |> shouldContainText "No System.Private.CoreLib was loaded"

    [<Test>]
    let ``A state that loaded a CoreLib of another major does not serve the framework under test`` () : unit =
        withTempDir (fun dir ->
            let patched = PatchedCoreLib.write 12us dir
            let corelib = Assembly.readFile NullLoggerFactory.Instance patched

            let e =
                Assert.Throws<Exception> (fun () -> FrameworkUnderTest.assertServes (stateHaving (Some corelib)))

            e.Message |> shouldContainText "states major 12"
        )

    [<Test>]
    let ``A state that loaded the selected CoreLib serves the framework under test`` () : unit =
        let corelib =
            Assembly.readFile NullLoggerFactory.Instance (FrameworkUnderTest.selected ()).CoreLibPath

        FrameworkUnderTest.assertServes (stateHaving (Some corelib))

    /// The runtime identifier of the runtime pack which `flake.nix` pins for `flavour`.
    let private packRid (flavour : SimulatedUnixFlavour) : string =
        match flavour with
        | SimulatedUnixFlavour.Linux -> "linux-x64"
        | SimulatedUnixFlavour.Darwin -> "osx-arm64"

    /// The name of a `deps.json`'s runtime target.
    let private runtimeTargetName (root : JsonElement) : string =
        let runtimeTarget = root.GetProperty "runtimeTarget"

        match runtimeTarget.ValueKind with
        | JsonValueKind.String -> runtimeTarget.GetString ()
        | _ -> runtimeTarget.GetProperty("name").GetString ()

    /// `deps_json_t::load` takes `runtimeTarget` either as a string or as an object with a `name`.
    [<TestCase("""{ "runtimeTarget": "T/rid" }""")>]
    [<TestCase("""{ "runtimeTarget": { "name": "T/rid", "signature": "" } }""")>]
    let ``A deps.json's runtime target is read in either of its forms`` (json : string) : unit =
        use document = JsonDocument.Parse json
        runtimeTargetName document.RootElement |> shouldEqual "T/rid"

    let pinnedPacks : TestCaseData list =
        [
            for runtime in EmulatedRuntime.supported do
                for flavour in [ SimulatedUnixFlavour.Linux ; SimulatedUnixFlavour.Darwin ] do
                    TestCaseData(runtime, flavour).SetArgDisplayNames (FrameworkUnderTest.name runtime, $"%A{flavour}")
        ]

    /// Every runtime asset which the host reads from `Microsoft.NETCore.App`'s `deps.json`, whose
    /// target is `targetName`, as the file name the asset is listed under and the path, relative to
    /// the framework directory, at which the host looks for it. The host parses the lowest
    /// framework in self-contained mode (`deps_resolver.h`, `deps_json_t::load_self_contained`):
    /// for each package that `libraries` declares, the `runtime` assets that the target lists for
    /// it. `runtimeTargets` is not read in that mode.
    let private runtimeAssets (root : JsonElement) (targetName : string) : (string * string) list =
        let target = root.GetProperty("targets").GetProperty targetName

        [
            for library in root.GetProperty("libraries").EnumerateObject () do
                let libraryType = library.Value.GetProperty("type").GetString ()

                match target.TryGetProperty library.Name with
                | true, package ->
                    match package.TryGetProperty "runtime" with
                    | true, runtime ->
                        for asset in runtime.EnumerateObject () do
                            // `resolve_tpa_list` skips a placeholder.
                            if not (asset.Name.EndsWith ("/_._", StringComparison.OrdinalIgnoreCase)) then
                                let localPath =
                                    match asset.Value.TryGetProperty "localPath" with
                                    | true, localPath -> localPath.GetString ()
                                    | false, _ -> null

                                // `deps_entry_t::to_dir_path`, for a runtime asset.
                                let probed =
                                    match localPath with
                                    | null
                                    | "" ->
                                        if
                                            String.Equals (
                                                libraryType,
                                                "runtimepack",
                                                StringComparison.OrdinalIgnoreCase
                                            )
                                        then
                                            asset.Name
                                        else
                                            Path.GetFileName asset.Name
                                    | localPath -> localPath

                                yield Path.GetFileName asset.Name, probed
                    | false, _ -> ()
                | false, _ -> ()
        ]

    /// The host reads `Microsoft.NETCore.App`'s `deps.json` as it reads a self-contained
    /// application's: a target package's assets count only if `libraries` declares the package, and
    /// a RID-specific (`runtimeTargets`) asset never counts. `resolve_tpa_list` skips a placeholder,
    /// an asset whose path ends `/_._`.
    [<Test>]
    let ``A pack's runtime assets are those the host reads from the lowest framework`` () : unit =
        let json =
            """{
  "runtimeTarget": { "name": "T/rid" },
  "targets": {
    "T/rid": {
      "Declared/1.0.0": {
        "runtime": { "A.dll": {}, "lib/net10.0/_._": {} },
        "runtimeTargets": { "runtimes/rid/lib/net10.0/B.dll": { "rid": "rid", "assetType": "runtime" } }
      },
      "Undeclared/1.0.0": { "runtime": { "C.dll": {} } }
    }
  },
  "libraries": {
    "Declared/1.0.0": { "type": "package", "serviceable": true, "sha512": "" }
  }
}"""

        use document = JsonDocument.Parse json
        runtimeAssets document.RootElement "T/rid" |> shouldEqual [ "A.dll", "A.dll" ]

    /// `deps_entry_t::to_dir_path`: a runtime asset is at its `localPath` when it has one; else, in
    /// a `runtimepack` library, at its listed path; else at its listed path's file name.
    [<Test>]
    let ``A pack's runtime asset is where the host looks for it`` () : unit =
        let json =
            """{
  "runtimeTarget": { "name": "T/rid" },
  "targets": {
    "T/rid": {
      "Package/1.0.0": {
        "runtime": {
          "A.dll": { "localPath": "sub/A.dll" },
          "lib/net10.0/B.dll": {},
          "D.dll": { "localPath": "" }
        }
      },
      "Pack/1.0.0": { "runtime": { "lib/net10.0/C.dll": {} } }
    }
  },
  "libraries": {
    "Package/1.0.0": { "type": "package", "serviceable": true, "sha512": "" },
    "Pack/1.0.0": { "type": "runtimepack", "serviceable": false, "sha512": "" }
  }
}"""

        use document = JsonDocument.Parse json

        runtimeAssets document.RootElement "T/rid"
        |> shouldEqual
            [
                "A.dll", "sub/A.dll"
                "B.dll", "B.dll"
                "D.dll", "D.dll"
                "C.dll", "lib/net10.0/C.dll"
            ]

    /// Each pinned pack is a `Microsoft.NETCore.App` for its RID, with the `deps.json` its nupkg
    /// ships. Every assembly in the directory is a runtime asset of that `deps.json`, and every
    /// runtime asset is an assembly in the directory.
    [<TestCaseSource(nameof pinnedPacks)>]
    let ``A pinned runtime pack's deps.json lists exactly the pack's assemblies``
        (runtime : EmulatedRuntime)
        (flavour : SimulatedUnixFlavour)
        : unit
        =
        let pack = FrameworkUnderTest.requireFrameworkPackDirectoryOf flavour runtime
        let depsPath = Path.Combine (pack, "Microsoft.NETCore.App.deps.json")

        if not (File.Exists depsPath) then
            failwith
                $"%s{pack} holds no Microsoft.NETCore.App.deps.json: flake.nix's frameworkPack must copy it out of the runtime pack"

        use document = JsonDocument.Parse (File.ReadAllBytes depsPath)
        let root = document.RootElement
        let targetName = runtimeTargetName root

        targetName
        |> shouldEqual $".NETCoreApp,Version=v%i{EmulatedRuntime.major runtime}.0/%s{packRid flavour}"

        // Filtered here rather than by a search pattern, whose casing follows the platform:
        // `AssemblyProbe` binds `X.DLL` as readily as `X.dll`.
        let assemblies =
            Directory.GetFiles pack
            |> Seq.map Path.GetFileName
            |> Seq.filter (fun name -> name.EndsWith (".dll", StringComparison.OrdinalIgnoreCase))
            |> List.ofSeq

        assemblies |> shouldContain "System.Private.CoreLib.dll"

        let assets = runtimeAssets root targetName

        // Each asset is a file of its own name directly in the pack, so its TPA name is that
        // file's stem.
        assets |> List.filter (fun (name, path) -> name <> path) |> shouldEqual []

        let listed = assets |> List.map snd

        // Each difference is compared with the empty list, so a failure names exactly the
        // offending files rather than printing both lists in full.
        Set.difference (Set.ofList listed) (Set.ofList assemblies)
        |> Set.toList
        |> shouldEqual []

        Set.difference (Set.ofList assemblies) (Set.ofList listed)
        |> Set.toList
        |> shouldEqual []
