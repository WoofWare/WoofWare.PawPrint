namespace WoofWare.PawPrint.Test

open System
open System.Collections.Immutable
open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open System.Runtime.Loader
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open NUnit.Framework
open WoofWare.PawPrint

[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestImageAdmission =

    /// What the test host's own CoreCLR made of loading an image into a fresh collectible
    /// `AssemblyLoadContext`: `LoadFromStream` runs the binder's `Assembly::Init` and then
    /// `Assembly::Create`, the two places CoreCLR refuses an image, just as binding a reference
    /// does (with one difference of HRESULT, noted at `hostAgrees`).
    [<RequireQualifiedAccess>]
    type private HostLoad =
        | Loaded
        | Refused of exceptionType : string * hresult : int

    let private hostLoad (image : byte[]) : HostLoad =
        let context = AssemblyLoadContext ("image-admission", isCollectible = true)

        try
            try
                use stream = new MemoryStream (image)
                context.LoadFromStream stream |> ignore<System.Reflection.Assembly>
                HostLoad.Loaded
            with e ->
                HostLoad.Refused (e.GetType().FullName, e.HResult)
        finally
            context.Unload ()

    let private corEBadImageFormat = 0x8007000B
    let private fusionEInvalidName = 0x80131047
    let private corELoadingReferenceAssembly = 0x80131058
    let private clrEBindArchitectureMismatch = 0x80132006

    /// Whether PawPrint's verdict on an image is the one the host's CoreCLR reached.
    let private hostAgrees (refusal : ImageRefusal option) (host : HostLoad) : bool =
        match refusal, host with
        | None, HostLoad.Loaded -> true
        | None, HostLoad.Refused _ -> false
        // CoreCLR loads an image built for one machine in a process of that machine's
        // architecture and no other; PawPrint models no process architecture, so it refuses
        // every such image, and the host may land on either side. Loading from a stream reports
        // the mismatch as CLR_E_BIND_ARCHITECTURE_MISMATCH where binding a reference reports
        // COR_E_BADIMAGEFORMAT.
        | Some (ImageRefusal.MachineSpecific _), HostLoad.Loaded -> true
        | Some (ImageRefusal.MachineSpecific _), HostLoad.Refused (_, hresult) ->
            hresult = clrEBindArchitectureMismatch || hresult = corEBadImageFormat
        | Some _, HostLoad.Loaded -> false
        | Some (ImageRefusal.NoArchitecture _), HostLoad.Refused (exceptionType, hresult) ->
            exceptionType = "System.BadImageFormatException" && hresult = corEBadImageFormat
        | Some (ImageRefusal.ContentType _), HostLoad.Refused (exceptionType, hresult) ->
            exceptionType = "System.IO.FileLoadException" && hresult = fusionEInvalidName
        | Some ImageRefusal.ReferenceAssembly, HostLoad.Refused (exceptionType, hresult) ->
            exceptionType = "System.BadImageFormatException"
            && hresult = corELoadingReferenceAssembly
        | Some (ImageRefusal.NoPlatform _), HostLoad.Refused (exceptionType, hresult) ->
            exceptionType = "System.BadImageFormatException" && hresult = corEBadImageFormat

    let private refusalOf (image : byte[]) : ImageRefusal option =
        use stream = new MemoryStream (image)
        use peReader = new PEReader (stream)
        ImageAdmission.refusal "the image under test" peReader

    let private compile (platform : Platform) (source : string) : byte[] =
        let compilation =
            CSharpCompilation.Create (
                "ImageAdmission",
                [ CSharpSyntaxTree.ParseText source ],
                Roslyn.metadataReferences [],
                CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithPlatform platform
            )

        use stream = new MemoryStream ()
        let result = compilation.Emit stream

        if not result.Success then
            let errors =
                result.Diagnostics
                |> Seq.filter (fun d -> d.Severity = DiagnosticSeverity.Error)
                |> Seq.map string<Diagnostic>
                |> String.concat Environment.NewLine

            failwith $"Compilation failed:\n%s{errors}"

        stream.ToArray ()

    let private marker = "public static class Marker { public static int Value() => 1; }"

    /// How the manifest of a generated image is marked a reference assembly, if it is.
    [<RequireQualifiedAccess>]
    type ReferenceMarking =
        | Unmarked
        /// CoreLib's `ReferenceAssemblyAttribute`.
        | FrameworkAttribute
        /// An attribute the image declares itself, under CoreLib's attribute's full name.
        | OwnAttribute
        /// An attribute of the same simple name in another namespace, which is not CoreLib's.
        | OtherNamespace

    let private sourceFor (marking : ReferenceMarking) : string =
        match marking with
        | ReferenceMarking.Unmarked -> marker
        | ReferenceMarking.FrameworkAttribute ->
            "[assembly: System.Runtime.CompilerServices.ReferenceAssembly]\n" + marker
        | ReferenceMarking.OwnAttribute ->
            """
[assembly: System.Runtime.CompilerServices.ReferenceAssembly]
namespace System.Runtime.CompilerServices
{
    public sealed class ReferenceAssemblyAttribute : System.Attribute { }
}
"""
            + marker
        | ReferenceMarking.OtherNamespace ->
            """
[assembly: Elsewhere.ReferenceAssembly]
namespace Elsewhere
{
    public sealed class ReferenceAssemblyAttribute : System.Attribute { }
}
"""
            + marker

    /// The headers and the manifest's flags word of a generated image, overwritten in place.
    type private ImagePatch =
        {
            Machine : uint16
            CorFlags : CorFlags
            /// The processor-architecture bits (`afPA_FullMask`, 0xF0) and the content-type bits
            /// (`afContentType_Mask`, 0xE00) to put in the manifest row's flags.
            ManifestBits : int
        }

    let private writeUInt16 (bytes : byte[]) (offset : int) (value : uint16) : unit =
        bytes.[offset] <- byte value
        bytes.[offset + 1] <- byte (value >>> 8)

    let private writeUInt32 (bytes : byte[]) (offset : int) (value : uint32) : unit =
        for i in 0..3 do
            bytes.[offset + i] <- byte (value >>> (8 * i))

    let private readUInt32 (bytes : byte[]) (offset : int) : uint32 =
        seq { 0..3 }
        |> Seq.sumBy (fun i -> uint32 bytes.[offset + i] <<< (8 * i))

    /// The file offset of the manifest row's `Flags` column: after `HashAlgId` (4 bytes) and the
    /// four 2-byte version numbers (ECMA-335 II.22.2).
    let private manifestFlagsOffset (image : byte[]) : int =
        use stream = new MemoryStream (image)
        use peReader = new PEReader (stream)
        let metadata = peReader.GetMetadataReader ()

        peReader.PEHeaders.MetadataStartOffset
        + metadata.GetTableMetadataOffset TableIndex.Assembly
        + 12

    let private patched (image : byte[]) (patch : ImagePatch) : byte[] =
        let bytes = Array.copy image

        let coffOffset, corFlagsOffset =
            use stream = new MemoryStream (image)
            use peReader = new PEReader (stream)
            // The COR header's `Flags` follow `cb`, the two runtime version numbers and the
            // metadata directory.
            peReader.PEHeaders.CoffHeaderStartOffset, peReader.PEHeaders.CorHeaderStartOffset + 16

        writeUInt16 bytes coffOffset patch.Machine
        writeUInt32 bytes corFlagsOffset (uint32 patch.CorFlags)

        let flagsOffset = manifestFlagsOffset image
        let original = readUInt32 image flagsOffset
        writeUInt32 bytes flagsOffset ((original &&& ~~~0xEF0u) ||| uint32 patch.ManifestBits)
        bytes

    let private baseImages : Lazy<Map<bool * ReferenceMarking, byte[]>> =
        lazy
            ([
                ReferenceMarking.Unmarked
                ReferenceMarking.FrameworkAttribute
                ReferenceMarking.OwnAttribute
                ReferenceMarking.OtherNamespace
             ]
             |> List.collect (fun marking ->
                 // AnyCPU emits PE32 and x64 emits PE32+; the machine is overwritten either way.
                 [
                     (false, marking), compile Platform.AnyCpu (sourceFor marking)
                     (true, marking), compile Platform.X64 (sourceFor marking)
                 ]
             )
             |> Map.ofList)

    type private GeneratedImage =
        {
            IsPE32Plus : bool
            Marking : ReferenceMarking
            Patch : ImagePatch
        }

    let private generatedImage : Gen<GeneratedImage> =
        gen {
            let! isPE32Plus = Gen.elements [ false ; true ]

            let! marking =
                Gen.frequency
                    [
                        3, Gen.constant ReferenceMarking.Unmarked
                        1, Gen.constant ReferenceMarking.FrameworkAttribute
                        1, Gen.constant ReferenceMarking.OwnAttribute
                        1, Gen.constant ReferenceMarking.OtherNamespace
                    ]

            // IMAGE_FILE_MACHINE_I386, _AMD64, _ARM64, _ARMNT, _IA64, and one that names nothing.
            let! machine =
                Gen.frequency
                    [
                        4, Gen.constant 0x014Cus
                        1, Gen.constant 0x8664us
                        1, Gen.constant 0xAA64us
                        1, Gen.constant 0x01C4us
                        1, Gen.constant 0x0200us
                        1, Gen.constant 0x1234us
                    ]

            let flag (value : CorFlags) (weight : int) : Gen<CorFlags> =
                Gen.frequency [ weight, Gen.constant value ; 1, Gen.constant (enum<CorFlags> 0) ]

            let! ilOnly = flag CorFlags.ILOnly 3
            let! requires32Bit = flag CorFlags.Requires32Bit 1
            let! prefers32Bit = flag CorFlags.Prefers32Bit 1

            let! processorArchitecture =
                Gen.frequency
                    [
                        4, Gen.constant 0x00
                        8, Gen.elements [ 0x10..0x10..0xF0 ]
                    ]

            let! contentType =
                Gen.frequency [ 6, Gen.constant 0x000 ; 2, Gen.elements [ 0x200..0x200..0xE00 ] ]

            return
                {
                    IsPE32Plus = isPE32Plus
                    Marking = marking
                    Patch =
                        {
                            Machine = machine
                            CorFlags = ilOnly ||| requires32Bit ||| prefers32Bit
                            ManifestBits = processorArchitecture ||| contentType
                        }
                }
        }

    [<Test>]
    let ``PawPrint refuses exactly the images CoreCLR refuses, for the reason CoreCLR gives`` () : unit =
        let property (generated : GeneratedImage) : unit =
            let image =
                patched (baseImages.Force().[generated.IsPE32Plus, generated.Marking]) generated.Patch

            let refusal = refusalOf image
            let host = hostLoad image

            if not (hostAgrees refusal host) then
                failwith $"PawPrint's verdict %A{refusal} disagrees with the host's %A{host} on %A{generated}"

        Check.One (Config.QuickThrowOnFailure.WithMaxTest 400, Prop.forAll (Arb.fromGen generatedImage) property)

    [<Test>]
    let ``each kind of refusal is reached`` () : unit =
        // The property above is only as good as the cases it meets; these pin one of each.
        let image (isPE32Plus : bool) (marking : ReferenceMarking) (patch : ImagePatch) : byte[] =
            patched (baseImages.Force().[isPE32Plus, marking]) patch

        let msil =
            {
                Machine = 0x014Cus
                CorFlags = CorFlags.ILOnly
                ManifestBits = 0
            }

        let cases : (string * byte[] * (ImageRefusal option -> bool)) list =
            [
                "AnyCPU", image false ReferenceMarking.Unmarked msil, Option.isNone
                "AnyCPU, 32-bit preferred",
                image
                    false
                    ReferenceMarking.Unmarked
                    { msil with
                        CorFlags = CorFlags.ILOnly ||| CorFlags.Requires32Bit ||| CorFlags.Prefers32Bit
                    },
                Option.isNone
                "PE32+ I386 IL-only", image true ReferenceMarking.Unmarked msil, Option.isNone
                "afPA_Specified with NoPlatform's bits",
                image false ReferenceMarking.Unmarked { msil with ManifestBits = 0xF0 },
                Option.isNone
                "an attribute of the same name in another namespace",
                image false ReferenceMarking.OtherNamespace msil,
                Option.isNone
                "32-bit required",
                image
                    false
                    ReferenceMarking.Unmarked
                    { msil with
                        CorFlags = CorFlags.ILOnly ||| CorFlags.Requires32Bit
                    },
                (function
                | Some (ImageRefusal.MachineSpecific _) -> true
                | _ -> false)
                "x64",
                image true ReferenceMarking.Unmarked { msil with Machine = 0x8664us },
                (function
                | Some (ImageRefusal.MachineSpecific _) -> true
                | _ -> false)
                "PE32+, 32-bit required",
                image
                    true
                    ReferenceMarking.Unmarked
                    { msil with
                        Machine = 0x8664us
                        CorFlags = CorFlags.ILOnly ||| CorFlags.Requires32Bit
                    },
                (function
                | Some (ImageRefusal.NoArchitecture _) -> true
                | _ -> false)
                "WindowsRuntime content type",
                image false ReferenceMarking.Unmarked { msil with ManifestBits = 0x200 },
                (function
                | Some (ImageRefusal.ContentType _) -> true
                | _ -> false)
                "CoreLib's ReferenceAssemblyAttribute",
                image false ReferenceMarking.FrameworkAttribute msil,
                (=) (Some ImageRefusal.ReferenceAssembly)
                "the image's own ReferenceAssemblyAttribute",
                image false ReferenceMarking.OwnAttribute msil,
                (=) (Some ImageRefusal.ReferenceAssembly)
                "NoPlatform",
                image false ReferenceMarking.Unmarked { msil with ManifestBits = 0x70 },
                (function
                | Some (ImageRefusal.NoPlatform _) -> true
                | _ -> false)
            ]

        for name, bytes, expected in cases do
            let refusal = refusalOf bytes

            if not (expected refusal) then
                failwith $"%s{name}: unexpected verdict %A{refusal}"

            if not (hostAgrees refusal (hostLoad bytes)) then
                failwith $"%s{name}: PawPrint's verdict %A{refusal} disagrees with the host's %A{hostLoad bytes}"

    /// Every image in `dir`, with whether PawPrint refuses it.
    let private refusalsIn (dir : string) : (string * ImageRefusal option) list =
        Directory.EnumerateFiles (dir, "*.dll")
        |> Seq.sort
        |> Seq.choose (fun path ->
            use stream = File.OpenRead path
            use peReader = new PEReader (stream)

            // The framework directory holds native libraries too.
            if isNull peReader.PEHeaders.CorHeader then
                None
            else
                Some (Path.GetFileName path, ImageAdmission.refusal path peReader)
        )
        |> List.ofSeq

    let private frameworkDirs () : string list =
        FrameworkUnderTest.runtimeDirs ()
        |> Seq.filter (fun dir -> File.Exists (Path.Combine (dir, "System.Private.CoreLib.dll")))
        |> List.ofSeq

    [<Test>]
    let ``every image of the framework under test is admitted`` () : unit =
        let dirs = frameworkDirs ()
        dirs |> shouldNotEqual []

        for dir in dirs do
            let images = refusalsIn dir
            List.length images |> shouldBeGreaterThan 100

            images
            |> List.filter (fun (_, refusal) -> Option.isSome refusal)
            |> shouldEqual []

    [<Test>]
    let ``every image of the linux-x64 framework is admitted`` () : unit =
        let images = refusalsIn (FrameworkUnderTest.requireLinuxFrameworkDirectory ())
        List.length images |> shouldBeGreaterThan 100

        images
        |> List.filter (fun (_, refusal) -> Option.isSome refusal)
        |> shouldEqual []

    /// A small ReadyToRun image of the framework under test, every one of which CoreCLR's
    /// crossgen marks `READYTORUN_FLAG_PLATFORM_NEUTRAL_SOURCE`.
    let private readyToRunImage () : byte[] =
        frameworkDirs ()
        |> List.map (fun dir -> Path.Combine (dir, "System.ComponentModel.dll"))
        |> List.find File.Exists
        |> File.ReadAllBytes

    /// The file offset of the ReadyToRun header's `Flags`, after its signature and two version
    /// numbers.
    let private readyToRunFlagsOffset (image : byte[]) : int =
        use stream = new MemoryStream (image)
        use peReader = new PEReader (stream)
        let directory = peReader.PEHeaders.CorHeader.ManagedNativeHeaderDirectory
        directory.Size |> shouldBeGreaterThan 0

        let section =
            peReader.PEHeaders.SectionHeaders
            |> Seq.find (fun section ->
                directory.RelativeVirtualAddress >= section.VirtualAddress
                && directory.RelativeVirtualAddress < section.VirtualAddress + section.VirtualSize
            )

        section.PointerToRawData + (directory.RelativeVirtualAddress - section.VirtualAddress) + 8

    [<Test>]
    let ``a ReadyToRun image is machine-specific unless its source was platform-neutral`` () : unit =
        let image = readyToRunImage ()
        let flagsOffset = readyToRunFlagsOffset image
        let flags = readUInt32 image flagsOffset
        flags &&& 1u |> shouldEqual 1u

        refusalOf image |> shouldEqual None
        hostAgrees None (hostLoad image) |> shouldEqual true

        let foreign = Array.copy image
        writeUInt32 foreign flagsOffset (flags &&& ~~~1u)

        let refusal = refusalOf foreign

        match refusal with
        | Some (ImageRefusal.MachineSpecific _) -> ()
        | other -> failwith $"expected a machine-specific refusal, got %A{other}"

        hostAgrees refusal (hostLoad foreign) |> shouldEqual true

    [<Test>]
    let ``a reference to a reference assembly is refused`` () : unit =
        // Measured by the case itself on the real runtime: the JIT binds `RefLib` when it
        // compiles `Use`, which raises the BadImageFormatException into `Main`.
        let library (marked : bool) =
            CrossAssemblySpec.library
                "RefLib"
                []
                [
                    (if marked then
                         "[assembly: System.Runtime.CompilerServices.ReferenceAssembly]\n"
                     else
                         "")
                    + "namespace RefLib { public static class Marker { public static int Value() => 1; } }"
                ]

        {
            Assemblies =
                [
                    library false
                    CrossAssemblySpec.entryPoint
                        "RefLib.Entry"
                        [ "RefLib" ]
                        [
                            """
using System;
using System.Runtime.CompilerServices;

class Program
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Use() => RefLib.Marker.Value();

    static int Main()
    {
        try
        {
            return Use();
        }
        catch (BadImageFormatException e) when (e.HResult == unchecked((int)0x80131058))
        {
            return 58;
        }
    }
}
"""
                        ]
                ]
            EntryAssemblyName = "RefLib.Entry"
            ExpectedReturnCode = 58
        }
        |> CrossAssemblyHarness.runTestExpectingRefusal
            [ "a reference assembly" ; "RefLib" ]
            []
            [ library true ]

    [<Test>]
    let ``an entry assembly that is a reference assembly is refused`` () : unit =
        let image =
            Roslyn.compileAssembly
                "RefEntry"
                OutputKind.ConsoleApplication
                []
                [
                    """
[assembly: System.Runtime.CompilerServices.ReferenceAssembly]
class Program { static int Main() => 0; }
"""
                ]

        match RealRuntime.executeWithRealRuntime [||] image with
        | RealRuntimeResult.UnhandledException report ->
            report |> shouldContainText "BadImageFormatException"
            report |> shouldContainText "Cannot load a reference assembly for execution."
        | other -> failwith $"expected the real runtime to refuse the image, got %A{other}"

        let _messages, loggerFactory = LoggerFactory.makeTest ()
        use stream = new MemoryStream (image)

        let refusal =
            try
                Program.run loggerFactory None stream (HostConfig.Default (FrameworkUnderTest.runtimeDirs ()))
                |> ignore<RunEnd>

                None
            with e ->
                Some e.Message

        match refusal with
        | Some message -> message |> shouldContainText "a reference assembly"
        | None -> failwith "expected PawPrint to refuse the image, but it ran it"
