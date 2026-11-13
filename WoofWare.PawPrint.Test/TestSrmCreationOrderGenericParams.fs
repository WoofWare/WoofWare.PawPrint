namespace WoofWare.PawPrint.Test

open System
open System.Buffers.Binary
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint
open WoofWare.PawPrint.Test.EmitScopeGen

/// What PawPrint relies on System.Reflection.Metadata to read correctly from an emit image whose
/// GenericParam table is in creation order, not owner order, and declared sorted all the same so that
/// SRM's reader opens it: every read of a row by its RID, and a parameter's constraints, which
/// GenericParamConstraint, genuinely sorted by its owner, holds. An SRM whose reads of such an image
/// change fails here.
///
/// SRM's lookup of an owner's parameters trusts the declaration and answers wrongly on such an image;
/// `GenericParamOwnerIndex` answers in its place, checked here too, and `TestGenericParamOwnerRatchet`
/// holds every other caller to it.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestSrmCreationOrderGenericParams =

    /// How a generated scope's generic parameters are laid out over their owners.
    [<RequireQualifiedAccess>]
    type private Layout =
        /// Each row's owner drawn afresh, so an owner's rows may be split by another's.
        | Interleaved
        /// Each owner's rows one run, the runs in an order drawn at random, as CoreCLR creates them.
        | Contiguous

    /// A scope with `<Module>` and up to five types, up to eight methods among them, a TypeRef and a
    /// TypeSpec for constraints to name, and generic parameters of those owners laid out as `layout`,
    /// each with up to two constraints.
    let private genOps (layout : Layout) : Gen<Op list> =
        gen {
            let! types = Gen.choose (1, 5)
            let! methodOwners = Gen.choose (0, 8) |> Gen.bind (fun n -> Gen.listOfLength n choice)
            let ownerCount = 1 + types + methodOwners.Length

            let genParameter (owner : int) : Gen<Op> =
                gen {
                    let! name = genName
                    let! flags = choice
                    let! constraints = Gen.listOfLength 2 choice |> Gen.bind Gen.subListOf
                    return Op.GenericParam (owner, name, flags, List.ofSeq constraints)
                }

            let! parameters =
                match layout with
                | Layout.Interleaved ->
                    Gen.choose (2, 12)
                    |> Gen.bind (fun n -> Gen.listOfLength n (Gen.choose (0, ownerCount - 1)))
                    |> Gen.bind (fun owners -> owners |> List.ofSeq |> List.map genParameter |> Gen.sequenceToList)
                | Layout.Contiguous ->
                    gen {
                        let! owners = Gen.shuffle [| 0 .. ownerCount - 1 |]
                        let! runs = Gen.choose (2, min 5 ownerCount)
                        let! lengths = Gen.listOfLength runs (Gen.choose (1, 3))

                        let! rows =
                            Seq.zip (Array.truncate runs owners) lengths
                            |> Seq.collect (fun (owner, length) -> List.replicate length owner)
                            |> Seq.map genParameter
                            |> List.ofSeq
                            |> Gen.sequenceToList

                        return rows
                    }

            return
                [
                    yield Op.AssemblyRef ("Ref", 1us, false)
                    yield Op.TypeRef (0, "Target", "N")
                    for i in 1..types do
                        yield Op.TypeDef ($"T%d{i}", "", i, None)
                    for owner in methodOwners do
                        yield Op.Method (owner, "M", 0, [], 0, BodyShape.NoBody)
                    yield Op.TypeSpec 0
                    yield! parameters
                ]
        }

    /// The header's `Sorted` mask (ECMA-335 II.24.2.6), found from where SRM says the `Module` table's
    /// rows start, which is straight after the header and its row counts, and checked by reading the
    /// header's `Valid` mask there too.
    let private sortedMask (pe : PEReader) (reader : MetadataReader) : uint64 =
        let metadata = pe.GetMetadata().GetContent ()

        let valid =
            Enum.GetValues<TableIndex> ()
            |> Array.filter (fun table -> reader.GetTableRowCount table > 0)
            |> Array.fold (fun mask table -> mask ||| (1UL <<< int table)) 0UL

        let present = Numerics.BitOperations.PopCount valid
        let header = reader.GetTableMetadataOffset TableIndex.Module - 4 * present - 24

        let at (offset : int) =
            BinaryPrimitives.ReadUInt64LittleEndian (metadata.AsSpan (header + offset, 8))

        at 8 |> shouldEqual valid
        at 16

    /// What a generated image's GenericParam table was like, for coverage.
    [<RequireQualifiedAccess>]
    type private Shape =
        | InOwnerOrder of Layout
        | OutOfOwnerOrder of Layout
        /// A constraint whose parameter's row is out of owner order.
        | ConstrainedOutOfOrder

    [<Test>]
    let ``SRM reads a GenericParam table written in creation order by RID, as the rows were written`` () : unit =
        let property (cover : Shape -> unit) (layout : Layout) (ops : Op list) : unit =
            let scope = apply Guid.Empty ops
            let genericParams = EmitScope.genericParams scope
            let constraints = EmitScope.genericParamConstraints scope

            let pe, reader = readImage (EmitScopeImage.write scope)
            use _ = pe

            sortedMask pe reader |> shouldEqual (1UL <<< int TableIndex.GenericParam)

            reader.GetTableRowCount TableIndex.GenericParam
            |> shouldEqual genericParams.Count

            // Whether each row's owner sorts before the row's before it.
            let outOfPlace =
                genericParams
                |> Seq.mapi (fun i row -> i > 0 && row.Owner.CodedIndex < genericParams.[i - 1].Owner.CodedIndex)
                |> Array.ofSeq

            for rid in 1 .. genericParams.Count do
                let expected = genericParams.[rid - 1]
                let actual = reader.GetGenericParameter (MetadataTokens.GenericParameterHandle rid)

                token actual.Parent
                |> shouldEqual (
                    match expected.Owner with
                    | TypeOrMethodDef.TypeDef owner -> 0x02000000 ||| owner.Rid
                    | TypeOrMethodDef.MethodDef owner -> 0x06000000 ||| owner.Rid
                )

                actual.Index |> shouldEqual (int expected.Number)
                actual.Attributes |> shouldEqual expected.Flags
                MetadataTokens.GetHeapOffset actual.Name |> shouldEqual expected.Name.Offset

                reader.GetString actual.Name
                |> shouldEqual (EmitScope.stringAt expected.Name scope)

                let owned =
                    constraints
                    |> Seq.indexed
                    |> Seq.filter (fun (_, row) -> row.Owner.Rid = rid)
                    |> Seq.map (fun (i, _) -> i + 1)
                    |> List.ofSeq

                actual.GetConstraints ()
                |> Seq.map (fun h ->
                    MetadataTokens.GetRowNumber (GenericParameterConstraintHandle.op_Implicit h : EntityHandle)
                )
                |> List.ofSeq
                |> shouldEqual owned

                if outOfPlace.[rid - 1] && not owned.IsEmpty then
                    cover Shape.ConstrainedOutOfOrder

            reader.GetTableRowCount TableIndex.GenericParamConstraint
            |> shouldEqual constraints.Count

            for rid in 1 .. constraints.Count do
                let expected = constraints.[rid - 1]

                let actual =
                    reader.GetGenericParameterConstraint (MetadataTokens.GenericParameterConstraintHandle rid)

                MetadataTokens.GetRowNumber (GenericParameterHandle.op_Implicit actual.Parameter : EntityHandle)
                |> shouldEqual expected.Owner.Rid

                token actual.Type
                |> shouldEqual (
                    match expected.Constraint with
                    | TypeDefOrRef.TypeDef target -> 0x02000000 ||| target.Rid
                    | TypeDefOrRef.TypeRef target -> 0x01000000 ||| target.Rid
                    | TypeDefOrRef.TypeSpec target -> 0x1B000000 ||| target.Rid
                )

            // PawPrint's own index of each owner's rows, against the scope.
            let index =
                match GenericParamOwnerIndex.ofReader reader with
                | Ok index -> index
                | Error error -> failwith $"the index refuses a creation-order image: %A{error}"

            let ownedBy (owner : TypeOrMethodDef) : int list =
                genericParams
                |> Seq.indexed
                |> Seq.filter (fun (_, row) -> row.Owner = owner)
                |> Seq.map (fun (i, _) -> i + 1)
                |> List.ofSeq

            for i in 1 .. (EmitScope.typeDefs scope).Count do
                GenericParamOwnerIndex.parametersOf
                    reader
                    (GenericParamOwner.TypeDef (MetadataTokens.TypeDefinitionHandle i))
                    index
                |> Seq.map MetadataTokens.GetRowNumber
                |> List.ofSeq
                |> shouldEqual (ownedBy (TypeOrMethodDef.TypeDef (TypeDefRid i)))

            for i in 1 .. (EmitScope.methodDefs scope).Count do
                GenericParamOwnerIndex.parametersOf
                    reader
                    (GenericParamOwner.MethodDef (MetadataTokens.MethodDefinitionHandle i))
                    index
                |> Seq.map MetadataTokens.GetRowNumber
                |> List.ofSeq
                |> shouldEqual (ownedBy (TypeOrMethodDef.MethodDef (MethodDefRid i)))

            if Array.exists id outOfPlace then
                cover (Shape.OutOfOwnerOrder layout)
            else
                cover (Shape.InOwnerOrder layout)

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 1000)
                (Arb.fromGen (
                    gen {
                        let! layout = Gen.elements [ Layout.Interleaved ; Layout.Contiguous ]
                        let! ops = genOps layout
                        return layout, ops
                    }
                ))
                (fun cover (layout, ops) -> property cover layout ops)

        coverage.Count (Shape.OutOfOwnerOrder Layout.Interleaved)
        |> shouldBeGreaterThan 200

        coverage.Count (Shape.OutOfOwnerOrder Layout.Contiguous)
        |> shouldBeGreaterThan 150

        coverage.Count (Shape.InOwnerOrder Layout.Contiguous) |> shouldBeGreaterThan 20
        coverage.Count Shape.ConstrainedOutOfOrder |> shouldBeGreaterThan 200
