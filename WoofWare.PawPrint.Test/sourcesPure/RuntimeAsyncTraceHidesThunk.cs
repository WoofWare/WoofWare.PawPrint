using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

// The stack trace of an exception thrown inside a runtime-async method names each method once. On
// .NET 11 the exception below crosses the async variants of `StreamReader.ReadBufferAsync` and
// `StreamReader.ReadToEndAsyncInternal`, and is caught by each one's task-returning thunk, an IL stub
// sharing the method's name, which the runtime leaves out of stack traces as it leaves out every IL
// stub (`MethodDesc::IsDiagnosticsHidden`). So does a stack captured live inside the same methods.
//
// The trace is formatted without source information (`new StackTrace (e, false)`). Formatted with
// it, as `Exception.StackTrace` formats it, every check below passes and `Main` returns 0 under
// PawPrint, but the run ends with exit code 2, on .NET 10 as on .NET 11.
//
// .NET 10's state machines give the same text for the exception: the trace formatter names a state
// machine's `MoveNext` by its method, and draws no line where an await raised the exception again.
// A live capture on .NET 10 names `ReadBufferAsync` twice, once for the method that starts its state
// machine and once for the `MoveNext` that machine is running, so the guest exits 7 there.
public class Program
{
    private sealed class ThrowingStream : Stream
    {
        // When set, `ReadAsync` captures the live stack instead of throwing, and reads end of stream.
        public string Captured;
        public bool Capture;

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
            if (!Capture) throw new InvalidOperationException ("from ReadAsync");
            Captured = new System.Diagnostics.StackTrace (false).ToString ();
            return new ValueTask<int> (0);
        }
    }

    private static int Occurrences (string text, string of)
    {
        int count = 0;
        for (int at = text.IndexOf (of, StringComparison.Ordinal); at >= 0; at = text.IndexOf (of, at + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    public static int Main (string[] args)
    {
        Task<string> faulted = new StreamReader (new ThrowingStream ()).ReadToEndAsync ();
        if (!faulted.IsFaulted) return 1;

        string trace = new System.Diagnostics.StackTrace (faulted.Exception.InnerException, false).ToString ().TrimEnd ();
        string newLine = Environment.NewLine;

        if (!trace.StartsWith ("   at Program.ThrowingStream.ReadAsync(", StringComparison.Ordinal)) return 2;
        int firstLineEnd = trace.IndexOf (newLine, StringComparison.Ordinal);
        if (firstLineEnd < 0) return 3;

        string expectedRest =
            "   at System.IO.StreamReader.ReadBufferAsync(CancellationToken cancellationToken)" + newLine
            + "   at System.IO.StreamReader.ReadToEndAsyncInternal(CancellationToken cancellationToken)";

        if (trace.Substring (firstLineEnd + newLine.Length) != expectedRest) return 4;

        // A stack captured live inside the same methods leaves the thunks out too.
        var capturing = new ThrowingStream { Capture = true };
        if (new StreamReader (capturing).ReadToEndAsync ().Result != "") return 5;
        if (Occurrences (capturing.Captured, "at Program.ThrowingStream.ReadAsync(") != 1) return 6;
        if (Occurrences (capturing.Captured, "at System.IO.StreamReader.ReadBufferAsync(") != 1) return 7;
        if (Occurrences (capturing.Captured, "at System.IO.StreamReader.ReadToEndAsyncInternal(") != 1) return 8;

        return 0;
    }
}
