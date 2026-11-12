using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

// An await of an `Async` method called by a `callvirt` that does not dispatch binds the method's
// async variant. On .NET 11, `XDocument.LoadAsync` returns the task-returning thunk of
// `LoadAsyncInternal`, which awaits `d.ReadContentFromAsync(reader, options, cancellationToken)
// .ConfigureAwait(false)` by `callvirt` of a method that is not virtual, so CoreCLR's JIT calls its
// async variant. That method awaits the overload taking no `LoadOptions`, which awaits the guest
// reader's `ReadAsync`, which the guest leaves pending and then faults.
//
// The three frames suspend into one chain of continuations, so the trace has them adjacent, with
// no awaiter between them, and `LoadAsyncInternal` reported at the IL offset of its `call Await`.
// Were the await of the task the ordinary variant returns, the awaiter's `GetResult` would come
// between the frames. Completed, the load returns the document.
//
// .NET 10 compiles the same methods as state machines, so there the guest checks the results alone.
//
// The exit code is the index of the first check that failed; 0 means all passed.
public class Program
{
    // A reader of `<root>`, whose first `ReadAsync` completes and whose second the guest completes
    // or faults.
    private sealed class PendingReader : XmlReader
    {
        private readonly NameTable _names = new NameTable ();
        private ReadState _state = ReadState.Initial;

        public TaskCompletionSource<bool> Pending;
        public int Reads;

        public override int AttributeCount => 0;
        public override string BaseURI => "";
        public override int Depth => 0;
        public override bool EOF => _state == ReadState.EndOfFile;
        public override bool IsEmptyElement => false;
        public override string LocalName => "root";
        public override string NamespaceURI => "";
        public override XmlNameTable NameTable => _names;
        public override XmlNodeType NodeType => _state == ReadState.Interactive ? XmlNodeType.Element : XmlNodeType.None;
        public override string Prefix => "";
        public override ReadState ReadState => _state;
        public override string Value => "";
        public override string GetAttribute (int i) => throw new ArgumentOutOfRangeException (nameof (i));
        public override string GetAttribute (string name) => null;
        public override string GetAttribute (string name, string namespaceURI) => null;
        public override string LookupNamespace (string prefix) => null;
        public override bool MoveToAttribute (string name) => false;
        public override bool MoveToAttribute (string name, string ns) => false;
        public override bool MoveToElement () => false;
        public override bool MoveToFirstAttribute () => false;
        public override bool MoveToNextAttribute () => false;
        public override bool Read () => throw new NotSupportedException ();
        public override bool ReadAttributeValue () => false;
        public override void ResolveEntity () => throw new NotSupportedException ();

        public override Task<bool> ReadAsync ()
        {
            Reads++;
            if (Reads == 1)
            {
                _state = ReadState.Interactive;
                return Task.FromResult (true);
            }

            Pending = new TaskCompletionSource<bool> ();
            return Pending.Task;
        }

        public void End ()
        {
            _state = ReadState.EndOfFile;
            Pending.SetResult (false);
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
        "System.Xml.Linq.XContainer::ReadContentFromAsync\n"
        + "System.Xml.Linq.XContainer::ReadContentFromAsync\n"
        + "System.Xml.Linq.XDocument::LoadAsyncInternal\n";

    private const string FormattedNet11 =
        "   at System.Xml.Linq.XContainer.ReadContentFromAsync(XmlReader r, CancellationToken cancellationToken)\n"
        + "   at System.Xml.Linq.XContainer.ReadContentFromAsync(XmlReader r, LoadOptions o, CancellationToken cancellationToken)\n"
        + "   at System.Xml.Linq.XDocument.LoadAsyncInternal(XmlReader reader, LoadOptions options, CancellationToken cancellationToken)\n";

    public static int Main (string[] args)
    {
        bool net11 = Environment.Version.Major >= 11;

        // The second read completes, at the end of the document.
        var reader = new PendingReader ();
        Task<XDocument> loading = XDocument.LoadAsync (reader, LoadOptions.None, CancellationToken.None);
        if (loading.IsCompleted) return 1;
        if (reader.Reads != 2) return 2;
        reader.End ();
        if (!loading.Wait (TimeSpan.FromMinutes (1))) return 3;
        if (loading.Result.Root.Name.LocalName != "root") return 4;

        // The second read faults.
        reader = new PendingReader ();
        loading = XDocument.LoadAsync (reader, LoadOptions.None, CancellationToken.None);
        if (loading.IsCompleted) return 5;
        var failure = new IOException ("the read failed");
        reader.Pending.SetException (failure);
        if (!ReferenceEquals (FaultOf (loading), failure)) return 6;

        if (net11)
        {
            StackFrame[] frames = new StackTrace (failure, false).GetFrames ();
            string described = Describe (frames);
            int at = described.IndexOf (ResumedNet11, StringComparison.Ordinal);
            if (at < 0) return 7;

            // The frame of `LoadAsyncInternal`, the last of the three, is at its `call Await`.
            int loadFrame = described.Substring (0, at + ResumedNet11.Length).Split ('\n').Length - 2;
            if (frames[loadFrame].GetILOffset () != 0x54) return 8;

            string formatted = new StackTrace (failure, false).ToString ().TrimEnd () + Environment.NewLine;
            if (formatted.IndexOf (FormattedNet11.Replace ("\n", Environment.NewLine), StringComparison.Ordinal) < 0) return 9;
        }

        return 0;
    }
}
