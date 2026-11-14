using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

// CoreLib synthesises a `FieldOffsetAttribute` for a field from its FieldLayout row (ECMA-335
// II.22.16), through `MetadataImport.GetFieldOffset`. Every field's `GetCustomAttributes`,
// `GetCustomAttributesData` and `IsDefined` asks, whatever its declaring type's layout, so a field
// with no row must come back with no attribute.
//
// `Explicit.D` sits at an odd offset and `C` overlaps `B`, so an answer taken from a neighbouring
// row, or one off by one, is caught. `Explicit.S` is static, and has no row although its type is
// explicitly laid out. C# emits no row for a field of a sequential struct or of a class.

[StructLayout(LayoutKind.Explicit)]
public struct Explicit
{
    [FieldOffset(0)] public int A;
    [FieldOffset(4)] public long B;
    [FieldOffset(4)] public int C;
    [FieldOffset(13)] public byte D;
    public static int S;
}

[StructLayout(LayoutKind.Sequential)]
public struct Sequential
{
    public int X;
    public long Y;
}

public class Plain
{
    public int P;
    public static string Q;
}

public class Program
{
    const BindingFlags All =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    static FieldInfo Field(Type type, string name) => type.GetField(name, All);

    // The offset `GetCustomAttributes(typeof(FieldOffsetAttribute))` reports, or -1 for none; -2 if
    // it reports more than one.
    static int OffsetAttribute(FieldInfo field)
    {
        object[] attributes = field.GetCustomAttributes(typeof(FieldOffsetAttribute), false);
        if (attributes.Length == 0) return -1;
        if (attributes.Length != 1) return -2;
        return ((FieldOffsetAttribute)attributes[0]).Value;
    }

    // The same, read through `GetCustomAttributesData`, whose list also holds any real custom
    // attributes; -3 if the attribute's constructor argument is not one Int32.
    static int OffsetData(FieldInfo field)
    {
        int found = -1;
        foreach (CustomAttributeData data in field.GetCustomAttributesData())
        {
            if (data.AttributeType != typeof(FieldOffsetAttribute)) continue;
            if (found != -1) return -2;
            IList<CustomAttributeTypedArgument> arguments = data.ConstructorArguments;
            if (arguments.Count != 1 || arguments[0].ArgumentType != typeof(int)) return -3;
            found = (int)arguments[0].Value;
        }

        return found;
    }

    public static int Main()
    {
        Type ex = typeof(Explicit);

        if (OffsetAttribute(Field(ex, "A")) != 0) return 1;
        if (OffsetAttribute(Field(ex, "B")) != 4) return 2;
        if (OffsetAttribute(Field(ex, "C")) != 4) return 3;
        if (OffsetAttribute(Field(ex, "D")) != 13) return 4;
        if (OffsetAttribute(Field(ex, "S")) != -1) return 5;

        if (OffsetData(Field(ex, "A")) != 0) return 6;
        if (OffsetData(Field(ex, "D")) != 13) return 7;
        if (OffsetData(Field(ex, "S")) != -1) return 8;

        Type seq = typeof(Sequential);
        if (OffsetAttribute(Field(seq, "X")) != -1) return 9;
        if (OffsetAttribute(Field(seq, "Y")) != -1) return 10;
        if (OffsetData(Field(seq, "Y")) != -1) return 11;

        Type plain = typeof(Plain);
        if (OffsetAttribute(Field(plain, "P")) != -1) return 12;
        if (OffsetAttribute(Field(plain, "Q")) != -1) return 13;
        if (OffsetData(Field(plain, "P")) != -1) return 14;

        if (!Field(ex, "D").IsDefined(typeof(FieldOffsetAttribute), false)) return 15;
        if (Field(ex, "S").IsDefined(typeof(FieldOffsetAttribute), false)) return 16;
        if (Field(seq, "X").IsDefined(typeof(FieldOffsetAttribute), false)) return 17;

        // Every attribute of `B`, not only those of one type: the offset is the only one it has.
        object[] all = Field(ex, "B").GetCustomAttributes(false);
        if (all.Length != 1) return 18;
        if (!(all[0] is FieldOffsetAttribute only) || only.Value != 4) return 19;

        if (Field(plain, "P").GetCustomAttributes(false).Length != 0) return 20;

        return 0;
    }
}
