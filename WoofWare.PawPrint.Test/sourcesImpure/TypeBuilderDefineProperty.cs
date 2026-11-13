using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

// `TypeBuilder_DefineProperty` and `TypeBuilder_DefineMethodSemantics`, through `TypeBuilder.DefineProperty` and
// `PropertyBuilder.SetGetMethod`, `SetSetMethod` and `AddOtherMethod`. In module `Props`: properties of two types
// defined interleaved, so that their tokens are in definition order across the module; the flags the emitter keeps;
// the signatures each calling convention gives; a duplicate, which the emitter does not look for; a property with
// two getters and three other methods; the tokens `SetGetMethod` gives methods; a property of a generic type; and,
// last, on a type of its own, a name the managed caller refuses only after the row is written. In module `Many`, the
// same two getters and three other methods once the module holds 25 MethodSemantics rows, from which CoreCLR's
// read-write metadata finds a property's rows through a hash, in reverse. Then an interface's property, a getter
// that is a `MethodBuilder` of another module whose token names a method of this one, and the calls the managed
// side refuses once a type is created. The test's terminal-state check reads `Props`' rows. Measured on .NET 10 to
// exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    const BindingFlags All =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    const MethodAttributes Accessor = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig;

    static ModuleBuilder NewModule (string name) =>
        AssemblyBuilder.DefineDynamicAssembly (new AssemblyName (name), AssemblyBuilderAccess.Run).DefineDynamicModule (name);

    /// An instance method returning `value`, or a static one.
    static MethodBuilder Constant (TypeBuilder type, string name, int value, bool isStatic = false)
    {
        MethodBuilder method = type.DefineMethod (
            name,
            Accessor | (isStatic ? MethodAttributes.Static : 0),
            typeof (int),
            Type.EmptyTypes);
        ILGenerator il = method.GetILGenerator ();
        il.Emit (OpCodes.Ldc_I4, value);
        il.Emit (OpCodes.Ret);
        return method;
    }

    static string Names (MethodInfo[] methods) => string.Join (",", methods.Select (m => m.Name));

    public static int Main ()
    {
        ModuleBuilder props = NewModule ("Props");
        TypeBuilder a = props.DefineType ("A", TypeAttributes.Public);
        TypeBuilder b = props.DefineType ("B", TypeAttributes.Public);
        TypeBuilder d = props.DefineType ("D", TypeAttributes.Public);
        TypeBuilder e = props.DefineType ("E", TypeAttributes.Public);

        // Properties 1 to 3, of A, B and A.
        FieldBuilder p1Field = a.DefineField ("_p1", typeof (int), FieldAttributes.Private);
        PropertyBuilder p1 = a.DefineProperty ("P1", PropertyAttributes.None, typeof (int), Type.EmptyTypes);
        PropertyBuilder q1 = b.DefineProperty (
            "Q1",
            PropertyAttributes.SpecialName | PropertyAttributes.RTSpecialName | PropertyAttributes.HasDefault,
            typeof (string),
            Type.EmptyTypes);
        PropertyBuilder p2 = a.DefineProperty ("P2", PropertyAttributes.None, CallingConventions.HasThis, typeof (long), Type.EmptyTypes);

        if (q1.Attributes != (PropertyAttributes.SpecialName | PropertyAttributes.RTSpecialName | PropertyAttributes.HasDefault))
        {
            return 1;
        }

        // Q1's getter, of B, is given its token first; then P1's setter and getter, of A.
        MethodBuilder q1Get = b.DefineMethod ("get_Q1", Accessor, typeof (string), Type.EmptyTypes);
        {
            ILGenerator il = q1Get.GetILGenerator ();
            il.Emit (OpCodes.Ldstr, "q");
            il.Emit (OpCodes.Ret);
        }

        q1.SetGetMethod (q1Get);

        MethodBuilder p1Get = a.DefineMethod ("get_P1", Accessor, typeof (int), Type.EmptyTypes);
        {
            ILGenerator il = p1Get.GetILGenerator ();
            il.Emit (OpCodes.Ldarg_0);
            il.Emit (OpCodes.Ldfld, p1Field);
            il.Emit (OpCodes.Ret);
        }

        MethodBuilder p1Set = a.DefineMethod ("set_P1", Accessor, typeof (void), new[] { typeof (int) });
        {
            ILGenerator il = p1Set.GetILGenerator ();
            il.Emit (OpCodes.Ldarg_0);
            il.Emit (OpCodes.Ldarg_1);
            il.Emit (OpCodes.Stfld, p1Field);
            il.Emit (OpCodes.Ret);
        }

        p1.SetSetMethod (p1Set);
        p1.SetGetMethod (p1Get);

        if (q1Get.MetadataToken != 0x06000001 || p1Get.MetadataToken != 0x06000002 || p1Set.MetadataToken != 0x06000003)
        {
            return 2;
        }

        // Two getters and three other methods, the getters interleaved with the others.
        MethodBuilder g1 = Constant (a, "get_P2_first", 1);
        MethodBuilder g2 = Constant (a, "get_P2_second", 2);
        MethodBuilder o1 = Constant (a, "other1", 3);
        MethodBuilder o2 = Constant (a, "other2", 4);
        MethodBuilder o3 = Constant (a, "other3", 5);
        p2.SetGetMethod (g1);
        p2.AddOtherMethod (o2);
        p2.SetGetMethod (g2);
        p2.AddOtherMethod (o1);
        p2.AddOtherMethod (o3);

        // Properties 4 and 5: the emitter does not look for an existing row.
        a.DefineProperty ("Dup", PropertyAttributes.None, typeof (int), Type.EmptyTypes);
        a.DefineProperty ("Dup", PropertyAttributes.None, typeof (int), Type.EmptyTypes);

        // Properties 6 to 8: a static property, an indexer, and one whose types are the module's own.
        FieldBuilder sField = a.DefineField ("s", typeof (int), FieldAttributes.Private | FieldAttributes.Static);
        PropertyBuilder s = a.DefineProperty ("S", PropertyAttributes.None, CallingConventions.Standard, typeof (int), Type.EmptyTypes);
        MethodBuilder sGet = a.DefineMethod ("get_S", Accessor | MethodAttributes.Static, typeof (int), Type.EmptyTypes);
        {
            ILGenerator il = sGet.GetILGenerator ();
            il.Emit (OpCodes.Ldsfld, sField);
            il.Emit (OpCodes.Ret);
        }

        s.SetGetMethod (sGet);

        PropertyBuilder item = a.DefineProperty ("Item", PropertyAttributes.None, typeof (int), new[] { typeof (int), typeof (string) });
        MethodBuilder itemGet = a.DefineMethod ("get_Item", Accessor, typeof (int), new[] { typeof (int), typeof (string) });
        {
            ILGenerator il = itemGet.GetILGenerator ();
            il.Emit (OpCodes.Ldarg_1);
            il.Emit (OpCodes.Ret);
        }

        item.SetGetMethod (itemGet);
        a.DefineProperty ("Self", PropertyAttributes.None, a, new Type[] { b });

        // Property 9. `SetGetMethod` asks get_X for its token, which first gives one to d0, defined before it, and
        // both after E's e0, which was asked for its token before.
        MethodBuilder d0 = d.DefineMethod ("d0", MethodAttributes.Public | MethodAttributes.Static, typeof (void), Type.EmptyTypes);
        d0.GetILGenerator ().Emit (OpCodes.Ret);
        MethodBuilder xGet = Constant (d, "get_X", 5, isStatic: true);
        MethodBuilder e0 = e.DefineMethod ("e0", MethodAttributes.Public | MethodAttributes.Static, typeof (void), Type.EmptyTypes);
        e0.GetILGenerator ().Emit (OpCodes.Ret);
        int e0Token = e0.MetadataToken;
        PropertyBuilder x = d.DefineProperty ("X", PropertyAttributes.None, CallingConventions.Standard, typeof (int), Type.EmptyTypes);
        x.SetGetMethod (xGet);

        if (e0Token != 0x0600000B || d0.MetadataToken != 0x0600000C || xGet.MetadataToken != 0x0600000D)
        {
            return 3;
        }

        // Property 10, of a generic type.
        TypeBuilder c = props.DefineType ("C", TypeAttributes.Public);
        GenericTypeParameterBuilder t = c.DefineGenericParameters ("T")[0];
        FieldBuilder cField = c.DefineField ("v", t, FieldAttributes.Public);
        PropertyBuilder v = c.DefineProperty ("V", PropertyAttributes.None, t, Type.EmptyTypes);
        MethodBuilder vGet = c.DefineMethod ("get_V", Accessor, t, Type.EmptyTypes);
        {
            ILGenerator il = vGet.GetILGenerator ();
            il.Emit (OpCodes.Ldarg_0);
            il.Emit (OpCodes.Ldfld, cField);
            il.Emit (OpCodes.Ret);
        }

        v.SetGetMethod (vGet);

        Type bCreated = b.CreateType ();
        Type aCreated = a.CreateType ();
        Type dCreated = d.CreateType ();
        e.CreateType ();
        Type cCreated = c.CreateType ();

        if (string.Join (",", aCreated.GetProperties (All).Select (p => p.Name + "@" + p.MetadataToken.ToString ("X8")))
            != "P1@17000001,P2@17000003,Dup@17000004,S@17000006,Item@17000007,Self@17000008")
        {
            return 4;
        }

        PropertyInfo q1Info = bCreated.GetProperty ("Q1");

        if (q1Info.MetadataToken != 0x17000002 || q1Info.Attributes != PropertyAttributes.SpecialName || q1Info.PropertyType != typeof (string))
        {
            return 5;
        }

        if ((string) q1Info.GetValue (Activator.CreateInstance (bCreated)) != "q")
        {
            return 6;
        }

        object aInstance = Activator.CreateInstance (aCreated);
        PropertyInfo p1Info = aCreated.GetProperty ("P1");
        p1Info.SetValue (aInstance, 30);

        if ((int) p1Info.GetValue (aInstance) != 30 || p1Info.GetGetMethod ().Name != "get_P1" || p1Info.GetSetMethod ().Name != "set_P1")
        {
            return 7;
        }

        // Fewer than 25 MethodSemantics rows: an association's rows in the order added, the last getter kept.
        PropertyInfo p2Info = aCreated.GetProperty ("P2");

        if (p2Info.GetGetMethod (true).Name != "get_P2_second"
            || Names (p2Info.GetAccessors (true)) != "get_P2_second,other2,other1,other3"
            || p2Info.PropertyType != typeof (long))
        {
            return 8;
        }

        // Reflection lists one of two properties with the same name and signature, and a property with no
        // accessors is not public.
        if (aCreated.GetProperty ("Dup") != null || aCreated.GetProperty ("Dup", All).MetadataToken != 0x17000004)
        {
            return 9;
        }

        if ((int) aCreated.GetProperty ("S").GetValue (null) != 0 || !aCreated.GetProperty ("S").GetGetMethod ().IsStatic)
        {
            return 10;
        }

        if (aCreated.GetProperty ("Item").GetIndexParameters ().Length != 2 || aCreated.GetProperty ("Self", All).PropertyType != aCreated)
        {
            return 11;
        }

        if ((int) dCreated.GetProperty ("X").GetValue (null) != 5)
        {
            return 12;
        }

        Type cOfInt = cCreated.MakeGenericType (typeof (int));
        object cInstance = Activator.CreateInstance (cOfInt);
        cOfInt.GetField ("v").SetValue (cInstance, 5);
        PropertyInfo vInfo = cOfInt.GetProperty ("V");

        if (vInfo.PropertyType != typeof (int) || (int) vInfo.GetValue (cInstance) != 5 || vInfo.MetadataToken != 0x1700000A)
        {
            return 13;
        }

        // A method of another type calling P1's accessors directly.
        TypeBuilder caller = props.DefineType ("Caller", TypeAttributes.Public);
        MethodBuilder roundTrip = caller.DefineMethod (
            "RoundTrip",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof (int),
            new[] { aCreated, typeof (int) });
        {
            ILGenerator il = roundTrip.GetILGenerator ();
            il.Emit (OpCodes.Ldarg_0);
            il.Emit (OpCodes.Ldarg_1);
            il.Emit (OpCodes.Callvirt, aCreated.GetProperty ("P1").GetSetMethod ());
            il.Emit (OpCodes.Ldarg_0);
            il.Emit (OpCodes.Callvirt, aCreated.GetProperty ("P1").GetGetMethod ());
            il.Emit (OpCodes.Ret);
        }

        Type callerCreated = caller.CreateType ();

        if ((int) callerCreated.GetMethod ("RoundTrip").Invoke (null, new object[] { aInstance, 41 }) != 41)
        {
            return 14;
        }

        // The managed caller refuses once the property's type is created, before the QCall.
        try
        {
            a.DefineProperty ("Late", PropertyAttributes.None, typeof (int), Type.EmptyTypes);
            return 15;
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            p1.SetGetMethod (p1Get);
            return 16;
        }
        catch (InvalidOperationException)
        {
        }

        // Properties 11 to 13. The QCall reads the name to its first NUL and writes the row; the managed caller then
        // refuses the name, but the row and its token stay.
        TypeBuilder named = props.DefineType ("Named", TypeAttributes.Public);
        named.DefineProperty ("First", PropertyAttributes.None, typeof (int), Type.EmptyTypes);

        try
        {
            named.DefineProperty ("\0x", PropertyAttributes.None, typeof (int), Type.EmptyTypes);
            return 17;
        }
        catch (ArgumentException)
        {
        }

        named.DefineProperty ("Last", PropertyAttributes.None, typeof (int), Type.EmptyTypes);
        Type namedCreated = named.CreateType ();

        if (string.Join (",", namedCreated.GetProperties (All).Select (p => "'" + p.Name + "'@" + p.MetadataToken.ToString ("X8")))
            != "'First'@1700000B,''@1700000C,'Last'@1700000D")
        {
            return 18;
        }

        // Module `Many`: P's five rows first, then twenty others, one getter each, making 25.
        ModuleBuilder many = NewModule ("Many");
        TypeBuilder m = many.DefineType ("M", TypeAttributes.Public);
        PropertyBuilder mp = m.DefineProperty ("P", PropertyAttributes.None, typeof (int), Type.EmptyTypes);
        MethodBuilder mg1 = Constant (m, "g1", 1);
        MethodBuilder mo1 = Constant (m, "o1", 2);
        MethodBuilder mg2 = Constant (m, "g2", 3);
        MethodBuilder mo2 = Constant (m, "o2", 4);
        MethodBuilder mo3 = Constant (m, "o3", 5);
        mp.SetGetMethod (mg1);
        mp.AddOtherMethod (mo1);
        mp.SetGetMethod (mg2);
        mp.AddOtherMethod (mo2);
        mp.AddOtherMethod (mo3);

        for (int i = 0; i < 20; i++)
        {
            PropertyBuilder filler = m.DefineProperty ("F" + i, PropertyAttributes.None, typeof (int), Type.EmptyTypes);
            filler.SetGetMethod (Constant (m, "get_F" + i, i));
        }

        Type mCreated = m.CreateType ();
        PropertyInfo mpInfo = mCreated.GetProperty ("P");

        if (mpInfo.GetGetMethod (true).Name != "g1" || Names (mpInfo.GetAccessors (true)) != "g1,o3,o2,o1")
        {
            return 19;
        }

        if (mCreated.GetProperty ("F19").GetGetMethod ().Name != "get_F19")
        {
            return 20;
        }

        // An interface's property, and a class's implementing it, read through the interface's.
        ModuleBuilder ifaces = NewModule ("Ifaces");
        TypeBuilder iface = ifaces.DefineType ("IHas", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        MethodBuilder ifaceGet = iface.DefineMethod (
            "get_Value",
            Accessor | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.NewSlot,
            typeof (int),
            Type.EmptyTypes);
        iface.DefineProperty ("Value", PropertyAttributes.None, typeof (int), Type.EmptyTypes).SetGetMethod (ifaceGet);
        Type ifaceCreated = iface.CreateType ();
        TypeBuilder impl = ifaces.DefineType ("Impl", TypeAttributes.Public, typeof (object), new[] { ifaceCreated });
        MethodBuilder implGet = impl.DefineMethod (
            "get_Value",
            Accessor | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot,
            typeof (int),
            Type.EmptyTypes);
        {
            ILGenerator il = implGet.GetILGenerator ();
            il.Emit (OpCodes.Ldc_I4, 42);
            il.Emit (OpCodes.Ret);
        }

        impl.DefineProperty ("Value", PropertyAttributes.None, typeof (int), Type.EmptyTypes).SetGetMethod (implGet);
        Type implCreated = impl.CreateType ();
        PropertyInfo ifaceValue = ifaceCreated.GetProperty ("Value");

        if (ifaceValue.MetadataToken != 0x17000001
            || implCreated.GetProperty ("Value").MetadataToken != 0x17000002
            || (int) ifaceValue.GetValue (Activator.CreateInstance (implCreated)) != 42)
        {
            return 21;
        }

        // A getter that is a MethodBuilder of module Y: its token, 0x06000001, names X's own get_Own.
        ModuleBuilder xModule = NewModule ("X");
        ModuleBuilder yModule = NewModule ("Y");
        TypeBuilder yType = yModule.DefineType ("YT", TypeAttributes.Public);
        MethodBuilder y0 = Constant (yType, "y0", 1, isStatic: true);
        TypeBuilder xType = xModule.DefineType ("XT", TypeAttributes.Public);
        MethodBuilder ownGet = Constant (xType, "get_Own", 7, isStatic: true);
        xType.DefineProperty ("Own", PropertyAttributes.None, CallingConventions.Standard, typeof (int), Type.EmptyTypes).SetGetMethod (ownGet);
        xType.DefineProperty ("Wild", PropertyAttributes.None, CallingConventions.Standard, typeof (int), Type.EmptyTypes).SetGetMethod (y0);

        if (y0.MetadataToken != 0x06000001 || ownGet.MetadataToken != 0x06000001)
        {
            return 22;
        }

        PropertyInfo wild = xType.CreateType ().GetProperty ("Wild");

        if (wild.GetGetMethod ().Name != "get_Own" || (int) wild.GetValue (null) != 7)
        {
            return 23;
        }

        return 0;
    }
}
