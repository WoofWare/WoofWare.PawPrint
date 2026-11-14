using System;
using System.Reflection;
using System.Reflection.Emit;

// `TypeBuilder_SetParamInfo`, through `MethodBuilder.DefineParameter` and `ConstructorBuilder.DefineParameter`, and
// the parameters read back through reflection on the created types. Each module is its own, so that its tokens are
// literals: `Ref`, the managed refusals and their order, and names that are null, empty, hold a NUL or a surrogate
// pair; `Flags`, every bit of the attributes and the specials, through the managed mask of the reserved bits;
// `Order`, positions out of order, and parameters interleaved across methods and types; `Dup`, two rows of one
// position and the malformed shapes `GetParameters` refuses; `Ctor`, a constructor's parameters and refusals; `Tok`,
// the MethodDef tokens a parameter forces; `Gen`, a generic type's and a generic method's; `Iface`, an interface's
// `out` parameter; `Copy`, a copy of an interface's parameters as Castle DynamicProxy makes one; and four modules in
// which the `ParamPtr` table starts in each way CoreCLR starts it without stranding a MethodDef row. Positions that do
// not fit sixteen bits are `TypeBuilderDefineParameterWide.cs`'s. The test's terminal-state check reads the Param rows
// of `Order`, `Dup` and `DirectTableThenSlide`. Measured on .NET 10 and 11 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public interface ICopied
{
    int Add (int left, int right);
    void Named (string first, out int second, int third = 7);
}

public static class Program
{
    const BindingFlags All =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    const string RangeMessage = "The specified parameter index is not in range.";

    static ModuleBuilder NewModule (string name) =>
        AssemblyBuilder.DefineDynamicAssembly (new AssemblyName (name), AssemblyBuilderAccess.Run).DefineDynamicModule (name);

    /// The token `TypeBuilder_SetParamInfo` handed back, which `RuntimeParameterBuilder` keeps privately.
    static int Token (ParameterBuilder parameter) =>
        (int) parameter.GetType ().GetField ("_token", BindingFlags.NonPublic | BindingFlags.Instance).GetValue (parameter);

    /// A public static method returning `default` of `ret`.
    static MethodBuilder Static (TypeBuilder type, string name, Type ret, params Type[] parameters)
    {
        MethodBuilder method = type.DefineMethod (name, MethodAttributes.Public | MethodAttributes.Static, ret, parameters);
        ILGenerator il = method.GetILGenerator ();
        if (ret == typeof (int))
        {
            il.Emit (OpCodes.Ldc_I4_7);
        }
        else if (ret != typeof (void))
        {
            il.Emit (OpCodes.Ldnull);
        }

        il.Emit (OpCodes.Ret);
        return method;
    }

    /// Whether `action` throws exactly `T`, and, for an `ArgumentException`, with this `ParamName`.
    static bool Throws<T> (Action action, string paramName = null) where T : Exception
    {
        try
        {
            action ();
            return false;
        }
        catch (Exception e)
        {
            if (e.GetType () != typeof (T))
            {
                return false;
            }

            return paramName == null || ((ArgumentException) (object) e).ParamName == paramName;
        }
    }

    static bool Is (ParameterInfo p, int position, string name, int attributes, int token) =>
        p != null && p.Position == position && p.Name == name && (int) p.Attributes == attributes && p.MetadataToken == token;

    /// The three-method type of the four `ParamPtr` modules: `X(int, int)`, `Y(int, int)` and `Z(int)`.
    static MethodBuilder[] ThreeMethods (TypeBuilder type) =>
        new[]
        {
            Static (type, "X", typeof (int), typeof (int), typeof (int)),
            Static (type, "Y", typeof (int), typeof (int), typeof (int)),
            Static (type, "Z", typeof (int), typeof (int)),
        };

    /// Defines `position` on `method`, and whether the token is `0x08000000 | rid`.
    static bool Define (MethodBuilder method, int position, string name, int rid) =>
        Token (method.DefineParameter (position, ParameterAttributes.None, name)) == (0x08000000 | rid);

