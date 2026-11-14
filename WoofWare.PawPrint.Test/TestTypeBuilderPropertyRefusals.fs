namespace WoofWare.PawPrint.Test

open System.IO
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// The accessors `TypeBuilder_DefineMethodSemantics` refuses, each by name, although real .NET writes a
/// row for each (measured on .NET 10). The properties PawPrint defines are in
/// `sourcesImpure/TypeBuilderDefineProperty.cs` and `sourcesImpure/TypeBuilderPropertyForeignAccessor.cs`.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestTypeBuilderPropertyRefusals =

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
    let ``an accessor whose token names no method of the module is refused`` () : unit =
        // `y2` is MethodDef 3 of module `Y`, and module `X` has one method. Real .NET writes the row with
        // RID 3, creates `XT`, and throws ArgumentOutOfRangeException from reflection over `Wild`.
        let exc =
            runRefused
                "PropertyAccessorOfAnotherModule.cs"
                """
        ModuleBuilder y = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Y"), AssemblyBuilderAccess.Run).DefineDynamicModule("Y");
        TypeBuilder yt = y.DefineType("YT", TypeAttributes.Public);
        MethodBuilder y0 = yt.DefineMethod("y0", MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);
        y0.GetILGenerator().Emit(OpCodes.Ldc_I4_1); y0.GetILGenerator().Emit(OpCodes.Ret);
        MethodBuilder y1 = yt.DefineMethod("y1", MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);
        y1.GetILGenerator().Emit(OpCodes.Ldc_I4_2); y1.GetILGenerator().Emit(OpCodes.Ret);
        MethodBuilder y2 = yt.DefineMethod("y2", MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);
        y2.GetILGenerator().Emit(OpCodes.Ldc_I4_3); y2.GetILGenerator().Emit(OpCodes.Ret);
        TypeBuilder xt = module.DefineType("XT", TypeAttributes.Public);
        MethodBuilder own = xt.DefineMethod("get_Own", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName, typeof(int), Type.EmptyTypes);
        own.GetILGenerator().Emit(OpCodes.Ldc_I4_7); own.GetILGenerator().Emit(OpCodes.Ret);
        xt.DefineProperty("Own", PropertyAttributes.None, CallingConventions.Standard, typeof(int), Type.EmptyTypes).SetGetMethod(own);
        xt.DefineProperty("Wild", PropertyAttributes.None, CallingConventions.Standard, typeof(int), Type.EmptyTypes).SetGetMethod(y2);"""

        exc.Message |> shouldContainText "TODO: TypeBuilder_DefineMethodSemantics"
        exc.Message |> shouldContainText "names no MethodDef row of"
