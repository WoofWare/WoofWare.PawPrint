namespace WoofWare.PawPrint

open System

/// The operating system the host runs on. It names the CoreCLR library which the host looks
/// for: `libcoreclr.so` on Linux, `libcoreclr.dylib` on macOS.
[<RequireQualifiedAccess>]
type internal HostPolicyPlatform =
    | Linux
    | MacOS

/// A shared framework, as `hostfxr` resolved it.
type internal HostPolicyFramework =
    {
        /// The framework's name, such as `Microsoft.NETCore.App`.
        Name : string
        /// The framework's directory, as `hostfxr` spells it.
        Directory : string
        /// The bytes of `<Directory>/<Name>.deps.json`, or `None` if there is no such file.
        DepsJson : byte[] option
    }

/// What an application's directory tells the host about the assemblies in it.
[<RequireQualifiedAccess>]
type internal HostPolicyAppManifest =
    /// The bytes of the application's `deps.json`.
    | DepsJson of contents : byte[]
    /// There is no `deps.json`. `names` are the names of the entries in the directory, in the
    /// order the host's directory listing gives them; the host considers each as a file.
    /// `coreClrExists` is whether the CoreCLR library's name, in the directory, names something
    /// `stat` finds: the host asks that apart from the listing, which omits some kinds of entry.
    | Listing of names : string list * coreClrExists : bool

/// An application in a directory.
type internal HostPolicyAppDirectory =
    {
        /// The application's directory, absolute, as the host spells it (with links resolved).
        Directory : string
        Manifest : HostPolicyAppManifest
        /// The file name of the entry assembly, in `Directory`.
        EntryFileName : string
    }

/// The application whose TPA the host computes.
[<RequireQualifiedAccess>]
type internal HostPolicyApplication =
    | OnDisk of HostPolicyAppDirectory
    /// An image held in memory, with no directory. It has no host counterpart: the TPA holds only
    /// the frameworks' entries.
    | InMemory

/// How the host launches the application.
[<RequireQualifiedAccess>]
type internal HostPolicyLaunch =
    /// On shared frameworks. `higher` are the frameworks above the lowest, in `hostfxr`'s order
    /// (highest first); `lowest` is the last, usually `Microsoft.NETCore.App`. `rids` is the
    /// host's RID list, most specific first, as `DOTNET_RUNTIME_ID` and the host's build give it.
    | FrameworkDependent of
        application : HostPolicyApplication *
        rids : string list *
        higher : HostPolicyFramework list *
        lowest : HostPolicyFramework
    /// With the runtime in the application's own directory, and no shared framework.
    | SelfContained of application : HostPolicyAppDirectory

/// The host settings which turn on file existence checks or add `deps.json` files, as the caller
/// found them. The host is modelled only with all of them absent.
type internal HostPolicySettings =
    {
        /// `System.Runtime.Loader.UseRidGraph` is `true` in the runtimeconfig.
        UseRidGraph : bool
        /// `Microsoft.NETCore.DotNetHostPolicy.SetAppPaths` is `true` in the merged runtimeconfig
        /// properties, so CoreCLR also binds a name outside the TPA from the application's
        /// directory.
        SetAppPaths : bool
        /// The additional probing paths the host admits.
        AdditionalProbingPaths : string list
        /// The additional `deps.json` files or directories (`DOTNET_ADDITIONAL_DEPS`, or
        /// `--additional-deps`) of a framework-dependent launch.
        AdditionalDepsJsons : string list
        /// The shared store directories which exist (`DOTNET_SHARED_STORE`, or `store` beside the
        /// `dotnet` muxer).
        SharedStores : string list
        /// The servicing directory, if it exists (`CORE_SERVICING`, or `/opt/coreservicing`).
        ServicingDirectory : string option
    }

/// What `HostPolicy.resolveTpa` computes the TPA of.
type internal HostPolicyInput =
    {
        /// Whose hostpolicy computes the TPA.
        Runtime : EmulatedRuntime
        Platform : HostPolicyPlatform
        Launch : HostPolicyLaunch
        /// The entry assembly's simple name, from its metadata.
        EntryAssemblyName : string
        Settings : HostPolicySettings
    }