    static int Ref ()
    {
        ModuleBuilder module = NewModule ("Ref");
        TypeBuilder a = module.DefineType ("A", TypeAttributes.Public);
        Type i = typeof (int);
        MethodBuilder m = Static (a, "M", typeof (int), i, i, i, i, i, i);
        ParameterBuilder first = m.DefineParameter (1, ParameterAttributes.None, "first");
        if (Token (first) != 0x08000001 || m.MetadataToken != 0x06000001 || first.Name != "first" || first.Position != 1)
        {
            return 1;
        }

        if (!Throws<ArgumentOutOfRangeException> (() => m.DefineParameter (-1, ParameterAttributes.None, "x"), "position")
            || !Throws<ArgumentOutOfRangeException> (() => m.DefineParameter (int.MinValue, ParameterAttributes.None, "x"), "position"))
        {
            return 2;
        }

        if (!Throws<ArgumentOutOfRangeException> (() => m.DefineParameter (7, ParameterAttributes.None, "x"), RangeMessage)
            || !Throws<ArgumentOutOfRangeException> (() => m.DefineParameter (int.MaxValue, ParameterAttributes.None, "x"), RangeMessage))
        {
            return 3;
        }

        ParameterBuilder ret = m.DefineParameter (0, ParameterAttributes.None, "ret");
        ParameterBuilder nullName = m.DefineParameter (2, ParameterAttributes.None, null);
        ParameterBuilder empty = m.DefineParameter (3, ParameterAttributes.None, "");
        ParameterBuilder leadingNul = m.DefineParameter (4, ParameterAttributes.None, "\0x");
        ParameterBuilder innerNul = m.DefineParameter (5, ParameterAttributes.None, "a\0b");
        ParameterBuilder unicode = m.DefineParameter (6, ParameterAttributes.None, "é中\U0001F600");
        if (Token (ret) != 0x08000002
            || Token (nullName) != 0x08000003
            || Token (empty) != 0x08000004
            || Token (leadingNul) != 0x08000005
            || Token (innerNul) != 0x08000006
            || Token (unicode) != 0x08000007)
        {
            return 4;
        }

        if (nullName.Name != null || empty.Name != "" || leadingNul.Name != "\0x" || innerNul.Name != "a\0b" || unicode.Name != "é中\U0001F600")
        {
            return 5;
        }

        if (!Throws<NotSupportedException> (() => m.GetParameters ())
            || !Throws<InvalidOperationException> (() => { ParameterInfo _ = m.ReturnParameter; }))
        {
            return 6;
        }

        MethodBuilder noParams = Static (a, "NoParams", typeof (void));
        if (!Throws<ArgumentOutOfRangeException> (() => noParams.DefineParameter (1, ParameterAttributes.None, "x"), RangeMessage))
        {
            return 7;
        }

        ParameterBuilder retval = noParams.DefineParameter (0, ParameterAttributes.Retval, "r");
        if (Token (retval) != 0x08000008 || retval.Attributes != (int) ParameterAttributes.Retval)
        {
            return 8;
        }

        MethodBuilder later = a.DefineMethod ("Later", MethodAttributes.Public | MethodAttributes.Static);
        if (!Throws<ArgumentOutOfRangeException> (() => later.DefineParameter (1, ParameterAttributes.None, "x"), RangeMessage))
        {
            return 9;
        }

        later.SetParameters (typeof (long));
        later.SetReturnType (typeof (void));
        later.GetILGenerator ().Emit (OpCodes.Ret);
        if (Token (later.DefineParameter (1, ParameterAttributes.None, "x")) != 0x08000009)
        {
            return 10;
        }

        Type created = a.CreateType ();

        if (!Throws<ArgumentOutOfRangeException> (() => m.DefineParameter (-1, ParameterAttributes.None, "x"), "position")
            || !Throws<InvalidOperationException> (() => m.DefineParameter (7, ParameterAttributes.None, "x"))
            || !Throws<InvalidOperationException> (() => m.DefineParameter (1, ParameterAttributes.None, "x")))
        {
            return 11;
        }

        TypeBuilder b = module.DefineType ("B", TypeAttributes.Public);
        MethodBuilder n = Static (b, "N", typeof (void), i);
        if (Token (n.DefineParameter (1, ParameterAttributes.None, "n")) != 0x0800000A)
        {
            return 12;
        }

        b.CreateType ();

        MethodInfo createdM = created.GetMethod ("M");
        ParameterInfo[] ps = createdM.GetParameters ();
        if (ps.Length != 6
            || !Is (ps[0], 0, "first", 0, 0x08000001)
            || !Is (ps[1], 1, "", 0, 0x08000003)
            || !Is (ps[2], 2, "", 0, 0x08000004)
            || !Is (ps[3], 3, "", 0, 0x08000005)
            || !Is (ps[4], 4, "a", 0, 0x08000006)
            || !Is (ps[5], 5, "é中\U0001F600", 0, 0x08000007))
        {
            return 13;
        }

        if (!Is (createdM.ReturnParameter, -1, "ret", 0, 0x08000002))
        {
            return 14;
        }

        ParameterInfo noParamsRet = created.GetMethod ("NoParams").ReturnParameter;
        if (!Is (noParamsRet, -1, "r", 0x0008, 0x08000008) || !noParamsRet.IsRetval)
        {
            return 15;
        }

        MethodInfo createdLater = created.GetMethod ("Later");
        ParameterInfo[] laterPs = createdLater.GetParameters ();
        if (laterPs.Length != 1
            || !Is (laterPs[0], 0, "x", 0, 0x08000009)
            || laterPs[0].ParameterType != typeof (long)
            || !Is (createdLater.ReturnParameter, -1, null, 0, 0x08000000))
        {
            return 16;
        }

        return 0;
    }

