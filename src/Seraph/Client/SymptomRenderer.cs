using HarmonyLib;
using Seraph.Network;
using Vintagestory.API.Client;

namespace Seraph.Client;

/// <summary>Symptoms and a single closed-eye yaw change; never writes controls, movement vectors or server physiology.</summary>
internal sealed class SymptomRenderer : IDisposable
{
    private readonly ICoreClientAPI api;
    private readonly Func<ServerSnapshot?> snapshot;
    private double elapsed;
    private readonly EyeClosureCycle eyes = new();
    private readonly Harmony harmony = new("vsslssmod.symptomvisuals");
    private readonly SymptomPostProcess postProcess;
    private readonly ClosedEyeOrientation orientation;
    private static SymptomRenderer? active;
    private double fatigue, blur, reliefUntil;
    private LoadVisuals loadVisuals;
    private double closedEyeClosure, closedEyePhase;
    private ServerSnapshot? lastVisualSnapshot;
    private long lastTurnSequence = -1;
    private double steadyLook, lastYaw, lastPitch;
    private bool hasLook;
    private bool Valid => !failed
        && api.World.Player?.Entity.Alive == true && snapshot() is { } state
        && (state.SymptomVisuals || state.IsClosedEyeResting || closedEyeClosure > 0);
    private static bool RenderGuis() => active is not { } owner || !owner.Valid
        || owner.VisibleClosure <= .01 || SymptomRegions.HasMenu(owner.api);
    private double VisibleClosure => Math.Max(eyes.Closure, closedEyeClosure);
    private double VisibleBlur => blur;
    private static void AfterPerception(float dt) => active?.Advance(dt);
    private static void AfterWindowRender() => active?.RenderFinalFrame();
    internal void Reset() { eyes.Reset(); loadVisuals = default; fatigue = blur = elapsed = reliefUntil = closedEyeClosure = closedEyePhase = steadyLook = 0; lastTurnSequence = -1; hasLook = false; lastVisualSnapshot = null; postProcess.ClearRest(); }
    private void Advance(float dt)
    {
        if (!Valid) { Reset(); return; }
        var state = snapshot()!;
        if (lastVisualSnapshot is { } old && (old.SessionEpoch != state.SessionEpoch || old.LifeGeneration != state.LifeGeneration)) Reset();
        // Menus/F4 do not change symptom ownership. A true pause freezes presentation time.
        var step = api.IsGamePaused ? 0 : dt;
        elapsed += float.IsFinite(step) ? Math.Clamp(step, 0, .25f) : 0;
        if (!ReferenceEquals(state, lastVisualSnapshot))
        {
            var speed = Math.Max(.001, api.World.Calendar.SpeedOfTime * 24d / Math.Max(1, api.World.Calendar.HoursPerDay));
            reliefUntil = elapsed + Math.Max(0, state.AlertnessReliefSeconds) / speed;
            if (state.SleepEyes is { } cycle)
            {
                eyes.Restore(new(cycle.OpenTime, cycle.Phase, cycle.Hold, cycle.Closing, cycle.Sequence));
                TurnAtClosure(state);
            }
            lastVisualSnapshot = state;
        }
        var relief = elapsed < reliefUntil;
        var sleeping = state.IsSleeping;
        var closedEyeResting = state.IsClosedEyeResting;
        var pressure = double.IsFinite(state.SleepPressure) ? Math.Clamp(state.SleepPressure, 0, 1) : 0;
        eyes.Advance(step, state.SymptomVisuals && !sleeping && !closedEyeResting && !relief && pressure >= .9
            ? pressure : 0);
        // K-key rest uses the same smooth eyelid timing as a drowsy closure, but
        // remains closed for the entire rest state instead of blinking randomly.
        if (float.IsFinite(step) && step > 0)
        {
            var delta = Math.Min(step, .25f) / (closedEyeResting ? .7 : .9);
            closedEyePhase = Math.Clamp(closedEyePhase + (closedEyeResting ? delta : -delta), 0, 1);
            closedEyeClosure = closedEyePhase * closedEyePhase * (3 - 2 * closedEyePhase);
        }
        TurnAtClosure(state);
        if (float.IsFinite(step) && step > 0)
        {
            var yaw = api.Input.MouseYaw; var pitch = api.Input.MousePitch;
            if (float.IsFinite(yaw) && float.IsFinite(pitch))
            {
                var stable = hasLook && Math.Abs(Math.IEEERemainder(yaw - lastYaw, Math.PI * 2)) + Math.Abs(pitch - lastPitch) < .04 * Math.Min(step, .25f);
                steadyLook = stable ? Math.Min(3, steadyLook + Math.Min(step, .25f)) : 0;
                lastYaw = yaw; lastPitch = pitch; hasLook = true;
            }
            else { steadyLook = 0; hasLook = false; }
        }
        var gauge = state.Gauges.FirstOrDefault(g => g.Key == "fatigue" && g.Available);
        var fatigueValue = state.SymptomVisuals ? gauge?.Value ?? 0 : 0;
        var target = LoadVisuals.Evaluate(fatigueValue, !state.SymptomVisuals || sleeping || closedEyeResting || relief ? 0 : pressure);
        loadVisuals = loadVisuals.Approach(target, step);
        // Relief and actual sleep clear this presentation immediately; persisted severity is never an input.
        if (sleeping || relief || closedEyeResting) loadVisuals = loadVisuals with { Blur = 0, Center = 0, Lid = 0, Ghost = 0 };
        fatigue = loadVisuals.Edge;
        blur = loadVisuals.Blur;
    }

