using Cairo;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace Seraph.Client;

/// <summary>Cached numeric sprites; animation changes only geometry and opacity.</summary>
internal sealed class AdrenalineHud : HudElement
{
    private readonly Func<ServerSnapshot?> snapshot;
    private readonly AdrenalineAudio audio;
    private readonly LoadedTexture?[] sharp = new LoadedTexture?[61], blurred = new LoadedTexture?[61];
    private readonly Vec4f color = new(1, 1, 1, 1);
    private readonly long preload;
    private int prepared = 1;
    private ServerSnapshot? previous;
    private double remaining;
    private LoadedTexture? edges;
    public override double DrawOrder => .2;
    public override bool Focusable => false;
    public override bool PrefersUngrabbedMouse => false;
    public override bool DisableMouseGrab => false;
    public override bool ShouldReceiveKeyboardEvents() => false;
    public override bool ShouldReceiveMouseEvents() => false;
    public override bool CaptureAllInputs() => false;
    public override bool TryClose() => false;

    internal AdrenalineHud(ICoreClientAPI api, Func<ServerSnapshot?> snapshot) : base(api)
    {
        this.snapshot = snapshot; audio = new(api, snapshot);
        SingleComposer = api.Gui.CreateCompo("vsslssmod-adrenaline", ElementBounds.Fixed(0, 0, 1, 1)).Compose();
        // Spread the one-time texture work over loading ticks, never over render frames.
        preload = api.Event.RegisterGameTickListener(_ =>
        {
            if (api.World.Player is null || prepared > 60) return;
            Prepare(prepared++);
        }, 50);
        TryOpen();
    }

    internal static double Remaining(double current, ServerSnapshot? state, ServerSnapshot? previous, double dt, bool paused)
    {
        if (state is null) return 0;
        var advance = paused || !double.IsFinite(dt) ? 0 : Math.Clamp(dt, 0, .25);
        if (ReferenceEquals(state, previous)) return Math.Max(0, current - advance);
        var received = double.IsFinite(state.Adrenaline) ? Math.Clamp(state.Adrenaline, 0, 1) * 60 : 0;
        if (previous is null || previous.SessionEpoch != state.SessionEpoch || previous.LifeGeneration != state.LifeGeneration
            || previous.Adrenaline <= 0) return received;
        return Math.Min(Math.Max(0, current), received); // A snapshot cannot restart a numeral animation.
    }

    private void Prepare(int number)
    {
        using var surface = new ImageSurface(Format.Argb32, 192, 128);
        using var ctx = new Context(surface);
        AdrenalineDisplay.DrawNumber(ctx, number, false);
        CairoTextureUpload.Update(capi, surface, ref sharp[number]);
        ctx.Operator = Operator.Clear; ctx.Paint(); ctx.Operator = Operator.Over;
        AdrenalineDisplay.DrawNumber(ctx, number, true);
        CairoTextureUpload.Update(capi, surface, ref blurred[number]);
        if (edges is not null) return;
        using var edgeSurface = new ImageSurface(Format.Argb32, 256, 256);
        using var edgeContext = new Context(edgeSurface);
        AdrenalineDisplay.DrawEdges(edgeContext, 256, 256);
        CairoTextureUpload.Update(capi, edgeSurface, ref edges);
    }

    public override void OnRenderGUI(float deltaTime)
    {
        var state = snapshot();
        remaining = Remaining(remaining, state, previous, deltaTime, capi.IsGamePaused); previous = state;
        if (remaining <= 0 || capi.World.Player?.Entity.Alive != true || state?.IsClosedEyeResting == true) return;
        var width = capi.Render.FrameWidth; var height = capi.Render.FrameHeight;
        if (width <= 0 || height <= 0) return;
        capi.Render.GlToggleBlend(true);
        if (edges is not null)
        {
            color.A = (float)(remaining / 60);
            capi.Render.Render2DTexture(edges.TextureId, 0, 0, width, height, 50, color);
        }
        if (SymptomRegions.HasMenu(capi)) return;
        var number = Math.Clamp((int)Math.Ceiling(remaining), 1, 60);
        if (sharp[number] is null || blurred[number] is null) return;
        var phase = Math.Clamp(number - remaining, 0, 1);
        var opacity = Math.Min(1, phase / .15) * (1 - Math.Clamp((phase - .6) / .4, 0, 1));
        var blur = Math.Clamp((phase - .6) / .4, 0, 1);
        var scale = Math.Min(Math.Max(.1, RuntimeEnv.GUIScale), Math.Min(width / 300d, height / 220d));
        var size = scale * (.75 + .5 * phase);
        var w = 192 * size; var h = 128 * size;
        var top = 48 * scale + (128 * scale - h) / 2;
        color.A = (float)(opacity * (1 - blur));
        capi.Render.Render2DTexture(sharp[number]!.TextureId, (float)((width - w) / 2), (float)top, (float)w, (float)h, 51, color);
        color.A = (float)(opacity * blur);
        capi.Render.Render2DTexture(blurred[number]!.TextureId, (float)((width - w) / 2), (float)top, (float)w, (float)h, 51, color);
    }

    internal void Reset() { remaining = 0; previous = null; audio.Reset(); }
    public override void Dispose()
    {
        capi.Event.UnregisterGameTickListener(preload); audio.Dispose();
        edges?.Dispose(); foreach (var texture in sharp) texture?.Dispose(); foreach (var texture in blurred) texture?.Dispose();
        base.Dispose();
    }
}

internal static class AdrenalineDisplay
{
    internal static void DrawNumber(Context ctx, int number, bool blur)
    {
        ctx.SelectFontFace("sans-serif", FontSlant.Normal, FontWeight.Bold); ctx.SetFontSize(76);
        var text = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var extents = ctx.TextExtents(text);
        var left = 96 - extents.Width / 2 - extents.XBearing;
        var top = 64 - extents.Height / 2 - extents.YBearing;
        if (!blur)
        {
            ctx.MoveTo(left, top); ctx.TextPath(text);
            ctx.SetSourceRGBA(.12, .07, 0, .8); ctx.LineWidth = 2.5; ctx.StrokePreserve();
            ctx.SetSourceRGB(1, .83, .12); ctx.Fill(); return;
        }
        // Precomputed Gaussian-like soft glyph; no blur shader or scene copy is needed.
        for (var x = -6; x <= 6; x += 3)
        for (var y = -6; y <= 6; y += 3)
        {
            ctx.SetSourceRGBA(1, .83, .12, .12 * Math.Exp(-(x * x + y * y) / 36d));
            ctx.MoveTo(left + x, top + y); ctx.ShowText(text);
        }
    }

    internal static void DrawEdges(Context ctx, double width, double height)
    {
        // Four soft edges leave the central view clear. Cairo supplies premultiplied alpha.
        Edge(0, 0, width * .18, 0, 0, 0, width * .18, height);
        Edge(width, 0, width * .82, 0, width * .82, 0, width * .18, height);
        Edge(0, 0, 0, height * .18, 0, 0, width, height * .18);
        Edge(0, height, 0, height * .82, 0, height * .82, width, height * .18);
        void Edge(double x0, double y0, double x1, double y1, double x, double y, double w, double h)
        {
            using var gradient = new LinearGradient(x0, y0, x1, y1);
            gradient.AddColorStop(0, new Color(1, .66, .04, .65));
            gradient.AddColorStop(.45, new Color(1, .66, .04, .22));
            gradient.AddColorStop(1, new Color(1, .66, .04, 0));
            ctx.SetSource(gradient); ctx.Rectangle(x, y, w, h); ctx.Fill();
        }
    }
}
