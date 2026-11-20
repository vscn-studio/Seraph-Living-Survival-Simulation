using Seraph.Core.Framework;
using Seraph.Network;
using Seraph.Core.State;

namespace Seraph.Server;

internal sealed partial class ServerCoordinator
{
    private static PanelMetric Metric(string key, double? value = null, string unit = "", double maximum = 0,
        double minimum = 0, string text = "", string detail = "") => new() {
        Key = key, Available = value is { } v && double.IsFinite(v) || text.Length > 0,
        Value = value is { } n && double.IsFinite(n) ? n : 0, Unit = unit,
        Maximum = maximum, Minimum = minimum, Bar = maximum > minimum, Text = text, Detail = detail
    };

    private PanelMetric[] StatusMetrics(Session s)
    {
        var gauges = HudGauges(s);
        var health = gauges.First(g => g.Key == "health");
        var oxygen = gauges.First(g => g.Key == "oxygen");
        var flows = s.Flows;
        var flowState = s.PhysiologyAdvancing ? "" : L(s.Player, "flow-paused");
        var waterDetail = flowState + L(s.Player, "flow-water", flows.Hourly(flows.AbsorbedWaterL * 1000), flows.Hourly(flows.LostWaterL * 1000));
        var toxicityDetail = L(s.Player, "blood-toxicity-detail",
            Number(s.Framework.Blood.WasteMl / BloodCirculation.WasteCapacityMl(s.State, s.Profile) * 100, "0.0"),
            Number(s.Framework.Blood.WasteMl, "0.0"), Number(BloodCirculation.WasteCapacityMl(s.State, s.Profile), "0.0"),
            Number(s.Framework.Blood.Toxicity * 100, "0.0"));
        var wasteDetail = flowState + L(s.Player, "flow-waste", flows.Hourly(flows.WasteProducedMl), flows.Hourly(flows.WasteClearedMl))
            + "\n" + toxicityDetail;
        var fuelDetail = flowState + L(s.Player, "flow-fuel", flows.Hourly(flows.GlycogenKJ) / 4.184,
            flows.Hourly(flows.FatKJ) / 4.184, flows.Hourly(flows.MuscleKJ) / 4.184);
        return [
            Metric("bleeding", ClinicalBleeding(s) * 60000, "mL/min"),
            Metric("health", health.Available ? health.Value : null, maximum: health.Maximum),
            Metric("stamina", s.Framework.Stamina * 100, maximum: 100,
                detail: L(s.Player, "activity-" + s.MovementActivity.ToString().ToLowerInvariant()) + " · "
                    + L(s.Player, "recovery-stage-" + s.Framework.ExertionRecoveryStage.ToString().ToLowerInvariant())
                    + "\n" + L(s.Player, "stamina-net-rate", s.ActivityRateStamina * 100)
                    + "\n" + L(s.Player, "stamina-nutrition-bonus", NutrientInteractions.StaminaBonus(s.Framework.Nutrition) * 100)),
            Metric("fatigue", s.Framework.Fatigue * 100, maximum: 100),
            Metric("pain", s.Framework.Pain * 100, maximum: 100, detail: L(s.Player, "pain-exercise-detail",
                s.Framework.MuscleSoreness * 100)),
            Metric("bodywater", ReserveDisplay.HydrationG(s.State, s.Profile), "g", ReserveDisplay.HydrationCapacityG, detail: waterDetail),
            Metric("blood", s.State.BloodVolumeL(s.Profile), "L", s.Profile.ReferenceBloodVolumeL),
            Metric("lactate", s.Framework.Blood.LactateG, "g", BloodCirculation.LactateCapacityG(s.State, s.Profile)),
            Metric("bloodwaste", s.Framework.Blood.WasteMl, "mL", BloodCirculation.WasteCapacityMl(s.State, s.Profile), detail: wasteDetail),
            Metric("bloodtoxicity", s.Framework.Blood.Toxicity * 100, maximum: 100, detail: toxicityDetail),
            Metric("bloodregen", s.Framework.Blood.RegenerationMlPerGameHour, "mL/h"),
            Metric("bloodfiltration", BloodCirculation.Filtration(s.Framework, s.State, s.Profile) * 100, maximum: 100),
            Metric("bloodclearance", s.Framework.Blood.FiltrationMlPerGameHour, "mL/h"),
            Metric("heartrate", AdrenalineEffects.HeartRate(s.Framework), "bpm"),
            Metric("breathrate", s.Framework.BreathDemandPerMinute, "min⁻¹"),
            Metric("bloodglucose", s.Framework.Clinical.BloodGlucoseMmol, "mmol/L", 15),
            Metric("whitecells", s.Framework.Clinical.WhiteCellReserve * 100, "%", 100),
            Metric("ketosis", s.Framework.Clinical.Ketosis * 100, "%", 100, detail: L(s.Player, "effect-help-ketosis")),
            Metric("renalfiltration", s.State.RenalFiltrationMlPerGameHour, "mL/h"),
            Metric("oxygen", oxygen.Available ? ReserveDisplay.Score(oxygen.Value, oxygen.Maximum) : null, maximum: 100),
            Metric("bowel", s.Interactions.BowelUnits, "g", BowelSettlement.CapacityG),
            Metric("sleeppressure", s.State.SleepPressure * 100, maximum: 100),
            Metric("musclemass", s.State.MuscleMassG / 1000, "kg", s.Profile.MuscleMassMaxG / 1000,
                detail: L(s.Player, "muscle-range", s.Profile.InitialMuscleMassG / 1000, s.State.MuscleTrainingStimulus * 100)),
            Metric("energybasal", s.HasEnergySnapshot ? s.EnergyBasalKcalPerGameHour : null, "kcal/h"),
            Metric("energycold", s.HasEnergySnapshot ? s.EnergyColdKcalPerGameHour : null, "kcal/h"),
            Metric("energyactivity", s.HasEnergySnapshot ? s.EnergyActivityKcalPerGameHour : null, "kcal/h"),
            Metric("energyrecovery", s.HasEnergySnapshot ? s.EnergyRecoveryKcalPerGameHour : null, "kcal/h"),
            Metric("energytotal", s.HasEnergySnapshot ? s.EnergyTotalKcalPerGameHour : null, "kcal/h", detail: fuelDetail)
        ];
    }

