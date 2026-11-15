using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

// The six pseudo-attributes that CoreCLR's emitter validates and then keeps as an ordinary row (`ClassInterface`,
// `InterfaceType`, `Guid`, `TypeLibVersion`, `ComCompatibleVersion`, `AllowPartiallyTrustedCallers`), through
// `SetCustomAttribute`. The emitter checks the owner's table, parses the blob by its own grammar (a `U2` for the two
// enum-typed constructors, whose enum's high half it then reads as the named-argument count), checks the value, and
// keeps the blob as given, or throws `ArgumentException` and writes nothing. Each module is its own: `Targets{n}`, each
// constructor with a well-formed blob on each of the ten owner kinds; `Values`, blobs on a type, and `Assembly{n}`,
// blobs on an assembly, each accepted or refused as measured, read back where real .NET can read them; `Order`; `GenParam`,
// an attribute on a generic parameter, which `CreateType` bakes and throws for; and `NSubstitute`, a proxy over `object`
// given `object`'s non-inheritable attributes as Castle DynamicProxy gives them. The test's terminal-state check reads
// the rows of `Targets{n}`, `Values`, `Assembly{n}`, `Order` and `GenParam`. Measured on .NET 10 and 11 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed; a failed read also prints what it saw.

[AttributeUsage (AttributeTargets.All, AllowMultiple = true)]
public class TagAttribute : Attribute
{
    public TagAttribute (string s) { S = s; }
    public string S;
}

public interface ICalc
{
    int Add (int a, int b);
}

public static class Program
{
    static readonly ConstructorInfo[] Constructors =
    {
        typeof (ClassInterfaceAttribute).GetConstructor (new[] { typeof (short) }),
        typeof (ClassInterfaceAttribute).GetConstructor (new[] { typeof (ClassInterfaceType) }),
        typeof (InterfaceTypeAttribute).GetConstructor (new[] { typeof (short) }),
        typeof (InterfaceTypeAttribute).GetConstructor (new[] { typeof (ComInterfaceType) }),
        typeof (GuidAttribute).GetConstructor (new[] { typeof (string) }),
        typeof (TypeLibVersionAttribute).GetConstructor (new[] { typeof (int), typeof (int) }),
        typeof (ComCompatibleVersionAttribute).GetConstructor (new[] { typeof (int), typeof (int), typeof (int), typeof (int) }),
        typeof (AllowPartiallyTrustedCallersAttribute).GetConstructor (Type.EmptyTypes),
    };

    const int CiShort = 0, CiEnum = 1, ItShort = 2, ItEnum = 3, GuidCtor = 4, Tlv = 5, Ccv = 6, Aptca = 7;

    const string GuidText = "01234567-89ab-cdef-0123-456789abcdef";

    static readonly string[] Owners = { "assembly", "module", "type", "method", "ctor", "param", "return", "field", "property", "event" };

    static byte[] B (params int[] bytes)
    {
        var result = new byte[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            result[i] = (byte) bytes[i];
        }

        return result;
    }

    static byte[] Cat (params byte[][] parts)
    {
        var result = new List<byte> ();
        foreach (byte[] part in parts)
        {
            result.AddRange (part);
        }

        return result.ToArray ();
    }

    static byte[] Ascii (string s) => Encoding.ASCII.GetBytes (s);

    /// A blob holding one string, its length written as `length` gives it, followed by `tail`.
    static byte[] Str (byte[] length, byte[] text, byte[] tail) => Cat (B (1, 0), length, text, tail);

    static byte[] Guid (string text) => Str (B (text.Length), Ascii (text), B (0, 0));

    static byte[] GuidWith (int at, byte b)
    {
        byte[] text = Ascii (GuidText);
        text[at] = b;
        return Str (B (36), text, B (0, 0));
    }

    // Each constructor's well-formed blob, as `CustomAttributeBuilder` writes it.
    static readonly byte[][] GoodBlobs =
    {
        B (1, 0, 1, 0, 0, 0),
        B (1, 0, 1, 0, 0, 0, 0, 0),
        B (1, 0, 1, 0, 0, 0),
        B (1, 0, 1, 0, 0, 0, 0, 0),
        Guid (GuidText),
        B (1, 0, 1, 0, 0, 0, 2, 0, 0, 0, 0, 0),
        B (1, 0, 1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0, 4, 0, 0, 0, 0, 0),
        B (1, 0, 0, 0),
    };

