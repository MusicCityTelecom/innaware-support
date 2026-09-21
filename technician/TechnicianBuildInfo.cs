using System.Reflection;

namespace InnAwareSupport.Technician;

internal static class TechnicianBuildInfo
{
    private static readonly Assembly Current =
        typeof(TechnicianBuildInfo).Assembly;

    public static string Version { get; } =
        Current.GetName().Version?.ToString() ?? "unknown";

    public static string InformationalVersion { get; } =
        Current
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? Version;
}
