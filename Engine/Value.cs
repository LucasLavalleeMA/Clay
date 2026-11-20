using System.Globalization;
using System.Numerics;

namespace Clay.Engine;

/// <summary>
/// A number with an optional unit (Number holds 20 for "20%" when IsPercent),
/// or a list of values when Items is set.
/// </summary>
public sealed class Value
{
    private readonly double _number;
    private readonly ExactNumber _exact;

    public IReadOnlyList<Value>? Items { get; }
    public bool IsArray => Items != null;

    public string? StringValue { get; }
    public bool IsString => StringValue != null;

    public CalcDate? DateValue { get; }
    public bool IsDate => DateValue.HasValue;

    public ComplexInterval? ComplexValue { get; }
    public bool IsComplex => ComplexValue.HasValue;

    public SymExpr? SymValue { get; }
    public bool IsSymbolic => SymValue != null;

    public double Number =>
        Items != null ? throw new EvalException("Expected a single number, not a list") :
        StringValue != null ? throw new EvalException("Expected a number, not text") :
        DateValue.HasValue ? throw new EvalException("Expected a number, not a date") :
        SymValue != null ? throw new EvalException("Expected a number, not a symbolic expression") :
        ComplexValue.HasValue ? (
            ComplexValue.Value.IsComplex ? throw new EvalException("Expected a real number") :
            ComplexValue.Value.HasError ? throw new EvalException("Expected an exact real number, not an interval") :
            ComplexValue.Value.Re) :
        _number;

    public ExactNumber Exact =>
        Items != null ? throw new EvalException("Expected a single number, not a list") :
        StringValue != null ? throw new EvalException("Expected a number, not text") :
        DateValue.HasValue ? throw new EvalException("Expected a number, not a date") :
        SymValue != null ? throw new EvalException("Expected a number, not a symbolic expression") :
        ComplexValue.HasValue ? (
            ComplexValue.Value.IsComplex ? throw new EvalException("Expected a real number") :
            ComplexValue.Value.HasError ? throw new EvalException("Expected an exact real number, not an interval") :
            ExactNumber.FromDouble(ComplexValue.Value.Re)) :
        _exact;

    public Unit? Unit { get; }
    public bool IsPercent { get; }

    /// <summary>Overrides the display (hex / binary / octal results).</summary>
    public string? Text { get; init; }

    public Value(double number, Unit? unit = null, bool isPercent = false)
    {
        _number = number;
        _exact = ExactNumber.FromDouble(number);
        Unit = unit;
        IsPercent = isPercent;
    }

    public Value(ExactNumber exact, Unit? unit = null, bool isPercent = false)
    {
        _exact = exact;
        _number = exact.ToDouble();
        Unit = unit;
        IsPercent = isPercent;
    }

    private Value(IReadOnlyList<Value> items) => Items = items;
    private Value(string text) => StringValue = text;
    private Value(CalcDate date) => DateValue = date;
    private Value(ComplexInterval complex) => ComplexValue = complex;
    private Value(SymExpr sym, bool isPercent = false) { SymValue = sym; IsPercent = isPercent; }

    public static Value List(IEnumerable<Value> items) => new(items.ToList());
    public static Value Str(string text) => new(text);
    public static Value Date(CalcDate date) => new(date);
    public static Value Complex(ComplexInterval complex) => new(complex);
    public static Value Sym(SymExpr sym) => new(sym);
    public static Value FromSym(SymExpr sym) =>
        sym is SymExpr.Num n ? new Value(n.Value) : new Value(sym);
    public static Value Pct(SymExpr sym) => new(sym, isPercent: true);

    public SymExpr ToSym()
    {
        if (Unit != null) throw new EvalException("Symbolic expressions do not support units");
        return SymValue != null
            ? (IsPercent ? Symbolic.Div(SymValue, Symbolic.FromLong(100), Ops.AssumePositive) : SymValue)
            : Symbolic.FromExact(ExactFrac);
    }

    public ComplexInterval ToComplexInterval()
    {
        if (Unit != null) throw new EvalException("Complex numbers do not support units");
        return ComplexValue ?? ComplexInterval.FromReal(Frac);
    }

