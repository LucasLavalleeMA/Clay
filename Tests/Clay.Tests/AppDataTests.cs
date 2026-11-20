using System.Text.Json;
using Clay.Engine;
using Clay.UI;

namespace Clay.Tests;

public class AppDataTests
{
    [Fact]
    public void SettingsRoundTrip()
    {
        var original = new Settings(
            12, "Degrees", false, "Sticky",
            PhysicsUnits: true, IgnoreWhitespace: true,
            AlwaysOnTop: true, AssumePositive: true, IntervalMode: 2);
        var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(original));

        Assert.NotNull(restored);
        restored.Validate();
        Assert.Equal(original, restored);
    }

    [Fact]
    public void OlderSettingsLeavePhysicsUnitsOff()
    {
        var restored = JsonSerializer.Deserialize<Settings>(
            """{"Decimals":6,"AngleMode":"Radians","GroupDigits":true,"Theme":"Default"}""");

        Assert.NotNull(restored);
        restored.Validate();
        Assert.False(restored.PhysicsUnits);
        Assert.False(restored.IgnoreWhitespace);
        Assert.False(restored.AlwaysOnTop);
        Assert.False(restored.AssumePositive);
        Assert.Equal(1, restored.IntervalMode);
    }

    [Theory]
    [InlineData(3, "Radians", "Default", 1)]
    [InlineData(6, "Invalid", "Default", 1)]
    [InlineData(6, "Degrees", "Invalid", 1)]
    [InlineData(6, "Radians", "Default", 0)]
    [InlineData(6, "Radians", "Default", 3)]
    public void InvalidSettingsAreRejected(int decimals, string angleMode, string theme, int intervalMode)
    {
        Assert.Throws<InvalidDataException>(() =>
            new Settings(decimals, angleMode, true, theme, IntervalMode: intervalMode).Validate());
    }

    [Fact]
    public void CachedRatesRoundTrip()
    {
        var now = DateTimeOffset.UtcNow;
        var original = new CachedRates(now, Rates());
        var restored = JsonSerializer.Deserialize<CachedRates>(JsonSerializer.Serialize(original));

        Assert.NotNull(restored);
        restored.Validate(now);
        Assert.Equal(now, restored.FetchedAt);
        Assert.Equal(original.Rates.Count, restored.Rates.Count);
    }

    [Fact]
    public void InvalidRatesAndDatesAreRejected()
    {
        var now = DateTimeOffset.UtcNow;
        var rates = Rates();
        Assert.Throws<InvalidDataException>(() => new CachedRates(now.AddMinutes(1), rates).Validate(now));
        rates.Remove("EUR");
        Assert.Throws<InvalidDataException>(() => CachedRates.ValidateRates(rates));
        rates["EUR"] = double.NaN;
        Assert.Throws<InvalidDataException>(() => CachedRates.ValidateRates(rates));
        rates["EUR"] = 1;
        rates["USD"] = 2;
        Assert.Throws<InvalidDataException>(() => CachedRates.ValidateRates(rates));
    }

    private static Dictionary<string, double> Rates() =>
        UnitTable.CurrencyCodes.ToDictionary(code => code, _ => 1.0);
}
