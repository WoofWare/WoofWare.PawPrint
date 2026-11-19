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

    /// The Kelvin sign and the long s fold to ASCII letters under .NET's invariant casing; dotless
    /// `ı` folds to `I` under CoreCLR's on Unix, so .NET 10 reads `Ztest.nı.dll` as a native image
    /// of `Ztest`.
    [<TestCase(0x212A, "K")>]
    [<TestCase(0x17F, "s")>]
    [<TestCase(0x131, "I")>]
    let ``A file name outside ASCII is refused`` (codePoint : int, ascii : string) =
        let letter = string (char codePoint)
        let path = $"/app/%s{letter}.dll"

        create [ ascii, $"/app/%s{ascii}.dll" ; letter, path ]
        |> shouldBe (Error (TpaRefusal.NonAsciiFileName (letter, path)))

        let nativeImage = $"/app/Ztest.n%s{letter}.dll"

        create [ $"Ztest.n%s{letter}", nativeImage ]
        |> shouldBe (Error (TpaRefusal.NonAsciiFileName ($"Ztest.n%s{letter}", nativeImage)))

    [<Test>]
    let ``A directory outside ASCII is accepted`` () =
        let path = $"/Jos%s{string (char 0xE9)}/A.dll"
        create [ "A", path ] |> shouldBe (Ok (Map.ofList [ "A", path ]))

    [<Test>]
    let ``A path holding NUL is refused`` () =
        let path = "/app/a" + string (char 0) + "b/A.dll"
        create [ "A", path ] |> shouldBe (Error (TpaRefusal.HoldsNul ("A", path)))

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

    /// CoreCLR's map hashes and compares names upper-cased, and the Kelvin sign is upper case
    /// already, so it does not find `K` (measured by Codex on .NET 10). The long s uppers to `S`.
    [<Test>]
    let ``tryFindIgnoringCase compares upper-cased names`` () =
        let tpa =
            TrustedPlatformAssemblies.tryCreate (Map.ofList [ "K", "/app/K.dll" ; "S", "/app/S.dll" ])
            |> Result.defaultWith (fun e -> failwith $"%A{e}")

        TrustedPlatformAssemblies.tryFindIgnoringCase (string (char 0x212A)) tpa
        |> shouldBe None

        TrustedPlatformAssemblies.tryFindIgnoringCase (string (char 0x17F)) tpa
        |> shouldBe (Some ("S", "/app/S.dll"))

    [<Test>]
    let ``tryFindIgnoringCase matches dotless i to I, as CoreCLR does on Unix`` () =
        let tpa =
            TrustedPlatformAssemblies.tryCreate (Map.ofList [ "ZtestI", "/app/ZtestI.dll" ])
            |> Result.defaultWith (fun e -> failwith $"%A{e}")

        TrustedPlatformAssemblies.tryFindIgnoringCase ("Ztest" + string (char 0x131)) tpa
        |> shouldBe (Some ("ZtestI", "/app/ZtestI.dll"))
