namespace WoofWare.PawPrint.Test

open System.IO
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// The static virtual resolution PawPrint refuses over a dynamic module's MethodImpl rows: a type created while
/// its module had fewer than 25 MethodImpl rows, resolved once the module has 25 or more, whose rows give one
/// body in the order added and another in reverse. CoreCLR walks the rows in the order added below 25 rows and
/// in reverse from 25, and caches a resolution where it makes one, so its answer there depends on whether it
/// resolved the call before the module crossed 25 rows (measured on .NET 10:
/// `TestStaticVirtualOverrideOrder`'s host test). The cases PawPrint answers are in
/// `sourcesImpure/TypeBuilderStaticVirtualOverrideOrder.cs`.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestStaticVirtualOverrideOrderRefusals =

    /// A guest whose `Main` runs `body` with `module` a dynamic module, `Fill` adding `count` MethodImpl rows to
    /// it on types of their own, and `Caller.CallDef<T>` making a static virtual call of `IDef.M` on `T`.
    let private source (body : string) : string =
        $$"""
using System;
using System.Reflection;
using System.Reflection.Emit;

public interface IDef { static virtual int M() => 0; }
public interface IFill { int F(); }

public static class Caller
{
    public static int CallDef<T>() where T : IDef => T.M();
}

public class Program
{
    static MethodBuilder StaticBody(TypeBuilder type, string name, int value)
    {
        MethodBuilder method = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, typeof(int), Type.EmptyTypes);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4, value);
        il.Emit(OpCodes.Ret);
        return method;
    }

    static void Fill(ModuleBuilder module, int count)
    {
        for (int i = 0; i < count; i++)
        {
            TypeBuilder type = module.DefineType("F" + i, TypeAttributes.Public);
            type.AddInterfaceImplementation(typeof(IFill));
            MethodBuilder method = type.DefineMethod("F", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Final | MethodAttributes.HideBySig, typeof(int), Type.EmptyTypes);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldc_I4, i);
            il.Emit(OpCodes.Ret);
            type.DefineMethodOverride(method, typeof(IFill).GetMethod("F"));
        }
    }

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
    let ``a type created below 25 rows whose rows disagree, resolved from 25, is refused`` () : unit =
        // Real .NET gives 2 if nothing resolved `IDef.M` on `C` before the 23 rows were added, and 1 at a call
        // site that did.
        let exc =
            runRefused
                "StaticVirtualOverrideWindow.cs"
                """
        TypeBuilder c = module.DefineType("C", TypeAttributes.Public);
        c.AddInterfaceImplementation(typeof(IDef));
        c.DefineMethodOverride(StaticBody(c, "B1", 1), typeof(IDef).GetMethod("M"));
        c.DefineMethodOverride(StaticBody(c, "B2", 2), typeof(IDef).GetMethod("M"));
        Type created = c.CreateType();
        Fill(module, 23);
        Console.WriteLine(typeof(Caller).GetMethod("CallDef").MakeGenericMethod(created).Invoke(null, null));"""

        exc.Message |> shouldContainText "TODO: static virtual dispatch"
        exc.Message |> shouldContainText "give B1 in the order added and B2 in reverse"

        exc.Message
        |> shouldContainText "had 2 MethodImpl rows, and the module now has 25"

    [<Test>]
    let ``an interface whose rows disagree, the most specific default body, is refused`` () : unit =
        // `I2`'s rows are the default-body search's only candidate, so its two orders are the call's two answers:
        // real .NET gives D1 at a call site that resolved before the 23 rows were added, and D2 at one that did not.
        let exc =
            runRefused
                "StaticVirtualOverrideWindowInterface.cs"
                """
        TypeBuilder i2 = module.DefineType("I2", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        i2.AddInterfaceImplementation(typeof(IDef));
        i2.DefineMethodOverride(StaticBody(i2, "D1", 1), typeof(IDef).GetMethod("M"));
        i2.DefineMethodOverride(StaticBody(i2, "D2", 2), typeof(IDef).GetMethod("M"));
        Type i2Created = i2.CreateType();
        TypeBuilder c = module.DefineType("C", TypeAttributes.Public);
        c.AddInterfaceImplementation(i2Created);
        Type created = c.CreateType();
        Fill(module, 23);
        Console.WriteLine(typeof(Caller).GetMethod("CallDef").MakeGenericMethod(created).Invoke(null, null));"""

        exc.Message |> shouldContainText "TODO: static virtual dispatch"
        exc.Message |> shouldContainText "the MethodImpl rows of I2"
        exc.Message |> shouldContainText "give D1 in the order added and D2 in reverse"

        exc.Message
        |> shouldContainText "had 2 MethodImpl rows, and the module now has 25"
