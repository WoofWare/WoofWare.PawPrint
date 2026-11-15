using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;

// `TypeBuilder_SetConstantValue`, the QCall behind `ParameterBuilder.SetConstant`, `FieldBuilder.SetConstant`,
// `PropertyBuilder.SetConstant` and `EnumBuilder.DefineLiteral`: it writes the owner's Constant row (one per owner,
// replaced by a later call, its blob left alone when the new value's blob is empty) and ORs the owner's HasDefault flag.
// Each module is its own:
// * `Values`: one type `T`; for each value case i, in this order, parameter 1 of `M{i}`, its return parameter, the
//   static literal field `F{i}`, the instance field `G{i}` and the accessor-less property `P{i}`, each defined just before
//   its `SetConstant`; read back through reflection after `CreateType`.
// * `Second`: for each sequence k, a type `S{k}` whose parameter `p` of `M(object)`, literal field `F` and property `P`,
//   all typed `object`, each get the sequence's two values in turn (the parameter's two calls, then the field's two, then
//   the property's two); and a type `Alternate` whose two parameters are set alternately. Every type is defined and every
//   call made before the first `CreateType`.
// * `Conversions`: the managed caller's conversions (an enum, `Nullable<>`, `DateTime`, null) and its refusals, each
//   refusal leaving its owner without a default.
// * `Enums`: `EnumBuilder` over `int`, `byte`, `long` and `ulong`, and a `TypeBuilder` enum given a literal before its
//   `value__` field exists.
// * `Order`: constants set for owners out of the order of their tokens.
// * `Accessors`: a property given its constant before its getter and setter; a property with only an Other method.
// * `Created`: `SetConstant` on a field and a property, and `DefineLiteral`, after `CreateType`.
// * `Castle`: Castle DynamicProxy 5.1.1's copy of an interface method's eight default values onto a proxy's method
//   (`MethodEmitter.DefineParameters` and `CopyDefaultValueConstant`, as their IL reads).
// * `Long`: a 32767-character string constant.
// The test's terminal-state check reads the rows of `Values` and `Second`. Measured on .NET 10 and 11 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public enum Colour { Red, Green, Blue }

public interface IDefaults
{
    int Eight (int a = 3, long b = 4L, string c = "five", bool d = true, double e = 1.5, char f = 'q', object g = null, Colour h = Colour.Blue);
}

public static class Program
{
    const BindingFlags AnyProperty = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    const string DoesNotMatch = "Constant does not match the defined type.";
    const string NullNotValid = "Null is not a valid constant value for this type.";
    const string AfterCreate = "Unable to change after type has been created.";

