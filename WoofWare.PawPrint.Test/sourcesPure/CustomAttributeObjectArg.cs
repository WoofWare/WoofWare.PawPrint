using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;

// ECMA-335 II.23.3 encodes an `object`-typed attribute argument as TAGGED_OBJECT: the value's own
// FieldOrPropType (an element-type tag, an enum's type name, or SZARRAY plus an element tag) in
// front of the value. CoreCLR decodes it in `GetDataFromBlob`'s `typeObject` arm and boxes what it
// reads as the type the tag names; CoreLib's managed `CustomAttributeData` parser decodes the same
// bytes independently, in `ParseCustomAttributeType`.
//
// Every attribute here carries a trailing int, and the enums are not int-wide, so a value read at
// the wrong width desynchronises the cursor and corrupts the tail rather than failing silently.
// An `object[]` parameter is SZARRAY whose elements are each TAGGED_OBJECT, which is a different
// path from an `object` parameter whose tag happens to say SZARRAY; both are here.
//
// Roslyn writes every null `object` argument as a null *string* (`0E FF`), whatever the C# cast
// says, so a null `Type` or a null array inside a TAGGED_OBJECT cannot be written from C#; the
// decoder's own tests cover those encodings.
//
// Named arguments of type `object` reach a different QCall; `CustomAttributeObjectNamedArg.cs`
// instantiates those, and this file reads them only through `CustomAttributeData`, which never
// instantiates anything.

public enum ByteFlag : byte
{
    None = 0,
    Big = 200,
}

