using Clay.Engine;

namespace Clay.Tests;

public class CompoundUnitTests
{
    private static LineResult Eval(string document, bool physics = false) =>
        new Calculator { PhysicsUnits = physics }.Evaluate(document)[^1];

    [Theory]
    [InlineData("5 m/s * 4 s", "20 m")]
    [InlineData("5 m/s × 4 s", "20 m")]
    [InlineData("4 s * 5 m/s", "20 m")]
    [InlineData("100 km / 2 h", "50 km/h")]
    [InlineData("60 mph * 30 min", "30 mi")]
    [InlineData("5 km/h * 30 min", "2.5 km")]
    [InlineData("2 kW * 3 h", "6 kWh")]
    [InlineData("6 kWh / 3 h", "2 kW")]
    [InlineData("100 J / 2 s", "50 W")]
    [InlineData("2 W * 3 s", "6 J")]
    [InlineData("10 km / 5 km", "2")]
    [InlineData("20 m/s * 3", "60 m/s")]
    [InlineData("product(2 m, 3 m)", "6 m²")]
    [InlineData("2 kg * 3 m/s^2", "6 kg·m/s²")]
    [InlineData("60 / 2 min", "30 min⁻¹")]
    public void MultipliesAndDividesUnits(string expression, string expected) =>
        AssertResult(Eval(expression), expected);

    [Theory]
    [InlineData("3 m * 4 m", "12 m²")]
    [InlineData("3 ft * 4 ft", "12 ft²")]
    [InlineData("2 m * 3 ft", "1.8288 m²")]
    [InlineData("3 ft * 2 m", "19.685039 ft²")]
    [InlineData("2 m * 3 m * 4 m", "24 m³")]
    [InlineData("1 acre / 100 ft in ft", "435.6 ft")]
    [InlineData("10 m * 1 ha", "100,000 m³")]
    public void MixedUnitsUseTheLeftUnit(string expression, string expected) =>
        AssertResult(Eval(expression), expected);

    [Theory]
    [InlineData("5 l / 100 km", "0.05 l/km")]
    [InlineData("400 km / 40 l", "10 km/l")]
    [InlineData("1 km per l", "1 km/l")]
    [InlineData("30 mi / 1 gal in km/l", "12.754311 km/l")]
    [InlineData("1 m² * 20 mm", "0.02 m³")]
    [InlineData("1000 kg/m^3 * 2 l", "2 kg")]
    public void KeepsRatesBetweenVolumeAndLength(string expression, string expected) =>
        AssertResult(Eval(expression), expected);

    [Theory]
    [InlineData("$0.1234 per kWh", "$0.1234/kWh")]
    [InlineData("rate = $0.12/kWh\nrate", "$0.12/kWh")]
    [InlineData("$0.12/kWh * 500 kWh", "$60.00")]
    [InlineData("1 / 2 kWh", "0.5 kWh⁻¹")]
    public void NamesCompoundDenominators(string expression, string expected) =>
        AssertResult(Eval(expression), expected);

    [Theory]
    [InlineData("5 m / s", "5 m/s")]
    [InlineData("60 miles per hour", "60 mph")]
    [InlineData("$15 per hour", "$15/h")]
    [InlineData("$15 per hour * 40 h", "$600.00")]
    [InlineData("8 h * $15/h", "$120.00")]
    [InlineData("9.8 m/s^2 * 2 s", "19.6 m/s")]
    [InlineData("9.8 m/s² * 2 s", "19.6 m/s")]
    [InlineData("2 m^2/s * 3 s", "6 m²")]
    [InlineData("1 kg·m/s² * 1 s", "1 kg·m/s")]
    [InlineData("5 m^2", "5 m²")]
    [InlineData("(3 m)^2", "9 m²")]
    [InlineData("sqrt(9 m²)", "3 m")]
    [InlineData("sqrt(16 m^2)", "4 m")]
    [InlineData("cbrt(27 m³)", "3 m")]
    public void ParsesUnitExpressions(string expression, string expected) =>
        AssertResult(Eval(expression), expected);

