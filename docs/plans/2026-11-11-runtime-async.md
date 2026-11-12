# Running runtime-async methods (.NET 11)

## Why

.NET 11 compiles every async method in CoreLib, and in every shared-framework library, as a
*runtime-async* method (`System.Private.CoreLib.csproj` sets `Features=runtime-async=on`;
`libraries/Directory.Build.targets` does the same for the shared framework). Since stage 1b
PawPrint runs such a method whose awaits all find completed tasks; since stage 2b it suspends one
at an await of a pending task and resumes it; since stage 3 it carries an exception across a
suspension, into a resumed frame's own handlers and out through further continuations.

.NET 10's CoreLib declares runtime async's infrastructure too (`AsyncHelpers.Await` and the like,
flagged `Async` and returning no promise).

## The mechanism

Paths are relative to the pinned runtime's `src/`.

### Variants

For each MethodDef returning `Task`, `Task<T>`, `ValueTask` or `ValueTask<T>`, CoreCLR's class
loader makes two `MethodDesc`s sharing the token (`vm/methodtablebuilder.cpp:3374-3437`):

* the *ordinary variant*, with the metadata signature;
* the *async variant*, with the signature unwrapped (`Task<T>` → `T`, `Task`/`ValueTask` →
  `void`) and the async calling convention.

One of the two owns the IL:

* A method flagged `MethodImplAttributes.Async` (0x2000) has IL written for the unwrapped
  signature, and the async variant owns it. The ordinary variant is an IL stub the VM emits,
  the *task-returning thunk* (`vm/asyncthunks.cpp:77-272`, copied by crossgen in
  `coreclr/tools/Common/TypeSystem/IL/Stubs/AsyncThunks.cs`).
* A method without the flag owns its IL as the ordinary variant. Its async variant is the same
  IL compiled so that every `ret` awaits the returned task through
  `AsyncHelpers.TransparentAwait` (`jit/importer.cpp:11883-12062`): an *async version*.

A method flagged `Async` that returns no promise type (only CoreLib may declare one: `Await`,
`Suspend`, `AwaitAwaiter` and the rest) has only an async-convention `MethodDesc`.

Every IL token, `ldftn`, delegate and reflection call binds the ordinary variant. Only the JIT's
await pattern (`call M; [ConfigureAwait]; call AsyncHelpers.Await`) and its tail-call patterns in
async versions bind an async variant (`jit/importer.cpp:5825-5925`, `:6051-6140`, `:9204-9259`).
Where the VM supplies no variant, the JIT itself falls back to calling the ordinary variant and
then `Await`'s IL (`:9238-9252`).

### The calling convention

An async-convention method takes a hidden continuation argument (null, except when it is being
resumed) and returns a hidden continuation (null when it completed; otherwise the `Continuation`
describing its suspended frame, and its formal return value is garbage).

After every async call in an async method, the caller tests the hidden return. If it is non-null,
the caller suspends: it allocates a `Continuation`, links it as the callee's continuation's
`Next`, stores its resume point, `Flags`, `ResumeInfo*` and live locals, finishes context
handling through CoreLib helpers, and returns its own continuation. Suspending runs no `finally`
(`jit/async.cpp:1427-1474`, `:2661-2834`). After a call marked by `TailAwait()`, the caller
returns the callee's continuation unchanged instead, with no continuation of its own.

The leaf of every suspension is the intrinsic `AsyncHelpers.AsyncSuspend(cont)`, which returns
`cont` as the method's hidden return. The managed helpers above it record what is being awaited
in the thread-static `RuntimeAsyncAwaitState` first.

A non-async caller reads the hidden return of its last async call through the intrinsic
`AsyncHelpers.AsyncCallContinuation()`. Only the task-returning thunk and the resumption stub do.

### Continuations

`Continuation` (`coreclr/System.Private.CoreLib/src/System/Runtime/CompilerServices/AsyncHelpers.CoreCLR.cs:74-146`)
has fields `Next`, `ResumeInfo*`, `Flags` and `State`. The JIT allocates a metadata-less subclass
per data layout (`vm/asynccontinuations.cpp:106-240`). CoreLib reads and writes four slots in its
data by byte offsets that `Flags` encodes: the `ExecutionContext`, the continuation context (a
`SynchronizationContext` or `TaskScheduler`), the exception, and the result, which other
continuations' resume functions write as an arbitrary `T`. Nothing managed reads the saved locals,
or `State`.

`ResumeInfo` is `{ delegate*<Continuation, ref byte, Continuation?> Resume; void* DiagnosticIP }`.
CoreLib's `RuntimeAsyncTask<T>.DispatchContinuations` (`:934-1061`) runs a chain from its head:
for each continuation it restores the captured `ExecutionContext` and calls
`cont.ResumeInfo->Resume(cont, ref <the next continuation's result slot>)`. For a JIT'd method,
`Resume` is a VM IL stub that calls the method's code with the continuation as its hidden argument
(`vm/jitinterface.cpp:15304-15440`); the method then switches on `State`, reloads its locals and
the awaited call's result, rethrows a stored exception, and carries on. CoreLib's own
continuations (`RuntimeAsyncTaskContinuation`, `ValueTaskSourceContinuation`) point `Resume` at a
managed method through a `[FixedAddressValueType]` static `ResumeInfo`.

An exception escaping a resume goes to the next continuation in the chain with an exception slot.
The JIT gives a continuation one only when its await lies in a user `try` region. That frame
rethrows the exception at its await. With no such continuation, the task faults. For each
continuation unwound through with a non-null `DiagnosticIP`, the dispatcher appends a stack frame
to the exception.

### Contexts

A user-written runtime-async body (the async variant of an `Async`-flagged method) captures the
thread's `ExecutionContext` and `SynchronizationContext` on entry, restores them on return and on
an exception unless it was resumed, and, at each suspension, calls
`FinishSuspensionWithContinuationContext` or `FinishSuspensionNoContinuationContext`
(`jit/async.cpp:438-662`, `:3105-3259`). Infrastructure methods (`Await`, `Suspend`, the thunks)
handle no contexts, and only they may `TailAwait`.

### Awaits are never in a handler

Roslyn moves an await written in a `catch` or `finally` out of the handler: RC1's
`BufferedStream.DisposeAsync` catches the exception into a local, awaits outside the handler, and
throws it again through `ExceptionDispatchInfo`. Neither the JIT nor CoreCLR's interpreter can
suspend in a handler, and PawPrint refuses to.

### Precedent: CoreCLR's own interpreter

`coreclr/interpreter` runs runtime-async methods. It keeps the hidden return in a side slot on the
interpreter frame. At a suspension it copies every IL local and every live evaluation-stack entry
into a continuation of a layout it asks the VM for, zeroing byrefs. Every continuation it makes
resumes through one FCall, which finds the method and resume point from the `ResumeInfo*`. It sets
`DiagnosticIP` to null, so exceptions crossing its continuations carry no async frames.

## Decisions

Patrick delegated these choices to Fable while he was away. Each lists the options considered and
what was chosen.

### 1. How PawPrint names the two variants

* (a) The metadata `MethodInfo` is the IL's owner, so for an `Async` method it is the async
  variant, and the ordinary variant is a new synthesised method. Every site that binds a token,
  an `ldftn`, a delegate, reflection or a vtable slot must then map an `Async` method to its thunk,
  and a missed site silently runs the async body as the ordinary variant.
* (b) Re-body at the frame push: the async variant is a marker on the frame. Two frames then carry
  the same `MethodInfo` with different bodies and signatures, which nothing that compares methods
  can tell apart.
* **(c, chosen) A variant on the method's identity**, as CoreCLR has two `MethodDesc`s:
  `Ordinary | AsyncVariant`, part of `MethodInfo.IdentityKey`'s type so that every consumer of a
  method's identity (vtable slot maps, function-pointer equality, signal handlers) must decide
  what it means. Every binding a token makes constructs `Ordinary`, which is what CoreCLR binds.
  One Loader function constructs `AsyncVariant`, with the unwrapped signature, and refuses a
  method without `MethodImplAttributes.Async` until stage 4.

  Two refinements:
  * Domain reads the IL of a MethodDef flagged `Async` as a distinct body case,
    `MethodBody.AsyncIl`, a pure metadata fact. The ordinary variant of such a method does not run
    that IL, and every site that reads a body (the escape analysis, intrinsic classification, the
    stack-shape import) must say what it means by it.
  * The variant is not the calling convention. `Await`, `Suspend` and the other `Async` methods in
    CoreLib that return no promise have a single `MethodDesc`, bound by ordinary tokens, with the
    async convention and the metadata signature. Which convention a method has is derived from the
    variant and the metadata (`RuntimeAsyncMethod.isAsyncCall` extended), not stored.

### 2. Where a suspended frame's state lives

* (a) Byte-copied into the continuation, as CoreCLR's interpreter does. PawPrint's values are
  `CliType`s, not bytes, and a byte image loses their provenance.
* **(b, chosen) The real slots in the continuation; the frame in a host-side record.** The
  continuation is a heap object whose layout holds exactly the slots CoreLib addresses, at the
  offsets its `Flags` encode. The suspended `MethodState` is kept in a map on the machine keyed by
  the continuation's address; PawPrint has no garbage collector, so the map keeps nothing alive
  that would otherwise die. The entry is linear: resuming removes it, and a second resume or a
  missing entry crashes. At suspension, byrefs in locals, arguments and the evaluation stack are
  zeroed, as CoreCLR's JIT and interpreter do (a stale `Span` local used before the await, an `in`
  argument or a struct's `this` are legal and common), and the frame's `stackalloc` memory is
  dropped, so a dangling pointer into it fails when used.

### 3. The continuation type

