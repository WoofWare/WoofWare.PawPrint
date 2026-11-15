# `ReflectionInvocation_CompileMethod` (issue #2268)

Status: implemented as option B (see "As built" at the end). The study was measured 2026-11-15 on `origin/main` at `9206d939b`.

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

### Decisions

Decided 2026-11-15, against `0d02a1738`. I checked the plan's claims against the source and the logs, and ran a throwaway version of option B under PawPrint. The build box was unreachable (ssh timed out), so that run was local, after 17:00 GMT. Nothing in any repository worktree was edited; the scratch worktree is removed.

The throwaway handler (20 lines) called `DynamicMethodExecution.concretize` from the QCall. The results, for nine of the probe's cases:

| case | restricted, PawPrint with B | real .NET R |
|---|---|---|
| `catchOpenGeneric` | IPE `0x8013153A` at `CreateDelegate`, and again at the second | same |
| `catchRewriteAfterCreate` | `ArgumentException` escapes, twice | same |
| `initLocalsClearedAfterCreate` | 0, 0 | same |
| `valid`, `staticFieldBfiR` | 42, 42; initialiser runs at first invoke | same |
| `ldstrRewriteAfterCreate` | 8, 8 | 6, 6 |
| `badTypeToken` | IPE at invoke | IPE at `CreateDelegate` |
| `stackUnderflow` | host stop at invoke | IPE at `CreateDelegate` |

`staticFieldPreciseR` was built but not re-run. The unrestricted controls were unchanged from the plan's table.

**1. Option B.**

- B is chosen: the QCall prepares the method (latches `initLocals` and the `catch` clause types) and raises a preparation failure in the guest.
- Preparation needs no frame of the dynamic method. `concretize` takes a handle and a state and no thread, and reads only the registry and the heap (`DynamicMethodExecution.fs:61-181`, `DynamicScopeOperand.entryObject`). The throwaway run confirms it.
- Preparation raises three guest exceptions, all from a `catch` clause's scope entry.
  - `InvalidProgramException`: a null slot, a type that is not closed, or a byref or `System.Void` (`TypeOperand.refusal`).
  - `BadImageFormatException` "Bad class token.": an entry that is not a `RuntimeTypeHandle`, or one whose `m_type` is null.
  - `ArgumentOutOfRangeException`: an index exactly at the list's length.
- Its host failures are all broken interpreter invariants, none reachable by a guest: an unregistered handle, a missing resolver, a scope entry off the heap, a malformed `m_type`, `_size` or `_initLocals`.
- A is rejected: it answers wrongly on three measured rows where B answers rightly.
- C is rejected: it is Stage 3 and Stage 8 of the 2026-10-24 plan built in a QCall handler, and it would latch bindings that instructions do not yet read.
- D is rejected, for three reasons.
  - It needs to know what the JIT surely compiles in a dynamic body, or it refuses valid bodies that have a dead arm. That is Stage 3's compile step without the latch.
  - The cheap form, running `analyseDynamic` at the QCall, memoises a stack shape at `CreateDelegate` while instructions still read the live scope. A rewrite between the two would leave them disagreeing.
  - It would hold restricted dynamic methods to a stricter rule than metadata methods, which keep the documented "fails when it is reached" timing.
- Split `DynamicMethodExecution.concretize` into `prepare` (returns the `PreparedDynamicMethod` or the failure, and latches) and `concretize` (calls `prepare`, then `concretizeDynamicMethod`). `prepare` keeps the "already latched" test inside it.
- The handler calls `prepare` unconditionally. It has no "already prepared" test of its own; `prepare` being idempotent is the `IsPointingToPrestub` check.
- The handler logs the diagnostic at Warning, as the two existing callers do. The throwaway did not, and the refusal was then silent in the log.
- The fields of the raise (`clrMessageFor … BadTokenKind.Class`) are written twice today (`UnaryMetadataCallOps.fs:834`, `AbstractMachine.fs:415`). Put them in one function in `DynamicMethodExecution` and have all three callers use it.
- A null handle or a handle that is not dynamic is a `failwith` naming the argument. `CreateDelegate` is the only caller in CoreLib on both runtimes, and `GetMethodDescriptor` runs first.
- The handler goes in `NativeRuntimeHelpers.tryExecuteQCall`, matched on `RuntimeHelpers.CompileMethod(RuntimeMethodHandleInternal) : void`, and is registered in `NativeQCall.handlers`. `DynamicMethodExecution.fs` compiles before it.
- Update the remark at `DynamicMethodExecution.fs:30-37`: `CompileMethod` is now a route to a first JIT that latches, and `PrepareMethod` is still not.
- Add a note to the 2026-10-24 plan's "Where it runs" with three points.
  - The compile step must be a function of a method and a state that pushes no frame, so that the prologue and this QCall both call it.
  - At this QCall the trace has no frame of the dynamic method (measured, `real-trace.log`), where the prologue's has one.
  - A dynamic method's failed compilation is not memoised: a second `CreateDelegate` compiles again.

