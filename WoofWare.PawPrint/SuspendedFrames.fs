namespace WoofWare.PawPrint

/// A runtime-async frame that suspended, as `SuspendedFrames` keeps it until it is resumed.
type SuspendedFrame =
    {
        /// The frame as it suspended: its program counter past the call it awaits, with that call's
        /// result not yet pushed, its byrefs zeroed and its `stackalloc` memory dropped. Its
        /// `ReturnState` is the one it suspended with; resuming gives it another.
        Frame : MethodState
        /// The type of the frame's continuation, which says whether the call it awaits returns a
        /// result, and of what type.
        Layout : ContinuationLayout
        /// The native block holding the `ResumeInfo` the continuation points at, which lives as long
        /// as the suspension does.
        ResumeInfo : NativeMemoryBlockId
    }

/// The runtime-async frames that have suspended and not yet resumed, each keyed by the continuation
/// it returned for itself (decision 2 of `docs/plans/2026-11-11-runtime-async.md`). PawPrint keeps
/// no object alive by holding it here, since it collects no garbage.
///
/// Each entry is linear: `suspend` adds it once, and `resume` takes it out once. Suspending again
/// under a continuation already present, or resuming one absent, is a bug in the interpreter, and
/// fails.
type SuspendedFrames =
    private
        {
            Frames : Map<ManagedHeapAddress, SuspendedFrame>
        }

[<RequireQualifiedAccess>]
module SuspendedFrames =

    let empty : SuspendedFrames =
        {
            Frames = Map.empty
        }

    /// Keep `frame`, which suspended with `continuation` as its own continuation.
    let suspend
        (continuation : ManagedHeapAddress)
        (frame : SuspendedFrame)
        (frames : SuspendedFrames)
        : SuspendedFrames
        =
        if frames.Frames.ContainsKey continuation then
            failwith
                $"BUG: %O{frame.Frame.ExecutingMethod} suspended with the continuation %O{continuation}, under which another frame is already suspended"

        {
            Frames = frames.Frames |> Map.add continuation frame
        }

    /// Take out the frame that suspended with `continuation`, to resume it.
    let resume (continuation : ManagedHeapAddress) (frames : SuspendedFrames) : SuspendedFrame * SuspendedFrames =
        match frames.Frames.TryFind continuation with
        | None ->
            failwith
                $"BUG: resuming the continuation %O{continuation}, under which no frame is suspended: it was resumed already, or no frame of PawPrint's made it"
        | Some frame ->
            frame,
            {
                Frames = frames.Frames |> Map.remove continuation
            }

    /// Every frame suspended now, keyed by its continuation.
    let all (frames : SuspendedFrames) : Map<ManagedHeapAddress, SuspendedFrame> = frames.Frames
