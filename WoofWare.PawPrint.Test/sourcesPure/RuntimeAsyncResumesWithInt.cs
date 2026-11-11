using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// CoreLib's runtime-async methods suspending at awaits of tasks the guest has not yet completed,
// and resuming once it completes them. On .NET 11, `StreamReader.ReadAsync(Memory<char>)` returns
// the task-returning thunk of `ReadAsyncInternal` (`ValueTask<int>`), which awaits the thunk of
// `ReadBufferAsync` (`ValueTask<int>`), which awaits the guest's `ReadAsync`. Neither await lies
// in a `try` region.
//
// The guest hands the reader the two bytes of U+00E9 in two reads. The first decodes to no
// characters, so `ReadBufferAsync` loops and suspends a second time in the same frame, which
// it resumes twice. `ReadAsyncInternal` suspends once, and resumes with the `int` that
// `ReadBufferAsync` returned.
//
// An `AsyncLocal` set before the call must flow to the stream's second `ReadAsync`, which
// `ReadBufferAsync` makes only after it has resumed: the guest changes the value before it
// completes the first read, so it is the `ExecutionContext` the suspension captured, and not
// whatever the resuming thread has, that the second read sees.
//
// .NET 10 compiles the same methods as state machines, with the same results.
//
// The exit code is the index of the first check that failed; 0 means all passed.
public class Program
{
    private static readonly AsyncLocal<int> s_flowed = new AsyncLocal<int> ();

    private sealed class PendingStream : Stream
    {
        private readonly SemaphoreSlim _requested = new SemaphoreSlim (0);
        private readonly object _gate = new object ();
        private TaskCompletionSource<int> _pending;
        private Memory<byte> _destination;

        public int Requests;
        public int FlowedValueMismatches;

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
            if (s_flowed.Value != 42)
            {
                Interlocked.Increment (ref FlowedValueMismatches);
            }

            var pending = new TaskCompletionSource<int> ();
            lock (_gate)
            {
                _pending = pending;
                _destination = buffer;
                Requests++;
            }

            _requested.Release ();
            return new ValueTask<int> (pending.Task);
        }

        // Wait until the reader has asked for bytes, then hand it `bytes`.
        public void Deliver (byte[] bytes)
        {
            _requested.Wait ();
            TaskCompletionSource<int> pending;
            lock (_gate)
            {
                pending = _pending;
                bytes.CopyTo (_destination.Span);
                _pending = null;
            }

            pending.SetResult (bytes.Length);
        }
    }

    public static int Main (string[] args)
    {
        var stream = new PendingStream ();
        var reader = new StreamReader (stream, new UTF8Encoding (false), false);
        char[] chars = new char[16];

        s_flowed.Value = 42;
        ValueTask<int> read = reader.ReadAsync (chars.AsMemory ());
        if (read.IsCompleted) return 1;
        if (stream.Requests != 1) return 2;

        s_flowed.Value = 7;

        stream.Deliver (new byte[] { 0xC3 });
        if (read.IsCompleted) return 3;

        stream.Deliver (new byte[] { 0xA9, (byte) '!' });

        int count = read.AsTask ().Result;
        if (count != 2) return 4;
        if (chars[0] != 'é') return 5;
        if (chars[1] != '!') return 6;
        if (stream.Requests != 2) return 7;
        if (stream.FlowedValueMismatches != 0) return 8;
        if (s_flowed.Value != 7) return 9;

        return 0;
    }
}
