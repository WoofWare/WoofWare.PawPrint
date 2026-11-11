# Running runtime-async methods (.NET 11)

## Why

.NET 11 compiles every async method in CoreLib, and in every shared-framework library, as a
*runtime-async* method (`System.Private.CoreLib.csproj` sets `Features=runtime-async=on`;
`libraries/Directory.Build.targets` does the same for the shared framework). PawPrint runs none
of them. Today it runs such a method's IL as if it were the `Task`-returning method its metadata
signature names, and the IL's `ret` (of a `T`, or of nothing) fails the signature check; or it
reaches `AsyncHelpers.Await`'s `TailAwait()` call and refuses it. Four .NET 11 tests are parked
on this: `StringContentRoundTrip.cs`, the Linux-CoreLib `FileSystemWatcher` test, and the two
ASP.NET two-process tests.

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

### 9. How the thunk's IL names its callees

`IlStub` has no locals or exception regions, and an IL operand is a metadata token or a dynamic
method's scope. The thunk needs locals, a `try`/`catch` inside a `try`/`finally`, `FromResult<T>`
and `CreateRuntimeAsyncTask<T>` at the method's `T`, and the async variant of its own method,
which no token names. **Chosen:** a third operand case, a scope of host-resolved stub operands like
CoreCLR's `TokenLookupMap`, with the thunk's IL kept as data in `WoofWare.PawPrint.Semantics`.
Rejected: a thunk written as an F# step machine, which would transcribe exception handling, and
whose `Push(&stackState)` needs a real frame local anyway.

### 10. Stack traces

The thunk's frame, and the resume entry, are left out of stack traces, as IL stubs are on real
.NET.

### 11. An await in a `try` region before stage 3

Suspending in a frame whose await lies in a `try` region (a `using` counts) is refused until stage
3 gives continuations exception slots. Without one, the dispatcher would skip that frame's handlers
silently.

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
3. **(2a) The primitives suspension needs**, each testable without runtime async:
   `[FixedAddressValueType]` statics and `Unsafe.AsPointer` of them, `calli` through a function
   pointer read from native memory, and writes of a struct holding references through
   `GetRawData`, `Unsafe.Add` and `Unsafe.As<byte, T>` into an object's field and into
   `Task<T>.m_result`.
4. **(2b) Suspension and resumption.** The continuation type; `AsyncSuspend`; suspending callers;
   `TailAwait`; the suspended-frame map; `ResumeInfo` and `AsyncResume`; delivering a result;
   `DiagnosticIP`, with its QCall refused.
5. **(3) Exceptions across suspension.** Exception slots for awaits in `try` regions; the
   dispatcher's unwinding; the QCall that appends async frames. Probably un-parks the
   `FileSystemWatcher` and ASP.NET tests.
6. **(4) The await pattern.** Binding async variants at `await` sites and in async versions'
   tail-call patterns; async versions of plain `Task`-returning methods; the second vtable slot.