/// What `HostPolicy.resolveTpa` found.
type internal HostPolicyResolution =
    {
        /// The TPA the host gives CoreCLR.
        Tpa : TrustedPlatformAssemblies
        /// The CoreCLR library the host loads, as the host finds it, before it resolves links.
        /// CoreCLR loads CoreLib from beside the library's real path. `resolveTpa` checks by
        /// spelling that this is the TPA's `System.Private.CoreLib`, and cannot see links; a caller
        /// which reads the filesystem must check that the CoreLib beside this file's real path is
        /// the TPA's file of that name.
        CoreClr : string
    }

/// Which `deps.json` something came from.
[<RequireQualifiedAccess>]
type internal HostPolicyManifest =
    | Application
    | Framework of name : string

/// A host setting which `HostPolicy.resolveTpa` does not model.
[<RequireQualifiedAccess>]
type internal HostPolicySetting =
    | AdditionalProbingPath of path : string
    | AdditionalDepsJson of path : string
    | SharedStore of path : string
    | ServicingDirectory of path : string

/// Why the host would fail the launch.
[<RequireQualifiedAccess>]
type internal HostPolicyFailure =
    /// A framework has no `deps.json`.
    | FrameworkDepsJsonMissing of framework : string
    /// The host cannot read the `deps.json`.
    | DepsJsonRejected of manifest : HostPolicyManifest * error : DepsJsonError
    /// An asset's file name differs from the file already in the TPA under its name.
    | DuplicateWithDifferentExtension of
        manifest : HostPolicyManifest *
        name : string *
        relativePath : string *
        existingPath : string
    /// A framework asset, which the host needs, is in no framework at or below its own whose
    /// `deps.json` has its package.
    | AssetNotFound of
        manifest : HostPolicyManifest *
        assetType : DepsAssetType *
        libraryName : string *
        libraryVersion : string *
        relativePath : string
    /// No `deps.json`, and no directory, holds the CoreCLR library.
    | CoreClrNotFound
    /// CoreCLR would refuse the TPA.
    | RuntimeRejectsTpa of TpaRefusal

/// Why PawPrint cannot reproduce what the host would do.
[<RequireQualifiedAccess>]
type internal HostPolicyUnreproducible =
    /// The runtimeconfig sets `System.Runtime.Loader.UseRidGraph`, so the host selects RID-specific
    /// assets by the RID graph.
    | UsesRidGraph
    /// The runtimeconfig sets `Microsoft.NETCore.DotNetHostPolicy.SetAppPaths`, so CoreCLR binds
    /// names outside the TPA too.
    | SetsAppPaths
    /// These settings turn on file existence checks, or add `deps.json` files.
    | UnmodelledSettings of settings : HostPolicySetting list
    /// The `deps.json` is one which the host's reader and PawPrint's might read differently, or
    /// on which the host's behaviour is undefined.
    | DepsJsonUnreadable of manifest : HostPolicyManifest * error : DepsJsonError
    /// Two entries of the listing give one simple name for one extension, so the host's answer
    /// depends on the order of its directory listing.
    | ListingNamesCollide of name : string * first : string * second : string
    /// Two frameworks list one runtime asset name, or both name the CoreCLR library, so the
    /// answer depends on the order of the frameworks.
    | FrameworksOverlap of name : string * higher : string * lower : string
    /// A framework-dependent application supplies the CoreCLR library, at `path`. The host loads
    /// it, and CoreLib, from wherever its links lead, which PawPrint cannot see.
    | CoreClrFromApplication of path : string
    /// The host's TPA is one PawPrint cannot bind from as CoreCLR does.
    | Tpa of TpaRefusal
    /// A native or resource probe directory holds `:`, which separates the entries of the host's
    /// directory lists.
    | ProbeDirectoryHoldsPathListSeparator of directory : string
    /// The TPA has no `System.Private.CoreLib`.
    | CoreLibAbsent
    /// The TPA's `System.Private.CoreLib` is not the one beside the CoreCLR library, which is the
    /// one CoreCLR loads.
    | CoreLibNotBesideCoreClr of coreLib : string * coreClr : string
    /// The TPA maps the entry assembly's name to another file, which CoreCLR would run instead.
    | EntryAssemblyShadowed of name : string * path : string

/// Why `HostPolicy.resolveTpa` gave no TPA.
[<RequireQualifiedAccess>]
type internal HostPolicyRefusal =
    | HostWouldFail of HostPolicyFailure
    | NotReproducible of HostPolicyUnreproducible