    private void TurnAtClosure(ServerSnapshot state)
    {
        if (!eyes.JustClosed || eyes.Sequence == lastTurnSequence) return;
        lastTurnSequence = eyes.Sequence;
        orientation.TryTurn(state);
    }

    private static double Severity(ServerSnapshot state, string id) => !state.SymptomVisuals ? 0 : state.ClinicalEffects
        .Where(e => e.Key == id && double.IsFinite(e.Severity))
        .Select(e => Math.Clamp(e.Severity, 0, 1)).DefaultIfEmpty().Max();
    private bool failed;
    internal SymptomRenderer(ICoreClientAPI api, Func<ServerSnapshot?> snapshot)
    {
        this.api = api; this.snapshot = snapshot; postProcess = new(api); orientation = new(api); active = this;
        var guiType = AccessTools.TypeByName("Vintagestory.Client.NoObf.GuiManager");
        var guiMethod = guiType is null ? null : AccessTools.DeclaredMethod(guiType, "OnRenderFrameGUI", [typeof(float)]);
        if (guiMethod is not null) harmony.Patch(guiMethod, prefix: new HarmonyMethod(typeof(SymptomRenderer), nameof(RenderGuis)));
        else api.Logger.Warning("[vsslssmod] Eye closure HUD suppression unavailable on this game build.");
        harmony.Patch(AccessTools.Method(typeof(PerceptionEffects), nameof(PerceptionEffects.OnBeforeGameRender)),
            postfix: new HarmonyMethod(typeof(SymptomRenderer), nameof(AfterPerception)));
        // 1.22.7 invokes this after GUI and Done even when F4 skips Ortho entirely.
        var clientType = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientMain");
        var finalMethod = clientType is null ? null : AccessTools.DeclaredMethod(clientType, "RenderToDefaultFramebuffer", [typeof(float)]);
        if (finalMethod is not null)
            harmony.Patch(finalMethod, postfix: new HarmonyMethod(typeof(SymptomRenderer), nameof(AfterWindowRender)));
        else api.Logger.Warning("[vsslssmod] Final symptom composition unavailable on this game build.");
    }

    private void RenderFinalFrame()
    {
        if (!Valid || snapshot() is not { } state) { Reset(); return; }
        if (!state.IsClosedEyeResting) postProcess.ClearRest();
        try
        {
            var dehydration = Severity(state, "dehydration"); var blood = Severity(state, "blood-loss");
            var heat = Severity(state, "heat-illness");
            // Vitamin A burden limits world-only dark adaptation before GUI composition.
            var intensity = Math.Max(Math.Max(dehydration, blood), heat);
            var dozing = eyes.Warning ? 1 : 0;
            var closure = Math.Max(VisibleClosure, closedEyeClosure);
            var visibleBlur = VisibleBlur;
            if (closure > 0 || fatigue > 0 || visibleBlur > 0 || loadVisuals.Lid > 0 || intensity > 0 || dozing > 0 || state.IsClosedEyeResting)
                postProcess.Render(elapsed, visibleBlur, fatigue, closure, intensity,
                    heat > 0 ? .5 : 0, 0, 0, 0, SymptomRegions.Menus(api), dozing,
                    state.IsClosedEyeResting ? state : null, loadVisuals.Center, loadVisuals.Lid,
                    loadVisuals.Ghost * Math.Clamp((steadyLook - .4) / 1.2, 0, 1));
        }
        catch (Exception ex)
        {
            failed = true; Reset();
            api.Logger.Warning("[vsslssmod] Symptom overlay disabled: {0}", ex.Message);
        }
    }
    public void Dispose()
    {
        Reset(); if (ReferenceEquals(active, this)) active = null;
        harmony.UnpatchAll("vsslssmod.symptomvisuals");
        postProcess.Dispose();
    }
}
