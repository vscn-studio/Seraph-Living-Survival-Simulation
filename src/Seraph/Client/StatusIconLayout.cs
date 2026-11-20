using Seraph.Network;

namespace Seraph.Client;

internal static class StatusIconLayout
{
    internal const double Size = 48, Pitch = 54, Margin = 14, Gap = 10, DefaultTop = 80;
    internal readonly record struct Obstacle(double X, double Y, double Width, double Height);
    internal readonly record struct Placement(int Columns, double Width, double Height, double Top, bool Visible);

    internal static int ActiveMask(string[] keys, StatusIconSnapshot[]? icons, bool stale)
    {
        if (stale || icons is null) return 0;
        var mask = 0;
        foreach (var icon in icons)
        {
            if (icon is not { Available: true, Active: true }) continue;
            var index = Array.IndexOf(keys, icon.Key);
            if (index >= 0) mask |= 1 << index;
        }
        return mask;
    }

    // All coordinates are GUI units, not physical pixels. Widen a crowded strip before clipping it.
    internal static Placement Place(int count, double screenWidth, double screenHeight, List<Obstacle> obstacles)
    {
        if (count <= 0 || !double.IsFinite(screenWidth) || !double.IsFinite(screenHeight)) return default;
        for (var columns = Math.Min(2, count); columns <= count; columns++)
        {
            var width = columns * Pitch - (Pitch - Size);
            if (width + Margin * 2 > screenWidth) break;
            var height = Math.Ceiling(count / (double)columns) * Pitch - (Pitch - Size);
            var left = screenWidth - Margin - width;
            var top = DefaultTop;
            // Repeat to account for stacked HUDs irrespective of enumeration order.
            for (var pass = 0; pass <= obstacles.Count; pass++)
            {
                var previous = top;
                foreach (var obstacle in obstacles)
                {
                    if (!double.IsFinite(obstacle.X + obstacle.Y + obstacle.Width + obstacle.Height)
                        || obstacle.Width <= 0 || obstacle.Height <= 0) continue;
                    if (left - 2 < obstacle.X + obstacle.Width && screenWidth - Margin + 2 > obstacle.X
                        && top < obstacle.Y + obstacle.Height + Gap && top + height > obstacle.Y - Gap)
                        top = obstacle.Y + obstacle.Height + Gap;
                }
                if (top == previous) break;
            }
            if (top + height <= screenHeight - Margin) return new(columns, width, height, top, true);
        }
        // A full map/menu may cover the whole available region. Do not draw behind it or offscreen.
        return new(Math.Min(2, count), Math.Min(2, count) * Pitch - 6, Math.Ceiling(count / 2d) * Pitch - 6, DefaultTop, false);
    }

    internal static double Shake(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return 0;
        var phase = seconds % 5;
        return phase >= .9 ? 0 : 2 * Math.Sin(phase / .9 * Math.PI) * Math.Sin(phase * Math.PI * 8);
    }
}
