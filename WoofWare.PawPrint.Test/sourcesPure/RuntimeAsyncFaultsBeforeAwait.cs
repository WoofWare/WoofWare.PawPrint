using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

// An exception escaping a runtime-async method before it suspends faults the task its ordinary
// variant returns, rather than leaving the call: on .NET 11 the task-returning thunk catches it and
// returns `AsyncHelpers.TaskFromException`, which cancels the task instead for an
// `OperationCanceledException`. `ThrowingStream.ReadAsync` throws synchronously from inside
// `StreamReader.ReadBufferAsync`, whose thunk faults its `ValueTask<int>`; `ReadToEndAsyncInternal`
// awaits that, rethrowing, and its own thunk faults the `Task<string>` the guest holds.
//
// .NET 10's state machines fault the task in the same way.
public class Program
{
    private sealed class ThrowingStream : Stream
    {
        private readonly Exception _toThrow;

        public ThrowingStream (Exception toThrow)
        {
            _toThrow = toThrow;
        }

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
            throw _toThrow;
        }
    }

    public static int Main (string[] args)
    {
        var thrown = new InvalidOperationException ("from ReadAsync");

        Task<string> faulted;
        try
        {
            faulted = new StreamReader (new ThrowingStream (thrown)).ReadToEndAsync ();
        }
        catch (Exception)
        {
            return 1;
        }

        if (!faulted.IsFaulted) return 2;
        if (faulted.Exception.InnerExceptions.Count != 1) return 3;
        if (!ReferenceEquals (faulted.Exception.InnerException, thrown)) return 4;

        try
        {
            faulted.GetAwaiter ().GetResult ();
            return 5;
        }
        catch (InvalidOperationException e) when (ReferenceEquals (e, thrown))
        {
        }

        var cancelled = new OperationCanceledException ("from ReadAsync");
        Task<string> cancelledTask = new StreamReader (new ThrowingStream (cancelled)).ReadToEndAsync ();
        if (!cancelledTask.IsCanceled) return 6;
        if (cancelledTask.Status != TaskStatus.Canceled) return 7;

        try
        {
            cancelledTask.GetAwaiter ().GetResult ();
            return 8;
        }
        catch (OperationCanceledException e) when (ReferenceEquals (e, cancelled))
        {
        }

        return 0;
    }
}
