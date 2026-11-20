using Seraph.Core.Simulation;
using Seraph.Core.State;

namespace Seraph.Core.Framework;

public sealed record ActivityContext(double ActivityPowerW, bool Sleeping, bool Resting, bool Mounted,
    bool Teleported, double OxygenAvailability, bool OxygenKnown)
{
    public double HealingMultiplier { get; init; } = 1;
    public double? PaidActivityEnergyJ { get; init; }
    public bool HasAir { get; init; } = true;
    public double SleepQuality { get; init; } = 1;
    public double BedQuality { get; init; } = .8;
}
public sealed record FrameworkOptions
{
    public double WoundHealingSeconds { get; init; } = 86400;
    // Gameplay pacing in bio seconds; independent of metabolic/thermal speed.
    public double FatigueRiseSeconds { get; init; } = 172800;
    public double FatigueRecoverySeconds { get; init; } = 28800;
    public double SeatedRecoverySeconds { get; init; } = 28800 / .385;
    // 1.5 mL/game minute less bleeding per game hour, in L/biological second.
    public double CoagulationLpsPerGameHour { get; init; } = .000025;
    public bool IntegrateBleeding { get; init; }
    public ThermalAdaptationOptions ThermalAdaptation { get; init; } = new();
    public PainOptions Pain { get; init; } = new();
    public string? Validate() => ThermalAdaptation is null || ThermalAdaptation.Validate() is not null || Pain is null || Pain.Validate() is not null || !BodyProfile.In(WoundHealingSeconds, 600, 1e8)
        || !BodyProfile.In(FatigueRiseSeconds, 60, 1e8) || !BodyProfile.In(FatigueRecoverySeconds, 30, 1e8)
        || !BodyProfile.In(SeatedRecoverySeconds, 30, 1e8) || !BodyProfile.In(CoagulationLpsPerGameHour, 0, .1)
        ? "Invalid framework time constants." : null;
}
public sealed record FrameworkResult(bool Accepted, FrameworkState State, string? Error);
public sealed record IntakeReceipt(string Id, string Source, double WaterL, double EnergyKJ, double Saturation,
    bool PortionVerified, bool EnergyIsGameEstimate, double ElectrolytesMg = 0);
public sealed record DiseaseDefinition(string Id, double IncubationSeconds, double SymptomaticSeconds,
    double RecoverySeconds, double MaximumBurden)
{
    public string? Validate() => string.IsNullOrWhiteSpace(Id) || Id.Length > 64
        || !BodyProfile.In(IncubationSeconds, 1, 1e8) || !BodyProfile.In(SymptomaticSeconds, 1, 1e8)
        || !BodyProfile.In(RecoverySeconds, 1, 1e8) || !BodyProfile.In(MaximumBurden, 0, 1) ? "Invalid disease definition." : null;
}

/// <summary>Pure modules for fatigue, wounds, exposure hazard, circulation trends and long-term adaptation.</summary>
public sealed class FrameworkKernel
{
    private readonly FrameworkOptions options;
    public FrameworkKernel(FrameworkOptions? options = null)
    {
        this.options = options ?? new();
        if (this.options.Validate() is { } error) throw new ArgumentException(error, nameof(options));
    }