    public static Value Pct(double number) => new(number, null, true);
    public static Value Pct(ExactNumber number) => new(number, null, true);

    /// <summary>The number as a plain quantity (20% becomes 0.2).</summary>
    public double Frac => IsPercent ? Number / 100 : Number;

    /// <summary>The exact number as a plain quantity (20% becomes 1/5).</summary>
    public ExactNumber ExactFrac => IsPercent ? Exact / ExactNumber.FromLong(100) : Exact;

}

public static class Ops
{
    // ------------------------------------------------------------ list helpers

    public static IEnumerable<Value> Flatten(Value v) =>
        v.Items == null ? [v] : v.Items.SelectMany(Flatten);

    /// <summary>Applies f to every scalar inside v, keeping the list structure.</summary>
    public static Value Map(Value v, Func<Value, Value> f) =>
        v.Items == null ? f(v) : Value.List(v.Items.Select(i => Map(i, f)));

    /// <summary>Applies f pairwise; a scalar is paired with every item of a list.</summary>
    public static Value Broadcast(Value a, Value b, Func<Value, Value, Value> f)
    {
        if (a.Items != null && b.Items != null)
        {
            if (a.Items.Count != b.Items.Count)
                throw new EvalException($"Lists differ in length ({a.Items.Count} vs {b.Items.Count})");
            return Value.List(a.Items.Zip(b.Items, (x, y) => Broadcast(x, y, f)));
        }
        if (a.Items != null) return Value.List(a.Items.Select(x => Broadcast(x, b, f)));
        if (b.Items != null) return Value.List(b.Items.Select(y => Broadcast(a, y, f)));
        var result = f(a, b);
        if (!result.IsArray && !result.IsString && !result.IsDate && !result.IsComplex && !result.IsSymbolic && double.IsNaN(result.Number)) throw new EvalException("Not a number");
        return result;
    }

    // ------------------------------------------------------------------ units

    /// <summary>Expresses v in the target unit's terms. Unitless values are taken as already in it.</summary>
    public static double In(Value v, Unit target)
    {
        if (v.Unit == null) return v.Frac;
        if (v.Unit.SameAs(target)) return v.Number;
        if (v.Unit.Dimension != target.Dimension)
            throw new EvalException($"Can't mix {v.Unit.Symbol} and {target.Symbol}");
        return Unit.Convert(v.Number, v.Unit, target);
    }

    public static Value ConvertTo(Value v, Unit target) =>
        Map(v, x =>
        {
            if (x.Unit != null && x.Unit.Offset == 0 && target.Offset == 0 &&
                x.Unit.Dimension != target.Dimension &&
                x.Unit.Dimension.Pow(-1) == target.Dimension)
            {
                if (x.Number == 0) throw new EvalException("Division by zero");
                double result = 1.0 / (x.Number * x.Unit.Factor) / target.Factor;
                if (Lost(result, x.Number, 1))
                {
                    double log = -Math.Log(Math.Abs(x.Number)) - Unit.LogProduct(x.Unit.Leaves) - Unit.LogProduct(target.Leaves);
                    result = Math.CopySign(Math.Exp(log), x.Number);
                }
                return new Value(Checked(result, x.Number, 1), target);
            }
            return new Value(In(x, target), target);
        });

    public static Value ToBaseUnits(Value v) =>
        Map(v, x =>
        {
            if (x.Unit == null) return x;
            var baseUnit = UnitTable.ToBaseUnits(x.Unit, out double scale);
            return new Value(x.Number * scale, baseUnit);
        });

    // ------------------------------------------------------------- arithmetic
    //
    // Numeric range of unit arithmetic (by design):
    // - Unit powers are limited to ±20 and unit sizes to 1e±300 (see UnitTable), so unit factors are finite.
    // - Coefficients are doubles. When an intermediate overflows or underflows, multiply, divide and power
    //   redo the calculation in logarithms; a result that is itself out of double range is an error (Checked).
    // - A zero coefficient gives zero without applying the unit scale, so 0 × huge scale isn't NaN. Zero to
    //   a negative power is division by zero.
    // - Scaling a unit-bearing value by a plain number is range-checked the same way; plain numbers alone
    //   keep plain double behavior (1e200 * 1e200 is ∞).
    // - Addition and "in" conversions keep plain double behavior, where a negligible term rounds to 0.

