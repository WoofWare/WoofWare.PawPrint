using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

// A tail shape whose call is a `callvirt` with a `constrained.` prefix. On .NET 11, `Parallel.ForEachAsync` runs its worker as
// `<ForEachAsync>b__57_0`, an `Async` method, which awaits `IAsyncEnumerator<T>::MoveNextAsync`;
// the JIT binds its async variant, which dispatch resolves to the async version of
// `Wrapper<PendingEnumerator>.MoveNextAsync`. That method's IL, compiled without optimisation, is
// `ldarg.0; ldflda Inner; constrained. !T; callvirt IAsyncEnumerator<int>::MoveNextAsync; ret`: a
// tail shape, at which the JIT binds the variant of the interface method too. The prefix transforms
// the receiver of the variant as it would the interface method's, for each of its three cases:
// * `PendingEnumerator`, a value type implementing the method itself: the variant of its own
//   `MoveNextAsync`, which runs on the address of `Inner`, not on a copy;
// * `PendingClass`, a reference type: the variant of what dispatch on the object finds;
// * `DefaultStruct`, a value type whose implementation is a default interface body: the variant of
//   what dispatch on a box finds.
//
// The enumerator's method returns a pending task, which the guest faults. Real .NET 11 resumes the
// wrapper's frame at its `callvirt`, IL_000C: the wrapper's await of the variant is no tail await,
// and the implementation's async version passes its own suspension on. Were the await of the
// `ValueTask` the ordinary `MoveNextAsync` returns, the wrapper's `ret` would pass that on, and the
// wrapper would have no frame among those resumed.
//
// .NET 10 compiles the methods it calls as state machines, so there the guest checks the results
// alone.
//
// The exit code is the index of the first check that failed; 0 means all passed.
public class Program
{
    private sealed class CountingScheduler : TaskScheduler
    {
        // Released each time a task the scheduler was given has run as far as it runs: finished, or
        // returned to the scheduler with its continuation registered.
        public readonly SemaphoreSlim Executed = new SemaphoreSlim (0);

        protected override void QueueTask (Task task)
        {
            ThreadPool.UnsafeQueueUserWorkItem (_ =>
            {
                TryExecuteTask (task);
                Executed.Release ();
            }, null);
        }

        protected override bool TryExecuteTaskInline (Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks () => null;
    }

    public struct PendingEnumerator : IAsyncEnumerator<int>
    {
        public TaskCompletionSource<bool> Pending;
        public int Moves;

        public int Current => 42;

        public ValueTask<bool> MoveNextAsync ()
        {
            Moves++;
            Pending = new TaskCompletionSource<bool> ();
            return new ValueTask<bool> (Pending.Task);
        }

        public ValueTask DisposeAsync () => default;
    }

    // Where a pending move's state lives when the receiver the method runs on is a copy.
    public sealed class Holder
    {
        public TaskCompletionSource<bool> Pending;
        public int Moves;
    }

    public sealed class PendingClass : IAsyncEnumerator<int>
    {
        public TaskCompletionSource<bool> Pending;
        public int Moves;

        public int Current => 42;

        public ValueTask<bool> MoveNextAsync ()
        {
            Moves++;
            Pending = new TaskCompletionSource<bool> ();
            return new ValueTask<bool> (Pending.Task);
        }

        public ValueTask DisposeAsync () => default;
    }

    public interface IDefaultMove : IAsyncEnumerator<int>
    {
        Holder State { get; }

        int IAsyncEnumerator<int>.Current => 42;

        ValueTask<bool> IAsyncEnumerator<int>.MoveNextAsync ()
        {
            State.Moves++;
            State.Pending = new TaskCompletionSource<bool> ();
            return new ValueTask<bool> (State.Pending.Task);
        }

        ValueTask IAsyncDisposable.DisposeAsync () => default;
    }

    public struct DefaultStruct : IDefaultMove
    {
        public Holder State { get; set; }
    }

    public sealed class Wrapper<T> : IAsyncEnumerable<int>, IAsyncEnumerator<int> where T : IAsyncEnumerator<int>
    {
        public T Inner;

        public int Current => Inner.Current;

        public IAsyncEnumerator<int> GetAsyncEnumerator (CancellationToken cancellationToken = default) => this;

        public ValueTask<bool> MoveNextAsync () => Inner.MoveNextAsync ();

        public ValueTask DisposeAsync () => default;
    }

    private static Exception FaultOf (Task task)
    {
        try
        {
            task.Wait ();
            return null;
        }
        catch (AggregateException e)
        {
            return e.InnerException;
        }
    }

    private static bool IsDispatcher (StackFrame frame)
    {
        string name = frame.GetMethod ().Name;
        return name == "DispatchContinuations" || name == "ResumeTaskContinuation";
    }