    public FrameworkResult Advance(FrameworkState old, BodyState body, BodyProfile profile,
        EnvironmentSample environment, ActivityContext activity, double dt)
    {
        var error = old.Validate() ?? body.Validate(profile) ?? environment.Validate();
        if (error is not null) return Reject(old, error);
        if (!BodyProfile.In(dt, 0, 8) || !BodyProfile.In(activity.ActivityPowerW, 0, 2000)
            || !BodyProfile.In(activity.HealingMultiplier, 0, 100)
            || !BodyProfile.In(activity.OxygenAvailability, 0, 1)
            || activity.PaidActivityEnergyJ is { } paid && !BodyProfile.In(paid, 0, 1e12)
            || !BodyProfile.In(activity.SleepQuality, 0, 1) || !BodyProfile.In(activity.BedQuality, 0, 1))
            return Reject(old, "Invalid framework input.");
        var power = activity.Teleported || activity.Sleeping ? 0 : activity.ActivityPowerW;
        var effort = Math.Clamp(power / 300, 0, 1);
        old = old with { Vitals = old.Vitals.WithNutrition(old.Nutrition, body, profile) };
        old = old with { TreatmentClockBioSeconds = body.ElapsedBioSeconds };
        var observed = old with { HasAir = activity.HasAir, OxygenKnown = activity.OxygenKnown,
            OxygenAvailability = activity.OxygenKnown ? activity.OxygenAvailability : old.OxygenAvailability };
        var condition = PlayerCondition.Evaluate(observed, body, profile);
        var functions = BodyFunctionResolver.Evaluate(observed, body, profile);
        var stamina = old.Stamina; // The adapter owns the real-time action ledger.
        var restSupport = 1 + Treatments.Relief(old, "fatigue") * .5;
        var paidWork = activity.PaidActivityEnergyJ ?? power * dt;
        var fatigue = activity.Sleeping ? old.Fatigue - dt * activity.SleepQuality * restSupport / options.FatigueRecoverySeconds
            : activity.Resting ? old.Fatigue - dt * restSupport / options.SeatedRecoverySeconds
            : old.Fatigue + Math.Max(0, paidWork) / (3500 * 4184) * 172800 / options.FatigueRiseSeconds;
        var recoveryMultiplier = condition.RecoveryMultiplier * activity.HealingMultiplier;
        var wounds = old.Wounds.Select(w => WoundPain.Advance(w, dt, options.Pain) with
        {
            BleedLps = Math.Max(w.NaturalClotFloorLps, w.BleedLps - options.CoagulationLpsPerGameHour * dt / 3600),
            Healing = Math.Clamp(w.Healing + dt * HealingRate(old, w, body.ElapsedBioSeconds,
                activity.Resting || activity.Sleeping, recoveryMultiplier), 0, 1),
            Contamination = w.Contamination * Math.Exp(-dt / 1800 * Treatments.Active(old).Where(t => t.Wound == w.EventSequence).Select(t => t.Definition.CleanFraction).DefaultIfEmpty().Max()),
            Pathogens = PathogenLoads.Scale(w.Pathogens, Math.Exp(-dt / 1800 * Treatments.Active(old).Where(t => t.Wound == w.EventSequence).Select(t => t.Definition.CleanFraction).DefaultIfEmpty().Max())),
            BandageQuality = Math.Max(w.BandageQuality, Treatments.Active(old).Where(t => t.Wound == w.EventSequence).Select(t => t.Definition.Hemostasis).DefaultIfEmpty().Max())
        }).ToArray();
        old = ExerciseMuscles.Recover(old, dt, activity.Sleeping, activity.Resting, power > 250);
        var pain = Treatments.Pain(old with { Wounds = wounds }, BloodCirculation.LactateCapacityG(body, profile));
        var oxygen = activity.OxygenKnown ? activity.OxygenAvailability : old.OxygenAvailability;
        var nutritionSufficiency = old.Nutrition.Assess().Sufficiency;
        var trendAlpha = 1 - Math.Exp(-dt / 604800);
        var environmentNoise = Math.Clamp(environment.WindSpeedMps / 12
            + (1 - environment.RoomVentilation) * .35 + environment.AirPollution * .25, 0, 1);
        var sleepQuality = activity.Sleeping ? activity.SleepQuality : old.SleepEfficiency;
        var immuneTarget = ImmuneSupport(old, body, profile, environment, pain);
        var immuneAlpha = 1 - Math.Exp(-dt / 172800);
        var pollutionAlpha = 1 - Math.Exp(-dt / 900);
        var next = old with
        {
            Treatments = old.Treatments.Where(t => t.EndsBioSeconds > body.ElapsedBioSeconds - dt).ToArray(),
            TreatmentDoses = old.TreatmentDoses.Where(d => body.ElapsedBioSeconds - d.BioSeconds < 18000).ToArray(),
            Complaints = old.Complaints.Select(c => c with { EndsBioSeconds = Math.Max(c.StartsBioSeconds + 1,
                c.EndsBioSeconds - dt * Math.Min(1.5, Treatments.Active(old).Where(t => t.Definition.Symptoms.Contains(c.Id))
                    .Sum(t => t.Definition.RecoveryBonus))) }).Where(c => c.EndsBioSeconds > body.ElapsedBioSeconds).ToArray(),
            DrugToxicBurden = old.DrugToxicBurden * Math.Exp(-dt / 54000),
            AlcoholBurden = old.AlcoholBurden * Math.Exp(-dt / 54000),
            Stamina = stamina,
            Fatigue = Math.Clamp(fatigue, 0, 1),
            Pain = pain, Wounds = wounds, LastActivityPowerW = power,
            Dirtiness = old.Dirtiness,
            HasAir = activity.HasAir, OxygenAvailability = oxygen, OxygenKnown = activity.OxygenKnown, PerfusionTrend = functions.Circulation,
            BreathDemandPerMinute = Math.Min(120, ((14 + 26 * effort) / Math.Max(.3, functions.RespiratoryCapacity) + 20 * (1 - oxygen)) * AdrenalineEffects.Ventilation(old)),
            NutritionTrend = Math.Clamp(old.NutritionTrend + trendAlpha * (nutritionSufficiency - old.NutritionTrend), 0, 1),
            ImmuneReserve = Math.Clamp(old.ImmuneReserve + immuneAlpha * (immuneTarget - old.ImmuneReserve), 0, 1),
            AirPollution = Math.Clamp(old.AirPollution + pollutionAlpha * (environment.AirPollution - old.AirPollution), 0, 1),
            CarbonMonoxideBurden = Math.Clamp(old.CarbonMonoxideBurden + (1 - Math.Exp(-dt / 300))
                * (environment.CarbonMonoxide - old.CarbonMonoxideBurden), 0, 1),
            ToxicBurden = Math.Clamp(old.ToxicBurden + (1 - Math.Exp(-dt / 900)) * (environment.ToxicGas - old.ToxicBurden), 0, 1),
            ChemicalBurden = Math.Clamp(old.ChemicalBurden + (1 - Math.Exp(-dt / 1800))
                * (EnvironmentalExposure.ChemicalInput(environment) - old.ChemicalBurden), 0, 1),
            BedQuality = activity.Sleeping ? Math.Clamp(activity.BedQuality, 0, 1) : old.BedQuality,
            SleepEfficiency = sleepQuality,
            EnvironmentNoise = environmentNoise
        };
        next = next with { Clinical = ClinicalIndicatorsModel.Advance(old.Clinical, next, body, profile, dt, oxygen, power) };
        return Commit(old, RestFeedback.Advance(ThermalAdaptation.Advance(next, body, profile, dt, options.ThermalAdaptation), body, activity.Sleeping));
    }

