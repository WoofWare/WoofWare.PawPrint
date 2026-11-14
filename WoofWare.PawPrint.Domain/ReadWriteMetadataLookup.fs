namespace WoofWare.PawPrint

open System

/// <summary>
/// The order in which CoreCLR's read-write metadata (<c>CMiniMdRW</c>), which a dynamic module's metadata
/// is, finds the rows of one key in a table it does not keep sorted: the rows of one association in
/// <c>MethodSemantics</c> (<c>FindMethodSemanticsHelper</c>), of one class in <c>MethodImpl</c>
/// (<c>FindMethodImplHelper</c>) and of one parent in <c>CustomAttribute</c>
/// (<c>CommonEnumCustomAttributeByName</c>). Each scans the table in RID order until the table holds 25
/// rows (<c>INDEX_ROW_COUNT_THRESHOLD</c>, md/enc/metamodelrw.cpp), and from then on walks a lookup hash
/// built by <c>GenericAddToHash</c>, whose buckets chain each new entry at the front, so in descending RID
/// order. An image on disk is sorted and read by a binary search instead, which this does not model.
/// </summary>
[<RequireQualifiedAccess>]
module ReadWriteMetadataLookup =

    /// The row count from which CoreCLR walks the hash (<c>INDEX_ROW_COUNT_THRESHOLD</c>).
    let hashThreshold : int = 25

    /// <summary>
    /// The rows of one key in the order CoreCLR's read-write metadata finds them, given the table's row
    /// count and the key's rows in RID order: as given below <see cref="hashThreshold"/> rows, and reversed
    /// from it.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The table's row count is negative, or smaller than the number of rows given.
    /// </exception>
    let rowsOfKey<'row> (tableRowCount : int) (matchingInRidOrder : 'row list) : 'row list =
        if tableRowCount < 0 || tableRowCount < matchingInRidOrder.Length then
            invalidArg
                (nameof tableRowCount)
                $"ReadWriteMetadataLookup.rowsOfKey: a table of %d{tableRowCount} rows cannot hold %d{matchingInRidOrder.Length} rows of one key"

        if tableRowCount >= hashThreshold then
            List.rev matchingInRidOrder
        else
            matchingInRidOrder
