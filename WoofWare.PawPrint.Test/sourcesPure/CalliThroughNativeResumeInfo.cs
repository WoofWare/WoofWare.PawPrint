using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// A `ResumeInfo` (CoreLib's `{ delegate*<Continuation, ref byte, Continuation?> Resume; void*
// DiagnosticIP }`) held in native memory rather than in a static, as the VM holds the ones it makes
// for JIT'd continuations: the function pointer is stored into the block, read back through the
// continuation's `ResumeInfo*`, and called with `calli`, as `RuntimeAsyncTask<T>.DispatchContinuations`
// calls it. Two blocks, two continuations: each call must reach its own block's function.
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

public static unsafe class Program
{
    private static Continuation? WriteState(Continuation cont, ref byte result)
    {
        Unsafe.As<byte, int>(ref result) = cont.State;
        return null;
    }

    private static Continuation? Suspend(Continuation cont, ref byte result)
    {
        return cont.Next;
    }

    private static int Run()
    {
        ResumeInfo* infos = (ResumeInfo*)NativeMemory.AllocZeroed((nuint)(2 * sizeof(ResumeInfo)));
        try
        {
            if (infos[0].Resume != null) return 1;

            infos[0].Resume = &WriteState;
            infos[0].DiagnosticIP = null;
            infos[1].Resume = &Suspend;
            infos[1].DiagnosticIP = infos;

            var first = new Continuation { State = 17, ResumeInfo = &infos[0] };
            var second = new Continuation { State = 4, ResumeInfo = infos + 1 };
            first.Next = second;
            second.Next = first;

            if (first.ResumeInfo->DiagnosticIP != null) return 2;
            if (second.ResumeInfo->DiagnosticIP != infos) return 3;

            int result = 0;
            Continuation? cont = first;
            Continuation? newCont = cont.ResumeInfo->Resume(cont, ref Unsafe.As<int, byte>(ref result));
            if (newCont != null) return 4;
            if (result != 17) return 5;

            cont = second;
            newCont = cont.ResumeInfo->Resume(cont, ref Unsafe.As<int, byte>(ref result));
            if (!ReferenceEquals(newCont, first)) return 6;
            if (result != 17) return 7;

            // The function pointer, read out of native memory into a local, equals the one stored.
            delegate*<Continuation, ref byte, Continuation?> read = second.ResumeInfo->Resume;
            if (read != (delegate*<Continuation, ref byte, Continuation?>)&Suspend) return 8;
            if (read == infos[0].Resume) return 9;

            return 0;
        }
        finally
        {
            NativeMemory.Free(infos);
        }
    }

    public static int Main() => Run();
}
