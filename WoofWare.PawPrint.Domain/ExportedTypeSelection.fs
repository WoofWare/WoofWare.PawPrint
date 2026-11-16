namespace WoofWare.PawPrint

open System.Collections.Generic
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335

/// An ExportedType row which CoreCLR's `Assembly.GetExportedTypes()` loads through a File of the
/// same assembly, with the File row that its chain of parent rows ends at.
type FileExport =
    {
        /// The ExportedType row.
        Export : ExportedTypeHandle
        /// The File row whose module CoreCLR must load to load the export.
        File : AssemblyFileHandle
    }

/// What CoreCLR's `AssemblyNative_GetExportedTypes` (vm/assemblynative.cpp) selects from one image,
/// before it loads anything: the rows behind `Assembly.GetExportedTypes()`.
type ExportedTypeSelection =
    {
        /// The externally visible TypeDefs, in ascending row order, from row 2.
        Definitions : TypeDefinitionHandle list
        /// The public ExportedType rows implemented by a File, in ascending row order. CoreCLR loads
        /// these after every one of `Definitions`, and on CoreCLR the first of them always throws,
        /// because it supports single-module assemblies only.
        FileExports : FileExport list
    }

[<RequireQualifiedAccess>]
module ExportedTypeSelection =

    /// <summary>
    /// The TypeDef rows of <paramref name="assembly"/> as CoreCLR's <c>EnumTypeDefInit</c> yields them:
    /// ascending from row 2. Row 1 is the global type, and is skipped by its position, whatever its
    /// name or flags.
    /// </summary>
    let enumeratedTypeDefs (assembly : DumpedAssembly) : TypeDefinitionHandle list =
        assembly.TypeDefs.Keys
        |> Seq.map (fun handle -> MetadataTokens.GetRowNumber (TypeDefinitionHandle.op_Implicit handle), handle)
        |> Seq.filter (fun (rowNumber, _) -> rowNumber > 1)
        |> Seq.sortBy fst
        |> Seq.map snd
        |> List.ofSeq

    let private exportedTypeToken (handle : ExportedTypeHandle) : int =
        MetadataTokens.GetToken (ExportedTypeHandle.op_Implicit handle : EntityHandle)

    let private describe (row : WoofWare.PawPrint.ExportedType) : string =
        let name =
            match row.Namespace with
            | Some ns when ns <> "" -> $"%s{ns}.%s{row.Name}"
            | _ -> row.Name

        $"ExportedType 0x%08X{exportedTypeToken row.Handle} (%s{name})"

    /// <summary>
    /// The rows of <paramref name="rows"/> (an ExportedType table, keyed by each row's own handle) which
    /// CoreCLR keeps: those whose chain of <c>NestedPublic</c> rows, followed through each parent
    /// row, ends at a <c>Public</c> row implemented by a File. A forwarder is never kept, nor is a chain
    /// that reaches a nil parent or a nil File. In ascending row order.
    /// </summary>
    /// <remarks>
    /// Fails, naming the row, where a <c>NestedPublic</c> row's parent is not a row of the table, or
    /// where a chain of <c>NestedPublic</c> rows is longer than the table (a cycle). CoreCLR throws on
    /// the first and never finishes on the second.
    /// </remarks>
    let fileExports (rows : IReadOnlyDictionary<ExportedTypeHandle, WoofWare.PawPrint.ExportedType>) : FileExport list =
        let isVisibility (expected : TypeAttributes) (row : WoofWare.PawPrint.ExportedType) : bool =
            row.TypeAttrs &&& TypeAttributes.VisibilityMask = expected

        let rec walk
            (start : WoofWare.PawPrint.ExportedType)
            (current : WoofWare.PawPrint.ExportedType)
            (length : int)
            : FileExport option
            =
            match current.Data with
            | ExportedTypeData.ForwardsTo _ -> None
            | ExportedTypeData.AssemblyFile file ->
                if not file.IsNil && isVisibility TypeAttributes.Public current then
                    Some
                        {
                            Export = start.Handle
                            File = file
                        }
                else
                    None
            | ExportedTypeData.ParentExportedType parent ->
                if parent.IsNil || not (isVisibility TypeAttributes.NestedPublic current) then
                    None
                else
                    match rows.TryGetValue parent with
                    | true, _ when length >= rows.Count ->
                        // The chain already holds as many rows as the table, so the parent is one of them.
                        failwith
                            $"ExportedTypeSelection.fileExports: the parents of %s{describe start} form a cycle of NestedPublic rows"
                    | true, parentRow -> walk start parentRow (length + 1)
                    | false, _ ->
                        failwith
                            $"ExportedTypeSelection.fileExports: %s{describe current}, reached from %s{describe start}, names parent ExportedType 0x%08X{exportedTypeToken parent}, which is not a row of the table"

        rows
        |> Seq.map (fun (KeyValue (handle, row)) ->
            if row.Handle <> handle then
                failwith
                    $"ExportedTypeSelection.fileExports: %s{describe row} is keyed by ExportedType 0x%08X{exportedTypeToken handle}"

            row
        )
        |> Seq.sortBy (fun row -> exportedTypeToken row.Handle)
        |> Seq.choose (fun row -> walk row row 1)
        |> List.ofSeq

    /// <summary>
    /// What <c>Assembly.GetExportedTypes()</c> of <paramref name="assembly"/> loads, without loading
    /// anything: <c>enumeratedTypeDefs</c> filtered by <c>TypeVisibility.isTypeDefExternallyVisible</c>,
    /// and <c>fileExports</c> of its ExportedType table.
    /// </summary>
    /// <remarks>
    /// Fails where <c>TypeVisibility.nestingChain</c> or <c>fileExports</c> fails.
    /// </remarks>
    let ofAssembly (assembly : DumpedAssembly) : ExportedTypeSelection =
        {
            Definitions =
                enumeratedTypeDefs assembly
                |> List.filter (fun handle ->
                    TypeVisibility.isTypeDefExternallyVisible assembly assembly.TypeDefs.[handle]
                )
            FileExports =
                fileExports (
                    assembly.ExportedTypes :> IReadOnlyDictionary<ExportedTypeHandle, WoofWare.PawPrint.ExportedType>
                )
        }
