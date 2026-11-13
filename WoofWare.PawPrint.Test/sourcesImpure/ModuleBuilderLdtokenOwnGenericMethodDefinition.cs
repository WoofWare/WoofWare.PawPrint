using System;
using System.Reflection;
using System.Reflection.Emit;

// `ldtoken` of a created type's generic method definition, emitted through the created type's `MethodInfo`:
// `GetMethodTokenInternal` stops at the MemberRef whose parent is the type's TypeDef, with no MethodSpec, and the
// handle it loads names the definition `G`. Measured on .NET 10 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Dyn"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("Dyn");

        // TypeDef 2: `static class D` with `static X G<X> (X x) => x`.
        TypeBuilder d = module.DefineType ("D", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        MethodBuilder g = d.DefineMethod ("G", MethodAttributes.Public | MethodAttributes.Static);
        GenericTypeParameterBuilder x = g.DefineGenericParameters ("X")[0];
        g.SetReturnType (x);
        g.SetParameters (x);
        ILGenerator gil = g.GetILGenerator ();
        gil.Emit (OpCodes.Ldarg_0);
        gil.Emit (OpCodes.Ret);
        Type created = d.CreateType ()!;
        MethodInfo createdG = created.GetMethod ("G")!;

        TypeBuilder e = module.DefineType ("E", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        MethodBuilder token = e.DefineMethod ("Token", MethodAttributes.Public | MethodAttributes.Static, typeof (RuntimeMethodHandle), Type.EmptyTypes);
        ILGenerator il = token.GetILGenerator ();
        il.Emit (OpCodes.Ldtoken, createdG);
        il.Emit (OpCodes.Ret);
        Type createdE = e.CreateType ()!;

        // The operand is MemberRef 1, `G` under TypeDef 2.
        if (created.Module.ResolveMethod (0x0A000001) != createdG)
        {
            return 1;
        }

        RuntimeMethodHandle handle = (RuntimeMethodHandle) createdE.GetMethod ("Token")!.Invoke (null, null)!;
        MethodBase named = MethodBase.GetMethodFromHandle (handle);

        if (named != createdG || !named.IsGenericMethodDefinition)
        {
            return 2;
        }

        return 0;
    }
}
