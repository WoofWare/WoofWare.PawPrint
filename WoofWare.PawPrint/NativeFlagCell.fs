namespace WoofWare.PawPrint

/// A `bool` the runtime keeps in memory of its own and passes to managed code by reference: the
/// hidden local CoreCLR's JIT gives a synchronized method to say whether it holds its monitor
/// (`lvaMonAcquired`, jit/flowgraph.cpp), which the runtime's monitor helpers take as a `ref bool`.
type NativeFlagCell =
    private
        {
            Block : NativeMemoryBlockId
        }

    /// The pointer to pass as the callee's `ref bool` argument.
    member this.Pointer : ManagedPointerSource =
        ManagedPointerSource.Byref
            {
                Root = ByrefRoot.NativeMemoryByte (this.Block, 0)
                Projections = []
            }

[<RequireQualifiedAccess>]
module NativeFlagCell =

    /// A fresh cell holding `false`, as the JIT initialises its local before anything can read it.
    ///
    /// The cell is written rather than left to the zeroed block, because a typed read of a native
    /// block finds only the cells written into it.
    let allocate (pool : NativeMemoryPool) : NativeFlagCell * NativeMemoryPool =
        let block, pool =
            NativeMemoryPool.allocate
                MemoryBlockInitialization.ZeroInitialized
                // The JIT gives its local a whole pointer-sized slot (`TYP_I_IMPL`), of which the
                // helpers read and write the first byte.
                (NativeBlockAlignment.ofBytes NativeReferenceCell.PointerSize)
                NativeReferenceCell.PointerSize
                pool

        let pool = NativeMemoryPool.writeCell block 0 (CliType.ofBool false) pool

        {
            Block = block
        },
        pool

    /// Release `cell`, as the JIT's local dies when its frame does. Any later read of it fails.
    let free (cell : NativeFlagCell) (pool : NativeMemoryPool) : NativeMemoryPool = NativeMemoryPool.free cell.Block pool
