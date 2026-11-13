using System;
using System.Reflection;
using System.Reflection.Emit;

// A generic `MethodBuilder`'s parameters: `TypeBuilder_DefineGenericParam` adds a method's parameters
// when the method is given its token, which emitting a call to an instantiation of it does, straight
// after its MethodDef row. Calls to its instantiations are MethodSpecs whose parent is that MethodDef,
// one for each instantiation however often it is called. Every generic parameter here belongs to a
// method, so the rows are created in owner order. Measured on .NET 10 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Dyn"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("Dyn");

        // TypeDef 2: `static class D`, so that `CreateType` adds no default constructor.
        TypeBuilder d = module.DefineType ("D", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);

        // MethodDef 1: `static int Caller () => G<int> (40) + G<int> (2) + (G<string> ("s") == null ? 1 : 0)`.
        MethodBuilder caller = d.DefineMethod ("Caller", MethodAttributes.Public | MethodAttributes.Static, typeof (int), Type.EmptyTypes);

        // MethodDef 2: `static X G<X> (X x) where X : IComparable => x`.
        MethodBuilder g = d.DefineMethod ("G", MethodAttributes.Public | MethodAttributes.Static);
        GenericTypeParameterBuilder x = g.DefineGenericParameters ("X")[0];
        x.SetInterfaceConstraints (typeof (IComparable));
        g.SetReturnType (x);
        g.SetParameters (x);
        ILGenerator gil = g.GetILGenerator ();
        gil.Emit (OpCodes.Ldarg_0);
        gil.Emit (OpCodes.Ret);

        if (x.MetadataToken != 0)
        {
            return 1;
        }

        ILGenerator il = caller.GetILGenerator ();
        il.Emit (OpCodes.Ldc_I4, 40);
        il.Emit (OpCodes.Call, g.MakeGenericMethod (typeof (int)));
        il.Emit (OpCodes.Ldc_I4, 2);
        il.Emit (OpCodes.Call, g.MakeGenericMethod (typeof (int)));
        il.Emit (OpCodes.Add);
        il.Emit (OpCodes.Ldstr, "s");
        il.Emit (OpCodes.Call, g.MakeGenericMethod (typeof (string)));
        il.Emit (OpCodes.Ldnull);
        il.Emit (OpCodes.Ceq);
        il.Emit (OpCodes.Add);
        il.Emit (OpCodes.Ret);

        // Emitting the first call gave `G` its token, and `Caller`, defined before it, its own first; `X`
        // got its row with `G`'s.
        if (x.MetadataToken != 0x2A000001)
        {
            return 2;
        }

        if (caller.MetadataToken != 0x06000001 || g.MetadataToken != 0x06000002)
        {
            return 3;
        }

        // TypeDef 3: `static class E`, with MethodDef 3: `static W Pick<V, W> (V v, W w) => w`.
        TypeBuilder e = module.DefineType ("E", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        MethodBuilder pick = e.DefineMethod ("Pick", MethodAttributes.Public | MethodAttributes.Static);
        GenericTypeParameterBuilder[] vw = pick.DefineGenericParameters ("V", "W");
        pick.SetReturnType (vw[1]);
        pick.SetParameters (vw[0], vw[1]);
        ILGenerator pil = pick.GetILGenerator ();
        pil.Emit (OpCodes.Ldarg_1);
        pil.Emit (OpCodes.Ret);

        Type createdD = d.CreateType ();

        // `Pick` is given its token by `CreateType`, and its parameters their rows then.
        if (vw[0].MetadataToken != 0)
        {
            return 4;
        }

        Type createdE = e.CreateType ();

        if (pick.MetadataToken != 0x06000003 || vw[0].MetadataToken != 0x2A000002 || vw[1].MetadataToken != 0x2A000003)
        {
            return 5;
        }

        MethodInfo createdG = createdD.GetMethod ("G");

        if (!createdG.IsGenericMethodDefinition || createdG.MetadataToken != 0x06000002)
        {
            return 6;
        }

        Type createdX = createdG.GetGenericArguments ()[0];

        if (createdX.MetadataToken != 0x2A000001 || createdX.Name != "X" || createdX.GenericParameterPosition != 0)
        {
            return 7;
        }

        if (createdX.DeclaringMethod == null || createdX.DeclaringMethod.Name != "G")
        {
            return 8;
        }

        Type[] constraints = createdX.GetGenericParameterConstraints ();

        if (constraints.Length != 1 || constraints[0] != typeof (IComparable))
        {
            return 9;
        }

        MethodInfo createdPick = createdE.GetMethod ("Pick");
        Type[] pickArguments = createdPick.GetGenericArguments ();

        if (pickArguments.Length != 2
            || pickArguments[0].MetadataToken != 0x2A000002 || pickArguments[0].Name != "V"
            || pickArguments[1].MetadataToken != 0x2A000003 || pickArguments[1].Name != "W"
            || pickArguments[1].GenericParameterPosition != 1)
        {
            return 10;
        }

        if ((int) createdD.GetMethod ("Caller").Invoke (null, null) != 42)
        {
            return 11;
        }

        if ((long) createdG.MakeGenericMethod (typeof (long)).Invoke (null, new object[] { 7L }) != 7L)
        {
            return 12;
        }

        if ((string) createdPick.MakeGenericMethod (typeof (int), typeof (string)).Invoke (null, new object[] { 1, "w" }) != "w")
        {
            return 13;
        }

        return 0;
    }
}
