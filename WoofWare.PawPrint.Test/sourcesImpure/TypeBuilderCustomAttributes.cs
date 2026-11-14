using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

// `TypeBuilder_DefineCustomAttribute`, through every builder's `SetCustomAttribute`, both overloads, and the rows read
// back through reflection on the created members. Each module is its own: `Parents`, one attribute on each of the nine
// owners a builder hands out, interleaved, with a type's and a method's generic parameter (whose rows are written when
// the parameter is), the two attributes `GenericTypeParameterBuilder` drops, the raw overload, and `DebuggableAttribute`
// on the module; `Refusals`, the managed refusals, and attributes on the assembly and the module after a type is
// created; `Malformed`, blobs CoreCLR stores as given and reflection refuses; `NonAttribute`, constructors of types that
// are not attributes; `Duplicates`; `Inherited`; `Order`, 33 rows interleaved over two types and the assembly, 21 of
// them on one type; `FirstOfTwoSmall` and `FirstOfTwoMid`, two `InlineArray` attributes on one struct, of which the type
// loader takes the first added below 25 rows; `Args`, the kinds of argument a blob holds; and `Friends`, two rows of one
// name on the assembly. The test's terminal-state check reads the rows of `Order` and `Parents`. Measured on .NET 10 and
// 11 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public enum Color
{
    Red = 1,
    Green = 2,
}

[AttributeUsage (AttributeTargets.All, AllowMultiple = true, Inherited = true)]
public class TagAttribute : Attribute
{
    public TagAttribute (string s) { S = s; }
    public TagAttribute (object o) { O = o; }
    public TagAttribute (Color c) { C = c; }
    public TagAttribute (Type t) { T = t; }
    public TagAttribute (int[] a) { A = a; }
    public TagAttribute (object[] oa) { OA = oa; }

    public string S;
    public object O;
    public Color C;
    public Type T;
    public int[] A;
    public object[] OA;
    public object OField;
    public Type TField;
    public int[] AField;
    public Color CField;
    public string Prop { get; set; }
    public object OProp { get; set; }

    public override string ToString ()
    {
        var parts = new List<string> ();
        if (S != null) parts.Add ("S=" + S);
        if (O != null) parts.Add ("O=" + Program.Fmt (O));
        if (C != 0) parts.Add ("C=" + C);
        if (T != null) parts.Add ("T=" + Program.Fmt (T));
        if (A != null) parts.Add ("A=" + Program.Fmt (A));
        if (OA != null) parts.Add ("OA=" + Program.Fmt (OA));
        if (OField != null) parts.Add ("OField=" + Program.Fmt (OField));
        if (TField != null) parts.Add ("TField=" + Program.Fmt (TField));
        if (AField != null) parts.Add ("AField=" + Program.Fmt (AField));
        if (CField != 0) parts.Add ("CField=" + CField);
        if (Prop != null) parts.Add ("Prop=" + Prop);
        if (OProp != null) parts.Add ("OProp=" + Program.Fmt (OProp));
        return "Tag{" + string.Join (";", parts) + "}";
    }
}

[AttributeUsage (AttributeTargets.All, AllowMultiple = false, Inherited = true)]
public class SingleAttribute : Attribute
{
    public SingleAttribute (int n) { N = n; }
    public int N;
    public override string ToString () => "Single" + N;
}

[AttributeUsage (AttributeTargets.All, AllowMultiple = true, Inherited = true)]
public class MultiInhAttribute : Attribute
{
    public MultiInhAttribute (int n) { N = n; }
    public int N;
    public override string ToString () => "Multi" + N;
}

[AttributeUsage (AttributeTargets.All, AllowMultiple = true, Inherited = false)]
public class NoInhAttribute : Attribute
{
    public NoInhAttribute (int n) { N = n; }
    public int N;
    public override string ToString () => "NoInh" + N;
}

public class WithCctor
{
    public static int Value;
    static WithCctor () { Value = 1; }
}

