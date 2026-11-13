using System;
using System.Reflection;
using System.Reflection.Emit;

// A dynamic module's references to methods of an instantiation of its own generic type `C<T>`: a call to
// `C<int>.M<long>` and one to `C<int>.Id`, emitted once through `TypeBuilder.GetMethod` before `C` is created and once
// through the created `C<int>`'s `MethodInfo`s after. Both times `GetMemberRefToken` names the method by a MemberRef
// under the TypeSpec for `C<int>`, which it finds or adds, with the generic definition's signature, and the
// instantiation of `M` by a MethodSpec under that MemberRef; the second time each is found again, so both bodies hold
// the same tokens, which the test's terminal-state check reads. Both bodies run. Measured on .NET 10 to exit 0.
//
// The references are resolved only once `C` is created: before then, .NET 10 throws `TypeLoadException` resolving them.
//
// Returns 0 on success, or the number of the first check that failed.
public static class Program
{
    static MethodBuilder Static (TypeBuilder type, string name, Type returnType, params Type[] parameters) =>
        type.DefineMethod (name, MethodAttributes.Public | MethodAttributes.Static, returnType, parameters);

    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Dyn"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("Dyn");

        // TypeDef 2: `static class C<T>` with `static U M<U> (T t, U u) => u` (MethodDef 1) and `static T Id (T t) => t`
        // (MethodDef 2).
        TypeBuilder c = module.DefineType ("C", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        GenericTypeParameterBuilder t = c.DefineGenericParameters ("T")[0];
        MethodBuilder m = c.DefineMethod ("M", MethodAttributes.Public | MethodAttributes.Static);
        GenericTypeParameterBuilder u = m.DefineGenericParameters ("U")[0];
        m.SetReturnType (u);
        m.SetParameters (t, u);
        ILGenerator mIl = m.GetILGenerator ();
        mIl.Emit (OpCodes.Ldarg_1);
        mIl.Emit (OpCodes.Ret);
        MethodBuilder id = Static (c, "Id", t, t);
        ILGenerator idIl = id.GetILGenerator ();
        idIl.Emit (OpCodes.Ldarg_0);
        idIl.Emit (OpCodes.Ret);

        // TypeDef 3: `static class User`, whose `Before` (MethodDef 3) calls `C<int>.M<long>` and `C<int>.Id` through
        // `TypeBuilder.GetMethod`, before `C` is created. The TypeSpec `C<int>` is TypeSpec 1; `M` is MemberRef 1
        // under it, and `M<long>` MethodSpec 1 under that; `Id` is MemberRef 2 under TypeSpec 1.
        Type builderOfInt = c.MakeGenericType (typeof (int));
        TypeBuilder user = module.DefineType ("User", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        MethodBuilder before = Static (user, "Before", typeof (int));
        ILGenerator beforeIl = before.GetILGenerator ();
        beforeIl.Emit (OpCodes.Ldc_I4, 1);
        beforeIl.Emit (OpCodes.Ldc_I8, 20L);
        beforeIl.Emit (OpCodes.Call, TypeBuilder.GetMethod (builderOfInt, m).MakeGenericMethod (typeof (long)));
        beforeIl.Emit (OpCodes.Conv_I4);
        beforeIl.Emit (OpCodes.Ldc_I4, 3);
        beforeIl.Emit (OpCodes.Call, TypeBuilder.GetMethod (builderOfInt, id));
        beforeIl.Emit (OpCodes.Add);
        beforeIl.Emit (OpCodes.Ret);

        if (c.MetadataToken != 0x02000002 || user.MetadataToken != 0x02000003)
        {
            return 1;
        }

        Type created = c.CreateType ()!;
        Type createdUser = user.CreateType ()!;

        if (m.MetadataToken != 0x06000001 || id.MetadataToken != 0x06000002 || before.MetadataToken != 0x06000003)
        {
            return 2;
        }

        // TypeDef 4: `static class After`, whose `A` (MethodDef 4) makes the same calls through the created `C<int>`'s
        // `MethodInfo`s, each named by the row `Before` added.
        Type createdOfInt = created.MakeGenericType (typeof (int));
        MethodInfo createdM = createdOfInt.GetMethod ("M")!;
        MethodInfo createdId = createdOfInt.GetMethod ("Id")!;
        TypeBuilder after = module.DefineType ("After", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        MethodBuilder a = Static (after, "A", typeof (int));
        ILGenerator aIl = a.GetILGenerator ();
        aIl.Emit (OpCodes.Ldc_I4, 1);
        aIl.Emit (OpCodes.Ldc_I8, 30L);
        aIl.Emit (OpCodes.Call, createdM.MakeGenericMethod (typeof (long)));
        aIl.Emit (OpCodes.Conv_I4);
        aIl.Emit (OpCodes.Ldc_I4, 4);
        aIl.Emit (OpCodes.Call, createdId);
        aIl.Emit (OpCodes.Add);
        aIl.Emit (OpCodes.Ret);
        Type createdAfter = after.CreateType ()!;

        if (after.MetadataToken != 0x02000004 || a.MetadataToken != 0x06000004)
        {
            return 3;
        }

        if ((int) createdUser.GetMethod ("Before")!.Invoke (null, null)! != 20 + 3)
        {
            return 4;
        }

        if ((int) createdAfter.GetMethod ("A")!.Invoke (null, null)! != 30 + 4)
        {
            return 5;
        }

        // Each row, resolved by its number now that `C` is created. Resolution cannot tell which body named which
        // row, nor read a MemberRef's parent, which the terminal-state check reads.
        Module resolving = created.Module;

        if (resolving.ResolveType (0x1B000001) != createdOfInt)
        {
            return 6;
        }

        if (resolving.ResolveMethod (0x0A000001) != createdM || resolving.ResolveMethod (0x0A000002) != createdId)
        {
            return 7;
        }

        if (resolving.ResolveMethod (0x2B000001) != createdM.MakeGenericMethod (typeof (long)))
        {
            return 8;
        }

        return 0;
    }
}
