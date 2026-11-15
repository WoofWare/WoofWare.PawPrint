# `ReflectionInvocation_CompileMethod` (issue #2268)

Status: research and options only; nothing is implemented. Measured 2026-11-15 on `origin/main` at `9206d939b`.

## What it is

### Who reaches it

`DynamicMethod.CreateDelegate` calls `RuntimeHelpers.CompileMethod` on the method before it binds the delegate, but only when `_restrictedSkipVisibility` is set (`DynamicMethod.CoreCLR.cs:47-56` on both runtimes; the comment at :51 reads "Compile the method since accessibility checks are done as part of compilation"). `CompileMethod` is the `LibraryImport` of the QCall (`RuntimeHelpers.CoreCLR.cs:196-197` on .NET 10, :197-198 on .NET 11), and `CreateDelegate` is its only caller in CoreLib on either runtime.

`_restrictedSkipVisibility` is set only for an ownerless ("anonymously hosted") method built with `restrictedSkipVisibility: true`: `Init` assigns `_restrictedSkipVisibility = skipVisibility` only on its `transparentMethod` path (`DynamicMethod.cs:253-257`), which the two constructors without an owner or module take (`DynamicMethod.cs:28-59`). The issue says "without an owner type or module". That is not quite the condition: an ownerless method built with `restrictedSkipVisibility: false` never reaches the QCall. `LambdaCompiler` sets the flag. Measured on both runtimes, the delegate `Expression.Lambda<Func<int>>(Expression.Constant(3)).Compile()` returns has a `DynamicMethod` as its `Method`, with `_restrictedSkipVisibility = True`, `_skipVisibility = False` and `_typeOwner = null`.

### What the QCall does

.NET 10, `vm/reflectioninvocation.cpp:1198-1210`:

```cpp
if (!pMD->IsPointingToPrestub())
    return;
BEGIN_QCALL;
pMD->DoPrestub(NULL);
END_QCALL;
```

.NET 11 (`reflectioninvocation.cpp:1259-1272`) is the same, except that it tests `ShouldCallPrestub()` instead of `IsPointingToPrestub()`. So the QCall compiles the method once. If the method has already been compiled, it does nothing. If a compilation failed, the method still points at the prestub, so the next call compiles it again.

`MethodDesc::DoPrestub` (`vm/prestub.cpp:2134` on .NET 10, :2355 on .NET 11) throws `InvalidOperationException` for a method with generic variables (:2150-2153). That never applies to a `DynamicMethod`. Otherwise a dynamic method is `IsNoMetadata`, so `DoPrestub` reaches `PrepareInitialCode` (:2271-2278 on .NET 10; :2492-2501 on .NET 11), which JIT-compiles the body. Everything compiling raises therefore comes out of `CreateDelegate`, past no handler in the dynamic method:

- `CORJIT_BADCODE` and `CORJIT_IMPLLIMITATION` become a bare `InvalidProgramException`, and `CORJIT_INTERNALERROR` one carrying `IDS_EE_JIT_COMPILER_ERROR` (`ThrowExceptionForJit`, `vm/jitinterface.cpp:12983-13005` on .NET 10, :13588 on .NET 11). Invalid IL takes this path: a stack underflow, or a branch outside the body or into the middle of an instruction.
- Token resolution: `ThrowBadTokenException` and the "PermitUninstDefOrRef" check (`jitinterface.cpp:958-962`) raise `InvalidProgramException`. A null `DynamicScope` entry under a type, method or field opcode also comes out as `InvalidProgramException`, measured below; `DynamicScopeOperand.fs`'s remarks cover the shapes PawPrint already models.
- The JIT's refusals of what a token names: `box` of a byref-like type raises `InvalidProgramException` (`NotSupported_ByRefLike`, :6133 on .NET 10, :6220 on .NET 11), `call` of an abstract method raises `BadImageFormatException`, and `newarr` of a byref-like element raises `TypeLoadException` (all measured below).
- Type loads the JIT makes (`JitTypeLoads.fs` lists the positions): `TypeLoadException`, for instance for a callee's return type that fails to load.
- Access checks. The QCall exists for them, but nothing raises one for these methods on CoreCLR. `ModifyCheckForDynamicMethod` (`jitinterface.cpp:150-185`) gives a restricted-skip-visibility method `kRestrictedMemberAccessNoTransparency`, and an ownerless method without the flag `kNormalAccessNoTransparency`. For both kinds, `AccessCheckOptions::DemandMemberAccess` returns `TRUE` whenever the target has a method table (`vm/clsload.cpp:3776-3783`; the enum is at `vm/clsload.hpp:335-360`). Measured below: private members of the guest's own type and of CoreLib are reachable with and without the flag, so no `MethodAccessException` or `FieldAccessException` arises.
- A `ldstr` naming a null scope entry does not fail compilation: the JIT embeds a null string, and the instruction's use of it throws `NullReferenceException` when it runs (measured).

