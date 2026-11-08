namespace WoofWare.PawPrint

open System.Collections.Immutable

/// The type one custom modifier names, as CoreCLR's <c>Signature_GetCustomModifiersAtOffset</c>
/// reads it from a signature blob.
[<RequireQualifiedAccess>]
type CustomModifierType =
    /// <c>ELEMENT_TYPE_CMOD_REQD</c> or <c>ELEMENT_TYPE_CMOD_OPT</c>: the <c>mdToken</c> CoreCLR
    /// passes to <c>ClassLoader::LoadTypeDefOrRefOrSpecThrowing</c> in the signature's module.
    /// <c>CorSigUncompressToken</c> builds it by OR-ing the compressed value shifted right by two
    /// with the table its low two bits name (TypeDef, TypeRef, TypeSpec, or <c>mdtBaseType</c>,
    /// which no loader accepts). A row number of 2^24 or more overflows into the table byte, so
    /// the table this token names need not be any of those four.
    | Token of token : int
    /// <c>ELEMENT_TYPE_CMOD_INTERNAL</c>: a <c>TypeHandle</c> written by the runtime that built the
    /// signature, as the blob's pointer-sized bytes.
    | RuntimeTypeHandle of pointer : ImmutableArray<byte>

/// What CoreCLR's <c>Signature_GetCustomModifiersAtOffset</c> answers for one signature blob,
/// offset and requiredness.
[<RequireQualifiedAccess>]
type CustomModifiersAtOffset =
    /// The modifiers, in the order of the <c>Type[]</c> CoreCLR returns.
    | Modifiers of CustomModifierType list
    /// A read ran off the end of the blob or met a malformed compressed integer, and CoreCLR
    /// throws from <c>IfFailThrow</c>.
    | BadSignature

/// CoreCLR's <c>Signature_GetCustomModifiersAtOffset</c> (runtimehandles.cpp), over a signature
/// blob's bytes, short of loading the types its modifiers name.
[<RequireQualifiedAccess>]
module SignatureCustomModifiers =

    let private elementTypeCModReqd : byte = 0x1Fuy
    let private elementTypeCModOpt : byte = 0x20uy
    let private elementTypeCModInternal : byte = 0x22uy
    let private elementTypeSentinel : byte = 0x41uy

    /// `CorSigUncompressToken` (cor.h).
    let private token (signature : ImmutableArray<byte>) (position : int) : struct (int * int) voption =
        match SignatureParameterOffset.data signature position with
        | ValueNone -> ValueNone
        | ValueSome (struct (value, next)) ->
            let table =
                match value &&& 3u with
                | 0u -> 0x02000000u
                | 1u -> 0x01000000u
                | 2u -> 0x1B000000u
                | _ -> 0x72000000u

            ValueSome (struct (int ((value >>> 2) ||| table), next))

    /// The custom modifiers whose requiredness is <c>required</c>, at <c>offset</c> in
    /// <c>signature</c>. <c>pointerSize</c> is the width of the <c>TypeHandle</c> that
    /// <c>ELEMENT_TYPE_CMOD_INTERNAL</c> carries. <c>offset</c> must be within
    /// <c>0 .. signature.Length</c>.
    let at
        (pointerSize : int)
        (signature : ImmutableArray<byte>)
        (offset : int)
        (required : bool)
        : CustomModifiersAtOffset
        =
        if offset < 0 || offset > signature.Length then
            invalidArg (nameof offset) $"offset %d{offset} is outside 0..%d{signature.Length}"

        let expected = if required then elementTypeCModReqd else elementTypeCModOpt

        // CoreCLR reads the modifiers twice, and the two passes do not agree. The first counts the
        // modifiers to return, stepping over any number of sentinels, and stops at the first byte
        // that is neither a sentinel nor a modifier prefix.
        let rec count (position : int) (found : int) : int voption =
            if position >= signature.Length then
                ValueNone
            else
                let b = signature.[position]

                if b = elementTypeCModReqd || b = elementTypeCModOpt then
                    SignatureParameterOffset.skipData signature (position + 1)
                    |> ValueOption.bind (fun next -> count next (if b = expected then found + 1 else found))
                elif b = elementTypeCModInternal then
                    if position + 1 >= signature.Length then
                        ValueNone
                    else
                        let matches = (signature.[position + 1] <> 0uy) = required

                        SignatureParameterOffset.skipPointer pointerSize signature (position + 2)
                        |> ValueOption.bind (fun next -> count next (if matches then found + 1 else found))
                elif b = elementTypeSentinel then
                    count (position + 1) found
                else
                    ValueSome found

        // The second reads from the start again until it has found as many as the first counted,
        // and reads every byte that is not CMOD_INTERNAL as a prefix to a token, sentinels and
        // terminators included. CoreCLR fills its array from the last index down, so prepending
        // leaves the list in the array's order.
        let rec populate (position : int) (remaining : int) (acc : CustomModifierType list) : CustomModifiersAtOffset =
            if remaining = 0 then
                CustomModifiersAtOffset.Modifiers acc
            elif position >= signature.Length then
                CustomModifiersAtOffset.BadSignature
            else
                let b = signature.[position]

                if b = elementTypeCModInternal then
                    if position + 1 >= signature.Length then
                        CustomModifiersAtOffset.BadSignature
                    else
                        let matches = (signature.[position + 1] <> 0uy) = required

                        match SignatureParameterOffset.skipPointer pointerSize signature (position + 2) with
                        | ValueNone -> CustomModifiersAtOffset.BadSignature
                        | ValueSome next when matches ->
                            let handle =
                                CustomModifierType.RuntimeTypeHandle (signature.Slice (position + 2, pointerSize))

                            populate next (remaining - 1) (handle :: acc)
                        | ValueSome next -> populate next remaining acc
                else
                    match token signature (position + 1) with
                    | ValueNone -> CustomModifiersAtOffset.BadSignature
                    | ValueSome (struct (t, next)) when b = expected ->
                        populate next (remaining - 1) (CustomModifierType.Token t :: acc)
                    | ValueSome (struct (_, next)) -> populate next remaining acc

        match count offset 0 with
        | ValueNone -> CustomModifiersAtOffset.BadSignature
        | ValueSome found -> populate offset found []
