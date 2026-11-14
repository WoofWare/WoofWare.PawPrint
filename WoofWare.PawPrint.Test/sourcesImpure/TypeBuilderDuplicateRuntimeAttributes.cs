using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

// Two `InlineArray` attributes on one dynamic struct, in modules of 25 CustomAttribute rows or more. CoreCLR's type
// loader finds the attribute by name (`CommonEnumCustomAttributeByName`), and from 25 rows walks the lookup hash, so it
// meets the struct's rows newest first and takes the last added, counted when the type is created, wherever the other
// rows are. Measured on .NET 10 and 11 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

[AttributeUsage (AttributeTargets.All, AllowMultiple = true)]
public class FillerAttribute : Attribute
{
    public FillerAttribute (int n) { }
}

public static class Program
{
    static int count;

    /// The length the type loader gives a struct of one `long` with `InlineArray(first)` and then `InlineArray(second)`,
    /// with `before` and `after` attributes on another type before and after the struct's.
    static int Length (int before, int after, int first, int second)
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("D" + (count++)), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("m");
        TypeBuilder filler = module.DefineType ("Filler", TypeAttributes.Public);
        ConstructorInfo fillerCtor = typeof (FillerAttribute).GetConstructor (new[] { typeof (int) });
        for (int i = 0; i < before; i++)
        {
            filler.SetCustomAttribute (new CustomAttributeBuilder (fillerCtor, new object[] { i }));
        }

        TypeBuilder s = module.DefineType (
            "S", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof (ValueType));
        s.DefineField ("e", typeof (long), FieldAttributes.Public);
        ConstructorInfo inlineArray = typeof (InlineArrayAttribute).GetConstructor (new[] { typeof (int) });
        s.SetCustomAttribute (new CustomAttributeBuilder (inlineArray, new object[] { first }));
        s.SetCustomAttribute (new CustomAttributeBuilder (inlineArray, new object[] { second }));
        for (int i = 0; i < after; i++)
        {
            filler.SetCustomAttribute (new CustomAttributeBuilder (fillerCtor, new object[] { i }));
        }

        filler.CreateType ();
        s.CreateType ();
        TypeBuilder h = module.DefineType ("H", TypeAttributes.Public);
        MethodBuilder size = h.DefineMethod ("Size", MethodAttributes.Public | MethodAttributes.Static, typeof (int), Type.EmptyTypes);
        ILGenerator il = size.GetILGenerator ();
        il.Emit (OpCodes.Sizeof, s);
        il.Emit (OpCodes.Ret);
        return (int) h.CreateType ().GetMethod ("Size").Invoke (null, null) / 8;
    }

    public static int Main ()
    {
        // 25 rows: the last added.
        if (Length (23, 0, 2, 3) != 3)
        {
            return 1;
        }

        if (Length (23, 0, 3, 2) != 2)
        {
            return 2;
        }

        // 32 rows, the filler's after the struct's.
        if (Length (0, 30, 2, 3) != 3)
        {
            return 3;
        }

        // 24 rows: still the first added.
        if (Length (22, 0, 2, 3) != 2)
        {
            return 4;
        }

        return 0;
    }
}
