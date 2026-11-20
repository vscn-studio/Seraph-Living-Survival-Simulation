using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Seraph.Client;

internal sealed partial class CharacterPanelIntegration
{
    // The environment panel replaces a vanilla window, so it deliberately has no pin.
    private static readonly string[] PinnableKeys = [BodyKey, DetailsKey(Panel.BloodCirculation),
        DetailsKey(Panel.DiseaseRecovery), DetailsKey(Panel.Functions), DetailsKey(Panel.EnergyBalance),
        DetailsKey(Panel.Thermoregulation)];
    private readonly HashSet<string> pinnedPanels = new(StringComparer.Ordinal);
    private PinnedCharacterPanelsHud? pinnedHud;
    private GuiComposer? OwnedComposer(string key) => dialog is null ? null
        : ((GuiDialog)dialog).Composers[key] ?? retainedPanels.GetValueOrDefault(key);

    private bool IsPanelVisible(string key) => dialog is not null && (((GuiDialog)dialog).IsOpened() || pinnedPanels.Contains(key));
    private bool CanRenderPinnedPanels() => levelReady && registered && dialog is not null
        && !((GuiDialog)dialog).IsOpened() && pinnedPanels.Count > 0;

    private void AddPinnedTitleBar(GuiComposer composer, string key, string title)
    {
        if (dialog is null || !PinnableKeys.Contains(key, StringComparer.Ordinal)) return;
        composer.AddInteractiveElement(new PinnedPanelTitleBar(api, title, composer,
            dialog.OnTitleBarClose, () => pinnedPanels.Contains(key), () => TogglePinnedPanel(key)), "panel-title");
    }

    private void TogglePinnedPanel(string key)
    {
        // Never accept vanilla, third-party, or obsolete composer keys.
        if (!PinnableKeys.Contains(key, StringComparer.Ordinal)) return;
        if (!pinnedPanels.Remove(key)) pinnedPanels.Add(key);
        UpdatePinnedHud();
    }

    private void UpdatePinnedHud()
    {
        if (pinnedPanels.Count == 0) { pinnedHud?.TryClose(); return; }
        pinnedHud ??= new PinnedCharacterPanelsHud(api, CanRenderPinnedPanels,
            dt => RenderPinnedPanels(dt, false), dt => RenderPinnedPanels(dt, true));
        pinnedHud.TryOpen(false);
    }

    private void OnCharacterClosed()
    {
        if (dialog is null) return;
        foreach (var key in PinnableKeys)
            OwnedComposer(key)?.GetElement("panel-title")?.OnFocusLost();
        foreach (var key in OwnedKeys)
        {
            if (((GuiDialog)dialog).Composers[key] is not { } composer) continue;
            retainedPanels[key] = composer;
            ((GuiDialog)dialog).Composers.Remove(key);
        }
    }

    private void RenderPinnedPanels(float dt, bool finalize)
    {
        if (dialog is null) return;
        // Borrow live composers. Ownership, disposal, dragging and C-key editing stay
        // with the original dialog; reopening C therefore cannot duplicate a window.
        foreach (var key in PinnableKeys)
        {
            if (!pinnedPanels.Contains(key) || OwnedComposer(key) is not { } composer) continue;
            if (finalize) composer.PostRender(dt);
            else composer.Render(dt);
        }
    }
}

/// <summary>Read-only render pass; it never registers a hotkey or receives gameplay input.</summary>
internal sealed class PinnedCharacterPanelsHud(ICoreClientAPI api, Func<bool> visible,
    Action<float> render, Action<float> finalize) : HudElement(api)
{
    [ThreadStatic] internal static bool RenderingReadOnly;
    public override bool Focusable => false;
    public override bool PrefersUngrabbedMouse => false;
    public override bool DisableMouseGrab => false;
    public override bool UnregisterOnClose => true;
    public override bool ShouldReceiveKeyboardEvents() => false;
    public override bool ShouldReceiveMouseEvents() => false;
    public override bool CaptureAllInputs() => false;
    public override bool CaptureRawMouse() => false;
    // Below inventory and pause dialogs, above the world. Native dialogs retain priority.
    public override double DrawOrder => .19;

    public override void OnRenderGUI(float deltaTime)
    {
        // Match HudElement's native depth so inventories and modal dialogs stay above it.
        capi.Render.GlPushMatrix();
        try
        {
            capi.Render.GlTranslate(0, 0, -150);
            ReadOnlyPass(render, deltaTime);
        }
        finally { capi.Render.GlPopMatrix(); }
    }
    public override void OnFinalizeFrame(float dt) => ReadOnlyPass(finalize, dt);

    private void ReadOnlyPass(Action<float> draw, float dt)
    {
        if (!visible()) return;
        var previous = RenderingReadOnly;
        RenderingReadOnly = true;
        try { draw(dt); }
        finally { RenderingReadOnly = previous; }
    }
}

/// <summary>Keep the game's title bar/menu/drag behavior, with one local pin button.</summary>
internal sealed class PinnedPanelTitleBar : GuiElementDialogTitleBar
{
    private bool pinPressed;
    private readonly double originalFontSize;
    private readonly GuiComposer composer;
    private readonly Func<bool> pinned;
    private readonly Action toggle;