**2. The QCall does not concretise.**

- The signature is already concretised during `CreateDelegate`: `Delegate_BindToMethodInfo` does it for a dynamic target (`NativeDelegate.fs:1279-1283`), straight after `CompileMethod` returns. Moving it gains nothing.
- Locals are not concretised by `concretizeDynamicMethod` at all. `ExecutionConcretization.concretizeLocals` does it when the frame is created (`IlMachineStateExecution.fs:1433`), and stops the run where a local does not bind.
- Moving that stop to `CreateDelegate` would only move a host stop. It stays where it is until it can be a raise, which is the JIT-import plan's work.

**3. `ldstr` is not latched at preparation.**

- Latching one operand family leaves the scope half latched: strings read at `CreateDelegate`, every other entry read at execution. That is harder to state than "instruction operands are read when they execute".
- Stage 8 latches the whole scope in one place. The measured row (8, 8 against 6, 6) goes in the divergence entry.

**4. One new paragraph, and no edits to the "fails when it is reached" entries.**

- Add a fourth paragraph to the `DynamicMethod` list in `docs/divergences.md` (beside "When a scope entry is read"), titled for what a restricted-skip-visibility method's `CreateDelegate` compiles.
- It says: PawPrint prepares the `catch` clause types and `initLocals` there. Everything else the JIT would refuse is still raised, or stops the run, at the first invocation. An instruction's scope operand rewritten after `CreateDelegate` is still heard.
- It points at the "fails when it is reached" entries by name and does not repeat them. There are six, not four (lines 826, 862, 916, 961, 1001, 1057).
- It says plainly that invalid IL (`BADCODE`) is not retired by any stage of the 2026-10-24 plan, whose "Not in scope" excludes it. Only the token failures shrink as Stages 3 to 8 land.
- Amend "When a scope entry is read" in the same list: for a restricted method, real .NET stops hearing rewrites at `CreateDelegate`, not at the first invocation.
- Re-measure the regression table's `ReflectionInvocation_CompileMethod` row (line 556) with the switch on, and update or delete it. Remove the sentence "`Expression.Compile()` stops before the code it compiles runs" if it is no longer true.

**5. The guest pins what both runtimes agree on about the trace, and not the first frame.**

- Measured with the throwaway: PawPrint's trace is `RuntimeHelpers.CompileMethod(RuntimeMethodHandleInternal)`, `DynamicMethod.CreateDelegate(Type, Object)`, `DynamicMethod.CreateDelegate(Type)`, then the caller.
- Real .NET's is `DynamicMethod.CreateDelegate(Type, Object)`, then the caller.
- Both extra frames are calls that optimised CoreLib leaves without a frame. That is the existing entry "A trace has a frame for every call that optimised code inlines or tail-calls". Add this pair to that entry as a second observable example.
- The guest asserts two things: the trace contains `DynamicMethod.CreateDelegate`, and it contains no frame naming the dynamic method. Both hold on real .NET and under B.
- The guest does not read `TargetSite`: PawPrint refuses to name the head of a runtime-raised trace.
- No work on the trace in this PR.

**6. File three issues, not two.**

