using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;

// `ModuleBuilder_GetMemberRefOfFieldInfo`, through `ILGenerator.Emit` of `ldsfld` and through
// `GetFieldMetadataToken`: a field of another module is a MemberRef whose signature is the field's,
// translated into the dynamic module (`TranslateSigWithScope`); and a field of a generic instantiation is
// a member of its TypeSpec. Measured on .NET 10 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Dyn"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("Dyn");

        // `object` is TypeRef 1, `T`'s parent.
        TypeBuilder t = module.DefineType (
            "T",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed);

        // static int Run () => string.Empty.Length + Type.EmptyTypes.Length + 7;
        MethodBuilder run = t.DefineMethod ("Run", MethodAttributes.Public | MethodAttributes.Static, typeof (int), Type.EmptyTypes);
        ILGenerator il = run.GetILGenerator ();
        il.Emit (OpCodes.Ldsfld, typeof (string).GetField (nameof (string.Empty))!);
        il.Emit (OpCodes.Callvirt, typeof (string).GetProperty (nameof (string.Length))!.GetGetMethod ()!);
        il.Emit (OpCodes.Ldsfld, typeof (Type).GetField (nameof (Type.EmptyTypes))!);
        il.Emit (OpCodes.Ldlen);
        il.Emit (OpCodes.Add);
        il.Emit (OpCodes.Ldc_I4_7);
        il.Emit (OpCodes.Add);
        il.Emit (OpCodes.Ret);

        // `String.Empty` (MemberRef 1), `get_Length` (2) and `Type.EmptyTypes` (3), whose `Type[]` names
        // the parent's TypeRef.
        if (module.GetFieldMetadataToken (typeof (string).GetField (nameof (string.Empty))!) != 0x0A000001)
        {
            return 1;
        }

        if (module.GetFieldMetadataToken (typeof (Type).GetField (nameof (Type.EmptyTypes))!) != 0x0A000003)
        {
            return 2;
        }

        // `TimeSpan`, through a copy of System.Text.RegularExpressions' reference to System.Runtime.
        if (module.GetFieldMetadataToken (typeof (Regex).GetField (nameof (Regex.InfiniteMatchTimeout))!) != 0x0A000004)
        {
            return 3;
        }

        // A member of the instantiation's TypeSpec, whose signature is the definition's `!0`.
        if (module.GetFieldMetadataToken (typeof (ValueTuple<int, int>).GetField (nameof (ValueTuple<int, int>.Item1))!) != 0x0A000005)
        {
            return 4;
        }

        Type created = t.CreateType ()!;

        if ((int) created.GetMethod ("Run")!.Invoke (null, null)! != 7)
        {
            return 5;
        }

        return 0;
    }
}
