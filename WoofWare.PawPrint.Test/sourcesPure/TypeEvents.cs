using System;
using System.Linq;
using System.Reflection;

// Reflection over a compiled assembly's events. `RuntimeType.GetEvents` walks the class hierarchy in
// managed code, asking three runtime primitives of each type with a token: `MetadataImport.Enum` for
// the type's Event rows, `MetadataImport.GetEventProps` for each row's name and flags, and
// `MetadataImport.GetName` for the name alone when it filters by name; each event's accessors come from
// the MethodSemantics rows. Every answer below was measured on .NET 10 and 11.
//
// Tokens are checked as relations (a type's events are consecutive and ascending in declaration order,
// and an instantiation's token is its definition's), so that a type added to this file renumbers
// nothing.
//
// Returns 0 on success, or the number of the first check that failed.

public interface INone { int M(); }

public interface IEv { event EventHandler Changed; int M(); }

// An interface lists only its own events, not those of the interfaces it extends.
public interface IEvDerived : IEv { int N(); }

[AttributeUsage(AttributeTargets.Event)]
public class MarkAttribute : Attribute
{
    public int N;
    public MarkAttribute(int n) { N = n; }
}

public class Pub
{
    public event EventHandler E1;
    private event Action E2;
    public static event EventHandler<int> S1;
    protected event Action<string> Prot;
    [Mark(7)] public event Action Marked;

    Action custom;
    public event Action Custom
    {
        add { custom += value; Log += "add;"; }
        remove { custom -= value; Log += "remove;"; }
    }

    public string Log = "";

    public void Fire()
    {
        E1?.Invoke(this, EventArgs.Empty);
        custom?.Invoke();
    }

    void Touch() { E2?.Invoke(); S1?.Invoke(null, 1); Prot?.Invoke(""); Marked?.Invoke(); }
}

public class Base
{
    public event Action BaseE;
    private event Action BasePriv;
    public event Action Shadowed;
    public static event Action BaseStatic;
    void Touch() { BaseE?.Invoke(); BasePriv?.Invoke(); Shadowed?.Invoke(); BaseStatic?.Invoke(); }
}

public class Derived : Base
{
    public event Action DerivedE;
    public new event Action<int> Shadowed;
    void Touch() { DerivedE?.Invoke(); Shadowed?.Invoke(1); }
}

public class G<T> { public event Action<T> E; void Touch() { E?.Invoke(default(T)); } }

public struct SEv { public event Action E; void Touch() { E?.Invoke(); } }

public class ExplicitImpl : IEv
{
    EventHandler h;
    event EventHandler IEv.Changed { add { h += value; } remove { h -= value; } }
    public int M() => 1;
}

public class Holder<T> where T : Derived { }

public class Holder2<U> { }

// One more event than `MetadataImport.Enum`'s sixteen-entry inline buffer holds.
public class Seventeen
{
    public event Action E00;
    public event Action E01;
    public event Action E02;
    public event Action E03;
    public event Action E04;
    public event Action E05;
    public event Action E06;
    public event Action E07;
    public event Action E08;
    public event Action E09;
    public event Action E10;
    public event Action E11;
    public event Action E12;
    public event Action E13;
    public event Action E14;
    public event Action E15;
    public event Action E16;

    void Touch()
    {
        E00?.Invoke(); E01?.Invoke(); E02?.Invoke(); E03?.Invoke(); E04?.Invoke(); E05?.Invoke();
        E06?.Invoke(); E07?.Invoke(); E08?.Invoke(); E09?.Invoke(); E10?.Invoke(); E11?.Invoke();
        E12?.Invoke(); E13?.Invoke(); E14?.Invoke(); E15?.Invoke(); E16?.Invoke();
    }
}

// `ProgressChanged` is declared by a CoreLib type and reflected through this one.
public class ProgressSub : Progress<int> { }

