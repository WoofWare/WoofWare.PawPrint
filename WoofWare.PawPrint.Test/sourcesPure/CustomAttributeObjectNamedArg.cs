using System;

// A named argument of type `object` is a NamedArg whose FieldOrPropType is TAGGED_OBJECT (0x51),
// so its value carries a second FieldOrPropType in front of it, exactly as an `object` fixed
// argument's does. Instantiating the attribute sets it through `CustomAttribute_CreatePropertyOrFieldData`,
// which decodes the value in the same `GetDataFromBlob` arm as the fixed-arg QCall.
// `CustomAttributeObjectArg.cs` covers `object` fixed arguments, and reads these named ones
// through `CustomAttributeData`, which never instantiates.
//
// The enum is byte-wide, so a value read at the wrong width desynchronises the cursor and
// corrupts the member set after it.

public enum ByteFlag : byte
{
    None = 0,
    Big = 200,
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
    public int Tail;
}

public class Other
{
}

[NamedObj(1, Prop = (byte)7, Field = "f", Tail = 101)]
[NamedObj(2, Prop = ByteFlag.Big, Field = typeof(Other), Tail = 102)]
[NamedObj(3, Prop = new int[] { 4, 5 }, Field = null, Tail = 103)]
[NamedObj(4, Prop = new object[] { 1.5, "s", ByteFlag.None }, Field = -9L, Tail = 104)]
public class NamedDecorated
{
}

public class CustomAttributeObjectNamedArg
{
    static NamedObjAttribute Named(int tag)
    {
        foreach (var a in Attribute.GetCustomAttributes(typeof(NamedDecorated), typeof(NamedObjAttribute)))
        {
            var n = (NamedObjAttribute)a;
            if (n.Tag == tag && n.Tail == 100 + tag) return n;
        }
        return null;
    }

    static bool Is(object v, Type t)
    {
        return v != null && v.GetType() == t;
    }

    static int Main(string[] argv)
    {
        var n1 = Named(1);
        if (n1 == null) return 1;
        if (!Is(n1.Prop, typeof(byte)) || (byte)n1.Prop != 7) return 2;
        if (!Is(n1.Field, typeof(string)) || (string)n1.Field != "f") return 3;

        var n2 = Named(2);
        if (n2 == null) return 4;
        if (!Is(n2.Prop, typeof(ByteFlag)) || (ByteFlag)n2.Prop != ByteFlag.Big) return 5;
        if (!(n2.Field is Type) || (Type)n2.Field != typeof(Other)) return 6;

        var n3 = Named(3);
        if (n3 == null) return 7;
        if (!Is(n3.Prop, typeof(int[])) || ((int[])n3.Prop).Length != 2 || ((int[])n3.Prop)[0] != 4 || ((int[])n3.Prop)[1] != 5) return 8;
        if (n3.Field != null) return 9;

        var n4 = Named(4);
        if (n4 == null) return 10;
        if (!Is(n4.Prop, typeof(object[])) || ((object[])n4.Prop).Length != 3) return 11;
        object[] mixed = (object[])n4.Prop;
        if (!Is(mixed[0], typeof(double)) || (double)mixed[0] != 1.5) return 12;
        if (!Is(mixed[1], typeof(string)) || (string)mixed[1] != "s") return 13;
        if (!Is(mixed[2], typeof(ByteFlag)) || (ByteFlag)mixed[2] != ByteFlag.None) return 14;
        if (!Is(n4.Field, typeof(long)) || (long)n4.Field != -9L) return 15;

        return 0;
    }
}