    /// <summary>Starts one bounded fight-or-flight window after a meaningful hit or bleeding event.</summary>
    public (FrameworkState State, BodyState Body, bool Accepted) ActivateAdrenaline(FrameworkState old, BodyState body,
        BodyProfile profile, double nowBioSeconds, double triggerDamage, bool bleeding)
    {
        if (old.Validate() is not null || body.Validate(profile) is not null
            || !double.IsFinite(nowBioSeconds) || !double.IsFinite(triggerDamage) || triggerDamage < 0
            // A meaningful hit is two health points (2% of the 100-point
            // player pool); any active bleed is an immediate trigger.
            || triggerDamage < 2 - 1e-6 && !bleeding
            || old.AdrenalineLevel > 0 || old.AdrenalineCooldownRemainingSeconds > 0)
            return (old, body, false);
        var fatigueDebt = Math.Min(.30, old.Fatigue);
        var sleepDebt = Math.Min(.20, body.SleepPressure);
        return (old with {
            AdrenalineLevel = 1,
            AdrenalineRemainingSeconds = 60,
            AdrenalineCooldownRemainingSeconds = 43200,
            AdrenalineFatigueDebt = fatigueDebt,
            AdrenalineSleepDebt = sleepDebt,
            Fatigue = old.Fatigue - fatigueDebt
        }, body with { SleepPressure = body.SleepPressure - sleepDebt }, true);
    }

