using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

// A chain of runtime-async methods, of every result kind, suspending at awaits of reads the guest
// has not yet completed, and resuming once it completes them. On .NET 11,
// `ZipArchiveEntry.OpenAsync` reaches the task-returning thunk of `OpenInReadModeAsyncCore`
// (`Task<Stream>`), which awaits `ThrowIfNotOpenableAsync` (`Task`), which awaits
// `IsOpenableAsync`, a `Task<(bool, string)>`: a struct holding a reference. That awaits
// `ZipLocalFileHeader.TrySkipBlockAsync` (`Task<bool>`), which awaits reads of the archive's
// stream (`int`), and `OpenInReadModeAsyncCore` then awaits `GetOffsetOfCompressedDataAsync`
// (`Task<long>`). None of those awaits lies in a `try` region.
//
// The archive's directory is read synchronously until the guest makes its stream's asynchronous reads
// pending; from then on the guest completes each read the methods ask for, one at a time,
// until the entry's stream is open.
//
// .NET 10 compiles the same methods as state machines, with the same results.
//
// The exit code is the index of the first check that failed; 0 means all passed.
public class Program
{
    private sealed class PendingStream : Stream
    {
        private readonly Stream _inner;
        private readonly object _gate = new object ();
        private TaskCompletionSource _arrived = new TaskCompletionSource (TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<int> _pending;
        private Memory<byte> _destination;

        public bool PendingReads;
        public int Requests;

        public PendingStream (Stream inner)
        {
            _inner = inner;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush ()
        {
        }

        public override int Read (byte[] buffer, int offset, int count) => _inner.Read (buffer, offset, count);
        public override long Seek (long offset, SeekOrigin origin) => _inner.Seek (offset, origin);
        public override void SetLength (long value) => throw new NotSupportedException ();
        public override void Write (byte[] buffer, int offset, int count) => throw new NotSupportedException ();

        public override Task<int> ReadAsync (byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync (buffer.AsMemory (offset, count), cancellationToken).AsTask ();

        public override ValueTask<int> ReadAsync (Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!PendingReads)
            {
                return new ValueTask<int> (_inner.Read (buffer.Span));
            }

            var pending = new TaskCompletionSource<int> ();
            lock (_gate)
            {
                _pending = pending;
                _destination = buffer;
                Requests++;
                _arrived.SetResult ();
            }

            return new ValueTask<int> (pending.Task);
        }

        // Completes once a read is waiting for `CompleteRead`.
        public Task Arrived
        {
            get
            {
                lock (_gate)
                {
                    return _arrived.Task;
                }
            }
        }

        public void CompleteRead ()
        {
            TaskCompletionSource<int> pending;
            Memory<byte> destination;
            lock (_gate)
            {
                pending = _pending;
                destination = _destination;
                _pending = null;
                _arrived = new TaskCompletionSource (TaskCreationOptions.RunContinuationsAsynchronously);
            }

            pending.SetResult (_inner.Read (destination.Span));
        }
    }

    // One entry, `greeting.txt`, stored uncompressed and holding "hello, world". Written out rather
    // than made with `ZipArchiveMode.Create`, which computes the entry's CRC-32 through zlib.
    private static readonly byte[] StoredArchive =
    {
        0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x21, 0x5C, 0x3A, 0x72,
        0xAB, 0xFF, 0x0C, 0x00, 0x00, 0x00, 0x0C, 0x00, 0x00, 0x00, 0x0C, 0x00, 0x00, 0x00, 0x67, 0x72,
        0x65, 0x65, 0x74, 0x69, 0x6E, 0x67, 0x2E, 0x74, 0x78, 0x74, 0x68, 0x65, 0x6C, 0x6C, 0x6F, 0x2C,
        0x20, 0x77, 0x6F, 0x72, 0x6C, 0x64, 0x50, 0x4B, 0x01, 0x02, 0x14, 0x00, 0x14, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x21, 0x5C, 0x3A, 0x72, 0xAB, 0xFF, 0x0C, 0x00, 0x00, 0x00, 0x0C, 0x00,
        0x00, 0x00, 0x0C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x01,
        0x00, 0x00, 0x00, 0x00, 0x67, 0x72, 0x65, 0x65, 0x74, 0x69, 0x6E, 0x67, 0x2E, 0x74, 0x78, 0x74,
        0x50, 0x4B, 0x05, 0x06, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x3A, 0x00, 0x00, 0x00,
        0x36, 0x00, 0x00, 0x00, 0x00, 0x00,
    };

    public static int Main (string[] args)
    {
        var archiveBytes = new MemoryStream (StoredArchive, writable: false);
        var stream = new PendingStream (archiveBytes);
        using var archive = new ZipArchive (stream, ZipArchiveMode.Read);
        ZipArchiveEntry entry = archive.GetEntry ("greeting.txt");
        if (entry == null) return 1;

        stream.PendingReads = true;
        Task<Stream> opening = entry.OpenAsync ();
        if (opening.IsCompleted) return 2;

        while (true)
        {
            Task arrived = stream.Arrived;
            Task.WaitAny (arrived, opening);
            if (!arrived.IsCompleted) break;
            stream.CompleteRead ();
        }

        if (!opening.IsCompletedSuccessfully) return 3;
        if (stream.Requests < 2) return 4;

        // Reading the entry would check its CRC-32 through zlib, so the guest only checks that it
        // was handed a readable stream.
        Stream opened = opening.Result;
        if (opened == null) return 5;
        if (!opened.CanRead) return 6;

        return 0;
    }
}
