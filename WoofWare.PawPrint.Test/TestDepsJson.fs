namespace WoofWare.PawPrint.Test

open System
open System.IO
open System.Text
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// `DepsJson.parse` against the host's reading of a `deps.json` (`deps_json_t::load` in
/// `src/native/corehost/hostpolicy/deps_format.cpp`), through a model: a generated description
/// of a manifest is rendered to JSON, parsed, and compared with the entries which the model says
/// the host reads from it.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestDepsJson =

    /// `shouldEqual`, but naming both values on failure: NUnit prints an F# `Result` as its type.
    let private shouldBe<'a when 'a : equality> (expected : 'a) (actual : 'a) : unit =
        if actual <> expected then
            Assert.Fail $"Expected:\n%A{expected}\nActual:\n%A{actual}"

    // ---------------------------------------------------------------------------------------
    // Rendering JSON.

    [<RequireQualifiedAccess>]
    type private Json =
        | Object of (string * Json) list
        | String of string
        /// Written as is: a number, `true`, `false`, `null`, or a small array or object.
        | Literal of string

    let private renderString (escapeAll : bool) (s : string) : string =
        let sb = StringBuilder ()
        sb.Append '"' |> ignore<StringBuilder>

        for c in s do
            if c = '"' then
                sb.Append "\\\"" |> ignore<StringBuilder>
            elif c = '\\' then
                sb.Append "\\\\" |> ignore<StringBuilder>
            elif c < ' ' || (escapeAll && not (Char.IsAsciiLetterOrDigit c)) then
                sb.Append("\\u").Append ((int c).ToString "X4") |> ignore<StringBuilder>
            else
                sb.Append c |> ignore<StringBuilder>

        sb.Append '"' |> ignore<StringBuilder>
        sb.ToString ()

    let rec private render (escapeAll : bool) (indent : bool) (depth : int) (json : Json) : string =
        match json with
        | Json.Literal text -> text
        | Json.String s -> renderString escapeAll s
        | Json.Object members ->
            let newline = if indent then "\n" else ""

            let pad (d : int) : string =
                if indent then String.replicate d "  " else ""

            let body =
                members
                |> List.map (fun (name, value) ->
                    pad (depth + 1)
                    + renderString escapeAll name
                    + ": "
                    + render escapeAll indent (depth + 1) value
                )
                |> String.concat ("," + newline)

            "{" + newline + body + newline + pad depth + "}"

    // ---------------------------------------------------------------------------------------
    // The model.

    /// A directory prefix of an asset's path: as written; as the host reads it, with `\` as `/`;
    /// and the last directory name, which the host takes as a resource asset's culture (`None`
    /// when there is no directory).
    type private DirSpec =
        {
            Written : string
            Read : string
            Culture : string option
        }

    let private dirs : DirSpec list =
        let same (d : string) (culture : string) : DirSpec =
            {
                Written = d
                Read = d
                Culture = Some culture
            }

        [
            {
                Written = ""
                Read = ""
                Culture = None
            }
            same "lib/net10.0/" "net10.0"
            same "lib/netstandard2.0/de/" "de"
            same "runtimes/unix/lib/net8.0/" "net8.0"
            {
                Written = "lib\\net8.0\\fr\\"
                Read = "lib/net8.0/fr/"
                Culture = Some "fr"
            }
            same "./" "."
            same "a.b/" "a.b"
            same "lib//de/" "de"
            same "/abs/" "abs"
        ]

    /// File names before their extension. `X.ni` loses its `.ni` as a TPA name; `Z.nı` (dotless
    /// i) does not.
    let private stems : string list =
        [ "A" ; "B.C" ; "System.Foo" ; "X.ni" ; "Y.NI" ; "Z.nı" ; "_" ]

    /// Extensions, each with the part before its last `.`-segment.
    let private extensions : (string * string) list =
        [
            ".dll", ""
            ".ni.dll", ".ni"
            ".NI.dll", ".NI"
            ".exe", ""
            ".so", ""
            "._", ""
            ".resources.dll", ".resources"
        ]

    [<RequireQualifiedAccess>]
    type private VersionSpec =
        | Absent
        | NotAString of literal : string
        | Text of written : string * expected : DepsVersion
        /// A version the host reads with a component above `Int32.MaxValue`.
        | OutOfRange of written : string

    [<RequireQualifiedAccess>]
    type private LocalPathSpec =
        | Absent
        | NotAString of literal : string
        | Text of written : string * expected : string option

    type private FileSpec =
        {
            Dir : DirSpec
            Stem : string
            Extension : string
            /// `Extension` without its last `.`-segment.
            ExtensionInner : string
            AssemblyVersion : VersionSpec
            FileVersion : VersionSpec
            LocalPath : LocalPathSpec
        }

    let private fileKey (f : FileSpec) : string = f.Dir.Written + f.Stem + f.Extension

    type private RidFileSpec =
        {
            File : FileSpec
            /// `None`: no `rid` member.
            Rid : string option
            /// `None`: no `assetType` member. Otherwise as written, and the type it names to the
            /// host, if any.
            AssetType : (string * DepsAssetType option) option
        }

    type private PackageSpec =
        {
            Name : string
            Version : string option
            /// The asset-type members present, in the order written.
            Assets : (DepsAssetType * FileSpec list) list
            RuntimeTargets : RidFileSpec list option
            Dependencies : bool
            /// Also a member `Runtime`, which the host does not read.
            WrongCaseMember : bool
        }

    let private key (name : string) (version : string option) : string =
        match version with
        | Some v -> name + "/" + v
        | None -> name

    let private packageKey (p : PackageSpec) : string = key p.Name p.Version

    [<RequireQualifiedAccess>]
    type private BadLibrary =
        | NotAnObject
        | NoSha512
        | Sha512NotAString
        | NoServiceable
        | ServiceableNotABool
        | NoType
        | TypeNotAString

    [<RequireQualifiedAccess>]
    type private LibraryValue =
        | Proper of typeWritten : string * expected : DepsLibraryType * serviceable : bool * withPaths : bool
        /// Not an object. Only for a key which names no package of the runtime target.
        | Junk of literal : string
        | Bad of BadLibrary

    type private LibrarySpec =
        {
            Name : string
            Version : string option
            Value : LibraryValue
        }

    type private DocSpec =
        {
            Mode : DepsJsonMode
            TargetName : string
            RuntimeTargetAsObject : bool
            Packages : PackageSpec list
            Libraries : LibrarySpec list
            /// Another target, which the host never reads.
            Decoy : (string * PackageSpec list) option
            DecoyFirst : bool
            /// A permutation of the root members.
            RootOrder : int list
            Bom : bool
            Indent : bool
            EscapeAll : bool
        }

    let private typeName (t : DepsAssetType) : string =
        match t with
        | DepsAssetType.Runtime -> "runtime"
        | DepsAssetType.Resources -> "resources"
        | DepsAssetType.Native -> "native"

    // ---------------------------------------------------------------------------------------
    // Rendering the model.

    let private versionMember (name : string) (v : VersionSpec) : (string * Json) list =
        match v with
        | VersionSpec.Absent -> []
        | VersionSpec.NotAString literal -> [ name, Json.Literal literal ]
        | VersionSpec.Text (written, _) -> [ name, Json.String written ]
        | VersionSpec.OutOfRange written -> [ name, Json.String written ]

    let private localPathMember (l : LocalPathSpec) : (string * Json) list =
        match l with
        | LocalPathSpec.Absent -> []
        | LocalPathSpec.NotAString literal -> [ "localPath", Json.Literal literal ]
        | LocalPathSpec.Text (written, _) -> [ "localPath", Json.String written ]

    let private fileMembers (f : FileSpec) : (string * Json) list =
        versionMember "assemblyVersion" f.AssemblyVersion
        @ versionMember "fileVersion" f.FileVersion
        @ localPathMember f.LocalPath

    let private renderPackage (p : PackageSpec) : Json =
        let assets =
            p.Assets
            |> List.map (fun (t, files) ->
                typeName t, Json.Object (files |> List.map (fun f -> fileKey f, Json.Object (fileMembers f)))
            )

        let runtimeTargets =
            match p.RuntimeTargets with
            | None -> []
            | Some files ->
                let file (rf : RidFileSpec) : string * Json =
                    let rid =
                        match rf.Rid with
                        | None -> []
                        | Some rid -> [ "rid", Json.String rid ]

                    let assetType =
                        match rf.AssetType with
                        | None -> []
                        | Some (written, _) -> [ "assetType", Json.String written ]

                    fileKey rf.File, Json.Object (rid @ assetType @ fileMembers rf.File)

                [ "runtimeTargets", Json.Object (files |> List.map file) ]

        let dependencies =
            if p.Dependencies then
                [ "dependencies", Json.Object [ "Other", Json.String "1.0.0" ] ]
            else
                []

        let wrongCase =
            if p.WrongCaseMember then
                [ "Runtime", Json.Object [ "Decoy.dll", Json.Object [] ] ]
            else
                []

        Json.Object (dependencies @ assets @ wrongCase @ runtimeTargets)

    let private renderLibrary (l : LibrarySpec) : string * Json =
        let proper (typeWritten : string) (serviceable : bool) (withPaths : bool) : (string * Json) list =
            [
                "type", Json.String typeWritten
                "serviceable", Json.Literal (if serviceable then "true" else "false")
                "sha512", Json.String "sha512-AAAA"
            ]
            @ (if withPaths then
                   [
                       "path", Json.String "a/1.0.0"
                       "hashPath", Json.String "a.1.0.0.nupkg.sha512"
                   ]
               else
                   [])

        let value =
            match l.Value with
            | LibraryValue.Proper (typeWritten, _, serviceable, withPaths) ->
                Json.Object (proper typeWritten serviceable withPaths)
            | LibraryValue.Junk literal -> Json.Literal literal
            | LibraryValue.Bad bad ->
                let without (name : string) =
                    proper "package" false false |> List.filter (fun (n, _) -> n <> name)

                let replace (name : string) (value : Json) =
                    proper "package" false false
                    |> List.map (fun (n, v) -> if n = name then (n, value) else (n, v))

                match bad with
                | BadLibrary.NotAnObject -> Json.Literal """["package"]"""
                | BadLibrary.NoSha512 -> Json.Object (without "sha512")
                | BadLibrary.Sha512NotAString -> Json.Object (replace "sha512" (Json.Literal "null"))
                | BadLibrary.NoServiceable -> Json.Object (without "serviceable")
                | BadLibrary.ServiceableNotABool -> Json.Object (replace "serviceable" (Json.String "true"))
                | BadLibrary.NoType -> Json.Object (without "type")
                | BadLibrary.TypeNotAString -> Json.Object (replace "type" (Json.Literal "1"))

        key l.Name l.Version, value

    let private renderDoc (doc : DocSpec) : byte[] =
        let runtimeTarget =
            if doc.RuntimeTargetAsObject then
                Json.Object [ "name", Json.String doc.TargetName ; "signature", Json.String "" ]
            else
                Json.String doc.TargetName

        let renderTarget (packages : PackageSpec list) : Json =
            Json.Object (packages |> List.map (fun p -> packageKey p, renderPackage p))

        let main = doc.TargetName, renderTarget doc.Packages

        let targets =
            match doc.Decoy with
            | None -> [ main ]
            | Some (name, packages) ->
                let decoy = name, renderTarget packages

                if doc.DecoyFirst then
                    [ decoy ; main ]
                else
                    [ main ; decoy ]

        let rootMembers =
            [|
                "runtimeTarget", runtimeTarget
                "compilationOptions", Json.Object []
                "targets", Json.Object targets
                "libraries", Json.Object (doc.Libraries |> List.map renderLibrary)
            |]

        let root = Json.Object (doc.RootOrder |> List.map (fun i -> rootMembers.[i]))
        let text = render doc.EscapeAll doc.Indent 0 root
        let body = Encoding.UTF8.GetBytes text

        if doc.Bom then
            Array.append [| 0xEFuy ; 0xBBuy ; 0xBFuy |] body
        else
            body

    // ---------------------------------------------------------------------------------------
    // What the host reads from the model.

    let private isNiSuffix (s : string) : bool =
        s.Length >= 3
        && s.[s.Length - 3] = '.'
        && (s.[s.Length - 2] = 'n' || s.[s.Length - 2] = 'N')
        && (s.[s.Length - 1] = 'i' || s.[s.Length - 1] = 'I')

    let private expectedAssetName (f : FileSpec) : string =
        let stem = f.Stem + f.ExtensionInner

        if isNiSuffix stem then
            stem.Substring (0, stem.Length - 3)
        else
            stem

    let private expectedVersion (v : VersionSpec) : DepsVersion =
        match v with
        | VersionSpec.Absent
        | VersionSpec.NotAString _ -> DepsVersion.Empty
        | VersionSpec.Text (_, expected) -> expected
        | VersionSpec.OutOfRange written -> failwith $"model error: an entry has the out-of-range version %s{written}"

    let private expectedLocalPath (l : LocalPathSpec) : string option =
        match l with
        | LocalPathSpec.Absent
        | LocalPathSpec.NotAString _ -> None
        | LocalPathSpec.Text (_, expected) -> expected

    let private keyParts (name : string) (version : string option) : string * string =
        match version with
        | Some v -> name, v
        | None -> name, name

    let private isFrameworkDependent (mode : DepsJsonMode) : bool =
        match mode with
        | DepsJsonMode.FrameworkDependent _ -> true
        | DepsJsonMode.SelfContained -> false

    /// Whether the host reads the package's library: it has an asset-type member in `targets`,
    /// or, when reading `runtimeTargets`, a file there of a type the host knows.
    let private packageExists (mode : DepsJsonMode) (p : PackageSpec) : bool =
        let inRuntimeTargets =
            match p.RuntimeTargets with
            | Some files ->
                files
                |> List.exists (fun rf ->
                    match rf.AssetType with
                    | Some (_, Some _) -> true
                    | _ -> false
                )
            | None -> false

        not p.Assets.IsEmpty || (isFrameworkDependent mode && inRuntimeTargets)

    let private mainPackage (doc : DocSpec) (l : LibrarySpec) : PackageSpec option =
        doc.Packages |> List.tryFind (fun p -> packageKey p = key l.Name l.Version)

    /// For each asset type, the RID-specific files that win, if any: those of the first listed
    /// RID which has files of that type.
    let private ridChoice
        (mode : DepsJsonMode)
        (p : PackageSpec)
        (t : DepsAssetType)
        : (string * FileSpec list) option
        =
        match mode, p.RuntimeTargets with
        | DepsJsonMode.SelfContained, _
        | _, None -> None
        | DepsJsonMode.FrameworkDependent rids, Some files ->
            let ofType =
                files
                |> List.filter (fun rf ->
                    match rf.AssetType with
                    | Some (_, Some t') -> t' = t
                    | _ -> false
                )

            rids
            |> List.tryFind (fun rid -> ofType |> List.exists (fun rf -> rf.Rid = Some rid))
            |> Option.map (fun rid ->
                rid,
                ofType
                |> List.filter (fun rf -> rf.Rid = Some rid)
                |> List.map (fun rf -> rf.File)
            )

    let private expectedDirRelativePath (libType : DepsLibraryType) (t : DepsAssetType) (f : FileSpec) : string =
        match expectedLocalPath f.LocalPath, libType with
        | Some local, _ -> local
        | None, DepsLibraryType.RuntimePack -> f.Dir.Read + f.Stem + f.Extension
        | None, DepsLibraryType.Other _ ->
            let fileName = f.Stem + f.Extension

            match t with
            | DepsAssetType.Resources ->
                match f.Dir.Culture with
                | Some culture -> culture + "/" + fileName
                | None -> fileName + "/" + fileName
            | DepsAssetType.Runtime
            | DepsAssetType.Native -> fileName

    let private member' (name : string) : DepsJsonPathSegment = DepsJsonPathSegment.Member name

    let private expected (doc : DocSpec) : Result<(DepsEntry * bool * string) list, DepsJsonError> =
        let fd = isFrameworkDependent doc.Mode

        let packagePath (p : PackageSpec) : DepsJsonPathSegment list =
            [ member' "targets" ; member' doc.TargetName ; member' (packageKey p) ]

        let versionFault (path : DepsJsonPathSegment list) (v : VersionSpec) : DepsJsonError option =
            match v with
            | VersionSpec.OutOfRange written -> Some (DepsJsonError.VersionComponentOutOfRange (path, written))
            | _ -> None

        let fileFaults (path : DepsJsonPathSegment list) (f : FileSpec) : DepsJsonError list =
            [
                versionFault (path @ [ member' "assemblyVersion" ]) f.AssemblyVersion
                versionFault (path @ [ member' "fileVersion" ]) f.FileVersion
            ]
            |> List.choose id

        let assetFaults =
            [
                for p in doc.Packages do
                    for t, files in p.Assets do
                        for f in files do
                            yield! fileFaults (packagePath p @ [ member' (typeName t) ; member' (fileKey f) ]) f
            ]

        let ridFaults =
            [
                if fd then
                    for p in doc.Packages do
                        for rf in Option.defaultValue [] p.RuntimeTargets do
                            let path = packagePath p @ [ member' "runtimeTargets" ; member' (fileKey rf.File) ]

                            match rf.AssetType with
                            | None -> yield DepsJsonError.MissingMember (path, "assetType")
                            | Some (_, None) -> ()
                            | Some (_, Some _) ->
                                yield! fileFaults path rf.File

                                if rf.Rid.IsNone then
                                    yield DepsJsonError.MissingMember (path, "rid")
            ]

        let libraryFaults =
            [
                for l in doc.Libraries do
                    match mainPackage doc l with
                    | Some p when packageExists doc.Mode p ->
                        let path = [ member' "libraries" ; member' (key l.Name l.Version) ]

                        match l.Value with
                        | LibraryValue.Bad bad ->
                            match bad with
                            | BadLibrary.NotAnObject -> DepsJsonError.WrongKind (path, DepsJsonKind.Object)
                            | BadLibrary.NoSha512 -> DepsJsonError.MissingMember (path, "sha512")
                            | BadLibrary.Sha512NotAString ->
                                DepsJsonError.WrongKind (path @ [ member' "sha512" ], DepsJsonKind.String)
                            | BadLibrary.NoServiceable -> DepsJsonError.MissingMember (path, "serviceable")
                            | BadLibrary.ServiceableNotABool ->
                                DepsJsonError.WrongKind (path @ [ member' "serviceable" ], DepsJsonKind.Bool)
                            | BadLibrary.NoType -> DepsJsonError.MissingMember (path, "type")
                            | BadLibrary.TypeNotAString ->
                                DepsJsonError.WrongKind (path @ [ member' "type" ], DepsJsonKind.String)
                        | LibraryValue.Proper _ -> ()
                        | LibraryValue.Junk _ -> failwith "model error: junk library value for a package the host reads"
                    | _ -> ()
            ]

        match ridFaults @ assetFaults @ libraryFaults with
        | [ fault ] -> Error fault
        | _ :: _ :: _ as faults -> failwith $"model error: more than one fault the host reads: %A{faults}"
        | [] ->

        [
            for l in doc.Libraries do
                match mainPackage doc l with
                | Some p when packageExists doc.Mode p ->
                    let libType =
                        match l.Value with
                        | LibraryValue.Proper (_, t, _, _) -> t
                        | v -> failwith $"model error: library value %A{v} survived the fault check"

                    let name, version = keyParts l.Name l.Version

                    for t in [ DepsAssetType.Runtime ; DepsAssetType.Resources ; DepsAssetType.Native ] do
                        let files, ridSpecific =
                            match ridChoice doc.Mode p t with
                            | Some (_, files) -> files, true
                            | None ->
                                p.Assets
                                |> List.tryFind (fun (t', _) -> t' = t)
                                |> Option.map snd
                                |> Option.defaultValue [],
                                false

                        for f in files do
                            let entry =
                                {
                                    LibraryName = name
                                    LibraryVersion = version
                                    LibraryType = libType
                                    AssetType = t
                                    AssetName = expectedAssetName f
                                    RelativePath = f.Dir.Read + f.Stem + f.Extension
                                    LocalPath = expectedLocalPath f.LocalPath
                                    AssemblyVersion = expectedVersion f.AssemblyVersion
                                    FileVersion = expectedVersion f.FileVersion
                                    IsRidSpecific = ridSpecific
                                }

                            let placeholder = f.Stem = "_" && f.Extension = "._" && f.Dir.Written <> ""
                            yield entry, placeholder, expectedDirRelativePath libType t f
                | _ -> ()
        ]
        |> Ok

    // ---------------------------------------------------------------------------------------
    // Generators.

    let private versionOf (components : int list) : DepsVersion =
        match components with
        | [ a ; b ] -> DepsVersion.MajorMinor (a, b)
        | [ a ; b ; c ] -> DepsVersion.MajorMinorBuild (a, b, c)
        | [ a ; b ; c ; d ] -> DepsVersion.MajorMinorBuildRevision (a, b, c, d)
        | _ -> failwith $"model error: %i{components.Length} components"

    /// Strings the host's `version_t::parse` fails on, leaving the version empty, including
    /// some whose components would be out of range if the host read that far.
    let private malformedVersions : string list =
        [
            "1"
            "1."
            ".1"
            "1..2"
            "1.2.3.4.5"
            "1.x"
            "v1.0"
            " 1.0"
            "1.0 "
            "-1.0"
            "+1.0"
            "1.2.3."
            "١.٢"
            "99999999999"
            "x.99999999999"
            "1.2.3.4.99999999999"
        ]

    let private outOfRangeVersions : string list =
        [
            "2147483648.0"
            "1.4294967296"
            "0.0.99999999999999999999999"
            "1.2.3.2147483648"
        ]

    let private notAStringLiterals : string list =
        [ "1" ; "true" ; "null" ; "{}" ; "[]" ]

    let private genVersion : Gen<VersionSpec> =
        Gen.frequency
            [
                2, Gen.constant VersionSpec.Absent
                1, Gen.elements notAStringLiterals |> Gen.map VersionSpec.NotAString
                5,
                gen {
                    let! count = Gen.choose (2, 4)
                    let! components = Gen.listOfLength count (Gen.choose (0, 20))
                    let written = components |> List.map string<int> |> String.concat "."
                    return VersionSpec.Text (written, versionOf components)
                }
                1,
                gen {
                    let! count = Gen.choose (2, 4)
                    let! components = Gen.listOfLength count (Gen.choose (0, 20))

                    let written =
                        components |> List.map (fun c -> "0" + string<int> c) |> String.concat "."

                    return VersionSpec.Text (written, versionOf components)
                }
                2,
                Gen.elements malformedVersions
                |> Gen.map (fun t -> VersionSpec.Text (t, DepsVersion.Empty))
                1, Gen.constant (VersionSpec.Text ("", DepsVersion.Empty))
            ]

    let private genLocalPath : Gen<LocalPathSpec> =
        Gen.frequency
            [
                4, Gen.constant LocalPathSpec.Absent
                1, Gen.elements notAStringLiterals |> Gen.map LocalPathSpec.NotAString
                1, Gen.constant (LocalPathSpec.Text ("", None))
                3,
                Gen.elements
                    [
                        "X.dll", "X.dll"
                        "sub\\Y.dll", "sub/Y.dll"
                        "../common/Z.dll", "../common/Z.dll"
                        "runtimes/unix/lib/net8.0/W.dll", "runtimes/unix/lib/net8.0/W.dll"
                    ]
                |> Gen.map (fun (written, read) -> LocalPathSpec.Text (written, Some read))
            ]

    let private genFile : Gen<FileSpec> =
        gen {
            let! dir = Gen.elements dirs

            let! stem, (extension, inner) =
                Gen.frequency
                    [
                        9, Gen.zip (Gen.elements stems) (Gen.elements extensions)
                        1, Gen.constant ("_", ("._", ""))
                    ]

            let! assemblyVersion = genVersion
            let! fileVersion = genVersion
            let! localPath = genLocalPath

            return
                {
                    Dir = dir
                    Stem = stem
                    Extension = extension
                    ExtensionInner = inner
                    AssemblyVersion = assemblyVersion
                    FileVersion = fileVersion
                    LocalPath = localPath
                }
        }

    let private genFiles : Gen<FileSpec list> =
        gen {
            let! count = Gen.choose (0, 4)
            let! files = Gen.listOfLength count genFile
            return files |> List.distinctBy fileKey
        }

    let private ridPool : string list =
        [ "osx-arm64" ; "osx" ; "unix-arm64" ; "unix" ; "any" ; "linux-x64" ]

    /// `assetType` spellings: the host compares them ignoring ASCII case only.
    let private assetTypeSpellings : (string * DepsAssetType option) list =
        [
            "runtime", Some DepsAssetType.Runtime
            "Runtime", Some DepsAssetType.Runtime
            "RUNTIME", Some DepsAssetType.Runtime
            "native", Some DepsAssetType.Native
            "Native", Some DepsAssetType.Native
            "resources", Some DepsAssetType.Resources
            "RESOURCES", Some DepsAssetType.Resources
            "runtıme", None
            "natİve", None
            "culture", None
        ]

    let private genRidFile : Gen<RidFileSpec> =
        gen {
            let! file = genFile
            let! rid = Gen.elements (ridPool @ [ "win-x64" ; "OSX" ; "Unix" ])
            let! assetType = Gen.elements assetTypeSpellings

            return
                {
                    File = file
                    Rid = Some rid
                    AssetType = Some assetType
                }
        }

    let private genPackage : Gen<PackageSpec> =
        gen {
            let! name = Gen.elements [ "A" ; "B.C" ; "System.Foo" ; "Microsoft.NETCore.App.Runtime.osx-arm64" ]
            let! version = Gen.elements [ Some "1.0.0" ; Some "10.0.14" ; Some "1.0/extra" ; None ]

            let! assets =
                [ DepsAssetType.Runtime ; DepsAssetType.Resources ; DepsAssetType.Native ]
                |> List.map (fun t ->
                    Gen.frequency [ 2, Gen.constant None ; 3, genFiles |> Gen.map (fun files -> Some (t, files)) ]
                )
                |> Gen.sequenceToList

            let! assets = Gen.shuffle (List.choose id assets)

            let! runtimeTargets =
                Gen.frequency
                    [
                        2, Gen.constant None
                        3,
                        gen {
                            let! count = Gen.choose (0, 5)
                            let! files = Gen.listOfLength count genRidFile
                            return Some (files |> List.distinctBy (fun rf -> fileKey rf.File))
                        }
                    ]

            let! dependencies = Gen.elements [ true ; false ]
            let! wrongCase = Gen.frequency [ 4, Gen.constant false ; 1, Gen.constant true ]

            return
                {
                    Name = name
                    Version = version
                    Assets = List.ofArray assets
                    RuntimeTargets = runtimeTargets
                    Dependencies = dependencies
                    WrongCaseMember = wrongCase
                }
        }

    let private genPackages : Gen<PackageSpec list> =
        gen {
            let! count = Gen.choose (0, 5)
            let! packages = Gen.listOfLength count genPackage
            return packages |> List.distinctBy packageKey
        }

    /// `type` spellings: the host compares with `runtimepack` after lowering ASCII letters only.
    let private libraryTypes : (string * DepsLibraryType) list =
        [
            "package", DepsLibraryType.Other "package"
            "project", DepsLibraryType.Other "project"
            "reference", DepsLibraryType.Other "reference"
            "runtimepack", DepsLibraryType.RuntimePack
            "RuntimePack", DepsLibraryType.RuntimePack
            "RUNTIMEPACK", DepsLibraryType.RuntimePack
            "runtımepack", DepsLibraryType.Other "runtımepack"
            "RUNTİMEPACK", DepsLibraryType.Other "RUNTİMEPACK"
            "runtimepac\u212A", DepsLibraryType.Other "runtimepac\u212A"
        ]

    let private genProperLibrary : Gen<LibraryValue> =
        gen {
            let! typeWritten, expected = Gen.elements libraryTypes
            let! serviceable = Gen.elements [ true ; false ]
            let! withPaths = Gen.elements [ true ; false ]
            return LibraryValue.Proper (typeWritten, expected, serviceable, withPaths)
        }

    let private genDoc : Gen<DocSpec> =
        gen {
            let! mode =
                Gen.frequency
                    [
                        1, Gen.constant DepsJsonMode.SelfContained
                        2,
                        gen {
                            let! count = Gen.choose (0, 5)
                            let! rids = Gen.shuffle ridPool
                            return DepsJsonMode.FrameworkDependent (List.ofArray rids |> List.truncate count)
                        }
                    ]

            let! targetName, decoyName =
                Gen.elements
                    [
                        ".NETCoreApp,Version=v10.0", ".NETCoreApp,Version=v10.0/osx-arm64"
                        ".NETCoreApp,Version=v10.0/osx-arm64", ".NETCoreApp,Version=v10.0"
                    ]

            let! packages = genPackages
            let! decoyPackages = genPackages
            let! hasDecoy = Gen.elements [ true ; false ]
            let! decoyFirst = Gen.elements [ true ; false ]

            let mainKeys = packages |> List.map (fun p -> p.Name, p.Version)

            let! kept =
                Gen.listOfLength mainKeys.Length (Gen.frequency [ 4, Gen.constant true ; 1, Gen.constant false ])

            let keptKeys = List.zip mainKeys kept |> List.filter snd |> List.map fst

            let extraKeys =
                [ "Orphan", Some "1.0.0" ; "a", Some "1.0.0" ; "system.foo", Some "10.0.14" ]
                @ (decoyPackages |> List.map (fun p -> p.Name, p.Version))

            let! extraKept =
                Gen.listOfLength extraKeys.Length (Gen.frequency [ 1, Gen.constant true ; 2, Gen.constant false ])

            let extraKeys =
                List.zip extraKeys extraKept
                |> List.filter snd
                |> List.map fst
                |> List.filter (fun k -> not (List.contains k mainKeys))

            let! libraries =
                (keptKeys |> List.map (fun k -> k, true))
                @ (extraKeys |> List.map (fun k -> k, false))
                |> List.distinctBy (fun ((name, version), _) -> key name version)
                |> List.map (fun ((name, version), named) ->
                    gen {
                        let! value =
                            if named then
                                genProperLibrary
                            else
                                Gen.frequency
                                    [
                                        2, genProperLibrary
                                        1, Gen.elements [ "null" ; "[]" ; "\"x\"" ; "1" ] |> Gen.map LibraryValue.Junk
                                    ]

                        return
                            {
                                Name = name
                                Version = version
                                Value = value
                            }
                    }
                )
                |> Gen.sequenceToList

            let! libraries = Gen.shuffle libraries
            let! rootOrder = Gen.shuffle [ 0 ; 1 ; 2 ; 3 ]
            let! asObject = Gen.elements [ true ; false ]
            let! bom = Gen.frequency [ 4, Gen.constant false ; 1, Gen.constant true ]
            let! indent = Gen.elements [ true ; false ]
            let! escapeAll = Gen.frequency [ 4, Gen.constant false ; 1, Gen.constant true ]

            return
                {
                    Mode = mode
                    TargetName = targetName
                    RuntimeTargetAsObject = asObject
                    Packages = packages
                    Libraries = List.ofArray libraries
                    Decoy = if hasDecoy then Some (decoyName, decoyPackages) else None
                    DecoyFirst = decoyFirst
                    RootOrder = List.ofArray rootOrder
                    Bom = bom
                    Indent = indent
                    EscapeAll = escapeAll
                }
        }

    // Injecting one fault.

    let private updateAt (index : int) (f : 'a -> 'a) (xs : 'a list) : 'a list =
        xs |> List.mapi (fun i x -> if i = index then f x else x)

    let private updateRidFile
        (packageIndex : int)
        (fileIndex : int)
        (f : RidFileSpec -> RidFileSpec)
        (packages : PackageSpec list)
        : PackageSpec list
        =
        packages
        |> updateAt
            packageIndex
            (fun p ->
                { p with
                    RuntimeTargets = p.RuntimeTargets |> Option.map (updateAt fileIndex f)
                }
            )

    let private ridFileSites (packages : PackageSpec list) : (int * int) list =
        [
            for pi, p in List.indexed packages do
                for fi in 0 .. (Option.defaultValue [] p.RuntimeTargets).Length - 1 do
                    yield pi, fi
        ]

    /// Every file of every package, as a function that replaces it.
    let private fileSites (packages : PackageSpec list) : ((FileSpec -> FileSpec) -> PackageSpec list) list =
        [
            for pi, p in List.indexed packages do
                for ti, (_, files) in List.indexed p.Assets do
                    for fi in 0 .. files.Length - 1 do
                        yield
                            fun (f : FileSpec -> FileSpec) ->
                                packages
                                |> updateAt
                                    pi
                                    (fun p ->
                                        { p with
                                            Assets = p.Assets |> updateAt ti (fun (t, files) -> t, updateAt fi f files)
                                        }
                                    )

                for fi in 0 .. (Option.defaultValue [] p.RuntimeTargets).Length - 1 do
                    yield
                        fun (f : FileSpec -> FileSpec) ->
                            packages
                            |> updateRidFile
                                pi
                                fi
                                (fun rf ->
                                    { rf with
                                        File = f rf.File
                                    }
                                )
        ]

    let private injectFault (doc : DocSpec) : Gen<DocSpec> =
        let pick (sites : 'a list) (apply : 'a -> Gen<DocSpec>) : Gen<DocSpec> =
            if sites.IsEmpty then
                Gen.constant doc
            else
                Gen.elements sites |> Gen.bind apply

        Gen.frequency
            [
                3, Gen.constant doc
                1,
                pick
                    (List.indexed doc.Libraries)
                    (fun (i, _) ->
                        Gen.elements
                            [
                                BadLibrary.NotAnObject
                                BadLibrary.NoSha512
                                BadLibrary.Sha512NotAString
                                BadLibrary.NoServiceable
                                BadLibrary.ServiceableNotABool
                                BadLibrary.NoType
                                BadLibrary.TypeNotAString
                            ]
                        |> Gen.map (fun bad ->
                            { doc with
                                Libraries =
                                    doc.Libraries
                                    |> updateAt
                                        i
                                        (fun l ->
                                            { l with
                                                Value = LibraryValue.Bad bad
                                            }
                                        )
                            }
                        )
                    )
                1,
                pick
                    (ridFileSites doc.Packages)
                    (fun (pi, fi) ->
                        Gen.constant
                            { doc with
                                Packages =
                                    doc.Packages
                                    |> updateRidFile
                                        pi
                                        fi
                                        (fun rf ->
                                            { rf with
                                                AssetType = None
                                            }
                                        )
                            }
                    )
                1,
                pick
                    (ridFileSites doc.Packages)
                    (fun (pi, fi) ->
                        Gen.constant
                            { doc with
                                Packages =
                                    doc.Packages
                                    |> updateRidFile
                                        pi
                                        fi
                                        (fun rf ->
                                            { rf with
                                                Rid = None
                                            }
                                        )
                            }
                    )
                1,
                gen {
                    let! inDecoy = Gen.frequency [ 3, Gen.constant false ; 1, Gen.constant true ]
                    let! text = Gen.elements outOfRangeVersions
                    let! assembly = Gen.elements [ true ; false ]

                    let setVersion (f : FileSpec) : FileSpec =
                        if assembly then
                            { f with
                                AssemblyVersion = VersionSpec.OutOfRange text
                            }
                        else
                            { f with
                                FileVersion = VersionSpec.OutOfRange text
                            }

                    match inDecoy, doc.Decoy with
                    | true, Some (name, decoyPackages) ->
                        return!
                            pick
                                (fileSites decoyPackages)
                                (fun site ->
                                    Gen.constant
                                        { doc with
                                            Decoy = Some (name, site setVersion)
                                        }
                                )
                    | _ ->
                        return!
                            pick
                                (fileSites doc.Packages)
                                (fun site ->
                                    Gen.constant
                                        { doc with
                                            Packages = site setVersion
                                        }
                                )
                }
            ]

    // ---------------------------------------------------------------------------------------
    // Coverage.

    [<RequireQualifiedAccess>]
    type private Label =
        | SelfContained
        | FrameworkDependent
        | Refused of kind : string
        /// A fault in a place the host does not read in this mode.
        | FaultNotRead
        | RidSpecificEntry
        /// A library with RID-specific files of a type, none for a listed RID, whose other files
        /// of that type stand.
        | RidAssetsUnmatched
        /// The chosen RID's files are not the first RID-specific files of that type in the
        /// document.
        | RidChosenNotFirstInDocument
        /// The first listed RID had no files of that type, and a later one won.
        | RidChosenNotFirstListed
        /// A file under a RID which differs from a listed RID only by case.
        | RidCaseVariant
        | SelfContainedSkipsRuntimeTargets
        | LibraryWithoutPackage
        | PackageWithoutLibrary
        | PackageOnlyInRuntimeTargets
        | PackageWithoutAssets
        | EmptyAssetMember
        | AssetMembersNotCanonicalOrder
        | WrongCaseMember
        | UnknownAssetType
        | AssetTypeNotLowerCase
        | LocalPathSet
        | LocalPathEmpty
        | LocalPathNotAString
        | Version of shape : string
        | Placeholder
        | PlaceholderWithoutDirectory
        | RuntimePack
        | RuntimePackLookalike
        | ResourcesEntry
        | ResourcesWithoutDirectory
        | NiStripped
        | Backslash
        | KeyWithoutSlash
        | KeyWithTwoSlashes
        | RuntimeTargetObject
        | RuntimeTargetString
        | Bom
        | EscapedEverything
        | Decoy
        | EntryCount of atLeastFive : bool

    let private labels (doc : DocSpec) (result : Result<(DepsEntry * bool * string) list, DepsJsonError>) : Label list =
        let fd = isFrameworkDependent doc.Mode

        let allFiles =
            [
                for p in doc.Packages do
                    for _, files in p.Assets do
                        yield! files

                    for rf in Option.defaultValue [] p.RuntimeTargets do
                        yield rf.File
            ]

        let hasFault =
            let faultyFile (f : FileSpec) =
                match f.AssemblyVersion, f.FileVersion with
                | VersionSpec.OutOfRange _, _
                | _, VersionSpec.OutOfRange _ -> true
                | _ -> false

            let decoyFiles =
                match doc.Decoy with
                | None -> []
                | Some (_, packages) ->
                    [
                        for p in packages do
                            for _, files in p.Assets do
                                yield! files

                            for rf in Option.defaultValue [] p.RuntimeTargets do
                                yield rf.File
                    ]

            (allFiles @ decoyFiles) |> List.exists faultyFile
            || doc.Libraries
               |> List.exists (fun l ->
                   match l.Value with
                   | LibraryValue.Bad _ -> true
                   | _ -> false
               )
            || doc.Packages
               |> List.exists (fun p ->
                   Option.defaultValue [] p.RuntimeTargets
                   |> List.exists (fun rf -> rf.Rid.IsNone || rf.AssetType.IsNone)
               )

        [
            if fd then Label.FrameworkDependent else Label.SelfContained

            match result with
            | Error e ->
                let kind =
                    match e with
                    | DepsJsonError.MissingMember (_, name) -> $"MissingMember %s{name}"
                    | DepsJsonError.WrongKind (_, kind) -> $"WrongKind %A{kind}"
                    | DepsJsonError.VersionComponentOutOfRange _ -> "VersionComponentOutOfRange"
                    | other -> $"unexpected: %A{other}"

                Label.Refused kind
            | Ok entries ->
                if hasFault then
                    Label.FaultNotRead

                Label.EntryCount (entries.Length >= 5)

                for entry, placeholder, _ in entries do
                    if entry.IsRidSpecific then
                        Label.RidSpecificEntry

                    if placeholder then
                        Label.Placeholder

                    if entry.AssetType = DepsAssetType.Resources then
                        Label.ResourcesEntry

                    match entry.LibraryType with
                    | DepsLibraryType.RuntimePack -> Label.RuntimePack
                    | DepsLibraryType.Other written when written.Length = "runtimepack".Length ->
                        Label.RuntimePackLookalike
                    | DepsLibraryType.Other _ -> ()

                    if entry.LibraryName = entry.LibraryVersion then
                        Label.KeyWithoutSlash

                    if entry.LibraryVersion.Contains '/' then
                        Label.KeyWithTwoSlashes

                    if entry.RelativePath.EndsWith "_._" && not (entry.RelativePath.Contains '/') then
                        Label.PlaceholderWithoutDirectory

                    if
                        entry.AssetType = DepsAssetType.Resources
                        && not (entry.RelativePath.Contains '/')
                    then
                        Label.ResourcesWithoutDirectory

            for f in allFiles do
                if isNiSuffix (f.Stem + f.ExtensionInner) then
                    Label.NiStripped

                if f.Dir.Written.Contains '\\' then
                    Label.Backslash

                match f.LocalPath with
                | LocalPathSpec.Text (_, Some _) -> Label.LocalPathSet
                | LocalPathSpec.Text (_, None) -> Label.LocalPathEmpty
                | LocalPathSpec.NotAString _ -> Label.LocalPathNotAString
                | LocalPathSpec.Absent -> ()

                for v in [ f.AssemblyVersion ; f.FileVersion ] do
                    match v with
                    | VersionSpec.Absent -> Label.Version "absent"
                    | VersionSpec.NotAString _ -> Label.Version "not a string"
                    | VersionSpec.Text ("", _) -> Label.Version "empty string"
                    | VersionSpec.Text (written, DepsVersion.Empty) when written <> "" -> Label.Version "malformed"
                    | VersionSpec.Text (written, _) when written.StartsWith '0' -> Label.Version "leading zeros"
                    | VersionSpec.Text (_, DepsVersion.MajorMinor _) -> Label.Version "two"
                    | VersionSpec.Text (_, DepsVersion.MajorMinorBuild _) -> Label.Version "three"
                    | VersionSpec.Text (_, DepsVersion.MajorMinorBuildRevision _) -> Label.Version "four"
                    | VersionSpec.Text _ -> ()
                    | VersionSpec.OutOfRange _ -> ()

            for p in doc.Packages do
                let named =
                    doc.Libraries |> List.exists (fun l -> key l.Name l.Version = packageKey p)

                if not named then
                    Label.PackageWithoutLibrary

                if p.Assets.IsEmpty then
                    Label.PackageWithoutAssets

                if p.Assets.IsEmpty && packageExists doc.Mode p then
                    Label.PackageOnlyInRuntimeTargets

                if p.Assets |> List.exists (fun (_, files) -> files.IsEmpty) then
                    Label.EmptyAssetMember

                let order = p.Assets |> List.map fst

                if order <> List.sort order then
                    Label.AssetMembersNotCanonicalOrder

                if p.WrongCaseMember then
                    Label.WrongCaseMember

                let ridFiles = Option.defaultValue [] p.RuntimeTargets

                if not fd && not ridFiles.IsEmpty then
                    Label.SelfContainedSkipsRuntimeTargets

                for rf in ridFiles do
                    match rf.AssetType with
                    | Some (_, None) -> Label.UnknownAssetType
                    | Some (written, Some _) when written <> written.ToLowerInvariant () -> Label.AssetTypeNotLowerCase
                    | _ -> ()

                match doc.Mode with
                | DepsJsonMode.SelfContained -> ()
                | DepsJsonMode.FrameworkDependent rids ->
                    for rf in ridFiles do
                        match rf.Rid with
                        | Some rid when
                            not (List.contains rid rids)
                            && rids
                               |> List.exists (fun r -> String.Equals (r, rid, StringComparison.OrdinalIgnoreCase))
                            ->
                            Label.RidCaseVariant
                        | _ -> ()

                    for t in [ DepsAssetType.Runtime ; DepsAssetType.Resources ; DepsAssetType.Native ] do
                        let ofType =
                            ridFiles
                            |> List.filter (fun rf ->
                                match rf.AssetType with
                                | Some (_, Some t') -> t' = t
                                | _ -> false
                            )

                        match ridChoice doc.Mode p t with
                        | None ->
                            if not ofType.IsEmpty && not (p.Assets |> List.forall (fun (t', _) -> t' <> t)) then
                                Label.RidAssetsUnmatched
                        | Some (rid, _) ->
                            if (List.head ofType).Rid <> Some rid then
                                Label.RidChosenNotFirstInDocument

                            if List.tryHead rids <> Some rid then
                                Label.RidChosenNotFirstListed

            for l in doc.Libraries do
                if (mainPackage doc l).IsNone then
                    Label.LibraryWithoutPackage

            if doc.RuntimeTargetAsObject then
                Label.RuntimeTargetObject
            else
                Label.RuntimeTargetString

            if doc.Bom then
                Label.Bom

            if doc.EscapeAll then
                Label.EscapedEverything

            if doc.Decoy.IsSome then
                Label.Decoy
        ]

    let private everyLabel : Label list =
        [
            Label.SelfContained
            Label.FrameworkDependent
            Label.Refused "MissingMember assetType"
            Label.Refused "MissingMember rid"
            Label.Refused "MissingMember sha512"
            Label.Refused "MissingMember serviceable"
            Label.Refused "MissingMember type"
            Label.Refused "WrongKind Object"
            Label.Refused "WrongKind String"
            Label.Refused "WrongKind Bool"
            Label.Refused "VersionComponentOutOfRange"
            Label.FaultNotRead
            Label.RidSpecificEntry
            Label.RidAssetsUnmatched
            Label.RidChosenNotFirstInDocument
            Label.RidChosenNotFirstListed
            Label.RidCaseVariant
            Label.SelfContainedSkipsRuntimeTargets
            Label.LibraryWithoutPackage
            Label.PackageWithoutLibrary
            Label.PackageOnlyInRuntimeTargets
            Label.PackageWithoutAssets
            Label.EmptyAssetMember
            Label.AssetMembersNotCanonicalOrder
            Label.WrongCaseMember
            Label.UnknownAssetType
            Label.AssetTypeNotLowerCase
            Label.LocalPathSet
            Label.LocalPathEmpty
            Label.LocalPathNotAString
            Label.Version "absent"
            Label.Version "not a string"
            Label.Version "empty string"
            Label.Version "malformed"
            Label.Version "leading zeros"
            Label.Version "two"
            Label.Version "three"
            Label.Version "four"
            Label.Placeholder
            Label.PlaceholderWithoutDirectory
            Label.RuntimePack
            Label.RuntimePackLookalike
            Label.ResourcesEntry
            Label.ResourcesWithoutDirectory
            Label.NiStripped
            Label.Backslash
            Label.KeyWithoutSlash
            Label.KeyWithTwoSlashes
            Label.RuntimeTargetObject
            Label.RuntimeTargetString
            Label.Bom
            Label.EscapedEverything
            Label.Decoy
            Label.EntryCount true
            Label.EntryCount false
        ]

    let private parseWithClassifiers
        (mode : DepsJsonMode)
        (contents : byte[])
        : Result<(DepsEntry * bool * string) list, DepsJsonError>
        =
        DepsJson.parse mode contents
        |> Result.map (List.map (fun entry -> entry, DepsEntry.isPlaceholder entry, DepsEntry.dirRelativePath entry))

    [<Test>]
    let ``A generated deps.json parses to the entries its model says the host reads`` () =
        let property (cover : Label -> unit) (doc : DocSpec) : unit =
            let expected = expected doc
            labels doc expected |> List.iter cover

            parseWithClassifiers doc.Mode (renderDoc doc) |> shouldBe expected

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 3000)
                (Arb.fromGen (genDoc |> Gen.bind injectFault))
                property

        let thin =
            everyLabel
            |> List.map (fun label -> label, coverage.Count label)
            |> List.filter (fun (_, count) -> count < 20)

        thin |> shouldBe []

    // ---------------------------------------------------------------------------------------
    // One case per refusal, and per rule a unit case can name.

    let private targetName : string = ".NETCoreApp,Version=v10.0"

    let private fd : DepsJsonMode =
        DepsJsonMode.FrameworkDependent [ "osx-arm64" ; "osx" ; "unix-arm64" ; "unix" ; "any" ]

    let private library (key : string) : string =
        $"\"%s{key}\": {{ \"type\": \"package\", \"serviceable\": false, \"sha512\": \"\" }}"

    /// A document with the given `targets` members of the runtime target, and `libraries` members.
    let private document (packages : string) (libraries : string) : string =
        $"{{ \"runtimeTarget\": {{ \"name\": \"%s{targetName}\" }}, \"targets\": {{ \"%s{targetName}\": {{ %s{packages} }} }}, \"libraries\": {{ %s{libraries} }} }}"

    let private onePackage (assets : string) : string =
        document $"\"P/1.0.0\": {{ %s{assets} }}" (library "P/1.0.0")

    let private parseText (mode : DepsJsonMode) (text : string) : Result<DepsEntry list, DepsJsonError> =
        DepsJson.parse mode (Encoding.UTF8.GetBytes text)

    let private m (name : string) : DepsJsonPathSegment = DepsJsonPathSegment.Member name

    let private packagePath : DepsJsonPathSegment list =
        [ m "targets" ; m targetName ; m "P/1.0.0" ]

    let private refuses (mode : DepsJsonMode) (text : string) (expected : DepsJsonError) : unit =
        parseText mode text |> shouldBe (Error expected)

    let private notStrict (mode : DepsJsonMode) (bytes : byte[]) : unit =
        match DepsJson.parse mode bytes with
        | Error (DepsJsonError.NotStrictJson _) -> ()
        | other -> failwith $"expected NotStrictJson, got %A{other}"

    let private entry (relativePath : string) (assetName : string) : DepsEntry =
        {
            LibraryName = "P"
            LibraryVersion = "1.0.0"
            LibraryType = DepsLibraryType.Other "package"
            AssetType = DepsAssetType.Runtime
            AssetName = assetName
            RelativePath = relativePath
            LocalPath = None
            AssemblyVersion = DepsVersion.Empty
            FileVersion = DepsVersion.Empty
            IsRidSpecific = false
        }

    [<Test>]
    let ``A minimal document parses`` () =
        parseText fd (onePackage "\"runtime\": { \"lib/P.dll\": {} }")
        |> shouldBe (Ok [ entry "lib/P.dll" "P" ])

        parseText DepsJsonMode.SelfContained (onePackage "\"runtime\": { \"lib/P.dll\": {} }")
        |> shouldBe (Ok [ entry "lib/P.dll" "P" ])

    [<Test>]
    let ``Bytes that are not UTF-8 are refused`` () =
        let text = onePackage "\"runtime\": { \"lib/P.dll\": {} }"
        let bytes = Encoding.UTF8.GetBytes (text.Replace ("lib/P.dll", "lib/P\u00FF.dll"))
        // Replace the two-byte encoding of U+00FF with a lone continuation byte.
        let index = bytes |> Array.findIndex (fun b -> b = 0xC3uy)

        let bytes =
            Array.append bytes.[.. index - 1] (Array.append [| 0xBFuy |] bytes.[index + 2 ..])

        DepsJson.parse fd bytes |> shouldBe (Error DepsJsonError.InvalidUtf8)

    [<Test>]
    let ``A byte order mark is skipped, once`` () =
        let text = Encoding.UTF8.GetBytes (onePackage "\"runtime\": { \"lib/P.dll\": {} }")
        let bom = [| 0xEFuy ; 0xBBuy ; 0xBFuy |]

        DepsJson.parse fd (Array.append bom text)
        |> shouldBe (Ok [ entry "lib/P.dll" "P" ])

        notStrict fd (Array.concat [ bom ; bom ; text ])

    [<Test>]
    let ``Documents which are not strict JSON are refused`` () =
        let good = onePackage "\"runtime\": { \"lib/P.dll\": {} }"
        notStrict fd (Encoding.UTF8.GetBytes "")
        notStrict fd (Encoding.UTF8.GetBytes "   ")
        // rapidjson accepts these three (kParseCommentsFlag, kParseStopWhenDoneFlag); PawPrint does not.
        notStrict fd (Encoding.UTF8.GetBytes ("// comment\n" + good))
        notStrict fd (Encoding.UTF8.GetBytes (good.Replace ("\"runtimeTarget\"", "/* c */ \"runtimeTarget\"")))
        notStrict fd (Encoding.UTF8.GetBytes (good + " trailing"))
        notStrict fd (Encoding.UTF8.GetBytes (good + " {}"))
        // Neither reader accepts a trailing comma.
        notStrict fd (Encoding.UTF8.GetBytes (good.Replace ("{} }", "{}, }")))
        let deep = String.replicate 70 "[" + String.replicate 70 "]"

        notStrict
            fd
            (Encoding.UTF8.GetBytes (good.Replace ("\"runtimeTarget\"", $"\"deep\": %s{deep}, \"runtimeTarget\"")))

    [<Test>]
    let ``An unpaired surrogate is refused`` () =
        refuses
            fd
            (onePackage "\"runtime\": { \"lib/P\\uD800.dll\": {} }")
            (DepsJsonError.UnpairedSurrogate (packagePath @ [ m "runtime" ]))

        refuses
            fd
            (onePackage "\"runtime\": { \"lib/P.dll\": { \"localPath\": \"\\uDC00\" } }")
            (DepsJsonError.UnpairedSurrogate (packagePath @ [ m "runtime" ; m "lib/P.dll" ; m "localPath" ]))

    [<Test>]
    let ``A duplicate member is refused, wherever it is`` () =
        refuses
            fd
            (onePackage "\"runtime\": { \"lib/P.dll\": {}, \"lib/P.dll\": {} }")
            (DepsJsonError.DuplicateMember (packagePath @ [ m "runtime" ], "lib/P.dll"))

        // In a part the host never reads.
        refuses
            fd
            ((onePackage "\"runtime\": { \"lib/P.dll\": {} }")
                .Replace ("\"runtimeTarget\"", "\"x\": { \"a\": 1, \"a\": 2 }, \"runtimeTarget\""))
            (DepsJsonError.DuplicateMember ([ m "x" ], "a"))

        // Escaped and unescaped spellings of one name are one name.
        refuses
            fd
            (onePackage "\"runtime\": { \"lib/P.dll\": {}, \"lib\\/P.dll\": {} }")
            (DepsJsonError.DuplicateMember (packagePath @ [ m "runtime" ], "lib/P.dll"))

    [<Test>]
    let ``A NUL character is refused`` () =
        refuses
            fd
            (onePackage "\"runtime\": { \"lib/P.dll\": { \"assemblyVersion\": \"1.0\\u0000x\" } }")
            (DepsJsonError.NulCharacter (packagePath @ [ m "runtime" ; m "lib/P.dll" ; m "assemblyVersion" ]))

        refuses
            fd
            (onePackage "\"runtime\": { \"lib/P\\u0000.dll\": {} }")
            (DepsJsonError.NulCharacter (packagePath @ [ m "runtime" ; m "lib/P\u0000.dll" ]))

    [<Test>]
    let ``A number rapidjson might refuse is refused`` () =
        let withNumber (n : string) : string =
            (onePackage "\"runtime\": { \"lib/P.dll\": {} }")
                .Replace ("\"runtimeTarget\"", $"\"n\": %s{n}, \"runtimeTarget\"")

        // rapidjson fails `0e309` with kParseErrorNumberTooBig although its value is zero.
        refuses fd (withNumber "0e309") (DepsJsonError.UnreproducibleNumber ([ m "n" ], "0e309"))
        refuses fd (withNumber "1E2") (DepsJsonError.UnreproducibleNumber ([ m "n" ], "1E2"))
        let long = String.replicate 301 "9"
        refuses fd (withNumber long) (DepsJsonError.UnreproducibleNumber ([ m "n" ], long))
        // Plain numbers are fine.
        parseText fd (withNumber "-12.5") |> Result.isOk |> shouldBe true

        parseText fd (withNumber (String.replicate 300 "9"))
        |> Result.isOk
        |> shouldBe true

    [<Test>]
    let ``The root must be an object`` () =
        refuses fd "[]" (DepsJsonError.WrongKind ([], DepsJsonKind.Object))
        refuses fd "\"x\"" (DepsJsonError.WrongKind ([], DepsJsonKind.Object))

    [<Test>]
    let ``runtimeTarget must be a string or an object with a string name`` () =
        let good = onePackage "\"runtime\": { \"lib/P.dll\": {} }"
        let objectForm = $"\"runtimeTarget\": {{ \"name\": \"%s{targetName}\" }}"

        refuses fd (good.Replace (objectForm + ", ", "")) (DepsJsonError.MissingMember ([], "runtimeTarget"))

        refuses
            fd
            (good.Replace (objectForm, "\"runtimeTarget\": 1"))
            (DepsJsonError.WrongKind ([ m "runtimeTarget" ], DepsJsonKind.StringOrObject))

        refuses
            fd
            (good.Replace (objectForm, "\"runtimeTarget\": { \"signature\": \"\" }"))
            (DepsJsonError.MissingMember ([ m "runtimeTarget" ], "name"))

        refuses
            fd
            (good.Replace (objectForm, "\"runtimeTarget\": { \"name\": null }"))
            (DepsJsonError.WrongKind ([ m "runtimeTarget" ; m "name" ], DepsJsonKind.String))

        parseText fd (good.Replace (objectForm, $"\"runtimeTarget\": \"%s{targetName}\""))
        |> shouldBe (Ok [ entry "lib/P.dll" "P" ])

    [<Test>]
    let ``targets and the runtime target must be objects`` () =
        let good = onePackage "\"runtime\": { \"lib/P.dll\": {} }"

        for mode in [ fd ; DepsJsonMode.SelfContained ] do
            refuses
                mode
                (good.Replace ($"\"targets\": {{ \"%s{targetName}\"", "\"targetz\": { \"x\""))
                (DepsJsonError.MissingMember ([], "targets"))

            refuses
                mode
                (good.Replace ($"\"targets\": {{ \"%s{targetName}\"", "\"targets\": { \"x\""))
                (DepsJsonError.MissingMember ([ m "targets" ], targetName))

            refuses
                mode
                (document $"\"P/1.0.0\": [] " (library "P/1.0.0"))
                (DepsJsonError.WrongKind (packagePath, DepsJsonKind.Object))

            refuses
                mode
                (onePackage "\"runtime\": [ \"lib/P.dll\" ]")
                (DepsJsonError.WrongKind (packagePath @ [ m "runtime" ], DepsJsonKind.Object))

            refuses
                mode
                (onePackage "\"native\": { \"P.so\": \"x\" }")
                (DepsJsonError.WrongKind (packagePath @ [ m "native" ; m "P.so" ], DepsJsonKind.Object))

        refuses
            fd
            $"{{ \"runtimeTarget\": \"%s{targetName}\", \"targets\": {{ \"%s{targetName}\": 1 }}, \"libraries\": {{}} }}"
            (DepsJsonError.WrongKind ([ m "targets" ; m targetName ], DepsJsonKind.Object))

    [<Test>]
    let ``runtimeTargets is read only when framework-dependent`` () =
        let cases : (string * DepsJsonError) list =
            [
                "\"runtimeTargets\": 1",
                DepsJsonError.WrongKind (packagePath @ [ m "runtimeTargets" ], DepsJsonKind.Object)
                "\"runtimeTargets\": { \"r/P.dll\": 1 }",
                DepsJsonError.WrongKind (packagePath @ [ m "runtimeTargets" ; m "r/P.dll" ], DepsJsonKind.Object)
                "\"runtimeTargets\": { \"r/P.dll\": { \"rid\": \"osx\" } }",
                DepsJsonError.MissingMember (packagePath @ [ m "runtimeTargets" ; m "r/P.dll" ], "assetType")
                "\"runtimeTargets\": { \"r/P.dll\": { \"rid\": \"osx\", \"assetType\": 1 } }",
                DepsJsonError.WrongKind (
                    packagePath @ [ m "runtimeTargets" ; m "r/P.dll" ; m "assetType" ],
                    DepsJsonKind.String
                )
                "\"runtimeTargets\": { \"r/P.dll\": { \"assetType\": \"RunTime\" } }",
                DepsJsonError.MissingMember (packagePath @ [ m "runtimeTargets" ; m "r/P.dll" ], "rid")
                "\"runtimeTargets\": { \"r/P.dll\": { \"assetType\": \"runtime\", \"rid\": [] } }",
                DepsJsonError.WrongKind (
                    packagePath @ [ m "runtimeTargets" ; m "r/P.dll" ; m "rid" ],
                    DepsJsonKind.String
                )
                "\"runtimeTargets\": { \"r/P.dll\": { \"assetType\": \"runtime\", \"rid\": \"osx\", \"fileVersion\": \"2147483648.0\" } }",
                DepsJsonError.VersionComponentOutOfRange (
                    packagePath @ [ m "runtimeTargets" ; m "r/P.dll" ; m "fileVersion" ],
                    "2147483648.0"
                )
            ]

        for runtimeTargets, error in cases do
            let text = onePackage ("\"runtime\": { \"lib/P.dll\": {} }, " + runtimeTargets)
            refuses fd text error

            parseText DepsJsonMode.SelfContained text
            |> shouldBe (Ok [ entry "lib/P.dll" "P" ])

        // An asset type the host does not know needs no `rid`, and its versions are not read.
        parseText
            fd
            (onePackage
                "\"runtime\": { \"lib/P.dll\": {} }, \"runtimeTargets\": { \"r/P.dll\": { \"assetType\": \"runtıme\", \"assemblyVersion\": \"2147483648.0\" } }")
        |> shouldBe (Ok [ entry "lib/P.dll" "P" ])

    [<Test>]
    let ``libraries must be an object, and a read library's members of the right kinds`` () =
        let good = onePackage "\"runtime\": { \"lib/P.dll\": {} }"
        let goodLibrary = library "P/1.0.0"

        for mode in [ fd ; DepsJsonMode.SelfContained ] do
            refuses
                mode
                (good.Replace ("\"libraries\"", "\"librariez\""))
                (DepsJsonError.MissingMember ([], "libraries"))

            refuses
                mode
                (good.Replace ($"\"libraries\": {{ %s{goodLibrary} }}", "\"libraries\": []"))
                (DepsJsonError.WrongKind ([ m "libraries" ], DepsJsonKind.Object))

            let libraryPath = [ m "libraries" ; m "P/1.0.0" ]

            let withLibrary (value : string) : string =
                good.Replace (goodLibrary, $"\"P/1.0.0\": %s{value}")

            refuses mode (withLibrary "null") (DepsJsonError.WrongKind (libraryPath, DepsJsonKind.Object))

            refuses
                mode
                (withLibrary "{ \"type\": \"package\", \"serviceable\": false }")
                (DepsJsonError.MissingMember (libraryPath, "sha512"))

            refuses
                mode
                (withLibrary "{ \"type\": \"package\", \"serviceable\": false, \"sha512\": 1 }")
                (DepsJsonError.WrongKind (libraryPath @ [ m "sha512" ], DepsJsonKind.String))

            refuses
                mode
                (withLibrary "{ \"type\": \"package\", \"sha512\": \"\" }")
                (DepsJsonError.MissingMember (libraryPath, "serviceable"))

            refuses
                mode
                (withLibrary "{ \"type\": \"package\", \"serviceable\": \"false\", \"sha512\": \"\" }")
                (DepsJsonError.WrongKind (libraryPath @ [ m "serviceable" ], DepsJsonKind.Bool))

            refuses
                mode
                (withLibrary "{ \"serviceable\": false, \"sha512\": \"\" }")
                (DepsJsonError.MissingMember (libraryPath, "type"))

            refuses
                mode
                (withLibrary "{ \"type\": null, \"serviceable\": false, \"sha512\": \"\" }")
                (DepsJsonError.WrongKind (libraryPath @ [ m "type" ], DepsJsonKind.String))

            // A library with no package in the runtime target is not read.
            parseText mode (good.Replace (goodLibrary, goodLibrary + ", \"Q/2.0.0\": null"))
            |> shouldBe (Ok [ entry "lib/P.dll" "P" ])

            // Nor is one whose package has no asset-type member.
            parseText mode (document "\"P/1.0.0\": { \"dependencies\": {} }" "\"P/1.0.0\": null")
            |> shouldBe (Ok [])

        // A package with assets only in `runtimeTargets` is read when framework-dependent.
        let ridOnly =
            document
                "\"P/1.0.0\": { \"runtimeTargets\": { \"r/P.dll\": { \"assetType\": \"runtime\", \"rid\": \"linux-x64\" } } }"
                "\"P/1.0.0\": null"

        refuses fd ridOnly (DepsJsonError.WrongKind ([ m "libraries" ; m "P/1.0.0" ], DepsJsonKind.Object))
        parseText DepsJsonMode.SelfContained ridOnly |> shouldBe (Ok [])

    [<Test>]
    let ``A version with a component above Int32.MaxValue is refused`` () =
        refuses
            fd
            (onePackage "\"runtime\": { \"lib/P.dll\": { \"assemblyVersion\": \"1.2.3.2147483648\" } }")
            (DepsJsonError.VersionComponentOutOfRange (
                packagePath @ [ m "runtime" ; m "lib/P.dll" ; m "assemblyVersion" ],
                "1.2.3.2147483648"
            ))

        parseText fd (onePackage "\"runtime\": { \"lib/P.dll\": { \"assemblyVersion\": \"1.2.3.2147483647\" } }")
        |> shouldBe (
            Ok
                [
                    { entry "lib/P.dll" "P" with
                        AssemblyVersion = DepsVersion.MajorMinorBuildRevision (1, 2, 3, Int32.MaxValue)
                    }
                ]
        )

    [<Test>]
    let ``Asset names, paths and local paths as the host reads them`` () =
        let parsed =
            parseText
                fd
                (onePackage
                    "\"runtime\": { \"lib\\\\net8.0\\\\A.ni.dll\": { \"localPath\": \"x\\\\A.dll\" }, \"a.b/C\": { \"localPath\": 1 }, \"lib/.dll\": { \"localPath\": \"\" } }")

        parsed
        |> shouldBe (
            Ok
                [
                    { entry "lib/net8.0/A.ni.dll" "A" with
                        LocalPath = Some "x/A.dll"
                    }
                    entry "a.b/C" "C"
                    entry "lib/.dll" ""
                ]
        )

    [<Test>]
    let ``A library key without a slash names the library and its version alike`` () =
        parseText fd (document "\"P\": { \"runtime\": { \"P.dll\": {} } }" (library "P"))
        |> shouldBe (
            Ok
                [
                    { entry "P.dll" "P" with
                        LibraryName = "P"
                        LibraryVersion = "P"
                    }
                ]
        )

    [<Test>]
    let ``Placeholders are classified by their path`` () =
        let classify (path : string) : bool =
            DepsEntry.isPlaceholder (entry path "_")

        classify "lib/net8.0/_._" |> shouldBe true
        classify "_._" |> shouldBe false
        classify "lib/_._x" |> shouldBe false

        parseText fd (onePackage "\"runtime\": { \"lib\\\\net8.0\\\\_._\": {} }")
        |> Result.map (List.map DepsEntry.isPlaceholder)
        |> shouldBe (Ok [ true ])

    [<Test>]
    let ``The directory-relative path of each kind of entry`` () =
        let e = entry "lib/net8.0/de/A.resources.dll" "A.resources"
        DepsEntry.dirRelativePath e |> shouldBe "A.resources.dll"

        DepsEntry.dirRelativePath
            { e with
                AssetType = DepsAssetType.Resources
            }
        |> shouldBe "de/A.resources.dll"

        DepsEntry.dirRelativePath
            { e with
                AssetType = DepsAssetType.Resources
                RelativePath = "A.resources.dll"
            }
        |> shouldBe "A.resources.dll/A.resources.dll"

        DepsEntry.dirRelativePath
            { e with
                LibraryType = DepsLibraryType.RuntimePack
            }
        |> shouldBe "lib/net8.0/de/A.resources.dll"

        DepsEntry.dirRelativePath
            { e with
                LibraryType = DepsLibraryType.RuntimePack
                LocalPath = Some "elsewhere/A.dll"
            }
        |> shouldBe "elsewhere/A.dll"

        DepsEntry.dirRelativePath
            { e with
                AssetType = DepsAssetType.Native
                LocalPath = Some "native/A.so"
            }
        |> shouldBe "native/A.so"

    [<Test>]
    let ``RID-specific assets of the first listed RID replace a library's other assets of that type`` () =
        let text =
            onePackage
                "\"runtime\": { \"lib/P.dll\": {} }, \"native\": { \"P.so\": {} }, \"runtimeTargets\": { \"runtimes/unix/lib/P.dll\": { \"rid\": \"unix\", \"assetType\": \"runtime\" }, \"runtimes/osx/lib/P.dll\": { \"rid\": \"osx\", \"assetType\": \"Runtime\" }, \"runtimes/osx/lib/Q.dll\": { \"rid\": \"osx\", \"assetType\": \"runtime\" }, \"runtimes/win/native/P.dll\": { \"rid\": \"win\", \"assetType\": \"native\" } }"

        let rid (path : string) (name : string) : DepsEntry =
            { entry path name with
                IsRidSpecific = true
            }

        let native =
            { entry "P.so" "P" with
                AssetType = DepsAssetType.Native
            }

        parseText fd text
        |> shouldBe (Ok [ rid "runtimes/osx/lib/P.dll" "P" ; rid "runtimes/osx/lib/Q.dll" "Q" ; native ])

        parseText (DepsJsonMode.FrameworkDependent [ "unix" ; "osx" ]) text
        |> shouldBe (Ok [ rid "runtimes/unix/lib/P.dll" "P" ; native ])

        parseText (DepsJsonMode.FrameworkDependent [ "OSX" ; "linux" ]) text
        |> shouldBe (Ok [ entry "lib/P.dll" "P" ; native ])

        parseText DepsJsonMode.SelfContained text
        |> shouldBe (Ok [ entry "lib/P.dll" "P" ; native ])

    [<Test>]
    let ``Entries come in libraries order, each library's types in a fixed order`` () =
        let text =
            document
                "\"Q/1.0.0\": { \"native\": { \"Q.so\": {} }, \"runtime\": { \"Q.dll\": {} } }, \"P/1.0.0\": { \"runtime\": { \"P.dll\": {} } }"
                (library "P/1.0.0" + ", " + library "Q/1.0.0")

        parseText fd text
        |> Result.map (List.map (fun e -> e.LibraryName, e.AssetType, e.RelativePath))
        |> shouldBe (
            Ok
                [
                    "P", DepsAssetType.Runtime, "P.dll"
                    "Q", DepsAssetType.Runtime, "Q.dll"
                    "Q", DepsAssetType.Native, "Q.so"
                ]
        )

    [<Test>]
    let ``Library types are compared with runtimepack ignoring ASCII case only`` () =
        let withType (t : string) : Result<DepsLibraryType list, DepsJsonError> =
            parseText
                fd
                (onePackage "\"runtime\": { \"lib/P.dll\": {} }"
                 |> fun s -> s.Replace ("\"type\": \"package\"", $"\"type\": \"%s{t}\""))
            |> Result.map (List.map (fun e -> e.LibraryType))

        withType "RuntimePack" |> shouldBe (Ok [ DepsLibraryType.RuntimePack ])
        withType "runtımepack" |> shouldBe (Ok [ DepsLibraryType.Other "runtımepack" ])

        withType "runtimepac\u212A"
        |> shouldBe (Ok [ DepsLibraryType.Other "runtimepac\u212A" ])

        withType "Package" |> shouldBe (Ok [ DepsLibraryType.Other "Package" ])

    // ---------------------------------------------------------------------------------------
    // The real frameworks' files.

    /// Runtime assets the plan measured (`docs/plans/2026-11-19-host-style-assembly-binding.md`,
    /// "Measured host TPAs"), by framework and version. A runtime bump needs a new row, measured.
    let private measuredRuntimeAssets : Map<string * string, int> =
        Map.ofList
            [
                ("Microsoft.NETCore.App", "10.0.14"), 172
                ("Microsoft.AspNetCore.App", "10.0.14"), 141
                ("Microsoft.NETCore.App", "11.0.0-rc.3.26522.148"), 181
                ("Microsoft.AspNetCore.App", "11.0.0-rc.3.26522.148"), 132
            ]

    let everyRuntime : TestCaseData list = FrameworkUnderTest.everyRuntime

    [<TestCaseSource(nameof everyRuntime)>]
    let ``Each shared framework's deps.json lists its directory's assemblies`` (runtime : EmulatedRuntime) : unit =
        let netCore = FrameworkUnderTest.requireSharedFrameworkDirectoryOf runtime

        let selected =
            match FrameworkUnderTest.locate runtime with
            | Ok selected -> selected
            | Error e -> failwith (FrameworkUnderTest.describe e)

        let frameworkName (dir : string) : string =
            dir
            |> Path.TrimEndingDirectorySeparator
            |> Path.GetDirectoryName
            |> Path.GetFileName

        let aspNetCore =
            selected.RuntimeDirs
            |> Seq.filter (fun dir -> frameworkName dir = "Microsoft.AspNetCore.App")
            |> Seq.exactlyOne

        for dir in [ netCore ; aspNetCore ] do
            let name = frameworkName dir
            let version = dir |> Path.TrimEndingDirectorySeparator |> Path.GetFileName
            let bytes = File.ReadAllBytes (Path.Combine (dir, name + ".deps.json"))

            let entries =
                match DepsJson.parse DepsJsonMode.SelfContained bytes with
                | Ok entries -> entries
                | Error e -> failwith $"%s{dir}: %A{e}"

            // No framework lists RID-specific assets, so the two modes agree.
            DepsJson.parse fd bytes |> shouldBe (Ok entries)

            let runtimeEntries =
                entries |> List.filter (fun e -> e.AssetType = DepsAssetType.Runtime)

            for e in runtimeEntries do
                e.IsRidSpecific |> shouldBe false
                DepsEntry.isPlaceholder e |> shouldBe false
                e.AssemblyVersion |> shouldNotEqual DepsVersion.Empty
                e.FileVersion |> shouldNotEqual DepsVersion.Empty

                Path.GetFileNameWithoutExtension (DepsEntry.dirRelativePath e)
                |> shouldBe e.AssetName

            let listed = runtimeEntries |> List.map DepsEntry.dirRelativePath |> List.sort

            let present =
                Directory.GetFiles dir
                |> Array.map Path.GetFileName
                |> Array.filter (fun f -> f.EndsWith (".dll", StringComparison.OrdinalIgnoreCase))
                |> Array.sort
                |> List.ofArray

            listed |> shouldBe present

            match Map.tryFind (name, version) measuredRuntimeAssets with
            | Some count -> runtimeEntries.Length |> shouldBe count
            | None -> failwith $"No measured runtime asset count for %s{name} %s{version}; measure it and add a row."
