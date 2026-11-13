namespace WoofWare.PawPrint

open System.Collections.Generic
open System.Collections.Immutable
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335

/// What owns a generic parameter: ECMA-335 II.24.2.6's <c>TypeOrMethodDef</c> coded index, the
/// GenericParam table's <c>Owner</c> column.
[<RequireQualifiedAccess>]
type GenericParamOwner =
    | TypeDef of TypeDefinitionHandle
    | MethodDef of MethodDefinitionHandle

/// One row of the GenericParam table (ECMA-335 II.22.20), as far as finding an owner's parameters
/// needs it.
type GenericParamOwnerRow =
    {
        /// The row itself, whose RID is its place in the table.
        Handle : GenericParameterHandle
        Owner : GenericParamOwner
        /// The row's <c>Number</c> column: the parameter's position in its owner's list.
        Number : int
    }

/// Why an image's GenericParam table is refused: in RID order, an owner's parameters must be numbered
/// 0, 1, ..., n-1, and <see cref="Row"/> is the first row that is not.
type GenericParamNumberingError =
    {
        Owner : GenericParamOwner
        Row : GenericParameterHandle
        /// How many rows of <see cref="Owner"/> precede <see cref="Row"/>, which is the number it
        /// should have had.
        Expected : int
        Actual : int
    }

/// <summary>
/// Each owner's generic parameters in one metadata image, in RID order: what
/// <c>TypeDefinition.GetGenericParameters</c> and <c>MethodDefinition.GetGenericParameters</c>
/// answer, found without trusting the image to be sorted by owner.
/// </summary>
/// <remarks>
/// Built by <c>GenericParamOwnerIndex.ofReader</c>, and answers only for the
/// <c>MetadataReader</c> it was built from: a lookup through another reader fails, even one over the
/// same image.
/// </remarks>
type GenericParamOwnerIndex =
    private
        {
            Reader : MetadataReader
            ByOwner : ImmutableDictionary<GenericParamOwner, ImmutableArray<GenericParameterHandle>>
        }

[<RequireQualifiedAccess>]
module GenericParamOwnerIndex =

    /// Every owner's rows, in the order given, which must be RID order starting at RID 1; or the first
    /// row whose number is not the count of its owner's rows before it.
    let internal group
        (rows : GenericParamOwnerRow seq)
        : Result<
              ImmutableDictionary<GenericParamOwner, ImmutableArray<GenericParameterHandle>>,
              GenericParamNumberingError
           >
        =
        let byOwner =
            Dictionary<GenericParamOwner, ImmutableArray<GenericParameterHandle>.Builder> ()

        use rows = rows.GetEnumerator ()
        let mutable previousRid = 0
        let mutable refused : GenericParamNumberingError option = None

        while refused.IsNone && rows.MoveNext () do
            let row = rows.Current

            let rid =
                MetadataTokens.GetRowNumber (GenericParameterHandle.op_Implicit row.Handle : EntityHandle)

            if rid <> previousRid + 1 then
                failwith
                    $"GenericParamOwnerIndex.group: GenericParam row %d{rid} follows row %d{previousRid}; the rows must be the whole table, in RID order"

            previousRid <- rid

            let owned =
                match byOwner.TryGetValue row.Owner with
                | true, owned -> owned
                | false, _ ->
                    let owned = ImmutableArray.CreateBuilder ()
                    byOwner.[row.Owner] <- owned
                    owned

            if row.Number = owned.Count then
                owned.Add row.Handle
            else
                refused <-
                    Some
                        {
                            Owner = row.Owner
                            Row = row.Handle
                            Expected = owned.Count
                            Actual = row.Number
                        }

        match refused with
        | Some error -> Error error
        | None ->
            let result = ImmutableDictionary.CreateBuilder ()

            for KeyValue (owner, owned) in byOwner do
                result.Add (owner, owned.ToImmutable ())

            Ok (result.ToImmutable ())

    /// The index of `reader`'s GenericParam table, read by RID; or the first row whose owner's numbering
    /// it breaks, for which the image is refused.
    let ofReader (reader : MetadataReader) : Result<GenericParamOwnerIndex, GenericParamNumberingError> =
        let ownerOf (handle : GenericParameterHandle) (parent : EntityHandle) : GenericParamOwner =
            match parent.Kind with
            | HandleKind.TypeDefinition -> GenericParamOwner.TypeDef (TypeDefinitionHandle.op_Explicit parent)
            | HandleKind.MethodDefinition -> GenericParamOwner.MethodDef (MethodDefinitionHandle.op_Explicit parent)
            | kind ->
                failwith
                    $"GenericParam row %d{MetadataTokens.GetRowNumber (GenericParameterHandle.op_Implicit handle : EntityHandle)} has an owner of kind %O{kind}, which a TypeOrMethodDef coded index cannot encode"

        seq {
            for rid in 1 .. reader.GetTableRowCount TableIndex.GenericParam do
                let handle = MetadataTokens.GenericParameterHandle rid
                let row = reader.GetGenericParameter handle

                yield
                    {
                        Handle = handle
                        Owner = ownerOf handle row.Parent
                        Number = row.Index
                    }
        }
        |> group
        |> Result.map (fun byOwner ->
            {
                Reader = reader
                ByOwner = byOwner
            }
        )

    /// `owner`'s generic parameters, in RID order, and empty if it has none. `reader` must be the very
    /// reader `index` was built from.
    let parametersOf
        (reader : MetadataReader)
        (owner : GenericParamOwner)
        (index : GenericParamOwnerIndex)
        : ImmutableArray<GenericParameterHandle>
        =
        if not (obj.ReferenceEquals (reader, index.Reader)) then
            failwith
                "GenericParamOwnerIndex.parametersOf: the index was built from another MetadataReader than the one it is asked through, so its handles need not name that reader's rows"

        match index.ByOwner.TryGetValue owner with
        | true, owned -> owned
        | false, _ -> ImmutableArray.Empty