    [ThreadStatic] private static int _intervalMode;
    internal static int IntervalMode => _intervalMode == 0 ? 1 : _intervalMode;

    [ThreadStatic] private static bool _assumePositive;
    internal static bool AssumePositive => _assumePositive;

    internal static IDisposable UseIntervalMode(int mode)
    {
        int prev = _intervalMode;
        _intervalMode = mode;
        return new ScopeReset(prev);
    }

    internal static IDisposable UseAssumePositive(bool assume)
    {
        bool prev = _assumePositive;
        _assumePositive = assume;
        return new AssumeReset(prev);
    }

    private sealed class ScopeReset(int prev) : IDisposable
    {
        public void Dispose() => _intervalMode = prev;
    }

    private sealed class AssumeReset(bool prev) : IDisposable
    {
        public void Dispose() => _assumePositive = prev;
    }

    public static Value Add(Value a, Value b, int sign) =>
        Broadcast(a, b, (x, y) => AddScalar(x, y, sign));

    public static Value Uncertainty(Value a, Value b) =>
        Broadcast(a, b, (x, y) =>
        {
            var cx = x.ToComplexInterval();
            if (y.Unit != null) throw new EvalException("Uncertainty does not support units");
            return Value.Complex(new ComplexInterval(cx.Re, cx.Im, Math.Abs(y.Frac), cx.ImErr));
        });

    public static Value Mul(Value a, Value b) =>
        a.IsArray && b.IsArray && (LinearAlgebra.IsMatrix(a) || LinearAlgebra.IsMatrix(b))
            ? LinearAlgebra.MatMul(a, b)
            : Broadcast(a, b, MulScalar);
    public static Value Div(Value a, Value b) => Broadcast(a, b, DivScalar);
    public static Value ElementWiseMul(Value a, Value b) =>
        !a.IsArray && !b.IsArray ? Mul(a, b) : LinearAlgebra.ElementWise(a, b, (x, y) => x * y);
    public static Value ElementWiseDiv(Value a, Value b) =>
        !a.IsArray && !b.IsArray ? Div(a, b) : LinearAlgebra.ElementWise(a, b, (x, y) => x / y);
    public static Value ElementWisePow(Value a, Value b) =>
        !a.IsArray && !b.IsArray ? Pow(a, b) : LinearAlgebra.ElementWise(a, b, ExactNumber.Pow);
    public static Value IntDiv(Value a, Value b) => Broadcast(a, b, IntDivScalar);
    public static Value Mod(Value a, Value b) => Broadcast(a, b, ModScalar);
    public static Value FloorMod(Value a, Value b) => Broadcast(a, b, FloorModScalar);
    public static Value Pow(Value a, Value b)
    {
        if (LinearAlgebra.IsMatrix(a) && !b.IsArray && b.Unit == null && b.ExactFrac.TryGetBigInteger(out var exp))
        {
            if (exp < -1000 || exp > 1000)
                throw new EvalException("Matrix exponent must be between -1000 and 1000");
            return LinearAlgebra.MatrixPow(a, (long)exp);
        }
        return Broadcast(a, b, PowScalar);
    }
    public static Value EntryWisePow(Value a, Value b) => Broadcast(a, b, PowScalar);
    public static Value Factorial(Value v) => Map(v, FactorialScalar);
    public static Value BitNot(Value v) => Map(v, x => new Value(ExactNumber.BitNot(RequirePlain(x))));
    public static Value BitAnd(Value a, Value b) => Broadcast(a, b, (x, y) => new Value(ExactNumber.BitAnd(RequirePlain(x), RequirePlain(y))));
    public static Value BitOr(Value a, Value b) => Broadcast(a, b, (x, y) => new Value(ExactNumber.BitOr(RequirePlain(x), RequirePlain(y))));
    public static Value BitXor(Value a, Value b) => Broadcast(a, b, (x, y) => new Value(ExactNumber.BitXor(RequirePlain(x), RequirePlain(y))));
    public static Value ShiftLeft(Value a, Value b) => Broadcast(a, b, (x, y) => new Value(ExactNumber.ShiftLeft(RequirePlain(x), RequirePlain(y))));
    public static Value ShiftRight(Value a, Value b) => Broadcast(a, b, (x, y) => new Value(ExactNumber.ShiftRight(RequirePlain(x), RequirePlain(y))));

