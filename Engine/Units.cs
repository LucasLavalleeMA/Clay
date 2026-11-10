using System.Text;

namespace Clay.Engine;

/// <summary>
/// Exponents of the base dimensions: length, mass, time, temperature, data, angle, currency and
/// electric current. Speed is length / time, energy is mass · length² / time², and so on.
/// </summary>
public readonly record struct Dim(int L, int M, int T, int K, int D, int A, int C, int I)
{
    public static readonly Dim None = default;
    public static readonly Dim Length = new(1, 0, 0, 0, 0, 0, 0, 0);
    public static readonly Dim Mass = new(0, 1, 0, 0, 0, 0, 0, 0);
    public static readonly Dim Time = new(0, 0, 1, 0, 0, 0, 0, 0);
    public static readonly Dim Temperature = new(0, 0, 0, 1, 0, 0, 0, 0);
    public static readonly Dim Data = new(0, 0, 0, 0, 1, 0, 0, 0);
    public static readonly Dim Angle = new(0, 0, 0, 0, 0, 1, 0, 0);
    public static readonly Dim Currency = new(0, 0, 0, 0, 0, 0, 1, 0);
    public static readonly Dim Current = new(0, 0, 0, 0, 0, 0, 0, 1);

    public static readonly Dim Area = Length.Pow(2);
    public static readonly Dim Volume = Length.Pow(3);
    public static readonly Dim Speed = Length / Time;
    public static readonly Dim Force = Mass * Length / Time.Pow(2);
    public static readonly Dim Energy = Force * Length;
    public static readonly Dim Power = Energy / Time;
    public static readonly Dim Pressure = Force / Area;
    public static readonly Dim Frequency = None / Time;
    public static readonly Dim Voltage = Power / Current;
    public static readonly Dim Resistance = Voltage / Current;

    public static Dim operator *(Dim a, Dim b) =>
        new(a.L + b.L, a.M + b.M, a.T + b.T, a.K + b.K, a.D + b.D, a.A + b.A, a.C + b.C, a.I + b.I);

    public static Dim operator /(Dim a, Dim b) => a * b.Pow(-1);

    public Dim Pow(int n) => new(L * n, M * n, T * n, K * n, D * n, A * n, C * n, I * n);
}

/// <summary>A unit raised to a power, such as s⁻¹ in m/s.</summary>
public readonly record struct Term(Unit Unit, int Power);

/// <summary>
/// A leaf unit with its own factor (m, h, kg), a named unit defined from other units (km/h, m², kWh),
/// or a compound produced by arithmetic (m/s²).
/// </summary>
public sealed class Unit
{
    private double _factor;
    private readonly IReadOnlyList<Term>? _leaves;

    public string Symbol { get; }
    public Dim Dimension { get; }

    /// <summary>Multiplier to the coherent base (for currencies: 1 / units-per-USD).</summary>
    public double Factor
    {
        get => _leaves == null ? _factor : Product(_leaves);
        internal set => _factor = value;
    }

    /// <summary>Offset in base units (absolute temperatures only).</summary>
    public double Offset { get; }

    /// <summary>Optional symbol shown before the number (e.g. "$").</summary>
    public string? Prefix { get; init; }

    /// <summary>Decimals used when displaying currencies.</summary>
    public int Decimals { get; init; } = 2;

    /// <summary>Only available when physics units are enabled.</summary>
    public bool Physics { get; init; }

    public bool IsLeaf => _leaves == null;
    public bool IsCurrency => IsLeaf && Dimension == Dim.Currency;

    /// <summary>The unit expressed as leaf units with powers, e.g. km/h is km · h⁻¹.</summary>
    public IReadOnlyList<Term> Leaves => _leaves ?? [new Term(this, 1)];

    public Unit(string symbol, Dim dimension, double factor, double offset = 0)
    {
        Symbol = symbol;
        Dimension = dimension;
        _factor = factor;
        Offset = offset;
    }

    internal Unit(string symbol, IEnumerable<Term> definition)
    {
        Symbol = symbol;
        _leaves = UnitTable.Merge(definition.SelectMany(t =>
            t.Unit.Leaves.Select(l => l with { Power = l.Power * t.Power })));
        Dimension = _leaves.Aggregate(Dim.None, (d, t) => d * t.Unit.Dimension.Pow(t.Power));
    }

