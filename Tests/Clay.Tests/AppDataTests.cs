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
        var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(original)).ShouldNotBeNull();

        restored.Validate();
        restored.ShouldBe(original);
    }

    [Fact]
    public void OlderSettingsLeavePhysicsUnitsOff()
    {
        var restored = JsonSerializer.Deserialize<Settings>(
            """{"Decimals":6,"AngleMode":"Radians","GroupDigits":true,"Theme":"Default"}""").ShouldNotBeNull();

        restored.Validate();
        restored.PhysicsUnits.ShouldBeFalse();
        restored.IgnoreWhitespace.ShouldBeFalse();
        restored.AlwaysOnTop.ShouldBeFalse();
        restored.AssumePositive.ShouldBeFalse();
        restored.IntervalMode.ShouldBe(1);
    }

    [Theory]
    [InlineData(3, "Radians", "Default", 1)]
    [InlineData(6, "Invalid", "Default", 1)]
    [InlineData(6, "Degrees", "Invalid", 1)]
    [InlineData(6, "Radians", "Default", 0)]
    [InlineData(6, "Radians", "Default", 3)]
    public void InvalidSettingsAreRejected(int decimals, string angleMode, string theme, int intervalMode)
    {
        Should.Throw<InvalidDataException>(() =>
            new Settings(decimals, angleMode, true, theme, IntervalMode: intervalMode).Validate());
    }

    [Fact]
    public void CachedRatesRoundTrip()
    {
        var now = DateTimeOffset.UtcNow;
        var original = new CachedRates(now, Rates());
        var restored = JsonSerializer.Deserialize<CachedRates>(JsonSerializer.Serialize(original)).ShouldNotBeNull();

        restored.Validate(now);
        restored.FetchedAt.ShouldBe(now);
        restored.Rates.Count.ShouldBe(original.Rates.Count);
    }

    [Fact]
    public void InvalidRatesAndDatesAreRejected()
    {
        var now = DateTimeOffset.UtcNow;
        var rates = Rates();
        Should.Throw<InvalidDataException>(() => new CachedRates(now.AddMinutes(1), rates).Validate(now));
        rates.Remove("EUR");
        Should.Throw<InvalidDataException>(() => CachedRates.ValidateRates(rates));
        rates["EUR"] = double.NaN;
        Should.Throw<InvalidDataException>(() => CachedRates.ValidateRates(rates));
        rates["EUR"] = 1;
        rates["USD"] = 2;
        Should.Throw<InvalidDataException>(() => CachedRates.ValidateRates(rates));
    }

    private static Dictionary<string, double> Rates() =>
        UnitTable.CurrencyCodes.ToDictionary(code => code, _ => 1.0);
}