    // Whether the emitter accepts each constructor's well-formed blob on each owner, in the order of `Constructors` and
    // `Owners`.
    static readonly bool[][] Accepted =
    {
        new[] { true, false, true, false, false, false, false, false, false, false },
        new[] { true, false, true, false, false, false, false, false, false, false },
        new[] { false, false, true, false, false, false, false, false, false, false },
        new[] { false, false, true, false, false, false, false, false, false, false },
        new[] { true, true, true, false, false, false, false, false, false, false },
        new[] { true, false, false, false, false, false, false, false, false, false },
        new[] { true, false, false, false, false, false, false, false, false, false },
        new[] { true, false, false, false, false, false, false, false, false, false },
    };

    // What reflection shows of each constructor's well-formed row: its attribute's name and its value.
    static readonly string[] GoodValues =
    {
        "ClassInterfaceAttribute(1)",
        "ClassInterfaceAttribute(1)",
        "InterfaceTypeAttribute(1)",
        "InterfaceTypeAttribute(1)",
        "GuidAttribute(" + GuidText + ")",
        "TypeLibVersionAttribute(1,2)",
        "ComCompatibleVersionAttribute(1,2,3,4)",
        "AllowPartiallyTrustedCallersAttribute()",
    };

    /// A blob of the `Values` and `Assembly{n}` modules: its constructor, its blob, whether the emitter keeps it, and
    /// what `GetCustomAttributesData` and `GetCustomAttributes` show of the owner holding it alone: a description, or
    /// "CustomAttributeFormatException", or null where the read is not made. `GetCustomAttributes` is not made where
    /// real .NET throws from a reader path at which PawPrint stops instead; the parked
    /// `TypeBuilderValidatedPseudoAttributeUnreadable.cs` makes those reads.
    sealed class Row
    {
        public int Constructor;
        public byte[] Blob;
        public bool Kept;
        public string Data;
        public string Instances;

        public Row (int constructor, byte[] blob, bool kept, string data = null, string instances = null)
        {
            Constructor = constructor;
            Blob = blob;
            Kept = kept;
            Data = data;
            Instances = instances;
        }
    }

    const string Cafe = "CustomAttributeFormatException";