    private static ExactNumber RequirePlain(Value v)
    {
        if (v.Unit != null) throw new EvalException("Bitwise operators require plain numbers");
        return v.ExactFrac;
    }

    private static Value AddScalar(Value a, Value b, int sign)
    {
        if (a.DateValue is { } da)
        {
            if (b.DateValue is { } db)
            {
                if (sign > 0) throw new EvalException("Can't add two dates");
                var days = (da.Instant - db.Instant).TotalDays;
                return new Value(days, UnitTable.Lookup("d"));
            }
            if (b.Unit?.Dimension != Dim.Time)
                throw new EvalException("Date arithmetic requires a time duration");
            double seconds = In(b, UnitTable.Lookup("s")!) * sign;
            return Value.Date(da.AddSeconds(seconds));
        }
        if (b.DateValue is { } db2 && sign > 0)
        {
            if (a.Unit?.Dimension != Dim.Time)
                throw new EvalException("Date arithmetic requires a time duration");
            double seconds = In(a, UnitTable.Lookup("s")!);
            return Value.Date(db2.AddSeconds(seconds));
        }

        if (a.IsSymbolic || b.IsSymbolic)
        {
            if (a.IsPercent && b.IsPercent)
            {
                var aPct = a.SymValue ?? Symbolic.FromExact(a.Exact);
                var bPct = b.SymValue ?? Symbolic.FromExact(b.Exact);
                return Value.Pct(Symbolic.Add(aPct, bPct, sign, AssumePositive));
            }
            if (b.IsPercent)
            {
                var pct = b.ToSym();
                var factor = Symbolic.Add(Symbolic.One, pct, sign, AssumePositive);
                return Value.FromSym(Symbolic.Mul(a.ToSym(), factor, AssumePositive));
            }
            return Value.FromSym(Symbolic.Add(a.ToSym(), b.ToSym(), sign, AssumePositive));
        }

        if (a.IsComplex || b.IsComplex)
        {
            if (b.IsPercent && !a.IsPercent)
            {
                var factor = ComplexInterval.Add(ComplexInterval.FromReal(1), b.ToComplexInterval(), sign, IntervalMode);
                return Value.Complex(ComplexInterval.Mul(a.ToComplexInterval(), factor, IntervalMode));
            }
            return Value.Complex(ComplexInterval.Add(a.ToComplexInterval(), b.ToComplexInterval(), sign, IntervalMode));
        }

        if (a.IsPercent && b.IsPercent)
            return Value.Pct(sign > 0 ? a.Exact + b.Exact : a.Exact - b.Exact);
        if (b.IsPercent)
        {
            var factor = sign > 0 ? ExactNumber.One + b.ExactFrac : ExactNumber.One - b.ExactFrac;
            return new Value(a.Exact * factor, a.Unit);
        }

        if (a.Unit == null && b.Unit == null)
            return new Value(sign > 0 ? a.ExactFrac + b.Exact : a.ExactFrac - b.Exact);
        var u = a.Unit ?? b.Unit!;
        if (a.Unit?.Dimension == Dim.Temperature && b.Unit?.Dimension == Dim.Temperature)
            return new Value(a.Number + sign * b.Number * b.Unit.Factor / a.Unit.Factor, a.Unit);
        return new Value(In(a, u) + sign * In(b, u), u);
    }

    private static Value MulScalar(Value a, Value b)
    {
        if (a.IsSymbolic || b.IsSymbolic)
            return Value.FromSym(Symbolic.Mul(a.ToSym(), b.ToSym(), AssumePositive));
        if (a.IsComplex || b.IsComplex)
            return Value.Complex(ComplexInterval.Mul(a.ToComplexInterval(), b.ToComplexInterval(), IntervalMode));
        if (a.Unit == null && b.Unit == null) return new Value(a.ExactFrac * b.ExactFrac);
        if (a.Unit != null && b.Unit != null) return Combine(a, b, 1);
        var unit = a.Unit ?? b.Unit;
        double r = a.Frac * b.Frac;
        return new Value(unit == null ? r : Checked(r, a.Frac, b.Frac), unit);
    }

