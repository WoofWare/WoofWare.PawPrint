using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

// An unfiltered read of a field's custom attributes -- `GetCustomAttributes(false)`, an `Attribute`
// or `object` filter, `GetCustomAttributesData()` -- also asks for the field's pseudo-attributes
// (RuntimeCustomAttributeData.cs `PseudoCustomAttribute.GetCustomAttributes(RuntimeFieldInfo, ...)`):
// `MarshalAs` from the field's marshalling row, `NonSerialized` from its flags, and `FieldOffset`
// from `MetadataImport.GetFieldOffset`, which reads the FieldLayout row. That is asked for every
// field, whatever attributes it carries, so a field of a sequential class exercises it as much as
// one of an explicit-layout struct does.

[AttributeUsage(AttributeTargets.Field)]
public sealed class NoteAttribute : Attribute
{
    public NoteAttribute(int value)
    {
        Value = value;
    }

    public int Value { get; }
}

public class Holder
{
    [Note(3)]
    public static int Plain;
}

[StructLayout(LayoutKind.Explicit)]
public struct Overlay
{
    [FieldOffset(0)]
    public long All;

    [FieldOffset(4)]
    [Note(4)]
    public int At4;
}

public class FieldCustomAttributesUnfiltered
{
    static int next = 1;
    static int firstFailure = 0;

    static void Check(bool ok)
    {
        int index = next;
        next = next + 1;
        if (!ok && firstFailure == 0)
        {
            firstFailure = index;
        }
    }

    public static int Main(string[] argv)
    {
        FieldInfo plain = typeof(Holder).GetField("Plain");

        object[] plainAttrs = plain.GetCustomAttributes(false);
        Check(plainAttrs.Length == 1);
        Check(plainAttrs.Length == 1 && ((NoteAttribute)plainAttrs[0]).Value == 3);
        Check(plain.GetCustomAttributesData().Count == 1);
        Check(plain.GetCustomAttribute<FieldOffsetAttribute>() == null);

        FieldInfo at4 = typeof(Overlay).GetField("At4");

        // The declared attribute and the FieldOffset pseudo-attribute, in an order this test does
        // not pin.
        object[] at4Attrs = at4.GetCustomAttributes(false);
        Check(at4Attrs.Length == 2);

        FieldOffsetAttribute offset = at4.GetCustomAttribute<FieldOffsetAttribute>();
        Check(offset != null && offset.Value == 4);
        Check(at4.IsDefined(typeof(FieldOffsetAttribute), false));
        Check(typeof(Overlay).GetField("All").GetCustomAttribute<FieldOffsetAttribute>().Value == 0);

        IList<CustomAttributeData> data = at4.GetCustomAttributesData();
        Check(data.Count == 2);

        bool sawOffset = false;
        foreach (CustomAttributeData d in data)
        {
            if (d.AttributeType == typeof(FieldOffsetAttribute))
            {
                sawOffset = (int)d.ConstructorArguments[0].Value == 4;
            }
        }

        Check(sawOffset);

        return firstFailure;
    }
}
