namespace WoofWare.PawPrint

/// A slot CoreLib reads or writes in a runtime-made continuation (`ConcreteTypeHandle.Continuation`)
/// at the byte offset the continuation's `Flags` encode.
[<RequireQualifiedAccess>]
type ContinuationSlot =
    | ExecutionContext
    | ContinuationContext
    | Exception
    | Result

/// Where a runtime-made continuation keeps each of its slots, and how its `Flags` say so: the
/// contract between CoreCLR's JIT, which lays a continuation out (`ContinuationLayoutBuilder::Create`
/// and `AsyncTransformation::CreateSuspension`, jit/async.cpp), and CoreLib, which finds a slot from
/// `Flags` alone (`ContinuationFlags` and `Continuation`, AsyncHelpers.CoreCLR.cs).
[<RequireQualifiedAccess>]
module ContinuationSlots =

    /// Bytes in a pointer, on every platform PawPrint emulates.
    [<Literal>]
    let PointerSize = 8

    /// The byte offset, from the start of a continuation's fields (`RuntimeHelpers.GetRawData`), of
    /// the first slot: past `Continuation`'s own `Next`, `ResumeInfo`, `Flags` and `State`
    /// (`Continuation.DataOffset`).
    [<Literal>]
    let DataOffset = 24

    // Each slot's index field in `Flags`: its first bit and its width
    // (`ContinuationFlags.*IndexFirstBit` and `*IndexNumBits`).
    let private indexField (slot : ContinuationSlot) : int * int =
        match slot with
        | ContinuationSlot.ExecutionContext -> 4, 2
        | ContinuationSlot.ContinuationContext -> 6, 2
        | ContinuationSlot.Exception -> 8, 3
        | ContinuationSlot.Result -> 11, 21

    /// The slots `layout` has, in the order the JIT lays them out: the three pointer-sized slots,
    /// then the result.
    let present (layout : ContinuationLayout) : ContinuationSlot list =
        [
            if layout.ExecutionContext then
                ContinuationSlot.ExecutionContext
            if layout.ContinuationContext then
                ContinuationSlot.ContinuationContext
            if layout.Exception then
                ContinuationSlot.Exception
            if layout.Result.IsSome then
                ContinuationSlot.Result
        ]

    /// The byte offset of `slot` from the start of a continuation's fields, or `None` if `layout` has
    /// no such slot. Every slot is pointer-aligned, as `Flags` can encode no other offset; the JIT
    /// rounds the result's offset up to a pointer for that reason.
    let offsetOf (slot : ContinuationSlot) (layout : ContinuationLayout) : int option =
        present layout
        |> List.tryFindIndex (fun present -> present = slot)
        |> Option.map (fun index -> DataOffset + index * PointerSize)

    /// The bits of `Flags` that say where each of `layout`'s slots lies: for each slot present, its
    /// index `1 + (offset - DataOffset) / PointerSize` in its field, which CoreLib turns back into
    /// `(DataOffset - PointerSize) + index * PointerSize`. An absent slot's index is 0.
    let slotIndexFlags (layout : ContinuationLayout) : int =
        (0, present layout)
        ||> List.fold (fun flags slot ->
            let firstBit, width = indexField slot

            let offset =
                match offsetOf slot layout with
                | Some offset -> offset
                | None -> failwith $"BUG: %O{layout} lists %O{slot} as present but gives it no offset"

            let index = 1 + (offset - DataOffset) / PointerSize

            if index >>> width <> 0 then
                failwith
                    $"BUG: %O{layout} puts %O{slot} at index %d{index}, which its %d{width}-bit field in Flags cannot hold"

            flags ||| (index <<< firstBit)
        )

    /// The bits of `Flags` that say what the continuation's frame was awaiting
    /// (`ContinuationFlags.ContinueOnThreadPool` and `ValueTaskAdaptedToTask`), as the JIT sets them
    /// when it makes the continuation (`AsyncTransformation::CreateSuspension`, jit/async.cpp). An
    /// await that continues on the captured context sets none here: CoreLib's
    /// `FinishSuspensionWithContinuationContext` adds the flag for the context it captures.
    let callFlags (flags : SuspendedCallFlags) : int =
        match flags with
        | SuspendedCallFlags.Continues ContinuationContextHandling.NoPreference
        | SuspendedCallFlags.Continues ContinuationContextHandling.ContinueOnCapturedContext -> 0
        | SuspendedCallFlags.Continues ContinuationContextHandling.ContinueOnThreadPool -> 1
        | SuspendedCallFlags.ValueTaskAdaptedToTask -> 1 <<< 3

/// What can be asked of a continuation type itself, rather than of its slots.
[<RequireQualifiedAccess>]
module ContinuationType =

    /// Fail at `operation`, a question that only a token, a signature or a reflection object
    /// could put about the type `layout`. None of those names a continuation type: the runtime
    /// makes it with no metadata, and PawPrint refuses `GetType()` on an object of one.
    let unasked (operation : string) (layout : ContinuationLayout) : 'a =
        failwith
            $"BUG: %s{operation} was asked about %O{layout}, a continuation type the runtime makes with no metadata, which no token, signature or RuntimeType names"
