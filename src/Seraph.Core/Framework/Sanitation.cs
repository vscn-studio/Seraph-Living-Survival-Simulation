using Seraph.Core.State;

namespace Seraph.Core.Framework;

public sealed record SanitationOptions
{
    public double DirtyGroundPerGameHour { get; init; } = .0025;
    public double PollutionPerPointPerGameHour { get; init; } = .00025;
    public double DirtyWaterRisePerGameHour { get; init; } = .006;
    public double CleanWaterFallPerGameHour { get; init; } = .5;
    public double RainWashPerGameHour { get; init; } = .1;
    public double CleanGameHours { get; init; } = 24;
    public double BathedGameHours { get; init; } = 72;
    public string? Validate() => new[] { DirtyGroundPerGameHour, PollutionPerPointPerGameHour,
        DirtyWaterRisePerGameHour, CleanWaterFallPerGameHour, RainWashPerGameHour }.Any(v => !BodyProfile.In(v, 0, 10))
        || !BodyProfile.In(CleanGameHours, 0, 1000) || !BodyProfile.In(BathedGameHours, 0, 1000) ? "Invalid sanitation rates." : null;
}

/// <summary>Immutable observations captured on the world thread; settlement never scans blocks.</summary>
public sealed record SanitationContext
{
    public bool DirtyGround { get; init; }
    public bool Moving { get; init; }
    public bool CarriesWaste { get; init; }
    public bool Immersed { get; init; }
    public double WaterDirtiness { get; init; }
    public bool Raining { get; init; }
    public double PollutionPoints { get; init; }
}

public static class Sanitation
{
    public const double DirtyThreshold = .8;
    public static double GainMultiplier(FrameworkState state) => state.BathedRemainingGameHours > 0 ? .2
        : state.CleanRemainingGameHours > 0 ? .5 : 1;
    public static FrameworkState Advance(FrameworkState state, SanitationContext context, double seconds, SanitationOptions? options = null)
    {
        options ??= new();
        if (!BodyProfile.In(seconds, 0, 8) || options.Validate() is not null
            || !BodyProfile.In(context.WaterDirtiness, 0, 1) || !BodyProfile.In(context.PollutionPoints, 0, 1000))
            throw new ArgumentOutOfRangeException(nameof(seconds));
        var hours = seconds / 3600;
        // Split exactly at protection expiry, independent of the caller's step size.
        var bathed = Math.Min(hours, state.BathedRemainingGameHours);
        var clean = Math.Max(0, Math.Min(hours, state.CleanRemainingGameHours) - bathed);
        var effectiveHours = bathed * .2 + clean * .5 + Math.Max(0, hours - bathed - clean);
        var dirt = state.Dirtiness + effectiveHours * ((context.DirtyGround && context.Moving ? options.DirtyGroundPerGameHour : 0)
            + context.PollutionPoints * options.PollutionPerPointPerGameHour);
        if (context.Immersed)
            dirt = dirt < context.WaterDirtiness
                ? Math.Min(context.WaterDirtiness, dirt + effectiveHours * options.DirtyWaterRisePerGameHour)
                : Math.Max(context.WaterDirtiness, dirt - hours * options.CleanWaterFallPerGameHour);
        else if (context.Raining) dirt -= hours * options.RainWashPerGameHour;
        if (context.CarriesWaste) dirt = Math.Max(DirtyThreshold, dirt);
        return state with { Dirtiness = Math.Clamp(dirt, 0, 1),
            CleanRemainingGameHours = Math.Max(0, state.CleanRemainingGameHours - hours),
            BathedRemainingGameHours = Math.Max(0, state.BathedRemainingGameHours - hours) };
    }
    // Physical solids determine cleaning residue. Infectious agents are handled
    // by their contact route, never converted into visible body dirt.
    public static double WaterResidue(WaterBatch water) => Math.Clamp(water.Turbidity * .3, 0, .9);
}
