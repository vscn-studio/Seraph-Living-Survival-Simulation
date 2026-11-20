using Seraph.Core.State;

namespace Seraph.Core.Framework;

/// <summary>Accepted server event, not a medical diagnosis. IDs are unique within a player's life.</summary>
public sealed record InjuryEvent(string Id, WoundKind Kind, string Region, double Severity,
    InjuryEvidence Evidence, double OccurredBioSeconds, double BleedLps = 0,
    double? TissueDamage = null, double? PainIntensity = null, double? IntegrityDamage = null);

public readonly record struct HitRegion(string Region, InjuryEvidence Evidence)
{
    public int VitalIndex => Anatomy.VitalIndex(Region);
}

public static class Anatomy
{
    public static int VitalIndex(string region) => region switch {
        "head-neck" => 0, "torso" or "chest" or "abdomen" => 1,
        "left-arm" or "left-hand" => 2, "right-arm" or "right-hand" => 3,
        "left-leg" or "left-foot" => 4, "right-leg" or "right-foot" => 5, _ => -1
    };
    public static string Parent(string region) => VitalIndex(region) is var i && i >= 0 ? VitalState.Regions[i] : "general";
    public static bool Known(string region) => region == "general" || VitalIndex(region) >= 0;

    /// <summary>World-relative ray hit transformed into body axes; coarse upright geometry, never bone evidence.</summary>
    public static HitRegion Locate(double x, double y, double z, double yaw, double height, double radius)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z) || !double.IsFinite(yaw)
            || !double.IsFinite(height) || height <= 0 || !double.IsFinite(radius) || radius <= 0
            || y < 0 || y > height + .05 || Math.Abs(x) > radius + .05 || Math.Abs(z) > radius + .05)
            return new("general", InjuryEvidence.Unlocated);
        var lateral = x * Math.Cos(yaw) - z * Math.Sin(yaw);
        var side = lateral < 0 ? "left-" : "right-";
        var h = y / height;
        if (h < .43 && Math.Abs(lateral) < radius * .1)
            return new("general", InjuryEvidence.Unlocated); // Midline does not identify a leg or foot.
        var region = h >= .8 ? "head-neck" : h < .1 ? side + "foot" : h < .43 ? side + "leg"
            : Math.Abs(lateral) > radius * .65 ? side + (h < .57 ? "hand" : "arm")
            : h >= .62 ? "chest" : "abdomen";
        return new(region, InjuryEvidence.GeometricEstimate);
    }
}

/// <summary>Derived, normalized remaining capabilities (1 = baseline). Never persisted as buff stacks.</summary>
public sealed record BodyFunctions
{
    public double Manipulation { get; init; } = 1;
    public double Filtration { get; init; } = 1;
    public double Dodge { get; init; }
    public double FatDamageReduction { get; init; }
    public double MuscleMeleeBonus { get; init; }
    public double Mobility { get; init; } = 1;
    public double Balance { get; init; } = 1;
    public double Endurance { get; init; } = 1;
    public double LaborEndurance { get; init; } = 1;
    public double LaborLoad { get; init; } = 1;
    public double SprintLoad { get; init; } = 1;
    public double RespiratoryCapacity { get; init; } = 1;
    public double Consciousness { get; init; } = 1;
    public double Circulation { get; init; } = 1;
    public double Digestion { get; init; } = 1;
    public double Immunity { get; init; } = 1;
    public string MobilityReason { get; init; } = "normal";
    public string ConsciousnessReason { get; init; } = "normal";
    public string RespiratoryReason { get; init; } = "normal";
    public string CirculationReason { get; init; } = "normal";
    public string DigestionReason { get; init; } = "normal";
    public string ImmunityReason { get; init; } = "normal";
    public string AttackReason { get; init; } = "normal";
    public string ManipulationReason { get; init; } = "normal";
    public string BowReason { get; init; } = "normal";
    public string EnduranceReason { get; init; } = "normal";
    public string BalanceReason { get; init; } = "normal";
    public string SprintReason { get; init; } = "normal";
    public string JumpReason { get; init; } = "normal";
    public double MeleeAttackSpeed { get; init; } = 1;
    public double Attack { get; init; } = 1;
    public double Mining { get; init; } = 1;
    public double Jump { get; init; } = 1;
    public double Sprint { get; init; } = 1;
    public double StrengthBonus { get; init; } = 1;
    public double Bow { get; init; } = 1;
    // Jump initiation is gated by the jump reserve itself. Consciousness and
    // unrelated systemic symptoms must not silently remove the native one-block arc.
    public bool CanJump => Jump >= .55;
    public bool SprintPermitted { get; init; } = true;
    public bool CanSprint => SprintPermitted && Sprint > 0 && Consciousness >= .3
        && RespiratoryCapacity > .2 && Circulation > .2;
    public bool CanDrawBow => Bow >= .3 && Consciousness >= .3;
    public bool CanUseActiveHand => Attack >= .2 && Mining >= .2 && Consciousness >= .3;
}