    static int Flags ()
    {
        ModuleBuilder module = NewModule ("Flags");
        TypeBuilder f = module.DefineType ("F", TypeAttributes.Public);
        int[] values = new int[40];
        for (int bit = 0; bit < 32; bit++)
        {
            values[bit] = 1 << bit;
        }

        int[] specials = { 0, 0xFFFF, -1, 0x1000, 0x2000, 0xF000, 0x7FFF0013, 0x0FFF };
        Array.Copy (specials, 0, values, 32, specials.Length);
        Type[] types = new Type[values.Length];
        for (int k = 0; k < types.Length; k++)
        {
            types[k] = typeof (int);
        }

        MethodBuilder m = Static (f, "M", typeof (int), types);
        for (int k = 0; k < values.Length; k++)
        {
            ParameterBuilder p = m.DefineParameter (k + 1, (ParameterAttributes) values[k], "p" + k);
            if (Token (p) != 0x08000001 + k || p.Attributes != (values[k] & ~0xF000))
            {
                return 30;
            }
        }

        ParameterBuilder ret = m.DefineParameter (0, (ParameterAttributes) (-1), "ret");
        if (Token (ret) != 0x08000029 || ret.Attributes != unchecked ((int) 0xFFFF0FFF))
        {
            return 31;
        }

        MethodInfo created = f.CreateType ().GetMethod ("M");
        ParameterInfo[] ps = created.GetParameters ();
        if (ps.Length != values.Length)
        {
            return 32;
        }

        for (int k = 0; k <= values.Length; k++)
        {
            ParameterInfo p = k < values.Length ? ps[k] : created.ReturnParameter;
            int row = k < values.Length ? values[k] & 0x0FFF : 0x0FFF;
            string name = k < values.Length ? "p" + k : "ret";
            if (!Is (p, k < values.Length ? k : -1, name, row, 0x08000001 + k))
            {
                return 33;
            }

            if (p.IsIn != ((row & 0x1) != 0)
                || p.IsOut != ((row & 0x2) != 0)
                || p.IsLcid != ((row & 0x4) != 0)
                || p.IsRetval != ((row & 0x8) != 0)
                || p.IsOptional != ((row & 0x10) != 0))
            {
                return 34;
            }

            object expected = (row & 0x10) != 0 ? Type.Missing : DBNull.Value;
            if (p.HasDefaultValue || p.DefaultValue != expected || p.RawDefaultValue != expected)
            {
                return 35;
            }
        }

        return 0;
    }

