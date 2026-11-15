using System;
using System.Reflection;
using System.Reflection.Emit;

// An emitted attribute whose constructor takes `object`, so that its blob holds a tagged value (`ELEMENT_TYPE_OBJECT`
// followed by the value's own type), instantiated through `GetCustomAttributes`. Measured on .NET 10 and 11 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

[AttributeUsage (AttributeTargets.All)]
public class BoxAttribute : Attribute
{
    public BoxAttribute (object o) { O = o; }
    public object O;
}

public static class Program
{
    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("TaggedObject"), AssemblyBuilderAccess.Run);
        TypeBuilder t = assembly.DefineDynamicModule ("TaggedObject").DefineType ("T", TypeAttributes.Public);
        t.SetCustomAttribute (
            new CustomAttributeBuilder (typeof (BoxAttribute).GetConstructor (new[] { typeof (object) }), new object[] { 5 }));
        Type tt = t.CreateType ();

        object[] attributes = tt.GetCustomAttributes (false);
        if (attributes.Length != 1 || !(((BoxAttribute) attributes[0]).O is int value) || value != 5)
        {
            return 1;
        }

        return 0;
    }
}
