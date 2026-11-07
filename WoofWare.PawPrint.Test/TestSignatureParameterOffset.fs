namespace WoofWare.PawPrint.Test

#nowarn "9"

open System
open System.Collections.Immutable
open System.Reflection
open System.Runtime.InteropServices
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// `SignatureParameterOffset.find` against the test host's own
/// `System.Signature.GetParameterOffsetInternal`, called through reflection, over generated
/// signature blobs: well-formed ones of every element encoding CoreCLR's `SigParser` skips, the same
/// with a byte truncated, replaced or inserted, and random bytes.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestSignatureParameterOffset =

    let private getParameterOffsetInternal : MethodInfo =
        typeof<obj>.Assembly
            .GetType("System.Signature", true)
            .GetMethod ("GetParameterOffsetInternal", BindingFlags.NonPublic ||| BindingFlags.Static)

    /// The host runtime's answer. The blob is copied into a buffer one byte longer, because
    /// `CorSigUncompressData` reads the byte at the end of a signature it has run out of before it
    /// checks the length.
    let private hostOffset (signature : byte[]) (parameterIndex : int) : int =
        let buffer = Array.append signature [| 0uy |]
        let handle = GCHandle.Alloc (buffer, GCHandleType.Pinned)

        try
            let pointer =
                Pointer.Box (
                    NativeInterop.NativePtr.toVoidPtr (
                        NativeInterop.NativePtr.ofNativeInt<byte> (handle.AddrOfPinnedObject ())
                    ),
                    typeof<Void>.MakePointerType ()
                )

            getParameterOffsetInternal.Invoke (
                (null : obj),
                [| box pointer ; box signature.Length ; box parameterIndex |]
            )
            |> unbox<int>
        finally
            handle.Free ()

    /// A compressed unsigned integer (ECMA-335 II.23.2), in an encoding of `Width` bytes that need
    /// not be the shortest: CoreCLR accepts any of the three widths the value fits.
    type private Compressed =
        {
            Value : uint32
            Width : int
        }

    type private Modifier =
        | Required of Compressed
        | Optional of Compressed
        | InternalModifier of required : byte * pointer : byte[]

    type private Element =
        | Primitive of byte
        | Variable of byte * Compressed
        | Bare of byte
        | Wrapped of byte * Element
        | Token of byte * Compressed
        | ModuleZapsig of Compressed * Element
        | FunctionPointer of MethodShape
        | Array of element : Element * rank : Compressed * sizes : Compressed list * lowerBounds : Compressed list
        | Internal of pointer : byte[]
        | GenericInstantiation of Element * Element list
        | Modified of Modifier list * Element
        | AfterSentinel of Element

    and private MethodShape =
        {
            CallingConvention : byte
            GenericCount : Compressed
            CountWidth : int
            Return : Element
            Parameters : Element list
        }

    let private encode (c : Compressed) : byte list =
        let v = c.Value

        match c.Width with
        | 1 -> [ byte v ]
        | 2 -> [ 0x80uy ||| byte (v >>> 8) ; byte v ]
        | 4 -> [ 0xC0uy ||| byte (v >>> 24) ; byte (v >>> 16) ; byte (v >>> 8) ; byte v ]
        | w -> failwith $"compressed width %d{w}"

    let private widthsFitting (v : uint32) : int list =
        [
            if v <= 0x7Fu then
                yield 1
            if v <= 0x3FFFu then
                yield 2
            yield 4
        ]

    let private count (n : int) (width : int) : Compressed =
        {
            Value = uint32 n
            Width = width
        }

    let rec private serialise (element : Element) : byte list =
        match element with
        | Element.Primitive b
        | Element.Bare b -> [ b ]
        | Element.Variable (b, c)
        | Element.Token (b, c) -> b :: encode c
        | Element.Wrapped (b, inner) -> b :: serialise inner
        | Element.ModuleZapsig (c, inner) -> 0x3Fuy :: encode c @ serialise inner
        | Element.FunctionPointer shape -> 0x1Buy :: serialiseMethod shape
        | Element.Array (inner, rank, sizes, lowerBounds) ->
            [
                yield 0x14uy
                yield! serialise inner
                yield! encode rank
                if rank.Value <> 0u then
                    yield! encode (count sizes.Length 1)

                    for s in sizes do
                        yield! encode s

                    yield! encode (count lowerBounds.Length 1)

                    for l in lowerBounds do
                        yield! encode l
            ]
        | Element.Internal pointer -> 0x21uy :: List.ofArray pointer
        | Element.GenericInstantiation (generic, arguments) ->
            [
                yield 0x15uy
                yield! serialise generic
                yield! encode (count arguments.Length 1)
                for a in arguments do
                    yield! serialise a
            ]
        | Element.Modified (modifiers, inner) ->
            [
                for m in modifiers do
                    match m with
                    | Modifier.Required t -> yield! 0x1Fuy :: encode t
                    | Modifier.Optional t -> yield! 0x20uy :: encode t
                    | Modifier.InternalModifier (required, pointer) ->
                        yield 0x22uy
                        yield required
                        yield! pointer
                yield! serialise inner
            ]
        | Element.AfterSentinel inner -> 0x41uy :: serialise inner

    and private serialiseMethod (shape : MethodShape) : byte list =
        [
            yield shape.CallingConvention
            if shape.CallingConvention &&& 0x10uy <> 0uy then
                yield! encode shape.GenericCount
            yield! encode (count shape.Parameters.Length shape.CountWidth)
            yield! serialise shape.Return
            for p in shape.Parameters do
                yield! serialise p
        ]

    /// The name of every encoding `element` contains, for the coverage floors.
    let rec private constructs (element : Element) : string list =
        match element with
        | Element.Primitive _ -> [ "primitive" ]
        | Element.Variable (b, _) -> [ $"variable 0x%02X{b}" ]
        | Element.Bare b -> [ $"bare 0x%02X{b}" ]
        | Element.Wrapped (b, inner) -> $"wrapped 0x%02X{b}" :: constructs inner
        | Element.Token _ -> [ "token" ]
        | Element.ModuleZapsig (_, inner) -> "module zapsig" :: constructs inner
        | Element.FunctionPointer shape ->
            "function pointer"
            :: (shape.Return :: shape.Parameters |> List.collect constructs)
        | Element.Array (inner, rank, _, _) ->
            (if rank.Value = 0u then "array of rank 0" else "array") :: constructs inner
        | Element.Internal _ -> [ "internal" ]
        | Element.GenericInstantiation (generic, arguments) ->
            "generic instantiation" :: (generic :: arguments |> List.collect constructs)
        | Element.Modified (modifiers, inner) ->
            [
                for m in modifiers do
                    match m with
                    | Modifier.Required _ -> yield "modreq"
                    | Modifier.Optional _ -> yield "modopt"
                    | Modifier.InternalModifier _ -> yield "internal modifier"
                yield! constructs inner
            ]
        | Element.AfterSentinel inner -> "sentinel" :: constructs inner

    let private genCompressed : Gen<Compressed> =
        gen {
            let! width = Gen.elements [ 1 ; 2 ; 4 ]

            let! value =
                match width with
                | 1 -> Gen.choose (0, 0x7F)
                | 2 -> Gen.choose (0, 0x3FFF)
                | _ -> Gen.choose (0, 0x1FFFFFFF)

            return
                {
                    Value = uint32 value
                    Width = width
                }
        }

    let private genSmallCompressed : Gen<Compressed> =
        gen {
            let! value = Gen.choose (0, 4)
            let! width = Gen.elements (widthsFitting (uint32 value))

            return
                {
                    Value = uint32 value
                    Width = width
                }
        }

    let private genPointer : Gen<byte[]> =
        Gen.arrayOfLength IntPtr.Size (Gen.choose (0, 255) |> Gen.map byte)

    let private genLeaf : Gen<Element> =
        Gen.oneof
            [
                Gen.elements ([ 0x00uy .. 0x0Euy ] @ [ 0x18uy ; 0x19uy ])
                |> Gen.map Element.Primitive
                Gen.map2 (fun b c -> Element.Variable (b, c)) (Gen.elements [ 0x13uy ; 0x1Euy ; 0x3Buy ]) genCompressed
                Gen.elements [ 0x1Cuy ; 0x16uy ; 0x3Euy ] |> Gen.map Element.Bare
                Gen.map2 (fun b c -> Element.Token (b, c)) (Gen.elements [ 0x11uy ; 0x12uy ]) genCompressed
                genPointer |> Gen.map Element.Internal
            ]

    let private genCallingConvention (allowField : bool) : Gen<byte> =
        gen {
            let! kind =
                if allowField then
                    Gen.choose (0, 15)
                else
                    Gen.elements ([ 0..15 ] |> List.filter (fun k -> k <> 6))

            let! flags = Gen.elements [ 0x00 ; 0x10 ; 0x20 ; 0x30 ; 0x60 ; 0x70 ]
            return byte (kind ||| flags)
        }

    let rec private genElement (depth : int) : Gen<Element> =
        if depth <= 0 then
            genLeaf
        else
            let sub = genElement (depth - 1)

            Gen.frequency
                [
                    4, genLeaf
                    2,
                    Gen.map2
                        (fun b e -> Element.Wrapped (b, e))
                        (Gen.elements [ 0x0Fuy ; 0x10uy ; 0x45uy ; 0x1Duy ; 0x3Duy ])
                        sub
                    1, Gen.map2 (fun c e -> Element.ModuleZapsig (c, e)) genCompressed sub
                    1, genMethodShape true (depth - 1) |> Gen.map Element.FunctionPointer
                    2,
                    gen {
                        let! inner = sub
                        let! rank = genSmallCompressed
                        let! sizes = Gen.listOf genCompressed |> Gen.map (List.ofSeq >> List.truncate 3)
                        let! lowerBounds = Gen.listOf genCompressed |> Gen.map (List.ofSeq >> List.truncate 3)
                        return Element.Array (inner, rank, sizes, lowerBounds)
                    }
                    2,
                    gen {
                        let! generic = sub
                        let! n = Gen.choose (0, 3)
                        let! arguments = Gen.listOfLength n sub
                        return Element.GenericInstantiation (generic, List.ofSeq arguments)
                    }
                    2,
                    gen {
                        let! n = Gen.choose (1, 3)

                        let genModifier =
                            Gen.frequency
                                [
                                    3, genCompressed |> Gen.map Modifier.Required
                                    3, genCompressed |> Gen.map Modifier.Optional
                                    1,
                                    Gen.map2
                                        (fun r p -> Modifier.InternalModifier (r, p))
                                        (Gen.elements [ 0uy ; 1uy ])
                                        genPointer
                                ]

                        let! modifiers = Gen.listOfLength n genModifier
                        let! inner = sub
                        return Element.Modified (List.ofSeq modifiers, inner)
                    }
                    1, sub |> Gen.map Element.AfterSentinel
                ]

    /// A method signature. The calling convention of one inside a function pointer may be any
    /// byte, field and local-signature kinds included, because `SkipMethodHeaderSignature` refuses
    /// exactly 0x06 and 0x07 and nothing else.
    and private genMethodShape (anyCallingConvention : bool) (depth : int) : Gen<MethodShape> =
        gen {
            let! callingConvention = genCallingConvention anyCallingConvention
            let! genericCount = genSmallCompressed
            let! n = Gen.choose (0, 4)
            let! countWidth = Gen.elements (widthsFitting (uint32 n))
            let! ret = genElement depth
            let! parameters = Gen.listOfLength n (genElement depth)

            return
                {
                    CallingConvention = callingConvention
                    GenericCount = genericCount
                    CountWidth = countWidth
                    Return = ret
                    Parameters = List.ofSeq parameters
                }
        }

    type private Case =
        {
            Signature : byte[]
            ParameterIndex : int
            /// The encodings among the elements a walk to `ParameterIndex` skips, when the blob is
            /// one the generator built intact.
            Skipped : string list
            /// Where the generator put the parameter's first byte, when the blob is one it built
            /// intact and `ParameterIndex` names a parameter.
            WalkEnd : int option
        }

    let private genIntact : Gen<Case> =
        Gen.oneof
            [
                gen {
                    let! shape = genMethodShape false 3
                    let elements = shape.Return :: shape.Parameters
                    let! parameterIndex = Gen.choose (-1, elements.Length)
                    let signature = serialiseMethod shape |> Array.ofList

                    return
                        {
                            Signature = signature
                            ParameterIndex = parameterIndex
                            Skipped =
                                elements
                                |> List.truncate (max 0 parameterIndex)
                                |> List.collect constructs
                                |> List.distinct
                            WalkEnd =
                                if parameterIndex < 0 || parameterIndex >= elements.Length then
                                    None
                                else
                                    let rest =
                                        elements |> List.skip parameterIndex |> List.sumBy (serialise >> List.length)

                                    Some (signature.Length - rest)
                        }
                }
                gen {
                    let! flags = Gen.elements [ 0x00uy ; 0x10uy ; 0x20uy ]
                    let! element = genElement 2
                    let! parameterIndex = Gen.frequency [ 4, Gen.constant 0 ; 1, Gen.elements [ -1 ; 1 ] ]

                    return
                        {
                            Signature = (0x06uy ||| flags) :: serialise element |> Array.ofList
                            ParameterIndex = parameterIndex
                            Skipped = []
                            WalkEnd = if parameterIndex = 0 then Some 1 else None
                        }
                }
            ]

    let private genCorrupted : Gen<Case> =
        gen {
            let! intact = genIntact
            let bytes = intact.Signature
            let! position = Gen.choose (1, bytes.Length)
            let! replacement = Gen.choose (0, 255) |> Gen.map byte

            // A cut just short of where the walk ends lands in the last element it skips, which
            // tests each of that element's length checks at its boundary.
            let! shortOfWalkEnd = Gen.choose (1, 3)

            let! corrupted =
                Gen.elements
                    [
                        yield Array.truncate position bytes
                        yield Array.updateAt (position - 1) replacement bytes
                        yield Array.insertAt position replacement bytes
                        match intact.WalkEnd with
                        | Some walkEnd when walkEnd - shortOfWalkEnd >= 1 ->
                            yield Array.truncate (walkEnd - shortOfWalkEnd) bytes
                        | _ -> ()
                    ]

            return
                { intact with
                    Signature = corrupted
                    Skipped = []
                    WalkEnd = None
                }
        }

    let private genRandom : Gen<Case> =
        gen {
            let! n = Gen.choose (1, 12)
            let! bytes = Gen.arrayOfLength n (Gen.choose (0, 255) |> Gen.map byte)
            let! parameterIndex = Gen.choose (0, 3)

            return
                {
                    Signature = bytes
                    ParameterIndex = parameterIndex
                    Skipped = []
                    WalkEnd = None
                }
        }

    type private Label =
        | Outcome of string
        | Skipped of string

    [<Test>]
    let ``find agrees with the host runtime's GetParameterOffsetInternal`` () : unit =
        let property (cover : Label -> unit) (case : Case) : unit =
            let ours =
                SignatureParameterOffset.find
                    IntPtr.Size
                    (ImmutableArray.Create<byte> case.Signature)
                    case.ParameterIndex

            match ours with
            | ParameterOffset.ParameterIndexOutOfRange (index, greatest) ->
                // The host walks on, or asserts in a checked build; it has nothing to compare.
                cover (Label.Outcome "out of range")
                index |> shouldEqual case.ParameterIndex
                (index < 0 || index > greatest) |> shouldEqual true
            | ParameterOffset.Offset offset ->
                cover (Label.Outcome "offset")

                for s in case.Skipped do
                    cover (Label.Skipped s)

                match case.WalkEnd with
                | Some walkEnd -> offset |> shouldEqual walkEnd
                | None -> ()

                hostOffset case.Signature case.ParameterIndex |> shouldEqual offset
            | ParameterOffset.BadSignature ->
                cover (Label.Outcome "bad signature")

                hostOffset case.Signature case.ParameterIndex
                |> shouldEqual SignatureParameterOffset.metaEBadSignature

        let coverage =
            CoverageSample.check
                (Config.QuickThrowOnFailure.WithMaxTest 3000)
                (Arb.fromGen (Gen.frequency [ 3, genIntact ; 2, genCorrupted ; 1, genRandom ]))
                property

        for outcome in [ "offset" ; "bad signature" ; "out of range" ] do
            coverage.Count (Label.Outcome outcome) |> shouldBeGreaterThan 100

        for skipped in
            [
                "primitive"
                "variable 0x13"
                "variable 0x1E"
                "variable 0x3B"
                "bare 0x1C"
                "bare 0x16"
                "bare 0x3E"
                "wrapped 0x0F"
                "wrapped 0x10"
                "wrapped 0x45"
                "wrapped 0x1D"
                "wrapped 0x3D"
                "token"
                "module zapsig"
                "function pointer"
                "array"
                "array of rank 0"
                "internal"
                "generic instantiation"
                "modreq"
                "modopt"
                "internal modifier"
                "sentinel"
            ] do
            coverage.Count (Label.Skipped skipped) |> shouldBeGreaterThan 10

    [<Test>]
    let ``A field signature's type is at offset 1, and it has no other parameter`` () : unit =
        let field = ImmutableArray.Create<byte> [| 0x06uy ; 0x08uy |]

        SignatureParameterOffset.find 8 field 0
        |> shouldEqual (ParameterOffset.Offset 1)

        SignatureParameterOffset.find 8 field 1
        |> shouldEqual (ParameterOffset.ParameterIndexOutOfRange (1, 0))

    [<Test>]
    let ``A method signature's indices run from its return type to its last parameter`` () : unit =
        // instance generic void M<1>(int32, string)
        let method =
            ImmutableArray.Create<byte> [| 0x30uy ; 0x01uy ; 0x02uy ; 0x01uy ; 0x08uy ; 0x0Euy |]

        [ -1 .. 3 ]
        |> List.map (SignatureParameterOffset.find 8 method)
        |> shouldEqual
            [
                ParameterOffset.ParameterIndexOutOfRange (-1, 2)
                ParameterOffset.Offset 3
                ParameterOffset.Offset 4
                ParameterOffset.Offset 5
                ParameterOffset.ParameterIndexOutOfRange (3, 2)
            ]
