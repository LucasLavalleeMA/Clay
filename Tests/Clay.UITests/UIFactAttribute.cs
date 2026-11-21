namespace Clay.UITests;

/// <summary>
/// A fact that launches real windows: skipped in CI (the CI environment variable is set) and when the
/// process has no interactive desktop, since Clay's window can't be shown or automated there.
/// </summary>
public sealed class UIFactAttribute : FactAttribute
{
    public UIFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CI") is { Length: > 0 } ci && !ci.Equals("false", StringComparison.OrdinalIgnoreCase))
            Skip = "UI tests don't run in CI";
        else if (!Environment.UserInteractive)
            Skip = "UI tests need an interactive desktop";
    }
}
