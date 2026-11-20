namespace Seraph.Core.Framework;

/// <summary>Transient rate modifiers; never repair organ capacity or create reserves.</summary>
public static class AdrenalineEffects
{
    public static FrameworkState AdvanceGameTimeCooldown(FrameworkState state, double gameSeconds)
    {
        if (!double.IsFinite(gameSeconds) || gameSeconds < 0) throw new ArgumentOutOfRangeException(nameof(gameSeconds));
        return state with { AdrenalineCooldownRemainingSeconds = Math.Max(0,
            state.AdrenalineCooldownRemainingSeconds - gameSeconds) };
    }

    public static double Level(FrameworkState state) => state.AdrenalineRemainingSeconds > 0
        && double.IsFinite(state.AdrenalineLevel) ? Math.Clamp(state.AdrenalineLevel, 0, 1) : 0;
    public static double AttackSpeed(FrameworkState state) => 1 + .20 * Level(state);
    public static double Flow(FrameworkState state) => 1 + .20 * Level(state);
    // Temporary constriction reduces wound loss without changing circulatory flow.
    public static double Bleeding(FrameworkState state) => 1 - .65 * Level(state);
    public static double Ventilation(FrameworkState state) => 1 + .25 * Level(state);
    public static double Pain(FrameworkState state) => 1 - .50 * Level(state);
    public static double Digestion(FrameworkState state) => 1 - .40 * Level(state);
    public static double HeartRate(FrameworkState state) => Math.Clamp(state.Clinical.HeartRateBpm * Flow(state), 25, 240);
}