    // Large and small factors can overflow or underflow partway through, depending on term order
    internal static double Product(IEnumerable<Term> terms)
    {
        double direct = terms.Aggregate(1.0, (f, t) => f * Math.Pow(t.Unit.Factor, t.Power));
        return double.IsFinite(direct) && direct != 0 ? direct : Math.Exp(LogProduct(terms));
    }

    internal static double LogProduct(IEnumerable<Term> terms) =>
        terms.Sum(t => t.Power * Math.Log(t.Unit.Factor));

    /// <summary>True when both units have the same leaves, such as km/h and km·h⁻¹.</summary>
    public bool SameAs(Unit other) =>
        ReferenceEquals(this, other) || UnitTable.Key(Leaves) == UnitTable.Key(other.Leaves);

    public static double Convert(double x, Unit from, Unit to)
    {
        if (from.Offset != 0 || to.Offset != 0) return (x * from.Factor + from.Offset - to.Offset) / to.Factor;

        double f = from.Factor, t = to.Factor;
        if (double.IsFinite(f) && double.IsFinite(t) && f != 0 && t != 0)
        {
            double direct = x * f / t;
            if (double.IsFinite(direct) && (direct != 0 || x == 0)) return direct;
        }
        if (x == 0 || !double.IsFinite(x)) return x;

        // Powered units such as PB²⁰ can overflow or underflow, so convert through logarithms
        var terms = UnitTable.Merge(from.Leaves.Concat(to.Leaves.Select(l => l with { Power = -l.Power })));
        return Math.Sign(x) * Math.Exp(Math.Log(Math.Abs(x)) + LogProduct(terms));
    }
}

public static class UnitTable
{
    private static readonly Dictionary<string, Unit> Exact = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Unit> Loose = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Unit> PhysicsExact = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Unit> PhysicsLoose = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Unit> Fiat = new(StringComparer.OrdinalIgnoreCase);

    // Leaf combinations that have a name: "km^1|h^-1" is km/h, "J^1|s^-1" is W.
    private static readonly Dictionary<string, Unit> Named = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Unit> PhysicsNamed = new(StringComparer.Ordinal);

    [ThreadStatic] private static bool _physics;

    /// <summary>
    /// The largest power a unit term can have (m²⁰). Unit factors run from 1e-6 to 1e15, so this keeps
    /// any single powered unit within about 1e±300.
    /// </summary>
    public const int MaxPower = 20;

    /// <summary>Whether physics units (N, Hz, A, V, Ω, ...) are available on this thread.</summary>
    public static bool PhysicsEnabled => _physics;

    /// <summary>Enables or disables physics units on this thread until the result is disposed.</summary>
    public static IDisposable UsePhysics(bool enabled)
    {
        var restore = new PhysicsScope(_physics);
        _physics = enabled;
        return restore;
    }

    private sealed class PhysicsScope(bool previous) : IDisposable
    {
        public void Dispose() => _physics = previous;
    }

    /// <summary>
    /// True when physics units are on and the name is exactly an enabled physics symbol (V, A, N), or one
    /// raised to a power (V², V⁰).
    /// </summary>
    public static bool IsPhysicsSymbol(string name)
    {
        if (!_physics) return false;
        int split = name.Length;
        while (split > 0 && Superscripts.Contains(name[split - 1])) split--;
        return PhysicsExact.ContainsKey(name[..split]) && (split == name.Length || IsFactor(name));
    }

    public static Unit? Lookup(string name)
    {
        if (Exact.TryGetValue(name, out var u)) return u;
        if (_physics && PhysicsExact.TryGetValue(name, out u)) return u;
        if (Loose.TryGetValue(name, out u)) return u;
        if (_physics && PhysicsLoose.TryGetValue(name, out u)) return u;
        return null;
    }

    /// <summary>A unit name or a unit expression such as m/s², kg·m/s² or N·m.</summary>
    public static Unit? Parse(string text)
    {
        var direct = Lookup(text);
        if (direct != null) return direct;
        if (!TryLeaves(text, out var leaves)) return null;

        // A dimensionless ratio such as m/in, or one that partly cancels such as m/in/s, is left to the
        // arithmetic so the conversion factor is applied
        if (leaves.Any(a => a.Power > 0 && leaves.Any(b => b.Power < 0 && a.Unit.Dimension == b.Unit.Dimension)))
            return null;

        return Compose(leaves, out double scale) is { } result && scale == 1 ? result : null;
    }

