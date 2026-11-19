namespace WoofWare.PawPrint.Test

open System
open System.Globalization
open System.Numerics
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// `DepsVersion` against a transcription of the host's `version_t`
/// (`src/native/corehost/hostpolicy/version.cpp`, the same in both pins).
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestDepsVersion =

    /// What the host's `version_t::parse` does with a string.
    [<RequireQualifiedAccess>]
    type private HostParse =
        /// `parse` returns false, leaving the version at `-1.-1.-1.-1`.
        | Fails
        /// `parse` returns true with these components, `-1` for one it did not read.
        | Parses of major : int * minor : int * build : int * revision : int
        /// `try_stou` reads a value above `Int32.MaxValue`: `std::stoul` either throws (above
        /// `ULONG_MAX`) or gives a value which `version_t` stores wrapped into an `int`.
        | ReadsOutOfRange

    exception private OutOfRange

    let private npos : int = -1

    /// `std::string::find (c, start)`.
    let private find (s : string) (c : char) (start : int) : int =
        if start > s.Length then npos else s.IndexOf (c, start)

    /// `std::string::substr (pos, count)`, for `pos <= size`.
    let private substr (s : string) (pos : int) (count : int) : string =
        let count =
            if count = npos then
                s.Length - pos
            else
                min count (s.Length - pos)

        s.Substring (pos, count)

    /// `try_stou` (`hostmisc/utils.cpp`): empty or non-digit fails; otherwise `std::stoul`, which
    /// the transcription refuses above `Int32.MaxValue`.
    let private tryStou (str : string) : int option =
        if str = "" then
            None
        elif str |> Seq.exists (fun c -> not ("0123456789".Contains c)) then
            None
        else
            let value = BigInteger.Parse (str, CultureInfo.InvariantCulture)

            if value > BigInteger Int32.MaxValue then
                raise OutOfRange

            Some (int value)

    /// `parse_internal`, line by line.
    let private transcribedParse (ver : string) : HostParse =
        let parseInternal () : HostParse =
            let majSep = find ver '.' 0

            if majSep = npos then
                HostParse.Fails
            else

            match tryStou (substr ver 0 majSep) with
            | None -> HostParse.Fails
            | Some major ->

            let minStart = majSep + 1
            let minSep = find ver '.' minStart

            if minSep = npos then
                match tryStou (substr ver minStart npos) with
                | None -> HostParse.Fails
                | Some minor -> HostParse.Parses (major, minor, -1, -1)
            else

            match tryStou (substr ver minStart (minSep - minStart)) with
            | None -> HostParse.Fails
            | Some minor ->

            let buildStart = minSep + 1
            let buildSep = find ver '.' buildStart

            if buildSep = npos then
                match tryStou (substr ver buildStart npos) with
                | None -> HostParse.Fails
                | Some build -> HostParse.Parses (major, minor, build, -1)
            else

            match tryStou (substr ver buildStart (buildSep - buildStart)) with
            | None -> HostParse.Fails
            | Some build ->

            match tryStou (substr ver (buildSep + 1) npos) with
            | None -> HostParse.Fails
            | Some revision -> HostParse.Parses (major, minor, build, revision)

        try
            parseInternal ()
        with OutOfRange ->
            HostParse.ReadsOutOfRange

    /// The host's four `int` fields for a version.
    let private fields (v : DepsVersion) : int * int * int * int =
        match v with
        | DepsVersion.Empty -> -1, -1, -1, -1
        | DepsVersion.MajorMinor (a, b) -> a, b, -1, -1
        | DepsVersion.MajorMinorBuild (a, b, c) -> a, b, c, -1
        | DepsVersion.MajorMinorBuildRevision (a, b, c, d) -> a, b, c, d

    let private asHostParse (parsed : Result<DepsVersion, unit>) : HostParse =
        match parsed with
        | Error () -> HostParse.ReadsOutOfRange
        | Ok DepsVersion.Empty -> HostParse.Fails
        | Ok v ->
            let a, b, c, d = fields v
            HostParse.Parses (a, b, c, d)

    /// `version_t::compare`, line by line.
    let private transcribedCompare (a : DepsVersion) (b : DepsVersion) : int =
        let aMajor, aMinor, aBuild, aRevision = fields a
        let bMajor, bMinor, bBuild, bRevision = fields b

        if aMajor <> bMajor then
            (if aMajor > bMajor then 1 else -1)
        elif aMinor <> bMinor then
            (if aMinor > bMinor then 1 else -1)
        elif aBuild <> bBuild then
            (if aBuild > bBuild then 1 else -1)
        elif aRevision <> bRevision then
            (if aRevision > bRevision then 1 else -1)
        else
            0

    /// A component as text: numbers either side of each boundary the host has, and text that is
    /// not a number. `٣` is a digit to `Char.IsDigit` and not to the host.
    let private componentText : Gen<string> =
        Gen.oneof
            [
                Gen.choose (0, 99999) |> Gen.map string<int>
                Gen.elements
                    [
                        "0"
                        "007"
                        "2147483647"
                        "02147483647"
                        "2147483648"
                        "4294967295"
                        "4294967296"
                        "18446744073709551616"
                        "99999999999999999999999"
                        ""
                        "x"
                        " 1"
                        "1 "
                        "-1"
                        "+1"
                        "٣"
                        "1e3"
                    ]
            ]

    /// Versions joined from one to six components, and arbitrary short strings over the
    /// characters that matter.
    let private versionText : Gen<string> =
        Gen.oneof
            [
                gen {
                    let! count = Gen.choose (1, 6)
                    let! parts = Gen.listOfLength count componentText
                    return String.Join (".", parts)
                }
                gen {
                    let! length = Gen.choose (0, 14)

                    let! chars =
                        Gen.arrayOfLength length (Gen.elements [ '0' ; '1' ; '9' ; '.' ; '.' ; 'x' ; ' ' ; '-' ; '٣' ])

                    return System.String chars
                }
            ]

    [<RequireQualifiedAccess>]
    type private ParseLabel =
        | NoDot
        | FailsWithDot
        | Parses of components : int
        | OutOfRange

    [<Test>]
    let ``DepsVersion.parse agrees with a transcription of version_t::parse`` () =
        let property (cover : ParseLabel -> unit) (text : string) : unit =
            let expected = transcribedParse text

            match expected with
            | HostParse.Fails when not (text.Contains '.') -> cover ParseLabel.NoDot
            | HostParse.Fails -> cover ParseLabel.FailsWithDot
            | HostParse.Parses (_, _, -1, _) -> cover (ParseLabel.Parses 2)
            | HostParse.Parses (_, _, _, -1) -> cover (ParseLabel.Parses 3)
            | HostParse.Parses _ -> cover (ParseLabel.Parses 4)
            | HostParse.ReadsOutOfRange -> cover ParseLabel.OutOfRange

            asHostParse (DepsVersion.parse text) |> shouldEqual expected

        let coverage =
            CoverageSample.check (Config.QuickThrowOnFailure.WithMaxTest 10000) (Arb.fromGen versionText) property

        for label in
            [
                ParseLabel.NoDot
                ParseLabel.FailsWithDot
                ParseLabel.Parses 2
                ParseLabel.Parses 3
                ParseLabel.Parses 4
                ParseLabel.OutOfRange
            ] do
            coverage.Count label |> shouldBeGreaterThan 50

    /// One unit case per branch of `parse_internal`, beside the property, so that a failure
    /// names the branch.
    [<TestCase("", "Fails")>]
    [<TestCase("1", "Fails")>]
    [<TestCase("99999999999", "Fails")>]
    [<TestCase("x.1", "Fails")>]
    [<TestCase("x.99999999999", "Fails")>]
    [<TestCase("1.", "Fails")>]
    [<TestCase("1.2", "1.2.-1.-1")>]
    [<TestCase("01.002", "1.2.-1.-1")>]
    [<TestCase("1.x.3", "Fails")>]
    [<TestCase("1.2.", "Fails")>]
    [<TestCase("1.2.3", "1.2.3.-1")>]
    [<TestCase("1.2.x.4", "Fails")>]
    [<TestCase("1.2.3.", "Fails")>]
    [<TestCase("1.2.3.4", "1.2.3.4")>]
    [<TestCase("1.2.3.4.5", "Fails")>]
    [<TestCase("1.2.3.4.99999999999", "Fails")>]
    [<TestCase("2147483647.2147483647.2147483647.2147483647", "2147483647.2147483647.2147483647.2147483647")>]
    [<TestCase("2147483648.0", "OutOfRange")>]
    [<TestCase("0.2147483648", "OutOfRange")>]
    [<TestCase("0.0.2147483648", "OutOfRange")>]
    [<TestCase("0.0.0.2147483648", "OutOfRange")>]
    [<TestCase("99999999999999999999999.x", "OutOfRange")>]
    [<TestCase("1.٣", "Fails")>]
    [<TestCase("1.+2", "Fails")>]
    let ``DepsVersion.parse on each branch`` (text : string, expected : string) : unit =
        let actual =
            match DepsVersion.parse text with
            | Error () -> "OutOfRange"
            | Ok DepsVersion.Empty -> "Fails"
            | Ok v ->
                let a, b, c, d = fields v
                $"%i{a}.%i{b}.%i{c}.%i{d}"

        actual |> shouldEqual expected
        asHostParse (DepsVersion.parse text) |> shouldEqual (transcribedParse text)

    let private version : Gen<DepsVersion> =
        let comp = Gen.choose (0, 2)

        Gen.oneof
            [
                Gen.constant DepsVersion.Empty
                Gen.map2 (fun a b -> DepsVersion.MajorMinor (a, b)) comp comp
                Gen.map3 (fun a b c -> DepsVersion.MajorMinorBuild (a, b, c)) comp comp comp
                gen {
                    let! a = comp
                    let! b = comp
                    let! c = comp
                    let! d = comp
                    return DepsVersion.MajorMinorBuildRevision (a, b, c, d)
                }
            ]

    [<RequireQualifiedAccess>]
    type private CompareLabel =
        | Below
        | Equal
        | Above
        /// Equal in every component both have, and differing in how many they have.
        | DifferOnlyInLength

    [<Test>]
    let ``DepsVersion.compare agrees with a transcription of version_t::compare`` () =
        let property (cover : CompareLabel -> unit) (a : DepsVersion, b : DepsVersion) : unit =
            let expected = transcribedCompare a b

            match expected with
            | -1 -> cover CompareLabel.Below
            | 0 -> cover CompareLabel.Equal
            | _ -> cover CompareLabel.Above

            let fa = fields a
            let fb = fields b

            let sharedAgree =
                let a1, a2, a3, a4 = fa
                let b1, b2, b3, b4 = fb

                [ a1, b1 ; a2, b2 ; a3, b3 ; a4, b4 ]
                |> List.forall (fun (x, y) -> x = y || x = -1 || y = -1)

            if sharedAgree && fa <> fb then
                cover CompareLabel.DifferOnlyInLength

            sign (DepsVersion.compare a b) |> shouldEqual expected
            // The host's equality is `compare = 0`; structural equality must agree with it.
            (a = b) |> shouldEqual (expected = 0)

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 5000)
                (Arb.fromGen (Gen.zip version version))
                property

        for label in
            [
                CompareLabel.Below
                CompareLabel.Equal
                CompareLabel.Above
                CompareLabel.DifferOnlyInLength
            ] do
            coverage.Count label |> shouldBeGreaterThan 50

    [<Test>]
    let ``An empty version is below every parsed version`` () =
        DepsVersion.compare DepsVersion.Empty (DepsVersion.MajorMinor (0, 0))
        |> sign
        |> shouldEqual -1

        DepsVersion.compare (DepsVersion.MajorMinor (1, 2)) (DepsVersion.MajorMinorBuild (1, 2, 0))
        |> sign
        |> shouldEqual -1

        DepsVersion.compare (DepsVersion.MajorMinorBuildRevision (1, 2, 3, 4)) (DepsVersion.MajorMinorBuild (1, 2, 3))
        |> sign
        |> shouldEqual 1

    [<Test>]
    let ``DepsVersion.parse does not read the current culture`` () =
        // The host's `try_stou` reads ASCII digits whatever the locale. A culture whose signs are
        // digits makes a culture-sensitive number parser refuse them.
        let culture = CultureInfo.InvariantCulture.Clone () :?> CultureInfo
        culture.NumberFormat.NegativeSign <- "2"
        culture.NumberFormat.PositiveSign <- "1"
        let previous = CultureInfo.CurrentCulture
        CultureInfo.CurrentCulture <- culture

        try
            DepsVersion.parse "2.1" |> shouldEqual (Ok (DepsVersion.MajorMinor (2, 1)))

            DepsVersion.parse "12.21.2147483647.1"
            |> shouldEqual (Ok (DepsVersion.MajorMinorBuildRevision (12, 21, Int32.MaxValue, 1)))

            DepsVersion.parse "2.2147483648" |> shouldEqual (Error ())
        finally
            CultureInfo.CurrentCulture <- previous
