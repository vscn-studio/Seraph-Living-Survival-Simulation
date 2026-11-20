using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Seraph.DebugUI.Client;

/// <summary>
/// Native Vintage Story HUD text for server-authorized administrator diagnostics.
/// It is display-only and never participates in input handling.
/// </summary>
public sealed class SeraphAdminDiagnosticsHud(ICoreClientAPI api) : HudElement(api)
{
    private const string ComposerKey = "vsslssmod-admin-diagnostics-hud";
    private const string TextKey = "diagnostics";
    private string lastText = string.Empty;

    public override bool Focusable => false;
    public override double InputOrder => 0;
    public override bool ShouldReceiveKeyboardEvents() => false;

    public void Update(SeraphDebugView? view)
    {
        if (view is null || !view.HasSnapshot || !view.CanViewDiagnostics)
        {
            if (IsOpened()) TryClose();
            return;
        }

        if (SingleComposer is null)
        {
            var font = CairoFont.WhiteDetailText().WithFontSize(12);
            float width = Math.Clamp(capi.Render.FrameWidth / Math.Max(.5f, RuntimeEnv.GUIScale) * .28f, 260, 380);
            float height = Math.Clamp(capi.Render.FrameHeight / Math.Max(.5f, RuntimeEnv.GUIScale) - 120, 180, 680);
            SingleComposer = capi.Gui.CreateCompo(ComposerKey,
                    ElementBounds.Fixed(EnumDialogArea.RightMiddle, 0, 0, width, height)
                        .WithFixedAlignmentOffset(-12, 0))
                .AddDynamicText(string.Empty, font, ElementBounds.Fixed(0, 0, width, height), TextKey)
                .Compose();
        }

        string text = BuildText(view);
        if (!string.Equals(text, lastText, StringComparison.Ordinal))
        {
            lastText = text;
            SingleComposer.GetDynamicText(TextKey).SetNewText(text);
        }
        if (!IsOpened()) TryOpen();
    }

    private static string BuildText(SeraphDebugView view)
    {
        var lines = new List<string> { Lang.Get("vsslssmoddebug:overlay-title") };
        Append(lines, Lang.Get("vsslssmoddebug:section-environment"), view.EnvironmentLines);
        Append(lines, Lang.Get("vsslssmoddebug:section-physiology"), view.PhysiologyLines);
        Append(lines, Lang.Get("vsslssmoddebug:section-clocks"), view.ClockLines);
        Append(lines, Lang.Get("vsslssmoddebug:section-data-quality"), view.QualityLines);
        Append(lines, Lang.Get("vsslssmoddebug:section-player-recovery"), view.PanelRecoveryLines);
        Append(lines, Lang.Get("vsslssmoddebug:section-player-nutrition"), view.PanelNutritionLines);
        Append(lines, Lang.Get("vsslssmoddebug:section-player-wounds"), view.PanelWoundLines);
        Append(lines, Lang.Get("vsslssmoddebug:section-water-interactions"), view.WaterInteractionLines);
        return string.Join("\n", lines);
    }

    private static void Append(List<string> target, string title, string[] values)
    {
        if (values is null || values.Length == 0) return;
        target.Add(title);
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) target.Add(value);
    }

    public override void OnRenderGUI(float deltaTime) => base.OnRenderGUI(deltaTime);
}