- An explicit-layout struct whose object field overlaps a non-object field loads and runs under PawPrint, where CoreCLR raises `TypeLoadException`. This is a wrong answer with no dynamic method involved, and is the most serious of the three.
- The JIT loads a callee's return type when it compiles the caller, and PawPrint does not. Mention it in the first issue as how the probe found it; `JitTypeLoads.fs` already lists the position.
- `newarr` of a byref-like element raises `TypeLoadException` with the parameterless constructor's message, where CoreCLR names the array type.
- Invalid IL is never an `InvalidProgramException`: it is a host stop when the offset executes, for a metadata method and a dynamic one. For a restricted dynamic method, `CreateDelegate` returns where CoreCLR raises. No plan covers this.
- I searched the open issues for the first two and for `PrepareMethod` and found nothing.

**Tests**

- Write the guests and the unit test first, in a commit of their own, and record the failing output (`Unimplemented native method … ReflectionInvocation_CompileMethod`) before the handler exists.
- Guest 1, `sourcesImpure/DynamicMethodCompiledAtCreateDelegate.cs`, with `IsDynamicCodeSupported=true`. It holds the plan's checks, each paired with the unrestricted control, plus these:
  - the double-`CreateDelegate` variant: rewrite the clause slot after the first `CreateDelegate`, call `CreateDelegate` again, and the rewrite is still unheard;
  - repair after failure: `catchOpenGeneric` raises at `CreateDelegate`, the guest rewrites the slot to a legal type, and the next `CreateDelegate` succeeds and the delegate runs. This is not yet measured on real .NET for the restricted method. The real-runtime oracle decides it; if real .NET disagrees, stop and report;
  - `InitLocals = false` set before `CreateDelegate` is heard by neither a later `InitLocals = true` nor anything else: the latch takes the value at `CreateDelegate`. Use a body that does not read unwritten bytes when the flag is false, or leave this check out;
  - the two trace assertions of decision 5;
  - the HResult `0x8013153A` on the `CreateDelegate` failure.
- Guest 2, `sourcesImpure/DynamicMethodCompileFailureTiming.cs`, parked everywhere. It holds the token cases only: `bad{Method,Field,Type}Token`, `callAbstract`, `newarrByRefLike`, `boxByRefLike`, `ldstrRewriteAfterCreate`. Order them by the stage that retires each (Stage 3, 4, 5, 6, then 8), so the parked ending moves forward one check at a time. The reason names the new divergence paragraph.
- Guest 3, `sourcesImpure/DynamicMethodInvalidIlAtCreateDelegate.cs`, parked everywhere, holding `stackUnderflow`, `branchPastEnd` and `branchMidInstruction`. It is separate because its ending is a host stop and no stage retires it. The reason names the invalid-IL issue.
- The unit test, in the style of `TestDynamicMethodMintOutcome`: after a restricted `CreateDelegate` with no invoke, the registry's preparation for the handle is `Some`; for the unrestricted control it is `None`.
- An `Expression.Lambda<Func<int>>(Expression.Constant(3)).Compile()` guest with the switch on, returning the delegate's result. It is the issue's motivating case, and neither it nor Moq was run under B. If it stops on something else, park it with that stop and do not fix it in this PR.
- Run the default suite and the four Guest categories as separate invocations.

**Mutants**

- The handler returns without preparing (option A): guest 1's `catchRewriteAfterCreate`, `initLocalsClearedAfterCreate` and `catchOpenGeneric` checks go red, and so does the unit test.
- The handler swallows the `Error` and completes: the `catchOpenGeneric` check goes red.
- `prepare` latches on the `Error` path: the repair check goes red.
- `prepare` skips its "already latched" test: the double-`CreateDelegate` check goes red. `DynamicMethodInitLocals.cs` should too; confirm it.
- The handler raises with the wrong fields: add a `catch` clause slot holding a non-`RuntimeTypeHandle` object to guest 1 and assert "Bad class token.", so that this mutant has a killer. Measure it on real .NET first.

**Parked or left alone**

- `calleeReturnFailsToLoad` and the `newarr` message: not in any guest of this PR; the issues carry them.
- `ExpressionLambdaCompile*.cs` keep running the expression interpreter by default. Do not change their registration.
- Access checks: nothing to do. Measured, nothing raises one for these methods.

### Where the decisions contradict the plan

