using System.Text.Json;

namespace InnAwareSupport.Technician;

internal static class WindowPlacementStore
{
    private sealed record Placement(
        int X,
        int Y,
        int Width,
        int Height,
        bool Maximized);

    private static string SettingsPath => Path.Combine(
        AppDiagnostics.BaseDirectory,
        "window.json");

    public static void Apply(Form form)
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return;

            var placement = JsonSerializer.Deserialize<Placement>(
                File.ReadAllText(SettingsPath));

            if (placement is null ||
                placement.Width < form.MinimumSize.Width ||
                placement.Height < form.MinimumSize.Height)
                return;

            var bounds = new Rectangle(
                placement.X,
                placement.Y,
                placement.Width,
                placement.Height);

            if (!Screen.AllScreens.Any(screen =>
                    Rectangle.Intersect(bounds, screen.WorkingArea).Width >= 200 &&
                    Rectangle.Intersect(bounds, screen.WorkingArea).Height >= 120))
                return;

            form.StartPosition = FormStartPosition.Manual;
            form.Bounds = bounds;

            if (placement.Maximized)
                form.WindowState = FormWindowState.Maximized;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Log("window_placement_apply_failed", ex);
        }
    }

    public static void Save(Form form)
    {
        try
        {
            Directory.CreateDirectory(AppDiagnostics.BaseDirectory);

            var bounds =
                form.WindowState == FormWindowState.Normal
                    ? form.Bounds
                    : form.RestoreBounds;

            var placement = new Placement(
                bounds.X,
                bounds.Y,
                Math.Max(bounds.Width, form.MinimumSize.Width),
                Math.Max(bounds.Height, form.MinimumSize.Height),
                form.WindowState == FormWindowState.Maximized);

            File.WriteAllText(
                SettingsPath,
                JsonSerializer.Serialize(
                    placement,
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            AppDiagnostics.Log("window_placement_save_failed", ex);
        }
    }
}
