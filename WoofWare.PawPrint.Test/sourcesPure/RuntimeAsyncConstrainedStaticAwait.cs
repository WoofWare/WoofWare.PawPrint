using System;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// An await of a static virtual method through a `constrained.` prefix. On .NET 11,
// `TarWriter.WriteEntryAsync` of an entry whose data stream cannot seek reaches
// `TarHeader.WriteWithUnseekableDataStreamCoreAsync<AsyncReadWriteAdapter>`, an `Async` method, which
// awaits `constrained. !!TAdapter call IReadWriteAdapter::CopyToAsync` with `ConfigureAwait(false)`.
// The JIT binds the async variant of the abstract interface method, and the prefix resolves it to
// the variant of `AsyncReadWriteAdapter.CopyToAsync`: an async version, which tail-awaits
// `Stream.CopyToAsync`'s, which tail-awaits the `Async` local function `<CopyToAsync>g__Core|30_0`,
// which awaits the guest's `ReadAsync`.
//
// The guest leaves the read pending, then completes or faults it. Faulted, the exception crosses
// `Core`'s continuation and then `WriteWithUnseekableDataStreamCoreAsync`'s, with only the
// dispatcher's frames between them. Were the await of the `ValueTask` the ordinary
// `AsyncReadWriteAdapter.CopyToAsync` returns, an awaiter's `GetResult` would come between them.
//
// The formatted trace hides the awaiter's frames and the dispatcher's; among the frames, the last
// the formatted trace hides before `Core`, past the dispatcher's, is a
// `RuntimeAsyncTaskContinuation.GetResult`.
//
// .NET 10 compiles the same methods as state machines, so there the guest checks the results alone.
//
// The exit code is the index of the first check that failed; 0 means all passed.
public class Program
{
    private sealed class PendingReadStream : Stream
    {
        public TaskCompletionSource<int> Pending;
        public int Reads;

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
            Reads++;
            Pending = new TaskCompletionSource<int> ();
            return new ValueTask<int> (Pending.Task);
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

    private static bool IsDispatcher (StackFrame frame)
    {
        string name = frame.GetMethod ().Name;
        return name == "DispatchContinuations" || name == "ResumeTaskContinuation";
    }

    // Whether the frames before `resumed`, past the dispatcher's, end with the awaiter's `GetResult`.
    // All of these are hidden from a formatted trace.
    private static bool AwaiterResumes (StackFrame[] frames, int resumed)
    {
        int i = resumed - 1;
        while (i >= 0 && IsDispatcher (frames[i]))
            i--;

        if (i < 0) return false;
        var method = frames[i].GetMethod ();
        return method.DeclaringType.FullName == "System.Runtime.CompilerServices.RuntimeAsyncTaskContinuation" && method.Name == "GetResult";
    }

    // The method of the first frame after `frame` that is not the dispatcher's, as `Type::Method`.
    private static string NextPastDispatcher (StackFrame[] frames, int frame)
    {
        int i = frame + 1;
        while (i < frames.Length && IsDispatcher (frames[i]))
            i++;

        if (i == frames.Length) return null;
        var method = frames[i].GetMethod ();
        return method.DeclaringType + "::" + method.Name;
    }

    // The index of the frame of `<CopyToAsync>g__Core|30_0`; -1 if there is none.
    private static int CoreFrame (StackFrame[] frames)
    {
        for (int i = 0; i < frames.Length; i++)
        {
            if (frames[i].GetMethod ().Name == "<CopyToAsync>g__Core|30_0") return i;
        }

        return -1;
    }

    private const string FormattedNet11 =
        "   at System.IO.Stream.<CopyToAsync>g__Core|30_0(Stream source, Stream destination, Int32 bufferSize, CancellationToken cancellationToken)\n"
        + "   at System.Formats.Tar.TarHeader.WriteWithUnseekableDataStreamCoreAsync[TAdapter](TarEntryFormat format, Stream destinationStream, Memory`1 buffer, Boolean shouldAdvanceToEnd, CancellationToken cancellationToken)\n"
        + "   at System.Formats.Tar.TarWriter.WriteEntryCoreAsync[TAdapter](TarEntry entry, CancellationToken cancellationToken)\n"
        + "   at System.Runtime.CompilerServices.AsyncHelpers.RuntimeAsyncTask`1.DispatchContinuations()";

    public static int Main (string[] args)
    {
        bool net11 = Environment.Version.Major >= 11;
        var archive = new MemoryStream ();
        var writer = new TarWriter (archive, TarEntryFormat.Ustar, leaveOpen: true);

        // A read that completes, with no data: the entry is its header alone.
        var data = new PendingReadStream ();
        Task writing = writer.WriteEntryAsync (new UstarTarEntry (TarEntryType.RegularFile, "a") { DataStream = data });
        if (writing.IsCompleted) return 1;
        if (data.Reads != 1) return 2;
        data.Pending.SetResult (0);
        if (writing.Status != TaskStatus.RanToCompletion) return 3;
        if (data.Reads != 1) return 4;
        if (archive.Length != 512) return 5;
        if (Encoding.ASCII.GetString (archive.GetBuffer (), 0, 2) != "a\0") return 6;

        // A read that faults.
        data = new PendingReadStream ();
        writing = writer.WriteEntryAsync (new UstarTarEntry (TarEntryType.RegularFile, "b") { DataStream = data });
        if (writing.IsCompleted) return 7;
        data.Pending.SetException (new IOException ("the read failed"));
        if (!writing.IsFaulted) return 8;
        Exception fault = FaultOf (writing);
        if (fault is not IOException || fault.Message != "the read failed") return 9;
        if (data.Reads != 1) return 10;

        if (net11)
        {
            string formatted = new StackTrace (fault, false).ToString ().TrimEnd ();
            if (formatted != FormattedNet11.Replace ("\n", Environment.NewLine)) return 11;
            StackFrame[] frames = new StackTrace (fault, false).GetFrames ();
            int core = CoreFrame (frames);
            if (core < 0) return 12;
            if (!AwaiterResumes (frames, core)) return 13;
            // Only the dispatcher's frames between `Core` and the frame awaiting the adapter.
            if (NextPastDispatcher (frames, core) != "System.Formats.Tar.TarHeader::WriteWithUnseekableDataStreamCoreAsync") return 14;
        }

        return 0;
    }
}
