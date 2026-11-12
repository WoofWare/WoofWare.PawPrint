namespace WoofWare.PawPrint.Test

open System
open System.Collections.Immutable
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint
open TypeIdentityTestHelpers

/// <summary>
/// A nested TypeDef's Namespace column is part of its name. ECMA-335 II.22.37 asks that it be null
/// (a warning, not an error) and makes Namespace+Name+encloser the uniqueness key, and CoreCLR's class
/// hash keys a nested row on its namespace and name under its encloser (<c>EEClassHashTable</c>,
/// classhash.cpp), so a name that spells a different namespace, or none, does not find it. Roslyn
/// never emits such a row; these images are written by hand to have several.
/// </summary>
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestNestedTypeNamespace =

    let private libName = "NestedNamespace.Lib"
    let private consumerName = "NestedNamespace.Consumer"
    let private facadeName = "NestedNamespace.Facade"

    /// The (Namespace, Name) of each TypeDef nested in `Probe.Outer`. Three share the simple name
    /// `Sys`, so a lookup by simple name alone cannot tell them apart.
    let private nestedRows : (string * string) list =
        [ "", "Sys" ; "A", "Sys" ; "B", "Sys" ; "A.B", "Deep" ; "A", "Only" ]

    /// The (Namespace, Name) of a TypeDef nested in the `A`/`Sys` row, so that a chain has a
    /// namespaced row at more than one level.
    let private grandchildRow : string * string = "C", "Leaf"

    /// Names to ask for under `Probe.Outer`: every row's own spelling, and others that differ from
    /// one only in namespace, only in case, or not at all similar.
    let private queries : (string * string) list =
        [
            for ns in [ "" ; "A" ; "B" ; "C" ; "a" ; "A.B" ; "a.b" ; "B.A" ] do
                for name in [ "Sys" ; "SYS" ; "Deep" ; "Only" ; "Nope" ] do
                    yield ns, name
        ]

    let private stringOrNil (metadata : MetadataBuilder) (s : string) : StringHandle =
        if s = "" then
            Unchecked.defaultof<StringHandle>
        else
            metadata.GetOrAddString s

    let private startImage (metadata : MetadataBuilder) (assemblyName : string) : AssemblyReferenceHandle =
        metadata.AddModule (
            0,
            metadata.GetOrAddString (assemblyName + ".dll"),
            metadata.GetOrAddGuid (Guid "3f6a2b1c-8d4e-4f07-9a1b-2c3d4e5f6a7b"),
            Unchecked.defaultof<GuidHandle>,
            Unchecked.defaultof<GuidHandle>
        )
        |> ignore<ModuleDefinitionHandle>

        metadata.AddAssembly (
            metadata.GetOrAddString assemblyName,
            Version (1, 0, 0, 0),
            Unchecked.defaultof<StringHandle>,
            Unchecked.defaultof<BlobHandle>,
            Unchecked.defaultof<AssemblyFlags>,
            AssemblyHashAlgorithm.None
        )
        |> ignore<AssemblyDefinitionHandle>

        let corelibRef =
            metadata.AddAssemblyReference (
                metadata.GetOrAddString "System.Private.CoreLib",
                Version (10, 0, 0, 0),
                Unchecked.defaultof<StringHandle>,
                Unchecked.defaultof<BlobHandle>,
                Unchecked.defaultof<AssemblyFlags>,
                Unchecked.defaultof<BlobHandle>
            )

        // The real runtime declines an image with no `<Module>` row, and it is an oracle here.
        metadata.AddTypeDefinition (
            Unchecked.defaultof<TypeAttributes>,
            Unchecked.defaultof<StringHandle>,
            metadata.GetOrAddString "<Module>",
            Unchecked.defaultof<EntityHandle>,
            MetadataTokens.FieldDefinitionHandle 1,
            MetadataTokens.MethodDefinitionHandle 1
        )
        |> ignore<TypeDefinitionHandle>

        corelibRef

    let private referenceTo (metadata : MetadataBuilder) (assemblyName : string) : AssemblyReferenceHandle =
        metadata.AddAssemblyReference (
            metadata.GetOrAddString assemblyName,
            Version (1, 0, 0, 0),
            Unchecked.defaultof<StringHandle>,
            Unchecked.defaultof<BlobHandle>,
            Unchecked.defaultof<AssemblyFlags>,
            Unchecked.defaultof<BlobHandle>
        )

    let private serialise (metadata : MetadataBuilder) : byte[] =
        let peBuilder =
            ManagedPEBuilder (
                PEHeaderBuilder (imageCharacteristics = (Characteristics.ExecutableImage ||| Characteristics.Dll)),
                MetadataRootBuilder metadata,
                BlobBuilder (),
                null,
                null,
                null,
                null,
                0,
                Unchecked.defaultof<MethodDefinitionHandle>,
                CorFlags.ILOnly
            )

        let peImage = BlobBuilder ()
        peBuilder.Serialize peImage |> ignore<BlobContentId>
        peImage.ToArray ()

    /// `Probe.Outer`, with `nestedRows` nested in it, and `grandchildRow` nested in its `A`/`Sys`.
    let private libImage : Lazy<byte[]> =
        lazy
            let metadata = MetadataBuilder ()
            let corelibRef = startImage metadata libName

            let objectRef =
                metadata.AddTypeReference (
                    (AssemblyReferenceHandle.op_Implicit corelibRef : EntityHandle),
                    metadata.GetOrAddString "System",
                    metadata.GetOrAddString "Object"
                )

            let outer =
                metadata.AddTypeDefinition (
                    TypeAttributes.Public ||| TypeAttributes.Class,
                    metadata.GetOrAddString "Probe",
                    metadata.GetOrAddString "Outer",
                    (TypeReferenceHandle.op_Implicit objectRef : EntityHandle),
                    MetadataTokens.FieldDefinitionHandle 1,
                    MetadataTokens.MethodDefinitionHandle 1
                )

            for ns, name in nestedRows do
                let nested =
                    metadata.AddTypeDefinition (
                        TypeAttributes.NestedPublic ||| TypeAttributes.Class,
                        stringOrNil metadata ns,
                        metadata.GetOrAddString name,
                        (TypeReferenceHandle.op_Implicit objectRef : EntityHandle),
                        MetadataTokens.FieldDefinitionHandle 1,
                        MetadataTokens.MethodDefinitionHandle 1
                    )

                metadata.AddNestedType (nested, outer)

                if (ns, name) = ("A", "Sys") then
                    let grandchildNs, grandchildName = grandchildRow

                    let grandchild =
                        metadata.AddTypeDefinition (
                            TypeAttributes.NestedPublic ||| TypeAttributes.Class,
                            stringOrNil metadata grandchildNs,
                            metadata.GetOrAddString grandchildName,
                            (TypeReferenceHandle.op_Implicit objectRef : EntityHandle),
                            MetadataTokens.FieldDefinitionHandle 1,
                            MetadataTokens.MethodDefinitionHandle 1
                        )

                    metadata.AddNestedType (grandchild, nested)

            serialise metadata

    /// One top-level `Consumer.D{i}` per entry of `queries`, deriving from a TypeRef nested in a
    /// TypeRef to `Probe.Outer` that spells the entry's namespace and name. Loading `D{i}` loads its
    /// base, so the host answers for the TypeRef by loading it or throwing `TypeLoadException`. A
    /// last `D{queries.Length}` derives from a TypeRef spelling `grandchildRow`, nested in the
    /// TypeRef that spells the `A`/`Sys` row.
    let private consumerImage : Lazy<byte[]> =
        lazy
            let metadata = MetadataBuilder ()
            startImage metadata consumerName |> ignore<AssemblyReferenceHandle>
            let libRef = referenceTo metadata libName

            let outerRef =
                metadata.AddTypeReference (
                    (AssemblyReferenceHandle.op_Implicit libRef : EntityHandle),
                    metadata.GetOrAddString "Probe",
                    metadata.GetOrAddString "Outer"
                )

            let derive (i : int) (baseRef : TypeReferenceHandle) : unit =
                metadata.AddTypeDefinition (
                    TypeAttributes.Public ||| TypeAttributes.Class,
                    metadata.GetOrAddString "Consumer",
                    metadata.GetOrAddString $"D%d{i}",
                    (TypeReferenceHandle.op_Implicit baseRef : EntityHandle),
                    MetadataTokens.FieldDefinitionHandle 1,
                    MetadataTokens.MethodDefinitionHandle 1
                )
                |> ignore<TypeDefinitionHandle>

            let nestedRefs =
                queries
                |> List.mapi (fun i (ns, name) ->
                    let nestedRef =
                        metadata.AddTypeReference (
                            (TypeReferenceHandle.op_Implicit outerRef : EntityHandle),
                            stringOrNil metadata ns,
                            metadata.GetOrAddString name
                        )

                    derive i nestedRef
                    (ns, name), nestedRef
                )
                |> Map.ofList

            let grandchildNs, grandchildName = grandchildRow

            let grandchildRef =
                metadata.AddTypeReference (
                    (TypeReferenceHandle.op_Implicit nestedRefs.[("A", "Sys")] : EntityHandle),
                    stringOrNil metadata grandchildNs,
                    metadata.GetOrAddString grandchildName
                )

            derive queries.Length grandchildRef

            serialise metadata

    /// A facade forwarding `Probe.Outer` to the library, with one nested ExportedType row per entry
    /// of `queries` under that forwarder.
    let private facadeImage : Lazy<byte[]> =
        lazy
            let metadata = MetadataBuilder ()
            startImage metadata facadeName |> ignore<AssemblyReferenceHandle>
            let libRef = referenceTo metadata libName

            let outerExport =
                metadata.AddExportedType (
                    // ECMA-335 II.23.1.15's `Forwarder` bit, which System.Reflection does not name.
                    enum<TypeAttributes> 0x00200000,
                    metadata.GetOrAddString "Probe",
                    metadata.GetOrAddString "Outer",
                    (AssemblyReferenceHandle.op_Implicit libRef : EntityHandle),
                    0
                )

            for ns, name in queries do
                metadata.AddExportedType (
                    TypeAttributes.NestedPublic,
                    stringOrNil metadata ns,
                    metadata.GetOrAddString name,
                    (ExportedTypeHandle.op_Implicit outerExport : EntityHandle),
                    0
                )
                |> ignore<ExportedTypeHandle>

            serialise metadata

    /// The row of the TypeDef nested in `Probe.Outer` whose Namespace and Name columns are exactly
    /// `ns` and `name`, read with System.Reflection.Metadata rather than through any PawPrint index.
    let private libRowSpelled (ns : string, name : string) : int option =
        use peReader = new PEReader (ImmutableArray.CreateRange (libImage.Force ()))
        let reader = peReader.GetMetadataReader ()

        reader.TypeDefinitions
        |> Seq.filter (fun handle ->
            let ty = reader.GetTypeDefinition handle

            not (ty.GetDeclaringType ()).IsNil
            && reader.GetString ty.Namespace = ns
            && reader.GetString ty.Name = name
        )
        |> Seq.map (fun handle -> MetadataTokens.GetRowNumber (TypeDefinitionHandle.op_Implicit handle : EntityHandle))
        |> Seq.tryExactlyOne

    /// Runs `f` against the images as the host CLR loads them. The context is collectible so the test
    /// host is not permanently populated with the fixture.
    let private withHostAssemblies (images : byte[] list) (f : Reflection.Assembly list -> 'a) : 'a =
        let context =
            Runtime.Loader.AssemblyLoadContext ("nestedTypeNamespaceOracle", isCollectible = true)

        try
            images
            |> List.map (fun image ->
                use stream = new MemoryStream (image)
                context.LoadFromStream stream
            )
            |> f
        finally
            context.Unload ()

    let private rowOfToken (token : int) : int = token &&& 0x00FFFFFF

    let private dumpedLib () : DumpedAssembly =
        dumpedAssembly (Some $"%s{libName}.dll") (libImage.Force ())

    let private outerOf (lib : DumpedAssembly) : TypeInfo<GenericParamFromMetadata, TypeDefn> =
        getTopLevelTypeDef lib "Probe" "Outer"

    /// The images are what they claim to be, so that a lookup answering by simple name alone cannot
    /// pass the tests below by luck.
    [<Test>]
    let ``the library has several nested rows sharing a simple name`` () : unit =
        nestedRows
        |> List.map libRowSpelled
        |> List.choose id
        |> List.distinct
        |> List.length
        |> shouldEqual nestedRows.Length

    /// `Assembly.GetType("Probe.Outer+" + segment, ignoreCase)` reaches
    /// `AssemblyNative_GetTypeCore` with `segment` as a nested name unsplit, because CoreLib's type
    /// name parser splits nested names only at '+'. The QCall splits the segment at its last '.'
    /// exactly as it does a top-level name and looks the two halves up as namespace and name.
    [<Test>]
    let ``a GetTypeCore nested segment names the row whose namespace and name it spells, as on the host`` () : unit =
        let lib = dumpedLib ()
        let outer = outerOf lib

        let cases =
            [
                for ignoreCase in [ false ; true ] do
                    for ns, name in queries do
                        let segment = if ns = "" then name else $"%s{ns}.%s{name}"
                        yield ignoreCase, segment
            ]

        let pawPrint =
            cases
            |> List.map (fun (ignoreCase, segment) ->
                match NativeRuntimeAssembly.tryNestedTypeDefOfSegment ignoreCase lib outer.TypeDefHandle segment with
                | Ok found ->
                    found
                    |> Option.map (fun ty ->
                        MetadataTokens.GetRowNumber (TypeDefinitionHandle.op_Implicit ty.TypeDefHandle : EntityHandle)
                    )
                | Error refusal -> failwith $"%s{segment} (ignoreCase=%b{ignoreCase}): unexpected refusal %O{refusal}"
            )

        let host =
            withHostAssemblies
                [ libImage.Force () ]
                (fun assemblies ->
                    let assembly = List.exactlyOne assemblies

                    cases
                    |> List.map (fun (ignoreCase, segment) ->
                        assembly.GetType ($"Probe.Outer+%s{segment}", false, ignoreCase)
                        |> Option.ofObj
                        |> Option.map (fun ty -> rowOfToken ty.MetadataToken)
                    )
                )

        // Not vacuous: the host finds every row, and misses too.
        host
        |> List.choose id
        |> List.distinct
        |> List.length
        |> shouldEqual nestedRows.Length

        host |> List.exists Option.isNone |> shouldEqual true

        List.zip cases pawPrint |> shouldEqual (List.zip cases host)

    /// CoreCLR resolves a TypeRef whose scope is another TypeRef by the referencing row's namespace
    /// and name (`ComputeHashFunctionWithTypeRef`, then `CompareKeys`, classhash.cpp).
    [<Test>]
    let ``a nested TypeRef resolves to the row whose namespace and name it spells, as on the host`` () : unit =
        let lib = dumpedLib ()

        let consumer =
            dumpedAssembly (Some $"%s{consumerName}.dll") (consumerImage.Force ())

        let assemblies = loadedAssemblies [ lib ; consumer ]

        let pawPrint =
            queries
            |> List.map (fun (ns, name) ->
                let typeRef =
                    findTypeRef
                        (fun typeRef ->
                            typeRef.Namespace = ns
                            && typeRef.Name = name
                            && match typeRef.ResolutionScope with
                               | TypeRefResolutionScope.TypeRef _ -> true
                               | _ -> false
                        )
                        consumer

                match LoadedTypeResolution.resolveTypeRef assemblies consumer ImmutableArray.Empty typeRef with
                | TypeResolutionResult.Resolved (resolvedIn, identity, _) ->
                    resolvedIn.Name.Name |> shouldEqual libName

                    MetadataTokens.GetRowNumber (
                        TypeDefinitionHandle.op_Implicit identity.TypeDefinition.Get : EntityHandle
                    )
                    |> Some
                | TypeResolutionResult.NotFound (TypeResolutionMiss.NestedTypeAbsent _) -> None
                | other -> failwith $"%s{ns}.%s{name}: unexpected resolution %O{other}"
            )

        let host =
            withHostAssemblies
                [ libImage.Force () ; consumerImage.Force () ]
                (fun assemblies ->
                    let consumer = assemblies.[1]

                    queries
                    |> List.mapi (fun i _ ->
                        // Loading the derived type loads its base, so a base that does not
                        // resolve is reported from here.
                        try
                            let derived = consumer.GetType ($"Consumer.D%d{i}", true)
                            Some (rowOfToken derived.BaseType.MetadataToken)
                        with :? TypeLoadException ->
                            None
                    )
                )

        host
        |> List.choose id
        |> List.distinct
        |> List.length
        |> shouldEqual nestedRows.Length

        host |> List.exists Option.isNone |> shouldEqual true

        List.zip queries pawPrint |> shouldEqual (List.zip queries host)

    /// A nested ExportedType row is followed into the forwarder's target by its own namespace and
    /// name, which CoreCLR hashes from the row (`ComputeHashFunctionWithExportedType`, classhash.cpp)
    /// as it does a TypeRef's. No host API resolves such a row on its own, so the oracle is the
    /// library's metadata read directly.
    [<Test>]
    let ``a nested ExportedType resolves to the row whose namespace and name it spells`` () : unit =
        let lib = dumpedLib ()

        let facade = dumpedAssembly (Some $"%s{facadeName}.dll") (facadeImage.Force ())

        let assemblies = loadedAssemblies [ lib ; facade ]

        let pawPrint =
            queries
            |> List.map (fun (ns, name) ->
                let export =
                    findExportedType
                        (fun export ->
                            (export.Namespace |> Option.defaultValue "") = ns
                            && export.Name = name
                            && match export.Data with
                               | ExportedTypeData.ParentExportedType _ -> true
                               | _ -> false
                        )
                        facade

                match LoadedTypeResolution.resolveTypeFromExport facade assemblies ImmutableArray.Empty export with
                | TypeResolutionResult.Resolved (resolvedIn, identity, _) ->
                    resolvedIn.Name.Name |> shouldEqual libName

                    MetadataTokens.GetRowNumber (
                        TypeDefinitionHandle.op_Implicit identity.TypeDefinition.Get : EntityHandle
                    )
                    |> Some
                | TypeResolutionResult.NotFound (TypeResolutionMiss.NestedTypeAbsent _) -> None
                | other -> failwith $"%s{ns}.%s{name}: unexpected resolution %O{other}"
            )

        let expected = queries |> List.map libRowSpelled

        expected |> List.choose id |> List.length |> shouldEqual nestedRows.Length

        List.zip queries pawPrint |> shouldEqual (List.zip queries expected)

    /// CoreCLR renders each row of a nested chain with its own Namespace column
    /// (`TypeString::AppendNestedTypeDef` hands every row to `AppendTypeDef`, which passes the row's
    /// namespace to `TypeNameBuilder::AddName`), so the `A`/`Sys` row under `Probe.Outer` is
    /// `Probe.Outer+A.Sys`. Without FormatNamespace, only the innermost row's name is rendered.
    [<Test>]
    let ``a nested TypeDef renders with its own namespace, as on the host`` () : unit =
        let lib = dumpedLib ()

        let nested =
            lib.TypeDefs.Values
            |> Seq.filter (fun ty -> ty.IsNested)
            |> Seq.map (fun ty ->
                MetadataTokens.GetRowNumber (TypeDefinitionHandle.op_Implicit ty.TypeDefHandle : EntityHandle), ty
            )
            |> Seq.sortBy fst
            |> List.ofSeq

        let pawPrint =
            nested
            |> List.map (fun (row, ty) ->
                row,
                (TypeInfo.fullName (fun h -> lib.TypeDefs.[h]) ty,
                 NativeRuntimeTypeHelpers.typeInfoDisplayName true lib ty,
                 NativeRuntimeTypeHelpers.typeInfoDisplayName false lib ty)
            )

        let host =
            withHostAssemblies
                [ libImage.Force () ]
                (fun assemblies ->
                    let hostModule = (List.exactlyOne assemblies).ManifestModule

                    nested
                    |> List.map (fun (row, _) ->
                        let ty = hostModule.ResolveType (0x02000000 ||| row)
                        row, (ty.FullName, ty.ToString (), ty.Name)
                    )
                )

        // Not vacuous: every nested row is compared, a namespaced one at each level of a chain.
        host |> List.length |> shouldEqual (nestedRows.Length + 1)

        host
        |> List.map (fun (_, (full, _, _)) -> full)
        |> shouldContain "Probe.Outer+A.Sys+C.Leaf"

        pawPrint |> shouldEqual host

    /// A nested TypeRef renders as the TypeDef it resolves to (`TypeRef.fullName`), which is how a
    /// self-call named through a TypeRef is recognised (`IntrinsicBody`). The host renders the
    /// resolved type; a TypeRef it does not resolve has no rendering to compare.
    [<Test>]
    let ``a nested TypeRef renders as the TypeDef it resolves to, as on the host`` () : unit =
        let consumer =
            dumpedAssembly (Some $"%s{consumerName}.dll") (consumerImage.Force ())

        let derivedCount = queries.Length + 1

        let pawPrint =
            List.init
                derivedCount
                (fun i ->
                    let derived = getTopLevelTypeDef consumer "Consumer" $"D%d{i}"

                    match derived.BaseType with
                    | Some (BaseTypeInfo.TypeRef baseRef) ->
                        TypeRef.fullName (fun h -> consumer.TypeRefs.[h]) consumer.TypeRefs.[baseRef]
                    | other -> failwith $"Consumer.D%d{i}: expected a TypeRef base, got %O{other}"
                )

        let host =
            withHostAssemblies
                [ libImage.Force () ; consumerImage.Force () ]
                (fun assemblies ->
                    let consumer = assemblies.[1]

                    List.init
                        derivedCount
                        (fun i ->
                            try
                                Some (consumer.GetType($"Consumer.D%d{i}", true).BaseType.FullName)
                            with :? TypeLoadException ->
                                None
                        )
                )

        let resolved =
            List.zip pawPrint host
            |> List.choose (fun (rendered, hostName) -> hostName |> Option.map (fun hostName -> rendered, hostName))

        resolved |> List.map snd |> shouldContain "Probe.Outer+A.Sys+C.Leaf"
        resolved |> List.length |> shouldEqual (nestedRows.Length + 1)

        resolved |> List.map fst |> shouldEqual (resolved |> List.map snd)
