using System.Reflection;

namespace InnAwareSupport.Agent;

internal static class AgentBuildInfo
{
    private static readonly Assembly Assembly =
        typeof(AgentBuildInfo).Assembly;

    public static string Version { get; } =
        Assembly.GetName().Version?.ToString() ?? "unknown";

    public static string InformationalVersion { get; } =
        Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? Version;
}