    /// A dynamic assembly named `name`, and its module of the same name.
    static (AssemblyBuilder, ModuleBuilder) NewModule (string name)
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName (name), AssemblyBuilderAccess.Run);
        return (assembly, assembly.DefineDynamicModule (name));
    }

    static int Fail (int check, string saw)
    {
        Console.WriteLine ("check " + check + ": " + saw);
        return check;
    }

    static string Show (object v)
    {
        if (v == null) return "null";
        if (v == DBNull.Value) return "DBNull";
        if (v == Missing.Value) return "Missing";
        if (v is string s)
        {
            var parts = new List<string> ();
            foreach (char c in s)
            {
                parts.Add (c >= 0x20 && c < 0x7f ? c.ToString () : "\\u" + ((int) c).ToString ("x4"));
            }

            return "string \"" + string.Join ("", parts) + "\" (length " + s.Length + ")";
        }

        if (v is float f) return "Single 0x" + BitConverter.SingleToInt32Bits (f).ToString ("x8");
        if (v is double d) return "Double 0x" + BitConverter.DoubleToInt64Bits (d).ToString ("x16");
        if (v is char ch) return "Char \\u" + ((int) ch).ToString ("x4");
        if (v is DateTime dt) return "DateTime ticks " + dt.Ticks + " kind " + dt.Kind;
        return v.GetType ().Name + " " + Convert.ToString (v, CultureInfo.InvariantCulture);
    }

    /// Equality by type and value, with floating-point values compared by their bits and `DateTime` by ticks and kind.
    static bool Same (object expected, object actual)
    {
        if (expected == null || actual == null) return expected == null && actual == null;
        if (expected.GetType () != actual.GetType ()) return false;
        if (expected is float f) return BitConverter.SingleToInt32Bits (f) == BitConverter.SingleToInt32Bits ((float) actual);
        if (expected is double d) return BitConverter.DoubleToInt64Bits (d) == BitConverter.DoubleToInt64Bits ((double) actual);
        if (expected is DateTime dt) return dt.Ticks == ((DateTime) actual).Ticks && dt.Kind == ((DateTime) actual).Kind;
        return expected.Equals (actual);
    }

    static string ShowException (Exception e)
    {
        if (e == null) return "no exception";
        string param = e is ArgumentException ae ? " ParamName " + (ae.ParamName ?? "null") : "";
        return e.GetType ().FullName + " \"" + e.Message + "\"" + param;
    }

    static Exception Catch (Action action)
    {
        try
        {
            action ();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    static bool IsArgumentException (Exception e, string message) =>
        e != null && e.GetType () == typeof (ArgumentException) && e.Message == message && ((ArgumentException) e).ParamName == null;

    static bool IsInvalidOperation (Exception e, string message) =>
        e != null && e.GetType () == typeof (InvalidOperationException) && e.Message == message;

    /// null if `p` has the flags `attributes` and reads `expectedDefault` through `DefaultValue` and `expectedRaw` through
    /// `RawDefaultValue`, with `HasDefaultValue` true; otherwise what it read.
    static string ReadParameter (ParameterInfo p, int attributes, object expectedDefault, object expectedRaw)
    {
        object d = p.DefaultValue;
        object r = p.RawDefaultValue;
        bool has = p.HasDefaultValue;
        if ((int) p.Attributes == attributes && has && Same (expectedDefault, d) && Same (expectedRaw, r)) return null;
        return "parameter " + p.Position + ": attributes 0x" + ((int) p.Attributes).ToString ("x4") + " HasDefaultValue " + has
            + " DefaultValue " + Show (d) + " RawDefaultValue " + Show (r) + "; expected 0x" + attributes.ToString ("x4") + ", "
            + Show (expectedDefault) + ", " + Show (expectedRaw);
    }

    /// null if `p` has no default: flags 0 and `HasDefaultValue` false; otherwise what it read.
    static string ReadNoDefault (ParameterInfo p)
    {
        if ((int) p.Attributes == 0 && !p.HasDefaultValue) return null;
        return "parameter " + p.Position + ": attributes 0x" + ((int) p.Attributes).ToString ("x4") + " HasDefaultValue " + p.HasDefaultValue
            + " DefaultValue " + Show (p.DefaultValue);
    }

    static string ReadLiteral (FieldInfo f, object expected)
    {
        if (f == null) return "no field";
        object v = f.GetRawConstantValue ();
        if ((int) f.Attributes == 0x8056 && Same (expected, v)) return null;
        return "field " + f.Name + ": attributes 0x" + ((int) f.Attributes).ToString ("x4") + " GetRawConstantValue " + Show (v)
            + "; expected 0x8056, " + Show (expected);
    }

    static string ReadInstanceField (FieldInfo f)
    {
        if (f == null) return "no field";
        Exception e = Catch (() => f.GetRawConstantValue ());
        if ((int) f.Attributes == 0x8006 && IsInvalidOperation (e, "Operation is not valid due to the current state of the object.")) return null;
        return "field " + f.Name + ": attributes 0x" + ((int) f.Attributes).ToString ("x4") + " GetRawConstantValue threw " + ShowException (e)
            + "; expected 0x8006 and InvalidOperationException";
    }

    static string ReadProperty (PropertyInfo p, object expected)
    {
        if (p == null) return "no property";
        object v = p.GetConstantValue ();
        if ((int) p.Attributes == 0x1000 && Same (expected, v)) return null;
        return "property " + p.Name + ": attributes 0x" + ((int) p.Attributes).ToString ("x4") + " GetConstantValue " + Show (v)
            + "; expected 0x1000, " + Show (expected);
    }

    sealed class ValueCase
    {
        public readonly Type Declared;
        public readonly object Value;
        public readonly object Expected;

        public ValueCase (Type declared, object value, object expected)
        {
            Declared = declared;
            Value = value;
            Expected = expected;
        }

        public ValueCase (Type declared, object value) : this (declared, value, value) { }
    }

    // The fourteen element types the QCall can see (twelve primitives, a string, and null), then strings with a NUL,
    // empty, and with a lone surrogate, a double NaN with a payload, U+FFFF, and a float quiet NaN with a payload.
    static readonly ValueCase[] ValueCases =
    {
        new ValueCase (typeof (bool), true),
        new ValueCase (typeof (char), 'x'),
        new ValueCase (typeof (sbyte), (sbyte) -5),
        new ValueCase (typeof (byte), (byte) 200),
        new ValueCase (typeof (short), (short) -300),
        new ValueCase (typeof (ushort), (ushort) 60000),
        new ValueCase (typeof (int), 5),
        new ValueCase (typeof (uint), 0xFFFFFFFEu),
        new ValueCase (typeof (long), 0x0123456789ABCDEFL),
        new ValueCase (typeof (ulong), ulong.MaxValue),
        new ValueCase (typeof (float), 1.5f),
        new ValueCase (typeof (double), -0.0),
        new ValueCase (typeof (string), "abc"),
        new ValueCase (typeof (string), null),
        // The QCall's string ends at its first NUL.
        new ValueCase (typeof (string), "a\0b", "a"),
        new ValueCase (typeof (string), ""),
        new ValueCase (typeof (string), "\uD800x"),
        new ValueCase (typeof (double), BitConverter.Int64BitsToDouble (0x7ff4000000000001)),
        new ValueCase (typeof (char), '￿'),
        new ValueCase (typeof (float), BitConverter.Int32BitsToSingle (0x7fc00001)),
    };

    // Checks 1 to 100: check 1 + 5 * i + k for value case i, where k is 0 for parameter 1 of `M{i}`, 1 for its return
    // parameter, 2 for the literal field `F{i}`, 3 for the instance field `G{i}` and 4 for the property `P{i}`.
    static int Values ()
    {
        var (_, module) = NewModule ("Values");
        TypeBuilder t = module.DefineType ("T", TypeAttributes.Public | TypeAttributes.Abstract);
        for (int i = 0; i < ValueCases.Length; i++)
        {
            ValueCase c = ValueCases[i];
            MethodBuilder m = t.DefineMethod ("M" + i, MethodAttributes.Public | MethodAttributes.Static, c.Declared, new[] { c.Declared });
            ILGenerator il = m.GetILGenerator ();
            il.Emit (OpCodes.Ldnull);
            il.Emit (OpCodes.Throw);
            m.DefineParameter (1, ParameterAttributes.None, "p").SetConstant (c.Value);
            m.DefineParameter (0, ParameterAttributes.None, null).SetConstant (c.Value);
            t.DefineField ("F" + i, c.Declared, FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal).SetConstant (c.Value);
            t.DefineField ("G" + i, c.Declared, FieldAttributes.Public).SetConstant (c.Value);
            t.DefineProperty ("P" + i, PropertyAttributes.None, c.Declared, Type.EmptyTypes).SetConstant (c.Value);
        }

        Type created = t.CreateType ();
        for (int i = 0; i < ValueCases.Length; i++)
        {
            ValueCase c = ValueCases[i];
            int check = 1 + 5 * i;
            MethodInfo m = created.GetMethod ("M" + i);
            string saw = ReadParameter (m.GetParameters ()[0], 0x1000, c.Expected, c.Expected);
            if (saw != null) return Fail (check, saw);
            saw = ReadParameter (m.ReturnParameter, 0x1000, c.Expected, c.Expected);
            if (saw != null) return Fail (check + 1, saw);
            saw = ReadLiteral (created.GetField ("F" + i), c.Expected);
            if (saw != null) return Fail (check + 2, saw);
            saw = ReadInstanceField (created.GetField ("G" + i));
            if (saw != null) return Fail (check + 3, saw);
            saw = ReadProperty (created.GetProperty ("P" + i, AnyProperty), c.Expected);
            if (saw != null) return Fail (check + 4, saw);
        }

        return 0;
    }

    // Each sequence's two values, in order, on each owner.
    static readonly object[][] Sequences =
    {
        new object[] { 5, 7 },
        new object[] { 5, "x" },
        new object[] { "abc", "" },
        new object[] { 5, "" },
        new object[] { null, 5 },
        new object[] { "abc", null },
        new object[] { "", "abc" },
        new object[] { 0x1122334455667788L, (byte) 2 },
        new object[] { "abcdef", "xy" },
        new object[] { (byte) 2, "" },
        new object[] { true, "" },
        new object[] { null, "" },
        new object[] { 'x', "" },
        new object[] { (short) 0x4142, "" },
        new object[] { 0x0044004300420041L, "" },
    };

    // An empty string writes no blob, so the row keeps the first value's bytes under the `STRING` type.
    static readonly object[] SecondExpected =
    {
        7, "x", "abc", "\u0005\u0000", 5, null, "abc", (byte) 2, "xy", "", "", "\0\0", "x", "䅂", "ABCD",
    };

    // Checks 101 to 146: check 101 + 3 * k + j for sequence k, where j is 0 for the parameter, 1 for the field and 2 for
    // the property; 146 for `Alternate`.
    static int Second ()
    {
        var (_, module) = NewModule ("Second");
        var types = new List<TypeBuilder> ();
        for (int k = 0; k < Sequences.Length; k++)
        {
            object[] values = Sequences[k];
            TypeBuilder t = module.DefineType ("S" + k, TypeAttributes.Public | TypeAttributes.Abstract);
            MethodBuilder m = t.DefineMethod ("M", MethodAttributes.Public | MethodAttributes.Static, typeof (void), new[] { typeof (object) });
            m.GetILGenerator ().Emit (OpCodes.Ret);
            ParameterBuilder p = m.DefineParameter (1, ParameterAttributes.None, "p");
            p.SetConstant (values[0]);
            p.SetConstant (values[1]);
            FieldBuilder f = t.DefineField ("F", typeof (object), FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal);
            f.SetConstant (values[0]);
            f.SetConstant (values[1]);
            PropertyBuilder pr = t.DefineProperty ("P", PropertyAttributes.None, typeof (object), Type.EmptyTypes);
            pr.SetConstant (values[0]);
            pr.SetConstant (values[1]);
            types.Add (t);
        }

        TypeBuilder alternate = module.DefineType ("Alternate", TypeAttributes.Public | TypeAttributes.Abstract);
        MethodBuilder am = alternate.DefineMethod (
            "M", MethodAttributes.Public | MethodAttributes.Static, typeof (void), new[] { typeof (int), typeof (int) });
        am.GetILGenerator ().Emit (OpCodes.Ret);
        ParameterBuilder a = am.DefineParameter (1, ParameterAttributes.None, "a");
        ParameterBuilder b = am.DefineParameter (2, ParameterAttributes.None, "b");
        b.SetConstant (1);
        a.SetConstant (2);
        b.SetConstant (3);
        a.SetConstant (4);

        for (int k = 0; k < Sequences.Length; k++)
        {
            Type created = types[k].CreateType ();
            object expected = SecondExpected[k];
            int check = 101 + 3 * k;
            string saw = ReadParameter (created.GetMethod ("M").GetParameters ()[0], 0x1000, expected, expected);
            if (saw != null) return Fail (check, saw);
            saw = ReadLiteral (created.GetField ("F"), expected);
            if (saw != null) return Fail (check + 1, saw);
            saw = ReadProperty (created.GetProperty ("P", AnyProperty), expected);
            if (saw != null) return Fail (check + 2, saw);
        }

        ParameterInfo[] ps = alternate.CreateType ().GetMethod ("M").GetParameters ();
        string sawA = ReadParameter (ps[0], 0x1000, 4, 4);
        string sawB = ReadParameter (ps[1], 0x1000, 3, 3);
        if (sawA != null || sawB != null) return Fail (146, (sawA ?? "a ok") + "; " + (sawB ?? "b ok"));
        return 0;
    }

    sealed class Conversion
    {
        public readonly Type Declared;
        public readonly object Value;
        public readonly object Default;
        public readonly object Raw;

        public Conversion (Type declared, object value, object defaultValue, object raw)
        {
            Declared = declared;
            Value = value;
            Default = defaultValue;
            Raw = raw;
        }
    }

    // Checks 150 to 174: 150 + k for accepted conversion k; 159 and 160 for null on an `int` field (the refusal, then
    // the field's flags); 161 + 2 * r and 162 + 2 * r for refusal r (the refusal on each owner, then the owners' flags).
    static int Conversions ()
    {
        var utc = new DateTime (2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var day = new DateTime (2020, 1, 2);

        // Accepted on a parameter, with what `DefaultValue` and `RawDefaultValue` read back.
        Conversion[] accepted =
        {
            new Conversion (typeof (Colour), Colour.Green, Colour.Green, 1),
            new Conversion (typeof (Colour), 1, Colour.Green, 1),
            new Conversion (typeof (int?), 5, 5, 5),
            new Conversion (typeof (int?), null, null, null),
            // `MdConstant.GetValue` converts to an enum only for an enum type, not for `Nullable<TEnum>`.
            new Conversion (typeof (Colour?), Colour.Blue, 2, 2),
            // A `DateTime` is stored as `I8` of its ticks; it reads back as a `DateTime` (Kind lost) only on `DateTime`.
            new Conversion (typeof (DateTime), utc, new DateTime (utc.Ticks, DateTimeKind.Unspecified), new DateTime (utc.Ticks, DateTimeKind.Unspecified)),
            new Conversion (typeof (DateTime?), day, day.Ticks, day.Ticks),
            new Conversion (typeof (object), day, day.Ticks, day.Ticks),
            // A parameter's `SetConstant` has no null check: null on a value type is accepted.
            new Conversion (typeof (int), null, null, null),
        };

        // Refused on a parameter, a literal field and a property, with the `ArgumentException`'s message.
        (Type, object, string)[] refused =
        {
            (typeof (Colour), 1L, DoesNotMatch),
            (typeof (long), 5, DoesNotMatch),
            (typeof (int), 5L, DoesNotMatch),
            (typeof (object), Colour.Green, "Colour is not a supported constant type."),
            (typeof (decimal), 1.5m, "System.Decimal is not a supported constant type."),
            (typeof (IntPtr), (IntPtr) 7, "System.IntPtr is not a supported constant type."),
            (typeof (Guid), Guid.Empty, "System.Guid is not a supported constant type."),
        };

        var (_, module) = NewModule ("Conversions");
        TypeBuilder t = module.DefineType ("C", TypeAttributes.Public | TypeAttributes.Abstract);
        for (int k = 0; k < accepted.Length; k++)
        {
            Conversion c = accepted[k];
            MethodBuilder m = t.DefineMethod ("A" + k, MethodAttributes.Public | MethodAttributes.Static, typeof (void), new[] { c.Declared });
            m.GetILGenerator ().Emit (OpCodes.Ret);
            m.DefineParameter (1, ParameterAttributes.None, "p").SetConstant (c.Value);
        }

        FieldBuilder nullField = t.DefineField ("NF", typeof (int), FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal);
        Exception e = Catch (() => nullField.SetConstant (null));
        if (!IsArgumentException (e, NullNotValid)) return Fail (159, ShowException (e));

        for (int r = 0; r < refused.Length; r++)
        {
            var (declared, value, message) = refused[r];
            MethodBuilder m = t.DefineMethod ("R" + r, MethodAttributes.Public | MethodAttributes.Static, typeof (void), new[] { declared });
            m.GetILGenerator ().Emit (OpCodes.Ret);
            ParameterBuilder p = m.DefineParameter (1, ParameterAttributes.None, "p");
            e = Catch (() => p.SetConstant (value));
            if (!IsArgumentException (e, message)) return Fail (161 + 2 * r, "parameter: " + ShowException (e));
            FieldBuilder f = t.DefineField ("RF" + r, declared, FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal);
            e = Catch (() => f.SetConstant (value));
            if (!IsArgumentException (e, message)) return Fail (161 + 2 * r, "field: " + ShowException (e));
            PropertyBuilder pr = t.DefineProperty ("RP" + r, PropertyAttributes.None, declared, Type.EmptyTypes);
            e = Catch (() => pr.SetConstant (value));
            if (!IsArgumentException (e, message)) return Fail (161 + 2 * r, "property: " + ShowException (e));
        }

        Type created = t.CreateType ();
        for (int k = 0; k < accepted.Length; k++)
        {
            Conversion c = accepted[k];
            string saw = ReadParameter (created.GetMethod ("A" + k).GetParameters ()[0], 0x1000, c.Default, c.Raw);
            if (saw != null) return Fail (150 + k, saw);
        }

        int nullFieldFlags = (int) created.GetField ("NF").Attributes;
        if (nullFieldFlags != 0x0056) return Fail (160, "field NF: attributes 0x" + nullFieldFlags.ToString ("x4"));

        for (int r = 0; r < refused.Length; r++)
        {
            string saw = ReadNoDefault (created.GetMethod ("R" + r).GetParameters ()[0]);
            if (saw != null) return Fail (162 + 2 * r, saw);
            int fieldFlags = (int) created.GetField ("RF" + r).Attributes;
            if (fieldFlags != 0x0056) return Fail (162 + 2 * r, "field: attributes 0x" + fieldFlags.ToString ("x4"));
            PropertyInfo pr = created.GetProperty ("RP" + r, AnyProperty);
            if (pr == null || (int) pr.Attributes != 0) return Fail (162 + 2 * r, pr == null ? "no property" : "property: attributes 0x" + ((int) pr.Attributes).ToString ("x4"));
        }

        return 0;
    }

    static string Names (Type t) => string.Join (",", Enum.GetNames (t));

    /// Each value of `t`'s `GetValues` as its name and its number.
    static string EnumValues (Type t)
    {
        var parts = new List<string> ();
        foreach (object v in Enum.GetValues (t))
        {
            parts.Add (v.GetType () == t ? v.ToString () + ":" + ((Enum) v).ToString ("D") : "not " + t.Name + ": " + Show (v));
        }

        return string.Join (",", parts);
    }

    static Type DefineEnum (ModuleBuilder module, string name, Type underlying, params (string, object)[] literals)
    {
        EnumBuilder e = module.DefineEnum (name, TypeAttributes.Public, underlying);
        foreach (var (literal, value) in literals)
        {
            e.DefineLiteral (literal, value);
        }

        return e.CreateType ();
    }

    // Checks 175 to 192.
    static int Enums ()
    {
        var (_, module) = NewModule ("Enums");

        // Sorted by value as unsigned, and stably: `Dup` after `B`.
        Type eInt = DefineEnum (module, "EInt", typeof (int), ("A", 0), ("B", 5), ("C", -1), ("Dup", 5));
        if (Names (eInt) != "A,B,Dup,C") return Fail (175, Names (eInt));
        if (EnumValues (eInt) != "A:0,B:5,B:5,C:-1") return Fail (176, EnumValues (eInt));
        string dup = eInt.GetField ("Dup").GetValue (null).ToString ();
        string five = Enum.ToObject (eInt, 5).ToString ();
        string minusOne = Enum.ToObject (eInt, -1).ToString ();
        if (dup != "B" || five != "B" || minusOne != "C") return Fail (177, dup + " " + five + " " + minusOne);
        bool defined5 = Enum.IsDefined (eInt, Enum.ToObject (eInt, 5));
        bool definedDup = Enum.IsDefined (eInt, "Dup");
        bool defined3 = Enum.IsDefined (eInt, Enum.ToObject (eInt, 3));
        if (!defined5 || !definedDup || defined3) return Fail (178, defined5 + " " + definedDup + " " + defined3);
        if (Enum.GetUnderlyingType (eInt) != typeof (int)) return Fail (179, Enum.GetUnderlyingType (eInt).Name);
        string[] intNames = { "A", "B", "C", "Dup" };
        int[] intValues = { 0, 5, -1, 5 };
        for (int i = 0; i < intNames.Length; i++)
        {
            FieldInfo f = eInt.GetField (intNames[i]);
            object raw = f.GetRawConstantValue ();
            if ((int) f.Attributes != 0x8056 || !Same (intValues[i], raw))
            {
                return Fail (179, f.Name + ": attributes 0x" + ((int) f.Attributes).ToString ("x4") + " " + Show (raw));
            }
        }

        Type eByte = DefineEnum (module, "EByte", typeof (byte), ("A", (byte) 1), ("B", (byte) 200));
        if (Names (eByte) != "A,B") return Fail (180, Names (eByte));
        if (EnumValues (eByte) != "A:1,B:200") return Fail (181, EnumValues (eByte));
        string b200 = Enum.ToObject (eByte, 200).ToString ();
        object byteRaw = eByte.GetField ("B").GetRawConstantValue ();
        if (b200 != "B" || Enum.IsDefined (eByte, Enum.ToObject (eByte, 5)) || !Same ((byte) 200, byteRaw)) return Fail (182, b200 + " " + Show (byteRaw));

        // `long.MinValue` is the largest as unsigned.
        Type eLong = DefineEnum (module, "ELong", typeof (long), ("A", long.MinValue), ("B", 5L));
        if (Names (eLong) != "B,A") return Fail (183, Names (eLong));
        if (EnumValues (eLong) != "B:5,A:-9223372036854775808") return Fail (184, EnumValues (eLong));
        string min = Enum.ToObject (eLong, long.MinValue).ToString ();
        if (min != "A" || !Enum.IsDefined (eLong, Enum.ToObject (eLong, 5))) return Fail (185, min);

        Type eULong = DefineEnum (module, "EULong", typeof (ulong), ("A", ulong.MaxValue), ("B", 1UL));
        if (Names (eULong) != "B,A") return Fail (186, Names (eULong));
        if (EnumValues (eULong) != "B:1,A:18446744073709551615") return Fail (187, EnumValues (eULong));
        string max = Enum.ToObject (eULong, ulong.MaxValue).ToString ();
        if (max != "A" || Enum.IsDefined (eULong, Enum.ToObject (eULong, 5))) return Fail (188, max);

        // A `TypeBuilder` enum's underlying type is its `value__` field's, so a literal of its own type is refused until
        // that field exists.
        TypeBuilder tEnum = module.DefineType ("TEnum", TypeAttributes.Public | TypeAttributes.Sealed, typeof (Enum));
        FieldBuilder early = tEnum.DefineField ("Early", tEnum, FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal);
        Exception e = Catch (() => early.SetConstant (1));
        if (!IsArgumentException (e, DoesNotMatch)) return Fail (189, ShowException (e));
        tEnum.DefineField ("value__", typeof (int), FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName);
        FieldBuilder a = tEnum.DefineField ("A", tEnum, FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal);
        e = Catch (() => a.SetConstant (3));
        if (e != null) return Fail (190, "A: " + ShowException (e));
        e = Catch (() => early.SetConstant (1));
        if (e != null) return Fail (190, "Early: " + ShowException (e));
        Type createdEnum = tEnum.CreateType ();
        if (Names (createdEnum) != "Early,A") return Fail (191, Names (createdEnum));
        if (EnumValues (createdEnum) != "Early:1,A:3") return Fail (192, EnumValues (createdEnum));
        return 0;
    }

    // Checks 193 to 196.
    static int Order ()
    {
        var (_, module) = NewModule ("Order");
        TypeBuilder t = module.DefineType ("O", TypeAttributes.Public | TypeAttributes.Abstract);
        MethodBuilder early = t.DefineMethod ("Early", MethodAttributes.Public | MethodAttributes.Static, typeof (void), new[] { typeof (int) });
        early.GetILGenerator ().Emit (OpCodes.Ret);
        ParameterBuilder earlyP = early.DefineParameter (1, ParameterAttributes.None, "p");
        MethodBuilder late = t.DefineMethod ("Late", MethodAttributes.Public | MethodAttributes.Static, typeof (void), new[] { typeof (int) });
        late.GetILGenerator ().Emit (OpCodes.Ret);
        ParameterBuilder lateP = late.DefineParameter (1, ParameterAttributes.None, "p");
        FieldBuilder f = t.DefineField ("F", typeof (int), FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal);
        PropertyBuilder p = t.DefineProperty ("P", PropertyAttributes.None, typeof (int), Type.EmptyTypes);

        lateP.SetConstant (1);
        f.SetConstant (2);
        earlyP.SetConstant (3);
        p.SetConstant (4);

        Type created = t.CreateType ();
        string saw = ReadParameter (created.GetMethod ("Late").GetParameters ()[0], 0x1000, 1, 1);
        if (saw != null) return Fail (193, saw);
        saw = ReadLiteral (created.GetField ("F"), 2);
        if (saw != null) return Fail (194, saw);
        saw = ReadParameter (created.GetMethod ("Early").GetParameters ()[0], 0x1000, 3, 3);
        if (saw != null) return Fail (195, saw);
        saw = ReadProperty (created.GetProperty ("P", AnyProperty), 4);
        if (saw != null) return Fail (196, saw);
        return 0;
    }

    // Checks 197 to 201.
    static int Accessors ()
    {
        var (_, module) = NewModule ("Accessors");

        // The constant first: `SetConstant` detaches only the accessors the property already has.
        TypeBuilder a = module.DefineType ("A", TypeAttributes.Public);
        a.DefineDefaultConstructor (MethodAttributes.Public);
        FieldBuilder backing = a.DefineField ("x", typeof (int), FieldAttributes.Private);
        PropertyBuilder x = a.DefineProperty ("X", PropertyAttributes.None, typeof (int), Type.EmptyTypes);
        x.SetConstant (7);
        MethodBuilder getter = a.DefineMethod ("get_X", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, typeof (int), Type.EmptyTypes);
        ILGenerator il = getter.GetILGenerator ();
        il.Emit (OpCodes.Ldarg_0);
        il.Emit (OpCodes.Ldfld, backing);
        il.Emit (OpCodes.Ret);
        x.SetGetMethod (getter);
        MethodBuilder setter = a.DefineMethod (
            "set_X", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, typeof (void), new[] { typeof (int) });
        il = setter.GetILGenerator ();
        il.Emit (OpCodes.Ldarg_0);
        il.Emit (OpCodes.Ldarg_1);
        il.Emit (OpCodes.Stfld, backing);
        il.Emit (OpCodes.Ret);
        x.SetSetMethod (setter);

        Type createdA = a.CreateType ();
        PropertyInfo xi = createdA.GetProperty ("X");
        if (xi == null) return Fail (197, "no property X");
        string get = xi.GetGetMethod ()?.Name ?? "null";
        string set = xi.GetSetMethod ()?.Name ?? "null";
        if (get != "get_X" || set != "set_X") return Fail (197, "getter " + get + " setter " + set);
        object instance = Activator.CreateInstance (createdA);
        xi.SetValue (instance, 42);
        object read = xi.GetValue (instance);
        if (!Same (42, read)) return Fail (198, Show (read));
        string saw = ReadProperty (xi, 7);
        if (saw != null) return Fail (199, saw);

        // An Other method is not an accessor the QCall detaches.
        TypeBuilder b = module.DefineType ("B", TypeAttributes.Public | TypeAttributes.Abstract);
        PropertyBuilder y = b.DefineProperty ("Y", PropertyAttributes.None, typeof (int), Type.EmptyTypes);
        MethodBuilder other = b.DefineMethod ("other_Y", MethodAttributes.Public | MethodAttributes.HideBySig, typeof (void), Type.EmptyTypes);
        other.GetILGenerator ().Emit (OpCodes.Ret);
        y.AddOtherMethod (other);
        y.SetConstant (7);

        Type createdB = b.CreateType ();
        PropertyInfo yi = createdB.GetProperty ("Y", AnyProperty | BindingFlags.DeclaredOnly);
        if (yi == null) return Fail (200, "no property Y");
        var accessors = new List<string> ();
        foreach (MethodInfo accessor in yi.GetAccessors (true))
        {
            accessors.Add (accessor.Name);
        }

        if (string.Join (",", accessors) != "other_Y") return Fail (200, string.Join (",", accessors));
        saw = ReadProperty (yi, 7);
        if (saw != null) return Fail (201, saw);
        return 0;
    }

    // Checks 202 to 204.
    static int Created ()
    {
        var (_, module) = NewModule ("Created");
        TypeBuilder t = module.DefineType ("C", TypeAttributes.Public | TypeAttributes.Abstract);
        FieldBuilder f = t.DefineField ("F", typeof (int), FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal);
        f.SetConstant (1);
        PropertyBuilder p = t.DefineProperty ("P", PropertyAttributes.None, typeof (int), Type.EmptyTypes);
        EnumBuilder e = module.DefineEnum ("E", TypeAttributes.Public, typeof (int));
        e.DefineLiteral ("A", 1);
        Type created = t.CreateType ();
        Type createdEnum = e.CreateType ();

        Exception x = Catch (() => f.SetConstant (2));
        if (!IsInvalidOperation (x, AfterCreate)) return Fail (202, ShowException (x));
        string saw = ReadLiteral (created.GetField ("F"), 1);
        if (saw != null) return Fail (202, saw);

        x = Catch (() => p.SetConstant (3));
        if (!IsInvalidOperation (x, AfterCreate)) return Fail (203, ShowException (x));
        PropertyInfo pi = created.GetProperty ("P", AnyProperty);
        if (pi == null || (int) pi.Attributes != 0) return Fail (203, pi == null ? "no property" : "attributes 0x" + ((int) pi.Attributes).ToString ("x4"));

        x = Catch (() => e.DefineLiteral ("B", 2));
        if (!IsInvalidOperation (x, AfterCreate)) return Fail (204, ShowException (x));
        if (Names (createdEnum) != "A") return Fail (204, Names (createdEnum));
        return 0;
    }

    static bool IsNullableType (Type t) => t.IsGenericType && t.GetGenericTypeDefinition () == typeof (Nullable<>);

    /// Castle.Core 5.1.1's `MethodEmitter.CopyDefaultValueConstant`, as its IL reads.
    static void CopyDefaultValueConstant (ParameterInfo from, ParameterBuilder to)
    {
        object defaultValue;
        try
        {
            defaultValue = from.DefaultValue;
        }
        catch (FormatException) when (from.ParameterType == typeof (DateTime))
        {
            defaultValue = null;
        }
        catch (FormatException) when (from.ParameterType.IsEnum)
        {
            defaultValue = null;
        }

        if (defaultValue is Missing) return;

        try
        {
            to.SetConstant (defaultValue);
        }
        catch (ArgumentException)
        {
            Type parameterType = from.ParameterType;
            Type parameterNonNullableType = parameterType;
            if (defaultValue == null)
            {
                if (IsNullableType (parameterType)) return;
                if (parameterType.IsValueType) return;
            }
            else if (IsNullableType (parameterType))
            {
                parameterNonNullableType = from.ParameterType.GetGenericArguments ()[0];
                if (parameterNonNullableType.IsEnum || parameterNonNullableType.IsAssignableFrom (defaultValue.GetType ())) return;
            }

            try
            {
                object coerced = Convert.ChangeType (defaultValue, parameterNonNullableType, CultureInfo.InvariantCulture);
                to.SetConstant (coerced);
                return;
            }
            catch
            {
            }

            throw;
        }
    }

    // Checks 205 to 214.
    static int Castle ()
    {
        MethodInfo im = typeof (IDefaults).GetMethod ("Eight");
        ParameterInfo[] ps = im.GetParameters ();
        object[] expectedDefault = { 3, 4L, "five", true, 1.5, 'q', null, Colour.Blue };
        object[] expectedRaw = { 3, 4L, "five", true, 1.5, 'q', null, 2 };
        for (int i = 0; i < ps.Length; i++)
        {
            string saw = ReadParameter (ps[i], 0x1010, expectedDefault[i], expectedRaw[i]);
            if (saw != null) return Fail (214, "the interface's own " + saw);
        }

        var (_, module) = NewModule ("Castle");
        TypeBuilder t = module.DefineType ("Proxy", TypeAttributes.Public, typeof (object), new[] { typeof (IDefaults) });
        t.DefineDefaultConstructor (MethodAttributes.Public);
        var types = new Type[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            types[i] = ps[i].ParameterType;
        }

        MethodBuilder m = t.DefineMethod (
            "Eight",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
            typeof (int),
            types);

        // `MethodEmitter.DefineParameters`, less its copy of custom attributes: the builder clears `HasDefault` from the
        // flags it is given, and `SetConstant` sets it again.
        for (int i = 0; i < ps.Length; i++)
        {
            ParameterInfo parameter = ps[i];
            ParameterBuilder pb = m.DefineParameter (i + 1, parameter.Attributes, parameter.Name);
            if ((parameter.Attributes & ParameterAttributes.HasDefault) != 0)
            {
                // Castle swallows any failure here; the reads below see it as a missing default.
                try
                {
                    CopyDefaultValueConstant (parameter, pb);
                }
                catch
                {
                }
            }
        }

        ILGenerator il = m.GetILGenerator ();
        il.Emit (OpCodes.Ldarg_1);
        il.Emit (OpCodes.Ret);
        t.DefineMethodOverride (m, im);

        Type created = t.CreateType ();
        var proxy = (IDefaults) Activator.CreateInstance (created);
        int defaulted = proxy.Eight ();
        int given = proxy.Eight (9);
        if (defaulted != 3 || given != 9) return Fail (205, defaulted + " " + given);

        ParameterInfo[] copied = created.GetMethod ("Eight").GetParameters ();
        for (int i = 0; i < copied.Length; i++)
        {
            string saw = ReadParameter (copied[i], 0x1010, expectedDefault[i], expectedRaw[i]);
            if (saw != null) return Fail (206 + i, saw);
        }

        return 0;
    }

    // Check 215.
    static int Long ()
    {
        var chars = new char[32767];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = (char) ('a' + i % 26);
        }

        string value = new string (chars);
        var (_, module) = NewModule ("Long");
        TypeBuilder t = module.DefineType ("L", TypeAttributes.Public | TypeAttributes.Abstract);
        MethodBuilder m = t.DefineMethod ("M", MethodAttributes.Public | MethodAttributes.Static, typeof (void), new[] { typeof (string) });
        m.GetILGenerator ().Emit (OpCodes.Ret);
        m.DefineParameter (1, ParameterAttributes.None, "p").SetConstant (value);
        object read = t.CreateType ().GetMethod ("M").GetParameters ()[0].DefaultValue;
        if (!(read is string s) || s != value)
        {
            return Fail (215, read is string r ? "string of length " + r.Length : Show (read));
        }

        return 0;
    }

    public static int Main ()
    {
        int result = Values ();
        if (result != 0) return result;
        result = Second ();
        if (result != 0) return result;
        result = Conversions ();
        if (result != 0) return result;
        result = Enums ();
        if (result != 0) return result;
        result = Order ();
        if (result != 0) return result;
        result = Accessors ();
        if (result != 0) return result;
        result = Created ();
        if (result != 0) return result;
        result = Castle ();
        if (result != 0) return result;
        return Long ();
    }
}