[<RequireQualifiedAccess>]
module internal HostPolicy =

    /// One `deps.json` the host reads (or, for the application, may lack), and the directory its
    /// entries resolve against. A launch's manifests are the application's, then the frameworks',
    /// highest level first, so that a manifest's index is the host's `fx_level`.
    type private Manifest =
        {
            Source : HostPolicyManifest
            /// Empty for an application held in memory.
            Directory : string
            Deps : DepsManifest option
        }

    /// What the TPA holds for one name, with the versions the host compares a later entry with.
    type private Resolved =
        {
            Path : string
            AssemblyVersion : DepsVersion
            FileVersion : DepsVersion
        }

    /// Where the host meets a TPA entry, in its order.
    [<RequireQualifiedAccess>]
    type private TpaSource =
        /// A runtime entry of the manifest at this index.
        | Entry of level : int * entry : DepsEntry
        /// A file of the application's directory, which has no `deps.json`.
        | DirectoryFile of name : string * path : string

    /// Where the host meets a native entry, in its order.
    [<RequireQualifiedAccess>]
    type private NativeSource =
        | Entry of level : int * entry : DepsEntry
        /// The application's directory, which has no `deps.json`.
        | ApplicationDirectory

    /// What two frameworks may both list.
    [<RequireQualifiedAccess>]
    type private OverlapKey =
        | RuntimeName of string
        | CoreClr

    let private coreLibName : string = "System.Private.CoreLib"

    let private coreClrFileName (platform : HostPolicyPlatform) : string =
        match platform with
        | HostPolicyPlatform.Linux -> "libcoreclr.so"
        | HostPolicyPlatform.MacOS -> "libcoreclr.dylib"

    /// `append_path` (`hostmisc/utils.cpp`).
    let private join (directory : string) (relative : string) : string =
        if relative.Length = 0 then
            directory
        elif directory.Length = 0 then
            relative
        elif directory.EndsWith '/' || relative.StartsWith '/' then
            directory + relative
        else
            directory + "/" + relative

    /// `get_filename`.
    let private fileNameOf (path : string) : string =
        path.Substring (path.LastIndexOf '/' + 1)

    /// `get_directory`: up to and including the last `/` of the path without its trailing `/`s,
    /// with any `/`s before that one dropped.
    let private directoryOf (path : string) : string =
        let trimmed = path.TrimEnd '/'

        match trimmed.LastIndexOf '/' with
        | -1 -> trimmed + "/"
        | separator -> trimmed.Substring(0, separator).TrimEnd '/' + "/"

    /// `path` without its `.` components and repeated `/`s: another spelling of the same file,
    /// whatever links the path passes through. `..` components are kept, since after a link they
    /// climb from the link's target.
    let private withoutDotComponents (path : string) : string =
        let components =
            path.Split '/'
            |> Array.filter (fun c -> c <> "" && c <> ".")
            |> String.concat "/"

        if path.StartsWith '/' then "/" + components else components

    /// `pal::strcasecmp` equality against an ASCII string: `OrdinalIgnoreCase` folds no non-ASCII
    /// character to an ASCII one.
    let private equalsIgnoringAsciiCase (a : string) (b : string) : bool =
        String.Equals (a, b, StringComparison.OrdinalIgnoreCase)

    /// The extensions `get_dir_assemblies` takes, in its order of preference.
    let private managedExtensions (runtime : EmulatedRuntime) : string list =
        match runtime with
        | EmulatedRuntime.Net10 -> [ ".ni.dll" ; ".dll" ; ".ni.exe" ; ".exe" ]
        | EmulatedRuntime.Net11 -> [ ".dll" ; ".exe" ]

    /// Each `(stem, file)` of `files` whose name ends `extension` in any ASCII case, with
    /// something before it.
    let private withExtension (extension : string) (files : string list) : (string * string) list =
        files
        |> List.choose (fun file ->
            if
                file.Length > extension.Length
                && equalsIgnoringAsciiCase (file.Substring (file.Length - extension.Length)) extension
            then
                Some (file.Substring (0, file.Length - extension.Length), file)
            else
                None
        )

    let private unmodelledSettings (settings : HostPolicySettings) : Result<unit, HostPolicyRefusal> =
        if settings.UseRidGraph then
            Error (HostPolicyRefusal.NotReproducible HostPolicyUnreproducible.UsesRidGraph)
        elif settings.SetAppPaths then
            Error (HostPolicyRefusal.NotReproducible HostPolicyUnreproducible.SetsAppPaths)
        else
            let unmodelled =
                List.concat
                    [
                        settings.AdditionalProbingPaths
                        |> List.map HostPolicySetting.AdditionalProbingPath
                        settings.AdditionalDepsJsons |> List.map HostPolicySetting.AdditionalDepsJson
                        settings.SharedStores |> List.map HostPolicySetting.SharedStore
                        settings.ServicingDirectory
                        |> Option.toList
                        |> List.map HostPolicySetting.ServicingDirectory
                    ]

            if unmodelled.IsEmpty then
                Ok ()
            else
                Error (HostPolicyRefusal.NotReproducible (HostPolicyUnreproducible.UnmodelledSettings unmodelled))

    let private parse
        (runtime : EmulatedRuntime)
        (source : HostPolicyManifest)
        (mode : DepsJsonMode)
        (bytes : byte[])
        : Result<DepsManifest, HostPolicyRefusal>
        =
        DepsJson.parse runtime mode bytes
        |> Result.mapError (fun error ->
            match error with
            // rapidjson refuses half a surrogate pair, and the host fails the launch.
            | DepsJsonError.UnpairedSurrogate _ ->
                HostPolicyRefusal.HostWouldFail (HostPolicyFailure.DepsJsonRejected (source, error))
            | _ -> HostPolicyRefusal.NotReproducible (HostPolicyUnreproducible.DepsJsonUnreadable (source, error))
        )

    /// The manifests of a launch, application first, read in the order in which
    /// `deps_resolver_t::valid` reports them. `rids` is `None` for a self-contained launch. The
    /// host reads the lowest framework's `deps.json`, and a self-contained application's, without
    /// RID-specific assets.
    let private readManifests
        (runtime : EmulatedRuntime)
        (application : HostPolicyAppDirectory option)
        (rids : string list option)
        (frameworks : HostPolicyFramework list)
        : Result<Manifest list, HostPolicyRefusal>
        =
        let frameworkDependent =
            DepsJsonMode.FrameworkDependent (Option.defaultValue [] rids)

        let appManifest () : Result<Manifest, HostPolicyRefusal> =
            let directory =
                match application with
                | Some app -> app.Directory.TrimEnd '/' + "/"
                | None -> ""

            let deps =
                match application with
                | Some {
                           Manifest = HostPolicyAppManifest.DepsJson bytes
                       } ->
                    let mode =
                        if rids.IsSome then
                            frameworkDependent
                        else
                            DepsJsonMode.SelfContained

                    parse runtime HostPolicyManifest.Application mode bytes |> Result.map Some
                | Some {
                           Manifest = HostPolicyAppManifest.Listing _
                       }
                | None -> Ok None

            deps
            |> Result.map (fun deps ->
                {
                    Source = HostPolicyManifest.Application
                    Directory = directory
                    Deps = deps
                }
            )

        let lowest = List.length frameworks - 1

        let frameworkManifest (i : int) (framework : HostPolicyFramework) () : Result<Manifest, HostPolicyRefusal> =
            let source = HostPolicyManifest.Framework framework.Name

            match framework.DepsJson with
            | None ->
                Error (HostPolicyRefusal.HostWouldFail (HostPolicyFailure.FrameworkDepsJsonMissing framework.Name))
            | Some bytes ->
                let mode =
                    if i = lowest then
                        DepsJsonMode.SelfContained
                    else
                        frameworkDependent

                parse runtime source mode bytes
                |> Result.map (fun deps ->
                    {
                        Source = source
                        Directory = framework.Directory
                        Deps = Some deps
                    }
                )

        (appManifest :: List.mapi frameworkManifest frameworks)
        |> List.fold
            (fun (state : Result<Manifest list, HostPolicyRefusal>) read ->
                state |> Result.bind (fun acc -> read () |> Result.map (fun m -> m :: acc))
            )
            (Ok [])
        |> Result.map List.rev

    let private entriesOf (assetType : DepsAssetType) (manifest : Manifest) : DepsEntry list =
        match manifest.Deps with
        | None -> []
        | Some deps -> deps.Entries |> List.filter (fun e -> e.AssetType = assetType)

    /// Refuses a listing in which two files of one extension give one stem: the host keeps
    /// whichever its directory listing gives first. Names the first file, in listing order, whose
    /// stem an earlier file of its extension gave.
    let private listingCollision (runtime : EmulatedRuntime) (listing : string list) : Result<unit, HostPolicyRefusal> =
        managedExtensions runtime
        |> List.tryPick (fun extension ->
            withExtension extension listing
            |> List.fold
                (fun (seen : Map<string, string>, found : HostPolicyUnreproducible option) (stem, file) ->
                    match found, Map.tryFind stem seen with
                    | Some _, _ -> seen, found
                    | None, Some first ->
                        seen, Some (HostPolicyUnreproducible.ListingNamesCollide (stem, first, file))
                    | None, None -> Map.add stem file seen, None
                )
                (Map.empty, None)
            |> snd
        )
        |> function
            | Some reason -> Error (HostPolicyRefusal.NotReproducible reason)
            | None -> Ok ()

    /// Refuses frameworks of which two list one runtime name, or both name the CoreCLR library:
    /// the host's order of frameworks decides those, and that order is an input which PawPrint
    /// has not tested.
    let private frameworkOverlap (coreClr : string) (frameworks : Manifest list) : Result<unit, HostPolicyRefusal> =
        let keysOf (manifest : Manifest) : (OverlapKey * string) list =
            [
                for e in entriesOf DepsAssetType.Runtime manifest do
                    if not (DepsEntry.isPlaceholder e) then
                        yield OverlapKey.RuntimeName e.AssetName, e.AssetName
                for e in entriesOf DepsAssetType.Native manifest do
                    if
                        not (DepsEntry.isPlaceholder e)
                        && equalsIgnoringAsciiCase (fileNameOf (DepsEntry.dirRelativePath e)) coreClr
                    then
                        yield OverlapKey.CoreClr, coreClr
            ]

        let keyed =
            frameworks
            |> List.map (fun f ->
                let keys = keysOf f
                f.Source, keys, keys |> List.map fst |> Set.ofList
            )

        let rec find (remaining : (HostPolicyManifest * (OverlapKey * string) list * Set<OverlapKey>) list) =
            match remaining with
            | [] -> None
            | (framework, keys, _) :: lower ->
                keys
                |> List.tryPick (fun (key, name) ->
                    lower
                    |> List.tryFind (fun (_, _, set) -> set.Contains key)
                    |> Option.map (fun (other, _, _) -> name, framework, other)
                )
                |> Option.orElseWith (fun () -> find lower)

        match find keyed with
        | None -> Ok ()
        | Some (name, HostPolicyManifest.Framework higher, HostPolicyManifest.Framework lower) ->
            Error (HostPolicyRefusal.NotReproducible (HostPolicyUnreproducible.FrameworksOverlap (name, higher, lower)))
        | Some (_, higher, lower) -> failwith $"logic error: %A{higher} or %A{lower} is not a framework"

    /// `probe_deps_entry` with no file existence checks: the application's entries resolve in its
    /// directory; a framework's, in the first framework from its own down whose `deps.json` has
    /// its package.
    let private probe (manifests : Manifest[]) (level : int) (entry : DepsEntry) : string option =
        let app = manifests.[0]

        if level = 0 && app.Directory.Length > 0 then
            if entry.IsRidSpecific && entry.LocalPath.IsNone then
                Some (join app.Directory entry.RelativePath)
            else
                Some (join app.Directory (DepsEntry.dirRelativePath entry))
        else
            seq { max level 1 .. manifests.Length - 1 }
            |> Seq.tryPick (fun j ->
                let framework = manifests.[j]

                match framework.Deps with
                | Some deps when
                    framework.Directory.Length > 0
                    && DepsManifest.hasPackage deps entry.LibraryName entry.LibraryVersion
                    ->
                    Some (join framework.Directory (DepsEntry.dirRelativePath entry))
                | _ -> None
            )

    let private notFound (manifests : Manifest[]) (level : int) (entry : DepsEntry) : HostPolicyRefusal =
        HostPolicyRefusal.HostWouldFail (
            HostPolicyFailure.AssetNotFound (
                manifests.[level].Source,
                entry.AssetType,
                entry.LibraryName,
                entry.LibraryVersion,
                entry.RelativePath
            )
        )

    /// One step of `resolve_tpa_list`: the TPA after the host meets `source`.
    let private step
        (manifests : Manifest[])
        (tpa : Map<string, Resolved>)
        (source : TpaSource)
        : Result<Map<string, Resolved>, HostPolicyRefusal>
        =
        match source with
        | TpaSource.DirectoryFile (name, path) ->
            if tpa.ContainsKey name then
                Ok tpa
            else
                tpa
                |> Map.add
                    name
                    {
                        Path = path
                        AssemblyVersion = DepsVersion.Empty
                        FileVersion = DepsVersion.Empty
                    }
                |> Ok
        | TpaSource.Entry (_, entry) when DepsEntry.isPlaceholder entry -> Ok tpa
        | TpaSource.Entry (level, entry) ->
            let resolved (path : string) : Resolved =
                {
                    Path = path
                    AssemblyVersion = entry.AssemblyVersion
                    FileVersion = entry.FileVersion
                }

            match Map.tryFind entry.AssetName tpa with
            | None ->
                match probe manifests level entry with
                | Some path -> Ok (Map.add entry.AssetName (resolved path) tpa)
                | None -> Error (notFound manifests level entry)
            | Some existing when fileNameOf entry.RelativePath <> fileNameOf existing.Path ->
                Error (
                    HostPolicyRefusal.HostWouldFail (
                        HostPolicyFailure.DuplicateWithDifferentExtension (
                            manifests.[level].Source,
                            entry.AssetName,
                            entry.RelativePath,
                            existing.Path
                        )
                    )
                )
            | Some existing ->
                let assembly = DepsVersion.compare entry.AssemblyVersion existing.AssemblyVersion

                // A later entry wins when it is newer, or as new: so a tie goes to the framework.
                if
                    assembly > 0
                    || (assembly = 0 && DepsVersion.compare entry.FileVersion existing.FileVersion >= 0)
                then
                    match probe manifests level entry with
                    // The same path keeps the earlier entry, versions and all.
                    | Some path when path = existing.Path -> Ok tpa
                    | Some path -> Ok (Map.add entry.AssetName (resolved path) tpa)
                    // Missing from the application, it is ignored; from a framework, it fails.
                    | None when level = 0 -> Ok tpa
                    | None -> Error (notFound manifests level entry)
                else
                    Ok tpa

    /// What the host's native probe finds.
    type private NativeProbe =
        {
            /// The CoreCLR library the host loads.
            CoreClr : string
            /// The index of the manifest which gave it: 0 for the application.
            CoreClrLevel : int
            /// The native probe directories, in the order the host adds them.
            Directories : string list
        }

    /// The native half of `resolve_probe_dirs`. The CoreCLR library the host loads is the first
    /// native entry, of the application then the frameworks, that resolves to a file of its name;
    /// or, with no application `deps.json`, the one in the application's directory. Fails as the
    /// host does on a native entry it cannot resolve.
    let private probeNatives
        (manifests : Manifest[])
        (coreClr : string)
        (sources : NativeSource list)
        (coreClrInApplicationDirectory : bool)
        : Result<NativeProbe, HostPolicyRefusal>
        =
        let found =
            sources
            |> List.fold
                (fun (state : Result<(string * int) option * string list, HostPolicyRefusal>) source ->
                    match state, source with
                    | Error _, _ -> state
                    | Ok _, NativeSource.Entry (_, entry) when DepsEntry.isPlaceholder entry -> state
                    | Ok (found, directories), NativeSource.Entry (level, entry) ->
                        match probe manifests level entry with
                        | Some path ->
                            let found =
                                match found with
                                | None when path.EndsWith ("/" + coreClr, StringComparison.OrdinalIgnoreCase) ->
                                    Some (path, level)
                                | _ -> found

                            Ok (found, directoryOf path :: directories)
                        | None when
                            entry.AssetName = "apphost"
                            && entry.LibraryName.EndsWith (
                                ".Microsoft.NETCore.DotNetAppHost",
                                StringComparison.OrdinalIgnoreCase
                            )
                            ->
                            state
                        | None -> Error (notFound manifests level entry)
                    | Ok (found, directories), NativeSource.ApplicationDirectory ->
                        let appDirectory = manifests.[0].Directory
                        let directories = appDirectory :: directories

                        match found with
                        // `file_exists_in_dir`, whose answer the input carries.
                        | None when coreClrInApplicationDirectory ->
                            Ok (Some (join appDirectory coreClr, 0), directories)
                        | _ -> Ok (found, directories)
                )
                (Ok (None, []))

        match found with
        | Error e -> Error e
        | Ok (None, _) -> Error (HostPolicyRefusal.HostWouldFail HostPolicyFailure.CoreClrNotFound)
        | Ok (Some (path, level), directories) ->
            Ok
                {
                    CoreClr = path
                    CoreClrLevel = level
                    Directories = List.rev directories
                }

    /// The resource half of `resolve_probe_dirs`: each resource entry's root (the directory above
    /// its culture's), and, with no application `deps.json`, the application's directory. The
    /// host only reports a resource it cannot find.
    let private resourceRoots (manifests : Manifest[]) (sources : NativeSource list) : string list =
        sources
        |> List.choose (fun source ->
            match source with
            | NativeSource.Entry (_, entry) when DepsEntry.isPlaceholder entry -> None
            | NativeSource.Entry (level, entry) ->
                probe manifests level entry |> Option.map (directoryOf >> directoryOf)
            | NativeSource.ApplicationDirectory -> Some manifests.[0].Directory
        )

    let private ofTpaRefusal (refusal : TpaRefusal) : HostPolicyRefusal =
        match refusal with
        // CoreCLR refuses these, and the runtime does not start.
        | TpaRefusal.RelativePath _
        | TpaRefusal.NoAssemblyExtension _ ->
            HostPolicyRefusal.HostWouldFail (HostPolicyFailure.RuntimeRejectsTpa refusal)
        | TpaRefusal.HoldsNul _
        | TpaRefusal.HoldsPathListSeparator _
        | TpaRefusal.EndsWithSpace _
        | TpaRefusal.NonAsciiFileName _
        | TpaRefusal.NotDll _
        | TpaRefusal.NameIsNotFileStem _
        | TpaRefusal.NamesEqualIgnoringCase _ ->
            HostPolicyRefusal.NotReproducible (HostPolicyUnreproducible.Tpa refusal)

    /// The TPA which the host would give CoreCLR for `input`, as hostpolicy's `resolve_tpa_list`
    /// computes it with no file existence checks; or why the host would fail the launch, or why
    /// PawPrint cannot reproduce its answer. Reads no file: the caller supplies every manifest and
    /// listing.
    ///
    /// Beyond the host's own rules, refuses a TPA whose `System.Private.CoreLib` is not the one
    /// beside the CoreCLR library, and one which maps the entry assembly's name to another file.
    let resolveTpa (input : HostPolicyInput) : Result<HostPolicyResolution, HostPolicyRefusal> =
        let application, rids, frameworks =
            match input.Launch with
            | HostPolicyLaunch.FrameworkDependent (application, rids, higher, lowest) ->
                let directory =
                    match application with
                    | HostPolicyApplication.OnDisk d -> Some d
                    | HostPolicyApplication.InMemory -> None

                directory, Some rids, higher @ [ lowest ]
            | HostPolicyLaunch.SelfContained d -> Some d, None, []

        let listing =
            match application with
            | Some {
                       Manifest = HostPolicyAppManifest.Listing (names, _)
                   } -> Some names
            | _ -> None

        let coreClr = coreClrFileName input.Platform

        unmodelledSettings input.Settings
        |> Result.bind (fun () -> readManifests input.Runtime application rids frameworks)
        |> Result.bind (fun manifests ->
            listing
            |> Option.map (listingCollision input.Runtime)
            |> Option.defaultValue (Ok ())
            |> Result.bind (fun () -> frameworkOverlap coreClr (List.tail manifests))
            |> Result.map (fun () -> Array.ofList manifests)
        )
        |> Result.bind (fun manifests ->
            let app = manifests.[0]
            let frameworkLevels = [ 1 .. manifests.Length - 1 ]

            let directoryFiles =
                match listing with
                | None -> []
                | Some files ->
                    managedExtensions input.Runtime
                    |> List.collect (fun extension ->
                        withExtension extension files
                        |> List.map (fun (stem, file) -> TpaSource.DirectoryFile (stem, app.Directory + file))
                    )

            let tpaSources =
                [
                    for e in entriesOf DepsAssetType.Runtime app -> TpaSource.Entry (0, e)
                    yield! directoryFiles
                    for level in frameworkLevels do
                        for e in entriesOf DepsAssetType.Runtime manifests.[level] -> TpaSource.Entry (level, e)
                ]

            let probeSources (assetType : DepsAssetType) : NativeSource list =
                [
                    for e in entriesOf assetType app -> NativeSource.Entry (0, e)
                    if listing.IsSome then
                        yield NativeSource.ApplicationDirectory
                    for level in frameworkLevels do
                        for e in entriesOf assetType manifests.[level] -> NativeSource.Entry (level, e)
                ]

            tpaSources
            |> List.fold
                (fun state source -> state |> Result.bind (fun tpa -> step manifests tpa source))
                (Ok Map.empty)
            |> Result.bind (fun tpa ->
                let coreClrInApplicationDirectory =
                    match application with
                    | Some {
                               Manifest = HostPolicyAppManifest.Listing (_, exists)
                           } -> exists
                    | _ -> false

                probeNatives manifests coreClr (probeSources DepsAssetType.Native) coreClrInApplicationDirectory
                |> Result.map (fun natives -> tpa, natives)
            )
            |> Result.bind (fun (tpa, natives) ->
                match application with
                // The host loads the library from wherever its links lead, and CoreLib from
                // beside that.
                | Some _ when natives.CoreClrLevel = 0 && rids.IsSome ->
                    Error (
                        HostPolicyRefusal.NotReproducible (
                            HostPolicyUnreproducible.CoreClrFromApplication natives.CoreClr
                        )
                    )
                | _ -> Ok (tpa, natives)
            )
            |> Result.bind (fun (tpa, natives) ->
                tpa
                |> Map.map (fun _ resolved -> resolved.Path)
                |> TrustedPlatformAssemblies.tryCreate
                |> Result.mapError ofTpaRefusal
                |> Result.map (fun tpa -> tpa, natives)
            )
            |> Result.bind (fun (tpa, natives) ->
                // CoreCLR splits its lists of directories at `:`.
                natives.Directories
                @ resourceRoots manifests (probeSources DepsAssetType.Resources)
                |> List.tryFind (fun directory -> directory.Contains ':')
                |> function
                    | Some directory ->
                        Error (
                            HostPolicyRefusal.NotReproducible (
                                HostPolicyUnreproducible.ProbeDirectoryHoldsPathListSeparator directory
                            )
                        )
                    | None -> Ok (tpa, natives.CoreClr)
            )
            |> Result.bind (fun (tpa, coreClrPath) ->
                // CoreCLR loads the CoreLib beside the CoreCLR library, whatever the TPA says.
                match TrustedPlatformAssemblies.tryFindIgnoringCase coreLibName tpa with
                | None -> Error (HostPolicyRefusal.NotReproducible HostPolicyUnreproducible.CoreLibAbsent)
                | Some (_, path) when
                    withoutDotComponents path
                    <> withoutDotComponents (join (directoryOf coreClrPath) (coreLibName + ".dll"))
                    ->
                    Error (
                        HostPolicyRefusal.NotReproducible (
                            HostPolicyUnreproducible.CoreLibNotBesideCoreClr (path, coreClrPath)
                        )
                    )
                | Some _ -> Ok (tpa, coreClrPath)
            )
            |> Result.bind (fun (tpa, coreClrPath) ->
                // CoreCLR runs the TPA's file of the entry assembly's name, not the file it was
                // given (measured on .NET 10 and 11).
                let entryPath =
                    application
                    |> Option.map (fun d -> withoutDotComponents (join app.Directory d.EntryFileName))

                match TrustedPlatformAssemblies.tryFindIgnoringCase input.EntryAssemblyName tpa with
                | Some (name, path) when Some (withoutDotComponents path) <> entryPath ->
                    Error (
                        HostPolicyRefusal.NotReproducible (
                            HostPolicyUnreproducible.EntryAssemblyShadowed (name, path)
                        )
                    )
                | _ ->
                    Ok
                        {
                            Tpa = tpa
                            CoreClr = coreClrPath
                        }
            )
        )
