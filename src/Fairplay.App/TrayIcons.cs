using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Fairplay.App;

/// <summary>Draws the tray glyph (concentric arcs over a triangle) at runtime, in a few states.</summary>
static class TrayIcons
{
    public enum Kind { Idle, Streaming, Busy }

    static readonly Dictionary<Kind, Icon> Cache = new();

    public static Icon Get(Kind kind)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(kind, out var icon)) Cache[kind] = icon = Draw(kind);
            return icon;
        }
    }

    static bool TaskbarIsLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    static Icon Draw(Kind kind)
    {
        int size = SystemInformation.SmallIconSize.Width * 2; // draw large, Windows scales down cleanly
        size = Math.Max(32, size);
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var baseColor = TaskbarIsLight() ? Color.FromArgb(32, 32, 32) : Color.FromArgb(240, 240, 240);
            var color = kind switch
            {
                Kind.Streaming => Color.FromArgb(10, 132, 255),
                Kind.Busy => Color.FromArgb(255, 159, 10),
                _ => baseColor,
            };
            float s = size / 32f;
            using var pen = new Pen(color, 2.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var c = new PointF(16 * s, 17 * s);
            foreach (var r in new[] { 5.5f, 10f, 14.5f })
            {
                if (kind == Kind.Idle && r > 14) pen.Color = Color.FromArgb(150, color);
                g.DrawArc(pen, c.X - r * s, c.Y - r * s, 2 * r * s, 2 * r * s, 205, 130);
            }
            using var brush = new SolidBrush(color);
            g.FillPolygon(brush, new[]
            {
                new PointF(16 * s, 18 * s),
                new PointF(24.5f * s, 30.5f * s),
                new PointF(7.5f * s, 30.5f * s),
            });
        }
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
}
