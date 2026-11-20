namespace Seraph.Core.Framework;

/// <summary>One sprint key: a bounded burst, then sustained running. Idle key taps cannot reset the burst.</summary>
public sealed class RunningPace
{
    public bool Held { get; private set; }
    public double Elapsed { get; private set; }
    public double Released { get; private set; } = 30;
    public bool Burst { get; private set; }
    public double Limit { get; private set; } = 1;
    public double BurstChargeSeconds { get; private set; }
    public static double Fraction(double stamina, double lactateRatio, bool exhausted, ExertionOptions options)
    {
        if (exhausted) return 0;
        static double Smooth(double x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }
        // Lactate does not progressively slow ordinary running. Capacity only
        // blocks a new burst and forces jogging at the hard limit.
        return Smooth(stamina / options.RunningSlowdownStamina);
    }
    public double Gain(ExertionOptions options, double stamina = 1, double lactateRatio = 0, bool exhausted = false)
    {
        if (!Held) return options.JoggingSpeedGain + (options.RunningSpeedGain - options.JoggingSpeedGain)
            * Fraction(stamina, lactateRatio, exhausted, options);
        if (!Burst) return Limit;
        var blend = Math.Clamp((Elapsed - options.BurstSeconds) / options.BurstTransitionSeconds, 0, 1);
        blend = blend * blend * (3 - 2 * blend);
        return Math.Min(Limit, 1 - (1 - options.RunningSpeedGain) * blend);
    }
    public ExertionKind Advance(bool intent, bool moving, double seconds, double stamina,
        double lactateRatio, bool exhausted, ExertionOptions options)
    {
        BurstChargeSeconds = 0;
        if (!intent)
        {
            Held = false; Burst = false; Elapsed = 0; Limit = 1;
            Released = Math.Min(30, Released + Math.Max(0, seconds));
            return ExertionKind.Walking;
        }
        if (!Held)
        {
            Burst = moving && Released >= options.BurstRearmSeconds && !exhausted
                && stamina >= options.UnlockStamina && lactateRatio < 1;
            Held = true; Elapsed = 0; Released = 0;
            Limit = Burst ? 1 : options.JoggingSpeedGain + (options.RunningSpeedGain - options.JoggingSpeedGain)
                * Fraction(stamina, lactateRatio, exhausted, options);
        }
        // Stopping while holding the key cannot bank another acceleration.
        if (!moving) { Burst = false; return ExertionKind.Standing; }
        if (exhausted) Burst = false;
        if (lactateRatio >= 1)
        {
            Burst = false;
            Limit = options.JoggingSpeedGain;
        }
        if (Burst) BurstChargeSeconds = Math.Min(Math.Max(0, seconds),
            Math.Max(0, options.BurstSeconds + options.BurstTransitionSeconds - Elapsed));
        var fraction = Fraction(stamina, lactateRatio, exhausted, options);
        var target = lactateRatio >= 1 ? options.JoggingSpeedGain
            : options.JoggingSpeedGain + ((Burst ? 1 : options.RunningSpeedGain) - options.JoggingSpeedGain) * fraction;
        // Ordinary reserve changes follow the curve directly; sudden losses settle within one second.
        var maxChange = (1 - options.JoggingSpeedGain) * Math.Max(0, seconds);
        Limit += Math.Clamp(target - Limit, -maxChange, maxChange);
        Elapsed = Math.Min(60, Elapsed + Math.Max(0, seconds));
        if (Burst && Elapsed >= options.BurstSeconds + options.BurstTransitionSeconds)
        { Burst = false; Limit = Math.Min(Limit, options.RunningSpeedGain); }
        return Burst ? ExertionKind.Sprinting : exhausted || stamina <= 0 || lactateRatio >= 1
            ? ExertionKind.Jogging : ExertionKind.Running;
    }
    public void Restore(bool held, double elapsed, double released, bool burst, double limit = 1)
    {
        Held = held; Elapsed = Math.Clamp(double.IsFinite(elapsed) ? elapsed : 60, 0, 60);
        Released = Math.Clamp(double.IsFinite(released) ? released : 0, 0, 30);
        Burst = held && burst;
        Limit = Math.Clamp(double.IsFinite(limit) ? limit : 0, 0, 1);
    }
}

/// <summary>Short-term burning and longer muscle soreness are separate from wounds and structural damage.</summary>
public static class ExerciseMuscles
{
    public static double Burning(double lactateRatio) => 0;
    public static FrameworkState Charge(FrameworkState state, ExertionKind kind, double realSeconds, double endurance, double runningFraction = 1)
    {
        var intensity = kind switch { ExertionKind.Sprinting => 1.5, ExertionKind.Running => .45 + .20 * Math.Clamp(runningFraction, 0, 1),
            ExertionKind.Jogging => .45,
            ExertionKind.Jump or ExertionKind.Climbing => 1, _ => 0 };
        if (intensity == 0 || realSeconds <= 0) return state;
        var load = Math.Clamp(state.ExerciseLoad + realSeconds * intensity / (600 * Math.Max(.4, endurance)), 0, 1);
        // Integrate the linear exposure ramp, including a step crossing the threshold.
        var rate = intensity / (600 * Math.Max(.4, endurance));
        // Exact integral of clamp(load + rate*t - threshold, 0, .8), including saturation.
        static double Integral(double x) => x <= 0 ? 0 : x < .8 ? x * x / 2 : .8 * x - .32;
        var exposure = (Integral(state.ExerciseLoad - .2 + rate * realSeconds)
            - Integral(state.ExerciseLoad - .2)) / (rate * realSeconds);
        var soreness = Math.Clamp(state.MuscleSoreness + realSeconds / 1800 * exposure * intensity, 0, 1);
        return state with { ExerciseLoad = load, MuscleSoreness = soreness };
    }
    public static FrameworkState Recover(FrameworkState state, double gameSeconds, bool sleeping, bool resting, bool working)
    {
        var halfLife = sleeping ? 3600 : resting ? 7200 : 14400;
        return state with {
            ExerciseLoad = state.ExerciseLoad * Math.Pow(.5, gameSeconds / 21600),
            MuscleSoreness = working ? state.MuscleSoreness : state.MuscleSoreness * Math.Pow(.5, gameSeconds / halfLife)
        };
    }
}
