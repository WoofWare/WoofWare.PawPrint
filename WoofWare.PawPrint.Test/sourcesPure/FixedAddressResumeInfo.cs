using System;
using System.Runtime.CompilerServices;

// The shapes by which .NET 11's CoreLib points a continuation at its resume function
// (`RuntimeAsyncTaskContinuation`, `AsyncHelpers.ValueTaskSourceContinuation`), with CoreLib's
// internal types copied:
//
// * a `[FixedAddressValueType] static readonly ResumeInfo`, initialised with `&Method`;
// * `(ResumeInfo*)Unsafe.AsPointer(in Holder.ResumeInfo)` in the continuation's constructor, stored
//   in a `ResumeInfo*` instance field;
// * `cont.ResumeInfo->Resume(cont, ref resultLoc)`, a `calli` through the function pointer read from
//   that struct, with the result location a `ref byte` the callee writes through as `ref int`, or a
//   null ref for a continuation without a result;
// * `cont.ResumeInfo != null && cont.ResumeInfo->DiagnosticIP != null`, as `UnwindToPossibleHandler`
//   tests it.
//
// A mutable `[FixedAddressValueType]` static checks that the pointer addresses the static itself
// rather than a copy of it.
public unsafe struct ResumeInfo
{
    public delegate*<Continuation, ref byte, Continuation?> Resume;
    public void* DiagnosticIP;
}

public unsafe class Continuation
{
    public Continuation? Next;
    public ResumeInfo* ResumeInfo;
    public int Flags;
    public int State;
}

public sealed unsafe class TaskContinuation : Continuation
{
    public int Payload;

    public TaskContinuation(int payload)
    {
        Payload = payload;
        ResumeInfo = (ResumeInfo*)Unsafe.AsPointer(in TaskContinuationResume.ResumeInfo);
    }

    private static class TaskContinuationResume
    {
        [FixedAddressValueType]
        public static readonly ResumeInfo ResumeInfo = new ResumeInfo
        {
            DiagnosticIP = null,
            Resume = &ResumeTaskContinuation,
        };

        private static Continuation? ResumeTaskContinuation(Continuation cont, ref byte result)
        {
            var taskCont = (TaskContinuation)cont;
            taskCont.Next = null;
            if (!Unsafe.IsNullRef(ref result))
            {
                Unsafe.As<byte, int>(ref result) = taskCont.Payload;
            }

            return taskCont.Payload < 0 ? taskCont : null;
        }
    }
}

public sealed unsafe class OtherContinuation : Continuation
{
    public OtherContinuation()
    {
        ResumeInfo = (ResumeInfo*)Unsafe.AsPointer(in OtherContinuationResume.ResumeInfo);
    }

    private static class OtherContinuationResume
    {
        [FixedAddressValueType]
        public static readonly ResumeInfo ResumeInfo = new ResumeInfo
        {
            DiagnosticIP = (void*)0x1234,
            Resume = &ResumeOther,
        };

        private static Continuation? ResumeOther(Continuation cont, ref byte result)
        {
            Unsafe.As<byte, int>(ref result) = 77;
            return null;
        }
    }
}

public static unsafe class Program
{
    private struct Counter
    {
        public int Value;
        public long Wide;
    }

    [FixedAddressValueType]
    private static Counter s_counter;

    private static bool HasDiagnosticIP(Continuation cont) =>
        cont.ResumeInfo != null && cont.ResumeInfo->DiagnosticIP != null;

    private static int Run()
    {
        var a = new TaskContinuation(42);
        var b = new TaskContinuation(-5);
        var other = new OtherContinuation();

        // Every continuation of one kind points at the one static.
        if (a.ResumeInfo == null) return 1;
        if (a.ResumeInfo != b.ResumeInfo) return 2;
        if (a.ResumeInfo == other.ResumeInfo) return 3;

        if (HasDiagnosticIP(a)) return 4;
        if (!HasDiagnosticIP(other)) return 5;
        if (other.ResumeInfo->DiagnosticIP != (void*)0x1234) return 6;
        if (a.ResumeInfo->Resume == null) return 7;
        if (a.ResumeInfo->Resume == other.ResumeInfo->Resume) return 8;

        // The dispatcher's call, with the result written through the location it passes.
        int resultSlot = 0;
        a.Next = b;
        Continuation? newCont = a.ResumeInfo->Resume(a, ref Unsafe.As<int, byte>(ref resultSlot));
        if (newCont != null) return 9;
        if (resultSlot != 42) return 10;
        if (a.Next != null) return 11;

        // A continuation with no result slot is passed a null ref.
        newCont = b.ResumeInfo->Resume(b, ref Unsafe.NullRef<byte>());
        if (!ReferenceEquals(newCont, b)) return 12;

        // The function pointer read from a different continuation's ResumeInfo.
        Continuation current = other;
        newCont = current.ResumeInfo->Resume(current, ref Unsafe.As<int, byte>(ref resultSlot));
        if (newCont != null) return 13;
        if (resultSlot != 77) return 14;

        // The pointer addresses the static, not a copy of it.
        Counter* counter = (Counter*)Unsafe.AsPointer(ref s_counter);
        counter->Value = 5;
        counter->Wide = 1L << 40;
        if (s_counter.Value != 5) return 15;
        if (s_counter.Wide != 1L << 40) return 16;
        s_counter.Value = 9;
        if (counter->Value != 9) return 17;
        if ((Counter*)Unsafe.AsPointer(ref s_counter) != counter) return 18;

        return 0;
    }

    public static int Main() => Run();
}
