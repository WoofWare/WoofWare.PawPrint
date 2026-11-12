using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Runtime-async methods that suspend at awaits inside `try` regions, and run the `finally` after
// resuming, whether the awaited call completed or faulted. On .NET 11, `StreamWriter.DisposeAsync`
// returns the task-returning thunk of `DisposeAsyncCore`, which awaits `FlushAsync()` in
// `try { ... } finally { CloseStreamFromDispose(...) }`, and that flush comes down to the local
// function `FlushAsyncInternal.Core`, which awaits the guest's `WriteAsync` outside any `try`.
// (Real .NET 11's await binds the async version of `FlushAsync`, which goes through
// `FlushAsyncInternalWithGuard`, awaiting inside a `using`, on the way.) The guest leaves the write
// pending, so the methods suspend, and later completes or faults it.
//
// Completed, the writer has written its bytes and closed the stream. Faulted, the exception crosses
// `Core`'s continuation, which has no handler, and the `finally` of each method awaiting it; the
// writer still closes the stream, and the task faults with the guest's exception object. Its trace
// names `Core` and, after it, `DisposeAsyncCore`.
//
// .NET 10 compiles the same methods as state machines, with the same results; its trace names
// each method's state machine.
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

    // The index of the first frame at or after `from` whose method's declaring type's name, or
    // whose method's name, contains `text`; -1 if none does.
    private static int FrameNaming (StackFrame[] frames, int from, string text)
    {
        for (int i = from; i < frames.Length; i++)
        {
            var method = frames[i].GetMethod ();
            if (method == null) continue;
            if (method.Name.Contains (text) || method.DeclaringType.Name.Contains (text)) return i;
        }

        return -1;
    }

    public static int Main (string[] args)
    {
        // The write completes after the methods have suspended.
        var completing = new PendingWriteStream ();
        var writer = new StreamWriter (completing, new UTF8Encoding (false));
        writer.Write ("hi");
        Task disposing = writer.DisposeAsync ().AsTask ();
        if (disposing.IsCompleted) return 1;
        if (completing.Writes != 1) return 2;
        if (completing.Disposals != 0) return 3;
        completing.Pending.SetResult ();
        if (FaultOf (disposing) != null) return 4;
        if (completing.Disposals != 1) return 5;
        if (Encoding.UTF8.GetString (completing.Written.ToArray ()) != "hi") return 6;

        // The write faults after the methods have suspended.
        var faulting = new PendingWriteStream ();
        writer = new StreamWriter (faulting, new UTF8Encoding (false));
        writer.Write ("hi");
        disposing = writer.DisposeAsync ().AsTask ();
        if (disposing.IsCompleted) return 7;
        if (faulting.Disposals != 0) return 8;
        var failure = new IOException ("the write failed");
        faulting.Pending.SetException (failure);
        Exception fault = FaultOf (disposing);
        if (!ReferenceEquals (fault, failure)) return 9;
        if (!disposing.IsFaulted) return 10;
        if (faulting.Disposals != 1) return 11;

        StackFrame[] frames = new StackTrace (fault, false).GetFrames ();
        int core = FrameNaming (frames, 0, "g__Core|");
        if (core < 0) return 12;
        if (FrameNaming (frames, core + 1, "DisposeAsyncCore") < 0) return 13;

        return 0;
    }
}
