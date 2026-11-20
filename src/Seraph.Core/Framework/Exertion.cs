namespace Seraph.Core.Framework;

public enum ExertionKind
{
    Standing, Walking, Sitting, Sprinting, Swimming, Mining, Bow, Attack, Jump,
    MountedControl, Rowing, Climbing, Diving, Carrying, Logging, Crafting, Igniting, LowPosture, HeavyTool,
    Running, Jogging
}
public readonly record struct ExertionResult(double Stamina, double Oxygen, bool SprintExhausted);
public enum RecoveryStage { Working, Delay, Recovering, MovingExhausted }

public sealed record ExertionOptions
{
    public double BurstSeconds { get; init; } = 3;
    public double BurstTransitionSeconds { get; init; } = 1;
    public double BurstRearmSeconds { get; init; } = 2;
    public double RunningSpeedGain { get; init; } = .8;
    public double RunningStaminaPerSecond { get; init; } = .009;
    public double JoggingSpeedGain { get; init; } = .25;
    public double RunningSlowdownStamina { get; init; } = .20;
    public double RestDelaySeconds { get; init; } = 2;
    public double SeatedRecoverySeconds { get; init; } = 25;
    public double StandingRecoverySeconds { get; init; } = 50;
    public double WalkingRecoverySeconds { get; init; } = 100;
    public double UnlockStamina { get; init; } = .20;
    public double ExhaustedJumpCooldownSeconds { get; init; } = 3;
    public double UnlockLactateRatio { get; init; } = .6;
    public string? Validate() => new[] { BurstSeconds, BurstTransitionSeconds, BurstRearmSeconds, RunningSpeedGain, RunningStaminaPerSecond, JoggingSpeedGain, RunningSlowdownStamina, RestDelaySeconds, SeatedRecoverySeconds, StandingRecoverySeconds, WalkingRecoverySeconds,
        UnlockStamina, UnlockLactateRatio, ExhaustedJumpCooldownSeconds }.Any(v => !double.IsFinite(v))
        || BurstSeconds is < 1 or > 10 || BurstTransitionSeconds is < .2 or > 5 || BurstRearmSeconds is < 1 or > 30
        || RunningSpeedGain is < .5 or > .95 || RunningStaminaPerSecond is <= 0 or > .02
        || JoggingSpeedGain is < .05 or > .5 || JoggingSpeedGain >= RunningSpeedGain || RunningSlowdownStamina is < .1 or > .5
        || RestDelaySeconds is < 0 or > 60 || SeatedRecoverySeconds is < 1 or > 3600
        || StandingRecoverySeconds is < 1 or > 3600 || WalkingRecoverySeconds is < 1 or > 3600
        || ExhaustedJumpCooldownSeconds is < 1 or > 10
        || UnlockStamina is < .1 or > 1 || UnlockLactateRatio is <= 0 or >= 1 ? "Invalid exertion recovery configuration." : null;
}

/// <summary>Short actions use elapsed wall time, independently of the calendar integrator.</summary>
public static class Exertion
{
    // More reserve time without altering the saved 0..1 stamina fraction.
    public static double StaminaCapacity(double endurance, NutritionState nutrition)
        => endurance * 1.5 * (1 + NutrientInteractions.StaminaBonus(nutrition));
    // Posture changes the work rate, never elapsed time or acknowledgement duration.
    public static double WorkMultiplier(ExertionKind kind, bool seated) => seated && kind is
        (ExertionKind.Mining or ExertionKind.Logging or ExertionKind.Crafting or ExertionKind.Igniting
        or ExertionKind.HeavyTool or ExertionKind.Bow or ExertionKind.Attack) ? .8 : 1;

