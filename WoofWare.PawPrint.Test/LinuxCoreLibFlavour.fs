namespace WoofWare.PawPrint.Test

open System
open System.Collections.Immutable
open System.IO
open NUnit.Framework
open WoofWare.PawPrint
open WoofWare.PosixKernel

/// Running a guest against the pinned linux-x64 framework (`FrameworkUnderTest.requireLinuxFrameworkDirectory`), for
/// the fixtures that need the Linux CoreLib flavour rather than the host's. See
/// `TestLinuxCoreLibFlavour` for why the two differ.
module LinuxCoreLibFlavour =

    let corelibPath (frameworkDir : string) : string =
        Path.Combine (frameworkDir, "System.Private.CoreLib.dll")

    /// Runtime dirs with the pinned linux-x64 framework first: `FrameworkUnderTest.assembliesFor`
    /// of an image in memory on the linux-x64 platform's pinned pack. Assembly binding takes the
    /// first directory that has a `<simple name>.dll`, so every framework assembly resolves from
    /// the pack; the framework under test's dirs stay on the list only to bind anything the pack
    /// does not carry. Fails unless `frameworkDir` is that pack, as
    /// `FrameworkUnderTest.requireLinuxFrameworkDirectory` names it.
    let runtimeDirsPreferringLinux (frameworkDir : string) : ImmutableArray<string> =
        let dirs =
            FrameworkUnderTest.assembliesFor
                (GuestFramework.PinnedPackOf SimulatedUnixPlatform.linuxX64)
                GuestApplication.InMemory

        if dirs.IsEmpty || dirs.[0] <> frameworkDir then
            failwith
                $"The linux-x64 runtime dirs must start with the pinned linux-x64 framework %s{frameworkDir}, but they are %A{List.ofSeq dirs}."

        dirs

    let runOnLinuxFramework (name : string) (frameworkDir : string) (source : string) : IlMachineState =
        let image = Roslyn.compile [ source ]
        let _, loggerFactory = LoggerFactory.makeTest ()
        use _loggerFactoryResource = loggerFactory
        use peImage = new MemoryStream (image)

        let outcome =
            Program.run loggerFactory (Some name) peImage (HostConfig.Default (runtimeDirsPreferringLinux frameworkDir))

        match outcome with
        | RunEnd.Ended (RunOutcome.NormalExit (terminalState, _, _)) -> terminalState
        | other -> failwith $"Expected the guest to exit normally on the linux-x64 CoreLib, got %O{other}"

    let loadedCorelibPath (terminalState : IlMachineState) : string =
        (FrameworkUnderTest.loadedCoreLib terminalState).OriginalPath
        |> Option.defaultWith (fun () -> failwith "Loaded CoreLib has no OriginalPath; cannot tell where it came from")
