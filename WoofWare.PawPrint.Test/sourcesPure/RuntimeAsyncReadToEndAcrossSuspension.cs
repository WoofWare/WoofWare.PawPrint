using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// `StreamReader.ReadToEndAsync` across reads the guest leaves pending. On .NET 11 it returns the
// task-returning thunk of `ReadToEndAsyncInternal`, which awaits `ReadBufferAsync` inside the `try`
// of a `using` (the scope that marks the reader busy), and `ReadBufferAsync` awaits the guest's
// `ReadAsync` outside any `try`. Both suspend at each pending read.
//
// Completed, the reader returns the text, and leaves itself free for the next read. Faulted, the
// exception crosses `ReadBufferAsync`'s continuation, which has no handler, then the `using` of
// `ReadToEndAsyncInternal`, whose `finally` frees the reader, and faults the task with the guest's
// exception object.
//
// .NET 10 compiles the same methods as state machines, with the same results.
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

    public static int Main (string[] args)
    {
        var stream = new PendingStream ();
        var reader = new StreamReader (stream, new UTF8Encoding (false), false);

        // Two reads complete, then one reads end of stream.
        Task<string> reading = reader.ReadToEndAsync ();
        if (reading.IsCompleted) return 1;
        if (stream.Requests != 1) return 2;
        stream.Deliver ("hello, ");
        if (reading.IsCompleted) return 3;
        if (stream.Requests != 2) return 4;
        stream.Deliver ("world");
        if (reading.IsCompleted) return 5;
        stream.Deliver ("");
        if (reading.Result != "hello, world") return 6;

        // A read faults: the reader is freed, and the task faults with the guest's exception.
        reading = reader.ReadToEndAsync ();
        if (reading.IsCompleted) return 7;
        stream.Deliver ("partial");
        if (reading.IsCompleted) return 8;
        var failure = new IOException ("the read failed");
        stream.Fault (failure);
        if (!ReferenceEquals (FaultOf (reading), failure)) return 9;
        if (!reading.IsFaulted) return 10;

        // The faulted read left the reader free, so another may start.
        reading = reader.ReadToEndAsync ();
        if (reading.IsCompleted) return 11;
        stream.Deliver ("");
        if (reading.Result != "") return 12;

        return 0;
    }
}
