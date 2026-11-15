namespace WoofWare.PawPrint.Test

open System.IO
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// Pins *where* a restricted-skip-visibility `DynamicMethod` is prepared for execution: at
/// `CreateDelegate`, through the `ReflectionInvocation_CompileMethod` QCall, before anything invokes
/// it. `sourcesImpure/DynamicMethodCompiledAtCreateDelegate.cs` pins what a guest can see of that;
/// this pins the registry's latch directly, so that it does not depend on any guest-visible effect.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestCompileMethodPreparation =

    /// Two ownerless methods, one built with `restrictedSkipVisibility: true` and one with `false`,
    /// each bound to a delegate and never invoked. The restricted one's `InitLocals` is cleared after
    /// `CreateDelegate`, which its latch must not hear.
    let private bindsWithoutInvoking =
        """
using System;
using System.Reflection.Emit;

class BindsWithoutInvoking
{
    static DynamicMethod Make(string name, bool restricted)
    {
        DynamicMethod dm = new DynamicMethod(name, typeof(int), Type.EmptyTypes, restricted);
        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4, 42);
        il.Emit(OpCodes.Ret);
        return dm;
    }

    static int Main()
    {
        DynamicMethod restricted = Make("Restricted", true);
        restricted.CreateDelegate(typeof(Func<int>));
        restricted.InitLocals = false;

        DynamicMethod unrestricted = Make("Unrestricted", false);
        unrestricted.CreateDelegate(typeof(Func<int>));
        return 0;
    }
}
"""

    /// The registry's entry for the one dynamic method the guest named `name`.
    let private dynamicMethodNamed (state : IlMachineState) (name : string) : DynamicMethodDefinition =
        match
            MethodHandleRegistry.dynamicMethods state.MethodHandles
            |> List.filter (fun (_, definition) -> definition.GetName () = name)
        with
        | [ _, definition ] -> definition
        | others -> failwith $"expected exactly one dynamic method named %s{name}, found %d{others.Length}"

    [<Test>]
    let ``CreateDelegate prepares a restricted method, and only a restricted one`` () : unit =
        let sourceName = "BindsWithoutInvoking.cs"
        let image = Roslyn.compile [ bindsWithoutInvoking ]

        let messages, loggerFactory =
            LoggerFactory.makeTestWithProperties [ "source_file", sourceName ]

        use _loggerFactoryResource = loggerFactory

        let dotnetRuntimes =
            FrameworkUnderTest.runtimeDirsFor KernelConfig.Default.UnixPlatform

        use peImage = new MemoryStream (image)

        // Reflection.Emit is off by default under PawPrint, so the guest would otherwise die of
        // `PlatformNotSupportedException` before emitting anything.
        let hostConfig = HostConfig.Default dotnetRuntimes

        let hostConfig =
            { hostConfig with
                Guest =
                    { hostConfig.Guest with
                        AppContext =
                            AppContextProperties.ofMap (
                                Map.ofList
                                    [
                                        "System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported", "true"
                                    ]
                            )
                    }
            }

        let terminalState =
            try
                match
                    BoundedRun.run loggerFactory sourceName (Some sourceName) peImage hostConfig
                    |> ExpectRun.ended
                with
                | RunOutcome.NormalExit (state, _, _)
                | RunOutcome.ProcessExit (state, _, _) -> state
                | other -> failwith $"guest did not exit normally: %O{other}"
            with _ ->
                for message in messages () do
                    System.Console.Error.WriteLine $"{message}"

                reraise ()

        terminalState.LatchedExitCode |> shouldEqual 0

        // Latched with `InitLocals` as it was when `CreateDelegate` ran, not as the guest left it.
        (dynamicMethodNamed terminalState "Restricted").GetPreparation ()
        |> shouldEqual (
            Some
                {
                    LocalsInit = true
                    CatchTypes = Map.empty
                }
        )

        // Nothing has invoked the unrestricted method, so nothing has prepared it.
        (dynamicMethodNamed terminalState "Unrestricted").GetPreparation ()
        |> shouldEqual None
