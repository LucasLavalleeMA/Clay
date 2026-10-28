namespace Clay.Engine;

public enum Dim
{
    Length, Mass, Volume, Time, Temperature, Data, Speed, Area, Angle,
    Energy, Power, Pressure, Currency
}

public sealed class Unit
{
    public string Symbol { get; }
    public Dim Dimension { get; }

    /// <summary>Multiplier to the dimension's base unit (for currencies: 1 / units-per-USD).</summary>
    public double Factor { get; set; }

    /// <summary>Offset in base units (temperature only).</summary>
    public double Offset { get; }

    /// <summary>Optional symbol shown before the number (e.g. "$").</summary>
    public string? Prefix { get; init; }

    /// <summary>Decimals used when displaying currencies.</summary>
    public int Decimals { get; init; } = 2;

    public bool IsCurrency => Dimension == Dim.Currency;

    public Unit(string symbol, Dim dimension, double factor, double offset = 0)
    {
        Symbol = symbol;
        Dimension = dimension;
        Factor = factor;
        Offset = offset;
    }

    public static double Convert(double x, Unit from, Unit to) =>
        (x * from.Factor + from.Offset - to.Offset) / to.Factor;
}

public static class UnitTable
{
    private static readonly Dictionary<string, Unit> Exact = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Unit> Loose = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Unit> Fiat = new(StringComparer.OrdinalIgnoreCase);

    public static Unit? Lookup(string name) =>
        Exact.TryGetValue(name, out var u) ? u : Loose.GetValueOrDefault(name);

    public static Unit Radian => Lookup("rad")!;
    public static Unit Inch => Lookup("inch")!;

    private static void Def(Dim dim, string symbol, double factor, params string[] names) =>
        Register(new Unit(symbol, dim, factor), names);

    private static void Register(Unit unit, params string[] names)
    {
        foreach (var n in names)
        {
            Exact.TryAdd(n, unit);
            Loose.TryAdd(n, unit);
        }
    }

    static UnitTable()
    {
        // Length (base: metre)
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

        // Mass (base: gram)
        Def(Dim.Mass, "g", 1, "g", "gram", "grams");
        Def(Dim.Mass, "kg", 1000, "kg", "kilogram", "kilograms", "kilo", "kilos");
        Def(Dim.Mass, "mg", 0.001, "mg", "milligram", "milligrams");
        Def(Dim.Mass, "lb", 453.59237, "lb", "lbs", "pound", "pounds");
        Def(Dim.Mass, "oz", 28.349523125, "oz", "ounce", "ounces");
        Def(Dim.Mass, "t", 1e6, "tonne", "tonnes");

        // Volume (base: litre)
        Def(Dim.Volume, "l", 1, "l", "liter", "liters", "litre", "litres");
        Def(Dim.Volume, "ml", 0.001, "ml", "milliliter", "milliliters", "millilitre", "millilitres");
        Def(Dim.Volume, "gal", 3.785411784, "gal", "gallon", "gallons");
        Def(Dim.Volume, "qt", 0.946352946, "qt", "quart", "quarts");
        Def(Dim.Volume, "pt", 0.473176473, "pt", "pint", "pints");
        Def(Dim.Volume, "cup", 0.2365882365, "cup", "cups");
        Def(Dim.Volume, "fl oz", 0.0295735295625, "floz");
        Def(Dim.Volume, "tbsp", 0.01478676478125, "tbsp", "tablespoon", "tablespoons");
        Def(Dim.Volume, "tsp", 0.00492892159375, "tsp", "teaspoon", "teaspoons");
        Def(Dim.Volume, "m³", 1000, "m3", "m³");

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

        // Speed (base: m/s)
        Def(Dim.Speed, "m/s", 1, "m/s", "mps");
        Def(Dim.Speed, "km/h", 1000.0 / 3600, "km/h", "kph", "kmh");
        Def(Dim.Speed, "mph", 1609.344 / 3600, "mph", "mi/h");
        Def(Dim.Speed, "ft/s", 0.3048, "ft/s", "fps");
        Def(Dim.Speed, "kn", 1852.0 / 3600, "kn", "knot", "knots");

        // Area (base: m²)
        Def(Dim.Area, "m²", 1, "m2", "m²", "sqm");
        Def(Dim.Area, "cm²", 1e-4, "cm2", "cm²");
        Def(Dim.Area, "km²", 1e6, "km2", "km²", "sqkm");
        Def(Dim.Area, "ha", 1e4, "ha", "hectare", "hectares");
        Def(Dim.Area, "acre", 4046.8564224, "acre", "acres");
        Def(Dim.Area, "ft²", 0.09290304, "ft2", "ft²", "sqft");
        Def(Dim.Area, "mi²", 2589988.110336, "mi2", "mi²", "sqmi");

        // Angle (base: radian)
        Def(Dim.Angle, "rad", 1, "rad", "radian", "radians");
        Def(Dim.Angle, "°", Math.PI / 180, "°", "deg", "degree", "degrees");

        // Energy (base: joule) and power (base: watt)
        Def(Dim.Energy, "J", 1, "J", "joule", "joules");
        Def(Dim.Energy, "kJ", 1000, "kJ");
        Def(Dim.Energy, "cal", 4.184, "cal", "calorie", "calories");
        Def(Dim.Energy, "kcal", 4184, "kcal");
        Def(Dim.Energy, "Wh", 3600, "Wh");
        Def(Dim.Energy, "kWh", 3.6e6, "kWh");
        Def(Dim.Power, "W", 1, "W", "watt", "watts");
        Def(Dim.Power, "kW", 1000, "kW");
        Def(Dim.Power, "MW", 1e6, "MW");
        Def(Dim.Power, "hp", 745.699872, "hp", "horsepower");

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
