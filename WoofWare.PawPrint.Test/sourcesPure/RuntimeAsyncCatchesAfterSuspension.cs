using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

// A runtime-async method that suspends at an await inside a `try` with a `catch`, and resumes. On
// .NET 11, `HttpContent.LoadIntoBufferAsync` returns the task-returning thunk of
// `LoadIntoBufferAsyncCore`, which awaits the task the guest's `SerializeToStreamAsync` returned
// inside `try { ... } catch (Exception e)`. The guest leaves that task pending, so the method
// suspends, and later completes or faults it.
//
// Completed, the method buffers what the guest wrote before its task completed. Faulted with
// anything but an `IOException` or `ObjectDisposedException`, the method's `catch` rethrows the same
// object (`RuntimeAsyncCatchWrapsAfterSuspension.cs` covers the `catch` wrapping an `IOException`).
//
// .NET 10 compiles the same method as a state machine, with the same results.
//
// The exit code is the index of the first check that failed; 0 means all passed.
public class Program
{
    private sealed class PendingContent : HttpContent
    {
        public readonly TaskCompletionSource Pending = new TaskCompletionSource ();
        public int Serialisations;

        protected override Task SerializeToStreamAsync (Stream stream, TransportContext context)
        {
            Serialisations++;
            stream.Write (new byte[] { 1, 2, 3 });
            return Pending.Task;
        }

        protected override bool TryComputeLength (out long length)
        {
            length = 0;
            return false;
        }
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

    public static int Main (string[] args)
    {
        // Completed after suspending: the bytes written before completion are buffered.
        var completed = new PendingContent ();
        Task loading = completed.LoadIntoBufferAsync ();
        if (loading.IsCompleted) return 1;
        if (completed.Serialisations != 1) return 2;
        completed.Pending.SetResult ();
        if (FaultOf (loading) != null) return 3;
        byte[] buffered = completed.ReadAsByteArrayAsync ().Result;
        if (buffered.Length != 3 || buffered[0] != 1 || buffered[2] != 3) return 4;
        if (completed.Serialisations != 1) return 5;

        // Faulted with an exception it does not wrap: the resumed frame's `catch` rethrows the same object.
        var rethrowing = new PendingContent ();
        loading = rethrowing.LoadIntoBufferAsync ();
        if (loading.IsCompleted) return 6;
        var other = new InvalidOperationException ("not a copy failure");
        rethrowing.Pending.SetException (other);
        if (!ReferenceEquals (FaultOf (loading), other)) return 7;

        return 0;
    }
}
