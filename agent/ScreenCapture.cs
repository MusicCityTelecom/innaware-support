using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace InnAwareSupport.Agent;

internal static class ScreenCapture
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize; public int flags; public nint hCursor; public POINT ptScreenPos; }
    private const int CURSOR_SHOWING = 0x00000001;

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern bool DrawIcon(nint hDC, int X, int Y, nint hIcon);

    public static byte[] CaptureJpeg(long quality = 48L)
    {
        var bounds = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            DrawCursor(graphics, bounds);
        }
        using var stream = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, Math.Clamp(quality, 20L, 90L));
        bitmap.Save(stream, encoder, parameters);
        return stream.ToArray();
    }

    private static void DrawCursor(Graphics graphics, Rectangle bounds)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || ci.flags != CURSOR_SHOWING || ci.hCursor == 0) return;
        var hdc = graphics.GetHdc();
        try { DrawIcon(hdc, ci.ptScreenPos.X - bounds.Left, ci.ptScreenPos.Y - bounds.Top, ci.hCursor); }
        finally { graphics.ReleaseHdc(hdc); }
    }
}