public static class BodyFunctionResolver
{
    public static double Remaining(Wound w) => Math.Clamp(Math.Max(w.Severity, w.TissueDamage ?? w.Severity) * (1 - w.Healing), 0, 1);
    public static double Pain(IEnumerable<Wound> wounds) => WoundPain.Resolve(wounds);

    // Shared health pools do not manufacture a second injury in hands, feet or both torso organs.
    private static double RegionalLoss(FrameworkState state, string region)
    {
        var index = Anatomy.VitalIndex(region);
        if (index < 0 || region.EndsWith("-hand") || region.EndsWith("-foot")) return 0;
        var loss = state.Vitals.EffectiveMaximumFor(index) - state.Vitals.EffectiveFor(index);
        if (index == 1)
        {
            var located = state.Wounds.Where(w => w.Region is "chest" or "abdomen")
                .Sum(w => (w.IntegrityDamage ?? 0) * (1 - w.Healing));
            loss = Math.Max(0, loss - located);
        }
        return Math.Clamp(loss / state.Vitals.EffectiveMaximumFor(index), 0, 1);
    }

    public static double ActionStage(double remaining) => remaining >= .8 ? 1 : remaining >= .6 ? .9
        : remaining >= .4 ? .75 : remaining >= .2 ? .5 : .25;
    private static double OverloadStage(double load) => load <= 0 ? 0 : load <= 1d / 3 ? 1d / 3 : load <= 2d / 3 ? 2d / 3 : 1;

    // Regional integrity and the wound describing the same injury are alternatives, never additive damage.
    public static double StructuralDamage(FrameworkState state, string region)
    {
        var index = Anatomy.VitalIndex(region);
        var vital = RegionalLoss(state, region);
        var wound = state.Wounds.Where(w => w.Region == region || w.Region == Anatomy.Parent(region))
            .Select(Remaining).DefaultIfEmpty().Max();
        return Math.Clamp(Math.Max(vital, wound), 0, 1);
    }

    private static (double Value, string Reason) Limit(params (double Value, string Reason)[] limits)
        => limits.MinBy(x => x.Value);

    private static (double Value, string Reason) DigestiveLimit(FrameworkState state, BodyState body, BodyProfile profile)
        => Limit((Hydration.OrganFactor(Hydration.Stage(body, profile)), "dehydration"),
            (1 - StructuralDamage(state, "abdomen") * .5, "torso"),
            (1 - ClinicalEffects.EntericBurden(state) * .4, "enteric-disease"),
            (1 - Math.Clamp(Math.Max(34 - body.CoreTemperatureC, body.CoreTemperatureC - 39.5) * .15, 0, .5), "temperature"),
            (body.GlycogenEnergyKJ + body.FatEnergyKJ < 100 ? .7 : 1, "energy"));

    public static double Digestion(FrameworkState state, BodyState body, BodyProfile? profile = null)
        => Math.Clamp(DigestiveLimit(state, body, profile ?? BodyProfile.Default).Value * AdrenalineEffects.Digestion(state), .15, 1);

    // Water does not need food digestion. Preserve local injury/disease limits
    // without making dehydration suppress its own treatment or sleep block it.
    public static double WaterAbsorption(FrameworkState state)
        => Math.Min(1 - StructuralDamage(state, "abdomen") * .5,
            1 - ClinicalEffects.EntericBurden(state) * .4)
            * NutrientInteractions.WaterAbsorptionSupport(state.Nutrition);

