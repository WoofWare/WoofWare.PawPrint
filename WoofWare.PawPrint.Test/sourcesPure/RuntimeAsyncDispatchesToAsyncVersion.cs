using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Awaits of a virtual method, whose implementation on the receiver is an async version. On .NET 11,
// `StreamWriter.DisposeAsync` returns the task-returning thunk of `DisposeAsyncCore`, which awaits
// `FlushAsync().ConfigureAwait(false)` by a `callvirt` of `TextWriter.FlushAsync()`, and
// `BroadcastingTextWriter.FlushAsync()` awaits each of its writers' `FlushAsync()` the same way.
// The JIT binds the async variant of `TextWriter.FlushAsync()`, and dispatch on a `StreamWriter`
// finds the variant of its override, an async version: `StreamWriter.FlushAsync()` has no `Async`
// flag. In that async version `RuntimeHelpers.IsRuntimeAsync()` answers true, so it ends
// `call FlushAsyncInternalWithGuard; ret`, a tail await of an `Async` method, which awaits
// `FlushAsyncInternal`'s async version, which tail-awaits the local function `Core`, which awaits
// the guest's `WriteAsync`.
//
// The guest leaves each write pending, then completes or faults it. Faulted, the exception crosses
// `Core`'s continuation, `FlushAsyncInternalWithGuard`'s, and the awaiting method's: the trace has
// the three resumed frames with no frame of `StreamWriter.FlushAsync` and no awaiter among them.
// Were the await of the task the ordinary variant returns, the trace would lack
// `FlushAsyncInternalWithGuard`, since `IsRuntimeAsync()` answers false in the ordinary variant,
// and would have an awaiter's `GetResult` before the awaiting method.
//
// The formatted trace hides the awaiter's frames and the dispatcher's; among the frames, the last
// the formatted trace hides before `Core`, past the dispatcher's, is a
// `RuntimeAsyncTaskContinuation.GetResult`.
//
// .NET 10 compiles the same methods as state machines, so there the guest checks the results alone.
//
// The exit code is the index of the first check that failed; 0 means all passed.
public class Program
{
    private sealed class PendingWriteStream : Stream
    {
        public readonly MemoryStream Written = new MemoryStream ();
        public TaskCompletionSource Pending;
        public int Writes;
        public int Disposals;

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