* **(a, chosen) A structural `ConcreteTypeHandle` case**, beside `Array` and `FunctionPointer`,
  whose identity is which slots it has and the result's type, never the method or call site. This
  is finer than CoreCLR's identity (data size and GC map), so `GetType()` on one stays refused.
* (b) One fixed subclass with every slot, the result as raw bytes. A result holding references
  would lose its provenance.

### 4. The resume function pointer

* (a) A synthesised resumption stub per suspension point, run as IL like CoreCLR's. With the frame
  saved host-side, the stub's dummy arguments and its entry check only rebuild a frame to discard
  it.
* **(b, chosen) One `FunctionPointerTarget.AsyncResume`**, as CoreCLR's interpreter resumes every
  continuation through one FCall. `calli` on it pops the continuation and the result location,
  removes the suspended frame from the map, and pushes it with a disposition that, on return,
  writes the result through the location and returns null, and on suspension returns the new
  continuation. `SetNextCallAsyncContinuation`, which only CoreCLR's resumption stub calls, stays
  refused.

### 5. `DiagnosticIP`

* (a) Null, as CoreCLR's interpreter sets it: an exception crossing a continuation then silently
  carries fewer frames than on JIT'd .NET.
* **(b, chosen) A pointer PawPrint can map back to its method** from stage 2, with the
  `AsyncHelpers_AddContinuationToExInternal` QCall refused until stage 3 makes it append the
  frame. The divergence stops loudly where it would happen.

### 6. The await pattern

* **(a, chosen until stage 4) Never bind an async variant from a call site**, the JIT's own
  fallback when the VM supplies no variant. Every `await M()` makes a `Task` and awaits it through
  `Await`'s IL.
* **(b, stage 4) Match the pattern as the JIT does**, binding the callee's async variant. Not
  optional: the two differ in traces, in what `IsRuntimeAsync()` answers, and in which thread
  schedules a continuation, as decision 13 measures.

### 7. The hidden return

On the caller's frame, as CoreCLR's interpreter keeps it: set when an async-convention callee
returns (null) or suspends (its continuation), and read and cleared by `AsyncCallContinuation()`.
A non-async frame holding a non-null hidden return that makes another call or returns before
reading it is a bug, and is asserted against. An async-convention caller never reads it: it
suspends, or after `TailAwait()` returns the continuation unchanged, as part of the callee's
leaving.

### 8. Context handling in user bodies

The frame calls CoreLib's helpers as managed calls at entry, at return, on an exception and at
each suspension, as the JIT's code calls them, through a step union like `VmRaiseStep`. Its hidden
values (`thread`, `ec`, `sc`, `resumed`) live in an `AsyncFrame option` on `MethodState`, since
`CaptureContexts` writes them through `out` parameters. The IL is not rewritten, as CoreCLR's
interpreter rewrites it, because that would shift the IL offsets stack traces expose.

As built in stage 1b: `AsyncFrame` is `Transparent` for runtime async's own machinery and
`SavesContexts` for a user body, the latter tracking how far the body has got
(`AsyncContextHandling`). The capture is the frame's prologue, after its type-initialisation
check, as the JIT orders them. On the exception path, the second pass of exception dispatch parks
the unwind on the frame instead of popping it; the frame's next step calls `RestoreContexts`, and
that call's return resumes the second pass, which then pops the frame. An exception escaping a
helper ends the run (`ExceptionEscape.AsyncContextHelper`): nothing the helpers do raises.

### 9. How the thunk's IL names its callees

`IlStub` has no locals or exception regions, and an IL operand is a metadata token or a dynamic
method's scope. The thunk needs locals, a `try`/`catch` inside a `try`/`finally`, `FromResult<T>`
and `CreateRuntimeAsyncTask<T>` at the method's `T`, and the async variant of its own method,
which no token names. **Chosen:** a third operand case, a scope of host-resolved stub operands like
CoreCLR's `TokenLookupMap`, with the thunk's IL kept as data in `WoofWare.PawPrint.Semantics`.
Rejected: a thunk written as an F# step machine, which would transcribe exception handling, and
whose `Push(&stackState)` needs a real frame local anyway.

As built in stage 1b: the frame running a stub holds it (`MethodState.VmStub`), and an operand
(`MetadataOperand.FromStubScope`, `ExceptionCatchType.FromStubScope`) indexes its scope. An entry
naming a CoreLib member resolves to that member's CoreLib token; the async variant and the
instantiations over the method's `T` resolve to concretised methods when the instruction runs.

### 10. Stack traces

The thunk's frame, and the resume entry, are left out of stack traces, as IL stubs are on real
.NET (`MethodDesc::IsDiagnosticsHidden`), and out of stack-crawl walks, which skip them too.

### 11. An await in a `try` region before stage 3

Suspending in a frame whose await lies in a `try` region (a `using` counts) is refused until stage
3 gives continuations exception slots. Without one, the dispatcher would skip that frame's handlers
silently.

Lifted in stage 3. A suspension in a filter or handler stays refused by name.

### 12. Virtual slots

.NET 11 gives every `Task`-returning virtual method two vtable slots, one per variant
(`MethodTableLayout.fs` assumes none). The thunk calls its async variant directly, so this first
matters in stage 4, when `callvirt` binds an async variant.

### 13. Which await sites bind a variant while stage 4 is part built

The JIT matches the await pattern only in a body with the async calling convention that is not an
async version, and the tail patterns only in an async version (`jit/importer.cpp:9229-9263`). At a
matched site it asks the VM for the named method's async variant
(`CORINFO_TOKENKIND_Await`, `vm/jitinterface.cpp:1082-1090`), and where there is none it compiles
the site as decision 6(a) does.

* (a) Bind at every matched site in one change. Nothing of it is testable until all of it is
  built: async versions, dispatch and both ways of continuing.
* (b) Bind at the sites a stage supports and refuse the rest by name. Every guest that reaches
  runtime-async library code would stop until the last stage, the ASP.NET tests among them.
* **(c, chosen) Classify each matched site from static facts, and keep decision 6(a) at the sites
  a stage does not yet bind.** The classification is one value, of three kinds: the site binds a
  variant; CoreCLR's VM supplies none there, with why; or PawPrint does not bind there yet, with
  why. It reads only what the JIT reads when it compiles the caller (the caller's frame kind, the
  pattern's shape, the opcode and its `constrained.` prefix, and the named method's row), never the
  receiver, so a site behaves one way on every execution. Each stage removes cases of the third
  kind, and stage 4f removes the kind. Every intermediate state is one the JIT can produce, since
  awaiting the task an ordinary variant returns is always legal, and none is a new divergence:
  each narrows decision 6(a)'s.

What the two differ in, measured on real .NET 11.0.0-rc.1 with methods the devshell SDK's compiler
makes runtime-async (see "Test guests"), a matched `await M()` against the same await of a task read
from a field, which the pattern does not match:

* **Traces**, where the callee is an `Async` body or tail-awaits its way to one. An exception
  crossing an unmatched await is thrown again by the task's awaiter, and the formatted trace gains
  `RuntimeAsyncTaskContinuation.GetResult(Task, Byte&)` (or its generic overload) between the
  callee's frame and the caller's. Across a matched await the two frames are adjacent. The caller's
  frame is reported at the IL offset of the `call Await`, not of the `call M`
  (`opcodeOffs = awaitOffset`, `:9305`). Where the callee is an async version that awaits what it
  returns, the awaiter's frames are there either way, and the traces are the same.
* **`RuntimeHelpers.IsRuntimeAsync()`** answers true in an async version. Five CoreLib methods
  call it (`StreamReader.ReadAsync(char[], int, int)`, both `StreamReader.ReadBlockAsync`s and both
  `StreamWriter.FlushAsync`s), and nothing else in RC1's shared framework or ASP.NET Core.
* **Scheduling**, where the callee is an async version and the await continues on a captured
  context. The async version's `ret` awaits through `TransparentSuspend`, whose continuation
  carries no continuation flags, where `Await`'s carries the captured context. When the awaited
  task forbids inline continuations (`RunContinuationsAsynchronously`),
  `RuntimeAsyncTaskContinuation.Execute` hands a flagged continuation to its context from the
  completing thread, and queues an unflagged one to the thread pool, whose thread then hands the
  next continuation to its context. Measured, 300 runs of each: a `SynchronizationContext`'s `Post`
  came from a pool thread every time for `await tcs.Task`, and from the completing thread every
  time for the field's task. And through a library method, from an ordinary guest:
  `Parallel.ForEachAsync` with a `TaskScheduler` of the guest's, over an enumerator whose
  `MoveNextAsync` returns such a task, has its continuation queued to the scheduler by a pool
  thread on real .NET 11, and by the completing thread on real .NET 10 and on PawPrint running
  .NET 11's CoreLib under decision 6(a).
