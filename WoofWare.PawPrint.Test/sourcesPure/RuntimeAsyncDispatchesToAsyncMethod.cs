using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

// An await of a virtual method whose implementation on the receiver is an `Async` method. On
// .NET 11, `IndentedTextWriter.WriteAsync(string)` is an `Async` method, which awaits its own
// protected virtual `OutputTabsAsync()` by `callvirt` with `ConfigureAwait(false)` at IL_000C, and
// `OutputTabsAsync` is an `Async` method too, which awaits the inner writer's `WriteAsync(string)` of
// the tab string at IL_0023. The JIT binds the async variant of `OutputTabsAsync`, and dispatch on an
// `IndentedTextWriter` finds that same method's variant.
//
// The guest's inner writer leaves each write pending, then completes or faults it. Faulted, the
// exception crosses `OutputTabsAsync`'s continuation and then `WriteAsync`'s, which are adjacent in
// one chain: the trace has the two next to each other, with no awaiter between them. Were the await
// of the task the ordinary variant returns, the awaiter's `GetResult` and the dispatcher would come
// between the two.
//
// The formatted trace hides the awaiter's frames and the dispatcher's; among the frames, the last
// the formatted trace hides before `OutputTabsAsync`, past the dispatcher's, is a
// `RuntimeAsyncTaskContinuation.GetResult`.
//
// .NET 10 compiles the same methods as state machines, so there the guest checks the results alone.
//
// The exit code is the index of the first check that failed; 0 means all passed.
public class Program
{
    private sealed class PendingWriter : TextWriter
    {
        public readonly StringBuilder Written = new StringBuilder ();
        public TaskCompletionSource Pending;
        public int Writes;

        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteAsync (string value)
        {
            Writes++;
            Written.Append (value);
            Pending = new TaskCompletionSource ();
            return Pending.Task;
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
        "System.Runtime.CompilerServices.AsyncHelpers+RuntimeAsyncTask`1[T]::DispatchContinuations\n"
        + "System.CodeDom.Compiler.IndentedTextWriter::OutputTabsAsync\n"
        + "System.CodeDom.Compiler.IndentedTextWriter::WriteAsync\n";

    private const string FormattedNet11 =
        "   at System.CodeDom.Compiler.IndentedTextWriter.OutputTabsAsync()\n"
        + "   at System.CodeDom.Compiler.IndentedTextWriter.WriteAsync(String value)";

    public static int Main (string[] args)
    {
        bool net11 = Environment.Version.Major >= 11;

        // A write whose tab pends, then completes; then the text pends, then completes.
        var inner = new PendingWriter ();
        var indented = new IndentedTextWriter (inner, "--") { Indent = 1 };
        Task writing = indented.WriteAsync ("x");
        if (writing.IsCompleted) return 1;
        if (inner.Writes != 1) return 2;
        inner.Pending.SetResult ();
        if (writing.IsCompleted) return 3;
        if (inner.Writes != 2) return 4;
        inner.Pending.SetResult ();
        if (FaultOf (writing) != null) return 5;
        if (inner.Written.ToString () != "--x") return 6;

        // A write whose tab pends, then faults.
        inner = new PendingWriter ();
        indented = new IndentedTextWriter (inner, "--") { Indent = 1 };
        writing = indented.WriteAsync ("y");
        if (writing.IsCompleted) return 7;
        if (inner.Writes != 1) return 8;
        var failure = new IOException ("the write failed");
        inner.Pending.SetException (failure);
        if (!ReferenceEquals (FaultOf (writing), failure)) return 9;
        if (inner.Writes != 1) return 10;

        if (net11)
        {
            StackFrame[] frames = new StackTrace (failure, false).GetFrames ();
            if (!Describe (frames).EndsWith (ResumedNet11, StringComparison.Ordinal)) return 11;

            // `OutputTabsAsync` and `WriteAsync` are each at their `call Await`.
            if (frames[frames.Length - 2].GetILOffset () != 0x23) return 12;
            if (frames[frames.Length - 1].GetILOffset () != 0x0C) return 13;

            string formatted = new StackTrace (failure, false).ToString ().TrimEnd ();
            if (formatted != FormattedNet11.Replace ("\n", Environment.NewLine)) return 14;

            if (!AwaiterResumes (frames, frames.Length - 2)) return 15;
        }

        return 0;
    }
}
