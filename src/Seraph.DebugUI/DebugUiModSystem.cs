using Seraph.DebugUI.Client;
using Seraph.Core.Framework;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Seraph.DebugUI;

public sealed class DebugUiModSystem : ModSystem
{
    private const string DebugHotkey = "vsslssmod-debug";
    private SeraphDebugWindow? window;
    private SeraphModSystem? seraph;
    private ICoreClientAPI? clientApi;
    private long listener;
    private bool requestedDiagnostics;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;
    public override double ExecuteOrder() => 1;

    public override void StartClientSide(ICoreClientAPI api)
    {
        clientApi = api;
        seraph = api.ModLoader.GetModSystem<SeraphModSystem>();
        window = new(api, ReadView, (speed, telemetry, revision) => seraph.ClientBridge?.ApplySettings(speed, telemetry, revision),
            (field, value, life) => seraph.ClientBridge?.ApplyDebugValue(field, value, life));
        listener = api.Event.RegisterGameTickListener(_ => ReconcileDiagnostics(), 500);
        api.Input.RegisterHotKey(DebugHotkey, Lang.Get("vsslssmoddebug:hotkey-debug"), GlKeys.V,
            HotkeyType.GUIOrOtherControls, altPressed: false, ctrlPressed: true, shiftPressed: true);
        api.Input.SetHotKeyHandler(DebugHotkey, _ => { Toggle(); return true; });
        api.ChatCommands.Create("vsslssmod").WithDescription(Lang.Get("vsslssmoddebug:command-description"))
            .HandleWith(_ => Toggle())
            .BeginSubCommand("debug").WithDescription(Lang.Get("vsslssmoddebug:command-debug-description"))
                .HandleWith(_ => Toggle()).EndSubCommand();
    }

    private TextCommandResult Toggle()
    {
        window?.Toggle();
        ReconcileDiagnostics();
        return TextCommandResult.Success(Lang.Get("vsslssmoddebug:window-toggled"));
    }

    private void ReconcileDiagnostics()
    {
        // Diagnostics are requested only while the debug window is open. The former
        // always-visible administrator overlay was intentionally removed.
        var desired = window?.IsOpen == true;
        if (desired == requestedDiagnostics) return;
        requestedDiagnostics = desired;
        seraph?.ClientBridge?.RequestDiagnostics(desired);
    }

    private SeraphDebugView? ReadView()
    {
        var bridge = seraph?.ClientBridge;
        var snapshot = bridge?.Snapshot;
        if (bridge is null) return null;
        if (snapshot is null) return new() { Summary = bridge.ConnectionMessage };
        var stale = !bridge.HasFreshSnapshot;
        return new()
        {
            HasSnapshot = !stale,
            CanViewDiagnostics = !stale && snapshot.CanViewDiagnostics,
            CanEditSettings = !stale && snapshot.CanEditSettings,
            DebugValues = !stale && snapshot.CanViewDiagnostics ? snapshot.DebugValues : [],
            SessionEpoch = snapshot.SessionEpoch, LifeGeneration = snapshot.LifeGeneration,
            DebugValueResponseSequence = bridge.DebugValueResponseSequence, DebugValueStatus = bridge.DebugValueStatus,
            Summary = stale ? bridge.ConnectionMessage : snapshot.Summary,
            PhysiologyLines = stale ? [] : snapshot.PhysiologyLines,
            EnvironmentLines = stale ? [] : snapshot.EnvironmentLines,
            ClockLines = stale ? [] : snapshot.ClockLines,
            QualityLines = stale ? [] : snapshot.QualityLines,
            PanelStatusLines = stale ? [] : snapshot.PanelStatusLines,
            PanelTemperatureLines = stale ? [] : snapshot.PanelTemperatureLines,
            PanelNutritionLines = stale ? [] : [.. snapshot.PanelNutritionLines, .. snapshot.Nutrients.Select(g => {
                var definition = Nutrients.All.FirstOrDefault(n => n.Id == g.Key);
                return g.Available ? Lang.Get("vsslssmod:nutrition-bar-label",
                    Lang.Get("vsslssmod:character-nutrient-" + g.Key), g.Value.ToString("0.##"),
                    g.Maximum.ToString("0.##"), definition?.Unit ?? "", g.Pending.ToString("0.##"))
                    : Lang.Get("vsslssmod:character-nutrient-" + g.Key) + "   / " + g.Maximum.ToString("0.##") + " " + definition?.Unit;
            })],
            PanelRecoveryLines = stale ? [] : snapshot.PanelRecoveryLines,
            PanelHydrationLines = stale ? [] : snapshot.PanelHydrationLines,
            PanelWoundLines = stale ? [] : [.. snapshot.PanelWoundLines, .. snapshot.BodyRegions.Select(r =>
                Lang.Get("vsslssmod:body-region-" + r.Region) + $" {r.Integrity:0.##}/{r.Maximum:0.##}")],
            WaterInteractionLines = stale ? [] : [.. snapshot.WaterLines, .. snapshot.InteractionLines],
            PhysiologySpeed = snapshot.PhysiologySpeed, TelemetryEnabled = snapshot.TelemetryEnabled,
            SettingsRevision = snapshot.SettingsRevision, SettingsResponseSequence = bridge.SettingsResponseSequence,
            StatusMessage = bridge.ConnectionMessage + "\n" + bridge.StatusMessage
        };
    }

    public override void Dispose()
    {
        clientApi?.Event.UnregisterGameTickListener(listener);
        clientApi?.Input.SetHotKeyHandler(DebugHotkey, _ => false);
        seraph?.ClientBridge?.RequestDiagnostics(false);
        window?.Dispose();
    }
}