    /// <summary>Combat response is independent of calendar speed; callers freeze it while paused/offline.</summary>
    public (FrameworkState State, BodyState Body) AdvanceAdrenaline(FrameworkState old, BodyState body, double realSeconds)
    {
        if (!double.IsFinite(realSeconds) || realSeconds < 0) throw new ArgumentOutOfRangeException(nameof(realSeconds));
        var remaining = Math.Max(0, old.AdrenalineRemainingSeconds - realSeconds);
        var expired = old.AdrenalineLevel > 0 && remaining == 0;
        return (old with {
            AdrenalineRemainingSeconds = remaining,
            AdrenalineLevel = expired ? 0 : old.AdrenalineLevel,
            Fatigue = Math.Clamp(old.Fatigue + (expired ? old.AdrenalineFatigueDebt : 0), 0, 1),
            AdrenalineFatigueDebt = expired ? 0 : old.AdrenalineFatigueDebt,
            AdrenalineSleepDebt = expired ? 0 : old.AdrenalineSleepDebt
        }, expired ? body with { SleepPressure = Math.Clamp(body.SleepPressure + old.AdrenalineSleepDebt, 0, 1) } : body);
    }

    /// <summary>The same slow-reserve target used by Advance; not an independent immunity pool.</summary>
    public static double ImmuneSupport(FrameworkState state, BodyState body, BodyProfile profile,
        EnvironmentSample environment, double? pain = null)
        => ImmuneSystem.Support(state, body, profile);

    /// <summary>Fraction of wound closure per calendar second, before the remaining-wound clamp.</summary>
    public double WoundHealingRate(FrameworkState state, Wound wound, BodyState body, BodyProfile profile, bool resting, double healingMultiplier = 1)
        => HealingRate(state, wound, body.ElapsedBioSeconds, resting, PlayerCondition.Evaluate(state, body, profile).RecoveryMultiplier
            * (BodyProfile.In(healingMultiplier, 0, 100) ? healingMultiplier : 1));

    private double HealingRate(FrameworkState state, Wound wound, double now, bool resting, double recovery)
        => 2d / 3600 / Math.Max(1, WoundRecovery.DamagePoints(wound)) / Math.Max(1, state.Wounds.Count(w => w.Healing < 1))
            * (86400 / options.WoundHealingSeconds) * (resting ? 1 : .5)
            * (wound.CleanUntilBioSeconds > now ? 1 : .75) * recovery
            * (1 + Math.Max(wound.BandageQuality, Treatments.Active(state).Where(t => t.Wound == wound.EventSequence).Select(t => t.Definition.WoundRepair).DefaultIfEmpty().Max()))
            * (1 - state.Diseases.Where(d => Pathogens.Wound(d.Pathogen)).Select(d => Treatments.Burden(state, d)).DefaultIfEmpty().Max());

    // Stored wound rates predate this balance adjustment. Scale at settlement so old
    // wounds receive the same protection without rewriting their clotting history.
    public double BleedLps(FrameworkState state, double bioSeconds, long pressedWound = 0)
    {
        if (!options.IntegrateBleeding) return 0;
        var total = state.Wounds.Sum(w => WoundCare.Rate(w));
        if (total <= 0) return 0;
        var pressureRatio = pressedWound == 0 ? 1 : state.Wounds.Sum(w => WoundCare.Rate(w, pressedWound)) / total;
        return Math.Min(0.0002, total * .1) * pressureRatio
            * AdrenalineEffects.Bleeding(state) * NutrientInteractions.BleedingMultiplier(state.Nutrition);
    }

