using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

// The pointers to stack locals that .NET 11's continuation dispatcher keeps in thread statics, with
// CoreLib's internal types copied (`AsyncHelpers.CoreCLR.cs`, `AsyncProfiler.cs`):
//
// * `RuntimeAsyncAwaitState`, a `[ThreadStatic]` struct taken by `ref`, whose `Push` stores a
//   `RuntimeAsyncStackState*` to the caller's local and whose `CaptureContexts` writes references
//   through it from a deeper frame;
// * `AsyncDispatcherInfo.t_current`, a `[ThreadStatic]` pointer to an explicit-layout `ref struct`
//   local, written through a `ref AsyncDispatcherInfo*` and linked to the outer dispatcher's by
//   `Next`, as `DispatchContinuations` links it.
//
// Only CoreLib's async instrumentation dereferences `t_current`, so this compares it and does not
// dereference it.
public class Continuation
{
    public Continuation? Next;
    public int State;
}

public unsafe ref struct RuntimeAsyncStackState
{
    public object? CriticalNotifier;
    public object? Notifier;
    public delegate*<Continuation, int, Action, void> AwaiterContinuation;
    public int AwaiterOffset;
    public ExecutionContext? LeafExecutionContext;
    public SynchronizationContext? LeafSynchronizationContext;
    public RuntimeAsyncStackState* Next;
}

public unsafe struct RuntimeAsyncAwaitState
{
    public Continuation? SentinelContinuation;
    public Thread? CurrentThread;
    public RuntimeAsyncStackState* StackState;

    public void CaptureContexts()
    {
        Thread? curThread = CurrentThread;
        StackState->LeafExecutionContext = ExecutionContext.Capture();
        StackState->LeafSynchronizationContext = SynchronizationContext.Current;
    }

    public void Push(RuntimeAsyncStackState* stackState)
    {
        stackState->AwaiterContinuation = null;
        stackState->AwaiterOffset = 0;
        stackState->Next = StackState;
        StackState = stackState;
        CurrentThread ??= Thread.CurrentThread;
    }

    public void Pop()
    {
        StackState = StackState->Next;
    }
}

public ref struct ProfilerInfo
{
    public ulong DispatcherId;
    public object? Context;
    public object? CurrentContinuation;
    public object? LastContinuation;
    public ref nint ContinuationTable;
    public uint ContinuationIndex;
    public bool CurrentContinuationCompleted;
    public bool CurrentContinuationResumes;
    public bool ReachedLastContinuation;
}

[StructLayout(LayoutKind.Explicit)]
public unsafe ref struct AsyncDispatcherInfo
{
    [FieldOffset(0)]
    public AsyncDispatcherInfo* Next;

    [FieldOffset(8)]
    public Continuation? NextContinuation;

    [FieldOffset(16)]
    public object? CurrentTask;

    [FieldOffset(24)]
    public ProfilerInfo AsyncProfilerInfo;

    [ThreadStatic]
    internal static unsafe AsyncDispatcherInfo* t_current;
}

public static unsafe class Program
{
    [ThreadStatic]
    private static RuntimeAsyncAwaitState t_runtimeAsyncAwaitState;

    private static object? Leaf()
    {
        ref RuntimeAsyncAwaitState state = ref t_runtimeAsyncAwaitState;
        state.CaptureContexts();
        return state.StackState->LeafExecutionContext;
    }

    // As `DispatchContinuations` brackets its loop: push both, run the chain, pop both.
    private static int Dispatch(Continuation head, int nested, AsyncDispatcherInfo* outer)
    {
        RuntimeAsyncStackState stackState;

        ref RuntimeAsyncAwaitState awaitState = ref t_runtimeAsyncAwaitState;
        awaitState.Push(&stackState);

        ref AsyncDispatcherInfo* refDispatcherInfo = ref AsyncDispatcherInfo.t_current;

        AsyncDispatcherInfo asyncDispatcherInfo;
        asyncDispatcherInfo.Next = refDispatcherInfo;
        asyncDispatcherInfo.NextContinuation = head;
        refDispatcherInfo = &asyncDispatcherInfo;

        int sum = 0;
        while (asyncDispatcherInfo.NextContinuation != null)
        {
            Continuation cur = asyncDispatcherInfo.NextContinuation;
            asyncDispatcherInfo.NextContinuation = cur.Next;
            sum += cur.State;

            if (AsyncDispatcherInfo.t_current != &asyncDispatcherInfo) return -1000;
            if (asyncDispatcherInfo.Next != outer) return -2000;

            if (!ReferenceEquals(Leaf(), stackState.LeafExecutionContext)) return -3000;
            if (!ReferenceEquals(stackState.LeafSynchronizationContext, SynchronizationContext.Current)) return -4000;

            if (cur.State == 2 && nested < 3)
            {
                int inner = Dispatch(new Continuation { State = 100 * nested }, nested + 1, &asyncDispatcherInfo);
                if (inner < 0) return inner;
                sum += inner;
                if (AsyncDispatcherInfo.t_current != &asyncDispatcherInfo) return -5000;
                if (awaitState.StackState != &stackState) return -6000;
            }
        }

        awaitState.Pop();
        refDispatcherInfo = asyncDispatcherInfo.Next;
        if (AsyncDispatcherInfo.t_current != outer) return -7000;
        return sum;
    }

    private static int Run()
    {
        if (AsyncDispatcherInfo.t_current != null) return 1;

        var chain = new Continuation
        {
            State = 1,
            Next = new Continuation { State = 2, Next = new Continuation { State = 3 } },
        };

        int sum = Dispatch(chain, 1, null);
        if (sum != 1 + 2 + 3 + 100) return 2;
        if (AsyncDispatcherInfo.t_current != null) return 3;
        if (t_runtimeAsyncAwaitState.StackState != null) return 4;
        if (t_runtimeAsyncAwaitState.CurrentThread != Thread.CurrentThread) return 5;

        // The thread statics are per thread: another thread starts from nothing.
        int other = -1;
        var thread = new Thread(() =>
        {
            if (AsyncDispatcherInfo.t_current != null || t_runtimeAsyncAwaitState.CurrentThread != null)
            {
                other = 1;
                return;
            }

            other = Dispatch(new Continuation { State = 7 }, 1, null) == 7 ? 0 : 2;
        });
        thread.Start();
        thread.Join();
        if (other != 0) return 10 + other;
        if (t_runtimeAsyncAwaitState.CurrentThread != Thread.CurrentThread) return 6;

        return 0;
    }

    public static int Main() => Run();
}
