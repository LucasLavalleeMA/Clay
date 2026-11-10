using System.Text.Json;
using Clay.Engine;

namespace Clay.Tests;

public class AppDataTests
{
    [Fact]
    public void SettingsRoundTrip()
    {
        var original = new Settings(8, "Degrees", false, "Sticky", PhysicsUnits: true);
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
    }

    [Theory]
    [InlineData(3, "Radians", "Default")]
    [InlineData(6, "Invalid", "Default")]
    [InlineData(6, "Degrees", "Invalid")]
    public void InvalidSettingsAreRejected(int decimals, string angleMode, string theme)
    {
        Assert.Throws<InvalidDataException>(() => new Settings(decimals, angleMode, true, theme).Validate());
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