* Nothing else found. Three `Async` methods awaiting one another over a pending task posted and
  resumed identically matched and unmatched, in eight configurations (captured context throughout
  or `ConfigureAwait(false)` inside, inline continuations allowed or not, completed by the
  context's thread or another), 300 runs each.

How many sites, in RC1's linux-x64 `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App`
(195,315 methods):

* 6,580 methods return a promise type and so have an async variant: 1,679 flagged `Async`, 4,440
  with IL and no flag (async versions), 461 abstract, none without IL. 3,081 are virtual and 31
  static virtual. 28 delegate methods return a promise type and have none.
* 3,540 calls of `AsyncHelpers.Await` in `Async` methods, 3,404 of them the end of a matched
  pattern: 975 name an `Async` method called directly, 788 a method with IL and no flag called
  directly, 1,321 a method that dispatches (1,085 virtual, 196 interface, 40 static virtual through
  `constrained.`), 300 one with no variant (a delegate's `Invoke`, or a row returning no promise
  type, such as `ConfigureAwait` itself applied to a local), and 20 the census's resolver did not
  classify. 8 more would match textually across a basic-block boundary, which the JIT's matcher
  cannot cross.
* Of the sites that bind, 1,822 have `ConfigureAwait(false)` and 1,262 continue on the captured
  context; 1,259 of the latter are ASP.NET Core's, three are `Microsoft.NETCore.App`'s
  (`Parallel.ForEachAsync`'s await of `MoveNextAsync`, and two awaits of `Stream.DisposeAsync` in
  `ZipArchiveEntry`), and none is CoreLib's. No site has `ConfigureAwait(true)`.
* Async versions hold 5,933 `ret`s: 4,274 end `call M; ret` where `M` has a variant, 73
  `call M; newobj ValueTask; ret`, and 1,586 wrap what they return. The census read each
  `call M; stloc; ldloca; call AsTask; ret` as the `call AsTask; ret` it ends with, among the
  4,274; the JIT matches the longer shape at `M` first, and never reads the shorter. Stage 4a's
  matcher finds 99 (74 in `Microsoft.NETCore.App`, 25 in `Microsoft.AspNetCore.App`), most a
  `Stream` override's `ReadAsync` or `WriteAsync` returning `ReadAsync(Memory<byte>, …).AsTask()`. Of the `call; ret`s, 67 return a `Task<T>` as a `Task` and 3 are in a
  `NoInlining` method. 22 async versions are of synchronized methods (`TextReader.SyncTextReader`,
  `TextWriter.SyncTextWriter`), and none holds `jmp` or `tail.`.
* No method carries `PreserveBaseOverridesAttribute` over a promise return, and no ComImport type
  declares a promise-returning method.

### 14. Where the patterns are recognised

* (a) In the interpreter, when `call Await` runs, by looking back at what made its operand. The
  task has been made by then: the thunk has run, and nothing can undo it.
* (b) By rewriting the IL when the frame is made (`frameBody`), replacing the pattern with one call
  through the stub operand scope of decision 9. The rewritten body is a second version of the
  method that every reader of IL offsets must map back, and the escape analysis would have to make
  the same rewrite to agree.
* **(c, chosen) As data in `WoofWare.PawPrint.Semantics`, read where the instruction's token is
  bound.** A pure function of a body and a call's offset says what follows the call in its basic
  block, as `BoxPattern.follower` does for `box`: in a runtime-async body, the `ConfigureAwait`
  call if any, with its constant, and the `Await` call, with its offset and the offset after it; in
  an async version, which of the three tail shapes. It returns the operands of the calls it read
  past, and its caller confirms they name the intrinsics the JIT tests for (`ConfigureAwait` of the
  four promise types, `AsyncHelpers.Await`, `ValueTask`'s one-argument constructors and `AsTask`).
  It never reads past the block's end, because the JIT's matchers are given the block's
  (`codeEndp`, `jit/importer.cpp:6494-6495`); a block starts wherever `BoxPattern` says one does.

  The interpreter reads it in the binder (`CallSiteBinding.bindCall`, and `callvirt`'s own
  binding), so that the method-entry import of `docs/plans/2026-10-24-jit-import-at-method-entry.md`
  binds the variant when it binds the call, as the JIT resolves the pattern's three tokens when
  it imports the first. The analysis reads the same function.

### 15. What a matched site executes

* (a) Each instruction of the pattern stays a step: the call pushes the variant's result where a
  promise was expected, and the instructions after it pass it through. `stloc` would then store a
  `T` in a `ValueTask<T>` local, and every step would lie about the stack's shape.
* **(b, chosen) The call's step is the whole pattern**, as the JIT imports it. The variant's frame
  returns to the instruction after `call Await`; the instructions between never run; and for
  suspension, `DiagnosticIP`, exception dispatch and a live stack walk, the caller stands at the
  `Await` call's offset while the variant runs. No instruction skipped is the start of a block, so
  none has entry processing of its own to skip (a join's widening, a stack-shape check): the next
  instruction entered is the one after the pattern, whose shape is the same either way. The `try`
  regions round the call and round the `Await` are the same, since a region's bounds start blocks.

The call is then any call. Virtual dispatch comes first (decision 19), then intrinsic
classification, then `frameBody`. A variant whose method PawPrint implements without running its
IL is refused by name; a search of PawPrint's own implementations finds none that returns a
promise type. The JIT folds
the variants of `Task.FromResult`, `Task.CompletedTask` and their `ValueTask` forms to their values;
running their async versions gives the same values and never suspends, so PawPrint does not fold.
The thunk takes no part: it stays the body of the ordinary variant of every `Async` method, for
callers without the async convention and for sites that bind no variant.

A matched site in a frame of runtime async's own machinery (CoreLib's `Async` methods returning no
promise) is refused by name. RC1 has none.

### 16. Which methods have a variant, and what PawPrint constructs

The VM gives a variant to the method a token names when its own row returns a promise type
(`ReturnsTaskOrValueTask`), its type is not a delegate, and its type is not ComImport. A generic
method returning its own type parameter has none, whatever the parameter is instantiated at.

`RuntimeAsyncMethod.asyncVariantOf` stays the one constructor, and constructs the variant of every
such method, with the unwrapped signature and the method's own body. What the variant is follows
from the body, as CoreCLR's follows from its flags:

* `MethodBody.AsyncIl`: the user's runtime-async body, which saves contexts.
* `MethodBody.Il`: an async version (decision 17).
* `MethodBody.Abstract`: only a name to dispatch on.
* Anything else (a P/Invoke, an InternalCall, a runtime-provided body): refused by name. RC1 has
  none.

Rejected: a third case of `MethodVariant` for an async version. CoreCLR has one `MethodDesc` for
it, the async variant, and a third case would make every consumer of a method's identity say what
it means by a distinction that only `frameBody` needs.

### 17. How an async version runs

* (a) Rewrite its IL, each `ret` becoming a call of `TransparentAwait` and a `ret`. A `ret` is one
  byte and a call five, so every offset after the first `ret` moves (decision 8 rejected this for
  the same reason).
* (b) A stub of `Semantics` data that calls the ordinary variant and awaits what it returns. The
  ordinary body would then run in a frame without the async convention, where
  `IsRuntimeAsync()` answers false and no tail pattern binds: decision 6(a) again, one frame down.
* **(c, chosen) The method's own IL in a frame that knows it is an async version**, a third case of
  `AsyncFrame`. It saves no contexts (`RequiresAsyncContextSaveAndRestore` is false for it),
  `IsRuntimeAsync()` answers true in it, and it appears in traces under the method's name
  (`IsDiagnosticsHidden` is false for it). Two instructions mean something else in it:
  * A `call` or `callvirt` followed in its block by one of the tail shapes, naming a method with a
    variant, calls the variant, and the frame then returns what it returned
    (`impMatchAsyncVersionTailCall`). A `Task<T>`-returning callee in a `Task`-returning method
    leaves a value the frame drops.
  * Any other `ret` awaits the promise on the stack: the frame calls
    `AsyncHelpers.TransparentAwait` at the method's unwrapped return type (`getAwaitReturnCall`),
    as the context helpers of decision 8 are called, and returns what it returns. The JIT's folds
    of `return default` and `return new ValueTask<T>(value)` (`impFoldAwaitedTopOfStack`) give
    what `TransparentAwait` gives for a completed `ValueTask`, so they are not modelled.

  Whether an await in an async version is a tail await, which passes the callee's continuation on
  and leaves no frame of the async version in a trace, is the JIT's rule, kept as a function in
  `Semantics` (`jit/importercalls.cpp:7899-7909`, `jit/importer.cpp:12008`): always for the wrapped
  `ret`; for `call; ret` and `call; newobj ValueTask; ret`, when the JIT optimises the body
  (`Tier0OptimizationEnabled`, which `Debuggable.allowsJitOptimisation` and `NoOptimization` decide),
  the callee's unwrapped return type is the method's, and `canTailCall` agrees (the caller is not
  the entry point and not `NoInlining`; a callee named exactly does not require a security object,
  `vm/jitinterface.cpp:8293`); never for the `AsTask` shape. There the frame calls the variant of
  the `ValueTask`-returning callee, and if that suspends, the frame suspends with a continuation of
  its own flagged `ValueTaskAdaptedToTask` (`jit/async.cpp:2809-2812`), so it appears in traces at
  its call's offset. The flag is what `AsTask()` means to a value-task source: CoreLib's walk from
  the source's continuation to the first one with a continuation flag stops at it
  (`AsyncHelpers.CoreCLR.cs:860-878`), so the source is not told to use the scheduling context and
  no later continuation has its flags cleared. Measured with a source of the probe's own under a
  captured context: `OnCompleted` gets `None` and the context one post through the shape, as
  through the ordinary variant; through `ValueTask<int> M() => Make()` it gets
  `UseSchedulingContext` and the context no post. RC1 has 99 `AsTask` shapes (74 in
  `Microsoft.NETCore.App`, 25 in `Microsoft.AspNetCore.App`), 22 of them calling a method that is
  not virtual. An await that is not a tail await suspends the frame as a frame of runtime async's
  machinery suspends (`CaptureExecutionContext`, and no continuation flags but the `AsTask`
  shape's). Measured:
  a trace crossing `Task<int> M() => Leaf()` has no frame of `M`, nor has one crossing
  `new ValueTask<int>(Leaf())` or a wrapped `ret`; one crossing `Task M() => Leaf()` (a `Task<int>`
  as a `Task`), a `NoInlining` `M`, or `LeafVT().AsTask()` has `M` at its call's offset; and the
  same assembly compiled without optimisation has `M` in the first two cases as well, and still
  not for the wrapped `ret`.

  The async version of a synchronized method runs like any other async version, except that it
  matches no tail shape (`AwaitPattern.tailShapeAfter` says so) and every `ret` wraps. Its monitor
  is whatever the ordinary variant's is. When PawPrint models the monitor, the exit goes
  immediately before the wrapped `ret`'s call of `TransparentAwait` (`jit/importer.cpp:11901-11914`).
  RC1 has 22, all returning already-completed promises (`Task.CompletedTask`, `FromResult` or
  `FromCanceled` after synchronous work), so none can suspend.

  Refused by name: `jmp`.

### 18. Where a caller keeps what it does if its call suspends

At an async call the JIT decides, per call, whether a suspension is passed on (a tail await) or
makes a continuation, and for the latter how the continuation continues: on the captured context
(a matched await without `ConfigureAwait(false)`), on the thread pool (with it), or with no
preference (every other async call), and whether it is flagged `ValueTaskAdaptedToTask`.

* (a) On the caller's frame, as `TailAwait.InFlight` is today. The frame then holds a fact about
  one call for as long as the frame lives, and each new fact is another state to clear.
* (b) Recomputed when the callee suspends, from the call site's offset. The binder decided once
  whether the site binds; a second reading that disagreed would suspend a call one way that was
  made the other.
* **(c, chosen) On the callee's return state**, beside the call site's offset, set by whatever
  made the call. It lives exactly as long as the call, `leaveBySuspension` reads it where it reads
  the disposition, and a frame made without it cannot be suspended through. `TailAwait.InFlight`
  moves there in the same change, leaving `TailAwait` to say only whether `TailAwait()` has been
  called.

`beginSuspension` lays the continuation out from it: for the thread pool, the
`ContinueOnThreadPool` flag and `FinishSuspensionNoContinuationContext`; for the captured context,
a continuation-context slot and `FinishSuspensionWithContinuationContext`, which writes the slot
and the flags itself; for no preference, what it does today.

### 19. The second vtable slot

Measured on .NET 11.0.0-rc.1: `abstract Task<int> V(); virtual Task W(); void N()` in a class take
slots 4, 6 and 9, and `GetNumVirtuals` is 8; an interface's `Task<int> A(); int B(); ValueTask C()`
take 0, 2 and 3 of 5. The variant's slot follows its ordinary sibling's. Reflection cannot name it:
`GetMethodAt` answers null for it (`vm/runtimehandles.cpp:444-451`), the method iterators skip it,
and every managed reader of a slot number uses it only to index the same table.

CoreCLR fills the variants' slots variant-to-variant: a declaration's variant is overridden by the
variant of whatever overrides the declaration. The builder finds a MethodImpl's declaration by
finding the ordinary one and taking its variant (`vm/methodtablebuilder.cpp:6217-6224`), and a
static virtual method's implementation the same way (`vm/methodtable.cpp:8351-8401`). The unwrapped
signatures are in bijection with the wrapped ones, given the `Task`-or-`ValueTask` kind the
comparison keeps (`SameAsyncVariantKind`), so the two slots can differ in one case only: an
override whose promise type differs from its declaration's, legal only as a covariant return
(`Task<T>` for `Task`), for which the builder adds a return-dropping thunk.

* (a) Lay the variants' slots out in `MethodTableLayout`, as CoreCLR does. Every slot number on
  .NET 11 moves, though nothing can see that; the layout becomes a function of the runtime, since
  .NET 10 makes no variants; and every reader of the tables must skip entries that are not
  methods reflection knows.
* (b) A second table of variants beside the first. Two tables that must agree, filled by the same
  matching.
* **(c, chosen) Dispatch a variant through its ordinary sibling.** The implementation of a
  variant on a receiver is the variant of the implementation of its ordinary sibling, which is the
  relation CoreCLR's own builder uses. `MethodTableLayout` keeps answering only for methods a
  MethodDef row declares, and a lookup of a variant in it stays an error; the dispatch functions
  (`ConcreteVirtualDispatch`, `StaticVirtualDispatch`) take the step, in one place each, and check
  that the implementation's unwrapped return type is the declaration's. Where it is not, the
  call is refused by name: CoreCLR runs a return-dropping thunk there, or, for an override that
  returns a subclass of `Task`, the inherited variant. RC1 has no return-dropping thunk; the
  census did not count the other.

  PawPrint's slot numbers on .NET 11 therefore stay those of a table without variants, which
  differ from CoreCLR's and which no guest can read except by reflecting over CoreLib's internals.

### 20. What the JIT's tier decides

Some of what a trace shows depends on how a body was compiled, and for each PawPrint gives the
answer of the JIT's first tier, which inlines nothing and makes no tail call, as it does
elsewhere:

* An async version that is not tail-awaiting is inlined into its caller by the optimising tier,
  and its frame leaves the trace: measured, `Task M() => Leaf()` shows `M` under tiered
  compilation's first tier and not with `TieredCompilation=0`. PawPrint shows it.
* The wrapped `ret`'s call of `TransparentAwait` is a real tail call when the JIT optimises fully
  (`GTF_CALL_M_IMPLICIT_TAILCALL`, `jit/importer.cpp:12010-12016`; read, not measured), so an
  exception from a task already faulted is thrown with no frame of the async version beneath it.
  PawPrint keeps the frame.
* Which of the awaiter's frames a trace has (decision 13's `GetResult`) depends on what the JIT
  inlined into them.
* The offset a library frame is reported at. Measured from an ordinary guest,
  `TarHeader.TryGetNextHeaderCoreAsync` suspended at its await at IL_0035 is reported at IL_001D,
  where the statement begins, while `StreamReader.ReadToEndAsyncInternal` is reported at its
  `Await` call, as methods the JIT's first tier compiled are. The first awaits a static method of
  a struct the compiler can inline, and the framework's code is precompiled; that this is why is
  read, not established. PawPrint reports the `Await` call's offset.

The rule of decision 17 for a tail await is not among them: both tiers apply it, and only a body
compiled without optimisation, which is a property of its assembly and its method, differs.

### Test guests

The test project's Roslyn (4.14) cannot compile a runtime-async method (5.x can). The stages test
through CoreLib's and the shared framework's runtime-async methods, called from ordinary guests,
which is how real programs reach them. A Roslyn bump, so that guests can declare runtime-async
methods of their own, is a separate change if a stage needs shapes no library method has.

The devshell SDK's compiler (10.0.401) does compile them: a project targeting `net10.0` with
`<Features>runtime-async=on</Features>`, `EnablePreviewFeatures` and `LangVersion` `preview` gives
methods flagged `Async`, which run on .NET 11 by roll-forward and fail to load on .NET 10. The
probes behind decisions 13 to 20 were built so, and run out of process on real .NET 11. Such a
probe's unmatched await must read its task from a field: in a Release build the compiler removes
a local between the call and the `Await`, and the pattern matches after all.

Shapes stage 4 handles that no method of RC1's shared framework or ASP.NET Core has: a covariant
override's return-dropping thunk, and an await of a method
called directly that continues on the captured context outside ASP.NET Core. Shapes only a guest's
own assembly gives: a tail shape in a body compiled without optimisation, as the test project
compiles guests (`Roslyn.compileAssembly`), which runs as an async version when library code awaits
a guest's override.

## Stages

Each stage is one pull request, with its tests first.

1. **(1a) The variant.** `MethodBody.AsyncIl`, `Ordinary | AsyncVariant` in the identity, the one
   constructor of `AsyncVariant`. No behaviour changes, except that running an `Async` method's
   ordinary variant becomes a named refusal in place of the stack-shape failure.
2. **(1b) The thunk and synchronous completion.** The stub operand scope; the task-returning
   thunk; `AsyncCallContinuation`; `IsRuntimeAsync()` answering true in every async-convention
   body; context capture and restore; `AsyncSuspend` and `TailAwait` still refused. An `Async`
   method whose awaits all find completed tasks runs; a pending await stops at the refusal.
   No `Async` method of .NET 11 RC1's shared framework calls `IsRuntimeAsync()`, so its `true`
   answer waits for stage 4's async versions (`StreamReader.ReadAsync`) to be observable.
3. **(2a) The primitives suspension needs**, each testable without runtime async:
   `[FixedAddressValueType]` statics and `Unsafe.AsPointer` of them, `calli` through a function
   pointer read from native memory, and writes of a struct holding references through
   `GetRawData`, `Unsafe.Add` and `Unsafe.As<byte, T>` into an object's field and into
   `Task<T>.m_result`.

   As measured: all of these already worked, so the stage is four guests and no interpreter
   change (`FixedAddressResumeInfo`, `CalliThroughNativeResumeInfo`, `RawDataResultSlotWrites`,
   `AsyncDispatcherStackPointers`). `[FixedAddressValueType]` needs no handling, because a
   PawPrint static never moves. In RC1 it marks only the two `ResumeInfo` statics of CoreLib's
   own continuations. `t_runtimeAsyncAwaitState` is a plain `[ThreadStatic]` struct, which CoreLib
   only takes by `ref`. The pointers CoreLib keeps in thread statics point at locals instead:
   `RuntimeAsyncAwaitState.StackState` and `AsyncDispatcherInfo.t_current`.

   One gap is left. A pointer stored with `stind.i` through a `ref T*`, as `t_current` is stored,
   reads back as a native int, and `ldfld` cannot dereference that. Only CoreLib's async
   instrumentation dereferences `t_current`, and it is off unless a profiler or debugger turns
   it on.
4. **(2b) Suspension and resumption.** The continuation type; `AsyncSuspend`; suspending callers;
   `TailAwait`; the suspended-frame map; `ResumeInfo` and `AsyncResume`; delivering a result;
   `DiagnosticIP`, with its QCall refused.

   As built:
   * The continuation type is `ConcreteTypeHandle.Continuation of ContinuationLayout`, the slots
     it has and its result's type. Its fields are CoreLib's `Continuation`'s, then one per slot,
     named `FieldId.ContinuationSlot` (no metadata names them), placed where
     `ContinuationSlots.offsetOf` says and encoded in `Flags` by
     `ContinuationSlots.slotIndexFlags`. `TestRuntimeAsyncSuspension` checks both against the
     constants of .NET 11's CoreLib (`DataOffset`, `ContinuationFlags.*IndexFirstBit`) for
     arbitrary layouts. Type-system questions only a token or a `RuntimeType` could ask of one fail
     through `ContinuationType.unasked`.
   * `AsyncSuspend` makes its frame leave with its argument as its hidden continuation
     (`IlMachineStateExecution.leaveBySuspension`). What happens next is the caller's: the thunk
     reads it through `AsyncCallContinuation` and gets a zero for the formal result; a frame of
     runtime async's machinery whose call was a tail await passes it on (`TailAwait` is a state
     of `AsyncFrame.Transparent`: requested, then in flight on the next async call); any other
     runtime-async frame suspends (`beginSuspension`), and CoreLib's dispatcher gets it from its
     `calli`. Under decision 6 every await is a call of an `Await` overload, so a chain has one
     continuation of a user body after CoreLib's own, and the user body's layout is always an
     `ExecutionContext` slot and the result's.
   * A suspending user body allocates its continuation, links it as the callee's `Next`, gives it a
     `ResumeInfo` in a native block of its own, and calls `FinishSuspensionNoContinuationContext`
     (the helper for an await of no continuation context) with a byref to the `ExecutionContext`
     slot. When that returns (`AsyncContextStep.AfterFinishSuspension`) the frame, its byrefs and
     byref-like values zeroed and its `stackalloc` memory dropped
     (`AsyncSuspension.suspendedFrame`), goes into `IlMachineState.SuspendedAsyncFrames` under its
     continuation, and leaves. No handler runs.
   * `ResumeInfo.Resume` is `FunctionPointerTarget.AsyncResume`. A `calli` through it takes the
     frame out of the map, frees its `ResumeInfo`, sets `resumed`, pushes the awaited call's
     result from the result slot, and makes the frame the dispatcher's callee
     (`ReturnValueDisposition.ResumedAsync`): its return writes its value through the result
     location and returns null; its suspension returns its new continuation. The map's entry is
     linear (`SuspendedFrames`).
   * `ResumeInfo.DiagnosticIP` is `FunctionPointerTarget.SuspensionPoint (method, ilOffset)`. CoreLib
     reads it through a `void*` field of native memory, which needed the read of a pointer slot
     holding a function pointer to return it as `toCliTypeCoerced` stored it. The
     `AsyncHelpers_AddContinuationToExInternal` QCall is refused, naming the method and its await.
   * Refused by name: a suspension at an await in a `try` region or handler (decision 11), and one
     in runtime async's machinery without a tail await, which only `AwaitTaskWithRareOptions` (an
     await with `ConfigureAwaitOptions.SuppressThrowing`) makes: its continuation's
     `ExecutionContext` comes from `AsyncHelpers.CaptureExecutionContext`, a helper with a result,
     which `ReturnValueDisposition.ResumeAsyncContexts` cannot yet deliver.

   Guests (`RuntimeAsyncResumesWith*.cs`) reach a result of each kind through library methods
   whose awaits lie outside `try` regions: `StreamReader.ReadAsync` (`int`, two suspensions in one
   frame, and an `AsyncLocal` that must flow through the `ExecutionContext` slot),
   `TextWriter.WriteLineAsync(StringBuilder)` (`void`, one suspension per write),
   `TcpListener.AcceptTcpClientAsync` (a reference, through an `IValueTaskSource`) and
   `ZipArchiveEntry.OpenAsync` (`bool`, `int`, `long`, `(bool, string)` and `Stream`). Most of
   CoreLib's own `Async` methods await inside a `try`: `ReadToEndAsync`, `ReadLineAsync`,
   `Stream.CopyToAsync`, `BufferedStream` and `File.*Async` stay refused until stage 3.

