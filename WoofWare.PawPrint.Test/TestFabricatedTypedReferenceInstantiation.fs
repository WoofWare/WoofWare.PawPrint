namespace WoofWare.PawPrint.Test

open System
open System.IO
open System.Reflection
open System.Reflection.Emit
open NUnit.Framework
open WoofWare.PawPrint

/// `System.TypedReference` as the argument of a generic method's and a generic type's
/// instantiation, each parameter carrying `allows ref struct` so that no constraint refuses it.
///
/// CoreCLR's `Generics::CheckInstantiation` (generics.cpp) refuses an argument whose element type
/// is `TYPEDBYREF`. .NET 10's VM reports `TypedReference` so, and refuses both instantiations:
/// the method's with `BadImageFormatException`, the type's with `TypeLoadException`. .NET 11's
/// treats it as an ordinary value type, and runs both.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestFabricatedTypedReferenceInstantiation =

    /// What the driver returns when the instantiation runs, for each shape.
    [<RequireQualifiedAccess>]
    type private Shape =
        /// `call int32 Plain::M<TypedReference>()`, returning 7.
        | Method
        /// `call int32 G`1<TypedReference>::Get()`, returning 9.
        | Type

    /// `static class Plain` with `static int M<T>() where T : allows ref struct` returning 7, and
    /// `static int CallsMethod()` calling `M<TypedReference>`; `class G<T> where T : allows ref
    /// struct` with `static int Get()` returning 9, and `Plain.CallsType()` calling
    /// `G<TypedReference>.Get()`.
    let private fabricate () : byte[] =
        let typedReference = typeof<obj>.Assembly.GetType "System.TypedReference"

        let builder =
            PersistedAssemblyBuilder (AssemblyName "TypedRefArg", typeof<obj>.Assembly)

        let modul = builder.DefineDynamicModule "TypedRefArg"

        let generic =
            modul.DefineType ("G`1", TypeAttributes.Public ||| TypeAttributes.Class, typeof<obj>)

        (generic.DefineGenericParameters [| "T" |]).[0].SetGenericParameterAttributes
            GenericParameterAttributes.AllowByRefLike

        let get =
            generic.DefineMethod (
                "Get",
                MethodAttributes.Public ||| MethodAttributes.Static,
                typeof<int>,
                Type.EmptyTypes
            )

        let getIl = get.GetILGenerator ()
        getIl.Emit (OpCodes.Ldc_I4, 9)
        getIl.Emit OpCodes.Ret

        generic.CreateType () |> ignore<Type>

        let plain =
            modul.DefineType ("Plain", TypeAttributes.Public ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed)

        let m =
            plain.DefineMethod ("M", MethodAttributes.Public ||| MethodAttributes.Static, typeof<int>, Type.EmptyTypes)

        (m.DefineGenericParameters [| "T" |]).[0].SetGenericParameterAttributes
            GenericParameterAttributes.AllowByRefLike

        let mIl = m.GetILGenerator ()
        mIl.Emit (OpCodes.Ldc_I4, 7)
        mIl.Emit OpCodes.Ret

        let caller (name : string) (callee : MethodInfo) : unit =
            let il =
                plain
                    .DefineMethod(
                        name,
                        MethodAttributes.Public ||| MethodAttributes.Static,
                        typeof<int>,
                        Type.EmptyTypes
                    )
                    .GetILGenerator ()

            il.Emit (OpCodes.Call, callee)
            il.Emit OpCodes.Ret

        caller "CallsMethod" (m.MakeGenericMethod [| typedReference |])
        caller "CallsType" (TypeBuilder.GetMethod (generic.MakeGenericType [| typedReference |], get))

        plain.CreateType () |> ignore<Type>

        use stream = new MemoryStream ()
        builder.Save stream
        stream.ToArray ()

    /// Returns what `Plain`'s caller returns, or 101 for a `BadImageFormatException` and 102 for a
    /// `TypeLoadException` as the runtime compiles it.
    let private driver (shape : Shape) : string =
        let caller =
            match shape with
            | Shape.Method -> "CallsMethod"
            | Shape.Type -> "CallsType"

        $$"""
using System;

public static class Program
{
    public static int Main()
    {
        try
        {
            return Plain.{{caller}}();
        }
        catch (BadImageFormatException)
        {
            return 101;
        }
        catch (TypeLoadException)
        {
            return 102;
        }
    }
}
"""

    let private runs (shape : Shape) : unit =
        // .NET 10 refuses both instantiations as the JIT compiles `Plain`'s caller, and PawPrint
        // stops the run there rather than raising the exception in the guest.
        ParkedOn.check
            [ EmulatedRuntime.Net10 ]
            "PawPrint stops the run at an instantiation CoreCLR refuses, rather than raising the refusal's exception in the guest; raising it there would un-park this"

        let expected =
            match shape with
            | Shape.Method -> 7
            | Shape.Type -> 9

        FabricatedGuest.run "TypedRefArg" (fabricate ()) "TypedRefArgDriver" (driver shape) expected

    [<Test>]
    let ``a method instantiated at TypedReference runs where the runtime admits it`` () : unit = runs Shape.Method

    [<Test>]
    let ``a type instantiated at TypedReference runs where the runtime admits it`` () : unit = runs Shape.Type