    private static Value DivScalar(Value a, Value b)
    {
        if (a.IsSymbolic || b.IsSymbolic)
            return Value.FromSym(Symbolic.Div(a.ToSym(), b.ToSym(), AssumePositive));
        if (a.IsComplex || b.IsComplex)
            return Value.Complex(ComplexInterval.Div(a.ToComplexInterval(), b.ToComplexInterval(), IntervalMode));
        if (b.ExactFrac.IsZero) throw new EvalException("Division by zero");
        if (a.Unit == null && b.Unit == null) return new Value(a.ExactFrac / b.ExactFrac);
        if (a.Unit != null && b.Unit != null && a.Unit.Dimension == b.Unit.Dimension)
        {
            if (a.Number == 0 && a.Unit.Offset == 0 && b.Unit.Offset == 0) return new Value(0);
            double r = a.Number / In(b, a.Unit);
            if (Lost(r, a.Number, b.Number) && a.Unit.Offset == 0 && b.Unit.Offset == 0)
                r = Math.Sign(a.Number) * Math.Sign(b.Number) * Math.Exp(Math.Log(Math.Abs(a.Number)) -
                    Math.Log(Math.Abs(b.Number)) - Unit.LogProduct(b.Unit.Leaves) + Unit.LogProduct(a.Unit.Leaves));
            return new Value(Checked(r, a.Number, b.Number));
        }
        if (b.Unit != null) return Combine(a, b, -1);
        double q = a.Frac / b.Frac;
        return new Value(a.Unit == null ? q : Checked(q, a.Frac, b.Frac), a.Unit);
    }

    /// <summary>True when finite, nonzero inputs gave an infinite or zero result.</summary>
    private static bool Lost(double result, double x, double y) =>
        (!double.IsFinite(result) || result == 0) && double.IsFinite(x) && double.IsFinite(y) && x != 0 && y != 0;

    /// <summary>Unit arithmetic reports an error rather than rounding a lost result to ∞ or 0.</summary>
    private static double Checked(double result, double x, double y) =>
        Lost(result, x, y) ? throw new EvalException("That result is too large or too small") : result;

    /// <summary>Multiplies (sign 1) or divides (sign -1) values whose units combine, e.g. m/s × s = m.</summary>
    private static Value Combine(Value a, Value b, int sign)
    {
        var right = b.Unit!;
        if (a.Unit is { Offset: not 0 } || right.Offset != 0)
        {
            var verb = sign > 0 ? "multiply" : "divide";
            throw new EvalException(a.Unit == null
                ? $"Can't {verb} by {right.Symbol}"
                : $"Can't {verb} {a.Unit.Symbol} by {right.Symbol}");
        }

        var terms = a.Unit?.Leaves.ToList() ?? [];
        double scale = 1, logScale = 0;
        foreach (var t in right.Leaves)
        {
            var (factor, power) = Absorb(terms, t.Unit, t.Power * sign);
            scale *= Math.Pow(factor, power);
            logScale += power * Math.Log(factor);
        }

        var unit = UnitTable.Compose(terms, out double named);
        double n = sign > 0 ? a.Frac * b.Number : a.Frac / b.Number;
        if (a.Frac == 0 || b.Number == 0) return new Value(0, unit);
        double result = n * scale * named;

        // Large powers can overflow or underflow on the way to a finite result, so redo those in logarithms
        if (Lost(result, a.Frac, b.Number))
        {
            double logNamed = unit == null ? Unit.LogProduct(UnitTable.Merge(terms)) : Math.Log(named);
            result = Math.Sign(a.Frac) * Math.Sign(b.Number) *
                Math.Exp(Math.Log(Math.Abs(a.Frac)) + sign * Math.Log(Math.Abs(b.Number)) + logScale + logNamed);
        }
        return new Value(Checked(result, a.Frac, b.Number), unit);
    }

