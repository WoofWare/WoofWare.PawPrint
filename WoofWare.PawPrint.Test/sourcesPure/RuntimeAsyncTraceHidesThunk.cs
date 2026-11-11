using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

// The stack trace of an exception thrown inside a runtime-async method names each method once. On
// .NET 11 the exception below crosses the async variants of `StreamReader.ReadBufferAsync` and
// `StreamReader.ReadToEndAsyncInternal`, and is caught by each one's task-returning thunk, an IL stub
// sharing the method's name, which the runtime leaves out of stack traces as it leaves out every IL
// stub (`MethodDesc::IsDiagnosticsHidden`).
//
// .NET 10's state machines give the same text: the trace formatter names a state machine's
// `MoveNext` by its method, and draws no line where an await raised the exception again.
public class Program
{
    private sealed class ThrowingStream : Stream
    {
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
            throw new InvalidOperationException ("from ReadAsync");
        }
    }

    public static int Main (string[] args)
    {
        Task<string> faulted = new StreamReader (new ThrowingStream ()).ReadToEndAsync ();
        if (!faulted.IsFaulted) return 1;

        string trace = faulted.Exception.InnerException.StackTrace;
        string newLine = Environment.NewLine;

        if (!trace.StartsWith ("   at Program.ThrowingStream.ReadAsync(", StringComparison.Ordinal)) return 2;
        int firstLineEnd = trace.IndexOf (newLine, StringComparison.Ordinal);
        if (firstLineEnd < 0) return 3;

        string expectedRest =
            "   at System.IO.StreamReader.ReadBufferAsync(CancellationToken cancellationToken)" + newLine
            + "   at System.IO.StreamReader.ReadToEndAsyncInternal(CancellationToken cancellationToken)";

        if (trace.Substring (firstLineEnd + newLine.Length) != expectedRest) return 4;

        return 0;
    }
}
