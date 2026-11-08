namespace WoofWare.PawPrint

/// What CoreCLR's JIT makes of an unchecked conversion from a float to an integer narrower than
/// 32 bits (`conv.i1`, `conv.u1`, `conv.i2`, `conv.u2`), whose result ECMA-335 leaves unspecified
/// for a NaN or out-of-range source. On every supported runtime NaN converts to 0, and a source
/// within the target's range truncates toward zero; the cases differ only for a source outside
/// it. `fgMorphExpandCast` (jit/morph.cpp) decides which rule a runtime follows.
[<RequireQualifiedAccess>]
type NarrowFloatConversion =
    /// The low bits of the saturating conversion to `int32`: `conv.u1` of 300.0 is 44, and
    /// `conv.u2` of 1e20 is 0xFFFF.
    | LowBitsOfSaturatedInt32
    /// Saturation to the target's own range: `conv.u1` of 300.0 is 255, and `conv.i2` of 1e20 is
    /// 32767. The JIT clamps the source to the target's range before converting it to `int32`.
    | Saturates

[<RequireQualifiedAccess>]
module NarrowFloatConversion =

    /// The rule `runtime`'s JIT follows.
    let ofRuntime (runtime : EmulatedRuntime) : NarrowFloatConversion =
        match runtime with
        | EmulatedRuntime.Net10 -> NarrowFloatConversion.LowBitsOfSaturatedInt32
        | EmulatedRuntime.Net11 -> NarrowFloatConversion.Saturates
