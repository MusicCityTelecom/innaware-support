namespace InnAwareSupport.Technician;

internal static class Program
{
    internal const string DefaultConsole = "https://remote.innawareucp.com";

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var url = DefaultConsole;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--server" && i + 1 < args.Length)
                url = args[++i].TrimEnd('/');
        }
        Application.Run(new TechnicianForm(url));
    }
}