    static int Order ()
    {
        ModuleBuilder module = NewModule ("Order");
        TypeBuilder a = module.DefineType ("A", TypeAttributes.Public);
        MethodBuilder m = Static (a, "M", typeof (int), typeof (int), typeof (string), typeof (long));
        if (!Define (m, 3, "c", 1) || !Define (m, 1, "a", 2) || !Define (m, 0, "ret", 3) || !Define (m, 2, "b", 4))
        {
            return 50;
        }

        TypeBuilder p = module.DefineType ("P", TypeAttributes.Public);
        TypeBuilder q = module.DefineType ("Q", TypeAttributes.Public);
        MethodBuilder p1 = Static (p, "p1", typeof (void), typeof (int), typeof (int));
        MethodBuilder p2 = Static (p, "p2", typeof (void), typeof (int));
        MethodBuilder q1 = Static (q, "q1", typeof (int), typeof (int));
        if (!Define (p2, 1, "w", 5) || !Define (p1, 1, "x", 6) || !Define (q1, 1, "z", 7) || !Define (p1, 2, "y", 8))
        {
            return 51;
        }

        Type pc = p.CreateType ();
        if (!Define (q1, 0, "qret", 9))
        {
            return 52;
        }

        if (m.MetadataToken != 0x06000001 || p1.MetadataToken != 0x06000002 || p2.MetadataToken != 0x06000003 || q1.MetadataToken != 0x06000004)
        {
            return 53;
        }

        MethodInfo am = a.CreateType ().GetMethod ("M");
        ParameterInfo[] ps = am.GetParameters ();
        if (ps.Length != 3
            || !Is (ps[0], 0, "a", 0, 0x08000002)
            || !Is (ps[1], 1, "b", 0, 0x08000004)
            || !Is (ps[2], 2, "c", 0, 0x08000001)
            || !Is (am.ReturnParameter, -1, "ret", 0, 0x08000003))
        {
            return 54;
        }

        ParameterInfo[] p1s = pc.GetMethod ("p1").GetParameters ();
        ParameterInfo[] p2s = pc.GetMethod ("p2").GetParameters ();
        if (p1s.Length != 2
            || !Is (p1s[0], 0, "x", 0, 0x08000006)
            || !Is (p1s[1], 1, "y", 0, 0x08000008)
            || p2s.Length != 1
            || !Is (p2s[0], 0, "w", 0, 0x08000005))
        {
            return 55;
        }

        MethodInfo qm = q.CreateType ().GetMethod ("q1");
        ParameterInfo[] q1s = qm.GetParameters ();
        if (q1s.Length != 1 || !Is (q1s[0], 0, "z", 0, 0x08000007) || !Is (qm.ReturnParameter, -1, "qret", 0, 0x08000009))
        {
            return 56;
        }

        return 0;
    }

    static int Dup ()
    {
        ModuleBuilder module = NewModule ("Dup");
        TypeBuilder d = module.DefineType ("D", TypeAttributes.Public);
        MethodBuilder twoSame = Static (d, "TwoSame", typeof (void), typeof (int), typeof (int));
        if (Token (twoSame.DefineParameter (1, ParameterAttributes.Out, "first")) != 0x08000001
            || Token (twoSame.DefineParameter (1, ParameterAttributes.In, "second")) != 0x08000002)
        {
            return 70;
        }

        MethodBuilder twoReturns = Static (d, "TwoReturns", typeof (int), typeof (int));
        if (!Define (twoReturns, 0, "r1", 3) || !Define (twoReturns, 0, "r2", 4))
        {
            return 71;
        }

        MethodBuilder threeRows = Static (d, "ThreeRows", typeof (void), typeof (int));
        if (!Define (threeRows, 1, "x", 5) || !Define (threeRows, 1, "y", 6) || !Define (threeRows, 1, "z", 7))
        {
            return 72;
        }

        MethodBuilder slide = Static (d, "Slide", typeof (void), typeof (int), typeof (int));
        if (!Define (slide, 2, "b1", 8) || !Define (slide, 1, "a", 9) || !Define (slide, 2, "b2", 10))
        {
            return 73;
        }

        MethodBuilder retAndOne = Static (d, "RetAndOne", typeof (int), typeof (int));
        if (!Define (retAndOne, 1, "a", 11) || !Define (retAndOne, 0, "r", 12) || !Define (retAndOne, 1, "b", 13))
        {
            return 74;
        }

        Type c = d.CreateType ();

        ParameterInfo[] same = c.GetMethod ("TwoSame").GetParameters ();
        if (same.Length != 2 || !Is (same[0], 0, "second", 0x0001, 0x08000002) || !Is (same[1], 1, null, 0, 0x08000000))
        {
            return 75;
        }

        ParameterInfo[] returns = c.GetMethod ("TwoReturns").GetParameters ();
        if (returns.Length != 1 || returns[0] != null)
        {
            return 76;
        }

        if (!Throws<BadImageFormatException> (() => { ParameterInfo _ = c.GetMethod ("TwoReturns").ReturnParameter; }))
        {
            return 77;
        }

        if (!Throws<BadImageFormatException> (() => c.GetMethod ("ThreeRows").GetParameters ())
            || !Throws<BadImageFormatException> (() => { ParameterInfo _ = c.GetMethod ("ThreeRows").ReturnParameter; }))
        {
            return 78;
        }

        if (!Throws<BadImageFormatException> (() => c.GetMethod ("RetAndOne").GetParameters ())
            || !Throws<BadImageFormatException> (() => { ParameterInfo _ = c.GetMethod ("RetAndOne").ReturnParameter; }))
        {
            return 79;
        }

        ParameterInfo[] slid = c.GetMethod ("Slide").GetParameters ();
        if (slid.Length != 2 || !Is (slid[0], 0, "a", 0, 0x08000009) || !Is (slid[1], 1, "b2", 0, 0x0800000A))
        {
            return 80;
        }

        c.GetMethod ("ThreeRows", All).Invoke (null, new object[] { 1 });
        return 0;
    }

