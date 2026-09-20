namespace InnAwareSupport.Technician;

internal static class Program
{
    internal const string DefaultServer = "https://remote.innawareucp.com";
    private const string DeepLinkScheme = "innaware-support-tech";

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var server = DefaultServer;
        string? startUrl = null;
        string? deepLink = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--server" && i + 1 < args.Length)
                server = args[++i].TrimEnd('/');
            else if (args[i] == "--url" && i + 1 < args.Length)
                startUrl = args[++i];
            else if (args[i].StartsWith(
                         DeepLinkScheme + "://",
                         StringComparison.OrdinalIgnoreCase))
                deepLink = args[i];
        }

        if (!string.IsNullOrWhiteSpace(deepLink))
            startUrl = ResolveDeepLink(server, deepLink);

        Application.Run(new TechnicianForm(server, startUrl));
    }

    private static string? ResolveDeepLink(string server, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(
                uri.Scheme,
                DeepLinkScheme,
                StringComparison.OrdinalIgnoreCase))
            return null;

        if (!string.Equals(
                uri.Host,
                "session",
                StringComparison.OrdinalIgnoreCase))
            return null;

        var raw = uri.AbsolutePath.Trim('/');
        if (!Guid.TryParse(raw, out var sessionId))
            return null;

        return server.TrimEnd('/') +
               "/?viewer=" +
               Uri.EscapeDataString(sessionId.ToString()) +
               "&popout=1";
    }
}
