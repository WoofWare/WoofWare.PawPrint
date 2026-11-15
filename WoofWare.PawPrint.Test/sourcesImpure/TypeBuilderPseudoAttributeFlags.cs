using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// The seven pseudo-attributes whose effect is a flag in their owner's row (`Serializable`, `NonSerialized`,
// `SpecialName`, `In`, `Out`, `Optional`, `PreserveSig`), through `SetCustomAttribute`. CoreCLR's emitter checks the
// owner's table, ORs the flag into the row and stores no CustomAttribute row, or throws `ArgumentException` for a wrong
// target; it never reads the blob. Each module is its own: `Matrix`, each attribute on each of the ten owner kinds;
// `Blobs`, blobs the emitter would refuse to parse; `Dup`; `Order`, ordinary rows interleaved with flags and a refusal;
// `GenParam`, an attribute on a generic parameter, which `CreateType` bakes and throws for; `CastleBits` and
// `CastleNone`, Castle DynamicProxy's copy of an interface method's `in`, `out` and optional parameters, with the
// parameter's flags given to `DefineParameter` and without; and `Impostor`, the module's own `OutAttribute`. The test's
// terminal-state check reads the rows of `Matrix`, `Order` and `GenParam`. Measured on .NET 10 and 11 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

[AttributeUsage (AttributeTargets.All, AllowMultiple = true)]
public class TagAttribute : Attribute
{
    public TagAttribute (string s) { S = s; }
    public string S;
}

public interface ICastle
{
    bool TryGet (string key, out int value);
    int Read (in long x);
    int Scale (int x, [Optional] int factor);
}

public static class Program
{
    static readonly Type[] Attributes =
    {
        typeof (SerializableAttribute),
        typeof (NonSerializedAttribute),
        typeof (SpecialNameAttribute),
        typeof (InAttribute),
        typeof (OutAttribute),
        typeof (OptionalAttribute),
        typeof (PreserveSigAttribute),
    };

    static readonly string[] Owners = { "assembly", "module", "type", "method", "ctor", "param", "return", "field", "property", "event" };

    // Whether the emitter accepts each attribute on each owner, in the order of `Attributes` and `Owners`.
    static readonly bool[][] Accepted =
    {
        new[] { false, false, true, false, false, false, false, false, false, false },
        new[] { false, false, false, false, false, false, false, true, false, false },
        new[] { false, false, true, true, true, false, false, true, true, true },
        new[] { false, false, false, false, false, true, true, false, false, false },
        new[] { false, false, false, false, false, true, true, false, false, false },
        new[] { false, false, false, false, false, true, true, false, false, false },
        new[] { false, false, false, true, true, false, false, false, false, false },
    };

    // Each owner's flags once its type is created, for the owners with a flags column ("type" to "event"), and the
    // implementation flags of "method" and "ctor".
    static readonly int[][] Flags =
    {
        new[] { 0x2001, 0x0016, 0x1806, 0x0000, 0x0000, 0x0006, 0x0000, 0x0000 },
        new[] { 0x0001, 0x0016, 0x1806, 0x0000, 0x0000, 0x0086, 0x0000, 0x0000 },
        new[] { 0x0401, 0x0816, 0x1806, 0x0000, 0x0000, 0x0206, 0x0200, 0x0200 },
        new[] { 0x0001, 0x0016, 0x1806, 0x0001, 0x0001, 0x0006, 0x0000, 0x0000 },
        new[] { 0x0001, 0x0016, 0x1806, 0x0002, 0x0002, 0x0006, 0x0000, 0x0000 },
        new[] { 0x0001, 0x0016, 0x1806, 0x0010, 0x0010, 0x0006, 0x0000, 0x0000 },
        new[] { 0x0001, 0x0016, 0x1806, 0x0000, 0x0000, 0x0006, 0x0000, 0x0000 },
    };

    static readonly int[][] ImplFlags =
    {
        new[] { 0, 0 }, new[] { 0, 0 }, new[] { 0, 0 }, new[] { 0, 0 }, new[] { 0, 0 }, new[] { 0, 0 }, new[] { 0x80, 0x80 },
    };

