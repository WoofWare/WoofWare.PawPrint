namespace WoofWare.PawPrint.Test

open System.IO
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// The calls of `StubHelpers.NextCallReturnAddress` PawPrint refuses: every one except a `call`
/// whose address the next instruction pops, which is how CoreLib's reflection invoke stubs call it
/// (`sourcesImpure/ReflectionInvokeThroughEmittedStub.cs`).
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestNextCallReturnAddressRefusal =

    /// A guest whose `Main` runs `body`, with `nextCallReturnAddress` bound to the intrinsic.
    let private source (body : string) : string =
        $$"""
using System;
using System.Reflection;
using System.Reflection.Emit;

public class Program
{
    public static int Main(string[] args)
    {
        MethodInfo nextCallReturnAddress =
            typeof(object).Assembly.GetType("System.StubHelpers.StubHelpers")
                .GetMethod("NextCallReturnAddress", BindingFlags.NonPublic | BindingFlags.Static);
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
    let ``a call whose address is kept is refused`` () : unit =
        // Stored rather than popped. The later call is the one whose return address the JIT would
        // supply.
        let exc =
            runRefused
                "NextCallReturnAddressKept.cs"
                """
        DynamicMethod dm = new DynamicMethod("KeepsAddress", typeof(IntPtr), Type.EmptyTypes, typeof(object).Module, true);
        ILGenerator il = dm.GetILGenerator();
        il.DeclareLocal(typeof(IntPtr));
        il.Emit(OpCodes.Call, nextCallReturnAddress);
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, typeof(GC).GetMethod("KeepAlive"));
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ret);
        ((Func<IntPtr>) dm.CreateDelegate(typeof(Func<IntPtr>)))();"""

        exc.Message
        |> shouldContainText "PawPrint has no representation of a code address"

        exc.Message |> shouldContainText "KeepsAddress at IL_0000"

    [<Test>]
    let ``a reflective call is refused`` () : unit =
        // No IL call site: the runtime's reflection invoker makes the call.
        let exc =
            runRefused "NextCallReturnAddressInvoked.cs" """nextCallReturnAddress.Invoke(null, null);"""

        exc.Message
        |> shouldContainText "PawPrint has no representation of a code address"