    private PanelMetric[] FunctionMetrics(Session s)
    {
        var f = EffectiveFunctions(s);
        var eyesClosed = EyesClosed(s);
        var dodgeReason = eyesClosed ? "closed-eyes" : "normal";
        PanelMetric Function(string key, double value, string reason) => Metric(key, value * 100, "%",
            text: (value * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%",
            detail: L(s.Player, "function-reason-" + reason));
        var jumpAllowed = JumpAllowed(s);
        var jumpValue = NativeInjuryActionBridge.AppliedJumpMultiplier(f.Jump, jumpAllowed);
        var jumpDetail = L(s.Player, "function-reason-" + (jumpValue < 1 ? f.JumpReason : "normal"));
        if (s.SprintExhausted) jumpDetail = (f.CanJump ? "" : jumpDetail + "\n")
            + L(s.Player, "jump-exhausted-detail", s.JumpCooldownRemaining, settings.Config.Exertion.UnlockStamina * 100);
        return [
            Function("consciousness", f.Consciousness, f.ConsciousnessReason),
            MobilityMetric(s, f.Mobility, f.MobilityReason),
            Function("respiratory", f.RespiratoryCapacity, f.RespiratoryReason),
            Function("circulation", f.Circulation, f.CirculationReason),
            Function("digestion", f.Digestion, f.DigestionReason),
            Function("immunity", f.Immunity, f.ImmunityReason),
            Function("manipulation", f.Manipulation, f.ManipulationReason),
            Metric("jump", jumpValue * 100, "%", text: (jumpValue * 100).ToString("0",
                System.Globalization.CultureInfo.InvariantCulture) + "%", detail: jumpDetail),
            Function("filtration", f.Filtration, "normal"),
            Function("dodge", eyesClosed ? 0 : f.Dodge, dodgeReason),
            Function("dodgefront", eyesClosed ? 0 : CombatFunctions.FacingDodge(f.Dodge, 1), dodgeReason),
            Function("dodgeback", eyesClosed ? 0 : CombatFunctions.FacingDodge(f.Dodge, -1), dodgeReason),
            Function("fatprotection", f.FatDamageReduction, "normal"),
            Function("musclemelee", f.MuscleMeleeBonus, "normal"),
            RecoveryMetrics(s).First(m => m.Key == "healingrate")
        ];
    }

    private PanelMetric MobilityMetric(Session s, double body, string reason)
    {
        var entity = s.Player.Entity;
        var combined = NativeStatInputs.Multiplier(entity, "walkspeed");
        var lines = new List<string> { L(s.Player, "mobility-body-detail", body * 100,
            L(s.Player, "function-reason-" + reason)) };
        foreach (var (source, value, weight) in NativeStatInputs.Sources(entity.Stats, "walkspeed"))
            lines.Add(L(s.Player, "mobility-source-detail", L(s.Player, NativeStatInputs.SourceLabel(source)), value * 100, weight));
        lines.Add(L(s.Player, "mobility-combined-detail", combined * 100));
        return Metric("mobility", combined * 100, "%", text: (combined * 100).ToString("0",
            System.Globalization.CultureInfo.InvariantCulture) + "%", detail: string.Join("\n", lines));
    }

    // Environmental and personal thermal metrics share the existing append-only network list.
    private PanelMetric[] TemperatureMetrics(Session s)
    {
        var t = s.Thermal;
        double? Rate(double value) => t.Seconds > 0 ? value : null;
        return [
            Metric("airtemp", s.Environment?.AirTemperatureC, "°C"),
            Metric("humidity", s.Environment?.RelativeHumidity * 100, "%"),
            Metric("wind", s.Environment?.WindSpeedMps, "m/s"),
            Metric("roomheat", s.Environment?.RoomHeatDeltaC, "°C"),
            Metric("ventilation", s.Environment?.RoomVentilation * 100, "%"),
            Metric("airpollution", s.Environment?.AirPollution * 100, "%"),
            Metric("environmentalcontamination", s.Environment?.EnvironmentalContamination * 100, "%"),
            Metric("coretemp", s.State.CoreTemperatureC, "°C"),
            Metric("skintemp", s.State.SkinTemperatureC, "°C"),
            Metric("feelslike", s.Environment is { } boundary ? s.Kernel.EquivalentTemperatureC(boundary) : null, "°C"),
            Metric("radianttemp", s.Environment?.RadiantTemperatureC, "°C"),
            Metric("feversetpoint", DiseaseSyndromes.FeverSetpointC(s.Framework), "°C"),
            ClothingMetric(s),
            Metric("wetness", s.Player.Entity.WatchedAttributes.TryGetFloat("wetness") is { } wet ? wet * 100 : null, "%"),
            Metric("heatproduction", Rate(t.HeatInW * 3600 / 4184), "kcal/h"),
            Metric("heatloss", Rate(t.HeatOutW * 3600 / 4184), "kcal/h"),
            Metric("heatbalance", Rate(t.NetW * 3600 / 4184), "kcal/h"),
            Metric("sweat", Rate(t.SweatLPerGameHour), "L/h"),
            Metric("shivering", Rate(t.ShiveringW * 3600 / 4184), "kcal/h"),
            Metric("sweatdebt", s.State.SweatDebtL, "L"),
            Metric("heatadaptation", s.Framework.HeatAdaptation * 15, "%"),
            Metric("coldadaptation", s.Framework.ColdAdaptation * 10, "%")
        ];
    }

    private PanelMetric ClothingMetric(Session s)
    {
        if (s.Environment is not { } environment) return Metric("clothing");
        var clothed = s.Kernel.DryHeatPowerW(s.State.SkinTemperatureC, environment);
        var bare = s.Kernel.DryHeatPowerW(s.State.SkinTemperatureC, environment with { ClothingInsulationClo = 0 });
        // A loss reduction is meaningful only while both boundaries lose heat.
        // In hot environments clothing can also slow heat gain; do not call that cooling.
        var losingHeat = clothed is >= 0 && bare is > 0;
        return Metric("clothing",
            text: losingHeat ? L(s.Player, "clothing-insulation-value", Math.Clamp(100 * (1 - clothed!.Value / bare!.Value), 0, 100)) : L(s.Player, "clothing-no-outward-loss"),
            detail: L(s.Player, "clothing-physical-detail", environment.ClothingInsulationClo) + "\n" + (losingHeat ? L(s.Player, "clothing-heat-comparison", bare!.Value * 3600 / 4184, clothed!.Value * 3600 / 4184)
                : L(s.Player, "clothing-heat-no-loss")));
    }

    private PanelMetric[] RecoveryMetrics(Session s)
    {
        var frame = s.Framework with { TreatmentClockBioSeconds = s.State.ElapsedBioSeconds };
        var resting = IsSeatedRest(s) || s.ClosedEyeRest != ClosedEyeRestMode.None
            || s.Player.Entity.WatchedAttributes.GetTreeAttribute("tiredness")?.GetInt("isSleeping") > 0;
        var wounds = frame.Wounds.Where(w => w.Healing < 1).ToArray();
        // With no wound, explicitly project the untreated new-wound rate at the current posture.
        var rates = (wounds.Length == 0 ? new[] { new Wound() } : wounds)
            .Select(w => frameworkKernel.WoundHealingRate(frame, w, s.State, s.Profile, resting, NativeStatInputs.PhysiologyMultiplier(s.Player.Entity, "healingeffectivness")) * 360000).ToArray();
        var healing = rates.Max() - rates.Min() < .005 ? L(s.Player, "healing-rate-value", rates[0])
            : L(s.Player, "healing-rate-range", rates.Min(), rates.Max());
        return [
            Metric("dirtiness", frame.Dirtiness * 100, "%", detail: string.Join("\n", new[] {
                s.Sanitation.CarriesWaste ? L(s.Player, "sanitation-carry-floor") : "",
                frame.BathedRemainingGameHours > 0 ? L(s.Player, "sanitation-bathed-protection", frame.BathedRemainingGameHours)
                    : frame.CleanRemainingGameHours > 0 ? L(s.Player, "sanitation-clean-protection", frame.CleanRemainingGameHours) : ""
            }.Where(line => line.Length > 0))),
            Metric("immune", frame.ImmuneReserve * 100, "%"),
            Metric("immunesystem", ImmuneSystem.Effective(frame) * 100, "%"),
            Metric("healingrate", text: healing, detail: L(s.Player, wounds.Length == 0 ? "healing-rate-new-wound" : "healing-rate-existing")),
            .. ClinicalEffects.Evaluate(frame, s.State, s.Profile)
                .Where(e => e.Id is "fever" or "food-poisoning" or "dysentery" or "sepsis" or "insect-carrier" or "renal-hyperactivity" or "smoke-inhalation")
                .Select(e => Metric("syndrome-" + e.Id, text: L(s.Player, "syndrome-row", L(s.Player, "effect-" + e.Id), e.Severity * 100),
                    detail: e.Id == "insect-carrier" ? L(s.Player, "vector-hours", frame.Syndromes.VectorHours, settings.Config.DiseaseExposure.VectorMaturationGameHours)
                        : L(s.Player, "effect-help-" + e.Id) + (e.Id == "smoke-inhalation"
                            ? "\n" + L(s.Player, "smoke-dose-detail", Number(frame.SmokeDose, "0.0"),
                                s.SmokeSampleValid ? Number(s.SmokeConcentration, "0.00") : "?",
                                Number(SmokeInhalation.Ventilation(Math.Max(s.ActivityPowerW, s.DemandPowerW)), "0.0")) : ""))),
            .. wounds.OrderByDescending(w => w.EventSequence).Select(w => Metric("wound-" + w.EventSequence,
                text: L(s.Player, "wound-status-row",
                    L(s.Player, "body-region-" + (Anatomy.Known(w.Region) ? w.Region : "general")),
                    L(s.Player, "character-wound-" + w.Kind.ToString().ToLowerInvariant()), BodyFunctionResolver.Remaining(w) * 100),
                detail: L(s.Player, "wound-status-detail", w.Healing * 100,
                    ClinicalBleeding(s, frame with { Wounds = [w] }) * 60000))),
            .. frame.Diseases.Select(d => Metric("disease-" + d.Pathogen,
                text: L(s.Player, "disease-row", L(s.Player, "effect-" + d.Pathogen),
                    L(s.Player, "disease-phase-" + d.Phase.ToString().ToLowerInvariant()), Treatments.Burden(frame, d) * 100),
                detail: DescribeDisease(s, d)))
        ];
    }

    private string DescribeDisease(Session s, DiseaseState d)
    {
        if (d.Phase == DiseasePhase.Exposed)
            return L(s.Player, "disease-exposure-detail", L(s.Player, "effect-" + d.Pathogen), d.CumulativeHazard);
        var estimate = 0d;
        if (DiseaseDefinitions.TryGetValue(d.Pathogen, out var def))
        {
            var incubation = ImmuneSystem.Duration(s.Framework, def, DiseasePhase.Incubating);
            var symptoms = ImmuneSystem.Duration(s.Framework, def, DiseasePhase.Symptomatic);
            var recovery = ImmuneSystem.Duration(s.Framework, def, DiseasePhase.Recovering);
            estimate = d.Phase switch {
                DiseasePhase.Incubating => incubation - d.PhaseSeconds + symptoms + recovery,
                DiseasePhase.Symptomatic => symptoms - d.PhaseSeconds + recovery,
                _ => recovery - d.PhaseSeconds
            };
        }
        return L(s.Player, "disease-recovery-short", Math.Max(0, estimate) / Treatments.Speed(s.Framework, d.Pathogen) / 3600);
    }

    private StatusIconSnapshot[] StatusIcons(Session s)
    {
        var sleepBand = s.State.SleepPressure switch { >= .95 => "extreme", >= .85 => "high", >= .7 => "moderate", _ => "normal" };
        var bComplex = KnownBurden(s, "bcomplex");
        var vitaminA = KnownBurden(s, "a");
        var vitaminC = KnownBurden(s, "c");
        return [
        new() { Key = "blood", Available = true,
            Active = ClinicalBleeding(s) > 0 },
        new() { Key = "sleep", Available = true, Active = s.State.SleepPressure >= .7,
            Detail = L(s.Player, "status-sleep-detail-" + sleepBand) },
        new() { Key = "defecation", Available = true, Active = s.Interactions.BowelUnits >= BowelSettlement.WarningG,
            Detail = L(s.Player, "status-bowel-detail", s.Interactions.BowelUnits, BowelSettlement.CapacityG,
                s.Interactions.BowelUnits / BowelSettlement.CapacityG * 100, BowelSettlement.ManualMinimumG,
                BowelSettlement.WarningG) },
        new() { Key = "bcomplex-deficiency", Available = bComplex.Known, Active = bComplex.Value > 0,
            Detail = bComplex.Known ? L(s.Player, "status-deficiency-detail", "B", bComplex.Value * 100) : "" },
        new() { Key = "vitamina-deficiency", Available = vitaminA.Known, Active = vitaminA.Value > 0,
            Detail = vitaminA.Known ? L(s.Player, "status-deficiency-detail", "A", vitaminA.Value * 100) : "" },
        new() { Key = "vitaminc-deficiency", Available = vitaminC.Known, Active = vitaminC.Value > 0,
            Detail = vitaminC.Known ? L(s.Player, "status-deficiency-detail", "C", vitaminC.Value * 100) : "" },
        new() { Key = "bloodtoxicity", Available = true, Active = s.Framework.Blood.Toxicity > 0 },
        new() { Key = "bug", Available = true, Active = s.Framework.Syndromes.VectorAttached,
            Detail = s.Framework.Syndromes.VectorAttached
                ? L(s.Player, "vector-hours", s.Framework.Syndromes.VectorHours, settings.Config.DiseaseExposure.VectorMaturationGameHours) : "" },
        new() { Key = "dysentery", Available = true, Active = DiseaseSyndromes.Severity(s.Framework, SymptomKit.Dysentery) > 0,
            Detail = DiseaseSyndromes.Severity(s.Framework, SymptomKit.Dysentery) > 0 ? L(s.Player, "effect-help-dysentery") : "" },
        new() { Key = "soiled", Available = true, Active = s.Framework.Dirtiness >= Sanitation.DirtyThreshold, Detail = L(s.Player, "medical-wash-help") },
        .. new[] { "dehydration", "hypothermia", "heat-illness", "ketosis", "bone-weakness", "anemia" }.Select(key => new StatusIconSnapshot {
            Key = key, Available = true, Active = ClinicalProjection(s).Any(e => key == "dehydration" ? e.Key is "thirst" or "dehydration"
                : e.Key == key),
            Detail = string.Join("\n", ClinicalProjection(s).Where(e => key == "dehydration" ? e.Key is "thirst" or "dehydration"
                : e.Key == key).Select(e => e.Description)) })
        ];
    }

    private static (bool Known, double Value) KnownBurden(Session s, string id)
    {
        var i = Array.FindIndex(Nutrients.All, n => n.Id == id);
        return i >= 0 && s.Framework.Nutrition.Known[i]
            ? (true, s.Framework.Nutrition.DeficiencyBurden[i]) : (false, 0);
    }

    private static (bool Known, double Value) MaxKnownBurden(Session s, params string[] ids)
    {
        var values = ids.Select(id => KnownBurden(s, id)).ToArray();
        return (values.Any(v => v.Known), values.Where(v => v.Known).Select(v => v.Value).DefaultIfEmpty().Max());
    }
}
