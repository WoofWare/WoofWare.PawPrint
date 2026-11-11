using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

// `RuntimeHelpers.IsRuntimeAsync()` answers whether the method calling it has runtime async's
// calling convention, which the JIT folds at the call (`compIsAsync`). `StreamWriter.FlushAsync`,
// whose ordinary variant runs here, asks it, and keeps the flush's task in `_asyncWriteTask` only
// when the answer is false; the async version of the same IL, which the JIT compiles only for a
// runtime-async caller's await, does not. The flush faults, so that its task is not the
// `Task.CompletedTask` the field starts out holding.
//
// .NET 10's `FlushAsync` keeps the task without asking.
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
        public override void Write (byte[] buffer, int offset, int count) => throw new NotSupportedException ();

        public override ValueTask WriteAsync (ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException ("from WriteAsync");
    }

    public static int Main (string[] args)
    {
        var writer = new StreamWriter (new ThrowingStream ());
        writer.Write ('x');

        Task flush = writer.FlushAsync ();
        if (!flush.IsFaulted) return 1;

        object kept = typeof (StreamWriter)
            .GetField ("_asyncWriteTask", BindingFlags.NonPublic | BindingFlags.Instance)
            .GetValue (writer);

        if (!ReferenceEquals (kept, flush)) return 2;

        return 0;
    }
}
