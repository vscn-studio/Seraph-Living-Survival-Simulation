using Vintagestory.API.Client;

namespace Seraph.Client;

internal readonly record struct ScreenRegion(int X, int Y, int Width, int Height)
{
    public ScreenRegion Intersect(ScreenRegion other)
    {
        var x = Math.Max(X, other.X); var y = Math.Max(Y, other.Y);
        return new(x, y, Math.Max(0, Math.Min(X + Width, other.X + other.Width) - x),
            Math.Max(0, Math.Min(Y + Height, other.Y + other.Height) - y));
    }
    public bool Empty => Width <= 0 || Height <= 0;
}

/// <summary>GUI coordinates in physical pixels, origin top left. Only out-of-game native menus are exempt.</summary>
internal static class SymptomRegions
{
    internal static bool IsProtected(GuiDialog dialog) => dialog.GetType().FullName is
        "Vintagestory.Client.NoObf.GuiDialogEscapeMenu" or "Vintagestory.Client.NoObf.GuiDialogConfirmRemapping";
    internal static bool HasMenu(ICoreClientAPI api) => api.Gui?.OpenedGuis?.Any(IsProtected) == true;
    internal static ScreenRegion[] Menus(ICoreClientAPI api)
    {
        if (api.Gui?.OpenedGuis is not { } dialogs) return [];
        var result = new List<ScreenRegion>();
        foreach (var dialog in dialogs)
        {
            if (!IsProtected(dialog)) continue;
            foreach (var composer in dialog.Composers.Values)
            {
                if (!composer.Enabled || !composer.Composed) continue;
                var b = composer.Bounds;
                if (!double.IsFinite(b.renderX) || !double.IsFinite(b.renderY) || !double.IsFinite(b.OuterWidth) || !double.IsFinite(b.OuterHeight)) continue;
                var x = (int)Math.Floor(b.renderX) - 4; var y = (int)Math.Floor(b.renderY) - 4;
                result.Add(new(x, y, (int)Math.Ceiling(b.OuterWidth) + 8, (int)Math.Ceiling(b.OuterHeight) + 8));
            }
        }
        return result.ToArray();
    }
    internal static List<ScreenRegion> Outside(int width, int height, IReadOnlyList<ScreenRegion> menus)
    {
        var result = new List<ScreenRegion> { new(0, 0, width, height) };
        foreach (var menu in menus)
        {
            var next = new List<ScreenRegion>();
            foreach (var r in result)
            {
                var cut = r.Intersect(menu);
                if (cut.Empty) { next.Add(r); continue; }
                void Add(ScreenRegion rect) { if (!rect.Empty) next.Add(rect); }
                Add(new(r.X, r.Y, r.Width, cut.Y - r.Y));
                Add(new(r.X, cut.Y + cut.Height, r.Width, r.Y + r.Height - cut.Y - cut.Height));
                Add(new(r.X, cut.Y, cut.X - r.X, cut.Height));
                Add(new(cut.X + cut.Width, cut.Y, r.X + r.Width - cut.X - cut.Width, cut.Height));
            }
            result = next;
        }
        return result;
    }
}
