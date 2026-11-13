using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

// A `ret` of an async version awaits the promise it returns as a tail await, leaving no frame of
// the async version to resume. On .NET 11, an asynchronous `XmlWriter`'s `FlushAsync` reaches
// `XmlUtf8RawTextWriter.FlushAsync`, which awaits `_stream.FlushAsync().ConfigureAwait(false)` at
// IL_0030. `Stream.FlushAsync()` is neither virtual nor flagged `Async`, so the JIT binds its async
// version, whose IL is `callvirt FlushAsync(CancellationToken); ret`: the guest's override returns
// a pending task, and the `ret` awaits it through `AsyncHelpers.TransparentAwait`. When the task
// faults, the frames have `XmlUtf8RawTextWriter.FlushAsync` straight after the awaiter's
// `GetResult` and the dispatcher's frames, with no frame of `Stream.FlushAsync`; the formatted trace
// hides the awaiter's and the dispatcher's, so it begins with `XmlUtf8RawTextWriter.FlushAsync`.
//
// The guest's checks hold whether or not the await binds the async version, since an await of the
// task the ordinary variant returns leaves no frame of `Stream.FlushAsync` either; they fail where
// the async version's `ret` suspends it with a continuation of its own.
//
// .NET 10 compiles the same methods as state machines, so there the guest checks the results alone.
//
// The exit code is the index of the first check that failed; 0 means all passed.
public class Program
{
    private sealed class PendingFlushStream : Stream
    {
        private TaskCompletionSource _pending;

        public readonly MemoryStream Written = new MemoryStream ();
        public int Flushes;

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
        public override void Write (byte[] buffer, int offset, int count) => Written.Write (buffer, offset, count);

        public override Task WriteAsync (byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Written.Write (buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync (ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Written.Write (buffer.Span);
            return default;
        }

        public override Task FlushAsync (CancellationToken cancellationToken)
        {
            Flushes++;
            _pending = new TaskCompletionSource ();
            return _pending.Task;
        }

        public void Complete () => _pending.SetResult ();

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

    // Whether the frames before `resumed`, past the dispatcher's, end with the awaiter's `GetResult`.
    // All of these are hidden from a formatted trace.
    private static bool AwaiterResumes (StackFrame[] frames, int resumed)
    {
        int i = resumed - 1;
        while (i >= 0 && (frames[i].GetMethod ().Name == "DispatchContinuations" || frames[i].GetMethod ().Name == "ResumeTaskContinuation"))
            i--;

        if (i < 0) return false;
        var method = frames[i].GetMethod ();
        return method.DeclaringType.FullName == "System.Runtime.CompilerServices.RuntimeAsyncTaskContinuation" && method.Name == "GetResult";
    }

    private static int IndexOfFrame (StackFrame[] frames, string typeName, string name)
    {
        for (int i = 0; i < frames.Length; i++)
        {
            var method = frames[i].GetMethod ();
            if (method.DeclaringType.FullName == typeName && method.Name == name) return i;
        }

        return -1;
    }

    public static int Main (string[] args)
    {
        bool net11 = Environment.Version.Major >= 11;
        var settings = new XmlWriterSettings { Async = true, Encoding = new UTF8Encoding (false) };

        // A flush that pends, then completes.
        var stream = new PendingFlushStream ();
        XmlWriter writer = XmlWriter.Create (stream, settings);
        writer.WriteStartElement ("doc");
        Task flushing = writer.FlushAsync ();
        if (flushing.IsCompleted) return 1;
        if (stream.Flushes != 1) return 2;
        if (Encoding.UTF8.GetString (stream.Written.ToArray ()) != "<?xml version=\"1.0\" encoding=\"utf-8\"?><doc") return 3;
        stream.Complete ();
        if (flushing.Status != TaskStatus.RanToCompletion) return 4;

        // A flush that pends, then faults.
        stream = new PendingFlushStream ();
        writer = XmlWriter.Create (stream, settings);
        writer.WriteStartElement ("doc");
        flushing = writer.FlushAsync ();
        if (flushing.IsCompleted) return 5;
        var failure = new IOException ("the flush failed");
        stream.Fault (failure);
        if (!ReferenceEquals (FaultOf (flushing), failure)) return 6;

        if (net11)
        {
            StackFrame[] frames = new StackTrace (failure, false).GetFrames ();
            if (IndexOfFrame (frames, "System.IO.Stream", "FlushAsync") >= 0) return 7;

            int flushingWriter = IndexOfFrame (frames, "System.Xml.XmlUtf8RawTextWriter", "FlushAsync");
            if (flushingWriter < 1) return 8;
            if (frames[flushingWriter].GetILOffset () != 0x30) return 9;

            string formatted = new StackTrace (failure, false).ToString ();
            if (!formatted.StartsWith ("   at System.Xml.XmlUtf8RawTextWriter.FlushAsync()" + Environment.NewLine, StringComparison.Ordinal))
                return 10;

            if (!AwaiterResumes (frames, flushingWriter)) return 11;
        }

        return 0;
    }
}
