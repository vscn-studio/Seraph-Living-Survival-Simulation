using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace Seraph.Client;

/// <summary>Three small white glints, kept inside the icon cell and drawn with one cached texture.</summary>
internal sealed class DarkAdaptationSparkles(ICoreClientAPI api) : IDisposable
{
    private LoadedTexture? texture;
    private readonly Vec4f tint = new(1, 1, 1, 1);
    private double time;
    internal void Reset() => time = 0;

    internal void Render(ElementBounds bounds, float dt, bool paused)
    {
        if (!paused && float.IsFinite(dt)) time = (time + Math.Clamp(dt, 0, .1f)) % 12;
        if (texture is null)
        {
            using var surface = new ImageSurface(Format.Argb32, 16, 16);
            using var ctx = new Context(surface);
            // A soft halo and narrow cross remain readable at small GUI scales.
            for (var radius = 6; radius >= 1; radius--)
            {
                ctx.SetSourceRGBA(1, 1, 1, .045 + (6 - radius) * .025);
                ctx.Arc(8, 8, radius, 0, Math.PI * 2); ctx.Fill();
            }
            ctx.SetSourceRGBA(1, 1, 1, .85); ctx.LineWidth = 1;
            ctx.MoveTo(8, 3); ctx.LineTo(8, 13); ctx.MoveTo(3, 8); ctx.LineTo(13, 8); ctx.Stroke();
            CairoTextureUpload.Update(api, surface, ref texture);
        }
        for (var i = 0; i < 3; i++)
        {
            var phase = (time + i * .8) % 2.4;
            var pulse = phase >= 1.1 ? 0 : Math.Pow(Math.Sin(phase / 1.1 * Math.PI), 2);
            if (pulse <= .001) continue;
            var x = i switch { 0 => .13, 1 => .87, _ => .72 };
            var y = i switch { 0 => .18, 1 => .48, _ => .87 };
            var size = bounds.InnerWidth * (.08 + .055 * pulse);
            tint.R = tint.G = tint.B = tint.A = (float)(.8 * pulse);
            api.Render.Render2DTexturePremultipliedAlpha(texture!.TextureId,
                bounds.renderX + x * bounds.InnerWidth - size / 2,
                bounds.renderY + y * bounds.InnerHeight - size / 2, size, size, 62, tint);
        }
    }

    public void Dispose() { texture?.Dispose(); texture = null; }
}