    private static readonly int[] Powers = [1, -1, 2, -2, 3, -3];

    /// <summary>
    /// Adds u^p to the terms. A term of a related dimension takes it in its own unit so the two cancel
    /// (the left unit wins): s joins h, s joins Hz as h⁻¹, ha joins m as m². Returns the numeric scale
    /// as a factor and the power to raise it to.
    /// Across different powers (volume and length) this only happens when the power grows or cancels,
    /// so rates such as l/km and km/l stay as written.
    /// </summary>
    private static (double Factor, int Power) Absorb(List<Term> terms, Unit u, int p)
    {
        int same = terms.FindIndex(t => ReferenceEquals(t.Unit, u));
        if (same >= 0)
        {
            terms[same] = terms[same] with { Power = terms[same].Power + p };
            return (1, 0);
        }

        static bool Joins(int k, int existing, int added) =>
            Math.Abs(k) == 1 || existing + added == 0 || Math.Sign(existing) == Math.Sign(added);

        // u is a power of an existing term: express u^p as t^(k·p)
        foreach (int k in Powers)
        {
            int i = terms.FindIndex(t => t.Unit.Dimension.Pow(k) == u.Dimension && Joins(k, t.Power, k * p));
            if (i < 0) continue;
            var t = terms[i].Unit;
            terms[i] = terms[i] with { Power = terms[i].Power + k * p };
            return (u.Factor / Math.Pow(t.Factor, k), p);
        }

        // An existing term is a power of u (ha against m): rewrite that term in u
        foreach (int k in Powers.Skip(2))
        {
            int i = terms.FindIndex(t => u.Dimension.Pow(k) == t.Unit.Dimension && Joins(k, k * t.Power, p));
            if (i < 0) continue;
            var t = terms[i];
            terms[i] = new Term(u, k * t.Power + p);
            return (t.Unit.Factor / Math.Pow(u.Factor, k), t.Power);
        }

        terms.Add(new Term(u, p));
        return (1, 0);
    }

    private static Value ModScalar(Value a, Value b)
    {
        if (a.Unit == null && b.Unit == null) return new Value(ExactNumber.Rem(a.ExactFrac, b.ExactFrac));
        if (b.Unit != null && a.Unit == null) throw new EvalException("Can't divide by a unit");
        double divisor = a.Unit != null ? In(b, a.Unit) : b.Frac;
        if (divisor == 0) throw new EvalException("Division by zero");
        return new Value(a.Frac % divisor, a.Unit);
    }

    private static Value FloorModScalar(Value a, Value b)
    {
        if (a.Unit == null && b.Unit == null) return new Value(ExactNumber.FloorMod(a.ExactFrac, b.ExactFrac));
        if (b.Unit != null && a.Unit == null) throw new EvalException("Can't divide by a unit");
        double divisor = a.Unit != null ? In(b, a.Unit) : b.Frac;
        if (divisor == 0) throw new EvalException("Division by zero");
        double r = a.Frac % divisor;
        if (r != 0 && (r < 0) != (divisor < 0))
            r += divisor;
        return new Value(r, a.Unit);
    }

    private static Value IntDivScalar(Value a, Value b)
    {
        if (a.Unit == null && b.Unit == null) return new Value(ExactNumber.IntDiv(a.ExactFrac, b.ExactFrac));
        var q = DivScalar(a, b);
        return new Value(Math.Truncate(q.Number), q.Unit);
    }

    private static Value PowScalar(Value a, Value b)
    {
        if (a.IsSymbolic || b.IsSymbolic)
            return Value.FromSym(Symbolic.Pow(a.ToSym(), b.ToSym(), AssumePositive));
        if (a.IsComplex || b.IsComplex)
            return Value.Complex(ComplexInterval.Pow(a.ToComplexInterval(), b.ToComplexInterval(), IntervalMode));
        if (b.Unit != null) throw new EvalException("An exponent can't have a unit");
        if (a.Unit == null) return new Value(ExactNumber.Pow(a.ExactFrac, b.ExactFrac));
        double e = b.Frac;
        if (e != 1) return UnitPow(a, e, x => Math.Pow(x, e));
        double result = Math.Pow(a.Frac, e);
        if (double.IsNaN(result)) throw new EvalException("Not a number");
        return new Value(result, a.Unit);
    }