    public static BodyFunctions Evaluate(FrameworkState state, BodyState body, BodyProfile profile,
        double nativeAlcohol = 0, double nativePsychedelic = 0, double bowelUnits = 0)
    {
        double At(string region)
        {
            var wound = state.Wounds.Where(w => w.Region == region).Select(w => {
                var type = w.Kind switch { WoundKind.Fracture => 1.25, WoundKind.Sprain => 1.05,
                    WoundKind.Contusion => region == "head-neck" ? 1 : .65,
                    WoundKind.Cut => .8, WoundKind.Burn => .85, _ => .9 };
                return Math.Clamp((Remaining(w) - .1) / .9 * type, 0, 1);
            }).DefaultIfEmpty().Max();
            var index = Anatomy.VitalIndex(region);
            var vital = RegionalLoss(state, region);
            return Math.Max(wound, vital);
        }
        var leftLeg = Math.Max(At("left-leg"), At("left-foot"));
        var rightLeg = Math.Max(At("right-leg"), At("right-foot"));
        var head = At("head-neck");
        var torso = At("torso");
        var chest = Math.Max(torso, At("chest"));
        var bloodRatio = Math.Clamp(body.BloodVolumeL(profile) / profile.ReferenceBloodVolumeL, 0, 1);
        // Blood loss has a grace band, then a progressively stronger circulatory limit.
        var bloodDeficit = Math.Clamp((.9 - bloodRatio) / .6, 0, 1);
        var circulation = Limit((BloodCirculation.Pumping(state, body, profile), bloodDeficit > 0 ? "blood" : "torso"));
        var respiratoryDisease = state.Diseases.Where(d => Pathogens.Airborne(d.Pathogen))
            .Select(d => Treatments.Burden(state, d)).DefaultIfEmpty().Max();
        var respiration = Limit((1 - Math.Max(0, chest - .25) * .9, "torso"),
            (1 - respiratoryDisease * .45, "respiratory-disease"),
            (SmokeInhalation.Capacity(state.SmokeDose), "smoke"),
            (1 - Math.Max(state.CarbonMonoxideBurden * .55, state.ToxicBurden * .4), "gas"));
        var alcohol = Math.Max(state.AlcoholBurden, double.IsFinite(nativeAlcohol) ? Math.Clamp(nativeAlcohol, 0, 1.1) : 0);
        var psychedelic = double.IsFinite(nativePsychedelic) ? Math.Clamp(nativePsychedelic, 0, 2) : 0;
        var asphyxia = !state.HasAir && state.OxygenKnown ? Math.Clamp((.1 - state.OxygenAvailability) / .1, 0, 1) : 0;
        var conscious = Limit((1 - Math.Max(0, head - .5) * 2, "head"),
            (1 - Math.Max(0, .6 - circulation.Value) * 1.6, "circulation"),
            (1 - Math.Max(state.CarbonMonoxideBurden * .6, asphyxia), asphyxia > state.CarbonMonoxideBurden * .6 ? "asphyxia" : "gas"),
            (1 - Math.Max(alcohol * .25, psychedelic * .2), "intoxication"),
            (1 - Treatments.ComplaintSeverity(state, "sedated") * .2, "sedation"));
        var wasteRatio = state.Blood.WasteMl / BloodCirculation.WasteCapacityMl(body, profile);
        var toxicityFactor = wasteRatio < .4 ? 1 : wasteRatio < .55 ? .95 : wasteRatio < .7 ? .88 : wasteRatio < .85 ? .75 : .6;
        if (toxicityFactor < 1) conscious = (conscious.Value * toxicityFactor, "blood-waste");
        var fuelFactor = body.GlycogenEnergyKJ <= .01 ? .85 : 1;
        var lactateRatio = state.Blood.LactateG / BloodCirculation.LactateCapacityG(body, profile);
        var laborLoadFactor = Exertion.LoadLimit(state.Stamina, lactateRatio, state.Fatigue, body.SleepPressure);
        var sprintLoadFactor = Exertion.LoadLimit(state.Stamina, lactateRatio, state.Fatigue, body.SleepPressure, sprint: true);
        var workLimit = Limit(laborLoadFactor,
            (Ketosis.Performance(state.Clinical.Ketosis), "ketosis"), (conscious.Value, conscious.Reason));
        var workPerformance = Math.Min(laborLoadFactor.Value, Ketosis.Performance(state.Clinical.Ketosis)) * conscious.Value;
        var thermal = Math.Clamp(Math.Max(35 - body.CoreTemperatureC, body.CoreTemperatureC - 39) * .12, 0, .3);
        var dehydration = 1 - Hydration.PerformanceFactor(Hydration.Stage(body, profile));
        var muscleRatio = body.MuscleMassG < 5000 ? .9 : 1;
        var fatKg = body.FatEnergyKJ / (DietaryMetabolism.FatKJPerG * 1000);
        var bMultiplier = 1 - .1 * MicroNutrition.Burden(state.Nutrition, "bcomplex");
        var composition = 1 - Math.Max(.25, 1 - Math.Max(0, fatKg - 30) * .05);
        var bowelOverload = bowelUnits >= BowelSettlement.CapacityG ? 1 : bowelUnits >= BowelSettlement.WarningG ? .5 : 0;
        var movement = Limit((1 - .22 * leftLeg - .22 * rightLeg, leftLeg >= rightLeg ? "left-leg" : "right-leg"),
            (1 - .05 * state.MuscleSoreness, "muscle-soreness"),
            (1 - RestFeedback.FatigueMovementPenalty(state.Fatigue), "fatigue"),
            (1 - thermal, "temperature"), (1 - dehydration, "dehydration"),
            (1 - composition, "composition"), (1 - .15 * bowelOverload, "bowel"),
            (Math.Min(1, conscious.Value + .2), "consciousness"));
        // Muscle supports melee damage only; there is no generic nutrition/action buff.
        var muscleBonus = body.MuscleMassG >= 45000 ? .10 : body.MuscleMassG >= 25000 ? .05 : 0;
        var manipulation = muscleRatio * (1 - (Math.Max(At("left-arm"), At("left-hand")) + Math.Max(At("right-arm"), At("right-hand"))) / 2) * workPerformance;
        var forceLimit = Limit((1 - Math.Max(At("right-arm") * .85, At("right-hand")), "right-arm"),
            (conscious.Value < .5 ? conscious.Value * 2 : 1, "consciousness"),
            (1 - composition, "composition"),
            (1 - Treatments.ComplaintSeverity(state, "dermatitis") * .15, "skin"));
        var bowLimit = Limit((forceLimit.Value, forceLimit.Reason),
            (1 - Math.Max(At("left-arm") * .85, At("left-hand")), "left-arm"));
        var anemia = Math.Max(NutrientInteractions.Burden(state.Nutrition, "iron"), NutrientInteractions.Burden(state.Nutrition, "bcomplex"));
        var laborEndurance = Limit((respiration.Value, "respiratory"), (circulation.Value, "circulation"),
            (Math.Clamp(muscleRatio, .55, 1), "muscle"),
            (1 - Math.Clamp((.35 - body.ElectrolyteBalance) * 1.2, 0, .45), "electrolytes"),
            (1 - .35 * state.Blood.Toxicity, "blood-waste"));
        // Lactate affects sprint pace and burst availability; it does not slow
        // ordinary labor endurance or hand actions.
        var endurance = laborEndurance;
        var balance = Limit((1 - Math.Max(0, head - .2) * .9, "head"), (1 - alcohol * .4, "intoxication"),
            (1 - Math.Max(leftLeg, rightLeg) * .15, leftLeg >= rightLeg ? "left-leg" : "right-leg"));
        var legDamage = Math.Max(StructuralDamage(state, "left-leg"), Math.Max(StructuralDamage(state, "right-leg"),
            Math.Max(StructuralDamage(state, "left-foot"), StructuralDamage(state, "right-foot"))));
        var hardJump = legDamage >= 2d / 15;
        var adrenaline = state.AdrenalineLevel > 0 && state.AdrenalineRemainingSeconds > 0 ? state.AdrenalineLevel : 0;
        // Native sprint is added on top of walking speed: never reapply mobility or adrenaline here.
        var sprint = Limit((1 - Math.Max(leftLeg, rightLeg) * .7,
            leftLeg >= rightLeg ? "left-leg" : "right-leg"), (1 - .1 * state.MuscleSoreness, "muscle-soreness"));
        string Reason((double Value, string Reason) limit, double support = 0, bool adrenal = false) => limit.Value < .995 ? limit.Reason
            : adrenal && adrenaline > 0 ? "adrenaline" : support > 0 ? "nutrition" : "normal";
        var digestiveLimit = DigestiveLimit(state, body, profile);
        var immuneLimit = Limit((state.ImmuneReserve, "immune-reserve"),
            (1 - .4 * StructuralDamage(state, "torso"), "torso"),
            (1 - .3 * Math.Max(state.Blood.Toxicity, Math.Max(state.ToxicBurden, state.DrugToxicBurden)), "toxicity"));
        return new() {
            Manipulation = manipulation, Filtration = BloodCirculation.Filtration(state, body, profile),
            Dodge = CombatFunctions.Dodge(body, conscious.Value, adrenaline),
            FatDamageReduction = Math.Clamp(body.FatEnergyKJ / (DietaryMetabolism.FatKJPerG * 1000) * .01, 0, .5),
            MuscleMeleeBonus = muscleBonus, MeleeAttackSpeed = AdrenalineEffects.AttackSpeed(state),
            Consciousness = Math.Clamp(conscious.Value * bMultiplier * (1 + .25 * adrenaline), 0, 1.25),
            Mobility = Math.Clamp(movement.Value * muscleRatio * bMultiplier * fuelFactor
                * (1 + .50 * adrenaline), 0, 1.5),
            RespiratoryCapacity = respiration.Value, Circulation = circulation.Value,
            Digestion = Digestion(state, body, profile), Immunity = ImmuneSystem.Effective(state),
            Balance = balance.Value, Endurance = endurance.Value, LaborEndurance = laborEndurance.Value,
            LaborLoad = laborLoadFactor.Value, SprintLoad = sprintLoadFactor.Value,
            Attack = (StructuralDamage(state, "right-arm") >= .95 || StructuralDamage(state, "right-hand") >= .95 || forceLimit.Value <= .150001 ? 0 : ActionStage(forceLimit.Value)) * muscleRatio * (1 + muscleBonus) * workPerformance,
            Mining = StructuralDamage(state, "right-arm") >= .95 || StructuralDamage(state, "right-hand") >= .95 || forceLimit.Value <= .150001 ? 0 : ActionStage(forceLimit.Value) * muscleRatio * workPerformance,
            Bow = StructuralDamage(state, "left-arm") >= .95 || StructuralDamage(state, "left-hand") >= .95 || StructuralDamage(state, "right-arm") >= .95 || StructuralDamage(state, "right-hand") >= .95 || bowLimit.Value <= .150001 ? 0 : ActionStage(bowLimit.Value) * muscleRatio * workPerformance, StrengthBonus = 1 + muscleBonus,
            Jump = legDamage >= .8 ? .5 : hardJump ? .89 : 1 + .20 * adrenaline,
            Sprint = Math.Min(ActionStage(sprint.Value), 1 - .1 * state.MuscleSoreness) * sprintLoadFactor.Value,
            SprintPermitted = Math.Max(leftLeg, rightLeg) < .6,
            MobilityReason = Reason(movement, adrenal: true), ConsciousnessReason = Reason(conscious, adrenal: true),
            RespiratoryReason = Reason(respiration), CirculationReason = Reason(circulation),
            DigestionReason = adrenaline > 0 ? "adrenaline" : Reason(digestiveLimit),
            ImmunityReason = Reason(immuneLimit),
            ManipulationReason = Reason(workLimit),
            AttackReason = Reason(Limit(forceLimit, workLimit)), BowReason = Reason(Limit(bowLimit, workLimit)), EnduranceReason = Reason(endurance),
            BalanceReason = Reason(balance), SprintReason = Reason(Limit(sprint, sprintLoadFactor), adrenal: true),
            JumpReason = legDamage >= 2d / 15 ? "leg-injury"
                : adrenaline > 0 ? "adrenaline" : "normal"
        };
    }
}

/// <summary>Combat probabilities, separate from native armor and noncombat damage.</summary>
public static class CombatFunctions
{
    public static double Dodge(BodyState body, double consciousness, double adrenaline)
    {
        var metabolism = body.GlycogenEnergyKJ > .01 ? 0 : body.FatEnergyKJ > .01 ? .08 : .12;
        return Math.Clamp(.36 - metabolism
            - Math.Clamp(1 - consciousness, 0, 1) * .20 + adrenaline * .10, 0, .95);
    }
    public static double FacingDodge(double baseline, double facingDot)
        => Math.Clamp(baseline - .10 + .25 * (Math.Clamp(facingDot, -1, 1) + 1) / 2, 0, .95);
}
