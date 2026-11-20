using HarmonyLib;
using Seraph.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Seraph.Client;

/// <summary>Preloaded beat buffers and a gain-only native hook. Never changes stored sound volumes.</summary>
internal sealed class AdrenalineAudio : IDisposable
{
    private readonly ICoreClientAPI api;
    private readonly Func<ServerSnapshot?> snapshot;
    private readonly long listener;
    private readonly Harmony harmony = new("vsslssmod.adrenaline-audio");
    private readonly ILoadedSound?[] beats = new ILoadedSound?[2];
    private static float alert;
    private bool loaded;
    private double envelope, untilBeat, remaining;
    private int nextBeat;
    private ServerSnapshot? previous;
    private string? epoch;
    private long life;

    internal AdrenalineAudio(ICoreClientAPI api, Func<ServerSnapshot?> snapshot)
    {
        this.api = api; this.snapshot = snapshot;
        var type = AccessTools.TypeByName("Vintagestory.Client.LoadedSoundNative");
        var gain = type is null ? null : AccessTools.PropertyGetter(type, "GlobalVolume");
        if (gain is not null) harmony.Patch(gain, postfix: new HarmonyMethod(typeof(AdrenalineAudio), nameof(Gain)));
        else api.Logger.Warning("[vsslssmod] Selective adrenaline sound gain unavailable on this game build.");
        listener = api.Event.RegisterGameTickListener(Tick, 50);
    }

    internal static bool DangerSound(SoundParams sound)
    {
        if (sound.SoundType is EnumSoundType.Music or EnumSoundType.MusicGlitchunaffected
            or EnumSoundType.Weather or EnumSoundType.Ambient or EnumSoundType.AmbientGlitchunaffected) return false;
        var path = sound.Location?.Path;
        if (path is null) return false;
        return path.StartsWith("sounds/creature/", StringComparison.Ordinal)
            || path.StartsWith("sounds/walk/", StringComparison.Ordinal)
            || path.StartsWith("sounds/player/hurt", StringComparison.Ordinal)
            || path.StartsWith("sounds/player/slap", StringComparison.Ordinal)
            || path.StartsWith("sounds/player/projectilehit", StringComparison.Ordinal)
            || path.StartsWith("sounds/weapon/", StringComparison.Ordinal)
            || path.StartsWith("sounds/player/hackingspearhit", StringComparison.Ordinal)
            || path == "sounds/held/torch-attack.ogg";
    }

    private static void Gain(SoundParams ___soundParams, ref float __result)
    {
        var strength = Volatile.Read(ref alert);
        if (strength > 0 && DangerSound(___soundParams)) __result *= 1 + .25f * strength;
    }

    private void Tick(float dt)
    {
        var state = snapshot();
        if (state is null || api.World.Player?.Entity.Alive != true)
        { Reset(); return; }
        if (epoch != state.SessionEpoch || life != state.LifeGeneration)
        { Reset(); epoch = state.SessionEpoch; life = state.LifeGeneration; }
        // Loading is kept out of render and character-panel callbacks, once per client world.
        if (!loaded)
        {
            loaded = true;
            try
            {
                for (var i = 0; i < beats.Length; i++)
                    beats[i] = api.World.LoadSound(new SoundParams(new AssetLocation("vsslssmod:sounds/body/heartbeat.ogg"))
                    { RelativePosition = true, DisposeOnFinish = false, Volume = 0, ShouldLoop = false });
            }
            catch (Exception ex) { api.Logger.Warning("[vsslssmod] Heartbeat audio unavailable: {0}", ex.Message); }
        }
        remaining = AdrenalineHud.Remaining(remaining, state, previous, dt, api.IsGamePaused);
        previous = state;
        if (api.IsGamePaused)
        {
            foreach (var beat in beats) if (beat?.IsPlaying == true) beat.Stop();
            Volatile.Write(ref alert, 0); return;
        }
        var step = float.IsFinite(dt) ? Math.Clamp(dt, 0, .25f) : 0;
        var on = remaining > 0 && !state.IsClosedEyeResting;
        envelope = Math.Clamp(envelope + (on ? step / 2 : -step / 1.5), 0, 1);
        Volatile.Write(ref alert, (float)envelope);
        foreach (var beat in beats) beat?.SetVolume((float)(.35 * envelope));
        if (envelope <= 0) { untilBeat = 0; return; }
        untilBeat -= step;
        if (untilBeat > 0) return;
        var sound = beats[nextBeat++ % beats.Length];
        if (sound is { IsReady: true, IsDisposed: false }) { sound.Stop(); sound.Start(); }
        var heartRate = double.IsFinite(state.EffectiveHeartRateBpm) ? Math.Clamp(state.EffectiveHeartRateBpm, 25, 240) : 72;
        untilBeat = 60 / heartRate; // Never replay beats missed during a delayed frame.
    }

    internal void Reset()
    {
        for (var i = 0; i < beats.Length; i++)
        { if (beats[i] is { IsDisposed: false } beat) { beat.Stop(); beat.Dispose(); } beats[i] = null; }
        loaded = false; nextBeat = 0;
        envelope = untilBeat = remaining = 0; previous = null; epoch = null; life = 0;
        Volatile.Write(ref alert, 0);
    }

    public void Dispose()
    {
        api.Event.UnregisterGameTickListener(listener); Reset();
        foreach (var beat in beats) beat?.Dispose();
        harmony.UnpatchAll("vsslssmod.adrenaline-audio");
    }
}
