using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// An await with `ConfigureAwait(false)` continues on the thread pool. On .NET 11,
// `StreamReader.ReadToEndAsyncInternal` awaits `ReadBufferAsync(cancellationToken)
// .ConfigureAwait(false)`, and the JIT calls the async variant of `ReadBufferAsync`; if it
// suspends, the continuation `ReadToEndAsyncInternal` makes carries
// `ContinuationFlags.ContinueOnThreadPool`. When `ReadBufferAsync` completes on a thread whose
// `SynchronizationContext` is not the default, CoreLib's dispatcher queues that continuation to
// the thread pool rather than run it under that context.
//
// The guest's decoder installs a context of its own on whichever thread decodes, which is the
// thread `ReadBufferAsync` resumes on, and the guest's stream records the context its next
// `ReadAsync` sees, which `ReadToEndAsyncInternal` calls through `ReadBufferAsync` once it has
// resumed. A continuation run under the decoder's context would see it; one queued to the thread
// pool sees none.
//
// .NET 10's state machines, and an await of the task the ordinary variant returns, continue on
// the thread pool too.
//
// The exit code is the index of the first check that failed; 0 means all passed.
public class Program
{
    private sealed class MarkingContext : SynchronizationContext
    {
    }

    private sealed class MarkingDecoder : Decoder
    {
        private readonly Decoder _inner;

        public static volatile bool Mark;

        public MarkingDecoder (Decoder inner)
        {
            _inner = inner;
        }

        private static void Touch ()
        {
            if (Mark) SynchronizationContext.SetSynchronizationContext (new MarkingContext ());
        }

        public override int GetCharCount (byte[] bytes, int index, int count)
        {
            Touch ();
            return _inner.GetCharCount (bytes, index, count);
        }

        public override int GetCharCount (ReadOnlySpan<byte> bytes, bool flush)
        {
            Touch ();
            return _inner.GetCharCount (bytes, flush);
        }

        public override int GetChars (byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            Touch ();
            return _inner.GetChars (bytes, byteIndex, byteCount, chars, charIndex);
        }

        public override int GetChars (byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex, bool flush)
        {
            Touch ();
            return _inner.GetChars (bytes, byteIndex, byteCount, chars, charIndex, flush);
        }

        public override int GetChars (ReadOnlySpan<byte> bytes, Span<char> chars, bool flush)
        {
            Touch ();
            return _inner.GetChars (bytes, chars, flush);
        }
    }

    private sealed class MarkingEncoding : UTF8Encoding
    {
        public MarkingEncoding () : base (false)
        {
        }

        public override Decoder GetDecoder () => new MarkingDecoder (base.GetDecoder ());
    }

    private sealed class PendingStream : Stream
    {
        private TaskCompletionSource<int> _pending;
        private Memory<byte> _destination;

        public int Requests;
        public SynchronizationContext LastContext;
        public readonly ManualResetEventSlim Requested = new ManualResetEventSlim (false);

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
            LastContext = SynchronizationContext.Current;
            _destination = buffer;
            _pending = new TaskCompletionSource<int> ();
            Requested.Set ();
            return new ValueTask<int> (_pending.Task);
        }

        public void Deliver (string text)
        {
            Requested.Reset ();
            byte[] bytes = Encoding.UTF8.GetBytes (text);
            bytes.CopyTo (_destination.Span);
            _pending.SetResult (bytes.Length);
        }
    }

    public static int Main (string[] args)
    {
        var stream = new PendingStream ();
        var reader = new StreamReader (stream, new MarkingEncoding (), false);

        Task<string> reading = reader.ReadToEndAsync ();
        if (reading.IsCompleted) return 1;
        if (stream.Requests != 1) return 2;
        if (stream.LastContext != null) return 3;

        MarkingDecoder.Mark = true;
        stream.Deliver ("hello");
        SynchronizationContext.SetSynchronizationContext (null);

        if (!stream.Requested.Wait (TimeSpan.FromMinutes (1))) return 4;
        MarkingDecoder.Mark = false;
        if (stream.Requests != 2) return 5;

        // `ReadToEndAsyncInternal` did not continue under the context the decoder installed.
        if (stream.LastContext is MarkingContext) return 6;

        stream.Deliver ("");
        if (reading.Result != "hello") return 7;

        return 0;
    }
}
