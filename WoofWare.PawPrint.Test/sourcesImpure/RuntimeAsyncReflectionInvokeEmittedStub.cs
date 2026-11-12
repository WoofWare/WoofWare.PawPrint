using System;
using System.Buffers;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

// `MethodInfo.Invoke` of CoreLib's runtime-async methods through the stub reflection emits, with
// dynamic code supported. A method's first reflective invocation goes through
// `RuntimeMethodHandle.InvokeMethod`; from the second onwards CoreLib's `MethodBaseInvoker` runs a
// `DynamicMethod` it emits, `InvokeStub_<Type>.<Method>`, which calls the method with a `call`.
// On .NET 11 that `call` binds the method's ordinary variant, which for a method flagged
// `MethodImplAttributes.Async` is the task-returning thunk: it runs the method's body, and turns
// whatever escapes it into the returned task's fault, so `Invoke` returns a faulted task rather
// than throwing `TargetInvocationException`. The thunk is left out of stack traces, as every IL
// stub is; the emitted stub is not.
//
// * `IndentedTextWriter.WriteAsync(string)` is an instance method returning `Task`, which awaits
//   the wrapped writer's `WriteAsync`.
// * `Stream`'s local function `FinishReadAsync` is static, returns `ValueTask<int>`, and awaits the
//   task it is given.
//
// The test harness registers this case with the dynamic-code switch set to "true", which is also
// real .NET's default; `sourcesPure/RuntimeAsyncReflectionInvoke.cs` is its counterpart without it.
// .NET 10 compiles the same methods as state machines, with the same results.
//
// The exit code is the index of the first check that failed; 0 means all passed.
public class Program
{
    // A writer whose `WriteAsync(string)` returns a task the guest completes, a completed task, or
    // throws.
    private sealed class PendingWriter : TextWriter
    {
        public TaskCompletionSource<object> Pending;
        public bool CompleteSynchronously;
        public Exception ThrowOnWrite;
        public string Written = "";
        public string CapturedStack;

        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteAsync (string value)
        {
            if (ThrowOnWrite != null) throw ThrowOnWrite;
            CapturedStack = Describe (new StackTrace (false).GetFrames ());
            Written += value;
            if (CompleteSynchronously) return Task.CompletedTask;
            Pending = new TaskCompletionSource<object> ();
            return Pending.Task;
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
    private const string WriteAsyncStubFrame = "::InvokeStub_IndentedTextWriter.WriteAsync\n";

    public static int Main (string[] args)
    {
        MethodInfo indentedWrite = typeof (IndentedTextWriter).GetMethod ("WriteAsync", new[] { typeof (string) });
        var inner = new PendingWriter ();
        var indented = new IndentedTextWriter (inner);

        // The first invocation, through the runtime's invoker.
        var write = (Task) indentedWrite.Invoke (indented, new object[] { "zero" });
        if (write.IsCompleted) return 1;
        if (Occurrences (inner.CapturedStack, WriteAsyncStubFrame) != 0) return 2;
        if (Occurrences (inner.CapturedStack, WriteAsyncFrame) != 1) return 3;
        inner.Pending.SetResult (null);
        if (!write.IsCompletedSuccessfully) return 4;

        // Through the emitted stub: suspends, then completes.
        write = (Task) indentedWrite.Invoke (indented, new object[] { "one" });
        if (write.IsCompleted) return 5;
        if (inner.Written != "zeroone") return 6;
        if (Occurrences (inner.CapturedStack, WriteAsyncStubFrame) != 1) return 7;
        if (Occurrences (inner.CapturedStack, WriteAsyncFrame) != 1) return 8;
        inner.Pending.SetResult (null);
        if (!write.IsCompletedSuccessfully) return 9;

        // Completes synchronously.
        inner.CompleteSynchronously = true;
        write = (Task) indentedWrite.Invoke (indented, new object[] { "two" });
        if (!write.IsCompletedSuccessfully) return 10;
        if (inner.Written != "zeroonetwo") return 11;

        // Faults before it suspends: the fault is the task's, and `Invoke` does not throw.
        var wrappedFailure = new FormatException ("the wrapped writer failed");
        inner.ThrowOnWrite = wrappedFailure;
        try
        {
            write = (Task) indentedWrite.Invoke (indented, new object[] { "three" });
        }
        catch (TargetInvocationException)
        {
            return 12;
        }

        if (!write.IsFaulted) return 13;
        if (!ReferenceEquals (FaultOf (write), wrappedFailure)) return 14;

        // A static method returning `ValueTask<int>`.
        MethodInfo finishRead = FinishReadAsync ();
        if (finishRead == null) return 15;
        byte[] destination = new byte[4];
        var read = (ValueTask<int>) finishRead.Invoke (null, new object[] { Task.FromResult (0), ArrayPool<byte>.Shared.Rent (16), new Memory<byte> (destination) });
        if (!read.IsCompletedSuccessfully) return 16;

        // Through the emitted stub: suspends, then completes with a value.
        byte[] rented = ArrayPool<byte>.Shared.Rent (16);
        rented[0] = 6;
        var reading = new TaskCompletionSource<int> ();
        read = (ValueTask<int>) finishRead.Invoke (null, new object[] { reading.Task, rented, new Memory<byte> (destination) });
        if (read.IsCompleted) return 17;
        reading.SetResult (1);
        if (!read.IsCompletedSuccessfully) return 18;
        if (read.Result != 1) return 19;
        if (destination[0] != 6) return 20;

        // Faults after suspending.
        reading = new TaskCompletionSource<int> ();
        read = (ValueTask<int>) finishRead.Invoke (null, new object[] { reading.Task, ArrayPool<byte>.Shared.Rent (16), new Memory<byte> (destination) });
        if (read.IsCompleted) return 21;
        var readFailure = new IOException ("the read failed");
        reading.SetException (readFailure);
        if (!read.IsFaulted) return 22;
        if (!ReferenceEquals (FaultOf (read.AsTask ()), readFailure)) return 23;

        return 0;
    }
}
