namespace WoofWare.PawPrint

open System

/// <summary>
/// The order or orders in which CoreCLR's read-write metadata has found a key's rows since the key's owner
/// was created, for a key whose rows were all added before then, as a class's <c>MethodImpl</c> rows are
/// added before <c>CreateType</c> (<c>ReadWriteMetadataLookup.rowsOfKeySinceCreation</c>).
/// </summary>
[<RequireQualifiedAccess>]
type RowsSinceCreation<'row> =
    /// Every read since the key's owner was created finds its rows in this order.
    | OneOrder of 'row list
    /// <summary>
    /// The owner was created while the table had fewer rows than the threshold, and the table has reached it
    /// since: a read made before then found the rows in the order added, and a read made now finds them
    /// reversed. A reader that caches what it found may hold either; one that does not finds the second.
    /// </summary>
    | TwoOrders of inOrderAdded : 'row list * reversed : 'row list

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

    /// <summary>
    /// The order or orders in which CoreCLR's read-write metadata has found a key's rows since the key's owner
    /// was created, given the table's row count when the owner was created and now, and the key's rows in RID
    /// order, all of which were added before the owner was created. One order if the table has fewer than
    /// <see cref="hashThreshold"/> rows now, or had at least that many when the owner was created
    /// (<see cref="rowsOfKey"/> of the count now); otherwise the order added and its reverse.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The count at creation is negative, the count now is smaller than it, or the key has more rows than the
    /// table had at creation.
    /// </exception>
    let rowsOfKeySinceCreation<'row>
        (rowsAtCreation : int)
        (rowsNow : int)
        (matchingInRidOrder : 'row list)
        : RowsSinceCreation<'row>
        =
        if rowsAtCreation < 0 || rowsNow < rowsAtCreation then
            invalidArg
                (nameof rowsNow)
                $"ReadWriteMetadataLookup.rowsOfKeySinceCreation: a table that had %d{rowsAtCreation} rows at creation cannot have %d{rowsNow} now; rows are only added"

        if rowsAtCreation < matchingInRidOrder.Length then
            invalidArg
                (nameof matchingInRidOrder)
                $"ReadWriteMetadataLookup.rowsOfKeySinceCreation: a key added before its owner was created cannot have %d{matchingInRidOrder.Length} rows in a table that then had %d{rowsAtCreation}"

        if rowsAtCreation < hashThreshold && rowsNow >= hashThreshold then
            RowsSinceCreation.TwoOrders (matchingInRidOrder, List.rev matchingInRidOrder)
        else
            RowsSinceCreation.OneOrder (rowsOfKey rowsNow matchingInRidOrder)
