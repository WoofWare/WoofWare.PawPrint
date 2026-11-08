namespace WoofWare.PawPrint.Test

#nowarn "9"

open System
open System.Collections.Immutable
open System.Reflection
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open FsCheck
open FsCheck.FSharp
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// A type of this assembly, whose module the host's `Signature` objects resolve modifier tokens in.
type private SignatureCustomModifiersAnchor = class end

/// `SignatureCustomModifiers.at` against the test host's own
/// `System.Signature.GetCustomModifiersAtOffset`, called through reflection on a `Signature` whose
/// blob is a generated run of custom modifiers, CMOD_INTERNALs and vararg sentinels before an
/// element type, intact or truncated.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestSignatureCustomModifiers =

    let private signatureType : Type =
        typeof<obj>.Assembly.GetType ("System.Signature", true)

    let private getCustomModifiersAtOffset : MethodInfo =
        signatureType.GetMethod (
            "GetCustomModifiersAtOffset",
            BindingFlags.NonPublic ||| BindingFlags.Instance,
            [| typeof<int> ; typeof<bool> |]
        )

    let private signatureField (name : string) : FieldInfo =
        signatureType.GetField (name, BindingFlags.NonPublic ||| BindingFlags.Instance)

    let private anchorModule : Module = typeof<SignatureCustomModifiersAnchor>.Module

    /// TypeDef tokens of this module which the host loads without a type context: every
    /// non-generic type.
    let private loadableTypeDefs : int list =
        typeof<SignatureCustomModifiersAnchor>.Assembly.GetTypes ()
        |> Seq.filter (fun t -> not t.ContainsGenericParameters)
        |> Seq.map (fun t -> t.MetadataToken)
        |> Seq.sort
        |> List.ofSeq

    let private loadableTypeDefSet : Set<int> = Set.ofList loadableTypeDefs

    /// Real TypeHandles of the host, for CMOD_INTERNAL to carry.
    let private typeHandles : nativeint list =
        [
            typeof<string>
            typeof<int>
            typeof<SignatureCustomModifiersAnchor>
            typeof<Uri>
        ]
        |> List.map (fun t -> t.TypeHandle.Value)

    let private typeHandleSet : Set<nativeint> = Set.ofList typeHandles

    let private pointerBytes (p : nativeint) : byte[] =
        if IntPtr.Size = 8 then
            BitConverter.GetBytes (int64 p)
        else
            BitConverter.GetBytes (int32 p)

    let private pointerOf (bytes : ImmutableArray<byte>) : nativeint =
        let bytes = Seq.toArray bytes

        if IntPtr.Size = 8 then
            nativeint (BitConverter.ToInt64 (bytes, 0))
        else
            nativeint (BitConverter.ToInt32 (bytes, 0))

    /// The host runtime's answer, or the exception it throws. The blob is copied into a buffer one
    /// byte longer, because `CorSigUncompressData` reads the byte at the end of a signature it has
    /// run out of before it checks the length.
    let private hostModifiers (signature : byte[]) (offset : int) (required : bool) : Result<Type[], exn> =
        let buffer = Array.append signature [| 0uy |]
        let handle = GCHandle.Alloc (buffer, GCHandleType.Pinned)

        try
            let signatureObject = RuntimeHelpers.GetUninitializedObject signatureType

            (signatureField "_declaringType").SetValue (signatureObject, typeof<SignatureCustomModifiersAnchor>)

            let pointer =
                Pointer.Box (
                    NativeInterop.NativePtr.toVoidPtr (
                        NativeInterop.NativePtr.ofNativeInt<byte> (handle.AddrOfPinnedObject ())
                    ),
                    typeof<Void>.MakePointerType ()
                )

            (signatureField "_sig").SetValue (signatureObject, pointer)
            (signatureField "_csig").SetValue (signatureObject, signature.Length)

            try
                getCustomModifiersAtOffset.Invoke (signatureObject, [| box offset ; box required |])
                |> unbox<Type[]>
                |> Ok
            with :? TargetInvocationException as e ->
                Error e.InnerException
        finally
            handle.Free ()

    /// A compressed TypeDefOrRefOrSpec-encoded TypeDef token, in an encoding of `Width` bytes that
    /// need not be the shortest.
    type private EncodedToken =
        {
            Token : int
            Width : int
        }

    type private Item =
        | Modifier of prefix : byte * EncodedToken
        | InternalModifier of required : byte * typeHandle : nativeint
        | Sentinel

    /// What ends the run of modifiers.
    type private Terminator =
        | ElementType of byte
        | Internal of typeHandle : nativeint

    let private encodeToken (t : EncodedToken) : byte list =
        let v = (uint32 (t.Token &&& 0x00FFFFFF) <<< 2)

        match t.Width with
        | 1 -> [ byte v ]
        | 2 -> [ 0x80uy ||| byte (v >>> 8) ; byte v ]
        | 4 -> [ 0xC0uy ||| byte (v >>> 24) ; byte (v >>> 16) ; byte (v >>> 8) ; byte v ]
        | w -> failwith $"compressed width %d{w}"

    let private serialiseItem (item : Item) : byte list =
        match item with
        | Item.Modifier (prefix, t) -> prefix :: encodeToken t
        | Item.InternalModifier (required, p) -> 0x22uy :: required :: List.ofArray (pointerBytes p)
        | Item.Sentinel -> [ 0x41uy ]

    let private serialiseTerminator (terminator : Terminator) : byte list =
        match terminator with
        | Terminator.ElementType b -> [ b ]
        | Terminator.Internal p -> 0x21uy :: List.ofArray (pointerBytes p)

    let private genToken : Gen<EncodedToken> =
        gen {
            let! token = Gen.elements loadableTypeDefs
            let v = uint32 (token &&& 0x00FFFFFF) <<< 2

            let! width =
                Gen.elements
                    [
                        if v <= 0x7Fu then
                            yield 1
                        if v <= 0x3FFFu then
                            yield 2
                        yield 4
                    ]

            return
                {
                    Token = token
                    Width = width
                }
        }

    let private genItem (sentinels : bool) : Gen<Item> =
        Gen.frequency
            [
                yield 4, Gen.map2 (fun p t -> Item.Modifier (p, t)) (Gen.elements [ 0x1Fuy ; 0x20uy ]) genToken
                yield
                    2,
                    Gen.map2
                        (fun r p -> Item.InternalModifier (r, p))
                        (Gen.elements [ 0uy ; 1uy ; 2uy ; 0xFFuy ])
                        (Gen.elements typeHandles)
                if sentinels then
                    yield 1, Gen.constant Item.Sentinel
            ]

    let private genTerminator : Gen<Terminator> =
        Gen.frequency
            [
                3,
                Gen.elements [ 0x08uy ; 0x0Euy ; 0x12uy ; 0x15uy ; 0x1Duy ; 0x45uy ]
                |> Gen.map Terminator.ElementType
                2, Gen.elements typeHandles |> Gen.map Terminator.Internal
            ]

    type private Case =
        {
            Signature : byte[]
            Offset : int
            Required : bool
            Items : Item list
            Terminator : Terminator
            Truncated : bool
        }

    let private genCase : Gen<Case> =
        gen {
            let! sentinels = Gen.frequency [ 3, Gen.constant false ; 1, Gen.constant true ]
            let! n = Gen.choose (0, 5)
            let! items = Gen.listOfLength n (genItem sentinels)
            let items = List.ofSeq items
            let! terminator = genTerminator
            let! prefixLength = Gen.choose (0, 3)
            let! prefix = Gen.arrayOfLength prefixLength (Gen.choose (0, 255) |> Gen.map byte)
            let! required = Gen.elements [ true ; false ]

            let intact =
                Array.append
                    prefix
                    (List.collect serialiseItem items @ serialiseTerminator terminator
                     |> Array.ofList)

            let! cut =
                Gen.frequency
                    [
                        3, Gen.constant None
                        1, Gen.choose (prefixLength, intact.Length - 1) |> Gen.map Some
                    ]

            return
                {
                    Signature =
                        match cut with
                        | None -> intact
                        | Some cut -> Array.truncate cut intact
                    Offset = prefixLength
                    Required = required
                    Items = items
                    Terminator = terminator
                    Truncated = cut.IsSome
                }
        }

    type private Label =
        | Outcome of string
        | Contains of string

    /// Whether the host can be asked about `modifiers` without loading a token it cannot, or reading
    /// a TypeHandle that does not exist: true when every one is a loadable TypeDef or a real handle.
    let private hostCanLoad (modifiers : CustomModifierType list) : bool =
        modifiers
        |> List.forall (fun m ->
            match m with
            | CustomModifierType.Token t -> loadableTypeDefSet.Contains t
            | CustomModifierType.RuntimeTypeHandle p -> typeHandleSet.Contains (pointerOf p)
        )

    [<Test>]
    let ``at agrees with the host runtime's GetCustomModifiersAtOffset`` () : unit =
        let property (cover : Label -> unit) (case : Case) : unit =
            let hasSentinel = case.Items |> List.contains Item.Sentinel

            for item in case.Items do
                match item with
                | Item.Modifier (0x1Fuy, _) -> cover (Label.Contains "modreq")
                | Item.Modifier _ -> cover (Label.Contains "modopt")
                | Item.InternalModifier (r, _) when (r <> 0uy) = case.Required ->
                    cover (Label.Contains "matching CMOD_INTERNAL")
                | Item.InternalModifier _ -> cover (Label.Contains "other CMOD_INTERNAL")
                | Item.Sentinel -> cover (Label.Contains "sentinel")

            match case.Terminator with
            | Terminator.Internal _ when not case.Truncated -> cover (Label.Contains "INTERNAL after the modifiers")
            | _ -> ()

            let ours =
                SignatureCustomModifiers.at
                    IntPtr.Size
                    (ImmutableArray.Create<byte> case.Signature)
                    case.Offset
                    case.Required

            match ours with
            | CustomModifiersAtOffset.Modifiers modifiers when hostCanLoad modifiers ->
                cover (Label.Outcome (if modifiers.IsEmpty then "no modifiers" else "modifiers"))

                if hasSentinel then
                    cover (Label.Outcome "checked with a sentinel")

                if not hasSentinel && not case.Truncated then
                    // Without a sentinel, both of CoreCLR's passes read the modifiers alike.
                    let expected =
                        case.Items
                        |> List.choose (fun item ->
                            match item with
                            | Item.Modifier (prefix, t) when (prefix = 0x1Fuy) = case.Required ->
                                Some (CustomModifierType.Token t.Token)
                            | Item.InternalModifier (r, p) when (r <> 0uy) = case.Required ->
                                Some (
                                    CustomModifierType.RuntimeTypeHandle (ImmutableArray.Create<byte> (pointerBytes p))
                                )
                            | _ -> None
                        )
                        |> List.rev

                    modifiers |> shouldEqual expected

                match hostModifiers case.Signature case.Offset case.Required with
                | Error e -> failwith $"host threw where ours answered %A{modifiers}: %O{e}"
                | Ok types ->
                    types.Length |> shouldEqual modifiers.Length

                    for t, m in Seq.zip types modifiers do
                        match m with
                        | CustomModifierType.Token token ->
                            t.Module |> shouldEqual anchorModule
                            t.MetadataToken |> shouldEqual token
                        | CustomModifierType.RuntimeTypeHandle p -> t.TypeHandle.Value |> shouldEqual (pointerOf p)
            | CustomModifiersAtOffset.Modifiers _ ->
                // The second pass misread a sentinel and named a token the host may not load.
                cover (Label.Outcome "unverifiable")
            | CustomModifiersAtOffset.BadSignature when hasSentinel ->
                // The host's second pass may load a misread token before it fails.
                cover (Label.Outcome "unverifiable")
            | CustomModifiersAtOffset.BadSignature ->
                cover (Label.Outcome "bad signature")

                match hostModifiers case.Signature case.Offset case.Required with
                | Ok types -> failwith $"host answered %A{types} where ours found a bad signature"
                | Error _ -> ()

        let coverage =
            CoverageSample.check (Config.QuickThrowOnFailure.WithMaxTest 3000) (Arb.fromGen genCase) property

        for outcome in [ "modifiers" ; "no modifiers" ; "bad signature" ; "checked with a sentinel" ] do
            coverage.Count (Label.Outcome outcome) |> shouldBeGreaterThan 100

        for contained in
            [
                "modreq"
                "modopt"
                "matching CMOD_INTERNAL"
                "other CMOD_INTERNAL"
                "sentinel"
                "INTERNAL after the modifiers"
            ] do
            coverage.Count (Label.Contains contained) |> shouldBeGreaterThan 100

    let private pointer : byte[] = Array.init 8 (fun i -> byte (0xA0 + i))

    [<Test>]
    let ``ELEMENT_TYPE_INTERNAL (0x21) after the modifiers ends them`` () : unit =
        // modreq(TypeDef 2) INTERNAL <pointer>
        let signature =
            ImmutableArray.CreateRange (Array.append [| 0x1Fuy ; 0x08uy ; 0x21uy |] pointer)

        SignatureCustomModifiers.at 8 signature 0 true
        |> shouldEqual (CustomModifiersAtOffset.Modifiers [ CustomModifierType.Token 0x02000002 ])

    [<Test>]
    let ``ELEMENT_TYPE_CMOD_INTERNAL (0x22) is a modifier whose requiredness is its next byte`` () : unit =
        // CMOD_INTERNAL required <pointer> modopt(TypeDef 2) I4
        let signature =
            ImmutableArray.CreateRange (
                Array.concat [ [| 0x22uy ; 0x01uy |] ; pointer ; [| 0x20uy ; 0x08uy ; 0x08uy |] ]
            )

        SignatureCustomModifiers.at 8 signature 0 true
        |> shouldEqual (
            CustomModifiersAtOffset.Modifiers
                [ CustomModifierType.RuntimeTypeHandle (ImmutableArray.Create<byte> pointer) ]
        )

        SignatureCustomModifiers.at 8 signature 0 false
        |> shouldEqual (CustomModifiersAtOffset.Modifiers [ CustomModifierType.Token 0x02000002 ])

    [<Test>]
    let ``The second pass reads a sentinel as a modifier prefix`` () : unit =
        // SENTINEL modreq(TypeDef 8) I4. The first pass counts one required modifier. The second
        // reads the sentinel as a prefix of the token 0x1F (BaseType row 7), which it skips; then
        // 0x20 as a prefix of the token 0x08 (TypeDef row 2), which is no CMOD_REQD, so it skips
        // that too, and runs off the end still looking for the modifier it counted.
        let signature = ImmutableArray.Create<byte> [| 0x41uy ; 0x1Fuy ; 0x20uy ; 0x08uy |]

        SignatureCustomModifiers.at 8 signature 0 true
        |> shouldEqual CustomModifiersAtOffset.BadSignature

        // Asked for optional modifiers instead, the first pass counts none and the second does not
        // run.
        SignatureCustomModifiers.at 8 signature 0 false
        |> shouldEqual (CustomModifiersAtOffset.Modifiers [])
