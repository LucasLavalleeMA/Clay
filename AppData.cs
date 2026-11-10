using Clay.Engine;

namespace Clay;

internal sealed record Settings(int Decimals, string AngleMode, bool GroupDigits, string Theme, bool PhysicsUnits = false)
{
    public void Validate()
    {
        if (Decimals is not (2 or 4 or 6 or 8) ||
            AngleMode is not ("Radians" or "Degrees") ||
            Theme is not ("Default" or "Light" or "Dark" or "Sticky"))
            throw new InvalidDataException("Settings contain unsupported values");
    }
}

internal sealed record CachedRates(DateTimeOffset FetchedAt, Dictionary<string, double> Rates)
{
    public void Validate(DateTimeOffset now)
    {
        if (Rates == null || FetchedAt == default || FetchedAt > now)
            throw new InvalidDataException("Rate cache is invalid");
        ValidateRates(Rates);
    }

    public static void ValidateRates(IReadOnlyDictionary<string, double> rates)
    {
        foreach (var code in UnitTable.CurrencyCodes)
            if (!rates.TryGetValue(code, out var rate) || !double.IsFinite(rate) || rate <= 0)
                throw new InvalidDataException($"Missing or invalid exchange rate for {code}");
        if (Math.Abs(rates["USD"] - 1) > 1e-9)
            throw new InvalidDataException("Exchange rates must be based on USD");
    }
}
