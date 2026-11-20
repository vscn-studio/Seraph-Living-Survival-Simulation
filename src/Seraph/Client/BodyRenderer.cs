using Cairo;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Seraph.Client;

internal enum BodyStage { Unknown, Healthy, Mild, Moderate, Severe, Critical, Depleted }

internal static class BodyRenderer
{
    private const string AssetRoot = "vsslssmod:textures/gui/body-player/";
    private static readonly ConditionalWeakTable<ICoreClientAPI, Artwork> Artworks = new();

    private sealed class Artwork : IDisposable
    {
        public readonly List<(string Region, ImageSurface Mask, byte[] Pixels)> Regions = [];
        public ImageSurface Outline { get; private set; } = null!;

        public Artwork(ICoreClientAPI api)
        {
            try
            {
                using var manifest = JsonDocument.Parse(api.Assets.Get(new AssetLocation(AssetRoot + "body-regions.json")).ToText());
                foreach (var region in manifest.RootElement.GetProperty("views").GetProperty("front").EnumerateArray())
                {
                    var mask = GuiElement.getImageSurfaceFromAsset(api, new AssetLocation(AssetRoot + region.GetProperty("mask").GetString()));
                    mask.Flush();
                    Regions.Add((region.GetProperty("id").GetString()!, mask, mask.Data));
                }
                Outline = GuiElement.getImageSurfaceFromAsset(api, new AssetLocation(AssetRoot + "player-front-outline-white.png"));
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            foreach (var region in Regions) region.Mask.Dispose();
            Outline?.Dispose();
        }
    }

    public static void Release(ICoreClientAPI api)
    {
        if (!Artworks.TryGetValue(api, out var artwork)) return;
        Artworks.Remove(api);
        artwork.Dispose();
    }

    // Color stages only: exact values and symptoms refresh the hovered text, not the silhouette texture.
    internal static string VisualKey(ServerSnapshot? snapshot) => snapshot is null ? "unknown"
        : string.Join("|", snapshot.BodyRegions.Select(r => $"{r.Region}:{Stage(r)}"));

    internal static BodyStage Stage(BodyRegionSnapshot? state)
    {
        if (state is null || !double.IsFinite(state.Integrity) || !double.IsFinite(state.Maximum)
            || state.Maximum <= 0 || state.Integrity < 0 || state.Integrity > state.Maximum) return BodyStage.Unknown;
        var fraction = state.Integrity / state.Maximum;
        // Visual thresholds do not change injury, healing, or body-function calculations.
        return fraction switch {
            <= 0 => BodyStage.Depleted, <= .25 => BodyStage.Critical,
            <= .5 => BodyStage.Severe, <= .75 => BodyStage.Moderate,
            _ => fraction < 1 || state.Wound || state.Pain ? BodyStage.Mild : BodyStage.Healthy
        };
    }

    internal static (double R, double G, double B, double A) Color(BodyStage stage) => stage switch {
        BodyStage.Healthy => (.12, .70, .32, .8),
        BodyStage.Mild => (.92, .8, .18, .85),
        BodyStage.Moderate => (.95, .55, .12, .85),
        BodyStage.Severe => (.93, .28, .1, .9),
        BodyStage.Critical => (.8, .06, .08, .9),
        BodyStage.Depleted => (.30, .03, .05, .9),
        _ => (.4, .43, .43, .65)
    };

    internal static string? HitTest(ICoreClientAPI api, ElementBounds bounds, double mouseX, double mouseY)
    {
        var scale = Math.Min(bounds.InnerWidth / 256, bounds.InnerHeight / 640);
        if (scale <= 0) return null;
        var x = (mouseX - bounds.renderX - (bounds.InnerWidth - 256 * scale) / 2) / scale;
        var y = (mouseY - bounds.renderY - (bounds.InnerHeight - 640 * scale) / 2) / scale;
        if (x < 0 || y < 0 || x >= 256 || y >= 640) return null;
        // Sample cached alpha masks, not rectangular limb bounds. No image reads,
        // Cairo allocations or GPU downloads occur during mouse movement.
        foreach (var (region, mask, pixels) in Artworks.GetValue(api, client => new Artwork(client)).Regions)
        {
            var px = (int)(x * mask.Width / 256); var py = (int)(y * mask.Height / 640);
            if (pixels[py * mask.Stride + px * 4 + (BitConverter.IsLittleEndian ? 3 : 0)] >= 128) return region;
        }
        return null;
    }

    public static void Draw(ICoreClientAPI api, Context ctx, ElementBounds bounds, ServerSnapshot? snapshot, bool local)
    {
        var width = bounds.InnerWidth; var height = bounds.InnerHeight;
        var scale = Math.Min(width / 256, height / 640);
        var artwork = Artworks.GetValue(api, client => new Artwork(client));
        ctx.Save();
        try
        {
            ctx.Rectangle(local ? 0 : bounds.drawX, local ? 0 : bounds.drawY, width, height); ctx.Clip();
            ctx.Translate((local ? 0 : bounds.drawX) + (width - 256 * scale) / 2,
                (local ? 0 : bounds.drawY) + (height - 640 * scale) / 2);
            ctx.Scale(scale, scale);
            foreach (var (region, mask, _) in artwork.Regions)
            {
                var state = snapshot?.BodyRegions.FirstOrDefault(r => r.Region == region);
                var color = Color(Stage(state));
                ctx.SetSourceRGBA(color.R, color.G, color.B, color.A);
                ctx.Save(); ctx.Scale(256d / mask.Width, 640d / mask.Height);
                ctx.MaskSurface(mask, 0, 0); ctx.Restore();
            }
            var outline = artwork.Outline;
            ctx.Save(); ctx.Scale(256d / outline.Width, 640d / outline.Height);
            ctx.SetSourceSurface(outline, 0, 0); ctx.Paint(); ctx.Restore();
        }
        finally { ctx.Restore(); }
    }
}
