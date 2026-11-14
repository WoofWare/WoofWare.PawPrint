using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

// `Type.StructLayoutAttribute` builds its answer from the TypeDef's layout and string-format flags
// and, through `MetadataImport.GetClassLayout`, from the type's ClassLayout row (ECMA-335
// II.22.8): `Pack` and `Size` are the row's, or 0 when the type has none.
//
// Each type below has a distinct Pack and Size, so an answer read from a neighbouring row, or
// with the two swapped, is caught. C# writes a ClassLayout row only for a type that names `Pack`
// or `Size`, or for an empty struct, whatever its `LayoutKind`.
//
// `GetCustomAttributes` does not synthesise a `StructLayoutAttribute` (only `Serializable` and
// `ComImport` are pseudo-attributes of a type), so it reports none for any of these.

[StructLayout(LayoutKind.Sequential)]
public struct SequentialStruct
{
    public int A;
    public long B;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
public struct SequentialPacked
{
    public byte A;
    public int B;
}

[StructLayout(LayoutKind.Sequential, Size = 24, CharSet = CharSet.Unicode)]
public struct SequentialSized
{
    public int A;
}

[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 13)]
public struct ExplicitStruct
{
    [FieldOffset(0)] public int A;
    [FieldOffset(5)] public long B;
}

[StructLayout(LayoutKind.Explicit)]
public struct ExplicitUnsized
{
    [FieldOffset(0)] public int A;
    [FieldOffset(0)] public float B;
}

[StructLayout(LayoutKind.Auto, Pack = 8, Size = 32, CharSet = CharSet.Auto)]
public struct AutoStruct
{
    public int A;
    public string B;
}

public struct EmptyStruct
{
}

public class PlainClass
{
    public int P;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 40, CharSet = CharSet.Ansi)]
public class SequentialClass
{
    public int A;
}

[StructLayout(LayoutKind.Explicit, Size = 48)]
public class ExplicitClass
{
    [FieldOffset(8)] public object O;
    [FieldOffset(0)] public int I;
}

[StructLayout(LayoutKind.Sequential, Pack = 16, Size = 56)]
public struct Generic<T>
{
    public T Value;
}

public class Outer
{
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 7)]
    public struct Nested
    {
        public byte A;
    }
}

public interface IThing
{
}

public class Program
{
    // Returns 0 if `type.StructLayoutAttribute` has the given fields, and otherwise a code in
    // `failure + 0..3` saying which differs: 0 Value, 1 Pack, 2 Size, 3 CharSet.
    static int Check(Type type, LayoutKind value, int pack, int size, CharSet charSet, int failure)
    {
        StructLayoutAttribute layout = type.StructLayoutAttribute;
        if (layout == null) return failure;
        if (layout.Value != value) return failure;
        if (layout.Pack != pack) return failure + 1;
        if (layout.Size != size) return failure + 2;
        if (layout.CharSet != charSet) return failure + 3;
        return 0;
    }

    // How many `StructLayoutAttribute`s `GetCustomAttributes(false)`, the typed overload, and
    // `GetCustomAttributesData` report together; and whether `IsDefined` claims one.
    static int ReportedAsCustomAttribute(Type type)
    {
        int count = 0;
        foreach (object attribute in type.GetCustomAttributes(false))
        {
            if (attribute is StructLayoutAttribute) count++;
        }

        count += type.GetCustomAttributes(typeof(StructLayoutAttribute), false).Length;

        foreach (CustomAttributeData data in type.GetCustomAttributesData())
        {
            if (data.AttributeType == typeof(StructLayoutAttribute)) count++;
        }

        if (type.IsDefined(typeof(StructLayoutAttribute), false)) count++;
        return count;
    }

    public static int Main()
    {
        int code;

        if ((code = Check(typeof(SequentialStruct), LayoutKind.Sequential, 0, 0, CharSet.Ansi, 10)) != 0) return code;
        if ((code = Check(typeof(SequentialPacked), LayoutKind.Sequential, 2, 0, CharSet.Ansi, 20)) != 0) return code;
        if ((code = Check(typeof(SequentialSized), LayoutKind.Sequential, 0, 24, CharSet.Unicode, 30)) != 0) return code;
        if ((code = Check(typeof(ExplicitStruct), LayoutKind.Explicit, 1, 13, CharSet.Ansi, 40)) != 0) return code;
        if ((code = Check(typeof(ExplicitUnsized), LayoutKind.Explicit, 0, 0, CharSet.Ansi, 50)) != 0) return code;
        if ((code = Check(typeof(AutoStruct), LayoutKind.Auto, 8, 32, CharSet.Auto, 60)) != 0) return code;
        if ((code = Check(typeof(EmptyStruct), LayoutKind.Sequential, 0, 1, CharSet.Ansi, 70)) != 0) return code;
        if ((code = Check(typeof(PlainClass), LayoutKind.Auto, 0, 0, CharSet.Ansi, 80)) != 0) return code;
        if ((code = Check(typeof(SequentialClass), LayoutKind.Sequential, 4, 40, CharSet.Ansi, 90)) != 0) return code;
        if ((code = Check(typeof(ExplicitClass), LayoutKind.Explicit, 0, 48, CharSet.Ansi, 100)) != 0) return code;

        // An instantiation reports its definition's row; so does the definition itself.
        if ((code = Check(typeof(Generic<int>), LayoutKind.Sequential, 16, 56, CharSet.Ansi, 110)) != 0) return code;
        if ((code = Check(typeof(Generic<>), LayoutKind.Sequential, 16, 56, CharSet.Ansi, 120)) != 0) return code;
        if ((code = Check(typeof(Outer.Nested), LayoutKind.Sequential, 1, 7, CharSet.Ansi, 130)) != 0) return code;

        // CoreLib answers before it asks the metadata for these.
        if (typeof(IThing).StructLayoutAttribute != null) return 140;
        if (typeof(SequentialPacked[]).StructLayoutAttribute != null) return 141;
        if (typeof(Generic<>).GetGenericArguments()[0].StructLayoutAttribute != null) return 142;

        // Each call builds a fresh attribute.
        if (ReferenceEquals(typeof(ExplicitStruct).StructLayoutAttribute, typeof(ExplicitStruct).StructLayoutAttribute)) return 150;

        if (ReportedAsCustomAttribute(typeof(SequentialPacked)) != 0) return 160;
        if (ReportedAsCustomAttribute(typeof(ExplicitStruct)) != 0) return 161;
        if (ReportedAsCustomAttribute(typeof(SequentialClass)) != 0) return 162;
        if (ReportedAsCustomAttribute(typeof(PlainClass)) != 0) return 163;

        return 0;
    }
}
