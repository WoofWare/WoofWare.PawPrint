using System;

public class ConvFloatSaturating
{
    // Exercises the unchecked float-to-integer conversions (`conv.i1` … `conv.u8`,
    // `conv.i`, `conv.u`) on sources that are NaN, infinite, or out of the target's
    // range, from both float64 and float32.
    //
    // On .NET 9+ every target of 32 bits or more saturates (NaN converts to 0). On
    // .NET 10 a narrower target takes the low bits of the saturated int32 conversion
    // rather than saturating to its own range: `(byte)300.0` is 44. On .NET 11 it
    // saturates to its own range too: `(byte)300.0` is 255.
    //
    // The sources are static fields so that Roslyn cannot fold the conversions.

    private static double s_nan = double.NaN;
    private static double s_posInf = double.PositiveInfinity;
    private static double s_negInf = double.NegativeInfinity;
    private static double s_huge = 1e20;
    private static double s_negHuge = -1e20;
    private static double s_twoTo31 = 2147483648.0;
    private static double s_twoTo63 = 9223372036854775808.0;
    private static double s_twoTo64 = 18446744073709551616.0;
    private static double s_threeHundred = 300.0;
    private static double s_minusOne = -1.0;
    private static double s_minusOneAndAHalf = -1.5;
    private static double s_seventyThousand = 70000.7;
    private static float s_nanSingle = float.NaN;
    private static float s_hugeSingle = 1e20f;
    private static float s_negHugeSingle = -1e20f;
    private static float s_threeHundredSingle = 300.0f;

    public static int Main(string[] args)
    {
        // NaN converts to 0 for every target.
        if ((int)s_nan != 0) return 1;
        if ((uint)s_nan != 0u) return 2;
        if ((long)s_nan != 0L) return 3;
        if ((ulong)s_nan != 0UL) return 4;
        if ((byte)s_nan != 0) return 5;
        if ((sbyte)s_nan != 0) return 6;
        if ((short)s_nan != 0) return 7;
        if ((ushort)s_nan != 0) return 8;
        if ((nint)s_nan != 0) return 9;
        if ((nuint)s_nan != 0) return 10;
        if ((int)s_nanSingle != 0) return 11;
        if ((ulong)s_nanSingle != 0UL) return 12;

        // 32- and 64-bit targets saturate.
        if ((int)s_posInf != int.MaxValue) return 20;
        if ((int)s_negInf != int.MinValue) return 21;
        if ((int)s_twoTo31 != int.MaxValue) return 22;
        if ((uint)s_huge != uint.MaxValue) return 23;
        if ((uint)s_minusOneAndAHalf != 0u) return 24;
        if ((long)s_twoTo63 != long.MaxValue) return 25;
        if ((long)s_negHuge != long.MinValue) return 26;
        if ((ulong)s_twoTo64 != ulong.MaxValue) return 27;
        if ((ulong)s_twoTo63 != 9223372036854775808UL) return 28;
        if ((ulong)s_minusOne != 0UL) return 29;
        if ((ulong)s_negInf != 0UL) return 30;
        if ((nint)s_huge != nint.MaxValue) return 31;
        if ((nuint)s_huge != nuint.MaxValue) return 32;
        if ((nuint)s_minusOne != 0) return 33;
        if ((int)s_hugeSingle != int.MaxValue) return 34;
        if ((long)s_negHugeSingle != long.MinValue) return 35;
        if ((uint)s_negHugeSingle != 0u) return 36;

        if (Environment.Version.Major == 10)
        {
            return NarrowTargetsWrapTheSaturatedInt32();
        }

        return NarrowTargetsSaturate();
    }

    private static int NarrowTargetsWrapTheSaturatedInt32()
    {
        if ((byte)s_threeHundred != 44) return 40;
        if ((sbyte)s_threeHundred != 44) return 41;
        if ((byte)s_minusOne != 255) return 42;
        if ((ushort)s_minusOne != 65535) return 43;
        if ((ushort)s_seventyThousand != 4464) return 44;
        if ((short)s_seventyThousand != 4464) return 45;
        if ((ushort)s_huge != 65535) return 46;
        if ((short)s_huge != -1) return 47;
        if ((sbyte)s_negHuge != 0) return 48;
        if ((byte)s_posInf != 255) return 49;
        if ((char)s_huge != '￿') return 50;
        if ((byte)s_threeHundredSingle != 44) return 51;
        if ((short)s_negHugeSingle != 0) return 52;
        if ((byte)s_minusOneAndAHalf != 255) return 53;

        return 0;
    }

    private static int NarrowTargetsSaturate()
    {
        if ((byte)s_threeHundred != 255) return 60;
        if ((sbyte)s_threeHundred != 127) return 61;
        if ((byte)s_minusOne != 0) return 62;
        if ((ushort)s_minusOne != 0) return 63;
        if ((ushort)s_seventyThousand != 65535) return 64;
        if ((short)s_seventyThousand != 32767) return 65;
        if ((ushort)s_huge != 65535) return 66;
        if ((short)s_huge != 32767) return 67;
        if ((sbyte)s_negHuge != -128) return 68;
        if ((byte)s_posInf != 255) return 69;
        if ((char)s_huge != '\uFFFF') return 70;
        if ((byte)s_threeHundredSingle != 255) return 71;
        if ((short)s_negHugeSingle != -32768) return 72;
        if ((byte)s_minusOneAndAHalf != 0) return 73;
        if ((sbyte)s_negInf != -128) return 74;
        if ((short)s_negInf != -32768) return 75;
        // A source within range truncates toward zero, as on every runtime.
        if ((sbyte)s_minusOneAndAHalf != -1) return 76;

        return 0;
    }
}
