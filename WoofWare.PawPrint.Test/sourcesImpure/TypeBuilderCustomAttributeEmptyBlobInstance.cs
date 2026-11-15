using System;
using System.Reflection;
using System.Reflection.Emit;

// An emitted attribute row whose blob is empty, which CoreCLR's emitter stores as given, instantiated through
// `GetCustomAttributes`: refused with `CustomAttributeFormatException`. Measured on .NET 10 and 11 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

[AttributeUsage (AttributeTargets.All)]
public class TagAttribute : Attribute
{
    public TagAttribute (string s) { S = s; }
    public string S;
}

public static class Program
{
    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("EmptyBlob"), AssemblyBuilderAccess.Run);
        TypeBuilder t = assembly.DefineDynamicModule ("EmptyBlob").DefineType ("T", TypeAttributes.Public);
        t.SetCustomAttribute (typeof (TagAttribute).GetConstructor (new[] { typeof (string) }), new byte[0]);
        Type tt = t.CreateType ();

        try
        {
            tt.GetCustomAttributes (false);
            return 1;
        }
        catch (Exception e)
        {
            if (e.GetType () != typeof (CustomAttributeFormatException))
            {
                return 2;
            }
        }

        return 0;
    }
}
