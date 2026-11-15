using System;
using System.Reflection;
using System.Reflection.Emit;

// An emitted attribute whose type is an instantiation of a generic attribute, `GenAttribute<int>`, instantiated through
// `GetCustomAttributes`. Measured on .NET 10 and 11 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

[AttributeUsage (AttributeTargets.All)]
public class GenAttribute<T> : Attribute
{
    public GenAttribute (int n) { N = n; }
    public int N;
}

public static class Program
{
    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("GenericInstance"), AssemblyBuilderAccess.Run);
        TypeBuilder t = assembly.DefineDynamicModule ("GenericInstance").DefineType ("T", TypeAttributes.Public);
        t.SetCustomAttribute (
            new CustomAttributeBuilder (typeof (GenAttribute<int>).GetConstructor (new[] { typeof (int) }), new object[] { 4 }));
        Type tt = t.CreateType ();

        object[] attributes = tt.GetCustomAttributes (false);
        if (attributes.Length != 1 || !(attributes[0] is GenAttribute<int> attribute) || attribute.N != 4)
        {
            return 1;
        }

        return 0;
    }
}
