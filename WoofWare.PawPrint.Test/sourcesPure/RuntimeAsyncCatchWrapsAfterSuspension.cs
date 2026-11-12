using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

// A runtime-async method that suspends at an await inside a `try`, and resumes with an exception
// that its own `catch` takes and replaces. On .NET 11, `HttpContent.LoadIntoBufferAsync` returns the
// task-returning thunk of `LoadIntoBufferAsyncCore`, which awaits the task the guest's
// `SerializeToStreamAsync` returned inside `try { ... } catch (Exception e)`, whose handler wraps an
// `IOException` in an `HttpRequestException`. The guest leaves that task pending, so the method
// suspends, and then faults it with an `IOException`.
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
        var content = new PendingContent ();
        Task loading = content.LoadIntoBufferAsync ();
        if (loading.IsCompleted) return 1;
        var ioException = new IOException ("the copy failed");
        content.Pending.SetException (ioException);
        Exception wrapped = FaultOf (loading);
        if (!(wrapped is HttpRequestException)) return 2;
        if (!ReferenceEquals (wrapped.InnerException, ioException)) return 3;
        if (!loading.IsFaulted) return 4;

        return 0;
    }
}
