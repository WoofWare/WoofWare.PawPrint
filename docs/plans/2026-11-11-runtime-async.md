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
  optional: scheduling order differs between the two.

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

### Test guests

The test project's Roslyn (4.14) cannot compile a runtime-async method (5.x can). The stages test
through CoreLib's and the shared framework's runtime-async methods, called from ordinary guests,
which is how real programs reach them. A Roslyn bump, so that guests can declare runtime-async
methods of their own, is a separate change if a stage needs shapes no library method has.

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
6. **(4) The await pattern.** Binding async variants at `await` sites and in async versions'
   tail-call patterns; async versions of plain `Task`-returning methods; the second vtable slot.

   What stage 2b leaves for it: once an await binds a variant, chains hold several continuations
   of user bodies, and an await without `ConfigureAwait(false)` gets a continuation-context slot
   and calls `FinishSuspensionWithContinuationContext`; `beginSuspension` now always lays out an
   `ExecutionContext` slot and the result's and calls the helper for no continuation context,
   which is right only under decision 6. The JIT also reuses a resumed frame's continuation when
   it suspends again; PawPrint makes a fresh one, which nothing managed can tell apart.

   What stage 3 leaves for it: binding variants makes one chain of the continuations an exception
   crosses, so its trace loses the awaiter frames each task added under decision 6, and the guests
   that compare traces (`RuntimeAsyncTraceCrossesSuspension.cs`, `StreamWriter.DisposeAsync`'s in
   `RuntimeAsyncFinallyAfterSuspension.cs`) can then check whole traces against real .NET 11's.
   A reused continuation must have its exception slot cleared on resumption, which
   `AsyncSuspension.takeException` already does.