        public override Task FlushAsync (CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read (byte[] buffer, int offset, int count) => throw new NotSupportedException ();
        public override long Seek (long offset, SeekOrigin origin) => throw new NotSupportedException ();
        public override void SetLength (long value) => throw new NotSupportedException ();
        public override void Write (byte[] buffer, int offset, int count) => throw new NotSupportedException ();

        public override ValueTask WriteAsync (ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            Written.Write (buffer.Span);
            Pending = new TaskCompletionSource ();
            return new ValueTask (Pending.Task);
        }

        protected override void Dispose (bool disposing)
        {
            Disposals++;
            base.Dispose (disposing);
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

    // Whether the frames before `resumed`, past the dispatcher's, end with the awaiter's `GetResult`.
    // All of these are hidden from a formatted trace.
    private static bool AwaiterResumes (StackFrame[] frames, int resumed)
    {
        int i = resumed - 1;
        while (i >= 0 && (frames[i].GetMethod ().Name == "DispatchContinuations" || frames[i].GetMethod ().Name == "ResumeTaskContinuation"))
            i--;

        if (i < 0) return false;
        var method = frames[i].GetMethod ();
        return method.DeclaringType.FullName == "System.Runtime.CompilerServices.RuntimeAsyncTaskContinuation" && method.Name == "GetResult";
    }

    private static string Describe (StackFrame[] frames)
    {
        var text = new StringBuilder ();
        foreach (StackFrame frame in frames)
        {
            var method = frame.GetMethod ();
            text.Append (method.DeclaringType).Append ("::").Append (method.Name).Append ('\n');
        }

        return text.ToString ();
    }

    // The index of the frame of `<FlushAsyncInternal>g__Core|82_0`; -1 if there is none.
    private static int CoreFrame (StackFrame[] frames)
    {
        for (int i = 0; i < frames.Length; i++)
        {
            if (frames[i].GetMethod ().Name == "<FlushAsyncInternal>g__Core|82_0") return i;
        }

        return -1;
    }

    private const string DisposeResumedNet11 =
        "System.IO.StreamWriter::<FlushAsyncInternal>g__Core|82_0\n"
        + "System.IO.StreamWriter::FlushAsyncInternalWithGuard\n"
        + "System.Runtime.CompilerServices.AsyncHelpers+RuntimeAsyncTask`1[T]::DispatchContinuations\n"
        + "System.IO.StreamWriter::DisposeAsyncCore\n"
        + "System.Runtime.CompilerServices.AsyncHelpers+RuntimeAsyncTask`1[T]::DispatchContinuations\n";

    private const string DisposeFormattedNet11 =
        "   at System.IO.StreamWriter.<FlushAsyncInternal>g__Core|82_0(Boolean flushStream, Boolean flushEncoder, CancellationToken cancellationToken)\n"
        + "   at System.IO.StreamWriter.FlushAsyncInternalWithGuard(Boolean flushStream, Boolean flushEncoder, CancellationToken cancellationToken)\n"
        + "   at System.IO.StreamWriter.DisposeAsyncCore()\n"
        + "   at System.Runtime.CompilerServices.AsyncHelpers.RuntimeAsyncTask`1.DispatchContinuations()";

    private const string BroadcastResumedNet11 =
        "System.IO.StreamWriter::<FlushAsyncInternal>g__Core|82_0\n"
        + "System.IO.StreamWriter::FlushAsyncInternalWithGuard\n"
        + "System.Runtime.CompilerServices.AsyncHelpers+RuntimeAsyncTask`1[T]::DispatchContinuations\n"
        + "System.IO.TextWriter+BroadcastingTextWriter::FlushAsync\n";

    private const string BroadcastFormattedNet11 =
        "   at System.IO.StreamWriter.<FlushAsyncInternal>g__Core|82_0(Boolean flushStream, Boolean flushEncoder, CancellationToken cancellationToken)\n"
        + "   at System.IO.StreamWriter.FlushAsyncInternalWithGuard(Boolean flushStream, Boolean flushEncoder, CancellationToken cancellationToken)\n"
        + "   at System.IO.TextWriter.BroadcastingTextWriter.FlushAsync()";

    public static int Main (string[] args)
    {
        bool net11 = Environment.Version.Major >= 11;

        // `DisposeAsync`, whose write completes after the methods have suspended.
        var stream = new PendingWriteStream ();
        var writer = new StreamWriter (stream, new UTF8Encoding (false));
        writer.Write ("hi");
        Task disposing = writer.DisposeAsync ().AsTask ();
        if (disposing.IsCompleted) return 1;
        if (stream.Writes != 1) return 2;
        if (stream.Disposals != 0) return 3;
        stream.Pending.SetResult ();
        if (FaultOf (disposing) != null) return 4;
        if (stream.Disposals != 1) return 5;
        if (Encoding.UTF8.GetString (stream.Written.ToArray ()) != "hi") return 6;

        // `DisposeAsync`, whose write faults after the methods have suspended.
        stream = new PendingWriteStream ();
        writer = new StreamWriter (stream, new UTF8Encoding (false));
        writer.Write ("hi");
        disposing = writer.DisposeAsync ().AsTask ();
        if (disposing.IsCompleted) return 7;
        var failure = new IOException ("the write failed");
        stream.Pending.SetException (failure);
        if (!ReferenceEquals (FaultOf (disposing), failure)) return 8;
        if (stream.Disposals != 1) return 9;

        if (net11)
        {
            StackFrame[] frames = new StackTrace (failure, false).GetFrames ();
            int core = CoreFrame (frames);
            if (core < 0) return 10;
            if (Describe (frames[core..]) != DisposeResumedNet11) return 11;

            // `DisposeAsyncCore`'s frame is at its `call Await`.
            if (frames[frames.Length - 2].GetILOffset () != 0x14) return 12;

            string formatted = new StackTrace (failure, false).ToString ().TrimEnd ();
            if (formatted != DisposeFormattedNet11.Replace ("\n", Environment.NewLine)) return 13;

            if (!AwaiterResumes (frames, core)) return 14;
        }

        // A broadcasting writer's `FlushAsync`, whose first writer's write completes after the
        // methods have suspended.
        stream = new PendingWriteStream ();
        writer = new StreamWriter (stream, new UTF8Encoding (false));
        writer.Write ("there");
        var broadcasting = TextWriter.CreateBroadcasting (writer, TextWriter.Null);
        Task flushing = broadcasting.FlushAsync ();
        if (flushing.IsCompleted) return 15;
        if (stream.Writes != 1) return 16;
        stream.Pending.SetResult ();
        if (FaultOf (flushing) != null) return 17;
        if (Encoding.UTF8.GetString (stream.Written.ToArray ()) != "there") return 18;

        // The same, whose write faults after the methods have suspended.
        stream = new PendingWriteStream ();
        writer = new StreamWriter (stream, new UTF8Encoding (false));
        writer.Write ("there");
        broadcasting = TextWriter.CreateBroadcasting (writer, TextWriter.Null);
        flushing = broadcasting.FlushAsync ();
        if (flushing.IsCompleted) return 19;
        failure = new IOException ("the broadcast write failed");
        stream.Pending.SetException (failure);
        if (!ReferenceEquals (FaultOf (flushing), failure)) return 20;

        if (net11)
        {
            StackFrame[] frames = new StackTrace (failure, false).GetFrames ();
            int core = CoreFrame (frames);
            if (core < 0) return 21;
            if (Describe (frames[core..]) != BroadcastResumedNet11) return 22;

            // `BroadcastingTextWriter.FlushAsync`'s frame is at its `call Await`.
            if (frames[frames.Length - 1].GetILOffset () != 0x19) return 23;

            string formatted = new StackTrace (failure, false).ToString ().TrimEnd ();
            if (formatted != BroadcastFormattedNet11.Replace ("\n", Environment.NewLine)) return 24;

            if (!AwaiterResumes (frames, core)) return 25;
        }

        return 0;
    }
}
