using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

// `ModuleBuilder_GetMemberRefFromSignature`, through `ILGenerator.Emit` of calls to members of a generic
// instantiation: the managed caller builds the member's signature from its generic definition, takes a
// TypeSpec for the instantiation as its parent, and asks for a MemberRef, which the emitter finds before
// it adds one. The emitted method constructs a `List<int>`, adds to it and reads its count. Measured on
// .NET 10 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Dyn"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("Dyn");

        TypeBuilder t = module.DefineType (
            "T",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed);

        ConstructorInfo listCtor = typeof (List<int>).GetConstructor (Type.EmptyTypes)!;
        MethodInfo add = typeof (List<int>).GetMethod ("Add")!;
        MethodInfo count = typeof (List<int>).GetProperty ("Count")!.GetMethod!;

        // static int Fill () { var l = new List<int> (); l.Add (3); l.Add (4); return l.Count; }
        MethodBuilder fill = t.DefineMethod ("Fill", MethodAttributes.Public | MethodAttributes.Static, typeof (int), Type.EmptyTypes);
        ILGenerator il = fill.GetILGenerator ();
        il.Emit (OpCodes.Newobj, listCtor);
        il.Emit (OpCodes.Dup);
        il.Emit (OpCodes.Ldc_I4_3);
        il.Emit (OpCodes.Callvirt, add);
        il.Emit (OpCodes.Dup);
        il.Emit (OpCodes.Ldc_I4_4);
        il.Emit (OpCodes.Callvirt, add);
        il.Emit (OpCodes.Callvirt, count);
        il.Emit (OpCodes.Ret);

        // `Add` was named twice, so it is one MemberRef.
        int addToken = module.GetMethodMetadataToken (add);

        if (addToken >> 24 != 0x0A || module.GetMethodMetadataToken (add) != addToken)
        {
            return 1;
        }

        Type type = t.CreateType ()!;

        if ((int) type.GetMethod ("Fill")!.Invoke (null, null)! != 2)
        {
            return 2;
        }

        return 0;
    }
}
