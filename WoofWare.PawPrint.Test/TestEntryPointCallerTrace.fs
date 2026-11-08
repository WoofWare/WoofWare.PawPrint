namespace WoofWare.PawPrint.Test

open System
open System.IO
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// The frames an exception escaping `Main` records, under PawPrint and under real .NET.
///
/// On .NET 11 the VM calls `Main` through CoreLib's `Environment.CallEntryPoint`, whose filter the
/// first pass visits on the way out, but CoreCLR records no frame for it in an exception's trace
/// (excep.cpp's `StackTraceInfo::AppendElement`). A guest cannot see that trace, since PawPrint
/// does not raise `AppDomain.UnhandledException`, so this reads it from the run's outcome, and
/// compares it with the trace real .NET prints.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestEntryPointCallerTrace =

    let private source : string =
        """
using System;

public class Program
{
    static void Thrower ()
    {
        throw new InvalidOperationException ("escapes Main");
    }

    public static int Main (string[] args)
    {
        Thrower ();
        return 0;
    }
}
"""

    let private image : Lazy<byte[]> = lazy (Roslyn.compile [ source ])

    /// The method name of each `   at Type.Method(...)` line of a printed trace, in order.
    let private printedFrameNames (report : string) : string list =
        report.Split '\n'
        |> Array.choose (fun line ->
            let line = line.Trim ()

            if line.StartsWith ("at ", StringComparison.Ordinal) then
                let qualified = line.Substring(3).Split('(').[0]
                Some (qualified.Substring (qualified.LastIndexOf '.' + 1))
            else
                None
        )
        |> Array.toList

    [<Test>]
    let ``an exception escaping Main records no frame beneath Main`` () : unit =
        let messages, loggerFactory = LoggerFactory.makeTest ()
        use _loggerFactoryResource = loggerFactory

        let real, interpreted =
            DifferentialOracle.alongsideInterpreted
                (fun () -> RealRuntime.executeWithRealRuntime [||] image.Value)
                (fun () ->
                    use peImage = new MemoryStream (image.Value)

                    let outcome =
                        try
                            Program.run
                                loggerFactory
                                (Some "EntryPointCallerTrace")
                                peImage
                                (HostConfig.Default (
                                    FrameworkUnderTest.runtimeDirsFor KernelConfig.Default.UnixPlatform
                                ))
                        with _ ->
                            for message in messages () do
                                Console.Error.WriteLine $"{message}"

                            reraise ()

                    match ExpectRun.ended outcome with
                    | RunOutcome.GuestUnhandledException (_, _, exn, _) ->
                        exn.Trace.Frames |> List.map (fun frame -> frame.Method.Name)
                    | other -> failwith $"expected the guest's exception to escape Main; the run ended %O{other}"
                )

        let expected = [ "Thrower" ; "Main" ]

        match real with
        | RealRuntimeResult.UnhandledException report -> printedFrameNames report |> shouldEqual expected
        | other -> failwith $"expected real .NET to report the guest's exception as unhandled; it ended %O{other}"

        interpreted |> shouldEqual expected
