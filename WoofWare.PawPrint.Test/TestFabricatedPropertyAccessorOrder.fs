namespace WoofWare.PawPrint.Test

open System
open System.Reflection
open System.Reflection.Emit
open NUnit.Framework

/// A property's accessors in an image on disk, with 25 MethodSemantics rows in all: CoreCLR's read-only
/// metadata finds a property's rows in RID order however many rows the table has, unlike a dynamic
/// module's read-write metadata, which reverses them from 25 rows (`ReadWriteMetadataLookup`). No compiler
/// gives a property three other methods, so the image is written by `PersistedAssemblyBuilder`.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestFabricatedPropertyAccessorOrder =

    let private fabricate () : byte[] =
        let builder =
            PersistedAssemblyBuilder (AssemblyName "AccessorOrder", typeof<obj>.Assembly)

        let modul = builder.DefineDynamicModule "AccessorOrder"
        let t = modul.DefineType ("T", TypeAttributes.Public ||| TypeAttributes.Class, typeof<obj>)

        t.DefineDefaultConstructor MethodAttributes.Public
        |> ignore<ConstructorBuilder>

        let constant (name : string) (value : int) : MethodBuilder =
            let method =
                t.DefineMethod (
                    name,
                    MethodAttributes.Public ||| MethodAttributes.SpecialName ||| MethodAttributes.HideBySig,
                    typeof<int>,
                    Type.EmptyTypes
                )

            let il = method.GetILGenerator ()
            il.Emit (OpCodes.Ldc_I4, value)
            il.Emit OpCodes.Ret
            method

        // P's getter and three other methods, then 21 properties of one getter each: 25 rows.
        let p = t.DefineProperty ("P", PropertyAttributes.None, typeof<int>, Type.EmptyTypes)
        p.SetGetMethod (constant "g" 0)
        p.AddOtherMethod (constant "o1" 1)
        p.AddOtherMethod (constant "o2" 2)
        p.AddOtherMethod (constant "o3" 3)

        for i in 0..20 do
            let filler =
                t.DefineProperty ($"F%d{i}", PropertyAttributes.None, typeof<int>, Type.EmptyTypes)

            filler.SetGetMethod (constant $"get_F%d{i}" i)

        t.CreateType () |> ignore<Type>

        use image = new IO.MemoryStream ()
        builder.Save image
        image.ToArray ()

    let private driver =
        """
using System;
using System.Linq;
using System.Reflection;

public static class Driver
{
    public static int Main()
    {
        PropertyInfo p = typeof(T).GetProperty("P");
        string accessors = string.Join(",", p.GetAccessors(true).Select(m => m.Name));

        if (accessors != "g,o1,o2,o3")
        {
            Console.WriteLine(accessors);
            return 1;
        }

        if (typeof(T).GetProperties().Length != 22)
        {
            return 2;
        }

        return 0;
    }
}
"""

    [<Test>]
    let ``an image on disk lists a property's accessors in RID order past 25 rows`` () : unit =
        FabricatedGuest.run "AccessorOrder" (fabricate ()) "AccessorOrderDriver" driver 0