    public FrameworkResult RecordIntake(FrameworkState old, IntakeReceipt receipt, double acceptedWaterL, double acceptedEnergyKJ)
    {
        if (old.Validate() is { } error) return Reject(old, error);
        if (string.IsNullOrWhiteSpace(receipt.Id) || receipt.Id.Length > 128 || string.IsNullOrWhiteSpace(receipt.Source)
            || !receipt.PortionVerified || !BodyProfile.In(receipt.WaterL, 0, 10)
            || !BodyProfile.In(receipt.EnergyKJ, 0, 100000) || !BodyProfile.In(receipt.Saturation, 0, 100000)
            || !BodyProfile.In(receipt.ElectrolytesMg, 0, 1e7)
            || !BodyProfile.In(acceptedWaterL, 0, receipt.WaterL) || !BodyProfile.In(acceptedEnergyKJ, 0, receipt.EnergyKJ))
            return Reject(old, "Unverified intake or invalid accepted flux.");
        if (old.RecentReceipts.Contains(receipt.Id, StringComparer.Ordinal)) return Reject(old, "Duplicate receipt.");
        return Commit(old, old with
        {
            EventSequence = checked(old.EventSequence + 1),
            TotalIntakeWaterL = old.TotalIntakeWaterL + acceptedWaterL,
            TotalIntakeEnergyKJ = old.TotalIntakeEnergyKJ + acceptedEnergyKJ,
            TotalObservedSaturation = old.TotalObservedSaturation + receipt.Saturation,
            RecentReceipts = old.RecentReceipts.TakeLast(255).Append(receipt.Id).ToArray()
        });
    }

    public FrameworkResult AddWound(FrameworkState old, WoundKind kind, double severity, double bleedLps, string region = "general")
        => RecordInjury(old, new("legacy:" + (old.EventSequence + 1), kind, region, severity,
            region == "general" ? InjuryEvidence.Unlocated : InjuryEvidence.LegacyUnknown, 0, bleedLps));

