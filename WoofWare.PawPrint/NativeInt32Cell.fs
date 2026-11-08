namespace WoofWare.PawPrint

/// A fresh native block of one `int32` cell: a local of the VM's, whose address it passes to a
/// CoreLib `[UnmanagedCallersOnly]` entry point as an `int*` for the callee to store a result
/// through, such as the return value `Environment.CallEntryPoint` stores for an `int Main`.
/// Made by `NativeInt32Cell.allocate`, and read by `NativeInt32Cell.read`.
type NativeInt32Cell =
    private
        {
            Block : NativeMemoryBlockId
        }

    /// The pointer to pass as the callee's `int*` argument.
    member this.Pointer : ManagedPointerSource =
        ManagedPointerSource.Byref
            {
                Root = ByrefRoot.NativeMemoryByte (this.Block, 0)
                Projections = []
            }

[<RequireQualifiedAccess>]
module NativeInt32Cell =

    [<Literal>]
    let private Size = 4

    /// A fresh cell holding `contents`.
    ///
    /// The cell is written rather than left to the zeroed block, because a typed read of a
    /// native block finds only the cells written into it.
    let allocate (contents : int) (pool : NativeMemoryPool) : NativeInt32Cell * NativeMemoryPool =
        let block, pool =
            NativeMemoryPool.allocate
                MemoryBlockInitialization.ZeroInitialized
                // An `int`'s 4-byte alignment is below the least a native block has.
                NativeBlockAlignment.minimum
                Size
                pool

        let pool =
            NativeMemoryPool.writeCell block 0 (CliType.Numeric (CliNumericType.Int32 contents)) pool

        {
            Block = block
        },
        pool

    /// What `cell` holds. Fails if it holds anything but an `int32`, which no CoreLib entry point
    /// stores there.
    let read (cell : NativeInt32Cell) (pool : NativeMemoryPool) : int =
        match NativeMemoryPool.tryReadCell cell.Block 0 pool with
        | Some (CliType.Numeric (CliNumericType.Int32 value)) -> value
        | Some other ->
            failwith
                $"The native int32 cell %O{cell.Block} holds %O{other}, which is not an int32; the callee wrote something other than an int32 into it."
        | None ->
            failwith
                $"logic error: the native int32 cell %O{cell.Block} has no cell at byte 0, though NativeInt32Cell.allocate writes one"