    internal PinnedPanelTitleBar(ICoreClientAPI api, string title, GuiComposer composer,
        Action close, Func<bool> pinned, Action toggle) : base(api, title, composer, close)
    {
        this.composer = composer;
        this.pinned = pinned;
        this.toggle = toggle;
        originalFontSize = Font.UnscaledFontsize;
    }

    internal static (double X, double Y, double Size) PinGeometry(double width, double height, double scale)
        => (width - 78 * scale, (height - 22 * scale) / 2, 22 * scale);

    private bool OverPin(MouseEvent args)
    {
        var (x, y, size) = PinGeometry(Bounds.OuterWidth, Bounds.InnerHeight, RuntimeEnv.GUIScale);
        return args.X >= Bounds.absX + x && args.X < Bounds.absX + x + size
            && args.Y >= Bounds.absY + y && args.Y < Bounds.absY + y + size;
    }

    public override void ComposeTextElements(Context ctx, ImageSurface surface)
    {
        Font.UnscaledFontsize = originalFontSize;
        var available = Math.Max(1, Bounds.OuterWidth - scaled(94));
        var textWidth = Font.GetTextExtents(text).XAdvance;
        if (textWidth > available) Font.UnscaledFontsize = Math.Max(9, originalFontSize * available / textWidth);
        base.ComposeTextElements(ctx, surface);
        var (x, y, size) = PinGeometry(Bounds.OuterWidth, Bounds.InnerHeight, RuntimeEnv.GUIScale);
        DrawPin(ctx, Bounds.drawX + x, Bounds.drawY + y, size, pinned());
    }

    internal static void DrawPin(Context ctx, double x, double y, double size, bool pinned)
    {
        ctx.Save();
        try
        {
            ctx.Translate(x + size / 2, y + size / 2);
            ctx.Scale(size / 22, size / 22);
            if (pinned) ctx.Rotate(-Math.PI / 4);
            ctx.SetSourceRGBA(pinned ? .95 : .9, pinned ? .16 : .86, pinned ? .12 : .78, 1);
            // Flat head, narrow neck, wide shoulder and a pointed needle.
            ctx.MoveTo(-5, -8); ctx.LineTo(5, -8); ctx.LineTo(5, -5);
            ctx.LineTo(3, -4); ctx.LineTo(3, 0); ctx.LineTo(6, 3);
            ctx.LineTo(1, 3); ctx.LineTo(0, 9); ctx.LineTo(-1, 3);
            ctx.LineTo(-6, 3); ctx.LineTo(-3, 0); ctx.LineTo(-3, -4);
            ctx.LineTo(-5, -5); ctx.ClosePath(); ctx.Fill();
        }
        finally { ctx.Restore(); }
    }

    public override void OnMouseDown(ICoreClientAPI client, MouseEvent args)
    {
        pinPressed = false;
        if (OverPin(args))
        {
            pinPressed = args.Button == EnumMouseButton.Left;
            args.Handled = true;
            return;
        }
        base.OnMouseDown(client, args);
    }

    public override void OnMouseUp(ICoreClientAPI client, MouseEvent args)
    {
        var pressed = pinPressed;
        pinPressed = false;
        if (pressed || OverPin(args))
        {
            args.Handled = true;
            if (pressed && OverPin(args) && args.Button == EnumMouseButton.Left)
            {
                toggle();
                composer.ReCompose();
            }
            return;
        }
        base.OnMouseUp(client, args);
    }

    public override void OnFocusLost() { pinPressed = false; base.OnFocusLost(); }
    public override void RenderInteractiveElements(float dt)
    {
        if (!PinnedCharacterPanelsHud.RenderingReadOnly) base.RenderInteractiveElements(dt);
    }
}

internal sealed class CharacterPanelHoverText(ICoreClientAPI api, string source, CairoFont font,
    int width, ElementBounds bounds) : GuiElementHoverText(api, "", font, width, bounds)
{
    private string pendingSource = source;
    private string? composedSource;
    private float composedScale;

    internal void SetSource(string value) => pendingSource = value;

    public override void RenderInteractiveElements(float dt)
    {
        if (PinnedCharacterPanelsHud.RenderingReadOnly) return;
        // Native SetNewText eagerly measures rich text, blurs a background and
        // uploads textures, even for a hidden tooltip. Only compose the hovered
        // row, using the latest source; intermediate snapshots need no Cairo work.
        if (pendingSource.Length == 0) { text = ""; return; }
        if ((IsVisible || IsPositionInside(api.Input.MouseX, api.Input.MouseY))
            && (text.Length == 0 || composedSource != pendingSource || composedScale != RuntimeEnv.GUIScale))
        {
            base.SetNewText(TooltipText.Panel(pendingSource, Font));
            composedSource = pendingSource;
            composedScale = RuntimeEnv.GUIScale;
        }
        base.RenderInteractiveElements(dt);
    }
}

internal static class CharacterPanelHoverExtensions
{
    internal static void SetCharacterPanelHoverText(this GuiComposer composer, string key, string source)
        => ((CharacterPanelHoverText)composer.GetElement(key)).SetSource(source);

    internal static GuiComposer AddCharacterPanelHoverText(this GuiComposer composer, string text, CairoFont font,
        int width, ElementBounds bounds, string key)
        => composer.AddInteractiveElement(new CharacterPanelHoverText(composer.Api, text, font, width, bounds), key);
}
