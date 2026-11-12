using System;
using System.Threading;
using System.Threading.Tasks;

// A runtime-async method suspending at an await of a task configured with
// `ConfigureAwaitOptions.SuppressThrowing`. On .NET 11, `SemaphoreSlim.WaitAsync` with a
// cancellation token, on a semaphore with no count, returns the task-returning thunk of
// `WaitUntilCountOrTimeoutAsync`, which awaits its waiter that way. `AsyncHelpers.Await` sends such
// an await to `AwaitTaskWithRareOptions`, which suspends at its own await of the awaiter, not as a
// tail await, so its continuation captures the `ExecutionContext` itself.
//
// Released, the wait completes true. Cancelled, the suppressed await completes without throwing, and
// the method then throws `OperationCanceledException`, which cancels its task.
//
// .NET 10 compiles the same method as a state machine, with the same results.
//
// The exit code is the index of the first check that failed; 0 means all passed.
public class Program
{
    private static readonly AsyncLocal<int> s_flowed = new AsyncLocal<int> ();

    public static int Main (string[] args)
    {
        var semaphore = new SemaphoreSlim (0);
        var source = new CancellationTokenSource ();

        // Released after the method has suspended.
        s_flowed.Value = 42;
        int seen = 0;
        Task<bool> waiting = semaphore.WaitAsync (Timeout.Infinite, source.Token);
        if (waiting.IsCompleted) return 1;
        Task<bool> observed = waiting.ContinueWith (t => { seen = s_flowed.Value; return t.Result; });
        s_flowed.Value = 7;
        semaphore.Release ();
        if (!waiting.Result) return 2;
        if (!observed.Result) return 3;
        if (seen != 42) return 4;
        if (semaphore.CurrentCount != 0) return 5;

        // Cancelled after the method has suspended.
        waiting = semaphore.WaitAsync (Timeout.Infinite, source.Token);
        if (waiting.IsCompleted) return 6;
        source.Cancel ();

        try
        {
            waiting.Wait ();
            return 7;
        }
        catch (AggregateException e) when (e.InnerException is OperationCanceledException)
        {
        }

        if (!waiting.IsCanceled) return 8;

        // The semaphore still works: the cancelled waiter is gone.
        semaphore.Release ();
        if (!semaphore.Wait (0)) return 9;

        return 0;
    }
}
