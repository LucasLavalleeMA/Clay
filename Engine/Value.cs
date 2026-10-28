using System.Globalization;

namespace Clay.Engine;

/// <summary>
/// A number with an optional unit (Number holds 20 for "20%" when IsPercent),
/// or a list of values when Items is set.
/// </summary>
public sealed class Value
{
    private readonly double _number;

    public IReadOnlyList<Value>? Items { get; }
    public bool IsArray => Items != null;

    public double Number =>
        Items == null ? _number : throw new EvalException("Expected a single number, not a list");

    public Unit? Unit { get; }
    public bool IsPercent { get; }

    /// <summary>Overrides the display (hex / binary / octal results).</summary>
    public string? Text { get; init; }

    public Value(double number, Unit? unit = null, bool isPercent = false)
    {
        _number = number;
        Unit = unit;
        IsPercent = isPercent;
    }

    private Value(IReadOnlyList<Value> items) => Items = items;

    public static Value List(IEnumerable<Value> items) => new(items.ToList());

    public static Value Pct(double number) => new(number, null, true);

    /// <summary>The number as a plain quantity (20% becomes 0.2).</summary>
    public double Frac => IsPercent ? Number / 100 : Number;

    /// <summary>True when the value, or any item inside it, carries a unit.</summary>
    public bool HasUnit => Items != null ? Items.Any(i => i.HasUnit) : Unit != null;
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
        return f(a, b);
    }

    /// <summary>Zero-based indexing; negative indexes count from the end.</summary>
    public static Value Index(Value list, Value index)
    {
        if (index.Items != null) throw new EvalException("Index must be a single number");
        double d = index.Frac;
        if (d != Math.Floor(d)) throw new EvalException("Index must be a whole number");

        int n = list.Items!.Count;
        if (Math.Abs(d) > n) throw new EvalException($"Index {d} is out of range (list has {n} items)");

        int i = (int)d;
        if (i < 0) i += n;
        if (i < 0 || i >= n) throw new EvalException($"Index {d} is out of range (list has {n} items)");
        return list.Items[i];
    }

    // ------------------------------------------------------------------ units

    /// <summary>Expresses v in the target unit's terms. Unitless values are taken as already in it.</summary>
    public static double In(Value v, Unit target)
    {
        if (v.Unit == null) return v.Frac;
        if (ReferenceEquals(v.Unit, target)) return v.Number;
        if (v.Unit.Dimension != target.Dimension)
            throw new EvalException($"Can't mix {v.Unit.Symbol} and {target.Symbol}");
        return Unit.Convert(v.Number, v.Unit, target);
    }

    public static Value ConvertTo(Value v, Unit target) =>
        Map(v, x => new Value(In(x, target), target));

    /// <summary>Gives unitless numbers (inside lists too) the unit; values that already have one are kept.</summary>
    public static Value AttachUnit(Value v, Unit unit) =>
        Map(v, x => x.Unit == null ? new Value(x.Frac, unit) : x);

    // ------------------------------------------------------------- arithmetic

    public static Value Add(Value a, Value b, int sign) =>
        Broadcast(a, b, (x, y) => AddScalar(x, y, sign));

    public static Value Mul(Value a, Value b) => Broadcast(a, b, MulScalar);
    public static Value Div(Value a, Value b) => Broadcast(a, b, DivScalar);
    public static Value Mod(Value a, Value b) => Broadcast(a, b, ModScalar);
    public static Value Pow(Value a, Value b) => Broadcast(a, b, PowScalar);
    public static Value Factorial(Value v) => Map(v, FactorialScalar);

    private static Value AddScalar(Value a, Value b, int sign)
    {
        if (a.IsPercent && b.IsPercent) return Value.Pct(a.Number + sign * b.Number);
        if (b.IsPercent) return new Value(a.Number * (1 + sign * b.Number / 100), a.Unit);

        if (a.Unit == null && b.Unit == null) return new Value(a.Frac + sign * b.Number);
        var u = a.Unit ?? b.Unit!;
        return new Value(In(a, u) + sign * In(b, u), u);
    }

    private static Value MulScalar(Value a, Value b)
    {
        if (a.Unit != null && b.Unit != null) throw new EvalException("Can't multiply two units");
        return new Value(a.Frac * b.Frac, a.Unit ?? b.Unit);
    }

    private static Value DivScalar(Value a, Value b)
    {
        if (b.Frac == 0) throw new EvalException("Division by zero");
        if (a.Unit != null && b.Unit != null)
        {
            if (a.Unit.Dimension != b.Unit.Dimension)
                throw new EvalException($"Can't divide {a.Unit.Symbol} by {b.Unit.Symbol}");
            return new Value(a.Number / In(b, a.Unit));
        }
        if (b.Unit != null) throw new EvalException("Can't divide by a unit");
        return new Value(a.Frac / b.Frac, a.Unit);
    }

    private static Value ModScalar(Value a, Value b)
    {
        if (b.Frac == 0) throw new EvalException("Division by zero");
        return new Value(a.Frac % b.Frac, a.Unit);
    }

    private static Value PowScalar(Value a, Value b)
    {
        double e = b.Frac;
        if (a.Unit != null && e != 1) throw new EvalException("Powers of units aren't supported");
        return new Value(Math.Pow(a.Frac, e), a.Unit);
    }

    private static Value FactorialScalar(Value v)
    {
        double n = v.Frac;
        if (n < 0 || n != Math.Floor(n) || n > 170)
            throw new EvalException("Factorial needs a whole number from 0 to 170");
        double r = 1;
        for (int i = 2; i <= n; i++) r *= i;
        return new Value(r);
    }
}

public static class Formatter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Format(Value v, int decimals, bool group)
    {
        if (v.Items != null)
            return "[" + string.Join(", ", v.Items.Select(i => Format(i, decimals, group))) + "]";

        if (v.Text != null) return v.Text;
        if (v.IsPercent) return Number(v.Number, decimals, group) + "%";

        var u = v.Unit;
        if (u == null) return Number(v.Number, decimals, group);

        if (u.IsCurrency)
        {
            double rounded = Math.Round(Math.Abs(v.Number), u.Decimals, MidpointRounding.AwayFromZero);
            string body = rounded.ToString(Pattern(u.Decimals, group, fixedDecimals: true), Inv);
            string sign = v.Number < 0 && rounded > 0 ? "-" : "";
            return u.Prefix != null ? sign + u.Prefix + body : sign + body + " " + u.Symbol;
        }

        string n = Number(v.Number, decimals, group);
        return u.Symbol == "°" ? n + "°" : n + " " + u.Symbol;
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
