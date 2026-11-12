using System;
using System.Buffers;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

// Delegates over CoreLib's runtime-async methods. On .NET 11 a delegate binds a method's ordinary
// variant, which for a method flagged `MethodImplAttributes.Async` is the task-returning thunk the
// runtime emits: it runs the method's body, and turns whatever escapes it into the returned task's
// fault. The thunk is left out of stack traces, as every IL stub is.
//
// * A method group over a virtual method (`ldvirtftn`, then the delegate's constructor), and one
//   over a base method (`ldftn`): `IndentedTextWriter.WriteAsync(string)` and
//   `OutputTabsAsync()`, instance methods returning `Task`, which await the wrapped writer.
// * `MethodInfo.CreateDelegate` over a static method: `Stream`'s local function `FinishReadAsync`,
//   which returns `ValueTask<int>` and awaits the task it is given, and `Stream.FinishWriteAsync`.
// * `Delegate.CreateDelegate` closed over an instance, and open over `TextWriter.WriteAsync(string)`,
//   which dispatches virtually to `IndentedTextWriter`'s.
// * A multicast delegate of two such methods, which runs both and returns the last one's task.
//
// .NET 10 compiles the same methods as state machines, with the same results: the method that
// starts the state machine catches into the task too, and is on the stack once.
//
// The exit code is the index of the first check that failed; 0 means all passed.
public class Program
{
    // A writer whose `WriteAsync(string)` returns a task the guest completes, or throws.
    private sealed class PendingWriter : TextWriter
    {
        public TaskCompletionSource<object> Pending;
        public Exception ThrowOnWrite;
        public string Written = "";
        public string CapturedStack;

        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteAsync (string value)
        {
            if (ThrowOnWrite != null) throw ThrowOnWrite;
            CapturedStack ??= Describe (new StackTrace (false).GetFrames ());
            Written += value;
            Pending = new TaskCompletionSource<object> ();
            return Pending.Task;
        }
    }

    private sealed class Indenting : IndentedTextWriter
    {
        public Indenting (TextWriter writer) : base (writer)
        {
        }

        public Func<string, Task> BaseWrite () => base.WriteAsync;

        public Func<Task> BaseTabs () => base.OutputTabsAsync;
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