    /// <summary>Whether the text is a unit raised to the power 0 (m⁰), which stands for the plain number 1.</summary>
    public static bool IsUnitless(string text) =>
        Lookup(text) == null && TryLeaves(text, out var leaves) && leaves.Count == 0;

    private static bool TryLeaves(string text, out List<Term> leaves)
    {
        leaves = [];
        if (text.Length == 0 || text.Equals("in", StringComparison.OrdinalIgnoreCase)) return false;

        var terms = new List<Term>();

        // Read left to right: / divides the next factor, · multiplies it (m/s·s is m)
        int start = 0, sign = 1;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '/' && text[i] != '·') continue;

            if (!TryFactor(text[start..i], out var unit, out int power) || unit.Offset != 0) return false;
            terms.Add(new Term(unit, sign * power));
            if (i < text.Length) sign = text[i] == '/' ? -1 : 1;
            start = i + 1;
        }
        leaves = Merge(terms.SelectMany(t => t.Unit.Leaves.Select(l => l with { Power = l.Power * t.Power })));
        return leaves.All(t => InRange(t.Power)) && SizeInRange(leaves);
    }

    /// <summary>Whether the text is a single unit factor such as m, s², or in.</summary>
    public static bool IsFactor(string text) => TryFactor(text, out _, out _);

    // "in" isn't in the general lookup because it's also the conversion keyword
    private static Unit? FactorUnit(string text) =>
        Lookup(text) ?? (text.Equals("in", StringComparison.OrdinalIgnoreCase) ? Inch : null);

    private static bool TryFactor(string text, out Unit unit, out int power)
    {
        power = 1;
        unit = FactorUnit(text)!;
        if (unit != null) return true;

        int split = text.Length;
        while (split > 0 && Superscripts.Contains(text[split - 1])) split--;
        if (split == 0 || split == text.Length) return false;

        var digits = new StringBuilder();
        foreach (char c in text[split..]) digits.Append(c == '⁻' ? '-' : (char)('0' + Superscripts.IndexOf(c)));
        unit = FactorUnit(text[..split])!;
        return unit != null && int.TryParse(digits.ToString(), out power) && InRange(power);
    }

    /// <summary>A leaf unit as coherent base units: ha is m², l is m³. Null for currencies and offset units.</summary>
    public static IReadOnlyList<Term>? Coherent(Unit leaf)
    {
        var d = leaf.Dimension;
        if (d.C != 0 || leaf.Offset != 0) return null;

        var terms = new List<Term>();
        void Add(int power, string symbol) { if (power != 0) terms.Add(new Term(Get(symbol), power)); }
        Add(d.L, "m"); Add(d.M, "kg"); Add(d.T, "s"); Add(d.K, "K"); Add(d.D, "B"); Add(d.A, "rad"); Add(d.I, "A");
        return terms;
    }

    /// <summary>Raises a unit to an integer power; scale is non-1 only for named results.</summary>
    public static Unit? Pow(Unit unit, int n, out double scale)
    {
        if (unit.Offset != 0 && n != 1) throw new EvalException($"Powers of {unit.Symbol} aren't supported");
        return Compose(unit.Leaves.Select(t => t with { Power = t.Power * n }), out scale);
    }

    /// <summary>Combines units without converting between them (for conversion targets such as mi per h).</summary>
    public static Unit? Divide(Unit a, Unit b) =>
        a.Offset != 0 || b.Offset != 0
            ? null
            : Compose(a.Leaves.Concat(b.Leaves.Select(t => t with { Power = -t.Power })), out _);

    /// <summary>
    /// Turns leaf terms back into a unit: cancelled terms disappear, a lone leaf is returned as itself,
    /// a known combination is returned as its named unit, and anything else becomes a compound.
    /// <paramref name="scale"/> converts a value from the raw terms into the returned unit.
    /// </summary>
    public static Unit? Compose(IEnumerable<Term> terms, out double scale)
    {
        var merged = Merge(terms);
        scale = 1;
        if (!merged.All(t => InRange(t.Power))) throw PowerTooLarge();
        if (!SizeInRange(merged)) throw new EvalException("That unit is too large or too small");
        if (merged.Count == 0) return null;
        if (merged.Count == 1 && merged[0].Power == 1) return merged[0].Unit;

        // Units that cancel in dimension but not in size (m³/l) leave a plain number
        if (merged.Aggregate(Dim.None, (d, t) => d * t.Unit.Dimension.Pow(t.Power)) == Dim.None)
        {
            scale = Unit.Product(merged);
            return null;
        }

        if (NamedFor(merged) is { } named)
        {
            scale = Unit.Product(merged) / named.Factor;
            return named;
        }

        return new Unit(SymbolFor(merged), merged);
    }

    private static bool InRange(int power) => power is >= -MaxPower and <= MaxPower;

    /// <summary>
    /// Units are limited to sizes from 1e-300 to 1e300 of their base units (PB²⁰·TB²⁰ is 1e540, so it is
    /// rejected). With this, every unit factor and conversion scale is a finite, nonzero double.
    /// </summary>
    private static readonly double MaxLogSize = 300 * Math.Log(10);

    private static bool SizeInRange(IEnumerable<Term> terms) => Math.Abs(Unit.LogProduct(terms)) <= MaxLogSize + 1e-9;

    internal static EvalException PowerTooLarge() => new($"Unit powers can't be larger than {MaxPower}");

    internal static List<Term> Merge(IEnumerable<Term> terms)
    {
        var merged = new List<Term>();
        foreach (var t in terms)
        {
            int i = merged.FindIndex(m => ReferenceEquals(m.Unit, t.Unit));
            if (i >= 0) merged[i] = merged[i] with { Power = merged[i].Power + t.Power };
            else merged.Add(t);
        }
        merged.RemoveAll(t => t.Power == 0);
        return merged;
    }

    internal static string Key(IEnumerable<Term> leaves) =>
        string.Join("|", leaves.OrderBy(t => t.Unit.Symbol, StringComparer.Ordinal).Select(t => $"{t.Unit.Symbol}^{t.Power}"));

    private const string Superscripts = "⁰¹²³⁴⁵⁶⁷⁸⁹⁻";

    private static string Sup(int n) =>
        string.Concat(n.ToString(System.Globalization.CultureInfo.InvariantCulture)
            .Select(c => c == '-' ? '⁻' : Superscripts[c - '0']));

    /// <summary>m/s², kg·m/s², $/h, $/kWh, min⁻¹, or kg·m⁻¹·s⁻² when there are several denominators.</summary>
    private static string SymbolFor(IReadOnlyList<Term> terms)
    {
        static string Part(Unit u, int p) => p == 1 ? u.Symbol : u.Symbol + Sup(p);

        var num = terms.Where(t => t.Power > 0).OrderBy(t => t.Unit.IsCurrency ? 0 : 1).ToList();
        var den = terms.Where(t => t.Power < 0).ToList();
        var top = string.Join("·", num.Select(t => Part(t.Unit, t.Power)));

        // A numerator or denominator with a name, such as W·h as Wh
        if (num.Count > 1 && NamedFor(num) is { } numerator) top = numerator.Symbol;
        if (den.Count > 1 && NamedFor(den.Select(t => t with { Power = -t.Power })) is { } named)
            return num.Count == 0 ? named.Symbol + Sup(-1) : top + "/" + named.Symbol;

        if (num.Count == 0 || den.Count > 1)
            return string.Join("·", num.Concat(den).Select(t => Part(t.Unit, t.Power)));

        return den.Count == 0 ? top : top + "/" + Part(den[0].Unit, -den[0].Power);
    }

    private static Unit? NamedFor(IEnumerable<Term> terms)
    {
        var key = Key(terms);
        return Named.TryGetValue(key, out var named) || (_physics && PhysicsNamed.TryGetValue(key, out named)) ? named : null;
    }

    public static Unit Radian => Lookup("rad")!;
    public static Unit Inch => Lookup("inch")!;
    public static IEnumerable<string> CurrencyCodes => Fiat.Keys;

    private static Unit Get(string symbol) => Exact.GetValueOrDefault(symbol) ?? PhysicsExact[symbol];

    private static void Def(Dim dim, string symbol, double factor, params string[] names) =>
        Register(new Unit(symbol, dim, factor), names);

    /// <summary>A named unit built from other units, e.g. km/h from km and h⁻¹.</summary>
    private static void Derive(string symbol, Term[] definition, params string[] names)
    {
        var unit = new Unit(symbol, definition);
        Register(unit, names);
        Named.TryAdd(Key(unit.Leaves), unit);
    }

    /// <summary>A product of leaves that should display as an existing unit, e.g. J/s as W.</summary>
    private static void Alias(string symbol, params Term[] terms)
    {
        var unit = Get(symbol);
        bool physics = unit.Physics || terms.Any(t => t.Unit.Physics);
        (physics ? PhysicsNamed : Named).TryAdd(Key(terms), unit);
    }

    private static void Register(Unit unit, params string[] names)
    {
        foreach (var n in names)
        {
            Exact.TryAdd(n, unit);
            Loose.TryAdd(n, unit);
        }
    }

    /// <summary>Physics units; short symbols are case-sensitive so A, V and N don't match words.</summary>
    private static void Phys(Dim dim, string symbol, double factor, params string[] longNames)
    {
        var unit = new Unit(symbol, dim, factor) { Physics = true };
        PhysicsExact.TryAdd(symbol, unit);
        foreach (var n in longNames)
        {
            PhysicsExact.TryAdd(n, unit);
            PhysicsLoose.TryAdd(n, unit);
        }
    }

    private static Term T(string symbol, int power = 1) => new(Get(symbol), power);

    static UnitTable()
    {
        // Length (base: meter)
        Def(Dim.Length, "m", 1, "m", "meter", "meters", "metre", "metres");
        Def(Dim.Length, "km", 1000, "km", "kilometer", "kilometers", "kilometre", "kilometres");
        Def(Dim.Length, "cm", 0.01, "cm", "centimeter", "centimeters", "centimetre", "centimetres");
        Def(Dim.Length, "mm", 0.001, "mm", "millimeter", "millimeters", "millimetre", "millimetres");
        Def(Dim.Length, "µm", 1e-6, "µm", "um", "micrometer", "micrometers");
        Def(Dim.Length, "mi", 1609.344, "mi", "mile", "miles");
        Def(Dim.Length, "yd", 0.9144, "yd", "yard", "yards");
        Def(Dim.Length, "ft", 0.3048, "ft", "foot", "feet");
        Def(Dim.Length, "in", 0.0254, "inch", "inches");   // "in" itself is the conversion keyword
        Def(Dim.Length, "nmi", 1852, "nmi", "nauticalmile", "nauticalmiles");

        // Mass (base: kilogram)
        Def(Dim.Mass, "g", 0.001, "g", "gram", "grams");
        Def(Dim.Mass, "kg", 1, "kg", "kilogram", "kilograms", "kilo", "kilos");
        Def(Dim.Mass, "mg", 1e-6, "mg", "milligram", "milligrams");
        Def(Dim.Mass, "lb", 0.45359237, "lb", "lbs", "pound", "pounds");
        Def(Dim.Mass, "oz", 0.028349523125, "oz", "ounce", "ounces");
        Def(Dim.Mass, "t", 1000, "tonne", "tonnes");

        // Volume (base: cubic meter)
        Def(Dim.Volume, "l", 0.001, "l", "liter", "liters", "litre", "litres");
        Def(Dim.Volume, "ml", 1e-6, "ml", "milliliter", "milliliters", "millilitre", "millilitres");
        Def(Dim.Volume, "gal", 0.003785411784, "gal", "gallon", "gallons");
        Def(Dim.Volume, "qt", 0.000946352946, "qt", "quart", "quarts");
        Def(Dim.Volume, "pt", 0.000473176473, "pt", "pint", "pints");
        Def(Dim.Volume, "cup", 0.0002365882365, "cup", "cups");
        Def(Dim.Volume, "fl oz", 0.0000295735295625, "floz");
        Def(Dim.Volume, "tbsp", 0.00001478676478125, "tbsp", "tablespoon", "tablespoons");
        Def(Dim.Volume, "tsp", 0.00000492892159375, "tsp", "teaspoon", "teaspoons");

        // Time (base: second)
        Def(Dim.Time, "ms", 0.001, "ms", "millisecond", "milliseconds");
        Def(Dim.Time, "s", 1, "s", "sec", "secs", "second", "seconds");
        Def(Dim.Time, "min", 60, "min", "mins", "minute", "minutes");
        Def(Dim.Time, "h", 3600, "h", "hr", "hrs", "hour", "hours");
        Def(Dim.Time, "d", 86400, "d", "day", "days");
        Def(Dim.Time, "wk", 604800, "wk", "week", "weeks");
        Def(Dim.Time, "mo", 2629746, "month", "months");
        Def(Dim.Time, "yr", 31556952, "yr", "yrs", "year", "years");

        // Temperature (base: kelvin)
        Register(new Unit("°C", Dim.Temperature, 1, 273.15), "°C", "C", "celsius", "degC");
        Register(new Unit("°F", Dim.Temperature, 5.0 / 9.0, 273.15 - 32 * 5.0 / 9.0), "°F", "F", "fahrenheit", "degF");
        Register(new Unit("K", Dim.Temperature, 1), "K", "kelvin");

        // Data (base: byte)
        Def(Dim.Data, "bit", 0.125, "bit", "bits");
        Def(Dim.Data, "B", 1, "B", "byte", "bytes");
        Def(Dim.Data, "KB", 1e3, "KB", "kilobyte", "kilobytes");
        Def(Dim.Data, "MB", 1e6, "MB", "megabyte", "megabytes");
        Def(Dim.Data, "GB", 1e9, "GB", "gigabyte", "gigabytes");
        Def(Dim.Data, "TB", 1e12, "TB", "terabyte", "terabytes");
        Def(Dim.Data, "PB", 1e15, "PB", "petabyte", "petabytes");
        Def(Dim.Data, "KiB", 1024, "KiB");
        Def(Dim.Data, "MiB", 1024.0 * 1024, "MiB");
        Def(Dim.Data, "GiB", 1024.0 * 1024 * 1024, "GiB");
        Def(Dim.Data, "TiB", 1024.0 * 1024 * 1024 * 1024, "TiB");

        // Area and volume from lengths, plus leaf areas (base: m²)
        Derive("m²", [T("m", 2)], "m2", "m²", "sqm");
        Derive("cm²", [T("cm", 2)], "cm2", "cm²");
        Derive("km²", [T("km", 2)], "km2", "km²", "sqkm");
        Derive("ft²", [T("ft", 2)], "ft2", "ft²", "sqft");
        Derive("mi²", [T("mi", 2)], "mi2", "mi²", "sqmi");
        Derive("m³", [T("m", 3)], "m3", "m³");
        Def(Dim.Area, "ha", 1e4, "ha", "hectare", "hectares");
        Def(Dim.Area, "acre", 4046.8564224, "acre", "acres");

        // Speed (base: m/s)
        Derive("m/s", [T("m"), T("s", -1)], "m/s", "mps");
        Derive("km/h", [T("km"), T("h", -1)], "km/h", "kph", "kmh");
        Derive("mph", [T("mi"), T("h", -1)], "mph", "mi/h");
        Derive("ft/s", [T("ft"), T("s", -1)], "ft/s", "fps");
        Derive("kn", [T("nmi"), T("h", -1)], "kn", "knot", "knots");

        // Angle (base: radian)
        Def(Dim.Angle, "rad", 1, "rad", "radian", "radians");
        Def(Dim.Angle, "°", Math.PI / 180, "°", "deg", "degree", "degrees");

        // Energy (base: joule) and power (base: watt)
        Def(Dim.Energy, "J", 1, "J", "joule", "joules");
        Def(Dim.Energy, "kJ", 1000, "kJ");
        Def(Dim.Energy, "cal", 4.184, "cal", "calorie", "calories");
        Def(Dim.Energy, "kcal", 4184, "kcal");
        Def(Dim.Power, "W", 1, "W", "watt", "watts");
        Def(Dim.Power, "kW", 1000, "kW");
        Def(Dim.Power, "MW", 1e6, "MW");
        Def(Dim.Power, "hp", 745.699872, "hp", "horsepower");
        Derive("Wh", [T("W"), T("h")], "Wh");
        Derive("kWh", [T("kW"), T("h")], "kWh");

        // Pressure (base: pascal)
        Def(Dim.Pressure, "Pa", 1, "Pa");
        Def(Dim.Pressure, "kPa", 1000, "kPa");
        Def(Dim.Pressure, "bar", 1e5, "bar");
        Def(Dim.Pressure, "psi", 6894.757293168, "psi");
        Def(Dim.Pressure, "atm", 101325, "atm");

        // Currencies (base: USD). Rates are units per 1 USD, offline fallbacks
        // that are replaced by live rates when they can be fetched.
        Cur("USD", 1.00, "$", 2, "$", "dollar", "dollars");
        Cur("EUR", 0.92, "€", 2, "€", "euro", "euros");
        Cur("GBP", 0.79, "£", 2, "£");
        Cur("JPY", 150, "¥", 0, "¥", "yen");
        Cur("INR", 83, "₹", 2, "₹", "rupee", "rupees");
        Cur("KRW", 1330, "₩", 0, "₩", "won");
        Cur("CAD", 1.37, null, 2);
        Cur("AUD", 1.52, null, 2);
        Cur("CHF", 0.88, null, 2);
        Cur("CNY", 7.2, null, 2, "yuan", "rmb");
        Cur("MXN", 17.5, null, 2);
        Cur("BRL", 5.0, null, 2);
        Cur("SEK", 10.6, null, 2);
        Cur("NOK", 10.7, null, 2);
        Cur("DKK", 6.9, null, 2);
        Cur("NZD", 1.65, null, 2);
        Cur("SGD", 1.35, null, 2);
        Cur("HKD", 7.8, null, 2);
        Cur("ZAR", 18.5, null, 2);
        Cur("PLN", 4.0, null, 2);

        // Physics units (opt-in). Coulomb and tesla are left out because C and T are taken.
        Phys(Dim.Force, "N", 1, "newton", "newtons");
        Phys(Dim.Force, "kN", 1000, "kilonewton", "kilonewtons");
        Phys(Dim.Force, "lbf", 4.4482216152605, "poundforce");
        Phys(Dim.Frequency, "Hz", 1, "hertz");
        Phys(Dim.Frequency, "kHz", 1e3, "kilohertz");
        Phys(Dim.Frequency, "MHz", 1e6, "megahertz");
        Phys(Dim.Frequency, "GHz", 1e9, "gigahertz");
        Phys(Dim.Current, "A", 1, "amp", "amps", "ampere", "amperes");
        Phys(Dim.Current, "mA", 1e-3, "milliamp", "milliamps");
        Phys(Dim.Voltage, "V", 1, "volt", "volts");
        Phys(Dim.Voltage, "mV", 1e-3, "millivolt", "millivolts");
        Phys(Dim.Voltage, "kV", 1e3, "kilovolt", "kilovolts");
        Phys(Dim.Resistance, "Ω", 1, "ohm", "ohms");
        Phys(Dim.Resistance, "kΩ", 1e3, "kohm", "kohms");

        Alias("W", T("J"), T("s", -1));
        Alias("J", T("W"), T("s"));
        Alias("N", T("kg"), T("m"), T("s", -2));
        Alias("J", T("N"), T("m"));
        Alias("m", T("J"), T("N", -1));
        Alias("Pa", T("N"), T("m", -2));
        Alias("Hz", T("s", -1));
        Alias("W", T("V"), T("A"));
        Alias("V", T("W"), T("A", -1));
        Alias("A", T("W"), T("V", -1));
        Alias("Ω", T("V"), T("A", -1));
        Alias("V", T("A"), T("Ω"));
        Alias("A", T("V"), T("Ω", -1));
    }

    private static void Cur(string code, double perUsd, string? prefix, int decimals, params string[] extra)
    {
        var unit = new Unit(code, Dim.Currency, 1.0 / perUsd) { Prefix = prefix, Decimals = decimals };
        Fiat[code] = unit;
        Register(unit, [code, .. extra]);
    }

    /// <summary>Applies live rates (units per 1 USD). Unknown codes are ignored.</summary>
    public static int UpdateRates(IReadOnlyDictionary<string, double> perUsd)
    {
        int n = 0;
        foreach (var (code, unit) in Fiat)
        {
            if (perUsd.TryGetValue(code, out var rate) && rate > 0)
            {
                unit.Factor = 1.0 / rate;
                n++;
            }
        }
        return n;
    }
}
