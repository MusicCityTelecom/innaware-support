using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace InnAwareSupport.Technician;

internal static class AppDiagnostics
{
    private static readonly object Gate = new();
    private static bool _initialized;

    public static string BaseDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InnAware",
        "SupportTechnician");

    public static string LogDirectory { get; } = Path.Combine(
        BaseDirectory,
        "logs");

    public static string LogPath { get; } = Path.Combine(
        LogDirectory,
        "technician.log");

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized) return;
            _initialized = true;

            Directory.CreateDirectory(LogDirectory);
            RotateIfNeeded();

            var version =
                Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion
                ?? Application.ProductVersion;

            WriteCore(
                $"application_start version={version} os={Environment.OSVersion.VersionString} " +
                $"framework={Environment.Version}");
        }
    }

    public static void Log(string message, Exception? exception = null)
    {
        try
        {
            lock (Gate)
            {
                if (!_initialized)
                    Initialize();

                WriteCore(message);
                if (exception is not null)
                    WriteCore(exception.ToString());
            }
        }
        catch
        {
            // Diagnostics must never crash the support application.
        }
    }

    public static void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            Process.Start(new ProcessStartInfo(LogDirectory)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log("open_log_folder_failed", ex);
        }
    }

    private static void WriteCore(string message)
    {
        var line =
            $"{DateTimeOffset.Now:O} [{Environment.ProcessId}] {message}{Environment.NewLine}";
        File.AppendAllText(LogPath, line, Encoding.UTF8);
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < 2 * 1024 * 1024)
                return;

            var previous = Path.Combine(LogDirectory, "technician.previous.log");
            if (File.Exists(previous))
                File.Delete(previous);

            File.Move(LogPath, previous);
        }
        catch
        {
            // Best effort only.
        }
    }
}
