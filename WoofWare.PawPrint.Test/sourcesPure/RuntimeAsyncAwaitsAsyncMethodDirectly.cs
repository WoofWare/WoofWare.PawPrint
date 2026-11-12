using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// An await of an `Async` method called directly binds the method's async variant. On .NET 11,
// `StreamReader.ReadToEndAsync` returns the task-returning thunk of `ReadToEndAsyncInternal`, whose
// await of `ReadBufferAsync(cancellationToken).ConfigureAwait(false)` at IL_0053, inside the `try`
// of a `using`, is the JIT's await pattern: CoreCLR's JIT calls the async variant of
// `ReadBufferAsync`, an `Async` method called directly, rather than awaiting the `ValueTask<int>`
// its ordinary variant returns. `ReadBufferAsync` awaits the guest's `ReadAsync`, which the guest
// leaves pending, so both suspend, into one chain of continuations.
//
// Completed, the reader returns the text across both suspensions. Faulted, the exception crosses
// `ReadBufferAsync`'s continuation, which has no handler, then `ReadToEndAsyncInternal`'s, whose
// `finally` frees the reader. Because the two frames are one chain, the trace has them adjacent,
// with no awaiter between them; and the frame of `ReadToEndAsyncInternal` is reported at the IL
// offset of its `call Await`, which the JIT gives the whole pattern. Were the await of the task
// the ordinary variant returns, the awaiter's `GetResult` would come between the two frames.
//
// Which of the awaiter's frames come before `ReadBufferAsync` depends on what the JIT inlined:
// .NET 11's inlines `RuntimeAsyncTaskContinuation.GetResult(ref byte)` into the dispatcher's resume
// function, so its formatted trace shows the generic `GetResult<T>(Task, ref byte)` last, and PawPrint,
// inlining nothing, shows the former. So the guest checks that a frame of
// `RuntimeAsyncTaskContinuation.GetResult` comes last before `ReadBufferAsync`, and the trace from
// there onward.
//
// .NET 10 compiles the same methods as state machines, so there the guest checks the results alone.
//
// The exit code is the index of the first check that failed; 0 means all passed.
public class Program
{
    private sealed class PendingStream : Stream
    {
        private TaskCompletionSource<int> _pending;
        private Memory<byte> _destination;

        public int Requests;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
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
        public override void Write (byte[] buffer, int offset, int count) => throw new NotSupportedException ();

        public override ValueTask<int> ReadAsync (Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Requests++;
            _destination = buffer;
            _pending = new TaskCompletionSource<int> ();
            return new ValueTask<int> (_pending.Task);
        }

        public void Deliver (string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes (text);
            bytes.CopyTo (_destination.Span);
            _pending.SetResult (bytes.Length);
        }

        public void Fault (Exception e) => _pending.SetException (e);
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

    private const string ResumedNet11 =
        "System.IO.StreamReader::ReadBufferAsync\n"
        + "System.IO.StreamReader::ReadToEndAsyncInternal\n"
        + "System.Runtime.CompilerServices.AsyncHelpers+RuntimeAsyncTask`1[T]::DispatchContinuations\n";

    private const string FormattedAwaiterNet11 = "   at System.Runtime.CompilerServices.RuntimeAsyncTaskContinuation.GetResult";

    private const string FormattedNet11 =
        "   at System.IO.StreamReader.ReadBufferAsync(CancellationToken cancellationToken)\n"
        + "   at System.IO.StreamReader.ReadToEndAsyncInternal(CancellationToken cancellationToken)\n"
        + "   at System.Runtime.CompilerServices.AsyncHelpers.RuntimeAsyncTask`1.DispatchContinuations()";

    public static int Main (string[] args)
    {
        bool net11 = Environment.Version.Major >= 11;
        var stream = new PendingStream ();
        var reader = new StreamReader (stream, new UTF8Encoding (false), false);

        // One read completes, then one reads end of stream: two suspensions.
        Task<string> reading = reader.ReadToEndAsync ();
        if (reading.IsCompleted) return 1;
        if (stream.Requests != 1) return 2;
        stream.Deliver ("hello, world");
        if (reading.IsCompleted) return 3;
        if (stream.Requests != 2) return 4;
        stream.Deliver ("");
        if (reading.Result != "hello, world") return 5;

        // A read completes, and the next faults.
        reading = reader.ReadToEndAsync ();
        if (reading.IsCompleted) return 6;
        stream.Deliver ("partial");
        if (reading.IsCompleted) return 7;
        var failure = new IOException ("the read failed");
        stream.Fault (failure);
        if (!ReferenceEquals (FaultOf (reading), failure)) return 8;

        if (net11)
        {
            StackFrame[] frames = new StackTrace (failure, false).GetFrames ();
            if (!Describe (frames).EndsWith (ResumedNet11, StringComparison.Ordinal)) return 9;

            // The frame of `ReadToEndAsyncInternal` is at its `call Await`.
            if (frames[frames.Length - 2].GetILOffset () != 0x53) return 10;

            string newLine = Environment.NewLine;
            string formatted = new StackTrace (failure, false).ToString ().TrimEnd ();
            string ending = newLine + FormattedNet11.Replace ("\n", newLine);
            if (!formatted.EndsWith (ending, StringComparison.Ordinal)) return 11;

            string beforeEnding = formatted.Substring (0, formatted.Length - ending.Length);
            int lastLine = beforeEnding.LastIndexOf (newLine, StringComparison.Ordinal);
            string awaiter = lastLine < 0 ? beforeEnding : beforeEnding.Substring (lastLine + newLine.Length);
            if (!awaiter.StartsWith (FormattedAwaiterNet11, StringComparison.Ordinal)) return 12;
        }

        // The faulted read left the reader free, so another may start.
        reading = reader.ReadToEndAsync ();
        if (reading.IsCompleted) return 13;
        stream.Deliver ("");
        if (reading.Result != "") return 14;

        return 0;
    }
}
