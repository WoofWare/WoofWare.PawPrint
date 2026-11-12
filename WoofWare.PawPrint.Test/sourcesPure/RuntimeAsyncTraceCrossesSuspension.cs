using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

// The stack trace of an exception that a resumed runtime-async frame rethrows. On .NET 11,
// `HttpContent.LoadIntoBufferAsync` returns the task-returning thunk of `LoadIntoBufferAsyncCore`,
// which awaits the task the guest's `SerializeToStreamAsync` returned, at IL offset 7, inside
// `try { ... } catch (Exception e)`, whose handler rethrows anything but an `IOException`. The
// guest leaves the task pending, so the method suspends, and then faults it.
//
// Resuming the continuation CoreLib made for that task throws the exception from the awaiter, and
// CoreLib's dispatcher catches it, then has the runtime append the frame of
// `LoadIntoBufferAsyncCore`, which its continuation's `DiagnosticIP` names, to its trace. The method
// rethrows it at its await, appending no frame of its own, and again from its `catch`, and the
// dispatcher catches it once more. So the trace ends with the dispatcher, the method at its await,
// and the dispatcher again, and a formatted trace shows that last frame, although the dispatcher
// is hidden from traces, because it shows the last frame regardless.
//
// Which of the awaiter's frames come before that depends on what the JIT inlined: .NET 11's inlines
// `RuntimeAsyncTaskContinuation.GetResult(ref byte)`, which PawPrint, inlining nothing, shows. So the
// guest checks the trace from the dispatcher's first catch onward, and how it begins.
//
// The first exception was never thrown, so its trace begins where the awaiter throws it. The second
// was thrown before the guest faulted the task with it, so its trace begins with that throw, and the
// awaiter's throw continues it after a boundary.
//
// .NET 10 compiles the same method as a state machine, whose `MoveNext` catches the exception itself,
// so there the trace ends at `MoveNext`. Formatting a trace that names a state machine stops under
// PawPrint on .NET 10 (see `RuntimeAsyncTraceHidesThunk.cs`), so there the guest checks the frames
// alone.
//
// The exit code is the index of the first check that failed; 0 means all passed.
public class Program
{
    private sealed class PendingContent : HttpContent
    {
        public readonly TaskCompletionSource Pending = new TaskCompletionSource ();

        protected override Task SerializeToStreamAsync (Stream stream, TransportContext context) => Pending.Task;

        protected override bool TryComputeLength (out long length)
        {
            length = 0;
            return false;
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

    private static void Throwing () => throw new InvalidOperationException ("thrown before the fault");

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

    private static int Occurrences (string text, string of)
    {
        int count = 0;
        for (int at = text.IndexOf (of, StringComparison.Ordinal); at >= 0; at = text.IndexOf (of, at + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private const string AwaiterThrows = "System.Runtime.ExceptionServices.ExceptionDispatchInfo::Throw\n";

    private const string ResumedNet11 =
        "System.Runtime.CompilerServices.AsyncHelpers+RuntimeAsyncTask`1[T]::DispatchContinuations\n"
        + "System.Net.Http.HttpContent::LoadIntoBufferAsyncCore\n"
        + "System.Runtime.CompilerServices.AsyncHelpers+RuntimeAsyncTask`1[T]::DispatchContinuations\n";

    private const string CatchingNet10 = "System.Net.Http.HttpContent+<LoadIntoBufferAsyncCore>d__53::MoveNext\n";

    private const string FormattedAwaiterNet11 =
        "   at System.Runtime.CompilerServices.RuntimeAsyncTaskContinuation.GetResult(Task task, Byte& result)\n";

    private const string FormattedResumedNet11 =
        "   at System.Net.Http.HttpContent.LoadIntoBufferAsyncCore(Task serializeToStreamTask, LimitArrayPoolWriteStream tempBuffer)\n"
        + "   at System.Runtime.CompilerServices.AsyncHelpers.RuntimeAsyncTask`1.DispatchContinuations()";

    // The exception `fault` of a pending serialisation, as `LoadIntoBufferAsync` faults with it.
    private static Exception Rethrown (Exception fault)
    {
        var content = new PendingContent ();
        Task loading = content.LoadIntoBufferAsync ();
        if (loading.IsCompleted) return null;
        content.Pending.SetException (fault);
        return FaultOf (loading);
    }

    // 0 if the trace of `rethrown` begins with `before` and then the awaiter's throw, formatted as
    // `formattedBefore` and then the awaiter's first visible frame, and ends as the guest expects;
    // otherwise the index of the first check that failed, counting from `first`.
    private static int Check (Exception rethrown, string before, string formattedBefore, int first)
    {
        bool net11 = Environment.Version.Major >= 11;

        StackFrame[] frames = new StackTrace (rethrown, false).GetFrames ();
        string described = Describe (frames);
        if (!described.StartsWith (before + AwaiterThrows, StringComparison.Ordinal)) return first;
        if (!described.EndsWith (net11 ? ResumedNet11 : CatchingNet10, StringComparison.Ordinal)) return first + 1;

        if (net11)
        {
            if (Occurrences (described, "LoadIntoBufferAsyncCore") != 1) return first + 2;

            // The frame the runtime appended for the continuation is at the await.
            if (frames[frames.Length - 2].GetILOffset () != 7) return first + 3;

            string newLine = Environment.NewLine;
            string formatted = new StackTrace (rethrown, false).ToString ().TrimEnd ();
            string begins = (formattedBefore + FormattedAwaiterNet11).Replace ("\n", newLine);
            if (!formatted.StartsWith (begins, StringComparison.Ordinal)) return first + 4;
            if (!formatted.EndsWith (FormattedResumedNet11.Replace ("\n", newLine), StringComparison.Ordinal)) return first + 5;
        }

        return 0;
    }

    public static int Main (string[] args)
    {
        // Never thrown before the fault.
        var unthrown = new InvalidOperationException ("never thrown");
        Exception rethrown = Rethrown (unthrown);
        if (!ReferenceEquals (rethrown, unthrown)) return 1;
        int failed = Check (rethrown, "", "", 2);
        if (failed != 0) return failed;

        // Thrown before the fault.
        Exception thrown;
        try
        {
            Throwing ();
            return 8;
        }
        catch (InvalidOperationException e)
        {
            thrown = e;
        }

        rethrown = Rethrown (thrown);
        if (!ReferenceEquals (rethrown, thrown)) return 9;

        return Check (
            rethrown,
            "Program::Throwing\nProgram::Main\n",
            "   at Program.Throwing()\n   at Program.Main(String[] args)\n--- End of stack trace from previous location ---\n",
            10);
    }
}