    /// A dynamic assembly named `name`, and its module of the same name.
    static (AssemblyBuilder, ModuleBuilder) NewModule (string name)
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName (name), AssemblyBuilderAccess.Run);
        return (assembly, assembly.DefineDynamicModule (name));
    }

    static CustomAttributeBuilder Cab0 (Type t) => new CustomAttributeBuilder (t.GetConstructor (Type.EmptyTypes), new object[0]);

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

    static string Names (IList<CustomAttributeData> rows)
    {
        var names = new List<string> ();
        foreach (CustomAttributeData row in rows)
        {
            names.Add (row.AttributeType.Name);
        }

        return string.Join (",", names);
    }

    static string Tags (IList<CustomAttributeData> rows)
    {
        var tags = new List<string> ();
        foreach (CustomAttributeData row in rows)
        {
            tags.Add (row.AttributeType == typeof (TagAttribute) ? (string) row.ConstructorArguments[0].Value : row.AttributeType.Name);
        }

        return string.Join (",", tags);
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

    /// A type with a method `int M(int x)` (its return parameter and `x` defined with no flags), a default
    /// constructor, a field, a property with a getter and an event with two accessors.
    static Members Define (ModuleBuilder module, string name)
    {
        var o = new Members ();
        o.T = module.DefineType (name, TypeAttributes.Public);
        o.M = o.T.DefineMethod ("M", MethodAttributes.Public | MethodAttributes.Static, typeof (int), new[] { typeof (int) });
        o.Return = o.M.DefineParameter (0, ParameterAttributes.None, "ret");
        o.X = o.M.DefineParameter (1, ParameterAttributes.None, "x");
        ILGenerator il = o.M.GetILGenerator ();
        il.Emit (OpCodes.Ldarg_0);
        il.Emit (OpCodes.Ldc_I4_1);
        il.Emit (OpCodes.Add);
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

    static void Apply (AssemblyBuilder assembly, ModuleBuilder module, Members o, string owner, CustomAttributeBuilder cab)
    {
        switch (owner)
        {
            case "assembly": assembly.SetCustomAttribute (cab); break;
            case "module": module.SetCustomAttribute (cab); break;
            case "type": o.T.SetCustomAttribute (cab); break;
            case "method": o.M.SetCustomAttribute (cab); break;
            case "ctor": o.C.SetCustomAttribute (cab); break;
            case "param": o.X.SetCustomAttribute (cab); break;
            case "return": o.Return.SetCustomAttribute (cab); break;
            case "field": o.F.SetCustomAttribute (cab); break;
            case "property": o.P.SetCustomAttribute (cab); break;
            case "event": o.E.SetCustomAttribute (cab); break;
            default: throw new Exception (owner);
        }
    }

    static int[] CreatedFlags (Type created)
    {
        MethodInfo m = created.GetMethod ("M");
        return new[]
        {
            (int) created.Attributes,
            (int) m.Attributes,
            (int) created.GetConstructor (Type.EmptyTypes).Attributes,
            (int) m.GetParameters ()[0].Attributes,
            (int) m.ReturnParameter.Attributes,
            (int) created.GetField ("F").Attributes,
            (int) created.GetProperty ("P").Attributes,
            (int) created.GetEvent ("E").Attributes,
        };
    }

    // Checks 1 to 168.
    static int Matrix ()
    {
        var (assembly, module) = NewModule ("Matrix");
        for (int a = 0; a < Attributes.Length; a++)
        {
            Members o = Define (module, Attributes[a].Name);
            for (int w = 0; w < Owners.Length; w++)
            {
                string owner = Owners[w];
                CustomAttributeBuilder cab = Cab0 (Attributes[a]);
                bool ok = Accepted[a][w]
                    ? Accepts (() => Apply (assembly, module, o, owner, cab))
                    : ThrowsInvalidAttribute (() => Apply (assembly, module, o, owner, cab));
                if (!ok)
                {
                    return 1 + 10 * a + w;
                }
            }

            // The token this row gets says how many rows the module holds.
            o.T.SetCustomAttribute (Tag ("after " + Attributes[a].Name));

            Type created;
            try
            {
                created = o.T.CreateType ();
            }
            catch (Exception)
            {
                return 162 + a;
            }

            int[] flags = CreatedFlags (created);
            for (int i = 0; i < flags.Length; i++)
            {
                if (flags[i] != Flags[a][i])
                {
                    return 71 + 8 * a + i;
                }
            }

            MethodInfo m = created.GetMethod ("M");
            if ((int) m.MethodImplementationFlags != ImplFlags[a][0])
            {
                return 127 + 2 * a;
            }

            if ((int) created.GetConstructor (Type.EmptyTypes).MethodImplementationFlags != ImplFlags[a][1])
            {
                return 128 + 2 * a;
            }

            // Reflection synthesises the attribute from the flag, for those it knows.
            string typeAttributes = Names (created.GetCustomAttributesData ());
            if (typeAttributes != (a == 0 ? "SerializableAttribute,TagAttribute" : "TagAttribute"))
            {
                return 141 + a;
            }

            string parameterAttributes = Names (m.GetParameters ()[0].GetCustomAttributesData ());
            if (parameterAttributes != (a == 3 ? "InAttribute" : a == 4 ? "OutAttribute" : a == 5 ? "OptionalAttribute" : ""))
            {
                return 148 + a;
            }

            if ((int) m.Invoke (null, new object[] { 4 }) != 5 || Activator.CreateInstance (created).GetType () != created)
            {
                return 155 + a;
            }
        }

        return 0;
    }

    // Checks 170 to 196.
    static int Blobs ()
    {
        byte[][] blobs =
        {
            new byte[0],
            new byte[] { 1, 0 },
            new byte[] { 0xFF, 0xFF },
            new byte[] { 0x01 },
            new byte[] { 1, 0, 0xDE, 0xAD, 0xBE, 0xEF },
            new byte[] { 1, 0, 1, 0, 0x99 },
        };

        var (_, module) = NewModule ("Blobs");
        Members o = Define (module, "T");
        ConstructorInfo serializable = typeof (SerializableAttribute).GetConstructor (Type.EmptyTypes);
        ConstructorInfo optional = typeof (OptionalAttribute).GetConstructor (Type.EmptyTypes);
        ConstructorInfo outAttribute = typeof (OutAttribute).GetConstructor (Type.EmptyTypes);
        for (int i = 0; i < blobs.Length; i++)
        {
            byte[] blob = blobs[i];
            if (!Accepts (() => o.T.SetCustomAttribute (serializable, blob))) return 170 + i;
            if (!Accepts (() => o.X.SetCustomAttribute (optional, blob))) return 176 + i;
            if (!Accepts (() => o.Return.SetCustomAttribute (outAttribute, blob))) return 182 + i;
            if (!ThrowsInvalidAttribute (() => o.M.SetCustomAttribute (serializable, blob))) return 188 + i;
        }

        Type created = o.T.CreateType ();
        if ((int) created.Attributes != 0x2001) return 194;
        MethodInfo m = created.GetMethod ("M");
        if ((int) m.GetParameters ()[0].Attributes != 0x10) return 195;
        if ((int) m.ReturnParameter.Attributes != 0x2) return 196;
        return 0;
    }

    // Checks 200 to 202.
    static int Dup ()
    {
        var (_, module) = NewModule ("Dup");
        Members o = Define (module, "T");
        o.T.SetCustomAttribute (Cab0 (typeof (SerializableAttribute)));
        o.T.SetCustomAttribute (Cab0 (typeof (SerializableAttribute)));
        o.X.SetCustomAttribute (Cab0 (typeof (OutAttribute)));
        o.X.SetCustomAttribute (Cab0 (typeof (OutAttribute)));
        o.X.SetCustomAttribute (Cab0 (typeof (InAttribute)));
        Type created = o.T.CreateType ();
        if ((int) created.Attributes != 0x2001) return 200;
        if ((int) created.GetMethod ("M").GetParameters ()[0].Attributes != 0x3) return 201;
        // The builder keeps the flags it was defined with.
        if ((int) o.X.Attributes != 0) return 202;
        return 0;
    }

    // Checks 205 and 206.
    static int Order ()
    {
        var (_, module) = NewModule ("Order");
        Members o = Define (module, "T");
        o.T.SetCustomAttribute (Tag ("a"));
        o.T.SetCustomAttribute (Cab0 (typeof (SerializableAttribute)));
        o.T.SetCustomAttribute (Tag ("b"));
        if (!ThrowsInvalidAttribute (() => o.M.SetCustomAttribute (Cab0 (typeof (SerializableAttribute)))))
        {
            return 205;
        }

        o.T.SetCustomAttribute (Tag ("c"));
        o.X.SetCustomAttribute (Cab0 (typeof (OptionalAttribute)));
        o.T.SetCustomAttribute (Tag ("d"));
        Type created = o.T.CreateType ();
        if (Tags (created.GetCustomAttributesData ()) != "SerializableAttribute,a,b,c,d")
        {
            return 206;
        }

        return 0;
    }

    // Checks 210 to 216.
    static int GenParam ()
    {
        var (_, module) = NewModule ("GenParam");

        TypeBuilder g = module.DefineType ("G", TypeAttributes.Public);
        GenericTypeParameterBuilder gp = g.DefineGenericParameters ("TT")[0];
        // Queued until `CreateType` bakes the parameter, which throws at the first.
        if (!Accepts (() => gp.SetCustomAttribute (Cab0 (typeof (SerializableAttribute))))) return 210;
        gp.SetCustomAttribute (Tag ("after"));
        if (!ThrowsInvalidAttribute (() => g.CreateType ())) return 211;
        if (g.IsCreated ()) return 212;

        TypeBuilder h = module.DefineType ("H", TypeAttributes.Public);
        MethodBuilder hm = h.DefineMethod ("M", MethodAttributes.Public | MethodAttributes.Static, typeof (void), Type.EmptyTypes);
        GenericTypeParameterBuilder mp = hm.DefineGenericParameters ("U")[0];
        hm.GetILGenerator ().Emit (OpCodes.Ret);
        if (!Accepts (() => mp.SetCustomAttribute (Cab0 (typeof (OutAttribute))))) return 213;
        mp.SetCustomAttribute (Tag ("after"));
        if (!ThrowsInvalidAttribute (() => h.CreateType ())) return 214;
        if (h.IsCreated ()) return 215;

        // The module still creates another type.
        TypeBuilder k = module.DefineType ("K", TypeAttributes.Public);
        k.DefineDefaultConstructor (MethodAttributes.Public);
        if (Activator.CreateInstance (k.CreateType ()).GetType ().Name != "K") return 216;
        return 0;
    }

    /// Castle's `AttributeUtil.ShouldSkipAttributeReplication` for a parameter of a method.
    static bool ShouldSkipAttributeReplication (Type attribute)
    {
        if (!attribute.IsPublic && !attribute.IsNestedPublic) return true;
        if (attribute == typeof (ComImportAttribute) || attribute == typeof (MarshalAsAttribute) || attribute == typeof (TypeIdentifierAttribute))
        {
            return true;
        }

        if (attribute == typeof (ParamArrayAttribute)) return false;
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

    /// A proxy of `ICastle` as Castle's `ClassEmitter` and `MethodEmitter` define one: the type `Public |
    /// Serializable`, each method with the interface method's signature, its parameters defined with the interface
    /// parameter's flags or with none, and each parameter's non-inheritable attributes copied onto it.
    static Type CastleProxy (string name, bool flagsInRow)
    {
        var (_, module) = NewModule (name);
        TypeBuilder t = module.DefineType (
            "Proxy", TypeAttributes.Public | TypeAttributes.Serializable, typeof (object), new[] { typeof (ICastle) });
        t.DefineDefaultConstructor (MethodAttributes.Public);
        foreach (MethodInfo im in typeof (ICastle).GetMethods ())
        {
            ParameterInfo[] ps = im.GetParameters ();
            var types = new Type[ps.Length];
            var required = new Type[ps.Length][];
            var optional = new Type[ps.Length][];
            for (int i = 0; i < ps.Length; i++)
            {
                types[i] = ps[i].ParameterType;
                required[i] = ps[i].GetRequiredCustomModifiers ();
                optional[i] = ps[i].GetOptionalCustomModifiers ();
            }

            MethodBuilder m = t.DefineMethod (
                im.Name,
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                CallingConventions.HasThis,
                im.ReturnType,
                null,
                null,
                types,
                required,
                optional);
            for (int i = 0; i < ps.Length; i++)
            {
                ParameterBuilder p = m.DefineParameter (i + 1, flagsInRow ? ps[i].Attributes : ParameterAttributes.None, ps[i].Name);
                foreach (CustomAttributeData data in ps[i].CustomAttributes)
                {
                    if (!ShouldSkipAttributeReplication (data.AttributeType))
                    {
                        p.SetCustomAttribute (Replicate (data));
                    }
                }
            }

            ILGenerator il = m.GetILGenerator ();
            switch (im.Name)
            {
                case "TryGet":
                    il.Emit (OpCodes.Ldarg_2);
                    il.Emit (OpCodes.Ldc_I4, 42);
                    il.Emit (OpCodes.Stind_I4);
                    il.Emit (OpCodes.Ldc_I4_1);
                    break;
                case "Read":
                    il.Emit (OpCodes.Ldarg_1);
                    il.Emit (OpCodes.Ldind_I8);
                    il.Emit (OpCodes.Conv_I4);
                    break;
                default:
                    il.Emit (OpCodes.Ldarg_1);
                    il.Emit (OpCodes.Ldarg_2);
                    il.Emit (OpCodes.Mul);
                    break;
            }

            il.Emit (OpCodes.Ret);
            t.DefineMethodOverride (m, im);
        }

        return t.CreateType ();
    }

    static int Castle (string name, bool flagsInRow, int check)
    {
        Type created = CastleProxy (name, flagsInRow);
        if ((int) created.Attributes != 0x2001) return check;

        var proxy = (ICastle) Activator.CreateInstance (created);
        if (!proxy.TryGet ("k", out int value) || value != 42) return check + 1;
        long seven = 7;
        if (proxy.Read (in seven) != 7) return check + 2;
        if (proxy.Scale (3, 4) != 12) return check + 3;

        ParameterInfo[] tryGet = created.GetMethod ("TryGet").GetParameters ();
        ParameterInfo[] read = created.GetMethod ("Read").GetParameters ();
        ParameterInfo[] scale = created.GetMethod ("Scale").GetParameters ();
        if ((int) tryGet[0].Attributes != 0 || (int) tryGet[1].Attributes != 0x2) return check + 4;
        if ((int) read[0].Attributes != 0x1) return check + 5;
        if ((int) scale[0].Attributes != 0 || (int) scale[1].Attributes != 0x10) return check + 6;
        if (Names (tryGet[1].GetCustomAttributesData ()) != "OutAttribute") return check + 7;
        if (Names (read[0].GetCustomAttributesData ()) != "InAttribute,IsReadOnlyAttribute") return check + 8;
        if (Names (scale[1].GetCustomAttributesData ()) != "OptionalAttribute") return check + 9;
        return 0;
    }

    // Checks 240 and 241.
    static int Impostor ()
    {
        var (_, module) = NewModule ("Impostor");
        TypeBuilder fake = module.DefineType ("System.Runtime.InteropServices.OutAttribute", TypeAttributes.Public, typeof (Attribute));
        ConstructorBuilder fakeCtor = fake.DefineDefaultConstructor (MethodAttributes.Public);
        Members o = Define (module, "T");
        o.X.SetCustomAttribute (new CustomAttributeBuilder (fakeCtor, new object[0]));
        fake.CreateType ();
        Type created = o.T.CreateType ();
        ParameterInfo x = created.GetMethod ("M").GetParameters ()[0];
        if ((int) x.Attributes != 0x2) return 240;
        // No row: reflection synthesises CoreLib's attribute from the flag, and shows nothing of the module's.
        IList<CustomAttributeData> rows = x.GetCustomAttributesData ();
        if (rows.Count != 1 || rows[0].AttributeType != typeof (OutAttribute)) return 241;
        return 0;
    }

    public static int Main ()
    {
        int result = Matrix ();
        if (result != 0) return result;
        result = Blobs ();
        if (result != 0) return result;
        result = Dup ();
        if (result != 0) return result;
        result = Order ();
        if (result != 0) return result;
        result = GenParam ();
        if (result != 0) return result;
        result = Castle ("CastleBits", true, 220);
        if (result != 0) return result;
        result = Castle ("CastleNone", false, 230);
        if (result != 0) return result;
        return Impostor ();
    }
}
