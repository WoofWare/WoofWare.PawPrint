using System;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// An await of an async version called directly, whose IL ends in the `AsTask` tail shape. On .NET
// 11, `TarFile.CreateFromDirectoryAsync` returns the task-returning thunk of
// `CreateFromDirectoryInternalAsync`, which awaits `TarWriter.WriteEntryAsync(string, string,
// CancellationToken)` with `ConfigureAwait(false)` at IL_00AF, by a `callvirt` of a method that is
// not virtual. `WriteEntryAsync` is not flagged `Async`, so the JIT binds its async version, which
// runs the method's own IL. That IL ends `call ReadFileFromDiskAndWriteToArchiveStreamAsEntryCoreAsync;
// stloc; ldloca; call AsTask; ret` at IL_002E: the async version calls the async variant of the
// `ValueTask`-returning callee, and this await is never a tail await. If the callee suspends, the
// async version suspends too, with a continuation of its own flagged `ValueTaskAdaptedToTask`.
//
// The entry's header goes to the guest's stream, whose write pends and then faults. The exception
// crosses the callee's continuation, then `WriteEntryAsync`'s, then
// `CreateFromDirectoryInternalAsync`'s, all in one chain: the trace has the three adjacent, with
// `WriteEntryAsync` at the offset of its call and `CreateFromDirectoryInternalAsync` at that of its
// `call Await`. Were the await of the task the ordinary variant returns, `WriteEntryAsync` would
// have returned before anything suspended, leaving no frame, and an awaiter's `GetResult` would come
// between the other two. The guest also compares the formatted trace whole, and every frame with
// real .NET 11's, less the awaiter's frames, which depend on what the JIT inlined into them.
//
// .NET 10 compiles the same methods as state machines, so there the guest checks the results alone.
//
// The exit code is the index of the first check that failed; 0 means all passed.
public class Program
{
    private sealed class PendingWriteStream : Stream
    {
        private TaskCompletionSource _pending;

        public int Writes;

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