    public static (double Stamina, double Oxygen) Cost(ExertionKind kind)
    {
        var baseline = BaseCost(kind);
        // One shared rate for authoritative actions and local reservations.
        return (baseline.Stamina * .8, baseline.Oxygen);
    }
    private static (double Stamina, double Oxygen) BaseCost(ExertionKind kind) => kind switch {
        ExertionKind.Sprinting => (.01875, .02), ExertionKind.Running => (.01125, .01),
        ExertionKind.Jogging => (0, .006), ExertionKind.Swimming => (0, .006),
        ExertionKind.Mining or ExertionKind.Igniting or ExertionKind.HeavyTool => (.0225, .012), ExertionKind.Bow => (.008, .003),
        ExertionKind.Attack => (.0625, .04), ExertionKind.Jump => (.025, .02),
        ExertionKind.MountedControl => (.006, .001), ExertionKind.Rowing => (.0125, .003),
        ExertionKind.Climbing => (.015, .015), ExertionKind.Diving => (.004, .006),
        ExertionKind.Carrying => (.01, .001), ExertionKind.Logging => (.0225, .012),
        ExertionKind.LowPosture => (0, .002),
        // Hand crafting is light intermittent work. Keep it below tool work so repeated
        // recipe clicks do not drain reserves like continuous mining or carving.
        ExertionKind.Crafting => (.0035, .001), _ => (0, 0) };
    public static double PowerW(ExertionKind kind) => (kind is ExertionKind.Bow or ExertionKind.Crafting ? 160 : 450) * 4184d / 3600;
    public static bool RunningMovement(ExertionKind kind) => kind is ExertionKind.Running or ExertionKind.Jogging;
    public static double MovementPowerW(ExertionKind kind, double runningFraction = 1) => (kind switch {
        ExertionKind.Sprinting => 600, ExertionKind.Running => 300 + 150 * Math.Clamp(runningFraction, 0, 1),
        ExertionKind.Jogging => 300,
        ExertionKind.Walking or ExertionKind.LowPosture => 160, _ => 0 }) * 4184d / 3600;
    // Fatigue and sleep still limit running ability; RunningPace separately owns short-term reserve limits.
    public static double RunningLoad(double stamina, double lactateRatio, double fatigue, double sleepPressure)
        => LoadLimit(stamina, lactateRatio, fatigue, sleepPressure, sprint: true).Value;
    public static double StaminaDelta(ExertionKind kind, double seconds, double endurance,
        ExertionOptions? options = null)
    {
        options ??= new();
        return kind == ExertionKind.Running
            ? -seconds * options.RunningStaminaPerSecond / Math.Max(.2, endurance)
            : -Cost(kind).Stamina * seconds / Math.Max(.2, endurance);
    }
    public static bool Exhausted(double stamina, double lactate, double capacity, bool latched, bool triggerHeld,
        bool recovering = true, ExertionOptions? options = null)
    {
        options ??= new();
        // This latch governs general action exhaustion and jump cadence. Lactate
        // is handled by RunningPace, where it limits only running and burst speed.
        return stamina <= 0 || latched && (stamina < options.UnlockStamina || triggerHeld || !recovering);
    }
    private static double Interpolate(double value, double lower, double lowerResult, double upper, double upperResult)
        => lowerResult + (upperResult - lowerResult) * (value - lower) / (upper - lower);
    // Lactate gates burst actions at capacity. It does not progressively slow
    // ordinary manipulation, walking, healing or fatigue recovery.
    public static double LactatePerformance(double ratio) => 1;
    public static double FatiguePerformance(double fatigue) => fatigue <= .6 ? 1
        : fatigue <= .8 ? 1 - .1 * (fatigue - .6) / .2
        : fatigue <= .9 ? .9 - .1 * (fatigue - .8) / .1
        : .8 - .05 * Math.Clamp((fatigue - .9) / .1, 0, 1);
    public static double StaminaSprintPerformance(double stamina) => stamina switch {
        >= .35 => 1,
        >= .20 => Interpolate(stamina, .20, .90, .35, 1),
        >= .10 => Interpolate(stamina, .10, .80, .20, .90),
        >= .05 => Interpolate(stamina, .05, .70, .10, .80),
        _ => .60 + .10 * Math.Clamp(stamina / .05, 0, 1)
    };
    public static double StaminaLaborPerformance(double stamina) => stamina switch {
        >= .35 => 1,
        >= .20 => Interpolate(stamina, .20, .95, .35, 1),
        >= .10 => Interpolate(stamina, .10, .70, .20, .95),
        >= .05 => Interpolate(stamina, .05, .45, .10, .70),
        _ => .25 + .20 * Math.Clamp(stamina / .05, 0, 1)
    };
    public static double SleepPressurePerformance(double pressure) => pressure <= .7 ? 1
        : pressure <= .85 ? 1 - .05 * (pressure - .7) / .15
        : pressure <= .95 ? .95 - .05 * (pressure - .85) / .10
        : .9 - .05 * Math.Clamp((pressure - .95) / .05, 0, 1);
    /// <summary>Alternative load limits: independent injury, fuel and oxygen effects are applied by the caller.</summary>
    public static (double Value, string Reason) LoadLimit(double stamina, double lactateRatio, double fatigue,
        double sleepPressure, bool sprint = false)
    {
        var limit = (Value: 1d, Reason: "normal");
        void Consider(double value, string reason) { if (value < limit.Value) limit = (value, reason); }
        // RunningPace owns reserve-related speed limits; do not apply them twice.
        Consider(FatiguePerformance(fatigue), "fatigue");
        Consider(SleepPressurePerformance(sleepPressure), "sleep-pressure");
        if (!sprint) Consider(StaminaLaborPerformance(stamina), "stamina");
        return limit;
    }
    public static bool Strong(ExertionKind kind) => kind is ExertionKind.Sprinting or ExertionKind.Mining
        or ExertionKind.Bow or ExertionKind.Swimming or ExertionKind.Attack or ExertionKind.Jump
        or ExertionKind.MountedControl or ExertionKind.Rowing or ExertionKind.Climbing
        or ExertionKind.Diving or ExertionKind.Carrying or ExertionKind.Logging or ExertionKind.Crafting
        or ExertionKind.Igniting or ExertionKind.HeavyTool;
    // Integrate breathing once per active tick, independently of action receipts or rest.
    public static double Breathe(double oxygen, double seconds, bool hasAir, double respiratoryCapacity = 1)
        => hasAir ? Math.Min(1, oxygen + Math.Max(0, seconds) / 45 * Math.Clamp(respiratoryCapacity, 0, 1)) : oxygen;

