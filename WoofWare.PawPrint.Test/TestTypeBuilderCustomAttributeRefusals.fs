namespace WoofWare.PawPrint.Test

open System.IO
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// The attributes `TypeBuilder_DefineCustomAttribute` refuses, each by name, where real .NET accepts or throws (measured
/// on .NET 10 and 11): a pseudo-attribute that PawPrint does not model, on a right target, which CoreCLR turns into a
/// flag or other rows, and on a wrong one, for which it throws; an ordinary attribute on a created type, which it writes;
/// `DebuggableAttribute` on the assembly, which it folds into the debugger bits; and a friend attribute whose name
/// PawPrint's friend scan rejects, for which it throws after writing the row. The attributes PawPrint stores are in
/// `sourcesImpure/TypeBuilderCustomAttributes.cs`, and the pseudo-attributes it models in
/// `sourcesImpure/TypeBuilderPseudoAttributeFlags.cs`.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestTypeBuilderCustomAttributeRefusals =

    /// A guest whose `Main` runs `body` with `module` a dynamic module.
    let private source (body : string) : string =
        $$"""
using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

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
    let ``an unmodelled pseudo-attribute is refused`` () : unit =
        // Real .NET sets `tdImport` on the interface `I` and writes no row.
        let exc =
            runRefused
                "UnmodelledPseudoCustomAttribute.cs"
                """
        TypeBuilder i = module.DefineType("I", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        i.SetCustomAttribute(new CustomAttributeBuilder(typeof(System.Runtime.InteropServices.ComImportAttribute).GetConstructor(Type.EmptyTypes), new object[0]));"""

        exc.Message |> shouldContainText "TODO: TypeBuilder_DefineCustomAttribute"

        exc.Message
        |> shouldContainText "System.Runtime.InteropServices.ComImportAttribute on 0x02000002 is a pseudo-attribute"

    [<Test>]
    let ``an unmodelled pseudo-attribute on a wrong target is refused rather than raised`` () : unit =
        // Real .NET throws ArgumentException "Invalid custom attribute provided.", which this guest catches: a field
        // is not among `MethodImplAttribute`'s targets. PawPrint models no target list for the name.
        let exc =
            runRefused
                "UnmodelledPseudoCustomAttributeWrongTarget.cs"
                """
        TypeBuilder t = module.DefineType("T", TypeAttributes.Public);
        FieldBuilder f = t.DefineField("F", typeof(int), FieldAttributes.Public);
        try
        {
            f.SetCustomAttribute(new CustomAttributeBuilder(typeof(MethodImplAttribute).GetConstructor(new[] { typeof(MethodImplOptions) }), new object[] { MethodImplOptions.NoInlining }));
            return 1;
        }
        catch (ArgumentException)
        {
        }"""

        exc.Message |> shouldContainText "TODO: TypeBuilder_DefineCustomAttribute"

        exc.Message
        |> shouldContainText "System.Runtime.CompilerServices.MethodImplAttribute on 0x04000001 is a pseudo-attribute"

    [<Test>]
    let ``an attribute on a created type is refused`` () : unit =
        // Real .NET writes the row, and `GetCustomAttributes` of the created type shows it.
        let exc =
            runRefused
                "CustomAttributeOnCreatedType.cs"
                """
        TypeBuilder t = module.DefineType("T", TypeAttributes.Public);
        t.CreateType();
        t.SetCustomAttribute(new CustomAttributeBuilder(typeof(ObsoleteAttribute).GetConstructor(new[] { typeof(string) }), new object[] { "late" }));"""

        exc.Message |> shouldContainText "TODO: TypeBuilder_DefineCustomAttribute"

        exc.Message
        |> shouldContainText "the owner 0x02000002 belongs to TypeDef 0x02000002, which is created"

    [<Test>]
    let ``DebuggableAttribute on the assembly is refused`` () : unit =
        // Real .NET writes the row and sets the assembly's debugger bits from it.
        let exc =
            runRefused
                "DebuggableOnAssembly.cs"
                """
        assembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(System.Diagnostics.DebuggableAttribute).GetConstructor(new[] { typeof(bool), typeof(bool) }), new object[] { true, false }));"""

        exc.Message |> shouldContainText "TODO: TypeBuilder_DefineCustomAttribute"
        exc.Message |> shouldContainText "DebuggableAttribute on the assembly"

    [<Test>]
    let ``a friend attribute that the friend scan rejects is refused`` () : unit =
        // Real .NET writes the row, then throws BadImageFormatException "Malformed custom attribute header." from
        // the QCall, which this guest catches.
        let exc =
            runRefused
                "MalformedFriendAttribute.cs"
                """
        try
        {
            assembly.SetCustomAttribute(typeof(InternalsVisibleToAttribute).GetConstructor(new[] { typeof(string) }), new byte[] { 1, 0, 9, (byte)'F' });
            return 1;
        }
        catch (BadImageFormatException)
        {
        }"""

        exc.Message |> shouldContainText "TODO: TypeBuilder_DefineCustomAttribute"

        exc.Message
        |> shouldContainText "a friend attribute that PawPrint's friend scan rejects"