Compiling also fixes what the JIT reads from the guest at its first compilation. It latches `initLocals` (`LCGMethodResolver::GetCodeInfo` under `if (!m_Code)`, which `DynamicMethodInitLocals.cs` describes), resolves every `catch` clause type and every scope token, and materialises every string literal. With the flag, that first compilation happens at `CreateDelegate`, not at the first call, and the guest can tell the difference, as measured below.

Compiling runs no type initialiser, not even that of a `beforefieldinit` type whose static field the body reads (measured below).

## Measurements

The probe is a C# console program with one case per body. Each case builds an ownerless `DynamicMethod` returning `int`, once with `restrictedSkipVisibility: true` ("R", which reaches the QCall) and once with `false` ("U", the control, which does not), and then:

1. calls `CreateDelegate`, and if that throws, calls it a second time;
2. runs an optional step after `CreateDelegate`: rewriting one scope slot through private reflection, with the slot named by searching `m_tokens` for the value it replaces, or setting `InitLocals`;
3. invokes the delegate twice.

The probe ran on the build box, built in Release and run framework-dependent on 10.0.14, then rolled forward to 11.0.0-rc.3.26522.148 (`DOTNET_ROLL_FORWARD=LatestMajor`, `DOTNET_ROLL_FORWARD_TO_PRERELEASE=1`). For PawPrint it was published `--self-contained` for osx-arm64 with `IsDynamicCodeSupported=true`, and each case ran under the Release App in its own process.

### Real .NET 10.0.14 and 11.0.0-rc.3

Every row is the same on both runtimes, except that the `newarr` message names CoreLib `11.0.0.0` on .NET 11. In the second-`CreateDelegate` column, "same" means it throws the same exception type again.

