namespace WoofWare.PawPrint.Test

open System.IO
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// The generic parameters `TypeBuilder_DefineGenericParam` and `TypeBuilder_TermCreateClass` refuse,
/// each by name, although real .NET defines each of them (measured on .NET 10, but for the type it
/// refuses to load). `sourcesImpure/TypeBuilderDefineGenericParam.cs` is the ones they define.
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
    let ``a type created after one with a higher TypeDef RID is refused`` () : unit =
        let exc =
            runRefused
                "GenericParamOutOfOrder.cs"
                """
        TypeBuilder a = module.DefineType("A", TypeAttributes.Public);
        TypeBuilder b = module.DefineType("B", TypeAttributes.Public);
        a.DefineGenericParameters("T");
        b.DefineGenericParameters("U");
        b.CreateType();
        a.CreateType();"""

        exc.Message
        |> shouldContainText "T of TypeDef (TypeDefRid 2) (coded index 4) of Dyn, Version=0.0.0.0"

        exc.Message
        |> shouldContainText "would sort before the last GenericParam row, of TypeDef (TypeDefRid 3) (coded index 6)"

    [<Test>]
    let ``a parameter with a constraint is refused`` () : unit =
        let exc =
            runRefused
                "GenericParamConstraint.cs"
                """
        TypeBuilder box = module.DefineType("Box", TypeAttributes.Public);
        box.DefineGenericParameters("T")[0].SetInterfaceConstraints(typeof(IDisposable));
        box.CreateType();"""

        exc.Message
        |> shouldContainText "for which CoreCLR adds a GenericParamConstraint row"

    [<Test>]
    let ``a generic method's parameter is refused`` () : unit =
        let exc =
            runRefused
                "GenericParamOfMethod.cs"
                """
        TypeBuilder holder = module.DefineType("Holder", TypeAttributes.Public);
        MethodBuilder method = holder.DefineMethod("M", MethodAttributes.Public | MethodAttributes.Static);
        method.DefineGenericParameters("U");
        method.GetILGenerator().Emit(OpCodes.Ret);
        holder.CreateType();"""

        exc.Message |> shouldContainText "U is a parameter of the method 0x06000001"

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
