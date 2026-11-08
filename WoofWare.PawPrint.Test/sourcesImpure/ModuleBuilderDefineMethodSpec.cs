using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

// `TypeBuilder_DefineMethodSpec`, through `ILGenerator.Emit` of calls to instantiations of generic methods:
// the managed caller asks for a reference to the generic method definition, then for a MethodSpec naming
// that reference and the instantiation, which the emitter finds before it adds one. `Array.IndexOf<int>`
// and `Array.Empty<T>` are members of a non-generic type of another module, so their reference is a
// MemberRef whose parent is a TypeRef; `List<int>.ConvertAll<long>` is a member of a generic
// instantiation, so its MemberRef's parent is a TypeSpec. The emitted `Run` calls the first three, and
// `Empty<int>` twice. Measured on .NET 10 to exit 0.
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

        MethodInfo emptyDefinition = typeof (Array).GetMethod ("Empty")!;
        MethodInfo emptyInt = emptyDefinition.MakeGenericMethod (typeof (int));
        MethodInfo emptyString = emptyDefinition.MakeGenericMethod (typeof (string));
        MethodInfo indexOfInt = null!;

        foreach (MethodInfo m in typeof (Array).GetMethods ())
        {
            if (m.Name == "IndexOf" && m.IsGenericMethodDefinition && m.GetParameters ().Length == 2)
            {
                indexOfInt = m.MakeGenericMethod (typeof (int));
            }
        }

        // static int Run () { var a = new int[3]; a[2] = 9; return Array.IndexOf (a, 9) * 10 + Array.Empty<int> ().Length + Array.Empty<string> ().Length + Array.Empty<int> ().Length; }
        MethodBuilder run = t.DefineMethod ("Run", MethodAttributes.Public | MethodAttributes.Static, typeof (int), Type.EmptyTypes);
        ILGenerator il = run.GetILGenerator ();
        il.Emit (OpCodes.Ldc_I4_3);
        il.Emit (OpCodes.Newarr, typeof (int));
        il.Emit (OpCodes.Dup);
        il.Emit (OpCodes.Ldc_I4_2);
        il.Emit (OpCodes.Ldc_I4_S, (sbyte) 9);
        il.Emit (OpCodes.Stelem_I4);
        il.Emit (OpCodes.Ldc_I4_S, (sbyte) 9);
        il.Emit (OpCodes.Call, indexOfInt);
        il.Emit (OpCodes.Ldc_I4_S, (sbyte) 10);
        il.Emit (OpCodes.Mul);
        il.Emit (OpCodes.Call, emptyInt);
        il.Emit (OpCodes.Ldlen);
        il.Emit (OpCodes.Add);
        il.Emit (OpCodes.Call, emptyString);
        il.Emit (OpCodes.Ldlen);
        il.Emit (OpCodes.Add);
        il.Emit (OpCodes.Call, emptyInt);
        il.Emit (OpCodes.Ldlen);
        il.Emit (OpCodes.Add);
        il.Emit (OpCodes.Ret);

        // static object Convert (List<int> l) => l.ConvertAll<long> (null); never run.
        MethodInfo convertAll = typeof (List<int>).GetMethod ("ConvertAll")!.MakeGenericMethod (typeof (long));
        MethodBuilder convert = t.DefineMethod ("Convert", MethodAttributes.Public | MethodAttributes.Static, typeof (object), new[] { typeof (List<int>) });
        il = convert.GetILGenerator ();
        il.Emit (OpCodes.Ldarg_0);
        il.Emit (OpCodes.Ldnull);
        il.Emit (OpCodes.Callvirt, convertAll);
        il.Emit (OpCodes.Ret);

        Type type = t.CreateType ()!;

        if ((int) type.GetMethod ("Run")!.Invoke (null, null)! != 20)
        {
            return 1;
        }

        return 0;
    }
}
