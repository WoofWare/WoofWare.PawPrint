using System;
using System.Reflection;

public struct Point
{
    public int X;
    public int Y;

    public Point(int x, int y)
    {
        X = x;
        Y = y;
    }
}

public sealed class Holder
{
    public bool Flag { get; set; }
    public int Number { get; set; }
    public string Name { get; set; }
    public Point Location { get; set; }
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class TaggedAttribute : Attribute
{
    public string Label { get; set; }
    public int Weight { get; set; }
}

[Tagged(Label = "first", Weight = 17)]
public sealed class TaggedTarget
{
}

public sealed class Box
{
    public readonly int Seen;

    public Box(ref int value)
    {
        Seen = value;
        value = value * 3;
    }
}

public static class Calls
{
    public static int RefOut(ref int a, out string b)
    {
        int old = a;
        a = a + 100;
        b = "out" + old;
        return old * 2;
    }

    public static int Five(int a, int b, int c, int d, int e)
    {
        return a + 10 * b + 100 * c + 1000 * d + 10000 * e;
    }

    public static int FiveWithRef(int a, ref int b, string c, Point d, long e)
    {
        int old = b;
        b = a + d.X + d.Y;
        return old + c.Length + (int)e;
    }
}

public static class Program
{
    // CoreLib's reflection invoker runs a method's first reflective invocation through the
    // runtime, and from the second onwards, with dynamic code supported, through a `DynamicMethod`
    // it emits (`InvokerEmitUtil`). When the call passes its arguments by reference, the emitted
    // stub is the `RefArgs` one: it receives an `IntPtr*` and reads argument `i` as the
    // `ByReference.Value` field at byte offset `i * IntPtr.Size` through that pointer. The pointer's
    // referent is a `StackAllocatedByRefs` (an inline array of four by-ref fields) for at most four
    // arguments, and a stackalloc'd block of `ByReference` for more.
    //
    // Routes to that stub:
    //  * `PropertyInfo.SetValue`, which calls the setter by reference whatever its parameter type;
    //  * each named property of a custom attribute, set the same way when the attribute is built;
    //  * `MethodBase.Invoke` of a method with a by-ref parameter, through the `StackAllocatedByRefs`
    //    for few arguments and the block for more;
    //  * `ConstructorInfo.Invoke` of a constructor with a by-ref parameter.
    // `Five` has no by-ref parameter, so it takes the `ObjSpanArgs` stub instead; it is here as the
    // control on argument order in a call of more than four.
    //
    // The test harness registers this case with the dynamic-code switch set to "true", which is
    // also real .NET's default.
    //
    // Returns 0 on success, or the number of the first check that failed.
    public static int Main(string[] args)
    {
        var h = new Holder();

        PropertyInfo flag = typeof(Holder).GetProperty("Flag");
        for (int i = 0; i < 3; i++)
        {
            bool want = i % 2 == 0;
            flag.SetValue(h, want);
            if (h.Flag != want)
            {
                return 10 + i;
            }
        }

        PropertyInfo number = typeof(Holder).GetProperty("Number");
        for (int i = 0; i < 3; i++)
        {
            number.SetValue(h, i * 7 - 5);
            if (h.Number != i * 7 - 5)
            {
                return 20 + i;
            }
        }

        PropertyInfo name = typeof(Holder).GetProperty("Name");
        for (int i = 0; i < 3; i++)
        {
            string want = "name" + i;
            name.SetValue(h, want);
            if (!ReferenceEquals(h.Name, want))
            {
                return 30 + i;
            }
        }

        PropertyInfo location = typeof(Holder).GetProperty("Location");
        for (int i = 0; i < 3; i++)
        {
            location.SetValue(h, new Point(i + 1, -(i + 2)));
            if (h.Location.X != i + 1 || h.Location.Y != -(i + 2))
            {
                return 40 + i;
            }
        }

        for (int i = 0; i < 3; i++)
        {
            object[] tags = typeof(TaggedTarget).GetCustomAttributes(typeof(TaggedAttribute), false);
            if (tags.Length != 1)
            {
                return 50 + i;
            }

            var tag = (TaggedAttribute)tags[0];
            if (tag.Label != "first" || tag.Weight != 17)
            {
                return 53 + i;
            }
        }

        MethodInfo refOut = typeof(Calls).GetMethod("RefOut");
        for (int i = 0; i < 3; i++)
        {
            object[] refOutArgs = new object[] { i + 5, null };
            object result = refOut.Invoke(null, refOutArgs);
            if ((int)result != (i + 5) * 2)
            {
                return 60 + i;
            }

            if ((int)refOutArgs[0] != i + 105)
            {
                return 63 + i;
            }

            if ((string)refOutArgs[1] != "out" + (i + 5))
            {
                return 66 + i;
            }
        }

        MethodInfo five = typeof(Calls).GetMethod("Five");
        for (int i = 0; i < 3; i++)
        {
            object result = five.Invoke(null, new object[] { 1, 2, 3, 4, i + 5 });
            if ((int)result != 4321 + 10000 * (i + 5))
            {
                return 70 + i;
            }
        }

        MethodInfo fiveWithRef = typeof(Calls).GetMethod("FiveWithRef");
        for (int i = 0; i < 3; i++)
        {
            object[] fiveArgs = new object[] { i + 1, 1000 + i, "abc", new Point(20, 300), 40000L };
            object result = fiveWithRef.Invoke(null, fiveArgs);
            if ((int)result != 1000 + i + 3 + 40000)
            {
                return 80 + i;
            }

            if ((int)fiveArgs[1] != i + 1 + 20 + 300)
            {
                return 83 + i;
            }
        }

        ConstructorInfo boxCtor = typeof(Box).GetConstructor(new[] { typeof(int).MakeByRefType() });
        for (int i = 0; i < 3; i++)
        {
            object[] ctorArgs = new object[] { i + 4 };
            var box = (Box)boxCtor.Invoke(ctorArgs);
            if (box.Seen != i + 4)
            {
                return 90 + i;
            }

            if ((int)ctorArgs[0] != (i + 4) * 3)
            {
                return 93 + i;
            }
        }

        return 0;
    }
}