    // The method of the first frame from `frame` in the direction `step` that is not the
    // dispatcher's, as `Type::Method`.
    private static string NextPastDispatcher (StackFrame[] frames, int frame, int step)
    {
        int i = frame + step;
        while (i >= 0 && i < frames.Length && IsDispatcher (frames[i]))
            i += step;

        if (i < 0 || i == frames.Length) return null;
        var method = frames[i].GetMethod ();
        return method.DeclaringType.Name + "::" + method.Name;
    }

    // The index of the frame of the wrapper's `MoveNextAsync`; -1 if there is none.
    private static int WrapperFrame (StackFrame[] frames)
    {
        for (int i = 0; i < frames.Length; i++)
        {
            var method = frames[i].GetMethod ();
            if (method.DeclaringType.Name == "Wrapper`1" && method.Name == "MoveNextAsync") return i;
        }

        return -1;
    }

    private const string FormattedNet11 =
        "   at Program.Wrapper`1.MoveNextAsync()\n"
        + "   at System.Threading.Tasks.Parallel.<>c__57`1.<ForEachAsync>b__57_0(Object o)";

    private static Task Loop<T> (Wrapper<T> source, CountingScheduler scheduler) where T : IAsyncEnumerator<int>
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = 1, TaskScheduler = scheduler };
        return Parallel.ForEachAsync (source, options, (value, cancellationToken) => default);
    }

    // Run a loop over `source` whose one move faults, and check what the fault crossed; `pending`
    // and `moves` read the move's state. The exit code is `base` plus the index of the first check
    // that failed, or 0.
    private static int Faults<T> (
        int @base,
        Wrapper<T> source,
        Func<TaskCompletionSource<bool>> pending,
        Func<int> moves)
        where T : IAsyncEnumerator<int>
    {
        var scheduler = new CountingScheduler ();
        Task loop = Loop (source, scheduler);
        // The worker has suspended at its await of the pending move.
        if (!scheduler.Executed.Wait (TimeSpan.FromMinutes (1))) return @base + 1;
        if (loop.IsCompleted) return @base + 2;
        if (moves () != 1) return @base + 3;
        pending ().SetException (new IOException ("the move failed"));
        Exception fault = FaultOf (loop);
        if (!loop.IsFaulted) return @base + 4;
        if (fault is not IOException || fault.Message != "the move failed") return @base + 5;

        if (Environment.Version.Major >= 11)
        {
            string formatted = new StackTrace (fault, false).ToString ().TrimEnd ();
            if (formatted != FormattedNet11.Replace ("\n", Environment.NewLine)) return @base + 6;
            StackFrame[] frames = new StackTrace (fault, false).GetFrames ();
            int wrapper = WrapperFrame (frames);
            if (wrapper < 0) return @base + 7;
            // The wrapper resumes at its `callvirt`, not at its `ret`.
            if (frames[wrapper].GetILOffset () != 0x0C) return @base + 8;
            if (NextPastDispatcher (frames, wrapper, -1) != "RuntimeAsyncTaskContinuation::GetResult") return @base + 9;
            if (NextPastDispatcher (frames, wrapper, 1) != "<>c__57`1::<ForEachAsync>b__57_0") return @base + 10;
        }

        return 0;
    }

    public static int Main (string[] args)
    {
        // A move that completes with no element: the loop ends.
        var scheduler = new CountingScheduler ();
        var source = new Wrapper<PendingEnumerator> ();
        Task loop = Loop (source, scheduler);
        if (!scheduler.Executed.Wait (TimeSpan.FromMinutes (1))) return 1;
        if (loop.IsCompleted) return 2;
        // The struct's method ran on `Inner` itself, not on a copy.
        if (source.Inner.Moves != 1) return 3;
        source.Inner.Pending.SetResult (false);
        if (!loop.Wait (TimeSpan.FromMinutes (1)) || loop.Status != TaskStatus.RanToCompletion) return 4;

        // Moves that fault, through each of the prefix's cases. The value type's own method runs on
        // `Inner` itself; the others on the object or its box, whose state the holder keeps.
        var own = new Wrapper<PendingEnumerator> ();
        int result = Faults (10, own, () => own.Inner.Pending, () => own.Inner.Moves);
        if (result != 0) return result;

        var reference = new Wrapper<PendingClass> { Inner = new PendingClass () };
        result = Faults (20, reference, () => reference.Inner.Pending, () => reference.Inner.Moves);
        if (result != 0) return result;

        var holder = new Holder ();
        var boxed = new Wrapper<DefaultStruct> { Inner = new DefaultStruct { State = holder } };
        result = Faults (30, boxed, () => holder.Pending, () => holder.Moves);
        if (result != 0) return result;

        return 0;
    }
}
