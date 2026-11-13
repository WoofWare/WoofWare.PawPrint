using System;
using System.Collections.Generic;
using System.Reflection;

// `DispatchProxy.Create` over an interface with properties: `System.Reflection.DispatchProxy`, the
// framework's own consumer of `TypeBuilder`, emits a type implementing `ISettings`, defining a
// property for each of the interface's, whose accessors forward to `SettingsProxy.Invoke`. The proxy keeps each property's
// value under the name its accessor's `MethodInfo` gives, so a value written through the proxy is
// read back through it. Measured on .NET 10 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public interface ISettings
{
    int Count { get; set; }
    string Name { get; }
}

public class SettingsProxy : DispatchProxy
{
    private readonly Dictionary<string, object> values = new Dictionary<string, object> ();

    protected override object Invoke (MethodInfo targetMethod, object[] args)
    {
        if (targetMethod.DeclaringType != typeof (ISettings) || !targetMethod.IsSpecialName)
        {
            throw new InvalidOperationException ("unexpected method " + targetMethod);
        }

        string name = targetMethod.Name;

        if (name.StartsWith ("set_", StringComparison.Ordinal))
        {
            if (targetMethod.ReturnType != typeof (void) || args.Length != 1)
            {
                throw new InvalidOperationException ("unexpected setter shape " + targetMethod);
            }

            values[name.Substring (4)] = args[0];
            return null;
        }

        if (name.StartsWith ("get_", StringComparison.Ordinal))
        {
            if (args.Length != 0)
            {
                throw new InvalidOperationException ("unexpected getter shape " + targetMethod);
            }

            string property = name.Substring (4);

            if (values.TryGetValue (property, out object value))
            {
                return value;
            }

            if (targetMethod.ReturnType == typeof (string))
            {
                return "default:" + property;
            }

            return 0;
        }

        throw new InvalidOperationException ("unexpected accessor " + name);
    }
}

public static class Program
{
    public static int Main ()
    {
        ISettings settings = DispatchProxy.Create<ISettings, SettingsProxy> ();

        if (settings.Count != 0)
        {
            return 1;
        }

        settings.Count = 5;

        if (settings.Count != 5)
        {
            return 2;
        }

        settings.Count = settings.Count * 3 + 1;

        if (settings.Count != 16)
        {
            return 3;
        }

        if (settings.Name != "default:Name")
        {
            return 4;
        }

        // A second proxy keeps its own values.
        ISettings other = DispatchProxy.Create<ISettings, SettingsProxy> ();

        if (other.Count != 0)
        {
            return 5;
        }

        if (settings.GetType () != other.GetType ())
        {
            return 6;
        }

        return 0;
    }
}
