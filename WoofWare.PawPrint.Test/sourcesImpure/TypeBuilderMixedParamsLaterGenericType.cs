using System;
using System.Reflection;
using System.Reflection.Emit;

// A generic method of a generic type, `A<TA>.M<U>`, and then a generic type `B<TB>` created after it.
// `First`'s two methods and default constructor are MethodDefs 1 to 3, so `A` is TypeDef 3, `M`
// MethodDef 4 and `B` TypeDef 4. `A`'s `CreateType` creates `TA` and then `U`, and `B`'s creates `TB`,
// whose owner sorts before `U`'s, so the GenericParam rows, numbered in the order they are created, are
// out of owner order. Measured on .NET 10 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Dyn"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("Dyn");

        // TypeDef 2, with MethodDefs 1 and 2, and its default constructor 3.
        TypeBuilder first = module.DefineType ("First", TypeAttributes.Public);
        first.DefineMethod ("F1", MethodAttributes.Public | MethodAttributes.Static).GetILGenerator ().Emit (OpCodes.Ret);
        first.DefineMethod ("F2", MethodAttributes.Public | MethodAttributes.Static).GetILGenerator ().Emit (OpCodes.Ret);
        first.CreateType ();

        // TypeDef 3: `static class A<TA>`, with MethodDef 4: `static object[] M<U> (TA a, U u) => new object[] { a, u }`.
        TypeBuilder a = module.DefineType ("A", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        GenericTypeParameterBuilder ta = a.DefineGenericParameters ("TA")[0];
        MethodBuilder m = a.DefineMethod ("M", MethodAttributes.Public | MethodAttributes.Static);
        GenericTypeParameterBuilder u = m.DefineGenericParameters ("U")[0];
        m.SetReturnType (typeof (object[]));
        m.SetParameters (ta, u);
        ILGenerator il = m.GetILGenerator ();
        il.Emit (OpCodes.Ldc_I4_2);
        il.Emit (OpCodes.Newarr, typeof (object));
        il.Emit (OpCodes.Dup);
        il.Emit (OpCodes.Ldc_I4_0);
        il.Emit (OpCodes.Ldarg_0);
        il.Emit (OpCodes.Box, ta);
        il.Emit (OpCodes.Stelem_Ref);
        il.Emit (OpCodes.Dup);
        il.Emit (OpCodes.Ldc_I4_1);
        il.Emit (OpCodes.Ldarg_1);
        il.Emit (OpCodes.Box, u);
        il.Emit (OpCodes.Stelem_Ref);
        il.Emit (OpCodes.Ret);

        Type createdA = a.CreateType ();

        if (a.MetadataToken != 0x02000003 || m.MetadataToken != 0x06000004)
        {
            return 1;
        }

        if (ta.MetadataToken != 0x2A000001 || u.MetadataToken != 0x2A000002)
        {
            return 2;
        }

        // TypeDef 4: `static class B<TB>`, with MethodDef 5: `static TB Id (TB b) => b`.
        TypeBuilder b = module.DefineType ("B", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        GenericTypeParameterBuilder tb = b.DefineGenericParameters ("TB")[0];
        MethodBuilder id = b.DefineMethod ("Id", MethodAttributes.Public | MethodAttributes.Static);
        id.SetReturnType (tb);
        id.SetParameters (tb);
        ILGenerator idil = id.GetILGenerator ();
        idil.Emit (OpCodes.Ldarg_0);
        idil.Emit (OpCodes.Ret);

        Type createdB = b.CreateType ();

        if (b.MetadataToken != 0x02000004 || id.MetadataToken != 0x06000005 || tb.MetadataToken != 0x2A000003)
        {
            return 3;
        }

        Type createdTa = createdA.GetGenericArguments ()[0];

        if (createdTa.MetadataToken != 0x2A000001 || createdTa.Name != "TA" || createdTa.DeclaringType != createdA)
        {
            return 4;
        }

        Type createdU = createdA.GetMethod ("M").GetGenericArguments ()[0];

        if (createdU.MetadataToken != 0x2A000002 || createdU.Name != "U"
            || createdU.DeclaringMethod == null || createdU.DeclaringMethod.MetadataToken != 0x06000004)
        {
            return 5;
        }

        Type createdTb = createdB.GetGenericArguments ()[0];

        if (createdTb.MetadataToken != 0x2A000003 || createdTb.Name != "TB" || createdTb.GenericParameterPosition != 0
            || createdTb.DeclaringMethod != null || createdTb.DeclaringType != createdB)
        {
            return 6;
        }

        object[] pair = (object[]) createdA.MakeGenericType (typeof (int)).GetMethod ("M").MakeGenericMethod (typeof (string)).Invoke (null, new object[] { 5, "u" });

        if (pair.Length != 2 || (int) pair[0] != 5 || (string) pair[1] != "u")
        {
            return 7;
        }

        if ((string) createdB.MakeGenericType (typeof (string)).GetMethod ("Id").Invoke (null, new object[] { "b" }) != "b")
        {
            return 8;
        }

        return 0;
    }
}
