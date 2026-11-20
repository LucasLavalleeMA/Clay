using System.Globalization;
using System.Numerics;

namespace Clay.Engine;

/// <summary>
/// Two-tier numeric representation modeled after libqalculate:
/// exact arbitrary-precision rationals (BigInteger Num / Den) for integer, decimal, and
/// rational arithmetic, falling back to IEEE-754 double for transcendentals or very large values.
/// </summary>
public readonly struct ExactNumber : IEquatable<ExactNumber>
{
    internal const int MaxBits = 4096;
    private const long MaxExactDoubleInt = 1L << 53;

    private readonly bool _isApprox;
    private readonly BigInteger _num;
    private readonly BigInteger _den;
    private readonly double _approx;

    public static readonly ExactNumber Zero = new(BigInteger.Zero, BigInteger.One);
    public static readonly ExactNumber One = new(BigInteger.One, BigInteger.One);

    private ExactNumber(BigInteger num, BigInteger den)
    {
        _isApprox = false;
        _num = num;
        _den = den;
        _approx = 0;
    }

    private ExactNumber(double approx)
    {
        _isApprox = true;
        _num = BigInteger.Zero;
        _den = BigInteger.Zero;
        _approx = approx;
    }

    public bool IsExact => !_isApprox;
    public BigInteger Num => _isApprox ? BigInteger.Zero : _num;
    public BigInteger Den => _isApprox ? BigInteger.Zero : (_den.IsZero ? BigInteger.One : _den);

    public bool IsZero => _isApprox ? _approx == 0 : _num.IsZero;
    public bool IsNaN => _isApprox && double.IsNaN(_approx);
    public bool IsInfinity => _isApprox && double.IsInfinity(_approx);
    public bool IsFinite => !_isApprox || double.IsFinite(_approx);
    public int Sign => _isApprox ? (_approx > 0 ? 1 : _approx < 0 ? -1 : 0) : _num.Sign;

    public static ExactNumber FromBigInteger(BigInteger value) =>
        value.GetBitLength() <= MaxBits ? new ExactNumber(value, BigInteger.One) : new ExactNumber((double)value);

    public static ExactNumber FromLong(long value) => new(new BigInteger(value), BigInteger.One);

    public static ExactNumber FromDouble(double value) => new(value);

    public static ExactNumber FromRational(BigInteger num, BigInteger den)
    {
        if (den.IsZero) throw new EvalException("Division by zero");
        if (num.IsZero) return Zero;
        if (den.Sign < 0)
        {
            num = -num;
            den = -den;
        }

        if (!den.IsOne)
        {
            var gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(num), den);
            if (!gcd.IsOne)
            {
                num /= gcd;
                den /= gcd;
            }
        }

        if (num.GetBitLength() > MaxBits || den.GetBitLength() > MaxBits)
            return new ExactNumber(RatioToDouble(num, den));

        return new ExactNumber(num, den);
    }

    /// <summary>Parses a normalized decimal literal (digits, optional '.', optional e/E exponent) into an ExactNumber.</summary>
    public static bool TryParseDecimal(string text, out ExactNumber result)
    {
        result = Zero;
        if (string.IsNullOrEmpty(text)) return false;

        int expIndex = text.IndexOfAny(['e', 'E']);
        string mantissa = expIndex >= 0 ? text[..expIndex] : text;
        int exp = 0;
        if (expIndex >= 0 && !int.TryParse(text[(expIndex + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out exp))
            return false;

        if (exp is < -100 or > 100)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return false;
            result = FromDouble(d);
            return true;
        }

        int dot = mantissa.IndexOf('.');
        string digits;
        int fracDigits = 0;
        if (dot >= 0)
        {
            digits = mantissa[..dot] + mantissa[(dot + 1)..];
            fracDigits = mantissa.Length - dot - 1;
        }
        else
        {
            digits = mantissa;
        }

        if (digits.Length == 0 || !BigInteger.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
            return false;

        int netExp = exp - fracDigits;
        if (netExp >= 0)
        {
            if (netExp > 100)
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    return false;
                result = FromDouble(d);
                return true;
            }
            result = FromBigInteger(num * BigInteger.Pow(10, netExp));
            return true;
        }

        int denPow = -netExp;
        if (denPow > 100)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return false;
            result = FromDouble(d);
            return true;
        }

        result = FromRational(num, BigInteger.Pow(10, denPow));
        return true;
    }

    public bool TryGetBigInteger(out BigInteger value)
    {
        if (!_isApprox)
        {
            if (Den.IsOne)
            {
                value = _num;
                return true;
            }
            value = BigInteger.Zero;
            return false;
        }

        if (double.IsFinite(_approx) && _approx == Math.Truncate(_approx) && Math.Abs(_approx) <= MaxExactDoubleInt)
        {
            value = new BigInteger(_approx);
            return true;
        }

        value = BigInteger.Zero;
        return false;
    }

    public double ToDouble() => _isApprox ? _approx : RatioToDouble(_num, Den);

    private static double RatioToDouble(BigInteger num, BigInteger den)
    {
        if (num.IsZero) return 0;
        if (den.IsOne) return (double)num;

        var absNum = BigInteger.Abs(num);
        long numBits = absNum.GetBitLength();
        long denBits = den.GetBitLength();
        if (numBits <= 1020 && denBits <= 1020)
            return (double)num / (double)den;

        int numShift = (int)Math.Max(0, numBits - 64);
        int denShift = (int)Math.Max(0, denBits - 64);
        double q = (double)(absNum >> numShift) / (double)(den >> denShift);
        double scaled = Math.ScaleB(q, numShift - denShift);
        return num.Sign < 0 ? -scaled : scaled;
    }

    public static ExactNumber operator -(ExactNumber a) =>
        a._isApprox ? new ExactNumber(-a._approx) : new ExactNumber(-a._num, a.Den);

    public static ExactNumber operator +(ExactNumber a, ExactNumber b) =>
        a.IsExact && b.IsExact
            ? FromRational(a._num * b.Den + b._num * a.Den, a.Den * b.Den)
            : new ExactNumber(a.ToDouble() + b.ToDouble());

    public static ExactNumber operator -(ExactNumber a, ExactNumber b) =>
        a.IsExact && b.IsExact
            ? FromRational(a._num * b.Den - b._num * a.Den, a.Den * b.Den)
            : new ExactNumber(a.ToDouble() - b.ToDouble());

    public static ExactNumber operator *(ExactNumber a, ExactNumber b) =>
        a.IsExact && b.IsExact
            ? FromRational(a._num * b._num, a.Den * b.Den)
            : new ExactNumber(a.ToDouble() * b.ToDouble());

    public static ExactNumber operator /(ExactNumber a, ExactNumber b)
    {
        if (b.IsZero) throw new EvalException("Division by zero");
        return a.IsExact && b.IsExact
            ? FromRational(a._num * b.Den, a.Den * b._num)
            : new ExactNumber(a.ToDouble() / b.ToDouble());
    }

    public static ExactNumber Pow(ExactNumber a, ExactNumber b)
    {
        if (a.IsExact && b.IsExact && b.Den.IsOne && b._num >= -10000 && b._num <= 10000)
        {
            int exp = (int)b._num;
            if (exp == 0) return One;
            BigInteger baseNum = a._num;
            BigInteger baseDen = a.Den;
            if (exp < 0)
            {
                if (baseNum.IsZero) throw new EvalException("Division by zero");
                (baseNum, baseDen) = (baseDen, baseNum);
                exp = -exp;
            }

            if (baseNum.GetBitLength() * exp <= MaxBits && baseDen.GetBitLength() * exp <= MaxBits)
                return FromRational(BigInteger.Pow(baseNum, exp), BigInteger.Pow(baseDen, exp));
        }

        if (b.IsExact && b.Den.IsOne)
        {
            double baseD = a.ToDouble();
            if (baseD == 0 && b._num.Sign < 0) throw new EvalException("Division by zero");
            double mag = Math.Pow(Math.Abs(baseD), b.ToDouble());
            if (double.IsNaN(mag)) throw new EvalException("Not a number");
            if (baseD < 0 && !b._num.IsEven && mag != 0) mag = -mag;
            return new ExactNumber(mag);
        }

        double result = Math.Pow(a.ToDouble(), b.ToDouble());
        if (double.IsNaN(result)) throw new EvalException("Not a number");
        return new ExactNumber(result);
    }

    public static ExactNumber Rem(ExactNumber a, ExactNumber b)
    {
        if (b.IsZero) throw new EvalException("Division by zero");
        if (a.IsExact && b.IsExact)
        {
            var left = a._num * b.Den;
            var right = b._num * a.Den;
            return FromRational(left % right, a.Den * b.Den);
        }
        return new ExactNumber(a.ToDouble() % b.ToDouble());
    }

    public static ExactNumber FloorMod(ExactNumber a, ExactNumber b)
    {
        var r = Rem(a, b);
        if (!r.IsZero && r.Sign != b.Sign)
            r += b;
        return r;
    }

    public static ExactNumber IntDiv(ExactNumber a, ExactNumber b)
    {
        if (b.IsZero) throw new EvalException("Division by zero");
        if (a.IsExact && b.IsExact)
            return FromBigInteger((a._num * b.Den) / (a.Den * b._num));
        return new ExactNumber(Math.Truncate(a.ToDouble() / b.ToDouble()));
    }

    public static ExactNumber BitNot(ExactNumber a) =>
        a.TryGetBigInteger(out var x)
            ? FromBigInteger(~x)
            : throw new EvalException("Bitwise operators require exact whole numbers");

    public static ExactNumber BitAnd(ExactNumber a, ExactNumber b) =>
        a.TryGetBigInteger(out var x) && b.TryGetBigInteger(out var y)
            ? FromBigInteger(x & y)
            : throw new EvalException("Bitwise operators require exact whole numbers");

    public static ExactNumber BitOr(ExactNumber a, ExactNumber b) =>
        a.TryGetBigInteger(out var x) && b.TryGetBigInteger(out var y)
            ? FromBigInteger(x | y)
            : throw new EvalException("Bitwise operators require exact whole numbers");

    public static ExactNumber BitXor(ExactNumber a, ExactNumber b) =>
        a.TryGetBigInteger(out var x) && b.TryGetBigInteger(out var y)
            ? FromBigInteger(x ^ y)
            : throw new EvalException("Bitwise operators require exact whole numbers");

    public static ExactNumber ShiftLeft(ExactNumber a, ExactNumber b)
    {
        if (!a.TryGetBigInteger(out var x) || !b.TryGetBigInteger(out var s))
            throw new EvalException("Bitwise operators require exact whole numbers");
        if (s < 0 || s > MaxBits)
            throw new EvalException("Shift count out of range");
        int shift = (int)s;
        if (!x.IsZero && x.GetBitLength() + shift > MaxBits)
            throw new EvalException("Bitwise result exceeds supported precision");
        return FromBigInteger(x << shift);
    }

    public static ExactNumber ShiftRight(ExactNumber a, ExactNumber b)
    {
        if (!a.TryGetBigInteger(out var x) || !b.TryGetBigInteger(out var s))
            throw new EvalException("Bitwise operators require exact whole numbers");
        if (s < 0 || s > MaxBits)
            throw new EvalException("Shift count out of range");
        return FromBigInteger(x >> (int)s);
    }

    public static ExactNumber Factorial(ExactNumber a)
    {
        if (!a.TryGetBigInteger(out var n) || n < 0 || n > 170)
            throw new EvalException("Factorial needs a whole number from 0 to 170");
        BigInteger r = BigInteger.One;
        for (int i = 2; i <= (int)n; i++) r *= i;
        return FromBigInteger(r);
    }

    public bool Equals(ExactNumber other) =>
        IsExact == other.IsExact && (IsExact
            ? _num == other._num && Den == other.Den
            : _approx.Equals(other._approx));

    public override bool Equals(object? obj) => obj is ExactNumber other && Equals(other);

    public static bool operator ==(ExactNumber a, ExactNumber b) => a.Equals(b);
    public static bool operator !=(ExactNumber a, ExactNumber b) => !a.Equals(b);
    public override int GetHashCode() =>
        HashCode.Combine(IsExact, IsExact ? HashCode.Combine(_num, Den) : _approx.GetHashCode());
}
