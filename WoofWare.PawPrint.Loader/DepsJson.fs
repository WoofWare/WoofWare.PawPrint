namespace WoofWare.PawPrint

open System
open System.Collections.Generic
open System.Text
open System.Text.Json

/// An assembly or file version as `hostpolicy` reads one from a `deps.json` (its `version_t`):
/// two to four components, or `Empty` when the text is absent or does not parse.
///
/// Compare two with `DepsVersion.compare`, never structurally: `Empty` is below every parsed
/// version, and a missing component is below zero.
[<RequireQualifiedAccess ; NoComparison>]
type internal DepsVersion =
    /// No version, or one which `version_t::parse` does not accept. The host stores
    /// `-1.-1.-1.-1`.
    | Empty
    | MajorMinor of major : int * minor : int
    | MajorMinorBuild of major : int * minor : int * build : int
    | MajorMinorBuildRevision of major : int * minor : int * build : int * revision : int

/// Which of a `deps.json` asset lists an entry came from.
[<RequireQualifiedAccess>]
type internal DepsAssetType =
    | Runtime
    | Resources
    | Native

/// A `libraries` entry's `type`, as far as `hostpolicy` distinguishes it.
[<RequireQualifiedAccess>]
type internal DepsLibraryType =
    /// `runtimepack`, in any ASCII casing.
    | RuntimePack
    /// Any other type, as written.
    | Other of written : string

/// How the host reads a `deps.json`.
[<RequireQualifiedAccess>]
type internal DepsJsonMode =
    /// Reads `runtimeTargets` as well as `targets`. A library's RID-specific assets of one type,
    /// for the first of `rids` that has any, replace its other assets of that type. The host
    /// reads the application's file this way unless the application is self-contained, and
    /// every framework's file except the lowest framework's.
    | FrameworkDependent of rids : string list
    /// Reads `targets` only. The host reads a self-contained application's file this way, and
    /// the lowest framework's.
    | SelfContained

/// One asset of a `deps.json`, as the host's `deps_entry_t` holds it.
[<NoComparison>]
type internal DepsEntry =
    {
        /// The `libraries` key up to its first `/`, or the whole key if it has none.
        LibraryName : string
        /// The `libraries` key after its first `/`, or the whole key if it has none.
        LibraryVersion : string
        LibraryType : DepsLibraryType
        AssetType : DepsAssetType
        /// The asset's file name without its extension, and without a further `.ni` (in any
        /// ASCII casing). The TPA is keyed by this name.
        AssetName : string
        /// The asset's path as listed, with each `\` read as `/`.
        RelativePath : string
        /// The asset's `localPath`, with each `\` read as `/`; `None` if it is absent, empty or
        /// not a string.
        LocalPath : string option
        AssemblyVersion : DepsVersion
        FileVersion : DepsVersion
        /// Whether the asset came from `runtimeTargets`.
        IsRidSpecific : bool
    }

/// What the host reads from one `deps.json`.
[<NoComparison>]
type internal DepsManifest =
    {
        /// The entries, in the host's order: each `libraries` member in order, and within one
        /// library its runtime, then resources, then native assets, each in the order of the
        /// runtime target. Placeholders are kept.
        Entries : DepsEntry list
        /// The packages for which the host's `deps_json_t::has_package` holds: those of the
        /// runtime target with an asset-type member (even an empty one), and, when reading
        /// `runtimeTargets`, those with RID-specific assets for a listed RID. Whether or not
        /// `libraries` names them. Test one with `DepsManifest.hasPackage`.
        Packages : Set<string>
    }

/// One step into a JSON value, for naming where a `deps.json` was refused.
[<RequireQualifiedAccess>]
type internal DepsJsonPathSegment =
    | Member of name : string
    | Element of index : int

/// The kind of JSON value which the host requires at a place it reads without checking.
[<RequireQualifiedAccess>]
type internal DepsJsonKind =
    | Object
    | String
    | Bool
    | StringOrObject