- The plan says the remaining divergence "would shrink as JIT-import Stages 3-8 land". That holds for token failures only. The 2026-10-24 plan's "Not in scope" excludes invalid IL, so the three invalid-IL rows stay as they are.
- The plan counts four "fails when it is reached" entries. `docs/divergences.md` has six.
- Question 2 assumes `concretizeDynamicMethod` loads the local types. It concretises the signature only; locals are concretised at frame creation. The signature is already concretised during `CreateDelegate`, at the bind.
- The plan has the handler test "already latched" before preparing. `concretize` already makes that test, so a second one in the handler would be a duplicate.
- The plan leaves PawPrint's trace for a QCall raise unmeasured. It is measured now: two frames more than real .NET's, `RuntimeHelpers.CompileMethod` and `CreateDelegate(Type)`.
- The 2026-10-24 plan memoises a compilation's outcome, failures included, and establishes the frame before compiling. Neither fits this QCall: a dynamic method's failed compilation is retried, and the trace has no frame of the method.
- The plan's B section says its effect is "predicted from the tables". It is now measured, and the prediction was right on every row I ran.

### As built

Built 2026-11-15 on `origin/main` at `6ab868184`, as the decisions say: option B.

- `DynamicMethodExecution.prepare` is the latching half of the old `concretize`, with the "already latched" test inside it; `concretize` calls it and then `ExecutionConcretization.concretizeDynamicMethod`.
- `DynamicMethodExecution.preparationFailureFields` holds the fields of the raise. The QCall, a `call` of a dynamic method and delegate invocation all use it; `raiseFromPoppedStub` in `AbstractMachine.fs` takes fields instead of a message so the third can.
- The handler is in `NativeRuntimeHelpers.tryExecuteQCall`, registered in `NativeQCall.handlers`. It calls `prepare` unconditionally, logs a refusal at Warning, and raises it with `NativeHandlerResult.raiseExceptionWithFields`. A handle naming a metadata method stops the run; a null handle stops it in `MethodHandleResolution.resolveMethodHandleFromArg`.
- `MethodHandleRegistry.dynamicMethods` is new, so that the unit test (`TestCompileMethodPreparation`) can find the guest's two methods by name.

#### Measurements

Every guest ran on real .NET 10.0.14 and on 11.0.0-rc.3.26522.148 (rolled forward), built in Release from the guest file itself:

| guest | real .NET 10 | real .NET 11 | controls (each the same on both) | PawPrint, host CoreLib | PawPrint, `Net11` |
|---|---|---|---|---|---|
| `DynamicMethodCompiledAtCreateDelegate.cs` | 0 | 0 | repair without the rewrite: 13; "Bad method token." expected: 15; double-`CreateDelegate` rewrite dropped: 7; third `CreateDelegate` expected to raise: 7 | 0 | 0 |
| `DynamicMethodCompileFailureTiming.cs` | 0 | 0 | restricted literal expected 8: 7 | 1 (parked) | 1 (parked) |
| `DynamicMethodInvalidIlAtCreateDelegate.cs` | 0 | 0 | another HResult expected: 1 | stack-shape stop at the control (parked) | the same (parked) |
| `ExpressionLambdaCompileEmitted.cs` | 3 | 3 | lambda returning 4: 4 | 3 | 3 |

The repair check, which the decisions left to the oracle, agrees with the decision on both runtimes: after a failed `CreateDelegate` and a repair of the clause's slot, the next `CreateDelegate` compiles the method and the repaired clause catches.

For guest 2, a temporary variant that recorded each half of each check, rather than stopping at the first, returned every unrestricted control right and every restricted half wrong (mask `10922`, bits 1, 3, …, 13), so its exit code will move forward one check at a time as the stages land.

With `runtimeBaseline` emptied (the switch on), `ExpressionLambdaCompile.cs` and `ExpressionLambdaCompileWide.cs` pass on both runtimes, where the regression table had them stopping at this QCall.

#### Mutants

Each was applied alone (the last row is two edits), built, and run against guest 1 (with `DynamicMethodInitLocals.cs`, `DynamicMethodInvalidCatchClause.cs` and the `Expression.Compile()` guest), and separately against `TestCompileMethodPreparation` and `TestNativeGetDynamicMethod`.

