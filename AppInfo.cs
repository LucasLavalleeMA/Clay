using System.Reflection;

namespace Clay.UI;

public static class AppInfo
{
    public static string Version { get; } =
        (Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    public static string DisplayVersion => $"{Version}";

    /// <summary>Autosave, settings, rate cache, and error log location; CLAY_DATA_DIR overrides it (used by UI tests).</summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("CLAY_DATA_DIR") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clay");
}
