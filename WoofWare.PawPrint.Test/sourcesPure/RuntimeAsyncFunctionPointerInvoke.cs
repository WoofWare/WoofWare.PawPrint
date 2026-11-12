using System;
using System.Buffers;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

// CoreLib's runtime-async methods called through `RuntimeMethodHandle.GetFunctionPointer` and
// `calli`, and through an `[UnsafeAccessor]`. On .NET 11 each binds a method's ordinary variant,
// which for a method flagged `MethodImplAttributes.Async` is the task-returning thunk the runtime
// emits: it runs the method's body, and turns whatever escapes it into the returned task's fault.
// The thunk is left out of stack traces, as every IL stub is.
//
// * `Stream`'s local function `FinishReadAsync` is static, returns `ValueTask<int>`, and awaits the
//   task it is given, so the guest decides whether it suspends and how it ends.
// * `IndentedTextWriter.WriteAsync(string)` is an instance method returning `Task`, which awaits
//   the wrapped writer's `WriteAsync`; through a function pointer it takes its receiver as the
//   first argument.
// * `Stream.FinishWriteAsync` is static and returns `Task`, reached by an `[UnsafeAccessor]`.
//
// .NET 10 compiles the same methods as state machines, with the same results: the method that
// starts the state machine catches into the task too, and is on the stack once.
//
// The exit code is the index of the first check that failed; 0 means all passed.
public static unsafe class Program
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

    [UnsafeAccessor (UnsafeAccessorKind.StaticMethod, Name = "FinishWriteAsync")]
    private static extern Task FinishWriteAsync (Stream declaringType, Task writeTask, byte[] localBuffer);

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

    public static int Main (string[] args)
    {
        // A static method returning `ValueTask<int>`: suspends, then completes; then faults after
        // suspending.
        MethodInfo finishReadMethod = FinishReadAsync ();
        if (finishReadMethod == null) return 1;
        var finishRead = (delegate*<Task<int>, byte[], Memory<byte>, ValueTask<int>>) finishReadMethod.MethodHandle.GetFunctionPointer ();
        byte[] rented = ArrayPool<byte>.Shared.Rent (16);
        rented[0] = 3;
        byte[] destination = new byte[4];
        var reading = new TaskCompletionSource<int> ();
        ValueTask<int> read = finishRead (reading.Task, rented, destination);
        if (read.IsCompleted) return 2;
        reading.SetResult (1);
        if (!read.IsCompletedSuccessfully) return 3;
        if (read.Result != 1) return 4;
        if (destination[0] != 3) return 5;

        reading = new TaskCompletionSource<int> ();
        read = finishRead (reading.Task, ArrayPool<byte>.Shared.Rent (16), destination);
        if (read.IsCompleted) return 6;
        var readFailure = new IOException ("the read failed");
        reading.SetException (readFailure);
        if (!read.IsFaulted) return 7;
        if (!ReferenceEquals (FaultOf (read.AsTask ()), readFailure)) return 8;

        // Completes synchronously.
        rented = ArrayPool<byte>.Shared.Rent (16);
        rented[0] = 4;
        read = finishRead (Task.FromResult (1), rented, destination);
        if (!read.IsCompletedSuccessfully) return 9;
        if (destination[0] != 4) return 10;

        // An instance method, its receiver the first argument: suspends, then completes; then
        // faults before it suspends.
        var indentedWrite =
            (delegate*<IndentedTextWriter, string, Task>) typeof (IndentedTextWriter)
                .GetMethod ("WriteAsync", new[] { typeof (string) })
                .MethodHandle.GetFunctionPointer ();
        var inner = new PendingWriter ();
        var indented = new IndentedTextWriter (inner);
        Task write = indentedWrite (indented, "one");
        if (write.IsCompleted) return 11;
        if (inner.Written != "one") return 12;
        if (Occurrences (inner.CapturedStack, "System.CodeDom.Compiler.IndentedTextWriter::WriteAsync\n") != 1) return 13;
        inner.Pending.SetResult (null);
        if (!write.IsCompletedSuccessfully) return 14;

        var wrappedFailure = new FormatException ("the wrapped writer failed");
        inner.ThrowOnWrite = wrappedFailure;
        try
        {
            write = indentedWrite (indented, "two");
        }
        catch (FormatException)
        {
            return 15;
        }

        if (!write.IsFaulted) return 16;
        if (!ReferenceEquals (FaultOf (write), wrappedFailure)) return 17;

        // An `[UnsafeAccessor]` of a static method returning `Task`: faults synchronously, then
        // suspends and completes.
        var writeFailure = new InvalidOperationException ("the write failed");
        Task written = FinishWriteAsync (null, Task.FromException (writeFailure), ArrayPool<byte>.Shared.Rent (16));
        if (!written.IsFaulted) return 18;
        if (!ReferenceEquals (FaultOf (written), writeFailure)) return 19;

        var writing = new TaskCompletionSource ();
        written = FinishWriteAsync (null, writing.Task, ArrayPool<byte>.Shared.Rent (16));
        if (written.IsCompleted) return 20;
        writing.SetResult ();
        if (!written.IsCompletedSuccessfully) return 21;

        return 0;
    }
}
