using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

// An ownerless `DynamicMethod` built with `restrictedSkipVisibility: true` is compiled by
// `CreateDelegate`, not by its first invocation: `CreateDelegate` calls `RuntimeHelpers.CompileMethod`
// (the QCall `ReflectionInvocation_CompileMethod`) for such a method, and for no other. The same
// method built with `restrictedSkipVisibility: false` is the control: it is compiled at its first
// invocation. `Expression.Compile()` builds the first kind.
//
// So what the JIT reads from the guest is read at `CreateDelegate` for the restricted method, and at
// the first invocation for the control: a `catch` clause's type, and `InitLocals`. And what the JIT
// refuses is raised from `CreateDelegate` for the restricted method, and from the first invocation for
// the control. Every check pairs the two, so that an implementation that never compiles at
// `CreateDelegate` fails here rather than passing on the control's timing.
//
// Not covered here, because PawPrint still raises them at the first invocation (see
// docs/divergences.md, "What a restricted-skip-visibility method's `CreateDelegate` compiles"):
// instruction operands the JIT refuses (`DynamicMethodCompileFailureTiming.cs`), and invalid IL
// (`DynamicMethodInvalidIlAtCreateDelegate.cs`).
//
// Returns 0 on success, or the number of the first check that failed. Every expectation was
// measured on real .NET 10.0.14 and 11.0.0-rc.3.

static class Events
{
    public static List<string> Log = new List<string>();

    public static int Note(string s)
    {
        Log.Add(s);
        return 5;
    }
}

// One type per run, since a type initialiser runs once.
class BfiRestricted
{
    public static int X = Events.Note("BfiRestricted");
}

class BfiUnrestricted
{
    public static int X = Events.Note("BfiUnrestricted");
}

class PreciseRestricted
{
    public static int X;

    static PreciseRestricted()
    {
        X = Events.Note("PreciseRestricted");
    }
}

class PreciseUnrestricted
{
    public static int X;

    static PreciseUnrestricted()
    {
        X = Events.Note("PreciseUnrestricted");
    }
}

public class Program
{
    private const int InvalidProgramHResult = unchecked((int)0x8013153A);

    private static DynamicMethod New(bool restricted, string name) =>
        new DynamicMethod(name, typeof(int), Type.EmptyTypes, restricted);

    private static Func<int> Create(DynamicMethod dm) => (Func<int>)dm.CreateDelegate(typeof(Func<int>));