    private static int Occurrences (string text, string of)
    {
        int count = 0;
        for (int at = text.IndexOf (of, StringComparison.Ordinal); at >= 0; at = text.IndexOf (of, at + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
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

    private static MethodInfo FinishReadAsync ()
    {
        foreach (MethodInfo method in typeof (Stream).GetMethods (BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (method.Name.StartsWith ("<ReadAsync>g__FinishReadAsync|", StringComparison.Ordinal)) return method;
        }

        return null;
    }

    private const string WriteAsyncFrame = "System.CodeDom.Compiler.IndentedTextWriter::WriteAsync\n";

    public static int Main (string[] args)
    {
        // A method group over a virtual method: suspends, then completes.
        var inner = new PendingWriter ();
        var indenting = new Indenting (inner);
        Func<string, Task> virtualWrite = indenting.WriteAsync;
        Task write = virtualWrite ("one");
        if (write.IsCompleted) return 1;
        if (inner.Written != "one") return 2;
        if (Occurrences (inner.CapturedStack, WriteAsyncFrame) != 1) return 3;
        inner.Pending.SetResult (null);
        if (!write.IsCompletedSuccessfully) return 4;

        // Faults before it suspends: the fault is the task's.
        var wrappedFailure = new FormatException ("the wrapped writer failed");
        inner.ThrowOnWrite = wrappedFailure;
        Task faulted;
        try
        {
            faulted = virtualWrite ("two");
        }
        catch (FormatException)
        {
            return 5;
        }

        if (!faulted.IsFaulted) return 6;
        if (!ReferenceEquals (FaultOf (faulted), wrappedFailure)) return 7;
        inner.ThrowOnWrite = null;

        // A method group over the base method.
        inner = new PendingWriter ();
        indenting = new Indenting (inner);
        Func<string, Task> baseWrite = indenting.BaseWrite ();
        write = baseWrite ("three");
        if (write.IsCompleted) return 8;
        if (Occurrences (inner.CapturedStack, WriteAsyncFrame) != 1) return 9;
        inner.Pending.SetResult (null);
        if (!write.IsCompletedSuccessfully) return 10;

        // A base method with indentation pending: it suspends at each tab it writes.
        inner = new PendingWriter ();
        indenting = new Indenting (inner) { Indent = 2 };
        Task tabs = indenting.BaseTabs () ();
        if (tabs.IsCompleted) return 11;
        if (inner.Written != "    ") return 12;
        inner.Pending.SetResult (null);
        if (tabs.IsCompleted) return 13;
        if (inner.Written != "        ") return 14;
        inner.Pending.SetResult (null);
        if (!tabs.IsCompletedSuccessfully) return 15;

        // A static method returning `ValueTask<int>`, bound by `CreateDelegate`.
        MethodInfo finishRead = FinishReadAsync ();
        if (finishRead == null) return 16;
        var finishReadDelegate = finishRead.CreateDelegate<Func<Task<int>, byte[], Memory<byte>, ValueTask<int>>> ();
        byte[] rented = ArrayPool<byte>.Shared.Rent (16);
        rented[0] = 5;
        byte[] destination = new byte[4];
        var reading = new TaskCompletionSource<int> ();
        ValueTask<int> read = finishReadDelegate (reading.Task, rented, destination);
        if (read.IsCompleted) return 17;
        reading.SetResult (1);
        if (!read.IsCompletedSuccessfully) return 18;
        if (read.Result != 1) return 19;
        if (destination[0] != 5) return 20;

        reading = new TaskCompletionSource<int> ();
        read = finishReadDelegate (reading.Task, ArrayPool<byte>.Shared.Rent (16), destination);
        var readFailure = new IOException ("the read failed");
        reading.SetException (readFailure);
        if (!read.IsFaulted) return 21;
        if (!ReferenceEquals (FaultOf (read.AsTask ()), readFailure)) return 22;

        // A static method returning `Task`, faulting synchronously.
        MethodInfo finishWrite = typeof (Stream).GetMethod ("FinishWriteAsync", BindingFlags.NonPublic | BindingFlags.Static);
        if (finishWrite == null) return 23;
        var finishWriteDelegate = (Func<Task, byte[], Task>) Delegate.CreateDelegate (typeof (Func<Task, byte[], Task>), finishWrite);
        var writeFailure = new InvalidOperationException ("the write failed");
        Task written = finishWriteDelegate (Task.FromException (writeFailure), ArrayPool<byte>.Shared.Rent (16));
        if (!written.IsFaulted) return 24;
        if (!ReferenceEquals (FaultOf (written), writeFailure)) return 25;

        // `Delegate.CreateDelegate` closed over an instance.
        MethodInfo indentedWrite = typeof (IndentedTextWriter).GetMethod ("WriteAsync", new[] { typeof (string) });
        inner = new PendingWriter ();
        var indented = new IndentedTextWriter (inner);
        var closed = (Func<string, Task>) Delegate.CreateDelegate (typeof (Func<string, Task>), indented, indentedWrite);
        write = closed ("four");
        if (write.IsCompleted) return 26;
        if (Occurrences (inner.CapturedStack, WriteAsyncFrame) != 1) return 27;
        inner.Pending.SetResult (null);
        if (!write.IsCompletedSuccessfully) return 28;

        // Open over the virtual method, dispatching to `IndentedTextWriter`'s.
        MethodInfo textWriterWrite = typeof (TextWriter).GetMethod ("WriteAsync", new[] { typeof (string) });
        var open = (Func<TextWriter, string, Task>) Delegate.CreateDelegate (typeof (Func<TextWriter, string, Task>), null, textWriterWrite);
        inner = new PendingWriter ();
        write = open (new IndentedTextWriter (inner), "five");
        if (write.IsCompleted) return 29;
        if (inner.Written != "five") return 30;
        if (Occurrences (inner.CapturedStack, WriteAsyncFrame) != 1) return 31;
        inner.Pending.SetResult (null);
        if (!write.IsCompletedSuccessfully) return 32;

        // Multicast: both run, in order, and the last one's task is returned.
        var first = new PendingWriter ();
        var second = new PendingWriter ();
        Func<string, Task> both = new IndentedTextWriter (first).WriteAsync;
        both += new IndentedTextWriter (second).WriteAsync;
        write = both ("six");
        if (first.Written != "six" || second.Written != "six") return 33;
        first.Pending.SetResult (null);
        if (write.IsCompleted) return 34;
        second.Pending.SetResult (null);
        if (!write.IsCompletedSuccessfully) return 35;

        return 0;
    }
}
