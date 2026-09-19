namespace InnAwareSupport.Technician;

internal static class Program
{
    internal const string DefaultServer = "https://remote.innawareucp.com";

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var server = DefaultServer;
        string? startUrl = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--server" && i + 1 < args.Length)
                server = args[++i].TrimEnd('/');
            else if (args[i] == "--url" && i + 1 < args.Length)
                startUrl = args[++i];
        }

        Application.Run(new TechnicianForm(server, startUrl));
    }
}
