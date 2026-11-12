using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// An await of an async version called directly, which tail-awaits its own callee. On .NET 11,
// `StreamWriter.WriteAsync(string)` with `AutoFlush` returns the task-returning thunk of
// `WriteAsyncInternal`, which awaits `FlushAsyncInternal(...).ConfigureAwait(false)` at IL_010B,
// inside the `try` of a `using`. `FlushAsyncInternal` is not flagged `Async`, so the JIT binds its
// async version, an async variant running the method's own IL. That IL ends
// `call <FlushAsyncInternal>g__Core|82_0; ret`, a tail shape whose callee returns what the method
// does: the async version calls the local function's async variant, and if that suspends, passes
// its continuation on as its own, leaving no frame of `FlushAsyncInternal` to resume.
//
// The local function awaits the guest's `WriteAsync`, which the guest leaves pending. Completed,
// the write lands. Faulted, the exception crosses the local function's continuation and then
// `WriteAsyncInternal`'s, which are adjacent in one chain: the trace has the local function and
// `WriteAsyncInternal` next to each other, with no frame of `FlushAsyncInternal` and no awaiter
// between them, and `WriteAsyncInternal` at the offset of its `call Await`. Were the await of the
// task the ordinary variant returns, the awaiter's `GetResult` would come between the two.
//
// Which of the awaiter's frames come before the local function depends on what the JIT inlined, so
// the guest checks that a frame of `RuntimeAsyncTaskContinuation.GetResult` comes last before it,
// and the trace from there onward.
//
// .NET 10 compiles the same methods as state machines, so there the guest checks the results alone.
//
// The exit code is the index of the first check that failed; 0 means all passed.
public class Program
{
    private sealed class PendingWriteStream : Stream
    {
        private TaskCompletionSource _pending;

        public readonly MemoryStream Written = new MemoryStream ();
        public int Writes;

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
            _pending = new TaskCompletionSource ();
            return new ValueTask (_pending.Task);
        }

        public void Complete () => _pending.SetResult ();

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
        "System.IO.StreamWriter::<FlushAsyncInternal>g__Core|82_0\n"
        + "System.IO.StreamWriter::WriteAsyncInternal\n"
        + "System.Runtime.CompilerServices.AsyncHelpers+RuntimeAsyncTask`1[T]::DispatchContinuations\n";

    private const string FormattedAwaiterNet11 = "   at System.Runtime.CompilerServices.RuntimeAsyncTaskContinuation.GetResult";

    private const string FormattedNet11 =
        "   at System.IO.StreamWriter.<FlushAsyncInternal>g__Core|82_0(Boolean flushStream, Boolean flushEncoder, CancellationToken cancellationToken)\n"
        + "   at System.IO.StreamWriter.WriteAsyncInternal(ReadOnlyMemory`1 source, Boolean appendNewLine, CancellationToken cancellationToken)\n"
        + "   at System.Runtime.CompilerServices.AsyncHelpers.RuntimeAsyncTask`1.DispatchContinuations()";

    public static int Main (string[] args)
    {
        bool net11 = Environment.Version.Major >= 11;
        var stream = new PendingWriteStream ();
        var writer = new StreamWriter (stream, new UTF8Encoding (false)) { AutoFlush = true };

        // A write that pends, then completes.
        Task writing = writer.WriteAsync ("hello");
        if (writing.IsCompleted) return 1;
        if (stream.Writes != 1) return 2;
        stream.Complete ();
        if (writing.Status != TaskStatus.RanToCompletion) return 3;
        if (Encoding.UTF8.GetString (stream.Written.ToArray ()) != "hello") return 4;

        // A write that pends, then faults.
        writing = writer.WriteAsync (", world");
        if (writing.IsCompleted) return 5;
        if (stream.Writes != 2) return 6;
        var failure = new IOException ("the write failed");
        stream.Fault (failure);
        if (!ReferenceEquals (FaultOf (writing), failure)) return 7;

        if (net11)
        {
            StackFrame[] frames = new StackTrace (failure, false).GetFrames ();
            if (!Describe (frames).EndsWith (ResumedNet11, StringComparison.Ordinal)) return 8;

            // The frame of `WriteAsyncInternal` is at its `call Await`.
            if (frames[frames.Length - 2].GetILOffset () != 0x10B) return 9;

            string newLine = Environment.NewLine;
            string formatted = new StackTrace (failure, false).ToString ().TrimEnd ();
            string ending = newLine + FormattedNet11.Replace ("\n", newLine);
            if (!formatted.EndsWith (ending, StringComparison.Ordinal)) return 10;

            string beforeEnding = formatted.Substring (0, formatted.Length - ending.Length);
            int lastLine = beforeEnding.LastIndexOf (newLine, StringComparison.Ordinal);
            string awaiter = lastLine < 0 ? beforeEnding : beforeEnding.Substring (lastLine + newLine.Length);
            if (!awaiter.StartsWith (FormattedAwaiterNet11, StringComparison.Ordinal)) return 11;
        }

        return 0;
    }
}
