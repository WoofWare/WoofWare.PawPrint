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

    /// Where parameter <c>parameterIndex</c> begins in <c>signature</c>: index 0 is a method's
    /// return type (or a field's type) and index i its i-th parameter. <c>pointerSize</c> is the
    /// width of the runtime-only pointers that <c>ELEMENT_TYPE_INTERNAL</c> and
    /// <c>ELEMENT_TYPE_CMOD_INTERNAL</c> carry.
    let find (pointerSize : int) (signature : ImmutableArray<byte>) (parameterIndex : int) : ParameterOffset =
        failwith "TODO: SignatureParameterOffset.find"
