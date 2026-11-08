namespace WoofWare.PawPrint.Test

open System
open System.IO
open System.Runtime.InteropServices
open System.Text.RegularExpressions
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint
open WoofWare.PosixKernel

/// `PosixSignalPal` is a transcription of a managed enum and of one rule in
/// `pal_signal.c`, so its oracles are those: every row below reads
/// `System.Runtime.InteropServices.PosixSignal` from the BCL this test host
/// runs rather than restating its values, and the numbering rows call the
/// host's own `SystemNative_GetPlatformSignalNumber`. What a kernel itself
/// knows of signals, with no `PosixSignal` in sight, is
/// `WoofWare.PosixKernel.Test`'s `TestSignal` and `TestSignalAgainstHost`.
///
/// That is a better position than the other three `*Pal` modules are in.
/// `Interop.Error`, the `SocketEvents` bits and the `AF_*` numbering are all
/// internal to the runtime, so their tests have to parse pinned source;
/// `PosixSignal` is public, so the real thing is callable here. It is callable
/// only on the test host's runtime, though, so each runtime's members are also
/// read from its pinned `pal_signal.h`.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestPosixSignalPal =

    /// The real export, in the shim this test host runs against. Pure: it reads
    /// the shim's compiled-in `<signal.h>` constants and installs nothing, which
    /// is why it is safe to call in-process where the enable/disable entry
    /// points are not.
    [<DllImport("libSystem.Native", EntryPoint = "SystemNative_GetPlatformSignalNumber")>]
    extern int private hostGetPlatformSignalNumber(int posixSignal)

    let private everyNumbering : SignalNumbering list =
        [ SignalNumbering.Linux ; SignalNumbering.Darwin ]

    /// The runtime this test host runs, whose BCL and shim the host rows call.
    let private hostRuntime : EmulatedRuntime =
        match Environment.Version.Major with
        | 10 -> EmulatedRuntime.Net10
        | major -> failwith $"TestPosixSignalPal: the test host runs .NET %d{major}, which this fixture does not name"

    /// The numbering the shim this test host runs against was compiled with,
    /// or `None` on a host PawPrint does not model.
    let private hostNumbering () : SignalNumbering option =
        if RuntimeInformation.IsOSPlatform OSPlatform.OSX then
            Some SignalNumbering.Darwin
        elif RuntimeInformation.IsOSPlatform OSPlatform.Linux then
            Some SignalNumbering.Linux
        else
            None

    /// Every member of the enum, read from the BCL, paired with the `Signal`
    /// this repo says it names. The *values* are the oracle; the pairing is
    /// this repo's claim, and `the enum has exactly the members this maps`
    /// below is what stops the two drifting apart.
    let private enumMembers : (PosixSignal * Signal) list =
        [
            PosixSignal.SIGHUP, Signal.SIGHUP
            PosixSignal.SIGINT, Signal.SIGINT
            PosixSignal.SIGQUIT, Signal.SIGQUIT
            PosixSignal.SIGTERM, Signal.SIGTERM
            PosixSignal.SIGCHLD, Signal.SIGCHLD
            PosixSignal.SIGCONT, Signal.SIGCONT
            PosixSignal.SIGWINCH, Signal.SIGWINCH
            PosixSignal.SIGTTIN, Signal.SIGTTIN
            PosixSignal.SIGTTOU, Signal.SIGTTOU
            PosixSignal.SIGTSTP, Signal.SIGTSTP
        ]

    /// Every member of the enum on `runtime`, by value, paired with the `Signal`
    /// this repo says it names. On the host's runtime the values are
    /// `enumMembers`'; `each runtime's members are those its pinned header
    /// declares` holds every runtime's to its source.
    let private membersOn (runtime : EmulatedRuntime) : (int * Signal) list =
        let shared = enumMembers |> List.map (fun (value, signal) -> int value, signal)

        match runtime with
        | EmulatedRuntime.Net10 -> shared
        | EmulatedRuntime.Net11 -> shared @ [ -11, Signal.SIGKILL ]

    /// Every signal either numbering has that the enum on `runtime` has no
    /// member for. They reach a guest handler as `PosixSignalInvalid`.
    let private withoutEnumMemberOn (runtime : EmulatedRuntime) : Signal list =
        [
            for numbering in everyNumbering do
                for signo in 1 .. Signal.highestSignoUnder numbering do
                    match Signal.ofRawSignoUnder numbering signo with
                    | ValueSome signal -> yield signal
                    | ValueNone -> failwith $"%O{numbering}: signo %d{signo} is within range"
        ]
        |> List.distinct
        |> List.filter (fun signal -> not (membersOn runtime |> List.exists (fun (_, named) -> named = signal)))

    /// A new member appearing upstream is a real divergence rather than a
    /// curiosity: `platformSignalNumber` would treat its negative value as an
    /// unknown number and answer 0, so `PosixSignalRegistration.Register`
    /// would throw `PlatformNotSupportedException` where real .NET registered
    /// the signal happily.
    [<Test>]
    let ``the enum has exactly the members this maps`` () : unit =
        System.Enum.GetValues<PosixSignal> ()
        |> Array.map int
        |> Set.ofArray
        |> shouldEqual (membersOn hostRuntime |> List.map fst |> Set.ofList)

    /// The same, for every runtime: `pal_signal.h` declares the C side of the
    /// enum, member for member (`PosixSignalSIGHUP = -1`, …), and the shim's
    /// conversions switch over exactly those.
    [<Test>]
    let ``each runtime's members are those its pinned header declares`` () : unit =
        for runtime in EmulatedRuntime.supported do
            let path =
                Path.Combine (
                    FrameworkUnderTest.requireRuntimeSourceOf runtime,
                    "src",
                    "native",
                    "libs",
                    "System.Native",
                    "pal_signal.h"
                )

            let pinned =
                Regex.Matches (File.ReadAllText path, @"^\s+PosixSignal(?<name>SIG[A-Z]+)\s*=\s*(?<value>-\d+)", RegexOptions.Multiline)
                |> Seq.map (fun m -> m.Groups.["name"].Value, int m.Groups.["value"].Value)
                |> Set.ofSeq

            pinned |> Set.isEmpty |> shouldEqual false

            (runtime, pinned)
            |> shouldEqual (
                runtime,
                membersOn runtime
                |> List.map (fun (value, signal) -> $"%A{signal}", value)
                |> Set.ofList
            )

    /// Which members each runtime's enum adds to .NET 10's, as literals, so
    /// that a table transposed in both this fixture and the library is seen.
    [<Test>]
    let ``only .NET 11's enum names SIGKILL, as -11`` () : unit =
        PosixSignalPal.platformSignalNumber EmulatedRuntime.Net10 SignalNumbering.Linux -11
        |> shouldEqual 0

        for numbering in everyNumbering do
            PosixSignalPal.platformSignalNumber EmulatedRuntime.Net11 numbering -11
            |> shouldEqual 9

        PosixSignalPal.toEnum EmulatedRuntime.Net10 Signal.SIGKILL |> shouldEqual 0
        PosixSignalPal.toEnum EmulatedRuntime.Net11 Signal.SIGKILL |> shouldEqual -11

    // ---------------------------------------------------------------------
    // `signalMax`.
    // ---------------------------------------------------------------------

    /// The values, which nothing else pins: `GetSignalMax()` is `SIGRTMAX`
    /// where the header defines it (glibc: 64) and `NSIG` otherwise (Darwin:
    /// 32). The Darwin number is one past the last signal Darwin has, and the
    /// host-equality test below is what shows the shim really does admit it.
    [<Test>]
    let ``signalMax is SIGRTMAX on Linux and NSIG on Darwin`` () : unit =
        PosixSignalPal.signalMax SignalNumbering.Linux |> shouldEqual 64
        PosixSignalPal.signalMax SignalNumbering.Darwin |> shouldEqual 32

    [<Test>]
    let ``signalMax is never below the kernel's own ceiling`` () : unit =
        // The shim asserts `signalCode <= GetSignalMax()` on every signo it
        // is handed, so a ceiling below the kernel's would refuse real
        // signals.
        for numbering in everyNumbering do
            (PosixSignalPal.signalMax numbering >= Signal.highestSignoUnder numbering)
            |> shouldEqual true

    // ---------------------------------------------------------------------
    // `platformSignalNumber`.
    // ---------------------------------------------------------------------

    [<Test>]
    let ``platformSignalNumber maps every member of the enum to its signo under each numbering`` () : unit =
        for runtime in EmulatedRuntime.supported do
            for numbering in everyNumbering do
                for value, signal in membersOn runtime do
                    PosixSignalPal.platformSignalNumber runtime numbering value
                    |> shouldEqual (Signal.toRawSignoUnder numbering signal)

    /// The three members whose number differs are asserted as literals too,
    /// so that this fixture sees a transposed table without going through the
    /// library's own function.
    [<Test>]
    let ``platformSignalNumber answers the divergent rows per numbering`` () : unit =
        for runtime in EmulatedRuntime.supported do
            PosixSignalPal.platformSignalNumber runtime SignalNumbering.Linux (int PosixSignal.SIGCHLD)
            |> shouldEqual 17

            PosixSignalPal.platformSignalNumber runtime SignalNumbering.Darwin (int PosixSignal.SIGCHLD)
            |> shouldEqual 20

            PosixSignalPal.platformSignalNumber runtime SignalNumbering.Linux (int PosixSignal.SIGCONT)
            |> shouldEqual 18

            PosixSignalPal.platformSignalNumber runtime SignalNumbering.Darwin (int PosixSignal.SIGCONT)
            |> shouldEqual 19

            PosixSignalPal.platformSignalNumber runtime SignalNumbering.Linux (int PosixSignal.SIGTSTP)
            |> shouldEqual 20

            PosixSignalPal.platformSignalNumber runtime SignalNumbering.Darwin (int PosixSignal.SIGTSTP)
            |> shouldEqual 18

    /// The BCL allows a guest to construct a `PosixSignal` from a raw native
    /// signo (`(PosixSignal)signo`). The real native code echoes it back iff
    /// `signal > 0 && signal <= GetSignalMax()`, without asking whether it is
    /// a signal it knows — so Darwin's 32 comes back as 32.
    [<Test>]
    let ``platformSignalNumber echoes every positive within signalMax and nothing beyond`` () : unit =
        for runtime in EmulatedRuntime.supported do
            for numbering in everyNumbering do
                let signalMax = PosixSignalPal.signalMax numbering

                for signo in 1..signalMax do
                    PosixSignalPal.platformSignalNumber runtime numbering signo |> shouldEqual signo

                PosixSignalPal.platformSignalNumber runtime numbering (signalMax + 1)
                |> shouldEqual 0

                PosixSignalPal.platformSignalNumber runtime numbering 100 |> shouldEqual 0

                PosixSignalPal.platformSignalNumber runtime numbering System.Int32.MaxValue
                |> shouldEqual 0

    /// Values outside the enum's range with no positive interpretation must
    /// produce 0, so the BCL raises `PlatformNotSupportedException`.
    [<Test>]
    let ``platformSignalNumber answers 0 for PosixSignalInvalid and unrecognised negatives`` () : unit =
        for runtime in EmulatedRuntime.supported do
            let lowest = membersOn runtime |> List.map fst |> List.min

            for numbering in everyNumbering do
                PosixSignalPal.platformSignalNumber runtime numbering 0 |> shouldEqual 0

                PosixSignalPal.platformSignalNumber runtime numbering (lowest - 1)
                |> shouldEqual 0

                PosixSignalPal.platformSignalNumber runtime numbering -100 |> shouldEqual 0

                PosixSignalPal.platformSignalNumber runtime numbering System.Int32.MinValue
                |> shouldEqual 0

    /// The host-equality row, and the one that can falsify a column rather
    /// than restate it: the shim this test host runs against was compiled
    /// against one platform's `<signal.h>`, so its answer for every input is
    /// what PawPrint must answer under that platform's numbering. macOS
    /// locally and Linux in CI each check one column.
    [<Test>]
    let ``platformSignalNumber agrees with this host's SystemNative_GetPlatformSignalNumber`` () : unit =
        match hostNumbering () with
        | None -> Assert.Ignore $"no Unix host to measure (%s{RuntimeInformation.OSDescription})"
        | Some numbering ->

        let inputs : int list =
            [ System.Int32.MinValue ; System.Int32.MaxValue ] @ [ -100 .. 100 ]

        for raw in inputs do
            let host = hostGetPlatformSignalNumber raw
            let modelled = PosixSignalPal.platformSignalNumber hostRuntime numbering raw

            if host <> modelled then
                failwith
                    $"%O{numbering}: for input %d{raw} PawPrint answers %d{modelled} but this host's SystemNative_GetPlatformSignalNumber answers %d{host}"

    // ---------------------------------------------------------------------
    // `handledWithoutRestoring`.
    // ---------------------------------------------------------------------

    /// Transcribed from the `switch` in `SystemNative_HandleNonCanceledPosixSignal`
    /// in the pinned `pal_signal.c`: the seven signals with an explicit arm.
    /// Every other case, named or not, takes `default:`.
    [<Test>]
    let ``handledWithoutRestoring names exactly the seven signals with an explicit arm`` () : unit =
        let explicitArms : Signal list =
            [
                Signal.SIGCONT
                Signal.SIGTSTP
                Signal.SIGTTIN
                Signal.SIGTTOU
                Signal.SIGCHLD
                Signal.SIGURG
                Signal.SIGWINCH
            ]

        for numbering in everyNumbering do
            for signo in 1 .. Signal.highestSignoUnder numbering do
                match Signal.ofRawSignoUnder numbering signo with
                | ValueNone -> failwith $"%O{numbering}: signo %d{signo} is within range"
                | ValueSome signal ->
                    PosixSignalPal.handledWithoutRestoring signal
                    |> shouldEqual (List.contains signal explicitArms)

    /// The row that motivates the function: SIGURG and Darwin's SIGIO are
    /// both discarded by the kernel, and only one of them has an arm.
    [<Test>]
    let ``handledWithoutRestoring splits the ignored-by-default signals by the shim's switch`` () : unit =
        PosixSignalPal.handledWithoutRestoring Signal.SIGURG |> shouldEqual true
        PosixSignalPal.handledWithoutRestoring Signal.SIGIO |> shouldEqual false
        PosixSignalPal.handledWithoutRestoring Signal.SIGINFO |> shouldEqual false

    // ---------------------------------------------------------------------
    // `toEnum`.
    // ---------------------------------------------------------------------

    [<Test>]
    let ``toEnum names every member of the enum`` () : unit =
        for runtime in EmulatedRuntime.supported do
            for value, signal in membersOn runtime do
                PosixSignalPal.toEnum runtime signal |> shouldEqual value

    /// `PosixSignalInvalid` is 0, and it is what real CoreCLR passes a handler
    /// for a signal the enum cannot name — `pal_signal.c` overwrites the
    /// out-parameter with it when `TryConvertSignalCodeToPosixSignal` fails.
    /// Every one of these is asserted, not just a representative: a mutation of
    /// one arm cannot be caught by a test of another.
    [<Test>]
    let ``toEnum answers PosixSignalInvalid for signals the enum cannot name`` () : unit =
        for runtime in EmulatedRuntime.supported do
            for signal in withoutEnumMemberOn runtime do
                PosixSignalPal.toEnum runtime signal |> shouldEqual 0

    /// And nothing else answers 0, which is what stops the row above passing
    /// for a `toEnum` that had simply stopped working.
    [<Test>]
    let ``toEnum answers 0 only for signals the enum cannot name`` () : unit =
        for runtime in EmulatedRuntime.supported do
            for _, signal in membersOn runtime do
                PosixSignalPal.toEnum runtime signal |> shouldNotEqual 0

    /// The row that tells the numberings apart: 19 is SIGCONT on Darwin and
    /// SIGSTOP on Linux, where SIGCONT is 18.
    [<Test>]
    let ``toEnum reads 19 as SIGCONT under Darwin only`` () : unit =
        let toEnumOf (numbering : SignalNumbering) (signo : int) : int =
            match Signal.ofRawSignoUnder numbering signo with
            | ValueSome signal -> PosixSignalPal.toEnum hostRuntime signal
            | ValueNone -> failwith $"%O{numbering}: signo %d{signo} is a signal"

        toEnumOf SignalNumbering.Darwin 19 |> shouldEqual (int PosixSignal.SIGCONT)
        toEnumOf SignalNumbering.Linux 19 |> shouldEqual 0
        toEnumOf SignalNumbering.Linux 18 |> shouldEqual (int PosixSignal.SIGCONT)

    /// The round trip the dispatcher relies on: the enum value it hands a
    /// handler, fed back through the registration arm under any numbering,
    /// names the signal the handler was registered for.
    [<Test>]
    let ``platformSignalNumber inverts toEnum on every member under each numbering`` () : unit =
        for runtime in EmulatedRuntime.supported do
            for numbering in everyNumbering do
                for _, signal in membersOn runtime do
                    PosixSignalPal.platformSignalNumber runtime numbering (PosixSignalPal.toEnum runtime signal)
                    |> shouldEqual (Signal.toRawSignoUnder numbering signal)