    // Blobs on a type, each on its own type `V{index}` of `Values`.
    static readonly Row[] TypeRows =
    {
        new Row (CiEnum, B (1, 0, 0, 0, 0, 0, 0, 0), true, "ClassInterfaceAttribute(0)", "ClassInterfaceAttribute(0)"),
        new Row (CiEnum, B (1, 0, 2, 0, 0, 0, 0, 0), true, "ClassInterfaceAttribute(2)", "ClassInterfaceAttribute(2)"),
        new Row (CiEnum, B (1, 0, 3, 0, 0, 0, 0, 0), false),
        // The enum's high half, 1, is read as a named-argument count, which no named argument follows.
        new Row (CiEnum, B (1, 0, 1, 0, 1, 0, 0, 0), false),
        new Row (CiEnum, B (1, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0), false),
        new Row (CiEnum, new byte[0], false),
        new Row (CiEnum, B (0xFF, 0xFF, 1, 0, 0, 0, 0, 0), false),
        new Row (CiEnum, B (1, 0), false),
        new Row (CiEnum, B (1, 0, 1), false),
        // The enum's high half is a count of 0, so nothing after it is read: kept, though reflection cannot read it.
        new Row (CiEnum, B (1, 0, 1, 0, 0, 0), true, Cafe, "ClassInterfaceAttribute(1)"),
        new Row (CiEnum, B (1, 0, 1, 0, 0, 0, 1, 0, 0x54, 0x08, 3, 0x46, 0x6F, 0x6F, 1, 0, 0, 0), true, Cafe, Cafe),
        new Row (CiShort, B (1, 0, 3, 0, 0, 0), false),
        new Row (CiShort, B (1, 0, 0xFF, 0x7F, 0, 0), false),
        new Row (ItEnum, B (1, 0, 0, 0, 0, 0, 0, 0), true, "InterfaceTypeAttribute(0)", "InterfaceTypeAttribute(0)"),
        new Row (ItEnum, B (1, 0, 3, 0, 0, 0, 0, 0), true, "InterfaceTypeAttribute(3)", "InterfaceTypeAttribute(3)"),
        new Row (ItEnum, B (1, 0, 4, 0, 0, 0, 0, 0), false),
        new Row (ItEnum, B (1, 0, 2, 0, 1, 0, 0, 0), false),
        new Row (ItShort, B (1, 0, 4, 0, 0, 0), false),
        new Row (GuidCtor, Guid ("{" + GuidText + "}"), false),
        new Row (GuidCtor, Guid ("0123"), false),
        new Row (GuidCtor, Guid ("0123456z-89ab-cdef-0123-456789abcdef"), false),
        new Row (GuidCtor, Guid ("0123456789abcdef0123456789abcdef0000"), false),
        new Row (GuidCtor, B (1, 0, 0xFF, 0, 0), false),
        new Row (GuidCtor, new byte[0], false),
        // The decisions' measurements: the named-argument count is signed, read only when two bytes are left, and a
        // count of 0 or below is accepted with whatever follows it.
        new Row (CiEnum, B (1, 0, 1, 0, 0xFF, 0xFF), true, Cafe, "ClassInterfaceAttribute(-65535)"),
        new Row (CiEnum, B (1, 0, 1, 0, 0, 0x80), true, Cafe, "ClassInterfaceAttribute(-2147483647)"),
        new Row (CiEnum, B (1, 0, 1, 0, 0xFF, 0xFF, 0xDE, 0xAD), true, "ClassInterfaceAttribute(-65535)", null),
        new Row (CiEnum, B (1, 0, 1, 0, 0xFF, 0x7F), false),
        new Row (CiEnum, B (1, 0, 1, 0, 0), true, Cafe, null),
        new Row (CiEnum, B (1, 0, 1, 0, 0xFF), true, Cafe, null),
        new Row (CiEnum, B (1, 0, 1, 0, 1, 0, 0x54, 0x08, 3, 0x46, 0x6F, 0x6F, 1, 0, 0, 0), false),
        new Row (CiEnum, B (1, 0, 1, 0, 1, 0, 0x53, 0x08, 3, 0x46, 0x6F, 0x6F, 1, 0, 0, 0), false),
        new Row (CiEnum, B (1, 0, 1, 0, 1, 0), false),
        new Row (CiEnum, B (1, 0, 1, 0, 0, 0, 0xDE, 0xAD, 0xBE, 0xEF), true, "ClassInterfaceAttribute(1)", null),
        new Row (CiEnum, B (1, 1, 1, 0, 0, 0), false),
        new Row (CiEnum, B (2, 0, 1, 0, 0, 0), false),
        new Row (CiShort, B (1, 0, 1, 0), true, Cafe, "ClassInterfaceAttribute(1)"),
        new Row (CiShort, B (1, 0, 0, 0x80, 0, 0), false),
        new Row (CiShort, B (1, 0, 0xFF, 0xFF, 0, 0), false),
        new Row (GuidCtor, Str (B (36), Ascii (GuidText), new byte[0]), true, Cafe, "GuidAttribute(" + GuidText + ")"),
        new Row (GuidCtor, Guid (GuidText.ToUpperInvariant ()), true, "GuidAttribute(" + GuidText.ToUpperInvariant () + ")", "GuidAttribute(" + GuidText.ToUpperInvariant () + ")"),
        new Row (GuidCtor, Guid ("01234567-89AB-cdef-0123-456789ABcdef"), true, "GuidAttribute(01234567-89AB-cdef-0123-456789ABcdef)", "GuidAttribute(01234567-89AB-cdef-0123-456789ABcdef)"),
        new Row (GuidCtor, Str (B (0x80, 36), Ascii (GuidText), B (0, 0)), true, "GuidAttribute(" + GuidText + ")", "GuidAttribute(" + GuidText + ")"),
        new Row (GuidCtor, Str (B (0xC0, 0, 0, 36), Ascii (GuidText), B (0, 0)), true, "GuidAttribute(" + GuidText + ")", "GuidAttribute(" + GuidText + ")"),
        new Row (GuidCtor, Str (B (36), Ascii (GuidText), B (0xFF, 0xFF)), true, "GuidAttribute(" + GuidText + ")", null),
        new Row (GuidCtor, Str (B (36), Ascii (GuidText), B (1, 0)), false),
        new Row (GuidCtor, Str (B (36), Ascii (GuidText), B (7)), true, Cafe, null),
        new Row (GuidCtor, GuidWith (5, 0), false),
        new Row (GuidCtor, GuidWith (5, 0xC3), false),
        new Row (GuidCtor, Str (B (37), Ascii (GuidText), new byte[0]), false),
        new Row (GuidCtor, Str (B (35), Ascii (GuidText.Substring (0, 35)), B (0, 0)), false),
        new Row (GuidCtor, B (1, 0, 0, 0, 0), false),
        new Row (GuidCtor, Guid ("012345678-9ab-cdef-0123-456789abcdef"), false),
        new Row (GuidCtor, Guid ("+1234567-89ab-cdef-0123-456789abcdef"), false),
        new Row (GuidCtor, Guid (" 1234567-89ab-cdef-0123-456789abcdef"), false),
    };

