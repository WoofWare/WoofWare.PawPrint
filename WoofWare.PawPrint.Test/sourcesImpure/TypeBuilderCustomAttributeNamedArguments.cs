using System;
using System.Reflection;
using System.Reflection.Emit;

// Emitted attributes with a named field of type `Type`, one of type `int[]` and one of an enum type, instantiated through
// `GetCustomAttributes`. Measured on .NET 10 and 11 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public enum Color
{
    Red = 1,
    Green = 2,
}

[AttributeUsage (AttributeTargets.All)]
public class TagAttribute : Attribute
{
    public Type TField;
    public int[] AField;
    public Color CField;
}

public static class Program
{
    static TagAttribute Read (ModuleBuilder module, string name, string field, object value)
    {
        TypeBuilder t = module.DefineType (name, TypeAttributes.Public);
        t.SetCustomAttribute (
            new CustomAttributeBuilder (
                typeof (TagAttribute).GetConstructor (Type.EmptyTypes),
                new object[0],
                new[] { typeof (TagAttribute).GetField (field) },
                new[] { value }));
        object[] attributes = t.CreateType ().GetCustomAttributes (false);
        return attributes.Length == 1 ? (TagAttribute) attributes[0] : null;
    }

    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("NamedArguments"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("NamedArguments");

        TagAttribute typed = Read (module, "T", "TField", typeof (byte));
        if (typed == null || typed.TField != typeof (byte))
        {
            return 1;
        }

        TagAttribute array = Read (module, "A", "AField", new[] { 9 });
        if (array == null || array.AField == null || array.AField.Length != 1 || array.AField[0] != 9)
        {
            return 2;
        }

        TagAttribute enumerated = Read (module, "E", "CField", Color.Green);
        if (enumerated == null || enumerated.CField != Color.Green)
        {
            return 3;
        }

        return 0;
    }
}