5. **(3) Exceptions across suspension.** Exception slots for awaits in `try` regions; the
   dispatcher's unwinding; the QCall that appends async frames. Probably un-parks the
   `FileSystemWatcher` and ASP.NET tests, which stage 2b measured stopping at the refusal of
   decision 11 in `Watcher.DequeueEvents`.

   What stage 2b leaves for it: `ContinuationSlots` already encodes an exception slot, so
   `layoutAwaiting` gives one for an await in a `try`, and resumption (`executeAsyncResumeCalli`)
   reads it, clears it and rethrows what it holds at the await before pushing the result. A
   resumed frame's program counter puts it back inside its `try` regions as it stands, since
   PawPrint finds handlers by offset; a suspension in a handler stays refused, as Roslyn moves
   awaits out of them. The QCall gets its frame from the `SuspensionPoint` in `DiagnosticIP`.
   An exception escaping a resumed frame already reaches the dispatcher's `catch`
   (`ExceptionEscape.Propagate`); only its unwinding through further continuations reaches the
   QCall. The non-tail suspension of `AwaitTaskWithRareOptions` still needs
   `AsyncHelpers.CaptureExecutionContext`, whose result a context helper's step must carry.

   As built:
   * `AsyncSuspension.layoutAwaiting` gives a continuation an exception slot when its await lies in
     a `try` region (`tryRegionsAround`), as the JIT does for any `try` but its own round the body.
     A suspension in a filter or handler stays refused by name (`handlersAround`). Measured on RC1:
     the shared framework's two packs and ASP.NET Core's hold 2604 `Async` methods with 5484 awaits
     (calls of `AsyncHelpers.Await*` and `*AwaitAwaiter`), 1733 of them in a `try` region and none
     in a filter or handler.
   * Resuming (`executeAsyncResumeCalli`) takes the exception from the slot and clears it
     (`AsyncSuspension.takeException`), as `RethrowExceptionOnResumption` does, and then raises it
     at the awaited call (`SuspendedFrame.CallSite`) without pushing a result. The JIT rethrows with
     `CORINFO_HELP_THROWEXACT`, which keeps the trace and, dispatching with `ExKind.RethrowFlag`,
     appends no frame for the frame it begins in (`RH_EH_FIRST_RETHROW_FRAME`): a `rethrow`'s
     behaviour, from an offset PawPrint names rather than the frame's program counter
     (`ExceptionDispatching.throwExactAt`). The frame's own handlers, found by offset, see it.
   * `AsyncHelpers_AddContinuationToExInternal` appends a frame of the `SuspensionPoint`'s method
     at its IL offset to the exception's trace, consuming a pending foreign-raise flag as every
     `AppendElement` does, and projects it as a dispatch's conclusion does
     (`ExceptionDispatching.appendContinuationFrame`): a fresh token, as CoreCLR's append copies a
     frozen trace rather than change it.
   * `AwaitTaskWithRareOptions`, a frame of runtime async's machinery suspending other than by a
     tail await, makes its continuation the same way and calls `CaptureExecutionContext()` in place
     of a suspension helper; that helper's result, which `ReturnFrameResult.ResumeAsyncContexts`
     now carries, goes into the continuation's `ExecutionContext` slot
     (`AsyncContextStep.AfterCaptureExecutionContext`). `SemaphoreSlim.WaitAsync` with a token or
     a timeout reaches it.

   Guests reach each path through library methods whose awaits lie in `try` regions:
   `HttpContent.LoadIntoBufferAsync` (a `catch` in the resumed frame, which rethrows, or wraps an
   `IOException`), `StreamWriter.DisposeAsync` and `StreamReader.ReadToEndAsync` (`finally` blocks
   after resuming, on both paths, and an exception crossing a continuation with no slot first),
   `RuntimeAsyncTraceCrossesSuspension.cs` (the trace against real .NET 11's) and
   `SemaphoreSlim.WaitAsync` (`AwaitTaskWithRareOptions`).

   Two things the guests cannot see. Nothing reads a continuation's exception slot after its
   frame resumes, since PawPrint never reuses a continuation; `TestRuntimeAsyncSuspension` checks
   that resuming clears it. And `AwaitTaskWithRareOptions`'s `ExecutionContext` slot is restored only
   for that frame's own resumption, which reads no context before the continuation after it
   restores its own: an `AsyncLocal` change handler on real .NET 11 logs the same switches whether
   the slot holds the captured context or null.

   PawPrint's traces of an exception crossing a suspension differ from real .NET 11's in two ways
   that are not stage 3's. The awaiter's frames differ by what the JIT inlined (.NET 11's inlines
   `RuntimeAsyncTaskContinuation.GetResult(ref byte)`; PawPrint shows it). And under decision 6 each
   await is its own task, so an exception crossing several awaits is thrown again from each task's
   awaiter, adding its frames once per task, where real .NET 11 unwinds one chain of continuations
   and shows them once; the formatter hides the `ExceptionDispatchInfo` boundaries between them,
   because the frame before each is an `Async` method. `StreamWriter.DisposeAsync`'s trace shows
   it, and lacks `FlushAsyncInternalWithGuard` too: `StreamWriter.FlushAsync` calls that only when
   `RuntimeHelpers.IsRuntimeAsync()` answers true, as it does in the async version of `FlushAsync`
   that real .NET 11's await binds.

   Parks, re-measured on 11.0.0-rc.1 (2026-11-12): the Linux-CoreLib `FileSystemWatcher` test
   passes and is un-parked. The two ASP.NET two-process tests get past `Watcher.DequeueEvents` and
   stop where the server's minimal-API request delegate invokes
   `RequestDelegateFactory.ExecuteResultWriteResponse`, an `Async` method, through reflection:
   `RuntimeMethodHandle.InvokeMethod` binds the ordinary variant, the task-returning thunk, but
   `NativeReflectionInvocation` concretises the async IL's locals before the call, which refuses
   (`MethodBody.AsyncIl`). Lifting it means a reflective invocation of an `Async` method running
   its thunk as a `call` does, with a guest that invokes one reflectively; which locals a `.cctor`
   it triggers sees loaded (the thunk's, or the IL's) wants measuring first.
6. **(3b) Indirect calls.** Every route that binds an `Async` method's ordinary variant other than
   a call token runs the task-returning thunk, as the token does.

   As built:
   * What a frame runs is chosen in one place, `IlMachineStateExecution.frameBody`: the metadata
     body; runtime-async IL as an IL body, for a method with the async calling convention; or the
     thunk, with its stub scope. `callMethodWithCommitment` builds from it every frame a guest's
     call reaches, after virtual dispatch and intrinsics.
   * Measured before the change, every indirect route already went through
     `callMethodWithCommitment` and ran the thunk: delegates bound by `ldftn`, `ldvirtftn`,
     `CreateDelegate` (static, closed and open over a virtual method) and multicast; `calli` through
     `RuntimeMethodHandle.GetFunctionPointer`; an `[UnsafeAccessor]`; and the `InvokeStub_` that
     reflection emits from a method's second invocation.
   * The one that did not was a method's first reflective invocation. `RuntimeMethodHandle.InvokeMethod`
     concretises the method's locals before its `.cctor` runs, since CoreCLR compiles the method
     first, and it asked for the async IL's. It now asks `frameBody` (except of a declaration with
     no body, which the call may yet dispatch to an override), and loads the thunk's: CoreCLR
     compiles the thunk, runs the `.cctor` in the thunk's prologue (`CEEInfo::initClass` asks for it
     in a static method of a type without `beforefieldinit`), and compiles the async variant only
     when the thunk calls it. This was read from the source, not measured: the thunk's locals name
     only CoreLib's types and the method's result type, which the signature has already loaded, so
     no guest can tell, and a mutant loading no locals for a thunk passes every guest.
   * Frames PawPrint builds without a guest's call (the entry point, CoreLib's startup calls, the
     POSIX signal handler) still take the metadata body. None can be an `Async` method returning a
     promise: a `Main` returning anything but `void` or `int` is refused first, and the signal
     handler is `[UnmanagedCallersOnly]`. `concretizeLocals` refuses runtime-async IL as a bug.

   Guests: `RuntimeAsyncReflectionInvoke.cs`, `RuntimeAsyncDelegateInvoke.cs` and
   `RuntimeAsyncFunctionPointerInvoke.cs`, against the oracle, and
   `sourcesImpure/RuntimeAsyncReflectionInvokeEmittedStub.cs`, with dynamic code supported so that
   PawPrint runs the emitted stub (PawPrint's tests otherwise turn it off, so the pure guest's later
   invocations take `InvokeMethod` under PawPrint and the stub under the oracle); checked to exit 0
   on real .NET 10 and 11. Their targets are `Stream`'s `FinishReadAsync` (a static local function
   returning `ValueTask<int>` that awaits a task the guest passes), `Stream.FinishWriteAsync`,
   `IndentedTextWriter.WriteAsync(string)` and `OutputTabsAsync` (awaiting a writer the guest
   supplies) and `StreamReader.ReadAsyncInternal`. Each checks synchronous completion, suspension,
   a fault reaching the task rather than `TargetInvocationException`, and that a live stack shows
   the method once.

   Parks, re-measured on 11.0.0-rc.1 (2026-11-12): both ASP.NET two-process tests pass and are
   un-parked.
