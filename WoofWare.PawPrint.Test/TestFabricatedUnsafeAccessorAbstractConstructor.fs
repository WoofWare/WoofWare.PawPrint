namespace WoofWare.PawPrint.Test

open System
open System.IO
open System.Reflection
open System.Reflection.Emit
open System.Runtime.CompilerServices
open NUnit.Framework

/// An `[UnsafeAccessor(UnsafeAccessorKind.Constructor)]` of a class, an abstract class, a struct
/// and a struct flagged abstract (and abstract sealed). CoreCLR synthesises the accessor's body as
/// a `newobj` of the constructor it binds (`vm/unsafeaccessors.cpp`), and the JIT refuses that
/// `newobj` for an abstract class with `InvalidOperationException`, "Instances of abstract classes
/// cannot be created.", and allocates a value type without asking whether it is abstract. C#
/// spells no abstract value type, so the image is fabricated, accessors and all.
///
/// Each type comes twice: once with a true instance constructor, and once whose only `.ctor` lacks
/// `RTSpecialName`. CoreCLR's lookup asks only for a method named `.ctor` that is not static, so it
/// binds the second too, and measured on .NET 10 and 11 the stub then behaves exactly as for the
/// first.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestFabricatedUnsafeAccessorAbstractConstructor =

    /// A type of the image, and how it is built.
    type private Shape =
        {
            Name : string
            Attributes : TypeAttributes
            IsValueType : bool
            /// Whether the `.ctor` carries `SpecialName` and `RTSpecialName`, making it an instance
            /// constructor; otherwise it is an ordinary instance method that happens to be named
            /// `.ctor`.
            RuntimeSpecial : bool
            /// Whether the accessor raises `InvalidOperationException` rather than constructing.
            Refuses : bool
        }

    let private shapes : Shape list =
        [
            "Cls", TypeAttributes.Class, false, false
            "AbsCls", TypeAttributes.Class ||| TypeAttributes.Abstract, false, true
            "Str", TypeAttributes.Sealed, true, false
            "AbsStr", TypeAttributes.Abstract, true, false
            "AbsSealedStr", TypeAttributes.Abstract ||| TypeAttributes.Sealed, true, false
        ]
        |> List.collect (fun (name, attributes, isValueType, refuses) ->
            [ true ; false ]
            |> List.map (fun runtimeSpecial ->
                {
                    Name = if runtimeSpecial then name else $"%s{name}Fake"
                    Attributes = attributes
                    IsValueType = isValueType
                    RuntimeSpecial = runtimeSpecial
                    Refuses = refuses
                }
            )
        )

    /// The image: for each shape, a public type `{ public int V; }` whose only method is a
    /// `.ctor(int)` storing its argument in `V`; and `static class Calls`, holding for each shape an
    /// `[UnsafeAccessor(Constructor)]` `MakeT(int)` and a method `object NewT(int)` that calls it and
    /// returns the stored field, boxed, because C# cannot name a value type flagged abstract and
    /// sealed.
    let private fabricate () : byte[] =
        let builder =
            PersistedAssemblyBuilder (AssemblyName "AccessorAbstract", typeof<obj>.Assembly)

        let modul = builder.DefineDynamicModule "AccessorAbstract"

        let calls =
            modul.DefineType ("Calls", TypeAttributes.Public ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed)

        let attributeCtor =
            typeof<UnsafeAccessorAttribute>.GetConstructor [| typeof<UnsafeAccessorKind> |]

        let publicStatic =
            MethodAttributes.Public
            ||| MethodAttributes.Static
            ||| MethodAttributes.HideBySig

        for shape in shapes do
            let ty =
                if shape.IsValueType then
                    modul.DefineType (
                        shape.Name,
                        TypeAttributes.Public ||| TypeAttributes.SequentialLayout ||| shape.Attributes,
                        typeof<ValueType>
                    )
                else
                    modul.DefineType (shape.Name, TypeAttributes.Public ||| shape.Attributes)

            let v = ty.DefineField ("V", typeof<int>, FieldAttributes.Public)

            let ctorIl =
                if shape.RuntimeSpecial then
                    let ctor =
                        ty.DefineConstructor (
                            MethodAttributes.Public
                            ||| MethodAttributes.HideBySig
                            ||| MethodAttributes.SpecialName
                            ||| MethodAttributes.RTSpecialName,
                            CallingConventions.Standard,
                            [| typeof<int> |]
                        )

                    ctor.GetILGenerator ()
                else
                    let fake =
                        ty.DefineMethod (
                            ".ctor",
                            MethodAttributes.Public ||| MethodAttributes.HideBySig,
                            typeof<Void>,
                            [| typeof<int> |]
                        )

                    fake.GetILGenerator ()

            ctorIl.Emit OpCodes.Ldarg_0
            ctorIl.Emit OpCodes.Ldarg_1
            ctorIl.Emit (OpCodes.Stfld, v)
            ctorIl.Emit OpCodes.Ret
            ty.CreateType () |> ignore<Type>

            let accessor =
                calls.DefineMethod ($"Make%s{shape.Name}", publicStatic, ty, [| typeof<int> |])

            accessor.SetCustomAttribute (
                CustomAttributeBuilder (attributeCtor, [| box UnsafeAccessorKind.Constructor |])
            )

            let wrapper =
                calls.DefineMethod ($"New%s{shape.Name}", publicStatic, typeof<obj>, [| typeof<int> |])

            let il = wrapper.GetILGenerator ()
            let local = il.DeclareLocal ty
            il.Emit OpCodes.Ldarg_0
            il.Emit (OpCodes.Call, accessor)
            il.Emit (OpCodes.Stloc, local)

            if shape.IsValueType then
                il.Emit (OpCodes.Ldloca, local)
            else
                il.Emit (OpCodes.Ldloc, local)

            il.Emit (OpCodes.Ldfld, v)
            il.Emit (OpCodes.Box, typeof<int>)
            il.Emit OpCodes.Ret

        calls.CreateType () |> ignore<Type>

        use image = new MemoryStream ()
        builder.Save image
        image.ToArray ()

    /// Shape `i` (counting from 1) returns `10 * i + 1` if it constructed where it should have
    /// refused or stored the wrong value, `10 * i + 2` if it threw the wrong exception, and 0 if it
    /// did as expected, so a disagreement names the shape.
    let private driverSource : string =
        let rows =
            shapes
            |> List.mapi (fun i shape ->
                let refuses = if shape.Refuses then "true" else "false"
                $"        r = Check(Calls.New%s{shape.Name}, %d{100 + i}, %s{refuses}, %d{10 * (i + 1)});\n        if (r != 0) return r;\n"
            )
            |> String.concat ""

        $$"""
using System;

public static class Driver
{
    private static int Check(Func<int, object> make, int v, bool refuses, int code)
    {
        try
        {
            object got = make(v);
            return !refuses && got is int stored && stored == v ? 0 : code + 1;
        }
        catch (Exception e)
        {
            return refuses
                && e.GetType() == typeof(InvalidOperationException)
                && e.Message == "Instances of abstract classes cannot be created."
                ? 0
                : code + 2;
        }
    }

    private static int Run()
    {
        int r;
{{rows}}
        return 0;
    }

    public static int Main(string[] args)
    {
        return Run();
    }
}
"""

    [<Test>]
    let ``a Constructor accessor refuses an abstract class and constructs anything else`` () : unit =
        FabricatedGuest.run "AccessorAbstract" (fabricate ()) "AccessorAbstractDriver" driverSource 0
