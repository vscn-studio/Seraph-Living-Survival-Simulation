using Cairo;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace Seraph.Client;

/// <summary>Read-only server projection, available without the optional ImGui integration.</summary>
internal sealed partial class SurvivalHud(ICoreClientAPI api, Func<bool>? fresh = null, Func<int>? darkStage = null) : HudElement(api)
{
    private BodyStatusHud? bodyHud;
    private BodyStatusHud BodyHud => bodyHud ??= new(capi);
    private readonly SlowCallbackProbe updateProbe = new("client.survival-hud.update", 50);
    private ServerSnapshot? current;
    private bool isStale;
    private PressureStatus? pressure;
    private readonly ActionProgressPresentation actionView = new();
    private readonly ActionRingRenderer actionRing = new(api);
    internal void UpdatePressure(PressureStatus? status) => pressure = status;
    private static readonly string[] GaugeKeys = ["health", "hydration", "oxygen", "stamina", "fatigue", "bowel"];
    private readonly (float Value, float Maximum, string Tooltip)[] barValues = new (float, float, string)[6];

    public override bool Focusable => false;
    public override double InputOrder => 1;
    public override bool ShouldReceiveKeyboardEvents() => false;

    // This HUD is informational. GuiDialog normally consumes the whole composer
    // rectangle, including the hotbar beneath it. Forward motion to hover controls
    // but never claim pointer input intended for the inventory.
    public override void OnMouseDown(MouseEvent args) { }
    public override void OnMouseUp(MouseEvent args) { }
    public override void OnMouseMove(MouseEvent args)
    {
        var handled = args.Handled;
        base.OnMouseMove(args);
        args.Handled = handled;
    }

    public void ToggleVisibility() => BodyHud.ToggleVisibility();

    public void Update(ServerSnapshot? snapshot, bool stale)
    {
        using var timing = updateProbe?.Measure(capi);
        current = snapshot; isStale = stale;
        BodyHud.Update(snapshot, stale);
        if (snapshot is null) { NativeHudReplacement.Active = false; actionView.Reset(); actionRing.ClearText(); TryClose(); return; }
        if (SingleComposer is null)
        {
            ComposeStatusIcons();
            var bars = capi.Gui.CreateCompo("vsslssmod-life-bars",
                ElementBounds.Fixed(EnumDialogArea.CenterBottom, 0, 0, 850, 140).WithFixedAlignmentOffset(0, 5));
            double[][] colors = [GuiStyle.HealthBarColor, [0.15, 0.6, 0.95], GuiStyle.OxygenBarColor,
                [0.2, 0.85, 0.35], [0.8, 0.6, 0.2], [0.6, 0.4, 0.2]];
            for (var i = 0; i < 6; i++)
            {
                var barBounds = ElementStdBounds.Statbar(i % 2 == 0 ? EnumDialogArea.LeftTop : EnumDialogArea.RightTop, 850 * 0.41)
                    .WithFixedHeight(10).WithFixedAlignmentOffset(i % 2 == 0 ? 0 : -1, 45 - i / 2 * 20);
                if (i % 2 == 0) bars.AddStatbar(barBounds, colors[i], "bar" + i);
                else bars.AddInvStatbar(barBounds, colors[i], "bar" + i);
            }
            Composers["bars"] = bars.Compose();
            for (var i = 0; i < GaugeKeys.Length; i++)
            {
                var n = i;
                var bar = bars.GetStatbar("bar" + i);
                MetricTrendDisplay.For(capi)?.Bind(bar, "hud-" + GaugeKeys[i]);
                bar.onGetStatbarValue = () => barValues[n].Tooltip ?? "";
                bar.SetLineInterval(GaugeKeys[i] switch { "health" => 1, "oxygen" => 1000, "bowel" => 1000, "hydration" => 1000, _ => 0.1f });
            }
        }
        RefreshStatusIcons();
        for (var i = 0; i < 6; i++)
        {
            var key = GaugeKeys[i];
            var g = snapshot.Gauges.FirstOrDefault(g => g.Key == key);
            var valid = !stale && g is { Available: true } && double.IsFinite(g.Value) && double.IsFinite(g.Maximum)
                && g.Maximum > 0 && g.Maximum <= float.MaxValue;
            var value = valid ? (float)Math.Clamp(g!.Value, 0, g.Maximum) : 0;
            var maximum = valid ? (float)g!.Maximum : 1;
            var tooltip = LocalizedText.Get("vsslssmod:hud-gauge-" + key);
            if (valid)
                tooltip += key switch
                {
                    "stamina" or "fatigue" => $" {value * 100:0}/{maximum * 100:0}%",
                    "hydration" => $" {value:0}/{maximum:0} g",
                    "bowel" => $" {value / 1000:0.00}/{maximum / 1000:0.00} kg",
                    _ => $" {value:0.0}/{maximum:0.0}"
                };
            var next = (value, maximum, tooltip);
            if (barValues[i] == next) continue;
            barValues[i] = next;
            Composers["bars"].GetStatbar("bar" + i).SetValues(value, 0, maximum);
        }
        if (!IsOpened()) TryOpen();
        NativeHudReplacement.Active = NativeHudReplacement.Available && IsOpened() && !stale
            && new[] { "health", "oxygen" }.All(key => snapshot.Gauges.Any(g => g.Key == key && g.Available
                && double.IsFinite(g.Value) && double.IsFinite(g.Maximum) && g.Maximum > 0 && g.Maximum <= float.MaxValue));
        Composers["bars"].Enabled = NativeHudReplacement.Active;
    }

    public override void OnRenderGUI(float deltaTime)
    {
        PositionStatusIcons(deltaTime);
        base.OnRenderGUI(deltaTime);
        RenderDarkAdaptationSparkles(deltaTime);
        actionView.Update(current, pressure, deltaTime, capi.World.Player?.Entity.Alive == true && !isStale && (fresh?.Invoke() ?? true), capi.IsGamePaused);
        if (!actionView.Visible) { actionRing.ClearText(); return; }
        var key = actionView.Action switch {
            "drink" or "produce-wash" or "defecate" or "clean" or "clean-body" or "clean-wound" or "bathe" or "bathe-wound" or "well-bucket-raise" or "pressure" => actionView.Action,
            _ => "generic"
        };
        var label = LocalizedText.Get("vsslssmod:world-action-" + key);
        if (actionView.Activity && pressure is { } p)
            label += "\n" + Lang.Get("vsslssmod:pressure-duration", actionView.Seconds.ToString("0.0"))
                + "\n" + Lang.Get("vsslssmod:pressure-flow", p.BeforeMlPerMinute.ToString("0.0"), p.EffectiveMlPerMinute.ToString("0.0"));
        actionRing.Render(actionView, label);
    }

    public override void Dispose() { ReleaseStatusArt(); bodyHud?.Dispose(); actionRing.Dispose(); base.Dispose(); }
}