7. **(4) The await pattern**, in the stages below. Each depends on the one before it unless it
   says otherwise, and each leaves every matched site either bound or awaiting as decision 6(a)
   does, by decision 13's classification, so each merges alone.

   A stage's guest shows that its sites bind by a difference decision 13 measured. Where the
   callee is, or tail-awaits its way to, an `Async` body, an exception that crosses the await
   after a suspension has the callee's frame and the caller's adjacent in its formatted trace,
   with no `RuntimeAsyncTaskContinuation.GetResult(Task, Byte&)` between them. Each guest is an
   ordinary program run on real .NET 11 first, as `RuntimeAsyncTraceCrossesSuspension.cs` was, and
   checks its constants only there. The traces quoted below were measured so.

8. **(4a) Await sites as data.** No interpreter behaviour changes.
   * `Semantics`: what follows a call in its block (decision 14), for the await pattern and the
     three tail shapes, and the tail-await rule of decision 17 as a function of static facts.
   * `Loader`: `asyncVariantOf` constructs the variant of every method decision 16 gives one, and
     refuses a delegate's method and a ComImport type's by name. `frameBody` refuses to run an
     async version, by name, and no call makes one.

   Tests first: properties of the matcher against a reference that reads the instructions
   backwards from each `call Await` and each `ret`, over generated instruction sequences and over
   every body of the framework under test (a match never contains a block's start; every
   `call Await` whose operand comes straight from a call in its block ends a match); and
   `TestRuntimeAsyncVariant` for the variants: `StreamReader.ReadBufferAsync` (`AsyncIl`),
   `StreamWriter.FlushAsyncInternal` and `TaskCompletionSource<T>.get_Task` (async versions),
   `Stream.ReadAsync` (virtual), `IAsyncEnumerator<T>.MoveNextAsync` (abstract), and none for
   `Func<Task>.Invoke` or `Task<T>.ConfigureAwait`. As a fact to pin: in
   `StreamReader.ReadToEndAsyncInternal`, the call at IL_0045 is followed by `ConfigureAwait(false)`
   and an `Await` at IL_0053.

   Depends on stage 3b.

   As built:
   * `AwaitPattern` (`WoofWare.PawPrint.Semantics`) reads what follows a `call` or `callvirt` in its
     block: `awaitAfter` the await pattern, as an `AwaitFollower` (the `ConfigureAwait` call with its
     constant, and the `Await` call's operand, its offset and the offset after it), and
     `tailShapeAfter` an async version's `TailShape` (`Return`, `NewValueTask`, `AsTask`). Each
     returns the operands it read past without resolving them. Where the caller finds one that does
     not name the intrinsic the JIT tests for, the JIT matches nothing at that call, since nothing
     shorter it would read instead can match there. A block starts where `BoxPattern.blockStarts`,
     now public, says.
   * Each answers at any call, so it answers too for the `ConfigureAwait` or `AsTask` call inside a
     longer match. The JIT never imports such a call once the longer match binds; the caller asks
     only at a call it runs.
   * `AwaitPattern.isTailAwait` is decision 17's rule over an `AsyncVersionAwait`: a wrapped `ret`,
     or a call in a tail shape with the `TailCallFacts` that `Tier0OptimizationEnabled` and
     `canTailCall` read.
   * `RuntimeAsyncMethod.asyncVariantOf` constructs the variant of every promise-returning method
     whose body is runtime-async IL, IL (an async version) or none (`MethodBody.Abstract`), and the
     variant keeps that body. It refuses by name a delegate's method (`DelegateMethod`, decided
     before the return type is classified, as the class loader decides it, so `Func<Task>.Invoke`
     is refused as a delegate's), a ComImport type's (`ComImportType`), and a P/Invoke, an
     InternalCall, a runtime-provided body or a row naming no IL (`UnmodelledBody`). A type is a
     delegate when its immediate base type is CoreLib's `MulticastDelegate`, bound by identity.
   * `IlMachineStateExecution.frameBody` refuses by name a variant whose body is not runtime-async
     IL.
   * `TestAwaitPattern` checks both matchers at every call against a reference that reads backwards
     from each `call Await` and each `ret`: over 3,000 generated bodies, whose block starts (branch
     targets and exception-region bounds) the generator records and `BoxPattern.blockStarts` must
     agree with, and over every IL body of the framework under test. Over RC1's linux-x64
     `Microsoft.NETCore.App` it reads the census's 1,932 awaits in 901 runtime-async bodies, and the
     4 that a block's start splits. `TestRuntimeAsyncVariant` checks the variant of every method of
     CoreLib and `System.Net.Http` on both runtimes, the methods named above, and the refusals over
     Roslyn-compiled fixtures, among them a `MulticastDelegate` of another assembly.
   * Not modelled: in debuggable code compiled with a debugger's scope information, the JIT also
     starts a block where a local's scope begins or ends (`fgMakeBasicBlocks`). PawPrint has no
     debugger.

9. **(4b) Awaits of `Async` methods called directly.** A matched site with `ConfigureAwait(false)`
   in a user's runtime-async body, whose method is flagged `Async` and does not dispatch (a `call`,
   or a `callvirt` of a method that is not virtual), binds the variant.
   * The binder returns decision 13's classification, and the call's step is the pattern
     (decision 15).
   * The callee's return state says what its caller does if it suspends (decision 18), and
     `TailAwait.InFlight` moves there.
   * `beginSuspension` sets `ContinueOnThreadPool` for such a call. A chain now holds several
     continuations of user bodies, which `leaveBySuspension`, the dispatcher's `calli` and
     exception slots already carry.

   The guest first: `StreamReader.ReadToEndAsync` over a stream of the guest's whose `ReadAsync`
   returns a pending task and then faults it. `ReadToEndAsyncInternal` awaits `ReadBufferAsync`
   directly, in a `try`. Real .NET 11's trace is `GetResult[T](Task, Byte&)`, `ReadBufferAsync`,
   `ReadToEndAsyncInternal`, the last at IL_0053; before the stage a second `GetResult` frame
   separates the two methods. The same guest reads to the end across two suspensions.
   `RuntimeAsyncResumesWithInt.cs` (`ReadAsyncInternal`, which awaits `ReadBufferAsync` outside any
   `try`) and the other `RuntimeAsync*` guests keep passing.

   Not yet bound: a site that continues on the captured context; a callee without the `Async`
   flag; a callee that dispatches; a `constrained.` call. Refused by name: a matched site in a
   frame of runtime async's machinery.

   As built:
   * `AwaitSiteBinding.classify` (`WoofWare.PawPrint.Loader`) is decision 13's classification, a
     pure function of `AwaitSiteFacts`: whether the await continues on the captured context, the
     opcode, the `constrained.` prefix, whether the named method dispatches, and its variant's body
     or why it has none. It answers `BindsVariant` (with the `ContinuationContextHandling` the
     caller's continuation gets), `NoVariant` (the `AsyncVariantRefusal`) or `NotYetBound` (an
     `AwaitNotYetBound`). A variant whose body PawPrint does not model is not yet bound, and stage
     4c refuses it. A call naming an abstract method that neither dispatches nor has a
     `constrained.` prefix is outside its domain, as the JIT refuses to compile one first.
   * `CallSiteBinding.awaitSite` asks `AwaitPattern.awaitAfter` at a `call` or `callvirt` the
     interpreter runs in a frame with the async calling convention, binds the pattern's other two
     operands and confirms they name CoreLib's `[Intrinsic]` `AsyncHelpers.Await` and a promise
     type's `ConfigureAwait` (where an operand names anything else, no site begins there), and
     classifies the site. `bindCall` returns `CallSiteBinding.BoundAwait` where it binds, and
     `callvirt`'s own binding asks before its `constrained.` handling. `UnsafeAwaitAwaiter` has a
     textual `call; call` that names no `Await`, which a binder skipping the confirmation refuses
     as a site in runtime async's machinery.
   * The bound call moves the caller's program counter to the `call Await` before it enters the
     variant (`UnaryMetadataCallOps.standAtAwait`), so the frame records that offset as its call
     site and advances past the `call Await` alone; the instructions between run never. A
     `callvirt`'s null check comes first, at the `callvirt`, since `OpcodeFaults` admits no
     `NullReferenceException` from a `call`; the JIT reports it at the await's offset (read, not
     measured), which no guest reaches.
   * `MethodReturnState.OnSuspension` is decision 18's record, set by `callMethodWithCommitment`
     for every callee with the async calling convention and `None` for every other:
     `CallerTakesContinuation` (the thunk, or CoreLib's dispatcher resuming a frame), `TailAwait`,
     or `CallerSuspends` with the handling. `leaveBySuspension` reads it with the disposition, and
     fails on a frame made without it. `TailAwait` is `NotRequested | Requested`; the call it
     marks consumes the mark, and a frame of runtime async's machinery that returns or suspends
     with the mark unconsumed fails.
   * `beginSuspension` takes the handling: `ContinuationSlots.handlingFlags` gives
     `ContinueOnThreadPool` its bit, checked against CoreLib's `ContinuationFlags`, and the helper
     stays `FinishSuspensionNoContinuationContext`.
   * An async variant that PawPrint would implement itself (`Intrinsics.call`), or that CoreCLR
     replaces (an `IntrinsicBody` other than its own IL), is refused by name in
     `callMethodWithCommitment`. No bound site reaches one.

   Guests, each run on real .NET 10 and 11 first: `RuntimeAsyncAwaitsAsyncMethodDirectly.cs` (the
   guest above; PawPrint shows `RuntimeAsyncTaskContinuation.GetResult(Byte&)` as the awaiter's
   last frame where .NET 11 shows the generic `GetResult[T](Task, Byte&)` it calls, which decision
   20 names, so the guest accepts either), `RuntimeAsyncAwaitsAsyncMethodByCallvirt.cs`
   (`XDocument.LoadAsync`, whose `LoadAsyncInternal` awaits `ReadContentFromAsync` by a `callvirt`
   of a method that is not virtual, IL_0054) and `RuntimeAsyncDirectAwaitContinuesOnThreadPool.cs`.
   The last shows the flag: a guest decoder installs a `SynchronizationContext` on the thread that
   resumes `ReadBufferAsync`, and CoreLib's dispatcher then queues `ReadToEndAsyncInternal`'s
   continuation to the thread pool (`QueueContinuationFollowUpActionIfNecessary`), as real .NET 10
   and 11 and decision 6(a) all do; a continuation without the flag runs inline under the
   decoder's context. `TestAwaitSiteBinding` checks the classification over generated facts
   against the rule above.

10. **(4c) Async versions.** `frameBody` runs the variant of a promise-returning IL method without
    the `Async` flag as decision 17 says: the tail shapes, the wrapped `ret`, the tail-await rule,
    and a suspension that is not a tail await. Matched sites with `ConfigureAwait(false)` bind such
    a method called directly, and a tail shape binds a callee called directly, `Async` or not.
    `IsRuntimeAsync()` already answers true in any frame with the async convention.

    The guest first: `StreamWriter.WriteAsync(string)` with `AutoFlush` over a stream of the
    guest's whose `WriteAsync` returns a pending task and then faults it. `WriteAsyncInternal`
    awaits `FlushAsyncInternal`, which has no `Async` flag and ends `call Core; ret`, a tail await of
    the local function that awaits the stream. Real .NET 11's trace is `GetResult(Task, Byte&)`,
    `<FlushAsyncInternal>g__Core|82_0`, `WriteAsyncInternal`, with no frame of `FlushAsyncInternal`;
    before the stage a second `GetResult` frame separates the two methods. A second guest, for a
    suspension that is not a tail await (a `Task<T>` returned as a `Task`: 67 `call; ret`s in RC1),
    takes whichever such method a guest reaches most simply, and shows its frame in the trace at
    its call's offset. The tail-await rule's property test is against a table measured on real
    .NET 11 from methods of each shape, compiled with and without optimisation.

    A third guest, for the `AsTask` shape (`sourcesImpure`, since it reads a directory):
    `TarFile.CreateFromDirectoryAsync` of a directory holding one file, into a stream of the
    guest's whose write pends and then faults. `CreateFromDirectoryInternalAsync` awaits
    `TarWriter.WriteEntryAsync(string, string, CancellationToken)` directly, which ends
    `call ReadFileFromDiskAndWriteToArchiveStreamAsEntryCoreAsync; stloc; ldloca; call AsTask; ret`.
    Real .NET 11's trace has `ReadFileFromDiskAndWriteToArchiveStreamAsEntryCoreAsync`,
    `TarWriter.WriteEntryAsync` and `CreateFromDirectoryInternalAsync` adjacent; before the stage it
    has a `GetResult` frame and no `WriteEntryAsync`. No guest reaches a value-task source of its
    own through the shape at this stage, so `TestRuntimeAsyncSuspension` checks that the
    continuation's `Flags` carry `ValueTaskAdaptedToTask` (bit 3), and stage 4f's guest gains the
    measured `OnCompleted` check once a captured-context caller can reach one. The frames beneath
    `WriteEntryCoreAsync` come from a `constrained.` site unbound until 4e, so the guest checks only
    the three adjacent frames; real .NET 11 reports `WriteEntryAsync` at IL_002E.

    The async version of a synchronized method is no longer refused; its guest lands in 4d, since
    all 22 in RC1 are virtual overrides.

    Not yet bound: a tail shape whose callee dispatches, which wraps instead, as it does where the
    callee has no variant. Refused by name: `jmp`; a variant of a method with no IL, or one
    PawPrint implements itself.

    As built:
    * `frameBody` gives the async variant of a method with IL a frame of a third kind,
      `AsyncFrame.AsyncVersion`, which runs the method's own IL and saves no contexts. Its step
      (`AsyncVersionStep`) is `Running` until it makes the call that ends its IL, and then
      `Returning`: once that call has completed, by returning or by the frame's resuming, the frame
      returns the call's result before running another instruction
      (`AsyncVersionReturn.returnsNow`, asked by `executeOneStep` as it asks for a context helper's
      call), dropping a `Task<T>`'s result in a method returning `Task`. No instruction of a tail
      shape after its call runs, as none of a matched await's does, and the IL is not rewritten.
    * A `ret` of a `Running` frame calls the `AsyncHelpers.TransparentAwait` that
      `getAwaitReturnCall` chooses, at the method's unwrapped return type, with the promise on the
      stack as its argument, standing at the `ret`, as a tail await
      (`AsyncVersionReturn.beforeReturn`). An exception it throws leaves through the async
      version's frame, which stays in the trace (decision 20).
    * `CallSiteBinding.awaitSite` reads a tail shape at a call an async version runs, unless the
      method is synchronized, confirms that the `newobj`'s or `AsTask`'s operand names CoreLib's
      intrinsic (a one-argument constructor of `ValueTask` or `ValueTask<T>`, not `ValueTask<T>(T)`),
      and classifies the site. Where it binds, `TailCallFacts.ofCall` (`Semantics`) reads decision
      17's facts from metadata, and `AwaitPattern.isTailAwait` decides: a tail await passes the
      callee's continuation on (`OnSuspension.TailAwait`); any other makes a continuation as runtime
      async's machinery does, through `CaptureExecutionContext`, flagged `ValueTaskAdaptedToTask`
      for the `AsTask` shape. The callee's unwrapped result type must be the method's or the method
      must return nothing; any other pair is refused by name, as only unverifiable IL makes one.
    * Decision 13's classification takes the pattern (`MatchedPattern.Await` with whether it
      continues on the captured context, or `MatchedPattern.TailShape`). An await with
      `ConfigureAwait(false)` binds an async version as it binds an `Async` method; a tail shape
      binds either with no continuation preference. `AwaitNotYetBound.CalleeIsAsyncVersion` is
      gone, and a variant whose body PawPrint does not model is `AwaitSiteBinding.Unmodelled`,
      refused by name where the site runs.
    * Decision 18's record says what a suspending caller's continuation is flagged with
      (`OnSuspension.CallerSuspends` of `SuspendedCallFlags`: `Continues` with a handling, or
      `ValueTaskAdaptedToTask`), and `callMethodWithCommitment` takes the caller's record, checked
      against the caller's frame kind. `ContinuationSlots.callFlags` gives the bits.
    * The stack-shape import reads an async version's IL against the ordinary variant's signature,
      whose `ret` returns the promise.
    * `jmp` is refused wherever it runs ("TODO: Jmp unimplemented"), an async version included.

    Guests, each run on real .NET 10 and 11 first:
    `RuntimeAsyncAsyncVersionTailAwait.cs` (`StreamWriter.WriteAsync(string)`, the trace above);
    `RuntimeAsyncAsyncVersionSuspends.cs` (an asynchronous `XmlReader` whose declaration names
    another encoding, so that `ParseXmlDeclarationAsync` awaits the async version of
    `SwitchEncodingAsync`, which ends `call ReadDataAsync; ret`, a `Task<int>` returned as a `Task`:
    real .NET 11's trace has `ReadDataAsync`, `SwitchEncodingAsync` at its call's offset, IL_0051,
    and `ParseXmlDeclarationAsync` adjacent); and `RuntimeAsyncAsyncVersionWrappedReturn.cs`, for
    the wrapped `ret`'s tail await, which no other guest suspends at (an asynchronous `XmlWriter`'s
    `FlushAsync` reaches `XmlUtf8RawTextWriter.FlushAsync`, which awaits the async version of
    `Stream.FlushAsync()`, whose `ret` awaits the guest's pending flush: real .NET 11's trace has no
    frame of `Stream.FlushAsync`). The last behaves the same bound or not, so it cannot fail first;
    a mutant whose wrapped `ret` suspends with a continuation of its own fails it. Real .NET 11
    reports `ParseXmlDeclarationAsync` at IL_024F, where a statement begins, rather than at its
    `call Await` at IL_026F, which PawPrint reports (decision 20), so that guest does not check it.

    The `AsTask` guest, `sourcesImpure/RuntimeAsyncAsyncVersionAsTask.cs`, exits 0 on real .NET 10
    and 11 but is parked on both runtimes: it stops before any await, where
    `TarWriter.ConstructEntryForWriting` names a file entry's owner from the passwd database,
    whatever the entry's format, through `SystemNative_GetPwUidR`, which PawPrint does not
    implement (and its group after it). Of RC1's `AsTask` shapes in methods that are not virtual,
    only `TarWriter.WriteEntryAsync`'s and `Socket`'s are awaited directly with
    `ConfigureAwait(false)`, so no active guest reaches the shape at this stage;
    `TestRuntimeAsyncSuspension` checks the flag's bit and a fresh continuation's `Flags`.

    `TestAwaitPattern` checks `TailCallFacts.ofCall` and `isTailAwait`, over methods of each shape
    that the test's Roslyn compiles in Release and in Debug, against which of them real .NET 11
    shows in a trace (under tiered compilation, `Ret`, `RetVoid`, `NewValueTask`, `NewValueTaskVoid`
    and `RetVT` have no frame in Release, and every shape has one in Debug; `Covariant`,
    `NoInlining`, `NoOptimization`, `AsTask` and `AsTaskVoid` have one in both). Two of the rule's
    facts no guest or fixture reaches: an entry point that is an async version, which C# cannot
    make (a `Task`-returning `Main` is wrapped by a synthesised entry point), and a callee flagged
    `RequireSecObject`. `TestAwaitSiteBinding` checks the classification against stage 4c's rule.

