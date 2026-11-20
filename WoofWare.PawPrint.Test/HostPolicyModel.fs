namespace WoofWare.PawPrint.Test

open System.Text
open System.Text.Json.Nodes

/// One asset of a generated `deps.json`.
type ManifestAsset =
    {
        /// The asset's member name: its path as listed.
        Path : string
        AssemblyVersion : string option
        FileVersion : string option
        LocalPath : string option
    }

/// A `runtimeTargets` asset of a generated `deps.json`.
type ManifestRidAsset =
    {
        Asset : ManifestAsset
        Rid : string
        /// `runtime` or `native`.
        AssetType : string
    }

/// A package of a generated `deps.json`: its member of the runtime target, and of `libraries`.
type ManifestLibrary =
    {
        /// The key, such as `Name/1.0.0`.
        Key : string
        /// The `libraries` entry's `type`.
        Type : string
        Runtime : ManifestAsset list
        Native : ManifestAsset list
        /// Resource assets, listed as `<dir>/<culture>/<name>.dll`.
        Resources : ManifestAsset list
        RidSpecific : ManifestRidAsset list
        /// Whether `libraries` names the package; the host reads no other.
        InLibraries : bool
    }

/// A generated `deps.json`, rendered by `ManifestSpec.render`.
type ManifestSpec =
    {
        Libraries : ManifestLibrary list
    }

[<RequireQualifiedAccess>]
module ManifestSpec =

    let private target : string = ".NETCoreApp,Version=v10.0/test"

    let private str (s : string) : JsonNode = JsonValue.Create s

    let private assetObject (asset : ManifestAsset) (extra : (string * string) list) : JsonObject =
        let o = JsonObject ()

        for key, value in extra do
            o.Add (key, str value)

        asset.AssemblyVersion |> Option.iter (fun v -> o.Add ("assemblyVersion", str v))
        asset.FileVersion |> Option.iter (fun v -> o.Add ("fileVersion", str v))
        asset.LocalPath |> Option.iter (fun v -> o.Add ("localPath", str v))
        o

    /// The JSON text of `spec`, with members in the order the spec lists them.
    let renderText (spec : ManifestSpec) : string =
        let root = JsonObject ()
        let runtimeTarget = JsonObject ()
        runtimeTarget.Add ("name", str target)
        root.Add ("runtimeTarget", runtimeTarget)

        let packages = JsonObject ()

        for library in spec.Libraries do
            let package = JsonObject ()

            let addAssets (name : string) (assets : ManifestAsset list) : unit =
                if not assets.IsEmpty then
                    let o = JsonObject ()

                    for asset in assets do
                        o.Add (asset.Path, assetObject asset [])

                    package.Add (name, o)

            addAssets "runtime" library.Runtime
            addAssets "native" library.Native
            addAssets "resources" library.Resources

            if not library.RidSpecific.IsEmpty then
                let o = JsonObject ()

                for rid in library.RidSpecific do
                    o.Add (rid.Asset.Path, assetObject rid.Asset [ "rid", rid.Rid ; "assetType", rid.AssetType ])

                package.Add ("runtimeTargets", o)

            packages.Add (library.Key, package)

        let targets = JsonObject ()
        targets.Add (target, packages)
        root.Add ("targets", targets)

        let libraries = JsonObject ()

        for library in spec.Libraries do
            if library.InLibraries then
                let o = JsonObject ()
                o.Add ("type", str library.Type)
                o.Add ("serviceable", JsonValue.Create false)
                o.Add ("sha512", str "")
                libraries.Add (library.Key, o)

        root.Add ("libraries", libraries)
        root.ToJsonString ()

    /// The UTF-8 bytes of `renderText spec`.
    let render (spec : ManifestSpec) : byte[] =
        Encoding.UTF8.GetBytes (renderText spec)

    /// An asset at `path` with no versions and no `localPath`.
    let asset (path : string) : ManifestAsset =
        {
            Path = path
            AssemblyVersion = None
            FileVersion = None
            LocalPath = None
        }

    /// A library listed in `libraries`, of type `package`, with these runtime assets alone.
    let library (key : string) (runtime : ManifestAsset list) : ManifestLibrary =
        {
            Key = key
            Type = "package"
            Runtime = runtime
            Native = []
            Resources = []
            RidSpecific = []
            InLibraries = true
        }