    public FrameworkResult RecordInjury(FrameworkState old, InjuryEvent injury)
    {
        if (old.Validate() is { } error) return Reject(old, error);
        if (injury is null || string.IsNullOrWhiteSpace(injury.Id) || injury.Id.Length > 128
            || !Anatomy.Known(injury.Region) || !Enum.IsDefined(injury.Kind) || !Enum.IsDefined(injury.Evidence)
            || !BodyProfile.In(injury.Severity, 0, 1) || !BodyProfile.In(injury.BleedLps, 0, .01)
            || !BodyProfile.In(injury.OccurredBioSeconds, 0, 1e15)
            || injury.IntegrityDamage is { } hp && !BodyProfile.In(hp, 0, 1000001)
            || injury.TissueDamage is { } tissue && !BodyProfile.In(tissue, 0, 1)
            || injury.PainIntensity is { } pain && !BodyProfile.In(pain, 0, 1)
            || injury.Kind is WoundKind.Fracture or WoundKind.Sprain && injury.Evidence != InjuryEvidence.Verified)
            return Reject(old, "Invalid injury or structural diagnosis without evidence.");
        if (old.RecentInjuryEvents.Contains(injury.Id, StringComparer.Ordinal)) return Reject(old, "Duplicate injury event.");
        var seq = checked(old.EventSequence + 1);
        var list = old.Wounds.ToList();
        if (list.Count == 32)
        {
            // Keep the new event, and explicitly mark the bounded loss of location in the two least severe old records.
            var pair = list.GroupBy(w => (w.Region, w.Kind)).Where(g => g.Count() >= 2)
                .OrderBy(g => g.Min(BodyFunctionResolver.Remaining)).FirstOrDefault()?.OrderBy(BodyFunctionResolver.Remaining).Take(2).ToArray()
                ?? list.OrderBy(BodyFunctionResolver.Remaining).Take(2).ToArray();
            var sameLocationAndKind = pair[0].Region == pair[1].Region && pair[0].Kind == pair[1].Kind;
            var severity = 1 - (1 - BodyFunctionResolver.Remaining(pair[0])) * (1 - BodyFunctionResolver.Remaining(pair[1]));
            var painLevel = 1 - pair.Aggregate(1d, (p, w) => p * (1 - (w.PainIntensity ?? w.Severity) * (1 - w.Healing)));
            list.Remove(pair[0]); list.Remove(pair[1]);
            list.Add(new() { EventSequence = pair[0].EventSequence, Kind = sameLocationAndKind ? pair[0].Kind : WoundKind.Generic,
                Region = sameLocationAndKind ? pair[0].Region : "general", Severity = severity,
                TissueDamage = severity, IntegrityDamage = Math.Min(1000001, pair.Sum(w => WoundRecovery.DamagePoints(w) * (1 - w.Healing))), PainIntensity = painLevel, PainPools = WoundPain.Merge(pair), Evidence = InjuryEvidence.CapacityAggregate,
                OccurredBioSeconds = Math.Min(pair[0].OccurredBioSeconds, pair[1].OccurredBioSeconds),
                Contamination = pair.Max(w => w.Contamination),
                Pathogens = PathogenLoads.Add(pair[0].Pathogens, pair[1].Pathogens),
                PressureUntilBioSeconds = pair.Min(w => w.PressureUntilBioSeconds),
                CleanUntilBioSeconds = pair.Min(w => w.CleanUntilBioSeconds),
                BleedLps = Math.Min(.01, pair.Sum(w => WoundCare.Rate(w))),
                InitialBleedLps = Math.Min(.01, pair.Sum(w => w.InitialBleedLps)),
                NaturalClotFloorLps = Math.Min(.01, pair.Sum(w => w.NaturalClotFloorLps * (1 - w.Healing) * (1 - w.BandageQuality))) });
        }
        list.Add(new() { EventSequence = seq, Kind = injury.Kind, Region = injury.Region, Severity = injury.Severity,
            BleedLps = injury.BleedLps, InitialBleedLps = injury.BleedLps,
            NaturalClotFloorLps = injury.BleedLps >= .001 ? injury.BleedLps * .25 : 0,
            Evidence = injury.Evidence, OccurredBioSeconds = injury.OccurredBioSeconds,
            Contamination = WoundCare.Open(injury.Kind, injury.BleedLps) ? .2 + old.Dirtiness * .6 : 0,
            Pathogens = WoundCare.Open(injury.Kind, injury.BleedLps) ? old.SkinPathogens : PathogenLoads.Empty,
            IntegrityDamage = injury.IntegrityDamage,
            TissueDamage = injury.TissueDamage, PainIntensity = injury.PainIntensity });
        return Commit(old, old with { EventSequence = seq, Wounds = list.ToArray(),
            RecentInjuryEvents = old.RecentInjuryEvents.TakeLast(127).Append(injury.Id).ToArray() });
    }

    public FrameworkResult Treat(FrameworkState old, string action, double bioSeconds)
    {
        if (old.Validate() is { } error) return Reject(old, error);
        if (!BodyProfile.In(bioSeconds, 0, 1e15) || action != "clean")
            return Reject(old, "Invalid treatment or no wound.");
        return Commit(old, old with { EventSequence = checked(old.EventSequence + 1), Dirtiness = old.Dirtiness * .2,
            Syndromes = old.Syndromes with { VectorAttached = false, VectorHours = 0, VectorHazard = 0, VectorThreshold = 0, VectorPathogen = null },
            SkinPathogens = PathogenLoads.Scale(old.SkinPathogens, .1),
            Wounds = old.Wounds.Select(w => w with { Pathogens = PathogenLoads.Scale(w.Pathogens, .5), Contamination = w.Contamination * .5,
                CleanUntilBioSeconds = bioSeconds + 3600 }).ToArray() });
    }

