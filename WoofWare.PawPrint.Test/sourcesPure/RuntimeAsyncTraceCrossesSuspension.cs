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
// Resuming the continuation CoreLib made for that task throws the exception, and CoreLib's
// dispatcher catches it, then has the runtime append the frame of `LoadIntoBufferAsyncCore`, which
// its continuation's `DiagnosticIP` names, to its trace. The method rethrows it at its await,
// appending no frame of its own, and again from its `catch`, and the dispatcher catches it once
// more. The trace names each method once, ending at the dispatcher, which a formatted trace shows
// although it hides its attribute-hidden frames, because it shows the last frame regardless.
//
// The first exception was never thrown, so its trace begins where the awaiter throws it. The second
// was thrown before the guest faulted the task with it, so its trace begins with that throw, and the
// awaiter's throw continues it after a boundary.
//
// .NET 10 compiles the same method as a state machine, whose `MoveNext` catches the exception itself,
// so there the trace ends at `MoveNext`. Which awaiter frames come before it depends on what the
// JIT inlined into it, so the guest checks only the end of the trace there; and formatting a trace
// that names a state machine stops under PawPrint on .NET 10 (see `RuntimeAsyncTraceHidesThunk.cs`),
// so it checks the frames alone.
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

    private const string AwaiterThrowsNet11 =
        "System.Runtime.ExceptionServices.ExceptionDispatchInfo::Throw\n"
        + "System.Runtime.CompilerServices.TaskAwaiter::ThrowForNonSuccess\n"
        + "System.Runtime.CompilerServices.TaskAwaiter::HandleNonSuccessAndDebuggerNotification\n"
        + "System.Runtime.CompilerServices.TaskAwaiter::ValidateEnd\n"
        + "System.Runtime.CompilerServices.RuntimeAsyncTaskContinuation::GetResult\n"
        + "System.Runtime.CompilerServices.RuntimeAsyncTaskContinuation+TaskContinuationResume::ResumeTaskContinuation\n"
        + "System.Runtime.CompilerServices.AsyncHelpers+RuntimeAsyncTask`1[T]::DispatchContinuations\n"
        + "System.Net.Http.HttpContent::LoadIntoBufferAsyncCore\n"
        + "System.Runtime.CompilerServices.AsyncHelpers+RuntimeAsyncTask`1[T]::DispatchContinuations\n";

    private const string CatchingNet10 = "System.Net.Http.HttpContent+<LoadIntoBufferAsyncCore>d__53::MoveNext\n";

    private const string FormattedNet11 =
        "   at System.Runtime.CompilerServices.RuntimeAsyncTaskContinuation.GetResult(Task task, Byte& result)\n"
        + "   at System.Net.Http.HttpContent.LoadIntoBufferAsyncCore(Task serializeToStreamTask, LimitArrayPoolWriteStream tempBuffer)\n"
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

    public static int Main (string[] args)
    {
        bool net11 = Environment.Version.Major >= 11;
        string newLine = Environment.NewLine;

        // Never thrown before the fault.
        var unthrown = new InvalidOperationException ("never thrown");
        Exception rethrown = Rethrown (unthrown);
        if (!ReferenceEquals (rethrown, unthrown)) return 1;

        StackFrame[] frames = new StackTrace (rethrown, false).GetFrames ();
        if (net11 ? Describe (frames) != AwaiterThrowsNet11 : !Describe (frames).EndsWith (CatchingNet10)) return 2;

        if (net11)
        {
            // The frame the runtime appended for the continuation is at the await.
            if (frames[7].GetILOffset () != 7) return 3;
            string formatted = new StackTrace (rethrown, false).ToString ().TrimEnd ();
            if (formatted != FormattedNet11.Replace ("\n", newLine)) return 4;
        }

        // Thrown before the fault.
        Exception thrown;
        try
        {
            Throwing ();
            return 5;
        }
        catch (InvalidOperationException e)
        {
            thrown = e;
        }

        rethrown = Rethrown (thrown);
        if (!ReferenceEquals (rethrown, thrown)) return 6;

        frames = new StackTrace (rethrown, false).GetFrames ();
        string throwing = "Program::Throwing\nProgram::Main\n";
        string described = Describe (frames);
        if (!described.StartsWith (throwing)) return 7;
        if (net11 ? described != throwing + AwaiterThrowsNet11 : !described.EndsWith (CatchingNet10)) return 8;

        if (net11)
        {
            string formatted = new StackTrace (rethrown, false).ToString ().TrimEnd ();
            string expected =
                "   at Program.Throwing()\n   at Program.Main(String[] args)\n--- End of stack trace from previous location ---\n"
                + FormattedNet11;
            if (formatted != expected.Replace ("\n", newLine)) return 9;
        }

        return 0;
    }
}
