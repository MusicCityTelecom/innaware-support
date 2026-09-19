using System.Text.Json;

namespace InnAwareSupport.Agent;

internal sealed record ElevationHandoff(
    string Server,
    string SessionId,
    string AgentToken,
    string WebSocketUrl,
    DateTime LiveExpiresAtUtc,
    bool RequestedControl,
    bool RequestedClipboard,
    bool RequestedFileTransfer)
{
    public static string Write(ElevationHandoff handoff)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InnAware",
            "Support");

        Directory.CreateDirectory(root);

        var path = Path.Combine(
            root,
            $"elevation-{Guid.NewGuid():N}.json");

        var json = JsonSerializer.Serialize(handoff);
        File.WriteAllText(path, json, Encoding.UTF8);
        File.SetAttributes(path, FileAttributes.Hidden);
        return path;
    }

    public static ElevationHandoff ReadAndDelete(string path)
    {
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<ElevationHandoff>(json)
                ?? throw new InvalidOperationException("Elevation handoff is empty.");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
