using Seraph.Core.Framework;
using Seraph.Core.State;
using Seraph.Core.Simulation;

internal static class InjuryChecks
{
    private static readonly BodyProfile Profile = BodyProfile.Default;
    private static readonly BodyState Body = BodyState.Create() with { SleepPressure = 0 };
    private static readonly FrameworkKernel Kernel = new();
    private static FrameworkState Injury(string region, WoundKind kind = WoundKind.Cut, double severity = 1, double pain = 0)
    {
        var result = Kernel.RecordInjury(new(), new("injury-1", kind, region, severity, InjuryEvidence.Verified, 20, PainIntensity: pain));
        Require(result.Accepted, result.Error ?? "Injury rejected.");
        return result.State;
    }
    private static BodyFunctions Resolve(FrameworkState state) => BodyFunctionResolver.Evaluate(state, Body, Profile);
    private static void Require(bool value, string message = "Injury assertion failed.") { if (!value) throw new Exception(message); }
    private static void Near(double a, double b) => Require(Math.Abs(a - b) < 1e-10, $"Expected {a}, received {b}.");

    public static void AbsoluteHealth()
    {
        var damaged = new VitalState().Change(-10, 2);
        Near(5, damaged.EffectiveFor(2));
        var strong = damaged.WithNutrition(NutritionState.Starting(), Body with { MuscleMassG = Profile.MuscleMassMaxG }, Profile);
        Near(130, strong.EffectiveMaximum); Near(10, strong.EffectiveFor(2));
        var ordinary = strong.WithNutrition(NutritionState.Starting(), Body, Profile);
        Near(5, ordinary.EffectiveFor(2));
        var crippled = ordinary.Change(-999, 2).Change(-999, 2);
        Near(20, crippled.EffectiveFor(1)); Near(0, crippled.EffectiveFor(2));
        Near(85, crippled.Health);
        Near(7, strong.Restore(2, 2).WithNutrition(NutritionState.Starting(), Body, Profile).EffectiveFor(2));
        Require(strong.Validate() is null && crippled.Validate() is null);
    }
    public static void Facing()
    {
        foreach (var yaw in new[] { 0d, Math.PI / 2, Math.PI, -Math.PI / 2 })
        foreach (var (x, y, region) in new[] { (-.29, 1.2, "left-arm"), (.29, .9, "right-hand"),
            (-.16, .5, "left-leg"), (.16, .1, "right-foot"), (0d, 1.35, "chest"), (0d, 1.7, "head-neck") })
        {
            var hit = Anatomy.Locate(x * Math.Cos(yaw), y, -x * Math.Sin(yaw), yaw, 1.8, .3);
            Require(hit.Region == region && hit.Evidence == InjuryEvidence.GeometricEstimate, "Facing changed anatomical side.");
        }
        Require(Anatomy.Locate(0, .3, 0, 0, 1.8, .3).Region == "general", "Midline fabricated side.");
        Require(Anatomy.Locate(3, 1, 0, 0, 1.8, .3).Region == "general");
        Require(Anatomy.Locate(double.NaN, 1, 0, 0, 1.8, .3).Region == "general");
        Require(Anatomy.Parent("left-hand") == "left-arm" && Anatomy.Parent("right-foot") == "right-leg");
    }
    public static void SideIndependence()
    {
        var f = Resolve(Injury("left-hand"));
        Require(f.Bow < f.Attack && f.CanUseActiveHand && f.CanJump);
        Near(1, f.Mobility); Near(Resolve(new()).Attack, f.Attack);
        var opposite = Resolve(Injury("right-hand"));
        Near(f.Bow, opposite.Bow);
        Require(opposite.Attack < f.Attack && opposite.Mining < f.Mining);
    }
    public static void MinorInjuries()
    {
        foreach (var region in new[] { "head-neck", "chest", "abdomen", "left-arm", "right-hand", "left-leg", "right-foot" })
        {
            var f = Resolve(Injury(region, severity: region is "left-leg" or "right-foot" ? .1 : .15));
            Require(f.CanDrawBow && f.CanUseActiveHand && f.CanJump && f.CanSprint, "Minor injury disabled an action.");
            Require(f.Balance > .9 && f.Consciousness == 1);
        }
    }
    public static void TypeAndThreshold()
    {
        var bruise = Resolve(Injury("left-leg", WoundKind.Contusion, .7));
        var fracture = Resolve(Injury("left-leg", WoundKind.Fracture, .7));
        Require(fracture.Mobility < bruise.Mobility && fracture.Jump == bruise.Jump);
        Near(.89, fracture.Jump);
        Require(fracture.CanJump && !fracture.CanSprint,
            "Leg injury should reduce clearance without disabling the jump input.");
        Require(Resolve(Injury("left-leg", WoundKind.Contusion, .3)).CanSprint);
        Require(Resolve(Injury("right-arm", WoundKind.Fracture)).CanUseActiveHand == false);
        Require(Resolve(Injury("head-neck", WoundKind.Generic)).Consciousness < .3);
    }
    public static void TorsoAndRespiration()
    {
        var chest = Resolve(Injury("chest")); var abdomen = Resolve(Injury("abdomen"));
        Require(chest.RespiratoryCapacity < abdomen.RespiratoryCapacity && chest.Endurance < 1);
        Near(1, chest.Mobility); Near(1, abdomen.Mobility);
        var frame = Injury("chest");
        var advanced = Kernel.Advance(frame, Body, Profile, new(), new(300, false, false, false, false, 1, true), 8);
        var healthy = Kernel.Advance(new(), Body, Profile, new(), new(300, false, false, false, false, 1, true), 8);
        Require(advanced.Accepted && healthy.Accepted);
        Require(advanced.State.Stamina == healthy.State.Stamina && advanced.State.BreathDemandPerMinute > healthy.State.BreathDemandPerMinute);
        Near(healthy.State.TotalIntakeEnergyKJ, advanced.State.TotalIntakeEnergyKJ);
        Near(healthy.State.Vitals.Health, advanced.State.Vitals.Health); // No second damage writer.
    }
    public static void Recovery()
    {
        var state = Injury("right-hand");
        var recovering = state with { Wounds = state.Wounds.Select(w => w with { Healing = .8 }).ToArray() };
        Require(!Resolve(state).CanUseActiveHand && Resolve(recovering).CanUseActiveHand);
        var healed = state with { Wounds = state.Wounds.Select(w => w with { Healing = 1 }).ToArray() };
        Near(Resolve(new()).Attack, Resolve(healed).Attack); Near(0, BodyFunctionResolver.Pain(healed.Wounds));
    }
    public static void Unlocated()
    {
        var state = Injury("general");
        var f = Resolve(state);
        Near(1, f.Mobility); Near(1, f.Mining); Near(Resolve(new()).Attack, f.Attack); Near(1, f.Balance);
        Require(state.Wounds.Single().Region == "general");
    }
    public static void CorrelatedPenalty()
    {
        var state = Injury("left-leg", WoundKind.Fracture, .8);
        var baseline = Resolve(state).Mobility;
        Near(baseline, Resolve(state with { Pain = 1, Fatigue = 1 }).Mobility);
        Near(baseline, Resolve(state with { Wounds = [.. state.Wounds, state.Wounds[0] with { EventSequence = 1 }] }).Mobility);
        var painfulHand = Injury("left-hand", pain: 1);
        Near(1, Resolve(painfulHand).Mobility);
        Require(Resolve(painfulHand).Bow < 1, "Hand injury must retain its relevant action effect.");
    }
    public static void ReplayAndValidation()
    {
        var injury = new InjuryEvent("verified", WoundKind.Cut, "left-hand", .4, InjuryEvidence.Verified, 42, TissueDamage: .5, PainIntensity: .2);
        var first = Kernel.RecordInjury(new(), injury);
        Require(first.Accepted && first.State.Wounds[0].OccurredBioSeconds == 42);
        var replay = Kernel.RecordInjury(first.State, injury);
        Require(!replay.Accepted && ReferenceEquals(first.State, replay.State));
        foreach (var bad in new[] { injury with { Severity = double.NaN }, injury with { TissueDamage = -1 },
            injury with { PainIntensity = double.PositiveInfinity }, injury with { Region = "imaginary" },
            injury with { Kind = WoundKind.Fracture, Evidence = InjuryEvidence.GeometricEstimate }, injury with { OccurredBioSeconds = -1 } })
            Require(!Kernel.RecordInjury(new(), bad).Accepted);
    }
    public static void Capacity()
    {
        var state = new FrameworkState();
        for (var i = 0; i < 70; i++)
        {
            var result = Kernel.RecordInjury(state, new($"hit-{i}", WoundKind.Cut, "left-hand", .2, InjuryEvidence.Verified, i));
            Require(result.Accepted && result.State.Validate() is null);
            state = result.State;
        }
        Require(state.Wounds.Length == 32 && state.RecentInjuryEvents.Length == 70);
        Require(state.Wounds.Any(w => w.Evidence == InjuryEvidence.CapacityAggregate));
        Require(state.Wounds.All(w => w.Region == "left-hand"));
        Require(state.Wounds.Any(w => w.OccurredBioSeconds == 69), "Latest injury was dropped at capacity.");
    }
    public static void Bounds()
    {
        foreach (var kind in Enum.GetValues<WoundKind>())
        foreach (var region in new[] { "head-neck", "chest", "left-hand", "right-arm", "left-leg", "right-foot", "general" })
        foreach (var severity in new[] { 0, .1, .5, 1 })
        {
            var state = Injury(region, kind, severity, severity);
            var f = Resolve(state);
            foreach (var v in new[] { f.Mobility, 
                f.Balance, f.Endurance, f.RespiratoryCapacity, f.Consciousness, f.Jump, f.Sprint, f.Attack, f.Mining, f.Bow })
                Require(double.IsFinite(v) && v is >= 0 and <= 2);
            Require(BodyFunctionResolver.Pain(state.Wounds) is >= 0 and <= 1);
        }
    }
}
