using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

// How .NET 11's CoreLib reads and writes a runtime-async continuation's slots and a task's result,
// with CoreLib's internal types and accessors copied (`Continuation` in `AsyncHelpers.CoreCLR.cs`,
// `RuntimeAsyncTaskContinuation`):
//
// * `Unsafe.As<byte, X>(ref Unsafe.Add(ref RuntimeHelpers.GetRawData(this), offset))`, the offset
//   decoded from `Flags`, to read the execution context and continuation context and to write the
//   exception;
// * `GetResultStorageOrNull`, a `ref byte` to the result slot or a null ref;
// * `GetResult<T>`, reached through a `delegate*<Task, ref byte, void>` instance field set to
//   `&GetResult<T>`, writing `Unsafe.As<byte, T>(ref result) = value`, into another continuation's
//   result slot and into `Task<T>.m_result` (as `Unsafe.As<T?, byte>(ref m_result)`, which is
//   `RuntimeAsyncTask<T>.GetResultStorage`).
//
// `RuntimeHelpers.GetRawData` and `Task<T>.m_result` are internal, so they are reached through
// `[UnsafeAccessor]`. A result type holding references, wider than a pointer, checks that a write
// through the byte view moves every field, and leaves the slots beside it alone.
[Flags]
public enum ContinuationFlags
{
    ExecutionContextIndexFirstBit = 4,
    ExecutionContextIndexNumBits = 2,
    ContinuationContextIndexFirstBit = 6,
    ContinuationContextIndexNumBits = 2,
    ExceptionIndexFirstBit = 8,
    ExceptionIndexNumBits = 3,
    ResultIndexFirstBit = 11,
    ResultIndexNumBits = 21,
}

public unsafe struct ResumeInfo
{
    public delegate*<Continuation, ref byte, Continuation?> Resume;
    public void* DiagnosticIP;
}

public unsafe class Continuation
{
    public Continuation? Next;
    public ResumeInfo* ResumeInfo;
    public ContinuationFlags Flags;
    public int State;

    private const int PointerSize = 8;

    private const int DataOffset = PointerSize /* Next */ + PointerSize /* Resume */ + 8 /* Flags + State */;

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "GetRawData")]
    public static extern ref byte GetRawData(
        [UnsafeAccessorType("System.Runtime.CompilerServices.RuntimeHelpers, System.Private.CoreLib")] object? declaringType,
        object obj);

    public unsafe object GetContinuationContext()
    {
        const uint mask = (1u << (int)ContinuationFlags.ContinuationContextIndexNumBits) - 1;
        uint index = ((uint)Flags >> (int)ContinuationFlags.ContinuationContextIndexFirstBit) & mask;
        ref byte data = ref GetRawData(null, this);
        return Unsafe.As<byte, object>(ref Unsafe.Add(ref data, (DataOffset - PointerSize) + index * PointerSize));
    }

    public bool HasException()
    {
        const uint mask = (1u << (int)ContinuationFlags.ExceptionIndexNumBits) - 1;
        return ((uint)Flags & (mask << (int)ContinuationFlags.ExceptionIndexFirstBit)) != 0;
    }

    public unsafe bool TryGetExecutionContext(out ExecutionContext? execContext)
    {
        const uint mask = (1u << (int)ContinuationFlags.ExecutionContextIndexNumBits) - 1;
        uint index = ((uint)Flags >> (int)ContinuationFlags.ExecutionContextIndexFirstBit) & mask;
        if (index == 0)
        {
            execContext = null;
            return false;
        }

        ref byte data = ref GetRawData(null, this);
        execContext = Unsafe.As<byte, ExecutionContext?>(ref Unsafe.Add(ref data, (DataOffset - PointerSize) + index * PointerSize));
        return true;
    }

    public void SetException(Exception ex)
    {
        const uint mask = (1u << (int)ContinuationFlags.ExceptionIndexNumBits) - 1;
        uint index = ((uint)Flags >> (int)ContinuationFlags.ExceptionIndexFirstBit) & mask;
        ref byte data = ref GetRawData(null, this);
        Unsafe.As<byte, Exception>(ref Unsafe.Add(ref data, (DataOffset - PointerSize) + index * PointerSize)) = ex;
    }

    public ref byte GetResultStorageOrNull()
    {
        const uint mask = (1u << (int)ContinuationFlags.ResultIndexNumBits) - 1;
        uint index = ((uint)Flags >> (int)ContinuationFlags.ResultIndexFirstBit) & mask;
        if (index == 0)
            return ref Unsafe.NullRef<byte>();
        ref byte data = ref GetRawData(null, this);
        return ref Unsafe.Add(ref data, (DataOffset - PointerSize) + index * PointerSize);
    }
}

// The data a JIT'd continuation carries, at the offsets its `Flags` encode: execution context at
// index 1, continuation context at 2, exception at 3, result at 4. CoreCLR lays a class's reference
// fields out first and in declaration order, so these are at raw-data offsets 24, 32, 40 and 48,
// which `Program.CheckLayout` confirms.
public sealed class JitContinuation<T> : Continuation
{
    public ExecutionContext? ExecContext;
    public object? ContinuationContext;
    public Exception? Exception;
    public T Result = default!;

    public JitContinuation(bool hasResult)
    {
        Flags = (ContinuationFlags)((1 << 4) | (2 << 6) | (3 << 8) | ((hasResult ? 4 : 0) << 11));
    }
}

