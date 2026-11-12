namespace WoofWare.PawPrint

/// Where the awaiting code continues when the call it awaits suspends and later completes: what the
/// continuation the awaiting frame makes for itself says (`ContinuationContextHandling`,
/// jit/async.h).
[<RequireQualifiedAccess>]
type ContinuationContextHandling =
    /// Wherever the awaited call completes. CoreCLR's JIT gives this to every async call that is not
    /// an await of a promise: in PawPrint, so far, each call of runtime async's own machinery.
    | NoPreference
    /// On the thread pool: an await of a promise with `ConfigureAwait(false)`, whose continuation
    /// carries `ContinuationFlags.ContinueOnThreadPool`.
    | ContinueOnThreadPool

/// What the async variant of a method runs, by the body `RuntimeAsyncMethod.asyncVariantOf` gives
/// it.
[<RequireQualifiedAccess>]
type AsyncVariantBody =
    /// The method's own runtime-async IL: it is flagged `MethodImplAttributes.Async`.
    | RuntimeAsyncIl
    /// The method's IL, as an async version: it is not flagged `Async`.
    | AsyncVersion
    /// None: the method is abstract, and dispatch finds what runs.
    | Abstract

/// The instruction that begins an await site.
[<RequireQualifiedAccess>]
type AwaitedCall =
    | Call
    | Callvirt

/// What CoreCLR's JIT reads of a matched await site (`impMatchTaskAwaitPattern`, importer.cpp) in a
/// user's runtime-async body when it decides whether the site binds an async variant: the pattern's
/// shape, the opcode and its `constrained.` prefix, and the row of the method the call names. It
/// never reads the receiver, so a site binds the same way on every execution.
type AwaitSiteFacts =
    {
        /// Whether the await continues on the captured context: the pattern has no `ConfigureAwait`
        /// call, or its constant is `ldc.i4.1`.
        ContinuesOnCapturedContext : bool
        /// The instruction that calls the named method.
        Opcode : AwaitedCall
        /// Whether a `constrained.` prefix precedes the call.
        Constrained : bool
        /// Whether the named method is virtual and not final, so that a `callvirt` of it dispatches
        /// (`MethodInfo.DispatchesVirtually`).
        NamedDispatchesVirtually : bool
        /// What the named method's async variant runs, or why it has none.
        NamedVariant : Result<AsyncVariantBody, AsyncVariantRefusal>
    }

/// Why PawPrint does not yet bind the async variant at a matched await site where CoreCLR's JIT
/// binds one (decision 13 of `docs/plans/2026-11-11-runtime-async.md`). The site calls the named
/// method and awaits the promise it returns, as the JIT compiles a site where the VM supplies no
/// variant (decision 6(a)).
[<RequireQualifiedAccess>]
type AwaitNotYetBound =
    /// The await continues on the captured context.
    | ContinuesOnCapturedContext
    /// The call has a `constrained.` prefix.
    | Constrained
    /// The call is a `callvirt` of a method that dispatches.
    | CalleeDispatches
    /// The named method is not flagged `Async`, so its variant is an async version.
    | CalleeIsAsyncVersion
    /// CoreCLR gives the named method an async variant whose body PawPrint does not model.
    | UnmodelledVariant of UnmodelledVariantBody

/// Decision 13's classification of a matched await site in a user's runtime-async body.
[<RequireQualifiedAccess>]
type AwaitSiteBinding =
    /// The call calls the named method's async variant, and the whole pattern is its step: if the
    /// variant suspends, the caller's continuation continues as `handling` says.
    | BindsVariant of handling : ContinuationContextHandling
    /// CoreCLR's VM supplies no async variant of the named method, so the JIT calls the method and
    /// awaits the promise it returns.
    | NoVariant of AsyncVariantRefusal
    /// CoreCLR binds the variant, and PawPrint does not yet.
    | NotYetBound of AwaitNotYetBound

[<RequireQualifiedAccess>]
module AwaitSiteBinding =

    /// What a matched await site in a user's runtime-async body binds, from `facts`. Where the site
    /// is not yet bound for more than one reason, the first of these is named: an unmodelled
    /// variant, the captured context, the `constrained.` prefix, dispatch, an async version.
    ///
    /// The named method may be abstract only where the call dispatches, or a `constrained.` prefix
    /// resolves it; CoreCLR's JIT refuses to compile a call naming one otherwise
    /// (`CallSiteBinding` raises that before asking).
    let classify (facts : AwaitSiteFacts) : AwaitSiteBinding =
        let dispatches =
            match facts.Opcode with
            | AwaitedCall.Callvirt -> facts.NamedDispatchesVirtually
            | AwaitedCall.Call -> false

        match facts.NamedVariant with
        | Ok AsyncVariantBody.Abstract when not dispatches && not facts.Constrained ->
            invalidArg
                (nameof facts)
                $"%A{facts} names an abstract method through a call that neither dispatches nor has a constrained. prefix"
        | Error (AsyncVariantRefusal.UnmodelledBody body) ->
            AwaitSiteBinding.NotYetBound (AwaitNotYetBound.UnmodelledVariant body)
        | Error refusal -> AwaitSiteBinding.NoVariant refusal
        | Ok body ->

        if facts.ContinuesOnCapturedContext then
            AwaitSiteBinding.NotYetBound AwaitNotYetBound.ContinuesOnCapturedContext
        elif facts.Constrained then
            AwaitSiteBinding.NotYetBound AwaitNotYetBound.Constrained
        elif dispatches then
            AwaitSiteBinding.NotYetBound AwaitNotYetBound.CalleeDispatches
        else

        match body with
        | AsyncVariantBody.RuntimeAsyncIl ->
            // `ConfigureAwait(false)` (`PREFIX_TASK_AWAIT_CONTINUE_ON_CAPTURED_CONTEXT` unset,
            // `impSetupAsyncCall`, importercalls.cpp).
            AwaitSiteBinding.BindsVariant ContinuationContextHandling.ContinueOnThreadPool
        | AsyncVariantBody.AsyncVersion -> AwaitSiteBinding.NotYetBound AwaitNotYetBound.CalleeIsAsyncVersion
        | AsyncVariantBody.Abstract ->
            failwith "logic error: an abstract method named by a call that does not dispatch was refused above"
