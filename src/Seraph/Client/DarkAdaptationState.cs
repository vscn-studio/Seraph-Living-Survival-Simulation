namespace Seraph.Client;

/// <summary>Client real-time eye response. No calendar or disconnected-time catch-up.</summary>
internal sealed class DarkAdaptationState
{
    internal double Level { get; private set; }
    internal double Darkness { get; private set; }
    internal int IconStage => Darkness <= 0 || Level < .7 - 1e-9 ? 0 : Level < .8 - 1e-9 ? 1 : 2;

    internal void Advance(double light, double seconds, bool paused)
    {
        if (!double.IsFinite(light) || !double.IsFinite(seconds) || seconds <= 0 || paused) return;
        Darkness = Math.Clamp((16 - light) / 12, 0, 1);
        var step = Math.Min(seconds, .5) / (Darkness > Level ? 60 : 5);
        Level += Math.Clamp(Darkness - Level, -step, step);
    }

    internal double Strength(double vitaminAPenalty)
    {
        var t = Math.Clamp((Level - .2) / .8, 0, 1);
        return t * t * (3 - 2 * t) * Darkness * (1 - .75 * Math.Clamp(vitaminAPenalty, 0, 1));
    }

    internal void Reset() { Level = Darkness = 0; }
}
