using System;

// Math.Log10(double) is [Intrinsic] + InternalCall in CoreCLR: it has no IL body, and the JIT lowers
// it to a call into the platform C library's `log10`. PawPrint implements it in-tree instead
// (DeterministicMath.log10), so that a replay cannot depend on which libm the recording machine had.
//
// This file runs under both PawPrint and the real runtime and the two exit codes are compared, so
// it may only assert things *both* must agree on. `log10` is not correctly rounded by every libm,
// so most checks below allow a slack far wider than an ulp. The exceptions are the special cases
// C99 Annex F fixes exactly, and the powers of ten 10^0 to 10^22, the only doubles whose decimal
// logarithm is rational: there the answer is an integer, a double, and fdlibm's `log10` (the
// ancestor of glibc's) promises it exactly, as do macOS's libm and PawPrint. Those are pinned
// exactly, because they are what a caller counting decimal digits relies on: FakeItEasy's
// `(int)Math.Log10(n) + 1` counts three digits in 1000 only if `Math.Log10(1000)` is exactly 3.
//
// The NaN cases check double.IsNaN and never the bits: hosts differ on the payload and the sign of
// both a generated NaN and a propagated one. The bit-exact specification of PawPrint's own answer
// lives in TestDeterministicMath.fs.
//
// Every failure code below is distinct and stays under 128. A process exit code is eight bits, so a
// code of 256 would reach the harness as 0 and a wrong answer would read as success; and codes from
// 128 up are indistinguishable from the 128+signo of a guest that died on a signal.
public static class MathLog10
{
    const double Log10Of2 = 0.30102999566398119521;

    static int SpecialCases()
    {
        // C17 F.10.3.8: log10(1) is +0, and the sign of that zero is specified.
        if (Math.Log10(1.0) != 0.0) return 1;
        if (double.IsNegative(Math.Log10(1.0))) return 2;

        // log10(±0) is -infinity, a pole rather than a domain error: both zeros alike.
        if (!double.IsNegativeInfinity(Math.Log10(0.0))) return 3;
        if (!double.IsNegativeInfinity(Math.Log10(-0.0))) return 4;

        if (!double.IsPositiveInfinity(Math.Log10(double.PositiveInfinity))) return 5;

        // Every argument below zero is a domain error, -infinity included.
        if (!double.IsNaN(Math.Log10(-1.0))) return 6;
        if (!double.IsNaN(Math.Log10(-double.Epsilon))) return 7;
        if (!double.IsNaN(Math.Log10(double.MinValue))) return 8;
        if (!double.IsNaN(Math.Log10(double.NegativeInfinity))) return 9;

        // NaN propagates. Which NaN is deliberately not asserted; see the file comment.
        if (!double.IsNaN(Math.Log10(double.NaN))) return 10;

        return 0;
    }

    static int PowersOfTen()
    {
        // 10^n for n from 0 to 22, each built by multiplying the last by 10, which is exact while
        // the product is a double: 10^n = 2^n 5^n, and 5^22 < 2^53 < 5^23. The answer is exactly n.
        double power = 1.0;

        for (int n = 0; n <= 22; n++)
        {
            if (Math.Log10(power) != n) return 20 + n;

            power *= 10.0;
        }

        return 0;
    }

    static int Digits(int n)
    {
        // How FakeItEasy's CallWriter pads a call number.
        return (int)Math.Log10(n) + 1;
    }

    static int DigitCounts()
    {
        // The digit count of every n from 1 to 9, and of the seven integers either side of each
        // power of ten from 10 to 10^6, counted by arithmetic.
        for (int n = 1; n <= 9; n++)
        {
            if (Digits(n) != 1) return 50;
        }

        int boundary = 1;

        for (int k = 1; k <= 6; k++)
        {
            boundary *= 10;

            for (int n = boundary - 3; n <= boundary + 3; n++)
            {
                int expected = n >= boundary ? k + 1 : k;
                if (Digits(n) != expected) return 50 + k;
            }
        }

        return 0;
    }

