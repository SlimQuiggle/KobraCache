using System.Reflection;

namespace KobraCache.Desktop;

public static class AppVersion
{
    public static string Display { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    public static Version Current { get; } =
        Version.TryParse(Display.Split(['-', '+'], 2)[0], out var version)
            ? version
            : typeof(AppVersion).Assembly.GetName().Version ?? new Version(0, 0, 0);
}
