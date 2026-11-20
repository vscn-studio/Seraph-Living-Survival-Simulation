using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace Seraph.Client;

/// <summary>Passive, fixed silhouette; details appear only under the released cursor.</summary>
internal sealed class BodyStatusHud(ICoreClientAPI api) : HudElement(api)
{
    internal const double Width = 112, Height = 280, LeftMargin = 10;
    private ServerSnapshot? current;
    private string? visualKey, hoverText;
    private float hoverScale;
    private bool available;
    private bool? visibilityOverride;
    private GuiElementHoverText? hover;
    public override bool Focusable => false;
    public override bool PrefersUngrabbedMouse => false;
    public override bool DisableMouseGrab => false;
    public override bool ShouldReceiveKeyboardEvents() => false;
    public override bool ShouldReceiveMouseEvents() => false;
    public override double DrawOrder => .19;

    public void ToggleVisibility()
    {
        visibilityOverride = !(visibilityOverride ?? IsVisibleByDefault());
        Refresh();
    }

    public void Update(ServerSnapshot? snapshot, bool stale)
    {
        current = stale ? null : snapshot;
        available = snapshot is not null;
        Refresh();
    }

    private bool IsVisibleByDefault() => current?.BodyRegions.Any(r => r.Wound || r.Pain
        || double.IsFinite(r.Integrity) && double.IsFinite(r.Maximum) && r.Integrity < r.Maximum - .001) == true;

    private void Refresh()
    {
        if (!available || !(visibilityOverride ?? IsVisibleByDefault())) { hover?.SetVisible(false); TryClose(); return; }
        if (SingleComposer is null) Compose();
        var key = BodyRenderer.VisualKey(current);
        if (key != visualKey)
        {
            SingleComposer.GetCustomDraw("body").Redraw();
            visualKey = key;
        }
        if (!IsOpened()) TryOpen(false);
    }

    private void Compose()
    {
        var bounds = ElementBounds.Fixed(EnumDialogArea.LeftMiddle, 0, 0, Width, Height)
            .WithFixedAlignmentOffset(LeftMargin, 0);
        var composer = capi.Gui.CreateCompo("vsslssmod-body-hud", bounds);
        composer.AddDynamicCustomDraw(ElementBounds.Fixed(0, 0, Width, Height),
            (ctx, _, body) => BodyRenderer.Draw(capi, ctx, body, current, true), "body");
        hover = new(capi, "", CairoFont.WhiteDetailText(), 260, ElementBounds.Fixed(0, 0, Width, Height));
        hover.SetAutoDisplay(false);
        composer.AddInteractiveElement(hover, "body-help");
        SingleComposer = composer.Compose();
        visualKey = BodyRenderer.VisualKey(current);
    }

    internal void RefreshHover()
    {
        if (hover is null) return;
        var region = IsOpened() && !capi.Input.MouseGrabbed
            ? BodyRenderer.HitTest(capi, SingleComposer.GetCustomDraw("body").Bounds, capi.Input.MouseX, capi.Input.MouseY) : null;
        hover.SetVisible(region is not null);
        if (region is null) return;
        var text = RegionTooltip(region, current?.BodyRegions.FirstOrDefault(r => r.Region == region));
        if (text == hoverText && hoverScale == RuntimeEnv.GUIScale) return;
        hover.SetNewText(text);
        hoverText = text; hoverScale = RuntimeEnv.GUIScale;
    }

    internal static string RegionTooltip(string region, BodyRegionSnapshot? state)
    {
        var title = Lang.Get("vsslssmod:body-region-" + region);
        var stage = BodyRenderer.Stage(state);
        var status = Lang.Get("vsslssmod:body-stage-" + stage.ToString().ToLowerInvariant());
        if (stage == BodyStage.Unknown) return title + "\n" + status;
        return title + "\n" + status + "\n" + Lang.Get("vsslssmod:body-hover-integrity",
            state!.Integrity.ToString("0.##"), state.Maximum.ToString("0.##"))
            + "\n" + Lang.Get(state.Wound ? "vsslssmod:body-hover-wounded" : "vsslssmod:body-hover-unwounded")
            + "\n" + Lang.Get(state.Pain ? "vsslssmod:body-hover-pain" : "vsslssmod:body-hover-no-pain");
    }

    public override void OnRenderGUI(float deltaTime)
    {
        RefreshHover();
        base.OnRenderGUI(deltaTime);
    }

    public override void Dispose() { TryClose(); base.Dispose(); }
}
