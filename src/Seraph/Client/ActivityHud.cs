using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace Seraph.Client;

/// <summary>Opt-in debug projection of the server's accepted settlement ledger.</summary>
internal sealed class ActivityHud(ICoreClientAPI api) : HudElement(api)
{
    public override bool Focusable => false;
    public override bool ShouldReceiveKeyboardEvents() => false;
    public override bool ShouldReceiveMouseEvents() => false;
    public override double InputOrder => 1;
    private double lastWidth;
    private double lastScale;
    private string? lastText;
    private bool debugEnabled;
    internal bool ToggleDebug()
    {
        debugEnabled = !debugEnabled;
        if (!debugEnabled) TryClose();
        return debugEnabled;
    }
    internal void ResetDebug() { debugEnabled = false; TryClose(); }
    internal static ElementBounds CreateBounds(double logicalWidth)
    {
        // Native 1.22.7 hotbar (including the offhand slot) spans +/-425 units.
        // Put the ledger 16 units to its left if both fit, otherwise above its left edge.
        const double width = 255, halfHotbar = 425, gap = 16, margin = 12;
        var wide = logicalWidth >= 2 * (halfHotbar + gap + width + margin);
        var x = wide ? -halfHotbar - gap - width / 2
            : Math.Max(-halfHotbar + width / 2, margin + width / 2 - logicalWidth / 2);
        return ElementBounds.Fixed(EnumDialogArea.CenterBottom, x, wide ? -12 : -140, width, 80);
    }
    internal static CairoFont LedgerFont() => CairoFont.WhiteDetailText().WithFontSize(13);
    internal static string FormatLedger(ActivityLedger ledger, string activity, string template)
        => string.Format(template, activity, ledger.DemandMultiplier.ToString("0.0"),
            ledger.PaidKcalPerGameHour.ToString("0"), (ledger.StaminaPerSecond * 100).ToString("+0.0;-0.0;0.0"),
            (ledger.OxygenPerSecond * 100).ToString("+0.0;-0.0;0.0"), (ledger.FatiguePerGameHour * 100).ToString("+0.00;-0.00;0.00"));
    public void Update(ActivityLedger? ledger)
    {
        if (!debugEnabled || ledger is null) { TryClose(); return; }
        var width = capi.Render.FrameWidth / Math.Max(.5, RuntimeEnv.GUIScale);
        if (!Composers.ContainsKey("activity") || lastWidth != width || lastScale != RuntimeEnv.GUIScale)
        {
            lastWidth = width;
            lastScale = RuntimeEnv.GUIScale;
            Composers["activity"]?.Dispose();
            lastText = null;
            Composers["activity"] = capi.Gui.CreateCompo("vsslssmod-activity", CreateBounds(width))
                .AddDynamicText("", LedgerFont(), ElementBounds.Fixed(0, 0, 255, 80), "ledger")
                .Compose();
        }
        var activity = LocalizedText.Get("vsslssmod:activity-" + ledger.Activity.ToLowerInvariant());
        var template = Lang.Get("vsslssmod:activity-ledger", activity,
            ledger.DemandMultiplier.ToString("0.0"), ledger.PaidKcalPerGameHour.ToString("0"),
            (ledger.StaminaPerSecond * 100).ToString("+0.0;-0.0;0.0"),
            (ledger.OxygenPerSecond * 100).ToString("+0.0;-0.0;0.0"),
            (ledger.FatiguePerGameHour * 100).ToString("+0.00;-0.00;0.00"));
        if (lastText != template)
        {
            Composers["activity"].GetDynamicText("ledger").SetNewText(template);
            lastText = template;
        }
        if (!IsOpened()) TryOpen();
    }
}