    private static Exception Threw(Action a)
    {
        try
        {
            a();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    private static bool Returns(Func<int> f, int expected)
    {
        try
        {
            return f() == expected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ThrowsExactly(Action a, Type expected)
    {
        Exception e = Threw(a);
        return e != null && e.GetType() == expected;
    }

    // The scope's token list, through private reflection: `DynamicILGenerator.m_scope.m_tokens`.
    private static IList ScopeTokens(DynamicMethod dm)
    {
        ILGenerator il = dm.GetILGenerator();
        object scope = il.GetType().GetField("m_scope", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(il);
        return (IList)scope.GetType().GetField("m_tokens", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(scope);
    }

    // Replaces the one scope entry equal to `from` by `to`. False unless exactly one entry matched,
    // so that a check can never pass by rewriting nothing.
    private static bool Rewrite(DynamicMethod dm, object from, object to)
    {
        IList tokens = ScopeTokens(dm);
        int found = -1;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] != null && tokens[i].Equals(from))
            {
                if (found != -1)
                {
                    return false;
                }

                found = i;
            }
        }

        if (found == -1)
        {
            return false;
        }

        tokens[found] = to;
        return true;
    }

    private static DynamicMethod Valid(bool restricted)
    {
        DynamicMethod dm = New(restricted, "Valid");
        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4, 42);
        il.Emit(OpCodes.Ret);
        return dm;
    }

    private static DynamicMethod ThrowsAtRun(bool restricted)
    {
        DynamicMethod dm = New(restricted, "ThrowsAtRun");
        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(Type.EmptyTypes));
        il.Emit(OpCodes.Throw);
        return dm;
    }

    // `try { throw new ArgumentException(); } catch (<clause>) { return 7; }`. With the clause
    // `InvalidOperationException`, the exception escapes; a clause rewritten to `ArgumentException`
    // before the method is compiled catches it, and the method returns 7.
    private static DynamicMethod CatchesOrNot(bool restricted, Type clause)
    {
        DynamicMethod dm = New(restricted, "CatchesOrNot");
        ILGenerator il = dm.GetILGenerator();
        LocalBuilder result = il.DeclareLocal(typeof(int));
        il.BeginExceptionBlock();
        il.Emit(OpCodes.Newobj, typeof(ArgumentException).GetConstructor(Type.EmptyTypes));
        il.Emit(OpCodes.Throw);
        il.BeginCatchBlock(clause);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldc_I4, 7);
        il.Emit(OpCodes.Stloc, result);
        il.EndExceptionBlock();
        il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Ret);
        return dm;
    }

    // `try { result = 5; } catch (<clause>) { }`, which never throws. A clause that cannot be one,
    // such as an open generic type, makes the method one the JIT refuses to compile.
    private static DynamicMethod QuietTry(bool restricted, string name, Type clause)
    {
        DynamicMethod dm = New(restricted, name);
        ILGenerator il = dm.GetILGenerator();
        LocalBuilder result = il.DeclareLocal(typeof(int));
        il.BeginExceptionBlock();
        il.Emit(OpCodes.Ldc_I4, 5);
        il.Emit(OpCodes.Stloc, result);
        il.BeginCatchBlock(clause);
        il.Emit(OpCodes.Pop);
        il.EndExceptionBlock();
        il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Ret);
        return dm;
    }

    // `try { throw new InvalidOperationException(); } catch (<clause>) { return 7; }`. With an
    // open generic clause the JIT refuses the method; with the clause repaired to
    // `InvalidOperationException` it compiles, and the clause catches what the body throws, so the
    // clause's type is used as well as resolved.
    private static DynamicMethod ThrowsIntoClause(bool restricted, string name, Type clause)
    {
        DynamicMethod dm = New(restricted, name);
        ILGenerator il = dm.GetILGenerator();
        LocalBuilder result = il.DeclareLocal(typeof(int));
        il.BeginExceptionBlock();
        il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(Type.EmptyTypes));
        il.Emit(OpCodes.Throw);
        il.BeginCatchBlock(clause);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldc_I4, 7);
        il.Emit(OpCodes.Stloc, result);
        il.EndExceptionBlock();
        il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Ret);
        return dm;
    }

    // `ldc.i4.4; localloc; ldind.i4; ret`: four bytes of stack, read without being written. Invoked
    // only while `InitLocals` is latched true, since PawPrint refuses to read unwritten bytes and
    // real .NET returns unspecified ones.
    private static DynamicMethod ReadsUnwritten(bool restricted)
    {
        DynamicMethod dm = New(restricted, "ReadsUnwritten");
        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4, 4);
        il.Emit(OpCodes.Localloc);
        il.Emit(OpCodes.Ldind_I4);
        il.Emit(OpCodes.Ret);
        return dm;
    }

    private static DynamicMethod ReadsStatic(bool restricted, Type owner)
    {
        DynamicMethod dm = New(restricted, "ReadsStatic");
        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldsfld, owner.GetField("X"));
        il.Emit(OpCodes.Ret);
        return dm;
    }

    // No type initialiser runs when the method is compiled, even for a `beforefieldinit` type whose
    // static field the body reads; the first invocation runs it.
    private static bool InitialiserRunsAtInvoke(bool restricted, Type owner)
    {
        Events.Log.Clear();
        Func<int> f = Create(ReadsStatic(restricted, owner));
        if (Events.Log.Count != 0)
        {
            return false;
        }

        if (!Returns(f, 5))
        {
            return false;
        }

        return Events.Log.Count == 1 && Events.Log[0] == owner.Name && Returns(f, 5) && Events.Log.Count == 1;
    }

    public static int Main()
    {
        // 1, 2. A valid body: both kinds bind and run.
        if (!Returns(Create(Valid(true)), 42))
        {
            return 1;
        }

        if (!Returns(Create(Valid(false)), 42))
        {
            return 2;
        }

        // 3, 4. A body that throws when it runs: compiling it raises nothing, for either kind.
        {
            Func<int> f = Create(ThrowsAtRun(true));
            if (!ThrowsExactly(() => f(), typeof(InvalidOperationException))
                || !ThrowsExactly(() => f(), typeof(InvalidOperationException)))
            {
                return 3;
            }
        }

        {
            Func<int> f = Create(ThrowsAtRun(false));
            if (!ThrowsExactly(() => f(), typeof(InvalidOperationException))
                || !ThrowsExactly(() => f(), typeof(InvalidOperationException)))
            {
                return 4;
            }
        }

        // 5. Control: a `catch` clause's scope slot rewritten after `CreateDelegate` is heard by an
        // unrestricted method, which is compiled at its first invocation.
        {
            DynamicMethod dm = CatchesOrNot(false, typeof(InvalidOperationException));
            Func<int> f = Create(dm);
            if (!Rewrite(dm, typeof(InvalidOperationException).TypeHandle, typeof(ArgumentException).TypeHandle)
                || !Returns(f, 7)
                || !Returns(f, 7))
            {
                return 5;
            }
        }

        // 6. ...and not by a restricted one, which `CreateDelegate` compiled with the clause as it
        // was then.
        {
            DynamicMethod dm = CatchesOrNot(true, typeof(InvalidOperationException));
            Func<int> f = Create(dm);
            if (!Rewrite(dm, typeof(InvalidOperationException).TypeHandle, typeof(ArgumentException).TypeHandle)
                || !ThrowsExactly(() => f(), typeof(ArgumentException))
                || !ThrowsExactly(() => f(), typeof(ArgumentException)))
            {
                return 6;
            }
        }

        // 7. A second `CreateDelegate` after the rewrite does not compile the method again: the
        // QCall does nothing for a method that is already compiled. The control hears the rewrite
        // through both delegates.
        {
            DynamicMethod dm = CatchesOrNot(false, typeof(InvalidOperationException));
            Func<int> first = Create(dm);
            if (!Rewrite(dm, typeof(InvalidOperationException).TypeHandle, typeof(ArgumentException).TypeHandle))
            {
                return 7;
            }

            Func<int> second = Create(dm);
            if (!Returns(second, 7) || !Returns(first, 7))
            {
                return 7;
            }
        }

        {
            DynamicMethod dm = CatchesOrNot(true, typeof(InvalidOperationException));
            Func<int> first = Create(dm);
            if (!Rewrite(dm, typeof(InvalidOperationException).TypeHandle, typeof(ArgumentException).TypeHandle))
            {
                return 7;
            }

            Func<int> second = Create(dm);
            if (!ThrowsExactly(() => second(), typeof(ArgumentException))
                || !ThrowsExactly(() => first(), typeof(ArgumentException)))
            {
                return 7;
            }
        }

        // 8. `InitLocals` cleared after `CreateDelegate` is not heard by a restricted method: it was
        // latched true when `CreateDelegate` compiled it. The control is not invoked, since it would
        // read unwritten bytes; it checks only that `CreateDelegate` succeeds.
        {
            DynamicMethod dm = ReadsUnwritten(true);
            Func<int> f = Create(dm);
            dm.InitLocals = false;
            if (!Returns(f, 0) || !Returns(f, 0))
            {
                return 8;
            }

            DynamicMethod control = ReadsUnwritten(false);
            if (Threw(() => Create(control)) != null)
            {
                return 8;
            }
        }

        // 9. Control: a `catch` clause naming an open generic type. The unrestricted method binds, and
        // its first invocation raises `InvalidProgramException`, as does its second.
        {
            Func<int> f = Create(QuietTry(false, "OpenGenericClauseControl", typeof(List<>)));
            Exception e = Threw(() => f());
            if (e == null || e.GetType() != typeof(InvalidProgramException) || e.HResult != InvalidProgramHResult)
            {
                return 9;
            }

            if (!ThrowsExactly(() => f(), typeof(InvalidProgramException)))
            {
                return 9;
            }
        }

        // 10, 11. The restricted method's `CreateDelegate` raises it instead, and a second
        // `CreateDelegate` raises it again: a failed compilation is not remembered.
        {
            DynamicMethod dm = QuietTry(true, "OpenGenericClauseRestricted", typeof(List<>));
            Exception e = Threw(() => Create(dm));
            if (e == null || e.GetType() != typeof(InvalidProgramException) || e.HResult != InvalidProgramHResult)
            {
                return 10;
            }

            if (!ThrowsExactly(() => Create(dm), typeof(InvalidProgramException)))
            {
                return 10;
            }

            // 11. The trace names `CreateDelegate`, and no frame names the dynamic method, which
            // never ran. Its first frame is not checked: real .NET's is `CreateDelegate(Type,
            // Object)`, PawPrint's is `RuntimeHelpers.CompileMethod` (see docs/divergences.md, "A
            // trace has a frame for every call that optimised code inlines or tail-calls").
            string trace = e.StackTrace ?? "";
            if (!trace.Contains("System.Reflection.Emit.DynamicMethod.CreateDelegate(")
                || trace.Contains("OpenGenericClauseRestricted"))
            {
                return 11;
            }
        }

        // 12. Repair after a failure. The control's first invocation fails; the guest rewrites the
        // clause's slot to a type a clause can name; the next invocation compiles and runs, and the
        // repaired clause catches.
        {
            DynamicMethod dm = ThrowsIntoClause(false, "RepairedControl", typeof(List<>));
            Func<int> f = Create(dm);
            if (!ThrowsExactly(() => f(), typeof(InvalidProgramException))
                || !Rewrite(dm, typeof(List<>).TypeHandle, typeof(InvalidOperationException).TypeHandle)
                || !Returns(f, 7))
            {
                return 12;
            }
        }

        // 13. The restricted method's failed `CreateDelegate` latches nothing either: after the same
        // repair, the next `CreateDelegate` compiles it, and the delegate runs.
        {
            DynamicMethod dm = ThrowsIntoClause(true, "Repaired", typeof(List<>));
            if (!ThrowsExactly(() => Create(dm), typeof(InvalidProgramException))
                || !Rewrite(dm, typeof(List<>).TypeHandle, typeof(InvalidOperationException).TypeHandle))
            {
                return 13;
            }

            Func<int> f = null;
            if (Threw(() => { f = Create(dm); }) != null || !Returns(f, 7))
            {
                return 13;
            }
        }

        // 14. A clause's slot holding something other than a `RuntimeTypeHandle`: a bad class token,
        // raised as `BadImageFormatException` with a fixed message. The control raises it at its
        // first invocation.
        {
            DynamicMethod dm = QuietTry(false, "NotATypeControl", typeof(InvalidOperationException));
            if (!Rewrite(dm, typeof(InvalidOperationException).TypeHandle, "not a type"))
            {
                return 14;
            }

            Func<int> f = Create(dm);
            Exception e = Threw(() => f());
            if (e == null || e.GetType() != typeof(BadImageFormatException) || e.Message != "Bad class token.")
            {
                return 14;
            }
        }

        // 15. ...and the restricted method at `CreateDelegate`.
        {
            DynamicMethod dm = QuietTry(true, "NotAType", typeof(InvalidOperationException));
            if (!Rewrite(dm, typeof(InvalidOperationException).TypeHandle, "not a type"))
            {
                return 15;
            }

            Exception e = Threw(() => Create(dm));
            if (e == null || e.GetType() != typeof(BadImageFormatException) || e.Message != "Bad class token.")
            {
                return 15;
            }
        }

        // 16 to 19. Compiling runs no type initialiser.
        if (!InitialiserRunsAtInvoke(true, typeof(BfiRestricted)))
        {
            return 16;
        }

        if (!InitialiserRunsAtInvoke(false, typeof(BfiUnrestricted)))
        {
            return 17;
        }

        if (!InitialiserRunsAtInvoke(true, typeof(PreciseRestricted)))
        {
            return 18;
        }

        if (!InitialiserRunsAtInvoke(false, typeof(PreciseUnrestricted)))
        {
            return 19;
        }

        return 0;
    }
}