11. **(4d) Dispatch of a variant.** A matched site or tail shape whose `callvirt` names a virtual or
    interface method binds the named method's variant, and `ConcreteVirtualDispatch` finds its
    implementation through the ordinary sibling (decision 19). The implementation is an `Async`
    body or an async version, both built.

    The guest first: `StreamWriter.DisposeAsync` over a pending, then faulting, stream.
    `DisposeAsyncCore` awaits `FlushAsync()` by `callvirt`; its async version is the first frame in
    which `IsRuntimeAsync()` answers true to a caller that reads it, so `FlushAsync` tail-awaits
    `FlushAsyncInternalWithGuard`. Real .NET 11's trace is `GetResult(Task, Byte&)`,
    `<FlushAsyncInternal>g__Core|82_0`, `FlushAsyncInternalWithGuard`, `DisposeAsyncCore`; before the
    stage it lacks `FlushAsyncInternalWithGuard`, as `RuntimeAsyncFinallyAfterSuspension.cs` records.
    A second guest, for an `Async` body reached by dispatch: `IndentedTextWriter.WriteAsync(string)`
    over a pending, then faulting, writer of the guest's, which awaits its own virtual
    `OutputTabsAsync`. Real .NET 11's trace is `GetResult(Task, Byte&)`, `OutputTabsAsync`,
    `WriteAsync`.

    A guest for the async version of a synchronized method (all 22 in RC1 are virtual overrides):
    `TextWriter.CreateBroadcasting(TextWriter.Synchronized(writer), TextWriter.Null)`, then
    `WriteAsync` and `FlushAsync`, both over a guest writer that completes and over a `StreamWriter`
    on a stream whose synchronous `Write` throws. Real .NET 11's trace is `SyncTextWriter.Flush`,
    `SyncTextWriter.FlushAsync`, `BroadcastingTextWriter.FlushAsync`. It behaves the same bound or
    not, so it cannot fail first; the stage shows a mutant failing it. A second guest, which does
    fail first: an unsynchronized `CreateBroadcasting(streamWriter).FlushAsync()`, whose real trace
    is `GetResult`, `<FlushAsyncInternal>g__Core|82_0`, `FlushAsyncInternalWithGuard`,
    `BroadcastingTextWriter.FlushAsync`.

    Refused by name: an implementation whose unwrapped return type is not its declaration's (the
    return-dropping thunk, and an override returning a subclass of `Task`). Not yet bound: a
    `constrained.` call.