    // Blobs on an assembly, each on its own assembly `Assembly{index}`.
    static readonly Row[] AssemblyRows =
    {
        new Row (CiEnum, B (1, 0, 2, 0, 0, 0, 0, 0), true, "ClassInterfaceAttribute(2)", "ClassInterfaceAttribute(2)"),
        new Row (CiEnum, B (1, 0, 3, 0, 0, 0, 0, 0), false),
        new Row (Tlv, B (1, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0), false),
        new Row (Tlv, B (1, 0, 1, 0, 0, 0), false),
        new Row (Ccv, B (1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0), false),
        new Row (Aptca, B (0xFF, 0xFF), true, Cafe, null),
        new Row (Aptca, new byte[0], true, null, null),
        new Row (Tlv, B (1, 0, 1, 0, 0, 0, 2, 0, 0, 0), true, Cafe, "TypeLibVersionAttribute(1,2)"),
        new Row (Tlv, B (1, 0, 1, 0, 0, 0, 2, 0, 0, 0, 0xFF, 0xFF), true, "TypeLibVersionAttribute(1,2)", null),
        new Row (Tlv, B (1, 0, 1, 0, 0, 0, 2, 0, 0, 0, 1, 0), false),
        new Row (Tlv, B (1, 0, 0, 0, 0, 0, 0, 0, 0, 0), true, Cafe, "TypeLibVersionAttribute(0,0)"),
        new Row (Tlv, B (1, 0, 0, 0, 0, 0, 0, 0, 0, 0x80), false),
        new Row (Tlv, B (1, 0, 1, 0, 0, 0, 2, 0, 0), false),
    };