    public static ExertionResult Apply(double stamina, double oxygen, ExertionKind kind, double seconds,
        bool hasAir, bool recover, bool exhausted, double recoverySupport = 1, double capacity = 1, double respiratoryCapacity = 1,
        double? oxygenRecoverySeconds = null, ExertionOptions? options = null, double workMultiplier = 1,
        double? recoveryCapacity = null)
    {
        var cost = Cost(kind);
        cost = (cost.Stamina * workMultiplier, hasAir ? 0 : cost.Oxygen * workMultiplier);
        var floor = hasAir ? Math.Min(oxygen, .1) : 0;
        var finalOxygen = Math.Max(floor, oxygen - cost.Oxygen * seconds);
        // Integrate hypoxic burden along the actual reserve trajectory, including its air floor.
        double Deficit(double o) => Math.Pow(Math.Max(0, .25 - o), 2) / .5;
        var drainingSeconds = cost.Oxygen > 0 ? (oxygen - finalOxygen) / cost.Oxygen : 0;
        var burden = cost.Oxygen > 0 ? (Deficit(finalOxygen) - Deficit(oxygen)) / cost.Oxygen : 0;
        burden += Math.Max(0, seconds - drainingSeconds) * Math.Clamp((.25 - finalOxygen) / .25, 0, 1);
        var stress = !hasAir && seconds > 0 ? 1 + burden / seconds * 1.5 : 1;
        oxygen = finalOxygen;
        stamina = Math.Clamp(stamina + (kind == ExertionKind.Running
            ? StaminaDelta(kind, seconds, capacity, options)
            : -cost.Stamina * seconds * stress / Math.Max(.2, capacity)), 0, 1);
        // The adapter applies this delta to the one native reserve, preserving smoke and immersion.
        if (recover && !Strong(kind) && !RunningMovement(kind))
        {
            options ??= new();
            var duration = kind == ExertionKind.Walking ? options.WalkingRecoverySeconds
                : kind == ExertionKind.Sitting ? options.SeatedRecoverySeconds : options.StandingRecoverySeconds;
            stamina = Math.Min(1, stamina + seconds / duration * 1.2
                    * Math.Clamp(recoverySupport, 0, 1) * Math.Clamp(recoveryCapacity ?? capacity, 0, 1));
        }
        // Only the authoritative rest transition can release the latch; running lactate is independent.
        return new(stamina, oxygen, stamina <= 0 || exhausted);
    }
}
