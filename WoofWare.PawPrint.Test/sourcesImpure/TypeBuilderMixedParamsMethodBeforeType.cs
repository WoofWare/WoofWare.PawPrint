using System;
using System.Reflection;
using System.Reflection.Emit;

// A generic method of a generic type, `C<T>.M<U>`, given its token before `CreateType` by asking for it:
// that creates the method's parameter `U` there and then, and `CreateType` the type's parameter `T`
// after it. `First`'s two methods and default constructor are MethodDefs 1 to 3, so `M` is MethodDef 4
// and `C` TypeDef 3, and the GenericParam rows, numbered in the order they are created, are out of owner
// order (`T`'s owner sorts before `U`'s). Measured on .NET 10 to exit 0.
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

        // TypeDef 3: `static class C<T>`.
        TypeBuilder c = module.DefineType ("C", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        GenericTypeParameterBuilder t = c.DefineGenericParameters ("T")[0];

        // MethodDef 4: `static object[] M<U> (T t, U u) => new object[] { t, u }`.
        MethodBuilder m = c.DefineMethod ("M", MethodAttributes.Public | MethodAttributes.Static);
        GenericTypeParameterBuilder u = m.DefineGenericParameters ("U")[0];
        m.SetReturnType (typeof (object[]));
        m.SetParameters (t, u);
        ILGenerator il = m.GetILGenerator ();
        il.Emit (OpCodes.Ldc_I4_2);
        il.Emit (OpCodes.Newarr, typeof (object));
        il.Emit (OpCodes.Dup);
        il.Emit (OpCodes.Ldc_I4_0);
        il.Emit (OpCodes.Ldarg_0);
        il.Emit (OpCodes.Box, t);
        il.Emit (OpCodes.Stelem_Ref);
        il.Emit (OpCodes.Dup);
        il.Emit (OpCodes.Ldc_I4_1);
        il.Emit (OpCodes.Ldarg_1);
        il.Emit (OpCodes.Box, u);
        il.Emit (OpCodes.Stelem_Ref);
        il.Emit (OpCodes.Ret);

        if (m.MetadataToken != 0x06000004)
        {
            return 1;
        }

        // Asking for `M`'s token created `U`; `T` waits for `CreateType`.
        if (u.MetadataToken != 0x2A000001 || t.MetadataToken != 0)
        {
            return 2;
        }

        Type created = c.CreateType ();

        if (c.MetadataToken != 0x02000003 || t.MetadataToken != 0x2A000002)
        {
            return 3;
        }

        Type createdT = created.GetGenericArguments ()[0];

        if (createdT.MetadataToken != 0x2A000002 || createdT.Name != "T" || createdT.GenericParameterPosition != 0
            || createdT.DeclaringMethod != null || createdT.DeclaringType != created)
        {
            return 4;
        }

        MethodInfo createdM = created.GetMethod ("M");
        Type createdU = createdM.GetGenericArguments ()[0];

        if (createdU.MetadataToken != 0x2A000001 || createdU.Name != "U" || createdU.GenericParameterPosition != 0
            || createdU.DeclaringMethod == null || createdU.DeclaringMethod.MetadataToken != 0x06000004)
        {
            return 5;
        }

        object[] pair = (object[]) created.MakeGenericType (typeof (int)).GetMethod ("M").MakeGenericMethod (typeof (string)).Invoke (null, new object[] { 5, "u" });

        if (pair.Length != 2 || (int) pair[0] != 5 || (string) pair[1] != "u")
        {
            return 6;
        }

        return 0;
    }
}
