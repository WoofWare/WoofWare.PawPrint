namespace WoofWare.PawPrint.Test

open System
open System.IO
open System.Reflection
open System.Reflection.Emit
open NUnit.Framework

/// `Span<int>`, a byref-like type, as the argument of a generic method's and a generic type's
/// instantiation, with and without `allows ref struct` on the parameter.
///
/// CoreCLR checks a method instantiation's constraints as `InstantiatedMethodDesc::NewInstantiatedMethodDesc`
/// first creates it (genmeth.cpp), raising `VerificationException`, and a type instantiation's as
/// `MethodTable::DoFullyLoad` completes its load (`SatisfiesClassConstraints`), raising
/// `TypeLoadException`. Both happen as the JIT compiles the method whose `call` names the
/// instantiation. A parameter without `allows ref struct` rejects a byref-like argument
/// (`TypeVarTypeDesc::SatisfiesConstraints`, typedesc.cpp). C# refuses to spell the rejected
/// instantiations.
///
/// Each caller here puts its `call` first and has no handler of its own, so whether the refusal
/// is raised as the caller is compiled or as its `call` runs cannot show.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestFabricatedByRefLikeConstraint =

    /// What the driver returns when the instantiation runs, for each shape.
    [<RequireQualifiedAccess>]
    type private Shape =
        /// `call int32 Plain::M<Span<int>>()`, returning 7.
        | Method
        /// `call int32 G`1<Span<int>>::Get()`, returning 9.
        | Type

    /// `static class Plain` with `static int M<T>()` returning 7, and `static int CallsMethod()`
    /// calling `M<Span<int>>`; `class G<T>` with `static int Get()` returning 9, and
    /// `Plain.CallsType()` calling `G<Span<int>>.Get()`. Each `T` carries `allows ref struct` if
    /// `allowsRefStruct`, and no constraint otherwise.
    let private fabricate (allowsRefStruct : bool) : byte[] =
        // F# refuses `typeof` of a byref-like type (FS0412).
        let span =
            typeof<obj>.Assembly.GetType("System.Span`1", true).MakeGenericType [| typeof<int> |]

        let attributes =
            if allowsRefStruct then
                GenericParameterAttributes.AllowByRefLike
            else
                GenericParameterAttributes.None

        let builder =
            PersistedAssemblyBuilder (AssemblyName "ByRefLikeArg", typeof<obj>.Assembly)

        let modul = builder.DefineDynamicModule "ByRefLikeArg"

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

        caller "CallsMethod" (m.MakeGenericMethod [| span |])
        caller "CallsType" (TypeBuilder.GetMethod (generic.MakeGenericType [| span |], get))

        plain.CreateType () |> ignore<Type>

        use stream = new MemoryStream ()
        builder.Save stream
        stream.ToArray ()

    /// Returns what `Plain`'s caller returns, or 102 for a `TypeLoadException` and 103 for a
    /// `VerificationException`.
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

    [<Test>]
    let ``a method instantiated at a byref-like type runs where its parameter allows ref struct`` () : unit =
        FabricatedGuest.run "ByRefLikeArg" (fabricate true) "ByRefLikeArgDriver" (driver Shape.Method) 7

    [<Test>]
    let ``a type instantiated at a byref-like type runs where its parameter allows ref struct`` () : unit =
        FabricatedGuest.run "ByRefLikeArg" (fabricate true) "ByRefLikeArgDriver" (driver Shape.Type) 9

    [<Test>]
    let ``a method instantiated at a byref-like type without allows ref struct raises VerificationException``
        ()
        : unit
        =
        FabricatedGuest.run "ByRefLikeArg" (fabricate false) "ByRefLikeArgDriver" (driver Shape.Method) 103

    [<Test>]
    let ``a type instantiated at a byref-like type without allows ref struct raises TypeLoadException`` () : unit =
        FabricatedGuest.run "ByRefLikeArg" (fabricate false) "ByRefLikeArgDriver" (driver Shape.Type) 102
