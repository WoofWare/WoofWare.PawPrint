using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;

public struct Big
{
    public long A;
    public long B;
    public long C;
}

public struct WithRef
{
    public string S;
    public int N;
}

public unsafe class Program
{
    // `ldelem <type>` and `stelem <type>` in a dynamic method's body, where the operand names an
    // entry of the method's `DynamicScope` rather than a metadata row. `Emit(OpCode, Type)` stores
    // the type's `RuntimeTypeHandle` in the scope whatever the opcode (`DynamicILGenerator.cs:129`),
    // and the JIT resolves it as it resolves `newarr`'s or `box`'s, refusing a byref, `System.Void`
    // and a type with generic variables when it compiles the body. The token decides only the
    // width and kind of the access: the JIT does not compare it with the array's element type.
    //
    // `ldelem` pushes the stored element whatever its operand says, so only the refusals (31 to
    // 45, 53 and 54, 55 to 68) see which entry an `ldelem` resolved; 71 to 73 hand the loaded value
    // to an instruction whose operand types must match its stack shape. `stelem` coerces what it
    // stores to the type it resolved, which 9, 14, 16 and 19 see.
    //
    // Every expectation was measured on real .NET 10 and on 11, and each check, perturbed on its
    // own, exits with its own number on both.
    //
    // Returns 0 on success, or the number of the first check that failed.

    public static int SField = 3;

    public static int Helper() => 7;

    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly string InvalidProgram = new InvalidProgramException().Message;

    // Whether `f` throws an exception of exactly the type `expected`, with `message` unless that
    // is null. Any other exception counts as a failure of the check rather than escaping it.
    private static bool Throws(Type expected, string message, Func<object> f)
    {
        try
        {
            f();
            return false;
        }
        catch (Exception e)
        {
            return e.GetType() == expected && (message == null || e.Message == message);
        }
    }

    // The generator of the last method `Make` minted, for `Rewritten` to reach its scope.
    private static ILGenerator lastIl;

    private static T Make<T>(Type ret, Type[] args, Action<ILGenerator> emit) where T : Delegate
    {
        DynamicMethod dm = new DynamicMethod("D", ret, args, typeof(Program).Module);
        ILGenerator il = dm.GetILGenerator();
        emit(il);
        lastIl = il;
        return (T) dm.CreateDelegate(typeof(T));
    }

