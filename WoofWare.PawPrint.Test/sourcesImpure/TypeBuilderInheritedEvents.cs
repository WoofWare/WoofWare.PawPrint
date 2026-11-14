using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

// A dynamic type deriving from a compiled class with events. `RuntimeType.GetEvents` asks
// `MetadataImport.Enum` for the dynamic module's TypeDef first, which has no Event rows, and then for
// the compiled base's. Every answer below was measured on .NET 10 and 11.
//
// Returns 0 on success, or the number of the first check that failed.

public class BaseWithEvents
{
    public event Action Pub;
    private event Action Priv;
    protected event Action Prot;
    void Touch() { Pub?.Invoke(); Priv?.Invoke(); Prot?.Invoke(); }
}

public static class Program
{
    const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    public static int Main()
    {
        AssemblyBuilder ab = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("NoEv"), AssemblyBuilderAccess.Run);
        ModuleBuilder mb = ab.DefineDynamicModule("NoEv");
        TypeBuilder tb = mb.DefineType("Plain", TypeAttributes.Public, typeof(BaseWithEvents));
        Type created = tb.CreateType();

        if (created.GetEvents(Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Length != 0) return 1;

        // The base's private event is dropped as an inherited private.
        EventInfo[] inherited = created.GetEvents(Instance);
        if (string.Join(",", inherited.Select(e => e.Name)) != "Pub,Prot") return 2;

        foreach (EventInfo e in inherited)
        {
            if (e.DeclaringType != typeof(BaseWithEvents)) return 3;
            if (e.ReflectedType != created) return 4;
            if (e.EventHandlerType != typeof(Action)) return 5;
        }

        EventInfo pub = created.GetEvent("Pub");
        if (pub == null || pub.MetadataToken != typeof(BaseWithEvents).GetEvent("Pub").MetadataToken) return 6;
        if (created.GetEvent("Priv", Instance) != null) return 7;

        // A handler added through the inherited event reaches the base's field.
        object instance = Activator.CreateInstance(created);
        int hits = 0;
        pub.AddEventHandler(instance, new Action(() => hits++));
        FieldInfo field = typeof(BaseWithEvents).GetField("Pub", BindingFlags.NonPublic | BindingFlags.Instance);
        ((Action)field.GetValue(instance))();
        if (hits != 1) return 8;

        return 0;
    }
}