public static class Program
{
    const BindingFlags All =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    static (AssemblyBuilder, ModuleBuilder) NewModule (string name)
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName (name), AssemblyBuilderAccess.Run);
        return (assembly, assembly.DefineDynamicModule (name));
    }

    static ConstructorInfo TagCtor (Type argument) => typeof (TagAttribute).GetConstructor (new[] { argument });

    static CustomAttributeBuilder Tag (string s) => new CustomAttributeBuilder (TagCtor (typeof (string)), new object[] { s });

    static CustomAttributeBuilder Multi (int n) =>
        new CustomAttributeBuilder (typeof (MultiInhAttribute).GetConstructor (new[] { typeof (int) }), new object[] { n });

    static CustomAttributeBuilder Single (int n) =>
        new CustomAttributeBuilder (typeof (SingleAttribute).GetConstructor (new[] { typeof (int) }), new object[] { n });

    static CustomAttributeBuilder NoInh (int n) =>
        new CustomAttributeBuilder (typeof (NoInhAttribute).GetConstructor (new[] { typeof (int) }), new object[] { n });

    /// A type's name as the checks spell it, with no assembly version in it.
    static string TypeName (Type t)
    {
        if (t.IsGenericType)
        {
            var args = new List<string> ();
            foreach (Type arg in t.GetGenericArguments ())
            {
                args.Add (TypeName (arg));
            }

            return t.GetGenericTypeDefinition ().FullName + "<" + string.Join (",", args) + ">";
        }

        return t.FullName;
    }

    public static string Fmt (object value)
    {
        if (value == null)
        {
            return "null";
        }

        if (value is Type t)
        {
            return "typeof(" + TypeName (t) + ")";
        }

        if (value is ReadOnlyCollection<CustomAttributeTypedArgument> collection)
        {
            var items = new List<string> ();
            foreach (CustomAttributeTypedArgument item in collection)
            {
                items.Add (Typed (item));
            }

            return "[" + string.Join (",", items) + "]";
        }

        if (value is Array array)
        {
            var items = new List<string> ();
            foreach (object item in array)
            {
                items.Add (Fmt (item));
            }

            return "[" + string.Join (",", items) + "]";
        }

        return value.ToString ();
    }

    static string Typed (CustomAttributeTypedArgument argument) => "(" + argument.ArgumentType.Name + ")" + Fmt (argument.Value);

    static string Data (CustomAttributeData data)
    {
        var args = new List<string> ();
        foreach (CustomAttributeTypedArgument argument in data.ConstructorArguments)
        {
            args.Add (Typed (argument));
        }

        string result = data.AttributeType.Name + "(" + string.Join (",", args) + ")";

        if (data.NamedArguments.Count > 0)
        {
            var named = new List<string> ();
            foreach (CustomAttributeNamedArgument argument in data.NamedArguments)
            {
                named.Add ((argument.IsField ? "F:" : "P:") + argument.MemberName + "=" + Typed (argument.TypedValue));
            }

            result += "{" + string.Join (",", named) + "}";
        }

        return result;
    }

    /// Every row's attribute data, in order, joined.
    static string Data (IList<CustomAttributeData> rows)
    {
        var parts = new List<string> ();
        foreach (CustomAttributeData row in rows)
        {
            parts.Add (Data (row));
        }

        return string.Join (" | ", parts);
    }

    /// Every attribute's own description, in order, joined.
    static string Attrs (object[] attributes)
    {
        var parts = new List<string> ();
        foreach (object attribute in attributes)
        {
            parts.Add (attribute.ToString ());
        }

        return string.Join (" | ", parts);
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

    static int Parents ()
    {
        var (assembly, module) = NewModule ("Parents");
        TypeBuilder t = module.DefineType ("T", TypeAttributes.Public);
        TypeBuilder g = module.DefineType ("G", TypeAttributes.Public);
        GenericTypeParameterBuilder[] gps = g.DefineGenericParameters ("GT");
        MethodBuilder m = t.DefineMethod ("M", MethodAttributes.Public | MethodAttributes.Static, typeof (void), new[] { typeof (int) });
        m.GetILGenerator ().Emit (OpCodes.Ret);
        MethodBuilder m2 = t.DefineMethod ("M2", MethodAttributes.Public | MethodAttributes.Static);
        GenericTypeParameterBuilder[] mgps = m2.DefineGenericParameters ("MU");
        m2.SetReturnType (typeof (void));
        m2.GetILGenerator ().Emit (OpCodes.Ret);
        // Queued: `M2` is not tokenised yet, so the row is written with the parameter's when it is.
        mgps[0].SetCustomAttribute (Tag ("mgp1"));
        // `DefineConstructor` tokenises the type's methods before it, `M2` and its generic parameter included.
        ConstructorBuilder c = t.DefineConstructor (MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
        c.GetILGenerator ().Emit (OpCodes.Ret);
        // Queued after the parameter's row is written: dropped.
        mgps[0].SetCustomAttribute (Tag ("mgp-dropped"));
        FieldBuilder f = t.DefineField ("F", typeof (int), FieldAttributes.Public);
        PropertyBuilder p = t.DefineProperty ("P", PropertyAttributes.None, typeof (int), null);
        EventBuilder e = t.DefineEvent ("E", EventAttributes.None, typeof (Action));
        ParameterBuilder pb = m.DefineParameter (1, ParameterAttributes.None, "x");

        assembly.SetCustomAttribute (Tag ("asm1"));
        t.SetCustomAttribute (Tag ("type1"));
        module.SetCustomAttribute (Tag ("module1"));
        m.SetCustomAttribute (Tag ("method1"));
        f.SetCustomAttribute (Tag ("field1"));
        t.SetCustomAttribute (Tag ("type2"));
        c.SetCustomAttribute (Tag ("ctor1"));
        p.SetCustomAttribute (Tag ("prop1"));
        e.SetCustomAttribute (Tag ("event1"));
        pb.SetCustomAttribute (Tag ("param1"));
        // Queued: written when `G` is created.
        gps[0].SetCustomAttribute (Tag ("gtp1"));
        assembly.SetCustomAttribute (Tag ("asm2"));
        // The raw overload, with the blob `Tag("raw")` encodes.
        t.SetCustomAttribute (TagCtor (typeof (string)), new byte[] { 1, 0, 3, (byte) 'r', (byte) 'a', (byte) 'w', 0, 0 });
        // On the module, an ordinary row: CoreCLR acts on it only for the assembly's token.
        module.SetCustomAttribute (
            new CustomAttributeBuilder (
                typeof (DebuggableAttribute).GetConstructor (new[] { typeof (DebuggableAttribute.DebuggingModes) }),
                new object[] { DebuggableAttribute.DebuggingModes.Default }));

        Type tt = t.CreateType ();
        Type gt = g.CreateType ();
        // Queued after `G` is created: dropped.
        gps[0].SetCustomAttribute (Tag ("gtp-dropped"));

        Assembly asm = tt.Assembly;
        if (Data (asm.GetCustomAttributesData ()) != "TagAttribute((String)asm1) | TagAttribute((String)asm2)")
        {
            return 1;
        }

        if (Attrs (asm.GetCustomAttributes (false)) != "Tag{S=asm1} | Tag{S=asm2}")
        {
            return 2;
        }

        if (Data (tt.Module.GetCustomAttributesData ())
            != "TagAttribute((String)module1) | DebuggableAttribute((DebuggingModes)1)")
        {
            return 3;
        }

        object[] moduleAttrs = tt.Module.GetCustomAttributes (false);
        if (moduleAttrs.Length != 2
            || moduleAttrs[0].ToString () != "Tag{S=module1}"
            || ((DebuggableAttribute) moduleAttrs[1]).DebuggingFlags != DebuggableAttribute.DebuggingModes.Default)
        {
            return 4;
        }

        if (Data (tt.GetCustomAttributesData ())
            != "TagAttribute((String)type1) | TagAttribute((String)type2) | TagAttribute((String)raw)")
        {
            return 5;
        }

        if (Attrs (tt.GetCustomAttributes (false)) != "Tag{S=type1} | Tag{S=type2} | Tag{S=raw}")
        {
            return 6;
        }

        MethodInfo method = tt.GetMethod ("M");
        if (Data (method.GetCustomAttributesData ()) != "TagAttribute((String)method1)")
        {
            return 7;
        }

        if (Attrs (method.GetCustomAttributes (false)) != "Tag{S=method1}")
        {
            return 8;
        }

        ConstructorInfo ctor = tt.GetConstructors ()[0];
        if (Data (ctor.GetCustomAttributesData ()) != "TagAttribute((String)ctor1)")
        {
            return 9;
        }

        if (Attrs (ctor.GetCustomAttributes (false)) != "Tag{S=ctor1}")
        {
            return 10;
        }

        PropertyInfo property = tt.GetProperty ("P", All);
        if (Data (property.GetCustomAttributesData ()) != "TagAttribute((String)prop1)")
        {
            return 11;
        }

        if (Attrs (property.GetCustomAttributes (false)) != "Tag{S=prop1}")
        {
            return 12;
        }

        EventInfo ev = tt.GetEvent ("E", All);
        if (Data (ev.GetCustomAttributesData ()) != "TagAttribute((String)event1)")
        {
            return 13;
        }

        if (Attrs (ev.GetCustomAttributes (false)) != "Tag{S=event1}")
        {
            return 14;
        }

        ParameterInfo parameter = method.GetParameters ()[0];
        if (Data (parameter.GetCustomAttributesData ()) != "TagAttribute((String)param1)")
        {
            return 15;
        }

        if (Attrs (parameter.GetCustomAttributes (false)) != "Tag{S=param1}")
        {
            return 16;
        }

        Type gtp = gt.GetGenericArguments ()[0];
        if (Data (gtp.GetCustomAttributesData ()) != "TagAttribute((String)gtp1)")
        {
            return 17;
        }

        if (Attrs (gtp.GetCustomAttributes (false)) != "Tag{S=gtp1}")
        {
            return 18;
        }

        Type mgp = tt.GetMethod ("M2").GetGenericArguments ()[0];
        if (Data (mgp.GetCustomAttributesData ()) != "TagAttribute((String)mgp1)")
        {
            return 19;
        }

        if (Attrs (mgp.GetCustomAttributes (false)) != "Tag{S=mgp1}")
        {
            return 20;
        }

        if (!tt.IsDefined (typeof (TagAttribute), false) || tt.IsDefined (typeof (SingleAttribute), false))
        {
            return 21;
        }

        if (!asm.IsDefined (typeof (TagAttribute)) || asm.IsDefined (typeof (SingleAttribute)))
        {
            return 22;
        }

        // Last, because a field's attribute read reaches `MetadataImport.GetFieldOffset`.
        FieldInfo field = tt.GetField ("F");
        if (Data (field.GetCustomAttributesData ()) != "TagAttribute((String)field1)")
        {
            return 23;
        }

        if (Attrs (field.GetCustomAttributes (false)) != "Tag{S=field1}")
        {
            return 24;
        }

        return 0;
    }

    static int Refusals ()
    {
        var (assembly, module) = NewModule ("Refusals");
        TypeBuilder t = module.DefineType ("T", TypeAttributes.Public);
        FieldBuilder f = t.DefineField ("F", typeof (int), FieldAttributes.Public);
        PropertyBuilder p = t.DefineProperty ("P", PropertyAttributes.None, typeof (int), null);
        EventBuilder e = t.DefineEvent ("E", EventAttributes.None, typeof (Action));

        if (!Throws<ArgumentNullException> (() => t.SetCustomAttribute (null, new byte[] { 1, 0, 0, 0 }), "con"))
        {
            return 30;
        }

        if (!Throws<ArgumentNullException> (() => t.SetCustomAttribute (TagCtor (typeof (string)), null), "binaryAttribute"))
        {
            return 31;
        }

        if (!Throws<ArgumentNullException> (() => t.SetCustomAttribute ((CustomAttributeBuilder) null), "customBuilder"))
        {
            return 32;
        }

        if (!Throws<ArgumentException> (() => new CustomAttributeBuilder (TagCtor (typeof (string)), new object[0])))
        {
            return 33;
        }

        if (!Throws<ArgumentException> (() => new CustomAttributeBuilder (TagCtor (typeof (string)), new object[] { 5 })))
        {
            return 34;
        }

        if (!Throws<ArgumentException> (() => new CustomAttributeBuilder (typeof (WithCctor).TypeInitializer, new object[0])))
        {
            return 35;
        }

        Type tt = t.CreateType ();
        // Obtained before the attributes below are added.
        Assembly asm = tt.Assembly;
        Module mod = tt.Module;

        if (!Throws<InvalidOperationException> (() => f.SetCustomAttribute (Tag ("field-after"))))
        {
            return 36;
        }

        if (!Throws<InvalidOperationException> (() => p.SetCustomAttribute (Tag ("prop-after"))))
        {
            return 37;
        }

        if (!Throws<InvalidOperationException> (() => e.SetCustomAttribute (Tag ("event-after"))))
        {
            return 38;
        }

        assembly.SetCustomAttribute (Tag ("asm-after"));
        module.SetCustomAttribute (Tag ("module-after"));

        if (Data (asm.GetCustomAttributesData ()) != "TagAttribute((String)asm-after)")
        {
            return 39;
        }

        if (Attrs (asm.GetCustomAttributes (false)) != "Tag{S=asm-after}")
        {
            return 40;
        }

        if (Data (mod.GetCustomAttributesData ()) != "TagAttribute((String)module-after)")
        {
            return 41;
        }

        if (Attrs (mod.GetCustomAttributes (false)) != "Tag{S=module-after}")
        {
            return 42;
        }

        return 0;
    }

    static int Malformed ()
    {
        var (_, module) = NewModule ("Malformed");
        ConstructorInfo tag = TagCtor (typeof (string));
        TypeBuilder badProlog = module.DefineType ("BadProlog", TypeAttributes.Public);
        badProlog.SetCustomAttribute (tag, new byte[] { 2, 0, 0, 0 });
        TypeBuilder truncated = module.DefineType ("Truncated", TypeAttributes.Public);
        truncated.SetCustomAttribute (tag, new byte[] { 1, 0, 9, (byte) 'a' });
        TypeBuilder trailing = module.DefineType ("Trailing", TypeAttributes.Public);
        trailing.SetCustomAttribute (tag, new byte[] { 1, 0, 1, (byte) 'a', 0, 0, 7, 7, 7 });
        TypeBuilder empty = module.DefineType ("Empty", TypeAttributes.Public);
        empty.SetCustomAttribute (tag, new byte[0]);

        Type badPrologT = badProlog.CreateType ();
        Type truncatedT = truncated.CreateType ();
        Type trailingT = trailing.CreateType ();
        Type emptyT = empty.CreateType ();

        if (!Throws<CustomAttributeFormatException> (() => badPrologT.GetCustomAttributesData ()))
        {
            return 50;
        }

        if (!Throws<CustomAttributeFormatException> (() => badPrologT.GetCustomAttributes (false)))
        {
            return 51;
        }

        if (!Throws<CustomAttributeFormatException> (() => truncatedT.GetCustomAttributesData ()))
        {
            return 52;
        }

        if (!Throws<CustomAttributeFormatException> (() => truncatedT.GetCustomAttributes (false)))
        {
            return 53;
        }

        // `GetCustomAttributesData` reads what it needs and ignores the bytes after it.
        if (Data (trailingT.GetCustomAttributesData ()) != "TagAttribute((String)a)")
        {
            return 54;
        }

        if (!Throws<CustomAttributeFormatException> (() => trailingT.GetCustomAttributes (false)))
        {
            return 55;
        }

        if (!Throws<CustomAttributeFormatException> (() => emptyT.GetCustomAttributesData ()))
        {
            return 56;
        }

        return 0;
    }

    static int NonAttribute ()
    {
        var (assembly, module) = NewModule ("NonAttribute");
        TypeBuilder o = module.DefineType ("O", TypeAttributes.Public);
        o.SetCustomAttribute (typeof (object).GetConstructor (Type.EmptyTypes), new byte[] { 1, 0, 0, 0 });
        assembly.SetCustomAttribute (
            typeof (Exception).GetConstructor (new[] { typeof (string) }), new byte[] { 1, 0, 1, (byte) 'z', 0, 0 });
        Type ot = o.CreateType ();

        if (Data (ot.GetCustomAttributesData ()) != "Object()")
        {
            return 60;
        }

        object[] objects = ot.GetCustomAttributes (false);
        if (objects.Length != 1 || objects[0].GetType () != typeof (object))
        {
            return 61;
        }

        if (Data (ot.Assembly.GetCustomAttributesData ()) != "Exception((String)z)")
        {
            return 62;
        }

        object[] exceptions = ot.Assembly.GetCustomAttributes (false);
        if (exceptions.Length != 1 || exceptions[0].GetType () != typeof (Exception) || ((Exception) exceptions[0]).Message != "z")
        {
            return 63;
        }

        return 0;
    }

    static int Duplicates ()
    {
        var (_, module) = NewModule ("Duplicates");
        TypeBuilder t = module.DefineType ("T", TypeAttributes.Public);
        t.SetCustomAttribute (Single (1));
        t.SetCustomAttribute (Single (2));
        t.SetCustomAttribute (Tag ("a"));
        t.SetCustomAttribute (Tag ("a"));
        Type tt = t.CreateType ();

        if (Data (tt.GetCustomAttributesData ())
            != "SingleAttribute((Int32)1) | SingleAttribute((Int32)2) | TagAttribute((String)a) | TagAttribute((String)a)")
        {
            return 65;
        }

        if (Attrs (tt.GetCustomAttributes (false)) != "Single1 | Single2 | Tag{S=a} | Tag{S=a}")
        {
            return 66;
        }

        if (Attrs (tt.GetCustomAttributes (typeof (SingleAttribute), false)) != "Single1 | Single2")
        {
            return 67;
        }

        if (!Throws<AmbiguousMatchException> (() => tt.GetCustomAttribute<SingleAttribute> ()))
        {
            return 68;
        }

        if (!tt.IsDefined (typeof (SingleAttribute), false))
        {
            return 69;
        }

        return 0;
    }

    static int Inherited ()
    {
        var (_, module) = NewModule ("Inherited");
        TypeBuilder b = module.DefineType ("B", TypeAttributes.Public);
        b.SetCustomAttribute (Single (1));
        b.SetCustomAttribute (Multi (1));
        b.SetCustomAttribute (NoInh (1));
        MethodBuilder bm = b.DefineMethod (
            "V", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot, typeof (void), Type.EmptyTypes);
        bm.GetILGenerator ().Emit (OpCodes.Ret);
        bm.SetCustomAttribute (Single (10));
        bm.SetCustomAttribute (Multi (10));
        PropertyBuilder bp = b.DefineProperty ("P", PropertyAttributes.None, typeof (int), null);
        MethodBuilder bget = b.DefineMethod (
            "get_P",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.SpecialName,
            typeof (int),
            Type.EmptyTypes);
        ILGenerator bil = bget.GetILGenerator ();
        bil.Emit (OpCodes.Ldc_I4_1);
        bil.Emit (OpCodes.Ret);
        bp.SetGetMethod (bget);
        bp.SetCustomAttribute (Multi (20));
        b.DefineDefaultConstructor (MethodAttributes.Public);
        Type bt = b.CreateType ();

        TypeBuilder d = module.DefineType ("D", TypeAttributes.Public, bt);
        d.SetCustomAttribute (Single (2));
        d.SetCustomAttribute (Multi (2));
        MethodBuilder dm = d.DefineMethod ("V", MethodAttributes.Public | MethodAttributes.Virtual, typeof (void), Type.EmptyTypes);
        dm.GetILGenerator ().Emit (OpCodes.Ret);
        dm.SetCustomAttribute (Multi (11));
        PropertyBuilder dp = d.DefineProperty ("P", PropertyAttributes.None, typeof (int), null);
        MethodBuilder dget = d.DefineMethod (
            "get_P", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName, typeof (int), Type.EmptyTypes);
        ILGenerator dil = dget.GetILGenerator ();
        dil.Emit (OpCodes.Ldc_I4_2);
        dil.Emit (OpCodes.Ret);
        dp.SetGetMethod (dget);
        d.DefineDefaultConstructor (MethodAttributes.Public);
        Type dt = d.CreateType ();

        TypeBuilder e = module.DefineType ("E", TypeAttributes.Public, dt);
        e.DefineDefaultConstructor (MethodAttributes.Public);
        Type et = e.CreateType ();

        if (Attrs (dt.GetCustomAttributes (true)) != "Single2 | Multi2 | Multi1")
        {
            return 70;
        }

        if (Attrs (dt.GetCustomAttributes (false)) != "Single2 | Multi2")
        {
            return 71;
        }

        if (Attrs (et.GetCustomAttributes (true)) != "Single2 | Multi2 | Multi1")
        {
            return 72;
        }

        if (!et.IsDefined (typeof (SingleAttribute), true)
            || et.IsDefined (typeof (NoInhAttribute), true)
            || dt.IsDefined (typeof (NoInhAttribute), true))
        {
            return 73;
        }

        if (Attrs (dt.GetMethod ("V").GetCustomAttributes (true)) != "Multi11 | Single10 | Multi10")
        {
            return 74;
        }

        PropertyInfo dpi = dt.GetProperty ("P", All);
        if (Attrs (Attribute.GetCustomAttributes (dpi, true)) != "Multi20")
        {
            return 75;
        }

        if (dpi.GetCustomAttributes (true).Length != 0)
        {
            return 76;
        }

        return 0;
    }

    static string MultiValues (IList<CustomAttributeData> rows)
    {
        var values = new List<string> ();
        foreach (CustomAttributeData row in rows)
        {
            values.Add (row.ConstructorArguments[0].Value.ToString ());
        }

        return string.Join (",", values);
    }

    static string MultiValues (object[] attributes)
    {
        var values = new List<string> ();
        foreach (object attribute in attributes)
        {
            values.Add (((MultiInhAttribute) attribute).N.ToString ());
        }

        return string.Join (",", values);
    }

    static int Order ()
    {
        var (assembly, module) = NewModule ("Order");
        TypeBuilder a = module.DefineType ("A", TypeAttributes.Public);
        TypeBuilder b = module.DefineType ("B", TypeAttributes.Public);
        var expectedA = new List<int> ();
        var expectedB = new List<int> ();

        for (int i = 0; i < 30; i++)
        {
            if (i % 3 == 0)
            {
                a.SetCustomAttribute (Multi (i));
                expectedA.Add (i);
            }
            else
            {
                b.SetCustomAttribute (Multi (i));
                expectedB.Add (i);
            }

            if (i == 12)
            {
                assembly.SetCustomAttribute (Multi (112));
            }
        }

        Type at = a.CreateType ();
        b.SetCustomAttribute (Multi (998));
        expectedB.Add (998);
        assembly.SetCustomAttribute (Multi (999));
        Type bt = b.CreateType ();

        string wantA = string.Join (",", expectedA);
        string wantB = string.Join (",", expectedB);

        if (MultiValues (at.GetCustomAttributesData ()) != wantA)
        {
            return 80;
        }

        if (MultiValues (at.GetCustomAttributes (false)) != wantA)
        {
            return 81;
        }

        if (expectedB.Count != 21)
        {
            return 82;
        }

        if (MultiValues (bt.GetCustomAttributesData ()) != wantB)
        {
            return 83;
        }

        if (MultiValues (bt.GetCustomAttributes (false)) != wantB)
        {
            return 84;
        }

        if (MultiValues (at.Assembly.GetCustomAttributesData ()) != "112,999")
        {
            return 85;
        }

        if (MultiValues (at.Assembly.GetCustomAttributes (false)) != "112,999")
        {
            return 86;
        }

        return 0;
    }

    /// A struct `S` of one `long` with `InlineArray(first)` and then `InlineArray(second)`, and a type `H` whose static
    /// `Size` returns `sizeof(S)`; `before` and `after` are how many attributes another type gets before and after `S`'s.
    static int InlineArrayLength (string name, int before, int after, int first, int second)
    {
        var (_, module) = NewModule (name);
        TypeBuilder filler = module.DefineType ("Filler", TypeAttributes.Public);
        for (int i = 0; i < before; i++)
        {
            filler.SetCustomAttribute (Multi (i));
        }

        TypeBuilder s = module.DefineType (
            "S", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof (ValueType));
        s.DefineField ("e", typeof (long), FieldAttributes.Public);
        ConstructorInfo inlineArray = typeof (InlineArrayAttribute).GetConstructor (new[] { typeof (int) });
        s.SetCustomAttribute (new CustomAttributeBuilder (inlineArray, new object[] { first }));
        s.SetCustomAttribute (new CustomAttributeBuilder (inlineArray, new object[] { second }));

        for (int i = 0; i < after; i++)
        {
            filler.SetCustomAttribute (Multi (before + i));
        }

        filler.CreateType ();
        s.CreateType ();
        TypeBuilder h = module.DefineType ("H", TypeAttributes.Public);
        MethodBuilder size = h.DefineMethod ("Size", MethodAttributes.Public | MethodAttributes.Static, typeof (int), Type.EmptyTypes);
        ILGenerator il = size.GetILGenerator ();
        il.Emit (OpCodes.Sizeof, s);
        il.Emit (OpCodes.Ret);
        Type ht = h.CreateType ();
        return (int) ht.GetMethod ("Size").Invoke (null, null) / 8;
    }

    static int FirstOfTwo ()
    {
        if (InlineArrayLength ("FirstOfTwoSmall", 0, 0, 2, 3) != 2)
        {
            return 90;
        }

        if (InlineArrayLength ("FirstOfTwoMid", FirstOfTwoMidBefore, FirstOfTwoMidAfter, 2, 3) != 2)
        {
            return 91;
        }

        return 0;
    }

    // Chosen so that SRM's unstable sort of the table by owner puts `S`'s second row before its first, over the image
    // PawPrint writes: 22 rows, below the 25 from which CoreCLR's type loader meets the last added first.
    const int FirstOfTwoMidBefore = 10;
    const int FirstOfTwoMidAfter = 10;

    /// A type of `module` given `builder`'s attribute and created, with what each reader of it gives or throws.
    static (string data, string attrs) ReadOne (ModuleBuilder module, string name, CustomAttributeBuilder builder)
    {
        TypeBuilder t = module.DefineType (name, TypeAttributes.Public);
        t.SetCustomAttribute (builder);
        Type tt = t.CreateType ();
        string data;
        try
        {
            data = Data (tt.GetCustomAttributesData ());
        }
        catch (Exception e)
        {
            data = "threw " + e.GetType ().Name;
        }

        string attrs;
        try
        {
            attrs = Attrs (tt.GetCustomAttributes (false));
        }
        catch (Exception e)
        {
            attrs = "threw " + e.GetType ().Name;
        }

        return (data, attrs);
    }

    static int Args ()
    {
        var (_, module) = NewModule ("Args");
        TypeBuilder own = module.DefineType ("Own", TypeAttributes.Public);
        Type ownT = own.CreateType ();
        FieldInfo F (string name) => typeof (TagAttribute).GetField (name);
        PropertyInfo P (string name) => typeof (TagAttribute).GetProperty (name);
        CustomAttributeBuilder One (Type argument, object value) => new CustomAttributeBuilder (TagCtor (argument), new object[] { value });

        // Each case: the builder, what `GetCustomAttributesData` gives, and what `GetCustomAttributes` gives, or null where
        // the attribute's instantiation reaches what PawPrint does not read yet (an `object`-typed value).
        var cases = new (CustomAttributeBuilder builder, string data, string attrs)[]
        {
            (One (typeof (string), "s"), "TagAttribute((String)s)", "Tag{S=s}"),
            (One (typeof (string), null), "TagAttribute((String)null)", "Tag{}"),
            (One (typeof (Color), Color.Green), "TagAttribute((Color)2)", "Tag{C=Green}"),
            (One (typeof (Type), typeof (int)), "TagAttribute((Type)typeof(System.Int32))", "Tag{T=typeof(System.Int32)}"),
            (One (typeof (Type), typeof (TagAttribute)), "TagAttribute((Type)typeof(TagAttribute))", "Tag{T=typeof(TagAttribute)}"),
            // Written as an assembly-qualified name, which no load of the dynamic assembly can satisfy.
            (One (typeof (Type), ownT), "threw FileNotFoundException", "threw FileNotFoundException"),
            (One (typeof (Type), own), "threw FileNotFoundException", "threw FileNotFoundException"),
            (One (typeof (Type), null), "TagAttribute((Type)null)", "Tag{}"),
            (One (typeof (Type), typeof (List<int>)),
                "TagAttribute((Type)typeof(System.Collections.Generic.List`1<System.Int32>))",
                "Tag{T=typeof(System.Collections.Generic.List`1<System.Int32>)}"),
            (One (typeof (int[]), new[] { 1, 2 }), "TagAttribute((Int32[])[(Int32)1,(Int32)2])", "Tag{A=[1,2]}"),
            (One (typeof (int[]), null), "TagAttribute((Int32[])null)", "Tag{}"),
            (One (typeof (object), 5), "TagAttribute((Int32)5)", null),
            (One (typeof (object), "os"), "TagAttribute((String)os)", null),
            (One (typeof (object), null), "TagAttribute((String)null)", null),
            (One (typeof (object), typeof (string)), "TagAttribute((Type)typeof(System.String))", null),
            (One (typeof (object), Color.Red), "TagAttribute((Color)1)", null),
            (One (typeof (object), new[] { 3, 4 }), "TagAttribute((Int32[])[(Int32)3,(Int32)4])", null),
            (One (typeof (object[]), new object[] { 1, "x", typeof (long), null, Color.Green }),
                "TagAttribute((Object[])[(Int32)1,(String)x,(Type)typeof(System.Int64),(String)null,(Color)2])",
                null),
            (new CustomAttributeBuilder (
                    TagCtor (typeof (string)),
                    new object[] { "n" },
                    new[] { F ("OField"), F ("TField"), F ("AField"), F ("CField") },
                    new object[] { 7L, typeof (byte), new[] { 9 }, Color.Red }),
                "TagAttribute((String)n){F:OField=(Int64)7,F:TField=(Type)typeof(System.Byte),F:AField=(Int32[])[(Int32)9],F:CField=(Color)1}",
                null),
            (new CustomAttributeBuilder (
                    TagCtor (typeof (string)),
                    new object[] { "n2" },
                    new[] { F ("TField"), F ("AField"), F ("CField") },
                    new object[] { typeof (byte), new[] { 9 }, Color.Red }),
                "TagAttribute((String)n2){F:TField=(Type)typeof(System.Byte),F:AField=(Int32[])[(Int32)9],F:CField=(Color)1}",
                "Tag{S=n2;TField=typeof(System.Byte);AField=[9];CField=Red}"),
            (new CustomAttributeBuilder (
                    TagCtor (typeof (string)), new object[] { "np" }, new[] { P ("Prop"), P ("OProp") }, new object[] { "pv", 1.5 }),
                "TagAttribute((String)np){P:Prop=(String)pv,P:OProp=(Double)1.5}",
                null),
            (new CustomAttributeBuilder (TagCtor (typeof (string)), new object[] { "np2" }, new[] { P ("Prop") }, new object[] { "pv" }),
                "TagAttribute((String)np2){P:Prop=(String)pv}",
                "Tag{S=np2;Prop=pv}"),
            (new CustomAttributeBuilder (
                    TagCtor (typeof (string)),
                    new object[] { "nb" },
                    new[] { P ("Prop") },
                    new object[] { null },
                    new[] { F ("OField") },
                    new object[] { null }),
                "TagAttribute((String)nb){F:OField=(String)null,P:Prop=(String)null}",
                null),
        };

        for (int i = 0; i < cases.Length; i++)
        {
            var (data, attrs) = ReadOne (module, "A" + i, cases[i].builder);
            if (data != cases[i].data)
            {
                return 100 + i;
            }

            if (cases[i].attrs != null && attrs != cases[i].attrs)
            {
                return 130 + i;
            }
        }

        return 0;
    }

    static int Friends ()
    {
        var (assembly, module) = NewModule ("Friends");
        TypeBuilder attribute = module.DefineType (
            "System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute", TypeAttributes.Public, typeof (Attribute));
        ConstructorBuilder ctor = attribute.DefineConstructor (
            MethodAttributes.Public, CallingConventions.HasThis, new[] { typeof (string) });
        ILGenerator il = ctor.GetILGenerator ();
        il.Emit (OpCodes.Ldarg_0);
        il.Emit (OpCodes.Call, typeof (Attribute).GetConstructor (All, null, Type.EmptyTypes, null));
        il.Emit (OpCodes.Ret);
        Type attributeType = attribute.CreateType ();
        ConstructorInfo created = attributeType.GetConstructor (new[] { typeof (string) });
        assembly.SetCustomAttribute (new CustomAttributeBuilder (created, new object[] { "First" }));
        assembly.SetCustomAttribute (new CustomAttributeBuilder (created, new object[] { "Second" }));

        if (Data (attributeType.Assembly.GetCustomAttributesData ())
            != "IgnoresAccessChecksToAttribute((String)First) | IgnoresAccessChecksToAttribute((String)Second)")
        {
            return 140;
        }

        return 0;
    }

    public static int Main ()
    {
        // `Parents` is last, because its field's attribute read reaches `MetadataImport.GetFieldOffset`.
        int result = Refusals ();
        if (result != 0) return result;
        result = Malformed ();
        if (result != 0) return result;
        result = NonAttribute ();
        if (result != 0) return result;
        result = Duplicates ();
        if (result != 0) return result;
        result = Inherited ();
        if (result != 0) return result;
        result = Order ();
        if (result != 0) return result;
        result = FirstOfTwo ();
        if (result != 0) return result;
        result = Args ();
        if (result != 0) return result;
        result = Friends ();
        if (result != 0) return result;
        return Parents ();
    }
}