public static class Program
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    static string Names(EventInfo[] events) => string.Join(",", events.Select(e => e.Name));

    // Each event's token is one more than the last's, and every token names the Event table.
    static bool Consecutive(EventInfo[] events)
    {
        for (int i = 0; i < events.Length; i++)
        {
            if ((events[i].MetadataToken & unchecked((int)0xFF000000)) != 0x14000000) return false;
            if (i > 0 && events[i].MetadataToken != events[i - 1].MetadataToken + 1) return false;
        }

        return true;
    }

    static bool Plain(EventInfo e) =>
        e.Attributes == EventAttributes.None
        && !e.IsSpecialName
        && e.IsMulticast
        && e.RaiseMethod == null
        && e.GetRaiseMethod(true) == null
        && e.GetOtherMethods(true).Length == 0;

    static bool Accessors(EventInfo e, string add, string remove) =>
        e.GetAddMethod(true) != null
        && e.GetAddMethod(true).Name == add
        && e.GetRemoveMethod(true) != null
        && e.GetRemoveMethod(true).Name == remove;

    public static int Main()
    {
        // Types with no events give none: an interface of methods, `object`, an array.
        if (typeof(INone).GetEvents(All).Length != 0) return 1;
        if (typeof(object).GetEvents(All).Length != 0) return 2;
        if (typeof(int[]).GetEvents(All).Length != 0) return 3;

        // Public events only by default, the static one included, in declaration order.
        EventInfo[] pubDefault = typeof(Pub).GetEvents();
        if (Names(pubDefault) != "E1,S1,Marked,Custom") return 4;

        EventInfo[] pub = typeof(Pub).GetEvents(All);
        if (Names(pub) != "E1,E2,S1,Prot,Marked,Custom") return 5;
        if (!Consecutive(pub)) return 6;

        foreach (EventInfo e in pub)
        {
            if (!Plain(e)) return 7;
            if (e.DeclaringType != typeof(Pub) || e.ReflectedType != typeof(Pub)) return 8;
            if (!Accessors(e, "add_" + e.Name, "remove_" + e.Name)) return 9;
        }

        Type[] handlerTypes = { typeof(EventHandler), typeof(Action), typeof(EventHandler<int>), typeof(Action<string>), typeof(Action), typeof(Action) };
        for (int i = 0; i < pub.Length; i++)
        {
            if (pub[i].EventHandlerType != handlerTypes[i]) return 10;
        }

        // The name filter, which reads each event's name through `MetadataImport.GetName`.
        EventInfo e1 = typeof(Pub).GetEvent("E1");
        if (e1 == null || e1.Name != "E1" || e1.MetadataToken != pub[0].MetadataToken) return 11;
        EventInfo e1Insensitive = typeof(Pub).GetEvent("e1", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
        if (e1Insensitive == null || e1Insensitive.Name != "E1") return 12;
        if (typeof(Pub).GetEvent("E2") != null) return 13;
        EventInfo e2 = typeof(Pub).GetEvent("E2", BindingFlags.NonPublic | BindingFlags.Instance);
        if (e2 == null || e2.MetadataToken != pub[1].MetadataToken) return 14;
        if (typeof(Pub).GetEvent("Missing") != null) return 15;
        if (typeof(Pub).GetEvent("S1", BindingFlags.Public | BindingFlags.Static) == null) return 16;

        // Adding and removing a handler through reflection, and a custom accessor.
        Pub p = new Pub();
        int hits = 0;
        EventHandler handler = (s, a) => hits++;
        e1.AddEventHandler(p, handler);
        p.Fire();
        e1.RemoveEventHandler(p, handler);
        p.Fire();
        if (hits != 1) return 17;

        typeof(Pub).GetEvent("Custom").AddEventHandler(p, new Action(() => hits += 10));
        p.Fire();
        if (hits != 11 || p.Log != "add;") return 18;

        // The hierarchy: `Base.Shadowed` is hidden by name, `BasePriv` is an inherited private, and
        // `BaseStatic` is listed only under `FlattenHierarchy`.
        EventInfo[] derived = typeof(Derived).GetEvents();
        if (Names(derived) != "DerivedE,Shadowed,BaseE") return 19;
        if (derived[1].EventHandlerType != typeof(Action<int>) || derived[1].DeclaringType != typeof(Derived)) return 20;
        if (derived[2].DeclaringType != typeof(Base) || derived[2].ReflectedType != typeof(Derived)) return 21;
        if (!Consecutive(new[] { derived[0], derived[1] })) return 22;

        if (Names(typeof(Derived).GetEvents(All)) != "DerivedE,Shadowed,BaseE") return 23;

        EventInfo[] flattened = typeof(Derived).GetEvents(All | BindingFlags.FlattenHierarchy);
        if (Names(flattened) != "DerivedE,Shadowed,BaseE,BaseStatic") return 24;
        if (flattened[3].DeclaringType != typeof(Base) || flattened[3].ReflectedType != typeof(Derived)) return 25;

        if (Names(typeof(Derived).GetEvents(All | BindingFlags.DeclaredOnly)) != "DerivedE,Shadowed") return 26;

        EventInfo[] baseEvents = typeof(Base).GetEvents(All | BindingFlags.DeclaredOnly);
        if (Names(baseEvents) != "BaseE,BasePriv,Shadowed,BaseStatic") return 27;
        if (!Consecutive(baseEvents)) return 28;

        // A generic instantiation and its definition share the Event row.
        EventInfo[] gInt = typeof(G<int>).GetEvents();
        EventInfo[] gOpen = typeof(G<>).GetEvents();
        if (gInt.Length != 1 || gOpen.Length != 1) return 29;
        if (gInt[0].EventHandlerType != typeof(Action<int>)) return 30;
        Type openHandler = gOpen[0].EventHandlerType;
        if (!openHandler.IsGenericType || openHandler.GetGenericTypeDefinition() != typeof(Action<>) || !openHandler.GetGenericArguments()[0].IsGenericParameter) return 31;
        if (gInt[0].MetadataToken != gOpen[0].MetadataToken) return 32;
        if (gInt[0].DeclaringType != typeof(G<int>) || gOpen[0].DeclaringType != typeof(G<>)) return 33;

        EventInfo[] sev = typeof(SEv).GetEvents();
        if (Names(sev) != "E" || sev[0].DeclaringType != typeof(SEv)) return 34;

        // An interface's event, and its explicit implementation.
        EventInfo[] iev = typeof(IEv).GetEvents(All);
        if (Names(iev) != "Changed" || !Accessors(iev[0], "add_Changed", "remove_Changed")) return 35;
        if (iev[0].EventHandlerType != typeof(EventHandler)) return 36;
        EventInfo[] explicitImpl = typeof(ExplicitImpl).GetEvents(All);
        if (Names(explicitImpl) != "IEv.Changed") return 37;
        if (!Accessors(explicitImpl[0], "IEv.add_Changed", "IEv.remove_Changed")) return 38;
        if (typeof(IEvDerived).GetEvents(All).Length != 0) return 39;

        // A custom attribute on an event.
        EventInfo marked = typeof(Pub).GetEvent("Marked");
        object[] marks = marked.GetCustomAttributes(typeof(MarkAttribute), false);
        if (marks.Length != 1 || ((MarkAttribute)marks[0]).N != 7) return 40;
        if (marked.GetCustomAttributesData().Count != 1) return 41;

        // A generic parameter steps up to its constraint's events; an unconstrained one and an array
        // have none.
        Type t = typeof(Holder<>).GetGenericArguments()[0];
        EventInfo[] ofT = t.GetEvents(All);
        if (Names(ofT) != "DerivedE,Shadowed,BaseE") return 42;
        if (ofT.Any(e => e.ReflectedType != t)) return 43;
        if (typeof(Holder2<>).GetGenericArguments()[0].GetEvents(All).Length != 0) return 44;
        if (typeof(Derived[]).GetEvents(All).Length != 0) return 45;

        // More events than the inline buffer holds.
        EventInfo[] seventeen = typeof(Seventeen).GetEvents(All | BindingFlags.DeclaredOnly);
        if (seventeen.Length != 17) return 46;
        for (int i = 0; i < 17; i++)
        {
            if (seventeen[i].Name != "E" + i.ToString("00")) return 47;
        }
        if (!Consecutive(seventeen)) return 48;
        if (typeof(Seventeen).GetEvent("E16") == null) return 49;

        // A CoreLib type's event, reflected through a type of this assembly.
        EventInfo[] progress = typeof(ProgressSub).GetEvents();
        if (Names(progress) != "ProgressChanged") return 50;
        if (progress[0].DeclaringType != typeof(Progress<int>) || progress[0].ReflectedType != typeof(ProgressSub)) return 51;
        if (progress[0].EventHandlerType != typeof(EventHandler<int>)) return 52;
        if (!Accessors(progress[0], "add_ProgressChanged", "remove_ProgressChanged")) return 53;

        return 0;
    }
}
