using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Awaits whose dispatch finds the async version of a synchronized method. `TextWriter.Synchronized`
// wraps a writer in a `SyncTextWriter`, whose methods are all synchronized on the wrapper, and on
// .NET 11 `BroadcastingTextWriter`'s `WriteAsync` and `FlushAsync` are `Async` methods awaiting each
// writer's by `callvirt` with `ConfigureAwait(false)`. The JIT binds the async variant of the named
// virtual method, and dispatch on a `SyncTextWriter` finds the variant of its override: an async
// version, since none of its methods is flagged `Async`. Each does synchronous work, under the
// monitor, and returns a completed or cancelled task, so every `ret` of the async version awaits it
// through `AsyncHelpers.TransparentAwait`. CoreCLR's JIT releases the monitor immediately before that
// call.
//
// So after each call the wrapper's monitor is free, whether the call completed, its synchronous
// work threw, or its awaited task was cancelled and `TransparentAwait` threw. Released twice, the
// exit helper would throw `SynchronizationLockException`. The trace of the exception the inner
// writer's `Flush` throws has the wrapper's `Flush` and `FlushAsync`, and the broadcasting writer's
// `FlushAsync`.
//
// .NET 10 compiles `BroadcastingTextWriter`'s methods as state machines, which call the ordinary
// methods, with the same results; there the trace has a state machine's `MoveNext` in place of the
// broadcasting writer's `FlushAsync`, so the guest does not check it.
//
// The exit code is the index of the first check that failed; 0 means all passed.
public class Program
{
    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException ();

        public override long Position
        {
            get => throw new NotSupportedException ();
            set => throw new NotSupportedException ();
        }

        public override void Flush ()
        {
        }

        public override int Read (byte[] buffer, int offset, int count) => throw new NotSupportedException ();
        public override long Seek (long offset, SeekOrigin origin) => throw new NotSupportedException ();
        public override void SetLength (long value) => throw new NotSupportedException ();

        public override void Write (byte[] buffer, int offset, int count) => throw new IOException ("the write failed");
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

    // The frames' methods from the first one declared by `firstType`, as `Type::Method` lines.
    private static string DescribeFrom (StackFrame[] frames, string firstType)
    {
        var text = new StringBuilder ();
        bool started = false;
        foreach (StackFrame frame in frames)
        {
            var method = frame.GetMethod ();
            started = started || method.DeclaringType.FullName == firstType;
            if (started) text.Append (method.DeclaringType).Append ("::").Append (method.Name).Append ('\n');
        }

        return text.ToString ();
    }

    private const string FlushTrace =
        "System.IO.TextWriter+SyncTextWriter::Flush\n"
        + "System.IO.TextWriter+SyncTextWriter::FlushAsync\n"
        + "System.IO.TextWriter+BroadcastingTextWriter::FlushAsync\n";

    public static int Main (string[] args)
    {
        // Calls that complete.
        var text = new StringWriter ();
        TextWriter synchronized = TextWriter.Synchronized (text);
        TextWriter broadcasting = TextWriter.CreateBroadcasting (synchronized, TextWriter.Null);
        Task writing = broadcasting.WriteAsync ("abc");
        if (writing.Status != TaskStatus.RanToCompletion) return 1;
        if (text.ToString () != "abc") return 2;
        if (Monitor.IsEntered (synchronized)) return 3;
        Task flushing = broadcasting.FlushAsync ();
        if (flushing.Status != TaskStatus.RanToCompletion) return 4;
        if (Monitor.IsEntered (synchronized)) return 5;

        // A call whose awaited task is cancelled: `TransparentAwait` throws after the release.
        var cancelled = new CancellationToken (true);
        flushing = broadcasting.FlushAsync (cancelled);
        if (!flushing.IsCanceled) return 6;
        if (Monitor.IsEntered (synchronized)) return 7;

        // Calls whose synchronous work throws under the monitor.
        var writer = new StreamWriter (new ThrowingStream (), new UTF8Encoding (false));
        synchronized = TextWriter.Synchronized (writer);
        broadcasting = TextWriter.CreateBroadcasting (synchronized, TextWriter.Null);
        writing = broadcasting.WriteAsync ("abc");
        if (writing.Status != TaskStatus.RanToCompletion) return 8;
        flushing = broadcasting.FlushAsync ();
        Exception fault = FaultOf (flushing);
        if (fault is not IOException || fault.Message != "the write failed") return 9;
        if (Monitor.IsEntered (synchronized)) return 10;

        if (Environment.Version.Major >= 11)
        {
            StackFrame[] frames = new StackTrace (fault, false).GetFrames ();
            if (DescribeFrom (frames, "System.IO.TextWriter+SyncTextWriter") != FlushTrace) return 11;
        }

        // The wrapper's monitor is free for another thread.
        bool taken = false;
        var other = new Thread (() =>
        {
            taken = Monitor.TryEnter (synchronized);
            if (taken) Monitor.Exit (synchronized);
        });
        other.Start ();
        other.Join ();
        if (!taken) return 12;

        return 0;
    }
}
