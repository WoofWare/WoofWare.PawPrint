using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;

// What CoreCLR's emitter does with a pseudo-attribute (`_IsKnownCustomAttribute`) and with its two security names,
// through `SetCustomAttribute`: it sets a flag or writes another table instead of a CustomAttribute row, keeps a row
// after validating it, or throws `ArgumentException` for a wrong target or a bad value. The name decides, wherever the
// type is defined: an impostor in the module, in another dynamic assembly or nested, is treated as the real attribute,
// and an instantiation of another assembly's generic impostor (a TypeSpec parent) is not. Measured on .NET 10 and 11
// to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    const BindingFlags All =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    static readonly byte[] NoArguments = { 1, 0, 0, 0 };

    static int count;

    static (AssemblyBuilder, ModuleBuilder) NewModule (string name)
    {
        AssemblyBuilder assembly =
            AssemblyBuilder.DefineDynamicAssembly (new AssemblyName (name + (count++)), AssemblyBuilderAccess.Run);
        return (assembly, assembly.DefineDynamicModule (name));
    }

    static CustomAttributeBuilder Cab0 (Type t) => new CustomAttributeBuilder (t.GetConstructor (Type.EmptyTypes), new object[0]);

    static CustomAttributeBuilder Cab (Type t, Type[] signature, params object[] args) =>
        new CustomAttributeBuilder (t.GetConstructor (signature), args);

    static MethodBuilder StaticVoid (TypeBuilder type, string name)
    {
        MethodBuilder method = type.DefineMethod (name, MethodAttributes.Public | MethodAttributes.Static, typeof (void), Type.EmptyTypes);
        method.GetILGenerator ().Emit (OpCodes.Ret);
        return method;
    }

    static bool ThrowsInvalidAttribute (Action action)
    {
        try
        {
            action ();
            return false;
        }
        catch (Exception e)
        {
            return e.GetType () == typeof (ArgumentException) && e.Message == "Invalid custom attribute provided.";
        }
    }

    static List<string> Names (IList<CustomAttributeData> rows)
    {
        var names = new List<string> ();
        foreach (CustomAttributeData row in rows)
        {
            names.Add (row.AttributeType.Name);
        }

        return names;
    }

    /// An attribute type with this full name in `module`, created; its created default constructor.
    static ConstructorInfo Impostor (ModuleBuilder module, string fullName, bool generic = false)
    {
        TypeBuilder type = module.DefineType (fullName, TypeAttributes.Public, typeof (Attribute));
        if (generic)
        {
            type.DefineGenericParameters ("T");
        }

        type.DefineDefaultConstructor (MethodAttributes.Public);
        Type created = type.CreateType ();
        return generic ? created.MakeGenericType (typeof (int)).GetConstructor (Type.EmptyTypes) : created.GetConstructor (Type.EmptyTypes);
    }

    static int Flags ()
    {
        var (assembly, module) = NewModule ("Flags");
        TypeBuilder t = module.DefineType ("T", TypeAttributes.Public);
        // The first call: under PawPrint, the classifier's stop.
        t.SetCustomAttribute (Cab0 (typeof (SerializableAttribute)));
        t.SetCustomAttribute (Cab0 (typeof (SpecialNameAttribute)));
        t.SetCustomAttribute (Cab0 (typeof (SuppressUnmanagedCodeSecurityAttribute)));
        t.SetCustomAttribute (Cab (typeof (GuidAttribute), new[] { typeof (string) }, "01234567-0123-0123-0123-001122334455"));

        if (!ThrowsInvalidAttribute (() => t.SetCustomAttribute (Cab (typeof (InterfaceTypeAttribute), new[] { typeof (short) }, (short) 99))))
        {
            return 1;
        }

        TypeBuilder ci = module.DefineType ("CI", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        ci.SetCustomAttribute (Cab0 (typeof (ComImportAttribute)));

        FieldBuilder f = t.DefineField ("F", typeof (int), FieldAttributes.Public);
        f.SetCustomAttribute (Cab0 (typeof (NonSerializedAttribute)));
        f.SetCustomAttribute (Cab0 (typeof (SpecialNameAttribute)));
        FieldBuilder fm = t.DefineField ("FM", typeof (string), FieldAttributes.Public);
        fm.SetCustomAttribute (Cab (typeof (MarshalAsAttribute), new[] { typeof (UnmanagedType) }, UnmanagedType.LPWStr));
        PropertyBuilder p = t.DefineProperty ("P", PropertyAttributes.None, typeof (int), null);
        p.SetCustomAttribute (Cab0 (typeof (SpecialNameAttribute)));
        EventBuilder e = t.DefineEvent ("E", EventAttributes.None, typeof (Action));
        e.SetCustomAttribute (Cab0 (typeof (SpecialNameAttribute)));

        MethodBuilder m = t.DefineMethod (
            "M", MethodAttributes.Public | MethodAttributes.Static, typeof (int), new[] { typeof (int), typeof (string) });
        ILGenerator il = m.GetILGenerator ();
        il.Emit (OpCodes.Ldc_I4_0);
        il.Emit (OpCodes.Ret);
        m.SetCustomAttribute (Cab0 (typeof (SpecialNameAttribute)));
        ParameterBuilder a = m.DefineParameter (1, ParameterAttributes.None, "a");
        a.SetCustomAttribute (Cab0 (typeof (InAttribute)));
        a.SetCustomAttribute (Cab0 (typeof (OutAttribute)));
        ParameterBuilder b = m.DefineParameter (2, ParameterAttributes.None, "b");
        b.SetCustomAttribute (Cab0 (typeof (OptionalAttribute)));
        b.SetCustomAttribute (Cab (typeof (MarshalAsAttribute), new[] { typeof (UnmanagedType) }, UnmanagedType.LPStr));

        MethodBuilder native = t.DefineMethod ("Native", MethodAttributes.Public | MethodAttributes.Static, typeof (int), Type.EmptyTypes);
        native.SetCustomAttribute (
            new CustomAttributeBuilder (
                typeof (DllImportAttribute).GetConstructor (new[] { typeof (string) }),
                new object[] { "libc" },
                new[] { typeof (DllImportAttribute).GetField ("EntryPoint") },
                new object[] { "getpid" }));
        MethodBuilder impl = StaticVoid (t, "Impl");
        impl.SetCustomAttribute (Cab (typeof (MethodImplAttribute), new[] { typeof (MethodImplOptions) }, MethodImplOptions.NoInlining));

        if (!ThrowsInvalidAttribute (
            () => impl.SetCustomAttribute (Cab (typeof (MethodImplAttribute), new[] { typeof (MethodImplOptions) }, (MethodImplOptions) 0x7FFF))))
        {
            return 2;
        }

        MethodBuilder ps = StaticVoid (t, "PS");
        ps.SetCustomAttribute (Cab0 (typeof (PreserveSigAttribute)));
        MethodBuilder sec = StaticVoid (t, "Sec");
        sec.SetCustomAttribute (Cab0 (typeof (SuppressUnmanagedCodeSecurityAttribute)));

        // A known attribute on a wrong target, or with a bad value: refused, and no row consumed.
        if (!ThrowsInvalidAttribute (() => ps.SetCustomAttribute (Cab0 (typeof (SerializableAttribute)))))
        {
            return 3;
        }

        if (!ThrowsInvalidAttribute (() => f.SetCustomAttribute (Cab0 (typeof (InAttribute)))))
        {
            return 4;
        }

        if (!ThrowsInvalidAttribute (
            () => ps.SetCustomAttribute (Cab (typeof (DllImportAttribute), new[] { typeof (string) }, ""))))
        {
            return 5;
        }

        if (!ThrowsInvalidAttribute (
            () => assembly.SetCustomAttribute (Cab (typeof (TypeLibVersionAttribute), new[] { typeof (int), typeof (int) }, -1, 0))))
        {
            return 6;
        }

        TypeBuilder s = module.DefineType ("S", TypeAttributes.Public | TypeAttributes.Sealed, typeof (ValueType));
        FieldBuilder x = s.DefineField ("X", typeof (int), FieldAttributes.Public);
        s.SetCustomAttribute (
            new CustomAttributeBuilder (
                typeof (StructLayoutAttribute).GetConstructor (new[] { typeof (LayoutKind) }),
                new object[] { LayoutKind.Explicit },
                new[] { typeof (StructLayoutAttribute).GetField ("Pack"), typeof (StructLayoutAttribute).GetField ("Size") },
                new object[] { 4, 16 }));
        x.SetCustomAttribute (Cab (typeof (FieldOffsetAttribute), new[] { typeof (int) }, 8));

        Type cit = ci.CreateType ();
        Type tt = t.CreateType ();
        Type st = s.CreateType ();

        if (!tt.IsSerializable || !tt.IsSpecialName || ((int) tt.Attributes & 0x00040000) == 0)
        {
            return 7;
        }

        List<string> typeRows = Names (tt.GetCustomAttributesData ());
        if (!typeRows.Contains ("SuppressUnmanagedCodeSecurityAttribute") || !typeRows.Contains ("GuidAttribute"))
        {
            return 8;
        }

        if (!cit.IsImport)
        {
            return 9;
        }

        FieldInfo tf = tt.GetField ("F");
        if (!tf.IsNotSerialized || !tf.IsSpecialName)
        {
            return 10;
        }

        if (((int) tt.GetField ("FM").Attributes & 0x1000) == 0)
        {
            return 11;
        }

        if (!tt.GetProperty ("P", All).IsSpecialName || !tt.GetEvent ("E", All).IsSpecialName)
        {
            return 12;
        }

        MethodInfo tm = tt.GetMethod ("M");
        if (!tm.IsSpecialName)
        {
            return 13;
        }

        ParameterInfo[] parameters = tm.GetParameters ();
        if (parameters[0].Attributes != (ParameterAttributes.In | ParameterAttributes.Out)
            || parameters[1].Attributes != (ParameterAttributes.Optional | ParameterAttributes.HasFieldMarshal))
        {
            return 14;
        }

        MethodInfo tn = tt.GetMethod ("Native");
        if ((tn.Attributes & MethodAttributes.PinvokeImpl) == 0)
        {
            return 15;
        }

        if ((tt.GetMethod ("Impl").MethodImplementationFlags & MethodImplAttributes.NoInlining) == 0)
        {
            return 16;
        }

        if ((tt.GetMethod ("PS").MethodImplementationFlags & MethodImplAttributes.PreserveSig) == 0)
        {
            return 17;
        }

        MethodInfo tsec = tt.GetMethod ("Sec");
        if ((tsec.Attributes & MethodAttributes.HasSecurity) == 0
            || !Names (tsec.GetCustomAttributesData ()).Contains ("SuppressUnmanagedCodeSecurityAttribute"))
        {
            return 18;
        }

        StructLayoutAttribute layout = st.StructLayoutAttribute;
        if (layout.Value != LayoutKind.Explicit || layout.Pack != 4 || layout.Size != 16)
        {
            return 19;
        }

        if (!Names (st.GetField ("X").GetCustomAttributesData ()).Contains ("FieldOffsetAttribute"))
        {
            return 20;
        }

        return 0;
    }

    static int Impostors ()
    {
        var (_, other) = NewModule ("Other");
        ConstructorInfo otherSerializable = Impostor (other, "System.SerializableAttribute");
        var (_, otherGeneric) = NewModule ("OtherG");
        ConstructorInfo otherGenericSerializable = Impostor (otherGeneric, "System.SerializableAttribute", generic: true);
        var (_, module) = NewModule ("Impostors");

        // In the module, through its `ConstructorBuilder`.
        TypeBuilder own = module.DefineType ("System.SerializableAttribute", TypeAttributes.Public, typeof (Attribute));
        ConstructorBuilder ownCtor = own.DefineDefaultConstructor (MethodAttributes.Public);
        TypeBuilder t1 = module.DefineType ("T1");
        t1.SetCustomAttribute (ownCtor, NoArguments);
        Type ownT = own.CreateType ();
        // In the module, through its created constructor.
        TypeBuilder t2 = module.DefineType ("T2");
        t2.SetCustomAttribute (ownT.GetConstructor (Type.EmptyTypes), NoArguments);
        // In another dynamic assembly, through a TypeRef.
        TypeBuilder t3 = module.DefineType ("T3");
        t3.SetCustomAttribute (otherSerializable, NoArguments);
        // Nested in a type of the module.
        TypeBuilder outer = module.DefineType ("Outer", TypeAttributes.Public);
        TypeBuilder nested = outer.DefineNestedType ("System.SerializableAttribute", TypeAttributes.NestedPublic, typeof (Attribute));
        ConstructorBuilder nestedCtor = nested.DefineDefaultConstructor (MethodAttributes.Public);
        TypeBuilder t4 = module.DefineType ("T4");
        t4.SetCustomAttribute (nestedCtor, NoArguments);
        outer.CreateType ();
        nested.CreateType ();
        // An instantiation of another assembly's generic impostor: a MemberRef on a TypeSpec, which has no name.
        TypeBuilder t5 = module.DefineType ("T5");
        t5.SetCustomAttribute (otherGenericSerializable, NoArguments);
        // Near misses: the case of the name, and no namespace.
        TypeBuilder t6 = module.DefineType ("T6");
        t6.SetCustomAttribute (Impostor (module, "System.Serializableattribute"), NoArguments);
        TypeBuilder t7 = module.DefineType ("T7");
        t7.SetCustomAttribute (Impostor (module, "SerializableAttribute"), NoArguments);

        if (!t1.CreateType ().IsSerializable || !t2.CreateType ().IsSerializable)
        {
            return 30;
        }

        if (!t3.CreateType ().IsSerializable)
        {
            return 31;
        }

        if (!t4.CreateType ().IsSerializable)
        {
            return 32;
        }

        Type t5t = t5.CreateType ();
        if (t5t.IsSerializable || t5t.GetCustomAttributesData ().Count != 1)
        {
            return 33;
        }

        Type t6t = t6.CreateType ();
        Type t7t = t7.CreateType ();
        if (t6t.IsSerializable || t7t.IsSerializable || t6t.GetCustomAttributesData ().Count != 1 || t7t.GetCustomAttributesData ().Count != 1)
        {
            return 34;
        }

        return 0;
    }

    static int Security ()
    {
        var (_, module) = NewModule ("Security");
        // CoreLib no longer defines `DynamicSecurityMethodAttribute`; the emitter compares the name alone.
        ConstructorInfo dynamicSecurity = Impostor (module, "System.Security.DynamicSecurityMethodAttribute");
        ConstructorInfo suppress = typeof (SuppressUnmanagedCodeSecurityAttribute).GetConstructor (Type.EmptyTypes);

        TypeBuilder t = module.DefineType ("T", TypeAttributes.Public);
        MethodBuilder m = StaticVoid (t, "M");
        m.SetCustomAttribute (dynamicSecurity, NoArguments);
        FieldBuilder f = t.DefineField ("F", typeof (int), FieldAttributes.Public);
        f.SetCustomAttribute (suppress, NoArguments);
        f.SetCustomAttribute (dynamicSecurity, NoArguments);
        TypeBuilder u = module.DefineType ("U", TypeAttributes.Public);
        u.SetCustomAttribute (dynamicSecurity, NoArguments);

        Type tt = t.CreateType ();
        Type ut = u.CreateType ();
        MethodInfo tm = tt.GetMethod ("M");

        // On a method: `mdRequireSecObject`, and no row.
        if ((tm.Attributes & MethodAttributes.RequireSecObject) == 0 || tm.GetCustomAttributesData ().Count != 0)
        {
            return 40;
        }

        // On a type: an ordinary row, and no flag.
        if (((int) ut.Attributes & 0x00040000) != 0 || ut.GetCustomAttributesData ().Count != 1)
        {
            return 41;
        }

        // On a field: both ordinary rows.
        if (string.Join (",", Names (tt.GetField ("F").GetCustomAttributesData ()))
            != "SuppressUnmanagedCodeSecurityAttribute,DynamicSecurityMethodAttribute")
        {
            return 42;
        }

        return 0;
    }

    public static int Main ()
    {
        int result = Flags ();
        if (result != 0) return result;
        result = Impostors ();
        if (result != 0) return result;
        return Security ();
    }
}