    /// <summary>The nth root of a value with a unit: sqrt(9 m²) is 3 m.</summary>
    public static Value Root(Value v, int n, Func<double, double> root) =>
        Map(v, x => x.Unit == null ? new Value(root(x.Frac)) : UnitPow(x, 1.0 / n, root));

    private static bool IsWhole(double p) => Math.Abs(p - Math.Round(p)) <= 1e-9;

    private static Value UnitPow(Value a, double e, Func<double, double> f)
    {
        var u = a.Unit!;
        if (u.Offset != 0) throw new EvalException($"Powers of {u.Symbol} aren't supported");
        if (u.Leaves.Any(t => Math.Abs(t.Power * e) > UnitTable.MaxPower)) throw UnitTable.PowerTooLarge();

        List<Term> terms;
        double logExtra = 0;
        if (u.Leaves.All(t => IsWhole(t.Power * e)))
            terms = u.Leaves.Select(t => t with { Power = (int)Math.Round(t.Power * e) }).ToList();
        else
            terms = CoherentPow(u, e, out logExtra);

        var unit = UnitTable.Compose(terms, out double scale);
        double fx = f(a.Number);
        if (double.IsNaN(fx)) throw new EvalException("Not a number");
        if (a.Number == 0)
        {
            if (e < 0) throw new EvalException("Division by zero");
            return new Value(fx, unit);
        }
        double result = fx * scale * Math.Exp(logExtra);
        if (Lost(result, a.Number, 1))
            result = Math.CopySign(Math.Exp(e * Math.Log(Math.Abs(a.Number)) + Math.Log(scale) + logExtra), fx);
        if (double.IsNaN(result)) throw new EvalException("Not a number");
        return new Value(Checked(result, a.Number, 1), unit);
    }

    /// <summary>
    /// Raises a compound to a power through coherent base units, so exponents that cancel across leaves
    /// work: sqrt(1 ha) is 100 m, and sqrt(100 J/kg) is 10 m/s. <paramref name="logFactor"/> is the
    /// conversion, as a logarithm so large powers can't overflow before the coefficient is applied.
    /// </summary>
    private static List<Term> CoherentPow(Unit u, double e, out double logFactor)
    {
        var exponents = new List<(Unit Unit, double Power)>();
        logFactor = 0;
        foreach (var t in u.Leaves)
        {
            double p = t.Power * e;
            if (UnitTable.Coherent(t.Unit) is not { } coherent)
            {
                exponents.Add((t.Unit, p));
                continue;
            }
            foreach (var c in coherent) exponents.Add((c.Unit, c.Power * p));
            logFactor += p * Math.Log(t.Unit.Factor);
        }

        var terms = new List<Term>();
        foreach (var group in exponents.GroupBy(x => x.Unit))
        {
            double p = group.Sum(x => x.Power);
            if (!IsWhole(p)) throw new EvalException($"Can't raise {u.Symbol} to that power");
            if (Math.Abs(p) > UnitTable.MaxPower) throw UnitTable.PowerTooLarge();
            if (Math.Round(p) != 0) terms.Add(new Term(group.Key, (int)Math.Round(p)));
        }
        return terms;
    }

    private static Value FactorialScalar(Value v)
    {
        if (v.Unit != null) throw new EvalException("Factorial needs a plain whole number");
        return new Value(ExactNumber.Factorial(v.ExactFrac));
    }
}

