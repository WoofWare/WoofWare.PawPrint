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
        failwith "TODO: GenericParamOwnerIndex.group is a stub"

    /// The index of `reader`'s GenericParam table, read by RID; or the first row whose owner's numbering
    /// it breaks, for which the image is refused.
    let ofReader (reader : MetadataReader) : Result<GenericParamOwnerIndex, GenericParamNumberingError> =
        failwith "TODO: GenericParamOwnerIndex.ofReader is a stub"

    /// `owner`'s generic parameters, in RID order, and empty if it has none. `reader` must be the very
    /// reader `index` was built from.
    let parametersOf
        (reader : MetadataReader)
        (owner : GenericParamOwner)
        (index : GenericParamOwnerIndex)
        : ImmutableArray<GenericParameterHandle>
        =
        failwith "TODO: GenericParamOwnerIndex.parametersOf is a stub"