12. **(4e) Constrained calls and static virtual methods.** A matched site whose call has a
    `constrained.` prefix binds the variant of what the prefix resolves: 40 sites in RC1 call a
    static abstract method of `IReadWriteAdapter` (`System.Formats.Tar`, `System.Net.Security`,
    `System.Net.Mail`), and 2 an interface's instance method. `StaticVirtualDispatch` takes
    decision 19's step.

    The guest first: `TarWriter.WriteEntryAsync` of a `UstarTarEntry` whose data stream is the
    guest's, cannot seek, and pends, then faults, on a read.
    `TarHeader.WriteWithUnseekableDataStreamCoreAsync<AsyncReadWriteAdapter>` awaits
    `constrained. !!TAdapter call IReadWriteAdapter::CopyToAsync`, whose async version tail-awaits
    `Stream.CopyToAsync`'s, which tail-awaits the `Async` local function `<CopyToAsync>g__Core|30_0`.
    Real .NET 11's trace is `GetResult[T](Task, Byte&)`, `<CopyToAsync>g__Core|30_0`,
    `WriteWithUnseekableDataStreamCoreAsync`, `GetResult(Task, Byte&)`, `WriteEntryCoreAsync`; before
    the stage another `GetResult` frame separates the local function from its caller.
    `TarReader.GetNextEntryAsync`, whose `TryGetNextHeaderCoreAsync` awaits the adapter's
    `ReadExactlyAsync`, will not do: no `Async` body lies under that await, and its trace is the
    same bound or not.