public enum LongFlag : long
{
    Zero = 0,
    Huge = 0x123456789ABCL,
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class ObjAttribute : Attribute
{
    public ObjAttribute(int tag, object value, int tail)
    {
        Tag = tag;
        Value = value;
        Tail = tail;
    }

    public int Tag { get; }
    public object Value { get; }
    public int Tail { get; }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class ObjArrayAttribute : Attribute
{
    public ObjArrayAttribute(int tag, object[] values, int tail)
    {
        Tag = tag;
        Values = values;
        Tail = tail;
    }

    public int Tag { get; }
    public object[] Values { get; }
    public int Tail { get; }
}

// Two adjacent object parameters, so the second is read from wherever the first left the cursor.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class PairAttribute : Attribute
{
    public PairAttribute(object first, object second, int tail)
    {
        First = first;
        Second = second;
        Tail = tail;
    }

    public object First { get; }
    public object Second { get; }
    public int Tail { get; }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class NamedObjAttribute : Attribute
{
    public NamedObjAttribute(int tag)
    {
        Tag = tag;
    }

    public int Tag { get; }
    public object Prop { get; set; }
    public object Field;
}

public class Other
{
}

[Obj(1, true, 1001)]
[Obj(2, 'Z', 1002)]
[Obj(3, (sbyte)-5, 1003)]
[Obj(4, (byte)250, 1004)]
[Obj(5, (short)-1234, 1005)]
[Obj(6, (ushort)60000, 1006)]
[Obj(7, -123456, 1007)]
[Obj(8, 4000000000u, 1008)]
[Obj(9, -1099511627776L, 1009)]
[Obj(10, 0xFEDCBA9876543210UL, 1010)]
[Obj(11, 1.5f, 1011)]
[Obj(12, -2.25, 1012)]
[Obj(13, "hello", 1013)]
[Obj(14, "", 1014)]
[Obj(15, null, 1015)]
[Obj(16, typeof(Other), 1016)]
[Obj(18, ByteFlag.Big, 1018)]
[Obj(19, LongFlag.Huge, 1019)]
[Obj(20, new int[] { 1, -2, 3 }, 1020)]
[Obj(21, new byte[0], 1021)]
[Obj(23, new string[] { "a", null, "" }, 1023)]
[Obj(24, new Type[] { typeof(Other), null, typeof(Decorated) }, 1024)]
[Obj(25, new ByteFlag[] { ByteFlag.Big, ByteFlag.None }, 1025)]
[Obj(26, new LongFlag[] { LongFlag.Huge }, 1026)]
[Obj(28, new object[] { 1, "two", typeof(Other), ByteFlag.Big, null, new int[] { 7 }, 3.5 }, 1028)]
[Obj(29, new bool[] { true, false }, 1029)]
[Obj(30, new char[] { 'x', 'y' }, 1030)]
[Obj(31, new sbyte[] { -1 }, 1031)]
[Obj(32, new short[] { -2 }, 1032)]
[Obj(33, new ushort[] { 3 }, 1033)]
[Obj(34, new uint[] { 4 }, 1034)]
[Obj(35, new long[] { -5 }, 1035)]
[Obj(36, new ulong[] { 6 }, 1036)]
[Obj(37, new float[] { 7.5f }, 1037)]
[Obj(38, new double[] { -8.5 }, 1038)]
[ObjArray(1, new object[] { 1, "x", null, typeof(Other), LongFlag.Huge, (byte)9 }, 2001)]
[ObjArray(2, new object[0], 2002)]
[ObjArray(3, null, 2003)]
[Pair(ByteFlag.Big, LongFlag.Huge, 3001)]
[Pair("s", typeof(Other), 3002)]
public class Decorated
{
}

[NamedObj(1, Prop = (byte)7, Field = "f")]
[NamedObj(2, Prop = ByteFlag.Big, Field = typeof(Other))]
[NamedObj(3, Prop = new int[] { 4, 5 }, Field = null)]
public class NamedDecorated
{
}

public class CustomAttributeObjectArg
{
    static int next = 1;
    static int firstFailure = 0;

    // Read once: each read instantiates every attribute of that type on the class.
    static Attribute[] objs;
    static Attribute[] objArrays;
    static Attribute[] pairs;
    static IList<CustomAttributeData> decoratedData;
    static IList<CustomAttributeData> namedData;

    static void Check(bool ok)
    {
        int index = next;
        next = next + 1;
        if (!ok && firstFailure == 0)
        {
            firstFailure = index;
        }
    }

    static ObjAttribute Obj(int tag)
    {
        foreach (var a in objs)
        {
            var o = (ObjAttribute)a;
            if (o.Tag == tag) return o;
        }
        return null;
    }

    static ObjArrayAttribute ObjArray(int tag)
    {
        foreach (var a in objArrays)
        {
            var o = (ObjArrayAttribute)a;
            if (o.Tag == tag) return o;
        }
        return null;
    }

    static PairAttribute Pair(int tail)
    {
        foreach (var a in pairs)
        {
            var p = (PairAttribute)a;
            if (p.Tail == tail) return p;
        }
        return null;
    }

    // The value of `Obj(tag, ...)`, provided the tail survived the decode.
    static object Value(int tag)
    {
        var o = Obj(tag);
        if (o == null || o.Tail != 1000 + tag) return "missing or bad tail";
        return o.Value;
    }

    static bool Is(object v, Type t)
    {
        return v != null && v.GetType() == t;
    }

    static CustomAttributeData Data(Type decorated, Type attribute, int tag)
    {
        foreach (var d in decorated == typeof(Decorated) ? decoratedData : namedData)
        {
            if (d.AttributeType != attribute) continue;
            if (d.ConstructorArguments.Count == 0) continue;
            var first = d.ConstructorArguments[0];
            if (first.ArgumentType == typeof(int) && (int)first.Value == tag) return d;
        }
        return null;
    }

    // The `object` argument of `Obj(tag, ...)` as CustomAttributeData reports it, provided the tail
    // survived the decode.
    static CustomAttributeTypedArgument DataValue(int tag)
    {
        var d = Data(typeof(Decorated), typeof(ObjAttribute), tag);
        if (d == null || d.ConstructorArguments.Count != 3) return default;
        var tail = d.ConstructorArguments[2];
        if (tail.ArgumentType != typeof(int) || (int)tail.Value != 1000 + tag) return default;
        return d.ConstructorArguments[1];
    }

    static bool Typed(CustomAttributeTypedArgument a, Type t, object v)
    {
        if (a.ArgumentType != t) return false;
        if (v == null) return a.Value == null;
        return a.Value != null && a.Value.GetType() == v.GetType() && a.Value.Equals(v);
    }

    // CoreLib lists named arguments by member (fields first), not in blob order, so they are looked
    // up by name.
    static CustomAttributeTypedArgument Named(CustomAttributeData d, string member)
    {
        foreach (var n in d.NamedArguments)
        {
            if (n.MemberName == member) return n.TypedValue;
        }
        return default;
    }

    static IList<CustomAttributeTypedArgument> Elements(CustomAttributeTypedArgument a, Type arrayType)
    {
        if (a.ArgumentType != arrayType) return null;
        return a.Value as ReadOnlyCollection<CustomAttributeTypedArgument>;
    }

    static int Main(string[] argv)
    {
        objs = Attribute.GetCustomAttributes(typeof(Decorated), typeof(ObjAttribute));
        objArrays = Attribute.GetCustomAttributes(typeof(Decorated), typeof(ObjArrayAttribute));
        pairs = Attribute.GetCustomAttributes(typeof(Decorated), typeof(PairAttribute));
        decoratedData = CustomAttributeData.GetCustomAttributes(typeof(Decorated));
        namedData = CustomAttributeData.GetCustomAttributes(typeof(NamedDecorated));

        // ---- Instances: boxed primitives (1-12) ----
        object v;
        v = Value(1); Check(Is(v, typeof(bool)) && (bool)v);
        v = Value(2); Check(Is(v, typeof(char)) && (char)v == 'Z');
        v = Value(3); Check(Is(v, typeof(sbyte)) && (sbyte)v == -5);
        v = Value(4); Check(Is(v, typeof(byte)) && (byte)v == 250);
        v = Value(5); Check(Is(v, typeof(short)) && (short)v == -1234);
        v = Value(6); Check(Is(v, typeof(ushort)) && (ushort)v == 60000);
        v = Value(7); Check(Is(v, typeof(int)) && (int)v == -123456);
        v = Value(8); Check(Is(v, typeof(uint)) && (uint)v == 4000000000u);
        v = Value(9); Check(Is(v, typeof(long)) && (long)v == -1099511627776L);
        v = Value(10); Check(Is(v, typeof(ulong)) && (ulong)v == 0xFEDCBA9876543210UL);
        v = Value(11); Check(Is(v, typeof(float)) && (float)v == 1.5f);
        v = Value(12); Check(Is(v, typeof(double)) && (double)v == -2.25);

        // ---- Instances: strings, null, types, enums (13-19) ----
        v = Value(13); Check(Is(v, typeof(string)) && (string)v == "hello");
        v = Value(14); Check(Is(v, typeof(string)) && (string)v == "");
        v = Value(15); Check(v == null);
        v = Value(16); Check(v is Type && (Type)v == typeof(Other));
        v = Value(18); Check(Is(v, typeof(ByteFlag)) && (ByteFlag)v == ByteFlag.Big);
        v = Value(19); Check(Is(v, typeof(LongFlag)) && (LongFlag)v == LongFlag.Huge);

        // ---- Instances: arrays (20-38) ----
        v = Value(20);
        Check(Is(v, typeof(int[])) && ((int[])v).Length == 3 && ((int[])v)[0] == 1 && ((int[])v)[1] == -2 && ((int[])v)[2] == 3);
        v = Value(21); Check(Is(v, typeof(byte[])) && ((byte[])v).Length == 0);
        v = Value(23);
        Check(Is(v, typeof(string[])) && ((string[])v).Length == 3 && ((string[])v)[0] == "a" && ((string[])v)[1] == null && ((string[])v)[2] == "");
        v = Value(24);
        Check(Is(v, typeof(Type[])) && ((Type[])v).Length == 3 && ((Type[])v)[0] == typeof(Other) && ((Type[])v)[1] == null && ((Type[])v)[2] == typeof(Decorated));
        v = Value(25);
        Check(Is(v, typeof(ByteFlag[])) && ((ByteFlag[])v).Length == 2 && ((ByteFlag[])v)[0] == ByteFlag.Big && ((ByteFlag[])v)[1] == ByteFlag.None);
        v = Value(26); Check(Is(v, typeof(LongFlag[])) && ((LongFlag[])v).Length == 1 && ((LongFlag[])v)[0] == LongFlag.Huge);

        v = Value(28);
        Check(Is(v, typeof(object[])) && ((object[])v).Length == 7);
        if (Is(v, typeof(object[])) && ((object[])v).Length == 7)
        {
            object[] mixed = (object[])v;
            Check(Is(mixed[0], typeof(int)) && (int)mixed[0] == 1);
            Check(Is(mixed[1], typeof(string)) && (string)mixed[1] == "two");
            Check(mixed[2] is Type && (Type)mixed[2] == typeof(Other));
            Check(Is(mixed[3], typeof(ByteFlag)) && (ByteFlag)mixed[3] == ByteFlag.Big);
            Check(mixed[4] == null);
            Check(Is(mixed[5], typeof(int[])) && ((int[])mixed[5]).Length == 1 && ((int[])mixed[5])[0] == 7);
            Check(Is(mixed[6], typeof(double)) && (double)mixed[6] == 3.5);
        }
        else
        {
            next = next + 7;
        }

        v = Value(29); Check(Is(v, typeof(bool[])) && ((bool[])v).Length == 2 && ((bool[])v)[0] && !((bool[])v)[1]);
        v = Value(30); Check(Is(v, typeof(char[])) && ((char[])v).Length == 2 && ((char[])v)[0] == 'x' && ((char[])v)[1] == 'y');
        v = Value(31); Check(Is(v, typeof(sbyte[])) && ((sbyte[])v).Length == 1 && ((sbyte[])v)[0] == -1);
        v = Value(32); Check(Is(v, typeof(short[])) && ((short[])v).Length == 1 && ((short[])v)[0] == -2);
        v = Value(33); Check(Is(v, typeof(ushort[])) && ((ushort[])v).Length == 1 && ((ushort[])v)[0] == 3);
        v = Value(34); Check(Is(v, typeof(uint[])) && ((uint[])v).Length == 1 && ((uint[])v)[0] == 4);
        v = Value(35); Check(Is(v, typeof(long[])) && ((long[])v).Length == 1 && ((long[])v)[0] == -5);
        v = Value(36); Check(Is(v, typeof(ulong[])) && ((ulong[])v).Length == 1 && ((ulong[])v)[0] == 6);
        v = Value(37); Check(Is(v, typeof(float[])) && ((float[])v).Length == 1 && ((float[])v)[0] == 7.5f);
        v = Value(38); Check(Is(v, typeof(double[])) && ((double[])v).Length == 1 && ((double[])v)[0] == -8.5);

        // ---- Instances: an object[] parameter ----
        var oa1 = ObjArray(1);
        Check(oa1 != null && oa1.Tail == 2001 && oa1.Values != null && oa1.Values.Length == 6);
        if (oa1 != null && oa1.Values != null && oa1.Values.Length == 6)
        {
            object[] vs = oa1.Values;
            Check(vs.GetType() == typeof(object[]));
            Check(Is(vs[0], typeof(int)) && (int)vs[0] == 1);
            Check(Is(vs[1], typeof(string)) && (string)vs[1] == "x");
            Check(vs[2] == null);
            Check(vs[3] is Type && (Type)vs[3] == typeof(Other));
            Check(Is(vs[4], typeof(LongFlag)) && (LongFlag)vs[4] == LongFlag.Huge);
            Check(Is(vs[5], typeof(byte)) && (byte)vs[5] == 9);
        }
        else
        {
            next = next + 7;
        }
        var oa2 = ObjArray(2);
        Check(oa2 != null && oa2.Tail == 2002 && oa2.Values != null && oa2.Values.GetType() == typeof(object[]) && oa2.Values.Length == 0);
        var oa3 = ObjArray(3);
        Check(oa3 != null && oa3.Tail == 2003 && oa3.Values == null);

        // ---- Instances: adjacent object parameters ----
        var p1 = Pair(3001);
        Check(p1 != null && Is(p1.First, typeof(ByteFlag)) && (ByteFlag)p1.First == ByteFlag.Big && Is(p1.Second, typeof(LongFlag)) && (LongFlag)p1.Second == LongFlag.Huge);
        var p2 = Pair(3002);
        Check(p2 != null && Is(p2.First, typeof(string)) && (string)p2.First == "s" && p2.Second is Type && (Type)p2.Second == typeof(Other));

        // ---- CustomAttributeData: scalars ----
        // A boxed primitive reports its own type; an enum reports the enum type but a value of the
        // underlying type; a null reports the type its tag named.
        Check(Typed(DataValue(1), typeof(bool), true));
        Check(Typed(DataValue(2), typeof(char), 'Z'));
        Check(Typed(DataValue(3), typeof(sbyte), (sbyte)-5));
        Check(Typed(DataValue(4), typeof(byte), (byte)250));
        Check(Typed(DataValue(5), typeof(short), (short)-1234));
        Check(Typed(DataValue(6), typeof(ushort), (ushort)60000));
        Check(Typed(DataValue(7), typeof(int), -123456));
        Check(Typed(DataValue(8), typeof(uint), 4000000000u));
        Check(Typed(DataValue(9), typeof(long), -1099511627776L));
        Check(Typed(DataValue(10), typeof(ulong), 0xFEDCBA9876543210UL));
        Check(Typed(DataValue(11), typeof(float), 1.5f));
        Check(Typed(DataValue(12), typeof(double), -2.25));
        Check(Typed(DataValue(13), typeof(string), "hello"));
        Check(Typed(DataValue(14), typeof(string), ""));
        Check(Typed(DataValue(15), typeof(string), null));
        Check(DataValue(16).ArgumentType == typeof(Type) && (Type)DataValue(16).Value == typeof(Other));
        Check(Typed(DataValue(18), typeof(ByteFlag), (byte)200));
        Check(Typed(DataValue(19), typeof(LongFlag), 0x123456789ABCL));

        // ---- CustomAttributeData: arrays ----
        var e20 = Elements(DataValue(20), typeof(int[]));
        Check(e20 != null && e20.Count == 3 && Typed(e20[0], typeof(int), 1) && Typed(e20[1], typeof(int), -2) && Typed(e20[2], typeof(int), 3));
        var e21 = Elements(DataValue(21), typeof(byte[]));
        Check(e21 != null && e21.Count == 0);
        var e23 = Elements(DataValue(23), typeof(string[]));
        Check(e23 != null && e23.Count == 3 && Typed(e23[0], typeof(string), "a") && Typed(e23[1], typeof(string), null) && Typed(e23[2], typeof(string), ""));
        var e24 = Elements(DataValue(24), typeof(Type[]));
        Check(e24 != null && e24.Count == 3 && (Type)e24[0].Value == typeof(Other) && Typed(e24[1], typeof(Type), null) && (Type)e24[2].Value == typeof(Decorated));
        var e25 = Elements(DataValue(25), typeof(ByteFlag[]));
        Check(e25 != null && e25.Count == 2 && Typed(e25[0], typeof(ByteFlag), (byte)200) && Typed(e25[1], typeof(ByteFlag), (byte)0));
        var e26 = Elements(DataValue(26), typeof(LongFlag[]));
        Check(e26 != null && e26.Count == 1 && Typed(e26[0], typeof(LongFlag), 0x123456789ABCL));
        var e28 = Elements(DataValue(28), typeof(object[]));
        Check(e28 != null && e28.Count == 7);
        if (e28 != null && e28.Count == 7)
        {
            Check(Typed(e28[0], typeof(int), 1));
            Check(Typed(e28[1], typeof(string), "two"));
            Check(e28[2].ArgumentType == typeof(Type) && (Type)e28[2].Value == typeof(Other));
            Check(Typed(e28[3], typeof(ByteFlag), (byte)200));
            Check(Typed(e28[4], typeof(string), null));
            var nested = Elements(e28[5], typeof(int[]));
            Check(nested != null && nested.Count == 1 && Typed(nested[0], typeof(int), 7));
            Check(Typed(e28[6], typeof(double), 3.5));
        }
        else
        {
            next = next + 7;
        }
        var e38 = Elements(DataValue(38), typeof(double[]));
        Check(e38 != null && e38.Count == 1 && Typed(e38[0], typeof(double), -8.5));

        // ---- CustomAttributeData: an object[] parameter ----
        var oad1 = Data(typeof(Decorated), typeof(ObjArrayAttribute), 1);
        var oadElems = oad1 == null ? null : Elements(oad1.ConstructorArguments[1], typeof(object[]));
        Check(oadElems != null && oadElems.Count == 6 && Typed(oadElems[0], typeof(int), 1) && Typed(oadElems[1], typeof(string), "x") && Typed(oadElems[2], typeof(string), null) && Typed(oadElems[4], typeof(LongFlag), 0x123456789ABCL) && Typed(oadElems[5], typeof(byte), (byte)9));
        var oad3 = Data(typeof(Decorated), typeof(ObjArrayAttribute), 3);
        Check(oad3 != null && Typed(oad3.ConstructorArguments[1], typeof(object[]), null) && Typed(oad3.ConstructorArguments[2], typeof(int), 2003));

        // ---- CustomAttributeData: named object arguments ----
        var n1 = Data(typeof(NamedDecorated), typeof(NamedObjAttribute), 1);
        var n2 = Data(typeof(NamedDecorated), typeof(NamedObjAttribute), 2);
        var n3 = Data(typeof(NamedDecorated), typeof(NamedObjAttribute), 3);
        Check(n1 != null && n1.NamedArguments.Count == 2 && n2 != null && n2.NamedArguments.Count == 2 && n3 != null && n3.NamedArguments.Count == 2);
        if (n1 != null && n1.NamedArguments.Count == 2 && n2 != null && n2.NamedArguments.Count == 2 && n3 != null && n3.NamedArguments.Count == 2)
        {
            Check(Typed(Named(n1, "Prop"), typeof(byte), (byte)7));
            Check(Typed(Named(n1, "Field"), typeof(string), "f"));
            Check(Typed(Named(n2, "Prop"), typeof(ByteFlag), (byte)200));
            Check(Named(n2, "Field").ArgumentType == typeof(Type) && (Type)Named(n2, "Field").Value == typeof(Other));
            var n3Prop = Elements(Named(n3, "Prop"), typeof(int[]));
            Check(n3Prop != null && n3Prop.Count == 2 && Typed(n3Prop[0], typeof(int), 4) && Typed(n3Prop[1], typeof(int), 5));
            Check(Typed(Named(n3, "Field"), typeof(string), null));
        }
        else
        {
            next = next + 6;
        }

        return firstFailure;
    }
}
