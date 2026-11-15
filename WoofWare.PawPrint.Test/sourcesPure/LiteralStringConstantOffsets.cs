using System;
using System.Reflection;

// String constants read back through `MetadataImport.GetDefaultValue`, whose `char*` points into
// the `#Blob` heap at the first byte of the constant's contents, and `MdConstant` copies them out
// with `new string(char*, 0, length)`.
//
// A string blob is a one-byte length prefix and an even number of content bytes, so the blobs of
// consecutive string constants start at alternating parities, and these lengths put contents at
// both odd and even heap offsets. `Long` is 64 characters, 128 bytes, which needs a two-byte length
// prefix. `Surrogate` holds a lone high surrogate, which only a copy of the code units keeps.
//
// As in LiteralFieldValue.cs, each literal has its own holder type and is reached through
// `GetFields()`, so that nothing but the Constant-table read is exercised.
public class One { public const string Value = "a"; }
public class Two { public const string Value = "bc"; }
public class Three { public const string Value = "def"; }
public class Four { public const string Value = "ghij"; }
public class Five { public const string Value = "klmno"; }
public class Six { public const string Value = "pqrstu"; }
public class Seven { public const string Value = "vwxyz01"; }
public class Long { public const string Value = "0123456789abcdef0123456789ABCDEF0123456789abcdef0123456789ABCDEF"; }
public class Surrogate { public const string Value = "x\uD800y"; }

public class Defaults
{
    public static void Method(string first = "odd?", string second = "even?", string third = "?") { }
}

public class LiteralStringConstantOffsets
{
    private static bool Check(Type t, string expected)
    {
        FieldInfo[] fields = t.GetFields();
        if (fields.Length != 1 || !fields[0].IsLiteral)
        {
            return false;
        }

        object actual = fields[0].GetRawConstantValue();
        return actual is string s && s == expected && s.Length == expected.Length;
    }

    public static int Main(string[] argv)
    {
        if (!Check(typeof(One), "a")) return 1;
        if (!Check(typeof(Two), "bc")) return 2;
        if (!Check(typeof(Three), "def")) return 3;
        if (!Check(typeof(Four), "ghij")) return 4;
        if (!Check(typeof(Five), "klmno")) return 5;
        if (!Check(typeof(Six), "pqrstu")) return 6;
        if (!Check(typeof(Seven), "vwxyz01")) return 7;
        if (!Check(typeof(Long), "0123456789abcdef0123456789ABCDEF0123456789abcdef0123456789ABCDEF")) return 8;
        if (!Check(typeof(Surrogate), "x\uD800y")) return 9;

        ParameterInfo[] ps = typeof(Defaults).GetMethod("Method").GetParameters();
        if (ps.Length != 3) return 10;
        if (!(ps[0].DefaultValue is string first) || first != "odd?") return 11;
        if (!(ps[1].DefaultValue is string second) || second != "even?") return 12;
        if (!(ps[2].RawDefaultValue is string third) || third != "?") return 13;

        return 0;
    }
}