    static int NegativePowersOfTen()
    {
        // 10^-k is not a double, so the argument is the double nearest it, whose decimal logarithm
        // lies within 5e-17 of -k. The slack is several ulps of k.
        double[] arguments =
        {
            1e-1, 1e-2, 1e-3, 1e-4, 1e-5, 1e-6, 1e-7, 1e-8, 1e-9, 1e-10, 1e-11, 1e-12, 1e-13, 1e-14,
            1e-15, 1e-16, 1e-17, 1e-18, 1e-19, 1e-20, 1e-21, 1e-22,
        };

        for (int i = 0; i < arguments.Length; i++)
        {
            int k = i + 1;
            if (Math.Abs(Math.Log10(arguments[i]) + k) > 1e-15 * k) return 60 + i;
        }

        return 0;
    }

    static int KnownValues()
    {
        // Values whose decimal logarithm is known to far more digits than a double holds, checked to
        // a relative tolerance of a few ulps.
        (double x, double log10)[] table =
        {
            (2.0, 0.30102999566398119521),
            (3.0, 0.47712125471966243730),
            (0.5, -0.30102999566398119521),
            (Math.E, 0.43429448190325180455),
            (123.456, 2.0915122016277716919),
            (1e23, 22.999999999999999964),
            (double.MaxValue, 308.25471555991674385),
            (double.Epsilon, -323.30621534311580366),
            (2.2250738585072014e-308, -307.65265556858878151),
        };

        for (int i = 0; i < table.Length; i++)
        {
            double actual = Math.Log10(table[i].x);

            if (Math.Abs(actual - table[i].log10) > 4e-16 * Math.Abs(table[i].log10))
                return 90 + i;
        }

        return 0;
    }

    static int IsMonotone()
    {
        // A strictly increasing function, sampled far enough apart that a one-ulp disagreement
        // between implementations cannot reorder adjacent results. The array is in increasing
        // order, which this also checks.
        double[] increasing =
        {
            double.Epsilon, 1e-320, 1e-300, 1e-100, 1e-10, 0.1, 0.5, 0.9, 0.999999, 1.0,
            1.000001, 1.1, 2.0, 10.0, 1e10, 1e100, 1e300, double.MaxValue,
        };

        double previous = double.NegativeInfinity;

        for (int i = 0; i < increasing.Length; i++)
        {
            double r = Math.Log10(increasing[i]);

            if (!(r > previous)) return 100 + i;

            previous = r;
        }

        return 0;
    }

    static int ProductRule()
    {
        // log10(x * 2^k) = log10(x) + k log10(2). Scaling by a power of two is exact, so both sides
        // are the same real number, and a mistake in how the exponent is split off from the mantissa
        // shows up here by a margin of order log10(2), not of order an ulp.
        double[] arguments = { 1.5, 3.0, 0.1, 0.7, 1.4142135623730951, 10.0, 6.02214076e23 };

        int[] shifts = { -900, -100, -1, 1, 7, 100, 900 };

        for (int i = 0; i < arguments.Length; i++)
        {
            double x = arguments[i];

            for (int j = 0; j < shifts.Length; j++)
            {
                double scaled = Math.ScaleB(x, shifts[j]);
                double lhs = Math.Log10(scaled);
                double rhs = Math.Log10(x) + shifts[j] * Log10Of2;

                if (Math.Abs(lhs - rhs) > 1e-12) return 120 + i;
            }
        }

        return 0;
    }

    public static int Main(string[] args)
    {
        int result;

        result = SpecialCases();
        if (result != 0) return result;

        result = PowersOfTen();
        if (result != 0) return result;

        result = DigitCounts();
        if (result != 0) return result;

        result = NegativePowersOfTen();
        if (result != 0) return result;

        result = KnownValues();
        if (result != 0) return result;

        result = IsMonotone();
        if (result != 0) return result;

        result = ProductRule();
        if (result != 0) return result;

        return 0;
    }
}
