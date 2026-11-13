namespace WoofWare.PawPrint.Test

open System.IO
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// The generic parameters `TypeBuilder_TermCreateClass` refuses, each by name, although real .NET
/// defines each of them (measured on .NET 10) and refuses to load their types, and the reference to a
/// generic method of the module's own created type that `ModuleBuilder_GetMemberRefFromSignature`
/// refuses. The generic parameters PawPrint defines are in `sourcesImpure/TypeBuilderDefineGenericParam.cs`,
/// `sourcesImpure/TypeBuilderGenericParamConstraints.cs`, `sourcesImpure/TypeBuilderGenericMethod.cs` and
/// the `sourcesImpure/TypeBuilderMixedParams*.cs` guests, the last in any owner order.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestTypeBuilderGenericParamRefusals =

    /// A guest whose `Main` runs `body` with `module` a dynamic module.
    let private source (body : string) : string =
        $$"""
using System;
using System.Reflection;
using System.Reflection.Emit;

public class Program
{
    public static int Main(string[] args)
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Dyn"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule("Dyn");
        {{body}}
        return 0;
    }
}
"""

    let private runRefused (name : string) (body : string) : GuestFailureException =
        let image = Roslyn.compile [ source body ]

        let _messages, loggerFactory =
            LoggerFactory.makeTestWithProperties [ "source_file", name ]

        use _loggerFactoryResource = loggerFactory
        use peImage = new MemoryStream (image)

        let host =
            HostConfig.Default (FrameworkUnderTest.runtimeDirsFor KernelConfig.Default.UnixPlatform)

        let host =
            { host with
                Guest =
                    { host.Guest with
                        AppContext =
                            AppContextProperties.ofMap (
                                Map.ofList
                                    [
                                        "System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported", "true"
                                    ]
                            )
                    }
            }

        Assert.Throws<GuestFailureException> (fun () ->
            BoundedRun.runWith loggerFactory BoundedRun.defaultMaxSteps name (Some name) peImage host
            |> ExpectRun.ended
            |> ignore<RunOutcome>
        )

    [<Test>]
    let ``a variant parameter of a method is refused`` () : unit =
        // CoreCLR's class loader throws TypeLoadException from `CreateType` for a covariant or
        // contravariant method parameter, of an interface's method too (measured on .NET 10).
        let exc =
            runRefused
                "GenericParamVariantMethod.cs"
                """
        TypeBuilder thing = module.DefineType("IThing", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        MethodBuilder m = thing.DefineMethod("M", MethodAttributes.Public | MethodAttributes.Static);
        m.DefineGenericParameters("U")[0].SetGenericParameterAttributes(GenericParameterAttributes.Contravariant);
        m.GetILGenerator().Emit(OpCodes.Ret);
        thing.CreateType();"""

        exc.Message
        |> shouldContainText "is a covariant or contravariant parameter of the method 0x06000001"

    [<Test>]
    let ``a generic method of the module's own created type, reached through its MethodInfo, is refused`` () : unit =
        // `GetMethodTokenInternal` finds the created type's `MethodInfo` of another module than the
        // `ModuleBuilder`, so `GetMemberRefToken` names `G` by a MemberRef whose parent is `D`'s TypeDef.
        let exc =
            runRefused
                "MemberRefOfOwnCreatedGenericMethod.cs"
                """
        TypeBuilder d = module.DefineType("D", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        MethodBuilder g = d.DefineMethod("G", MethodAttributes.Public | MethodAttributes.Static);
        GenericTypeParameterBuilder x = g.DefineGenericParameters("X")[0];
        g.SetReturnType(x);
        g.SetParameters(x);
        ILGenerator gil = g.GetILGenerator();
        gil.Emit(OpCodes.Ldarg_0);
        gil.Emit(OpCodes.Ret);
        Type created = d.CreateType();
        TypeBuilder e = module.DefineType("E", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        ILGenerator il = e.DefineMethod("C", MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes).GetILGenerator();
        il.Emit(OpCodes.Ldc_I4, 42);
        il.Emit(OpCodes.Call, created.GetMethod("G").MakeGenericMethod(typeof(int)));
        il.Emit(OpCodes.Ret);"""

        exc.Message |> shouldContainText "the parent 0x02000002 is a TypeDef of Dyn"

    [<Test>]
    let ``a covariant parameter of a class is refused`` () : unit =
        let exc =
            runRefused
                "GenericParamVariantClass.cs"
                """
        TypeBuilder box = module.DefineType("Box", TypeAttributes.Public);
        box.DefineGenericParameters("T")[0].SetGenericParameterAttributes(GenericParameterAttributes.Covariant);
        box.CreateType();"""

        exc.Message
        |> shouldContainText "is covariant or contravariant, and the type is not an interface"

    [<Test>]
    let ``both variance bits are refused, even on an interface`` () : unit =
        let exc =
            runRefused
                "GenericParamBothVariances.cs"
                """
        TypeBuilder thing = module.DefineType("IThing", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        thing.DefineGenericParameters("T")[0].SetGenericParameterAttributes(
            GenericParameterAttributes.Covariant | GenericParameterAttributes.Contravariant);
        thing.CreateType();"""

        exc.Message |> shouldContainText "has both variance bits set"
