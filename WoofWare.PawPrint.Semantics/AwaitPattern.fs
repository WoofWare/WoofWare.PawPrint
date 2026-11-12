namespace WoofWare.PawPrint

/// The `ConfigureAwait` call in an await pattern: `ldc.i4.0` or `ldc.i4.1`, then a `call` or
/// `callvirt` of this method.
type ConfigureAwaitCall<'operand> =
    {
        /// Whether the constant is `ldc.i4.1` rather than `ldc.i4.0`. Read as the argument of either
        /// overload (a `bool`, or `ConfigureAwaitOptions`, whose 1 is `ContinueOnCapturedContext`),
        /// it says whether the await continues on the captured context.
        ContinueOnCapturedContext : bool
        /// The operand of the call.
        Method : 'operand
    }

/// What follows a call in its basic block where the JIT reads an await of the promise the call
/// returns (`impMatchTaskAwaitPattern`, importer.cpp): optionally a `ConfigureAwait` call, then a
/// `call` (never `callvirt`) that awaits.
type AwaitFollower<'operand> =
    {
        /// The `ConfigureAwait` call between the two, if any. Before it there may be a `stloc` and an
        /// `ldloca` of one local, through which a `ValueTask`'s `ConfigureAwait` takes its receiver.
        ConfigureAwait : ConfigureAwaitCall<'operand> option
        /// The operand of the `call` that awaits.
        Await : 'operand
        /// The offset of the `call` that awaits.
        AwaitOffset : int
        /// The offset of the instruction after the `call` that awaits.
        AfterAwait : int
    }

/// What follows a call in its basic block where the JIT reads one of an async version's tail
/// shapes (`impMatchAsyncVersionTailCall`, importer.cpp).
[<RequireQualifiedAccess>]
type TailShape<'operand> =
    /// `ret`.
    | Return
    /// `newobj` of this constructor, then `ret`.
    | NewValueTask of constructor : 'operand
    /// `stloc` and `ldloca` of one local, `call` (never `callvirt`) of this method, then `ret`.
    | AsTask of asTask : 'operand

/// What the JIT knows, when it compiles an async version, of a call in one of its tail shapes, for
/// deciding whether the call's await is a tail await.
type TailCallFacts =
    {
        /// Whether the async version's assembly lets the JIT optimise its code
        /// (`Debuggable.allowsJitOptimisation`).
        AssemblyAllowsOptimisation : bool
        /// Whether the async version is flagged `MethodImplAttributes.NoOptimization`.
        CallerNoOptimization : bool
        /// Whether the async version is flagged `MethodImplAttributes.NoInlining`.
        CallerNoInlining : bool
        /// Whether the async version is its module's entry point.
        CallerIsEntryPoint : bool
        /// Whether the callee's async variant returns the async version's own return type. They differ
        /// where a method returning `Task` or `ValueTask` returns a callee's `Task<T>` or
        /// `ValueTask<T>`.
        ReturnTypesAgree : bool
        /// Whether the call names its callee exactly, dispatching to nothing, and the callee is flagged
        /// `MethodAttributes.RequireSecObject`, as a method that looks for its caller's frame is.
        ExactCalleeRequiresSecObject : bool
    }

/// An await an async version makes of its own accord (decision 17 of
/// `docs/plans/2026-11-11-runtime-async.md`).
[<RequireQualifiedAccess>]
type AsyncVersionAwait<'operand> =
    /// A `ret` that ends no tail shape, which awaits the promise on the stack through
    /// `AsyncHelpers.TransparentAwait`.
    | WrappedReturn
    /// A call in a tail shape, which calls its callee's async variant.
    | TailCall of TailShape<'operand> * TailCallFacts

[<RequireQualifiedAccess>]
module AwaitFollower =

    let map (f : 'a -> 'b) (follower : AwaitFollower<'a>) : AwaitFollower<'b> =
        {
            ConfigureAwait =
                follower.ConfigureAwait
                |> Option.map (fun call ->
                    {
                        ContinueOnCapturedContext = call.ContinueOnCapturedContext
                        Method = f call.Method
                    }
                )
            Await = f follower.Await
            AwaitOffset = follower.AwaitOffset
            AfterAwait = follower.AfterAwait
        }

[<RequireQualifiedAccess>]
module TailShape =

    let map (f : 'a -> 'b) (shape : TailShape<'a>) : TailShape<'b> =
        match shape with
        | TailShape.Return -> TailShape.Return
        | TailShape.NewValueTask constructor -> TailShape.NewValueTask (f constructor)
        | TailShape.AsTask asTask -> TailShape.AsTask (f asTask)

/// The JIT's await pattern and the tail shapes of an async version, as it reads them from a call in
/// a body: never past the end of the call's basic block, whose ends `BoxPattern.blockStarts` gives.
///
/// Each answer is a shape whose operands are not yet checked. The caller confirms each names the
/// intrinsic the JIT tests for, and where one does not, the JIT matches nothing at that call:
/// `ConfigureAwait` of `Task`, `Task<T>`, `ValueTask` or `ValueTask<T>`, and `AsyncHelpers.Await`,
/// for an await; for a tail shape, a constructor of `ValueTask` or `ValueTask<T>` taking one
/// argument that is not the `T` of `ValueTask<T>(T)`, or `AsTask` of either.
[<RequireQualifiedAccess>]
module AwaitPattern =

    /// What follows the `call` or `callvirt` at `callOffset` in `body`, where the JIT reads an await
    /// of the promise it returns. The JIT reads it only in a body with the async calling convention
    /// that is not an async version.
    let awaitAfter (body : MethodInstructions) (callOffset : int) : AwaitFollower<MetadataOperand> option =
        failwith "TODO: AwaitPattern.awaitAfter"

    /// The tail shape the `call` or `callvirt` at `callOffset` in `body` ends, if any. The JIT reads
    /// one only in an async version, and not in the async version of a synchronized method.
    let tailShapeAfter (body : MethodInstructions) (callOffset : int) : TailShape<MetadataOperand> option =
        failwith "TODO: AwaitPattern.tailShapeAfter"

    /// Whether `await` is a tail await, which passes its callee's continuation on as the async
    /// version's own and leaves no frame of the async version to resume (`impSetupAsyncCall`,
    /// importercalls.cpp; `getAwaitReturnCall`, importer.cpp; `CEEInfo::canTailCall`,
    /// jitinterface.cpp).
    let isTailAwait (await : AsyncVersionAwait<'operand>) : bool =
        failwith "TODO: AwaitPattern.isTailAwait"
