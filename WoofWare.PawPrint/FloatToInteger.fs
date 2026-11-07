namespace WoofWare.PawPrint

/// The unchecked float-to-integer conversions (`conv.i1` … `conv.u8`, `conv.i`, `conv.u`
/// applied to a float), with CoreCLR's .NET 9+ semantics on every host.
///
/// ECMA-335 leaves an out-of-range or NaN source unspecified, and the host's own `conv.*`
/// fills that gap differently by runtime and architecture (a .NET 8 x64 host returns
/// 0x80000000 for most of them), so the interpreter must not execute one on a value it
/// has not first brought into range. The rule implemented here is:
///
/// * a target of 32 bits or more: NaN converts to 0; otherwise the source is truncated
///   toward zero and clamped into the target's range;
/// * a narrower target: as the caller's `NarrowFloatConversion` says, which depends on the
///   runtime.
///
/// A float32 source gives the same answer as its widening to double, which is exact, so
/// only double sources are taken.
[<RequireQualifiedAccess>]
module FloatToInteger =

    // Each bound is a power of two and therefore an exact double. Within the open interval
    // between a target's bounds, truncation lands in range, and there ECMA-335 does define the
    // host conversion: it truncates toward zero.

    /// 2^63.
    let private twoTo63 : float = 9223372036854775808.0

    /// 2^64.
    let private twoTo64 : float = 18446744073709551616.0

    /// 2^31.
    let private twoTo31 : float = 2147483648.0

    /// 2^32.
    let private twoTo32 : float = 4294967296.0

    /// `conv.i8`; also `conv.i` on a 64-bit target.
    let toInt64 (x : float) : int64 =
        if System.Double.IsNaN x then 0L
        elif x <= -twoTo63 then System.Int64.MinValue
        elif x >= twoTo63 then System.Int64.MaxValue
        else int64 x

    /// `conv.u8`; also `conv.u` on a 64-bit target.
    let toUInt64 (x : float) : uint64 =
        if System.Double.IsNaN x || x <= 0.0 then 0UL
        elif x >= twoTo64 then System.UInt64.MaxValue
        else uint64 x

    /// `conv.i4`.
    let toInt32 (x : float) : int32 =
        if System.Double.IsNaN x then 0
        elif x <= -twoTo31 then System.Int32.MinValue
        elif x >= twoTo31 then System.Int32.MaxValue
        else int32 x

    /// `conv.u4`.
    let toUInt32 (x : float) : uint32 =
        if System.Double.IsNaN x || x <= 0.0 then 0u
        elif x >= twoTo32 then System.UInt32.MaxValue
        else uint32 x

    // `LowBitsOfSaturatedInt32` is an integer narrowing of the int32 conversion, which F#
    // performs unchecked and which ECMA-335 defines as truncation to the low bits.
    // `Saturates` clamps the source into the target's range first, as the JIT does; the clamped
    // value is in int32's range, so the int32 conversion truncates it toward zero into the
    // target's range, and the narrowing changes nothing. NaN passes through the clamp and
    // converts to 0.

    /// `x` clamped into [`lower`, `upper`], or NaN if `x` is NaN.
    let private clamp (lower : float) (upper : float) (x : float) : float =
        if x < lower then lower
        elif x > upper then upper
        else x

    /// `conv.i2`, by `rule`.
    let toInt16 (rule : NarrowFloatConversion) (x : float) : int16 =
        match rule with
        | NarrowFloatConversion.LowBitsOfSaturatedInt32 -> int16 (toInt32 x)
        | NarrowFloatConversion.Saturates -> int16 (toInt32 (clamp -32768.0 32767.0 x))

    /// `conv.u2`, by `rule`.
    let toUInt16 (rule : NarrowFloatConversion) (x : float) : uint16 =
        match rule with
        | NarrowFloatConversion.LowBitsOfSaturatedInt32 -> uint16 (toInt32 x)
        | NarrowFloatConversion.Saturates -> uint16 (toInt32 (clamp 0.0 65535.0 x))

    /// `conv.i1`, by `rule`.
    let toInt8 (rule : NarrowFloatConversion) (x : float) : int8 =
        match rule with
        | NarrowFloatConversion.LowBitsOfSaturatedInt32 -> int8 (toInt32 x)
        | NarrowFloatConversion.Saturates -> int8 (toInt32 (clamp -128.0 127.0 x))

    /// `conv.u1`, by `rule`.
    let toUInt8 (rule : NarrowFloatConversion) (x : float) : uint8 =
        match rule with
        | NarrowFloatConversion.LowBitsOfSaturatedInt32 -> uint8 (toInt32 x)
        | NarrowFloatConversion.Saturates -> uint8 (toInt32 (clamp 0.0 255.0 x))
