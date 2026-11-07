namespace WoofWare.PawPrint.Test

open System
open System.IO
open System.Reflection
open System.Reflection.Emit
open NUnit.Framework
open WoofWare.PawPrint

/// `System.TypedReference` as the argument of a generic method's and a generic type's
/// instantiation, with and without `allows ref struct` on the parameter.
///
/// CoreCLR's `Generics::CheckInstantiation` (generics.cpp) refuses an argument whose element type
/// is `TYPEDBYREF`. .NET 10's VM reports `TypedReference` so, and refuses both instantiations
/// whatever the parameter allows: the method's with `BadImageFormatException`, the type's with
/// `TypeLoadException`. .NET 11's treats it as an ordinary value type, so the parameter's
/// constraints decide: with `allows ref struct` both run, and without it CoreCLR refuses the
/// byref-like argument as it checks constraints, the method's with `VerificationException` and the
/// type's with `TypeLoadException`.
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

    /// `static class Plain` with `static int M<T>()` returning 7, and `static int CallsMethod()`
    /// calling `M<TypedReference>`; `class G<T>` with `static int Get()` returning 9, and
    /// `Plain.CallsType()` calling `G<TypedReference>.Get()`. Each `T` carries `allows ref struct`
    /// if `allowsRefStruct`, and no constraint otherwise.
    let private fabricate (allowsRefStruct : bool) : byte[] =
        let typedReference = typeof<obj>.Assembly.GetType "System.TypedReference"

        let attributes =
            if allowsRefStruct then
                GenericParameterAttributes.AllowByRefLike
            else
                GenericParameterAttributes.None

        let builder =
            PersistedAssemblyBuilder (AssemblyName "TypedRefArg", typeof<obj>.Assembly)

        let modul = builder.DefineDynamicModule "TypedRefArg"

        let generic =
            modul.DefineType ("G`1", TypeAttributes.Public ||| TypeAttributes.Class, typeof<obj>)

        (generic.DefineGenericParameters [| "T" |]).[0].SetGenericParameterAttributes attributes

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

        (m.DefineGenericParameters [| "T" |]).[0].SetGenericParameterAttributes attributes

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

    /// Returns what `Plain`'s caller returns, or 101 for a `BadImageFormatException`, 102 for a
    /// `TypeLoadException` and 103 for a `VerificationException` as the runtime compiles it.
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
        catch (System.Security.VerificationException)
        {
            return 103;
        }
    }
}
"""

    /// .NET 10 refuses every instantiation here as the JIT compiles `Plain`'s caller.
    let private stopsAtRefusal : string =
        "PawPrint stops the run at an instantiation CoreCLR refuses, rather than raising the refusal's exception in the guest; raising it there would un-park this"

    [<Test>]
    let ``a method instantiated at TypedReference runs where the runtime admits it`` () : unit =
        ParkedOn.check [ EmulatedRuntime.Net10 ] stopsAtRefusal
        FabricatedGuest.run "TypedRefArg" (fabricate true) "TypedRefArgDriver" (driver Shape.Method) 7

    [<Test>]
    let ``a type instantiated at TypedReference runs where the runtime admits it`` () : unit =
        ParkedOn.check [ EmulatedRuntime.Net10 ] stopsAtRefusal
        FabricatedGuest.run "TypedRefArg" (fabricate true) "TypedRefArgDriver" (driver Shape.Type) 9

    /// The interpreter checks no generic constraints, so it runs an instantiation whose byref-like
    /// argument a parameter without `allows ref struct` refuses.
    let private constraintsUnchecked : string =
        "PawPrint checks no generic constraint as it instantiates a type or method, so it runs a byref-like argument for a parameter without `allows ref struct`; checking them would un-park this"

    [<Test>]
    let ``a method instantiated at TypedReference without allows ref struct is refused`` () : unit =
        ParkedOn.check [ EmulatedRuntime.Net10 ] stopsAtRefusal
        ParkedOn.check [ EmulatedRuntime.Net11 ] constraintsUnchecked

        FabricatedGuest.run
            "TypedRefArg"
            (fabricate false)
            "TypedRefArgDriver"
            (driver Shape.Method)
            (match FrameworkUnderTest.runtime () with
             | EmulatedRuntime.Net10 -> 101
             | EmulatedRuntime.Net11 -> 103)

    [<Test>]
    let ``a type instantiated at TypedReference without allows ref struct is refused`` () : unit =
        ParkedOn.check [ EmulatedRuntime.Net10 ] stopsAtRefusal
        ParkedOn.check [ EmulatedRuntime.Net11 ] constraintsUnchecked

        FabricatedGuest.run
            "TypedRefArg"
            (fabricate false)
            "TypedRefArgDriver"
            (driver Shape.Type)
            (match FrameworkUnderTest.runtime () with
             | EmulatedRuntime.Net10 -> 102
             | EmulatedRuntime.Net11 -> 102)
