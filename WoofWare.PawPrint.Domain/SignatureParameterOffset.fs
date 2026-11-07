namespace WoofWare.PawPrint

open System.Collections.Immutable

/// What CoreCLR's <c>SignatureNative::GetParameterOffsetInternal</c> answers for one signature
/// blob and parameter index.
[<RequireQualifiedAccess>]
type ParameterOffset =
    /// The byte offset, from the start of the blob, at which the parameter's element begins,
    /// including any custom modifiers and vararg sentinel before it.
    | Offset of int
    /// The walk ran off the end of the blob or met a byte it does not accept. CoreCLR returns
    /// <c>META_E_BAD_SIGNATURE</c> (<see cref="M:WoofWare.PawPrint.SignatureParameterOffset.metaEBadSignature"/>),
    /// which <c>Signature.GetParameterOffset</c> throws through <c>Marshal.ThrowExceptionForHR</c>.
    | BadSignature
    /// The index is outside <c>0 .. greatestIndex</c>: the parameter count for a method-shaped
    /// signature, 0 for a field's. CoreCLR asserts against this only in a checked build, and a
    /// release build walks on past the last parameter, so the answer is undefined rather than an
    /// error the guest can observe.
    | ParameterIndexOutOfRange of parameterIndex : int * greatestIndex : int