| mutant | killed by |
|---|---|
| The handler completes without preparing (option A) | guest 1 exits 6 (`catchRewriteAfterCreate`); with checks 6 and 7 disabled, check 8 (`InitLocals` cleared after `CreateDelegate`) stops on the uninitialised read; the unit test (preparation `None`) |
| The handler swallows the `Error` and completes | guest 1 exits 10 (`catchOpenGeneric` at `CreateDelegate`) |
| `prepare` latches on the `Error` path | guest 1 exits 9 (the control's second invocation runs the latched method); `TestNativeGetDynamicMethod`'s "a failed preparation latches nothing" |
| `prepare` skips its "already latched" test | guest 1 exits 7, at the third `CreateDelegate`; survives the check as the decisions wrote it (see below) |
| `prepare` skips its test and `latchPreparation` overwrites | guest 1 exits 6; `DynamicMethodInitLocals.cs` stops on the uninitialised read; `TestNativeGetDynamicMethod`'s "initLocals is latched by the first execution" |
| The handler raises with no fields | guest 1 exits 15 ("Bad class token." at `CreateDelegate`) |
| `preparationFailureFields` returns no fields | guest 1 exits 14 (the control's "Bad class token." at invocation) |

#### Suites

Run at `0846dbc8f` after building `WoofWare.PawPrint.slnx` with the reference assemblies cleared, each as its own invocation, on the host CoreLib (.NET 10):

| selection | result |
|---|---|
| default (`dotnet test`) | 5758 passed, 37 skipped, of 5795 |
| `TestCategory=Guest` | 234 passed of 234 |
| `TestCategory=GuestSolo` | 2 passed of 2 |
| `TestCategory=GuestPure` | 1195 passed of 1195 |
| `TestCategory=GuestImpure` | 298 passed of 298 |

The new guests, the unit test and the two parked guests were also run with `PAWPRINT_TEST_RUNTIME=Net11`, with the results in the measurements table.

#### Where the decisions were wrong or incomplete

- **The double-`CreateDelegate` check does not kill the mutant it was meant for.** `MethodHandleRegistry.latchPreparation` is itself idempotent: it returns the first latch whatever it is handed. So a `prepare` that skips its own test reads the rewritten clause, gets `Ok`, and is handed back the old latch, and the decided check passes (measured: the mutant survives guest 1 without the addition). The only observable difference is a re-read that *fails*. Guest 1's check 7 therefore also rewrites the slot to an open generic type after the second `CreateDelegate` and expects a third to succeed and the delegate to behave as compiled, which real .NET does on both runtimes (control: 7). That kills it. `DynamicMethodInitLocals.cs` does not, for the same reason; it goes red only when `latchPreparation` overwrites too.
- **The `InitLocals = false` before `CreateDelegate` check is left out**, as the decisions allowed. With the flag latched false, the only observable is a read of unwritten bytes, which PawPrint refuses and real .NET answers with unspecified ones. Check 8 (cleared after `CreateDelegate`, latched true) and the unit test (which asserts `LocalsInit = true` after the guest clears it) pin the latch's timing instead.
- **The mutants predicted to turn several checks red** are seen at the first red check, since the guest stops there. For option A, check 10 is hidden behind check 8's host stop; the swallowing mutant shows that check 10 kills a `CompileMethod` that raises nothing.
- **"Prepare latches on the Error path" is killed first by check 9, not the repair check**: the unrestricted control's second invocation finds the latch and runs the method.
- Guest 2's order: the decisions list the bad scope tokens under Stages 3, 5 and 6 by their opcode. They are `DynamicScope` operands, which Stage 8 binds; whichever stage binds them ahead of their instruction retires them. The guest's header says so.
- Guest 3's ending is the host stop the decisions expected because each check exercises the unrestricted control first; with the restricted method first it would exit 1 instead.
- The "`Expression.Compile()` stops before the code it compiles runs" sentence in `docs/divergences.md` is gone, as is `Expression.Compile()` from the list of what stops in the emit machinery. The regression-table row is kept, with its measurement, and says both guests now pass with the switch on: deleting it would leave the table's totals, which describe the measurement at `96f548f9`, inconsistent with its rows.