    static int Ctor ()
    {
        ModuleBuilder module = NewModule ("Ctor");
        TypeBuilder c = module.DefineType ("C", TypeAttributes.Public);
        ConstructorBuilder ctor = c.DefineConstructor (
            MethodAttributes.Public,
            CallingConventions.Standard,
            new[] { typeof (int), typeof (string) });
        ILGenerator il = ctor.GetILGenerator ();
        il.Emit (OpCodes.Ldarg_0);
        il.Emit (OpCodes.Call, typeof (object).GetConstructor (Type.EmptyTypes));
        il.Emit (OpCodes.Ret);

        if (Token (ctor.DefineParameter (0, ParameterAttributes.None, "ret")) != 0x08000001
            || Token (ctor.DefineParameter (2, ParameterAttributes.Optional, "s")) != 0x08000002
            || Token (ctor.DefineParameter (1, ParameterAttributes.None, "i")) != 0x08000003)
        {
            return 90;
        }

        if (!Throws<ArgumentOutOfRangeException> (() => ctor.DefineParameter (3, ParameterAttributes.None, "x"), RangeMessage)
            || !Throws<ArgumentOutOfRangeException> (() => ctor.DefineParameter (-1, ParameterAttributes.None, "x"), "position"))
        {
            return 91;
        }

        ConstructorBuilder defaultCtor = c.DefineDefaultConstructor (MethodAttributes.Public);
        if (!Throws<ArgumentOutOfRangeException> (() => defaultCtor.DefineParameter (1, ParameterAttributes.None, "x"), RangeMessage))
        {
            return 92;
        }

        ConstructorBuilder typeInitializer = c.DefineTypeInitializer ();
        typeInitializer.GetILGenerator ().Emit (OpCodes.Ret);
        if (Token (typeInitializer.DefineParameter (0, ParameterAttributes.None, "r")) != 0x08000004)
        {
            return 93;
        }

        if (ctor.MetadataToken != 0x06000001 || defaultCtor.MetadataToken != 0x06000002 || typeInitializer.MetadataToken != 0x06000003)
        {
            return 94;
        }

        Type t = c.CreateType ();
        ParameterInfo[] ps = t.GetConstructor (new[] { typeof (int), typeof (string) }).GetParameters ();
        if (ps.Length != 2
            || !Is (ps[0], 0, "i", 0, 0x08000003)
            || !Is (ps[1], 1, "s", 0x0010, 0x08000002)
            || ps[1].DefaultValue != Type.Missing
            || ps[1].HasDefaultValue)
        {
            return 95;
        }

        if (!Throws<InvalidOperationException> (() => ctor.DefineParameter (1, ParameterAttributes.None, "x")))
        {
            return 96;
        }

        if (Activator.CreateInstance (t, 1, "s") == null)
        {
            return 97;
        }

        return 0;
    }

    static int Tok ()
    {
        ModuleBuilder module = NewModule ("Tok");
        TypeBuilder a = module.DefineType ("A", TypeAttributes.Public);
        TypeBuilder b = module.DefineType ("B", TypeAttributes.Public);
        MethodBuilder a1 = Static (a, "a1", typeof (void), typeof (int));
        MethodBuilder a2 = Static (a, "a2", typeof (void), typeof (int));
        MethodBuilder b1 = Static (b, "b1", typeof (void), typeof (int));
        if (!Define (b1, 1, "x", 1) || b1.MetadataToken != 0x06000001)
        {
            return 110;
        }

        if (!Define (a2, 1, "y", 2) || a1.MetadataToken != 0x06000002 || a2.MetadataToken != 0x06000003)
        {
            return 111;
        }

        Type ac = a.CreateType ();
        Type bc = b.CreateType ();
        if (!Is (ac.GetMethod ("a1").GetParameters ()[0], 0, null, 0, 0x08000000)
            || !Is (ac.GetMethod ("a2").GetParameters ()[0], 0, "y", 0, 0x08000002)
            || !Is (bc.GetMethod ("b1").GetParameters ()[0], 0, "x", 0, 0x08000001))
        {
            return 112;
        }

        return 0;
    }

