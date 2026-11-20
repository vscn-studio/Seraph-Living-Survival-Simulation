using Seraph.Core.State;
using System.Text.Json.Serialization;

namespace Seraph.Core.Framework;

/// <summary>Regional HP with absolute injury deficits, independent of nutrition-driven maximum changes.</summary>
public sealed record VitalState
{
    public static readonly string[] Regions = ["head-neck", "torso", "left-arm", "right-arm", "left-leg", "right-leg"];
    public const double Maximum = 100;
    public const double MaximumWithMuscle = 130;
    public static double MaximumFor(int region) => region is 0 or 1 ? 20 : 15;
    [JsonRequired] public int IntegrityVersion { get; init; } = 1;
    private double[] legacyIntegrity = Enumerable.Range(0, 6).Select(MaximumFor).ToArray();
    // Compatibility projection for anatomical/action consumers. HP settlement uses DamageDeficits.
    public double[] Integrity {
        get => DamageDeficits is null ? legacyIntegrity : Enumerable.Range(0, 6)
            .Select(i => EffectiveFor(i) / EffectiveMaximumFor(i) * MaximumFor(i)).ToArray();
        init { legacyIntegrity = value; DamageDeficits = null; }
    }
    public double[]? DamageDeficits { get; init; }
    public double CalciumPenalty { get; init; }
    public double MuscleBonus { get; init; }
    [JsonIgnore] public double ExternalMaximumPoints { get; init; }
    public VitalState WithExternalMaximum(double points)
    {
        points = double.IsFinite(points) ? Math.Clamp(points, -90, 1000000) : 0;
        return points == ExternalMaximumPoints ? this : this with { DamageDeficits = Deficits(), ExternalMaximumPoints = points };
    }
    public double EffectiveMaximumFor(int region) => Math.Max(.1,
        MaximumFor(region) * (1 + ExternalMaximumPoints / Maximum) + MuscleBonus - CalciumPenalty);
    public double EffectiveFor(int region) => DamageDeficits is null
        ? legacyIntegrity[region] / MaximumFor(region) * EffectiveMaximumFor(region)
        : Math.Max(0, EffectiveMaximumFor(region) - DamageDeficits[region]);
    public double EffectiveMaximum => Enumerable.Range(0, 6).Sum(EffectiveMaximumFor);
    private double[] Deficits() => DamageDeficits is null ? Enumerable.Range(0, 6)
        .Select(i => EffectiveMaximumFor(i) - EffectiveFor(i)).ToArray() : (double[])DamageDeficits.Clone();
    public VitalState WithNutrition(NutritionState nutrition, BodyState? body = null, BodyProfile? profile = null)
    {
        var calcium = 5 * MicroNutrition.Burden(nutrition, "calcium");
        var muscle = body is null || profile is null ? MuscleBonus : body.MuscleMassG >= profile.MuscleMassMaxG * .9 ? 5
            : body.MuscleMassG >= profile.MuscleMassMaxG * .5 ? 2.5 : 0;
        if (DamageDeficits is not null && calcium == CalciumPenalty && muscle == MuscleBonus) return this;
        return this with { DamageDeficits = Deficits(), CalciumPenalty = calcium, MuscleBonus = muscle };
    }
    public VitalState() { }
    [JsonConstructor]
    public VitalState(double[] integrity, int integrityVersion = 1, double calciumPenalty = 0,
        double muscleBonus = 0, double[]? damageDeficits = null)
    { legacyIntegrity = integrity; IntegrityVersion = integrityVersion; CalciumPenalty = calciumPenalty;
        MuscleBonus = muscleBonus; DamageDeficits = damageDeficits; }
    public bool Lethal => EffectiveFor(0) <= 0 || EffectiveFor(1) <= 0;
    public double Health => Lethal ? 0 : Enumerable.Range(0, 6).Sum(EffectiveFor);
    public string? Validate() => !BodyProfile.In(CalciumPenalty, 0, 5) || !BodyProfile.In(MuscleBonus, 0, 5) || !BodyProfile.In(ExternalMaximumPoints, -90, 1000000)
        || IntegrityVersion != 1 || legacyIntegrity is null || legacyIntegrity.Length != 6
        || legacyIntegrity.Where((v, i) => !BodyProfile.In(v, 0, MaximumFor(i))).Any()
        || DamageDeficits is not null && (DamageDeficits.Length != 6 || DamageDeficits.Any(v => !BodyProfile.In(v, 0, 1000001)))
        ? "Invalid regional integrity." : null;
    public VitalState Restore(double points, int region)
    {
        if (region < 0) return Change(points);
        if (!double.IsFinite(points) || points <= 0 || region >= 6) return this;
        var deficits = Deficits(); deficits[region] = Math.Max(0, deficits[region] - points);
        return this with { DamageDeficits = deficits };
    }
    public VitalState Change(double delta, int region = -1, bool legs = false)
    {
        if (!double.IsFinite(delta)) return this;
        var deficits = Deficits();
        if (delta > 0)
        {
            var total = deficits.Sum();
            if (total > 0) for (var i = 0; i < 6; i++) deficits[i] -= Math.Min(delta, total) * deficits[i] / total;
        }
        else if (region >= 0 && region < 6)
            deficits[region] += Math.Min(-delta, EffectiveFor(region)); // Ordinary limb damage never spills to torso.
        else
        {
            var indices = legs ? new[] { 4, 5 } : Enumerable.Range(0, 6).ToArray();
            var remaining = -delta;
            for (var pass = 0; pass < 6 && remaining > 1e-9; pass++)
            {
                var live = indices.Where(i => EffectiveMaximumFor(i) - deficits[i] > 1e-9).ToArray();
                if (live.Length == 0) { if (legs) { indices = [0, 1, 2, 3]; continue; } break; }
                var share = remaining / live.Length;
                foreach (var i in live) { var loss = Math.Min(EffectiveMaximumFor(i) - deficits[i], share); deficits[i] += loss; remaining -= loss; }
            }
        }
        return this with { DamageDeficits = deficits.Select(v => Math.Max(0, v)).ToArray() };
    }
}
