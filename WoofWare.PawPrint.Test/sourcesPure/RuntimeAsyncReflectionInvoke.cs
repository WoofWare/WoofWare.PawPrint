using System;
using System.Buffers;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// `MethodInfo.Invoke` of CoreLib's runtime-async methods. On .NET 11 reflection binds a method's
// ordinary variant, which for a method flagged `MethodImplAttributes.Async` is the task-returning
// thunk the runtime emits: it runs the method's body, and turns whatever escapes it into the
// returned task's fault, so `Invoke` returns a faulted task rather than throwing
// `TargetInvocationException`. The thunk is left out of stack traces, as every IL stub is.
//
// A `MethodInfo`'s first `Invoke` goes through `RuntimeMethodHandle.InvokeMethod`; later ones run a
// stub reflection emits, which calls the method with a `call`. Each method below is invoked several
// times, so both paths run.
//
// * `Stream`'s local function `FinishReadAsync` is static, returns `ValueTask<int>`, and awaits the
//   task it is given, so the guest decides whether it suspends and how it ends.
// * `Stream.FinishWriteAsync` is static and returns `Task`.
// * `IndentedTextWriter.WriteAsync(string)` is a public instance method, which awaits the wrapped
//   writer's `WriteAsync`. It is invoked both through its own `MethodInfo` and through
//   `TextWriter.WriteAsync(string)`'s, which reflection dispatches virtually to it.
// * `StreamReader.ReadAsyncInternal` is an instance method returning `ValueTask<int>`.
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

    private sealed class ThrowingStream : Stream
    {
        public Exception Failure;

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

        public override ValueTask<int> ReadAsync (Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw Failure;
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

    public static int Main (string[] args)
    {
        MethodInfo finishRead = FinishReadAsync ();
        if (finishRead == null) return 1;

        // Suspends at the await of a pending task, on the first invocation.
        byte[] rented = ArrayPool<byte>.Shared.Rent (16);
        rented[0] = 7;
        rented[1] = 8;
        byte[] destination = new byte[4];
        var reading = new TaskCompletionSource<int> ();
        var read = (ValueTask<int>) finishRead.Invoke (null, new object[] { reading.Task, rented, new Memory<byte> (destination) });
        if (read.IsCompleted) return 2;
        reading.SetResult (2);
        if (!read.IsCompleted) return 3;
        if (read.Result != 2) return 4;
        if (destination[0] != 7 || destination[1] != 8) return 5;

        // Completes synchronously.
        rented = ArrayPool<byte>.Shared.Rent (16);
        rented[0] = 9;
        read = (ValueTask<int>) finishRead.Invoke (null, new object[] { Task.FromResult (1), rented, new Memory<byte> (destination) });
        if (!read.IsCompletedSuccessfully) return 6;
        if (read.Result != 1) return 7;
        if (destination[0] != 9) return 8;

        // Faults after suspending: the task faults with the awaited task's exception object.
        rented = ArrayPool<byte>.Shared.Rent (16);
        reading = new TaskCompletionSource<int> ();
        read = (ValueTask<int>) finishRead.Invoke (null, new object[] { reading.Task, rented, new Memory<byte> (destination) });
        if (read.IsCompleted) return 9;
        var readFailure = new IOException ("the read failed");
        reading.SetException (readFailure);
        if (!read.IsFaulted) return 10;
        if (!ReferenceEquals (FaultOf (read.AsTask ()), readFailure)) return 11;

        // A static method returning `Task`, faulting before it suspends, on its first invocation:
        // the fault is the task's, and `Invoke` does not throw.
        MethodInfo finishWrite = typeof (Stream).GetMethod ("FinishWriteAsync", BindingFlags.NonPublic | BindingFlags.Static);
        if (finishWrite == null) return 12;
        var writeFailure = new InvalidOperationException ("the write failed");
        Task written;
        try
        {
            written = (Task) finishWrite.Invoke (null, new object[] { Task.FromException (writeFailure), ArrayPool<byte>.Shared.Rent (16) });
        }
        catch (TargetInvocationException)
        {
            return 13;
        }

        if (!written.IsFaulted) return 14;
        if (!ReferenceEquals (FaultOf (written), writeFailure)) return 15;

        var writing = new TaskCompletionSource ();
        written = (Task) finishWrite.Invoke (null, new object[] { writing.Task, ArrayPool<byte>.Shared.Rent (16) });
        if (written.IsCompleted) return 16;
        writing.SetResult ();
        if (!written.IsCompletedSuccessfully) return 17;

        // A public instance method, through its own `MethodInfo`. Its thunk is not on the stack, and
        // the method is, once.
        MethodInfo indentedWrite = typeof (IndentedTextWriter).GetMethod ("WriteAsync", new[] { typeof (string) });
        if (indentedWrite.DeclaringType != typeof (IndentedTextWriter)) return 18;
        var inner = new PendingWriter ();
        var indented = new IndentedTextWriter (inner);
        var write = (Task) indentedWrite.Invoke (indented, new object[] { "one" });
        if (write.IsCompleted) return 19;
        if (inner.Written != "one") return 20;
        if (Occurrences (inner.CapturedStack, "System.CodeDom.Compiler.IndentedTextWriter::WriteAsync\n") != 1) return 21;
        inner.Pending.SetResult (null);
        if (!write.IsCompletedSuccessfully) return 22;

        write = (Task) indentedWrite.Invoke (indented, new object[] { "two" });
        if (write.IsCompleted) return 23;
        inner.Pending.SetResult (null);
        if (!write.IsCompletedSuccessfully) return 24;

        var wrappedFailure = new FormatException ("the wrapped writer failed");
        inner.ThrowOnWrite = wrappedFailure;
        try
        {
            write = (Task) indentedWrite.Invoke (indented, new object[] { "three" });
        }
        catch (TargetInvocationException)
        {
            return 25;
        }

        if (!write.IsFaulted) return 26;
        if (!ReferenceEquals (FaultOf (write), wrappedFailure)) return 27;

        // The same method, reached by reflection's virtual dispatch from `TextWriter`'s `MethodInfo`.
        MethodInfo textWriterWrite = typeof (TextWriter).GetMethod ("WriteAsync", new[] { typeof (string) });
        inner = new PendingWriter ();
        indented = new IndentedTextWriter (inner);
        write = (Task) textWriterWrite.Invoke (indented, new object[] { "four" });
        if (write.IsCompleted) return 28;
        if (inner.Written != "four") return 29;
        if (Occurrences (inner.CapturedStack, "System.CodeDom.Compiler.IndentedTextWriter::WriteAsync\n") != 1) return 30;
        inner.Pending.SetResult (null);
        if (!write.IsCompletedSuccessfully) return 31;

        // An instance method returning `ValueTask<int>`, faulting before it suspends on its first
        // invocation, then completing synchronously.
        MethodInfo readInternal = typeof (StreamReader).GetMethod ("ReadAsyncInternal", BindingFlags.NonPublic | BindingFlags.Instance);
        if (readInternal == null) return 32;
        var streamFailure = new EndOfStreamException ("the stream failed");
        var failing = new StreamReader (new ThrowingStream { Failure = streamFailure });
        char[] chars = new char[8];
        ValueTask<int> charsRead;
        try
        {
            charsRead = (ValueTask<int>) readInternal.Invoke (failing, new object[] { new Memory<char> (chars), CancellationToken.None });
        }
        catch (TargetInvocationException)
        {
            return 33;
        }

        if (!charsRead.IsFaulted) return 34;
        if (!ReferenceEquals (FaultOf (charsRead.AsTask ()), streamFailure)) return 35;

        var reader = new StreamReader (new MemoryStream (Encoding.UTF8.GetBytes ("hi")));
        charsRead = (ValueTask<int>) readInternal.Invoke (reader, new object[] { new Memory<char> (chars), CancellationToken.None });
        if (!charsRead.IsCompletedSuccessfully) return 36;
        if (charsRead.Result != 2) return 37;
        if (chars[0] != 'h' || chars[1] != 'i') return 38;

        return 0;
    }
}
