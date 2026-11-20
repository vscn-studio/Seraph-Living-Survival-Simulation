using Seraph.Core.State;

namespace Seraph.Core.Framework;

public sealed record PainOptions
{
    public double AcuteHalfLifeHours { get; init; } = 2;
    public double SlowAcuteHalfLifeHours { get; init; } = 6;
    public string? Validate() => !BodyProfile.In(AcuteHalfLifeHours, .01, 1000)
        || !BodyProfile.In(SlowAcuteHalfLifeHours, .01, 1000) ? "Invalid pain half-life." : null;
}

// Logarithmic contributions preserve the combined pain when bounded wound records merge.
public sealed record PainPools(double Fast, double Slow, double Residual)
{
    public bool Valid => BodyProfile.In(Fast, 0, 1000) && BodyProfile.In(Slow, 0, 1000)
        && BodyProfile.In(Residual, 0, 1000);
}

public static class WoundPain
{
    public static PainPools Pools(Wound w)
    {
        if (w.PainPools is { } pools) return pools;
        var hazard = -Math.Log(1 - Math.Clamp((w.PainIntensity ?? w.Severity) * (1 - w.Healing) * .45, 0, .8));
        var slow = w.Kind is WoundKind.Fracture or WoundKind.Burn or WoundKind.ElectricBurn;
        return new(slow ? 0 : hazard * .8, slow ? hazard * .8 : 0, hazard * .2);
    }
    public static double Contribution(Wound w)
    {
        if (w.Healing >= 1) return 0;
        var p = Pools(w);
        return 1 - Math.Exp(-p.Fast - p.Slow - p.Residual);
    }
    public static double Resolve(IEnumerable<Wound> wounds, Func<Wound, double>? relief = null) =>
        1 - wounds.Aggregate(1d, (r, w) => r * (1 - Contribution(w) * (1 - Math.Clamp(relief?.Invoke(w) ?? 0, 0, .6))));
    public static Wound Advance(Wound w, double seconds, PainOptions options)
    {
        var p = Pools(w);
        return w with { PainPools = p with {
            Fast = p.Fast * Math.Exp(-Math.Log(2) * seconds / (options.AcuteHalfLifeHours * 3600)),
            Slow = p.Slow * Math.Exp(-Math.Log(2) * seconds / (options.SlowAcuteHalfLifeHours * 3600)) } };
    }
    public static Wound PaidHealing(Wound before, Wound proposed, double healing)
    {
        var p = Pools(proposed with { Healing = before.Healing });
        var ratio = before.Healing >= 1 ? 0 : Math.Clamp((1 - healing) / (1 - before.Healing), 0, 1);
        return proposed with { Healing = healing, PainPools = p with { Residual = p.Residual * ratio } };
    }
    public static PainPools Merge(IEnumerable<Wound> wounds)
    {
        var pools = wounds.Select(Pools).ToArray();
        return new(pools.Sum(p => p.Fast), pools.Sum(p => p.Slow), pools.Sum(p => p.Residual));
    }
}