/// CoreCLR's <c>SignatureNative::GetParameterOffsetInternal</c> (runtimehandles.cpp), over a
/// signature blob's bytes.
[<RequireQualifiedAccess>]
module SignatureParameterOffset =

    /// <c>META_E_BAD_SIGNATURE</c>, the HRESULT CoreCLR returns in place of an offset when the walk
    /// fails.
    let metaEBadSignature : int = int 0x80131192u

    // Each step below is one of `SigParser`'s (sigparser.h, utilcode/sigparser.cpp), taking the
    // position of the next unread byte and answering the position after what it read, or
    // ValueNone where `SigParser` answers META_E_BAD_SIGNATURE. Every failure in this walk is
    // that one HRESULT.

    /// `CorSigUncompressData` (cor.h): one compressed integer, of whichever width its first byte
    /// announces, however small the value.
    let private data (signature : ImmutableArray<byte>) (position : int) : struct (uint32 * int) voption =
        let remaining = signature.Length - position

        if remaining < 1 then
            ValueNone
        else
            let first = signature.[position]

            if first &&& 0x80uy = 0uy then
                ValueSome (struct (uint32 first, position + 1))
            elif first &&& 0xC0uy = 0x80uy then
                if remaining < 2 then
                    ValueNone
                else
                    ValueSome (
                        struct ((uint32 (first &&& 0x3Fuy) <<< 8) ||| uint32 signature.[position + 1], position + 2)
                    )
            elif first &&& 0xE0uy = 0xC0uy then
                if remaining < 4 then
                    ValueNone
                else
                    let value =
                        (uint32 (first &&& 0x1Fuy) <<< 24)
                        ||| (uint32 signature.[position + 1] <<< 16)
                        ||| (uint32 signature.[position + 2] <<< 8)
                        ||| uint32 signature.[position + 3]

                    ValueSome (struct (value, position + 4))
            else
                ValueNone

    /// `GetData(NULL)`, and also `GetToken(NULL)`: a compressed token is a compressed integer whose
    /// two low bits name its table, and every table tag decodes.
    let private skipData (signature : ImmutableArray<byte>) (position : int) : int voption =
        match data signature position with
        | ValueSome (struct (_, next)) -> ValueSome next
        | ValueNone -> ValueNone

    /// `GetPointer(NULL)`.
    let private skipPointer (pointerSize : int) (signature : ImmutableArray<byte>) (position : int) : int voption =
        if signature.Length - position < pointerSize then
            ValueNone
        else
            ValueSome (position + pointerSize)

    let private elementTypeCModReqd : byte = 0x1Fuy
    let private elementTypeCModOpt : byte = 0x20uy
    let private elementTypeCModInternal : byte = 0x22uy
    let private elementTypeMax : byte = 0x23uy
    let private elementTypeSentinel : byte = 0x41uy

    /// `SkipCustomModifiers`: at most one vararg sentinel, then any custom modifiers, then a check
    /// that what follows is an element type `SigParser` accepts after them. It does not consume
    /// that element type.
    let private skipCustomModifiers
        (pointerSize : int)
        (signature : ImmutableArray<byte>)
        (position : int)
        : int voption
        =
        if position >= signature.Length then
            ValueNone
        else
            let position =
                if signature.[position] = elementTypeSentinel then
                    position + 1
                else
                    position

            let rec modifiers (position : int) : int voption =
                if position >= signature.Length then
                    ValueNone
                else
                    let b = signature.[position]

                    if b = elementTypeCModReqd || b = elementTypeCModOpt then
                        skipData signature (position + 1) |> ValueOption.bind modifiers
                    elif b = elementTypeCModInternal then
                        // One byte saying whether the modifier is required, then a TypeHandle.
                        // `SigParser` checks for the byte before the pointer, but a blob too short
                        // for the byte is too short for the pointer, and both fail alike.
                        skipPointer pointerSize signature (position + 2) |> ValueOption.bind modifiers
                    elif b < elementTypeMax then
                        ValueSome position
                    else
                        match b with
                        // VAR_ZAPSIG, NATIVE_VALUETYPE_ZAPSIG, CANON_ZAPSIG, MODULE_ZAPSIG, PINNED
                        | 0x3Buy
                        | 0x3Duy
                        | 0x3Euy
                        | 0x3Fuy
                        | 0x45uy -> ValueSome position
                        | _ -> ValueNone

            modifiers position

    /// `GetElemType`: the element type after any sentinel and custom modifiers, and the position
    /// after it.
    let private elementType
        (pointerSize : int)
        (signature : ImmutableArray<byte>)
        (position : int)
        : struct (byte * int) voption
        =
        if position < signature.Length && signature.[position] < elementTypeCModReqd then
            ValueSome (struct (signature.[position], position + 1))
        else
            match skipCustomModifiers pointerSize signature position with
            | ValueSome position when position < signature.Length ->
                ValueSome (struct (signature.[position], position + 1))
            | _ -> ValueNone

    /// Applies `step` `n` times, stopping at the first failure.
    let rec private repeat (n : uint32) (step : int -> int voption) (position : int) : int voption =
        if n = 0u then
            ValueSome position
        else
            match step position with
            | ValueSome next -> repeat (n - 1u) step next
            | ValueNone -> ValueNone

    /// `SkipExactlyOne`: one element, with everything it contains.
    let rec private skipExactlyOne
        (pointerSize : int)
        (signature : ImmutableArray<byte>)
        (position : int)
        : int voption
        =
        let skipOne = skipExactlyOne pointerSize signature

        match elementType pointerSize signature position with
        | ValueNone -> ValueNone
        | ValueSome (struct (typ, position)) ->
            match typ with
            // `CorIsPrimitiveType`: everything below PTR, and I and U.
            | typ when typ < 0x0Fuy || typ = 0x18uy || typ = 0x19uy -> ValueSome position
            // VAR, MVAR, VAR_ZAPSIG: a number.
            | 0x13uy
            | 0x1Euy
            | 0x3Buy -> skipData signature position
            // OBJECT, TYPEDBYREF, CANON_ZAPSIG
            | 0x1Cuy
            | 0x16uy
            | 0x3Euy -> ValueSome position
            // BYREF, PTR, PINNED, SZARRAY, NATIVE_VALUETYPE_ZAPSIG: one element.
            | 0x10uy
            | 0x0Fuy
            | 0x45uy
            | 0x1Duy
            | 0x3Duy -> skipOne position
            // VALUETYPE, CLASS: a token.
            | 0x11uy
            | 0x12uy -> skipData signature position
            // MODULE_ZAPSIG: a module index, then one element.
            | 0x3Fuy -> skipData signature position |> ValueOption.bind skipOne
            // FNPTR: a whole method signature.
            | 0x1Buy -> skipSignature pointerSize signature position
            // ARRAY: the element, the rank, and for a nonzero rank the sizes and lower bounds,
            // each list preceded by its length.
            | 0x14uy ->
                match skipOne position |> ValueOption.bind (data signature) with
                | ValueNone -> ValueNone
                | ValueSome (struct (0u, position)) -> ValueSome position
                | ValueSome (struct (_, position)) ->
                    let counted (position : int) : int voption =
                        match data signature position with
                        | ValueSome (struct (n, position)) -> repeat n (skipData signature) position
                        | ValueNone -> ValueNone

                    counted position |> ValueOption.bind counted
            // SENTINEL: unreachable, since `elementType` consumes a sentinel and refuses a second.
            | 0x41uy -> ValueSome position
            // INTERNAL: a TypeHandle.
            | 0x21uy -> skipPointer pointerSize signature position
            // GENERICINST: the generic type, then its arguments, preceded by their number.
            | 0x15uy ->
                match skipOne position |> ValueOption.bind (data signature) with
                | ValueSome (struct (n, position)) -> repeat n skipOne position
                | ValueNone -> ValueNone
            | _ -> ValueNone

    /// `SkipSignature`: `SkipMethodHeaderSignature`, which refuses a calling convention of exactly
    /// FIELD or LOCAL_SIG (unmasked, so with any flag set it is read as a method's), then the
    /// parameters.
    and private skipSignature (pointerSize : int) (signature : ImmutableArray<byte>) (position : int) : int voption =
        if position >= signature.Length then
            ValueNone
        else
            let callingConvention = signature.[position]

            if callingConvention = 0x06uy || callingConvention = 0x07uy then
                ValueNone
            else
                let afterGenericCount =
                    if callingConvention &&& 0x10uy <> 0uy then
                        skipData signature (position + 1)
                    else
                        ValueSome (position + 1)

                match afterGenericCount |> ValueOption.bind (data signature) with
                | ValueNone -> ValueNone
                | ValueSome (struct (parameterCount, position)) ->
                    // The return type, then the parameters.
                    skipExactlyOne pointerSize signature position
                    |> ValueOption.bind (repeat parameterCount (skipExactlyOne pointerSize signature))

    /// Where parameter <c>parameterIndex</c> begins in <c>signature</c>: index 0 is a method's
    /// return type (or a field's type) and index i its i-th parameter. <c>pointerSize</c> is the
    /// width of the runtime-only pointers that <c>ELEMENT_TYPE_INTERNAL</c> and
    /// <c>ELEMENT_TYPE_CMOD_INTERNAL</c> carry.
    let find (pointerSize : int) (signature : ImmutableArray<byte>) (parameterIndex : int) : ParameterOffset =
        let ofPosition (position : int voption) : ParameterOffset =
            match position with
            | ValueSome position -> ParameterOffset.Offset position
            | ValueNone -> ParameterOffset.BadSignature

        if signature.IsEmpty then
            ParameterOffset.BadSignature
        else
            let callingConvention = signature.[0]

            if callingConvention &&& 0x0Fuy = 0x06uy then
                if parameterIndex <> 0 then
                    ParameterOffset.ParameterIndexOutOfRange (parameterIndex, 0)
                else
                    ParameterOffset.Offset 1
            else
                let afterGenericCount =
                    if callingConvention &&& 0x10uy <> 0uy then
                        skipData signature 1
                    else
                        ValueSome 1

                match afterGenericCount |> ValueOption.bind (data signature) with
                | ValueNone -> ParameterOffset.BadSignature
                | ValueSome (struct (parameterCount, position)) ->
                    // A compressed integer is at most 0x1FFFFFFF, so the count fits an int.
                    let parameterCount = int parameterCount

                    if parameterIndex < 0 || parameterIndex > parameterCount then
                        ParameterOffset.ParameterIndexOutOfRange (parameterIndex, parameterCount)
                    else
                        repeat (uint32 parameterIndex) (skipExactlyOne pointerSize signature) position
                        |> ofPosition
