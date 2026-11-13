namespace WoofWare.PawPrint.Test

open System
open System.Collections.Immutable
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// `GenericParamOwnerIndex`: its pure core against a model, over GenericParam tables in any owner
/// order; its `MetadataReader` adapter against the same model, over images `MetadataBuilder` writes,
/// which are sorted; that it answers only through the reader it was built from; and that
/// `Assembly.read` refuses an image whose parameters are misnumbered.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestGenericParamOwnerIndex =

    let private codedIndex (owner : GenericParamOwner) : int =
        match owner with
        | GenericParamOwner.TypeDef h ->
            (MetadataTokens.GetRowNumber (TypeDefinitionHandle.op_Implicit h : EntityHandle)
             <<< 1)
            ||| 0
        | GenericParamOwner.MethodDef h ->
            (MetadataTokens.GetRowNumber (MethodDefinitionHandle.op_Implicit h : EntityHandle)
             <<< 1)
            ||| 1

    /// A GenericParam table: the owners that exist, and each row's owner and number, in RID order.
    type private Table =
        {
            Owners : GenericParamOwner list
            Rows : (GenericParamOwner * int) list
        }

    /// Each owner's rows, by RID, in RID order.
    let private modelGroups (table : Table) : (GenericParamOwner * int list) list =
        [
            for owner in table.Owners do
                yield
                    owner,
                    table.Rows
                    |> List.indexed
                    |> List.filter (fun (_, (o, _)) -> o = owner)
                    |> List.map (fun (i, _) -> i + 1)
        ]

    /// The row the index must refuse, if any: for each owner, the first of its rows whose number is not
    /// its position among them; of those, the one with the lowest RID. As (RID, owner, position, number).
    let private modelError (table : Table) : (int * GenericParamOwner * int * int) option =
        [
            for owner in table.Owners do
                let numbered =
                    table.Rows
                    |> List.indexed
                    |> List.filter (fun (_, (o, _)) -> o = owner)
                    |> List.map (fun (i, (_, number)) -> i + 1, number)

                match
                    numbered
                    |> List.indexed
                    |> List.tryFind (fun (position, (_, number)) -> number <> position)
                with
                | Some (position, (rid, number)) -> yield rid, owner, position, number
                | None -> ()
        ]
        |> List.sortBy (fun (rid, _, _, _) -> rid)
        |> List.tryHead

    /// Whether some owner's rows are not contiguous.
    let private interleaved (table : Table) : bool =
        modelGroups table
        |> List.exists (fun (_, rids) ->
            match rids with
            | [] -> false
            | first :: _ -> List.last rids - first + 1 <> rids.Length
        )

    /// Between one and eight owners, `TypeDef`s numbered from `firstTypeDef`.
    let private genOwners (firstTypeDef : int) : Gen<GenericParamOwner list> =
        gen {
            let! types = Gen.choose (0, 4)
            let! methods = Gen.choose ((if types = 0 then 1 else 0), 4)

            return
                [
                    for i in 0 .. types - 1 do
                        yield GenericParamOwner.TypeDef (MetadataTokens.TypeDefinitionHandle (firstTypeDef + i))
                    for i in 1..methods do
                        yield GenericParamOwner.MethodDef (MetadataTokens.MethodDefinitionHandle i)
                ]
        }

    /// A table over `owners`, each row numbered as its owner's next, then sorted by owner if `sorted`
    /// and renumbered, then, if `corrupt`, one row's number replaced by another.
    let private genTable (sorted : bool) (owners : GenericParamOwner list) : Gen<Table> =
        gen {
            let! picks = Gen.listOf (Gen.elements owners)

            let picks = if sorted then List.sortBy codedIndex picks else picks

            let numbered =
                picks
                |> List.mapFold
                    (fun (seen : Map<int, int>) owner ->
                        let count = seen |> Map.tryFind (codedIndex owner) |> Option.defaultValue 0
                        (owner, count), Map.add (codedIndex owner) (count + 1) seen
                    )
                    Map.empty
                |> fst

            let! corrupt = Gen.elements [ false ; true ]

            let! rows =
                if corrupt && not numbered.IsEmpty then
                    gen {
                        let! victim = Gen.choose (0, numbered.Length - 1)
                        let! shift = Gen.choose (1, 20)

                        return
                            numbered
                            |> List.mapi (fun i (owner, number) ->
                                if i = victim then
                                    owner, (number + shift) % 21
                                else
                                    owner, number
                            )
                    }
                else
                    Gen.constant numbered

            return
                {
                    Owners = owners
                    Rows = rows
                }
        }

    [<RequireQualifiedAccess>]
    type private Outcome =
        | Grouped of interleaved : bool
        | Refused

    /// What `answer` (each owner's rows, by RID, or the refused row) says about `table`, checked against
    /// the model.
    let private checkAgainstModel
        (cover : Outcome -> unit)
        (table : Table)
        (answer : Result<GenericParamOwner -> int list, GenericParamNumberingError>)
        : unit
        =
        match answer, modelError table with
        | Ok parametersOf, None ->
            for owner, rids in modelGroups table do
                parametersOf owner |> shouldEqual rids

            cover (Outcome.Grouped (interleaved table))
        | Error error, Some (rid, owner, position, number) ->
            MetadataTokens.GetRowNumber (GenericParameterHandle.op_Implicit error.Row : EntityHandle)
            |> shouldEqual rid

            error.Owner |> shouldEqual owner
            error.Expected |> shouldEqual position
            error.Actual |> shouldEqual number
            cover Outcome.Refused
        | Ok _, Some refused -> failwith $"grouped a table the model refuses at %A{refused}: %A{table}"
        | Error error, None -> failwith $"refused a table the model groups, at %A{error}: %A{table}"

    let private rowsOf (table : Table) : GenericParamOwnerRow list =
        table.Rows
        |> List.mapi (fun i (owner, number) ->
            {
                Handle = MetadataTokens.GenericParameterHandle (i + 1)
                Owner = owner
                Number = number
            }
        )

    [<Test>]
    let ``the core groups each owner's rows in RID order, or refuses the first misnumbered row`` () : unit =
        let property (cover : Outcome -> unit) (table : Table) : unit =
            GenericParamOwnerIndex.group (rowsOf table)
            |> Result.map (fun byOwner ->
                fun owner ->
                    match byOwner.TryGetValue owner with
                    | true, rows -> rows |> Seq.map MetadataTokens.GetRowNumber |> List.ofSeq
                    | false, _ -> []
            )
            |> checkAgainstModel cover table

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 500)
                (Arb.fromGen (
                    gen {
                        let! sorted = Gen.elements [ false ; true ]
                        let! owners = genOwners 1
                        return! genTable sorted owners
                    }
                ))
                property

        coverage.Count (Outcome.Grouped true) |> shouldBeGreaterThan 50
        coverage.Count (Outcome.Grouped false) |> shouldBeGreaterThan 50
        coverage.Count Outcome.Refused |> shouldBeGreaterThan 100

    [<Test>]
    let ``the core refuses rows that are not the table from RID 1 in order`` () : unit =
        let owner = GenericParamOwner.TypeDef (MetadataTokens.TypeDefinitionHandle 1)

        let row (rid : int) (number : int) : GenericParamOwnerRow =
            {
                Handle = MetadataTokens.GenericParameterHandle rid
                Owner = owner
                Number = number
            }

        for rows in
            [
                [ row 2 0 ]
                [ row 1 0 ; row 3 1 ]
                [ row 2 0 ; row 1 1 ]
                [ row 1 0 ; row 1 1 ]
            ] do
            Assert.Throws<Exception> (fun () -> GenericParamOwnerIndex.group rows |> ignore)
            |> ignore<Exception>

        GenericParamOwnerIndex.group [ row 1 0 ; row 2 1 ]
        |> Result.map (fun byOwner -> byOwner.[owner] |> Seq.map MetadataTokens.GetRowNumber |> List.ofSeq)
        |> shouldEqual (Ok [ 1 ; 2 ])

    /// An image whose TypeDefs are `<Module>` and then `types` classes, whose `methods` methods all belong
    /// to the last of them (or to `<Module>`, if there are none), and whose GenericParam rows are `rows`,
    /// which `MetadataBuilder` requires to be sorted by owner. Each method's signature declares as many
    /// generic parameters as it has rows.
    ///
    /// `MetadataBuilder` also refuses an owner's numbers unless they increase, so the rows are written
    /// numbered 0, 1, ... for each owner, and each row's `Number` is then overwritten in the bytes.
    let private image (types : int) (methods : int) (rows : (GenericParamOwner * int) list) : byte[] =
        let md = MetadataBuilder ()
        let text (s : string) : StringHandle = md.GetOrAddString s

        md.AddModule (0, text "GenericParamOwners", md.GetOrAddGuid (Guid.NewGuid ()), GuidHandle (), GuidHandle ())
        |> ignore<ModuleDefinitionHandle>

        md.AddAssembly (
            text "GenericParamOwners",
            Version (1, 0, 0, 0),
            StringHandle (),
            BlobHandle (),
            enum 0,
            AssemblyHashAlgorithm.None
        )
        |> ignore<AssemblyDefinitionHandle>

        let coreName = typeof<obj>.Assembly.GetName ()

        let coreRef =
            md.AddAssemblyReference (
                text coreName.Name,
                coreName.Version,
                StringHandle (),
                md.GetOrAddBlob (coreName.GetPublicKeyToken ()),
                enum 0,
                BlobHandle ()
            )

        let objectRef =
            md.AddTypeReference (AssemblyReferenceHandle.op_Implicit coreRef, text "System", text "Object")

        md.AddTypeDefinition (
            TypeAttributes.NotPublic,
            StringHandle (),
            text "<Module>",
            EntityHandle (),
            MetadataTokens.FieldDefinitionHandle 1,
            MetadataTokens.MethodDefinitionHandle 1
        )
        |> ignore<TypeDefinitionHandle>

        for i in 1..types do
            md.AddTypeDefinition (
                TypeAttributes.Public ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed,
                StringHandle (),
                text $"C%d{i}",
                TypeReferenceHandle.op_Implicit objectRef,
                MetadataTokens.FieldDefinitionHandle 1,
                // Every type's method list starts at 1, so that the last of them owns every method.
                MetadataTokens.MethodDefinitionHandle 1
            )
            |> ignore<TypeDefinitionHandle>

        let bodies = BlobBuilder ()
        let encoder = MethodBodyStreamEncoder bodies

        for i in 1..methods do
            let arity =
                rows
                |> List.filter (fun (owner, _) ->
                    owner = GenericParamOwner.MethodDef (MetadataTokens.MethodDefinitionHandle i)
                )
                |> List.length

            let il = InstructionEncoder (BlobBuilder ())
            il.OpCode ILOpCode.Ret

            let signature =
                if arity = 0 then
                    [| 0x00uy ; 0uy ; 0x01uy |]
                else
                    [| 0x10uy ; byte arity ; 0uy ; 0x01uy |]

            md.AddMethodDefinition (
                MethodAttributes.Public ||| MethodAttributes.Static,
                MethodImplAttributes.IL,
                text $"M%d{i}",
                md.GetOrAddBlob signature,
                encoder.AddMethodBody il,
                MetadataTokens.ParameterHandle 1
            )
            |> ignore<MethodDefinitionHandle>

        rows
        |> List.iteri (fun i (owner, number) ->
            let parent =
                match owner with
                | GenericParamOwner.TypeDef h -> TypeDefinitionHandle.op_Implicit h : EntityHandle
                | GenericParamOwner.MethodDef h -> MethodDefinitionHandle.op_Implicit h : EntityHandle

            let position =
                rows |> List.take i |> List.filter (fun (o, _) -> o = owner) |> List.length

            md.AddGenericParameter (parent, GenericParameterAttributes.None, text $"P%d{i + 1}", position)
            |> ignore<GenericParameterHandle>
        )

        let pe =
            ManagedPEBuilder (PEHeaderBuilder.CreateLibraryHeader (), MetadataRootBuilder md, bodies)

        let blob = BlobBuilder ()
        pe.Serialize blob |> ignore<BlobContentId>
        let bytes = blob.ToArray ()

        let numberOffsets =
            use pe = new PEReader (ImmutableArray.Create<byte> bytes)
            let reader = pe.GetMetadataReader ()
            let table = reader.GetTableMetadataOffset TableIndex.GenericParam
            let rowSize = reader.GetTableRowSize TableIndex.GenericParam

            // `Number` is the row's first column, two bytes wide (ECMA-335 II.22.20).
            List.init rows.Length (fun i -> pe.PEHeaders.MetadataStartOffset + table + i * rowSize)

        for offset, (_, number) in List.zip numberOffsets rows do
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian (
                Span<byte> (bytes, offset, 2),
                uint16 number
            )

        do
            use pe = new PEReader (ImmutableArray.Create<byte> bytes)
            let reader = pe.GetMetadataReader ()

            rows
            |> List.mapi (fun i _ -> (reader.GetGenericParameter (MetadataTokens.GenericParameterHandle (i + 1))).Index)
            |> shouldEqual (List.map snd rows)

        bytes

    let private typeCount (owners : GenericParamOwner list) : int =
        owners
        |> List.filter (
            function
            | GenericParamOwner.TypeDef _ -> true
            | GenericParamOwner.MethodDef _ -> false
        )
        |> List.length

    [<Test>]
    let ``the adapter reads each row's owner and number by RID`` () : unit =
        let property (cover : Outcome -> unit) (table : Table) : unit =
            let methods = table.Owners.Length - typeCount table.Owners

            use pe =
                new PEReader (ImmutableArray.Create<byte> (image (typeCount table.Owners) methods table.Rows))

            let reader = pe.GetMetadataReader ()

            reader.GetTableRowCount TableIndex.GenericParam |> shouldEqual table.Rows.Length

            GenericParamOwnerIndex.ofReader reader
            |> Result.map (fun index ->
                fun owner ->
                    GenericParamOwnerIndex.parametersOf reader owner index
                    |> Seq.map MetadataTokens.GetRowNumber
                    |> List.ofSeq
            )
            |> checkAgainstModel cover table

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 200)
                // TypeDef 1 is `<Module>`.
                (Arb.fromGen (genOwners 2 |> Gen.bind (genTable true)))
                property

        coverage.Count (Outcome.Grouped false) |> shouldBeGreaterThan 50
        coverage.Count Outcome.Refused |> shouldBeGreaterThan 50

    /// `C1` (TypeDef 2) with two parameters, numbered as given, and its method `M1` (MethodDef 1) with one.
    let private twoOwners (typeNumbers : int * int) : byte[] =
        let c = GenericParamOwner.TypeDef (MetadataTokens.TypeDefinitionHandle 2)
        let m = GenericParamOwner.MethodDef (MetadataTokens.MethodDefinitionHandle 1)
        // MethodDef 1's coded index, 3, sorts before TypeDef 2's, 4.
        image 1 1 [ m, 0 ; c, fst typeNumbers ; c, snd typeNumbers ]

    [<Test>]
    let ``the index answers only through the reader it was built from`` () : unit =
        let bytes = twoOwners (0, 1)
        use built = new PEReader (ImmutableArray.Create<byte> bytes)
        use other = new PEReader (ImmutableArray.Create<byte> bytes)
        let reader = built.GetMetadataReader ()
        let owner = GenericParamOwner.TypeDef (MetadataTokens.TypeDefinitionHandle 2)

        let index =
            match GenericParamOwnerIndex.ofReader reader with
            | Ok index -> index
            | Error error -> failwith $"refused a well-numbered image: %A{error}"

        GenericParamOwnerIndex.parametersOf reader owner index
        |> Seq.map MetadataTokens.GetRowNumber
        |> List.ofSeq
        |> shouldEqual [ 2 ; 3 ]

        // The same image, read again: a reader of its own, which the index must not answer for.
        Assert.Throws<Exception> (fun () ->
            GenericParamOwnerIndex.parametersOf (other.GetMetadataReader ()) owner index
            |> ignore<ImmutableArray<GenericParameterHandle>>
        )
        |> _.Message
        |> shouldContainText "reader"

    let private loggerFactory = snd (LoggerFactory.makeTest ())

    let private read (bytes : byte[]) : DumpedAssembly =
        use stream = new MemoryStream (bytes)
        Assembly.read loggerFactory (Some "GenericParamOwners.dll") stream

    [<Test>]
    let ``Assembly.read reads each owner's parameters through the index`` () : unit =
        let assembly = read (twoOwners (0, 1))

        let names (generics : GenericParamFromMetadata ImmutableArray) : (string * int) list =
            generics |> Seq.map (fun (p, _) -> p.Name, p.SequenceNumber) |> List.ofSeq

        assembly.TypeDefs.[MetadataTokens.TypeDefinitionHandle 2].Generics
        |> names
        |> shouldEqual [ "P2", 0 ; "P3", 1 ]

        assembly.Methods.[MetadataTokens.MethodDefinitionHandle 1].Generics
        |> names
        |> shouldEqual [ "P1", 0 ]

    [<TestCase(0, 0)>]
    [<TestCase(1, 0)>]
    [<TestCase(1, 2)>]
    let ``Assembly.read refuses an image whose parameters are misnumbered`` (first : int, second : int) : unit =
        Assert.Throws<Exception> (fun () -> read (twoOwners (first, second)) |> ignore<DumpedAssembly>)
        |> _.Message
        |> shouldContainText "GenericParam"