/// Why `DepsJson.parse` refused a `deps.json`. Each path runs from the document's root.
[<RequireQualifiedAccess>]
type internal DepsJsonError =
    /// The bytes after any UTF-8 byte order mark are not valid UTF-8.
    | InvalidUtf8
    /// System.Text.Json does not read the document as one strict JSON value: for instance it
    /// is empty, malformed, holds a comment or a trailing comma, has content after its root
    /// value, or nests deeper than 64. With the line, and the byte within that line, at which
    /// it stopped, each counted from zero, where the reader reports them.
    | NotStrictJson of line : int64 option * bytePositionInLine : int64 option
    /// A `\u` escape names half of a surrogate pair without the other half, in the string at
    /// `path` or in a member name of the object at `path`.
    | UnpairedSurrogate of path : DepsJsonPathSegment list
    /// The object at `path` has two members called `name`.
    | DuplicateMember of path : DepsJsonPathSegment list * name : string
    /// The string at `path`, or the name of the member at `path`, holds U+0000.
    | NulCharacter of path : DepsJsonPathSegment list
    /// The number at `path` is written with an exponent, or with more than 300 characters.
    | UnreproducibleNumber of path : DepsJsonPathSegment list * text : string
    /// The host reads a member `name` of the object at `path`, and there is none.
    | MissingMember of path : DepsJsonPathSegment list * name : string
    /// The host reads the value at `path` as `expected`, and it is another kind.
    | WrongKind of path : DepsJsonPathSegment list * expected : DepsJsonKind
    /// The version string at `path` has a component above `Int32.MaxValue`.
    | VersionComponentOutOfRange of path : DepsJsonPathSegment list * text : string

[<RequireQualifiedAccess>]
module internal DepsVersion =

    /// The host's four `int` fields, `-1` for each one not read.
    let private fields (v : DepsVersion) : int * int * int * int =
        match v with
        | DepsVersion.Empty -> -1, -1, -1, -1
        | DepsVersion.MajorMinor (major, minor) -> major, minor, -1, -1
        | DepsVersion.MajorMinorBuild (major, minor, build) -> major, minor, build, -1
        | DepsVersion.MajorMinorBuildRevision (major, minor, build, revision) -> major, minor, build, revision

    /// `version_t::compare`: negative, zero or positive as `a` is below, equal to or above `b`.
    let compare (a : DepsVersion) (b : DepsVersion) : int =
        // Field by field, as `version_t::compare` does; tuples of ints compare the same way.
        Operators.compare (fields a) (fields b)

    /// How one component of a version reads.
    [<RequireQualifiedAccess>]
    type private Component =
        | NotANumber
        | Number of int
        | AboveInt32

    /// `try_stou` (`hostmisc/utils.cpp`): fails on an empty string or one holding anything but
    /// ASCII digits; otherwise `std::stoul`.
    let private readComponent (text : string) : Component =
        if text.Length = 0 || text |> Seq.exists (fun c -> c < '0' || c > '9') then
            Component.NotANumber
        else
            let digits = text.TrimStart '0'

            if digits.Length > 10 then
                Component.AboveInt32
            else
                // Ten digits or fewer fit an int64. Accumulated by hand, because .NET's number
                // parsers read the current culture.
                let value =
                    digits
                    |> Seq.fold (fun (acc : int64) (c : char) -> acc * 10L + int64 (int c - int '0')) 0L

                if value > int64 Int32.MaxValue then
                    Component.AboveInt32
                else
                    Component.Number (int value)

    /// `version_t::parse`, which the host applies to a non-empty version string. `Ok
    /// DepsVersion.Empty` when the host's parse fails. `Error ()` when the host would read a
    /// component above `Int32.MaxValue`, which it does not store faithfully.
    let parse (text : string) : Result<DepsVersion, unit> =
        // `parse_internal` (`hostpolicy/version.cpp`) needs a `.` before it reads anything, then
        // reads up to four `.`-separated components in order, stopping at the first that fails.
        // Its fourth component runs to the end of the string, so a fifth makes it fail.
        let parts = text.Split '.'

        if parts.Length < 2 then
            Ok DepsVersion.Empty
        else

        let components =
            if parts.Length <= 4 then
                List.ofArray parts
            else
                List.ofArray parts.[0..2] @ [ String.Join (".", parts.[3..]) ]

        let rec read (acc : int list) (remaining : string list) : Result<int list option, unit> =
            match remaining with
            | [] -> Ok (Some (List.rev acc))
            | head :: tail ->
                match readComponent head with
                | Component.NotANumber -> Ok None
                | Component.AboveInt32 -> Error ()
                | Component.Number n -> read (n :: acc) tail

        match read [] components with
        | Error () -> Error ()
        | Ok None -> Ok DepsVersion.Empty
        | Ok (Some [ major ; minor ]) -> Ok (DepsVersion.MajorMinor (major, minor))
        | Ok (Some [ major ; minor ; build ]) -> Ok (DepsVersion.MajorMinorBuild (major, minor, build))
        | Ok (Some [ major ; minor ; build ; revision ]) ->
            Ok (DepsVersion.MajorMinorBuildRevision (major, minor, build, revision))
        | Ok (Some read) -> failwith $"logic error: read %i{read.Length} version components from %s{text}"

