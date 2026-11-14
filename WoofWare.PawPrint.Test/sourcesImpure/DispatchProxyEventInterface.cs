using System;
using System.Linq;
using System.Reflection;

// `DispatchProxy.Create` over an interface with an event of its own, extending an interface with a
// second. `ProxyBuilder.AddInterfaceImpl` enumerates each interface's events and defines one on the
// proxy type for each, through `TypeBuilder.DefineEvent`, with accessors forwarding to
// `EventProxy.Invoke`. One proxy only, so that nothing about constructing a second stands between this
// and the events. Measured on .NET 10 and 11 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public interface IBaseNotify
{
    event Action BaseChanged;
}

public interface INotify : IBaseNotify
{
    event EventHandler Changed;
    int Get();
}

public class EventProxy : DispatchProxy
{
    public static string Log = "";

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        Log += targetMethod.Name + ";";

        switch (targetMethod.Name)
        {
            case "Get":
                return 42;
            case "add_Changed":
            case "remove_Changed":
                if (!(args[0] is EventHandler)) throw new InvalidOperationException("unexpected handler " + args[0]);
                return null;
            case "add_BaseChanged":
            case "remove_BaseChanged":
                if (!(args[0] is Action)) throw new InvalidOperationException("unexpected handler " + args[0]);
                return null;
        }

        throw new InvalidOperationException("unexpected method " + targetMethod);
    }
}

public static class Program
{
    const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    public static int Main()
    {
        INotify proxy = DispatchProxy.Create<INotify, EventProxy>();

        EventHandler handler = (s, e) => { };
        Action baseHandler = () => { };
        proxy.Changed += handler;
        proxy.Changed -= handler;
        proxy.BaseChanged += baseHandler;
        proxy.BaseChanged -= baseHandler;

        if (proxy.Get() != 42) return 1;
        if (EventProxy.Log != "add_Changed;remove_Changed;add_BaseChanged;remove_BaseChanged;Get;") return 2;

        // The proxy type has one event for each of the interfaces' events.
        EventInfo[] events = proxy.GetType().GetEvents(Instance).OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
        if (events.Length != 2) return 3;
        if (events[0].Name != "BaseChanged" || events[1].Name != "Changed") return 4;
        if (events[0].EventHandlerType != typeof(Action) || events[1].EventHandlerType != typeof(EventHandler)) return 5;
        if (events[0].GetAddMethod(true).Name != "add_BaseChanged" || events[0].GetRemoveMethod(true).Name != "remove_BaseChanged") return 6;
        if (events[1].GetAddMethod(true).Name != "add_Changed" || events[1].GetRemoveMethod(true).Name != "remove_Changed") return 7;
        if (events.Any(e => e.DeclaringType != proxy.GetType() || e.GetRaiseMethod(true) != null)) return 8;

        return 0;
    }
}
