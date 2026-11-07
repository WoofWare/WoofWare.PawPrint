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
    let private elementTypeCModInternal : byte = 0x21uy
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

        let rec scan (position : int) (acc : CustomModifierType list) : CustomModifiersAtOffset =
            if position >= signature.Length then
                CustomModifiersAtOffset.BadSignature
            else
                let b = signature.[position]

                if b = elementTypeCModReqd || b = elementTypeCModOpt then
                    match token signature (position + 1) with
                    | ValueNone -> CustomModifiersAtOffset.BadSignature
                    | ValueSome (struct (t, next)) ->
                        scan
                            next
                            (if b = expected then
                                 CustomModifierType.Token t :: acc
                             else
                                 acc)
                elif b = elementTypeCModInternal then
                    failwith $"TODO: CMOD_INTERNAL (0x21) at offset %d{position}"
                elif b = elementTypeSentinel then
                    scan (position + 1) acc
                else
                    CustomModifiersAtOffset.Modifiers acc

        scan offset []
