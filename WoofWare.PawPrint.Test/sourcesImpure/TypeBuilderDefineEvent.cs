using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

// `TypeBuilder_DefineEvent`, and the Event association of `TypeBuilder_DefineMethodSemantics`, through
// `TypeBuilder.DefineEvent` and `EventBuilder.SetAddOnMethod`, `SetRemoveOnMethod`, `SetRaiseMethod` and
// `AddOtherMethod`, each read back through reflection. In module `Ref`: the arguments the managed side refuses
// before the QCall, which consume no token; a name with an embedded NUL; event types of every kind, each followed
// by a fresh TypeSpec's token, which shows how many TypeSpec rows the event added; a duplicate, which the emitter
// does not look for; and the calls refused once a type is created. In `Flags`, each single flag bit and nine other
// values. In `Inter`, events of several types defined interleaved, before and after one type is created and on a
// type defined later, so that the EventMap is not in TypeDef order and an EventPtr is needed. In `S11`, `S24` and
// `S25`, a property's and an event's accessors interleaved, the two raises included, in modules either side of the
// 25 MethodSemantics rows from which CoreCLR finds an association's rows in reverse. In `Refl`, what reflection
// makes of a generic type's event, of events with every kind of accessor, of a struct's and an interface's, and of
// a dynamic base's event hidden by name. Then an accessor of another type of the module, one of a created base, one
// of another module whose token names a method of this one, and an event type that reflection does not read. The
// test's terminal-state check reads `Ref`'s, `Flags`', `Inter`'s and `S11`'s rows. Measured on .NET 10 and 11 to
// exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    const BindingFlags All =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    static ModuleBuilder NewModule (string name) =>
        AssemblyBuilder.DefineDynamicAssembly (new AssemblyName (name), AssemblyBuilderAccess.Run).DefineDynamicModule (name);

    /// The token `TypeBuilder_DefineEvent` handed back, which `EventBuilder` keeps privately.
    static int Token (EventBuilder e) =>
        (int) e.GetType ().GetField ("m_evToken", BindingFlags.NonPublic | BindingFlags.Instance).GetValue (e);

    /// A method taking one argument of type `t`. Given a field, it combines its argument into the field, or
    /// removes it if its name starts with "remove"; abstract, it has no body.
    static MethodBuilder Accessor (
        TypeBuilder type,
        string name,
        Type t,
        FieldBuilder field = null,
        bool isStatic = false,
        bool isAbstract = false,
        bool isPrivate = false)
    {
        MethodAttributes attributes =
            (isPrivate ? MethodAttributes.Private : MethodAttributes.Public)
            | MethodAttributes.SpecialName
            | MethodAttributes.HideBySig
            | (isStatic ? MethodAttributes.Static : 0)
            | (isAbstract ? MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.NewSlot : 0);
        MethodBuilder method = type.DefineMethod (name, attributes, typeof (void), new[] { t });

        if (isAbstract)
        {
            return method;
        }

        ILGenerator il = method.GetILGenerator ();

        if (field != null)
        {
            if (!isStatic)
            {
                il.Emit (OpCodes.Ldarg_0);
                il.Emit (OpCodes.Ldarg_0);
                il.Emit (OpCodes.Ldfld, field);
            }
            else
            {
                il.Emit (OpCodes.Ldsfld, field);
            }

            il.Emit (isStatic ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1);
            il.Emit (
                OpCodes.Call,
                typeof (Delegate).GetMethod (name.StartsWith ("remove") ? "Remove" : "Combine", new[] { typeof (Delegate), typeof (Delegate) }));
            il.Emit (OpCodes.Castclass, t);
            il.Emit (isStatic ? OpCodes.Stsfld : OpCodes.Stfld, field);
        }

        il.Emit (OpCodes.Ret);
        return method;
    }

    static MethodBuilder Plain (TypeBuilder type, string name)
    {
        MethodBuilder method = type.DefineMethod (name, MethodAttributes.Public | MethodAttributes.HideBySig, typeof (void), Type.EmptyTypes);
        method.GetILGenerator ().Emit (OpCodes.Ret);
        return method;
    }

    static string Name (MethodInfo method) => method == null ? "null" : method.DeclaringType.Name + "." + method.Name;

    /// An event's accessors, as `add/remove/raise/others`.
    static string Accessors (EventInfo e) =>
        Name (e.GetAddMethod (true)) + "/" + Name (e.GetRemoveMethod (true)) + "/" + Name (e.GetRaiseMethod (true)) + "/"
        + string.Join (",", e.GetOtherMethods (true).Select (Name));

    static string Listed (EventInfo[] events) =>
        string.Join (",", events.Select (e => e.Name + "@" + e.MetadataToken.ToString ("X8")));

    /// Whether `action` throws an exception of exactly type `T`, naming parameter `parameter` if one is given.
    static bool Throws<T> (Action action, string parameter = null)
        where T : Exception
    {
        try
        {
            action ();
            return false;
        }
        catch (Exception e)
        {
            return e.GetType () == typeof (T) && (parameter == null || ((ArgumentException) e).ParamName == parameter);
        }
    }

    /// `EventHandlerType`, or the name of the exception it throws.
    static string HandlerType (EventInfo e)
    {
        try
        {
            Type t = e.EventHandlerType;
            return t == null ? "null" : t.ToString ();
        }
        catch (Exception x)
        {
            return x.GetType ().Name;
        }
    }

    static int Refusals ()
    {
        ModuleBuilder module = NewModule ("Ref");
        TypeBuilder a = module.DefineType ("A", TypeAttributes.Public);
        TypeBuilder g = module.DefineType ("G", TypeAttributes.Public);
        GenericTypeParameterBuilder t = g.DefineGenericParameters ("T")[0];

        EventBuilder e0 = a.DefineEvent ("E0", EventAttributes.None, typeof (Action));

        if (Token (e0) != 0x14000001 || module.GetTypeMetadataToken (typeof (Func<sbyte>)) != 0x1B000001)
        {
            return 1;
        }

        // The name is checked first, then the type, all before the QCall.
        if (!Throws<ArgumentNullException> (() => a.DefineEvent (null, EventAttributes.None, typeof (Action)), "name"))
        {
            return 2;
        }

        if (!Throws<ArgumentException> (() => a.DefineEvent ("", EventAttributes.None, typeof (Action)), "name"))
        {
            return 3;
        }

        if (!Throws<ArgumentException> (() => a.DefineEvent ("\0x", EventAttributes.None, typeof (Action)), "name")
            || !Throws<ArgumentException> (() => a.DefineEvent ("\0", EventAttributes.None, typeof (Action)), "name"))
        {
            return 4;
        }

        if (!Throws<ArgumentNullException> (() => a.DefineEvent ("NullType", EventAttributes.None, null), "type"))
        {
            return 5;
        }

        if (!Throws<ArgumentNullException> (() => a.DefineEvent (null, EventAttributes.None, null), "name")
            || !Throws<ArgumentException> (() => a.DefineEvent ("", EventAttributes.None, null), "name"))
        {
            return 6;
        }

        // None of the seven consumed a token; the QCall reads a name to its first NUL.
        EventBuilder e1 = a.DefineEvent ("E1", EventAttributes.None, typeof (Action));
        EventBuilder embedded = a.DefineEvent ("a\0b", EventAttributes.None, typeof (Action));

        if (Token (e1) != 0x14000002 || Token (embedded) != 0x14000003)
        {
            return 7;
        }

        // Each of these event types is a TypeSpec, added before the QCall.
        EventBuilder byRef = a.DefineEvent ("ByRef", EventAttributes.None, typeof (int).MakeByRefType ());

        if (Token (byRef) != 0x14000004 || module.GetTypeMetadataToken (typeof (Func<short>)) != 0x1B000003)
        {
            return 8;
        }

        EventBuilder pointer = a.DefineEvent ("Ptr", EventAttributes.None, typeof (int).MakePointerType ());

        if (Token (pointer) != 0x14000005 || module.GetTypeMetadataToken (typeof (Func<ushort>)) != 0x1B000005)
        {
            return 9;
        }

        EventBuilder array = a.DefineEvent ("Arr", EventAttributes.None, typeof (int[]));

        if (Token (array) != 0x14000006 || module.GetTypeMetadataToken (typeof (Func<uint>)) != 0x1B000007)
        {
            return 10;
        }

        EventBuilder open = a.DefineEvent ("Open", EventAttributes.None, typeof (Action<>));

        if (Token (open) != 0x14000007
            || module.GetTypeMetadataToken (typeof (Func<ulong>)) != 0x1B000009
            || module.GetTypeMetadataToken (typeof (Action<>)) != 0x01000004)
        {
            return 11;
        }

        // `Action<int>` twice shares one TypeSpec.
        EventBuilder ai1 = a.DefineEvent ("AI1", EventAttributes.None, typeof (Action<int>));
        EventBuilder ai2 = a.DefineEvent ("AI2", EventAttributes.None, typeof (Action<int>));

        if (Token (ai1) != 0x14000008
            || Token (ai2) != 0x14000009
            || module.GetTypeMetadataToken (typeof (Func<float>)) != 0x1B00000B
            || module.GetTypeMetadataToken (typeof (Action<int>)) != 0x1B00000A)
        {
            return 12;
        }

        // A type parameter and a generic TypeBuilder are TypeSpecs; a TypeBuilder is its TypeDef.
        EventBuilder ofT = g.DefineEvent ("OfT", EventAttributes.None, t);

        if (Token (ofT) != 0x1400000A || module.GetTypeMetadataToken (typeof (Func<double>)) != 0x1B00000D)
        {
            return 13;
        }

        EventBuilder ofA = a.DefineEvent ("OfA", EventAttributes.None, a);

        if (Token (ofA) != 0x1400000B || module.GetTypeMetadataToken (typeof (Func<char>)) != 0x1B00000E)
        {
            return 14;
        }

        EventBuilder ofG = a.DefineEvent ("OfG", EventAttributes.None, g);

        if (Token (ofG) != 0x1400000C || module.GetTypeMetadataToken (typeof (Func<bool>)) != 0x1B000010)
        {
            return 15;
        }

        // A TypeRef the module already has is used again.
        if (module.GetTypeMetadataToken (typeof (EventHandler)) != 0x01000005)
        {
            return 16;
        }

        EventBuilder eh = a.DefineEvent ("EH", EventAttributes.None, typeof (EventHandler));

        if (Token (eh) != 0x1400000D
            || module.GetTypeMetadataToken (typeof (EventHandler)) != 0x01000005
            || module.GetTypeMetadataToken (typeof (Action)) != 0x01000002)
        {
            return 17;
        }

        // The emitter does not look for an existing row.
        EventBuilder duplicate = a.DefineEvent ("E1", EventAttributes.None, typeof (Action));

        if (Token (duplicate) != 0x1400000E)
        {
            return 18;
        }

        EventBuilder acc = a.DefineEvent ("Acc", EventAttributes.None, typeof (Action));

        if (!Throws<ArgumentNullException> (() => acc.SetAddOnMethod (null), "mdBuilder")
            || !Throws<ArgumentNullException> (() => acc.AddOtherMethod (null), "mdBuilder"))
        {
            return 19;
        }

        MethodBuilder o = Plain (a, "o");
        acc.AddOtherMethod (o);
        acc.AddOtherMethod (o);
        acc.SetAddOnMethod (Accessor (a, "add_Acc", typeof (Action)));
        Type created = a.CreateType ();

        // Reflection lists the first of the two `E1`s.
        if (Listed (created.GetEvents (All))
            != "E0@14000001,E1@14000002,a@14000003,ByRef@14000004,Ptr@14000005,Arr@14000006,Open@14000007,AI1@14000008,AI2@14000009,OfA@1400000B,OfG@1400000C,EH@1400000D,Acc@1400000F")
        {
            return 20;
        }

        EventInfo accInfo = created.GetEvent ("Acc", All);

        if (Accessors (accInfo) != "A.add_Acc/null/null/A.o,A.o" || HandlerType (accInfo) != "System.Action")
        {
            return 21;
        }

        EventInfo e0Info = created.GetEvent ("E0", All);

        if (HandlerType (e0Info) != "NullReferenceException" || e0Info.Attributes != EventAttributes.None)
        {
            return 22;
        }

        // Once the type is created: the null check first, then `ThrowIfCreated`.
        if (!Throws<ArgumentNullException> (() => acc.SetAddOnMethod (null), "mdBuilder")
            || !Throws<InvalidOperationException> (() => acc.SetAddOnMethod (o)))
        {
            return 23;
        }

        // The name's checks precede `ThrowIfCreated`, which precedes the type's.
        if (!Throws<ArgumentNullException> (() => a.DefineEvent (null, EventAttributes.None, typeof (Action)), "name")
            || !Throws<ArgumentException> (() => a.DefineEvent ("", EventAttributes.None, typeof (Action)), "name")
            || !Throws<ArgumentException> (() => a.DefineEvent ("\0x", EventAttributes.None, typeof (Action)), "name")
            || !Throws<InvalidOperationException> (() => a.DefineEvent ("X", EventAttributes.None, null))
            || !Throws<InvalidOperationException> (() => a.DefineEvent ("X", EventAttributes.None, typeof (Action))))
        {
            return 24;
        }

        // None of those consumed a token either.
        TypeBuilder b = module.DefineType ("B", TypeAttributes.Public);

        if (Token (b.DefineEvent ("F", EventAttributes.None, typeof (Action))) != 0x14000010)
        {
            return 25;
        }

        return 0;
    }

    static int Flags ()
    {
        ModuleBuilder module = NewModule ("Flags");
        TypeBuilder t = module.DefineType ("T", TypeAttributes.Public);
        int[] values = Enumerable.Range (0, 32).Select (i => 1 << i)
            .Concat (new[] { 0, 0x0600, 0xFFFF, 0xFFFE, 0xFBFF, 0x7FFF0600, -1, -2, 0x1FFFF })
            .ToArray ();

        EventBuilder[] builders = values.Select ((v, i) => t.DefineEvent ("F" + i, (EventAttributes) v, typeof (Action))).ToArray ();

        for (int i = 0; i < builders.Length; i++)
        {
            if (Token (builders[i]) != 0x14000001 + i)
            {
                return 26;
            }
        }

        EventInfo[] events = t.CreateType ().GetEvents (All);

        if (events.Length != values.Length)
        {
            return 27;
        }

        // 0 for -1; otherwise the low sixteen bits without `RTSpecialName`.
        for (int i = 0; i < values.Length; i++)
        {
            int expected = values[i] == -1 ? 0 : values[i] & 0xFBFF;
            EventInfo e = events[i];

            if (e.Name != "F" + i || (int) e.Attributes != expected || e.IsSpecialName != ((expected & 0x0200) != 0))
            {
                return 28;
            }
        }

        return 0;
    }

    static int Interleaved ()
    {
        ModuleBuilder module = NewModule ("Inter");
        TypeBuilder a = module.DefineType ("A", TypeAttributes.Public);
        TypeBuilder b = module.DefineType ("B", TypeAttributes.Public);
        TypeBuilder c = module.DefineType ("C", TypeAttributes.Public);
        TypeBuilder d = module.DefineType ("D", TypeAttributes.Public);

        int Define (TypeBuilder type, string name) => Token (type.DefineEvent (name, EventAttributes.None, typeof (Action)));

        int[] first = { Define (b, "Zed"), Define (a, "E1"), Define (c, "G1"), Define (a, "Alpha"), Define (b, "F2"), Define (a, "E3") };
        Type bCreated = b.CreateType ();
        int[] afterB = { Define (c, "G2"), Define (a, "E4") };
        TypeBuilder e = module.DefineType ("E", TypeAttributes.Public);
        int[] onE = { Define (e, "H1"), Define (c, "G3"), Define (e, "H2") };

        if (!first.Concat (afterB).Concat (onE).SequenceEqual (Enumerable.Range (0x14000001, 11)))
        {
            return 29;
        }

        Type cCreated = c.CreateType ();
        Type aCreated = a.CreateType ();
        Type dCreated = d.CreateType ();
        Type eCreated = e.CreateType ();

        if (Listed (aCreated.GetEvents (All)) != "E1@14000002,Alpha@14000004,E3@14000006,E4@14000008"
            || Listed (bCreated.GetEvents (All)) != "Zed@14000001,F2@14000005"
            || Listed (cCreated.GetEvents (All)) != "G1@14000003,G2@14000007,G3@1400000A"
            || Listed (dCreated.GetEvents (All)) != ""
            || Listed (eCreated.GetEvents (All)) != "H1@14000009,H2@1400000B")
        {
            return 30;
        }

        return 0;
    }

    /// A module whose type has a property `P` and an event `E`, both RID 1, given eleven accessors in turn
    /// after `fillers` other properties' getters. Returns `P`'s accessors and `E`'s.
    static string Semantics (string name, int fillers)
    {
        ModuleBuilder module = NewModule (name);
        TypeBuilder t = module.DefineType ("T", TypeAttributes.Public);
        PropertyBuilder p = t.DefineProperty ("P", PropertyAttributes.None, typeof (int), Type.EmptyTypes);
        EventBuilder e = t.DefineEvent ("E", EventAttributes.None, typeof (Action));

        MethodBuilder Getter (string n)
        {
            MethodBuilder m = t.DefineMethod (n, MethodAttributes.Public | MethodAttributes.SpecialName, typeof (int), Type.EmptyTypes);
            ILGenerator il = m.GetILGenerator ();
            il.Emit (OpCodes.Ldc_I4_0);
            il.Emit (OpCodes.Ret);
            return m;
        }

        MethodBuilder Setter (string n)
        {
            MethodBuilder m = t.DefineMethod (n, MethodAttributes.Public | MethodAttributes.SpecialName, typeof (void), new[] { typeof (int) });
            m.GetILGenerator ().Emit (OpCodes.Ret);
            return m;
        }

        for (int i = 0; i < fillers; i++)
        {
            t.DefineProperty ("Q" + i, PropertyAttributes.None, typeof (int), Type.EmptyTypes).SetGetMethod (Getter ("get_Q" + i));
        }

        p.SetGetMethod (Getter ("g1"));
        e.SetAddOnMethod (Accessor (t, "a1", typeof (Action)));
        p.AddOtherMethod (Plain (t, "po1"));
        e.SetRemoveOnMethod (Accessor (t, "r1", typeof (Action)));
        e.AddOtherMethod (Plain (t, "eo1"));
        p.SetSetMethod (Setter ("s1"));
        e.SetAddOnMethod (Accessor (t, "a2", typeof (Action)));
        p.SetGetMethod (Getter ("g2"));
        e.AddOtherMethod (Plain (t, "eo2"));
        e.SetRaiseMethod (Plain (t, "f1"));
        e.SetRaiseMethod (Plain (t, "f2"));

        if (Token (e) != 0x14000001)
        {
            return "token";
        }

        Type c = t.CreateType ();
        PropertyInfo pi = c.GetProperty ("P", All);
        return Name (pi.GetGetMethod (true)) + "/" + Name (pi.GetSetMethod (true)) + "/"
            + string.Join (",", pi.GetAccessors (true).Select (Name)) + " " + Accessors (c.GetEvent ("E", All));
    }

    static int Reflected ()
    {
        ModuleBuilder module = NewModule ("Refl");

        // A generic type's event of type `Action<T>`.
        TypeBuilder g = module.DefineType ("G", TypeAttributes.Public);
        GenericTypeParameterBuilder gt = g.DefineGenericParameters ("T")[0];
        Type actionOfT = typeof (Action<>).MakeGenericType (gt);
        EventBuilder changed = g.DefineEvent ("Changed", EventAttributes.None, actionOfT);
        changed.SetAddOnMethod (Accessor (g, "add_Changed", actionOfT));
        changed.SetRemoveOnMethod (Accessor (g, "remove_Changed", actionOfT));
        Type gCreated = g.CreateType ();
        EventInfo open = gCreated.GetEvent ("Changed", All);
        EventInfo closed = gCreated.MakeGenericType (typeof (int)).GetEvent ("Changed", All);

        if (HandlerType (open) != "System.Action`1[T]"
            || HandlerType (closed) != "System.Action`1[System.Int32]"
            || open.MetadataToken != 0x14000001
            || closed.MetadataToken != open.MetadataToken
            || Accessors (closed) != "G.add_Changed/G.remove_Changed/null/")
        {
            return 34;
        }

        // Events of every kind of accessor; `Z`'s add, and `Inst`'s, are defined before every event.
        TypeBuilder t = module.DefineType ("T", TypeAttributes.Public);
        FieldBuilder handlers = t.DefineField ("_h", typeof (Action), FieldAttributes.Public);
        FieldBuilder statics = t.DefineField ("_s", typeof (EventHandler<int>), FieldAttributes.Public | FieldAttributes.Static);
        MethodBuilder addZ = Accessor (t, "add_Z", typeof (Action));
        MethodBuilder addInst = Accessor (t, "add_Inst", typeof (Action), handlers);
        MethodBuilder removeInst = Accessor (t, "remove_Inst", typeof (Action), handlers);
        EventBuilder inst = t.DefineEvent ("Inst", EventAttributes.SpecialName, typeof (Action));
        inst.SetAddOnMethod (addInst);
        inst.SetRemoveOnMethod (removeInst);
        EventBuilder stat = t.DefineEvent ("Stat", EventAttributes.None, typeof (EventHandler<int>));
        stat.SetAddOnMethod (Accessor (t, "add_Stat", typeof (EventHandler<int>), statics, isStatic: true));
        EventBuilder priv = t.DefineEvent ("Priv", EventAttributes.None, typeof (Action));
        priv.SetAddOnMethod (Accessor (t, "add_Priv", typeof (Action), isPrivate: true));
        EventBuilder notDelegate = t.DefineEvent ("NotDelegate", EventAttributes.None, typeof (int));
        notDelegate.SetAddOnMethod (Accessor (t, "add_NotDelegate", typeof (int)));
        EventBuilder raiseOnly = t.DefineEvent ("RaiseOnly", EventAttributes.None, typeof (Action));
        raiseOnly.SetRaiseMethod (Plain (t, "raise_RaiseOnly"));
        EventBuilder z = t.DefineEvent ("Z", EventAttributes.None, typeof (Action));
        z.SetAddOnMethod (addZ);
        t.DefineDefaultConstructor (MethodAttributes.Public);
        Type tCreated = t.CreateType ();

        if (Listed (tCreated.GetEvents (All))
            != "Inst@14000002,Stat@14000003,Priv@14000004,NotDelegate@14000005,RaiseOnly@14000006,Z@14000007")
        {
            return 35;
        }

        EventInfo instInfo = tCreated.GetEvent ("Inst");

        if ((int) instInfo.Attributes != 0x0200
            || !instInfo.IsSpecialName
            || Accessors (instInfo) != "T.add_Inst/T.remove_Inst/null/"
            || HandlerType (tCreated.GetEvent ("Stat")) != "System.EventHandler`1[System.Int32]"
            || Accessors (tCreated.GetEvent ("Priv", All)) != "T.add_Priv/null/null/"
            || Accessors (tCreated.GetEvent ("RaiseOnly")) != "null/null/T.raise_RaiseOnly/")
        {
            return 36;
        }

        EventInfo notDelegateInfo = tCreated.GetEvent ("NotDelegate");

        if (HandlerType (notDelegateInfo) != "null"
            || notDelegateInfo.IsMulticast
            || HandlerType (tCreated.GetEvent ("RaiseOnly")) != "NullReferenceException")
        {
            return 37;
        }

        // A public raise method alone makes an event public.
        if (string.Join (",", tCreated.GetEvents ().Select (e => e.Name)) != "Inst,Stat,NotDelegate,RaiseOnly,Z"
            || string.Join (",", tCreated.GetEvents (BindingFlags.NonPublic | BindingFlags.Instance).Select (e => e.Name)) != "Priv"
            || string.Join (",", tCreated.GetEvents (BindingFlags.Public | BindingFlags.Static).Select (e => e.Name)) != "Stat")
        {
            return 38;
        }

        object instance = Activator.CreateInstance (tCreated);
        int hits = 0;
        Action handler = () => hits++;
        instInfo.AddEventHandler (instance, handler);
        instInfo.AddEventHandler (instance, handler);
        ((Action) tCreated.GetField ("_h").GetValue (instance)) ();
        instInfo.RemoveEventHandler (instance, handler);
        ((Action) tCreated.GetField ("_h").GetValue (instance)) ();

        if (hits != 3)
        {
            return 39;
        }

        // A struct's event, and an interface's with abstract accessors.
        TypeBuilder s = module.DefineType ("S", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof (ValueType));
        EventBuilder se = s.DefineEvent ("SE", EventAttributes.None, typeof (Action));
        se.SetAddOnMethod (Accessor (s, "add_SE", typeof (Action)));
        Type sCreated = s.CreateType ();
        TypeBuilder i = module.DefineType ("I", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        EventBuilder ie = i.DefineEvent ("IE", EventAttributes.None, typeof (EventHandler));
        ie.SetAddOnMethod (Accessor (i, "add_IE", typeof (EventHandler), isAbstract: true));
        ie.SetRemoveOnMethod (Accessor (i, "remove_IE", typeof (EventHandler), isAbstract: true));
        Type iCreated = i.CreateType ();

        if (Listed (sCreated.GetEvents (All)) != "SE@14000008"
            || Accessors (sCreated.GetEvent ("SE")) != "S.add_SE/null/null/"
            || Listed (iCreated.GetEvents (All)) != "IE@14000009"
            || Accessors (iCreated.GetEvent ("IE")) != "I.add_IE/I.remove_IE/null/"
            || HandlerType (iCreated.GetEvent ("IE")) != "System.EventHandler")
        {
            return 40;
        }

        // A dynamic derived type's event hides its dynamic base's of the same name.
        TypeBuilder dynBase = module.DefineType ("DynBase", TypeAttributes.Public);
        dynBase.DefineEvent ("BaseE", EventAttributes.None, typeof (Action)).SetAddOnMethod (Accessor (dynBase, "add_BaseE", typeof (Action)));
        dynBase.DefineDefaultConstructor (MethodAttributes.Public);
        Type dynBaseCreated = dynBase.CreateType ();
        TypeBuilder dynDerived = module.DefineType ("DynDerived", TypeAttributes.Public, dynBaseCreated);
        dynDerived.DefineEvent ("BaseE", EventAttributes.None, typeof (Action<int>))
            .SetAddOnMethod (Accessor (dynDerived, "add_BaseE2", typeof (Action<int>)));
        dynDerived.DefineEvent ("Own", EventAttributes.None, typeof (Action)).SetAddOnMethod (Accessor (dynDerived, "add_Own", typeof (Action)));
        EventInfo[] derivedEvents = dynDerived.CreateType ().GetEvents (BindingFlags.Public | BindingFlags.Instance);

        if (Listed (derivedEvents) != "BaseE@1400000B,Own@1400000C"
            || HandlerType (derivedEvents[0]) != "System.Action`1[System.Int32]"
            || Accessors (derivedEvents[0]) != "DynDerived.add_BaseE2/null/null/")
        {
            return 41;
        }

        return 0;
    }

    static int Foreign ()
    {
        // An accessor of another type of the module, created first: reflection refuses the unrelated type.
        ModuleBuilder module = NewModule ("Foreign");
        TypeBuilder fa = module.DefineType ("FA", TypeAttributes.Public);
        TypeBuilder fb = module.DefineType ("FB", TypeAttributes.Public);
        MethodBuilder fbAdd = Accessor (fb, "add_X", typeof (Action));
        fb.CreateType ();
        fa.DefineEvent ("X", EventAttributes.None, typeof (Action)).SetAddOnMethod (fbAdd);
        Type faCreated = fa.CreateType ();

        if (!Throws<ArgumentException> (() => faCreated.GetEvents (All)) || !Throws<ArgumentException> (() => faCreated.GetEvent ("X", All)))
        {
            return 42;
        }

        // An accessor of a created base resolves.
        TypeBuilder baseType = module.DefineType ("Base", TypeAttributes.Public);
        FieldBuilder baseField = baseType.DefineField ("_h", typeof (Action), FieldAttributes.Public);
        MethodBuilder baseAdd = Accessor (baseType, "add_FromBase", typeof (Action), baseField);
        baseType.DefineDefaultConstructor (MethodAttributes.Public);
        Type baseCreated = baseType.CreateType ();
        TypeBuilder derived = module.DefineType ("Derived", TypeAttributes.Public, baseCreated);
        EventBuilder y = derived.DefineEvent ("Y", EventAttributes.None, typeof (Action));
        y.SetAddOnMethod (baseAdd);
        derived.DefineDefaultConstructor (MethodAttributes.Public);
        Type derivedCreated = derived.CreateType ();
        EventInfo yInfo = derivedCreated.GetEvent ("Y", All);

        if (Listed (derivedCreated.GetEvents (All)) != "Y@14000002" || Accessors (yInfo) != "Base.add_FromBase/null/null/")
        {
            return 43;
        }

        object instance = Activator.CreateInstance (derivedCreated);
        int hits = 0;
        yInfo.AddEventHandler (instance, new Action (() => hits++));
        ((Action) baseCreated.GetField ("_h").GetValue (instance)) ();

        if (hits != 1)
        {
            return 44;
        }

        return 0;
    }

    static int OtherModule ()
    {
        // An add method that is a MethodBuilder of module Y: its token, 0x06000001, names X's own add_Own.
        ModuleBuilder yModule = NewModule ("Y");
        TypeBuilder ty = yModule.DefineType ("TY", TypeAttributes.Public);
        MethodBuilder y0 = Accessor (ty, "y0", typeof (Action));
        ModuleBuilder xModule = NewModule ("X");
        TypeBuilder tx = xModule.DefineType ("TX", TypeAttributes.Public);
        MethodBuilder own = Accessor (tx, "add_Own", typeof (Action));
        EventBuilder wild = tx.DefineEvent ("Wild", EventAttributes.None, typeof (Action));

        if (y0.MetadataToken != 0x06000001 || own.MetadataToken != 0x06000001)
        {
            return 45;
        }

        wild.SetAddOnMethod (y0);
        EventInfo[] events = tx.CreateType ().GetEvents (All);

        if (Listed (events) != "Wild@14000001" || Accessors (events[0]) != "TX.add_Own/null/null/")
        {
            return 46;
        }

        return 0;
    }

    static int Weird ()
    {
        // Reflection takes the handler type from the add method, not from the row's event type.
        ModuleBuilder module = NewModule ("Weird");
        TypeBuilder t = module.DefineType ("T", TypeAttributes.Public);
        t.DefineEvent ("Weird", EventAttributes.None, typeof (int)).SetAddOnMethod (Accessor (t, "add_Weird", typeof (Action)));

        if (HandlerType (t.CreateType ().GetEvent ("Weird")) != "System.Action")
        {
            return 47;
        }

        return 0;
    }

    public static int Main ()
    {
        int result = Refusals ();

        if (result != 0)
        {
            return result;
        }

        result = Flags ();

        if (result != 0)
        {
            return result;
        }

        result = Interleaved ();

        if (result != 0)
        {
            return result;
        }

        // Below 25 MethodSemantics rows each association's rows in the order added, the last of a semantics
        // kept; from 25, in reverse. Property and event rows count together.
        const string fewer = "T.g2/T.s1/T.g2,T.s1,T.po1 T.a2/T.r1/T.f2/T.eo1,T.eo2";

        if (Semantics ("S11", 0) != fewer)
        {
            return 31;
        }

        if (Semantics ("S24", 13) != fewer)
        {
            return 32;
        }

        if (Semantics ("S25", 14) != "T.g1/T.s1/T.g1,T.s1,T.po1 T.a1/T.r1/T.f1/T.eo2,T.eo1")
        {
            return 33;
        }

        result = Reflected ();

        if (result != 0)
        {
            return result;
        }

        result = Foreign ();

        if (result != 0)
        {
            return result;
        }

        result = OtherModule ();

        if (result != 0)
        {
            return result;
        }

        return Weird ();
    }
}
