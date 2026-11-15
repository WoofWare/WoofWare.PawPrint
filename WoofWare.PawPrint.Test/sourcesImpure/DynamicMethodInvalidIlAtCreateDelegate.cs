using System;
using System.Reflection.Emit;

// Where invalid IL is refused, for an ownerless `DynamicMethod` built with
// `restrictedSkipVisibility: true`, whose `CreateDelegate` compiles it
// (`RuntimeHelpers.CompileMethod`), and for the same method built with `false`, the control, which
// is compiled at its first invocation. On real .NET the JIT refuses each body with an
// `InvalidProgramException`: from the restricted method's `CreateDelegate`, again from a second
// `CreateDelegate`, and from the control's first invocation and its second.
//
// PawPrint raises none of these: it stops the run when the invalid offset executes (#2278). Each
// check exercises the control first, so the run stops there.
//
// Returns 0 on success, or the number of the first check that failed. Every expectation was
// measured on real .NET 10.0.14 and 11.0.0-rc.3.

public class Program
{
    private delegate void Build(ILGenerator il);

    private static DynamicMethod New(bool restricted, Build build)
    {
        DynamicMethod dm = new DynamicMethod("Invalid", typeof(int), Type.EmptyTypes, restricted);
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

    private const int InvalidProgram = unchecked((int)0x8013153A);

    private static bool IsInvalidProgram(Exception e) =>
        e != null && e.GetType() == typeof(InvalidProgramException) && e.HResult == InvalidProgram;

    private static bool RefusedAtCreateDelegate(Build build)
    {
        Func<int> control = null;
        if (Threw(() => { control = Create(New(false, build)); }) != null
            || !IsInvalidProgram(Threw(() => control()))
            || !IsInvalidProgram(Threw(() => control())))
        {
            return false;
        }

        DynamicMethod restricted = New(true, build);
        return IsInvalidProgram(Threw(() => Create(restricted))) && IsInvalidProgram(Threw(() => Create(restricted)));
    }

    public static int Main()
    {
        // 1. A `pop` of an empty stack.
        if (!RefusedAtCreateDelegate(il =>
            {
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Ret);
            }))
        {
            return 1;
        }

        // 2. A branch to the offset just past the body's last instruction.
        if (!RefusedAtCreateDelegate(il =>
            {
                Label end = il.DefineLabel();
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Brtrue, end);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Ret);
                il.MarkLabel(end);
            }))
        {
            return 2;
        }

        // 3. A branch into the middle of an instruction: `br.s` at 0 is two bytes, `ldc.i4` at 2 has
        // its operand at 3 to 6, and the target 2 + 1 = 3 is inside it.
        if (!RefusedAtCreateDelegate(il =>
            {
                il.Emit(OpCodes.Br_S, (sbyte)1);
                il.Emit(OpCodes.Ldc_I4, 0x01020304);
                il.Emit(OpCodes.Ret);
            }))
        {
            return 3;
        }

        return 0;
    }
}
