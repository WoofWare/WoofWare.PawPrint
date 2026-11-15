namespace WoofWare.PawPrint.Test

open System.Collections.Immutable
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open System.Runtime.InteropServices
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// `FieldInfo.Offset` is the FieldLayout row (ECMA-335 II.22.16) of the field, its Offset column read
/// as the unsigned four bytes it is. System.Reflection.Metadata's `FieldDefinition.GetOffset` reports
/// an offset above `Int32.MaxValue` as -1, its answer for no row; CoreCLR reads the column raw, and
/// reflects it as a `FieldOffsetAttribute` whose `Value` is those bits as an `int`.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestFieldInfoLayoutOffset =

    [<RequireQualifiedAccess>]
    type private Layout =
        | AutoClass
        | SequentialClass
        | SequentialStruct
        | ExplicitStruct

        /// The host can load the type whatever its rows say: CoreCLR ignores FieldLayout rows on a
        /// type that is not explicitly laid out, and refuses most of them on one that is.
        member this.HostLoads : bool =
            match this with
            | Layout.AutoClass
            | Layout.SequentialClass
            | Layout.SequentialStruct -> true
            | Layout.ExplicitStruct -> false

    /// A written type: its layout, and for each of its `int32` fields its FieldLayout rows, in table
    /// order. II.22.16 allows at most one.
    type private WrittenType =
        {
            Layout : Layout
            Rows : uint32 list list
        }

    /// An image declaring `N.T<i>` for each written type, with `int32` fields `F<j>`. Hand-written,
    /// because a C# compiler writes no row with an offset above `Int32.MaxValue`, nor one for a field
    /// of a type that is not explicitly laid out.
    let private writeImage (types : WrittenType list) : byte[] =
        let builder = MetadataBuilder ()

        let signature =
            let blob = BlobBuilder ()
            BlobEncoder(blob).Field().Type().Int32 ()
            builder.GetOrAddBlob blob

        builder.AddModule (
            0,
            builder.GetOrAddString "LayoutRows.dll",
            builder.GetOrAddGuid (System.Guid "1a7f0000-0000-0000-0000-000000000000"),
            GuidHandle (),
            GuidHandle ()
        )
        |> ignore<ModuleDefinitionHandle>

        builder.AddAssembly (
            builder.GetOrAddString "LayoutRows",
            System.Version (1, 0, 0, 0),
            StringHandle (),
            BlobHandle (),
            enum<AssemblyFlags> 0,
            AssemblyHashAlgorithm.None
        )
        |> ignore<AssemblyDefinitionHandle>

        let systemRuntime : EntityHandle =
            builder.AddAssemblyReference (
                builder.GetOrAddString "System.Runtime",
                System.Version (8, 0, 0, 0),
                StringHandle (),
                builder.GetOrAddBlob [| 0xb0uy ; 0x3fuy ; 0x5fuy ; 0x7fuy ; 0x11uy ; 0xd5uy ; 0x0auy ; 0x3auy |],
                enum<AssemblyFlags> 0,
                BlobHandle ()
            )
            |> AssemblyReferenceHandle.op_Implicit

        let typeRef (name : string) : EntityHandle =
            builder.AddTypeReference (systemRuntime, builder.GetOrAddString "System", builder.GetOrAddString name)
            |> TypeReferenceHandle.op_Implicit

        let objectRef = typeRef "Object"
        let valueTypeRef = typeRef "ValueType"

        builder.AddTypeDefinition (
            enum<TypeAttributes> 0,
            StringHandle (),
            builder.GetOrAddString "<Module>",
            EntityHandle (),
            MetadataTokens.FieldDefinitionHandle 1,
            MetadataTokens.MethodDefinitionHandle 1
        )
        |> ignore<TypeDefinitionHandle>

        let mutable nextField = 1

        types
        |> List.iteri (fun typeIndex written ->
            let attributes, baseType =
                match written.Layout with
                | Layout.AutoClass -> TypeAttributes.Public, objectRef
                | Layout.SequentialClass -> TypeAttributes.Public ||| TypeAttributes.SequentialLayout, objectRef
                | Layout.SequentialStruct ->
                    TypeAttributes.Public
                    ||| TypeAttributes.SequentialLayout
                    ||| TypeAttributes.Sealed,
                    valueTypeRef
                | Layout.ExplicitStruct ->
                    TypeAttributes.Public
                    ||| TypeAttributes.ExplicitLayout
                    ||| TypeAttributes.Sealed,
                    valueTypeRef

            builder.AddTypeDefinition (
                attributes,
                builder.GetOrAddString "N",
                builder.GetOrAddString $"T%d{typeIndex}",
                baseType,
                MetadataTokens.FieldDefinitionHandle nextField,
                MetadataTokens.MethodDefinitionHandle 1
            )
            |> ignore<TypeDefinitionHandle>

            written.Rows
            |> List.iteri (fun fieldIndex rows ->
                let field =
                    builder.AddFieldDefinition (
                        FieldAttributes.Public,
                        builder.GetOrAddString $"F%d{fieldIndex}",
                        signature
                    )

                for offset in rows do
                    // The column is four bytes; `AddFieldLayout` takes them as an `int`.
                    builder.AddFieldLayout (field, int offset)

                nextField <- nextField + 1
            )
        )

        // `MetadataBuilder` refuses to write two rows for one field, as II.22.16 forbids; the
        // refusal test needs them written.
        let suppressValidation =
            types
            |> List.exists (fun t -> t.Rows |> List.exists (fun rows -> rows.Length > 1))

        let peBuilder =
            ManagedPEBuilder (
                PEHeaderBuilder (imageCharacteristics = Characteristics.Dll),
                MetadataRootBuilder (builder, suppressValidation = suppressValidation),
                BlobBuilder ()
            )

        let blob = BlobBuilder ()
        peBuilder.Serialize blob |> ignore<BlobContentId>
        blob.ToArray ()

    /// Each field's `FieldInfo.Offset`, as `FieldInfo.make` reads it out of `image`, in field-row order.
    let private offsetsOf (image : byte[]) : uint32 option list =
        use peReader = new PEReader (ImmutableArray.CreateRange image)
        let mr = peReader.GetMetadataReader ()

        let genericParamOwners =
            match GenericParamOwnerIndex.ofReader mr with
            | Ok index -> index
            | Error error -> failwith $"the written image's generic parameters are misnumbered: %A{error}"

        mr.FieldDefinitions
        |> Seq.map (fun handle ->
            (FieldInfo.make mr genericParamOwners (AssemblyName "LayoutRows") handle (mr.GetFieldDefinition handle))
                .Offset
        )
        |> List.ofSeq

    let private singleRow (rows : uint32 list) : uint32 option =
        match rows with
        | [] -> None
        | [ offset ] -> Some offset
        | _ -> failwith $"expected at most one row, got %A{rows}"

    [<Test>]
    let ``FieldInfo reports offsets at and above 2^31 raw, apart from no row`` () : unit =
        let rows =
            [
                []
                [ 0u ]
                [ 0x7FFFFFFFu ]
                [ 0x80000000u ]
                [ 0xFFFFFFFBu ]
                [ 0xFFFFFFFFu ]
            ]

        let image =
            writeImage
                [
                    {
                        Layout = Layout.SequentialClass
                        Rows = rows
                    }
                ]

        offsetsOf image |> shouldEqual (rows |> List.map singleRow)

    [<Test>]
    let ``FieldInfo refuses a field with two FieldLayout rows`` () : unit =
        let image =
            writeImage
                [
                    {
                        Layout = Layout.ExplicitStruct
                        Rows = [ [ 0u ] ; [ 4u ; 8u ] ; [ 12u ] ]
                    }
                ]

        let ex = Assert.Catch (fun () -> offsetsOf image |> ignore<uint32 option list>)
        ex.Message |> shouldContainText "II.22.16"
        ex.Message |> shouldContainText "F1"

    [<RequireQualifiedAccess>]
    type private Coverage =
        | NoRow
        | BelowTwoToThe31
        | AtOrAboveTwoToThe31
        | AllOnes
        | HostOracle

    /// The oracle is what was written, and for a type the host can load, what the host's reflection
    /// reads back: the `FieldOffsetAttribute` it synthesises from the row, whose `Value` is the
    /// column's bits as an `int`.
    [<Test>]
    let ``FieldInfo reports every drawn FieldLayout row as written and as the host reflects it`` () : unit =
        let offsetGen : Gen<uint32> =
            Gen.frequency
                [
                    2, Gen.choose (0, 64) |> Gen.map uint32
                    2, Gen.elements [ 0x7FFFFFFFu ; 0x80000000u ; 0xFFFFFFFCu ; 0xFFFFFFFFu ]
                    3, ArbMap.defaults |> ArbMap.generate<uint32>
                ]

        let typeGen : Gen<WrittenType> =
            gen {
                let! layout =
                    Gen.elements
                        [
                            Layout.AutoClass
                            Layout.SequentialClass
                            Layout.SequentialStruct
                            Layout.ExplicitStruct
                        ]

                let! fieldCount = Gen.choose (0, 4)

                let! rows =
                    Gen.frequency [ 1, Gen.constant [] ; 3, offsetGen |> Gen.map List.singleton ]
                    |> Gen.listOfLength fieldCount

                return
                    {
                        Layout = layout
                        Rows = rows
                    }
            }

        let property (cover : Coverage -> unit) (types : WrittenType list) : unit =
            let expected = types |> List.collect (fun t -> t.Rows |> List.map singleRow)

            for offset in expected do
                match offset with
                | None -> cover Coverage.NoRow
                | Some 0xFFFFFFFFu -> cover Coverage.AllOnes
                | Some o when o >= 0x80000000u -> cover Coverage.AtOrAboveTwoToThe31
                | Some _ -> cover Coverage.BelowTwoToThe31

            let image = writeImage types
            offsetsOf image |> shouldEqual expected

            let context =
                System.Runtime.Loader.AssemblyLoadContext ("LayoutRows", isCollectible = true)

            try
                let hostAssembly = context.LoadFromStream (new MemoryStream (image))

                types
                |> List.iteri (fun typeIndex written ->
                    if written.Layout.HostLoads && not written.Rows.IsEmpty then
                        cover Coverage.HostOracle

                        let fields =
                            (hostAssembly.GetType ($"N.T%d{typeIndex}", true))
                                .GetFields (BindingFlags.Public ||| BindingFlags.Instance)
                            |> Array.sortBy _.MetadataToken
                            |> Array.map (fun f ->
                                f.GetCustomAttribute<FieldOffsetAttribute> ()
                                |> Option.ofObj
                                |> Option.map (fun a -> uint32 a.Value)
                            )
                            |> List.ofArray

                        fields |> shouldEqual (written.Rows |> List.map singleRow)
                )
            finally
                context.Unload ()

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 200)
                (Arb.fromGen (Gen.choose (1, 4) |> Gen.bind (fun n -> Gen.listOfLength n typeGen)))
                property

        for label in
            [
                Coverage.NoRow
                Coverage.BelowTwoToThe31
                Coverage.AtOrAboveTwoToThe31
                Coverage.AllOnes
                Coverage.HostOracle
            ] do
            coverage.Count label |> shouldBeGreaterThan 15

    /// Every FieldLayout row CoreLib carries is far below 2^31, so System.Reflection.Metadata's reading
    /// is exact there, and the raw reader must agree with it field by field.
    [<Test>]
    let ``FieldInfo agrees with System.Reflection.Metadata over every CoreLib field`` () : unit =
        use peReader = new PEReader (File.OpenRead HostImage.coreLibPath)
        let mr = peReader.GetMetadataReader ()

        let genericParamOwners =
            match GenericParamOwnerIndex.ofReader mr with
            | Ok index -> index
            | Error error -> failwith $"CoreLib's generic parameters are misnumbered: %A{error}"

        let mutable withRow = 0

        for handle in mr.FieldDefinitions do
            let def = mr.GetFieldDefinition handle

            let ours =
                (FieldInfo.make mr genericParamOwners (AssemblyName "System.Private.CoreLib") handle def).Offset

            let srm =
                match def.GetOffset () with
                | -1 -> None
                | o -> Some (uint32 o)

            if ours.IsSome then
                withRow <- withRow + 1

            ours |> shouldEqual srm

        withRow |> shouldBeGreaterThan 100