| case | body | R: `CreateDelegate` | R: 2nd `CreateDelegate` | R: invokes | U: `CreateDelegate` | U: invokes |
|---|---|---|---|---|---|---|
| `valid` | `ldc.i4 42; ret` | ok | – | 42, 42 | ok | 42, 42 |
| `throwsAtRun` | `newobj InvalidOperationException; throw` | ok | – | IOE `0x80131509`, twice | ok | IOE, twice |
| `stackUnderflow` | `pop; ldc.i4.1; ret` | `InvalidProgramException` `0x8013153A` "Common Language Runtime detected an invalid program." | same | – | ok | IPE, twice |
| `branchPastEnd` | `brtrue` to the offset after the last `ret` | IPE | same | – | ok | IPE, twice |
| `branchMidInstruction` | `br.s +1` into `ldc.i4`'s operand | IPE | same | – | ok | IPE, twice |
| `badMethodToken` | `call` scope index 0xFFF | IPE | same | – | ok | IPE, twice |
| `badFieldToken` | `ldsfld` scope index 0xFFF | IPE | same | – | ok | IPE, twice |
| `badTypeToken` | `castclass` scope index 0xFFF | IPE | same | – | ok | IPE, twice |
| `badStringToken` | `ldstr` scope index 0xFFF; `callvirt get_Length` | ok | – | `NullReferenceException` `0x80004003`, twice | ok | NRE, twice |
| `privateMethodApp` | `call` a private static method of the guest | ok | – | 11, 11 | ok | 11, 11 |
| `privateFieldApp` | `ldsfld` a private static field of the guest | ok | – | 13, 13 | ok | 13, 13 |
| `privateFieldCoreLib` | `ldfld String._stringLength` | ok | – | 3, 3 | ok | 3, 3 |
| `callAbstract` | `call Stream.Read(byte[], int, int)` | `BadImageFormatException` `0x8007000B` "Bad IL format." | same | – | ok | BIFE, twice |
| `newarrByRefLike` | `newarr Span<int>` | `TypeLoadException` `0x80131522` "Could not create array type 'System.Span`1[System.Int32][]' from assembly 'System.Private.CoreLib, …' because the element type is ByRef-like." | same | – | ok | TLE, twice |
| `boxByRefLike` | `ldloc` a `Span<int>`; `box`; `ldnull; cgt.un` | IPE | same | – | ok | IPE, twice |
| `calleeReturnFailsToLoad` | `call` a method returning an explicit-layout struct whose object field overlaps an `int` | TLE "Could not load type 'BadLayout' … because it contains an object field at offset 0 that is incorrectly aligned or overlapped by a non-object field." | same | – | ok | TLE, twice |
| `catchOpenGeneric` | `catch (List<>)` round a block that does not throw | IPE | same | – | ok | IPE, twice |
| `catchRewriteAfterCreate` | `try { throw new ArgumentException(); } catch (InvalidOperationException) { return 7; }`; after `CreateDelegate`, scope slot 3 (the clause's type) is rewritten to `ArgumentException` | ok | – | `ArgumentException` escapes, twice | ok | 7, 7 |
| `ldstrRewriteAfterCreate` | `ldstr "before"; callvirt get_Length`; after `CreateDelegate`, scope slot 2 is rewritten to `"after!!!"` | ok | – | 6, 6 | ok | 8, 8 |
| `initLocalsClearedAfterCreate` | `ldc.i4 4; localloc; ldind.i4`, with `InitLocals = false` set after `CreateDelegate` | ok | – | 0, 0 | ok | 0, 1 (unspecified bytes) |
| `staticField{Bfi,Precise}` | `ldsfld` of a `beforefieldinit` or precise type's field, whose initialiser logs | ok, no initialiser ran | – | 5, 5; the initialiser runs on the first invoke | ok | as R |

The stack trace of the failed `CreateDelegate` (both IPE cases, on both runtimes) begins at `System.Reflection.Emit.DynamicMethod.CreateDelegate(Type delegateType, Object target)`, followed by the caller. It has no `RuntimeHelpers.CompileMethod` frame and no frame of the dynamic method.

To build `calleeReturnFailsToLoad` without loading the struct in the probe itself, the probe adds `method.MethodHandle` to the scope with `DynamicScope.GetTokenFor` and emits the token raw. `ILGenerator.Emit(OpCode, MethodInfo)` reads the return type, so it loads `BadLayout` while the probe builds the body.

So on both runtimes, with the flag, every compile-time failure moves from the first invocation to `CreateDelegate`, and everything compilation reads from the guest is read at `CreateDelegate`. Without the flag, both happen at the first invocation. That is the whole difference.

### PawPrint today (`9206d939b`)

With the QCall unimplemented, every R case stops at `Unimplemented native method (PInvokeImpl QCall!ReflectionInvocation_CompileMethod)` (exit 134). Nothing before the QCall stops: building, minting (`ModuleHandle_GetDynamicMethod`) and `GetMethodDescriptor` all get through for every body. The U column is PawPrint's behaviour at first invocation today.

With a throwaway no-op handler (`NativeHandlerResult.completed`, since reverted), every R row equals PawPrint's U row, as the table below shows: the method is prepared at its first invocation, as an unrestricted one is.

| case | PawPrint U (= R with a no-op) | real .NET R | real .NET U |
|---|---|---|---|
| `valid`, `throwsAtRun`, `private*` (3 rows), `staticField*` (4 runs) | as real .NET | as PawPrint | as PawPrint |
| `stackUnderflow` | host stop: `stack shape: … invalid IL: StackUnderflow (0, Nullary Pop, 0)` | IPE at `CreateDelegate` | IPE at invoke |
| `branchPastEnd` | host stop: `… BranchOutsideBody (1, 8)` | IPE at `CreateDelegate` | IPE at invoke |
| `branchMidInstruction` | host stop: `… BranchOutsideBody (0, 3)` | IPE at `CreateDelegate` | IPE at invoke |
| `badMethodToken`, `badFieldToken`, `badTypeToken` | IPE at invoke, twice | IPE at `CreateDelegate` | as PawPrint |
| `badStringToken` | NRE at invoke, twice | as PawPrint | as PawPrint |
| `callAbstract` | BIFE at invoke, twice | BIFE at `CreateDelegate` | as PawPrint |
| `newarrByRefLike` | TLE at invoke, twice, with the default message "Failure has occurred while loading a type." | TLE at `CreateDelegate` | TLE at invoke, with CoreCLR's message |
| `boxByRefLike` | IPE at invoke, twice | IPE at `CreateDelegate` | as PawPrint |
| `calleeReturnFailsToLoad` | **runs, returns 0, twice** | TLE at `CreateDelegate` | TLE at invoke |
| `catchOpenGeneric` | IPE at invoke, twice (`DynamicMethodExecution.concretize`) | IPE at `CreateDelegate` | as PawPrint |
| `catchRewriteAfterCreate` | 7, 7 | `ArgumentException` escapes | as PawPrint |
| `ldstrRewriteAfterCreate` | 8, 8 | 6, 6 | as PawPrint |
| `initLocalsClearedAfterCreate` | host stop: `MemoryBlock.readBytes: byte at offset 0 in <stack memory block #0> is uninitialised` | 0, 0 | 0, then unspecified bytes |

Two of these rows are gaps of their own, which every option below leaves as they are:

- `calleeReturnFailsToLoad`: PawPrint neither refuses the overlapping explicit layout nor loads a callee's return type as the JIT does, so it runs a method that CoreCLR refuses to compile. That happens without any dynamic method, and is worth an issue of its own.
- `newarrByRefLike`: the exception type is right, but the message is the parameterless constructor's.

### What PawPrint has for compiling ahead of time

The issue assumes that PawPrint already imports a method's whole body when the method is entered. On `main` it does not. Of `docs/plans/2026-10-24-jit-import-at-method-entry.md`, these stages have landed:

- Stage 1, the two bounds on what the JIT compiles and the order it imports it in (`Semantics/JitImport.fs`, #2011);
- Stage 2b, which makes the resolvers return results instead of calling `failwith` (#2017, #2024, #2037, #2043, #2048, …);
- the one binder for `call`, `ldftn` and `constrained.` (#2112, `MethodTokenBinding.fs`);
- the type-system refusals at concretisation (#2054, #2078, #2098, #2111);
- `Semantics/JitTypeLoads.fs`.

Stage 3, the compile prologue, has not landed. `JitImport` and `JitTypeLoads` are consumed only by `WoofWare.PawPrint.Analysis` and, in the interpreter, by `CatchClauseRefusal` (a host stop at frame creation, `MethodState.Empty`, `MethodState.fs:770`). Every token is still bound when its instruction runs. So there is no import that could be run "without entering the method", and Stage 3's "Where it runs" puts it in a frame prologue.

What the interpreter does have for a dynamic method's first compilation:

1. **`DynamicMethodExecution.concretize`** (`DynamicMethodExecution.fs:61`). Before a dynamic method's first frame, it latches `initLocals` and every `catch` clause's type into the method-handle registry (`MethodHandleRegistry.latchPreparation`). If a clause type is unusable, it returns `Error (exceptionType, diagnostic)` and latches nothing, so the next attempt prepares the method again. That is CoreCLR's retry, which those remarks record as measured. Its two callers, the `call` of a scope method (`UnaryMetadataCallOps.fs:812`) and delegate invocation (`AbstractMachine.fs:296`), raise the `Error` as `RaiseOrigin.Callee`, which puts it in the caller as CoreCLR does. A latched method is never prepared again, which matches the QCall's `IsPointingToPrestub` check. The remarks at `DynamicMethodExecution.fs:30-37` already say that any other route to a first JIT (they name `RuntimeHelpers.PrepareMethod`) "must latch here too". `CompileMethod` is such a route.
2. **`StackShapeOfMethod`**. A pure whole-body analysis, which for a dynamic method reads every scope operand ahead of time (`analyseDynamic`). It is computed when the method's first instruction runs and memoised in `state.StackShapes`. When execution reaches an offset the analysis found invalid (an underflow, a branch outside the body), the run stops on the host with a `failwith`; nothing raises it in the guest (`StackShapeOfMethod.fs:629-655`).
3. **`DynamicScopeOperand.tryMethod` / `tryField` / `closedType`**. Each reads one scope entry from the heap without running guest code, and returns `ScopeEntryRefusal.GuestException (type, why)` or `Unsupported`. One shape needs guest code: a scope entry naming an unminted `DynamicMethod` (`ScopeMethodResolution.NeedsMinting`) has to be minted, which is a managed call.

Every other compile failure in the table (an abstract call, a byref-like `box` or `newarr`, a bad scope token) is raised by its instruction when it runs. These are the timing divergences `docs/divergences.md` already records for metadata methods ("… fails when it is reached", lines 826, 916, 1001, 1057), applied to a dynamic method's body.

## Options

### A. A no-op

The handler returns at once. This is the throwaway from the issue, and Moq's `Setup` and `Verify` pass with it.

How it diverges, measured: a restricted method is prepared at its first invocation, as an unrestricted one is. That shows in three ways.
- A guest can rewrite a `catch` clause's scope slot between `CreateDelegate` and the first call and be heard (`catchRewriteAfterCreate`: 7 where real .NET lets `ArgumentException` escape).
- Clearing `InitLocals` after `CreateDelegate` takes effect: PawPrint stops on the uninitialised read where real .NET returns 0.
- Every compile failure arrives at the first call instead of at `CreateDelegate`. A guest that wraps `CreateDelegate` in a `try`, as `LambdaCompiler`'s callers may, sees the failure somewhere else.

- **Blast radius**: one line in the QCall table.
- **Reversibility**: total.
- **Information kept**: none. The run that results claims the method was compiled when nothing was, which makes it a wrong answer rather than a stop.

### B. Prepare at `CompileMethod`: what PawPrint already latches at first execution, latched at `CreateDelegate`

The handler resolves its `RuntimeMethodHandleInternal` argument to the registry's `DynamicMethodHandle`. If the method's preparation is already latched, it does nothing, which matches `IsPointingToPrestub`. Otherwise it runs the preparation half of `DynamicMethodExecution.concretize` and returns:

- on `Ok`, nothing: the latch is now in the registry, and the first invocation finds it;
- on `Error (exceptionType, why)`, `NativeHandlerResult.RaiseException` with the same message that `UnaryMetadataCallOps.fs`'s call arm passes (`DynamicScopeOperand.clrMessageFor … BadTokenKind.Class`). The method stays unprepared, so a second `CreateDelegate` raises again, as measured.

The handler only raises a guest exception or latches. Any other argument is a PawPrint bug, and the handler stops the run with a `failwith` naming it: `EmptyHandle` cannot arrive, because `GetMethodDescriptor` runs first and guarantees a handle, and a method handle that is not dynamic cannot arrive either.

That probably means splitting `DynamicMethodExecution.concretize` into `prepare` (the latch) and the concretisation, so that the QCall does not concretise a frame's worth of method it will not push. One of the questions below asks whether it should concretise.

Measured effect, predicted from the tables (the noop runs pin every other row): `catchRewriteAfterCreate`, `initLocalsClearedAfterCreate` and `catchOpenGeneric` agree with real .NET, and the rest are as with A. The divergence that remains is that an instruction-level compile failure in a restricted method's body is raised at its first invocation, or stops the run there for invalid IL, and not at `CreateDelegate`. A `ldstr` or another scope entry rewritten after `CreateDelegate` is still heard. That would be one new entry in `docs/divergences.md`, alongside the four "fails when it is reached" entries, and it would shrink as JIT-import Stages 3-8 land.

- **Blast radius**: one QCall handler, plus a split of `DynamicMethodExecution.concretize`. The registry's latch is unchanged, and so are its two other callers.
- **Reversibility**: high. The handler is self-contained.
- **Information kept**: the guest sees the latch at the right time, and the failures PawPrint can model at preparation are raised at the right place. The rest keep today's timing, and the divergence entry says so.

### C. Compile at `CompileMethod`: B, plus an import of the whole body ahead of time

As B, and then also read the whole body now and raise what compiling it would raise:

- StackShape's invalid offsets that lie in surely-compiled code (`JitImport.compiledCode`) become `InvalidProgramException`. Branch targets are checked over the whole body, since `fgFindJumpTargets` scans all of it.
- Every scope operand in maybe-compiled code is read with `DynamicScopeOperand.try*`, and a refusal becomes its `GuestException`.
- An abstract `call`, and a byref-like `box` or `newarr`.
- Where several sites fail, the 2026-10-24 plan's outcome rule picks the one that raises, from precedence, `JitImport.importOrder`; otherwise the run stops.

The bindings are latched as well, so that instructions read them instead of binding again. Without that, one token would be bound twice, at compile and at execution, which the plan forbids ("no family is bound in two places"). This is that plan's Stages 3 and 8, restricted to dynamic methods. A scope entry naming an unminted `DynamicMethod` needs a managed call in the middle of a QCall, which Stage 8 left to be decided.

- **Blast radius**: large. It is a second implementation of the compile prologue, which Stage 3 would then have to merge with or replace.
- **Reversibility**: low once instructions read the latched bindings.
- **Information kept**: the most. On this probe, every row except `calleeReturnFailsToLoad` (a separate gap) and the `newarr` message would agree with real .NET.

The structurally better form of C is to build Stage 3 with its compile step as a function of a method that pushes no frame, which the prologue calls and so does this QCall. Then `CompileMethod` costs one call once Stage 3 exists. That is a reason to do B now and to record in the 2026-10-24 plan that Stage 3's compile step must be callable without a frame.

### D. Prepare, and refuse what PawPrint would raise late

As B, and then read the body ahead with what already exists: StackShape's whole-body analysis, which reads every scope operand of a dynamic method, and the shapes `JitImport` bounds. Where the read finds something CoreCLR would refuse to compile, the run stops at `CreateDelegate` with a diagnostic naming the site and the exception CoreCLR raises; nothing is raised in the guest.

This trades availability for never answering with the wrong timing. Valid bodies, which include everything `LambdaCompiler` and Moq emit, are unaffected. An invalid body, which today raises its exception in the guest at the first invocation, would stop the run instead. Under B it would raise in the guest, at the wrong time.

Two risks:
- A false refusal of a valid body, where a refusal sits on a branch the JIT folds away. The read would have to keep to surely-compiled code, or refuse only when unsure, and pick between those.
- It makes restricted dynamic methods stricter than metadata methods, which today keep the documented "fails when it is reached" timing.

- **Blast radius**: medium. One new read over a dynamic body, on the same inputs C would use, but with no binder and no latch.
- **Reversibility**: high. Each refusal becomes a raise as C or Stage 3 lands.
- **Information kept**: full, because the stop names exactly what diverges, and no wrong answer is produced.

### Comparison

| | A no-op | B prepare | C compile | D prepare + refuse |
|---|---|---|---|---|
| Moq `Setup`/`Verify`, `Expression.Compile` | pass | pass | pass | pass |
| latch at `CreateDelegate` (initLocals, catch types) | no | yes | yes | yes |
| instruction-level compile failures | at invoke | at invoke (documented) | at `CreateDelegate` | stop at `CreateDelegate` |
| scope rewrites after `CreateDelegate` (beyond `catch`) | heard | heard (documented) | not heard | heard (documented) |
| new code | none | one handler, one split | a second compile prologue | a read-ahead check |
| consistent with metadata methods' timing today | – | yes | ahead of them | stricter than them |

Recommendation: B. It is the smallest change that makes everything PawPrint already models at a dynamic method's first compilation happen when CoreCLR compiles it. What remains is the same timing trade PawPrint already documents for every metadata method, and Stage 3 will retire it for both. C is Stage 3, done in the wrong place. D is a possible tightening if the decider wants restricted dynamic methods held to a higher bar than metadata methods.

## Oracle

Every expectation below was measured on real .NET 10.0.14 and 11.0.0-rc.3 by the probe above. These guests would be new `sourcesImpure` cases registered in `TestImpureCases.fs`, beside `DynamicMethodInvalidCatchClause.cs` and `DynamicMethodInitLocals.cs`. They need `IsDynamicCodeSupported=true`, which a case's `AppContext` sets in `TestImpureCases.fs`, as `DynamicCodeSupportedOverride.cs`'s registration does. Each returns 0 on success, or the number of the first check that failed. Each check pairs the restricted method with the unrestricted control, so that the test fails if the QCall is never reached.

1. **`DynamicMethodCompiledAtCreateDelegate.cs`** (passes under B, C and D; fails under A):
   - `catchRewriteAfterCreate`: R lets `ArgumentException` escape on both invokes; U returns 7.
   - `initLocalsClearedAfterCreate`: R returns 0 on both invokes. The U control must not read the unwritten bytes, so it checks only that `CreateDelegate` succeeds.
   - `catchOpenGeneric`: R's `CreateDelegate` raises `InvalidProgramException` with HResult `0x8013153A`, and a second `CreateDelegate` raises it again; U's `CreateDelegate` succeeds and its invoke raises it.
   - `valid`, `throwsAtRun`: `CreateDelegate` succeeds and the invokes behave as on real .NET.
   - `staticField{Bfi,Precise}`: no type initialiser has run when R's `CreateDelegate` returns, and it runs on the first invoke.
   - Optionally, the trace: the exception's first frame is `DynamicMethod.CreateDelegate`. Whether PawPrint reproduces this depends on whether a `RaiseException` from a QCall handler names the `RuntimeHelpers.CompileMethod` frame, which has not been measured. Leave it out unless the decider wants it pinned.
2. **`DynamicMethodCompileFailureTiming.cs`** (passes only under C). It holds the same checks over `stackUnderflow`, `branchPastEnd`, `branchMidInstruction`, `bad{Method,Field,Type}Token`, `callAbstract`, `newarrByRefLike`, `boxByRefLike` and `ldstrRewriteAfterCreate`: each raises at R's `CreateDelegate` with the measured type and HResult, and at U's first invoke. Under B it is parked on every runtime, with the reason naming the new divergence entry and the ending PawPrint reaches (the first wrong check's number, or the stack-shape stop for invalid IL). Under D it is parked with the refusal as its ending.
3. **A unit test in the style of `TestDynamicMethodMintOutcome`** (B, C, D). Run a minimal restricted `CreateDelegate` without invoking it, and assert that the registry's preparation for that handle is `Some` once the QCall returns. For the unrestricted control it stays `None`. This pins where the latch happens without depending on a guest-visible effect.
4. **Mutation checks**:
   - Replacing the handler with A's no-op turns guest 1's first two checks red.
   - Latching on the `Error` path turns the second-`CreateDelegate` check red.
   - Skipping the `already latched` test, so the method is prepared twice, turns `catchRewriteAfterCreate` red if the rewrite is moved before a second `CreateDelegate`. Guest 1 should include that variant: rewrite the slot after the first `CreateDelegate`, call `CreateDelegate` again, and expect the rewrite still unheard.
5. **Both suites, run separately**, with the four Guest categories. `ExpressionLambdaCompile*.cs` run the expression interpreter by default (dynamic code unsupported), so they do not reach the QCall. The `docs/divergences.md` row naming them as regressing on `CompileMethod` when the switch is on needs re-measuring after the change, and updating or deleting.

## Questions for the decider

1. **Which option?** A, B, C or D. The recommendation is B, with a note added to the 2026-10-24 plan that Stage 3's compile step must be callable without pushing a frame, so that `CompileMethod` can call it later.
2. **Under B, should the QCall also concretise the method?** That means loading its signature and local types, as `ExecutionConcretization.concretizeDynamicMethod` does, and as CoreCLR's JIT does when it compiles (`JitTypePosition.Local`, `OwnParameter`, `OwnReturn`). Doing it moves a failure to load a local's or parameter's type to `CreateDelegate`. But concretisation's failures are host `failwith`s today, not results, so a body with a bad local would stop the run at `CreateDelegate` instead of at the first invoke. That stop is harmless, but it is a second change of timing.
3. **Under B, should `ldstr` be latched at preparation too?** That would fix `ldstrRewriteAfterCreate`, and with it the existing "mutation between two invocations" divergence that `DynamicScopeOperand.fs` and `UnaryStringTokenIlOp` describe. It is part of JIT-import Stage 8, and its absence is already documented. The suggestion is to leave it to Stage 8.
4. **The divergence entry under B**: a new entry ("a restricted-skip-visibility `DynamicMethod`'s body is compiled at `CreateDelegate` only as far as its `catch` clauses and `initLocals`"), or a sentence added to each of the four "fails when it is reached" entries?
5. **Should guest 1 pin the exception's stack trace** (first frame `DynamicMethod.CreateDelegate`, no `RuntimeHelpers.CompileMethod`)? That needs a measurement of what PawPrint's trace shows for a QCall's `RaiseException`, and perhaps work on it.
6. **The two separate gaps** this probe found: a callee whose return type fails to load (an overlapping explicit layout) runs under PawPrint, and the `newarr` of a byref-like element gives the default message. File them as issues of their own?
