namespace WoofWare.PawPrint.Test

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Text.Json.Nodes
open FsUnitTyped
open Microsoft.CodeAnalysis
open NUnit.Framework
open WoofWare.PawPrint

/// `HostPolicy.resolveTpa` against the real host. Each case lays out an application directory
/// (a generated `deps.json` or none, with some of its files missing and none a real assembly but
/// the guest), runs a guest which writes `AppContext.GetData ("TRUSTED_PLATFORM_ASSEMBLIES")` on
/// the framework under test, and compares the function's map with CoreCLR's reading of that
/// string. The host reads only the `deps.json`, so the other files need not be assemblies.
///
/// The function's input comes from the child process itself: its RID list and its frameworks'
/// directories, as the host's trace names them.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestHostPolicyRealHost =

    /// Writes the TPA through `System.IO.File`, which CoreLib holds, so that the guest loads no
    /// assembly but itself, `System.Runtime` and CoreLib, whatever the TPA says of the others.
    let private guestSource : string =
        """
public static class Program
{
    static string Get(string key)
    {
        var value = System.AppContext.GetData(key);
        return value == null ? "<null>" : (string)value;
    }

    public static int Main(string[] args)
    {
        var text = new System.Text.StringBuilder();
        text.Append("TPA ").Append(Get("TRUSTED_PLATFORM_ASSEMBLIES")).Append('\n');
        text.Append("CORELIB ").Append(typeof(object).Assembly.Location).Append('\n');
        foreach (var a in System.AppDomain.CurrentDomain.GetAssemblies())
            text.Append("LOADED ").Append(a.GetName().Name).Append('\n');
        System.IO.File.WriteAllText(args[0], text.ToString());
        return 0;
    }
}
"""

    let private guestName : string = "TpaGuest"

    let private guestImage : Lazy<byte[]> =
        lazy (Roslyn.compileOptimizedAssembly guestName OutputKind.ConsoleApplication [ guestSource ])

    /// The assemblies the guest may load; no generated entry names one of them.
    let private guestLoads : Set<string> =
        Set.ofList [ guestName ; "System.Runtime" ; "System.Private.CoreLib" ]

    let private coreClrFileName : string =
        if RuntimeInformation.IsOSPlatform OSPlatform.OSX then
            "libcoreclr.dylib"
        else
            "libcoreclr.so"

    let private platform : HostPolicyPlatform =
        if RuntimeInformation.IsOSPlatform OSPlatform.OSX then
            HostPolicyPlatform.MacOS
        else
            HostPolicyPlatform.Linux

    /// The settings of decision 10 as the child has them. `launch` removes the variables which
    /// give them, so only the filesystem can: a store beside the muxer, or the default servicing
    /// directory. This errs towards reporting one; the trace check in `compare` catches one
    /// missed here.
    let private gatherSettings () : HostPolicySettings =
        // `shared_store::get_paths` names `<store>/<arch>/<tfm>`, and the host probes it only if it
        // exists.
        let version = Path.GetFileName (FrameworkUnderTest.sharedFrameworkDirectory ())

        let besideMuxer =
            Path.Combine (
                Path.GetDirectoryName RealRuntime.muxerPath,
                "store",
                RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant (),
                "net" + version.Substring (0, version.IndexOf '.') + ".0"
            )

        {
            UseRidGraph = false
            SetAppPaths = false
            AdditionalProbingPaths = []
            AdditionalDepsJsons = []
            // The host's `pal::directory_exists` is its `file_exists`: an entry of any kind counts.
            SharedStores = if Path.Exists besideMuxer then [ besideMuxer ] else []
            ServicingDirectory =
                if Path.Exists "/opt/coreservicing" then
                    Some "/opt/coreservicing"
                else
                    None
        }

    /// What one run of the guest left behind.
    type private HostRun =
        {
            ExitCode : int
            /// What the guest wrote, or `None` if it wrote nothing.
            Output : string option
            Trace : string
            Stderr : string
        }

    /// Whether `run` ended as the host does when it fails the launch as `failure` predicts: the
    /// guest wrote nothing, the process exited with the host's status for that failure (the low
    /// byte of its `StatusCode`, `src/native/corehost/error_codes.h`), and stderr holds the host's
    /// message for it.
    let private hostFailedAs (failure : HostPolicyFailure) (run : HostRun) : bool =
        let status, message =
            match failure with
            // `ResolverInitFailure`, from `deps_resolver_t::valid`.
            | HostPolicyFailure.FrameworkDepsJsonMissing _ -> 0x8B, "missing dependencies manifest"
            | HostPolicyFailure.DepsJsonRejected _ -> 0x8B, "An error occurred while parsing"
            // `ResolverResolveFailure`, from `resolve_tpa_list` and `resolve_probe_dirs`.
            | HostPolicyFailure.DuplicateWithDifferentExtension _ ->
                0x8C, "has already been found but with a different file extension"
            // The duplicate's message begins the same way; this one ends so.
            | HostPolicyFailure.AssetNotFound _ -> 0x8C, ") was not found:"
            // `CoreClrResolveFailure`.
            | HostPolicyFailure.CoreClrNotFound -> 0x87, "Could not resolve CoreCLR path"
            // `CoreClrInitFailure`: `coreclr_initialize` returned `E_INVALIDARG`.
            | HostPolicyFailure.RuntimeRejectsTpa _ -> 0x89, "Failed to create CoreCLR, HRESULT: 0x80070057"

        run.Output.IsNone && run.ExitCode = status && run.Stderr.Contains message

    /// Run the guest in `appDir` on the real host with `runtimeConfig`, tracing the host.
    let private launch (scratch : string) (appDir : string) (runtimeConfig : string) (rollForward : bool) : HostRun =
        let configPath = Path.Combine (scratch, "guest.runtimeconfig.json")
        File.WriteAllText (configPath, runtimeConfig)
        let output = Path.Combine (scratch, "out.txt")
        let trace = Path.Combine (scratch, "trace.txt")
        let psi = ProcessStartInfo RealRuntime.muxerPath
        psi.ArgumentList.Add "exec"

        // The host prefers `DOTNET_ROLL_FORWARD`, which the test host may set, to the
        // runtimeconfig, and the command line to both.
        if rollForward then
            psi.ArgumentList.Add "--roll-forward"
            psi.ArgumentList.Add "Disable"

        psi.ArgumentList.Add "--runtimeconfig"
        psi.ArgumentList.Add configPath
        psi.ArgumentList.Add (Path.Combine (appDir, guestName + ".dll"))
        psi.ArgumentList.Add output
        psi.WorkingDirectory <- scratch
        psi.UseShellExecute <- false
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        // The host prefers the `DOTNET_HOST_TRACE` variables to the `COREHOST_TRACE` ones; set
        // both, so that neither inherited spelling decides the trace this parses.
        for prefix in [ "DOTNET_HOST_TRACE" ; "COREHOST_TRACE" ] do
            psi.Environment.[prefix] <- "1"
            psi.Environment.[prefix + "_VERBOSITY"] <- "4"
            psi.Environment.[prefix + "FILE"] <- trace

        // The settings of decision 10 which the environment gives, removed so that the child runs
        // with the settings `gatherSettings` reports.
        // And startup hooks, which would load assemblies the comparison does not allow.
        for name in
            [
                "CORE_SERVICING"
                "DOTNET_SHARED_STORE"
                "DOTNET_ADDITIONAL_DEPS"
                "DOTNET_STARTUP_HOOKS"
            ] do
            psi.Environment.Remove name |> ignore<bool>

        use proc = Process.Start psi
        let stdout = proc.StandardOutput.ReadToEndAsync ()
        let stderr = proc.StandardError.ReadToEndAsync ()

        if not (proc.WaitForExit (TimeSpan.FromSeconds 60.0)) then
            proc.Kill true
            failwith $"The guest in %s{appDir} did not exit within a minute."

        stdout.Wait ()

        {
            ExitCode = proc.ExitCode
            Output =
                if File.Exists output then
                    Some (File.ReadAllText output)
                else
                    None
            Trace = if File.Exists trace then File.ReadAllText trace else ""
            Stderr = stderr.Result
        }

    /// The lines of `trace` after the first line equal to `header`, up to the first that
    /// `isEnd` accepts.
    let private traceBlock (trace : string) (header : string) (isEnd : string -> bool) : string list option =
        let lines = trace.Split '\n' |> Array.map (fun l -> l.TrimEnd '\r')

        match Array.tryFindIndex (fun (l : string) -> l = header) lines with
        | None -> None
        | Some start ->
            lines
            |> Array.skip (start + 1)
            |> Array.takeWhile (isEnd >> not)
            |> List.ofArray
            |> Some

    /// The host's RID list, as its trace prints it. The host prints it when it reads a
    /// `deps.json` as framework-dependent, and reads the list for nothing else; so when it read
    /// none that way (`readFrameworkDependent` is false), the list is empty.
    let private ridsOf (readFrameworkDependent : bool) (trace : string) : string list =
        match traceBlock trace "Host RID list = [" (fun l -> l = "]") with
        | Some lines -> lines |> List.map (fun l -> l.Trim().TrimEnd ',')
        | None ->
            if readFrameworkDependent then
                failwith $"The host's trace has no RID list.\n%s{trace}"

            []

    /// The host's probe configurations, as its trace prints them.
    let private probesOf (trace : string) : string list =
        match
            traceBlock
                trace
                "-- Probe configurations:"
                (fun l -> not (l.StartsWith ("  probe ", StringComparison.Ordinal)))
        with
        | Some lines -> lines |> List.map (fun l -> l.Trim ())
        | None -> failwith "The host's trace has no probe configurations."

    /// The frameworks' directories, highest level first, as the host's probe configurations name
    /// them.
    let private frameworkDirsOf (trace : string) : string list =
        probesOf trace
        |> List.choose (fun probe ->
            let prefix = "probe type=framework dir=["

            if probe.StartsWith (prefix, StringComparison.Ordinal) then
                let close = probe.IndexOf ("] fx_level=", StringComparison.Ordinal)
                Some (probe.Substring (prefix.Length, close - prefix.Length))
            else
                None
        )

    /// CoreCLR's map from the TPA string: the simple name of each path, ignoring case, keeping
    /// the first path for a name (`ApplicationContext::SetupBindingPaths`).
    let private coreClrMap (tpa : string) : Map<string, string> =
        tpa.Split (':', StringSplitOptions.RemoveEmptyEntries)
        |> Array.fold
            (fun (acc : (string * string) list) (path : string) ->
                let file = path.Substring (path.LastIndexOf '/' + 1)
                let lower = file.ToLowerInvariant ()

                let stem =
                    if
                        lower.EndsWith (".ni.dll", StringComparison.Ordinal)
                        || lower.EndsWith (".ni.exe", StringComparison.Ordinal)
                    then
                        file.Substring (0, file.Length - 7)
                    else
                        file.Substring (0, file.Length - 4)

                if
                    acc
                    |> List.exists (fun (n, _) -> String.Equals (n, stem, StringComparison.OrdinalIgnoreCase))
                then
                    acc
                else
                    (stem, path) :: acc
            )
            []
        |> Map.ofList

    /// What a comparison found.
    [<RequireQualifiedAccess>]
    type private Compared =
        | SameTpa
        | HostFailedAsPredicted
        | NotReproducible

    /// Compare the function's answer for `input` with what `run` shows the host did.
    let private compare (description : string) (input : HostPolicyInput) (run : HostRun) : Compared =
        let context () =
            $"%s{description}\nexit code %d{run.ExitCode}\nstderr:\n%s{run.Stderr}\nguest output:\n%A{run.Output}"

        match HostPolicy.resolveTpa input with
        | Error (HostPolicyRefusal.NotReproducible (HostPolicyUnreproducible.UnmodelledSettings settings)) ->
            Assert.Inconclusive
                $"This host turns on file existence checks or adds deps.json files (%A{settings}), which HostPolicy.resolveTpa does not model, so the real host cannot be compared with it here."

            failwith "unreachable"
        | Error (HostPolicyRefusal.NotReproducible _) -> Compared.NotReproducible
        | Error (HostPolicyRefusal.HostWouldFail failure) ->
            if not (hostFailedAs failure run) then
                Assert.Fail
                    $"HostPolicy.resolveTpa says the host would fail (%A{failure}), but the run did not end as that failure ends.\n%s{context ()}"

            Compared.HostFailedAsPredicted
        | Ok resolution ->
            let tpa = resolution.Tpa

            let output =
                match run.Output with
                | Some output -> output
                | None ->
                    Assert.Fail $"HostPolicy.resolveTpa gave a TPA, but the guest did not run.\n%s{context ()}"
                    failwith "unreachable"

            let lines = output.Split '\n'

            let loaded =
                lines
                |> Array.choose (fun l ->
                    if l.StartsWith ("LOADED ", StringComparison.Ordinal) then
                        Some (l.Substring 7)
                    else
                        None
                )
                |> Set.ofArray

            if not (Set.isSubset loaded guestLoads) then
                Assert.Fail
                    $"The guest loaded %A{Set.difference loaded guestLoads}, which a generated entry may name; keep those names out of the generator.\n%s{context ()}"

            let tpaLine =
                lines |> Array.find (fun l -> l.StartsWith ("TPA ", StringComparison.Ordinal))

            // The CoreLib the guest runs on is the TPA's, by its real path. And what the caller must
            // check, since the function cannot see links, says so too: the host opens the CoreCLR
            // library of its fixed name in the directory of the found library's real path, and
            // CoreCLR loads CoreLib from beside the real path of that.
            let ranOn =
                lines
                |> Array.find (fun l -> l.StartsWith ("CORELIB ", StringComparison.Ordinal))
                |> fun l -> FrameworkUnderTest.resolveLinks (l.Substring 8)

            match TrustedPlatformAssemblies.tryFindIgnoringCase "System.Private.CoreLib" tpa with
            | None -> Assert.Fail $"HostPolicy.resolveTpa gave a TPA without CoreLib.\n%s{context ()}"
            | Some (_, coreLib) ->
                if FrameworkUnderTest.resolveLinks coreLib <> ranOn then
                    Assert.Fail $"The guest ran on CoreLib %s{ranOn}, but the TPA's is %s{coreLib}.\n%s{context ()}"

                let found = FrameworkUnderTest.resolveLinks resolution.CoreClr

                let fixedName = Path.Combine (Path.GetDirectoryName found, coreClrFileName)

                if not (File.Exists fixedName) then
                    Assert.Fail $"The host opens %s{fixedName}, which does not exist.\n%s{context ()}"

                let appended =
                    Path.Combine (Path.GetDirectoryName found, "System.Private.CoreLib.dll")

                if appended.Contains ':' then
                    Assert.Fail
                        $"The host appends %s{appended} to the TPA string, which CoreCLR splits at ':'.\n%s{context ()}"

                let loaded = FrameworkUnderTest.resolveLinks fixedName

                let beside =
                    Path.Combine (Path.GetDirectoryName loaded, "System.Private.CoreLib.dll")
                    |> FrameworkUnderTest.resolveLinks

                if beside <> ranOn then
                    Assert.Fail
                        $"The CoreLib beside %s{loaded} is %s{beside}, but the guest ran on %s{ranOn}.\n%s{context ()}"

            // The host checked no file's existence: every probe it set up is the application's or
            // a framework's, and no probe looked at the disk.
            for probe in probesOf run.Trace do
                if
                    probe <> "probe type=app"
                    && not (probe.StartsWith ("probe type=framework ", StringComparison.Ordinal))
                then
                    Assert.Fail
                        $"The host set up the probe %s{probe}, which the settings gathered here did not report.\n%s{context ()}"

            if run.Trace.Contains "    Exists: " || run.Trace.Contains "    Does not exist: " then
                Assert.Fail
                    $"The host checked files' existence, which the settings gathered here did not report.\n%s{context ()}"

            let expected = TrustedPlatformAssemblies.toMap tpa
            let actual = coreClrMap (tpaLine.Substring 4)

            let normalise (m : Map<string, string>) : Map<string, string> =
                m |> Map.toSeq |> Seq.map (fun (n, p) -> n.ToUpperInvariant (), p) |> Map.ofSeq

            if normalise actual <> normalise expected then
                let missing =
                    Map.toList expected
                    |> List.filter (fun (n, p) -> Map.tryFind n actual <> Some p)

                let extra =
                    Map.toList actual
                    |> List.filter (fun (n, p) ->
                        not (
                            expected
                            |> Map.exists (fun n' p' ->
                                String.Equals (n, n', StringComparison.OrdinalIgnoreCase) && p = p'
                            )
                        )
                    )

                Assert.Fail
                    $"The real host's TPA differs from HostPolicy.resolveTpa's.\nOnly the function's: %A{missing}\nOnly the host's: %A{extra}\n%s{context ()}"

            Compared.SameTpa

    /// The host switches `gatherSettings` reports as off, set off in the application's
    /// runtimeconfig, whose properties the host prefers to its frameworks'.
    let private pinnedSwitches () : JsonObject =
        let o = JsonObject ()
        o.Add ("System.Runtime.Loader.UseRidGraph", JsonValue.Create false)
        o.Add ("Microsoft.NETCore.DotNetHostPolicy.SetAppPaths", JsonValue.Create false)
        o

    /// A runtimeconfig naming `frameworks` (name and version), with roll-forward disabled, and
    /// `pinnedSwitches`.
    let private frameworkDependentConfig (frameworks : (string * string) list) : string =
        let version = snd (List.head frameworks)
        let tfm = "net" + version.Substring (0, version.IndexOf '.') + ".0"
        let array = JsonArray ()

        for name, version in frameworks do
            let o = JsonObject ()
            o.Add ("name", JsonValue.Create name)
            o.Add ("version", JsonValue.Create version)
            array.Add o

        let options = JsonObject ()
        options.Add ("tfm", JsonValue.Create tfm)
        options.Add ("rollForward", JsonValue.Create "Disable")
        options.Add ("frameworks", array)
        options.Add ("configProperties", pinnedSwitches ())
        let root = JsonObject ()
        root.Add ("runtimeOptions", options)
        root.ToJsonString ()

    /// The runtime assets of `Microsoft.NETCore.App` under test: name, path as listed, and the two
    /// versions as written.
    let private frameworkAssets : Lazy<(string * string * string * string) list> =
        lazy
            (let dir = FrameworkUnderTest.sharedFrameworkDirectory ()
             let path = Path.Combine (dir, "Microsoft.NETCore.App.deps.json")
             let root = JsonNode.Parse (File.ReadAllText path)
             let target = root.["runtimeTarget"].["name"].GetValue<string> ()

             [
                 for KeyValue (_, package : JsonNode) in root.["targets"].[target].AsObject () do
                     let package = package.AsObject ()

                     if package.ContainsKey "runtime" then
                         for KeyValue (key : string, asset : JsonNode) in package.["runtime"].AsObject () do
                             let file = key.Substring (key.LastIndexOf '/' + 1)

                             if file.EndsWith (".dll", StringComparison.Ordinal) then
                                 let name = file.Substring (0, file.Length - 4)
                                 let o = asset.AsObject ()

                                 yield
                                     name,
                                     key,
                                     o.["assemblyVersion"].GetValue<string> (),
                                     o.["fileVersion"].GetValue<string> ()
             ]
             |> List.filter (fun (name, _, _, _) -> not (guestLoads.Contains name)))

    /// A version below, equal to or above `version` in one component, or no version, or one the
    /// host cannot parse.
    let private varyVersion (rng : Random) (version : string) : string option =
        let parts = version.Split '.' |> Array.map int

        let withPart (i : int) (delta : int) : string option =
            parts
            |> Array.mapi (fun j p -> if j = i then max 0 (p + delta) else p)
            |> Array.map string<int>
            |> String.concat "."
            |> Some

        match rng.Next 8 with
        | 0 -> withPart 0 -1
        | 1
        | 2 -> Some version
        | 3 -> withPart (parts.Length - 1) 1
        | 4 -> withPart (parts.Length - 1) -1
        | 5 -> withPart 0 1
        | 6 -> None
        | _ -> Some "x"

    /// One generated application directory, laid out under `appDir`, with what the host must be
    /// told of it. Returns the manifest and a description.
    let private layOut (rng : Random) (appDir : string) : HostPolicyAppManifest * string =
        let assets = frameworkAssets.Force () |> Array.ofList
        let pick () = assets.[rng.Next assets.Length]
        let junk = "not an assembly"B

        let write (relative : string) : unit =
            let path = Path.Combine (appDir, relative)
            Directory.CreateDirectory (Path.GetDirectoryName path) |> ignore<DirectoryInfo>
            File.WriteAllBytes (path, junk)

        File.WriteAllBytes (Path.Combine (appDir, guestName + ".dll"), guestImage.Force ())
        let entryCount = 1 + rng.Next 6
        // About one case in seven has one entry which makes the launch fail.
        let failing =
            if rng.Next 7 = 0 then
                Some (1 + rng.Next entryCount)
            else
                None

        if rng.Next 5 = 0 then
            // No deps.json: the host takes the directory's files, with empty versions.
            let names =
                [
                    for _ in 1..entryCount do
                        match rng.Next 6 with
                        | 0 -> yield $"Extra%d{rng.Next 3}.dll"
                        | _ ->
                            let name, _, _, _ = pick ()
                            yield name + ".dll"
                ]
                |> List.distinct

            for name in names do
                write name

            let listing =
                Directory.GetFileSystemEntries appDir
                |> Array.map Path.GetFileName
                |> List.ofArray

            let coreClrExists = File.Exists (Path.Combine (appDir, coreClrFileName))
            HostPolicyAppManifest.Listing (listing, coreClrExists), $"no deps.json: %A{names}"
        else
            let library (key : string) (runtime : ManifestAsset list) (rid : ManifestRidAsset list) : ManifestLibrary =
                { ManifestSpec.library key runtime with
                    RidSpecific = rid
                }

            let libraries =
                [
                    yield
                        { ManifestSpec.library $"%s{guestName}/1.0.0" [ ManifestSpec.asset (guestName + ".dll") ] with
                            Type = "project"
                        }
                    for k in 1..entryCount do
                        let name, _, assemblyVersion, fileVersion =
                            match rng.Next 6 with
                            | 0 -> $"Extra%d{rng.Next 3}", "", "1.0.0.0", "1.0.0.0"
                            | _ -> pick ()

                        let asset (path : string) (localPath : string option) : ManifestAsset =
                            {
                                Path = path
                                AssemblyVersion = varyVersion rng assemblyVersion
                                FileVersion = varyVersion rng fileVersion
                                LocalPath = localPath
                            }

                        let key = $"Lib%d{k}/1.0.%d{rng.Next 2}"

                        let variant =
                            match failing with
                            | Some failingEntry when failingEntry = k -> rng.Next 2
                            | _ -> 2 + rng.Next 18

                        match variant with
                        | 0 ->
                            // The host fails: a name the framework lists, with another extension.
                            yield library key [ asset $"lib/net10.0/%s{name}.exe" None ] []
                        | 1 ->
                            // CoreCLR fails: a path with no assembly extension.
                            yield library key [ asset $"lib/net10.0/%s{name}.dll" (Some $"sub/%s{name}.txt") ] []
                        | 2
                        | 3 ->
                            let localPath = $"sub/%s{name}.dll"

                            if rng.Next 3 > 0 then
                                write localPath

                            yield library key [ asset $"lib/net10.0/%s{name}.dll" (Some localPath) ] []
                        | 4
                        | 5
                        | 6 ->
                            let rids =
                                [ "osx-arm64" ; "osx" ; "unix" ; "linux-x64" ; "linux" ; "any" ; "win-x64" ]
                                |> List.filter (fun _ -> rng.Next 2 = 0)

                            let ridAssets =
                                [
                                    for rid in rids do
                                        let path = $"runtimes/%s{rid}/lib/net10.0/%s{name}.dll"

                                        if rng.Next 3 > 0 then
                                            write path

                                        yield
                                            {
                                                Asset = asset path None
                                                Rid = rid
                                                AssetType = "runtime"
                                            }
                                ]

                            yield library key [ asset $"lib/net10.0/%s{name}.dll" None ] ridAssets
                        | _ ->
                            if rng.Next 3 > 0 then
                                write (name + ".dll")

                            yield library key [ asset $"lib/net10.0/%s{name}.dll" None ] []
                ]

            let spec : ManifestSpec =
                {
                    Libraries = libraries
                }

            let text = ManifestSpec.renderText spec
            File.WriteAllText (Path.Combine (appDir, guestName + ".deps.json"), text)
            HostPolicyAppManifest.DepsJson (Encoding.UTF8.GetBytes text), $"deps.json: %s{text}"

    /// A fresh directory under the temp directory, with its links resolved, as the host spells it.
    let private freshDirectory () : string =
        let temp = Directory.CreateTempSubdirectory "pawprint-tpa-"
        FrameworkUnderTest.resolveLinks temp.FullName

    let private deleteTree (dir : string) : unit =
        try
            for entry in Directory.EnumerateFileSystemEntries (dir, "*", SearchOption.AllDirectories) do
                File.SetAttributes (entry, FileAttributes.Normal)

            Directory.Delete (dir, true)
        with
        | :? IOException
        | :? UnauthorizedAccessException -> ()

    /// What the host wrote, measured on .NET 10.0.14 for the duplicate (exit 140) and CoreCLR's
    /// refusal of the TPA (exit 137).
    let private duplicateMessage : string =
        "Error:\n  An assembly specified in the application dependencies manifest (Microsoft.NETCore.App.deps.json) has already been found but with a different file extension:\n    package: 'Microsoft.NETCore.App.Runtime.osx-arm64', version: '10.0.14'\n    path: 'System.Net.Primitives.dll'\n"

    let private duplicate : HostPolicyFailure =
        HostPolicyFailure.DuplicateWithDifferentExtension (
            HostPolicyManifest.Framework "Microsoft.NETCore.App",
            "System.Net.Primitives",
            "System.Net.Primitives.dll",
            "/app/sub/System.Net.Primitives.txt"
        )

    let private tpaRejected : HostPolicyFailure =
        HostPolicyFailure.RuntimeRejectsTpa (TpaRefusal.NoAssemblyExtension ("Extra", "/app/sub/Extra.txt"))

    let private ended (exitCode : int) (stderr : string) : HostRun =
        {
            ExitCode = exitCode
            Output = None
            Trace = ""
            Stderr = stderr
        }

    [<Test>]
    let ``The oracle confirms a predicted host failure by its status and message`` () =
        hostFailedAs duplicate (ended 140 duplicateMessage) |> shouldEqual true

        hostFailedAs tpaRejected (ended 137 "Failed to create CoreCLR, HRESULT: 0x80070057\n")
        |> shouldEqual true

    /// A guest which crashes before writing its report must not pass as the host failing.
    [<Test>]
    let ``The oracle does not take a guest crash for a predicted host failure`` () =
        let crash =
            ended 134 "Unhandled exception. System.BadImageFormatException: Bad IL format.\n"

        hostFailedAs duplicate crash |> shouldEqual false
        hostFailedAs tpaRejected crash |> shouldEqual false

    /// A process killed by SIGKILL exits 137, the host's status for CoreCLR refusing the TPA.
    [<Test>]
    let ``The oracle does not take a killed guest for CoreCLR refusing the TPA`` () =
        hostFailedAs tpaRejected (ended 137 "") |> shouldEqual false

    /// hostpolicy prints the missing-assembly message as a warning too, for an application entry,
    /// and launches; the guest can then crash.
    [<Test>]
    let ``The oracle does not take a warning followed by a guest crash for a missing asset`` () =
        let missing =
            HostPolicyFailure.AssetNotFound (
                HostPolicyManifest.Framework "Microsoft.NETCore.App",
                DepsAssetType.Runtime,
                "P",
                "P",
                "Y.dll"
            )

        let warnedThenCrashed =
            ended
                134
                "Warning:\n  An assembly specified in the application dependencies manifest (TpaGuest.deps.json) was not found:\nUnhandled exception. System.IO.FileNotFoundException\n"

        hostFailedAs missing warnedThenCrashed |> shouldEqual false

        // The duplicate's message begins as the missing-assembly one does, and exits with the
        // same status.
        hostFailedAs missing (ended 140 duplicateMessage) |> shouldEqual false

    [<Test>]
    let ``The oracle does not take one host failure for another`` () =
        hostFailedAs tpaRejected (ended 140 duplicateMessage) |> shouldEqual false

        hostFailedAs duplicate (ended 137 "Failed to create CoreCLR, HRESULT: 0x80070057\n")
        |> shouldEqual false

    [<Test>]
    let ``resolveTpa agrees with the real host on generated applications`` () =
        let seed = Random.Shared.Next ()
        TestContext.Out.WriteLine $"seed %d{seed}"
        let rng = Random seed
        let stopwatch = Stopwatch.StartNew ()
        let netCoreDir = FrameworkUnderTest.sharedFrameworkDirectory ()

        let outcomes =
            [
                for case in 1..25 do
                    let root = freshDirectory ()

                    try
                        let appDir = Path.Combine (root, "app")
                        let scratch = Path.Combine (root, "scratch")
                        Directory.CreateDirectory appDir |> ignore<DirectoryInfo>
                        Directory.CreateDirectory scratch |> ignore<DirectoryInfo>
                        let manifest, description = layOut rng appDir
                        let withAspNetCore = rng.Next 4 = 0

                        let configured =
                            [
                                yield "Microsoft.NETCore.App", Path.GetFileName netCoreDir
                                if withAspNetCore then
                                    yield
                                        "Microsoft.AspNetCore.App",
                                        Path.GetFileName (FrameworkUnderTest.aspNetCoreFrameworkDirectory ())
                            ]

                        let run = launch scratch appDir (frameworkDependentConfig configured) true

                        // The frameworks, in the host's order, with the directories it gave them.
                        let frameworks =
                            frameworkDirsOf run.Trace
                            |> List.map (fun dir ->
                                let name = Path.GetFileName (Path.GetDirectoryName dir)
                                let depsPath = Path.Combine (dir, name + ".deps.json")

                                ({
                                    Name = name
                                    Directory = dir
                                    DepsJson =
                                        if File.Exists depsPath then
                                            Some (File.ReadAllBytes depsPath)
                                        else
                                            None
                                }
                                : HostPolicyFramework)
                            )

                        if frameworks.Length <> configured.Length then
                            Assert.Fail
                                $"The host set up %A{frameworks |> List.map (fun f -> f.Directory)} for %A{configured}.\n%s{run.Trace}"

                        let input : HostPolicyInput =
                            {
                                Runtime = FrameworkUnderTest.runtime ()
                                Platform = platform
                                Launch =
                                    HostPolicyLaunch.FrameworkDependent (
                                        HostPolicyApplication.OnDisk
                                            {
                                                Directory = appDir
                                                Manifest = manifest
                                                EntryFileName = guestName + ".dll"
                                            },
                                        ridsOf
                                            (frameworks.Length > 1
                                             || (
                                                 match manifest with
                                                 | HostPolicyAppManifest.DepsJson _ -> true
                                                 | HostPolicyAppManifest.Listing _ -> false
                                             ))
                                            run.Trace,
                                        List.take (frameworks.Length - 1) frameworks,
                                        List.last frameworks
                                    )
                                EntryAssemblyName = guestName
                                EntryAssemblyCulture = None
                                Settings = gatherSettings ()
                            }

                        yield compare $"case %d{case}, seed %d{seed}: %s{description}" input run
                    finally
                        deleteTree root
            ]

        let count (c : Compared) : int =
            outcomes |> List.filter ((=) c) |> List.length

        TestContext.Out.WriteLine
            $"%d{count Compared.SameTpa} same TPA, %d{count Compared.HostFailedAsPredicted} host failures predicted, %d{count Compared.NotReproducible} not reproducible, in %d{stopwatch.ElapsedMilliseconds} ms"

        // A case compares a TPA with probability above 0.8, so fewer than four of 25 happens by
        // chance less than once in 10^12 runs.
        if count Compared.SameTpa < 4 then
            Assert.Fail $"Only %d{count Compared.SameTpa} of 25 cases compared a TPA (seed %d{seed})."

    [<Test>]
    let ``resolveTpa agrees with the real host on a self-contained application`` () =
        let root = freshDirectory ()

        try
            let appDir = Path.Combine (root, "app")
            let scratch = Path.Combine (root, "scratch")
            Directory.CreateDirectory appDir |> ignore<DirectoryInfo>
            Directory.CreateDirectory scratch |> ignore<DirectoryInfo>
            let frameworkDir = FrameworkUnderTest.sharedFrameworkDirectory ()

            // The runtime's own files, as a self-contained publish carries them.
            for file in Directory.GetFiles frameworkDir do
                let name = Path.GetFileName file

                if not (name.StartsWith ("Microsoft.NETCore.App.", StringComparison.Ordinal)) then
                    let target = Path.Combine (appDir, name)
                    File.Copy (file, target)
                    File.SetAttributes (target, FileAttributes.Normal)

            File.WriteAllBytes (Path.Combine (appDir, guestName + ".dll"), guestImage.Force ())

            // The framework's manifest, with the guest's library added: what the SDK writes for a
            // self-contained publish, less the libraries this guest does not need.
            let root' =
                JsonNode.Parse (File.ReadAllText (Path.Combine (frameworkDir, "Microsoft.NETCore.App.deps.json")))

            let target = root'.["runtimeTarget"].["name"].GetValue<string> ()
            let guestTarget = JsonObject ()
            let runtime = JsonObject ()
            runtime.Add (guestName + ".dll", JsonObject ())
            guestTarget.Add ("runtime", runtime)
            root'.["targets"].[target].AsObject().Add ($"%s{guestName}/1.0.0", guestTarget)
            let guestLibrary = JsonObject ()
            guestLibrary.Add ("type", JsonValue.Create "project")
            guestLibrary.Add ("serviceable", JsonValue.Create false)
            guestLibrary.Add ("sha512", JsonValue.Create "")
            root'.["libraries"].AsObject().Add ($"%s{guestName}/1.0.0", guestLibrary)
            let depsText = root'.ToJsonString ()
            File.WriteAllText (Path.Combine (appDir, guestName + ".deps.json"), depsText)

            let version = Path.GetFileName frameworkDir
            let tfm = "net" + version.Substring (0, version.IndexOf '.') + ".0"

            let config =
                $"""{{"runtimeOptions":{{"tfm":"%s{tfm}","includedFrameworks":[{{"name":"Microsoft.NETCore.App","version":"%s{version}"}}],"configProperties":%s{(pinnedSwitches ()).ToJsonString ()}}}}}"""

            let run = launch scratch appDir config false

            if not (run.Trace.Contains "Executing as a self-contained app") then
                Assert.Fail $"The host did not run the guest as self-contained.\n%s{run.Stderr}"

            let input : HostPolicyInput =
                {
                    Runtime = FrameworkUnderTest.runtime ()
                    Platform = platform
                    Launch =
                        HostPolicyLaunch.SelfContained
                            {
                                Directory = appDir
                                Manifest = HostPolicyAppManifest.DepsJson (Encoding.UTF8.GetBytes depsText)
                                EntryFileName = guestName + ".dll"
                            }
                    EntryAssemblyName = guestName
                    EntryAssemblyCulture = None
                    Settings = gatherSettings ()
                }

            match compare "self-contained" input run with
            | Compared.SameTpa -> ()
            | other -> Assert.Fail $"Expected a TPA to compare, got %A{other}"
        finally
            deleteTree root
