namespace WoofWare.PawPrint.Test

open NUnit.Framework
open FsUnitTyped
open WoofWare.PawPrint

/// `TrustedPlatformAssemblies.tryCreate` against CoreCLR's reading of the TPA string
/// (`GetNextTPAPath` in `src/coreclr/binder/utils.cpp`, and `ApplicationContext::SetupBindingPaths`
/// in `src/coreclr/binder/applicationcontext.cpp`, which keys a case-insensitive map by the file
/// name without its extension).
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestTrustedPlatformAssemblies =

    let private shouldBe<'a when 'a : equality> (expected : 'a) (actual : 'a) : unit =
        if actual <> expected then
            Assert.Fail $"Expected:\n%A{expected}\nActual:\n%A{actual}"

    let private create (entries : (string * string) list) : Result<Map<string, string>, TpaRefusal> =
        TrustedPlatformAssemblies.tryCreate (Map.ofList entries)
        |> Result.map TrustedPlatformAssemblies.toMap

    [<Test>]
    let ``A map of absolute dll paths keyed by their stems is a TPA`` () =
        let entries =
            [
                "System.Private.CoreLib", "/fx/System.Private.CoreLib.dll"
                "A", "/app/A.dll"
                "B.C", "/app/sub/B.C.DLL"
                "", "/app/.dll"
                "With Space", "/a b/With Space.dll"
            ]

        create entries |> shouldBe (Ok (Map.ofList entries))

    [<Test>]
    let ``An empty map is a TPA`` () = create [] |> shouldBe (Ok Map.empty)

    [<Test>]
    let ``A relative path is refused`` () =
        create [ "A", "app/A.dll" ]
        |> shouldBe (Error (TpaRefusal.RelativePath ("A", "app/A.dll")))

        create [ "A", "\"/app/A.dll\"" ]
        |> shouldBe (Error (TpaRefusal.RelativePath ("A", "\"/app/A.dll\"")))

    [<Test>]
    let ``A path holding the path-list separator is refused`` () =
        create [ "A", "/colon:dir/A.dll" ]
        |> shouldBe (Error (TpaRefusal.HoldsPathListSeparator ("A", "/colon:dir/A.dll")))

    [<Test>]
    let ``A path ending with a space is refused`` () =
        create [ "A", "/app/A.dll " ]
        |> shouldBe (Error (TpaRefusal.EndsWithSpace ("A", "/app/A.dll ")))

    [<TestCase("/app/A.txt")>]
    [<TestCase("/app/A")>]
    [<TestCase("/app/")>]
    [<TestCase("/app/A.dll/")>]
    let ``A path with no assembly extension is refused`` (path : string) =
        create [ "A", path ]
        |> shouldBe (Error (TpaRefusal.NoAssemblyExtension ("A", path)))

    [<TestCase("A", "/app/A.exe")>]
    [<TestCase("A", "/app/A.EXE")>]
    [<TestCase("A", "/app/A.ni.dll")>]
    [<TestCase("A", "/app/A.NI.Dll")>]
    [<TestCase("A.ni", "/app/A.ni.dll")>]
    [<TestCase("A", "/app/A.ni.exe")>]
    let ``A path to a file other than a dll is refused`` (name : string, path : string) =
        create [ name, path ] |> shouldBe (Error (TpaRefusal.NotDll (name, path)))

    [<TestCase("A", "/app/B.dll")>]
    [<TestCase("a", "/app/A.dll")>]
    [<TestCase("A", "/app/A.dll.dll")>]
    [<TestCase("A.dll", "/app/A.dll")>]
    let ``A name which is not the file stem is refused`` (name : string, path : string) =
        create [ name, path ]
        |> shouldBe (Error (TpaRefusal.NameIsNotFileStem (name, path)))

    [<Test>]
    let ``Names equal ignoring case are refused`` () =
        create [ "System.Xml", "/fx/System.Xml.dll" ; "system.xml", "/app/system.xml.dll" ]
        |> shouldBe (Error (TpaRefusal.NamesEqualIgnoringCase ("System.Xml", "system.xml")))

    [<Test>]
    let ``Names equal under invariant lower casing but not under upper casing are refused`` () =
        // The Kelvin sign lowers to `k` under the invariant culture, and upper casing leaves it as
        // it is.
        let kelvin = string (char 0x212A)

        create [ "K", "/app/K.dll" ; kelvin, $"/app/%s{kelvin}.dll" ]
        |> shouldBe (Error (TpaRefusal.NamesEqualIgnoringCase ("K", kelvin)))

    [<Test>]
    let ``Names equal under invariant upper casing but not under lower casing are refused`` () =
        // The long s uppers to `S` under the invariant culture, and lower casing leaves it as it
        // is.
        let longS = string (char 0x17F)

        create [ "s", "/app/s.dll" ; longS, $"/app/%s{longS}.dll" ]
        |> shouldBe (Error (TpaRefusal.NamesEqualIgnoringCase ("s", longS)))

    [<Test>]
    let ``The first refusal in ordinal order of name is returned`` () =
        create [ "B", "/app/B.exe" ; "A", "relative/A.dll" ; "C", "/app/C.txt" ]
        |> shouldBe (Error (TpaRefusal.RelativePath ("A", "relative/A.dll")))

    [<Test>]
    let ``A per-entry refusal comes before a collision`` () =
        create [ "a", "/app/a.dll" ; "A", "/app/A.dll" ; "Z", "/app/Z.exe" ]
        |> shouldBe (Error (TpaRefusal.NotDll ("Z", "/app/Z.exe")))

    [<Test>]
    let ``tryFindIgnoringCase finds a name in any case`` () =
        let tpa =
            TrustedPlatformAssemblies.tryCreate (Map.ofList [ "System.Xml", "/fx/System.Xml.dll" ; "B", "/app/B.dll" ])
            |> Result.defaultWith (fun e -> failwith $"%A{e}")

        TrustedPlatformAssemblies.tryFindIgnoringCase "SYSTEM.XML" tpa
        |> shouldBe (Some ("System.Xml", "/fx/System.Xml.dll"))

        TrustedPlatformAssemblies.tryFindIgnoringCase "b" tpa
        |> shouldBe (Some ("B", "/app/B.dll"))

        TrustedPlatformAssemblies.tryFindIgnoringCase "C" tpa |> shouldBe None