    static (AssemblyBuilder, ModuleBuilder) NewModule (string name)
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName (name), AssemblyBuilderAccess.Run);
        return (assembly, assembly.DefineDynamicModule (name));
    }

    static CustomAttributeBuilder Tag (string s) =>
        new CustomAttributeBuilder (typeof (TagAttribute).GetConstructor (new[] { typeof (string) }), new object[] { s });

    /// Whether `action` throws exactly what `TypeBuilder_DefineCustomAttribute` throws for a refused attribute.
    static bool ThrowsInvalidAttribute (Action action)
    {
        try
        {
            action ();
            return false;
        }
        catch (Exception e)
        {
            return e.GetType () == typeof (ArgumentException)
                && e.Message == "Invalid custom attribute provided."
                && ((ArgumentException) e).ParamName == null
                && e.HResult == unchecked ((int) 0x80070057);
        }
    }

    static bool Accepts (Action action)
    {
        try
        {
            action ();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static string Describe (object attribute)
    {
        switch (attribute)
        {
            case ClassInterfaceAttribute a: return "ClassInterfaceAttribute(" + (int) a.Value + ")";
            case InterfaceTypeAttribute a: return "InterfaceTypeAttribute(" + (int) a.Value + ")";
            case GuidAttribute a: return "GuidAttribute(" + a.Value + ")";
            case TypeLibVersionAttribute a: return "TypeLibVersionAttribute(" + a.MajorVersion + "," + a.MinorVersion + ")";
            case ComCompatibleVersionAttribute a:
                return "ComCompatibleVersionAttribute(" + a.MajorVersion + "," + a.MinorVersion + "," + a.BuildNumber + "," + a.RevisionNumber + ")";
            case TagAttribute a: return "Tag(" + a.S + ")";
            default: return attribute.GetType ().Name + "()";
        }
    }

    static string Describe (CustomAttributeData data)
    {
        if (data.AttributeType == typeof (TagAttribute))
        {
            return "Tag(" + data.ConstructorArguments[0].Value + ")";
        }

        var arguments = new List<string> ();
        foreach (CustomAttributeTypedArgument argument in data.ConstructorArguments)
        {
            arguments.Add (Convert.ToString (argument.Value));
        }

        return data.AttributeType.Name + "(" + string.Join (",", arguments) + ")";
    }

    /// What `GetCustomAttributesData` shows: each row described, or the name of the exception it throws.
    static string ReadData (Func<IList<CustomAttributeData>> read)
    {
        try
        {
            var described = new List<string> ();
            foreach (CustomAttributeData data in read ())
            {
                described.Add (Describe (data));
            }

            return string.Join (" ", described);
        }
        catch (Exception e)
        {
            return e.GetType ().Name;
        }
    }

    /// What `GetCustomAttributes` shows: each attribute described, or the name of the exception it throws.
    static string ReadInstances (Func<object[]> read)
    {
        try
        {
            var described = new List<string> ();
            foreach (object attribute in read ())
            {
                described.Add (Describe (attribute));
            }

            return string.Join (" ", described);
        }
        catch (Exception e)
        {
            return e.GetType ().Name;
        }
    }

    static bool Same (string expected, string actual, string what)
    {
        if (expected == actual)
        {
            return true;
        }

        Console.WriteLine (what + ": expected <" + expected + ">, got <" + actual + ">");
        return false;
    }

    sealed class Members
    {
        public TypeBuilder T;
        public MethodBuilder M;
        public ParameterBuilder Return;
        public ParameterBuilder X;
        public ConstructorBuilder C;
        public FieldBuilder F;
        public PropertyBuilder P;
        public EventBuilder E;
    }

    /// A type with a method `int M(int x)`, a default constructor, a field, a property with a getter and an event
    /// with two accessors.
    static Members Define (ModuleBuilder module, string name)
    {
        var o = new Members ();
        o.T = module.DefineType (name, TypeAttributes.Public);
        o.M = o.T.DefineMethod ("M", MethodAttributes.Public | MethodAttributes.Static, typeof (int), new[] { typeof (int) });
        o.Return = o.M.DefineParameter (0, ParameterAttributes.None, "ret");
        o.X = o.M.DefineParameter (1, ParameterAttributes.None, "x");
        ILGenerator il = o.M.GetILGenerator ();
        il.Emit (OpCodes.Ldarg_0);
        il.Emit (OpCodes.Ret);
        o.C = o.T.DefineDefaultConstructor (MethodAttributes.Public);
        o.F = o.T.DefineField ("F", typeof (int), FieldAttributes.Public);
        MethodBuilder getter =
            o.T.DefineMethod ("get_P", MethodAttributes.Public | MethodAttributes.SpecialName, typeof (int), Type.EmptyTypes);
        il = getter.GetILGenerator ();
        il.Emit (OpCodes.Ldc_I4_7);
        il.Emit (OpCodes.Ret);
        o.P = o.T.DefineProperty ("P", PropertyAttributes.None, typeof (int), Type.EmptyTypes);
        o.P.SetGetMethod (getter);
        MethodBuilder adder =
            o.T.DefineMethod ("add_E", MethodAttributes.Public | MethodAttributes.SpecialName, typeof (void), new[] { typeof (EventHandler) });
        adder.GetILGenerator ().Emit (OpCodes.Ret);
        MethodBuilder remover =
            o.T.DefineMethod ("remove_E", MethodAttributes.Public | MethodAttributes.SpecialName, typeof (void), new[] { typeof (EventHandler) });
        remover.GetILGenerator ().Emit (OpCodes.Ret);
        o.E = o.T.DefineEvent ("E", EventAttributes.None, typeof (EventHandler));
        o.E.SetAddOnMethod (adder);
        o.E.SetRemoveOnMethod (remover);
        return o;
    }

    static void Apply (AssemblyBuilder assembly, ModuleBuilder module, Members o, string owner, ConstructorInfo ctor, byte[] blob)
    {
        switch (owner)
        {
            case "assembly": assembly.SetCustomAttribute (ctor, blob); break;
            case "module": module.SetCustomAttribute (ctor, blob); break;
            case "type": o.T.SetCustomAttribute (ctor, blob); break;
            case "method": o.M.SetCustomAttribute (ctor, blob); break;
            case "ctor": o.C.SetCustomAttribute (ctor, blob); break;
            case "param": o.X.SetCustomAttribute (ctor, blob); break;
            case "return": o.Return.SetCustomAttribute (ctor, blob); break;
            case "field": o.F.SetCustomAttribute (ctor, blob); break;
            case "property": o.P.SetCustomAttribute (ctor, blob); break;
            case "event": o.E.SetCustomAttribute (ctor, blob); break;
            default: throw new Exception (owner);
        }
    }

    // Checks 1 to 120.
    static int Targets ()
    {
        for (int c = 0; c < Constructors.Length; c++)
        {
            var (assembly, module) = NewModule ("Targets" + c);
            Members o = Define (module, "T");
            for (int w = 0; w < Owners.Length; w++)
            {
                string owner = Owners[w];
                ConstructorInfo ctor = Constructors[c];
                byte[] blob = GoodBlobs[c];
                bool ok = Accepted[c][w]
                    ? Accepts (() => Apply (assembly, module, o, owner, ctor, blob))
                    : ThrowsInvalidAttribute (() => Apply (assembly, module, o, owner, ctor, blob));
                if (!ok)
                {
                    return 1 + 10 * c + w;
                }
            }

            // The token this row gets says how many rows the module holds.
            o.T.SetCustomAttribute (Tag ("after"));
            Type created = o.T.CreateType ();

            string onType = Accepted[c][2] ? GoodValues[c] + " Tag(after)" : "Tag(after)";
            if (!Same (onType, ReadData (() => created.GetCustomAttributesData ()), "Targets" + c + " type data")) return 81 + c;
            if (!Same (onType, ReadInstances (() => created.GetCustomAttributes (false)), "Targets" + c + " type instances")) return 89 + c;
            string onAssembly = Accepted[c][0] ? GoodValues[c] : "";
            if (!Same (onAssembly, ReadData (() => assembly.GetCustomAttributesData ()), "Targets" + c + " assembly data")) return 97 + c;
            if (!Same (onAssembly, ReadInstances (() => assembly.GetCustomAttributes (false)), "Targets" + c + " assembly instances")) return 105 + c;
            string onModule = Accepted[c][1] ? GoodValues[c] : "";
            if (!Same (onModule, ReadData (() => module.GetCustomAttributesData ()), "Targets" + c + " module data")) return 113 + c;
        }

        return 0;
    }

    static string Expected (Row row, bool data) => row.Kept ? (data ? row.Data : row.Instances) : "";

    // Checks 121 to 179: 121 plus the row's index, for its acceptance or its reads.
    static int Values ()
    {
        var (_, module) = NewModule ("Values");
        for (int i = 0; i < TypeRows.Length; i++)
        {
            Row row = TypeRows[i];
            TypeBuilder t = module.DefineType ("V" + i, TypeAttributes.Public);
            bool ok = row.Kept
                ? Accepts (() => t.SetCustomAttribute (Constructors[row.Constructor], row.Blob))
                : ThrowsInvalidAttribute (() => t.SetCustomAttribute (Constructors[row.Constructor], row.Blob));
            if (!ok)
            {
                return 121 + i;
            }

            Type created = t.CreateType ();
            string data = Expected (row, true);
            if (data != null && !Same (data, ReadData (() => created.GetCustomAttributesData ()), "V" + i + " data")) return 121 + i;
            string instances = Expected (row, false);
            if (instances != null && !Same (instances, ReadInstances (() => created.GetCustomAttributes (false)), "V" + i + " instances"))
            {
                return 121 + i;
            }
        }

        return 0;
    }

    // Checks 180 to 199: 180 plus the row's index, for its acceptance or its reads.
    static int Assemblies ()
    {
        for (int i = 0; i < AssemblyRows.Length; i++)
        {
            Row row = AssemblyRows[i];
            var (assembly, _) = NewModule ("Assembly" + i);
            bool ok = row.Kept
                ? Accepts (() => assembly.SetCustomAttribute (Constructors[row.Constructor], row.Blob))
                : ThrowsInvalidAttribute (() => assembly.SetCustomAttribute (Constructors[row.Constructor], row.Blob));
            if (!ok)
            {
                return 180 + i;
            }

            string data = Expected (row, true);
            if (data != null && !Same (data, ReadData (() => assembly.GetCustomAttributesData ()), "Assembly" + i + " data")) return 180 + i;
            string instances = Expected (row, false);
            if (instances != null && !Same (instances, ReadInstances (() => assembly.GetCustomAttributes (false)), "Assembly" + i + " instances"))
            {
                return 180 + i;
            }
        }

        return 0;
    }

    // Checks 200 to 202.
    static int Order ()
    {
        var (_, module) = NewModule ("Order");
        Members o = Define (module, "T");
        o.T.SetCustomAttribute (Tag ("a"));
        o.T.SetCustomAttribute (Constructors[CiEnum], GoodBlobs[CiEnum]);
        o.T.SetCustomAttribute (Tag ("b"));
        if (!ThrowsInvalidAttribute (() => o.T.SetCustomAttribute (Constructors[CiEnum], B (1, 0, 3, 0, 0, 0, 0, 0)))) return 200;
        o.T.SetCustomAttribute (Tag ("c"));
        if (!ThrowsInvalidAttribute (() => o.M.SetCustomAttribute (Constructors[GuidCtor], GoodBlobs[GuidCtor]))) return 201;
        o.T.SetCustomAttribute (Constructors[CiEnum], GoodBlobs[CiEnum]);
        o.T.SetCustomAttribute (Tag ("d"));
        Type created = o.T.CreateType ();
        string expected = "Tag(a) ClassInterfaceAttribute(1) Tag(b) Tag(c) ClassInterfaceAttribute(1) Tag(d)";
        if (!Same (expected, ReadData (() => created.GetCustomAttributesData ()), "Order")) return 202;
        return 0;
    }

    // Checks 210 to 215.
    static int GenParam ()
    {
        var (_, module) = NewModule ("GenParam");
        TypeBuilder g = module.DefineType ("G", TypeAttributes.Public);
        GenericTypeParameterBuilder gp = g.DefineGenericParameters ("TT")[0];
        // Queued until `CreateType` bakes the parameter, which throws at the first.
        if (!Accepts (() => gp.SetCustomAttribute (Constructors[CiEnum], GoodBlobs[CiEnum]))) return 210;
        gp.SetCustomAttribute (Tag ("after"));
        if (!ThrowsInvalidAttribute (() => g.CreateType ())) return 211;
        if (g.IsCreated ()) return 212;

        TypeBuilder h = module.DefineType ("H", TypeAttributes.Public);
        MethodBuilder hm = h.DefineMethod ("M", MethodAttributes.Public | MethodAttributes.Static, typeof (void), Type.EmptyTypes);
        GenericTypeParameterBuilder mp = hm.DefineGenericParameters ("U")[0];
        hm.GetILGenerator ().Emit (OpCodes.Ret);
        if (!Accepts (() => mp.SetCustomAttribute (Constructors[GuidCtor], GoodBlobs[GuidCtor]))) return 213;
        if (!ThrowsInvalidAttribute (() => h.CreateType ())) return 214;

        // The module still creates another type.
        TypeBuilder k = module.DefineType ("K", TypeAttributes.Public);
        k.DefineDefaultConstructor (MethodAttributes.Public);
        if (Activator.CreateInstance (k.CreateType ()).GetType ().Name != "K") return 215;
        return 0;
    }

    /// Castle's `AttributeUtil.ShouldSkipAttributeReplication` for a type.
    static bool ShouldSkipAttributeReplication (Type attribute)
    {
        if (!attribute.IsPublic && !attribute.IsNestedPublic) return true;
        if (attribute == typeof (ComImportAttribute) || attribute == typeof (MarshalAsAttribute) || attribute == typeof (TypeIdentifierAttribute))
        {
            return true;
        }

        var usages = new List<AttributeUsageAttribute> (CustomAttributeExtensions.GetCustomAttributes<AttributeUsageAttribute> (attribute, true));
        return usages.Count == 0 || usages[0].Inherited;
    }

    /// Castle's `AttributeUtil.CreateInfo`, as a builder.
    static CustomAttributeBuilder Replicate (CustomAttributeData data)
    {
        var arguments = new object[data.ConstructorArguments.Count];
        for (int i = 0; i < arguments.Length; i++)
        {
            arguments[i] = data.ConstructorArguments[i].Value;
        }

        var properties = new List<PropertyInfo> ();
        var propertyValues = new List<object> ();
        var fields = new List<FieldInfo> ();
        var fieldValues = new List<object> ();
        foreach (CustomAttributeNamedArgument named in data.NamedArguments)
        {
            if (named.IsField)
            {
                fields.Add ((FieldInfo) named.MemberInfo);
                fieldValues.Add (named.TypedValue.Value);
            }
            else
            {
                properties.Add ((PropertyInfo) named.MemberInfo);
                propertyValues.Add (named.TypedValue.Value);
            }
        }

        return new CustomAttributeBuilder (
            data.Constructor, arguments, properties.ToArray (), propertyValues.ToArray (), fields.ToArray (), fieldValues.ToArray ());
    }

    // Checks 220 to 225. NSubstitute's `Substitute.For<ICalc>()` asks Castle for a class proxy of `object`
    // implementing `ICalc`; Castle's `ClassEmitter` defines the type `Public | Serializable` and copies `object`'s
    // non-inheritable attributes onto it, `ClassInterface(AutoDispatch)` among them.
    static int NSubstitute ()
    {
        var (_, module) = NewModule ("NSubstitute");
        TypeBuilder t = module.DefineType (
            "ObjectProxy", TypeAttributes.Public | TypeAttributes.Serializable, typeof (object), new[] { typeof (ICalc) });
        t.DefineDefaultConstructor (MethodAttributes.Public);
        var replicated = new List<string> ();
        foreach (CustomAttributeData data in typeof (object).GetCustomAttributesData ())
        {
            if (!ShouldSkipAttributeReplication (data.AttributeType))
            {
                t.SetCustomAttribute (Replicate (data));
                replicated.Add (data.AttributeType.Name);
            }
        }

        if (!Same ("SerializableAttribute NullableContextAttribute ClassInterfaceAttribute ComVisibleAttribute TypeForwardedFromAttribute", string.Join (" ", replicated), "replicated"))
        {
            return 220;
        }

        MethodInfo add = typeof (ICalc).GetMethod ("Add");
        MethodBuilder m = t.DefineMethod (
            "Add",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
            typeof (int),
            new[] { typeof (int), typeof (int) });
        ILGenerator il = m.GetILGenerator ();
        il.Emit (OpCodes.Ldarg_1);
        il.Emit (OpCodes.Ldarg_2);
        il.Emit (OpCodes.Add);
        il.Emit (OpCodes.Ret);
        t.DefineMethodOverride (m, add);

        Type created = t.CreateType ();
        if ((int) created.Attributes != 0x2001) return 221;
        if (((ICalc) Activator.CreateInstance (created)).Add (2, 3) != 5) return 222;
        if (!Same (ReadData (() => typeof (object).GetCustomAttributesData ()), ReadData (() => created.GetCustomAttributesData ()), "proxy data"))
        {
            return 223;
        }

        var classInterface = (ClassInterfaceAttribute) Attribute.GetCustomAttribute (created, typeof (ClassInterfaceAttribute), false);
        if (classInterface == null || classInterface.Value != ClassInterfaceType.AutoDispatch) return 224;
        if (!created.IsSerializable) return 225;
        return 0;
    }

    public static int Main ()
    {
        int result = Targets ();
        if (result != 0) return result;
        result = Values ();
        if (result != 0) return result;
        result = Assemblies ();
        if (result != 0) return result;
        result = Order ();
        if (result != 0) return result;
        result = GenParam ();
        if (result != 0) return result;
        return NSubstitute ();
    }
}