    // `array[index]`, read by `ldelem <element>`.
    private static TResult Load<TArray, TResult>(Type element, TArray array, int index)
    {
        return Make<Func<TArray, int, TResult>>(typeof(TResult), new[] { typeof(TArray), typeof(int) }, il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldelem, element);
            il.Emit(OpCodes.Ret);
        })(array, index);
    }

    // `array[index] = value`, written by `stelem <element>`.
    private static void Store<TArray, TValue>(Type element, TArray array, int index, TValue value)
    {
        Make<Action<TArray, int, TValue>>(null, new[] { typeof(TArray), typeof(int), typeof(TValue) }, il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Stelem, element);
            il.Emit(OpCodes.Ret);
        })(array, index, value);
    }

    // `ldelem <t>` of a null array at index 0, popped; then 1.
    private static object LdelemOfNull(Type t)
    {
        return Make<Func<int>>(typeof(int), Type.EmptyTypes, il =>
        {
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldelem, t);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ret);
        })();
    }

    // `ldelem <t>` of index 0 of a fresh `int[1]`, popped; then 1.
    private static object LdelemOfIntArray(Type t)
    {
        return Make<Func<int>>(typeof(int), Type.EmptyTypes, il =>
        {
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Newarr, typeof(int));
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldelem, t);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ret);
        })();
    }

    // `stelem <t>` of a zero (or of null) into a null array at index 0; then 1.
    private static object StelemIntoNull(Type t, bool nullValue)
    {
        return Make<Func<int>>(typeof(int), Type.EmptyTypes, il =>
        {
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(nullValue ? OpCodes.Ldnull : OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stelem, t);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ret);
        })();
    }

    // `ldelem <t>` with nothing on the stack. The JIT refuses the operand before it looks at the
    // stack.
    private static object BareLdelem(Type t)
    {
        return Make<Func<int>>(typeof(int), Type.EmptyTypes, il =>
        {
            il.Emit(OpCodes.Ldelem, t);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ret);
        })();
    }

    private static List<object> Tokens(ILGenerator il)
    {
        object scope = il.GetType().GetField("m_scope", Any).GetValue(il);
        return (List<object>) scope.GetType().GetField("m_tokens", Any).GetValue(scope);
    }

    // The slot holding `t`'s handle, or -1 if none or several do.
    private static int SlotOf(List<object> tokens, Type t)
    {
        int found = -1;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] is RuntimeTypeHandle h && h.Equals(t.TypeHandle))
            {
                if (found >= 0)
                {
                    return -1;
                }
                found = i;
            }
        }
        return found;
    }

    // `ldelem int32` of a null array (or `stelem int32` of 0 into one), with the scope entry for
    // `int` replaced after `CreateDelegate` and before the first invocation, which is when the
    // body is compiled. `replacement` is not written when `replace` is false.
    private static object Rewritten(OpCode op, bool replace, object replacement)
    {
        Func<int> f = Make<Func<int>>(typeof(int), Type.EmptyTypes, il =>
        {
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldc_I4_0);
            if (op == OpCodes.Stelem)
            {
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Stelem, typeof(int));
                il.Emit(OpCodes.Ldc_I4_1);
            }
            else
            {
                il.Emit(OpCodes.Ldelem, typeof(int));
            }
            il.Emit(OpCodes.Ret);
        });
        List<object> tokens = Tokens(lastIl);
        int slot = SlotOf(tokens, typeof(int));
        if (slot < 1)
        {
            // Not an exception any check expects, so the check fails.
            throw new InvalidOperationException("the scope does not hold exactly one entry for int");
        }
        if (replace)
        {
            tokens[slot] = replacement;
        }
        return f();
    }

    public static int Main(string[] args)
    {
        Type byrefInt = typeof(int).MakeByRefType();
        Type openDefinition = typeof(List<>);
        Type genericParameter = typeof(List<>).GetGenericArguments()[0];
        Type openConstruction = typeof(List<>).MakeGenericType(typeof(Dictionary<,>).GetGenericArguments()[0]);

        // 1. The issue's program, in an anonymously hosted dynamic method: `ldelem DateTime`, then
        // `stelem object` and `ldelem object`.
        DynamicMethod issue = new DynamicMethod("e", typeof(object), new[] { typeof(object[]), typeof(DateTime[]) });
        ILGenerator issueIl = issue.GetILGenerator();
        issueIl.Emit(OpCodes.Ldarg_0);
        issueIl.Emit(OpCodes.Ldc_I4_1);
        issueIl.Emit(OpCodes.Ldarg_1);
        issueIl.Emit(OpCodes.Ldc_I4_0);
        issueIl.Emit(OpCodes.Ldelem, typeof(DateTime));
        issueIl.Emit(OpCodes.Box, typeof(DateTime));
        issueIl.Emit(OpCodes.Stelem, typeof(object));
        issueIl.Emit(OpCodes.Ldarg_0);
        issueIl.Emit(OpCodes.Ldc_I4_1);
        issueIl.Emit(OpCodes.Ldelem, typeof(object));
        issueIl.Emit(OpCodes.Ret);
        var issueDelegate = (Func<object[], DateTime[], object>) issue.CreateDelegate(typeof(Func<object[], DateTime[], object>));
        if (((DateTime) issueDelegate(new object[2], new[] { new DateTime(2020, 1, 2) })).Year != 2020)
        {
            return 1;
        }

        // 2 to 22. Each element type read from an array the caller filled, or written to one it
        // then reads.
        if (Load<string[], string>(typeof(string), new[] { "a", "b" }, 1) != "b")
        {
            return 2;
        }

        string[] strings = new string[2];
        Store(typeof(string), strings, 1, "z");
        if (strings[0] != null || strings[1] != "z")
        {
            return 3;
        }

        if (Load<int[], int>(typeof(int), new[] { 5, 6 }, 1) != 6)
        {
            return 4;
        }

        int[] ints = new int[2];
        Store(typeof(int), ints, 1, 99);
        if (ints[0] != 0 || ints[1] != 99)
        {
            return 5;
        }

        // 6 to 8. The narrow integers widen as their own type says: sign-extended, zero-extended,
        // and zero-extended.
        if (Load<short[], int>(typeof(short), new short[] { -2 }, 0) != -2)
        {
            return 6;
        }

        if (Load<byte[], int>(typeof(byte), new byte[] { 200 }, 0) != 200)
        {
            return 7;
        }

        if (Load<char[], int>(typeof(char), new[] { '￾' }, 0) != 65534)
        {
            return 8;
        }

        // 9. `stelem int16` truncates the `int32` it is given.
        short[] shorts = new short[1];
        Store(typeof(short), shorts, 0, 0x12345);
        if (shorts[0] != 0x2345)
        {
            return 9;
        }

        if (Load<long[], long>(typeof(long), new[] { 0x123456789AL }, 0) != 0x123456789AL)
        {
            return 10;
        }

        if (Load<float[], float>(typeof(float), new[] { 1.5f }, 0) != 1.5f)
        {
            return 11;
        }

        double[] doubles = new double[1];
        Store(typeof(double), doubles, 0, 2.25);
        if (doubles[0] != 2.25)
        {
            return 12;
        }

        // 13, 14. A struct wider than any primitive.
        Big loadedBig = Load<Big[], Big>(typeof(Big), new[] { new Big { A = 1 }, new Big { A = 2, B = 3, C = 4 } }, 1);
        if (loadedBig.A != 2 || loadedBig.B != 3 || loadedBig.C != 4)
        {
            return 13;
        }

        Big[] bigs = new Big[2];
        Store(typeof(Big), bigs, 1, new Big { A = 5, B = 6, C = 7 });
        if (bigs[0].A != 0 || bigs[0].B != 0 || bigs[0].C != 0 || bigs[1].A != 5 || bigs[1].B != 6 || bigs[1].C != 7)
        {
            return 14;
        }

        // 15, 16. A struct holding a reference.
        WithRef loadedWithRef = Load<WithRef[], WithRef>(typeof(WithRef), new[] { new WithRef { S = "q", N = 8 } }, 0);
        if (loadedWithRef.S != "q" || loadedWithRef.N != 8)
        {
            return 15;
        }

        WithRef[] withRefs = new WithRef[1];
        Store(typeof(WithRef), withRefs, 0, new WithRef { S = "r", N = 9 });
        if (withRefs[0].S != "r" || withRefs[0].N != 9)
        {
            return 16;
        }

        // 17. A generic class instantiation.
        List<int> list = new List<int> { 4 };
        if (!ReferenceEquals(Load<List<int>[], List<int>>(typeof(List<int>), new[] { list }, 0), list))
        {
            return 17;
        }

        // 18, 19. A generic struct instantiation.
        KeyValuePair<int, string> loadedPair = Load<KeyValuePair<int, string>[], KeyValuePair<int, string>>(
            typeof(KeyValuePair<int, string>),
            new[] { new KeyValuePair<int, string>(3, "t") },
            0);
        if (loadedPair.Key != 3 || loadedPair.Value != "t")
        {
            return 18;
        }

        var pairs = new KeyValuePair<int, string>[1];
        Store(typeof(KeyValuePair<int, string>), pairs, 0, new KeyValuePair<int, string>(4, "u"));
        if (pairs[0].Key != 4 || pairs[0].Value != "u")
        {
            return 19;
        }

        // 20 to 22. An enum, a nullable and a pointer.
        if (Load<DayOfWeek[], DayOfWeek>(typeof(DayOfWeek), new[] { DayOfWeek.Friday }, 0) != DayOfWeek.Friday)
        {
            return 20;
        }

        int? loadedNullable = Load<int?[], int?>(typeof(int?), new int?[] { null, 12 }, 1);
        if (!loadedNullable.HasValue || loadedNullable.Value != 12)
        {
            return 21;
        }

        int pointee = 77;
        int*[] pointers = new int*[] { &pointee };
        Func<int*[], int> throughPointer = Make<Func<int*[], int>>(typeof(int), new[] { typeof(int*[]) }, il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldelem, typeof(int*));
            il.Emit(OpCodes.Ldind_I4);
            il.Emit(OpCodes.Ret);
        });
        if (throughPointer(pointers) != 77)
        {
            return 22;
        }

        // 23 to 28. The array's element type is not the operand's. `ldelem` reads the element
        // whatever the operand says, and `stelem` of a reference makes the covariant store check
        // against the array, not against the operand.
        string stored = "s";
        if (!ReferenceEquals(Load<string[], object>(typeof(object), new[] { stored }, 0), stored))
        {
            return 23;
        }

        string[] covariant = new string[1];
        if (!Throws(typeof(ArrayTypeMismatchException), null, () =>
            {
                Store<object[], object>(typeof(object), covariant, 0, new object());
                return null;
            }) || covariant[0] != null)
        {
            return 24;
        }

        string[] viaObjects = new string[1];
        Store<object[], string>(typeof(string), viaObjects, 0, "fine");
        if (viaObjects[0] != "fine")
        {
            return 25;
        }

        if (Load<uint[], int>(typeof(int), new[] { 0xFFFFFFFFu }, 0) != -1)
        {
            return 26;
        }

        if (Load<DayOfWeek[], int>(typeof(int), new[] { DayOfWeek.Saturday }, 0) != 6)
        {
            return 27;
        }

        DayOfWeek[] days = new DayOfWeek[1];
        Store(typeof(int), days, 0, 2);
        if (days[0] != DayOfWeek.Tuesday)
        {
            return 28;
        }

        // 29, 30. The faults of an access the operand allows.
        if (!Throws(typeof(NullReferenceException), null, () => Load<int[], int>(typeof(int), null, 0)))
        {
            return 29;
        }

        if (!Throws(typeof(IndexOutOfRangeException), null, () => Load<int[], int>(typeof(int), new int[1], 5)))
        {
            return 30;
        }

        // 31 to 45. An operand the JIT refuses: `System.Void`, a byref, an open generic definition,
        // a bare generic parameter and an open construction. Each is refused under three bodies
        // whose operand would otherwise fault, run, and fault. With the controls 46 to 52, this
        // keeps the refusal from widening to every value type or to the byref-like types.
        if (!Throws(typeof(InvalidProgramException), InvalidProgram, () => LdelemOfNull(typeof(void))))
        {
            return 31;
        }

        if (!Throws(typeof(InvalidProgramException), InvalidProgram, () => LdelemOfIntArray(typeof(void))))
        {
            return 32;
        }

        if (!Throws(typeof(InvalidProgramException), InvalidProgram, () => StelemIntoNull(typeof(void), false)))
        {
            return 33;
        }

        if (!Throws(typeof(InvalidProgramException), InvalidProgram, () => LdelemOfNull(byrefInt)))
        {
            return 34;
        }

        if (!Throws(typeof(InvalidProgramException), InvalidProgram, () => LdelemOfIntArray(byrefInt)))
        {
            return 35;
        }

        if (!Throws(typeof(InvalidProgramException), InvalidProgram, () => StelemIntoNull(byrefInt, false)))
        {
            return 36;
        }

        if (!Throws(typeof(InvalidProgramException), InvalidProgram, () => LdelemOfNull(openDefinition)))
        {
            return 37;
        }

        if (!Throws(typeof(InvalidProgramException), InvalidProgram, () => LdelemOfIntArray(openDefinition)))
        {
            return 38;
        }

        if (!Throws(typeof(InvalidProgramException), InvalidProgram, () => StelemIntoNull(openDefinition, true)))
        {
            return 39;
        }

        if (!Throws(typeof(InvalidProgramException), InvalidProgram, () => LdelemOfNull(genericParameter)))
        {
            return 40;
        }

        if (!Throws(typeof(InvalidProgramException), InvalidProgram, () => LdelemOfIntArray(genericParameter)))
        {
            return 41;
        }

        if (!Throws(typeof(InvalidProgramException), InvalidProgram, () => StelemIntoNull(genericParameter, true)))
        {
            return 42;
        }

        if (!Throws(typeof(InvalidProgramException), InvalidProgram, () => LdelemOfNull(openConstruction)))
        {
            return 43;
        }

        if (!Throws(typeof(InvalidProgramException), InvalidProgram, () => LdelemOfIntArray(openConstruction)))
        {
            return 44;
        }

        if (!Throws(typeof(InvalidProgramException), InvalidProgram, () => StelemIntoNull(openConstruction, true)))
        {
            return 45;
        }

        // 46 to 48. The same three bodies over `int`, which the JIT does not refuse.
        if (!Throws(typeof(NullReferenceException), null, () => LdelemOfNull(typeof(int))))
        {
            return 46;
        }

        if ((int) LdelemOfIntArray(typeof(int)) != 1)
        {
            return 47;
        }

        if (!Throws(typeof(NullReferenceException), null, () => StelemIntoNull(typeof(int), false)))
        {
            return 48;
        }

        // 49 to 52. Nor does it refuse a byref-like operand, though no array can hold one.
        if (!Throws(typeof(NullReferenceException), null, () => LdelemOfNull(typeof(Span<int>))))
        {
            return 49;
        }

        if (!Throws(typeof(NullReferenceException), null, () => StelemIntoNull(typeof(Span<int>), false)))
        {
            return 50;
        }

        if (!Throws(typeof(NullReferenceException), null, () => LdelemOfNull(typeof(TypedReference))))
        {
            return 51;
        }

        if (!Throws(typeof(NullReferenceException), null, () => StelemIntoNull(typeof(TypedReference), false)))
        {
            return 52;
        }

        // 53, 54. A refused operand with nothing on the stack: the refusal comes first.
        if (!Throws(typeof(InvalidProgramException), InvalidProgram, () => BareLdelem(typeof(void))))
        {
            return 53;
        }

        if (!Throws(typeof(InvalidProgramException), InvalidProgram, () => BareLdelem(openDefinition)))
        {
            return 54;
        }

        // 55 to 68. The scope entry rewritten through private reflection after `CreateDelegate`,
        // for `ldelem` (55 to 61) and then for `stelem` (62 to 68). The first of each seven is the
        // control, which leaves the entry as it was; the entries that are not types change the
        // answer from the control's, so the rewrite reaches the entry the instruction reads.
        MethodInfo helper = typeof(Program).GetMethod("Helper");
        FieldInfo field = typeof(Program).GetField("SField");

        if (!Throws(typeof(NullReferenceException), null, () => Rewritten(OpCodes.Ldelem, false, null)))
        {
            return 55;
        }

        if (!Throws(typeof(BadImageFormatException), "Bad class token.", () => Rewritten(OpCodes.Ldelem, true, "not a type")))
        {
            return 56;
        }

        if (!Throws(typeof(InvalidProgramException), InvalidProgram, () => Rewritten(OpCodes.Ldelem, true, null)))
        {
            return 57;
        }

        if (!Throws(typeof(BadImageFormatException), "Bad class token.", () => Rewritten(OpCodes.Ldelem, true, default(RuntimeTypeHandle))))
        {
            return 58;
        }

        if (!Throws(typeof(BadImageFormatException), "Bad class token.", () => Rewritten(OpCodes.Ldelem, true, helper.MethodHandle)))
        {
            return 59;
        }

        if (!Throws(typeof(BadImageFormatException), "Bad class token.", () => Rewritten(OpCodes.Ldelem, true, field.FieldHandle)))
        {
            return 60;
        }

        if (!Throws(typeof(InvalidProgramException), InvalidProgram, () => Rewritten(OpCodes.Ldelem, true, typeof(void).TypeHandle)))
        {
            return 61;
        }

        if (!Throws(typeof(NullReferenceException), null, () => Rewritten(OpCodes.Stelem, false, null)))
        {
            return 62;
        }

        if (!Throws(typeof(BadImageFormatException), "Bad class token.", () => Rewritten(OpCodes.Stelem, true, "not a type")))
        {
            return 63;
        }

        if (!Throws(typeof(InvalidProgramException), InvalidProgram, () => Rewritten(OpCodes.Stelem, true, null)))
        {
            return 64;
        }

        if (!Throws(typeof(BadImageFormatException), "Bad class token.", () => Rewritten(OpCodes.Stelem, true, default(RuntimeTypeHandle))))
        {
            return 65;
        }

        if (!Throws(typeof(BadImageFormatException), "Bad class token.", () => Rewritten(OpCodes.Stelem, true, helper.MethodHandle)))
        {
            return 66;
        }

        if (!Throws(typeof(BadImageFormatException), "Bad class token.", () => Rewritten(OpCodes.Stelem, true, field.FieldHandle)))
        {
            return 67;
        }

        if (!Throws(typeof(InvalidProgramException), InvalidProgram, () => Rewritten(OpCodes.Stelem, true, typeof(void).TypeHandle)))
        {
            return 68;
        }

        // 69, 70. The `System.Linq.Expressions` compiler emits `ldelem <type>` and `stelem <type>`
        // for an element that is a value type with no primitive type code, into a dynamic method
        // (which has no declaring type).
        ParameterExpression dates = Expression.Parameter(typeof(DateTime[]), "a");
        Func<DateTime[], DateTime> arrayIndex = Expression.Lambda<Func<DateTime[], DateTime>>(
            Expression.ArrayIndex(dates, Expression.Constant(1)),
            dates).Compile();
        if (arrayIndex(new[] { new DateTime(2001, 1, 1), new DateTime(2002, 2, 2) }).Year != 2002
            || arrayIndex.Method.DeclaringType != null)
        {
            return 69;
        }

        ParameterExpression first = Expression.Parameter(typeof(Big), "x");
        ParameterExpression second = Expression.Parameter(typeof(Big), "y");
        Func<Big, Big, Big[]> newArrayInit = Expression.Lambda<Func<Big, Big, Big[]>>(
            Expression.NewArrayInit(typeof(Big), first, second),
            first,
            second).Compile();
        Big[] initialised = newArrayInit(new Big { A = 1, B = 2, C = 9 }, new Big { A = 1, B = 2, C = 3 });
        if (initialised.Length != 2 || initialised[0].C != 9 || initialised[1].C != 3)
        {
            return 70;
        }

        // 71 to 73. The loaded value handed to an instruction whose operand types must match the
        // stack's.
        Func<long[], long> addLong = Make<Func<long[], long>>(typeof(long), new[] { typeof(long[]) }, il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldelem, typeof(long));
            il.Emit(OpCodes.Ldc_I8, 5L);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Ret);
        });
        if (addLong(new[] { 0x100000000L }) != 0x100000005L)
        {
            return 71;
        }

        Func<double[], double> addDouble = Make<Func<double[], double>>(typeof(double), new[] { typeof(double[]) }, il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldelem, typeof(double));
            il.Emit(OpCodes.Ldc_R8, 2.5);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Ret);
        });
        if (addDouble(new[] { 1.25 }) != 3.75)
        {
            return 72;
        }

        Func<Big[], long> thirdField = Make<Func<Big[], long>>(typeof(long), new[] { typeof(Big[]) }, il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ldelem, typeof(Big));
            il.Emit(OpCodes.Ldfld, typeof(Big).GetField("C"));
            il.Emit(OpCodes.Ret);
        });
        if (thirdField(new[] { new Big { C = 10 }, new Big { A = 11, B = 12, C = 13 } }) != 13)
        {
            return 73;
        }

        // 74, 75. A jagged array, whose operand is itself an array type.
        Func<int[][], int> innerLength = Make<Func<int[][], int>>(typeof(int), new[] { typeof(int[][]) }, il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ldelem, typeof(int[]));
            il.Emit(OpCodes.Ldlen);
            il.Emit(OpCodes.Conv_I4);
            il.Emit(OpCodes.Ret);
        });
        if (innerLength(new[] { new int[2], new int[3] }) != 3)
        {
            return 74;
        }

        int[][] jagged = new int[2][];
        int[] inner = new[] { 8, 9 };
        Store(typeof(int[]), jagged, 1, inner);
        if (jagged[0] != null || !ReferenceEquals(jagged[1], inner))
        {
            return 75;
        }

        // 76. `stelem` of null over a reference-type operand, into an array that is not null.
        Func<string[], int> storeNull = Make<Func<string[], int>>(typeof(int), new[] { typeof(string[]) }, il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Stelem, typeof(string));
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ret);
        });
        string[] toClear = new[] { "x", "y" };
        if (storeNull(toClear) != 1 || toClear[0] != "x" || toClear[1] != null)
        {
            return 76;
        }

        return 0;
    }
}