    [Theory]
    [InlineData("10 m/s in km/h", "36 km/h")]
    [InlineData("1 m/s^2 in ft/s^2", "3.28084 ft/s²")]
    [InlineData("100 km/h in miles per hour", "62.137119 mph")]
    [InlineData("1 kWh in J", "3,600,000 J")]
    [InlineData("1 m³ in l", "1,000 l")]
    [InlineData("1 gal in l", "3.785412 l")]
    [InlineData("1 lb in g", "453.59237 g")]
    public void ConvertsCompoundUnits(string expression, string expected) =>
        AssertResult(Eval(expression), expected);

    [Theory]
    [InlineData("v = 3\n2 V", "2 V")]
    [InlineData("a = 3\n2 A * 3 V", "6 W")]
    [InlineData("n = 4\n5 N * 2 m", "10 J")]
    [InlineData("v² = 3\n2 V²", "2 V²")]
    [InlineData("v⁰ = 3\n2 V⁰", "2")]
    [InlineData("v² = 3\n2 v²", "6")]
    public void PhysicsSymbolsBeatCaseInsensitiveVariables(string document, string expected) =>
        AssertResult(Eval(document, physics: true), expected);

    [Theory]
    [InlineData("5 m^0", "5")]
    [InlineData("(5 m)^0", "1")]
    [InlineData("5 in^2", "5 in²")]
    [InlineData("5 inch^2", "5 in²")]
    [InlineData("(2 inch)^2", "4 in²")]
    [InlineData("4 in²", "4 in²")]
    [InlineData("4 in² * 2 in", "8 in³")]
    [InlineData("sqrt(1 ha)", "100 m")]
    [InlineData("cbrt(8 l)", "0.2 m")]
    [InlineData("sqrt(4 acre)", "127.229814 m")]
    [InlineData("sqrt(100 J / 1 kg)", "10 m/s")]
    [InlineData("1 m² in in^2", "1,550.0031 in²")]
    [InlineData("1 m/s in in per s", "39.370079 in/s")]
    [InlineData("5 in per s", "5 in/s")]
    [InlineData("5 in / s", "5 in/s")]
    [InlineData("5 m / in", "196.850394")]
    [InlineData("5 m/in", "196.850394")]
    [InlineData("1 m/cm as a %", "10,000%")]
    [InlineData("5 m/m/s", "5 s⁻¹")]
    [InlineData("1 m/s²^2", "1 m/s⁴")]
    [InlineData("1 m²^2/s", "1 m⁴/s")]
    [InlineData("1 m/s^2^2", "1 m/s⁴")]
    [InlineData("1 m^2^2/s", "1 m⁴/s")]
    [InlineData("1 m/in/s", "39.370079 s⁻¹")]
    [InlineData("5 m^2^2", "5 m⁴")]
    [InlineData("5 m^+2", "5 m²")]
    [InlineData("5 m/s^+2", "5 m/s²")]
    [InlineData("5 m⁰", "5")]
    [InlineData("5 m/s⁰", "5 m")]
    [InlineData("5 m⁰/s", "5 s⁻¹")]
    [InlineData("2 * m⁰", "2")]
    [InlineData("5 m⁰^2", "5")]
    [InlineData("5 m⁰ +", "5")]
    [InlineData("5 m/s·s", "5 m")]
    [InlineData("5 kg·m/s²", "5 kg·m/s²")]
    [InlineData("10 m/min(2, 4)", "5 m")]
    [InlineData("2 * m⁰^2", "2")]
    [InlineData("5 m^2^2/s", "5 m⁴/s")]
    [InlineData("5 per m/s", "5 m⁻¹·s⁻¹")]
    [InlineData("5 / m/s", "5 m⁻¹·s⁻¹")]
    [InlineData("1 m/s^2.0", "1 m/s²")]
    [InlineData("1 m/s^2e0", "1 m/s²")]
    [InlineData("1 m³/l as a %", "100,000%")]
    [InlineData("1 m² / 1 l * 1 m", "1,000")]
    public void HandlesZeroPowersInchesAndLeafRoots(string expression, string expected) =>
        AssertResult(Eval(expression), expected);

