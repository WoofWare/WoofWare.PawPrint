using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// An await that continues on the captured context. On .NET 11, `Parallel.ForEachAsync` over an
// `IAsyncEnumerable<T>` runs each worker as `<ForEachAsync>b__57_0`, an `Async` method, which awaits
// `callvirt IAsyncEnumerator<T>::MoveNextAsync` at IL_002F with no `ConfigureAwait`. The JIT binds
// the async variant of the interface method, which dispatch resolves to the async version of the
// guest's `MoveNextAsync`. If that suspends, the worker's continuation records the scheduler the
// worker runs on (`FinishSuspensionWithContinuationContext`), and the async version's own
// continuation records nothing.
//
// The guest's `MoveNextAsync` returns a task that forbids inline continuations, and another thread
// completes it. CoreLib's dispatcher queues the async version's unflagged continuation to the
// thread pool, and from there hands the worker's to the guest's scheduler: the scheduler is given
// it by a thread other than the completing one. Were the await of the `ValueTask` the ordinary
// `MoveNextAsync` returns, the awaiter would hand it to the scheduler from the completing thread,
// as .NET 10's state machines do.
//
// Either way the loop's body runs on the guest's scheduler. Were the worker's continuation to
// record no context, it would run where the async version's resumed, on the thread pool.
//
// The exit code is the index of the first check that failed; 0 means all passed.
public class Program
{
    private sealed class CountingScheduler : TaskScheduler
    {
        private readonly object _lock = new object ();
        private readonly List<Thread> _queuers = new List<Thread> ();

        // Released each time a task the scheduler was given has run as far as it runs: finished, or
        // returned to the scheduler with its continuation registered.
        public readonly SemaphoreSlim Executed = new SemaphoreSlim (0);

        public Thread[] Queuers ()
        {
            lock (_lock) return _queuers.ToArray ();
        }

        protected override void QueueTask (Task task)
        {
            lock (_lock) _queuers.Add (Thread.CurrentThread);
            ThreadPool.UnsafeQueueUserWorkItem (_ =>
            {
                TryExecuteTask (task);
                Executed.Release ();
            }, null);
        }

        protected override bool TryExecuteTaskInline (Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks () => null;
    }

    private sealed class PendingEnumerable : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        public TaskCompletionSource<bool> Pending;
        public int Moves;
        public int Current { get; private set; }

        public IAsyncEnumerator<int> GetAsyncEnumerator (CancellationToken cancellationToken = default) => this;

        public ValueTask<bool> MoveNextAsync ()
        {
            Moves++;
            if (Moves == 1)
            {
                Current = 42;
                Pending = new TaskCompletionSource<bool> (TaskCreationOptions.RunContinuationsAsynchronously);
                return new ValueTask<bool> (Pending.Task);
            }

            return new ValueTask<bool> (false);
        }

        public ValueTask DisposeAsync () => default;
    }

    public static int Main (string[] args)
    {
        bool net11 = Environment.Version.Major >= 11;
        var scheduler = new CountingScheduler ();
        var source = new PendingEnumerable ();
        int bodies = 0;
        int item = 0;
        TaskScheduler bodyScheduler = null;

        var options = new ParallelOptions { MaxDegreeOfParallelism = 1, TaskScheduler = scheduler };
        Task loop = Parallel.ForEachAsync (source, options, (value, cancellationToken) =>
        {
            bodies++;
            item = value;
            bodyScheduler = TaskScheduler.Current;
            return default;
        });

        // The worker has suspended at its await of the pending move, so the completing thread runs
        // its continuation, rather than the worker finding the move complete.
        if (!scheduler.Executed.Wait (TimeSpan.FromMinutes (1))) return 1;
        if (loop.IsCompleted) return 2;
        if (source.Moves != 1) return 3;
        int queuedBefore = scheduler.Queuers ().Length;

        var completer = new Thread (() => source.Pending.SetResult (true));
        completer.Start ();
        completer.Join ();

        if (!loop.Wait (TimeSpan.FromMinutes (1))) return 4;
        if (loop.Status != TaskStatus.RanToCompletion) return 5;
        if (bodies != 1 || item != 42) return 6;
        if (source.Moves != 2) return 7;
        if (bodyScheduler != scheduler) return 8;

        Thread[] queuers = scheduler.Queuers ();
        if (queuers.Length <= queuedBefore) return 9;
        Thread resumer = queuers[queuedBefore];
        if (net11)
        {
            if (resumer == completer) return 10;
        }
        else
        {
            if (resumer != completer) return 11;
        }

        return 0;
    }
}
