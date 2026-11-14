namespace WoofWare.PawPrint

/// Where the awaiting code continues when the call it awaits suspends and later completes: what the
/// continuation the awaiting frame makes for itself says (`ContinuationContextHandling`,
/// jit/async.h).
[<RequireQualifiedAccess>]
type ContinuationContextHandling =
    /// Wherever the awaited call completes. CoreCLR's JIT gives this to every async call that is not
    /// an await of a promise: each call of runtime async's own machinery, and an async version's
    /// calls in its tail shapes and of `AsyncHelpers.TransparentAwait`.
    | NoPreference
    /// On the thread pool: an await of a promise with `ConfigureAwait(false)`, whose continuation
    /// carries `ContinuationFlags.ContinueOnThreadPool`.
    | ContinueOnThreadPool

/// What the continuation of a frame suspending at an async call says of that call, besides where
/// its slots lie: the flags CoreCLR's JIT sets from the call's `AsyncCallInfo`
/// (`AsyncTransformation::CreateSuspension`, jit/async.cpp).
[<RequireQualifiedAccess>]
type SuspendedCallFlags =
    /// The awaiting code continues as `handling` says.
    | Continues of handling : ContinuationContextHandling
    /// The call is an async version's `AsTask` tail shape (`IsValueTaskAsTask`): the awaiting code
    /// has no preference where it continues, and the continuation is flagged
    /// `ContinuationFlags.ValueTaskAdaptedToTask`, at which CoreLib stops its search for the flags
    /// it gives a value-task source.
    | ValueTaskAdaptedToTask

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

/// Which of the JIT's patterns a matched await site is.
[<RequireQualifiedAccess>]
type MatchedPattern =
    /// The await pattern in a user's runtime-async body (`impMatchTaskAwaitPattern`, importer.cpp).
    /// `continuesOnCapturedContext` says whether the await continues on the captured context: the
    /// pattern has no `ConfigureAwait` call, or its constant is `ldc.i4.1`.
    | Await of continuesOnCapturedContext : bool
    /// One of the tail shapes of an async version (`impMatchAsyncVersionTailCall`, importer.cpp).
    | TailShape

/// What CoreCLR's JIT reads of a matched await site when it decides whether the site binds an async
/// variant: the pattern and its shape, the opcode and its `constrained.` prefix, and the row of the
/// method the call names. It never reads the receiver, so a site binds the same way on every
/// execution.
type AwaitSiteFacts =
    {
        /// Which pattern the site matches.
        Pattern : MatchedPattern
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
    /// The call is a `callvirt` with a `constrained.` prefix: of an instance method, through a
    /// receiver whose type the prefix names.
    | ConstrainedCallvirt

/// Decision 13's classification of a matched await site: an await in a user's runtime-async body,
/// or a tail shape in an async version.
[<RequireQualifiedAccess>]
type AwaitSiteBinding =
    /// The call calls the named method's async variant, and the whole pattern is its step: if the
    /// variant suspends and the caller makes a continuation of its own, that continuation continues
    /// as `handling` says.
    | BindsVariant of handling : ContinuationContextHandling
    /// CoreCLR's VM supplies no async variant of the named method, so the JIT calls the method and
    /// awaits the promise it returns.
    | NoVariant of AsyncVariantRefusal
    /// CoreCLR binds the variant, and PawPrint does not yet.
    | NotYetBound of AwaitNotYetBound
    /// CoreCLR binds the variant, whose body PawPrint does not model, so the site is refused by
    /// name where it runs.
    | Unmodelled of UnmodelledVariantBody

[<RequireQualifiedAccess>]
module AwaitSiteBinding =

    /// What a matched await site binds, from `facts`. An unmodelled variant is named before any
    /// reason the site is not yet bound; of those, the first of these is named: the captured
    /// context, a `callvirt`'s `constrained.` prefix. A site that dispatches binds the named
    /// method's variant, whose implementation on the receiver dispatch finds through its ordinary
    /// sibling (decision 19 of `docs/plans/2026-11-11-runtime-async.md`); so does a `call` with a
    /// `constrained.` prefix, as of a static virtual method, whose implementation the prefix
    /// resolves the same way.
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
        | Error (AsyncVariantRefusal.UnmodelledBody body) -> AwaitSiteBinding.Unmodelled body
        | Error refusal -> AwaitSiteBinding.NoVariant refusal
        | Ok body ->

        let continuesOnCapturedContext =
            match facts.Pattern with
            | MatchedPattern.Await continues -> continues
            | MatchedPattern.TailShape -> false

        if continuesOnCapturedContext then
            AwaitSiteBinding.NotYetBound AwaitNotYetBound.ContinuesOnCapturedContext
        elif facts.Constrained && facts.Opcode = AwaitedCall.Callvirt then
            AwaitSiteBinding.NotYetBound AwaitNotYetBound.ConstrainedCallvirt
        else

        match body with
        | AsyncVariantBody.Abstract when not dispatches && not facts.Constrained ->
            failwith
                "logic error: an abstract method named by a call that neither dispatches nor has a constrained. prefix was refused above"
        | AsyncVariantBody.Abstract
        | AsyncVariantBody.RuntimeAsyncIl
        | AsyncVariantBody.AsyncVersion ->

        match facts.Pattern with
        | MatchedPattern.Await _ ->
            // `ConfigureAwait(false)` (`PREFIX_TASK_AWAIT_CONTINUE_ON_CAPTURED_CONTEXT` unset,
            // `impSetupAsyncCall`, importercalls.cpp).
            AwaitSiteBinding.BindsVariant ContinuationContextHandling.ContinueOnThreadPool
        | MatchedPattern.TailShape ->
            // A tail shape is no task await (`PREFIX_IS_TASK_AWAIT` unset), so its call's handling
            // stays `None`.
            AwaitSiteBinding.BindsVariant ContinuationContextHandling.NoPreference
