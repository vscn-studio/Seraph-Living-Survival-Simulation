using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace Seraph.Client;

internal sealed partial class SurvivalHud
{
    private static readonly string[] IconKeys = ["blood", "sleep", "defecation", "bcomplex-deficiency",
        "vitamina-deficiency", "vitaminc-deficiency", "bloodtoxicity", "bug", "soiled",
        "dehydration", "hypothermia", "heat-illness", "dysentery", "ketosis", "bone-weakness", "anemia", "darkadaptation"];
    // Resource names may change without changing the server status keys.
    private static readonly IReadOnlyDictionary<string, string> AssetNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["bug"] = "bug-icon",
        ["dysentery"] = "dysentery-icon",
        ["dehydration"] = "dehydration-icon",
        ["hypothermia"] = "cold",
        ["heat-illness"] = "hot",
        ["ketosis"] = "ketosis",
        ["bone-weakness"] = "bone-weakness",
        ["anemia"] = "anemia"
    };
    private readonly Dictionary<string, ImageSurface> statusArt = [];
    private readonly Dictionary<string, string> iconCache = [];
    private readonly List<StatusIconLayout.Obstacle> statusObstacles = new(32);
    private int activeIconMask, composedIconMask = -1, statusColumns = 2;
    private double statusAnimationTime;
    private const int DarkIconBit = 1 << 16;
    private int darkIconStage;
    private readonly DarkAdaptationSparkles darkSparkles = new(api);

    private void ComposeStatusIcons()
    {
        var count = System.Numerics.BitOperations.PopCount((uint)activeIconMask);
        var oldComposer = SingleComposer;
        // The native coordinates HUD moves below all RightTop composers (even hidden ones).
        // Manage our vertical position independently so avoidance only flows from us to it.
        var composer = capi.Gui.CreateCompo("vsslssmod-status-icons",
            ElementBounds.Fixed(EnumDialogArea.RightFixed, 0, 0, statusColumns * 54 - 6, Math.Max(1, Math.Ceiling(count / (double)statusColumns) * 54 - 6))
                .WithFixedAlignmentOffset(-StatusIconLayout.Margin, StatusIconLayout.DefaultTop));
        iconCache.Clear();
        var slot = 0;
        for (var i = 0; i < IconKeys.Length; i++)
        {
            if ((activeIconMask & (1 << i)) == 0) continue;
            var key = IconKeys[i];
            var bounds = ElementBounds.Fixed(slot % statusColumns * 54, slot / statusColumns * 54, 48, 48);
            slot++;
            iconCache[key] = IconHover(key);
            composer.AddDynamicCustomDraw(bounds, (ctx, _, b) => DrawStatusIcon(ctx, b, key), "icon-" + key)
                .AddHoverText(iconCache[key], CairoFont.WhiteDetailText(), 300, bounds.FlatCopy(), "icon-help-" + key);
        }
        SingleComposer = composer.Compose();
        SingleComposer.Enabled = count > 0;
        composedIconMask = activeIconMask;
        oldComposer?.Dispose();
    }

    private string IconHover(string key)
    {
        var title = LocalizedText.Get("vsslssmod:status-icon-" + key);
        if (key == "darkadaptation") return TooltipText.Panel(title + "\n"
            + LocalizedText.Get(darkIconStage == 2 ? "vsslssmod:darkadaptation-ready" : "vsslssmod:darkadaptation-adjusting"), CairoFont.WhiteDetailText());
        var icon = !isStale ? current?.StatusIcons.FirstOrDefault(i => i.Key == key) : null;
        // Clinical descriptions often repeat the help text; retain live values
        // only where the detail adds information such as severity or bite age.
        var hasLiveDetail = key is "sleep" or "defecation" or "bcomplex-deficiency"
            or "vitamina-deficiency" or "vitaminc-deficiency" or "bug";
        var detail = hasLiveDetail && icon is { Available: true, Detail.Length: > 0 } ? "\n" + icon.Detail : "";
        return TooltipText.Panel(title + detail + "\n" + TooltipText.Help("status-icon-help-" + key, "status-icon-" + key),
            CairoFont.WhiteDetailText());
    }

    private void DrawStatusIcon(Context ctx, ElementBounds bounds, string key)
    {
        var artKey = key == "darkadaptation" ? darkIconStage == 2 ? "dark-adaptation-open" : "dark-adaptation-close" : key;
        if (!statusArt.TryGetValue(artKey, out var surface))
        {
            var assetName = AssetNames.TryGetValue(key, out var mapped) ? mapped : key + "-icon";
            var author = key is "darkadaptation" or "ketosis" or "bone-weakness" or "anemia" ? "hansjack" : "ch4ver";
            var path = author + "/" + (key == "darkadaptation" ? artKey : assetName);
            statusArt[artKey] = surface = GuiElement.getImageSurfaceFromAsset(capi,
                new AssetLocation("vsslssmod:textures/gui/status/" + path + ".png"));
        }
        ctx.Save();
        ctx.Scale(bounds.InnerWidth / surface.Width, bounds.InnerHeight / surface.Height);
        ctx.SetSourceSurface(surface, 0, 0);
        ctx.Paint();
        if (key != "darkadaptation")
        {
            ctx.SetSourceRGBA(1, .36, .15, .95);
            ctx.LineWidth = 1.5; ctx.Rectangle(1, 1, surface.Width - 2, surface.Height - 2); ctx.Stroke();
        }
        ctx.Restore();
    }

    private void RefreshStatusIcons()
    {
        var mask = StatusIconLayout.ActiveMask(IconKeys, current?.StatusIcons, isStale);
        if (!isStale && darkIconStage != 0) mask |= DarkIconBit;
        if ((mask & ~activeIconMask) != 0) statusAnimationTime = 0;
        activeIconMask = mask;
        PositionStatusIcons(0);
        if (mask == 0) return;
        foreach (var key in IconKeys)
        {
            if (!iconCache.TryGetValue(key, out var previous)) continue;
            var next = IconHover(key);
            if (previous == next) continue;
            SingleComposer.GetHoverText("icon-help-" + key).SetNewText(next);
            iconCache[key] = next;
        }
    }

    internal void PositionStatusIcons(float deltaTime)
    {
        if (SingleComposer is null) return;
        var nextDark = isStale || !(fresh?.Invoke() ?? true) || current is null
            || current.IsSleeping || current.IsClosedEyeResting ? 0 : darkStage?.Invoke() ?? 0;
        if (darkIconStage != nextDark)
        {
            darkIconStage = nextDark;
            activeIconMask = (activeIconMask & ~DarkIconBit) | (nextDark > 0 ? DarkIconBit : 0);
            composedIconMask = -1; // Only threshold transitions rebuild the icon, never sparkle frames.
            darkSparkles.Reset();
        }
        var count = System.Numerics.BitOperations.PopCount((uint)activeIconMask);
        if (count == 0 || isStale || !(fresh?.Invoke() ?? true))
        { SingleComposer.Enabled = false; return; }
        var scale = Math.Max(.1, RuntimeEnv.GUIScale);
        statusObstacles.Clear();
        // Read actual visible bounds: supports map resizing/moving and other native HUD dialogs.
        foreach (var dialog in capi.Gui.OpenedGuis)
        {
            if (ReferenceEquals(dialog, this) || !dialog.IsOpened()
                || dialog.DialogType != EnumDialogType.HUD && dialog is not GuiDialogWorldMap) continue;
            var obstacle = dialog.SingleComposer;
            if (obstacle is not { Enabled: true }) continue;
            var b = obstacle.Bounds;
            statusObstacles.Add(new(b.renderX / scale, b.renderY / scale, b.OuterWidth / scale, b.OuterHeight / scale));
        }
        var placement = StatusIconLayout.Place(count, capi.Render.FrameWidth / scale, capi.Render.FrameHeight / scale, statusObstacles);
        if (composedIconMask != activeIconMask || statusColumns != placement.Columns)
        { statusColumns = Math.Max(1, placement.Columns); ComposeStatusIcons(); }
        SingleComposer.Enabled = placement.Visible;
        if (!placement.Visible) return;
        var bounds = SingleComposer.Bounds;
        var hovering = bounds.PointInside(capi.Input.MouseX, capi.Input.MouseY);
        var paused = capi.IsGamePaused;
        if (!paused && !hovering && float.IsFinite(deltaTime)) statusAnimationTime += Math.Clamp(deltaTime, 0, .1f);
        var offsetX = -StatusIconLayout.Margin + (paused || hovering ? 0 : StatusIconLayout.Shake(statusAnimationTime));
        if (bounds.fixedOffsetX == offsetX && bounds.fixedOffsetY == placement.Top) return;
        bounds.fixedOffsetX = offsetX; bounds.fixedOffsetY = placement.Top;
        // Move existing textures and their hitboxes together. Never redraw/recompose for animation.
        bounds.MarkDirtyRecursive(); bounds.CalcWorldBounds();
    }

    internal void ReleaseStatusArt()
    {
        darkSparkles.Dispose();
        foreach (var surface in statusArt.Values) surface.Dispose();
        statusArt.Clear(); iconCache.Clear();
    }

    internal void RenderDarkAdaptationSparkles(float dt)
    {
        if (darkIconStage != 2 || isStale || !(fresh?.Invoke() ?? true)
            || SingleComposer is not { Enabled: true } || !IsOpened()) return;
        if (SingleComposer.GetElement("icon-darkadaptation") is not { } icon) return;
        capi.Render.GlPushMatrix();
        try { capi.Render.GlTranslate(0, 0, -150); darkSparkles.Render(icon.Bounds, dt, capi.IsGamePaused); }
        finally { capi.Render.GlPopMatrix(); }
    }
}
