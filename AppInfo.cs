using System.Reflection;

namespace Clay;

public static class AppInfo
{
    public static string Version { get; } =
        (Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    public static string DisplayVersion => $"{Version}";
}