    [Theory]
    [InlineData("5k/s", "5,000 s⁻¹")]
    [InlineData("$5k/month", "$5,000/mo")]
    [InlineData("$5M/yr", "$5,000,000/yr")]
    [InlineData("5 K/s", "5 K/s")]
    [InlineData("2 kg/m ^ 2", "2 kg/m²")]
    [InlineData("2 m/s ^ -2", "2 m·s²")]
    [InlineData("2 s ^ 2/m", "2 s²/m")]
    [InlineData("150 Wh/km * 100 km", "15,000 Wh")]
    [InlineData("10 kW·h/km", "10 kWh/km")]
    [InlineData("5 $^2", "5 USD²")]
    [InlineData("5 USD^2", "5 USD²")]
    [InlineData("5 $^0", "5")]
    [InlineData("5 m / s^(2)", "5 m/s²")]
    [InlineData("n = 2\n5 m / s^n", "5 m/s²")]
    [InlineData("5 kg per m^(1+1)", "5 kg/m²")]
    [InlineData("2 * m⁰^(2)", "2")]
    [InlineData("1 m in cm per s^0", "100 cm")]
    [InlineData("1 m in cm per s⁰", "100 cm")]
    [InlineData("1 m in cm/s⁰", "100 cm")]
    [InlineData("1 m/s in cm per s per s^0", "100 cm/s")]
    [InlineData("1 s^-1 in m^0 per s", "1 s⁻¹")]
    [InlineData("1 s^-1 in m⁰ per s", "1 s⁻¹")]
    [InlineData("5 kWh/$", "5 kWh/USD")]
    [InlineData("5 kWh per $", "5 kWh/USD")]
    [InlineData("5 kWh / $2", "2.5 kWh/USD")]
    [InlineData("10 kWh/USD in kWh/$", "10 kWh/USD")]
    [InlineData("1 mi^20 in km^20", "13,582.437148 km²⁰")]
    [InlineData("1e10 PB^20 in TB^20", "1E+70 TB²⁰")]
    [InlineData("0 m^20 in nmi^20", "0 nmi²⁰")]
    [InlineData("1e200 mm^20 * 1e200 km^-19", "1E+286 mm")]
    [InlineData("1e300 cm^20 * 1e10 m^-20", "1E+270")]
    [InlineData("-1e300 cm^20 * 1e10 m^-20", "-1E+270")]
    [InlineData("1e-300 PB^20 / 1e-300 B^20", "1E+300")]
    [InlineData("-1e-300 PB^20 / 1e-300 B^20", "-1E+300")]
    [InlineData("1e300 B^20 / 1e-300 PB^20", "1E+300")]
    [InlineData("sqrt(1e300 PB^19·B)", "3.162278E+292 B¹⁰")]
    [InlineData("0 km^20 * 1 mm^-19", "0 km")]
    [InlineData("0 m / 5 m", "0")]
    [InlineData("1e308 bit^20 / 1e-9 PB^20", "0.086736")]
    [InlineData("1e-300 PB^20 / 1 bit^20", "1.152921E+18")]
    [InlineData("0 PB^20 * 1 bit^-20", "0")]
    [InlineData("0 PB^20 / 1e-300 B^20", "0")]
    [InlineData("5 PB^20", "5 PB²⁰")]
    [InlineData("1e200 * 1e200", "∞")]
    [InlineData("0 m * 5", "0 m")]
    [InlineData("0 m / 5", "0 m")]
    [InlineData("(0 m)^2", "0 m²")]
    [InlineData("1 s^-1 in m per m per s", "1 s⁻¹")]
    public void HandlesScaleWordsSpacedPowersAndNamedNumerators(string expression, string expected) =>
        AssertResult(Eval(expression), expected);

