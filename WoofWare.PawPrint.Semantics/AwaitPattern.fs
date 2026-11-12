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

    /// The instruction after the one at `offset` in `body`, with its offset, where it is in the same
    /// basic block.
    let private nextInBlock (body : MethodInstructions) (starts : Set<int>) (offset : int) : (IlOp * int) option =
        let next = offset + IlOp.NumberOfBytes body.Locations.[offset]

        match body.Locations.TryFind next with
        | Some op when not (starts.Contains next) -> Some (op, next)
        | _ -> None

    let private requireCall (body : MethodInstructions) (callOffset : int) : unit =
        match body.Locations.TryFind callOffset with
        | Some (IlOp.UnaryMetadataToken (UnaryMetadataTokenIlOp.Call, _))
        | Some (IlOp.UnaryMetadataToken (UnaryMetadataTokenIlOp.Callvirt, _)) -> ()
        | other ->
            invalidArg
                (nameof callOffset)
                $"The instruction at offset %d{callOffset} is %O{other}, not a call or callvirt"

    let private stlocIndex (op : IlOp) : uint16 option =
        match op with
        | IlOp.Nullary NullaryIlOp.Stloc_0 -> Some 0us
        | IlOp.Nullary NullaryIlOp.Stloc_1 -> Some 1us
        | IlOp.Nullary NullaryIlOp.Stloc_2 -> Some 2us
        | IlOp.Nullary NullaryIlOp.Stloc_3 -> Some 3us
        | IlOp.UnaryConst (UnaryConstIlOp.Stloc_s index) -> Some (uint16 index)
        | IlOp.UnaryConst (UnaryConstIlOp.Stloc index) -> Some index
        | _ -> None

    let private ldlocaIndex (op : IlOp) : uint16 option =
        match op with
        | IlOp.UnaryConst (UnaryConstIlOp.Ldloca_s index) -> Some (uint16 index)
        | IlOp.UnaryConst (UnaryConstIlOp.Ldloca index) -> Some index
        | _ -> None

    /// The offset of the `ldloca` of a `stloc` and `ldloca` of one local straight after the
    /// instruction at `offset`, in its block (`impMatchStlocLdloca`, importer.cpp).
    let private storedAndAddressed (body : MethodInstructions) (starts : Set<int>) (offset : int) : int option =
        match nextInBlock body starts offset with
        | Some (stloc, stlocOffset) ->
            match stlocIndex stloc, nextInBlock body starts stlocOffset with
            | Some local, Some (ldloca, ldlocaOffset) when ldlocaIndex ldloca = Some local -> Some ldlocaOffset
            | _ -> None
        | None -> None

    /// What follows the `call` or `callvirt` at `callOffset` in `body`, where the JIT reads an await
    /// of the promise it returns. The JIT reads it only in a body with the async calling convention
    /// that is not an async version.
    let awaitAfter (body : MethodInstructions) (callOffset : int) : AwaitFollower<MetadataOperand> option =
        requireCall body callOffset
        let starts = BoxPattern.blockStarts body

        // The `ConfigureAwait` call, if any, and the offset of the last instruction before the
        // await; `None` where the pattern cannot match. Once a `stloc; ldloca` has been read, the JIT
        // requires a `ConfigureAwait` after it.
        let configured : (ConfigureAwaitCall<MetadataOperand> option * int) option =
            let local = storedAndAddressed body starts callOffset
            let beforeConstant = local |> Option.defaultValue callOffset

            let constant =
                match nextInBlock body starts beforeConstant with
                | Some (IlOp.Nullary NullaryIlOp.LdcI4_0, constantOffset) -> Some (false, constantOffset)
                | Some (IlOp.Nullary NullaryIlOp.LdcI4_1, constantOffset) -> Some (true, constantOffset)
                | _ -> None

            let configureAwait =
                constant
                |> Option.bind (fun (continueOnCapturedContext, constantOffset) ->
                    match nextInBlock body starts constantOffset with
                    | Some (IlOp.UnaryMetadataToken (UnaryMetadataTokenIlOp.Call, method), callOffset)
                    | Some (IlOp.UnaryMetadataToken (UnaryMetadataTokenIlOp.Callvirt, method), callOffset) ->
                        let call =
                            {
                                ContinueOnCapturedContext = continueOnCapturedContext
                                Method = method
                            }

                        Some (call, callOffset)
                    | _ -> None
                )

            match configureAwait, local with
            | Some (call, callOffset), _ -> Some (Some call, callOffset)
            | None, Some _ -> None
            | None, None -> Some (None, callOffset)

        configured
        |> Option.bind (fun (configureAwait, beforeAwait) ->
            match nextInBlock body starts beforeAwait with
            | Some (IlOp.UnaryMetadataToken (UnaryMetadataTokenIlOp.Call, await) as op, awaitOffset) ->
                Some
                    {
                        ConfigureAwait = configureAwait
                        Await = await
                        AwaitOffset = awaitOffset
                        AfterAwait = awaitOffset + IlOp.NumberOfBytes op
                    }
            | _ -> None
        )

    /// The tail shape that follows the `call` or `callvirt` at `callOffset` in `body`, if any. The JIT
    /// reads one only in an async version, and not in the async version of a synchronized method.
    let tailShapeAfter (body : MethodInstructions) (callOffset : int) : TailShape<MetadataOperand> option =
        requireCall body callOffset
        let starts = BoxPattern.blockStarts body

        let returnsAfter (offset : int) : bool =
            match nextInBlock body starts offset with
            | Some (IlOp.Nullary NullaryIlOp.Ret, _) -> true
            | _ -> false

        match nextInBlock body starts callOffset with
        | Some (IlOp.Nullary NullaryIlOp.Ret, _) -> Some TailShape.Return
        | Some (IlOp.UnaryMetadataToken (UnaryMetadataTokenIlOp.Newobj, constructor), newobjOffset) ->
            if returnsAfter newobjOffset then
                Some (TailShape.NewValueTask constructor)
            else
                None
        | _ ->
            match storedAndAddressed body starts callOffset with
            | None -> None
            | Some ldlocaOffset ->
                match nextInBlock body starts ldlocaOffset with
                | Some (IlOp.UnaryMetadataToken (UnaryMetadataTokenIlOp.Call, asTask), asTaskOffset) when
                    returnsAfter asTaskOffset
                    ->
                    Some (TailShape.AsTask asTask)
                | _ -> None

    /// Whether `await` is a tail await, which passes its callee's continuation on as the async
    /// version's own and leaves no frame of the async version to resume (`impSetupAsyncCall`,
    /// importercalls.cpp; `getAwaitReturnCall`, importer.cpp; `CEEInfo::canTailCall`,
    /// jitinterface.cpp).
    let isTailAwait (await : AsyncVersionAwait<'operand>) : bool =
        match await with
        | AsyncVersionAwait.WrappedReturn -> true
        // Its continuation must adapt the `ValueTask` to a `Task` (`IsValueTaskAsTask`).
        | AsyncVersionAwait.TailCall (TailShape.AsTask _, _) -> false
        | AsyncVersionAwait.TailCall (TailShape.Return, facts)
        | AsyncVersionAwait.TailCall (TailShape.NewValueTask _, facts) ->
            // `Tier0OptimizationEnabled`: neither debuggable code nor `JIT_FLAG_MIN_OPT`.
            facts.AssemblyAllowsOptimisation
            && not facts.CallerNoOptimization
            && facts.ReturnTypesAgree
            && not facts.CallerIsEntryPoint
            && not facts.CallerNoInlining
            && not facts.ExactCalleeRequiresSecObject
