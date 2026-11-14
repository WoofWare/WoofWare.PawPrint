using System;
using System.Reflection;
using System.Reflection.Emit;

// `Type.IsVisible` over a dynamic module's types. A `TypeBuilder` answers by the managed walk in
// `Type.IsVisible`; the `RuntimeType` that `CreateType` returns asks the `RuntimeTypeHandle_IsVisible`
// QCall, which reads the dynamic module's TypeDef flags as it reads any image's. Every answer below was
// measured on .NET 10 and 11.
//
// Returns 0 on success, or the number of the first check that failed.

public class PubG<T> { }
internal class Internal { }

public static class Program
{
    public static int Main()
    {
        AssemblyBuilder ab = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Dyn"), AssemblyBuilderAccess.Run);
        ModuleBuilder mb = ab.DefineDynamicModule("Dyn");

        TypeBuilder pub = mb.DefineType("DPub", TypeAttributes.Public);
        TypeBuilder notPublic = mb.DefineType("DNotPublic", TypeAttributes.NotPublic);

        if (!pub.IsVisible) return 1;
        if (notPublic.IsVisible) return 2;

        Type createdPub = pub.CreateType();
        Type createdNotPublic = notPublic.CreateType();

        if (!createdPub.IsVisible) return 3;
        if (createdNotPublic.IsVisible) return 4;

        // A top-level type with a nested visibility is refused before any QCall.
        try
        {
            mb.DefineType("DNested", TypeAttributes.NestedPublic);
            return 5;
        }
        catch (ArgumentException)
        {
        }

        TypeBuilder g = mb.DefineType("DG", TypeAttributes.Public);
        g.DefineGenericParameters("T");
        Type dg = g.CreateType();

        if (!dg.IsVisible) return 6;
        if (!dg.MakeGenericType(typeof(int)).IsVisible) return 7;
        if (dg.MakeGenericType(typeof(Internal)).IsVisible) return 8;
        if (typeof(PubG<>).MakeGenericType(createdNotPublic).IsVisible) return 9;
        if (!typeof(PubG<>).MakeGenericType(createdPub).IsVisible) return 10;

        return 0;
    }
}