    [Theory]
    [InlineData("20 °C * 2 m")]
    [InlineData("2 m / 20 °C")]
    [InlineData("(2 °C)^2")]
    [InlineData("sqrt(2 m)")]
    [InlineData("sqrt(2 ha * 1 m)")]
    [InlineData("2 m + 3 s")]
    [InlineData("5 m/s in km")]
    [InlineData("1 K/s in °C per s")]
    [InlineData("1 m/s in m per °C")]
    [InlineData("(3 m)^(2 s)")]
    [InlineData("9 m²^0.5")]
    [InlineData("5 m/s in m/in")]
    [InlineData("1 m in °C per °C")]
    [InlineData("1 m/s^2.5")]
    [InlineData("5 m^21")]
    [InlineData("5 m^1e10")]
    [InlineData("(5 m)^1e10")]
    [InlineData("5 m^20 * 5 m")]
    [InlineData("1e-300 km^100 / 1e-300 m^100")]
    [InlineData("sqrt(1e-300 PB^43·B)")]
    [InlineData("5 m / s^(1 s)")]
    [InlineData("(5 s)^(1 s)")]
    [InlineData("10^(3 km)")]
    [InlineData("1e-200 m * 1e-200 m")]
    [InlineData("1e200 m * 1e200 m")]
    [InlineData("(1e200 m)^2")]
    [InlineData("1e-300 PB^20·TB^20 / 1 B^20·KB^20")]
    [InlineData("1 PB^20 / 1 bit^20")]
    [InlineData("(-1e-36 ha)^9")]
    [InlineData("5 µm^20/PB^20")]
    [InlineData("5 µm^20 * 1 PB^-20")]
    [InlineData("1e200 m * 1e200")]
    [InlineData("1e200 * 1e200 m")]
    [InlineData("1e-200 m / 1e200")]
    [InlineData("(0 m)^-1")]
    [InlineData("(0 m²)^-0.5")]
    [InlineData("5 s²^2147483647")]
    [InlineData("5 s^65536^65536")]
    [InlineData("m⁰ = 3")]
    [InlineData("1 m⁻²¹⁴⁷⁴⁸³⁶⁴⁸")]
    [InlineData("1 m/s⁻²¹⁴⁷⁴⁸³⁶⁴⁸")]
    [InlineData("1 m⁻²¹⁴⁷⁴⁸³⁶⁴⁸^2")]
    public void RejectsInvalidUnitArithmetic(string expression) =>
        Assert.Equal(LineKind.Error, Eval(expression).Kind);

    [Theory]
    [InlineData("5 N * 2 m", "10 J")]
    [InlineData("2 kg * 3 m/s^2", "6 N")]
    [InlineData("12 V / 2 Ω", "6 A")]
    [InlineData("2 A * 3 V", "6 W")]
    [InlineData("60 Hz * 2 s", "120")]
    [InlineData("10 J / 2 N", "5 m")]
    [InlineData("10 N / 2 m²", "5 Pa")]
    [InlineData("1 kN in N", "1,000 N")]
    [InlineData("1 kg·m/s² in N", "1 N")]
    [InlineData("100 W / 2 V", "50 A")]
    [InlineData("5 kN", "5 kN")]
    [InlineData("0.5 as a %", "50%")]
    [InlineData("5 k/s", "5,000 Hz")]
    public void PhysicsUnitsWhenEnabled(string expression, string expected) =>
        AssertResult(Eval(expression, physics: true), expected);

    [Fact]
    public void PoweredResultsDoNotUnderflow()
    {
        Assert.Equal(1e-286, Eval("1e-200 km^20 / 1e200 mm^19").Value!.Number, 1e-295);
        Assert.Equal(3.1622776601683885e-8, Eval("sqrt(1e-300 PB^19·B)").Value!.Number, 1e-17);
        Assert.Equal(1e-304, Eval("(1e-36 ha)^9.5").Value!.Number, 1e-313);
        var big = UnitTable.Parse("PB²⁰·TB¹⁰·mg²⁰·µm²⁰");
        Assert.NotNull(big);
        Assert.Equal(1.0, big.Factor / 1e180, 1e-9);
    }

    [Fact]
    public void PhysicsSymbolsAreVariablesWhenDisabled()
    {
        AssertResult(Eval("N = 5\nN * 2"), "10");
        AssertResult(Eval("V = 3\n2 V"), "6");
    }

    [Fact]
    public void ReservedPhysicsSymbolDoesNotResolveToDifferentlyCasedVariable()
    {
        Assert.NotEqual(LineKind.Result, Eval("v = 3\nV * 2", physics: true).Kind);
        AssertResult(Eval("v = 3\nv * 2", physics: true), "6");
    }

    [Fact]
    public void PhysicsSymbolsAreReservedWhenEnabled()
    {
        var result = Eval("N = 5", physics: true);
        Assert.Equal(LineKind.Error, result.Kind);
        Assert.Contains("reserved", result.Text);
    }

    [Fact]
    public void PhysicsSettingDoesNotLeakBetweenEvaluations()
    {
        AssertResult(Eval("5 N * 2 m", physics: true), "10 J");
        Assert.False(UnitTable.PhysicsEnabled);
        Assert.Null(UnitTable.Lookup("N"));
    }

    private static void AssertResult(LineResult result, string expected)
    {
        Assert.True(result.Kind == LineKind.Result, $"{result.Kind}: {result.Text}");
        Assert.Equal(expected, result.Text);
    }
}
