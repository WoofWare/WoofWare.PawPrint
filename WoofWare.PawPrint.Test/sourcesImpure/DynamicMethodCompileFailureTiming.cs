using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;

// Where an instruction operand that the JIT refuses is raised, for an ownerless `DynamicMethod`
// built with `restrictedSkipVisibility: true`, whose `CreateDelegate` compiles it
// (`RuntimeHelpers.CompileMethod`), and for the same method built with `false`, the control, which
// is compiled at its first invocation. On real .NET the restricted method's `CreateDelegate` raises
// the exception, and raises it again when called again; the control's `CreateDelegate` succeeds and
// its first invocation raises it.
//
// PawPrint raises each of these when the instruction executes, for both kinds (see
// docs/divergences.md, "What a restricted-skip-visibility method's `CreateDelegate` compiles"). The
// checks are in the order of the stages of docs/plans/2026-10-24-jit-import-at-method-entry.md that
// bind each operand when a method is compiled: a `call` (Stage 3), `box` and `newarr` (Stage 4), a
// field (Stage 5), another type operand (Stage 6), and `ldstr` (Stage 8). A bad scope token is a
// `DynamicScope` operand, which Stage 8 may be the one to bind ahead of its instruction.
//
// Returns 0 on success, or the number of the first check that failed. Every expectation was
// measured on real .NET 10.0.14 and 11.0.0-rc.3.

public class Program
{
    private delegate void Build(ILGenerator il);

    private static DynamicMethod New(bool restricted, Build build)
    {
        DynamicMethod dm = new DynamicMethod("Refused", typeof(int), Type.EmptyTypes, restricted);
        build(dm.GetILGenerator());
        return dm;
    }

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

    private static bool ThrowsExactly(Action a, Type expected, int hResult)
    {
        Exception e = Threw(a);
        return e != null && e.GetType() == expected && e.HResult == hResult;
    }

    // The control binds, and its first two invocations raise `expected`; the restricted method's
    // first two `CreateDelegate`s raise it.
    private static bool RaisedAtCreateDelegate(Build build, Type expected, int hResult)
    {
        Func<int> control = null;
        if (Threw(() => { control = Create(New(false, build)); }) != null
            || !ThrowsExactly(() => control(), expected, hResult)
            || !ThrowsExactly(() => control(), expected, hResult))
        {
            return false;
        }

        DynamicMethod restricted = New(true, build);
        return ThrowsExactly(() => Create(restricted), expected, hResult)
            && ThrowsExactly(() => Create(restricted), expected, hResult);
    }

    private static IList ScopeTokens(DynamicMethod dm)
    {
        ILGenerator il = dm.GetILGenerator();
        object scope = il.GetType().GetField("m_scope", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(il);
        return (IList)scope.GetType().GetField("m_tokens", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(scope);
    }

    // Replaces the one scope entry equal to `from` by `to`. False unless exactly one entry matched.
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

    // `ldstr "before"; callvirt get_Length; ret`, with the literal's scope slot rewritten to
    // "after!!!" after `CreateDelegate`: 6 if the literal was read when `CreateDelegate` compiled the
    // method, 8 if it was read later.
    private static int LengthAfterRewrite(bool restricted)
    {
        DynamicMethod dm = New(
            restricted,
            il =>
            {
                il.Emit(OpCodes.Ldstr, "before");
                il.Emit(OpCodes.Callvirt, typeof(string).GetProperty("Length").GetGetMethod());
                il.Emit(OpCodes.Ret);
            }
        );
        Func<int> f = Create(dm);
        if (!Rewrite(dm, "before", "after!!!"))
        {
            return -1;
        }

        int first = f();
        return f() == first ? first : -2;
    }

    private const int InvalidProgram = unchecked((int)0x8013153A);
    private const int BadImageFormat = unchecked((int)0x8007000B);
    private const int TypeLoad = unchecked((int)0x80131522);

    public static int Main()
    {
        // 1. Stage 3: a `call` of an abstract method.
        if (!RaisedAtCreateDelegate(
                il =>
                {
                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Call, typeof(Stream).GetMethod("Read", new[] { typeof(byte[]), typeof(int), typeof(int) }));
                    il.Emit(OpCodes.Ret);
                },
                typeof(BadImageFormatException),
                BadImageFormat))
        {
            return 1;
        }

        // 2. Stage 3: a `call` whose operand names no scope entry.
        if (!RaisedAtCreateDelegate(
                il =>
                {
                    il.Emit(OpCodes.Call, 0x0A000FFF);
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Ret);
                },
                typeof(InvalidProgramException),
                InvalidProgram))
        {
            return 2;
        }

        // 3. Stage 4: a `box` of a byref-like type.
        if (!RaisedAtCreateDelegate(
                il =>
                {
                    il.DeclareLocal(typeof(Span<int>));
                    il.Emit(OpCodes.Ldloc_0);
                    il.Emit(OpCodes.Box, typeof(Span<int>));
                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Cgt_Un);
                    il.Emit(OpCodes.Ret);
                },
                typeof(InvalidProgramException),
                InvalidProgram))
        {
            return 3;
        }

        // 4. Stage 4: a `newarr` of a byref-like element type. Its message is not checked (#2277).
        if (!RaisedAtCreateDelegate(
                il =>
                {
                    il.Emit(OpCodes.Ldc_I4_1);
                    il.Emit(OpCodes.Newarr, typeof(Span<int>));
                    il.Emit(OpCodes.Pop);
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Ret);
                },
                typeof(TypeLoadException),
                TypeLoad))
        {
            return 4;
        }

        // 5. Stage 5: a field operand that names no scope entry.
        if (!RaisedAtCreateDelegate(
                il =>
                {
                    il.Emit(OpCodes.Ldsfld, 0x04000FFF);
                    il.Emit(OpCodes.Ret);
                },
                typeof(InvalidProgramException),
                InvalidProgram))
        {
            return 5;
        }

        // 6. Stage 6: a `castclass` whose operand names no scope entry.
        if (!RaisedAtCreateDelegate(
                il =>
                {
                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Castclass, 0x02000FFF);
                    il.Emit(OpCodes.Pop);
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Ret);
                },
                typeof(InvalidProgramException),
                InvalidProgram))
        {
            return 6;
        }

        // 7. Stage 8: a string literal is read when the method is compiled. The control hears the
        // rewrite, and the restricted method does not.
        if (LengthAfterRewrite(false) != 8 || LengthAfterRewrite(true) != 6)
        {
            return 7;
        }

        return 0;
    }
}