public sealed unsafe class TaskContinuation : Continuation
{
    internal Task? Task;
    private delegate*<Task, ref byte, void> _getResult;

    public void GetResult(ref byte returnValue)
    {
        Task task = Task!;
        Task = null;
        _getResult(task, ref returnValue);
    }

    public void Initialize(Task task)
    {
        Task = task;
        _getResult = &GetResult;
    }

    public void Initialize<T>(Task<T> task)
    {
        Task = task;
        _getResult = &GetResult<T>;
    }

    private static void GetResult(Task task, ref byte result)
    {
        task.GetAwaiter().GetResult();
    }

    private static void GetResult<T>(Task task, ref byte result)
    {
        Task<T> taskOfT = Unsafe.As<Task, Task<T>>(ref task);
        Unsafe.As<byte, T>(ref result) = taskOfT.Result;
    }
}

public static class TaskResult<T>
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "m_result")]
    public static extern ref T Field(Task<T> task);

    public static ref byte GetResultStorage(Task<T> task) => ref Unsafe.As<T?, byte>(ref Field(task));
}

public struct Pair
{
    public string Name;
    public long Count;
    public object? Tag;
}

public static class Program
{
    private static int CheckLayout<T>(JitContinuation<T> cont, int failure)
    {
        ref byte data = ref Continuation.GetRawData(null, cont);
        if (Unsafe.ByteOffset(ref data, ref Unsafe.As<int, byte>(ref cont.State)) != 20) return failure;
        if (Unsafe.ByteOffset(ref data, ref Unsafe.As<ExecutionContext?, byte>(ref cont.ExecContext)) != 24) return failure + 1;
        if (Unsafe.ByteOffset(ref data, ref Unsafe.As<object?, byte>(ref cont.ContinuationContext)) != 32) return failure + 2;
        if (Unsafe.ByteOffset(ref data, ref Unsafe.As<Exception?, byte>(ref cont.Exception)) != 40) return failure + 3;
        if (Unsafe.ByteOffset(ref data, ref Unsafe.As<T, byte>(ref cont.Result)) != 48) return failure + 4;
        return 0;
    }

    // One result of type `T` delivered as the dispatcher delivers it: from a completed task, through
    // a `TaskContinuation`'s `_getResult`, into the next continuation's result slot and into a
    // task's `m_result`.
    private static int Deliver<T>(T initial, T value, Func<T, T, bool> equal, int failure)
    {
        var next = new JitContinuation<T>(hasResult: true) { State = 31, Result = initial };
        next.Exception = new InvalidOperationException("sentinel");
        int layout = CheckLayout(next, failure);
        if (layout != 0) return layout;

        var source = new TaskContinuation();
        source.Initialize(Task.FromResult(value));
        source.GetResult(ref next.GetResultStorageOrNull());
        if (!equal(next.Result, value)) return failure + 5;
        if (next.State != 31) return failure + 6;
        if (next.Exception?.Message != "sentinel") return failure + 7;
        if (source.Task != null) return failure + 8;

        Task<T> target = Task.FromResult(initial);
        source.Initialize(Task.FromResult(value));
        source.GetResult(ref TaskResult<T>.GetResultStorage(target));
        if (!equal(target.Result, value)) return failure + 9;

        return 0;
    }

    private static int Run()
    {
        // The context and exception slots.
        var cont = new JitContinuation<int>(hasResult: false);
        ExecutionContext? ec = ExecutionContext.Capture();
        var ctx = new SynchronizationContext();
        cont.ExecContext = ec;
        cont.ContinuationContext = ctx;
        int layout = CheckLayout(cont, 1);
        if (layout != 0) return layout;

        if (!cont.TryGetExecutionContext(out ExecutionContext? readEc)) return 10;
        if (!ReferenceEquals(readEc, ec)) return 11;
        if (!ReferenceEquals(cont.GetContinuationContext(), ctx)) return 12;
        if (!cont.HasException()) return 13;
        var ex = new InvalidOperationException("boom");
        cont.SetException(ex);
        if (!ReferenceEquals(cont.Exception, ex)) return 14;
        if (!ReferenceEquals(cont.ContinuationContext, ctx)) return 15;
        if (!Unsafe.IsNullRef(ref cont.GetResultStorageOrNull())) return 16;

        // A continuation without a result is resumed with a null result location.
        var voidSource = new TaskContinuation();
        voidSource.Initialize(Task.CompletedTask);
        voidSource.GetResult(ref cont.GetResultStorageOrNull());

        int r;
        r = Deliver(1000, 123456, (a, b) => a == b, 20);
        if (r != 0) return r;
        r = Deliver(1000L, -(1L << 50), (a, b) => a == b, 40);
        if (r != 0) return r;
        r = Deliver("before", "after", (a, b) => ReferenceEquals(a, b), 60);
        if (r != 0) return r;
        var tag = new object();
        r = Deliver(
            new Pair { Name = "before", Count = 1, Tag = null },
            new Pair { Name = "after", Count = 1L << 40, Tag = tag },
            (a, b) => ReferenceEquals(a.Name, b.Name) && a.Count == b.Count && ReferenceEquals(a.Tag, b.Tag),
            80);
        if (r != 0) return r;

        return 0;
    }

    public static int Main() => Run();
}