    static int Gen ()
    {
        ModuleBuilder module = NewModule ("Gen");
        TypeBuilder g = module.DefineType ("G`1", TypeAttributes.Public);
        GenericTypeParameterBuilder t = g.DefineGenericParameters ("T")[0];
        MethodBuilder m = g.DefineMethod ("M", MethodAttributes.Public, typeof (void), new Type[] { t, typeof (int) });
        m.GetILGenerator ().Emit (OpCodes.Ret);
        if (!Define (m, 1, "x", 1) || Token (m.DefineParameter (2, ParameterAttributes.Optional, "n")) != 0x08000002)
        {
            return 120;
        }

        TypeBuilder h = module.DefineType ("H", TypeAttributes.Public);
        MethodBuilder id = h.DefineMethod ("Id", MethodAttributes.Public | MethodAttributes.Static);
        GenericTypeParameterBuilder u = id.DefineGenericParameters ("U")[0];
        id.SetParameters (u);
        id.SetReturnType (u);
        ILGenerator il = id.GetILGenerator ();
        il.Emit (OpCodes.Ldarg_0);
        il.Emit (OpCodes.Ret);
        if (!Define (id, 1, "u", 3) || !Define (id, 0, "r", 4))
        {
            return 121;
        }

        Type gc = g.CreateType ();
        Type hc = h.CreateType ();
        foreach (MethodInfo method in new[] { gc.GetMethod ("M"), gc.MakeGenericType (typeof (int)).GetMethod ("M") })
        {
            ParameterInfo[] ps = method.GetParameters ();
            if (ps.Length != 2 || !Is (ps[0], 0, "x", 0, 0x08000001) || !Is (ps[1], 1, "n", 0x0010, 0x08000002))
            {
                return 122;
            }
        }

        MethodInfo open = hc.GetMethod ("Id");
        foreach (MethodInfo method in new[] { open, open.MakeGenericMethod (typeof (string)) })
        {
            ParameterInfo[] ps = method.GetParameters ();
            if (ps.Length != 1 || !Is (ps[0], 0, "u", 0, 0x08000003) || !Is (method.ReturnParameter, -1, "r", 0, 0x08000004))
            {
                return 123;
            }
        }

        if ((string) open.MakeGenericMethod (typeof (string)).Invoke (null, new object[] { "hi" }) != "hi")
        {
            return 124;
        }

        return 0;
    }

