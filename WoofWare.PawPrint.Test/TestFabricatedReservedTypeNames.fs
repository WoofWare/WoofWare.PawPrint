namespace WoofWare.PawPrint.Test

open System
open System.IO
open System.Reflection
open System.Reflection.Emit
open NUnit.Framework

/// Reflection's names for types and generic parameters whose metadata names hold characters a
/// type name reserves (`, [ ] & * + \`). CoreCLR's `TypeNameBuilder::AddName` escapes each name and
/// namespace it appends, so every row of a nesting chain is escaped on its own and the `+` joining
/// the rows is not; and a generic parameter's name is escaped wherever it is rendered. No C# source
/// can spell these names, and PawPrint's Reflection.Emit cannot yet define a nested or generic type,
/// so the types are fabricated, and the driver checks each name on both runtimes.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestFabricatedReservedTypeNames =

    /// `Out*er.Ou+ter`, with nested `In,ner` and `N&s.In]ner` (whose own row has the namespace
    /// `N&s`); `Ge[n`2<T+U, V\W>`; and `Probe`, whose static methods return those types by `ldtoken`
    /// and which declares `static void M<X+Y>()`.
    let private fabricate () : byte[] =
        let builder =
            PersistedAssemblyBuilder (AssemblyName "Reserved", typeof<obj>.Assembly)

        let modul = builder.DefineDynamicModule "Reserved"

        let staticClass =
            TypeAttributes.Public ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed

        let nestedStaticClass =
            TypeAttributes.NestedPublic
            ||| TypeAttributes.Abstract
            ||| TypeAttributes.Sealed

        let outer = modul.DefineType ("Out*er.Ou+ter", staticClass)
        let inner = outer.DefineNestedType ("In,ner", nestedStaticClass)
        let namespacedInner = outer.DefineNestedType ("N&s.In]ner", nestedStaticClass)
        let gen = modul.DefineType ("Ge[n`2", staticClass)

        gen.DefineGenericParameters [| "T+U" ; "V\\W" |]
        |> ignore<GenericTypeParameterBuilder[]>

        let probe = modul.DefineType ("Probe", staticClass)
        let getTypeFromHandle = typeof<Type>.GetMethod "GetTypeFromHandle"

        for name, target in
            [
                "Outer", (outer :> Type)
                "Inner", (inner :> Type)
                "NamespacedInner", (namespacedInner :> Type)
                "Gen", (gen :> Type)
            ] do
            let getter =
                probe.DefineMethod (name, MethodAttributes.Public ||| MethodAttributes.Static, typeof<Type>, [||])

            let il = getter.GetILGenerator ()
            il.Emit (OpCodes.Ldtoken, target)
            il.Emit (OpCodes.Call, getTypeFromHandle)
            il.Emit OpCodes.Ret

        let m =
            probe.DefineMethod ("M", MethodAttributes.Public ||| MethodAttributes.Static, typeof<Void>, [||])

        m.DefineGenericParameters [| "X+Y" |] |> ignore<GenericTypeParameterBuilder[]>
        m.GetILGenerator().Emit OpCodes.Ret

        for t in [ outer ; inner ; namespacedInner ; gen ; probe ] do
            t.CreateType () |> ignore<Type>

        use image = new MemoryStream ()
        builder.Save image
        image.ToArray ()

    [<Test>]
    let ``names holding reserved characters are escaped row by row`` () : unit =
        FabricatedGuest.run
            "Reserved"
            (fabricate ())
            "ReservedDriver"
            """
using System;

public static class Driver
{
    static int failed;

    static void Check (int code, string actual, string expected)
    {
        if (failed == 0 && actual != expected)
        {
            Console.WriteLine ($"check {code}: expected <{expected}>, got <{actual}>");
            failed = code;
        }
    }

    public static int Main (string[] args)
    {
        Type outer = Probe.Outer ();
        Check (1, outer.FullName, "Out\\*er.Ou\\+ter");
        Check (2, outer.Name, "Ou\\+ter");
        Check (3, outer.Namespace, "Out*er");

        Type inner = Probe.Inner ();
        Check (11, inner.FullName, "Out\\*er.Ou\\+ter+In\\,ner");
        Check (12, inner.Name, "In\\,ner");
        Check (13, inner.ToString (), "Out\\*er.Ou\\+ter+In\\,ner");

        // A nested row with a namespace of its own: CoreCLR appends it, escaped, after the `+`.
        Type namespacedInner = Probe.NamespacedInner ();
        Check (21, namespacedInner.FullName, "Out\\*er.Ou\\+ter+N\\&s.In\\]ner");
        Check (22, namespacedInner.Name, "In\\]ner");

        Type gen = Probe.Gen ();
        Check (31, gen.FullName, "Ge\\[n`2");
        Check (32, gen.Name, "Ge\\[n`2");
        Check (33, gen.ToString (), "Ge\\[n`2[T\\+U,V\\\\W]");

        Type[] formals = gen.GetGenericArguments ();
        Check (41, formals[0].Name, "T\\+U");
        Check (42, formals[1].ToString (), "V\\\\W");

        Type closed = gen.MakeGenericType (typeof (int), inner);
        Check (51, closed.ToString (), "Ge\\[n`2[System.Int32,Out\\*er.Ou\\+ter+In\\,ner]");
        Check (52, closed.FullName, "Ge\\[n`2[[System.Int32, " + typeof (int).Assembly.FullName + "],[Out\\*er.Ou\\+ter+In\\,ner, " + inner.Assembly.FullName + "]]");

        var m = typeof (Probe).GetMethod ("M");
        Type methodFormal = m.GetGenericArguments ()[0];
        Check (71, methodFormal.Name, "X\\+Y");
        Check (72, m.ToString (), "Void M[X\\+Y]()");

        return failed;
    }
}
"""
            0
