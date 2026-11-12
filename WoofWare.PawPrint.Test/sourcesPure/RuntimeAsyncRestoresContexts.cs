using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

// A change to the thread's `ExecutionContext` (through an `AsyncLocal`) or `SynchronizationContext`
// made inside a runtime-async method that completes synchronously does not outlive the call. On
// .NET 11 the async variant of a method flagged `MethodImplAttributes.Async` captures both contexts
// on entry (`AsyncHelpers.CaptureContexts`) and puts them back on return and when an exception
// leaves it (`AsyncHelpers.RestoreContexts`). `ContextChangingStream.ReadAsync` is called from
// inside `StreamReader.ReadBufferAsync`, and changes both.
//
// .NET 10's `AsyncMethodBuilderCore.Start` restores both in the same way.
public class Program
{
    private static readonly AsyncLocal<int> Local = new AsyncLocal<int> ();

    private sealed class ContextChangingStream : Stream
    {
        private readonly bool _throw;

        public ContextChangingStream (bool shouldThrow)
        {
            _throw = shouldThrow;
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
            Local.Value = 42;
            SynchronizationContext.SetSynchronizationContext (new SynchronizationContext ());

            if (_throw)
            {
                throw new InvalidOperationException ("from ReadAsync");
            }

            // End of stream.
            return new ValueTask<int> (0);
        }
    }

    public static int Main (string[] args)
    {
        Local.Value = 7;

        Task<string> completed = new StreamReader (new ContextChangingStream (false)).ReadToEndAsync ();
        if (!completed.IsCompletedSuccessfully) return 1;
        if (completed.Result != "") return 2;
        if (Local.Value != 7) return 3;
        if (SynchronizationContext.Current != null) return 4;

        Task<string> faulted = new StreamReader (new ContextChangingStream (true)).ReadToEndAsync ();
        if (!faulted.IsFaulted) return 5;
        if (Local.Value != 7) return 6;
        if (SynchronizationContext.Current != null) return 7;

        return 0;
    }
}