13. **(4f) Awaits that continue on the captured context.** A matched site without
    `ConfigureAwait(false)` binds as the others do, and its continuation gets a continuation-context
    slot and `FinishSuspensionWithContinuationContext` (decision 18). With this the classification's
    third kind has no case left, and is deleted: every matched site binds unless CoreCLR's VM
    supplies no variant there.

    The guest first: `Parallel.ForEachAsync` over an `IAsyncEnumerable<T>` of the guest's, with
    `ParallelOptions.TaskScheduler` a scheduler of the guest's that counts what it is given, and a
    `MoveNextAsync` that returns a pending `ValueTask<bool>` and then completes it from another
    thread. The loop body's await of `MoveNextAsync()` (`<ForEachAsync>b__57_0`, IL_002F) is one of
    the three such sites outside ASP.NET Core. With a task that forbids inline continuations, real
    .NET 11 queues the continuation to the guest's scheduler from a pool thread; before the stage
    PawPrint queues it from the completing thread (decision 13). The guest also checks that the
    loop's body runs on its scheduler, which holds before the stage too, where `Await` captures
    the scheduler: a mutant that calls the helper for no continuation context must fail it. It
    also checks, through an `AsTask` shape a captured-context caller reaches, that a value-task
    source of the guest's gets `None` in `OnCompleted` and its context one post, as measured for
    decision 17.

14. **(4g) Whole traces.** The guests that compare traces (`RuntimeAsyncTraceCrossesSuspension.cs`,
    `StreamWriter.DisposeAsync`'s in `RuntimeAsyncFinallyAfterSuspension.cs`, and the guests of
    stages 4b to 4f) compare whole traces with real .NET 11's, less the awaiter frames decision 20
    names. `docs/divergences.md` records what decision 20 leaves, and decision 19's slot numbers.
    The JIT also reuses a resumed frame's continuation when it suspends again; PawPrint makes a
    fresh one, which nothing managed can tell apart, and `AsyncSuspension.takeException` clears the
    exception slot a reused one would carry.

15. **(4h) The escape analysis reads await sites.** `EscapeAnalysis` asks decision 13's
    classification what a call in a runtime-async body runs: at a bound site the variant's body,
    whose exceptions escape at the site, rather than the opaque thunk. Depends on stage 4a alone
    for the data, and on stage 4f for the classification being final; its oracle is
    `TestEscapeAnalysis`, whose fixture needs runtime-async methods of its own (see "Test guests").