public static class Formatter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Format(Value v, int decimals, bool group)
    {
        if (v.Items != null)
            return "[" + string.Join(", ", v.Items.Select(i => Format(i, decimals, group))) + "]";

        if (v.StringValue != null)
        {
            char q = v.StringValue.EnumerateRunes().Count() == 1 ? '\'' : '"';
            var escaped = v.StringValue.Replace("\\", "\\\\").Replace(q.ToString(), "\\" + q);
            return $"{q}{escaped}{q}";
        }

        if (v.DateValue is { } date)
            return date.Format();

        if (v.ComplexValue is { } cx)
            return cx.Format(decimals, group);

        if (v.SymValue is { } sym)
            return v.IsPercent ? Symbolic.Format(sym, decimals) + "%" : Symbolic.Format(sym, decimals);

        if (v.Text != null) return v.Text;
        if (v.IsPercent) return FormatExact(v.Exact, decimals, group) + "%";

        var u = v.Unit;
        if (u == null) return FormatExact(v.Exact, decimals, group);

        if (u.IsCurrency)
        {
            double rounded = Math.Round(Math.Abs(v.Number), u.Decimals, MidpointRounding.AwayFromZero);
            string body = rounded.ToString(Pattern(u.Decimals, group, fixedDecimals: true), Inv);
            string sign = v.Number < 0 && rounded > 0 ? "-" : "";
            return u.Prefix != null ? sign + u.Prefix + body : sign + body + " " + u.Symbol;
        }

        string n = FormatExact(v.Exact, decimals, group);

        // Currency rates such as $15/h keep the currency's prefix
        var money = u.Leaves.Where(t => t.Unit.IsCurrency).ToList();
        if (!u.IsLeaf && money is [{ Power: 1, Unit.Prefix: { } prefix } m] && u.Symbol.StartsWith(m.Unit.Symbol, StringComparison.Ordinal))
        {
            string body = Number(Math.Abs(v.Number), decimals, group);
            string sign = v.Number < 0 && body != "0" ? "-" : "";
            return sign + prefix + body + u.Symbol[m.Unit.Symbol.Length..];
        }

        return u.Symbol == "°" ? n + "°" : n + " " + u.Symbol;
    }

    private static string FormatExact(ExactNumber x, int decimals, bool group)
    {
        double d = x.ToDouble();
        if (!x.IsExact || x.IsZero || (double.IsFinite(d) && d != 0))
            return Number(d, decimals, group);

        bool neg = x.Num.Sign < 0;
        var absNum = BigInteger.Abs(x.Num);
        var den = x.Den;
        int exp10 = (int)Math.Floor(BigInteger.Log10(absNum) - BigInteger.Log10(den));

        static BigInteger ScaleAndRound(BigInteger n, BigInteger d, int shift)
        {
            BigInteger sNum = shift >= 0 ? n * BigInteger.Pow(10, shift) : n;
            BigInteger sDen = shift >= 0 ? d : d * BigInteger.Pow(10, -shift);
            var (q, r) = BigInteger.DivRem(sNum, sDen);
            if (r * 2 >= sDen) q += 1;
            return q;
        }

        var scaled = ScaleAndRound(absNum, den, 6 - exp10);
        if (scaled < 1_000_000)
        {
            exp10--;
            scaled = ScaleAndRound(absNum, den, 6 - exp10);
        }
        while (scaled >= 10_000_000)
        {
            scaled = (scaled + 5) / 10;
            exp10++;
        }

        string digits = scaled.ToString(Inv);
        string frac = digits.Length > 1 ? digits[1..].TrimEnd('0') : "";
        string mantissa = frac.Length > 0 ? $"{digits[0]}.{frac}" : digits[0].ToString();
        string sign = neg ? "-" : "";
        string expSign = exp10 >= 0 ? "+" : "";
        return $"{sign}{mantissa}E{expSign}{exp10}";
    }

    public static string Number(double x, int decimals, bool group)
    {
        if (double.IsNaN(x)) return "NaN";
        if (double.IsInfinity(x)) return x > 0 ? "∞" : "-∞";
        if (x == 0) return "0";

        double r = Math.Round(x, Math.Min(decimals, 15), MidpointRounding.AwayFromZero);
        if (r == 0) return "0";

        double abs = Math.Abs(x);
        if (abs >= 1e15 || abs < 1e-6) return x.ToString("0.######E+0", Inv);

        return r.ToString(Pattern(decimals, group, fixedDecimals: false), Inv);
    }

    private static string Pattern(int decimals, bool group, bool fixedDecimals)
    {
        string whole = group ? "#,##0" : "0";
        if (decimals <= 0) return whole;
        return whole + "." + new string(fixedDecimals ? '0' : '#', decimals);
    }
}
