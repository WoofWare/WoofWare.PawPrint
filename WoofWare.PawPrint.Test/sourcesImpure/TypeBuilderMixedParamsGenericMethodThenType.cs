using System;
using System.Reflection;
using System.Reflection.Emit;

// A generic method `G<X>` of a type `P` that is not generic, and then a generic type `Q<T>` created after
// it, whose method calls `G<T>`. `P`'s three other methods are MethodDefs 1 to 3, so `G` is MethodDef 4,
// and `Q` is TypeDef 3. `P`'s `CreateType` creates `X`, and `Q`'s creates `T`, whose owner sorts before
// `X`'s, so the GenericParam rows, numbered in the order they are created, are out of owner order.
// Measured on .NET 10 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Dyn"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("Dyn");

        // TypeDef 2: `static class P`, with MethodDefs 1 to 3, and MethodDef 4: `static X G<X> (X x) => x`.
        TypeBuilder p = module.DefineType ("P", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);

        for (int i = 1; i <= 3; i++)
        {
            p.DefineMethod ("F" + i, MethodAttributes.Public | MethodAttributes.Static).GetILGenerator ().Emit (OpCodes.Ret);
        }

        MethodBuilder g = p.DefineMethod ("G", MethodAttributes.Public | MethodAttributes.Static);
        GenericTypeParameterBuilder x = g.DefineGenericParameters ("X")[0];
        g.SetReturnType (x);
        g.SetParameters (x);
        ILGenerator gil = g.GetILGenerator ();
        gil.Emit (OpCodes.Ldarg_0);
        gil.Emit (OpCodes.Ret);

        Type createdP = p.CreateType ();

        if (p.MetadataToken != 0x02000002 || g.MetadataToken != 0x06000004 || x.MetadataToken != 0x2A000001)
        {
            return 1;
        }

        // TypeDef 3: `static class Q<T>`, with MethodDef 5: `static T Call (T t) => P.G<T> (t)`.
        TypeBuilder q = module.DefineType ("Q", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        GenericTypeParameterBuilder t = q.DefineGenericParameters ("T")[0];
        MethodBuilder call = q.DefineMethod ("Call", MethodAttributes.Public | MethodAttributes.Static);
        call.SetReturnType (t);
        call.SetParameters (t);
        ILGenerator cil = call.GetILGenerator ();
        cil.Emit (OpCodes.Ldarg_0);
        cil.Emit (OpCodes.Call, g.MakeGenericMethod (t));
        cil.Emit (OpCodes.Ret);

        Type createdQ = q.CreateType ();

        if (q.MetadataToken != 0x02000003 || call.MetadataToken != 0x06000005 || t.MetadataToken != 0x2A000002)
        {
            return 2;
        }

        Type createdX = createdP.GetMethod ("G").GetGenericArguments ()[0];

        if (createdX.MetadataToken != 0x2A000001 || createdX.Name != "X" || createdX.GenericParameterPosition != 0
            || createdX.DeclaringMethod == null || createdX.DeclaringMethod.MetadataToken != 0x06000004)
        {
            return 3;
        }

        Type createdT = createdQ.GetGenericArguments ()[0];

        if (createdT.MetadataToken != 0x2A000002 || createdT.Name != "T" || createdT.GenericParameterPosition != 0
            || createdT.DeclaringMethod != null || createdT.DeclaringType != createdQ)
        {
            return 4;
        }

        if ((string) createdQ.MakeGenericType (typeof (string)).GetMethod ("Call").Invoke (null, new object[] { "q" }) != "q")
        {
            return 5;
        }

        if ((long) createdP.GetMethod ("G").MakeGenericMethod (typeof (long)).Invoke (null, new object[] { 7L }) != 7L)
        {
            return 6;
        }

        return 0;
    }
}