    /// <summary>Permanent/chronic consequences need a verified source; replays cannot stack the same cause.</summary>
    public FrameworkResult RecordChronicEffect(FrameworkState old, ChronicEffect effect)
    {
        if (old.Validate() is { } error) return Reject(old, error);
        if (old.ChronicEffects.Any(e => e.SourceEvent == effect.SourceEvent && e.Kind == effect.Kind))
            return Reject(old, "Duplicate chronic consequence.");
        if (old.ChronicEffects.Length >= 16) return Reject(old, "Chronic effect capacity reached.");
        return Commit(old, old with { EventSequence = checked(old.EventSequence + 1),
            ChronicEffects = [.. old.ChronicEffects, effect] });
    }

    /// <summary>Only an explicit, server-verified exposure calls this. No default ambient or particle exposure.</summary>
    public FrameworkResult Expose(FrameworkState old, DiseaseDefinition definition, double hazardPerSecond, double dt, string source = "verified-exposure")
    {
        var error = old.Validate() ?? definition.Validate();
        if (error is not null) return Reject(old, error);
        if (!BodyProfile.In(hazardPerSecond, 0, 100) || !BodyProfile.In(dt, 0, 8)) return Reject(old, "Invalid hazard.");
        if (!TreatmentDefinition.Key(source)) return Reject(old, "Invalid exposure source.");
        if (hazardPerSecond == 0 || dt == 0) return new(true, old, null);
        var list = old.Diseases.ToList();
        var index = list.FindIndex(d => d.Pathogen == definition.Id);
        var random = new DeterministicRandom(old.RandomState);
        var thresholds = old.InfectionThresholdHistory.ToDictionary(p => p.Key, p => p.Value);
        if (index < 0)
        {
            if (list.Count >= 32) return Reject(old, "Disease capacity reached.");
            index = list.Count;
            if (!thresholds.TryGetValue(definition.Id, out var threshold))
                thresholds[definition.Id] = threshold = -Math.Log(Math.Max(1e-15, 1 - random.NextUnit()));
            list.Add(new() { Pathogen = definition.Id, InfectionThreshold = threshold });
        }
        thresholds.TryAdd(definition.Id, list[index].InfectionThreshold);
        var entry = list[index] with { SecondsSinceExposure = 0, Source = source };
        list[index] = entry;
        if (entry.Phase == DiseasePhase.Exposed)
        {
            var hazard = Math.Min(1e12, entry.CumulativeHazard + hazardPerSecond * dt * ImmuneSystem.Susceptibility(old) * (1 - old.ImmuneMemory.GetValueOrDefault(definition.Id)));
            list[index] = entry with { CumulativeHazard = hazard,
                Phase = hazard >= entry.InfectionThreshold ? DiseasePhase.Incubating : DiseasePhase.Exposed };
        }
        return Commit(old, old with { EventSequence = checked(old.EventSequence + 1), RandomState = random.State, Diseases = list.ToArray(), InfectionThresholdHistory = thresholds });
    }

