using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

// An await of an async version called directly, which suspends itself rather than tail-awaiting.
// On .NET 11, an asynchronous `XmlReader` over a stream whose XML declaration names an encoding
// other than the one the reader detected switches encodings: `ParseXmlDeclarationAsync` awaits
// `SwitchEncodingAsync(...).ConfigureAwait(false)` at IL_024F. `SwitchEncodingAsync` is not
// flagged `Async`, so the JIT binds its async version, which runs the method's own IL. That IL ends
// `call ReadDataAsync; ret` at IL_0051, a tail shape, but `ReadDataAsync` returns a `Task<int>` and
// `SwitchEncodingAsync` a `Task`, so the call's await is not a tail await: if `ReadDataAsync`
// suspends, the async version suspends too, with a continuation of its own, and when it resumes it
// drops the `int` and returns.
//
// `ReadDataAsync` reads the guest's stream, which hands over the declaration and then leaves the
// next read pending. Completed, the document parses. Faulted, the exception crosses
// `ReadDataAsync`'s continuation, then `SwitchEncodingAsync`'s, then `ParseXmlDeclarationAsync`'s,
// all in one chain: the trace has the three adjacent, with `SwitchEncodingAsync` at the offset of
// its call. Were the await of the task
// the ordinary variant returns, `SwitchEncodingAsync` would have returned before anything
// suspended, leaving no frame, and an awaiter's `GetResult` would come between the other two.
// The formatted trace hides the awaiter's frames and the dispatcher's, so it begins with the three;
// among the frames, the last it hides before them, past the dispatcher's, is a
// `RuntimeAsyncTaskContinuation.GetResult`.
//
// .NET 10 compiles the same methods as state machines, so there the guest checks the results alone.
//
// The exit code is the index of the first check that failed; 0 means all passed.
public class Program
{
    private sealed class PendingReadStream : Stream
    {
        private readonly byte[] _declaration;
        private TaskCompletionSource<int> _pending;
        private Memory<byte> _destination;

        public int Reads;

        public PendingReadStream (string declaration)
        {
            _declaration = Encoding.ASCII.GetBytes (declaration);
        }

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

        public override Task<int> ReadAsync (byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync (new Memory<byte> (buffer, offset, count), cancellationToken).AsTask ();

        public override ValueTask<int> ReadAsync (Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;

            if (Reads == 1)
            {
                _declaration.CopyTo (buffer);
                return new ValueTask<int> (_declaration.Length);
            }

            // The end of the stream, once the pending read has completed.
            if (Reads > 2) return new ValueTask<int> (0);

            _destination = buffer;
            _pending = new TaskCompletionSource<int> ();
            return new ValueTask<int> (_pending.Task);
        }

        public void Deliver (string text)
        {
            byte[] bytes = Encoding.Latin1.GetBytes (text);
            bytes.CopyTo (_destination.Span);
            _pending.SetResult (bytes.Length);
        }

        public void Fault (Exception e) => _pending.SetException (e);
    }

    private const string Declaration = "<?xml version=\"1.0\" encoding=\"iso-8859-1\"?>";

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
        "System.Xml.XmlTextReaderImpl::ReadDataAsync\n"
        + "System.Xml.XmlTextReaderImpl::SwitchEncodingAsync\n"
        + "System.Xml.XmlTextReaderImpl::ParseXmlDeclarationAsync\n";

    private const string FormattedNet11 =
        "   at System.Xml.XmlTextReaderImpl.ReadDataAsync()\n"
        + "   at System.Xml.XmlTextReaderImpl.SwitchEncodingAsync(Encoding newEncoding)\n"
        + "   at System.Xml.XmlTextReaderImpl.ParseXmlDeclarationAsync(Boolean isTextDecl)\n";

    private static int IndexOfFrame (StackFrame[] frames, string name)
    {
        for (int i = 0; i < frames.Length; i++)
        {
            if (frames[i].GetMethod ().Name == name) return i;
        }

        return -1;
    }

    public static int Main (string[] args)
    {
        bool net11 = Environment.Version.Major >= 11;
        var settings = new XmlReaderSettings { Async = true };

        // The read after the declaration pends, then completes with the rest of the document.
        var stream = new PendingReadStream (Declaration);
        XmlReader reader = XmlReader.Create (stream, settings);
        Task<bool> reading = reader.ReadAsync ();
        if (reading.IsCompleted) return 1;
        if (stream.Reads != 2) return 2;
        stream.Deliver ("<doc>caf\u00e9</doc>");
        if (!reading.IsCompleted || !reading.Result) return 3;
        if (reader.NodeType != XmlNodeType.XmlDeclaration) return 4;
        if (!reader.ReadAsync ().Result || reader.Name != "doc") return 5;
        if (!reader.ReadAsync ().Result || reader.Value != "caf\u00e9") return 6;
        if (!reader.ReadAsync ().Result || reader.NodeType != XmlNodeType.EndElement) return 7;
        if (reader.ReadAsync ().Result) return 8;

        // The read after the declaration pends, then faults.
        stream = new PendingReadStream (Declaration);
        reader = XmlReader.Create (stream, settings);
        reading = reader.ReadAsync ();
        if (reading.IsCompleted) return 9;
        if (stream.Reads != 2) return 10;
        var failure = new IOException ("the read failed");
        stream.Fault (failure);
        if (!ReferenceEquals (FaultOf (reading), failure)) return 11;

        if (net11)
        {
            StackFrame[] frames = new StackTrace (failure, false).GetFrames ();
            if (!Describe (frames).Contains (ResumedNet11, StringComparison.Ordinal)) return 12;

            // `SwitchEncodingAsync` is at its call. Real .NET 11 reports `ParseXmlDeclarationAsync`
            // at IL_024F, where a statement begins, rather than at its `call Await` at IL_026F,
            // which is the offset the JIT's first tier reports and PawPrint does (decision 20), so
            // the guest does not check it.
            int switching = IndexOfFrame (frames, "SwitchEncodingAsync");
            if (frames[switching].GetILOffset () != 0x51) return 13;

            string formatted = new StackTrace (failure, false).ToString ();
            if (!formatted.StartsWith (FormattedNet11.Replace ("\n", Environment.NewLine), StringComparison.Ordinal)) return 14;

            if (!AwaiterResumes (frames, IndexOfFrame (frames, "ReadDataAsync"))) return 15;
        }

        return 0;
    }
}
