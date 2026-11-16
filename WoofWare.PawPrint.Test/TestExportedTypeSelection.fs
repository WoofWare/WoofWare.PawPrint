namespace WoofWare.PawPrint.Test

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open System.Runtime.Loader
open System.Threading.Tasks
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open Microsoft.CodeAnalysis
open NUnit.Framework
open WoofWare.PawPrint

/// `ExportedTypeSelection`, what CoreCLR's `AssemblyNative_GetExportedTypes` selects from one image,
/// checked three ways: against the host's `GetExportedTypes()` over generated Roslyn assemblies (the
/// TypeDef half, and the exclusion of forwarders); against the host over generated multi-module
/// assemblies (the File half, where the host throws `FileLoadException` naming the File); and against a
/// transcription of CoreCLR's ExportedType loop over generated rows, including shapes no compiler
/// writes.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestExportedTypeSelection =

    // The factory is intentionally undisposed: the DumpedAssembly loggers close over its sinks.
    let private loggerFactory : Microsoft.Extensions.Logging.ILoggerFactory =
        let _, loggerFactory = LoggerFactory.makeTest ()
        loggerFactory

    // ----- Generated assemblies -----

    /// The accessibility of a top-level type.
    [<RequireQualifiedAccess>]
    type TopAccess =
        | Public
        | Internal
        | File

    /// The six accessibilities of a nested type, as C# writes them.
    let private nestedKeywords : string list =
        [
            "public"
            "private"
            "protected"
            "internal"
            "private protected"
            "protected internal"
        ]

    type NestedType =
        {
            Keyword : string
            Arity : int
            Children : NestedType list
        }

    type TopType =
        {
            Access : TopAccess
            InNamespace : bool
            Arity : int
            Children : NestedType list
        }

    /// A CoreLib type for `[assembly: TypeForwardedTo]`, each with nested public types of its own,
    /// which Roslyn writes as ExportedType rows too.
    let private forwardable : string list =
        [
            "System.Text.StringBuilder"
            "System.Environment"
            "System.Collections.Generic.Dictionary<,>"
        ]

    type Forest =
        {
            Tops : TopType list
            Forwarders : string list
        }

    /// The probability that a nested type is `public`, fuzzed per forest, so that some forests
    /// have long public chains and some have almost none.
    let private genNested (publicBias : float) : int -> Gen<NestedType> =
        let rec genAt (depth : int) : Gen<NestedType> =
            gen {
                let! roll = Gen.choose (0, 999)

                let! keyword =
                    if float roll < publicBias * 1000.0 then
                        Gen.constant "public"
                    else
                        Gen.elements nestedKeywords

                let! arity = Gen.frequency [ 6, Gen.constant 0 ; 2, Gen.constant 1 ; 1, Gen.constant 2 ]

                let! childCount =
                    if depth >= 3 then
                        Gen.constant 0
                    else
                        Gen.frequency
                            [
                                3, Gen.constant 0
                                3, Gen.constant 1
                                2, Gen.constant 2
                                1, Gen.constant 3
                            ]

                let! children = Gen.listOfLength childCount (genAt (depth + 1))

                return
                    {
                        Keyword = keyword
                        Arity = arity
                        Children = children
                    }
            }

        genAt

    let private genTop (publicBias : float) (allowFile : bool) : Gen<TopType> =
        gen {
            let! access =
                Gen.frequency
                    [
                        int (publicBias * 10.0) + 1, Gen.constant TopAccess.Public
                        3, Gen.constant TopAccess.Internal
                        (if allowFile then 1 else 0), Gen.constant TopAccess.File
                    ]

            let! inNamespace = Gen.elements [ true ; false ]
            let! arity = Gen.frequency [ 6, Gen.constant 0 ; 2, Gen.constant 1 ; 1, Gen.constant 2 ]

            let! childCount =
                Gen.frequency
                    [
                        2, Gen.constant 0
                        3, Gen.constant 1
                        3, Gen.constant 2
                        1, Gen.constant 3
                    ]

            let! children = Gen.listOfLength childCount (genNested publicBias 1)

            return
                {
                    Access = access
                    InNamespace = inNamespace
                    Arity = arity
                    Children = children
                }
        }

    let private genTops (allowFile : bool) : Gen<TopType list> =
        gen {
            let! publicBias = Gen.elements [ 0.1 ; 0.5 ; 0.9 ]
            let! count = Gen.choose (1, 5)
            return! Gen.listOfLength count (genTop publicBias allowFile)
        }

    let private genForest : Gen<Forest> =
        gen {
            let! tops = genTops true
            let! forwarders = Gen.subListOf forwardable

            return
                {
                    Tops = tops
                    Forwarders = List.ofSeq forwarders
                }
        }

    let private typeParameters (prefix : string) (arity : int) : string =
        if arity = 0 then
            ""
        else
            List.init arity (fun i -> $"%s{prefix}%d{i}")
            |> String.concat ", "
            |> sprintf "<%s>"

    /// The C# for `tops`, each type named after its path so that no two names collide, prefixed by
    /// `namePrefix`.
    let private topsSource (namePrefix : string) (tops : TopType list) : string =
        let builder = Text.StringBuilder ()

        let rec emitNested (indent : string) (name : string) (depth : int) (nested : NestedType) : unit =
            let parameters = typeParameters $"P%d{depth}_" nested.Arity

            builder.Append ($"%s{indent}%s{nested.Keyword} class %s{name}%s{parameters}\n%s{indent}{{\n")
            |> ignore

            nested.Children
            |> List.iteri (fun i child -> emitNested (indent + "    ") $"%s{name}_%d{i}" (depth + 1) child)

            builder.Append ($"%s{indent}}}\n") |> ignore

        tops
        |> List.iteri (fun i top ->
            let keyword =
                match top.Access with
                | TopAccess.Public -> "public"
                | TopAccess.Internal -> "internal"
                | TopAccess.File -> "file"

            let name = $"%s{namePrefix}T%d{i}"

            if top.InNamespace then
                builder.Append ($"namespace Ns%d{i}\n{{\n") |> ignore

            let parameters = typeParameters "P0_" top.Arity

            builder.Append ($"%s{keyword} class %s{name}%s{parameters}\n{{\n") |> ignore

            top.Children
            |> List.iteri (fun j child -> emitNested "    " $"%s{name}_%d{j}" 1 child)

            builder.Append "}\n" |> ignore

            if top.InNamespace then
                builder.Append "}\n" |> ignore
        )

        builder.ToString ()

    let private forestSource (forest : Forest) : string =
        let forwarders =
            forest.Forwarders
            |> List.map (fun t -> $"[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(%s{t}))]\n")
            |> String.concat ""

        forwarders + topsSource "" forest.Tops

    /// `image` with the Flags column of TypeDef row 1, the global `<Module>` type, set to `Public`.
    let private withModuleTypePublic (image : byte[]) : byte[] =
        let copy = Array.copy image
        use pe = new PEReader (new MemoryStream (image))
        let metadata = pe.GetMetadataReader ()

        let offset =
            pe.PEHeaders.MetadataStartOffset
            + metadata.GetTableMetadataOffset TableIndex.TypeDef

        let moduleType = metadata.GetTypeDefinition (MetadataTokens.TypeDefinitionHandle 1)

        if BitConverter.ToUInt32 (copy, offset) <> uint32 moduleType.Attributes then
            failwith "TypeDef row 1's Flags column is not where it was expected"

        if
            moduleType.Attributes &&& TypeAttributes.VisibilityMask
            <> TypeAttributes.NotPublic
        then
            failwith $"TypeDef row 1 is already %O{moduleType.Attributes}"

        BitConverter.GetBytes (uint32 TypeAttributes.Public)
        |> Array.iteri (fun i b -> copy.[offset + i] <- b)

        copy

    /// What the host's `GetExportedTypes()` does with `image`, loaded from its bytes into a collectible
    /// context of its own: the metadata tokens of the array, or the `FileLoadException` it throws.
    let private hostExportedTypes (image : byte[]) : Result<int list, FileLoadException> =
        let context =
            AssemblyLoadContext ("ExportedTypeSelectionOracle", isCollectible = true)

        try
            let assembly = context.LoadFromStream (new MemoryStream (image))

            try
                assembly.GetExportedTypes ()
                |> Array.map (fun t -> t.MetadataToken)
                |> List.ofArray
                |> Ok
            with :? FileLoadException as e ->
                Error e
        finally
            context.Unload ()

    let private read (image : byte[]) : DumpedAssembly =
        use stream = new MemoryStream (image)
        AssemblyApi.read loggerFactory None stream

    let private typeDefToken (handle : TypeDefinitionHandle) : int =
        MetadataTokens.GetToken (TypeDefinitionHandle.op_Implicit handle : EntityHandle)

    [<RequireQualifiedAccess>]
    type SingleModuleReach =
        | ModuleTypePublic
        | NestedPublicUnderInternal
        | NestedPublicUnderFamilyOrAssembly
        | KeptAtDepthTwo
        | KeptAtDepthThree
        | KeptGeneric
        | Forwarder
        | FileType
        | NothingKept

    let rec private nestedLabels
        (cover : SingleModuleReach -> unit)
        (enclosersPublic : bool)
        (encloserKeyword : string)
        (depth : int)
        (nested : NestedType)
        : unit
        =
        let isPublic = nested.Keyword = "public"

        if isPublic && encloserKeyword = "internal" then
            cover SingleModuleReach.NestedPublicUnderInternal

        if isPublic && encloserKeyword = "protected internal" then
            cover SingleModuleReach.NestedPublicUnderFamilyOrAssembly

        let kept = enclosersPublic && isPublic

        if kept && depth = 2 then
            cover SingleModuleReach.KeptAtDepthTwo

        if kept && depth = 3 then
            cover SingleModuleReach.KeptAtDepthThree

        if kept && nested.Arity > 0 then
            cover SingleModuleReach.KeptGeneric

        nested.Children
        |> List.iter (nestedLabels cover kept nested.Keyword (depth + 1))

    [<Test>]
    let ``The Definitions are the host's GetExportedTypes, in order, over generated assemblies`` () : unit =
        let property (cover : SingleModuleReach -> unit) (forest : Forest, moduleTypePublic : bool) : unit =
            let image =
                Roslyn.compileAssembly
                    "ExportedTypeSelectionFixture"
                    OutputKind.DynamicallyLinkedLibrary
                    []
                    [ forestSource forest ]

            let image =
                if moduleTypePublic then
                    cover SingleModuleReach.ModuleTypePublic
                    withModuleTypePublic image
                else
                    image

            for top in forest.Tops do
                let isPublic = top.Access = TopAccess.Public

                if top.Access = TopAccess.File then
                    cover SingleModuleReach.FileType

                top.Children
                |> List.iter (
                    nestedLabels
                        cover
                        isPublic
                        (match top.Access with
                         | TopAccess.Public -> "public"
                         | TopAccess.Internal
                         | TopAccess.File -> "internal")
                        1
                )

            if not forest.Forwarders.IsEmpty then
                cover SingleModuleReach.Forwarder

            let expected =
                match hostExportedTypes image with
                | Ok tokens -> tokens
                | Error e -> failwith $"the host threw for a single-module assembly: %O{e}"

            if expected.IsEmpty then
                cover SingleModuleReach.NothingKept

            let selection = ExportedTypeSelection.ofAssembly (read image)

            selection.Definitions |> List.map typeDefToken |> shouldEqual expected
            selection.FileExports |> shouldEqual []

        let arbitrary =
            Arb.fromGen (Gen.zip genForest (Gen.frequency [ 3, Gen.constant false ; 1, Gen.constant true ]))

        let coverage =
            CoverageSample.check (Config.QuickThrowOnFailure.WithMaxTest 60) arbitrary property

        for label, floor in
            [
                SingleModuleReach.ModuleTypePublic, 8
                SingleModuleReach.NestedPublicUnderInternal, 8
                SingleModuleReach.NestedPublicUnderFamilyOrAssembly, 3
                SingleModuleReach.KeptAtDepthTwo, 8
                SingleModuleReach.KeptAtDepthThree, 3
                SingleModuleReach.KeptGeneric, 8
                SingleModuleReach.Forwarder, 20
                SingleModuleReach.FileType, 8
                SingleModuleReach.NothingKept, 2
            ] do
            if coverage.Count label < floor then
                failwith $"the fixed sample reached %A{label} only %d{coverage.Count label} times"

    // ----- Generated multi-module assemblies -----

    /// Whether any type of `tops` is visible outside its assembly, the condition under which Roslyn
    /// writes a File-implemented ExportedType row for it: a type is visible only if its top-level
    /// encloser is public, which is then visible itself.
    let private hasVisible (tops : TopType list) : bool =
        tops |> List.exists (fun top -> top.Access = TopAccess.Public)

    [<RequireQualifiedAccess>]
    type MultiModuleReach =
        | FirstModuleHiddenSecondVisible
        | FirstModuleVisible
        | NeitherVisible
        | NestedExport

    [<Test>]
    let ``The first File export names the File the host's FileLoadException names`` () : unit =
        let property (cover : MultiModuleReach -> unit) (first : TopType list, second : TopType list) : unit =
            let modules =
                [ "A", first ; "B", second ]
                |> List.map (fun (prefix, tops) ->
                    // Roslyn names a netmodule after its assembly name.
                    let name = $"%s{prefix}.netmodule"

                    let image =
                        Roslyn.compileAssembly prefix OutputKind.NetModule [] [ topsSource prefix tops ]

                    MetadataReference.CreateFromImage (image, MetadataReferenceProperties.Module, filePath = name)
                    :> MetadataReference
                )

            let manifest =
                Roslyn.compileAssembly
                    "MultiModuleFixture"
                    OutputKind.DynamicallyLinkedLibrary
                    modules
                    [ "internal class ManifestOnly { public class NestedInManifest { } }" ]

            match hasVisible first, hasVisible second with
            | false, true -> cover MultiModuleReach.FirstModuleHiddenSecondVisible
            | true, _ -> cover MultiModuleReach.FirstModuleVisible
            | false, false -> cover MultiModuleReach.NeitherVisible

            let assembly = read manifest

            if
                assembly.ExportedTypes.Values
                |> Seq.exists (fun row ->
                    match row.Data with
                    | ExportedTypeData.ParentExportedType _ -> true
                    | _ -> false
                )
            then
                cover MultiModuleReach.NestedExport

            let selection = ExportedTypeSelection.ofAssembly assembly

            let fileName (file : AssemblyFileHandle) : string =
                let metadata = assembly.PeReader.GetMetadataReader ()
                metadata.GetString (metadata.GetAssemblyFile file).Name

            selection.Definitions |> shouldEqual []

            match hostExportedTypes manifest, selection.FileExports with
            | Ok [], [] -> ()
            | Ok tokens, _ -> failwith $"the host returned %A{tokens} for a manifest with no public type of its own"
            | Error e, first :: _ -> fileName first.File |> shouldEqual e.FileName
            | Error e, [] -> failwith $"the host threw for %s{e.FileName}, but FileExports is empty"

        let arbitrary = Arb.fromGen (Gen.zip (genTops false) (genTops false))

        let coverage =
            CoverageSample.check (Config.QuickThrowOnFailure.WithMaxTest 40) arbitrary property

        for label, floor in
            [
                MultiModuleReach.FirstModuleHiddenSecondVisible, 4
                MultiModuleReach.FirstModuleVisible, 10
                MultiModuleReach.NeitherVisible, 1
                MultiModuleReach.NestedExport, 10
            ] do
            if coverage.Count label < floor then
                failwith $"the fixed sample reached %A{label} only %d{coverage.Count label} times"

    // ----- Generated ExportedType rows -----

    /// An `Implementation` column as a raw token, as CoreCLR's loop reads it.
    let private fileTable : int = 0x26
    let private exportedTypeTable : int = 0x27
    let private assemblyRefTable : int = 0x23

    /// CoreCLR's loop over the ExportedType table (assemblynative.cpp:850-894 on .NET 10), transcribed
    /// over raw tokens: each row's flags and `Implementation` token, keyed by rid. The rows it keeps, in
    /// rid order, each with the File token it ended at.
    let private coreClrModel (rows : Map<int, uint32 * int>) : (int * int) list =
        let typeFromToken (token : int) : int = (token >>> 24) &&& 0xFF
        let isTdNestedPublic (flags : uint32) : bool = flags &&& 7u = 2u
        let isTdPublic (flags : uint32) : bool = flags &&& 7u = 1u

        [
            for KeyValue (rid, (rowFlags, rowImpl)) in rows do
                let mutable dwFlags = rowFlags
                let mutable mdImpl = rowImpl

                while typeFromToken mdImpl = exportedTypeTable
                      && mdImpl <> (exportedTypeTable <<< 24)
                      && isTdNestedPublic dwFlags do
                    let flags, impl = rows.[mdImpl &&& 0x00FFFFFF]
                    dwFlags <- flags
                    mdImpl <- impl

                if
                    typeFromToken mdImpl = fileTable
                    && mdImpl <> (fileTable <<< 24)
                    && isTdPublic dwFlags
                then
                    yield rid, mdImpl
        ]

    /// One generated row: its visibility, and what its `Implementation` names.
    [<RequireQualifiedAccess>]
    type GeneratedImplementation =
        | Forwarder
        | File of rid : int
        | Parent of rid : int

    let private exportedTypeRow
        (rid : int)
        (visibility : TypeAttributes)
        (implementation : GeneratedImplementation)
        : ExportedType
        =
        {
            Handle = MetadataTokens.ExportedTypeHandle rid
            Name = $"Row%d{rid}"
            Namespace = None
            NamespaceDefn = Unchecked.defaultof<NamespaceDefinitionHandle>
            TypeAttrs = visibility
            Data =
                match implementation with
                | GeneratedImplementation.Forwarder ->
                    ExportedTypeData.ForwardsTo (MetadataTokens.AssemblyReferenceHandle 1)
                | GeneratedImplementation.File file ->
                    ExportedTypeData.AssemblyFile (MetadataTokens.AssemblyFileHandle file)
                | GeneratedImplementation.Parent parent ->
                    ExportedTypeData.ParentExportedType (MetadataTokens.ExportedTypeHandle parent)
        }

    let private rawImplementation (implementation : GeneratedImplementation) : int =
        match implementation with
        | GeneratedImplementation.Forwarder -> (assemblyRefTable <<< 24) ||| 1
        | GeneratedImplementation.File file -> (fileTable <<< 24) ||| file
        | GeneratedImplementation.Parent parent -> (exportedTypeTable <<< 24) ||| parent

    let private visibilities : TypeAttributes list =
        [
            TypeAttributes.NotPublic
            TypeAttributes.Public
            TypeAttributes.NestedPublic
            TypeAttributes.NestedPrivate
            TypeAttributes.NestedFamily
            TypeAttributes.NestedAssembly
            TypeAttributes.NestedFamANDAssem
            TypeAttributes.NestedFamORAssem
        ]

    /// Rows 1 to n, acyclic: each row's parent, if any, comes earlier in a random order of the rows,
    /// which may put it at a higher rid. A nil parent or a nil File stands for CoreCLR's `mdExportedTypeNil`
    /// and `mdFileNil`.
    let private genRows : Gen<(TypeAttributes * GeneratedImplementation) list> =
        gen {
            let! count = Gen.choose (1, 12)
            let! order = Gen.shuffle [| 1..count |]
            // How strongly the visibilities lean to the ones that continue (`NestedPublic` under a
            // parent) or end (`Public` at a File) a kept chain.
            let! bias = Gen.elements [ 0 ; 3 ; 10 ]

            let! assigned =
                order
                |> Array.mapi (fun position rid ->
                    gen {
                        let! implementation =
                            Gen.frequency
                                [
                                    2, Gen.constant GeneratedImplementation.Forwarder
                                    4, Gen.choose (1, 3) |> Gen.map GeneratedImplementation.File
                                    1, Gen.constant (GeneratedImplementation.File 0)
                                    (if position = 0 then 0 else 6),
                                    Gen.choose (0, position - 1 |> max 0)
                                    |> Gen.map (fun j -> GeneratedImplementation.Parent order.[j])
                                    1, Gen.constant (GeneratedImplementation.Parent 0)
                                ]

                        let favoured =
                            match implementation with
                            | GeneratedImplementation.Parent _ -> TypeAttributes.NestedPublic
                            | GeneratedImplementation.File _
                            | GeneratedImplementation.Forwarder -> TypeAttributes.Public

                        let! visibility = Gen.frequency [ bias, Gen.constant favoured ; 8, Gen.elements visibilities ]

                        return rid, (visibility, implementation)
                    }
                )
                |> List.ofArray
                |> Gen.sequenceToList

            return assigned |> Seq.sortBy fst |> Seq.map snd |> List.ofSeq
        }

    [<RequireQualifiedAccess>]
    type RowReach =
        | KeptThroughParent
        | KeptThroughTwoParents
        | KeptThroughLaterRid
        | KeptAtFile
        | NestedPublicUnderNotPublicFile
        | PublicUnderParent
        | NilParentNestedPublic
        | NilFilePublic
        | ForwarderPublic

    [<Test>]
    let ``The File-export walk keeps what CoreCLR's loop keeps, over generated rows`` () : unit =
        let property (cover : RowReach -> unit) (generated : (TypeAttributes * GeneratedImplementation) list) : unit =
            let rows =
                generated
                |> List.mapi (fun i (visibility, implementation) -> exportedTypeRow (i + 1) visibility implementation)

            let table =
                rows
                |> List.map (fun row -> KeyValuePair (row.Handle, row))
                |> ImmutableDictionary.CreateRange

            let model =
                generated
                |> List.mapi (fun i (visibility, implementation) ->
                    i + 1, (uint32 visibility, rawImplementation implementation)
                )
                |> Map.ofList
                |> coreClrModel

            let actual =
                ExportedTypeSelection.fileExports table
                |> List.map (fun export ->
                    MetadataTokens.GetRowNumber (ExportedTypeHandle.op_Implicit export.Export : EntityHandle),
                    MetadataTokens.GetToken (AssemblyFileHandle.op_Implicit export.File : EntityHandle)
                )

            let byRid = generated |> List.mapi (fun i row -> i + 1, row) |> Map.ofList

            let rec parentsWalked (rid : int) : int =
                match byRid.[rid] with
                | _, GeneratedImplementation.Parent parent when parent <> 0 -> 1 + parentsWalked parent
                | _ -> 0

            for rid, _ in model do
                match parentsWalked rid with
                | 0 -> cover RowReach.KeptAtFile
                | 1 -> cover RowReach.KeptThroughParent
                | _ -> cover RowReach.KeptThroughTwoParents

                match byRid.[rid] with
                | _, GeneratedImplementation.Parent parent when parent > rid -> cover RowReach.KeptThroughLaterRid
                | _ -> ()

            for rid, (visibility, implementation) in Map.toSeq byRid do
                match visibility, implementation with
                | TypeAttributes.NestedPublic, GeneratedImplementation.Parent parent when parent <> 0 ->
                    match byRid.[parent] with
                    | TypeAttributes.NotPublic, GeneratedImplementation.File file when file <> 0 ->
                        cover RowReach.NestedPublicUnderNotPublicFile
                    | _ -> ()
                | TypeAttributes.Public, GeneratedImplementation.Parent parent when parent <> 0 ->
                    cover RowReach.PublicUnderParent
                | TypeAttributes.NestedPublic, GeneratedImplementation.Parent 0 -> cover RowReach.NilParentNestedPublic
                | TypeAttributes.Public, GeneratedImplementation.File 0 -> cover RowReach.NilFilePublic
                | TypeAttributes.Public, GeneratedImplementation.Forwarder -> cover RowReach.ForwarderPublic
                | _ -> ()

            actual |> shouldEqual model

        let coverage =
            CoverageSample.check (Config.QuickThrowOnFailure.WithMaxTest 2000) (Arb.fromGen genRows) property

        for label, floor in
            [
                RowReach.KeptThroughParent, 200
                RowReach.KeptThroughTwoParents, 100
                RowReach.KeptThroughLaterRid, 100
                RowReach.KeptAtFile, 200
                RowReach.NestedPublicUnderNotPublicFile, 50
                RowReach.PublicUnderParent, 100
                RowReach.NilParentNestedPublic, 50
                RowReach.NilFilePublic, 50
                RowReach.ForwarderPublic, 100
            ] do
            if coverage.Count label < floor then
                failwith $"the fixed sample reached %A{label} only %d{coverage.Count label} times"

    let private tableOf (rows : ExportedType list) : ImmutableDictionary<ExportedTypeHandle, ExportedType> =
        rows
        |> List.map (fun row -> KeyValuePair (row.Handle, row))
        |> ImmutableDictionary.CreateRange

    [<Test>]
    let ``A cycle of NestedPublic rows fails, naming a row`` () : unit =
        let table =
            tableOf
                [
                    exportedTypeRow 1 TypeAttributes.NestedPublic (GeneratedImplementation.Parent 2)
                    exportedTypeRow 2 TypeAttributes.NestedPublic (GeneratedImplementation.Parent 3)
                    exportedTypeRow 3 TypeAttributes.NestedPublic (GeneratedImplementation.Parent 1)
                ]

        // On a separate thread with a bound, so that a walk which never ends fails this test rather
        // than hanging the suite.
        let walk =
            Task.Run<exn option> (fun () ->
                try
                    ExportedTypeSelection.fileExports table |> ignore<FileExport list>
                    None
                with e ->
                    Some e
            )

        if not (walk.Wait (TimeSpan.FromSeconds 30.0)) then
            failwith "the walk did not finish within 30 seconds"

        match walk.Result with
        | None -> failwith "the walk accepted a cycle"
        | Some e ->
            e.Message |> shouldContainText "Row1"
            e.Message |> shouldContainText "cycle"

    [<Test>]
    let ``A NestedPublic row whose parent is not in the table fails, naming the row`` () : unit =
        let table =
            tableOf
                [
                    exportedTypeRow 1 TypeAttributes.Public (GeneratedImplementation.File 1)
                    exportedTypeRow 2 TypeAttributes.NestedPublic (GeneratedImplementation.Parent 7)
                ]

        let e =
            Assert.Throws<Exception> (fun () -> ExportedTypeSelection.fileExports table |> ignore<FileExport list>)

        e.Message |> shouldContainText "Row2"
        e.Message |> shouldContainText "0x27000007"

    [<Test>]
    let ``A row whose parent is not in the table is not followed unless it is NestedPublic`` () : unit =
        // CoreCLR reads the parent only to continue a `NestedPublic` chain, so an absent parent under
        // any other visibility is never looked up.
        let table =
            tableOf
                [
                    exportedTypeRow 1 TypeAttributes.Public (GeneratedImplementation.File 1)
                    exportedTypeRow 2 TypeAttributes.NestedAssembly (GeneratedImplementation.Parent 7)
                ]

        ExportedTypeSelection.fileExports table
        |> shouldEqual
            [
                {
                    Export = MetadataTokens.ExportedTypeHandle 1
                    File = MetadataTokens.AssemblyFileHandle 1
                }
            ]
