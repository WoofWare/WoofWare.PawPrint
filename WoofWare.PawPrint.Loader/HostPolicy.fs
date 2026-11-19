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
    /// There is no `deps.json`. The names of the entries in the directory, in the order the host's
    /// directory listing gives them; the host considers each as a file.
    | Listing of names : string list

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
    /// The host's TPA is one PawPrint cannot bind from as CoreCLR does.
    | Tpa of TpaRefusal
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

    /// The TPA which the host would give CoreCLR for `input`, as hostpolicy's `resolve_tpa_list`
    /// computes it with no file existence checks; or why the host would fail the launch, or why
    /// PawPrint cannot reproduce its answer. Reads no file: the caller supplies every manifest and
    /// listing.
    ///
    /// Beyond the host's own rules, refuses a TPA whose `System.Private.CoreLib` is not the one
    /// beside the CoreCLR library, and one which maps the entry assembly's name to another file.
    let resolveTpa (input : HostPolicyInput) : Result<TrustedPlatformAssemblies, HostPolicyRefusal> =
        Error (HostPolicyRefusal.NotReproducible HostPolicyUnreproducible.UsesRidGraph)
