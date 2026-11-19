namespace WoofWare.PawPrint.Test

open System
open System.Collections.Generic
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// `HostPolicy.resolveTpa` against a line-by-line transcription of hostpolicy's
/// `deps_resolver_t::resolve_tpa_list`, `get_dir_assemblies`, `probe_deps_entry` and the native
/// half of `resolve_probe_dirs` (`src/native/corehost/hostpolicy/deps_resolver.cpp`), with
/// PawPrint's own refusals written out beside it; then the metamorphic properties of the plan for
/// #2351, its measured table, and a unit case per refusal.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestHostPolicy =

    let private shouldBe<'a when 'a : equality> (expected : 'a) (actual : 'a) : unit =
        if actual <> expected then
            Assert.Fail $"Expected:\n%A{expected}\nActual:\n%A{actual}"

    // ---------------------------------------------------------------------------------------
    // The transcription.

    /// What the transcription's coverage counts.
    [<RequireQualifiedAccess>]
    type private Label =
        /// The outcome: `Ok`, or the refusal's case.
        | Outcome of string
        /// A framework entry replaced an application entry.
        | AppEntryReplacedByFramework
        /// A later application entry replaced an earlier one.
        | AppEntryReplacedByApp
        /// A later entry with equal versions replaced an earlier one.
        | TieReplaced
        /// An application entry kept its name against an older framework entry.
        | AppEntryKeptAgainstFramework
        /// A later entry was as new, but resolved to the same path, so the earlier one's versions
        /// stayed.
        | SamePathKept
        /// A framework entry resolved into the directory of a lower framework.
        | FrameworkEntryInLowerFramework
        /// An application's RID-specific entry with no `localPath` was probed at its listed path.
        | RidSpecificAppEntryAtListedPath
        /// A file of the application's directory listing joined the TPA.
        | ListingEntry
        /// The CoreCLR library was the application's.
        | CoreClrInApp

    type private RefusedException (refusal : HostPolicyRefusal) =
        inherit Exception ($"%A{refusal}")
        member _.Refusal : HostPolicyRefusal = refusal

    /// `deps_resolved_asset_t`, and the level of the manifest it came from, for coverage.
    type private ResolvedAsset =
        {
            ResolvedPath : string
            AssemblyVersion : DepsVersion
            FileVersion : DepsVersion
            Level : int
        }

    let private coreClrFileName (platform : HostPolicyPlatform) : string =
        match platform with
        | HostPolicyPlatform.Linux -> "libcoreclr.so"
        | HostPolicyPlatform.MacOS -> "libcoreclr.dylib"

    /// `append_path` (`hostmisc/utils.cpp`).
    let private appendPath (path1 : string) (path2 : string) : string =
        if path2.Length = 0 then
            path1
        elif path1.Length = 0 then
            path2
        elif path1.[path1.Length - 1] <> '/' && path2.[0] <> '/' then
            path1 + "/" + path2
        else
            path1 + path2

    /// `get_filename`.
    let private getFilename (path : string) : string =
        if path.Length = 0 then
            path
        else
            match path.LastIndexOf '/' with
            | -1 -> path
            | namePos -> path.Substring (namePos + 1)

    /// `get_directory`.
    let private getDirectory (path : string) : string =
        let mutable ret = path

        while ret.Length > 0 && ret.[ret.Length - 1] = '/' do
            ret <- ret.Substring (0, ret.Length - 1)

        match ret.LastIndexOf '/' with
        | -1 -> ret + "/"
        | pathSep ->
            let mutable pos = pathSep

            while pos >= 0 && ret.[pos] = '/' do
                pos <- pos - 1

            ret.Substring (0, pos + 1) + "/"

    let private asciiLower (s : string) : string =
        System.String (
            s.ToCharArray ()
            |> Array.map (fun c -> if c >= 'A' && c <= 'Z' then char (int c + 32) else c)
        )

    /// `pal::strcasecmp (a, b) == 0`, in the C locale.
    let private strcaseEqual (a : string) (b : string) : bool = asciiLower a = asciiLower b

    /// `utils::ends_with (str, suffix, false)`.
    let private endsWithIgnoringCase (str : string) (suffix : string) : bool =
        str.Length >= suffix.Length
        && strcaseEqual (str.Substring (str.Length - suffix.Length)) suffix

    /// Whether CoreCLR takes two simple names for one: equal upper-cased, with dotless `ı` read as
    /// `I`, as CoreCLR's Unix fold reads it.
    let private collide (a : string) (b : string) : bool =
        let fold (s : string) : string =
            s.Replace(char 0x131, 'I').ToUpperInvariant ()

        fold a = fold b

    /// `path` with its `.` components and repeated separators dropped: another spelling of the
    /// same file, whatever links it passes.
    let private withoutDots (path : string) : string =
        let mutable p = path
        let mutable previous = ""

        while p <> previous do
            previous <- p
            p <- p.Replace("/./", "/").Replace ("//", "/")

        p

    let private transcribe
        (cover : Label -> unit)
        (input : HostPolicyInput)
        : Result<Map<string, string> * string, HostPolicyRefusal>
        =
        let refuse (refusal : HostPolicyRefusal) : 'a = raise (RefusedException refusal)

        let fail (failure : HostPolicyFailure) : 'a =
            refuse (HostPolicyRefusal.HostWouldFail failure)

        let unreproducible (reason : HostPolicyUnreproducible) : 'a =
            refuse (HostPolicyRefusal.NotReproducible reason)

        try
            // PawPrint: the settings it does not model.
            let settings = input.Settings

            if settings.UseRidGraph then
                unreproducible HostPolicyUnreproducible.UsesRidGraph

            if settings.SetAppPaths then
                unreproducible HostPolicyUnreproducible.SetsAppPaths

            let unmodelled =
                [
                    for p in settings.AdditionalProbingPaths -> HostPolicySetting.AdditionalProbingPath p
                    for p in settings.AdditionalDepsJsons -> HostPolicySetting.AdditionalDepsJson p
                    for p in settings.SharedStores -> HostPolicySetting.SharedStore p
                    for p in Option.toList settings.ServicingDirectory -> HostPolicySetting.ServicingDirectory p
                ]

            if not unmodelled.IsEmpty then
                unreproducible (HostPolicyUnreproducible.UnmodelledSettings unmodelled)

            // `m_fx_definitions`: [0] is the application, then the frameworks, highest level first.
            let appDirectory, rids, frameworks, isFrameworkDependent =
                match input.Launch with
                | HostPolicyLaunch.FrameworkDependent (application, rids, higher, lowest) ->
                    let appDirectory =
                        match application with
                        | HostPolicyApplication.OnDisk d -> Some d
                        | HostPolicyApplication.InMemory -> None

                    appDirectory, rids, Array.ofList (higher @ [ lowest ]), true
                | HostPolicyLaunch.SelfContained d -> Some d, [], [||], false

            let fxCount = 1 + frameworks.Length
            let lowestFramework = fxCount - 1
            let fxDir (i : int) : string = frameworks.[i - 1].Directory

            let manifestOf (i : int) : HostPolicyManifest =
                if i = 0 then
                    HostPolicyManifest.Application
                else
                    HostPolicyManifest.Framework frameworks.[i - 1].Name

            // `m_app_dir`, as `get_directory` of the application's path gives it.
            let appDir =
                match appDirectory with
                | Some d -> d.Directory.TrimEnd '/' + "/"
                | None -> ""

            let listing =
                match appDirectory with
                | Some {
                           Manifest = HostPolicyAppManifest.Listing names
                       } -> Some names
                | _ -> None

            // The deps files: `deps_resolver_t`'s constructor parses them, and `valid` checks them
            // from the application down.
            let mode (i : int) : DepsJsonMode =
                if i <> lowestFramework || (lowestFramework = 0 && isFrameworkDependent) then
                    DepsJsonMode.FrameworkDependent rids
                else
                    DepsJsonMode.SelfContained

            let fxDeps : DepsManifest option[] = Array.create fxCount None

            for i in 0 .. fxCount - 1 do
                let bytes =
                    if i = 0 then
                        match appDirectory with
                        | Some {
                                   Manifest = HostPolicyAppManifest.DepsJson b
                               } -> Some b
                        | _ -> None
                    else
                        match frameworks.[i - 1].DepsJson with
                        | None -> fail (HostPolicyFailure.FrameworkDepsJsonMissing frameworks.[i - 1].Name)
                        | Some b -> Some b

                match bytes with
                | None -> ()
                | Some b ->
                    match DepsJson.parse input.Runtime (mode i) b with
                    | Ok m -> fxDeps.[i] <- Some m
                    | Error (DepsJsonError.UnpairedSurrogate _ as e) ->
                        fail (HostPolicyFailure.DepsJsonRejected (manifestOf i, e))
                    | Error e -> unreproducible (HostPolicyUnreproducible.DepsJsonUnreadable (manifestOf i, e))

            let entriesOf (i : int) (assetType : DepsAssetType) : DepsEntry list =
                match fxDeps.[i] with
                | None -> []
                | Some m -> m.Entries |> List.filter (fun e -> e.AssetType = assetType)

            let isPlaceholder (e : DepsEntry) : bool =
                e.RelativePath.EndsWith ("/_._", StringComparison.Ordinal)

            let managedExtensions =
                match input.Runtime with
                | EmulatedRuntime.Net10 -> [ ".ni.dll" ; ".dll" ; ".ni.exe" ; ".exe" ]
                | EmulatedRuntime.Net11 -> [ ".dll" ; ".exe" ]

            // PawPrint: two files of one extension pass with one stem.
            match listing with
            | None -> ()
            | Some files ->
                for ext in managedExtensions do
                    let firstFor = Dictionary<string, string> StringComparer.Ordinal

                    for file in files do
                        if
                            file.Length > ext.Length
                            && strcaseEqual (file.Substring (file.Length - ext.Length)) ext
                        then
                            let stem = file.Substring (0, file.Length - ext.Length)

                            match firstFor.TryGetValue stem with
                            | true, first ->
                                unreproducible (HostPolicyUnreproducible.ListingNamesCollide (stem, first, file))
                            | false, _ -> firstFor.[stem] <- file

            // PawPrint: two frameworks list one runtime name, or both name the CoreCLR library.
            let coreClrName = coreClrFileName input.Platform

            let namesCoreClr (e : DepsEntry) : bool =
                not (isPlaceholder e)
                && strcaseEqual (getFilename (DepsEntry.dirRelativePath e)) coreClrName

            for i in 1 .. fxCount - 1 do
                let candidates : (string * (int -> bool)) list =
                    [
                        for e in entriesOf i DepsAssetType.Runtime do
                            if not (isPlaceholder e) then
                                yield
                                    e.AssetName,
                                    fun j ->
                                        entriesOf j DepsAssetType.Runtime
                                        |> List.exists (fun f -> not (isPlaceholder f) && f.AssetName = e.AssetName)
                        for e in entriesOf i DepsAssetType.Native do
                            if namesCoreClr e then
                                yield
                                    coreClrName, (fun j -> entriesOf j DepsAssetType.Native |> List.exists namesCoreClr)
                    ]

                for name, listedBy in candidates do
                    match [ i + 1 .. fxCount - 1 ] |> List.tryFind listedBy with
                    | Some j ->
                        unreproducible (
                            HostPolicyUnreproducible.FrameworksOverlap (
                                name,
                                frameworks.[i - 1].Name,
                                frameworks.[j - 1].Name
                            )
                        )
                    | None -> ()

            // `resolve_tpa_list`.
            let items = Dictionary<string, ResolvedAsset> StringComparer.Ordinal

            /// `to_path`, with no file existence check.
            let toPath (baseDir : string) (relativePath : string) : string option =
                if baseDir.Length = 0 then
                    None
                else
                    Some (appendPath baseDir relativePath)

            /// `probe_deps_entry`: the application's probe config, then each framework's, highest
            /// level first.
            let probeDepsEntry (entry : DepsEntry) (depsDir : string) (fxLevel : int) : string option =
                let fromApp =
                    if fxLevel <> 0 then
                        None
                    elif entry.IsRidSpecific && entry.LocalPath.IsNone then
                        let found = toPath depsDir entry.RelativePath

                        if found.IsSome then
                            cover Label.RidSpecificAppEntryAtListedPath

                        found
                    else
                        toPath depsDir (DepsEntry.dirRelativePath entry)

                match fromApp with
                | Some _ -> fromApp
                | None ->
                    [ 1 .. fxCount - 1 ]
                    |> List.tryPick (fun j ->
                        if fxLevel > j then
                            None
                        else
                            match fxDeps.[j] with
                            | Some m when DepsManifest.hasPackage m entry.LibraryName entry.LibraryVersion ->
                                let found = toPath (fxDir j) (DepsEntry.dirRelativePath entry)

                                if found.IsSome && j <> fxLevel then
                                    cover Label.FrameworkEntryInLowerFramework

                                found
                            | _ -> None
                    )

            let missing (entry : DepsEntry) (fxLevel : int) : 'a =
                fail (
                    HostPolicyFailure.AssetNotFound (
                        manifestOf fxLevel,
                        entry.AssetType,
                        entry.LibraryName,
                        entry.LibraryVersion,
                        entry.RelativePath
                    )
                )

            let processEntry (depsDir : string) (entry : DepsEntry) (fxLevel : int) : unit =
                if isPlaceholder entry then
                    ()
                else

                match items.TryGetValue entry.AssetName with
                | false, _ ->
                    match probeDepsEntry entry depsDir fxLevel with
                    | Some resolvedPath ->
                        items.[entry.AssetName] <-
                            {
                                ResolvedPath = resolvedPath
                                AssemblyVersion = entry.AssemblyVersion
                                FileVersion = entry.FileVersion
                                Level = fxLevel
                            }
                    | None -> missing entry fxLevel
                | true, existing ->
                    if getFilename entry.RelativePath <> getFilename existing.ResolvedPath then
                        fail (
                            HostPolicyFailure.DuplicateWithDifferentExtension (
                                manifestOf fxLevel,
                                entry.AssetName,
                                entry.RelativePath,
                                existing.ResolvedPath
                            )
                        )

                    let assembly = DepsVersion.compare entry.AssemblyVersion existing.AssemblyVersion
                    let file = DepsVersion.compare entry.FileVersion existing.FileVersion

                    if assembly > 0 || (assembly = 0 && file >= 0) then
                        match probeDepsEntry entry depsDir fxLevel with
                        | Some resolvedPath ->
                            if resolvedPath <> existing.ResolvedPath then
                                if existing.Level = 0 && fxLevel <> 0 then
                                    cover Label.AppEntryReplacedByFramework

                                if existing.Level = 0 && fxLevel = 0 then
                                    cover Label.AppEntryReplacedByApp

                                if assembly = 0 && file = 0 then
                                    cover Label.TieReplaced

                                items.Remove entry.AssetName |> ignore<bool>

                                items.[entry.AssetName] <-
                                    {
                                        ResolvedPath = resolvedPath
                                        AssemblyVersion = entry.AssemblyVersion
                                        FileVersion = entry.FileVersion
                                        Level = fxLevel
                                    }
                            else
                                cover Label.SamePathKept
                        | None ->
                            if fxLevel <> 0 then
                                missing entry fxLevel
                    elif existing.Level = 0 && fxLevel <> 0 then
                        cover Label.AppEntryKeptAgainstFramework

            match appDirectory with
            | None -> ()
            | Some _ ->
                for entry in entriesOf 0 DepsAssetType.Runtime do
                    processEntry appDir entry 0

                if fxDeps.[0].IsNone then
                    // `get_dir_assemblies`
                    for ext in managedExtensions do
                        for file in Option.defaultValue [] listing do
                            if file.Length <= ext.Length then
                                ()
                            else
                                let fileName = file.Substring (0, file.Length - ext.Length)
                                let fileExt = file.Substring fileName.Length

                                if not (strcaseEqual fileExt ext) then
                                    ()
                                elif items.ContainsKey fileName then
                                    ()
                                else
                                    let filePath =
                                        if appDir.Length > 0 && appDir.[appDir.Length - 1] <> '/' then
                                            appDir + "/" + file
                                        else
                                            appDir + file

                                    cover Label.ListingEntry

                                    items.[fileName] <-
                                        {
                                            ResolvedPath = filePath
                                            AssemblyVersion = DepsVersion.Empty
                                            FileVersion = DepsVersion.Empty
                                            Level = 0
                                        }

            if isFrameworkDependent then
                for i in 1 .. fxCount - 1 do
                    for entry in entriesOf i DepsAssetType.Runtime do
                        processEntry (fxDir i) entry i

            // `resolve_probe_dirs` for native assets, which finds the CoreCLR library.
            let nativeItems = HashSet<string> StringComparer.Ordinal
            // The directories in the order the host adds them, for PawPrint's check below.
            let nativeDirs = List<string> ()
            let coreClrPath = ref ""
            let coreClrLevel = ref -1

            let addPackageCacheEntry (entry : DepsEntry) (depsDir : string) (fxLevel : int) : unit =
                if nativeItems.Contains entry.AssetName then
                    ()
                elif isPlaceholder entry then
                    ()
                else
                    match probeDepsEntry entry depsDir fxLevel with
                    | Some candidate ->
                        // `init_known_entry_path`
                        if
                            coreClrPath.Value.Length = 0
                            && endsWithIgnoringCase candidate ("/" + coreClrName)
                        then
                            coreClrPath.Value <- candidate
                            coreClrLevel.Value <- fxLevel

                        nativeItems.Add (getDirectory candidate) |> ignore<bool>
                        nativeDirs.Add (getDirectory candidate)
                    | None ->
                        if
                            entry.AssetName = "apphost"
                            && endsWithIgnoringCase entry.LibraryName ".Microsoft.NETCore.DotNetAppHost"
                        then
                            ()
                        else
                            missing entry fxLevel

            match appDirectory with
            | None -> ()
            | Some _ ->
                for entry in entriesOf 0 DepsAssetType.Native do
                    addPackageCacheEntry entry appDir 0

                if fxDeps.[0].IsNone then
                    nativeItems.Add appDir |> ignore<bool>
                    nativeDirs.Add appDir

                    // `file_exists_in_dir`. PawPrint refuses a listed name equal to the library's
                    // only ignoring case, not knowing whether the filesystem folds case.
                    if coreClrPath.Value.Length = 0 then
                        let files = Option.defaultValue [] listing

                        if List.contains coreClrName files then
                            coreClrPath.Value <- appendPath appDir coreClrName
                            coreClrLevel.Value <- 0
                        else
                            match files |> List.tryFind (strcaseEqual coreClrName) with
                            | Some listed -> unreproducible (HostPolicyUnreproducible.CoreClrNameCaseOnly listed)
                            | None -> ()

            for i in 1 .. fxCount - 1 do
                for entry in entriesOf i DepsAssetType.Native do
                    addPackageCacheEntry entry (fxDir i) i

            if coreClrPath.Value.Length = 0 then
                fail HostPolicyFailure.CoreClrNotFound

            // PawPrint: the host loads an application's CoreCLR library from wherever its links
            // lead.
            if isFrameworkDependent && coreClrLevel.Value = 0 then
                unreproducible (HostPolicyUnreproducible.CoreClrFromApplication coreClrPath.Value)

            // `resolve_probe_dirs` for resources, which only reports a resource it cannot find.
            let resourceRoots = List<string> ()

            let addResource (entry : DepsEntry) (depsDir : string) (fxLevel : int) : unit =
                if not (isPlaceholder entry) then
                    match probeDepsEntry entry depsDir fxLevel with
                    | Some candidate -> resourceRoots.Add (getDirectory (getDirectory candidate))
                    | None -> ()

            match appDirectory with
            | None -> ()
            | Some _ ->
                for entry in entriesOf 0 DepsAssetType.Resources do
                    addResource entry appDir 0

                if fxDeps.[0].IsNone then
                    resourceRoots.Add appDir

            for i in 1 .. fxCount - 1 do
                for entry in entriesOf i DepsAssetType.Resources do
                    addResource entry (fxDir i) i

            if
                appDirectory.IsSome
                && coreClrPath.Value.StartsWith (appDir, StringComparison.Ordinal)
            then
                cover Label.CoreClrInApp

            // CoreCLR's reading of the TPA string (`GetNextTPAPath`), which PawPrint holds it to.
            let tpa = items |> Seq.map (fun kv -> kv.Key, kv.Value.ResolvedPath) |> Map.ofSeq

            for KeyValue (name, path) in tpa do
                let fileName = getFilename path
                let lowerFileName = asciiLower fileName

                if not (path.StartsWith '/') then
                    fail (HostPolicyFailure.RuntimeRejectsTpa (TpaRefusal.RelativePath (name, path)))
                elif path.Contains (char 0) then
                    unreproducible (HostPolicyUnreproducible.Tpa (TpaRefusal.HoldsNul (name, path)))
                elif path.Contains ':' then
                    unreproducible (HostPolicyUnreproducible.Tpa (TpaRefusal.HoldsPathListSeparator (name, path)))
                elif path.EndsWith ' ' then
                    unreproducible (HostPolicyUnreproducible.Tpa (TpaRefusal.EndsWithSpace (name, path)))
                elif fileName |> Seq.exists (fun c -> int c > 127) then
                    unreproducible (HostPolicyUnreproducible.Tpa (TpaRefusal.NonAsciiFileName (name, path)))
                elif
                    not (lowerFileName.EndsWith (".dll", StringComparison.Ordinal))
                    && not (lowerFileName.EndsWith (".exe", StringComparison.Ordinal))
                then
                    fail (HostPolicyFailure.RuntimeRejectsTpa (TpaRefusal.NoAssemblyExtension (name, path)))
                elif
                    lowerFileName.EndsWith (".exe", StringComparison.Ordinal)
                    || lowerFileName.EndsWith (".ni.dll", StringComparison.Ordinal)
                then
                    unreproducible (HostPolicyUnreproducible.Tpa (TpaRefusal.NotDll (name, path)))
                elif fileName.Substring (0, fileName.Length - 4) <> name then
                    unreproducible (HostPolicyUnreproducible.Tpa (TpaRefusal.NameIsNotFileStem (name, path)))

            let names = tpa |> Map.toList |> List.map fst

            names
            |> List.iteri (fun k name ->
                match names |> List.take k |> List.tryFind (collide name) with
                | Some first ->
                    unreproducible (HostPolicyUnreproducible.Tpa (TpaRefusal.NamesEqualIgnoringCase (first, name)))
                | None -> ()
            )

            // PawPrint: CoreCLR splits its directory lists at `:`.
            match Seq.append nativeDirs resourceRoots |> Seq.tryFind (fun d -> d.Contains ':') with
            | Some directory -> unreproducible (HostPolicyUnreproducible.ProbeDirectoryHoldsPathListSeparator directory)
            | None -> ()

            let findIgnoringCase (wanted : string) : (string * string) option =
                tpa |> Map.toList |> List.tryFind (fun (n, _) -> collide n wanted)

            // PawPrint: CoreCLR loads the CoreLib beside the CoreCLR library.
            match findIgnoringCase "System.Private.CoreLib" with
            | None -> unreproducible HostPolicyUnreproducible.CoreLibAbsent
            | Some (_, path) ->
                let expected =
                    appendPath (getDirectory coreClrPath.Value) "System.Private.CoreLib.dll"

                if path <> expected then
                    unreproducible (HostPolicyUnreproducible.CoreLibNotBesideCoreClr (path, coreClrPath.Value))

            // PawPrint: CoreCLR runs the TPA's file of the entry assembly's name.
            match findIgnoringCase input.EntryAssemblyName with
            | None -> ()
            | Some (name, path) ->
                match appDirectory with
                | Some d when withoutDots path = withoutDots (appendPath appDir d.EntryFileName) -> ()
                | _ -> unreproducible (HostPolicyUnreproducible.EntryAssemblyShadowed (name, path))

            Ok (tpa, coreClrPath.Value)
        with :? RefusedException as e ->
            Error e.Refusal

    let private tpaRefusalName (refusal : TpaRefusal) : string =
        match refusal with
        | TpaRefusal.RelativePath _ -> "RelativePath"
        | TpaRefusal.HoldsNul _ -> "HoldsNul"
        | TpaRefusal.NonAsciiFileName _ -> "NonAsciiFileName"
        | TpaRefusal.HoldsPathListSeparator _ -> "HoldsPathListSeparator"
        | TpaRefusal.EndsWithSpace _ -> "EndsWithSpace"
        | TpaRefusal.NoAssemblyExtension _ -> "NoAssemblyExtension"
        | TpaRefusal.NotDll _ -> "NotDll"
        | TpaRefusal.NameIsNotFileStem _ -> "NameIsNotFileStem"
        | TpaRefusal.NamesEqualIgnoringCase _ -> "NamesEqualIgnoringCase"

    let private outcomeName (result : Result<Map<string, string> * string, HostPolicyRefusal>) : string =
        match result with
        | Ok _ -> "Ok"
        | Error (HostPolicyRefusal.HostWouldFail failure) ->
            match failure with
            | HostPolicyFailure.FrameworkDepsJsonMissing _ -> "FrameworkDepsJsonMissing"
            | HostPolicyFailure.DepsJsonRejected _ -> "DepsJsonRejected"
            | HostPolicyFailure.DuplicateWithDifferentExtension _ -> "DuplicateWithDifferentExtension"
            | HostPolicyFailure.AssetNotFound (_, assetType, _, _, _) -> $"AssetNotFound %A{assetType}"
            | HostPolicyFailure.CoreClrNotFound -> "CoreClrNotFound"
            | HostPolicyFailure.RuntimeRejectsTpa r -> "RuntimeRejectsTpa " + tpaRefusalName r
        | Error (HostPolicyRefusal.NotReproducible reason) ->
            match reason with
            | HostPolicyUnreproducible.UsesRidGraph -> "UsesRidGraph"
            | HostPolicyUnreproducible.SetsAppPaths -> "SetsAppPaths"
            | HostPolicyUnreproducible.CoreClrFromApplication _ -> "CoreClrFromApplication"
            | HostPolicyUnreproducible.CoreClrNameCaseOnly _ -> "CoreClrNameCaseOnly"
            | HostPolicyUnreproducible.ProbeDirectoryHoldsPathListSeparator _ -> "ProbeDirectoryHoldsPathListSeparator"
            | HostPolicyUnreproducible.UnmodelledSettings _ -> "UnmodelledSettings"
            | HostPolicyUnreproducible.DepsJsonUnreadable _ -> "DepsJsonUnreadable"
            | HostPolicyUnreproducible.ListingNamesCollide _ -> "ListingNamesCollide"
            | HostPolicyUnreproducible.FrameworksOverlap _ -> "FrameworksOverlap"
            | HostPolicyUnreproducible.Tpa r -> "Tpa " + tpaRefusalName r
            | HostPolicyUnreproducible.CoreLibAbsent -> "CoreLibAbsent"
            | HostPolicyUnreproducible.CoreLibNotBesideCoreClr _ -> "CoreLibNotBesideCoreClr"
            | HostPolicyUnreproducible.EntryAssemblyShadowed _ -> "EntryAssemblyShadowed"

    // ---------------------------------------------------------------------------------------
    // The generated inputs.

    [<RequireQualifiedAccess>]
    type private AppModel =
        | Deps of ManifestSpec
        | Listing of string list
        | InMemory

    [<RequireQualifiedAccess>]
    type private FrameworkDeps =
        | Manifest of ManifestSpec
        | Missing
        | Bytes of byte[]

    type private FrameworkModel =
        {
            Name : string
            Directory : string
            Deps : FrameworkDeps
        }

    /// A generated input. A self-contained case has no frameworks and an application on disk; a
    /// framework-dependent one has at least one framework.
    type private Case =
        {
            Runtime : EmulatedRuntime
            Platform : HostPolicyPlatform
            SelfContained : bool
            AppDirectory : string
            App : AppModel
            /// In host order, highest first.
            Frameworks : FrameworkModel list
            Rids : string list
            EntryAssemblyName : string
            Settings : HostPolicySettings
        }

    let private noSettings : HostPolicySettings =
        {
            UseRidGraph = false
            SetAppPaths = false
            AdditionalProbingPaths = []
            AdditionalDepsJsons = []
            SharedStores = []
            ServicingDirectory = None
        }

    let private toInput (case : Case) : HostPolicyInput =
        let appDirectory (manifest : HostPolicyAppManifest) : HostPolicyAppDirectory =
            {
                Directory = case.AppDirectory
                Manifest = manifest
                EntryFileName = "Entry.dll"
            }

        let onDisk =
            match case.App with
            | AppModel.Deps spec -> Some (appDirectory (HostPolicyAppManifest.DepsJson (ManifestSpec.render spec)))
            | AppModel.Listing names -> Some (appDirectory (HostPolicyAppManifest.Listing names))
            | AppModel.InMemory -> None

        let framework (f : FrameworkModel) : HostPolicyFramework =
            {
                Name = f.Name
                Directory = f.Directory
                DepsJson =
                    match f.Deps with
                    | FrameworkDeps.Manifest spec -> Some (ManifestSpec.render spec)
                    | FrameworkDeps.Missing -> None
                    | FrameworkDeps.Bytes b -> Some b
            }

        let launch =
            if case.SelfContained then
                match onDisk with
                | Some d -> HostPolicyLaunch.SelfContained d
                | None -> failwith "logic error: a self-contained case held in memory"
            else
                let application =
                    match onDisk with
                    | Some d -> HostPolicyApplication.OnDisk d
                    | None -> HostPolicyApplication.InMemory

                let frameworks = case.Frameworks |> List.map framework
                let higher = List.take (frameworks.Length - 1) frameworks
                HostPolicyLaunch.FrameworkDependent (application, case.Rids, higher, List.last frameworks)

        {
            Runtime = case.Runtime
            Platform = case.Platform
            Launch = launch
            EntryAssemblyName = case.EntryAssemblyName
            Settings = case.Settings
        }

    /// A `deps.json` whose `runtimeTarget` escapes half a surrogate pair, which the host's parser
    /// refuses.
    let private unpairedSurrogateManifest : byte[] =
        Text.Encoding.UTF8.GetBytes "{\"runtimeTarget\":\"\\ud800\"}"

    let private namePool : string list =
        [
            "A"
            "B"
            "a"
            "C.D"
            "System.Private.CoreLib"
            "Entry"
            "entry"
            "Z" + string (char 0x131)
        ]

    let private versionGen : Gen<string option> =
        Gen.elements
            [
                None
                Some "1.0.0.0"
                Some "1.0.0.1"
                Some "1.0.1.0"
                Some "2.0.0.0"
                Some "1.0"
                Some "x"
                Some "0.9"
            ]

    let private localPathGen (noneWeight : int) (file : string) : Gen<string option> =
        Gen.frequency
            [
                noneWeight, Gen.constant None
                2, Gen.constant (Some ("sub/" + file))
                1, Gen.constant (Some ("./" + file))
                1, Gen.constant (Some ("../up/" + file))
                1, Gen.constant (Some "sub/Other.dll")
                1, Gen.constant (Some "")
                1, Gen.constant (Some ("/abs/" + file))
                1, Gen.constant (Some (file + " "))
            ]

    let private runtimeAssetGen (names : string list) (noLocalPathWeight : int) : Gen<ManifestAsset> =
        gen {
            let! placeholder = Gen.choose (0, 24)

            if placeholder = 0 then
                return ManifestSpec.asset "lib/net10.0/_._"
            else
                let! name = Gen.elements names
                let! prefix = Gen.elements [ "" ; "lib/net10.0/" ; "sub/" ]

                let! ext =
                    Gen.frequency
                        [
                            20, Gen.constant ".dll"
                            1, Gen.constant ".DLL"
                            1, Gen.constant ".exe"
                            1, Gen.constant ".ni.dll"
                            1, Gen.constant ".txt"
                        ]

                let file = name + ext
                let! assemblyVersion = versionGen
                let! fileVersion = versionGen
                let! localPath = localPathGen noLocalPathWeight file

                return
                    {
                        Path = prefix + file
                        AssemblyVersion = assemblyVersion
                        FileVersion = fileVersion
                        LocalPath = localPath
                    }
        }

    let private nativePool : string list =
        [
            "libfoo.dylib"
            "libcoreclr.dylib"
            "libcoreclr.so"
            "native/libcoreclr.so"
            "LibCoreClr.dylib"
            "lib/_._"
        ]

    let private nativeAssetGen (paths : string list) : Gen<ManifestAsset> =
        gen {
            let! path = Gen.elements paths

            let! localPath =
                Gen.frequency
                    [
                        18, Gen.constant None
                        2, Gen.constant (Some ("sub/" + path))
                        1, Gen.constant (Some ("c:native/" + path))
                    ]

            return
                { ManifestSpec.asset path with
                    LocalPath = localPath
                }
        }

    let private ridAssetGen (names : string list) (rids : string list) : Gen<ManifestRidAsset> =
        gen {
            let! rid = Gen.elements (rids @ [ "win-x64" ; "OSX-ARM64" ; "linux-musl-x64" ])
            let! native = Gen.frequency [ 4, Gen.constant false ; 1, Gen.constant true ]
            let! name = Gen.elements names
            let! assemblyVersion = versionGen
            let! fileVersion = versionGen

            let! localPath = Gen.frequency [ 8, Gen.constant None ; 1, Gen.constant (Some ("rid/" + name + ".dll")) ]

            let path =
                if native then
                    $"runtimes/%s{rid}/native/lib%s{name}.so"
                else
                    $"runtimes/%s{rid}/lib/net10.0/%s{name}.dll"

            return
                {
                    Asset =
                        {
                            Path = path
                            AssemblyVersion = assemblyVersion
                            FileVersion = fileVersion
                            LocalPath = localPath
                        }
                    Rid = rid
                    AssetType = if native then "native" else "runtime"
                }
        }

    let private libraryGen
        (names : string list)
        (nativePaths : string list)
        (rids : string list)
        (noLocalPathWeight : int)
        (key : string)
        : Gen<ManifestLibrary>
        =
        gen {
            let! libraryType =
                Gen.frequency
                    [
                        6, Gen.constant "package"
                        1, Gen.constant "project"
                        1, Gen.constant "runtimepack"
                    ]

            let! runtimeCount = Gen.choose (0, 3)
            let! runtime = Gen.listOfLength runtimeCount (runtimeAssetGen names noLocalPathWeight)
            let! nativeCount = Gen.frequency [ 6, Gen.constant 0 ; 2, Gen.constant 1 ; 1, Gen.constant 2 ]
            let! native = Gen.listOfLength nativeCount (nativeAssetGen nativePaths)
            let! resourceCount = Gen.frequency [ 6, Gen.constant 0 ; 2, Gen.constant 1 ]

            let resourceGen : Gen<ManifestAsset> =
                gen {
                    let! name = Gen.elements names
                    let! culture = Gen.elements [ "en" ; "fr" ]

                    let! localPath =
                        Gen.frequency
                            [
                                8, Gen.constant None
                                1, Gen.constant (Some $"d:res/%s{culture}/%s{name}.resources.dll")
                            ]

                    return
                        { ManifestSpec.asset $"lib/net10.0/%s{culture}/%s{name}.resources.dll" with
                            LocalPath = localPath
                        }
                }

            let! resources = Gen.listOfLength resourceCount resourceGen
            let! ridCount = Gen.frequency [ 6, Gen.constant 0 ; 2, Gen.constant 1 ; 1, Gen.constant 2 ]
            let! ridSpecific = Gen.listOfLength ridCount (ridAssetGen names rids)
            let! inLibraries = Gen.frequency [ 19, Gen.constant true ; 1, Gen.constant false ]

            return
                {
                    Key = key
                    Type = libraryType
                    Runtime = runtime |> List.distinctBy (fun a -> a.Path)
                    Native = native |> List.distinctBy (fun a -> a.Path)
                    Resources = resources |> List.distinctBy (fun a -> a.Path)
                    RidSpecific = ridSpecific |> List.distinctBy (fun r -> r.Asset.Path)
                    InLibraries = inLibraries
                }
        }

    /// The library which holds a runtime's CoreLib and CoreCLR library.
    /// As `coreLibrary`, with the CoreCLR library's `localPath` under `nativeDirectory`.
    let private coreLibraryIn (nativeDirectory : string) (platform : HostPolicyPlatform) : ManifestLibrary =
        { ManifestSpec.library
              "Core/1.0"
              [
                  { ManifestSpec.asset "System.Private.CoreLib.dll" with
                      AssemblyVersion = Some "9.0.0.0"
                  }
              ] with
            Native =
                [
                    { ManifestSpec.asset (coreClrFileName platform) with
                        LocalPath =
                            if nativeDirectory = "" then
                                None
                            else
                                Some (nativeDirectory + coreClrFileName platform)
                    }
                ]
        }

    let private coreLibrary (platform : HostPolicyPlatform) : ManifestLibrary = coreLibraryIn "" platform

    let private entryLibrary : ManifestLibrary =
        { ManifestSpec.library "Entry/1.0.0" [ ManifestSpec.asset "Entry.dll" ] with
            Type = "project"
        }

    /// A manifest beginning with `opening`, then up to four libraries keyed by `keyGen`.
    let private manifestGen
        (names : string list)
        (nativePaths : string list)
        (rids : string list)
        (noLocalPathWeight : int)
        (keyGen : int -> Gen<string>)
        (opening : ManifestLibrary list)
        : Gen<ManifestSpec>
        =
        gen {
            let! count = Gen.choose (0, 4)

            let! libraries =
                [ 1..count ]
                |> List.map (fun j -> keyGen j |> Gen.bind (libraryGen names nativePaths rids noLocalPathWeight))
                |> Gen.sequenceToList

            return
                {
                    Libraries = opening @ libraries |> List.distinctBy (fun l -> l.Key)
                }
        }

    /// How a generated case is shaped.
    type private CaseShape =
        {
            /// Whether a framework library's key may lack a `/`, so that its own manifest does not
            /// have its package.
            AllowSlashlessKeys : bool
            MinFrameworks : int
            /// Whether each framework's names, and the native libraries of those above the lowest,
            /// are its own, so that no two frameworks list one name.
            SuffixFrameworkNames : bool
            /// Which application cases, by weight: framework-dependent with a `deps.json`, with a
            /// listing, in memory; self-contained with a `deps.json`, with a listing.
            AppWeights : int * int * int * int * int
        }

    let private anyShape : CaseShape =
        {
            AllowSlashlessKeys = true
            MinFrameworks = 1
            SuffixFrameworkNames = false
            AppWeights = (40, 25, 15, 12, 8)
        }

    let private frameworkGen
        (shape : CaseShape)
        (platform : HostPolicyPlatform)
        (rids : string list)
        (noLocalPathWeight : int)
        (withCore : bool)
        (coreClrDirectory : string)
        (k : int)
        (isLowest : bool)
        : Gen<FrameworkModel>
        =
        gen {
            let! directory =
                Gen.frequency
                    [
                        90, Gen.constant $"/fx/F%d{k}"
                        5, Gen.constant $"/fx/F%d{k}/"
                        3, Gen.constant $"/c:fx/F%d{k}"
                    ]

            let keyGen (j : int) : Gen<string> =
                Gen.frequency
                    [
                        yield 10, Gen.constant $"L%d{k}_%d{j}/1.0"
                        yield 1, Gen.constant "S/1.0"
                        if shape.AllowSlashlessKeys then
                            yield 1, Gen.constant $"P%d{j}"
                            // A higher framework's entry from `Q` resolves into a lower framework
                            // which has the package `Q/Q`.
                            yield 4, Gen.constant "Q"
                            yield 4, Gen.constant "Q/Q"
                    ]

            // Often enough to count: a higher framework's library `Q`, whose package its own manifest
            // lacks, and the lowest framework's `Q/Q`, so that the entry resolves there.
            let! withQ =
                Gen.frequency
                    [
                        3, Gen.constant false
                        (if shape.AllowSlashlessKeys then 1 else 0), Gen.constant true
                    ]

            let opening =
                [
                    if isLowest && withCore then
                        yield coreLibraryIn coreClrDirectory platform
                    if withQ then
                        if isLowest then
                            yield ManifestSpec.library "Q/Q" [ ManifestSpec.asset "QBase.dll" ]
                        else
                            yield ManifestSpec.library "Q" [ ManifestSpec.asset $"Q%d{k}.dll" ]
                ]

            let names, nativePaths =
                if shape.SuffixFrameworkNames then
                    namePool |> List.map (fun n -> $"%s{n}%d{k}"),
                    (if isLowest then nativePool else [ $"libfoo%d{k}.dylib" ])
                else
                    namePool, nativePool

            let! spec = manifestGen names nativePaths rids noLocalPathWeight keyGen opening

            let! deps =
                Gen.frequency
                    [
                        97, Gen.constant (FrameworkDeps.Manifest spec)
                        1, Gen.constant FrameworkDeps.Missing
                        1, Gen.constant (FrameworkDeps.Bytes "{"B)
                        1, Gen.constant (FrameworkDeps.Bytes unpairedSurrogateManifest)
                    ]

            return
                {
                    Name = $"F%d{k}"
                    Directory = directory
                    Deps = deps
                }
        }

    let private listingGen
        (names : string list)
        (platform : HostPolicyPlatform)
        (withEntry : bool)
        (withCore : bool)
        : Gen<string list>
        =
        gen {
            let! count = Gen.choose (0, 5)

            let fileGen =
                gen {
                    let! name = Gen.elements names

                    let! ext =
                        Gen.frequency
                            [
                                10, Gen.constant ".dll"
                                2, Gen.constant ".DLL"
                                1, Gen.constant ".ni.dll"
                                1, Gen.constant ".exe"
                                1, Gen.constant ".ni.exe"
                                1, Gen.constant ".txt"
                                1, Gen.constant ""
                            ]

                    return name + ext
                }

            let! files = Gen.listOfLength count fileGen

            let! coreClr =
                Gen.frequency
                    [
                        18, Gen.constant []
                        1, Gen.constant [ coreClrFileName platform ]
                        1, Gen.constant [ (coreClrFileName platform).ToUpperInvariant () ]
                    ]

            let core =
                if withCore then
                    [ "System.Private.CoreLib.dll" ; coreClrFileName platform ]
                else
                    []

            let entry = if withEntry then [ "Entry.dll" ] else []
            return entry @ core @ coreClr @ files |> List.distinct
        }

    let private caseGenOf (shape : CaseShape) : Gen<Case> =
        gen {
            let! runtime = Gen.elements [ EmulatedRuntime.Net10 ; EmulatedRuntime.Net11 ]
            let! platform = Gen.elements [ HostPolicyPlatform.Linux ; HostPolicyPlatform.MacOS ]

            let rids =
                match platform with
                | HostPolicyPlatform.MacOS -> [ "osx-arm64" ; "osx" ; "unix-arm64" ; "unix" ; "any" ]
                | HostPolicyPlatform.Linux -> [ "linux-x64" ; "linux" ; "unix-x64" ; "unix" ; "any" ]

            // The weight of "no `localPath`", fuzzed so that some cases have many.
            let! noLocalPathWeight = Gen.elements [ 4 ; 12 ; 40 ]
            let fdDeps, fdListing, fdMemory, scDeps, scListing = shape.AppWeights

            let! appKind =
                Gen.frequency
                    [
                        fdDeps, Gen.constant 0
                        fdListing, Gen.constant 1
                        fdMemory, Gen.constant 2
                        scDeps, Gen.constant 3
                        scListing, Gen.constant 4
                    ]

            let selfContained = appKind >= 3

            let! appDirectory =
                Gen.frequency
                    [
                        80, Gen.constant "/app"
                        10, Gen.constant "/app/"
                        5, Gen.constant "/a b/app"
                        2, Gen.constant "/app:x"
                        2, Gen.constant "app"
                        1, Gen.constant "/app//"
                    ]

            let! withCore = Gen.frequency [ 85, Gen.constant true ; 15, Gen.constant false ]
            // Sometimes the CoreCLR library is away from CoreLib.
            let! coreClrDirectory = Gen.frequency [ 15, Gen.constant "" ; 1, Gen.constant "native/" ]
            let! withEntry = Gen.frequency [ 75, Gen.constant true ; 25, Gen.constant false ]

            let! frameworkCount =
                if selfContained then
                    Gen.constant 0
                else
                    Gen.frequency [ 5, Gen.constant 1 ; 3, Gen.constant 2 ; 2, Gen.constant 3 ]
                    |> Gen.map (max shape.MinFrameworks)

            let! frameworks =
                [ 1..frameworkCount ]
                |> List.map (fun k ->
                    frameworkGen shape platform rids noLocalPathWeight withCore coreClrDirectory k (k = frameworkCount)
                )
                |> Gen.sequenceToList

            let appKeyGen (j : int) : Gen<string> =
                Gen.frequency [ 4, Gen.constant $"App%d{j}/1.0" ; 1, Gen.constant "S/1.0" ]

            let appNames =
                if shape.SuffixFrameworkNames then
                    namePool
                    @ [
                        for k in 1..3 do
                            for n in namePool -> $"%s{n}%d{k}"
                    ]
                else
                    namePool

            let! app =
                match appKind with
                | 0
                | 3 ->
                    let opening =
                        [
                            if withEntry then
                                yield entryLibrary
                            if selfContained && withCore then
                                yield coreLibraryIn coreClrDirectory platform
                        ]

                    manifestGen appNames nativePool rids noLocalPathWeight appKeyGen opening
                    |> Gen.map AppModel.Deps
                | 1
                | 4 ->
                    listingGen appNames platform withEntry (selfContained && withCore)
                    |> Gen.map AppModel.Listing
                | _ -> Gen.constant AppModel.InMemory

            let! settings =
                Gen.frequency
                    [
                        94, Gen.constant noSettings
                        1,
                        Gen.constant
                            { noSettings with
                                UseRidGraph = true
                            }
                        1,
                        Gen.constant
                            { noSettings with
                                SetAppPaths = true
                            }
                        1,
                        Gen.constant
                            { noSettings with
                                AdditionalProbingPaths = [ "/probe" ]
                            }
                        1,
                        Gen.constant
                            { noSettings with
                                AdditionalDepsJsons = [ "/extra.deps.json" ]
                            }
                        1,
                        Gen.constant
                            { noSettings with
                                SharedStores = [ "/store/arm64/net10.0" ]
                            }
                        1,
                        Gen.constant
                            { noSettings with
                                ServicingDirectory = Some "/opt/coreservicing"
                            }
                    ]

            let! entryAssemblyName = Gen.frequency [ 9, Gen.constant "Entry" ; 1, Gen.constant "A" ]

            return
                {
                    Runtime = runtime
                    Platform = platform
                    SelfContained = selfContained
                    AppDirectory = appDirectory
                    App = app
                    Frameworks = frameworks
                    Rids = rids
                    EntryAssemblyName = entryAssemblyName
                    Settings = settings
                }
        }

    let private resolve (case : Case) : Result<Map<string, string>, HostPolicyRefusal> =
        HostPolicy.resolveTpa (toInput case)
        |> Result.map (fun r -> TrustedPlatformAssemblies.toMap r.Tpa)

    // ---------------------------------------------------------------------------------------
    // Against the transcription.

    let private requiredOutcomes : string list =
        [
            "Ok"
            "FrameworkDepsJsonMissing"
            "DepsJsonRejected"
            "DuplicateWithDifferentExtension"
            "AssetNotFound Runtime"
            "AssetNotFound Native"
            "CoreClrNotFound"
            "RuntimeRejectsTpa RelativePath"
            "RuntimeRejectsTpa NoAssemblyExtension"
            "UsesRidGraph"
            "SetsAppPaths"
            "CoreClrFromApplication"
            "CoreClrNameCaseOnly"
            "ProbeDirectoryHoldsPathListSeparator"
            "Tpa NonAsciiFileName"
            "UnmodelledSettings"
            "DepsJsonUnreadable"
            "ListingNamesCollide"
            "FrameworksOverlap"
            "Tpa HoldsPathListSeparator"
            "Tpa EndsWithSpace"
            "Tpa NotDll"
            "Tpa NameIsNotFileStem"
            "Tpa NamesEqualIgnoringCase"
            "CoreLibAbsent"
            "CoreLibNotBesideCoreClr"
            "EntryAssemblyShadowed"
        ]

    [<Test>]
    let ``resolveTpa agrees with a transcription of resolve_tpa_list`` () =
        let property (cover : Label -> unit) (case : Case) : unit =
            let input = toInput case
            let expected = transcribe cover input
            cover (Label.Outcome (outcomeName expected))

            HostPolicy.resolveTpa input
            |> Result.map (fun r -> TrustedPlatformAssemblies.toMap r.Tpa, r.CoreClr)
            |> shouldBe expected

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 10000)
                (Arb.fromGen (caseGenOf anyShape))
                property

        for outcome in requiredOutcomes do
            if coverage.Count (Label.Outcome outcome) < 10 then
                Assert.Fail
                    $"The fixed sample reached the outcome %s{outcome} %d{coverage.Count (Label.Outcome outcome)} times"

        coverage.Count (Label.Outcome "Ok") |> shouldBeGreaterThan 1000

        for label in
            [
                Label.AppEntryReplacedByFramework
                Label.AppEntryReplacedByApp
                Label.TieReplaced
                Label.AppEntryKeptAgainstFramework
                Label.SamePathKept
                Label.FrameworkEntryInLowerFramework
                Label.RidSpecificAppEntryAtListedPath
                Label.ListingEntry
                Label.CoreClrInApp
            ] do
            if coverage.Count label < 50 then
                Assert.Fail $"The fixed sample reached %A{label} %d{coverage.Count label} times"

    // ---------------------------------------------------------------------------------------
    // Metamorphic properties.

    let private isOk (result : Result<'a, 'b>) : bool =
        match result with
        | Ok _ -> true
        | Error _ -> false

    /// Checks `property` over 10 000 cases of `gen`, and that at least `minOk` of the fixed
    /// sample's cases reached a TPA.
    let private checkMetamorphic<'a> (minOk : int) (gen : Gen<'a>) (property : 'a -> bool) : unit =
        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 10000)
                (Arb.fromGen gen)
                (fun (cover : bool -> unit) (x : 'a) -> cover (property x))

        if coverage.Count true < minOk then
            Assert.Fail $"Only %d{coverage.Count true} cases of the fixed sample reached a TPA"

    /// The frameworks above the lowest in another order, given by `keys`.
    let private permuteHigher (keys : int list) (case : Case) : Case =
        let higher = List.take (case.Frameworks.Length - 1) case.Frameworks
        let permuted = List.zip keys higher |> List.sortBy fst |> List.map snd

        { case with
            Frameworks = permuted @ [ List.last case.Frameworks ]
        }

    [<Test>]
    let ``Permuting the frameworks above the lowest leaves the TPA unchanged`` () =
        let shape =
            { anyShape with
                AllowSlashlessKeys = false
                MinFrameworks = 3
                SuffixFrameworkNames = true
                AppWeights = (40, 25, 15, 0, 0)
            }

        let gen =
            gen {
                let! case = caseGenOf shape
                let! keys = Gen.listOfLength (case.Frameworks.Length - 1) (Gen.choose (0, 1000))
                return case, keys
            }

        checkMetamorphic
            200
            gen
            (fun (case, keys) ->
                let original = resolve case
                let permuted = resolve (permuteHigher keys case)

                match original with
                | Ok _ -> permuted |> shouldBe original
                | Error _ ->
                    if isOk permuted then
                        Assert.Fail $"Permuting the higher frameworks made %A{original} into %A{permuted}"

                isOk original
            )

    [<Test>]
    let ``An application entry stated above every framework's version wins its name`` () =
        let shape =
            { anyShape with
                AppWeights = (1, 0, 0, 0, 0)
            }

        let gen =
            gen {
                let! case = caseGenOf shape

                let candidates =
                    [
                        for f in case.Frameworks do
                            match f.Deps with
                            | FrameworkDeps.Manifest spec ->
                                for library in spec.Libraries do
                                    for asset in library.Runtime do
                                        let file = asset.Path.Substring (asset.Path.LastIndexOf '/' + 1)

                                        if file.EndsWith (".dll", StringComparison.Ordinal) then
                                            let name = file.Substring (0, file.Length - 4)

                                            if name <> "System.Private.CoreLib" then
                                                yield name
                            | FrameworkDeps.Missing
                            | FrameworkDeps.Bytes _ -> ()
                    ]
                    |> List.distinct

                let! name =
                    if candidates.IsEmpty then
                        Gen.constant None
                    else
                        Gen.elements candidates |> Gen.map Some

                return case, name
            }

        checkMetamorphic
            200
            gen
            (fun (case, name) ->
                match name, case.App with
                | Some name, AppModel.Deps spec ->
                    let raised =
                        ManifestSpec.library
                            "Raised/1.0"
                            [
                                { ManifestSpec.asset (name + ".dll") with
                                    AssemblyVersion = Some "999.0.0.0"
                                    FileVersion = Some "1.0.0.0"
                                }
                            ]

                    let case =
                        { case with
                            App =
                                AppModel.Deps
                                    {
                                        Libraries = raised :: spec.Libraries
                                    }
                        }

                    match resolve case with
                    | Ok tpa ->
                        Map.tryFind name tpa
                        |> shouldBe (Some (case.AppDirectory.TrimEnd '/' + "/" + name + ".dll"))

                        true
                    | Error _ -> false
                | _ -> false
            )

    [<Test>]
    let ``With no deps.json, each framework's names resolve into that framework's directory`` () =
        let shape =
            { anyShape with
                AllowSlashlessKeys = false
                AppWeights = (0, 1, 0, 0, 0)
            }

        checkMetamorphic
            200
            (caseGenOf shape)
            (fun case ->
                match resolve case with
                | Error _ -> false
                | Ok tpa ->
                    let input = toInput case

                    let frameworks =
                        match input.Launch with
                        | HostPolicyLaunch.FrameworkDependent (_, rids, higher, lowest) ->
                            let all = higher @ [ lowest ]

                            all
                            |> List.mapi (fun i f ->
                                let mode =
                                    if i = all.Length - 1 then
                                        DepsJsonMode.SelfContained
                                    else
                                        DepsJsonMode.FrameworkDependent rids

                                match f.DepsJson with
                                | Some bytes ->
                                    match DepsJson.parse case.Runtime mode bytes with
                                    | Ok m -> f.Directory, m.Entries
                                    | Error e -> failwith $"logic error: a TPA from an unreadable manifest: %A{e}"
                                | None -> failwith "logic error: a TPA with a framework's deps.json missing"
                            )
                        | HostPolicyLaunch.SelfContained _ -> failwith "logic error: a self-contained case"

                    for directory, entries in frameworks do
                        for entry in entries do
                            if entry.AssetType = DepsAssetType.Runtime && not (DepsEntry.isPlaceholder entry) then
                                let path = Map.find entry.AssetName tpa

                                if not (path.StartsWith (directory, StringComparison.Ordinal)) then
                                    Assert.Fail $"%s{entry.AssetName} resolved to %s{path}, outside %s{directory}"

                    true
            )

    [<Test>]
    let ``Every path of the TPA lies in the directory of a manifest`` () =
        checkMetamorphic
            1000
            (caseGenOf anyShape)
            (fun case ->
                match resolve case with
                | Error _ -> false
                | Ok tpa ->
                    let directories =
                        [
                            match case.App with
                            | AppModel.InMemory -> ()
                            | AppModel.Deps _
                            | AppModel.Listing _ -> yield case.AppDirectory.TrimEnd '/' + "/"
                            for f in case.Frameworks -> f.Directory
                        ]

                    for KeyValue (name, path) in tpa do
                        if
                            not (
                                directories
                                |> List.exists (fun d -> path.StartsWith (d, StringComparison.Ordinal))
                            )
                        then
                            Assert.Fail $"%s{name} resolved to %s{path}, outside every manifest's directory"

                    true
            )

    [<Test>]
    let ``The TPA's names are distinct ignoring case`` () =
        checkMetamorphic
            1000
            (caseGenOf anyShape)
            (fun case ->
                match resolve case with
                | Error _ -> false
                | Ok tpa ->
                    let names = tpa |> Map.toList |> List.map fst

                    for a in names do
                        for b in names do
                            if a <> b && collide a b then
                                Assert.Fail $"The TPA holds both %s{a} and %s{b}"

                    true
            )

    [<Test>]
    let ``Permuting the directory listing leaves the TPA unchanged`` () =
        let shape =
            { anyShape with
                AppWeights = (0, 3, 0, 0, 1)
            }

        let gen =
            gen {
                let! case = caseGenOf shape

                match case.App with
                | AppModel.Listing names ->
                    let! shuffled = Gen.shuffle names
                    return case, List.ofArray shuffled
                | AppModel.Deps _
                | AppModel.InMemory -> return failwith "logic error: a listing case without a listing"
            }

        checkMetamorphic
            200
            gen
            (fun (case, shuffled) ->
                let original = resolve case

                let permuted =
                    resolve
                        { case with
                            App = AppModel.Listing shuffled
                        }

                match original with
                | Ok _ -> permuted |> shouldBe original
                | Error _ ->
                    if isOk permuted then
                        Assert.Fail $"Permuting the listing made %A{original} into %A{permuted}"

                isOk original
            )

    // ---------------------------------------------------------------------------------------
    // Examples.

    let private abstractions : string =
        "Microsoft.Extensions.DependencyInjection.Abstractions"

    /// A `Microsoft.NETCore.App` manifest holding CoreLib, `extra`, and the CoreCLR library.
    let private netCoreApp (version : string) (extra : ManifestAsset list) : ManifestSpec =
        {
            Libraries =
                [
                    { ManifestSpec.library
                          $"Microsoft.NETCore.App.Runtime.osx-arm64/%s{version}"
                          ({ ManifestSpec.asset "System.Private.CoreLib.dll" with
                              AssemblyVersion = Some (version.Substring (0, 2) + ".0.0.0")
                              FileVersion = Some "1.0.0.0"
                           }
                           :: extra) with
                        Native = [ ManifestSpec.asset "libcoreclr.dylib" ]
                    }
                ]
        }

    let private framework (name : string) (directory : string) (spec : ManifestSpec) : HostPolicyFramework =
        {
            Name = name
            Directory = directory
            DepsJson = Some (ManifestSpec.render spec)
        }

    let private macRids : string list =
        [ "osx-arm64" ; "osx" ; "unix-arm64" ; "unix" ; "any" ]

    let private onDisk (manifest : HostPolicyAppManifest) : HostPolicyApplication =
        HostPolicyApplication.OnDisk
            {
                Directory = "/app"
                Manifest = manifest
                EntryFileName = "Entry.dll"
            }

    let private frameworkDependent
        (runtime : EmulatedRuntime)
        (application : HostPolicyApplication)
        (higher : HostPolicyFramework list)
        (lowest : HostPolicyFramework)
        : HostPolicyInput
        =
        {
            Runtime = runtime
            Platform = HostPolicyPlatform.MacOS
            Launch = HostPolicyLaunch.FrameworkDependent (application, macRids, higher, lowest)
            EntryAssemblyName = "Entry"
            Settings = noSettings
        }

    let private appWith (libraries : ManifestLibrary list) : HostPolicyApplication =
        onDisk (
            HostPolicyAppManifest.DepsJson (
                ManifestSpec.render
                    {
                        Libraries = entryLibrary :: libraries
                    }
            )
        )

    let private versioned
        (path : string)
        (assemblyVersion : string option)
        (fileVersion : string option)
        : ManifestAsset
        =
        { ManifestSpec.asset path with
            AssemblyVersion = assemblyVersion
            FileVersion = fileVersion
        }

    /// The plan's "Measured host TPAs" table: where the TPA puts the abstractions assembly, by
    /// variant and runtime. A deleted file changes nothing the function sees, which is the table's
    /// point: the host does not look.
    [<TestCase("as is", "Net10", "app")>]
    [<TestCase("as is", "Net11", "framework")>]
    [<TestCase("no deps.json", "Net10", "app")>]
    [<TestCase("no deps.json", "Net11", "framework")>]
    [<TestCase("file deleted", "Net10", "app")>]
    [<TestCase("file deleted", "Net11", "framework")>]
    [<TestCase("tie", "Net10", "app")>]
    [<TestCase("tie", "Net11", "framework")>]
    [<TestCase("file version + 1", "Net10", "app")>]
    [<TestCase("file version + 1", "Net11", "app")>]
    [<TestCase("deps says 12", "Net10", "app")>]
    [<TestCase("deps says 12", "Net11", "app")>]
    [<TestCase("deps says 12, file deleted", "Net10", "app")>]
    [<TestCase("deps says 12, file deleted", "Net11", "app")>]
    [<TestCase("no versions", "Net10", "app")>]
    [<TestCase("no versions", "Net11", "framework")>]
    let ``The measured table`` (variant : string, runtime : string, expected : string) =
        let runtime, fxVersion, fxExtra =
            match runtime with
            | "Net10" -> EmulatedRuntime.Net10, "10.0.14", []
            | "Net11" ->
                EmulatedRuntime.Net11,
                "11.0.0-rc.3.26522.148",
                [ versioned (abstractions + ".dll") (Some "11.0.0.0") (Some "11.0.26.52348") ]
            | other -> failwith $"unknown runtime %s{other}"

        let appDeps (assemblyVersion : string option) (fileVersion : string option) : HostPolicyApplication =
            appWith
                [
                    ManifestSpec.library
                        $"%s{abstractions}/9.0.2"
                        [ versioned $"lib/net9.0/%s{abstractions}.dll" assemblyVersion fileVersion ]
                ]

        let application =
            match variant with
            | "as is"
            | "file deleted" -> appDeps (Some "9.0.0.0") (Some "9.0.225.6610")
            | "no deps.json" ->
                onDisk (
                    HostPolicyAppManifest.Listing
                        [
                            "Entry.dll"
                            abstractions + ".dll"
                            "Microsoft.Extensions.DependencyInjection.dll"
                        ]
                )
            | "tie" -> appDeps (Some "11.0.0.0") (Some "11.0.26.52348")
            | "file version + 1" -> appDeps (Some "11.0.0.0") (Some "11.0.26.52349")
            | "deps says 12"
            | "deps says 12, file deleted" -> appDeps (Some "12.0.0.0") (Some "1.0.0.0")
            | "no versions" -> appDeps None None
            | other -> failwith $"unknown variant %s{other}"

        let fxDir = $"/fx/%s{fxVersion}"

        let input =
            frameworkDependent
                runtime
                application
                []
                (framework "Microsoft.NETCore.App" fxDir (netCoreApp fxVersion fxExtra))

        let expectedPath =
            match expected with
            | "app" -> $"/app/%s{abstractions}.dll"
            | _ -> $"%s{fxDir}/%s{abstractions}.dll"

        match HostPolicy.resolveTpa input with
        | Error e -> Assert.Fail $"%A{e}"
        | Ok resolution ->
            TrustedPlatformAssemblies.toMap resolution.Tpa
            |> Map.tryFind abstractions
            |> shouldBe (Some expectedPath)

    let private baseFramework : HostPolicyFramework =
        framework "Microsoft.NETCore.App" "/fx/10.0.14" (netCoreApp "10.0.14" [])

    let private expectRefusal (refusal : HostPolicyRefusal) (input : HostPolicyInput) : unit =
        HostPolicy.resolveTpa input
        |> Result.map (fun r -> TrustedPlatformAssemblies.toMap r.Tpa)
        |> shouldBe (Error refusal)

    [<Test>]
    let ``A minimal framework-dependent launch gives the application and the framework`` () =
        frameworkDependent EmulatedRuntime.Net10 (appWith []) [] baseFramework
        |> HostPolicy.resolveTpa
        |> Result.map (fun r -> TrustedPlatformAssemblies.toMap r.Tpa, r.CoreClr)
        |> shouldBe (
            Ok (
                Map.ofList
                    [
                        "Entry", "/app/Entry.dll"
                        "System.Private.CoreLib", "/fx/10.0.14/System.Private.CoreLib.dll"
                    ],
                "/fx/10.0.14/libcoreclr.dylib"
            )
        )

    /// Measured by Codex on .NET 10: the host keeps the `./` in the TPA, and runs the guest.
    [<Test>]
    let ``An entry assembly listed at ./ is the entry assembly`` () =
        let application =
            onDisk (
                HostPolicyAppManifest.DepsJson (
                    ManifestSpec.render
                        {
                            Libraries =
                                [
                                    { ManifestSpec.library
                                          "Entry/1.0.0"
                                          [
                                              { ManifestSpec.asset "Entry.dll" with
                                                  LocalPath = Some "./Entry.dll"
                                              }
                                          ] with
                                        Type = "project"
                                    }
                                ]
                        }
                )
            )

        frameworkDependent EmulatedRuntime.Net10 application [] baseFramework
        |> HostPolicy.resolveTpa
        |> Result.map (fun r -> TrustedPlatformAssemblies.toMap r.Tpa |> Map.tryFind "Entry")
        |> shouldBe (Ok (Some "/app/./Entry.dll"))

    [<Test>]
    let ``A second application entry resolving to the same path keeps the first one's versions`` () =
        // Stated 12.0.0.0 the second time, but the second resolves to the same file, so the host
        // keeps 1.0.0.0 for the comparison with the framework, and the framework's 2.0.0.0 wins.
        let application =
            appWith
                [
                    ManifestSpec.library "X/1.0" [ versioned "X.dll" (Some "1.0.0.0") None ]
                    ManifestSpec.library "X/12.0" [ versioned "lib/X.dll" (Some "12.0.0.0") None ]
                ]

        let lowest =
            framework
                "Microsoft.NETCore.App"
                "/fx/10.0.14"
                (netCoreApp "10.0.14" [ versioned "X.dll" (Some "2.0.0.0") None ])

        frameworkDependent EmulatedRuntime.Net10 application [] lowest
        |> HostPolicy.resolveTpa
        |> Result.map (fun r -> TrustedPlatformAssemblies.toMap r.Tpa |> Map.tryFind "X")
        |> shouldBe (Ok (Some "/fx/10.0.14/X.dll"))

    [<Test>]
    let ``An application's RID-specific entry without a localPath resolves to its listed path`` () =
        let application =
            appWith
                [
                    { ManifestSpec.library "X/1.0" [] with
                        RidSpecific =
                            [
                                {
                                    Asset = ManifestSpec.asset "runtimes/unix/lib/net10.0/X.dll"
                                    Rid = "unix"
                                    AssetType = "runtime"
                                }
                                {
                                    Asset = ManifestSpec.asset "runtimes/osx/lib/net10.0/X.dll"
                                    Rid = "osx"
                                    AssetType = "runtime"
                                }
                            ]
                    }
                ]

        frameworkDependent EmulatedRuntime.Net10 application [] baseFramework
        |> HostPolicy.resolveTpa
        |> Result.map (fun r -> TrustedPlatformAssemblies.toMap r.Tpa |> Map.tryFind "X")
        |> shouldBe (Ok (Some "/app/runtimes/osx/lib/net10.0/X.dll"))

    [<Test>]
    let ``A self-contained launch reads only the application`` () =
        let application : HostPolicyAppDirectory =
            {
                Directory = "/app"
                Manifest =
                    HostPolicyAppManifest.DepsJson (
                        ManifestSpec.render
                            {
                                Libraries =
                                    [
                                        entryLibrary
                                        { coreLibrary HostPolicyPlatform.MacOS with
                                            RidSpecific =
                                                [
                                                    {
                                                        Asset = ManifestSpec.asset "runtimes/osx/lib/net10.0/Y.dll"
                                                        Rid = "osx"
                                                        AssetType = "runtime"
                                                    }
                                                ]
                                        }
                                    ]
                            }
                    )
                EntryFileName = "Entry.dll"
            }

        {
            Runtime = EmulatedRuntime.Net10
            Platform = HostPolicyPlatform.MacOS
            Launch = HostPolicyLaunch.SelfContained application
            EntryAssemblyName = "Entry"
            Settings = noSettings
        }
        |> HostPolicy.resolveTpa
        |> Result.map (fun r -> TrustedPlatformAssemblies.toMap r.Tpa, r.CoreClr)
        |> shouldBe (
            Ok (
                Map.ofList
                    [
                        "Entry", "/app/Entry.dll"
                        "System.Private.CoreLib", "/app/System.Private.CoreLib.dll"
                    ],
                "/app/libcoreclr.dylib"
            )
        )

    // ---------------------------------------------------------------------------------------
    // One case per refusal.

    [<Test>]
    let ``Refused: the RID graph`` () =
        { frameworkDependent EmulatedRuntime.Net10 (appWith []) [] baseFramework with
            Settings =
                { noSettings with
                    UseRidGraph = true
                }
        }
        |> expectRefusal (HostPolicyRefusal.NotReproducible HostPolicyUnreproducible.UsesRidGraph)

    [<Test>]
    let ``Refused: settings which turn on existence checks`` () =
        { frameworkDependent EmulatedRuntime.Net10 (appWith []) [] baseFramework with
            Settings =
                { noSettings with
                    AdditionalProbingPaths = [ "/probe" ]
                    AdditionalDepsJsons = [ "/extra.deps.json" ]
                    SharedStores = [ "/store" ]
                    ServicingDirectory = Some "/svc"
                }
        }
        |> expectRefusal (
            HostPolicyRefusal.NotReproducible (
                HostPolicyUnreproducible.UnmodelledSettings
                    [
                        HostPolicySetting.AdditionalProbingPath "/probe"
                        HostPolicySetting.AdditionalDepsJson "/extra.deps.json"
                        HostPolicySetting.SharedStore "/store"
                        HostPolicySetting.ServicingDirectory "/svc"
                    ]
            )
        )

    [<Test>]
    let ``Refused: a framework without a deps.json`` () =
        frameworkDependent
            EmulatedRuntime.Net10
            (appWith [])
            []
            { baseFramework with
                DepsJson = None
            }
        |> expectRefusal (
            HostPolicyRefusal.HostWouldFail (HostPolicyFailure.FrameworkDepsJsonMissing "Microsoft.NETCore.App")
        )

    [<Test>]
    let ``Refused: a deps.json the host fails to parse`` () =
        frameworkDependent
            EmulatedRuntime.Net10
            (onDisk (HostPolicyAppManifest.DepsJson unpairedSurrogateManifest))
            []
            baseFramework
        |> expectRefusal (
            HostPolicyRefusal.HostWouldFail (
                HostPolicyFailure.DepsJsonRejected (
                    HostPolicyManifest.Application,
                    DepsJsonError.UnpairedSurrogate [ DepsJsonPathSegment.Member "runtimeTarget" ]
                )
            )
        )

    [<Test>]
    let ``Refused: a deps.json PawPrint cannot read as the host does`` () =
        frameworkDependent
            EmulatedRuntime.Net10
            (appWith [])
            []
            { baseFramework with
                DepsJson = Some "{"B
            }
        |> expectRefusal (
            HostPolicyRefusal.NotReproducible (
                HostPolicyUnreproducible.DepsJsonUnreadable (
                    HostPolicyManifest.Framework "Microsoft.NETCore.App",
                    DepsJsonError.NotStrictJson (Some 0L, Some 1L)
                )
            )
        )

    [<Test>]
    let ``Refused: a name found again with another file name`` () =
        let lowest =
            framework "Microsoft.NETCore.App" "/fx/10.0.14" (netCoreApp "10.0.14" [ ManifestSpec.asset "X.dll" ])

        frameworkDependent
            EmulatedRuntime.Net10
            (appWith [ ManifestSpec.library "X/1.0" [ ManifestSpec.asset "X.exe" ] ])
            []
            lowest
        |> expectRefusal (
            HostPolicyRefusal.HostWouldFail (
                HostPolicyFailure.DuplicateWithDifferentExtension (
                    HostPolicyManifest.Framework "Microsoft.NETCore.App",
                    "X",
                    "X.dll",
                    "/app/X.exe"
                )
            )
        )

    [<Test>]
    let ``Refused: a framework asset whose package no framework has`` () =
        let lowest =
            framework
                "Microsoft.NETCore.App"
                "/fx/10.0.14"
                {
                    Libraries =
                        (netCoreApp "10.0.14" []).Libraries
                        @ [ ManifestSpec.library "P" [ ManifestSpec.asset "Y.dll" ] ]
                }

        frameworkDependent EmulatedRuntime.Net10 (appWith []) [] lowest
        |> expectRefusal (
            HostPolicyRefusal.HostWouldFail (
                HostPolicyFailure.AssetNotFound (
                    HostPolicyManifest.Framework "Microsoft.NETCore.App",
                    DepsAssetType.Runtime,
                    "P",
                    "P",
                    "Y.dll"
                )
            )
        )

    [<Test>]
    let ``Refused: a native framework asset whose package no framework has`` () =
        let lowest =
            framework
                "Microsoft.NETCore.App"
                "/fx/10.0.14"
                {
                    Libraries =
                        (netCoreApp "10.0.14" []).Libraries
                        @ [
                            { ManifestSpec.library "P" [] with
                                Native = [ ManifestSpec.asset "libfoo.dylib" ]
                            }
                        ]
                }

        frameworkDependent EmulatedRuntime.Net10 (appWith []) [] lowest
        |> expectRefusal (
            HostPolicyRefusal.HostWouldFail (
                HostPolicyFailure.AssetNotFound (
                    HostPolicyManifest.Framework "Microsoft.NETCore.App",
                    DepsAssetType.Native,
                    "P",
                    "P",
                    "libfoo.dylib"
                )
            )
        )

    /// `resolve_probe_dirs` lets an `apphost` asset of a `*.Microsoft.NETCore.DotNetAppHost` package
    /// go missing, where it fails the launch for any other native asset.
    [<Test>]
    let ``A missing apphost asset does not fail the launch`` () =
        let lowest =
            framework
                "Microsoft.NETCore.App"
                "/fx/10.0.14"
                {
                    Libraries =
                        (netCoreApp "10.0.14" []).Libraries
                        @ [
                            { ManifestSpec.library "Runtime.osx-arm64.Microsoft.NETCore.DotNetAppHost" [] with
                                Native = [ ManifestSpec.asset "apphost" ]
                            }
                        ]
                }

        frameworkDependent EmulatedRuntime.Net10 (appWith []) [] lowest
        |> HostPolicy.resolveTpa
        |> Result.map (fun r -> TrustedPlatformAssemblies.toMap r.Tpa)
        |> shouldBe (
            Ok (
                Map.ofList
                    [
                        "Entry", "/app/Entry.dll"
                        "System.Private.CoreLib", "/fx/10.0.14/System.Private.CoreLib.dll"
                    ]
            )
        )

    [<Test>]
    let ``Refused: no CoreCLR library`` () =
        let lowest =
            framework
                "Microsoft.NETCore.App"
                "/fx/10.0.14"
                {
                    Libraries =
                        [
                            ManifestSpec.library "Runtime/10.0.14" [ ManifestSpec.asset "System.Private.CoreLib.dll" ]
                        ]
                }

        frameworkDependent EmulatedRuntime.Net10 (appWith []) [] lowest
        |> expectRefusal (HostPolicyRefusal.HostWouldFail HostPolicyFailure.CoreClrNotFound)

    [<Test>]
    let ``Refused: a relative path in the TPA`` () =
        frameworkDependent
            EmulatedRuntime.Net10
            (HostPolicyApplication.OnDisk
                {
                    Directory = "app"
                    Manifest = HostPolicyAppManifest.Listing [ "Entry.dll" ]
                    EntryFileName = "Entry.dll"
                })
            []
            baseFramework
        |> expectRefusal (
            HostPolicyRefusal.HostWouldFail (
                HostPolicyFailure.RuntimeRejectsTpa (TpaRefusal.RelativePath ("Entry", "app/Entry.dll"))
            )
        )

    [<Test>]
    let ``Refused: a TPA path with no assembly extension`` () =
        frameworkDependent
            EmulatedRuntime.Net10
            (appWith
                [
                    ManifestSpec.library
                        "X/1.0"
                        [
                            { ManifestSpec.asset "X.dll" with
                                LocalPath = Some "sub/X.txt"
                            }
                        ]
                ])
            []
            baseFramework
        |> expectRefusal (
            HostPolicyRefusal.HostWouldFail (
                HostPolicyFailure.RuntimeRejectsTpa (TpaRefusal.NoAssemblyExtension ("X", "/app/sub/X.txt"))
            )
        )

    [<Test>]
    let ``Refused: two listed files of one stem and extension`` () =
        frameworkDependent
            EmulatedRuntime.Net10
            (onDisk (HostPolicyAppManifest.Listing [ "Entry.dll" ; "X.dll" ; "X.DLL" ]))
            []
            baseFramework
        |> expectRefusal (
            HostPolicyRefusal.NotReproducible (HostPolicyUnreproducible.ListingNamesCollide ("X", "X.dll", "X.DLL"))
        )

    [<Test>]
    let ``Refused: two frameworks listing one name`` () =
        let higher =
            framework
                "Microsoft.AspNetCore.App"
                "/fx/asp"
                {
                    Libraries = [ ManifestSpec.library "Asp/10.0.14" [ ManifestSpec.asset "Z.dll" ] ]
                }

        let lowest =
            framework "Microsoft.NETCore.App" "/fx/10.0.14" (netCoreApp "10.0.14" [ ManifestSpec.asset "Z.dll" ])

        frameworkDependent EmulatedRuntime.Net10 (appWith []) [ higher ] lowest
        |> expectRefusal (
            HostPolicyRefusal.NotReproducible (
                HostPolicyUnreproducible.FrameworksOverlap ("Z", "Microsoft.AspNetCore.App", "Microsoft.NETCore.App")
            )
        )

    [<Test>]
    let ``Refused: two frameworks naming the CoreCLR library`` () =
        let higher =
            framework
                "Microsoft.AspNetCore.App"
                "/fx/asp"
                {
                    Libraries =
                        [
                            { ManifestSpec.library "Asp/10.0.14" [] with
                                Native = [ ManifestSpec.asset "native/libcoreclr.dylib" ]
                            }
                        ]
                }

        frameworkDependent EmulatedRuntime.Net10 (appWith []) [ higher ] baseFramework
        |> expectRefusal (
            HostPolicyRefusal.NotReproducible (
                HostPolicyUnreproducible.FrameworksOverlap (
                    "libcoreclr.dylib",
                    "Microsoft.AspNetCore.App",
                    "Microsoft.NETCore.App"
                )
            )
        )

    [<Test>]
    let ``Refused: a TPA path holding the path-list separator`` () =
        frameworkDependent
            EmulatedRuntime.Net10
            (HostPolicyApplication.OnDisk
                {
                    Directory = "/app:x"
                    Manifest = HostPolicyAppManifest.Listing [ "Entry.dll" ]
                    EntryFileName = "Entry.dll"
                })
            []
            baseFramework
        |> expectRefusal (
            HostPolicyRefusal.NotReproducible (
                HostPolicyUnreproducible.Tpa (TpaRefusal.HoldsPathListSeparator ("Entry", "/app:x/Entry.dll"))
            )
        )

    [<Test>]
    let ``Refused: a TPA path ending with a space`` () =
        frameworkDependent
            EmulatedRuntime.Net10
            (appWith
                [
                    ManifestSpec.library
                        "X/1.0"
                        [
                            { ManifestSpec.asset "X.dll" with
                                LocalPath = Some "X.dll "
                            }
                        ]
                ])
            []
            baseFramework
        |> expectRefusal (
            HostPolicyRefusal.NotReproducible (
                HostPolicyUnreproducible.Tpa (TpaRefusal.EndsWithSpace ("X", "/app/X.dll "))
            )
        )

    [<Test>]
    let ``Refused: an exe in the TPA`` () =
        frameworkDependent
            EmulatedRuntime.Net10
            (onDisk (HostPolicyAppManifest.Listing [ "Entry.dll" ; "X.exe" ]))
            []
            baseFramework
        |> expectRefusal (
            HostPolicyRefusal.NotReproducible (HostPolicyUnreproducible.Tpa (TpaRefusal.NotDll ("X", "/app/X.exe")))
        )

    [<Test>]
    let ``Refused: a localPath naming another file`` () =
        frameworkDependent
            EmulatedRuntime.Net10
            (appWith
                [
                    ManifestSpec.library
                        "X/1.0"
                        [
                            { ManifestSpec.asset "X.dll" with
                                LocalPath = Some "sub/Other.dll"
                            }
                        ]
                ])
            []
            baseFramework
        |> expectRefusal (
            HostPolicyRefusal.NotReproducible (
                HostPolicyUnreproducible.Tpa (TpaRefusal.NameIsNotFileStem ("X", "/app/sub/Other.dll"))
            )
        )

    [<Test>]
    let ``Refused: two names equal ignoring case`` () =
        let lowest =
            framework "Microsoft.NETCore.App" "/fx/10.0.14" (netCoreApp "10.0.14" [ ManifestSpec.asset "X.dll" ])

        frameworkDependent
            EmulatedRuntime.Net10
            (appWith [ ManifestSpec.library "X/1.0" [ ManifestSpec.asset "x.dll" ] ])
            []
            lowest
        |> expectRefusal (
            HostPolicyRefusal.NotReproducible (
                HostPolicyUnreproducible.Tpa (TpaRefusal.NamesEqualIgnoringCase ("X", "x"))
            )
        )

    [<Test>]
    let ``Refused: no CoreLib`` () =
        let lowest =
            framework
                "Microsoft.NETCore.App"
                "/fx/10.0.14"
                {
                    Libraries =
                        [
                            { ManifestSpec.library "Runtime/10.0.14" [] with
                                Native = [ ManifestSpec.asset "libcoreclr.dylib" ]
                            }
                        ]
                }

        frameworkDependent EmulatedRuntime.Net10 (appWith []) [] lowest
        |> expectRefusal (HostPolicyRefusal.NotReproducible HostPolicyUnreproducible.CoreLibAbsent)

    [<Test>]
    let ``Refused: a CoreLib away from the CoreCLR library`` () =
        let lowest =
            framework
                "Microsoft.NETCore.App"
                "/fx/10.0.14"
                {
                    Libraries =
                        [
                            { ManifestSpec.library
                                  "Runtime/10.0.14"
                                  [
                                      { ManifestSpec.asset "System.Private.CoreLib.dll" with
                                          LocalPath = Some "sub/System.Private.CoreLib.dll"
                                      }
                                  ] with
                                Native = [ ManifestSpec.asset "libcoreclr.dylib" ]
                            }
                        ]
                }

        frameworkDependent EmulatedRuntime.Net10 (appWith []) [] lowest
        |> expectRefusal (
            HostPolicyRefusal.NotReproducible (
                HostPolicyUnreproducible.CoreLibNotBesideCoreClr (
                    "/fx/10.0.14/sub/System.Private.CoreLib.dll",
                    "/fx/10.0.14/libcoreclr.dylib"
                )
            )
        )

    [<Test>]
    let ``Refused: a framework-dependent application whose deps.json names the CoreCLR library`` () =
        let application =
            appWith
                [
                    { ManifestSpec.library "Runtime/1.0" [] with
                        Native = [ ManifestSpec.asset "libcoreclr.dylib" ]
                    }
                ]

        frameworkDependent EmulatedRuntime.Net10 application [] baseFramework
        |> expectRefusal (
            HostPolicyRefusal.NotReproducible (HostPolicyUnreproducible.CoreClrFromApplication "/app/libcoreclr.dylib")
        )

    [<Test>]
    let ``Refused: a framework-dependent application whose directory holds the CoreCLR library`` () =
        frameworkDependent
            EmulatedRuntime.Net10
            (onDisk (HostPolicyAppManifest.Listing [ "Entry.dll" ; "libcoreclr.dylib" ]))
            []
            baseFramework
        |> expectRefusal (
            HostPolicyRefusal.NotReproducible (HostPolicyUnreproducible.CoreClrFromApplication "/app/libcoreclr.dylib")
        )

    [<Test>]
    let ``Refused: a listed CoreCLR library whose name matches only ignoring case`` () =
        let application : HostPolicyAppDirectory =
            {
                Directory = "/app"
                Manifest =
                    HostPolicyAppManifest.Listing [ "Entry.dll" ; "System.Private.CoreLib.dll" ; "LIBCORECLR.SO" ]
                EntryFileName = "Entry.dll"
            }

        {
            Runtime = EmulatedRuntime.Net10
            Platform = HostPolicyPlatform.Linux
            Launch = HostPolicyLaunch.SelfContained application
            EntryAssemblyName = "Entry"
            Settings = noSettings
        }
        |> expectRefusal (
            HostPolicyRefusal.NotReproducible (HostPolicyUnreproducible.CoreClrNameCaseOnly "LIBCORECLR.SO")
        )

    [<Test>]
    let ``Refused: the AppPaths switch`` () =
        { frameworkDependent EmulatedRuntime.Net10 (appWith []) [] baseFramework with
            Settings =
                { noSettings with
                    SetAppPaths = true
                }
        }
        |> expectRefusal (HostPolicyRefusal.NotReproducible HostPolicyUnreproducible.SetsAppPaths)

    /// Measured by Codex on .NET 10 and 11: a resource root holding `:` made CoreCLR fail to start
    /// (0x80070057).
    [<Test>]
    let ``Refused: a resource probe directory holding the path-list separator`` () =
        let application =
            appWith
                [
                    { ManifestSpec.library "Res/1.0" [] with
                        Resources =
                            [
                                { ManifestSpec.asset "lib/net10.0/en/Res.resources.dll" with
                                    LocalPath = Some "d:bad/en/Res.resources.dll"
                                }
                            ]
                    }
                ]

        frameworkDependent EmulatedRuntime.Net10 application [] baseFramework
        |> expectRefusal (
            HostPolicyRefusal.NotReproducible (
                HostPolicyUnreproducible.ProbeDirectoryHoldsPathListSeparator "/app/d:bad/"
            )
        )

    [<Test>]
    let ``Refused: a file name outside ASCII`` () =
        let name = "Ztest" + string (char 0x131)

        frameworkDependent
            EmulatedRuntime.Net10
            (appWith [ ManifestSpec.library "Z/1.0" [ ManifestSpec.asset (name + ".dll") ] ])
            []
            baseFramework
        |> expectRefusal (
            HostPolicyRefusal.NotReproducible (
                HostPolicyUnreproducible.Tpa (TpaRefusal.NonAsciiFileName (name, $"/app/%s{name}.dll"))
            )
        )

    [<Test>]
    let ``Refused: an in-memory entry assembly named as a framework assembly`` () =
        let lowest =
            framework "Microsoft.NETCore.App" "/fx/10.0.14" (netCoreApp "10.0.14" [ ManifestSpec.asset "A.dll" ])

        { frameworkDependent EmulatedRuntime.Net10 HostPolicyApplication.InMemory [] lowest with
            EntryAssemblyName = "a"
        }
        |> expectRefusal (
            HostPolicyRefusal.NotReproducible (
                HostPolicyUnreproducible.EntryAssemblyShadowed ("A", "/fx/10.0.14/A.dll")
            )
        )

    /// Measured on real .NET 10.0.14 and 11.0.0-rc.3: a directory with no `deps.json` holding an
    /// entry assembly named `System.Web.HttpUtility`. The host launched, and CoreCLR ran the
    /// framework's `System.Web.HttpUtility.dll` as the entry assembly (no entry point, so
    /// `MissingMethodException`).
    [<Test>]
    let ``Refused: an entry assembly on disk which the TPA maps to the framework's file`` () =
        let lowest =
            framework
                "Microsoft.NETCore.App"
                "/fx/10.0.14"
                (netCoreApp "10.0.14" [ versioned "Entry.dll" (Some "10.0.0.0") None ])

        frameworkDependent EmulatedRuntime.Net10 (onDisk (HostPolicyAppManifest.Listing [ "Entry.dll" ])) [] lowest
        |> expectRefusal (
            HostPolicyRefusal.NotReproducible (
                HostPolicyUnreproducible.EntryAssemblyShadowed ("Entry", "/fx/10.0.14/Entry.dll")
            )
        )