    public FrameworkResult ProgressDiseases(FrameworkState old, IReadOnlyDictionary<string, DiseaseDefinition> definitions, double dt, IReadOnlySet<string>? ongoingExposure = null)
    {
        if (old.Validate() is { } error) return Reject(old, error);
        if (!BodyProfile.In(dt, 0, 8)) return Reject(old, "Invalid disease delta.");
        var result = new List<DiseaseState>();
        var thresholds = old.InfectionThresholdHistory.ToDictionary(p => p.Key, p => p.Value);
        var memory = PathogenLoads.Scale(old.ImmuneMemory, Math.Exp(-Math.Log(2) * dt / 259200)).ToDictionary(p => p.Key, p => p.Value);
        foreach (var disease in old.Diseases)
        {
            if (!definitions.TryGetValue(disease.Pathogen, out var def)) { result.Add(disease); continue; }
            if (def.Validate() is { } definitionError) return Reject(old, definitionError);
            thresholds.TryAdd(disease.Pathogen, disease.InfectionThreshold);
            if (disease.Phase == DiseasePhase.Exposed)
            {
                var hazard = disease.CumulativeHazard * Math.Exp(-Math.Log(2) * dt / 21600);
                if (hazard >= 1e-6) result.Add(disease with { CumulativeHazard = hazard, SecondsSinceExposure = disease.SecondsSinceExposure + dt });
                continue;
            }
            // Integrate real biological time across both medicine and disease-stage boundaries.
            // Only exposure verified during this step slows recovery; past ingestion never does.
            var end = old.TreatmentClockBioSeconds;
            var start = Math.Max(0, end - dt);
            if (end == 0) end = dt; // Legacy callers without a treatment clock.
            var boundaries = old.Treatments.SelectMany(t => new[] { t.StartsBioSeconds, t.EndsBioSeconds })
                .Where(t => t > start && t < end).Append(start).Append(end).Distinct().Order().ToArray();
            var time = disease.PhaseSeconds;
            var phase = disease.Phase;
            var effectiveImmune = ImmuneSystem.Effective(old);
            for (var j = 1; j < boundaries.Length && phase != DiseasePhase.Exposed; j++)
            {
                var remaining = boundaries[j] - boundaries[j - 1];
                var speed = Treatments.Speed(old, disease.Pathogen, (boundaries[j] + boundaries[j - 1]) / 2);
                for (var i = 0; i < 3 && remaining > 0; i++)
                {
                    var duration = ImmuneSystem.Duration(old, def, phase);
                    var rate = speed * (phase == DiseasePhase.Recovering && ongoingExposure?.Contains(disease.Pathogen) == true ? .75 : 1);
                    var needed = Math.Max(0, duration - time) / rate;
                    if (remaining < needed) { time += remaining * rate; break; }
                    remaining -= needed; time = 0;
                    if (phase == DiseasePhase.Recovering) { phase = DiseasePhase.Exposed; break; }
                    phase++;
                }
            }
            if (phase == DiseasePhase.Exposed) { memory[disease.Pathogen] = .5; continue; }
            var burden = phase == DiseasePhase.Symptomatic ? def.MaximumBurden * Math.Clamp(time / Math.Max(1, ImmuneSystem.Duration(old, def, DiseasePhase.Symptomatic) * .2), 0, 1) * Math.Clamp(1 + (1 - effectiveImmune) * .25, 1, 1.25)
                : phase == DiseasePhase.Recovering ? def.MaximumBurden * (1 - time / (def.RecoverySeconds * Math.Clamp(1 + (1 - effectiveImmune) * .5, 1, 1.5))) : 0;
            result.Add(disease with { Phase = phase, PhaseSeconds = time, SecondsSinceExposure = disease.SecondsSinceExposure + dt, Burden = Math.Clamp(burden, 0, 1) });
        }
        return Commit(old, old with { Diseases = result.ToArray(), ImmuneMemory = memory, InfectionThresholdHistory = thresholds });
    }

    private static FrameworkResult Commit(FrameworkState old, FrameworkState next) => next.Validate() is { } error
        ? Reject(old, error) : new(true, next, null);
    private static FrameworkResult Reject(FrameworkState old, string error) => new(false, old, error);
}

/// <summary>One resolver, one movement contribution. Correlated symptoms use max, never multiply penalties.</summary>
public static class FunctionResolver
{
    public static double MovementPenalty(FrameworkState state, BodyState body, BodyProfile profile)
        => 1 - BodyFunctionResolver.Evaluate(state, body, profile).Mobility;

}