/// Small helpers for the host's string handling, on the platforms PawPrint models, where the
/// directory separator is `/`.
[<RequireQualifiedAccess>]
module private HostStrings =

    let private asciiLower (c : char) : char =
        if c >= 'A' && c <= 'Z' then char (int c + 32) else c

    /// `strcasecmp` in the C locale, as the host compares asset types and suffixes.
    let equalsIgnoringAsciiCase (a : string) (b : string) : bool =
        a.Length = b.Length && Seq.forall2 (fun x y -> asciiLower x = asciiLower y) a b

    /// `get_filename_without_ext` (`hostmisc/utils.cpp`): after the last `/` or `\`, and up to
    /// the last `.` if that comes after it.
    let filenameWithoutExtension (path : string) : string =
        let start = (path.LastIndexOfAny [| '/' ; '\\' |]) + 1
        let dot = path.LastIndexOf '.'

        if dot < start then
            path.Substring start
        else
            path.Substring (start, dot - start)

    /// `get_filename`: after the last `/`.
    let filename (path : string) : string =
        path.Substring ((path.LastIndexOf '/') + 1)

    /// `get_directory`: up to and including the last `/` of the path without its trailing `/`s,
    /// with any `/`s before that one dropped; the whole path and a `/` if it has none.
    let directory (path : string) : string =
        let trimmed = path.TrimEnd '/'

        match trimmed.LastIndexOf '/' with
        | -1 -> trimmed + "/"
        | sep -> trimmed.Substring(0, sep).TrimEnd '/' + "/"

    /// `append_path`: joins with a `/` unless either side is empty or already has one there.
    let appendPath (path : string) (component' : string) : string =
        if component'.Length = 0 then
            path
        elif path.Length = 0 then
            component'
        elif path.EndsWith '/' || component'.StartsWith '/' then
            path + component'
        else
            path + "/" + component'

[<RequireQualifiedAccess>]
module internal DepsEntry =

    /// Whether the host treats the entry as a placeholder, which adds nothing to the TPA.
    let isPlaceholder (entry : DepsEntry) : bool =
        // `resolve_tpa_list` (`hostpolicy/deps_resolver.cpp`) skips an asset whose path ends
        // `/_._`, compared ignoring case; the suffix has no letters.
        entry.RelativePath.EndsWith ("/_._", StringComparison.Ordinal)

    /// The path, relative to the directory of the entry's `deps.json`, at which
    /// `deps_entry_t::to_dir_path` looks for the entry's file. The host probes a framework's
    /// directory this way, and the application's too, except for a RID-specific entry with no
    /// `LocalPath`: that one it looks for at its `RelativePath` (`to_package_path`).
    let dirRelativePath (entry : DepsEntry) : string =
        match entry.LocalPath, entry.LibraryType with
        | Some localPath, _ -> localPath
        // "runtimepack assets set the path to the local path"
        | None, DepsLibraryType.RuntimePack -> entry.RelativePath
        | None, DepsLibraryType.Other _ ->
            let fileName = HostStrings.filename entry.RelativePath

            match entry.AssetType with
            | DepsAssetType.Resources ->
                // Resources are listed as `lib/<tfm>/<culture>/<name>.dll` and looked for as
                // `<culture>/<name>.dll`.
                let directory = HostStrings.directory entry.RelativePath
                // `remove_trailing_dir_separator`: `get_directory` ends with exactly one `/`.
                let culture = HostStrings.filename (directory.Substring (0, directory.Length - 1))

                HostStrings.appendPath culture fileName
            | DepsAssetType.Runtime
            | DepsAssetType.Native -> fileName

[<RequireQualifiedAccess>]
module internal DepsManifest =

    /// `deps_json_t::has_package`: whether the manifest has the package `name/version`. A
    /// framework's entry is found in that framework's directory only if the framework's own
    /// manifest has its `LibraryName/LibraryVersion`.
    let hasPackage (manifest : DepsManifest) (name : string) (version : string) : bool =
        manifest.Packages.Contains (name + "/" + version)

[<RequireQualifiedAccess>]
module internal DepsJson =

    type private Path = DepsJsonPathSegment list

    let private utf8Bom : byte[] = [| 0xEFuy ; 0xBBuy ; 0xBFuy |]

    let private strictUtf8 : UTF8Encoding = UTF8Encoding (false, true)

    let private ofMember (path : Path) (name : string) : Path =
        path @ [ DepsJsonPathSegment.Member name ]

    /// Folds `f` over `xs` until the first error.
    let private traverse (f : 'a -> Result<'b, DepsJsonError>) (xs : 'a list) : Result<'b list, DepsJsonError> =
        let rec go (acc : 'b list) (xs : 'a list) : Result<'b list, DepsJsonError> =
            match xs with
            | [] -> Ok (List.rev acc)
            | x :: rest ->
                match f x with
                | Ok y -> go (y :: acc) rest
                | Error e -> Error e

        go [] xs

    /// A string as `JsonElement` decodes it, or an error for an unpaired surrogate escape.
    let private decode (path : Path) (read : unit -> string) : Result<string, DepsJsonError> =
        let text =
            try
                Some (read ())
            with :? InvalidOperationException ->
                None

        match text with
        | None -> Error (DepsJsonError.UnpairedSurrogate path)
        | Some text when text.Contains '\000' -> Error (DepsJsonError.NulCharacter path)
        | Some text -> Ok text

    /// Checks what the two readers might read differently, everywhere in the document: rapidjson
    /// (with the host's flags) for the host, System.Text.Json for PawPrint.
    let rec private checkReadersAgree (path : Path) (value : JsonElement) : Result<unit, DepsJsonError> =
        match value.ValueKind with
        | JsonValueKind.Object ->
            let seen = HashSet<string> (StringComparer.Ordinal)

            value.EnumerateObject ()
            |> List.ofSeq
            |> traverse (fun property ->
                // rapidjson's `FindMember` finds the first of two members of one name, and its
                // iteration visits both, so the host's reading depends on which way it looks.
                match decode path (fun () -> property.Name) with
                | Error (DepsJsonError.NulCharacter _) ->
                    Error (DepsJsonError.NulCharacter (ofMember path property.Name))
                | Error e -> Error e
                | Ok name when not (seen.Add name) -> Error (DepsJsonError.DuplicateMember (path, name))
                | Ok name -> checkReadersAgree (ofMember path name) property.Value
            )
            |> Result.map ignore<unit list>
        | JsonValueKind.Array ->
            value.EnumerateArray ()
            |> List.ofSeq
            |> List.indexed
            |> traverse (fun (i, element) -> checkReadersAgree (path @ [ DepsJsonPathSegment.Element i ]) element)
            |> Result.map ignore<unit list>
        | JsonValueKind.String -> decode path value.GetString |> Result.map ignore<string>
        | JsonValueKind.Number ->
            // rapidjson fails the whole document with kParseErrorNumberTooBig on an exponent
            // above 308 less the fraction's digits, whatever the value (`0e309`), and on a
            // value past `double`'s range. Without an exponent, 300 characters stay in range.
            let text = value.GetRawText ()

            if text.Contains 'e' || text.Contains 'E' || text.Length > 300 then
                Error (DepsJsonError.UnreproducibleNumber (path, text))
            else
                Ok ()
        | _ -> Ok ()

    /// The member `name` of an object which `checkReadersAgree` has passed, so has at most one.
    let private tryMember (obj : JsonElement) (name : string) : JsonElement option =
        match obj.TryGetProperty name with
        | true, value -> Some value
        | false, _ -> None

    let private requireMember (path : Path) (obj : JsonElement) (name : string) : Result<JsonElement, DepsJsonError> =
        match tryMember obj name with
        | Some value -> Ok value
        | None -> Error (DepsJsonError.MissingMember (path, name))

    let private requireObject (path : Path) (value : JsonElement) : Result<JsonElement, DepsJsonError> =
        if value.ValueKind = JsonValueKind.Object then
            Ok value
        else
            Error (DepsJsonError.WrongKind (path, DepsJsonKind.Object))

    let private requireString (path : Path) (value : JsonElement) : Result<string, DepsJsonError> =
        if value.ValueKind = JsonValueKind.String then
            Ok (value.GetString ())
        else
            Error (DepsJsonError.WrongKind (path, DepsJsonKind.String))

    let private requireBool (path : Path) (value : JsonElement) : Result<bool, DepsJsonError> =
        match value.ValueKind with
        | JsonValueKind.True -> Ok true
        | JsonValueKind.False -> Ok false
        | _ -> Error (DepsJsonError.WrongKind (path, DepsJsonKind.Bool))

    /// A member the host must find, of the kind it must be: absent or another kind is a
    /// pointer the host dereferences without checking.
    let private requireObjectMember
        (path : Path)
        (obj : JsonElement)
        (name : string)
        : Result<JsonElement, DepsJsonError>
        =
        requireMember path obj name |> Result.bind (requireObject (ofMember path name))

    let private requireStringMember (path : Path) (obj : JsonElement) (name : string) : Result<string, DepsJsonError> =
        requireMember path obj name |> Result.bind (requireString (ofMember path name))

    /// The members of an object, in document order.
    let private members (obj : JsonElement) : (string * JsonElement) list =
        obj.EnumerateObject () |> Seq.map (fun p -> p.Name, p.Value) |> List.ofSeq

    /// `get_optional_property`: a string member's value, or empty for a member absent or not a
    /// string.
    let private optionalString (obj : JsonElement) (name : string) : string =
        match tryMember obj name with
        | Some value when value.ValueKind = JsonValueKind.String -> value.GetString ()
        | _ -> ""

    /// The asset types, in the order the host lists them (`s_known_asset_types`).
    let private assetTypes : (string * DepsAssetType) list =
        [
            "runtime", DepsAssetType.Runtime
            "resources", DepsAssetType.Resources
            "native", DepsAssetType.Native
        ]

    /// One file of an asset list, as `deps_asset_t` holds it.
    type private Asset =
        {
            Name : string
            RelativePath : string
            LocalPath : string option
            AssemblyVersion : DepsVersion
            FileVersion : DepsVersion
        }

    let private readVersion (path : Path) (file : JsonElement) (name : string) : Result<DepsVersion, DepsJsonError> =
        match optionalString file name with
        | "" -> Ok DepsVersion.Empty
        | text ->
            match DepsVersion.parse text with
            | Ok version -> Ok version
            | Error () -> Error (DepsJsonError.VersionComponentOutOfRange (ofMember path name, text))

    /// `deps_asset_t`'s constructor, from a file's key and value (`process_targets`,
    /// `process_runtime_targets`).
    let private readAsset (path : Path) (key : string) (file : JsonElement) : Result<Asset, DepsJsonError> =
        readVersion path file "assemblyVersion"
        |> Result.bind (fun assemblyVersion ->
            readVersion path file "fileVersion"
            |> Result.map (fun fileVersion ->
                // `reconcile_libraries_with_targets` strips a further `.ni`, in any ASCII case.
                let name =
                    let name = HostStrings.filenameWithoutExtension key

                    if
                        name.Length >= 3
                        && HostStrings.equalsIgnoringAsciiCase (name.Substring (name.Length - 3)) ".ni"
                    then
                        name.Substring (0, name.Length - 3)
                    else
                        name

                {
                    Name = name
                    // "Deps file does not follow spec. It uses '\\', should use '/'"
                    // (`deps_entry.h`).
                    RelativePath = key.Replace ('\\', '/')
                    LocalPath =
                        match optionalString file "localPath" with
                        | "" -> None
                        | localPath -> Some (localPath.Replace ('\\', '/'))
                    AssemblyVersion = assemblyVersion
                    FileVersion = fileVersion
                }
            )
        )

    /// `process_targets`: for each package of the target, its assets of each type, for each
    /// asset-type member it has (even an empty one).
    let private processTargets
        (targetPath : Path)
        (packages : (string * JsonElement) list)
        : Result<Map<string, Map<DepsAssetType, Asset list>>, DepsJsonError>
        =
        packages
        |> traverse (fun (package, value) ->
            let packagePath = ofMember targetPath package

            requireObject packagePath value
            |> Result.bind (fun value ->
                assetTypes
                |> List.choose (fun (typeName, assetType) ->
                    tryMember value typeName |> Option.map (fun files -> typeName, assetType, files)
                )
                |> traverse (fun (typeName, assetType, files) ->
                    let filesPath = ofMember packagePath typeName

                    requireObject filesPath files
                    |> Result.bind (fun files ->
                        members files
                        |> traverse (fun (key, file) ->
                            let filePath = ofMember filesPath key
                            requireObject filePath file |> Result.bind (readAsset filePath key)
                        )
                    )
                    |> Result.map (fun assets -> assetType, assets)
                )
                |> Result.map (fun byType -> package, byType)
            )
        )
        |> Result.map (fun packages ->
            packages
            |> List.filter (fun (_, byType) -> not byType.IsEmpty)
            |> List.map (fun (package, byType) -> package, Map.ofList byType)
            |> Map.ofList
        )

    /// `process_runtime_targets` and `perform_rid_fallback`: for each package of the target
    /// with a `runtimeTargets` file of a type the host knows, and each such type, the files of
    /// the first of `rids` which has any of that type, or none if no listed RID has any.
    let private processRuntimeTargets
        (rids : string list)
        (targetPath : Path)
        (packages : (string * JsonElement) list)
        : Result<Map<string, Map<DepsAssetType, Asset list>>, DepsJsonError>
        =
        packages
        |> traverse (fun (package, value) ->
            let packagePath = ofMember targetPath package

            requireObject packagePath value
            |> Result.bind (fun value ->
                match tryMember value "runtimeTargets" with
                | None -> Ok (package, [])
                | Some runtimeTargets ->
                    let runtimeTargetsPath = ofMember packagePath "runtimeTargets"

                    requireObject runtimeTargetsPath runtimeTargets
                    |> Result.bind (fun runtimeTargets ->
                        members runtimeTargets
                        |> traverse (fun (key, file) ->
                            let filePath = ofMember runtimeTargetsPath key

                            requireObject filePath file
                            |> Result.bind (fun file ->
                                requireStringMember filePath file "assetType"
                                |> Result.bind (fun assetType ->
                                    match
                                        assetTypes
                                        |> List.tryFind (fun (typeName, _) ->
                                            HostStrings.equalsIgnoringAsciiCase typeName assetType
                                        )
                                    with
                                    | None -> Ok None
                                    | Some (_, assetType) ->
                                        readAsset filePath key file
                                        |> Result.bind (fun asset ->
                                            requireStringMember filePath file "rid"
                                            |> Result.map (fun rid -> Some (assetType, rid, asset))
                                        )
                                )
                            )
                        )
                        |> Result.map (fun files -> package, List.choose id files)
                    )
            )
        )
        |> Result.map (fun packages ->
            packages
            |> List.filter (fun (_, files) -> not files.IsEmpty)
            |> List.map (fun (package, files) ->
                let selected =
                    assetTypes
                    |> List.choose (fun (_, assetType) ->
                        let ofType = files |> List.filter (fun (t, _, _) -> t = assetType)

                        rids
                        |> List.tryFind (fun rid -> ofType |> List.exists (fun (_, r, _) -> r = rid))
                        |> Option.map (fun rid ->
                            assetType,
                            ofType
                            |> List.filter (fun (_, r, _) -> r = rid)
                            |> List.map (fun (_, _, asset) -> asset)
                        )
                    )

                package, Map.ofList selected
            )
            |> Map.ofList
        )

    /// The runtime target's name: `runtimeTarget` itself if it is a string, else its `name`.
    let private runtimeTargetName (root : JsonElement) : Result<string, DepsJsonError> =
        match tryMember root "runtimeTarget" with
        | None -> Error (DepsJsonError.MissingMember ([], "runtimeTarget"))
        | Some value ->
            let path = [ DepsJsonPathSegment.Member "runtimeTarget" ]

            match value.ValueKind with
            | JsonValueKind.String -> Ok (value.GetString ())
            | JsonValueKind.Object -> requireStringMember path value "name"
            | _ -> Error (DepsJsonError.WrongKind (path, DepsJsonKind.StringOrObject))

    /// `reconcile_libraries_with_targets`: each library in order which has assets, and its
    /// assets of each type.
    let private reconcile
        (runtime : EmulatedRuntime)
        (root : JsonElement)
        (hasAssets : string -> bool)
        (assetsOf : string -> DepsAssetType -> Asset list * bool)
        : Result<DepsEntry list, DepsJsonError>
        =
        let librariesPath = [ DepsJsonPathSegment.Member "libraries" ]

        requireObjectMember [] root "libraries"
        |> Result.bind (fun libraries ->
            members libraries
            |> List.filter (fun (key, _) -> hasAssets key)
            |> traverse (fun (key, library) ->
                let libraryPath = ofMember librariesPath key

                requireObject libraryPath library
                |> Result.bind (fun library ->
                    // The host reads `sha512` and `serviceable` without checking them, though
                    // nothing PawPrint models depends on their values. .NET 11's hostpolicy no
                    // longer reads `sha512`.
                    let sha512 =
                        match runtime with
                        | EmulatedRuntime.Net10 ->
                            requireStringMember libraryPath library "sha512" |> Result.map ignore
                        | EmulatedRuntime.Net11 -> Ok ()

                    sha512
                    |> Result.bind (fun () -> requireMember libraryPath library "serviceable")
                    |> Result.bind (requireBool (ofMember libraryPath "serviceable"))
                    |> Result.bind (fun _ -> requireStringMember libraryPath library "type")
                    |> Result.map (fun libraryType ->
                        let libraryType =
                            if HostStrings.equalsIgnoringAsciiCase libraryType "runtimepack" then
                                DepsLibraryType.RuntimePack
                            else
                                DepsLibraryType.Other libraryType

                        // `lib_name.substr (pos + 1)` with `pos = npos` is the whole key.
                        let libraryName, libraryVersion =
                            match key.IndexOf '/' with
                            | -1 -> key, key
                            | slash -> key.Substring (0, slash), key.Substring (slash + 1)

                        [
                            for _, assetType in assetTypes do
                                let assets, ridSpecific = assetsOf key assetType

                                for asset in assets do
                                    {
                                        LibraryName = libraryName
                                        LibraryVersion = libraryVersion
                                        LibraryType = libraryType
                                        AssetType = assetType
                                        AssetName = asset.Name
                                        RelativePath = asset.RelativePath
                                        LocalPath = asset.LocalPath
                                        AssemblyVersion = asset.AssemblyVersion
                                        FileVersion = asset.FileVersion
                                        IsRidSpecific = ridSpecific
                                    }
                        ]
                    )
                )
            )
            |> Result.map List.concat
        )

    let private assetsOfType
        (byPackage : Map<string, Map<DepsAssetType, Asset list>>)
        (package : string)
        (assetType : DepsAssetType)
        : Asset list
        =
        byPackage
        |> Map.tryFind package
        |> Option.bind (Map.tryFind assetType)
        |> Option.defaultValue []

    /// `load_framework_dependent` and `load_self_contained`.
    let private load
        (runtime : EmulatedRuntime)
        (mode : DepsJsonMode)
        (root : JsonElement)
        : Result<DepsManifest, DepsJsonError>
        =
        runtimeTargetName root
        |> Result.bind (fun target ->
            let targetsPath = [ DepsJsonPathSegment.Member "targets" ]
            let targetPath = ofMember targetsPath target

            requireObjectMember [] root "targets"
            |> Result.bind (fun targets -> requireObjectMember targetsPath targets target)
            |> Result.bind (fun target ->
                let packages = members target

                match mode with
                | DepsJsonMode.SelfContained ->
                    processTargets targetPath packages
                    |> Result.bind (fun assets ->
                        reconcile
                            runtime
                            root
                            assets.ContainsKey
                            (fun package assetType -> assetsOfType assets package assetType, false)
                        |> Result.map (fun entries ->
                            {
                                Entries = entries
                                Packages = assets.Keys |> Set.ofSeq
                            }
                        )
                    )
                | DepsJsonMode.FrameworkDependent rids ->
                    processRuntimeTargets rids targetPath packages
                    |> Result.bind (fun ridAssets ->
                        processTargets targetPath packages
                        |> Result.bind (fun assets ->
                            let hasAssets (package : string) : bool =
                                assets.ContainsKey package || ridAssets.ContainsKey package

                            // A library's RID-specific assets of one type, if any survived RID
                            // selection, replace its other assets of that type.
                            let assetsOf (package : string) (assetType : DepsAssetType) : Asset list * bool =
                                match assetsOfType ridAssets package assetType with
                                | [] -> assetsOfType assets package assetType, false
                                | ridSpecific -> ridSpecific, true

                            // `has_package` asks for RID-specific assets which survived RID
                            // selection, unlike `hasAssets`.
                            let packages =
                                Seq.append
                                    assets.Keys
                                    (ridAssets |> Map.filter (fun _ byType -> not byType.IsEmpty) |> Map.keys)
                                |> Set.ofSeq

                            reconcile runtime root hasAssets assetsOf
                            |> Result.map (fun entries ->
                                {
                                    Entries = entries
                                    Packages = packages
                                }
                            )
                        )
                    )
            )
        )

    /// Read a `deps.json` as `runtime`'s hostpolicy does (`deps_json_t::load`).
    ///
    /// Refuses a document which the host would refuse, or on which its behaviour is undefined
    /// (a member it reads without checking is absent or of another kind), or which rapidjson
    /// and System.Text.Json might read differently.
    let parse
        (runtime : EmulatedRuntime)
        (mode : DepsJsonMode)
        (contents : byte[])
        : Result<DepsManifest, DepsJsonError>
        =
        // `json_parser_t::parse_file` skips one UTF-8 byte order mark.
        let body =
            if contents.Length >= 3 && contents.[0..2] = utf8Bom then
                ReadOnlyMemory<byte> (contents, 3, contents.Length - 3)
            else
                ReadOnlyMemory<byte> contents

        // rapidjson, without kParseValidateEncodingFlag, passes invalid UTF-8 through; .NET's
        // decoders do not agree with it on what to substitute.
        let validUtf8 =
            try
                strictUtf8.GetCharCount body.Span |> ignore<int>
                true
            with :? DecoderFallbackException ->
                false

        if not validUtf8 then
            Error DepsJsonError.InvalidUtf8
        else

        let document =
            try
                // The defaults refuse comments, trailing commas, content after the root value and
                // nesting deeper than 64.
                Ok (JsonDocument.Parse body)
            with :? JsonException as e ->
                Error (
                    DepsJsonError.NotStrictJson (Option.ofNullable e.LineNumber, Option.ofNullable e.BytePositionInLine)
                )

        match document with
        | Error e -> Error e
        | Ok document ->

        use document = document
        let root = document.RootElement

        checkReadersAgree [] root
        |> Result.bind (fun () -> requireObject [] root)
        |> Result.bind (load runtime mode)
