namespace WoofWare.PawPrint.Test

open System
open System.IO
open System.Threading
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open Microsoft.CodeAnalysis
open NUnit.Framework
open NUnit.Framework.Internal
open WoofWare.PawPrint

/// `RealRuntime.captureAssemblyInPlace` and its echoing form `RealRuntime.executeAssemblyInPlace`:
/// an assembly on disk runs where it lies, with the AppContext properties it is given, and
/// nothing is written into its directory.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestRealRuntimeInPlace =

    /// A property no test ever passes, so it reads as null unless something other than the
    /// oracle's own runtimeconfig reached the guest.
    [<Literal>]
    let private AbsentProperty = "WoofWare.Test.Absent"

    /// The property the runtimeconfig beside the assembly names, in the tests that put one there.
    [<Literal>]
    let private BesideProperty = "WoofWare.Test.Beside"

    /// Prints a line `<name>=<value>` for each property its arguments name, the value as the
    /// four-digit hex of each UTF-16 code unit (so that any value is one line of ASCII), or `null`.
    let private printsPropertiesImage : Lazy<byte[]> =
        lazy
            Roslyn.compileAssembly
                "PrintsProperties"
                OutputKind.ConsoleApplication
                []
                [
                    """
using System;
using System.Text;

public static class Program
{
    public static int Main(string[] args)
    {
        foreach (var name in args)
        {
            object value = AppContext.GetData(name);
            string shown;
            if (value is null)
            {
                shown = "null";
            }
            else if (value is string text)
            {
                var hex = new StringBuilder();
                foreach (char c in text)
                {
                    hex.Append(((int)c).ToString("X4"));
                }
                shown = hex.ToString();
            }
            else
            {
                shown = "not a string: " + value.GetType().FullName;
            }
            Console.WriteLine(name + "=" + shown);
        }
        return 0;
    }
}
"""
                ]

    /// What `printsPropertiesImage` prints for `name` when the guest sees `value`.
    let private printedLine (name : string) (value : string option) : string =
        let shown =
            match value with
            | None -> "null"
            | Some text -> text |> Seq.map (fun c -> (int c).ToString "X4") |> String.concat ""

        name + "=" + shown + Environment.NewLine

    /// A runtimeconfig that the host would refuse to start the guest with, since it names a
    /// framework version that does not exist and forbids rolling forward; and that names
    /// `BesideProperty`, so a guest started from it anyway would show it.
    let private besideRuntimeConfig : string =
        $$"""{
  "runtimeOptions": {
    "tfm": "net10.0",
    "rollForward": "Disable",
    "framework": { "name": "Microsoft.NETCore.App", "version": "0.0.1" },
    "configProperties": { "{{BesideProperty}}": "beside", "WoofWare.Test.Given": "beside" }
  }
}
"""

    /// Write `image` as `<assemblyName>.dll` into a new directory of its own under `parent`, with
    /// `besideConfig` as its `<assemblyName>.runtimeconfig.json` if given; run `body` on the DLL's
    /// path; then remove the directory.
    let private laidOutUnder<'a>
        (parent : string)
        (assemblyName : string)
        (image : byte[])
        (besideConfig : string option)
        (body : string -> 'a)
        : 'a
        =
        let directory =
            Path.Combine (parent, "pawprint-in-place-test-" + Path.GetRandomFileName ())

        Directory.CreateDirectory directory |> ignore<DirectoryInfo>

        try
            let dllPath = Path.Combine (directory, assemblyName + ".dll")
            File.WriteAllBytes (dllPath, image)

            match besideConfig with
            | Some contents ->
                File.WriteAllText (Path.Combine (directory, assemblyName + ".runtimeconfig.json"), contents)
            | None -> ()

            body dllPath
        finally
            Directory.Delete (directory, true)

    /// `laidOutUnder` the temp directory.
    let private laidOut<'a>
        (assemblyName : string)
        (image : byte[])
        (besideConfig : string option)
        (body : string -> 'a)
        : 'a
        =
        laidOutUnder (Path.GetTempPath ()) assemblyName image besideConfig body

    /// Every file in `dllPath`'s directory, by name, with its bytes: as text for a JSON file, so
    /// that a failure shows what changed, and otherwise as their SHA-256.
    let private directoryContents (dllPath : string) : (string * string) list =
        Directory.GetFiles (Path.GetDirectoryName dllPath)
        |> Array.sort
        |> Array.map (fun path ->
            let bytes = File.ReadAllBytes path

            let shown =
                if path.EndsWith (".json", StringComparison.Ordinal) then
                    "text: " + Text.Encoding.UTF8.GetString bytes
                else
                    "SHA-256: " + Convert.ToHexString (Security.Cryptography.SHA256.HashData bytes)

            Path.GetFileName path, shown
        )
        |> List.ofArray

    /// A property name: the test's own prefix, so that no name collides with one the host or the
    /// runtime gives meaning to.
    let private genName : Gen<string> =
        gen {
            let! length = Gen.choose (1, 12)

            let! chars =
                Gen.listOfLength length (Gen.elements ("abcdefghijklmnopqrstuvwxyz0123456789._-" |> List.ofSeq))

            return "WoofWare.Test.Generated." + System.String (Array.ofList chars)
        }

    /// A property value. JSON escaping, non-ASCII text and characters outside the BMP are what a
    /// runtimeconfig writer can get wrong; a lone surrogate has no UTF-8 encoding, so no
    /// runtimeconfig can carry one, and none is drawn.
    let private genValue : Gen<string> =
        let pieces =
            [
                "plain"
                ""
                " "
                "\""
                "\\"
                "/"
                "\n"
                "\t"
                "\u0001"
                "é"
                "中文"
                "\U0001F600"
                "="
                "{}"
                "null"
                "true"
                "0"
            ]

        gen {
            let! count = Gen.choose (0, 4)
            let! chosen = Gen.listOfLength count (Gen.elements pieces)
            return String.concat "" chosen
        }

    let private genProperties : Gen<Map<string, string>> =
        gen {
            let! count = Gen.choose (0, 4)
            let! names = Gen.listOfLength count genName
            let! values = Gen.listOfLength count genValue
            return List.zip names values |> Map.ofList
        }

    /// Run `printsPropertiesImage` at `dllPath` with `properties`, asking for each of them and
    /// for `AbsentProperty`, and check it printed exactly what it was given.
    let private seesExactly (dllPath : string) (properties : Map<string, string>) : unit =
        let names = (properties |> Map.toList |> List.map fst) @ [ AbsentProperty ]

        let run =
            RealRuntime.captureAssemblyInPlace (AppContextProperties.ofMap properties) (Array.ofList names) dllPath

        let expected =
            names
            |> List.map (fun name -> printedLine name (Map.tryFind name properties))
            |> String.concat ""

        (run.Result, run.Stdout, run.Stderr)
        |> shouldEqual (RealRuntimeResult.NormalExit 0, expected, "")

    [<Test>]
    let ``the guest sees exactly the AppContext properties it is given`` () : unit =
        laidOut
            "PrintsProperties"
            printsPropertiesImage.Value
            None
            (fun dllPath ->
                // Each case starts a process.
                Check.One (
                    Config.QuickThrowOnFailure.WithMaxTest 15,
                    Prop.forAll (Arb.fromGen genProperties) (seesExactly dllPath)
                )
            )

    [<Test>]
    let ``a runtimeconfig beside the assembly is neither read nor changed`` () : unit =
        laidOut
            "PrintsProperties"
            printsPropertiesImage.Value
            (Some besideRuntimeConfig)
            (fun dllPath ->
                let before = directoryContents dllPath

                let run =
                    RealRuntime.captureAssemblyInPlace
                        (AppContextProperties.ofMap (Map.ofList [ "WoofWare.Test.Given", "given" ]))
                        [| "WoofWare.Test.Given" ; BesideProperty |]
                        dllPath

                (run.Result, run.Stdout, run.Stderr)
                |> shouldEqual (
                    RealRuntimeResult.NormalExit 0,
                    printedLine "WoofWare.Test.Given" (Some "given")
                    + printedLine BesideProperty None,
                    ""
                )

                directoryContents dllPath |> shouldEqual before
            )

    /// Run `printsPropertiesImage` with `properties`, and return the message of the exception the
    /// oracle raises, failing if it raises none.
    let private refusal (properties : Map<string, string>) : string =
        laidOut
            "PrintsProperties"
            printsPropertiesImage.Value
            None
            (fun dllPath ->
                let names = properties |> Map.toList |> List.map fst |> Array.ofList

                let outcome =
                    try
                        Ok (RealRuntime.captureAssemblyInPlace (AppContextProperties.ofMap properties) names dllPath)
                    with e ->
                        Error e.Message

                match outcome with
                | Error message -> message
                | Ok run ->
                    failwith $"expected the oracle to refuse the properties %A{properties}, but it ran: %A{run}"
            )

    /// A real host refuses a runtimeconfig naming one of these before the guest starts, with an
    /// exit code that would otherwise read as the guest's.
    [<TestCase "TRUSTED_PLATFORM_ASSEMBLIES">]
    [<TestCase "HOST_RUNTIME_CONTRACT">]
    let ``a property the hosting layer populates is refused, not run`` (name : string) : unit =
        refusal (Map.ofList [ name, "value" ]) |> shouldContainText name

    [<Test>]
    let ``APP_PATHS alongside the switch that makes the host set it is refused, not run`` () : unit =
        refusal (
            Map.ofList
                [
                    "APP_PATHS", "value"
                    "Microsoft.NETCore.DotNetHostPolicy.SetAppPaths", "true"
                ]
        )
        |> shouldContainText "APP_PATHS"

    /// A lone surrogate has no UTF-8 encoding, so no runtimeconfig can carry it as given.
    [<Test>]
    let ``a property name or value that is not well-formed UTF-16 is refused, not run`` () : unit =
        let loneSurrogate = string (char 0xD800)

        refusal (Map.ofList [ "WoofWare.Test.Value", "a" + loneSurrogate ])
        |> shouldContainText "WoofWare.Test.Value"

        refusal (Map.ofList [ "WoofWare.Test." + loneSurrogate, "value" ])
        |> shouldContainText "UTF-16"

    [<Test>]
    let ``concurrent runs of one assembly each see their own properties`` () : unit =
        // More than two, started together, and several rounds of them: two runs that read each
        // other's runtimeconfig only do so when their starts overlap, which nothing here can
        // force, so this makes the overlap likely rather than certain.
        let runsPerRound = 8

        laidOut
            "PrintsProperties"
            printsPropertiesImage.Value
            None
            (fun dllPath ->
                for round in 1..3 do
                    use barrier = new Barrier (runsPerRound)

                    let runs =
                        List.init
                            runsPerRound
                            (fun index ->
                                let value = $"round %d{round} run %d{index}"

                                let task =
                                    Tasks.Task.Factory.StartNew (
                                        (fun () ->
                                            barrier.SignalAndWait ()

                                            RealRuntime.captureAssemblyInPlace
                                                (AppContextProperties.ofMap (
                                                    Map.ofList [ "WoofWare.Test.Concurrent", value ]
                                                ))
                                                [| "WoofWare.Test.Concurrent" |]
                                                dllPath
                                        ),
                                        Tasks.TaskCreationOptions.LongRunning
                                    )

                                value, task
                            )

                    // Every run is awaited before any failure is reported, so that none is still
                    // running when `laidOut` removes the assembly's directory.
                    (try
                        runs
                        |> List.map snd
                        |> Seq.cast<Tasks.Task>
                        |> Seq.toArray
                        |> Tasks.Task.WaitAll
                     with _ ->
                         ())

                    runs
                    |> List.map (fun (value, task) ->
                        let run = task.GetAwaiter().GetResult ()
                        value, (run.Result, run.Stdout, run.Stderr)
                    )
                    |> shouldEqual (
                        runs
                        |> List.map (fun (value, _) ->
                            value,
                            (RealRuntimeResult.NormalExit 0, printedLine "WoofWare.Test.Concurrent" (Some value), "")
                        )
                    )
            )

    /// Writes two lines to stdout, the second with no newline and the first ended by "\r\n", one
    /// line to stderr, and exits 3; or, given the argument `throw`, writes one line to stdout and
    /// throws.
    let private writesImage : Lazy<byte[]> =
        lazy
            Roslyn.compileAssembly
                "Writes"
                OutputKind.ConsoleApplication
                []
                [
                    """
using System;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "throw")
        {
            Console.Out.Write("before the throw\n");
            Console.Out.Flush();
            throw new InvalidOperationException("thrown by the guest");
        }
        Console.Out.Write("first\r\nsecond");
        Console.Error.Write("on stderr\n");
        return 3;
    }
}
"""
                ]

    [<Test>]
    let ``stdout, stderr and the ending are captured, as lines`` () : unit =
        laidOut
            "Writes"
            writesImage.Value
            None
            (fun dllPath ->
                RealRuntime.captureAssemblyInPlace AppContextProperties.empty [||] dllPath
                |> shouldEqual
                    {
                        Result = RealRuntimeResult.NormalExit 3
                        Stdout = "first" + Environment.NewLine + "second" + Environment.NewLine
                        Stderr = "on stderr" + Environment.NewLine
                    }

                let thrown =
                    RealRuntime.captureAssemblyInPlace AppContextProperties.empty [| "throw" |] dllPath

                thrown.Stdout |> shouldEqual ("before the throw" + Environment.NewLine)

                match thrown.Result with
                | RealRuntimeResult.UnhandledException report ->
                    report
                    |> shouldContainText "System.InvalidOperationException: thrown by the guest"
                | other -> failwith $"expected an unhandled exception, got %A{other}"
            )

    /// Exits as its first argument says: `exit <n>`, `throw`, or `failfast`; or, given `args`,
    /// prints its command line joined by `|`, the first element by its file name alone, and exits
    /// 0; or, given `framework`, prints the directory CoreLib was loaded from and exits 0; or,
    /// given `cwd`, prints the name of its working directory and whether `EndsAsTold.dll` is in
    /// it, and exits 0.
    ///
    /// The host resolves symbolic links in these paths (`/tmp` on macOS, the Nix store's links),
    /// so the guest cannot print one as the test spells it.
    let private endsAsToldImage : Lazy<byte[]> =
        lazy
            Roslyn.compileAssembly
                "EndsAsTold"
                OutputKind.ConsoleApplication
                []
                [
                    """
using System;
using System.IO;

public static class Program
{
    public static int Main(string[] args)
    {
        switch (args[0])
        {
            case "exit":
                return int.Parse(args[1]);
            case "throw":
                throw new InvalidOperationException("thrown by the guest");
            case "failfast":
                Environment.FailFast("failed fast by the guest");
                return 1;
            case "args":
                var commandLine = Environment.GetCommandLineArgs();
                commandLine[0] = Path.GetFileName(commandLine[0]);
                Console.WriteLine("command line: " + string.Join("|", commandLine));
                return 0;
            case "framework":
                Console.WriteLine("framework: " + Path.GetDirectoryName(typeof(object).Assembly.Location));
                return 0;
            case "cwd":
                Console.WriteLine("working directory: " + Path.GetFileName(Directory.GetCurrentDirectory()) + " " + File.Exists("EndsAsTold.dll"));
                return 0;
            default:
                return 100;
        }
    }
}
"""
                ]

    [<Test>]
    let ``executeAssemblyInPlace reports how the guest ended`` () : unit =
        laidOut
            "EndsAsTold"
            endsAsToldImage.Value
            None
            (fun dllPath ->
                RealRuntime.executeAssemblyInPlace [| "exit" ; "42" |] dllPath
                |> shouldEqual (RealRuntimeResult.NormalExit 42)

                match RealRuntime.executeAssemblyInPlace [| "throw" |] dllPath with
                | RealRuntimeResult.UnhandledException report ->
                    report
                    |> shouldContainText "System.InvalidOperationException: thrown by the guest"
                | other -> failwith $"expected an unhandled exception, got %A{other}"

                match RealRuntime.executeAssemblyInPlace [| "failfast" |] dllPath with
                | RealRuntimeResult.Aborted (ObservedFatalError.FailFast, report) ->
                    report |> shouldContainText "failed fast by the guest"
                | other -> failwith $"expected a FailFast abort, got %A{other}"
            )

    /// The oracle's scratch directory goes in the temp directory, so an assembly lying there
    /// directly would see it in its own directory.
    [<Test>]
    let ``an assembly directly in the temp directory is refused, not run`` () : unit =
        let dllPath =
            Path.Combine (Path.GetTempPath (), "pawprint-in-place-test-" + Path.GetRandomFileName () + ".dll")

        File.WriteAllBytes (dllPath, endsAsToldImage.Value)

        try
            let outcome =
                try
                    Ok (RealRuntime.captureAssemblyInPlace AppContextProperties.empty [| "exit" ; "0" |] dllPath)
                with e ->
                    Error e.Message

            match outcome with
            | Error message -> message |> shouldContainText "temp directory"
            | Ok run -> failwith $"expected the oracle to refuse an assembly in the temp directory, but it ran: %A{run}"
        finally
            File.Delete dllPath

    /// As above, with the temp directory reached through a symbolic link, which the oracle must
    /// see through.
    [<Test>]
    let ``an assembly in the temp directory, reached through a symbolic link, is refused, not run`` () : unit =
        let name = "pawprint-in-place-test-" + Path.GetRandomFileName ()
        let dllPath = Path.Combine (Path.GetTempPath (), name + ".dll")
        let linkParent = Path.Combine (Path.GetTempPath (), name)
        Directory.CreateDirectory linkParent |> ignore<DirectoryInfo>

        try
            File.WriteAllBytes (dllPath, endsAsToldImage.Value)
            let link = Path.Combine (linkParent, "temp")

            Directory.CreateSymbolicLink (link, Path.GetTempPath ())
            |> ignore<FileSystemInfo>

            let outcome =
                try
                    Ok (
                        RealRuntime.captureAssemblyInPlace
                            AppContextProperties.empty
                            [| "exit" ; "0" |]
                            (Path.Combine (link, name + ".dll"))
                    )
                with e ->
                    Error e.Message

            match outcome with
            | Error message -> message |> shouldContainText "temp directory"
            | Ok run -> failwith $"expected the oracle to refuse an assembly in the temp directory, but it ran: %A{run}"
        finally
            File.Delete dllPath
            // Not recursive, so as not to follow the link into the temp directory.
            File.Delete (Path.Combine (linkParent, "temp"))
            Directory.Delete linkParent

    [<Test>]
    let ``a relative path to the assembly names the same assembly`` () : unit =
        // Under the test host's working directory, so that the relative path is a directory and
        // a file name: one that climbed with `..` past the root of the guest's working directory
        // would resolve to the same file from there too.
        laidOutUnder
            Environment.CurrentDirectory
            "EndsAsTold"
            endsAsToldImage.Value
            None
            (fun dllPath ->
                let relative = Path.GetRelativePath (Environment.CurrentDirectory, dllPath)

                relative
                |> shouldEqual (Path.Combine (Path.GetFileName (Path.GetDirectoryName dllPath), "EndsAsTold.dll"))

                RealRuntime.captureAssemblyInPlace AppContextProperties.empty [| "exit" ; "5" |] relative
                |> _.Result
                |> shouldEqual (RealRuntimeResult.NormalExit 5)
            )

    [<Test>]
    let ``the guest's working directory is the assembly's directory`` () : unit =
        laidOut
            "EndsAsTold"
            endsAsToldImage.Value
            None
            (fun dllPath ->
                let run =
                    RealRuntime.captureAssemblyInPlace AppContextProperties.empty [| "cwd" |] dllPath

                (run.Result, run.Stdout)
                |> shouldEqual (
                    RealRuntimeResult.NormalExit 0,
                    $"working directory: %s{Path.GetFileName (Path.GetDirectoryName dllPath)} True"
                    + Environment.NewLine
                )
            )

    [<Test>]
    let ``executeAssemblyInPlace starts the guest with its arguments after the assembly's path`` () : unit =
        laidOut
            "EndsAsTold"
            endsAsToldImage.Value
            None
            (fun dllPath ->
                RealRuntime.executeAssemblyInPlace [| "args" ; "two words" ; "" |] dllPath
                |> shouldEqual (RealRuntimeResult.NormalExit 0)

                // Echoed to the test's own output.
                TestExecutionContext.CurrentContext.CurrentResult.Output
                |> shouldContainText ("command line: EndsAsTold.dll|args|two words|" + Environment.NewLine)

                let run =
                    RealRuntime.captureAssemblyInPlace
                        AppContextProperties.empty
                        [| "args" ; "two words" ; "" |]
                        dllPath

                (run.Result, run.Stdout)
                |> shouldEqual (
                    RealRuntimeResult.NormalExit 0,
                    "command line: EndsAsTold.dll|args|two words|" + Environment.NewLine
                )
            )

    [<Test>]
    let ``executeAssemblyInPlace runs the framework under test and leaves the runtimeconfig beside the assembly alone``
        ()
        : unit
        =
        laidOut
            "EndsAsTold"
            endsAsToldImage.Value
            (Some besideRuntimeConfig)
            (fun dllPath ->
                let before = directoryContents dllPath

                RealRuntime.executeAssemblyInPlace [| "framework" |] dllPath
                |> shouldEqual (RealRuntimeResult.NormalExit 0)

                // Echoed to the test's own output.
                let printed =
                    TestExecutionContext.CurrentContext.CurrentResult.Output.Split Environment.NewLine
                    |> Array.choose (fun line ->
                        if line.StartsWith ("framework: ", StringComparison.Ordinal) then
                            Some (line.Substring "framework: ".Length)
                        else
                            None
                    )

                printed
                |> Array.map FrameworkUnderTest.resolveLinks
                |> shouldEqual
                    [|
                        FrameworkUnderTest.resolveLinks (FrameworkUnderTest.sharedFrameworkDirectory ())
                    |]

                directoryContents dllPath |> shouldEqual before
            )