    static int Iface ()
    {
        ModuleBuilder module = NewModule ("Iface");
        TypeBuilder i = module.DefineType ("I", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        MethodBuilder im = i.DefineMethod (
            "M",
            MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
            typeof (int),
            new[] { typeof (int), typeof (int).MakeByRefType () });
        if (!Define (im, 1, "value", 1) || Token (im.DefineParameter (2, ParameterAttributes.Out, "result")) != 0x08000002)
        {
            return 140;
        }

        Type ic = i.CreateType ();
        TypeBuilder c = module.DefineType ("C", TypeAttributes.Public, typeof (object), new[] { ic });
        MethodBuilder cm = c.DefineMethod (
            "M",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.Final | MethodAttributes.NewSlot,
            typeof (int),
            new[] { typeof (int), typeof (int).MakeByRefType () });
        ILGenerator il = cm.GetILGenerator ();
        il.Emit (OpCodes.Ldarg_2);
        il.Emit (OpCodes.Ldarg_1);
        il.Emit (OpCodes.Stind_I4);
        il.Emit (OpCodes.Ldarg_1);
        il.Emit (OpCodes.Ret);
        if (Token (cm.DefineParameter (2, ParameterAttributes.Out, "r")) != 0x08000003)
        {
            return 141;
        }

        c.DefineDefaultConstructor (MethodAttributes.Public);
        Type cc = c.CreateType ();

        ParameterInfo[] ips = ic.GetMethod ("M").GetParameters ();
        if (ips.Length != 2 || !Is (ips[0], 0, "value", 0, 0x08000001) || !Is (ips[1], 1, "result", 0x0002, 0x08000002) || !ips[1].IsOut)
        {
            return 142;
        }

        ParameterInfo[] cps = cc.GetMethod ("M").GetParameters ();
        if (cps.Length != 2 || !Is (cps[0], 0, null, 0, 0x08000000) || !Is (cps[1], 1, "r", 0x0002, 0x08000003))
        {
            return 143;
        }

        object[] args = { 5, 0 };
        if ((int) ic.GetMethod ("M").Invoke (Activator.CreateInstance (cc), args) != 5 || (int) args[1] != 5)
        {
            return 144;
        }

        return 0;
    }

    static int Copy ()
    {
        ModuleBuilder module = NewModule ("Copy");
        TypeBuilder proxy = module.DefineType ("Proxy", TypeAttributes.Public, typeof (object), new[] { typeof (ICopied) });
        int rid = 0;
        foreach (MethodInfo source in typeof (ICopied).GetMethods ())
        {
            ParameterInfo[] sourceParameters = source.GetParameters ();
            Type[] types = new Type[sourceParameters.Length];
            for (int k = 0; k < types.Length; k++)
            {
                types[k] = sourceParameters[k].ParameterType;
            }

            MethodBuilder method = proxy.DefineMethod (
                source.Name,
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.Final | MethodAttributes.NewSlot,
                source.ReturnType,
                types);
            foreach (ParameterInfo p in sourceParameters)
            {
                ParameterBuilder copied = method.DefineParameter (p.Position + 1, p.Attributes, p.Name);
                rid++;
                if (Token (copied) != (0x08000000 | rid))
                {
                    return 160;
                }
            }

            ILGenerator il = method.GetILGenerator ();
            if (source.Name == "Add")
            {
                il.Emit (OpCodes.Ldarg_1);
                il.Emit (OpCodes.Ldarg_2);
                il.Emit (OpCodes.Add);
            }
            else
            {
                il.Emit (OpCodes.Ldarg_2);
                il.Emit (OpCodes.Ldarg_3);
                il.Emit (OpCodes.Stind_I4);
            }

            il.Emit (OpCodes.Ret);
        }

        proxy.DefineDefaultConstructor (MethodAttributes.Public);
        Type created = proxy.CreateType ();

        ParameterInfo[] add = created.GetMethod ("Add").GetParameters ();
        if (add.Length != 2 || !Is (add[0], 0, "left", 0, 0x08000001) || !Is (add[1], 1, "right", 0, 0x08000002))
        {
            return 161;
        }

        ParameterInfo third = typeof (ICopied).GetMethod ("Named").GetParameters ()[2];
        if ((int) third.Attributes != 0x1010)
        {
            return 162;
        }

        ParameterInfo[] named = created.GetMethod ("Named").GetParameters ();
        if (named.Length != 3
            || !Is (named[0], 0, "first", 0, 0x08000003)
            || !Is (named[1], 1, "second", 0x0002, 0x08000004)
            || !Is (named[2], 2, "third", 0x0010, 0x08000005)
            || named[2].DefaultValue != Type.Missing
            || named[2].HasDefaultValue)
        {
            return 163;
        }

        ICopied instance = (ICopied) Activator.CreateInstance (created);
        instance.Named ("f", out int second, 9);
        if (instance.Add (2, 3) != 5 || second != 9)
        {
            return 164;
        }

        return 0;
    }

    /// The table starts in the direct step, because a later method already has a row; then two slides follow on
    /// methods that are not the last MethodDef row.
    static int DirectTableThenSlide ()
    {
        ModuleBuilder module = NewModule ("DirectTableThenSlide");
        TypeBuilder t = module.DefineType ("T", TypeAttributes.Public);
        MethodBuilder[] ms = ThreeMethods (t);
        if (ms[2].MetadataToken != 0x06000003)
        {
            return 180;
        }

        if (!Define (ms[2], 1, "z", 1)
            || !Define (ms[0], 2, "b", 2)
            || !Define (ms[0], 1, "a", 3)
            || !Define (ms[1], 2, "d", 4)
            || !Define (ms[1], 1, "c", 5)
            || !Define (ms[1], 0, "r", 6)
            || !Define (ms[2], 0, "zr", 7))
        {
            return 181;
        }

        Type c = t.CreateType ();
        ParameterInfo[] x = c.GetMethod ("X").GetParameters ();
        ParameterInfo[] y = c.GetMethod ("Y").GetParameters ();
        ParameterInfo[] z = c.GetMethod ("Z").GetParameters ();
        if (!Is (x[0], 0, "a", 0, 0x08000003)
            || !Is (x[1], 1, "b", 0, 0x08000002)
            || !Is (y[0], 0, "c", 0, 0x08000005)
            || !Is (y[1], 1, "d", 0, 0x08000004)
            || !Is (c.GetMethod ("Y").ReturnParameter, -1, "r", 0, 0x08000006)
            || !Is (z[0], 0, "z", 0, 0x08000001)
            || !Is (c.GetMethod ("Z").ReturnParameter, -1, "zr", 0, 0x08000007))
        {
            return 182;
        }

        return 0;
    }

    /// One call starts the table in the direct step and then slides.
    static int DirectAndSlideOneCall ()
    {
        ModuleBuilder module = NewModule ("DirectAndSlideOneCall");
        TypeBuilder t = module.DefineType ("T", TypeAttributes.Public);
        MethodBuilder[] ms = ThreeMethods (t);
        if (ms[2].MetadataToken != 0x06000003)
        {
            return 190;
        }

        if (!Define (ms[0], 2, "b", 1)
            || !Define (ms[1], 1, "c", 2)
            || !Define (ms[0], 1, "a", 3)
            || !Define (ms[2], 1, "z", 4)
            || !Define (ms[1], 0, "r", 5))
        {
            return 191;
        }

        Type c = t.CreateType ();
        ParameterInfo[] x = c.GetMethod ("X").GetParameters ();
        ParameterInfo[] y = c.GetMethod ("Y").GetParameters ();
        if (!Is (x[0], 0, "a", 0, 0x08000003)
            || !Is (x[1], 1, "b", 0, 0x08000001)
            || !Is (y[0], 0, "c", 0, 0x08000002)
            || !Is (y[1], 1, null, 0, 0x08000000)
            || !Is (c.GetMethod ("Y").ReturnParameter, -1, "r", 0, 0x08000005)
            || !Is (c.GetMethod ("Z").GetParameters ()[0], 0, "z", 0, 0x08000004))
        {
            return 192;
        }

        return 0;
    }

    /// A slide on the method that is the last MethodDef row starts the table; methods tokenised afterwards then get
    /// parameters.
    static int SlideOnLastThenLater ()
    {
        ModuleBuilder module = NewModule ("SlideOnLastThenLater");
        TypeBuilder t = module.DefineType ("T", TypeAttributes.Public);
        MethodBuilder[] ms = ThreeMethods (t);
        if (ms[1].MetadataToken != 0x06000002)
        {
            return 200;
        }

        if (!Define (ms[1], 2, "d", 1)
            || !Define (ms[1], 1, "c", 2)
            || !Define (ms[0], 1, "a", 3)
            || !Define (ms[2], 1, "z", 4)
            || !Define (ms[2], 0, "zr", 5)
            || !Define (ms[1], 0, "r", 6))
        {
            return 201;
        }

        Type c = t.CreateType ();
        ParameterInfo[] x = c.GetMethod ("X").GetParameters ();
        ParameterInfo[] y = c.GetMethod ("Y").GetParameters ();
        if (!Is (x[0], 0, "a", 0, 0x08000003)
            || !Is (x[1], 1, null, 0, 0x08000000)
            || !Is (y[0], 0, "c", 0, 0x08000002)
            || !Is (y[1], 1, "d", 0, 0x08000001)
            || !Is (c.GetMethod ("Y").ReturnParameter, -1, "r", 0, 0x08000006)
            || !Is (c.GetMethod ("Z").GetParameters ()[0], 0, "z", 0, 0x08000004)
            || !Is (c.GetMethod ("Z").ReturnParameter, -1, "zr", 0, 0x08000005))
        {
            return 202;
        }

        return 0;
    }

    /// Two rows of one position on a method with a later MethodDef row: a tie, which does not slide.
    static int TieIsNoSlide ()
    {
        ModuleBuilder module = NewModule ("TieIsNoSlide");
        TypeBuilder t = module.DefineType ("T", TypeAttributes.Public);
        MethodBuilder[] ms = ThreeMethods (t);
        if (ms[2].MetadataToken != 0x06000003)
        {
            return 210;
        }

        if (!Define (ms[0], 1, "a1", 1) || !Define (ms[0], 1, "a2", 2) || !Define (ms[1], 1, "c", 3) || !Define (ms[2], 1, "z", 4))
        {
            return 211;
        }

        Type c = t.CreateType ();
        ParameterInfo[] x = c.GetMethod ("X").GetParameters ();
        if (!Is (x[0], 0, "a2", 0, 0x08000002)
            || !Is (x[1], 1, null, 0, 0x08000000)
            || !Is (c.GetMethod ("Y").GetParameters ()[0], 0, "c", 0, 0x08000003)
            || !Is (c.GetMethod ("Z").GetParameters ()[0], 0, "z", 0, 0x08000004))
        {
            return 212;
        }

        return 0;
    }

    public static int Main ()
    {
        Func<int>[] modules =
        {
            Ref,
            Flags,
            Order,
            Dup,
            Ctor,
            Tok,
            Gen,
            Iface,
            Copy,
            DirectTableThenSlide,
            DirectAndSlideOneCall,
            SlideOnLastThenLater,
            TieIsNoSlide,
        };

        foreach (Func<int> module in modules)
        {
            int result = module ();
            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    }
}