        public override Task FlushAsync (CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read (byte[] buffer, int offset, int count) => throw new NotSupportedException ();
        public override long Seek (long offset, SeekOrigin origin) => throw new NotSupportedException ();
        public override void SetLength (long value) => throw new NotSupportedException ();
        public override void Write (byte[] buffer, int offset, int count) => throw new NotSupportedException ();

        public override ValueTask WriteAsync (ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            _pending = new TaskCompletionSource ();
            return new ValueTask (_pending.Task);
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

    private static string Describe (StackFrame[] frames)
    {
        var text = new StringBuilder ();
        foreach (StackFrame frame in frames)
        {
            var method = frame.GetMethod ();
            text.Append (method.DeclaringType).Append ("::").Append (method.Name).Append ('\n');
        }

        return text.ToString ();
    }

    private const string ResumedNet11 =
        "System.Formats.Tar.TarWriter::ReadFileFromDiskAndWriteToArchiveStreamAsEntryCoreAsync\n"
        + "System.Formats.Tar.TarWriter::WriteEntryAsync\n"
        + "System.Formats.Tar.TarFile::CreateFromDirectoryInternalAsync\n";

    private const string FormattedNet11 =
        "   at System.Formats.Tar.TarHeader.WriteWithSeekableDataStreamCoreAsync[TAdapter](TarEntryFormat format, Stream archiveStream, Memory`1 buffer, CancellationToken cancellationToken)\n"
        + "   at System.Formats.Tar.TarHeader.WriteAsPaxCoreAsync[TAdapter](Stream archiveStream, Memory`1 buffer, CancellationToken cancellationToken)\n"
        + "   at System.Formats.Tar.TarWriter.WriteEntryCoreAsync[TAdapter](TarEntry entry, CancellationToken cancellationToken)\n"
        + "   at System.Formats.Tar.TarWriter.ReadFileFromDiskAndWriteToArchiveStreamAsEntryCoreAsync[TAdapter](String fullPath, String entryName, FileOptions fileOptions, CancellationToken cancellationToken)\n"
        + "   at System.Formats.Tar.TarWriter.WriteEntryAsync(String fileName, String entryName, CancellationToken cancellationToken)\n"
        + "   at System.Formats.Tar.TarFile.CreateFromDirectoryInternalAsync(String sourceDirectoryName, Stream destination, Boolean includeBaseDirectory, Boolean leaveOpen, TarWriterOptions options, CancellationToken cancellationToken)\n"
        + "   at System.Formats.Tar.TarFile.CreateFromDirectoryInternalAsync(String sourceDirectoryName, Stream destination, Boolean includeBaseDirectory, Boolean leaveOpen, TarWriterOptions options, CancellationToken cancellationToken)\n"
        + "   at System.Runtime.CompilerServices.AsyncHelpers.RuntimeAsyncTask`1.DispatchContinuations()\n";

    private static int IndexOfFrame (StackFrame[] frames, string name)
    {
        for (int i = 0; i < frames.Length; i++)
        {
            if (frames[i].GetMethod ().Name == name) return i;
        }

        return -1;
    }

    // Whether `frame` is one of the awaiter's, between its throw and the dispatcher's frames. Which
    // of these a trace has depends on what the JIT inlined into them.
    private static bool IsAwaiters (StackFrame frame)
    {
        string type = frame.GetMethod ().DeclaringType.FullName;
        return type == "System.Runtime.CompilerServices.TaskAwaiter"
            || type == "System.Runtime.CompilerServices.RuntimeAsyncTaskContinuation";
    }

    // The method of every frame but the awaiter's, as `Type::Method` lines.
    private static string DescribeWhole (StackFrame[] frames)
    {
        var text = new StringBuilder ();
        foreach (StackFrame frame in frames)
        {
            if (IsAwaiters (frame)) continue;
            var method = frame.GetMethod ();
            text.Append (method.DeclaringType).Append ("::").Append (method.Name).Append ('\n');
        }

        return text.ToString ();
    }

    private const string WholeNet11 =
        "System.Runtime.ExceptionServices.ExceptionDispatchInfo::Throw\n"
        + "System.Runtime.CompilerServices.RuntimeAsyncTaskContinuation+TaskContinuationResume::ResumeTaskContinuation\n"
        + "System.Runtime.CompilerServices.AsyncHelpers+RuntimeAsyncTask`1[T]::DispatchContinuations\n"
        + "System.Formats.Tar.TarHeader::WriteWithSeekableDataStreamCoreAsync\n"
        + "System.Formats.Tar.TarHeader::WriteAsPaxCoreAsync\n"
        + "System.Runtime.ExceptionServices.ExceptionDispatchInfo::Throw\n"
        + "System.Runtime.CompilerServices.RuntimeAsyncTaskContinuation+TaskContinuationResume::ResumeTaskContinuation\n"
        + "System.Runtime.CompilerServices.AsyncHelpers+RuntimeAsyncTask`1[T]::DispatchContinuations\n"
        + "System.Formats.Tar.TarWriter::WriteEntryCoreAsync\n"
        + "System.Runtime.CompilerServices.AsyncHelpers+RuntimeAsyncTask`1[T]::DispatchContinuations\n"
        + "System.Formats.Tar.TarWriter::ReadFileFromDiskAndWriteToArchiveStreamAsEntryCoreAsync\n"
        + "System.Formats.Tar.TarWriter::WriteEntryAsync\n"
        + "System.Formats.Tar.TarFile::CreateFromDirectoryInternalAsync\n"
        + "System.Runtime.ExceptionServices.ExceptionDispatchInfo::Throw\n"
        + "System.Formats.Tar.TarFile::CreateFromDirectoryInternalAsync\n"
        + "System.Runtime.CompilerServices.AsyncHelpers+RuntimeAsyncTask`1[T]::DispatchContinuations\n";

    public static int Main (string[] args)
    {
        bool net11 = Environment.Version.Major >= 11;

        string source = Path.Combine (Path.GetTempPath (), "RuntimeAsyncAsyncVersionAsTask-" + Environment.ProcessId);
        Directory.CreateDirectory (source);
        File.WriteAllText (Path.Combine (source, "entry.txt"), "hello");

        try
        {
            var stream = new PendingWriteStream ();
            Task creating = TarFile.CreateFromDirectoryAsync (source, stream, false);
            if (creating.IsCompleted) return 1;
            if (stream.Writes != 1) return 2;
            var failure = new IOException ("the write failed");
            stream.Fault (failure);
            if (!ReferenceEquals (FaultOf (creating), failure)) return 3;

            if (net11)
            {
                StackFrame[] frames = new StackTrace (failure, false).GetFrames ();
                if (!Describe (frames).Contains (ResumedNet11, StringComparison.Ordinal)) return 4;

                // `WriteEntryAsync` is at its call, `CreateFromDirectoryInternalAsync` at its
                // `call Await`.
                int writing = IndexOfFrame (frames, "WriteEntryAsync");
                if (frames[writing].GetILOffset () != 0x2E) return 5;
                if (frames[writing + 1].GetILOffset () != 0xAF) return 6;

                string formatted = new StackTrace (failure, false).ToString ().TrimEnd () + Environment.NewLine;
                if (formatted != FormattedNet11.Replace ("\n", Environment.NewLine)) return 7;

                if (DescribeWhole (frames) != WholeNet11) return 8;
            }

            return 0;
        }
        finally
        {
            Directory.Delete (source, true);
        }
    }
}
