using System;
using System.Diagnostics;
using System.Reflection;

public class Program
{
    // `MethodInfo.Invoke` on one method, several times, with dynamic code supported.
    //
    // CoreLib's `MethodBaseInvoker` runs a method's first reflective invocation through the
    // runtime's own invoker, and from the second onwards through a `DynamicMethod` it emits,
    // `InvokeStub_<Type>.<Method>`, scoped to CoreLib's module (`InvokerEmitUtil`). For
    // `MethodInfo.Invoke` that stub begins `call StubHelpers.NextCallReturnAddress; pop`, which
    // CoreCLR's JIT expands to the address the stub's next call returns to, and whose presence
    // stops the JIT inlining anything into the stub or tail-calling out of it.
    //
    // The test harness registers this case with the dynamic-code switch set to "true", which is
    // also real .NET's default.
    //
    // Returns 0 on success, or the number of the first check that failed.

    // The names of the methods in the first `count` frames of the stack, innermost first.
    private static string[] InnerFrames(int count)
    {
        StackTrace trace = new StackTrace();
        string[] names = new string[count];

        // Frame 0 is this method.
        for (int i = 0; i < count; i++)
        {
            MethodBase m = trace.GetFrame(i + 1).GetMethod();
            names[i] = m == null ? "<null>" : m.Name;
        }

        return names;
    }

    public static string[] Frames()
    {
        string[] names = InnerFrames(2);
        // Not in tail position, so that no frame can be elided on any runtime.
        GC.KeepAlive(names);
        return names;
    }

    public static int Main(string[] args)
    {
        Assembly self = typeof(Program).Assembly;
        MethodInfo getExecutingAssembly = typeof(Assembly).GetMethod("GetExecutingAssembly", Type.EmptyTypes);
        MethodInfo getCallingAssembly = typeof(Assembly).GetMethod("GetCallingAssembly", Type.EmptyTypes);
        MethodInfo frames = typeof(Program).GetMethod("Frames");

        for (int i = 0; i < 3; i++)
        {
            // `GetExecutingAssembly` asks for its caller by stack crawl. Every frame between it
            // and `Main` is reflection's: the runtime's invoker on the first call, the emitted
            // `InvokeStub_Assembly.GetExecutingAssembly` (a CoreLib dynamic method) on the later
            // ones. The crawl skips both, so the answer is this assembly every time.
            if (!ReferenceEquals(getExecutingAssembly.Invoke(null, null), self))
            {
                return 1 + 10 * i;
            }

            // `GetCallingAssembly`'s crawl skips the same frames, so it answers whatever called
            // `Main`: on .NET 10 the VM, from native code, so the crawl falls back to this
            // assembly; on .NET 11 CoreLib's `Environment.CallEntryPoint`. Measured on 10.0.12
            // and on 11.0.0-rc.1.
            Assembly callerOfMain = Environment.Version.Major == 10 ? self : typeof(object).Assembly;
            if (!ReferenceEquals(getCallingAssembly.Invoke(null, null), callerOfMain))
            {
                return 2 + 10 * i;
            }

            // Which frame calls a reflectively invoked method: the first call's goes through the
            // runtime's invoker, and every later one through the emitted stub, which keeps
            // `Frames` in a frame of its own rather than inlining it.
            string[] names = (string[]) frames.Invoke(null, null);

            if (names[0] != "Frames")
            {
                return 3 + 10 * i;
            }

            bool fromStub = names[1] == "InvokeStub_Program.Frames";

            if (fromStub != (i > 0))
            {
                return 4 + 10 * i;
            }
        }

        return 0;
    }
}
